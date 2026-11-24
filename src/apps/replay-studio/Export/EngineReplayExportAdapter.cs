// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: EngineReplayExportAdapter.cs
// System: Replay Studio Desktop GUI Tool
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReplayRenderer.Service;
using ReplayRenderer.Service.Configuration;
using ReplayRenderer.Service.Encoding;
using ReplayRenderer.Service.Models;
using ReplayRenderer.Service.Monitoring;

namespace ReplayStudio.GUI.Export;

/// <summary>
///     Bridges Replay Studio export parameters into the real offscreen <c>hon_x64.exe</c>
///     <see cref="ReplayRendererService" /> pipeline and enforces fail-closed prerequisites
///     (<c>.honreplay</c>, <c>hon_x64.exe</c>, zero-window-exposure, and <see cref="FfprobeVideoVerifier" /> validation).
/// </summary>
public sealed class EngineReplayExportAdapter
{
    private readonly Func<RenderJobConfiguration, CancellationToken, Task<(bool Success, FfprobeVideoVerificationResult? Verification)>> _executeRendererAsync;
    private readonly ILogger<EngineReplayExportAdapter> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly FfprobeVideoVerifier _verifier;

    public EngineReplayExportAdapter(
        ILoggerFactory? loggerFactory = null,
        FfprobeVideoVerifier? verifier = null)
    {
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<EngineReplayExportAdapter>();
        _verifier = verifier ?? new FfprobeVideoVerifier(_loggerFactory.CreateLogger<FfprobeVideoVerifier>());
        _executeRendererAsync = ExecuteViaReplayRendererServiceAsync;
    }

    internal EngineReplayExportAdapter(
        ILoggerFactory? loggerFactory,
        FfprobeVideoVerifier? verifier,
        Func<RenderJobConfiguration, CancellationToken, Task<(bool Success, FfprobeVideoVerificationResult? Verification)>> executeRendererAsync)
    {
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<EngineReplayExportAdapter>();
        _verifier = verifier ?? new FfprobeVideoVerifier(_loggerFactory.CreateLogger<FfprobeVideoVerifier>());
        _executeRendererAsync = executeRendererAsync ?? throw new ArgumentNullException(nameof(executeRendererAsync));
    }

    /// <summary>
    ///     Maps a Replay Studio <see cref="ExportVideoCodec" /> selection to the corresponding
    ///     <see cref="ReplayRendererService" /> <see cref="VideoCodec" />.
    /// </summary>
    public static VideoCodec MapCodec(ExportVideoCodec codec)
    {
        return codec switch
        {
            ExportVideoCodec.H264Nvenc => VideoCodec.H264Nvenc,
            ExportVideoCodec.HevcNvenc => VideoCodec.HevcNvenc,
            _ => VideoCodec.Libx264CpuFallback
        };
    }

    /// <summary>
    ///     Maps a Replay Studio <see cref="ExportVideoCodec" /> to the expected <c>ffprobe</c> stream <c>codec_name</c>.
    /// </summary>
    public static string GetExpectedFfprobeCodecName(ExportVideoCodec codec)
    {
        return codec switch
        {
            ExportVideoCodec.HevcNvenc => "hevc",
            _ => "h264"
        };
    }

    /// <summary>
    ///     Validates fail-closed prerequisites for <see cref="ReplayExportBackendMode.EngineHeadless" />
    ///     and translates <see cref="ExportParameters" /> into a <see cref="RenderJobConfiguration" />.
    /// </summary>
    public bool TryBuildRenderConfiguration(
        ExportParameters parameters,
        out RenderJobConfiguration? config,
        out string? errorMessage)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        config = null;
        errorMessage = null;

        try
        {
            parameters.Validate();
        }
        catch (ArgumentException ex)
        {
            errorMessage = $"EngineHeadless export validation failed: {ex.Message}";
            _logger.LogError("[EngineReplayExportAdapter] {Error}", errorMessage);
            return false;
        }

        // 1. Fail-closed check: valid .honreplay source archive is mandatory
        if (string.IsNullOrWhiteSpace(parameters.ReplayFilePath))
        {
            errorMessage =
                "EngineHeadless export requires a valid .honreplay file path, but none was provided. " +
                "Load a .honreplay archive in Replay Studio (or set ExportParameters.ReplayFilePath), " +
                "or explicitly switch BackendMode to SoftwarePreview.";
            _logger.LogError("[EngineReplayExportAdapter] {Error}", errorMessage);
            return false;
        }

        string fullReplayPath = Path.GetFullPath(parameters.ReplayFilePath);
        if (!File.Exists(fullReplayPath))
        {
            errorMessage =
                $"EngineHeadless export failed: .honreplay archive not found at '{fullReplayPath}'. " +
                "Verify the replay file exists on disk or explicitly switch BackendMode to SoftwarePreview.";
            _logger.LogError("[EngineReplayExportAdapter] {Error}", errorMessage);
            return false;
        }

