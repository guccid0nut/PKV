// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ASPIRE.Common.Hosting;

/// <summary>
/// Static thread-safe registry tracking all active <see cref="ResilientBackgroundService"/> instances
/// across the host for runtime telemetry and degraded health check reporting.
/// </summary>
public static class ResilientBackgroundServiceRegistry
{
    private static readonly ConcurrentDictionary<ResilientBackgroundService, byte> s_workers = new();

    public static void Register(ResilientBackgroundService worker)
    {
        ArgumentNullException.ThrowIfNull(worker);
        s_workers.TryAdd(worker, 0);
    }

    public static void Unregister(ResilientBackgroundService worker)
    {
        if (worker != null)
        {
            s_workers.TryRemove(worker, out _);
        }
    }

    public static void Clear() => s_workers.Clear();

    public static IReadOnlyCollection<ResilientBackgroundService> AllWorkers => s_workers.Keys.ToArray();

    public static bool HasDegradedWorkers(out IReadOnlyList<ResilientBackgroundService> degraded)
    {
        List<ResilientBackgroundService> list = s_workers.Keys.Where(w => w.IsDegraded).ToList();
        degraded = list;
        return list.Count > 0;
    }
}

/// <summary>
/// Base class for background workers with self-healing restart loops, exponential backoff,
/// structured failure metrics, and health-check integration.
/// </summary>
public abstract class ResilientBackgroundService : BackgroundService
{
    private readonly ILogger _logger;
    private readonly TimeSpan _initialBackoff;
    private readonly TimeSpan _maxBackoff;
    private readonly TimeProvider _timeProvider;

    public string ServiceName { get; }
    public int ConsecutiveFailures { get; private set; }
    public long TotalUnhandledExceptions { get; private set; }
    public DateTimeOffset? LastExceptionAt { get; private set; }
    public string? LastExceptionMessage { get; private set; }

    /// <summary>
    /// Worker is marked degraded once it experiences 3 or more consecutive unhandled exceptions.
    /// </summary>
    public bool IsDegraded => ConsecutiveFailures >= 3;

    protected ResilientBackgroundService(
        ILogger logger,
        string serviceName,
        TimeSpan? initialBackoff = null,
        TimeSpan? maxBackoff = null,
        TimeProvider? timeProvider = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ServiceName = string.IsNullOrWhiteSpace(serviceName) ? GetType().Name : serviceName;
        _initialBackoff = initialBackoff ?? TimeSpan.FromSeconds(1);
        _maxBackoff = maxBackoff ?? TimeSpan.FromSeconds(30);
        _timeProvider = timeProvider ?? TimeProvider.System;

        if (_initialBackoff <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(initialBackoff), "Initial backoff must be positive.");
        if (_maxBackoff < _initialBackoff)
            throw new ArgumentOutOfRangeException(nameof(maxBackoff), "Max backoff cannot be less than initial backoff.");

        ResilientBackgroundServiceRegistry.Register(this);
    }

    /// <summary>
    /// Core execution logic. Return normally for one-shot startup services (e.g. DatabaseInitializer),
    /// or run an infinite loop until <paramref name="stoppingToken"/> is canceled.
    /// </summary>
    protected abstract Task ExecuteCoreAsync(CancellationToken stoppingToken);

    /// <summary>
    /// Helper for long-running worker loops to reset the consecutive failure count after a successful batch or tick.
    /// </summary>
    protected void ResetFailureCounter()
    {
        ConsecutiveFailures = 0;
    }

    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[ResilientWorker:{ServiceName}] Resilient background worker starting...", ServiceName);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ExecuteCoreAsync(stoppingToken).ConfigureAwait(false);

                // Normal completion of ExecuteCoreAsync (for one-shot startup services) exits the loop cleanly without restarting.
                _logger.LogInformation("[ResilientWorker:{ServiceName}] Worker completed normally; exiting execution loop.", ServiceName);
                break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("[ResilientWorker:{ServiceName}] Worker shutdown requested; cleanly exiting execution loop.", ServiceName);
                break;
            }
            catch (Exception ex)
            {
                ConsecutiveFailures++;
                TotalUnhandledExceptions++;
                LastExceptionAt = _timeProvider.GetUtcNow();
                LastExceptionMessage = ex.Message;

                TimeSpan delay = CalculateBackoffWithJitter(ConsecutiveFailures);

                _logger.LogError(
                    ex,
                    "[ResilientWorker:{ServiceName}] Unhandled exception (consecutive={Consecutive}, total={Total}); restarting after {DelayMs:F0}ms backoff. Error: {ErrorMessage}",
                    ServiceName,
                    ConsecutiveFailures,
                    TotalUnhandledExceptions,
                    delay.TotalMilliseconds,
                    ex.Message);

                try
                {
                    await Task.Delay(delay, _timeProvider, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    _logger.LogInformation("[ResilientWorker:{ServiceName}] Worker shutdown requested during backoff delay; cleanly exiting.", ServiceName);
                    break;
                }
            }
        }
    }

    private TimeSpan CalculateBackoffWithJitter(int failureCount)
    {
        int power = Math.Min(failureCount - 1, 10);
        double multiplier = Math.Pow(2.0, power);
        double backoffMs = _initialBackoff.TotalMilliseconds * multiplier;
        double clampedMs = Math.Min(backoffMs, _maxBackoff.TotalMilliseconds);

        // Apply +/- 20% jitter (0.80 - 1.20)
        double jitter = 0.8 + (Random.Shared.NextDouble() * 0.4);
        double finalMs = Math.Max(clampedMs * jitter, 10.0);
        return TimeSpan.FromMilliseconds(finalMs);
    }

    public override void Dispose()
    {
        ResilientBackgroundServiceRegistry.Unregister(this);
        base.Dispose();
        GC.SuppressFinalize(this);
    }
}
