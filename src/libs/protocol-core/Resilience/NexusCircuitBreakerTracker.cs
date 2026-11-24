// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Diagnostics;

namespace ASPIRE.Common.Resilience;

/// <summary>
///     Represents the operational snapshot of a resilience circuit breaker.
/// </summary>
public sealed record CircuitBreakerStateInfo(
    string PipelineName,
    string State,
    DateTimeOffset LastTransitionUtc,
    TimeSpan? BreakDuration = null,
    string? FailureReason = null);

/// <summary>
///     Thread-safe global tracker for resilience circuit breaker states across microservices.
///     Integrates directly into .NET Aspire health checks and OpenTelemetry metrics.
/// </summary>
public static class NexusCircuitBreakerTracker
{
    private static readonly ConcurrentDictionary<string, CircuitBreakerStateInfo> CircuitStates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Records a circuit breaker transitioning to the OPEN (tripped) state.
    /// </summary>
    public static void RecordOpen(string pipelineName, TimeSpan breakDuration, string? failureReason = null)
    {
        if (string.IsNullOrWhiteSpace(pipelineName)) pipelineName = "unknown-pipeline";

        CircuitBreakerStateInfo stateInfo = new(
            pipelineName,
            "Open",
            TimeProvider.System.GetUtcNow(),
            breakDuration,
            failureReason);

        CircuitStates[pipelineName] = stateInfo;

        Trace.WriteLine($"[RESILIENCE-TRACKER] Circuit breaker OPENED for pipeline '{pipelineName}'. BreakDuration: {breakDuration.TotalSeconds:F1}s, Reason: {failureReason ?? "Unknown"}");
    }

    /// <summary>
    ///     Records a circuit breaker transitioning to the HALF-OPEN (probing) state.
    /// </summary>
    public static void RecordHalfOpen(string pipelineName)
    {
        if (string.IsNullOrWhiteSpace(pipelineName)) pipelineName = "unknown-pipeline";

        CircuitBreakerStateInfo stateInfo = new(
            pipelineName,
            "HalfOpen",
            TimeProvider.System.GetUtcNow());

        CircuitStates[pipelineName] = stateInfo;

        Trace.WriteLine($"[RESILIENCE-TRACKER] Circuit breaker HALF-OPEN for pipeline '{pipelineName}'. Probing downstream service.");
    }

    /// <summary>
    ///     Records a circuit breaker transitioning to the CLOSED (healthy/recovered) state.
    /// </summary>
    public static void RecordClosed(string pipelineName)
    {
        if (string.IsNullOrWhiteSpace(pipelineName)) pipelineName = "unknown-pipeline";

        CircuitStates.TryRemove(pipelineName, out _);

        Trace.WriteLine($"[RESILIENCE-TRACKER] Circuit breaker CLOSED for pipeline '{pipelineName}'. Downstream service recovered.");
    }

    /// <summary>
    ///     Checks if any resilience circuit breakers are currently in an OPEN or HALF-OPEN state.
    /// </summary>
    public static bool HasOpenCircuits(out string[] openPipelines, out IReadOnlyList<CircuitBreakerStateInfo> details)
    {
        List<CircuitBreakerStateInfo> openList = [];

        foreach (KeyValuePair<string, CircuitBreakerStateInfo> kvp in CircuitStates)
        {
            if (string.Equals(kvp.Value.State, "Open", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(kvp.Value.State, "HalfOpen", StringComparison.OrdinalIgnoreCase))
            {
                openList.Add(kvp.Value);
            }
        }

        openPipelines = openList.Select(x => x.PipelineName).ToArray();
        details = openList.AsReadOnly();
        return openList.Count > 0;
    }

    /// <summary>
    ///     Convenience overload checking if any circuit is currently open.
    /// </summary>
    public static bool HasOpenCircuits(out string[] openPipelines)
    {
        return HasOpenCircuits(out openPipelines, out _);
    }

    /// <summary>
    ///     Retrieves the current state info for a specific pipeline, or null if closed/untracked.
    /// </summary>
    public static CircuitBreakerStateInfo? GetState(string pipelineName)
    {
        if (string.IsNullOrWhiteSpace(pipelineName)) return null;
        return CircuitStates.TryGetValue(pipelineName, out CircuitBreakerStateInfo? info) ? info : null;
    }

    /// <summary>
    ///     Retrieves all active tracked circuit breaker states.
    /// </summary>
    public static IReadOnlyDictionary<string, CircuitBreakerStateInfo> GetAllStates()
    {
        return new Dictionary<string, CircuitBreakerStateInfo>(CircuitStates, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Resets all tracked states (primarily intended for unit testing).
    /// </summary>
    public static void Reset()
    {
        CircuitStates.Clear();
    }
}
