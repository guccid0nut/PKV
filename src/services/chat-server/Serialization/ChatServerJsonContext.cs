// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json.Serialization;
using NEXUS.Contracts.CQRS.Telemetry;
using NEXUS.Contracts.Telemetry;

namespace TRANSMUTANSTEIN.ChatServer.Serialization;

/// <summary>
///     Source-generated JSON serialization context for TRANSMUTANSTEIN.ChatServer,
///     providing reflection-free, Native AOT compatible serialization across
///     Redis telemetry channels, active region keys, population breakdowns, and Discord presence updates.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(DiscordPresenceUpdateDto))]
[JsonSerializable(typeof(List<DiscordPresenceUpdateDto>))]
[JsonSerializable(typeof(TelemetryEventMessage))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(PopulationBreakdownResponseDto))]
[JsonSerializable(typeof(RegionCountDto))]
[JsonSerializable(typeof(TmmQueueBreakdownDto))]
[JsonSerializable(typeof(List<RegionCountDto>))]
[JsonSerializable(typeof(List<TmmQueueBreakdownDto>))]
public sealed partial class ChatServerJsonContext : JsonSerializerContext
{
}
