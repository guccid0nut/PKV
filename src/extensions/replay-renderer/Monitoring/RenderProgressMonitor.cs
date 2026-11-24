// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: RenderProgressMonitor.cs
// System: NEXUS Replay Video Renderer Service
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ReplayRenderer.Service.Models;

namespace ReplayRenderer.Service.Monitoring;

/// <summary>
///     Real-time frame telemetry monitor and 5.0-second stall watchdog.
///     Computes sliding-window rendering throughput and triggers automatic recovery
///     if the Vulkan swapchain or K2 engine halts frame delivery.
/// </summary>
public sealed class RenderProgressMonitor(
    ILogger<RenderProgressMonitor> logger,
    string jobId = "",
    long? estimatedTotalFrames = null,
    TimeSpan? stallTimeout = null) : IAsyncDisposable
{
    private readonly long? _estimatedTotalFrames = estimatedTotalFrames;
    private readonly string _jobId = jobId;
    private readonly ILogger<RenderProgressMonitor> _logger = logger;
    private readonly TimeSpan _stallTimeout = stallTimeout ?? TimeSpan.FromSeconds(5);
    private readonly Stopwatch _stopwatch = new();
    private CancellationTokenSource? _cts;
    private bool _disposed;
    private long _frameCount;
    private long _lastFrameTicks;
    private Task? _monitorTask;

    public long TotalFramesRendered => Interlocked.Read(ref _frameCount);
    public TimeSpan ElapsedTime => _stopwatch.Elapsed;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        _disposed = true;
        _stopwatch.Stop();

        if (_cts != null)
        {
            _cts.Cancel();
            if (_monitorTask != null)
                try
                {
                    await _monitorTask;
                }
                catch
                {
                    // Suppress cancellation
                }

            _cts.Dispose();
            _cts = null;
        }
    }

    public event Action<RenderJobProgress>? OnProgressUpdated;
    public event Action? OnStallDetected;

    /// <summary>
    ///     Starts the watchdog timer and telemetry calculation loop.
    /// </summary>
    public void Start()
    {
        _lastFrameTicks = Environment.TickCount64;
        _frameCount = 0;
        _stopwatch.Restart();

        _cts = new CancellationTokenSource();
        _monitorTask = Task.Run(() => MonitorLoopAsync(_cts.Token), CancellationToken.None);
        _logger.LogInformation("[RenderMonitor] Watchdog started with {Timeout}s stall threshold for job '{JobId}'.",
            _stallTimeout.TotalSeconds, _jobId);
    }

    /// <summary>
    ///     Stops the watchdog timer and monitoring loop.
    /// </summary>
    public void Stop()
    {
        _cts?.Cancel();
    }

    /// <summary>
    ///     Notifies the watchdog that a new video frame was successfully captured and piped.
    /// </summary>
    public void NotifyFrameRendered()
    {
        Interlocked.Increment(ref _frameCount);
        Interlocked.Exchange(ref _lastFrameTicks, Environment.TickCount64);
    }

    private async Task MonitorLoopAsync(CancellationToken cancellationToken)
    {
        TimeSpan tickInterval = TimeSpan.FromMilliseconds(Math.Clamp(_stallTimeout.TotalMilliseconds / 8.0, 20, 100));
        PeriodicTimer timer = new(tickInterval);
        long previousFrames = 0;

        try
        {
            while (!cancellationToken.IsCancellationRequested && await timer.WaitForNextTickAsync(cancellationToken))
            {
                long currentFrames = Interlocked.Read(ref _frameCount);
                long framesDelta = currentFrames - previousFrames;
                previousFrames = currentFrames;

                double currentFps = framesDelta;
                double speedMultiplier = currentFps / 60.0;
                long elapsedSinceLastFrameMs = Environment.TickCount64 - Interlocked.Read(ref _lastFrameTicks);

                bool isStalled = currentFrames > 0 && elapsedSinceLastFrameMs > _stallTimeout.TotalMilliseconds;

                RenderJobProgress progress = new()
                {
                    JobId = _jobId,
                    State = isStalled ? RenderJobState.Stalled : RenderJobState.Capturing,
                    TotalFramesRendered = currentFrames,
                    CurrentFps = currentFps,
                    SpeedMultiplier = speedMultiplier,
                    ElapsedTime = _stopwatch.Elapsed,
                    EstimatedTotalFrames = _estimatedTotalFrames,
                    IsStalled = isStalled,
                    LastFrameTimestamp = TimeProvider.System.GetUtcNow().UtcDateTime
                };

                string progressStr =
                    progress.PercentComplete.HasValue ? $" ({progress.PercentComplete.Value:F1}%)" : "";

                _logger.LogInformation(
                    "[RenderMonitor] Render Status: {Frames} frames{Prog} ({Elapsed:mm\\:ss}) | Rate: {FPS:F1} FPS ({Speed:F2}x) | Last Frame: {Last:F0}ms ago",
                    currentFrames, progressStr, _stopwatch.Elapsed, currentFps, speedMultiplier,
                    elapsedSinceLastFrameMs);

                OnProgressUpdated?.Invoke(progress);

                if (isStalled)
                {
                    _logger.LogError(
                        "[RenderMonitor] CRITICAL: Frame stream stalled for {Elapsed:F1} seconds (> {Limit}s limit)! Triggering watchdog recovery.",
                        elapsedSinceLastFrameMs / 1000.0, _stallTimeout.TotalSeconds);
                    OnStallDetected?.Invoke();
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("[RenderMonitor] Monitor loop canceled cleanly.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[RenderMonitor] Exception in monitor loop: {Message}", ex.Message);
        }
    }
}
