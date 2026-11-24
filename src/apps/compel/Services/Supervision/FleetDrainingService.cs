// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: FleetDrainingService.cs
// System: COMPEL - Zero-Downtime Rolling Fleet Draining & Graceful Handoff
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace COMPEL.Services.Supervision;

/// <summary>
/// Result of an individual dedicated server worker drain operation.
/// </summary>
public sealed record FleetDrainResult(
    int Port,
    bool Success,
    bool Immediate,
    string Reason,
    string? Message = null);

/// <summary>
/// Configuration options for orchestrating rolling fleet upgrades without service disruption.
/// </summary>
public sealed record RollingUpgradeOptions(
    int MaxConcurrentDraining = 2,
    TimeSpan? PerWorkerTimeout = null,
    string UpgradeReason = "Rolling Fleet Upgrade");

/// <summary>
/// Summary result of an orchestrated rolling upgrade across the worker fleet.
/// </summary>
public sealed record RollingFleetUpgradeResult(
    bool IsComplete,
    int TotalWorkers,
    int UpgradedWorkers,
    int FailedWorkers,
    IReadOnlyList<FleetDrainResult> DrainResults);

/// <summary>
/// Orchestrates zero-downtime rolling upgrades and graceful match server worker draining.
/// When workers are idle, they are retired and resurrected immediately.
/// When workers have active matches, draining is deferred until the match cleanly concludes,
/// preventing player disconnects and preserving match telemetry.
/// </summary>
public sealed class FleetDrainingService : IHostedService
{
    private readonly Func<int, DedicatedServerWorker?> _workerResolver;
    private readonly Func<DedicatedServerWorker, Task<bool>> _workerResurrector;
    private readonly MatchServerManagerSupervisor? _supervisor;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<bool>> _drainCompletions = new();

    /// <summary>
    /// Initializes a new instance of <see cref="FleetDrainingService"/> with explicit delegates.
    /// </summary>
    public FleetDrainingService(
        Func<int, DedicatedServerWorker?> workerResolver,
        Func<DedicatedServerWorker, Task<bool>> workerResurrector,
        ILogger logger,
        TimeProvider? timeProvider = null,
        MatchServerManagerSupervisor? supervisor = null)
    {
        _workerResolver = workerResolver ?? throw new ArgumentNullException(nameof(workerResolver));
        _workerResurrector = workerResurrector ?? throw new ArgumentNullException(nameof(workerResurrector));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _supervisor = supervisor;
    }

    /// <summary>
    /// Dependency-injected constructor coupling with <see cref="MatchServerManagerSupervisor"/>.
    /// </summary>
    public FleetDrainingService(
        MatchServerManagerSupervisor supervisor,
        ILogger<FleetDrainingService> logger,
        TimeProvider? timeProvider = null)
        : this(
            port => supervisor.FindWorker(port),
            async worker => await supervisor.RequestWorkerRestart(worker.Port, CancellationToken.None).ConfigureAwait(false),
            logger,
            timeProvider,
            supervisor)
    {
    }

