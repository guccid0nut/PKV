// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Text.Json.Serialization;

namespace Kongor.Cli.Mcp.Services;

/// <summary>
/// Execution options for kongor_matchmaking_simulate.
/// </summary>
public sealed record MatchmakingSimulateOptions
{
    [JsonPropertyName("clientCount")]
    public int ClientCount { get; init; } = 10;

    [JsonPropertyName("serverPort")]
    public int? ServerPort { get; init; }

    [JsonPropertyName("serverHost")]
    public string ServerHost { get; init; } = "127.0.0.1";

    [JsonPropertyName("queueType")]
    public string QueueType { get; init; } = "pvp";

    [JsonPropertyName("gameType")]
    public string GameType { get; init; } = "normal";

    [JsonPropertyName("mapName")]
    public string MapName { get; init; } = "caldavar";

    [JsonPropertyName("gameMode")]
    public string GameMode { get; init; } = "ap";

    [JsonPropertyName("region")]
    public string Region { get; init; } = "USE";

    [JsonPropertyName("timeoutSeconds")]
    public int TimeoutSeconds { get; init; } = 15;

    [JsonPropertyName("simulateMatchServer")]
    public bool SimulateMatchServer { get; init; } = false;
}

/// <summary>
/// Min, Max, and Avg latency statistics in milliseconds.
/// </summary>
public sealed class LatencyStats
{
    [JsonPropertyName("minMs")]
    public double MinMs { get; init; }

    [JsonPropertyName("maxMs")]
    public double MaxMs { get; init; }

    [JsonPropertyName("avgMs")]
    public double AvgMs { get; init; }
}

/// <summary>
/// Latency summary breakdown across connection, handshake, queue join, and match formation.
/// </summary>
public sealed class MatchmakingLatencySummary
{
    [JsonPropertyName("connectMs")]
    public LatencyStats ConnectMs { get; init; } = new();

    [JsonPropertyName("handshakeMs")]
    public LatencyStats HandshakeMs { get; init; } = new();

    [JsonPropertyName("queueJoinMs")]
    public LatencyStats QueueJoinMs { get; init; } = new();

    [JsonPropertyName("matchFormationMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LatencyStats? MatchFormationMs { get; init; }
}

/// <summary>
/// Granular status and timing report for an individual virtual client.
/// </summary>
public sealed class VirtualClientStatus
{
    [JsonPropertyName("clientIndex")]
    public int ClientIndex { get; init; }

    [JsonPropertyName("accountId")]
    public int AccountId { get; init; }

    [JsonPropertyName("accountName")]
    public string AccountName { get; init; } = string.Empty;

    [JsonPropertyName("connected")]
    public bool Connected { get; init; }

    [JsonPropertyName("connectLatencyMs")]
    public double ConnectLatencyMs { get; init; }

    [JsonPropertyName("handshakeComplete")]
    public bool HandshakeComplete { get; init; }

    [JsonPropertyName("handshakeLatencyMs")]
    public double HandshakeLatencyMs { get; init; }

    [JsonPropertyName("queueJoined")]
    public bool QueueJoined { get; init; }

    [JsonPropertyName("queueJoinLatencyMs")]
    public double QueueJoinLatencyMs { get; init; }

    [JsonPropertyName("matchAssigned")]
    public bool MatchAssigned { get; init; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; init; }
}

/// <summary>
/// Server assignment details returned upon match formation.
/// </summary>
public sealed class MatchAssignmentInfo
{
    [JsonPropertyName("matchId")]
    public int MatchId { get; init; }

    [JsonPropertyName("serverAddress")]
    public string ServerAddress { get; init; } = string.Empty;

    [JsonPropertyName("serverPort")]
    public int ServerPort { get; init; }

    [JsonPropertyName("map")]
    public string Map { get; init; } = string.Empty;

    [JsonPropertyName("teamSize")]
    public int TeamSize { get; init; }
}

/// <summary>
/// Structured result returned by kongor_matchmaking_simulate.
/// </summary>
public sealed class MatchmakingSimulateResult
{
    [JsonPropertyName("status")]
    public string Status { get; init; } = "success";

    [JsonPropertyName("resolvedPort")]
    public int ResolvedPort { get; init; }

    [JsonPropertyName("discoveryTier")]
    public string DiscoveryTier { get; init; } = "default";

    [JsonPropertyName("clientCount")]
    public int ClientCount { get; init; }

    [JsonPropertyName("successfulConnections")]
    public int SuccessfulConnections { get; init; }

    [JsonPropertyName("handshakeCount")]
    public int HandshakeCount { get; init; }

    [JsonPropertyName("queueJoinedCount")]
    public int QueueJoinedCount { get; init; }

    [JsonPropertyName("totalElapsedMs")]
    public double TotalElapsedMs { get; init; }

    [JsonPropertyName("latency")]
    public MatchmakingLatencySummary Latency { get; init; } = new();

    [JsonPropertyName("matchAssigned")]
    public bool MatchAssigned { get; init; }

    [JsonPropertyName("matchAssignment")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MatchAssignmentInfo? MatchAssignment { get; init; }

    [JsonPropertyName("clients")]
    public IReadOnlyList<VirtualClientStatus> Clients { get; init; } = [];

    [JsonPropertyName("socketsCleaned")]
    public int SocketsCleaned { get; init; }

    [JsonPropertyName("errorMessage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorMessage { get; init; }

    [JsonPropertyName("remediationCategory")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RemediationCategory { get; init; }

    [JsonPropertyName("remediationHint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RemediationHint { get; init; }

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; init; } = TimeProvider.System.GetUtcNow().ToString("o");
}

/// <summary>
/// Service interface for headless ChatServer matchmaking benchmarking and port resolution.
/// </summary>
public interface IMatchmakingSimulateService
{
    /// <summary>
    /// Resolves the target ChatServer TCP client port using 3-tier discovery:
    /// Tier 1: Explicit parameter (1-65535) -> "explicit"
    /// Tier 2: Aspire environment configuration -> "aspire"
    /// Tier 3: Fallback default (11031) -> "default"
    /// </summary>
    (int Port, string DiscoveryTier) ResolveChatServerPort(int? explicitPort, Func<string, string?>? envLookup = null);

    /// <summary>
    /// Executes headless simulation connecting up to 10 concurrent virtual clients,
    /// completing binary handshake, placing groups in queue, tracking latency metrics,
    /// and cleanly closing all sockets without leaks.
    /// </summary>
    Task<MatchmakingSimulateResult> SimulateMatchmakingAsync(
        MatchmakingSimulateOptions options,
        CancellationToken cancellationToken = default);
}
