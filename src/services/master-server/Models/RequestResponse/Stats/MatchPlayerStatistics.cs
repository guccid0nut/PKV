// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using System.Globalization;
using KONGOR.MasterServer.Helpers.Stats;

namespace KONGOR.MasterServer.Models.RequestResponse.Stats;

public partial class MatchPlayerStatistics(
    MatchStartData matchStartData,
    Account account,
    PlayerStatistics playerStatistics,
    AccountStatistics currentMatchTypeStatistics,
    AccountStatistics publicMatchStatistics,
    AccountStatistics matchmakingStatistics)
{
    /// <summary>
    ///     The unique identifier for the match.
    /// </summary>
    [PHPProperty("match_id")]
    public int MatchID { get; init; } = playerStatistics.MatchID;

    /// <summary>
    ///     The player's account ID.
    /// </summary>
    [PHPProperty("account_id")]
    public int AccountID { get; init; } = playerStatistics.AccountID;

    /// <summary>
    ///     The account name (nickname) of the player.
    /// </summary>
    [PHPProperty("nickname")]
    public string AccountName { get; init; } = playerStatistics.AccountName;

    /// <summary>
    ///     The clan ID of the player's clan, or "0" if the player is not in a clan.
    /// </summary>
    [PHPProperty("clan_id")]
    public string ClanID { get; init; } = (playerStatistics.ClanID ?? 0).ToString();

    /// <summary>
    ///     The unique identifier of the hero played in the match.
    /// </summary>
    [PHPProperty("hero_id")]
    public string HeroProductID { get; init; } = (playerStatistics.HeroProductID ?? 0).ToString();

    /// <summary>
    ///     The lobby position of the player (0-9), indicating their slot in the pre-match lobby.
    /// </summary>
    [PHPProperty("position")]
    public string Position { get; init; } = playerStatistics.LobbyPosition.ToString();

    /// <summary>
    ///     The team the player was on ("1" for Legion, "2" for Hellbourne).
    /// </summary>
    [PHPProperty("team")]
    public string Team { get; init; } = playerStatistics.Team.ToString();

    /// <summary>
    ///     The final hero level reached by the player in the match (1-25).
    /// </summary>
    [PHPProperty("level")]
    public string Level { get; init; } = playerStatistics.HeroLevel.ToString();

    /// <summary>
    ///     The number of wins on the player's account.
    /// </summary>
    [PHPProperty("wins")]
    public string TotalWonMatches { get; init; } = currentMatchTypeStatistics.MatchesWon.ToString();

    /// <summary>
    ///     The number of losses on the player's account.
    /// </summary>
    [PHPProperty("losses")]
    public string TotalLostMatches { get; init; } = currentMatchTypeStatistics.MatchesLost.ToString();

    /// <summary>
    ///     The number of conceded matches on the player's account.
    /// </summary>
    [PHPProperty("concedes")]
    public string TotalConcededMatches { get; init; } = currentMatchTypeStatistics.MatchesConceded.ToString();

    /// <summary>
    ///     The number of concede votes the player cast during the match.
    /// </summary>
    [PHPProperty("concedevotes")]
    public string ConcedeVotes { get; init; } = playerStatistics.ConcedeVotes.ToString();

    /// <summary>
    ///     The number of times the player bought back into the match after dying.
    /// </summary>
    [PHPProperty("buybacks")]
    public string Buybacks { get; init; } = playerStatistics.Buybacks.ToString();

    /// <summary>
    ///     The number of disconnections on the player's account.
    /// </summary>
    [PHPProperty("discos")]
    public string TotalDisconnections { get; init; } = currentMatchTypeStatistics.MatchesDisconnected.ToString();

    /// <summary>
    ///     The number of times the player was kicked from matches on their account.
    /// </summary>
    [PHPProperty("kicked")]
    public string TotalKicks { get; init; } = currentMatchTypeStatistics.MatchesKicked.ToString();

    /// <summary>
    ///     The player's Public Skill Rating (PSR).
    /// </summary>
    [PHPProperty("pub_skill")]
    public string PublicMatchRating { get; init; } =
        publicMatchStatistics.SkillRating.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    ///     The number of public matches played on the player's account.
    /// </summary>
    [PHPProperty("pub_count")]
    public string PublicMatchCount { get; init; } = publicMatchStatistics.MatchesPlayed.ToString();

    /// <summary>
    ///     The player's solo Matchmaking Rating (MMR).
    /// </summary>
    [PHPProperty("amm_solo_rating")]
    public string SoloRankedMatchRating { get; init; } =
        matchmakingStatistics.SkillRating.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    ///     The number of solo ranked matches played on the player's account.
    /// </summary>
    [PHPProperty("amm_solo_count")]
    public string SoloRankedMatchCount { get; init; } = matchmakingStatistics.MatchesPlayed.ToString();

    /// <summary>
    ///     The player's team Matchmaking Rating (MMR).
    /// </summary>
    [PHPProperty("amm_team_rating")]
    public string TeamRankedMatchRating { get; init; } =
        matchmakingStatistics.SkillRating.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    ///     The number of team ranked matches played on the player's account.
    /// </summary>
    [PHPProperty("amm_team_count")]
    public string TeamRankedMatchCount { get; init; } = matchmakingStatistics.MatchesPlayed.ToString();

    /// <summary>
    ///     The player's performance score across all matches, calculated as (Kills + Assists) / Max(1, Deaths).
    /// </summary>
    [PHPProperty("avg_score")]
    public string PerformanceScore { get; init; } = currentMatchTypeStatistics.PerformanceScore.ToString("F2");

    /// <summary>
    ///     The number of enemy hero kills achieved by the player in the match.
    /// </summary>
    [PHPProperty("herokills")]
    public string HeroKills { get; init; } = playerStatistics.HeroKills.ToString();

    /// <summary>
    ///     The total damage dealt to enemy heroes by the player in the match.
    /// </summary>
    [PHPProperty("herodmg")]
    public string HeroDamage { get; init; } = playerStatistics.HeroDamage.ToString();

    /// <summary>
    ///     The total experience gained by the player's hero in the match.
    /// </summary>
    [PHPProperty("heroexp")]
    public string HeroExperience { get; init; } = playerStatistics.HeroExperience.ToString();

    /// <summary>
    ///     The total gold earned by the player's hero in the match.
    /// </summary>
    [PHPProperty("herokillsgold")]
    public string HeroGold { get; init; } = playerStatistics.GoldFromHeroKills.ToString();

    /// <summary>
    ///     The number of assists (participating in hero kills without landing the final blow) achieved by the player.
    /// </summary>
    [PHPProperty("heroassists")]
    public string HeroAssists { get; init; } = playerStatistics.HeroAssists.ToString();

    /// <summary>
    ///     The number of times the player died in the match.
    /// </summary>
    [PHPProperty("deaths")]
    public string Deaths { get; init; } = playerStatistics.HeroDeaths.ToString();

    /// <summary>
    ///     The total gold lost by the player due to deaths in the match.
    /// </summary>
    [PHPProperty("goldlost2death")]
    public string GoldLostToDeath { get; init; } = playerStatistics.GoldLostToDeath.ToString();

    /// <summary>
    ///     The total time in seconds the player spent dead (waiting to respawn) during the match.
    /// </summary>
    [PHPProperty("secs_dead")]
    public string SecondsDead { get; init; } = playerStatistics.SecondsDead.ToString();

    /// <summary>
    ///     The number of friendly team creeps killed by the player (last-hitting own creeps for gold/experience).
    /// </summary>
    [PHPProperty("teamcreepkills")]
    public string TeamCreepKills { get; init; } = playerStatistics.TeamCreepKills.ToString();

    /// <summary>
    ///     The total damage dealt to friendly team creeps by the player.
    /// </summary>
    [PHPProperty("teamcreepdmg")]
    public string TeamCreepDamage { get; init; } = playerStatistics.TeamCreepDamage.ToString();

    /// <summary>
    ///     The total experience gained from killing friendly team creeps.
    /// </summary>
    [PHPProperty("teamcreepexp")]
    public string TeamCreepExperience { get; init; } = playerStatistics.TeamCreepExperience.ToString();

    /// <summary>
    ///     The total gold earned from killing friendly team creeps.
    /// </summary>
    [PHPProperty("teamcreepgold")]
    public string TeamCreepGold { get; init; } = playerStatistics.TeamCreepGold.ToString();

    /// <summary>
    ///     The number of neutral creeps killed by the player (jungle creeps).
    /// </summary>
    [PHPProperty("neutralcreepkills")]
    public string NeutralCreepKills { get; init; } = playerStatistics.NeutralCreepKills.ToString();

    /// <summary>
    ///     The total damage dealt to neutral creeps by the player.
    /// </summary>
    [PHPProperty("neutralcreepdmg")]
    public string NeutralCreepDamage { get; init; } = playerStatistics.NeutralCreepDamage.ToString();

    /// <summary>
    ///     The total experience gained from killing neutral creeps.
    /// </summary>
    [PHPProperty("neutralcreepexp")]
    public string NeutralCreepExperience { get; init; } = playerStatistics.NeutralCreepExperience.ToString();

    /// <summary>
    ///     The total gold earned from killing neutral creeps.
    /// </summary>
    [PHPProperty("neutralcreepgold")]
    public string NeutralCreepGold { get; init; } = playerStatistics.NeutralCreepGold.ToString();

    /// <summary>
    ///     The total damage dealt to enemy buildings (towers, barracks, base structures) by the player.
    /// </summary>
    [PHPProperty("bdmg")]
    public string BuildingDamage { get; init; } = playerStatistics.BuildingDamage.ToString();

    /// <summary>
    ///     The total experience gained from damaging or destroying enemy buildings.
    /// </summary>
    [PHPProperty("bdmgexp")]
    public string BuildingExperience { get; init; } = playerStatistics.ExperienceFromBuildings.ToString();

    /// <summary>
    ///     The number of enemy buildings (towers, barracks) destroyed by the player.
    /// </summary>
    [PHPProperty("razed")]
    public string BuildingsRazed { get; init; } = playerStatistics.BuildingsRazed.ToString();

    /// <summary>
    ///     The total gold earned from damaging or destroying enemy buildings.
    /// </summary>
    [PHPProperty("bgold")]
    public string BuildingGold { get; init; } = playerStatistics.GoldFromBuildings.ToString();

    /// <summary>
    ///     The number of friendly creeps denied by the player (last-hitting friendly creeps to prevent opponents from gaining
    ///     gold/experience).
    /// </summary>
    [PHPProperty("denies")]
    public string Denies { get; init; } = playerStatistics.Denies.ToString();

    /// <summary>
    ///     The total experience denied to opponents through denying friendly creeps.
    /// </summary>
    [PHPProperty("exp_denied")]
    public string ExperienceDenied { get; init; } = playerStatistics.ExperienceDenied.ToString();

    /// <summary>
    ///     The total gold accumulated by the player at the end of the match.
    /// </summary>
    [PHPProperty("gold")]
    public string Gold { get; init; } = playerStatistics.Gold.ToString();

    /// <summary>
    ///     The total gold spent by the player on items during the match.
    /// </summary>
    [PHPProperty("gold_spent")]
    public string GoldSpent { get; init; } = playerStatistics.GoldSpent.ToString();

    /// <summary>
    ///     The total experience gained by the player during the match.
    /// </summary>
    [PHPProperty("exp")]
    public string Experience { get; init; } = playerStatistics.Experience.ToString();

    /// <summary>
    ///     The total number of actions performed by the player during the match (clicks, commands, ability usage, etc.).
    /// </summary>
    [PHPProperty("actions")]
    public string Actions { get; init; } = playerStatistics.Actions.ToString();

    /// <summary>
    ///     The total time in seconds the player was actively playing in the match.
    /// </summary>
    [PHPProperty("secs")]
    public string Seconds { get; init; } = playerStatistics.SecondsPlayed.ToString();

    /// <summary>
    ///     The number of consumable items (potions, wards, teleport scrolls, etc.) purchased by the player.
    /// </summary>
    [PHPProperty("consumables")]
    public string Consumables { get; init; } = playerStatistics.ConsumablesPurchased.ToString();

    /// <summary>
    ///     The number of observer or sentry wards placed by the player during the match.
    /// </summary>
    [PHPProperty("wards")]
    public string Wards { get; init; } = playerStatistics.WardsPlaced.ToString();

    /// <summary>
    ///     The total time in seconds the player spent within experience range of dying enemy units.
    /// </summary>
    [PHPProperty("time_earning_exp")]
    public string TimeEarningExperience { get; init; } = playerStatistics.TimeEarningExperience.ToString();

    /// <summary>
    ///     The number of First Blood awards earned by the player (1 or 0).
    /// </summary>
    [PHPProperty("bloodlust")]
    public string FirstBlood { get; init; } = playerStatistics.FirstBlood.ToString();

    /// <summary>
    ///     The number of Double Kill awards earned by the player (killing 2 heroes in quick succession).
    /// </summary>
    [PHPProperty("doublekill")]
    public string DoubleKill { get; init; } = playerStatistics.DoubleKill.ToString();

    /// <summary>
    ///     The number of Triple Kill awards earned by the player (killing 3 heroes in quick succession).
    /// </summary>
    [PHPProperty("triplekill")]
    public string TripleKill { get; init; } = playerStatistics.TripleKill.ToString();

    /// <summary>
    ///     The number of Quad Kill awards earned by the player (killing 4 heroes in quick succession).
    /// </summary>
    [PHPProperty("quadkill")]
    public string QuadKill { get; init; } = playerStatistics.QuadKill.ToString();

    /// <summary>
    ///     The number of Annihilation awards earned by the player (killing all 5 enemy heroes in quick succession).
    /// </summary>
    [PHPProperty("annihilation")]
    public string Annihilation { get; init; } = playerStatistics.Annihilation.ToString();

    /// <summary>
    ///     The number of 3-kill streaks achieved by the player (killing 3 heroes without dying).
    /// </summary>
    [PHPProperty("ks3")]
    public string KillStreak3 { get; init; } = playerStatistics.KillStreak03.ToString();

    /// <summary>
    ///     The number of 4-kill streaks achieved by the player (killing 4 heroes without dying).
    /// </summary>
    [PHPProperty("ks4")]
    public string KillStreak4 { get; init; } = playerStatistics.KillStreak04.ToString();

    /// <summary>
    ///     The number of 5-kill streaks achieved by the player (killing 5 heroes without dying).
    /// </summary>
    [PHPProperty("ks5")]
    public string KillStreak5 { get; init; } = playerStatistics.KillStreak05.ToString();

    /// <summary>
    ///     The number of 6-kill streaks achieved by the player (killing 6 heroes without dying).
    /// </summary>
    [PHPProperty("ks6")]
    public string KillStreak6 { get; init; } = playerStatistics.KillStreak06.ToString();

    /// <summary>
    ///     The number of 7-kill streaks achieved by the player (killing 7 heroes without dying).
    /// </summary>
    [PHPProperty("ks7")]
    public string KillStreak7 { get; init; } = playerStatistics.KillStreak07.ToString();

    /// <summary>
    ///     The number of 8-kill streaks achieved by the player (killing 8 heroes without dying).
    /// </summary>
    [PHPProperty("ks8")]
    public string KillStreak8 { get; init; } = playerStatistics.KillStreak08.ToString();

    /// <summary>
    ///     The number of 9-kill streaks achieved by the player (killing 9 heroes without dying).
    /// </summary>
    [PHPProperty("ks9")]
    public string KillStreak9 { get; init; } = playerStatistics.KillStreak09.ToString();

    /// <summary>
    ///     The number of 10-kill streaks achieved by the player (killing 10 heroes without dying).
    /// </summary>
    [PHPProperty("ks10")]
    public string KillStreak10 { get; init; } = playerStatistics.KillStreak10.ToString();

    /// <summary>
    ///     The number of 15-kill streaks achieved by the player (killing 15 heroes without dying).
    /// </summary>
    [PHPProperty("ks15")]
    public string KillStreak15 { get; init; } = playerStatistics.KillStreak15.ToString();

    /// <summary>
    ///     The number of Smackdown awards earned by the player (killing a player after taunting them).
    /// </summary>
    [PHPProperty("smackdown")]
    public string Smackdown { get; init; } = playerStatistics.Smackdown.ToString();

    /// <summary>
    ///     The number of Humiliation awards earned by the player (getting killed by a player after taunting them).
    /// </summary>
    [PHPProperty("humiliation")]
    public string Humiliation { get; init; } = playerStatistics.Humiliation.ToString();

    /// <summary>
    ///     The number of Nemesis awards earned by the player (repeatedly killing the same enemy hero).
    /// </summary>
    [PHPProperty("nemesis")]
    public string Nemesis { get; init; } = playerStatistics.Nemesis.ToString();

    /// <summary>
    ///     The number of Retribution awards earned by the player (killing an enemy hero who has killed you repeatedly).
    /// </summary>
    [PHPProperty("retribution")]
    public string Retribution { get; init; } = playerStatistics.Retribution.ToString();

    /// <summary>
    ///     Whether the player used a token (game access token or dice token) during the match ("1" if used, "0" otherwise).
    /// </summary>
    [PHPProperty("used_token")]
    public string UsedToken { get; init; } = playerStatistics.UsedToken.ToString();

    /// <summary>
    ///     The hero identifier in the format Hero_{Snake_Case_Name} (e.g. "Hero_Andromeda", "Hero_Legionnaire").
    /// </summary>
    [PHPProperty("cli_name")]
    public required string HeroIdentifier { get; init; }

    /// <summary>
    ///     The clan tag of the player's clan, or empty string if the player is not in a clan.
    /// </summary>
    [PHPProperty("tag")]
    public string ClanTag { get; init; } = account.Clan?.Tag ?? string.Empty;

    /// <summary>
    ///     The alternative avatar name used by the player during the match, or empty string if using the default hero skin.
    /// </summary>
    [PHPProperty("alt_avatar_name")]
    public string AlternativeAvatarName { get; init; } = playerStatistics.AlternativeAvatarName ?? string.Empty;

    /// <summary>
    ///     Seasonal campaign progression information for the player in the match.
    /// </summary>
    [PHPProperty("campaign_info")]
    public SeasonProgress SeasonProgress { get; init; } = new(matchStartData, playerStatistics, matchmakingStatistics);

    /// <summary>
    ///     Selected upgrades string for custom account icon, chat symbol, and name color.
    /// </summary>
    [PHPProperty("selected_upgrades")]
    public string SelectedUpgrades { get; init; } = string.Join("|", account.SelectedStoreItemCodes);

    /// <summary>
    ///     Experience points (Diamonds / Account XP) earned based on match outcome.
    /// </summary>
    [PHPProperty("perf_victory_exp")]
    public string MatchPerformanceVictoryExperience { get; init; } = "25";

    /// <summary>
    ///     Silver coins earned based on match outcome.
    /// </summary>
    [PHPProperty("perf_victory_gc")]
    public string MatchPerformanceVictoryGoldCoins { get; init; } = "25";

    /// <summary>
    ///     Game a day bonus experience (Diamonds / Account XP).
    /// </summary>
    [PHPProperty("perf_first_exp")]
    public string MatchPerformanceFirstMatchExperience { get; init; } = "25";

    /// <summary>
    ///     Game a day bonus silver coins.
    /// </summary>
    [PHPProperty("perf_first_gc")]
    public string MatchPerformanceFirstMatchGoldCoins { get; init; } = "25";

    /// <summary>
    ///     Social bonus silver coins for queuing with friends.
    /// </summary>
    [PHPProperty("perf_social_bonus_gc")]
    public string MatchPerformanceSocialBonusGoldCoins { get; init; } = "25";

    /// <summary>
    ///     Social bonus experience.
    /// </summary>
    [PHPProperty("perf_social_bonus_exp")]
    public string MatchPerformanceSocialBonusExperience { get; init; } = "25";

    /// <summary>
    ///     Consecutive matches played without a break.
    /// </summary>
    [PHPProperty("perf_consec_played")]
    public string MatchPerformanceConsecutiveMatchesPlayed { get; init; } = "2";

    /// <summary>
    ///     Consecutive match bonus experience.
    /// </summary>
    [PHPProperty("perf_consec_exp")]
    public string MatchPerformanceConsecutiveMatchExperience { get; init; } = "25";

    /// <summary>
    ///     Consecutive match bonus silver coins.
    /// </summary>
    [PHPProperty("perf_consec_gc")]
    public string MatchPerformanceConsecutiveMatchGoldCoins { get; init; } = "25";

    [PHPProperty("perf_annihilation_exp")] public string MatchPerformanceAnnihilationExperience { get; init; } = "0";

    [PHPProperty("perf_annihilation_gc")] public string MatchPerformanceAnnihilationGoldCoins { get; init; } = "0";

    [PHPProperty("perf_bloodlust_exp")] public string MatchPerformanceBloodlustExperience { get; init; } = "0";

    [PHPProperty("perf_bloodlust_gc")] public string MatchPerformanceBloodlustGoldCoins { get; init; } = "0";

    [PHPProperty("perf_ks15_exp")] public string MatchPerformanceKillStreak15Experience { get; init; } = "0";

    [PHPProperty("perf_ks15_gc")] public string MatchPerformanceKillStreak15GoldCoins { get; init; } = "0";

    [PHPProperty("perf_wins")]
    public string MatchPerformanceTotalWins { get; init; } = currentMatchTypeStatistics.MatchesWon.ToString();

    [PHPProperty("perf_wins_delta")]
    public string MatchPerformanceWinsDelta { get; init; } = playerStatistics.Win.ToString();

    [PHPProperty("perf_wins_gc")] public string MatchPerformanceWinsGoldCoins { get; init; } = "0";

    [PHPProperty("perf_herokills")]
    public string MatchPerformanceTotalHeroKills { get; init; } = playerStatistics.HeroKills.ToString();

    [PHPProperty("perf_herokills_delta")]
    public string MatchPerformanceHeroKillsDelta { get; init; } = playerStatistics.HeroKills.ToString();

    [PHPProperty("perf_herokills_gc")] public string MatchPerformanceHeroKillsGoldCoins { get; init; } = "0";

    [PHPProperty("perf_heroassists")]
    public string MatchPerformanceTotalHeroAssists { get; init; } = playerStatistics.HeroAssists.ToString();

    [PHPProperty("perf_heroassists_delta")]
    public string MatchPerformanceHeroAssistsDelta { get; init; } = playerStatistics.HeroAssists.ToString();

    [PHPProperty("perf_heroassists_gc")] public string MatchPerformanceHeroAssistsGoldCoins { get; init; } = "0";

    [PHPProperty("perf_wards")]
    public string MatchPerformanceTotalWards { get; init; } = playerStatistics.WardsPlaced.ToString();

    [PHPProperty("perf_wards_delta")]
    public string MatchPerformanceWardsDelta { get; init; } = playerStatistics.WardsPlaced.ToString();

    [PHPProperty("perf_wards_gc")] public string MatchPerformanceWardsGoldCoins { get; init; } = "0";

    [PHPProperty("perf_smackdown")]
    public string MatchPerformanceTotalSmackdowns { get; init; } = playerStatistics.Smackdown.ToString();

    [PHPProperty("perf_smackdown_delta")]
    public string MatchPerformanceSmackdownDelta { get; init; } = playerStatistics.Smackdown.ToString();

    [PHPProperty("perf_smackdown_gc")] public string MatchPerformanceSmackdownGoldCoins { get; init; } = "0";

    [PHPProperty("perf_level_gc")] public string MatchPerformanceLevelGoldCoins { get; init; } = "0";

    [PHPProperty("levelup_coins")] public string LevelUpCoins { get; init; } = "0";

    [PHPProperty("legacy_bonus_gc")] public string LegacyBonusGoldCoins { get; init; } = "0";

    [PHPProperty("limited_stats_coins")] public string LimitedStatsCoins { get; init; } = "0";

    [PHPProperty("perf_bots_gc")] public string MatchPerformanceBotsGoldCoins { get; init; } = "0";

    [PHPProperty("perf_multiplier_mmpoints")]
    public string MatchPerformanceMultiplierMMPoints { get; init; } = "0";

    [PHPProperty("cur_diamond")] public string CurDiamond { get; init; } = "25";

    [PHPProperty("season_diamond")] public string SeasonDiamond { get; init; } = "25";

    [PHPProperty("mastery_exp_original")] public int MasteryExperienceOriginal { get; init; } = 500;

    [PHPProperty("mastery_exp_match")] public int MasteryExperienceMatch { get; init; } = 100;

    [PHPProperty("mastery_exp_bonus")] public int MasteryExperienceBonus { get; init; } = 20;

    [PHPProperty("mastery_exp_boost")] public int MasteryExperienceBoost { get; init; } = 0;

    [PHPProperty("mastery_exp_to_boost")] public int MasteryExperienceToBoost { get; init; } = 240;

    [PHPProperty("mastery_canboost")]
    public string MasteryCanBoost { get; set; } =
        !MasteryConsumables.HasClaimedMatchBoost(account?.User, playerStatistics.MatchID) &&
        MasteryConsumables.GetTotalMasteryBoostsOwned(account?.User, isSuper: false) > 0
            ? "1"
            : "0";

    [PHPProperty("mastery_super_canboost")]
    public string MasterySuperCanBoost { get; set; } =
        !MasteryConsumables.HasClaimedMatchBoost(account?.User, playerStatistics.MatchID) &&
        MasteryConsumables.GetTotalMasteryBoostsOwned(account?.User, isSuper: true) > 0
            ? "1"
            : "0";

    [PHPProperty("mastery_boostnum")]
    public int MasteryBoostNum { get; set; } =
        MasteryConsumables.GetTotalMasteryBoostsOwned(account?.User, isSuper: false);

    [PHPProperty("mastery_super_boostnum")]
    public int MasterySuperBoostNum { get; set; } =
        MasteryConsumables.GetTotalMasteryBoostsOwned(account?.User, isSuper: true);

    internal MatchPlayerStatistics CloneWithMasteryBoostState(Account? latestAccount, int matchId)
    {
        MatchPlayerStatistics clone = (MatchPlayerStatistics)MemberwiseClone();
        bool matchClaimed = MasteryConsumables.HasClaimedMatchBoost(latestAccount?.User, matchId);
        int normalOwned = MasteryConsumables.GetTotalMasteryBoostsOwned(latestAccount?.User, isSuper: false);
        int superOwned = MasteryConsumables.GetTotalMasteryBoostsOwned(latestAccount?.User, isSuper: true);
        clone.MasteryBoostNum = normalOwned;
        clone.MasterySuperBoostNum = superOwned;
        clone.MasteryCanBoost = !matchClaimed && normalOwned > 0 ? "1" : "0";
        clone.MasterySuperCanBoost = !matchClaimed && superOwned > 0 ? "1" : "0";
        return clone;
    }

    [PHPProperty("mastery_exp_super_boost")]
    public int MasteryExperienceSuperBoost { get; init; } = 0;

    [PHPProperty("mastery_matchid")] public int MasteryMatchID { get; init; } = playerStatistics.MatchID;

    [PHPProperty("mastery_boost_product_id")]
    public int MasteryBoostProductID { get; init; } = 3609;

    [PHPProperty("mastery_boost_gold_coin")]
    public int MasteryBoostGoldCoin { get; init; } = 40;

    [PHPProperty("mastery_boost_silver_coin")]
    public int MasteryBoostSilverCoin { get; init; } = 200;

    [PHPProperty("mastery_maxlevel_herocount")]
    public int MasteryMaxLevelHeroCount { get; init; } = 0;

    [PHPProperty("mastery_maxlevel_addon")]
    public int MasteryMaxLevelAddon { get; init; } = 20;

    [PHPProperty("mastery_exp_event")] public int MasteryExperienceEventBonus { get; init; } = 0;

    /// <summary>
    ///     Custom gameplay telemetry slot 0 (e.g., Strikers Goals, DevoWars Hook Attempts, Enfo's Wave Reached).
    /// </summary>
    [PHPProperty("gameplaystat0")]
    public string GameplayStat0 { get; init; } = playerStatistics.GameplayStat0.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    ///     Custom gameplay telemetry slot 1 (e.g., Strikers Goal Assists, DevoWars Hook Hits, Enfo's Difficulty).
    /// </summary>
    [PHPProperty("gameplaystat1")]
    public string GameplayStat1 { get; init; } = playerStatistics.GameplayStat1.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    ///     Custom gameplay telemetry slot 2 (e.g., Strikers Own Goals, CTF Flag Captures, DevoWars Headshots).
    /// </summary>
    [PHPProperty("gameplaystat2")]
    public string GameplayStat2 { get; init; } = playerStatistics.GameplayStat2.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    ///     Custom gameplay telemetry slot 3 (e.g., Strikers Steals, CTF Flag Returns, Enfo's Lives Left).
    /// </summary>
    [PHPProperty("gameplaystat3")]
    public string GameplayStat3 { get; init; } = playerStatistics.GameplayStat3.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    ///     Custom gameplay telemetry slot 4 (e.g., Strikers Possession Seconds, CTF Flag Carrier Kills).
    /// </summary>
    [PHPProperty("gameplaystat4")]
    public string GameplayStat4 { get; init; } = playerStatistics.GameplayStat4.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    ///     Custom gameplay telemetry slot 5 (e.g., Strikers Knocked Out, CTF Distance Run With Flag).
    /// </summary>
    [PHPProperty("gameplaystat5")]
    public string GameplayStat5 { get; init; } = playerStatistics.GameplayStat5.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    ///     Custom gameplay telemetry slot 6 (e.g., Strikers Knockouts, CTF Damage to Flag Carriers).
    /// </summary>
    [PHPProperty("gameplaystat6")]
    public string GameplayStat6 { get; init; } = playerStatistics.GameplayStat6.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    ///     Custom gameplay telemetry slot 7 (e.g., Strikers Runes Gathered).
    /// </summary>
    [PHPProperty("gameplaystat7")]
    public string GameplayStat7 { get; init; } = playerStatistics.GameplayStat7.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    ///     Custom gameplay telemetry slot 8.
    /// </summary>
    [PHPProperty("gameplaystat8")]
    public string GameplayStat8 { get; init; } = playerStatistics.GameplayStat8.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    ///     Custom gameplay telemetry slot 9.
    /// </summary>
    [PHPProperty("gameplaystat9")]
    public string GameplayStat9 { get; init; } = playerStatistics.GameplayStat9.ToString(CultureInfo.InvariantCulture);
}
