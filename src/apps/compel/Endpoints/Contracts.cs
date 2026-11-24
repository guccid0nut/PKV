// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace COMPEL.Endpoints;

/// <summary>
///     The response to a latency probe against the control plane.
/// </summary>
public sealed record PingResponse(string Application, string Version, long ServerTimeUnixMilliseconds);

/// <summary>
///     The game, voice, and ping ports the manager has been allocated. Local ports are what the servers bind; public ports
///     are what clients connect to (equal to the local ports unless the proxy is enabled).
/// </summary>
public sealed record PortAllocationResponse(
    int GameStart,
    int GameEnd,
    int VoiceStart,
    int VoiceEnd,
    int PublicGameStart,
    int PublicGameEnd,
    int PublicVoiceStart,
    int PublicVoiceEnd,
    int PingPort
);

/// <summary>
///     The runtime and resource status of an individual dedicated game server worker.
/// </summary>
public sealed record DedicatedWorkerStatus(
    int InstanceIndex,
    int Port,
    int VoicePort,
    int PublicGamePort,
    int? ProcessId,
    bool Running,
    long WorkingSetBytes,
    double WorkingSetMb,
    double UptimeSeconds,
    int CrashCount,
    string State = "Available",
    double TickrateHz = 0.0,
    double TickVarianceMs = 0.0,
    string? ServerName = null,
    bool? IpcConnected = null,
    string? IpcPath = null,
    string? IpcTransport = null
);

/// <summary>
///     Captured log line emitted by a dedicated game server or recorded by operator command execution.
/// </summary>
public sealed record DedicatedServerLogLine(DateTimeOffset Timestamp, string Text, bool IsError, string Source = "server");

/// <summary>
///     Recent captured console and event logs for an individual dedicated game server.
/// </summary>
public sealed record DedicatedServerLogsResponse(int Port, string ServerName, IReadOnlyList<DedicatedServerLogLine> Logs);

/// <summary>
///     A snapshot of the match server manager's configuration and runtime state.
/// </summary>
public sealed record StatusResponse(
    string Application,
    string Version,
    string ServerNamePrefix,
    string Location,
    string? ServerAddress,
    int Instances,
    bool UseProxy,
    int PortRangeOffset,
    PortAllocationResponse Ports,
    string? DistributionVersion,
    string SynchronisationState,
    bool ManagerRunning,
    bool ProxyRunning,
    bool? PingResponderBound,
    int ProxyFailedForwarderCount,
    long ProxyDroppedDatagramCount = 0,
    bool ProxyIsUnderAttack = false,
    double UptimeSeconds = 0,
    int ActiveWorkers = 0,
    IReadOnlyList<DedicatedWorkerStatus>? Workers = null,
    string? OperatingSystem = null,
    COMPEL.Services.Telemetry.RlStatusSnapshot? RlArena = null
);

/// <summary>
///     The outcome of a lifecycle action (synchronise, start, stop, or restart) requested against the control plane.
/// </summary>
public sealed record ActionResponse(string Action, bool Accepted, string Detail);

/// <summary>
///     Command execution request targeting a dedicated server worker.
/// </summary>
public sealed record ExecuteCommandRequest(string Command);

/// <summary>
///     Result of piping a command to a dedicated server worker.
/// </summary>
public sealed record ExecuteCommandResponse(int Port, string Command, bool Success, string Channel, string Message);
