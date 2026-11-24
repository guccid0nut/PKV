// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: VideoExportPipeline.cs
// System: Replay Studio Desktop GUI Tool
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReplayRenderer.Service.Encoding;
using ReplayStudio.GUI.Common;
using ReplayStudio.GUI.Director;
using ReplayStudio.GUI.Engine;
using ReplayStudio.GUI.Models;

namespace ReplayStudio.GUI.Export;

/// <summary>
///     Hardware-accelerated video & audio export pipeline.
///     Supports genuine offscreen <c>hon_x64.exe</c> engine rendering (<see cref="ReplayExportBackendMode.EngineHeadless" />)
///     via <see cref="EngineReplayExportAdapter" /> as well as explicit <see cref="ReplayExportBackendMode.SoftwarePreview" />
///     streaming into FFmpeg with <see cref="FfprobeVideoVerifier" /> post-export container verification.
/// </summary>
public sealed class VideoExportPipeline
{
    private readonly EngineReplayExportAdapter _engineAdapter;
    private readonly ILogger<VideoExportPipeline> _logger;
    private readonly FfprobeVideoVerifier _videoVerifier;

    public VideoExportPipeline(
        ILogger<VideoExportPipeline>? logger = null,
        EngineReplayExportAdapter? engineAdapter = null,
        FfprobeVideoVerifier? videoVerifier = null)
    {
        _logger = logger ?? NullLogger<VideoExportPipeline>.Instance;
        _videoVerifier = videoVerifier ?? new FfprobeVideoVerifier();
        _engineAdapter = engineAdapter ?? new EngineReplayExportAdapter(verifier: _videoVerifier);
    }

    /// <summary>
    ///     Builds production-grade FFmpeg command line arguments matching hardware encoder standards.
    /// </summary>
    public static string BuildFfmpegArguments(
        ExportParameters parameters,
        string videoInput = "-",
        string? audioInput = null)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        string videoInputArgs = videoInput.Equals("-", StringComparison.Ordinal)
            ? $"-f rawvideo -vcodec rawvideo -pix_fmt bgra -s {parameters.Width}x{parameters.Height} -r {parameters.TargetFps} -i -"
            : $"-f rawvideo -vcodec rawvideo -pix_fmt bgra -s {parameters.Width}x{parameters.Height} -r {parameters.TargetFps} -thread_queue_size 1024 -i \"{videoInput}\"";

        string audioInputArgs;
        if (!string.IsNullOrWhiteSpace(audioInput))
        {
            audioInputArgs = $"-f wav -thread_queue_size 1024 -i \"{audioInput}\"";
        }
        else if (parameters.IncludeAudio)
        {
            audioInputArgs = $"-f lavfi -i anullsrc=channel_layout=stereo:sample_rate={parameters.AudioSampleRate}";
        }
        else
        {
            audioInputArgs = string.Empty;
        }

        int gopSize = parameters.TargetFps * 2; // Exactly 2.0-second GOP length

        string videoEncodingArgs = parameters.Codec switch
        {
            ExportVideoCodec.H264Nvenc =>
                $"-c:v h264_nvenc -preset p7 -tune hq -rc vbr -cq {parameters.ConstantQuality} -b:v {parameters.VideoBitrate} -maxrate {parameters.MaxBitrate} -bufsize {parameters.BufferSize} -spatial-aq 1 -temporal-aq 1 -rc-lookahead 32 -g {gopSize} -bf 3 -b_ref_mode middle -pix_fmt yuv420p",

            ExportVideoCodec.HevcNvenc =>
                $"-c:v hevc_nvenc -preset p7 -tune hq -rc vbr -cq {Math.Max(10, parameters.ConstantQuality - 2)} -b:v {parameters.VideoBitrate} -maxrate {parameters.MaxBitrate} -bufsize {parameters.BufferSize} -spatial-aq 1 -temporal-aq 1 -rc-lookahead 32 -g {gopSize} -bf 3 -b_ref_mode middle -pix_fmt yuv420p",

            ExportVideoCodec.H264Qsv =>
                $"-c:v h264_qsv -preset veryslow -global_quality {parameters.ConstantQuality} -b:v {parameters.VideoBitrate} -maxrate {parameters.MaxBitrate} -bufsize {parameters.BufferSize} -g {gopSize} -pix_fmt nv12",

            ExportVideoCodec.H264Amf =>
                $"-c:v h264_amf -quality quality -rc cbr -b:v {parameters.VideoBitrate} -maxrate {parameters.MaxBitrate} -bufsize {parameters.BufferSize} -g {gopSize} -pix_fmt nv12",

            _ =>
                $"-c:v libx264 -preset slow -crf {parameters.ConstantQuality} -g {gopSize} -pix_fmt yuv420p"
        };

