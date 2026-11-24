// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: AgonesAllocationModels.cs
// System: TRANSMUTANSTEIN - Chat Server & Matchmaking
// Subsystem: Cloud-Native Fleet Orchestration (Agones Integration)
// ============================================================================

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace TRANSMUTANSTEIN.ChatServer.Services.Agones;

/// <summary>
///     Represents an Agones GameServerAllocation request payload.
///     Supports both direct Allocator service and Kubernetes CRD representations.
/// </summary>
public sealed class GameServerAllocationRequest
{
    [JsonPropertyName("apiVersion")]
    public string ApiVersion { get; set; } = "allocation.agones.dev/v1";

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "GameServerAllocation";

    [JsonPropertyName("namespace")]
    public string Namespace { get; set; } = "default";

    [JsonPropertyName("spec")]
    public GameServerAllocationSpec Spec { get; set; } = new();
}

public sealed class GameServerAllocationSpec
{
    [JsonPropertyName("multiClusterSetting")]
    public MultiClusterSetting? MultiClusterSetting { get; set; }

    [JsonPropertyName("required")]
    public GameServerSelector? Required { get; set; }

    [JsonPropertyName("preferred")]
    public List<GameServerPreferredSelector>? Preferred { get; set; }

    [JsonPropertyName("selectors")]
    public List<GameServerSelector>? Selectors { get; set; }
}

public sealed class MultiClusterSetting
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }
}

public sealed class GameServerSelector
{
    [JsonPropertyName("matchLabels")]
    public Dictionary<string, string> MatchLabels { get; set; } = new();
}

public sealed class GameServerPreferredSelector
{
    [JsonPropertyName("priority")]
    public int Priority { get; set; } = 1;

    [JsonPropertyName("matchLabels")]
    public Dictionary<string, string> MatchLabels { get; set; } = new();
}

/// <summary>
///     Represents the response returned by Agones upon processing an allocation request.
/// </summary>
public sealed class GameServerAllocationResponse
{
    [JsonPropertyName("status")]
    public GameServerAllocationStatus? Status { get; set; }
}

public sealed class GameServerAllocationStatus
{
    /// <summary>
    ///     Allocation state: "Allocated", "UnAllocated", or "Contention".
    /// </summary>
    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("gameServerName")]
    public string GameServerName { get; set; } = string.Empty;

    [JsonPropertyName("address")]
    public string Address { get; set; } = string.Empty;

    [JsonPropertyName("nodeName")]
    public string NodeName { get; set; } = string.Empty;

    [JsonPropertyName("ports")]
    public List<GameServerPort> Ports { get; set; } = new();

    public bool IsAllocated => string.Equals(State, "Allocated", System.StringComparison.OrdinalIgnoreCase);
}

public sealed class GameServerPort
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("port")]
    public int Port { get; set; }
}

/// <summary>
///     High-level result of a matchmaker allocation attempt.
/// </summary>
public sealed class GameServerAllocationResult
{
    public bool Succeeded { get; set; }
    public string State { get; set; } = string.Empty;
    public string GameServerName { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public int GamePort { get; set; }
    public int VoicePort { get; set; }
    public string? ErrorMessage { get; set; }

    public static GameServerAllocationResult Failed(string error) => new()
    {
        Succeeded = false,
        State = "Failed",
        ErrorMessage = error
    };

    public static GameServerAllocationResult Unallocated(string message = "No matching GameServer available in fleet") => new()
    {
        Succeeded = false,
        State = "UnAllocated",
        ErrorMessage = message
    };

    public static GameServerAllocationResult Success(string gameServerName, string address, int gamePort, int voicePort) => new()
    {
        Succeeded = true,
        State = "Allocated",
        GameServerName = gameServerName,
        Address = address,
        GamePort = gamePort,
        VoicePort = voicePort
    };
}
