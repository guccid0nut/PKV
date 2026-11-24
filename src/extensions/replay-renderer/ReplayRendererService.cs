// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: ReplayRendererService.cs
// System: NEXUS Replay Video Renderer Service
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Diagnostics;
using System.Net.Http.Json;
using ASPIRE.Common.Hosting;
using ASPIRE.Common.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NEXUS.Contracts.Replay;
using NEXUS.Contracts.Security;
using ReplayRenderer.Service.Audio;
using ReplayRenderer.Service.Configuration;
using ReplayRenderer.Service.Encoding;
using ReplayRenderer.Service.Ipc;
using ReplayRenderer.Service.Models;
using ReplayRenderer.Service.Monitoring;
using ReplayRenderer.Service.ProcessLifecycle;
using ReplayRenderer.Service.Queues;

namespace ReplayRenderer.Service;

/// <summary>
///     Main orchestrator service managing the complete lifecycle of a replay rendering session:
///     sandboxed HoN engine execution, Vulkan swapchain interception, WASAPI audio loopback,
///     high-throughput IPC streaming, NVENC FFmpeg encoding, and watchdog monitoring.
/// </summary>
public sealed class ReplayRendererService(
    ILogger<ReplayRendererService> logger,
    ILoggerFactory loggerFactory,
    TelemetryLogger telemetryLogger,
    RenderJobConfiguration? initialJobConfig = null,
    ReplayRenderJobQueue? jobQueue = null,
    IHttpClientFactory? httpClientFactory = null,
    IConfiguration? configuration = null) : ResilientBackgroundService(logger, nameof(ReplayRendererService))
{
    private static readonly int[] ValidAudioDivisorFramerates = [24, 30, 48, 60, 120, 240];

    private readonly IConfiguration? _configuration = configuration;
    private readonly IHttpClientFactory? _httpClientFactory = httpClientFactory;
    private readonly RenderJobConfiguration? _initialJobConfig = initialJobConfig;
    private readonly ReplayRenderJobQueue _jobQueue = jobQueue ?? new ReplayRenderJobQueue();
    private readonly ILogger<ReplayRendererService> _logger = logger;
    private readonly ILoggerFactory _loggerFactory = loggerFactory;
    private readonly TelemetryLogger _telemetryLogger = telemetryLogger;

    /// <summary>
    ///     Result of the most recent post-export <c>ffprobe</c> container verification run by <see cref="ExecuteJobAsync" />.
    /// </summary>
    public FfprobeVideoVerificationResult? LastVerificationResult { get; private set; }

    /// <summary>
    ///     Builds a validated <see cref="RenderJobConfiguration" /> from a queued <see cref="ReplayRenderJob" />,
    ///     honoring resolution presets, 48kHz audio divisor framerates, POV lock, HUD suppression, and clip ranges.
    /// </summary>
    public RenderJobConfiguration BuildConfiguration(ReplayRenderJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        string outputMp4 = job.OutputPath;
        if (string.IsNullOrWhiteSpace(outputMp4))
        {
            outputMp4 = Path.Combine(AppContext.BaseDirectory, "artifacts", "videos", $"M{job.MatchId}.mp4");
            job.OutputPath = outputMp4;
        }

        string gameDir = Environment.GetEnvironmentVariable("HON_GAME_PATH") ??
                         CommandLineParser.DetectGameDirectory() ??
                         Path.Combine(Directory.GetCurrentDirectory(), "games", "client");
        string vulkanLayerDir = Environment.GetEnvironmentVariable("VULKAN_LAYER_PATH") ??
                                CommandLineParser.DetectVulkanLayerDirectory(gameDir) ??
                                Path.Combine(Directory.GetCurrentDirectory(), "native", "vulkan-layer");

        uint renderWidth = 1920;
        uint renderHeight = 1080;
        if (CommandLineParser.TryParseResolution(job.Resolution, out uint parsedW, out uint parsedH))
        {
            renderWidth = parsedW;
            renderHeight = parsedH;
        }
        else if (!string.IsNullOrWhiteSpace(job.Resolution))
        {
            _logger.LogWarning(
                "[ReplayRendererService] Unrecognized resolution '{Resolution}' for Job {JobId}; defaulting to 1920x1080.",
                job.Resolution, job.JobId);
        }

        int targetFps = job.Framerate > 0 ? job.Framerate : 60;
        bool isAllowedFps = false;
        for (int i = 0; i < ValidAudioDivisorFramerates.Length; i++)
        {
            if (ValidAudioDivisorFramerates[i] == targetFps)
            {
                isAllowedFps = true;
                break;
            }
        }

        if (!isAllowedFps)
        {
            _logger.LogWarning(
                "[ReplayRendererService] Requested framerate {RequestedFps} for Job {JobId} is not an exact divisor of 48000 Hz audio sample rate; coercing to 60 FPS.",
                targetFps, job.JobId);
            targetFps = 60;
        }

        int povClientIndex = -1;
        bool isFreeOrOverviewCam = string.Equals(job.CameraMode, "FreeCam", StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(job.CameraMode, "Spectator", StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(job.CameraMode, "Overview", StringComparison.OrdinalIgnoreCase);

        if (!isFreeOrOverviewCam)
        {
            if (job.TargetPlayerIndex is >= 0 and <= 15)
            {
                povClientIndex = job.TargetPlayerIndex;
            }
            else if (!string.IsNullOrWhiteSpace(job.TargetPlayerName) &&
                     !string.Equals(job.TargetPlayerName, "Free Cam", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    povClientIndex = CommandLineParser.ResolvePlayerSlot(job.TargetPlayerName, job.ReplayFilePath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        "[ReplayRendererService] Could not resolve player '{PlayerName}' for Job {JobId} ({Message}); defaulting PovClientIndex to 0.",
                        job.TargetPlayerName, job.JobId, ex.Message);
                    povClientIndex = 0;
                }
            }
            else
            {
                povClientIndex = 0;
            }
        }

        int startSeconds = Math.Max(0, job.StartMs / 1000);
        TimeSpan? maxDuration = job.DurationMs > 0 ? TimeSpan.FromMilliseconds(job.DurationMs) : null;

        return new RenderJobConfiguration
        {
            ReplayFilePath = job.ReplayFilePath,
            OutputFilePath = outputMp4,
            GameDirectory = gameDir,
            VulkanLayerPath = vulkanLayerDir,
            RenderWidth = renderWidth,
            RenderHeight = renderHeight,
            TargetFramerate = targetFps,
            PovClientIndex = povClientIndex,
            EnableCleanCinematicUi = !job.ShowHud,
            StartSeconds = startSeconds,
            MaxDuration = maxDuration
        };
    }

    protected override async Task ExecuteCoreAsync(CancellationToken stoppingToken)
    {
        if (_initialJobConfig != null)
        {
            _logger.LogInformation("[ReplayRendererService] Executing background render job from configuration...");
            bool success = await ExecuteJobAsync(_initialJobConfig, stoppingToken);
            _logger.LogInformation("[ReplayRendererService] Background render job finished. Success: {Success}",
                success);
            return;
        }

        _logger.LogInformation(
            "[ReplayRendererService] Service started in active daemon mode. Ready to receive render jobs via /render endpoint.");

        while (!stoppingToken.IsCancellationRequested)
        {
            ReplayRenderJob? currentJob = null;
            try
            {
                ReplayRenderJob job = await _jobQueue.DequeueAsync(stoppingToken);
                currentJob = job;
                _logger.LogInformation("[ReplayRendererService] Dequeued render job {JobId} for Match #{MatchId}",
                    job.JobId, job.MatchId);
                job.Status = "Processing";

                Stopwatch stopwatch = Stopwatch.StartNew();
                bool success = false;
                string relativeVideoUrl = $"/videos/M{job.MatchId}.mp4";

                try
                {
                    RenderJobConfiguration config = BuildConfiguration(job);
                    string outputMp4 = config.OutputFilePath;

                    string? targetDir = Path.GetDirectoryName(outputMp4);
                    if (!string.IsNullOrWhiteSpace(targetDir) && !Directory.Exists(targetDir))
                        Directory.CreateDirectory(targetDir);

                    string honExePath = Path.Combine(config.GameDirectory, "hon_x64.exe");
                    if (File.Exists(config.ReplayFilePath) && File.Exists(honExePath))
                    {
                        success = await ExecuteJobAsync(config, stoppingToken);
                        if (!success && LastVerificationResult != null && !LastVerificationResult.IsValid)
                        {
                            job.ErrorMessage = $"Post-export ffprobe verification failed: {LastVerificationResult.FailureReason}";
                        }
                    }
                    else
                    {
                        bool allowHeadlessStub = (_configuration?.GetValue<bool>("ReplayRenderer:AllowHeadlessStub") == true) ||
                            string.Equals(Environment.GetEnvironmentVariable("REPLAY_RENDERER_ALLOW_HEADLESS_STUB"), "1", StringComparison.OrdinalIgnoreCase);

                        if (allowHeadlessStub)
                        {
                            _logger.LogInformation(
                                "[ReplayRendererService] Headless test stub active: synthesizing ISO BMFF fixture for Match #{MatchId} ({ReplayPath}, {HonPath}).",
                                job.MatchId, config.ReplayFilePath, honExePath);

                            if (!File.Exists(outputMp4))
                            {
                                byte[] dummyMp4Header =
                                    [0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70, 0x69, 0x73, 0x6F, 0x6D];
                                await File.WriteAllBytesAsync(outputMp4, dummyMp4Header, stoppingToken);
                            }

                            success = true;
                        }
                        else
                        {
                            string missingTarget = !File.Exists(config.ReplayFilePath)
                                ? $"Replay file not found: '{config.ReplayFilePath}'"
                                : $"HoN executable not found: '{honExePath}'";
                            _logger.LogError("[ReplayRendererService] Render failed: {MissingTarget}", missingTarget);
                            job.ErrorMessage = missingTarget;
                            success = false;
                        }
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "[ReplayRendererService] Error executing render job {JobId} for Match #{MatchId}: {Message}",
                        job.JobId, job.MatchId, ex.Message);
                    job.ErrorMessage = ex.Message;
                    success = false;
                }
                finally
                {
                    stopwatch.Stop();
                    job.CompletedAt = TimeProvider.System.GetUtcNow();
                    job.RenderDurationMs = stopwatch.ElapsedMilliseconds;
                    job.Status = success ? "Completed" : "Failed";

                    _logger.LogInformation(
                        "[ReplayRendererService] Render job {JobId} finished with status '{Status}' in {Duration}ms",
                        job.JobId, job.Status, job.RenderDurationMs);

                    if (!string.IsNullOrWhiteSpace(job.CallbackUrl))
                        await SendCallbackAsync(job, success, relativeVideoUrl, job.RenderDurationMs, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReplayRendererService] Unhandled error in job processing loop: {Message}",
                    ex.Message);
                if (currentJob != null && currentJob.Status == "Processing")
                {
                    currentJob.Status = "Failed";
                    currentJob.ErrorMessage ??= ex.Message;
                    currentJob.CompletedAt ??= TimeProvider.System.GetUtcNow();
                }
            }
        }
    }

    private async Task SendCallbackAsync(ReplayRenderJob job, bool success, string videoUrl, long durationMs,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(job.CallbackUrl)) return;

        try
        {
            _logger.LogInformation(
                "[ReplayRendererService] Sending authenticated render completion callback to {CallbackUrl} for Match #{MatchId}",
                job.CallbackUrl, job.MatchId);

            ReplayRenderCallbackDto payload = new()
            {
                MatchId = job.MatchId,
                Status = success ? "Completed" : "Failed",
                VideoUrl = videoUrl,
                RenderDurationMs = durationMs,
                ErrorMessage = job.ErrorMessage
            };

            string internalServiceKey = _configuration?["InternalServiceKey"] ??
                                        Environment.GetEnvironmentVariable("InternalServiceKey") ??
                                        SecurityConstants.DefaultInternalServiceKey;

            using HttpClient? ownedClient = _httpClientFactory == null ? SharedSocketsHttpClientFactory.Create() : null;
            HttpClient client = _httpClientFactory?.CreateClient("ReplayRendererCallback") ?? ownedClient!;
            using CancellationTokenSource timeoutCts =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

            using HttpRequestMessage requestMessage = new(HttpMethod.Post, job.CallbackUrl)
            {
                Content = JsonContent.Create(payload)
            };
            requestMessage.Headers.TryAddWithoutValidation(
                SecurityConstants.InternalServiceKeyHeader,
                internalServiceKey);

            using HttpResponseMessage response = await client.SendAsync(requestMessage, timeoutCts.Token);

            if (response.IsSuccessStatusCode)
                _logger.LogInformation(
                    "[ReplayRendererService] Callback to {CallbackUrl} delivered successfully ({StatusCode})",
                    job.CallbackUrl, response.StatusCode);
            else
                _logger.LogWarning(
                    "[ReplayRendererService] Callback to {CallbackUrl} returned non-success code {StatusCode}",
                    job.CallbackUrl, response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[ReplayRendererService] Failed to send completion callback to {CallbackUrl} for Match #{MatchId}: {Message}",
                job.CallbackUrl, job.MatchId, ex.Message);
        }
    }

    /// <summary>
    ///     Executes a deterministic replay rendering job from start to finish.
    /// </summary>
    public async Task<bool> ExecuteJobAsync(RenderJobConfiguration config, CancellationToken cancellationToken)
    {
        config.Validate();
        LastVerificationResult = null;

        string jobId = Guid.NewGuid().ToString("N");
        string framePipeName = $"hon_frame_pipe_{jobId}";
        string audioPipeName = $"hon_audio_pipe_{jobId}";
        string willowmakerPipeName = config.IpcPipeName ?? $"hon_willowmaker_pipe_{jobId}";
        config.IpcPipeName = willowmakerPipeName;

        _telemetryLogger.LogJobBanner(config, jobId);
        _telemetryLogger.LogSystemDiagnostics();

        using WindowsJobObjectSandbox jobSandbox = new(_loggerFactory.CreateLogger<WindowsJobObjectSandbox>());
        await using ReplayProcessLauncher processLauncher =
            new(_loggerFactory.CreateLogger<ReplayProcessLauncher>(), jobSandbox);

        AudioFrameSyncBuffer audioBuffer = new(
            _loggerFactory.CreateLogger<AudioFrameSyncBuffer>(),
            config.AudioSampleRate,
            config.AudioChannels,
            config.AudioBitsPerSample,
            config.TargetFramerate);

        await using WasapiLoopbackAudioCapture audioCapture = new(
            _loggerFactory.CreateLogger<WasapiLoopbackAudioCapture>(),
            audioBuffer,
            audioPipeName);

        await using NamedPipeFrameServer frameServer = new(
            _loggerFactory.CreateLogger<NamedPipeFrameServer>(),
            framePipeName,
            config.RenderWidth,
            config.RenderHeight);

        await using FfmpegPipelineController ffmpegController = new(
            _loggerFactory.CreateLogger<FfmpegPipelineController>(),
            jobSandbox);

        await using RenderProgressMonitor progressMonitor = new(
            _loggerFactory.CreateLogger<RenderProgressMonitor>(),
            jobId,
            null,
            config.StallTimeout);

        using CancellationTokenSource jobCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Attach watchdog stall trigger
        progressMonitor.OnStallDetected += () =>
        {
            _logger.LogError(
                "[ReplayRendererService] CRITICAL: Rendering stall detected by watchdog. Aborting job {JobId}.", jobId);
            jobCts.Cancel();
        };

        // Attach engine replay completed trigger
        processLauncher.OnReplayCompleted += () =>
        {
            _logger.LogInformation(
                "[ReplayRendererService] Engine signaled end of replay stream. Preparing for finalization.");
        };

        try
        {
            // 0. Start WILLOWMAKER IPC Pipe Bridge Server for hon_x64 engine handshake
            string replayFileName = Path.GetFileName(config.ReplayFilePath);
            await using WillowmakerIpcBridge willowmakerIpc = new(
                _loggerFactory.CreateLogger<WillowmakerIpcBridge>(),
                replayFileName,
                willowmakerPipeName,
                config.StartSeconds);
            await willowmakerIpc.StartAsync(jobCts.Token);

            // 1. Start WASAPI Audio Pipe Server
            await audioCapture.StartAsync(jobCts.Token);

            // 2. Start Video Frame Named Pipe Server
            await frameServer.StartAsync(null, jobCts.Token);

            // 3. Start FFmpeg Encoding Pipeline
            FfmpegEncodingOptions encodingOptions = new()
            {
                VideoInputSource = frameServer.PipePath,
                AudioInputSource = audioCapture.PipePath,
                OutputFilePath = config.OutputFilePath,
                Width = config.RenderWidth,
                Height = config.RenderHeight,
                Framerate = config.TargetFramerate,
                Codec = config.Codec,
                AudioSampleRate = config.AudioSampleRate,
                AudioChannels = config.AudioChannels,
                AudioBitrateKbps = config.AudioBitrateKbps,
                ConstantQuality = config.ConstantQuality,
                VideoBitrate = config.VideoBitrate,
                MaxBitrate = config.MaxBitrate,
                BufferSize = config.BufferSize
            };

            ffmpegController.Start(encodingOptions, config.FfmpegExecutablePath);

            // 4. Launch HoN Game Engine inside Win32 Job Object
            processLauncher.Launch(config, framePipeName);

            VulkanMemoryMappedReceiver? memoryMappedReceiver = null;
            WindowFrameCapturer? windowFrameCapturer = null;

            if (OperatingSystem.IsWindows() && processLauncher.ProcessId.HasValue)
            {
                bool targetReached = false;
                DateTimeOffset launchTime = TimeProvider.System.GetUtcNow();
                long targetFrames = config.MaxDuration.HasValue
                    ? (long)(config.MaxDuration.Value.TotalSeconds * config.TargetFramerate)
                    : long.MaxValue;

                if (config.UseSharedMemoryIpc)
                {
                    _logger.LogInformation(
                        "[ReplayRendererService] Attaching Vulkan MemoryMappedReceiver to HoN PID {PID}...",
                        processLauncher.ProcessId.Value);
                    memoryMappedReceiver = new VulkanMemoryMappedReceiver(
                        _loggerFactory.CreateLogger<VulkanMemoryMappedReceiver>(),
                        processLauncher.ProcessId.Value,
                        config.RenderWidth,
                        config.RenderHeight);

                    await memoryMappedReceiver.StartAsync(async (header, frameData) =>
                    {
                        if (targetReached) return;

                        if (!processLauncher.IsReplayWorldLoaded)
                        {
                            return;
                        }

                        progressMonitor.NotifyFrameRendered();

                        ValueTask videoTask = frameServer.WriteFrameAsync(frameData, jobCts.Token);
                        ValueTask audioTask = audioCapture.WriteAudioFrameAsync(jobCts.Token);

                        await videoTask;
                        await audioTask;

                        if (progressMonitor.TotalFramesRendered >= targetFrames)
                        {
                            targetReached = true;
                            progressMonitor.Stop();
                            _logger.LogInformation(
                                "[ReplayRendererService] Reached target duration {Duration:F1}s ({Frames} frames). Stopping replay session...",
                                config.MaxDuration!.Value.TotalSeconds, progressMonitor.TotalFramesRendered);
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await willowmakerIpc.SendCommandAsync("Quit");
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogDebug(ex, "[ReplayRendererService] Non-fatal IPC Quit notice during shutdown.");
                                }

                                await processLauncher.StopAsync(TimeSpan.FromSeconds(3));
                            });
                        }
                    }, jobCts.Token);
                }
                else
                {
                    _logger.LogInformation(
                        "[ReplayRendererService] Attaching Window Frame Capturer to HoN PID {PID} (AllowForegroundActivation={AllowFg})...",
                        processLauncher.ProcessId.Value, config.AllowForegroundActivation);
                    windowFrameCapturer = new WindowFrameCapturer(
                        _loggerFactory.CreateLogger<WindowFrameCapturer>(),
                        processLauncher.ProcessId.Value,
                        config.RenderWidth,
                        config.RenderHeight,
                        config.TargetFramerate,
                        config.AllowForegroundActivation);

                    await windowFrameCapturer.StartAsync(async frameData =>
                    {
                        if (targetReached) return;

                        if (!processLauncher.IsReplayWorldLoaded)
                        {
                            return;
                        }

                        progressMonitor.NotifyFrameRendered();
                        ValueTask videoTask = frameServer.WriteFrameAsync(frameData, jobCts.Token);
                        ValueTask audioTask = audioCapture.WriteAudioFrameAsync(jobCts.Token);

                        await videoTask;
                        await audioTask;

                        if (progressMonitor.TotalFramesRendered >= targetFrames)
                        {
                            targetReached = true;
                            progressMonitor.Stop();
                            _logger.LogInformation(
                                "[ReplayRendererService] Reached target duration {Duration:F1}s ({Frames} frames). Stopping replay session...",
                                config.MaxDuration!.Value.TotalSeconds, progressMonitor.TotalFramesRendered);
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await willowmakerIpc.SendCommandAsync("Quit");
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogDebug(ex, "[ReplayRendererService] Non-fatal IPC Quit notice during shutdown.");
                                }

                                await processLauncher.StopAsync(TimeSpan.FromSeconds(3));
                            });
                        }
                    }, jobCts.Token);
                }
            }

            // 5. Start Watchdog & Progress Monitor
            progressMonitor.Start();

            // 6. Await Engine Execution
            _logger.LogInformation(
                "[ReplayRendererService] Rendering pipeline active. Awaiting HoN replay completion...");
            int exitCode = await processLauncher.ExitTask.WaitAsync(jobCts.Token);
            progressMonitor.Stop();
            _logger.LogInformation("[ReplayRendererService] HoN engine exited with code {ExitCode}.", exitCode);

            if (OperatingSystem.IsWindows())
            {
                if (memoryMappedReceiver != null) await memoryMappedReceiver.DisposeAsync();
                if (windowFrameCapturer != null)
                {
                    _logger.LogInformation(
                        "[ReplayRendererService] WindowFrameCapturer completed: Frames={Frames}, PrintWindow={Pw}, ZeroWindowExposure={ZeroExposure}.",
                        windowFrameCapturer.TotalFramesCaptured,
                        windowFrameCapturer.PrintWindowSuccessCount,
                        windowFrameCapturer.ZeroWindowExposureEnforced);
                    await windowFrameCapturer.DisposeAsync();
                }
            }

            // Flush and wait for FFmpeg to consume all buffered frames from video pipe
            await frameServer.WaitForDrainAsync(TimeSpan.FromSeconds(5));

            // Close frame and audio pipe inputs to send clean EOF to FFmpeg
            await frameServer.DisposeAsync();
            await audioCapture.DisposeAsync();
            await willowmakerIpc.DisposeAsync();

            // 7. Flush and finalize FFmpeg container
            await ffmpegController.StopAsync(TimeSpan.FromSeconds(30));

            int? ffmpegExitCode = ffmpegController.ExitTask.IsCompletedSuccessfully
                ? await ffmpegController.ExitTask
                : null;
            if (ffmpegExitCode.HasValue && ffmpegExitCode.Value != 0)
            {
                _logger.LogError("[ReplayRendererService] FFmpeg encoder exited with non-zero code {ExitCode} for '{Output}'.",
                    ffmpegExitCode.Value, config.OutputFilePath);
            }

            // 8. Log final summary report
            _telemetryLogger.LogJobSummary(
                config,
                progressMonitor.TotalFramesRendered,
                progressMonitor.ElapsedTime,
                audioBuffer.UnderrunFramesCount,
                frameServer.TotalBytesReceived);

            // 9. Post-export ffprobe verification (fail-closed)
            if (config.VerifyOutputWithFfprobe)
            {
                FfprobeVideoVerifier verifier = new(_loggerFactory.CreateLogger<FfprobeVideoVerifier>());
                FfprobeVerificationOptions verifyOptions = new()
                {
                    ExpectedCodecName = FfprobeVideoVerifier.GetExpectedCodecName(config.Codec),
                    ExpectedWidth = (int)config.RenderWidth,
                    ExpectedHeight = (int)config.RenderHeight,
                    MinimumFrameCount = 1,
                    MinimumDurationSeconds = 0.001,
                    MinimumFileSizeBytes = 1,
                    FfprobeExecutablePath = config.FfprobeExecutablePath,
                    FfmpegExecutablePath = config.FfmpegExecutablePath
                };

                FfprobeVideoVerificationResult probeResult = await verifier.VerifyAsync(
                    config.OutputFilePath,
                    verifyOptions,
                    cancellationToken);
                LastVerificationResult = probeResult;

                if (!probeResult.IsValid)
                {
                    _logger.LogError(
                        "[ReplayRendererService] Post-export ffprobe verification FAILED for '{Output}': {Summary}",
                        config.OutputFilePath, probeResult.Summary);
                    return false;
                }

                _logger.LogInformation(
                    "[ReplayRendererService] Post-export ffprobe verification PASSED for '{Output}': {Summary}",
                    config.OutputFilePath, probeResult.Summary);
                return ffmpegExitCode is null or 0;
            }

            return File.Exists(config.OutputFilePath) &&
                   new FileInfo(config.OutputFilePath).Length > 0 &&
                   ffmpegExitCode is null or 0;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("[ReplayRendererService] Render job {JobId} was canceled or aborted by watchdog.",
                jobId);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ReplayRendererService] Fatal error during render job {JobId}: {Message}", jobId,
                ex.Message);
            return false;
        }
        finally
        {
            progressMonitor.Stop();
            await frameServer.DisposeAsync();
            await audioCapture.DisposeAsync();
        }
    }
}
