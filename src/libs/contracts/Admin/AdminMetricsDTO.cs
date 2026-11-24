// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using NEXUS.Contracts.CQRS.Telemetry;

namespace NEXUS.Contracts.Admin;

/// <summary>
///     Ecosystem-wide telemetry, performance metrics, and queue snapshot for admin monitoring.
/// </summary>
public record AdminMetricsDTO(
    int ConcurrentConnectedUsers,
    int ActiveMatchmakingQueues,
    double AverageWaitTimeSeconds,
    int ActiveMatchServers,
    List<string> ActiveGameRegions,
    long SlowQueriesTotal,
    long MasterServerRequestsTotal,
    long ChatCommandsTotal,
    double P95LatencyMs,
    Dictionary<string, long> ServiceMemoryBytes,
    DateTimeOffset Timestamp,
    PopulationBreakdownResponseDto? PopulationBreakdown = null
);
