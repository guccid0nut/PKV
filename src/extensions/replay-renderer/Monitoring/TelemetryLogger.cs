// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: TelemetryLogger.cs
// System: NEXUS Replay Video Renderer Service
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using ReplayRenderer.Service.Models;

namespace ReplayRenderer.Service.Monitoring;

/// <summary>
///     Structured telemetry and developer diagnostics logger.
///     Emits high-visibility banners, system environment profiles, memory metrics, and render summaries.
/// </summary>
public sealed class TelemetryLogger(ILogger<TelemetryLogger> logger)
{
    private readonly ILogger<TelemetryLogger> _logger = logger;

    /// <summary>
    ///     Logs a prominent ASCII startup banner and configuration matrix.
    /// </summary>
    public void LogJobBanner(RenderJobConfiguration config, string jobId)
    {
        _logger.LogInformation("=================================================================================");
        _logger.LogInformation(" NEXUS HoN K2 Deterministic Replay Video Renderer Service (.NET 10)");
        _logger.LogInformation(" Job ID:          {JobId}", jobId);
        _logger.LogInformation(" Replay File:     {Replay}", config.ReplayFilePath);
        _logger.LogInformation(" Output Video:    {Output}", config.OutputFilePath);
        _logger.LogInformation(" Resolution:      {W}x{H} @ {FPS} FPS (Locked Frame Delta: 16.666ms)",
            config.RenderWidth, config.RenderHeight, config.TargetFramerate);
        _logger.LogInformation(" Codec:           {Codec} (CQ: {CQ}, Target Bitrate: {Bitrate}, Max: {Max})",
            config.Codec, config.ConstantQuality, config.VideoBitrate, config.MaxBitrate);
        _logger.LogInformation(
            " Audio Config:    {Rate}Hz Stereo 16-bit PCM (Exact 800 samples/frame, 3200 bytes/frame)",
            config.AudioSampleRate);
        _logger.LogInformation(" IPC Transport:   {Transport}",
            config.UseSharedMemoryIpc
                ? "Win32 Shared Memory (Triple-Buffered Ring)"
                : "Win32 Named Pipe (64MB Kernel Buffer)");
        _logger.LogInformation(" Sandboxing:      Win32 Job Object (JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE)");
        _logger.LogInformation(" Headless Mode:   {Headless}", config.Headless);
        _logger.LogInformation("=================================================================================");
    }

    /// <summary>
    ///     Logs host runtime environment, OS build, memory, and CPU topology.
    /// </summary>
    public void LogSystemDiagnostics()
    {
        Process currentProcess = Process.GetCurrentProcess();
        long workingSetMb = currentProcess.WorkingSet64 / (1024 * 1024);

        _logger.LogInformation("[Diagnostics] OS: {OS} | Architecture: {Arch} | .NET Runtime: {Runtime}",
            RuntimeInformation.OSDescription, RuntimeInformation.OSArchitecture,
            RuntimeInformation.FrameworkDescription);
        _logger.LogInformation("[Diagnostics] CPU Cores: {Cores} | Service Working Set: {Mem} MB | PID: {PID}",
            Environment.ProcessorCount, workingSetMb, currentProcess.Id);
    }

    /// <summary>
    ///     Logs the completion summary report including final video file metrics and audio sync stats.
    /// </summary>
    public void LogJobSummary(
        RenderJobConfiguration config,
        long totalFramesRendered,
        TimeSpan elapsedTime,
        long audioUnderruns,
        long totalVideoBytes)
    {
        double averageFps = elapsedTime.TotalSeconds > 0 ? totalFramesRendered / elapsedTime.TotalSeconds : 0;
        double speedFactor = averageFps / config.TargetFramerate;
        double videoDurationSeconds = (double)totalFramesRendered / config.TargetFramerate;

        _logger.LogInformation("=================================================================================");
        _logger.LogInformation(" REPLAY RENDERING COMPLETED SUCCESSFULLY");
        _logger.LogInformation(" Total Frames Rendered:   {Frames:N0} frames", totalFramesRendered);
        _logger.LogInformation(" Video Duration:          {Sec:F2} seconds ({Dur:mm\\:ss})", videoDurationSeconds,
            TimeSpan.FromSeconds(videoDurationSeconds));
        _logger.LogInformation(" Total Render Time:       {Elapsed:mm\\:ss\\.ff}", elapsedTime);
        _logger.LogInformation(" Average Processing Rate: {AvgFps:F2} FPS ({Speed:F2}x real-time)", averageFps,
            speedFactor);
        _logger.LogInformation(" Audio Sync Status:       {Underruns} frame underruns (0-PCM silence padded)",
            audioUnderruns);
        _logger.LogInformation(" Raw Video Ingested:      {SizeMb:F2} MB ({Gbps:F2} Gbps average bandwidth)",
            totalVideoBytes / (1024.0 * 1024.0),
            elapsedTime.TotalSeconds > 0 ? totalVideoBytes * 8.0 / (elapsedTime.TotalSeconds * 1_000_000_000.0) : 0);

        if (File.Exists(config.OutputFilePath))
        {
            FileInfo outputInfo = new(config.OutputFilePath);
            _logger.LogInformation(" Final Encoded Output:    {Path} ({SizeMb:F2} MB)", outputInfo.FullName,
                outputInfo.Length / (1024.0 * 1024.0));
        }
        else
        {
            _logger.LogWarning(" Output file not found at: {Path}", config.OutputFilePath);
        }

        _logger.LogInformation("=================================================================================");
    }
}
