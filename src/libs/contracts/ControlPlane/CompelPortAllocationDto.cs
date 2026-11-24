// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json.Serialization;

namespace NEXUS.Contracts.ControlPlane;

/// <summary>
///     Detailed port range allocations for local match server instances and public client connections.
/// </summary>
public sealed class CompelPortAllocationDto
{
    [JsonPropertyName("gameStart")] public int GameStart { get; set; }

    [JsonPropertyName("gameEnd")] public int GameEnd { get; set; }

    [JsonPropertyName("voiceStart")] public int VoiceStart { get; set; }

    [JsonPropertyName("voiceEnd")] public int VoiceEnd { get; set; }

    [JsonPropertyName("publicGameStart")] public int PublicGameStart { get; set; }

    [JsonPropertyName("publicGameEnd")] public int PublicGameEnd { get; set; }

    [JsonPropertyName("publicVoiceStart")] public int PublicVoiceStart { get; set; }

    [JsonPropertyName("publicVoiceEnd")] public int PublicVoiceEnd { get; set; }

    [JsonPropertyName("pingPort")] public int PingPort { get; set; }
}
