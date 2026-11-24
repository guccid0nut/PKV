// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace ASPIRE.Common.Serialization.PHP;

public class SimpleSeasonStats : IPhpSerializable
{
    [PHPProperty("wins")] public required int RankedMatchesWon { get; set; }

    [PHPProperty("losses")] public required int RankedMatchesLost { get; set; }

    [PHPProperty("h_wins")] public int HardcoreWins { get; set; }

    [PHPProperty("h_losses")] public int HardcoreLosses { get; set; }

    [PHPProperty("con_wins")] public int CasualWins { get; set; }

    [PHPProperty("con_losses")] public int CasualLosses { get; set; }

    [PHPProperty("win_streak")] public required int WinStreak { get; set; }

    [PHPProperty("rank")] public required string CurrentRank { get; set; }

    [PHPProperty("current_level")] public string LegacyLevel => CurrentRank;

    /// <summary>
    ///     The current matchmaking rating (MMR).
    ///     References `playerstats_mmr` in Lua (Index 14).
    /// </summary>
    [PHPProperty("smr")]
    public required string RankedRating { get; set; }

    /// <summary>
    ///     The highest rank achieved in the season.
    /// </summary>
    [PHPProperty("highest_level_current")]
    public string? HighestRank { get; set; }

    [PHPProperty("is_placement")] public required int InPlacementPhase { get; set; }

    [PHPProperty("pub_skill")] public string PublicRating { get; set; } = "1500.000";

    [PHPProperty("kam_rating")] public string CasualRating { get; set; } = "1500.000";

    // Legacy Alias Mappings for Lua
    // Lua expects 'rnk_' prefix for Normal mode and 'cs_' for Casual.

    [PHPProperty("rnk_wins")] public int RnkWins => RankedMatchesWon;

    [PHPProperty("rnk_losses")] public int RnkLosses => RankedMatchesLost;

    [PHPProperty("rnk_games_played")] public int RnkGamesPlayed => RankedMatchesWon + RankedMatchesLost;

    [PHPProperty("cs_wins")] public int CsWins => CasualWins;

    [PHPProperty("cs_losses")] public int CsLosses => CasualLosses;

    [PHPProperty("cs_games_played")] public int CsGamesPlayed => CasualWins + CasualLosses;

    [PHPProperty("acc_wins")] public int AccWins => RankedMatchesWon;

    [PHPProperty("acc_games_played")] public int AccGamesPlayed => RankedMatchesWon + RankedMatchesLost;

    // Helper to hold raw stats for legacy mapping
    // Internal to avoid PHP serialization
    public PlayerStatisticsAggregatedDTO? GenericStats { get; set; }

    /// <summary>
    ///     Serializes season stats directly into PHP wire format with zero reflection and zero allocations.
    /// </summary>
    public void SerializePhp(ref Utf8PhpWriter writer)
    {
        writer.WriteArrayHeader(22);

        writer.WriteKey("wins");
        writer.WriteInt(RankedMatchesWon);

        writer.WriteKey("losses");
        writer.WriteInt(RankedMatchesLost);

        writer.WriteKey("h_wins");
        writer.WriteInt(HardcoreWins);

        writer.WriteKey("h_losses");
        writer.WriteInt(HardcoreLosses);

        writer.WriteKey("con_wins");
        writer.WriteInt(CasualWins);

        writer.WriteKey("con_losses");
        writer.WriteInt(CasualLosses);

        writer.WriteKey("win_streak");
        writer.WriteInt(WinStreak);

        writer.WriteKey("rank");
        writer.WriteString(CurrentRank ?? string.Empty);

        writer.WriteKey("current_level");
        writer.WriteString(LegacyLevel ?? string.Empty);

        writer.WriteKey("smr");
        writer.WriteString(RankedRating ?? string.Empty);

        writer.WriteKey("highest_level_current");
        if (HighestRank is not null)
            writer.WriteString(HighestRank);
        else
            writer.WriteNull();

        writer.WriteKey("is_placement");
        writer.WriteInt(InPlacementPhase);

        writer.WriteKey("pub_skill");
        writer.WriteString(PublicRating ?? "1500.000");

        writer.WriteKey("kam_rating");
        writer.WriteString(CasualRating ?? "1500.000");

        writer.WriteKey("rnk_wins");
        writer.WriteInt(RnkWins);

        writer.WriteKey("rnk_losses");
        writer.WriteInt(RnkLosses);

        writer.WriteKey("rnk_games_played");
        writer.WriteInt(RnkGamesPlayed);

        writer.WriteKey("cs_wins");
        writer.WriteInt(CsWins);

        writer.WriteKey("cs_losses");
        writer.WriteInt(CsLosses);

        writer.WriteKey("cs_games_played");
        writer.WriteInt(CsGamesPlayed);

        writer.WriteKey("acc_wins");
        writer.WriteInt(AccWins);

        writer.WriteKey("acc_games_played");
        writer.WriteInt(AccGamesPlayed);

        writer.WriteArrayFooter();
    }
}
