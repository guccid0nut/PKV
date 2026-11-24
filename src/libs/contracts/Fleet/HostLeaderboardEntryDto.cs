// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.Fleet;

/// <summary>
///     Server host performance and reliability metrics displayed on the hosting leaderboard.
/// </summary>
public sealed class HostLeaderboardEntryDto
{
    public int Rank { get; set; }
    public int AccountId { get; set; }
    public required string AccountName { get; set; }
    public required string HostAccountName { get; set; }
    public required string AvatarUrl { get; set; }
    public string? DiscordUsername { get; set; }
    public int GamesHosted { get; set; }
    public int GamesPlayed { get; set; }
    public double HostingRatio { get; set; }
    public double CommunityContributionPercentage { get; set; }
    public required string HostBadgeTier { get; set; }
    public string Location { get; set; } = "USE";
    public int Instances { get; set; } = 2;
}
