// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json.Serialization;
using NEXUS.Contracts.Cdn;
using NEXUS.Contracts.Telemetry;

namespace NEXUS.Contracts.Serialization;

/// <summary>
///     Source-generated JSON serialization context for NEXUS.Contracts DTOs,
///     providing reflection-free, Native AOT compatible serialization.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(DiscordPresenceUpdateDto))]
[JsonSerializable(typeof(List<DiscordPresenceUpdateDto>))]
[JsonSerializable(typeof(Manifest))]
[JsonSerializable(typeof(ManifestDocument))]
[JsonSerializable(typeof(ManifestEntry))]
[JsonSerializable(typeof(TelemetryEventMessage))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class NexusJsonContext : JsonSerializerContext
{
}