        string audioEncodingArgs = parameters.IncludeAudio
            ? $"-c:a aac -b:a {parameters.AudioBitrateKbps}k -ar {parameters.AudioSampleRate} -ac {parameters.AudioChannels}"
            : "-an";

        string containerArgs = "-shortest -movflags +faststart";
        string fullOutputPath = Path.GetFullPath(parameters.OutputFilePath);

        string combinedInputs = string.IsNullOrWhiteSpace(audioInputArgs)
            ? videoInputArgs
            : $"{videoInputArgs} {audioInputArgs}";

        return $"-y {combinedInputs} {videoEncodingArgs} {audioEncodingArgs} {containerArgs} \"{fullOutputPath}\"";
    }

    /// <summary>
    ///     Executes video export for a replay session using the active snapshot engine and procedural frame renderer.
    /// </summary>
    public async Task<ExportResult> ExportReplayAsync(
        ExportParameters parameters,
        ReplaySnapshotEngine snapshotEngine,
        CameraDirector cameraDirector,
        LightingController lightingController,
        MapEnvironmentManager mapManager,
        IReadOnlyList<CombatEventMarker>? combatEvents = null,
        IProgress<ExportProgress>? progress = null,
        Stream? customOutputStream = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(snapshotEngine);
        ArgumentNullException.ThrowIfNull(cameraDirector);
        ArgumentNullException.ThrowIfNull(lightingController);
        ArgumentNullException.ThrowIfNull(mapManager);

        _logger.LogInformation(
            "[VideoExportPipeline] Starting replay export (Backend={Backend}, Preset={Preset}, In={In}ms, Out={Out}ms, Duration={Dur}ms, Frames={Frames})...",
            parameters.BackendMode, parameters.Preset.DisplayName, parameters.InMarkerMs, parameters.OutMarkerMs, parameters.TotalDurationMs, parameters.TotalFrames);

        if (parameters.BackendMode == ReplayExportBackendMode.EngineHeadless)
        {
            _logger.LogInformation(
                "[VideoExportPipeline] Routing ExportReplayAsync to offscreen EngineHeadless backend (Replay='{Replay}', Output='{Output}').",
                parameters.ReplayFilePath ?? "<none>", parameters.OutputFilePath);
            return await _engineAdapter.ExportAsync(parameters, progress, ct);
        }

        // Frame rendering callback
        async Task RenderReplayFrame(int timestampMs, NativeBufferRing ring)
        {
            GameStateSnapshot snapshot = snapshotEngine.GetInterpolatedSnapshot(timestampMs);

            // Compute camera
            Vector3 heroPos = Vector3.Zero;
            float heroHeading = 0f;
            if (cameraDirector.Mode == CameraMode.PlayerPov || cameraDirector.Mode == CameraMode.FirstPerson)
            {
                HeroSnapshot hero = snapshot.Heroes[0];
                if (hero != null)
                {
                    heroPos = hero.Position;
                    heroHeading = hero.HeadingAngle;
                }
            }

            float dt = 1.0f / Math.Max(1, parameters.TargetFps);
            float aspectRatio = (float)parameters.Width / Math.Max(1, parameters.Height);
            cameraDirector.Update(dt, heroPos, heroHeading, timestampMs, aspectRatio);

            // Unmanaged CPU procedural render pass
            RenderProceduralFrame(ring, snapshot, timestampMs, parameters.TargetFps);

            await Task.CompletedTask;
        }

        return await ExportVideoAsync(
            parameters,
            RenderReplayFrame,
            combatEvents,
            progress,
            customOutputStream,
            ct);
    }

    /// <summary>
    ///     Executes video export streaming uncompressed frames from a frame generator callback into FFmpeg stdin.
    /// </summary>
    public async Task<ExportResult> ExportVideoAsync(
        ExportParameters parameters,
        Func<int, NativeBufferRing, Task>? renderFrameCallback,
        IReadOnlyList<CombatEventMarker>? combatEvents = null,
        IProgress<ExportProgress>? progress = null,
        Stream? customOutputStream = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        if (parameters.BackendMode == ReplayExportBackendMode.EngineHeadless)
        {
            _logger.LogInformation(
                "[VideoExportPipeline] Routing ExportVideoAsync to offscreen EngineHeadless backend (Replay='{Replay}', Output='{Output}').",
                parameters.ReplayFilePath ?? "<none>", parameters.OutputFilePath);
            return await _engineAdapter.ExportAsync(parameters, progress, ct);
        }

        parameters.Validate();

        int totalFrames = parameters.TotalFrames;
        if (totalFrames <= 0)
        {
            throw new InvalidOperationException("Total frames to export must be strictly positive.");
        }

        string? outputDir = Path.GetDirectoryName(parameters.OutputFilePath);
        if (!string.IsNullOrWhiteSpace(outputDir) && !Directory.Exists(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        string? tempAudioPath = null;
        Process? ffmpegProcess = null;
        ConcurrentQueue<string> ffmpegStderrTail = new();
        Stream? targetStream = customOutputStream;
        Stopwatch totalSw = Stopwatch.StartNew();
        Stopwatch frameWindowSw = Stopwatch.StartNew();

        int renderedFrames = 0;
        int windowFrameCount = 0;
        double currentFps = parameters.TargetFps;

        using NativeBufferRing ring = new(parameters.Width, parameters.Height);
        using AudioStreamSyncBuffer audioBuffer = new(
            sampleRate: parameters.AudioSampleRate,
            channels: parameters.AudioChannels,
            bitsPerSample: 16,
            targetFps: parameters.TargetFps);

        try
        {
            // 1. Synthesize audio track if audio is enabled
            if (parameters.IncludeAudio)
            {
                byte[] audioPcm = audioBuffer.SynthesizeAudioTrack(
                    parameters.InMarkerMs,
                    parameters.OutMarkerMs,
                    parameters.TargetFps,
                    combatEvents);

                tempAudioPath = Path.Combine(Path.GetTempPath(), $"replay_studio_audio_{Guid.NewGuid():N}.wav");
                AudioStreamSyncBuffer.SaveToWavFile(tempAudioPath, audioPcm, parameters.AudioSampleRate, parameters.AudioChannels, 16);
                _logger.LogInformation("[VideoExportPipeline] Prepared temporary audio track: '{Path}' ({Bytes} bytes)", tempAudioPath, audioPcm.Length);
            }

            // 2. Launch FFmpeg process or use custom stream
            string ffmpegArgs = BuildFfmpegArguments(parameters, "-", tempAudioPath);
            _logger.LogInformation("[VideoExportPipeline] Launching FFmpeg with args: {Args}", ffmpegArgs);

            bool isSimulatedPipe = false;
            if (targetStream == null)
            {
                ffmpegProcess = TryLaunchFfmpeg(parameters.FfmpegExecutablePath, ffmpegArgs, ffmpegStderrTail);
                if (ffmpegProcess != null)
                {
                    targetStream = ffmpegProcess.StandardInput.BaseStream;
                    _logger.LogInformation("[VideoExportPipeline] Connected to FFmpeg stdin stream (PID: {Pid})", ffmpegProcess.Id);
                }
                else if (parameters.AllowSimulatedFallback)
                {
                    _logger.LogWarning(
                        "[VideoExportPipeline] FFmpeg executable '{Path}' not found; AllowSimulatedFallback=true enabled headless sink.",
                        parameters.FfmpegExecutablePath);
                    isSimulatedPipe = true;
                    targetStream = new MemoryStream();
                }
                else
                {
                    totalSw.Stop();
                    string missingMsg =
                        $"FFmpeg executable '{parameters.FfmpegExecutablePath}' was not found or failed to start. " +
                        "Install FFmpeg on PATH or specify FfmpegExecutablePath to export MP4 video (Replay Studio software preview renderer is experimental; refusing to write placeholder file).";
                    _logger.LogError("[VideoExportPipeline] {Message}", missingMsg);
                    return new ExportResult
                    {
                        Success = false,
                        BackendMode = ReplayExportBackendMode.SoftwarePreview,
                        OutputFilePath = parameters.OutputFilePath,
                        TotalFramesRendered = 0,
                        Duration = totalSw.Elapsed,
                        OutputSizeBytes = 0,
                        AverageFps = 0.0,
                        ErrorMessage = missingMsg
                    };
                }
            }

            // 3. Frame streaming loop
            for (int i = 0; i < totalFrames; i++)
            {
                ct.ThrowIfCancellationRequested();

                int frameTimestampMs = parameters.GetFrameTimestampMs(i);

                // Render frame into ring buffer
                if (renderFrameCallback != null)
                {
                    await renderFrameCallback(frameTimestampMs, ring);
                }
                else
                {
                    RenderProceduralFrame(ring, null, frameTimestampMs, parameters.TargetFps);
                }

                // Stream uncompressed front buffer bytes into targetStream
                UnmanagedMemoryStream frameStream;
                unsafe
                {
                    frameStream = new UnmanagedMemoryStream(ring.FrontBufferPtr, ring.BufferSizeBytes);
                }
                using (frameStream)
                {
                    await frameStream.CopyToAsync(targetStream, 65536, ct);
                }

                renderedFrames++;
                windowFrameCount++;

                // Compute real-time FPS & ETA every 5 frames or at the end
                if (frameWindowSw.ElapsedMilliseconds >= 100 || renderedFrames == totalFrames)
                {
                    double windowSec = frameWindowSw.Elapsed.TotalSeconds;
                    if (windowSec > 0.001)
                    {
                        currentFps = windowFrameCount / windowSec;
                    }
                    frameWindowSw.Restart();
                    windowFrameCount = 0;

                    int remainingFrames = totalFrames - renderedFrames;
                    double etaSec = currentFps > 0.1 ? remainingFrames / currentFps : 0.0;
                    double percent = (double)renderedFrames / totalFrames * 100.0;

                    ExportProgress prog = new()
                    {
                        RenderedFrames = renderedFrames,
                        TotalFrames = totalFrames,
                        PercentComplete = percent,
                        CurrentFps = currentFps,
                        EtaSeconds = etaSec,
                        Elapsed = totalSw.Elapsed,
                        CurrentTimestampMs = frameTimestampMs,
                        StatusText = $"Encoding frames: {renderedFrames}/{totalFrames} ({percent:F0}%)"
                    };

                    progress?.Report(prog);

                    if (renderedFrames % Math.Max(1, totalFrames / 10) == 0 || renderedFrames == totalFrames)
                    {
                        _logger.LogInformation(
                            "[VideoExportPipeline] Progress: {Rendered}/{Total} ({Percent:F1}%) • {Fps:F1} FPS • ETA: {Eta:F1}s",
                            renderedFrames, totalFrames, percent, currentFps, etaSec);
                    }
                }
            }

            FfprobeVideoVerificationResult? verificationResult = null;

            // Flush & close stdin
            if (ffmpegProcess != null)
            {
                await targetStream.FlushAsync(ct);
                ffmpegProcess.StandardInput.Close();
                _logger.LogInformation("[VideoExportPipeline] Frame stream closed. Awaiting FFmpeg process exit...");

                using CancellationTokenSource processCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                processCts.CancelAfter(TimeSpan.FromSeconds(30));
                await ffmpegProcess.WaitForExitAsync(processCts.Token);

                int ffmpegExitCode = ffmpegProcess.ExitCode;
                if (ffmpegExitCode != 0)
                {
                    totalSw.Stop();
                    string stderrSummary = string.Join(" | ", ffmpegStderrTail.ToArray());
                    string exitError =
                        $"FFmpeg exited with non-zero code {ffmpegExitCode} while encoding '{parameters.OutputFilePath}'. Stderr: {stderrSummary}";
                    _logger.LogError("[VideoExportPipeline] {Error}", exitError);
                    DeleteFileSafe(parameters.OutputFilePath);
                    return new ExportResult
                    {
                        Success = false,
                        BackendMode = ReplayExportBackendMode.SoftwarePreview,
                        OutputFilePath = parameters.OutputFilePath,
                        TotalFramesRendered = renderedFrames,
                        Duration = totalSw.Elapsed,
                        OutputSizeBytes = 0,
                        AverageFps = 0.0,
                        ErrorMessage = exitError
                    };
                }

                if (parameters.VerifyOutputWithFfprobe)
                {
                    FfprobeVerificationOptions verifyOptions = new()
                    {
                        ExpectedCodecName = EngineReplayExportAdapter.GetExpectedFfprobeCodecName(parameters.Codec),
                        ExpectedWidth = parameters.Width,
                        ExpectedHeight = parameters.Height,
                        MinimumFrameCount = 1,
                        MinimumDurationSeconds = 0.001,
                        MinimumFileSizeBytes = 1,
                        FfprobeExecutablePath = parameters.FfprobeExecutablePath,
                        FfmpegExecutablePath = parameters.FfmpegExecutablePath
                    };

                    verificationResult = await _videoVerifier.VerifyAsync(
                        parameters.OutputFilePath,
                        verifyOptions,
                        ct);

                    if (!verificationResult.IsValid)
                    {
                        totalSw.Stop();
                        string verifyError =
                            $"Post-export ffprobe verification failed for '{parameters.OutputFilePath}': {verificationResult.FailureReason}";
                        _logger.LogError("[VideoExportPipeline] {Error}", verifyError);
                        return new ExportResult
                        {
                            Success = false,
                            BackendMode = ReplayExportBackendMode.SoftwarePreview,
                            OutputFilePath = parameters.OutputFilePath,
                            TotalFramesRendered = renderedFrames,
                            Duration = totalSw.Elapsed,
                            OutputSizeBytes = 0,
                            AverageFps = 0.0,
                            ErrorMessage = verifyError,
                            VerificationResult = verificationResult
                        };
                    }
                }
            }
            else if (isSimulatedPipe)
            {
                await targetStream.FlushAsync(ct);
                // In simulated mode, create mock output file if path is provided
                if (!string.IsNullOrWhiteSpace(parameters.OutputFilePath))
                {
                    byte[] mockContent = Encoding.UTF8.GetBytes($"[REPLAY_STUDIO_VIDEO_EXPORT_MP4_CONTAINER_{parameters.Width}x{parameters.Height}_{totalFrames}_FRAMES]");
                    await File.WriteAllBytesAsync(parameters.OutputFilePath, mockContent, ct);
                }
            }

            totalSw.Stop();
            long outputSize = File.Exists(parameters.OutputFilePath) ? new FileInfo(parameters.OutputFilePath).Length : 0;
            double avgFps = totalSw.Elapsed.TotalSeconds > 0 ? (double)renderedFrames / totalSw.Elapsed.TotalSeconds : 0.0;

            _logger.LogInformation(
                "[VideoExportPipeline] Export completed successfully! Backend={Backend}, Total frames: {Frames}, Elapsed: {Elapsed:F2}s, Avg FPS: {Fps:F1}, Size: {Size:F2} MB, Verification: {Verification}",
                ReplayExportBackendMode.SoftwarePreview, renderedFrames, totalSw.Elapsed.TotalSeconds, avgFps,
                (double)outputSize / (1024.0 * 1024.0), verificationResult?.Summary ?? "not run (custom/simulated stream)");

            return new ExportResult
            {
                Success = true,
                BackendMode = ReplayExportBackendMode.SoftwarePreview,
                OutputFilePath = parameters.OutputFilePath,
                TotalFramesRendered = renderedFrames,
                Duration = totalSw.Elapsed,
                OutputSizeBytes = outputSize,
                AverageFps = avgFps,
                ErrorMessage = null,
                VerificationResult = verificationResult
            };
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[VideoExportPipeline] Video export cancelled by user after {Frames} frames", renderedFrames);
            KillProcessSafe(ffmpegProcess);
            DeleteFileSafe(parameters.OutputFilePath);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[VideoExportPipeline] Video export failed: {Message}", ex.Message);
            KillProcessSafe(ffmpegProcess);
            DeleteFileSafe(parameters.OutputFilePath);
            return new ExportResult
            {
                Success = false,
                BackendMode = ReplayExportBackendMode.SoftwarePreview,
                OutputFilePath = parameters.OutputFilePath,
                TotalFramesRendered = renderedFrames,
                Duration = totalSw.Elapsed,
                OutputSizeBytes = 0,
                AverageFps = 0.0,
                ErrorMessage = ex.Message
            };
        }
        finally
        {
            if (ffmpegProcess != null)
            {
                ffmpegProcess.Dispose();
            }

            if (!string.IsNullOrWhiteSpace(tempAudioPath))
            {
                DeleteFileSafe(tempAudioPath);
            }
        }
    }

    private Process? TryLaunchFfmpeg(string executablePath, string arguments, ConcurrentQueue<string> stderrTail)
    {
        try
        {
            ProcessStartInfo psi = new()
            {
                FileName = executablePath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            Process process = new() { StartInfo = psi };
            if (process.Start())
            {
                // Drain stderr asynchronously to prevent buffer deadlock
                process.ErrorDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        _logger.LogTrace("[FFmpeg] {Stderr}", e.Data);
                        stderrTail.Enqueue(e.Data);
                        while (stderrTail.Count > 25)
                        {
                            stderrTail.TryDequeue(out _);
                        }
                    }
                };
                process.BeginErrorReadLine();
                return process;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[VideoExportPipeline] Could not spawn FFmpeg process ({Path}): {Message}", executablePath, ex.Message);
        }

        return null;
    }

    private static void RenderProceduralFrame(NativeBufferRing ring, GameStateSnapshot? snapshot, int timestampMs, int fps)
    {
        unsafe
        {
            IntPtr backAddr = ring.AcquireBackBuffer();
            uint* pixelPtr = (uint*)backAddr;
            int width = ring.Width;
            int height = ring.Height;
            int totalPixels = width * height;

            // Generate stylized dark glassmorphic grid background with animated color waves
            double t = (double)timestampMs / 1000.0;
            byte r = (byte)(15 + 10 * Math.Sin(t * 1.5));
            byte g = (byte)(23 + 15 * Math.Cos(t * 1.2));
            byte b = (byte)(42 + 20 * Math.Sin(t * 0.8));
            uint bgPixel = (uint)(r | (g << 8) | (b << 16) | (255 << 24));

            for (int i = 0; i < totalPixels; i++)
            {
                pixelPtr[i] = bgPixel;
            }

            // Draw center crosshair and animated pulse marker
            int centerX = width / 2;
            int centerY = height / 2;
            int radius = (int)(20 + 10 * Math.Sin(t * 4.0));
            uint crosshairColor = 0xFF38BDF8; // Cyan (#38BDF8)

            for (int dx = -radius; dx <= radius; dx++)
            {
                int x = centerX + dx;
                if (x >= 0 && x < width && centerY >= 0 && centerY < height)
                {
                    pixelPtr[centerY * width + x] = crosshairColor;
                }
            }

            for (int dy = -radius; dy <= radius; dy++)
            {
                int y = centerY + dy;
                if (centerX >= 0 && centerX < width && y >= 0 && y < height)
                {
                    pixelPtr[y * width + centerX] = crosshairColor;
                }
            }

            ring.CommitBackBuffer();
        }
    }

    private void KillProcessSafe(Process? process)
    {
        if (process != null && !process.HasExited)
        {
            try
            {
                process.Kill(true);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[VideoExportPipeline] Cleanup notice while terminating FFmpeg process: {Message}", ex.Message);
            }
        }
    }

    private void DeleteFileSafe(string? filePath)
    {
        if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
        {
            try
            {
                File.Delete(filePath);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[VideoExportPipeline] Cleanup notice while deleting temporary file '{Path}': {Message}", filePath, ex.Message);
            }
        }
    }
}

