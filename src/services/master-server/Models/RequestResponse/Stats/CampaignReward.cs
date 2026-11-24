// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace KONGOR.MasterServer.Models.RequestResponse.Stats;

public partial class CampaignReward
{
    /// <summary>
    ///     The previous level of the player in the Con (Champions of Newerth) campaign.
    /// </summary>
    [PHPProperty("old_lvl")]
    public int PreviousCampaignLevel { get; init; } = 5;

    /// <summary>
    ///     The current level of the player in the Con (Champions of Newerth) campaign.
    /// </summary>
    [PHPProperty("curr_lvl")]
    public int CurrentCampaignLevel { get; init; } = 6;

    /// <summary>
    ///     The next level of the player in the Con (Champions of Newerth) campaign.
    /// </summary>
    [PHPProperty("next_lvl")]
    public int NextLevel { get; init; } = 0;

    /// <summary>
    ///     The rank required to level up in the Con (Champions of Newerth) campaign.
    /// </summary>
    [PHPProperty("require_rank")]
    public int RequireRank { get; init; } = 0;

    /// <summary>
    ///     The number of additional matches the player needs to play to level up in the Con (Champions of Newerth) campaign.
    /// </summary>
    [PHPProperty("need_more_play")]
    public int NeedMorePlay { get; init; } = 0;

    /// <summary>
    ///     Whether the current level reward has already been claimed by the player.
    /// </summary>
    [PHPProperty("reward_taken")]
    public int RewardTaken { get; init; } = 0;

    /// <summary>
    ///     The percentage progress towards the next level in the Con (Champions of Newerth) campaign before the current match.
    /// </summary>
    [PHPProperty("percentage_before")]
    public string PercentageBefore { get; init; } = "0.92";

    /// <summary>
    ///     The percentage progress towards the next level in the Con (Champions of Newerth) campaign.
    /// </summary>
    [PHPProperty("percentage")]
    public string Percentage { get; init; } = "1.00";
}
