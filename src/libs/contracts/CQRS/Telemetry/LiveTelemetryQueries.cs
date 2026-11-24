// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;
using MemoryPack;
using System.Text.Json.Serialization;

namespace NEXUS.Contracts.CQRS.Telemetry;

/// <summary>
/// DTO representing a top winrate leaderboard player in live telemetry.
/// </summary>
[MemoryPackable]
public sealed partial class LeaderboardPlayerDto
{
    [JsonPropertyName("rank")] public int Rank { get; set; }
    [JsonPropertyName("accountName")] public string AccountName { get; set; } = string.Empty;
    [JsonPropertyName("winRate")] public double WinRate { get; set; }
    [JsonPropertyName("matchesPlayed")] public int MatchesPlayed { get; set; }
    [JsonPropertyName("rankTier")] public string RankTier { get; set; } = "Immortal";
}

/// <summary>
/// DTO representing the live ecosystem telemetry status.
/// </summary>
[MemoryPackable]
public sealed partial class LiveTelemetryResponseDto
{
    [JsonPropertyName("onlinePlayers")] public int OnlinePlayers { get; set; }
    [JsonPropertyName("matchesInProgress")] public int MatchesInProgress { get; set; }
    [JsonPropertyName("topWinratePlayers")] public List<LeaderboardPlayerDto> TopWinratePlayers { get; set; } = [];
}

/// <summary>
/// Query to retrieve live ecosystem telemetry and leaderboard metrics.
/// </summary>
public sealed record GetLiveTelemetryQuery : IRequest<LiveTelemetryResponseDto>;
