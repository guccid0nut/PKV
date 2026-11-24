// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: AgonesJsonContext.cs
// System: TRANSMUTANSTEIN - Chat Server & Matchmaking
// Subsystem: Cloud-Native Fleet Orchestration (Agones Integration)
// ============================================================================

using System.Text.Json.Serialization;

namespace TRANSMUTANSTEIN.ChatServer.Services.Agones;

/// <summary>
///     Source-generated JSON serialization context for Agones GameServerAllocation payloads,
///     providing reflection-free, Native AOT compatible serialization.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(GameServerAllocationRequest))]
[JsonSerializable(typeof(GameServerAllocationResponse))]
[JsonSerializable(typeof(GameServerAllocationStatus))]
[JsonSerializable(typeof(GameServerAllocationSpec))]
[JsonSerializable(typeof(GameServerSelector))]
[JsonSerializable(typeof(GameServerPreferredSelector))]
[JsonSerializable(typeof(GameServerPort))]
public sealed partial class AgonesJsonContext : JsonSerializerContext
{
}
