// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using Microsoft.Extensions.Logging;

namespace COMPEL.Logging;

public static partial class Log
{
    // MatchServerManagerSupervisor
    [LoggerMessage(Level = LogLevel.Information, Message = "[Supervisor] MatchServerManager Supervisor loop started. Manager ID: {ManagerId}")]
    public static partial void LogSupervisorStarted(this ILogger logger, int managerId);

    [LoggerMessage(Level = LogLevel.Information, Message = "[Supervisor] MatchServerManager Supervisor loop stopped")]
    public static partial void LogSupervisorStopped(this ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "[Supervisor] Spawning HoN game server process on Port {Port} (Slot #{Slot})")]
    public static partial void LogSpawningServer(this ILogger logger, ushort port, int slot);

    [LoggerMessage(Level = LogLevel.Information, Message = "[Supervisor] Successfully assigned Process {ProcessId} to Win32 ProcessTreeSandbox Job Object")]
    public static partial void LogSandboxAssigned(this ILogger logger, int processId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[Supervisor] Server process {ProcessId} exited unexpectedly with code {ExitCode}")]
    public static partial void LogServerProcessExited(this ILogger logger, int processId, int exitCode);

    [LoggerMessage(Level = LogLevel.Error, Message = "[Supervisor] Failed to spawn HoN game server process")]
    public static partial void LogServerSpawnFailed(this ILogger logger, Exception ex);

    // DistributionSynchronisationService
    [LoggerMessage(Level = LogLevel.Information, Message = "Initial CDN Synchronisation Is Disabled; Proceeding With The Existing Local Distribution")]
    public static partial void LogCdnSyncDisabled(this ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Synchronising Match Server Distribution {Variant} From {Host} Into {Directory}")]
    public static partial void LogSynchronisingDistribution(this ILogger logger, string variant, string host, string directory);

    [LoggerMessage(Level = LogLevel.Information, Message = "Synchronisation Plan: {Plan}")]
    public static partial void LogSynchronisationPlan(this ILogger logger, string plan);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Downloaded {Path} ({Size:N0} Bytes)")]
    public static partial void LogChunkDownloaded(this ILogger logger, string path, long size);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Deleted {Path}")]
    public static partial void LogFileDeleted(this ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Synchronisation Did Not Fully Complete ({Failures} File(s) Failed); Retrying In {Seconds} Seconds")]
    public static partial void LogSynchronisationIncomplete(this ILogger logger, int failures, double seconds);

    [LoggerMessage(Level = LogLevel.Error, Message = "Distribution Synchronisation Failed")]
    public static partial void LogDistributionSyncFailed(this ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Proceeding With The Existing Local Distribution At {InstallationDirectory}")]
    public static partial void LogProceedingWithLocalDistribution(this ILogger logger, string installationDirectory);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No Local Distribution Is Present; Retrying Synchronisation In {Seconds} Seconds")]
    public static partial void LogNoLocalDistributionRetrying(this ILogger logger, double seconds);

    // UDPProxyService
    [LoggerMessage(Level = LogLevel.Information, Message = "[UDPProxy] Starting UDP Proxy on {BindingAddress}:{Port}")]
    public static partial void LogUdpProxyStarted(this ILogger logger, string bindingAddress, int port);

    [LoggerMessage(Level = LogLevel.Information, Message = "[UDPProxy] Stopping UDP Proxy")]
    public static partial void LogUdpProxyStopped(this ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[UDPProxy] Rate limited packet from {RemoteEndPoint}. Drops: {DropCount}")]
    public static partial void LogUdpRateLimited(this ILogger logger, string remoteEndPoint, long dropCount);

    // UDPPingResponder
    [LoggerMessage(Level = LogLevel.Information, Message = "[UDPPing] UDP Ping Responder listening on {BindingAddress}:{Port}")]
    public static partial void LogUdpPingStarted(this ILogger logger, string bindingAddress, int port);

    [LoggerMessage(Level = LogLevel.Information, Message = "[UDPPing] Stopping UDP Ping Responder")]
    public static partial void LogUdpPingStopped(this ILogger logger);

    // Telemetry
    [LoggerMessage(Level = LogLevel.Information, Message = "[CompelTelemetry] Relaying {Count} telemetry events to Master Server")]
    public static partial void LogCompelTelemetryRelayed(this ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[CompelTelemetry] Failed to relay telemetry batch")]
    public static partial void LogCompelTelemetryFailed(this ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "[CombatLog] Parsed combat log with {EventCount} events for Match #{MatchId}")]
    public static partial void LogCombatLogParsed(this ILogger logger, int eventCount, int matchId);
}
