// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: RenderJobProgress.cs
// System: NEXUS Replay Video Renderer Service
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

namespace ReplayRenderer.Service.Models;

/// <summary>
///     Execution state of a replay rendering job.
/// </summary>
public enum RenderJobState
{
    Initializing = 0,
    SpawningGame = 1,
    Capturing = 2,
    Encoding = 3,
    Finalizing = 4,
    Completed = 5,
    Failed = 6,
    Stalled = 7
}

/// <summary>
///     Telemetry snapshot containing real-time rendering statistics and progress.
/// </summary>
public sealed class RenderJobProgress
{
    /// <summary>
    ///     Unique identifier of the render job.
    /// </summary>
    public string JobId { get; set; } = string.Empty;

    /// <summary>
    ///     Current lifecycle state.
    /// </summary>
    public RenderJobState State { get; set; } = RenderJobState.Initializing;

    /// <summary>
    ///     Cumulative count of uncompressed video frames successfully captured and piped.
    /// </summary>
    public long TotalFramesRendered { get; set; }

    /// <summary>
    ///     Instantaneous rendering throughput in frames per second sampled over a sliding window.
    /// </summary>
    public double CurrentFps { get; set; }

    /// <summary>
    ///     Real-time playback speed multiplier relative to target 60 FPS (e.g. 1.85x).
    /// </summary>
    public double SpeedMultiplier { get; set; }

    /// <summary>
    ///     Elapsed wall-clock time since the rendering session started.
    /// </summary>
    public TimeSpan ElapsedTime { get; set; } = TimeSpan.Zero;

    /// <summary>
    ///     Optional estimated total frame count derived from parsed replay header match ticks.
    /// </summary>
    public long? EstimatedTotalFrames { get; set; }

    /// <summary>
    ///     Estimated percentage completion (0.0 to 100.0%).
    /// </summary>
    public double? PercentComplete
    {
        get
        {
            if (EstimatedTotalFrames.HasValue && EstimatedTotalFrames.Value > 0)
            {
                double pct = (double)TotalFramesRendered / EstimatedTotalFrames.Value * 100.0;
                return Math.Clamp(pct, 0.0, 100.0);
            }

            return null;
        }
    }

    /// <summary>
    ///     Indicates whether the frame stream is currently stalled beyond the watchdog threshold.
    /// </summary>
    public bool IsStalled { get; set; }

    /// <summary>
    ///     Time of the most recently received frame.
    /// </summary>
    public DateTime LastFrameTimestamp { get; set; } = TimeProvider.System.GetUtcNow().UtcDateTime;

    /// <summary>
    ///     Descriptive error message if the job failed or stalled.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    ///     Returns a human-readable telemetry summary line.
    /// </summary>
    public override string ToString()
    {
        string progressStr = PercentComplete.HasValue ? $" ({PercentComplete.Value:F1}%)" : string.Empty;
        return
            $"[Job {JobId}] State={State} | Frames={TotalFramesRendered}{progressStr} | Rate={CurrentFps:F1} fps ({SpeedMultiplier:F2}x) | Elapsed={ElapsedTime:mm\\:ss}";
    }
}