    /// <summary>
    /// Starts the hosted service and subscribes to supervisor.MatchEnded for event-driven drain execution.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_supervisor != null)
        {
            _supervisor.MatchEnded += HandleSupervisorMatchEndedAsync;
            _logger.LogInformation("[FleetDraining] Subscribed to supervisor.MatchEnded for zero-downtime rolling fleet draining.");
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops the hosted service and detaches event listeners.
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_supervisor != null)
        {
            _supervisor.MatchEnded -= HandleSupervisorMatchEndedAsync;
            _logger.LogInformation("[FleetDraining] Unsubscribed from supervisor.MatchEnded.");
        }
        return Task.CompletedTask;
    }

    private async Task HandleSupervisorMatchEndedAsync(DedicatedServerWorker worker)
    {
        try
        {
            await OnMatchEndedAsync(worker).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[FleetDraining] Error processing MatchEnded event for worker on port {Port}", worker.Port);
        }
    }

    /// <summary>
    /// Marks a dedicated worker instance on a specific port for draining.
    /// If the worker is currently idle ("Available"), it is immediately retired with "quit" and resurrected.
    /// If the worker is in an active match ("InMatch"), draining is deferred until the match ends.
    /// </summary>
    [Obsolete("Use MarkWorkerDrainingAsync instead")]
    public bool MarkWorkerDraining(int port, string reason)
    {
#pragma warning disable RS0030 // Justification: Obsolete synchronous backward compatibility wrapper
        return Task.Run(async () => await MarkWorkerDrainingAsync(port, reason).ConfigureAwait(false)).GetAwaiter().GetResult();
#pragma warning restore RS0030
    }

    /// <summary>
    /// Asynchronously marks a dedicated worker instance on a specific port for draining.
    /// </summary>
    public async Task<bool> MarkWorkerDrainingAsync(int port, string reason, CancellationToken cancellationToken = default)
    {
        DedicatedServerWorker? worker = _workerResolver(port);
        if (worker is null)
        {
            _logger.LogWarning("[FleetDraining] Cannot mark worker on port {Port} for draining: worker not found", port);
            return false;
        }

        if (worker.CurrentState.Equals("Available", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("[FleetDraining] Worker on port {Port} is idle. Executing immediate clean drain ({Reason})", port, reason);
            worker.CurrentState = "Restarting";
            await worker.WritePipeAsync("quit", cancellationToken: cancellationToken).ConfigureAwait(false);
            bool resurrected = await _workerResurrector(worker).ConfigureAwait(false);
            return resurrected;
        }

        // Active match in progress: defer draining until match ends
        worker.IsDraining = true;
        worker.DrainReason = reason;
        worker.DrainStartTime = _timeProvider.GetUtcNow();
        _drainCompletions[port] = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _logger.LogInformation("[FleetDraining] Worker on port {Port} is active (state={State}). Marked for deferred drain ({Reason})",
            port, worker.CurrentState, reason);

        return true;
    }

    /// <summary>
    /// Cancels a pending deferred drain operation for a worker on the specified port.
    /// </summary>
    public bool CancelDraining(int port)
    {
        DedicatedServerWorker? worker = _workerResolver(port);
        if (worker is null || !worker.IsDraining)
        {
            return false;
        }

        worker.IsDraining = false;
        worker.DrainReason = null;
        worker.DrainStartTime = null;

        if (_drainCompletions.TryRemove(port, out TaskCompletionSource<bool>? tcs))
        {
            tcs.TrySetCanceled();
        }

        _logger.LogInformation("[FleetDraining] Cancelled pending drain on port {Port}", port);
        return true;
    }

    /// <summary>
    /// Invoked when a worker completes an active match (e.g. GS_SLEEP / EndGame / Match ended).
    /// If the worker was marked for deferred draining, issues "quit" to stdin and invokes resurrection.
    /// </summary>
    public async Task<bool> OnMatchEndedAsync(DedicatedServerWorker worker, CancellationToken cancellationToken = default)
    {
        if (!worker.IsDraining)
        {
            return false;
        }

        _logger.LogInformation("[FleetDraining] Worker on port {Port} completed match. Executing deferred drain ({Reason})",
            worker.Port, worker.DrainReason);

        worker.CurrentState = "Restarting";
        await worker.WritePipeAsync("quit", cancellationToken: cancellationToken).ConfigureAwait(false);
        bool resurrected = await _workerResurrector(worker).ConfigureAwait(false);

        if (_drainCompletions.TryRemove(worker.Port, out TaskCompletionSource<bool>? tcs))
        {
            tcs.TrySetResult(resurrected);
        }

        return resurrected;
    }

    /// <summary>
    /// Orchestrates a rolling fleet upgrade across all specified workers while enforcing a maximum
    /// concurrency limit to preserve server cluster availability.
    /// </summary>
    public async Task<RollingFleetUpgradeResult> RollingFleetUpgradeAsync(
        IReadOnlyList<DedicatedServerWorker> workers,
        RollingUpgradeOptions options,
        CancellationToken cancellationToken = default)
    {
        if (workers.Count == 0)
        {
            return new RollingFleetUpgradeResult(
                IsComplete: true,
                TotalWorkers: 0,
                UpgradedWorkers: 0,
                FailedWorkers: 0,
                DrainResults: Array.Empty<FleetDrainResult>());
        }

        int maxConcurrency = Math.Max(1, options.MaxConcurrentDraining);
        SemaphoreSlim throttle = new(maxConcurrency, maxConcurrency);
        List<Task<FleetDrainResult>> upgradeTasks = new(workers.Count);

        foreach (DedicatedServerWorker worker in workers)
        {
            await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);

            upgradeTasks.Add(Task.Run(async () =>
            {
                try
                {
                    return await DrainWorkerForUpgradeAsync(worker, options, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    throttle.Release();
                }
            }, cancellationToken));
        }

        FleetDrainResult[] results = await Task.WhenAll(upgradeTasks).ConfigureAwait(false);

        int total = results.Length;
        int upgraded = results.Count(r => r.Success);
        int failed = results.Count(r => !r.Success);
        bool isComplete = failed == 0;

        return new RollingFleetUpgradeResult(
            IsComplete: isComplete,
            TotalWorkers: total,
            UpgradedWorkers: upgraded,
            FailedWorkers: failed,
            DrainResults: results);
    }

    private async Task<FleetDrainResult> DrainWorkerForUpgradeAsync(
        DedicatedServerWorker worker,
        RollingUpgradeOptions options,
        CancellationToken cancellationToken)
    {
        if (worker.CurrentState.Equals("Available", StringComparison.OrdinalIgnoreCase))
        {
            worker.CurrentState = "Restarting";
            await worker.WritePipeAsync("quit", cancellationToken: cancellationToken).ConfigureAwait(false);
            bool ok = await _workerResurrector(worker).ConfigureAwait(false);
            return new FleetDrainResult(worker.Port, ok, Immediate: true, options.UpgradeReason);
        }

        if (worker.CurrentState.Equals("InMatch", StringComparison.OrdinalIgnoreCase) || worker.IsDraining)
        {
            TaskCompletionSource<bool> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _drainCompletions[worker.Port] = tcs;
            worker.IsDraining = true;
            worker.DrainReason = options.UpgradeReason;
            worker.DrainStartTime = _timeProvider.GetUtcNow();

            using CancellationTokenSource timeoutCts = options.PerWorkerTimeout.HasValue
                ? new CancellationTokenSource(options.PerWorkerTimeout.Value)
                : new CancellationTokenSource();
            using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            try
            {
                bool ok = await tcs.Task.WaitAsync(linkedCts.Token).ConfigureAwait(false);
                return new FleetDrainResult(worker.Port, ok, Immediate: false, options.UpgradeReason);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                _logger.LogWarning("[FleetDraining] Worker on port {Port} timed out waiting to complete match ({Timeout}s)",
                    worker.Port, options.PerWorkerTimeout?.TotalSeconds);
                CancelDraining(worker.Port);
                return new FleetDrainResult(worker.Port, false, Immediate: false, options.UpgradeReason, "Drain timeout exceeded");
            }
        }

        // For non-active workers (e.g. stopped, recovering), trigger resurrection directly
        bool fallbackOk = await _workerResurrector(worker).ConfigureAwait(false);
        return new FleetDrainResult(worker.Port, fallbackOk, Immediate: true, options.UpgradeReason);
    }
}
