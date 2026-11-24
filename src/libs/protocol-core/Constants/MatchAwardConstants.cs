// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace ASPIRE.Common.Constants;

/// <summary>
///     Defines canonical protocol dictionary keys and descriptive names for end-of-match award metrics (awd_*).
/// </summary>
public static class MatchAwardConstants
{
    /// <summary>
    ///     Award for Most Annihilations (5 kills within multi-kill window). (awd_mann)
    /// </summary>
    public const string MostAnnihilations = "awd_mann";

    /// <summary>
    ///     Award for Most Quad Kills (4 kills within multi-kill window). (awd_mqk)
    /// </summary>
    public const string MostQuadKills = "awd_mqk";

    /// <summary>
    ///     Award for Largest Kill Streak (without dying). (awd_lgks)
    /// </summary>
    public const string LargestKillStreak = "awd_lgks";

    /// <summary>
    ///     Award for Most Smackdowns (taunt kills). (awd_msd)
    /// </summary>
    public const string MostSmackdowns = "awd_msd";

    /// <summary>
    ///     Award for Most Hero Kills. (awd_mkill)
    /// </summary>
    public const string MostKills = "awd_mkill";

    /// <summary>
    ///     Award for Most Hero Assists. (awd_masst)
    /// </summary>
    public const string MostAssists = "awd_masst";

    /// <summary>
    ///     Award for Least Deaths. (awd_ledth)
    /// </summary>
    public const string LeastDeaths = "awd_ledth";

    /// <summary>
    ///     Award for Most Building / Tower Damage Dealt. (awd_mbdmg)
    /// </summary>
    public const string MostBuildingDamage = "awd_mbdmg";

    /// <summary>
    ///     Award for Most Enemy Wards Killed / Destroyed. (awd_mwk)
    /// </summary>
    public const string MostWardsKilled = "awd_mwk";

    /// <summary>
    ///     Award for Most Hero Damage Dealt. (awd_mhdd)
    /// </summary>
    public const string MostHeroDamageDealt = "awd_mhdd";

    /// <summary>
    ///     Award for Highest Creep Score (Creep Kills + Neutral Creep Kills + Denies). (awd_hcs)
    /// </summary>
    public const string HighestCreepScore = "awd_hcs";
}
