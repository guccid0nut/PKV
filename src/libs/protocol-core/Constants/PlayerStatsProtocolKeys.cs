// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace ASPIRE.Common.Constants;

/// <summary>
///     Defines canonical protocol dictionary keys and descriptive names for player_stats payload fields.
/// </summary>
public static class PlayerStatsProtocolKeys
{
    public const string HeroId = "hero_id";
    public const string Nickname = "nickname";
    public const string Team = "team";
    public const string Position = "position";
    public const string GroupNumber = "group_num";
    public const string Benefit = "benefit";
    public const string Wins = "wins";
    public const string Losses = "losses";
    public const string Disconnects = "discos";
    public const string Concedes = "concedes";
    public const string Kicked = "kicked";
    public const string SocialBonus = "social_bonus";
    public const string UsedToken = "used_token";
    public const string ConcedeVotes = "concedevotes";
    public const string HeroKills = "herokills";
    public const string HeroDamage = "herodmg";
    public const string GoldFromHeroKills = "herokillsgold";
    public const string HeroAssists = "heroassists";
    public const string HeroExperience = "heroexp";
    public const string Deaths = "deaths";
    public const string Buybacks = "buybacks";
    public const string GoldLostToDeath = "goldlost2death";
    public const string SecondsDead = "secs_dead";
    public const string TeamCreepKills = "teamcreepkills";
    public const string TeamCreepDamage = "teamcreepdmg";
    public const string TeamCreepGold = "teamcreepgold";
    public const string TeamCreepExperience = "teamcreepexp";
    public const string NeutralCreepKills = "neutralcreepkills";
    public const string NeutralCreepDamage = "neutralcreepdmg";
    public const string NeutralCreepGold = "neutralcreepgold";
    public const string NeutralCreepExperience = "neutralcreepexp";
    public const string BuildingDamage = "bdmg";
    public const string BuildingsRazed = "razed";
    public const string ExperienceFromBuildings = "bdmgexp";
    public const string GoldFromBuildings = "bgold";
    public const string Denies = "denies";
    public const string ExperienceDenied = "exp_denied";
    public const string Gold = "gold";
    public const string GoldSpent = "gold_spent";
    public const string Experience = "exp";
    public const string Actions = "actions";
    public const string SecondsPlayed = "secs";
    public const string HeroLevel = "level";
    public const string Consumables = "consumables";
    public const string Wards = "wards";
    public const string FirstBlood = "bloodlust";
    public const string DoubleKill = "doublekill";
    public const string TripleKill = "triplekill";
    public const string QuadKill = "quadkill";
    public const string Annihilation = "annihilation";
    public const string KillStreak03 = "ks3";
    public const string KillStreak04 = "ks4";
    public const string KillStreak05 = "ks5";
    public const string KillStreak06 = "ks6";
    public const string KillStreak07 = "ks7";
    public const string KillStreak08 = "ks8";
    public const string KillStreak09 = "ks9";
    public const string KillStreak10 = "ks10";
    public const string KillStreak15 = "ks15";
    public const string Smackdown = "smackdown";
    public const string Humiliation = "humiliation";
    public const string Nemesis = "nemesis";
    public const string Retribution = "retribution";
    public const string Score = "score";
    public const string TimeEarningExperience = "time_earning_exp";

    // Cosmetics
    public const string AlternativeAvatarName = "alt_avatar_name";
    public const string AlternativeAvatarProductId = "alt_avatar_pid";
    public const string WardName = "ward_name";
    public const string WardProductId = "ward_pid";
    public const string TauntName = "taunt_name";
    public const string TauntProductId = "taunt_pid";
    public const string AnnouncerName = "announcer_name";
    public const string AnnouncerProductId = "announcer_pid";
    public const string CourierName = "courier_name";
    public const string CourierProductId = "courier_pid";
    public const string AccountIconName = "account_icon_name";
    public const string AccountIconProductId = "account_icon_pid";
    public const string ChatColorName = "chat_color_name";
    public const string ChatColorProductId = "chat_color_pid";

    // Public / Ranked specifics
    public const string PublicMatchCount = "pub_count";
    public const string PublicSkillRating = "pub_skill";
    public const string RankedMatchCount = "amm_team_count";
    public const string RankedSkillRating = "amm_team_rating";
    public const string MostValuablePlayer = "mvp";
}
