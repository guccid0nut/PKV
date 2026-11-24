// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace KONGOR.MasterServer.Models.RequestResponse.SRP;

public partial class MilestonesAwardTooltip
{
    /// <summary>
    ///     Awarded for hero assists.
    /// </summary>
    [PHPProperty("heroassists")]
    public MilestoneAwardTooltip HeroAssists { get; set; } = new()
    {
        AwardName = "heroassists",
        Experience = "100",
        GoblinCoins = "5",
        Modulo = "250"
    };

    /// <summary>
    ///     Awarded for hero kills.
    /// </summary>
    [PHPProperty("herokills")]
    public MilestoneAwardTooltip HeroKills { get; set; } = new()
    {
        AwardName = "herokills",
        Experience = "100",
        GoblinCoins = "5",
        Modulo = "250"
    };

    /// <summary>
    ///     Awarded for killing heroes after taunting them.
    /// </summary>
    [PHPProperty("smackdown")]
    public MilestoneAwardTooltip Smackdown { get; set; } = new()
    {
        AwardName = "smackdown",
        Experience = "50",
        GoblinCoins = "1",
        Modulo = "10"
    };

    /// <summary>
    ///     Awarded for placing wards.
    /// </summary>
    [PHPProperty("wards")]
    public MilestoneAwardTooltip Wards { get; set; } = new()
    {
        AwardName = "wards",
        Experience = "100",
        GoblinCoins = "5",
        Modulo = "50"
    };

    /// <summary>
    ///     Awarded for winning matches.
    /// </summary>
    [PHPProperty("wins")]
    public MilestoneAwardTooltip Wins { get; set; } = new()
    {
        AwardName = "wins",
        Experience = "200",
        GoblinCoins = "10",
        Modulo = "50"
    };
}
