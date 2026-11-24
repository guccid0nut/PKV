// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json.Serialization;
using ASPIRE.Common.IPC;
using ASPIRE.Common.Models.ServerManagement;
using ASPIRE.Common.Telemetry;
using NEXUS.Contracts.Telemetry;

namespace Kongor.Core.Serialization.JSON;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(MatchServerManager))]
[JsonSerializable(typeof(List<MatchServerManager>))]
[JsonSerializable(typeof(MatchServer))]
[JsonSerializable(typeof(List<MatchServer>))]
[JsonSerializable(typeof(MatchStartData))]
[JsonSerializable(typeof(List<MatchStartData>))]
[JsonSerializable(typeof(LiveServerTickPayloadDto))]
[JsonSerializable(typeof(EntityTelemetryEvent))]
[JsonSerializable(typeof(List<EntityTelemetryEvent>))]
[JsonSerializable(typeof(WillowmakerIpcMessage))]
[JsonSerializable(typeof(EngineStateDto))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(DiscordPresenceUpdateDto))]
[JsonSerializable(typeof(List<DiscordPresenceUpdateDto>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(List<int>))]
public sealed partial class KongorCoreJsonContext : JsonSerializerContext
{
}
