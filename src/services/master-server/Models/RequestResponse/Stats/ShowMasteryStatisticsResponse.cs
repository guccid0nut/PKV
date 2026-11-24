// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace KONGOR.MasterServer.Models.RequestResponse.Stats;

/// <summary>
///     Response for show_stats with table="mastery" (hero mastery progression data).
/// </summary>
public partial class ShowMasteryStatisticsResponse(Account account)
{
    [PHPProperty("account_id")] public string AccountID { get; init; } = account.ID.ToString();

    [PHPProperty("nickname")] public string Nickname { get; init; } = account.Name;

    [PHPProperty("name")] public string ClanName { get; init; } = account.Clan?.Name ?? string.Empty;

    [PHPProperty("rank")] public string ClanRank { get; init; } = account.ClanTier.ToString();

    [PHPProperty("standing")] public string Standing { get; init; } = "3";

    [PHPProperty("create_date")]
    public string CreateDate { get; init; } = account.TimestampCreated.ToString("MM/dd/yyyy");

    [PHPProperty("last_activity")]
    public string LastActivity { get; init; } = account.TimestampLastActive.ToString("MM/dd/yyyy");

    [PHPProperty("selected_upgrades")]
    public List<string> SelectedStoreItems { get; init; } = account.SelectedStoreItemCodes;

    [PHPProperty("level")] public int Level { get; init; } = account.User.TotalLevel;

    [PHPProperty("level_exp")] public int LevelExperience { get; init; } = account.User.TotalExperience;

    [PHPProperty("mastery_info")] public List<HeroMasteryInfo> MasteryInfo { get; set; } = [];

    [PHPProperty("mastery_rewards")] public List<MasteryRewardTier> MasteryRewards { get; set; } = [];

    [PHPProperty("vested_threshold")] public int VestedThreshold => 5;

    [PHPProperty(0)] public bool Zero => true;
}
