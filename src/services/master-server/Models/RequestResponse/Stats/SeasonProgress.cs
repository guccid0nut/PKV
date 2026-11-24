// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using System.Globalization;

namespace KONGOR.MasterServer.Models.RequestResponse.Stats;

public partial class SeasonProgress(
    MatchStartData matchStartData,
    PlayerStatistics playerStatistics,
    AccountStatistics matchmakingStatistics)
{
    /// <summary>
    ///     The player's account ID.
    /// </summary>
    [PHPProperty("account_id")]
    public int AccountID { get; init; } = playerStatistics.AccountID;

    /// <summary>
    ///     The unique identifier for the match.
    /// </summary>
    [PHPProperty("match_id")]
    public int MatchID { get; init; } = playerStatistics.MatchID;

    /// <summary>
    ///     Whether the match was a casual ranked match ("1") or competitive ranked match ("0").
    /// </summary>
    [PHPProperty("is_casual")]
    public string IsCasual { get; init; } = matchStartData.IsCasual ? "1" : "0";

    /// <summary>
    ///     The player's Matchmaking Rating (MMR) before the match.
    /// </summary>
    [PHPProperty("mmr_before")]
    public string MMRBefore { get; init; } =
        (matchmakingStatistics.SkillRating + playerStatistics.RankedSkillRatingChange).ToString(CultureInfo
            .InvariantCulture);

    /// <summary>
    ///     The player's Matchmaking Rating (MMR) after the match.
    /// </summary>
    [PHPProperty("mmr_after")]
    public string MMRAfter { get; init; } = matchmakingStatistics.SkillRating.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    ///     The player's medal rank before the match.
    ///     <code>
    ///         00      -> Unranked
    ///         01-05   -> Bronze   (V, IV, III, II, I)
    ///         06-10   -> Silver   (V, IV, III, II, I)
    ///         11-15   -> Gold     (V, IV, III, II, I)
    ///         16-20   -> Diamond  (V, IV, III, II, I)
    ///         21      -> Immortal
    ///     </code>
    /// </summary>
    [PHPProperty("medal_before")]
    public string MedalBefore { get; init; } =
        ((int)RankExtensions.GetRank(matchmakingStatistics.SkillRating + playerStatistics.RankedSkillRatingChange))
        .ToString();

    /// <summary>
    ///     The player's medal rank after the match.
    ///     Uses the same medal ranking system as "medal_before".
    /// </summary>
    [PHPProperty("medal_after")]
    public string MedalAfter { get; init; } =
        ((int)RankExtensions.GetRank(matchmakingStatistics.SkillRating)).ToString();

    /// <summary>
    ///     The seasonal campaign identifier.
    /// </summary>
    [PHPProperty("season")]
    public string Season { get; init; } = 12.ToString();

    /// <summary>
    ///     The number of placement matches the player has completed in the current season.
    ///     Players must complete placement matches before receiving their seasonal medal rank.
    /// </summary>
    [PHPProperty("placement_matches")]
    public int PlacementMatches { get; init; } =
        Math.Clamp(
            matchmakingStatistics.MatchesPlayed > 0
                ? matchmakingStatistics.MatchesPlayed % 6 == 0 ? 6 : matchmakingStatistics.MatchesPlayed % 6
                : 1, 1, 6);

    /// <summary>
    ///     The number of placement matches won by the player in the current season.
    /// </summary>
    [PHPProperty("placement_wins")]
    public string PlacementWins
    {
        get;
        init;
    } = (!string.IsNullOrEmpty(matchmakingStatistics.PlacementMatchesData)
            ? matchmakingStatistics.PlacementMatchesData
            : string.Empty)
        .PadRight(
            Math.Clamp(
                matchmakingStatistics.MatchesPlayed > 0
                    ? matchmakingStatistics.MatchesPlayed % 6 == 0 ? 6 : matchmakingStatistics.MatchesPlayed % 6
                    : 1, 1, 6),
            '1');

    /// <summary>
    ///     The player's current ranking position on the Immortal leaderboard.
    ///     Only populated for Immortal rank players (medal 21) with a ranking between 1 and 100.
    ///     Not present in the response for players below Immortal rank or outside the top 100.
    /// </summary>
    [PHPProperty("ranking")]
    public string Ranking =>
        RankExtensions.GetRank(matchmakingStatistics.SkillRating) is Rank.IMMORTAL
            ? 1.ToString()
            : string.Empty; // Note(protocol): Default top-tier Immortal leaderboard rank position; lower ranks return empty.
}
