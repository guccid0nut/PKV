// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace KONGOR.MasterServer.Models.RequestResponse.Upgrades;

public partial class OwnedStoreItemInfoDTO
{
    [PHPProperty("data")] public string Data { get; init; } = string.Empty;

    [PHPProperty("start_time")] public string StartTime { get; init; } = string.Empty;

    [PHPProperty("end_time")] public string EndTime { get; init; } = string.Empty;

    [PHPProperty("used")] public int Used { get; init; } = 0;
}

public partial class AccountFieldStatsDTO
{
    [PHPProperty("account_id")] public required string AccountID { get; init; }

    [PHPProperty("level")] public int Level { get; init; } = 1;

    [PHPProperty("level_exp")] public string LevelExp { get; init; } = "0";

    [PHPProperty("acc_pub_skill")] public string PublicSkillRating { get; init; } = "1500.000";

    [PHPProperty("rnk_amm_team_rating")] public string RankedRating { get; init; } = "1500.000";

    [PHPProperty("cs_amm_team_rating")] public string CasualRating { get; init; } = "1500.000";

    [PHPProperty("acc_games_played")] public int TotalGamesPlayed { get; init; } = 0;

    [PHPProperty("acc_discos")] public int TotalDiscos { get; init; } = 0;

    [PHPProperty("rnk_games_played")] public int RankedGamesPlayed { get; init; } = 0;

    [PHPProperty("rnk_discos")] public int RankedDiscos { get; init; } = 0;

    [PHPProperty("cs_games_played")] public int CasualGamesPlayed { get; init; } = 0;

    [PHPProperty("cs_discos")] public int CasualDiscos { get; init; } = 0;

    [PHPProperty("mid_games_played")] public int MidGamesPlayed { get; init; } = 0;

    [PHPProperty("mid_discos")] public int MidDiscos { get; init; } = 0;

    [PHPProperty("rift_games_played")] public int RiftGamesPlayed { get; init; } = 0;

    [PHPProperty("rift_discos")] public int RiftDiscos { get; init; } = 0;

    [PHPProperty("cam_games_played")] public int CampaignGamesPlayed { get; init; } = 0;

    [PHPProperty("cam_discos")] public int CampaignDiscos { get; init; } = 0;

    [PHPProperty("cam_cs_games_played")] public int CampaignCasualGamesPlayed { get; init; } = 0;

    [PHPProperty("cam_cs_discos")] public int CampaignCasualDisconnections { get; init; } = 0;

    [PHPProperty("acc_trial_games_played")]
    public int TrialGamesPlayed { get; init; } = 0;

    [PHPProperty("standing")] public int Standing { get; init; } = 3;

    [PHPProperty("is_new")] public int IsNew { get; init; } = 0;
}

public partial class GetInitialStatisticsResponseDTO
{
    [PHPProperty("infos")] public required Dictionary<int, AccountFieldStatsDTO> Information { get; init; }
}

public partial class GetUpgradesResponseDTO
{
    [PHPProperty("field_stats")] public required Dictionary<int, AccountFieldStatsDTO> FieldStats { get; init; }

    [PHPProperty("my_upgrades")] public required List<string> MyUpgrades { get; init; }

    [PHPProperty("my_upgrades_info", IsDiscriminatedUnion = true)]
    public required Dictionary<string, OneOf<StoreItemData, StoreItemDiscountCoupon>> MyUpgradesInfo { get; init; }

    [PHPProperty("selected_upgrades")] public required List<string> SelectedUpgrades { get; init; }

    [PHPProperty("points")] public required int Points { get; init; }

    [PHPProperty("mmpoints")] public required int MMPoints { get; init; }

    [PHPProperty("game_tokens")] public int GameTokens { get; init; } = 999999;

    [PHPProperty("standing")] public int Standing { get; init; } = 3;

    [PHPProperty("season_level")] public int SeasonLevel { get; init; } = 100;

    [PHPProperty("creep_level")] public int CreepLevel { get; init; } = 0;

    [PHPProperty("timestamp")]
    public int Timestamp { get; init; } =
        Convert.ToInt32(Math.Min(TimeProvider.System.GetUtcNow().ToUnixTimeSeconds(), Convert.ToInt64(int.MaxValue)));

    [PHPProperty("vested_threshold")] public int VestedThreshold => 5;

    [PHPProperty(0)] public bool Zero => true;
}