        if (!string.Equals(Path.GetExtension(fullReplayPath), ".honreplay", StringComparison.OrdinalIgnoreCase))
        {
            errorMessage =
                $"EngineHeadless export failed: '{fullReplayPath}' does not have a .honreplay extension. " +
                "Provide a valid .honreplay replay archive or explicitly switch BackendMode to SoftwarePreview.";
            _logger.LogError("[EngineReplayExportAdapter] {Error}", errorMessage);
            return false;
        }

        if (new FileInfo(fullReplayPath).Length <= 0)
        {
            errorMessage =
                $"EngineHeadless export failed: .honreplay archive '{fullReplayPath}' is empty (0 bytes). " +
                "Provide a valid non-empty .honreplay file or explicitly switch BackendMode to SoftwarePreview.";
            _logger.LogError("[EngineReplayExportAdapter] {Error}", errorMessage);
            return false;
        }

        // 2. Fail-closed check: valid HoN client directory containing hon_x64.exe is mandatory
        string? resolvedGameDir;
        if (!string.IsNullOrWhiteSpace(parameters.GameDirectory))
        {
            resolvedGameDir = Path.GetFullPath(parameters.GameDirectory);
        }
        else
        {
            string? envGameDir = Environment.GetEnvironmentVariable("HON_GAME_PATH");
            resolvedGameDir = !string.IsNullOrWhiteSpace(envGameDir)
                ? Path.GetFullPath(envGameDir)
                : CommandLineParser.DetectGameDirectory();
        }

        if (string.IsNullOrWhiteSpace(resolvedGameDir))
        {
            errorMessage =
                "EngineHeadless export failed: Heroes of Newerth game directory containing hon_x64.exe could not be detected. " +
                "Set ExportParameters.GameDirectory (or HON_GAME_PATH) to a valid client folder containing hon_x64.exe, " +
                "or explicitly switch BackendMode to SoftwarePreview.";
            _logger.LogError("[EngineReplayExportAdapter] {Error}", errorMessage);
            return false;
        }

        string honExePath = Path.Combine(resolvedGameDir, "hon_x64.exe");
        if (!File.Exists(honExePath))
        {
            errorMessage =
                $"EngineHeadless export failed: hon_x64.exe not found at '{honExePath}'. " +
                "Set ExportParameters.GameDirectory (or HON_GAME_PATH) to a valid Heroes of Newerth client installation containing hon_x64.exe, " +
                "or explicitly switch BackendMode to SoftwarePreview.";
            _logger.LogError("[EngineReplayExportAdapter] {Error}", errorMessage);
            return false;
        }

        string resolvedLayerDir = !string.IsNullOrWhiteSpace(parameters.VulkanLayerPath)
            ? Path.GetFullPath(parameters.VulkanLayerPath)
            : (CommandLineParser.DetectVulkanLayerDirectory(resolvedGameDir) ?? resolvedGameDir);

        VideoCodec mappedCodec = MapCodec(parameters.Codec);
        if (parameters.Codec is ExportVideoCodec.H264Qsv or ExportVideoCodec.H264Amf)
        {
            _logger.LogWarning(
                "[EngineReplayExportAdapter] Codec '{RequestedCodec}' is not directly exposed by ReplayRendererService; mapping to '{MappedCodec}' (H.264).",
                parameters.Codec, mappedCodec);
        }

        int startSeconds = Math.Max(0, parameters.InMarkerMs / 1000);
        TimeSpan maxDuration = TimeSpan.FromMilliseconds(Math.Max(1, parameters.TotalDurationMs));

        RenderJobConfiguration built = new()
        {
            ReplayFilePath = fullReplayPath,
            OutputFilePath = Path.GetFullPath(parameters.OutputFilePath),
            GameDirectory = resolvedGameDir,
            VulkanLayerPath = resolvedLayerDir,
            RenderWidth = (uint)parameters.Width,
            RenderHeight = (uint)parameters.Height,
            TargetFramerate = parameters.TargetFps,
            PovClientIndex = parameters.PovClientIndex,
            StartSeconds = startSeconds,
            MaxDuration = maxDuration,
            EnableCleanCinematicUi = parameters.EnableCleanCinematicUi,
            Codec = mappedCodec,
            FfmpegExecutablePath = parameters.FfmpegExecutablePath,
            FfprobeExecutablePath = parameters.FfprobeExecutablePath,
            VerifyOutputWithFfprobe = parameters.VerifyOutputWithFfprobe,
            AudioSampleRate = parameters.AudioSampleRate,
            AudioChannels = parameters.AudioChannels,
            AudioBitrateKbps = parameters.AudioBitrateKbps,
            VideoBitrate = parameters.VideoBitrate,
            MaxBitrate = parameters.MaxBitrate,
            BufferSize = parameters.BufferSize,
            ConstantQuality = parameters.ConstantQuality,
            Headless = true,
            AllowForegroundActivation = parameters.AllowForegroundActivation,
            UseSharedMemoryIpc = parameters.UseSharedMemoryIpc,
            StallTimeout = parameters.StallTimeout,
            ProcessTimeout = parameters.ProcessTimeout
        };

