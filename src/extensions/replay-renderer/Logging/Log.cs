// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using Microsoft.Extensions.Logging;

namespace ReplayRenderer.Service.Logging;

public static partial class Log
{
    // ReplayRendererService
    [LoggerMessage(Level = LogLevel.Information, Message = "[ReplayRenderer] Replay Renderer Service starting. Workers: {WorkerCount}")]
    public static partial void LogRendererStarting(this ILogger logger, int workerCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "[ReplayRenderer] Replay Renderer Service stopping")]
    public static partial void LogRendererStopping(this ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "[ReplayRenderer] Starting render job #{JobId} for Match #{MatchId}")]
    public static partial void LogRenderJobStarted(this ILogger logger, string jobId, int matchId);

    [LoggerMessage(Level = LogLevel.Information, Message = "[ReplayRenderer] Render job #{JobId} completed in {ElapsedSeconds}s")]
    public static partial void LogRenderJobCompleted(this ILogger logger, string jobId, double elapsedSeconds);

    [LoggerMessage(Level = LogLevel.Error, Message = "[ReplayRenderer] Render job #{JobId} failed")]
    public static partial void LogRenderJobFailed(this ILogger logger, Exception ex, string jobId);

    // ReplayProcessLauncher
    [LoggerMessage(Level = LogLevel.Information, Message = "[ProcessLauncher] Launching HoN replay renderer process. Match ID: {MatchId}")]
    public static partial void LogLaunchingReplayProcess(this ILogger logger, int matchId);

    [LoggerMessage(Level = LogLevel.Information, Message = "[ProcessLauncher] Replay process {ProcessId} assigned to Windows Job Object sandbox")]
    public static partial void LogReplayProcessSandboxed(this ILogger logger, int processId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[ProcessLauncher] Replay process {ProcessId} exited with code {ExitCode}")]
    public static partial void LogReplayProcessExited(this ILogger logger, int processId, int exitCode);

    // FfmpegPipelineController
    [LoggerMessage(Level = LogLevel.Information, Message = "[FFmpeg] Started video encoder pipe for Job #{JobId}: {Resolution} @ {Fps}fps ({Codec})")]
    public static partial void LogFfmpegStarted(this ILogger logger, string jobId, string resolution, int fps, string codec);

    [LoggerMessage(Level = LogLevel.Information, Message = "[FFmpeg] Finalized video output: {OutputPath} ({FileSizeBytes:N0} Bytes)")]
    public static partial void LogFfmpegCompleted(this ILogger logger, string outputPath, long fileSizeBytes);

    // TelemetryLogger
    [LoggerMessage(Level = LogLevel.Debug, Message = "[RendererTelemetry] Render Progress: {PercentComplete}% ({FramesRendered}/{TotalFrames} frames, {Fps:F1} FPS)")]
    public static partial void LogRenderProgress(this ILogger logger, double percentComplete, int framesRendered, int totalFrames, double fps);
}