        try
        {
            built.Validate();
        }
        catch (ArgumentException ex)
        {
            errorMessage = $"EngineHeadless render configuration validation failed: {ex.Message}";
            _logger.LogError("[EngineReplayExportAdapter] {Error}", errorMessage);
            return false;
        }

        config = built;
        _logger.LogInformation(
            "[EngineReplayExportAdapter] Built offscreen engine render config: Replay='{Replay}', GameDir='{GameDir}', Output='{Output}', {W}x{H}@{Fps}fps, Codec={Codec}, Start={Start}s, MaxDur={Dur:F2}s, Headless={Headless}, AllowForegroundActivation={AllowFg}",
            built.ReplayFilePath,
            built.GameDirectory,
            built.OutputFilePath,
            built.RenderWidth,
            built.RenderHeight,
            built.TargetFramerate,
            built.Codec,
            built.StartSeconds,
            built.MaxDuration?.TotalSeconds ?? 0.0,
            built.Headless,
            built.AllowForegroundActivation);
        return true;
    }

    /// <summary>
    ///     Executes an offscreen <c>hon_x64.exe</c> replay export via <see cref="ReplayRendererService" />
    ///     and validates the output container using <see cref="FfprobeVideoVerifier" />.
    /// </summary>
    public async Task<ExportResult> ExportAsync(
        ExportParameters parameters,
        IProgress<ExportProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        Stopwatch stopwatch = Stopwatch.StartNew();

        if (!TryBuildRenderConfiguration(parameters, out RenderJobConfiguration? config, out string? validationError) ||
            config == null)
        {
            stopwatch.Stop();
            return new ExportResult
            {
                Success = false,
                BackendMode = ReplayExportBackendMode.EngineHeadless,
                OutputFilePath = parameters.OutputFilePath,
                TotalFramesRendered = 0,
                Duration = stopwatch.Elapsed,
                OutputSizeBytes = 0,
                AverageFps = 0.0,
                ErrorMessage = validationError ?? "EngineHeadless configuration validation failed.",
                ZeroWindowExposureEnforced = !parameters.AllowForegroundActivation
            };
        }

        string? outputDir = Path.GetDirectoryName(config.OutputFilePath);
        if (!string.IsNullOrWhiteSpace(outputDir) && !Directory.Exists(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        int expectedFrames = parameters.TotalFrames;
        progress?.Report(new ExportProgress
        {
            RenderedFrames = 0,
            TotalFrames = expectedFrames,
            PercentComplete = 5.0,
            CurrentFps = 0.0,
            EtaSeconds = 0.0,
            Elapsed = stopwatch.Elapsed,
            CurrentTimestampMs = parameters.InMarkerMs,
            StatusText = "Launching offscreen hon_x64.exe replay renderer (zero-window-exposure)..."
        });

        try
        {
            (bool renderSuccess, FfprobeVideoVerificationResult? probeResult) =
                await _executeRendererAsync(config, ct);

            if (config.VerifyOutputWithFfprobe && probeResult == null && File.Exists(config.OutputFilePath))
            {
                progress?.Report(new ExportProgress
                {
                    RenderedFrames = expectedFrames,
                    TotalFrames = expectedFrames,
                    PercentComplete = 92.0,
                    CurrentFps = 0.0,
                    EtaSeconds = 0.0,
                    Elapsed = stopwatch.Elapsed,
                    CurrentTimestampMs = parameters.OutMarkerMs,
                    StatusText = "Verifying encoded MP4 container via ffprobe..."
                });

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

                probeResult = await _verifier.VerifyAsync(config.OutputFilePath, verifyOptions, ct);
            }

            stopwatch.Stop();

            if (config.VerifyOutputWithFfprobe && (probeResult == null || !probeResult.IsValid))
            {
                string verifyFailure = probeResult != null
                    ? $"EngineHeadless export failed post-export ffprobe verification: {probeResult.FailureReason}"
                    : $"EngineHeadless export failed: ReplayRendererService did not produce an output MP4 at '{config.OutputFilePath}'.";
                _logger.LogError("[EngineReplayExportAdapter] {Error}", verifyFailure);
                return new ExportResult
                {
                    Success = false,
                    BackendMode = ReplayExportBackendMode.EngineHeadless,
                    OutputFilePath = config.OutputFilePath,
                    TotalFramesRendered = 0,
                    Duration = stopwatch.Elapsed,
                    OutputSizeBytes = 0,
                    AverageFps = 0.0,
                    ErrorMessage = verifyFailure,
                    VerificationResult = probeResult,
                    ZeroWindowExposureEnforced = !config.AllowForegroundActivation
                };
            }

            if (!renderSuccess)
            {
                string renderFailure =
                    $"EngineHeadless export failed: ReplayRendererService exited without completing the render for '{config.ReplayFilePath}'.";
                _logger.LogError("[EngineReplayExportAdapter] {Error}", renderFailure);
                return new ExportResult
                {
                    Success = false,
                    BackendMode = ReplayExportBackendMode.EngineHeadless,
                    OutputFilePath = config.OutputFilePath,
                    TotalFramesRendered = 0,
                    Duration = stopwatch.Elapsed,
                    OutputSizeBytes = 0,
                    AverageFps = 0.0,
                    ErrorMessage = renderFailure,
                    VerificationResult = probeResult,
                    ZeroWindowExposureEnforced = !config.AllowForegroundActivation
                };
            }

            long outputSize = probeResult != null && probeResult.FileSizeBytes > 0
                ? probeResult.FileSizeBytes
                : (File.Exists(config.OutputFilePath) ? new FileInfo(config.OutputFilePath).Length : 0);

            int renderedFrames = probeResult != null && probeResult.FrameCount > 0
                ? (int)Math.Min(int.MaxValue, probeResult.FrameCount)
                : expectedFrames;

            double avgFps = stopwatch.Elapsed.TotalSeconds > 0.0
                ? renderedFrames / stopwatch.Elapsed.TotalSeconds
                : 0.0;

            progress?.Report(new ExportProgress
            {
                RenderedFrames = renderedFrames,
                TotalFrames = renderedFrames,
                PercentComplete = 100.0,
                CurrentFps = avgFps,
                EtaSeconds = 0.0,
                Elapsed = stopwatch.Elapsed,
                CurrentTimestampMs = parameters.OutMarkerMs,
                StatusText = probeResult != null
                    ? $"Engine export verified ({probeResult.CodecName} {probeResult.Width}x{probeResult.Height}, {probeResult.FrameCount} frames)"
                    : $"Engine export completed ({renderedFrames} frames)"
            });

            _logger.LogInformation(
                "[EngineReplayExportAdapter] EngineHeadless export SUCCEEDED: Output='{Output}', Frames={Frames}, Size={Size:N0} bytes, Elapsed={Elapsed:F2}s, Verification={Verification}",
                config.OutputFilePath,
                renderedFrames,
                outputSize,
                stopwatch.Elapsed.TotalSeconds,
                probeResult?.Summary ?? "skipped");

            return new ExportResult
            {
                Success = true,
                BackendMode = ReplayExportBackendMode.EngineHeadless,
                OutputFilePath = config.OutputFilePath,
                TotalFramesRendered = renderedFrames,
                Duration = stopwatch.Elapsed,
                OutputSizeBytes = outputSize,
                AverageFps = avgFps,
                ErrorMessage = null,
                VerificationResult = probeResult,
                ZeroWindowExposureEnforced = !config.AllowForegroundActivation
            };
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[EngineReplayExportAdapter] EngineHeadless export canceled by caller.");
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "[EngineReplayExportAdapter] EngineHeadless export threw an exception: {Message}", ex.Message);
            return new ExportResult
            {
                Success = false,
                BackendMode = ReplayExportBackendMode.EngineHeadless,
                OutputFilePath = config.OutputFilePath,
                TotalFramesRendered = 0,
                Duration = stopwatch.Elapsed,
                OutputSizeBytes = 0,
                AverageFps = 0.0,
                ErrorMessage = $"EngineHeadless export exception: {ex.Message}",
                ZeroWindowExposureEnforced = !config.AllowForegroundActivation
            };
        }
    }

    private async Task<(bool Success, FfprobeVideoVerificationResult? Verification)> ExecuteViaReplayRendererServiceAsync(
        RenderJobConfiguration config,
        CancellationToken ct)
    {
        TelemetryLogger telemetryLogger = new(_loggerFactory.CreateLogger<TelemetryLogger>());
        using ReplayRendererService rendererService = new(
            _loggerFactory.CreateLogger<ReplayRendererService>(),
            _loggerFactory,
            telemetryLogger);

        bool success = await rendererService.ExecuteJobAsync(config, ct);
        return (success, rendererService.LastVerificationResult);
    }
}
