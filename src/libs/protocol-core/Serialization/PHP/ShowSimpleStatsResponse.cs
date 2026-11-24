// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using System.Diagnostics.CodeAnalysis;
using OneOf;

namespace ASPIRE.Common.Serialization.PHP;

[SuppressMessage("ReSharper", "InconsistentNaming")]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores")]
public class ShowSimpleStatsResponse : IPhpSerializable
{
    /// <summary>
    ///     The name and clan tag of the account.
    /// </summary>
    [PHPProperty("nickname")]
    public required string NameWithClanTag { get; set; }

    /// <summary>
    ///     The ID of the account.
    /// </summary>
    [PHPProperty("account_id")]
    public required OneOf<int, string> ID { get; set; }

    /// <summary>
    ///     The level of the account.
    /// </summary>
    [PHPProperty("level")]
    public required int Level { get; set; }

    /// <summary>
    ///     The total experience of the account.
    /// </summary>
    [PHPProperty("level_exp")]
    public required int LevelExperience { get; set; }

    /// <summary>
    ///     The total number of avatars that the account owns.
    /// </summary>
    [PHPProperty("avatar_num")]
    public required int NumberOfAvatarsOwned { get; set; }

    /// <summary>
    ///     The total number of heroes that the account owns.
    ///     There are currently 139 total heroes.
    /// </summary>
    [PHPProperty("hero_num")]
    public int NumberOfHeroesOwned { get; set; } = 139;

    /// <summary>
    ///     The total number of matches that the account has played.
    /// </summary>
    [PHPProperty("total_played")]
    public required int TotalMatchesPlayed { get; set; }

    /// <summary>
    ///     Legacy field for "total_games_played" expected by some client scripts (e.g., player_stats_v2.lua).
    ///     This duplicates "total_played" to ensure compatibility.
    /// </summary>
    [PHPProperty("total_games_played")]
    public required int TotalGamesPlayedLegacy { get; set; }

    [PHPProperty("create_date")] public required string AccountCreationDate { get; set; }

    [PHPProperty("last_activity")] public required string LastActivityDate { get; set; }

    [PHPProperty("total_discos")] public required int TotalDisconnects { get; set; }

    [PHPProperty("cur_games_played")] public int CurrentGamesPlayed { get; set; }

    [PHPProperty("cur_discos")] public int CurrentDisconnects { get; set; }

    [PHPProperty("cam_wins")] public required int RankedWins { get; set; }

    [PHPProperty("cam_losses")] public required int RankedLosses { get; set; }

    // Favorite Heroes
    [PHPProperty("favHero1")] public required string FavHero1 { get; set; }

    [PHPProperty("favHero1Time")] public required int FavHero1Time { get; set; }

    [PHPProperty("favHero2")] public required string FavHero2 { get; set; }

    [PHPProperty("favHero2Time")] public required int FavHero2Time { get; set; }

    [PHPProperty("favHero3")] public required string FavHero3 { get; set; }

    [PHPProperty("favHero3Time")] public required int FavHero3Time { get; set; }

    [PHPProperty("favHero4")] public required string FavHero4 { get; set; }

    [PHPProperty("favHero4Time")] public required int FavHero4Time { get; set; }

    [PHPProperty("favHero5")] public required string FavHero5 { get; set; }

    [PHPProperty("favHero5Time")] public required int FavHero5Time { get; set; }

    // Full Identifiers for Display Name (e.g. "Hero_Jereziah")

    [PHPProperty("favHero1_2")] public required string FavHero1_2 { get; set; }

    [PHPProperty("favHero2_2")] public required string FavHero2_2 { get; set; }

    [PHPProperty("favHero3_2")] public required string FavHero3_2 { get; set; }

    [PHPProperty("favHero4_2")] public required string FavHero4_2 { get; set; }

    [PHPProperty("favHero5_2")] public required string FavHero5_2 { get; set; }

    /// <summary>
    ///     The current season.
    ///     The last season before the services went offline was 12.
    /// </summary>
    [PHPProperty("season_id")]
    public required int CurrentSeason { get; set; }

    /// <summary>
    ///     Controls the visual appearance of tournament/seasonal buildings (towers, barracks, etc.) in matches.
    /// </summary>
    [PHPProperty("season_level")]
    public int SeasonLevel { get; set; } = 100;

    /// <summary>
    ///     Unused.
    /// </summary>
    [PHPProperty("creep_level")]
    public int CreepLevel { get; set; } = 100;

    /// <summary>
    ///     Simple current season statistics.
    /// </summary>
    [PHPProperty("season_normal")]
    public required SimpleSeasonStats SimpleSeasonStats { get; set; }

    /// <summary>
    ///     Simple current casual season statistics.
    /// </summary>
    [PHPProperty("season_casual")]
    public required SimpleSeasonStats SimpleCasualSeasonStats { get; set; }

    /// <summary>
    ///     Simple current midwars season statistics.
    /// </summary>
    [PHPProperty("season_midwars")]
    public required SimpleSeasonStats SimpleMidWarsSeasonStats { get; set; }

    /// <summary>
    ///     The total number of MVP awards of the account.
    /// </summary>
    [PHPProperty("mvp_num")]
    public required string MVPAwardsCount { get; set; }

    /// <summary>
    ///     The names of the account's top 4 awards.
    /// </summary>
    [PHPProperty("award_top4_name")]
    public required List<string> Top4AwardNames { get; set; }

    /// <summary>
    ///     The counts of the account's top 4 awards.
    /// </summary>
    [PHPProperty("award_top4_num")]
    public required List<string> Top4AwardCounts { get; set; }

    /// <summary>
    ///     The index of the custom icon equipped, or "0" if no custom icon is equipped.
    /// </summary>
    [PHPProperty("slot_id")]
    public required string CustomIconSlotID { get; set; }

    /// <summary>
    ///     The collection of owned store items.
    /// </summary>
    [PHPProperty("my_upgrades")]
    public required List<string> OwnedStoreItems { get; set; }

    /// <summary>
    ///     The collection of selected store items.
    /// </summary>
    [PHPProperty("selected_upgrades")]
    public required List<string> SelectedStoreItems { get; set; }

    /// <summary>
    ///     Metadata attached to each of the account's owned store items.
    /// </summary>
    [PHPProperty("my_upgrades_info")]
    public required Dictionary<string, OwnedStoreItemDTO> OwnedStoreItemsData { get; set; }

    /// <summary>
    ///     Tokens for the Kros Dice random ability draft.
    /// </summary>
    [PHPProperty("dice_tokens")]
    public string DiceTokens { get; set; } = "1";

    /// <summary>
    ///     Tokens which grant temporary access to game modes.
    /// </summary>
    [PHPProperty("game_tokens")]
    public int GameTokens { get; set; }

    /// <summary>
    ///     The server time (in UTC seconds).
    /// </summary>
    [PHPProperty("timestamp")]
    public long ServerTimestamp { get; set; } = TimeProvider.System.GetUtcNow().ToUnixTimeSeconds();

    /// <summary>
    ///     The minimum number of matches a free-to-play account must complete to become verified.
    /// </summary>
    [PHPProperty("vested_threshold")]
    public static OneOf<int, string> VestedThreshold => 5;

    /// <summary>
    ///     Success indicator flag.
    /// </summary>
    [PHPProperty(0)]
    public static bool Zero => true;

    // ==================================================================================
    // TOP LEVEL RANK FIELDS (Required for Profile UI)
    // ==================================================================================

    /// <summary>Legacy rank name (e.g. "Diamond 1") or ID.</summary>
    [PHPProperty("rank")]
    public string? Rank { get; set; }

    /// <summary>Current Badge ID (0-21) as string.</summary>
    [PHPProperty("current_level")]
    public string? CurrentRankTop { get; set; }

    /// <summary>Matchmaking Rating (MMR) as string.</summary>
    [PHPProperty("smr")]
    public string? RankedRatingTop { get; set; }

    /// <summary>Highest Badge ID achieved as string.</summary>
    [PHPProperty("highest_level_current")]
    public string? HighestRankTop { get; set; }

    [PHPProperty("con_reward")] public string? ConReward { get; set; }

    // ==================================================================================
    // TOP LEVEL STATISTICS FIELDS (Required for Profile UI -> Statistics Tab)
    // ==================================================================================

    [PHPProperty("herokills")] public string? HeroKills { get; set; }

    [PHPProperty("deaths")] public string? Deaths { get; set; }

    [PHPProperty("heroassists")] public string? HeroAssists { get; set; }

    [PHPProperty("k_d_a")] public string? KDA { get; set; }

    [PHPProperty("avgGameLength")] public string? AvgGameLength { get; set; }

    [PHPProperty("avgXP_min")] public string? AvgXPMin { get; set; }

    [PHPProperty("avgDenies")] public string? AvgDenies { get; set; }

    [PHPProperty("avgCreepKills")] public string? AvgCreepKills { get; set; }

    [PHPProperty("avgNeutralKills")] public string? AvgNeutralKills { get; set; }

    [PHPProperty("avgActions_min")] public string? AvgActionsMin { get; set; }

    [PHPProperty("avgWardsUsed")] public string? AvgWardsUsed { get; set; }

    [PHPProperty("gold")] public string? Gold { get; set; }
 
    [PHPProperty("secs")] public string? Secs { get; set; }

    [PHPProperty("exp")] public string? Exp { get; set; }

    // Smackdowns Panel
    [PHPProperty("humiliation")] public string? Humiliation { get; set; }

    [PHPProperty("smackdown")] public string? Smackdown { get; set; }

    [PHPProperty("nemesis")] public string? Nemesis { get; set; }

    [PHPProperty("retribution")] public string? Retribution { get; set; }

    // ==================================================================================
    // MASTERY FIELDS
    // ==================================================================================

    [PHPProperty("mastery_info")] public object? MasteryInfo { get; set; }

    [PHPProperty("mastery_rewards")] public object? MasteryRewards { get; set; }

    /// <summary>
    ///     Serializes all player stats directly into PHP wire format with zero reflection and zero intermediate dictionary allocation.
    /// </summary>
    public void SerializePhp(ref Utf8PhpWriter writer)
    {
        writer.WriteArrayHeader(71);

        writer.WriteKey("nickname");
        writer.WriteString(NameWithClanTag ?? string.Empty);

        writer.WriteKey("account_id");
        if (ID.IsT0)
            writer.WriteInt(ID.AsT0);
        else
            writer.WriteString(ID.AsT1 ?? string.Empty);

        writer.WriteKey("level");
        writer.WriteInt(Level);

        writer.WriteKey("level_exp");
        writer.WriteInt(LevelExperience);

        writer.WriteKey("avatar_num");
        writer.WriteInt(NumberOfAvatarsOwned);

        writer.WriteKey("hero_num");
        writer.WriteInt(NumberOfHeroesOwned);

        writer.WriteKey("total_played");
        writer.WriteInt(TotalMatchesPlayed);

        writer.WriteKey("total_games_played");
        writer.WriteInt(TotalGamesPlayedLegacy);

        writer.WriteKey("create_date");
        writer.WriteString(AccountCreationDate ?? string.Empty);

        writer.WriteKey("last_activity");
        writer.WriteString(LastActivityDate ?? string.Empty);

        writer.WriteKey("total_discos");
        writer.WriteInt(TotalDisconnects);

        writer.WriteKey("cur_games_played");
        writer.WriteInt(CurrentGamesPlayed);

        writer.WriteKey("cur_discos");
        writer.WriteInt(CurrentDisconnects);

        writer.WriteKey("cam_wins");
        writer.WriteInt(RankedWins);

        writer.WriteKey("cam_losses");
        writer.WriteInt(RankedLosses);

        writer.WriteKey("favHero1");
        writer.WriteString(FavHero1 ?? string.Empty);

        writer.WriteKey("favHero1Time");
        writer.WriteInt(FavHero1Time);

        writer.WriteKey("favHero2");
        writer.WriteString(FavHero2 ?? string.Empty);

        writer.WriteKey("favHero2Time");
        writer.WriteInt(FavHero2Time);

        writer.WriteKey("favHero3");
        writer.WriteString(FavHero3 ?? string.Empty);

        writer.WriteKey("favHero3Time");
        writer.WriteInt(FavHero3Time);

        writer.WriteKey("favHero4");
        writer.WriteString(FavHero4 ?? string.Empty);

        writer.WriteKey("favHero4Time");
        writer.WriteInt(FavHero4Time);

        writer.WriteKey("favHero5");
        writer.WriteString(FavHero5 ?? string.Empty);

        writer.WriteKey("favHero5Time");
        writer.WriteInt(FavHero5Time);

        writer.WriteKey("favHero1_2");
        writer.WriteString(FavHero1_2 ?? string.Empty);

        writer.WriteKey("favHero2_2");
        writer.WriteString(FavHero2_2 ?? string.Empty);

        writer.WriteKey("favHero3_2");
        writer.WriteString(FavHero3_2 ?? string.Empty);

        writer.WriteKey("favHero4_2");
        writer.WriteString(FavHero4_2 ?? string.Empty);

        writer.WriteKey("favHero5_2");
        writer.WriteString(FavHero5_2 ?? string.Empty);

        writer.WriteKey("season_id");
        writer.WriteInt(CurrentSeason);

        writer.WriteKey("season_level");
        writer.WriteInt(SeasonLevel);

        writer.WriteKey("creep_level");
        writer.WriteInt(CreepLevel);

        writer.WriteKey("season_normal");
        if (SimpleSeasonStats is not null)
            SimpleSeasonStats.SerializePhp(ref writer);
        else
            writer.WriteNull();

        writer.WriteKey("season_casual");
        if (SimpleCasualSeasonStats is not null)
            SimpleCasualSeasonStats.SerializePhp(ref writer);
        else
            writer.WriteNull();

        writer.WriteKey("season_midwars");
        if (SimpleMidWarsSeasonStats is not null)
            SimpleMidWarsSeasonStats.SerializePhp(ref writer);
        else
            writer.WriteNull();

        writer.WriteKey("mvp_num");
        writer.WriteString(MVPAwardsCount ?? string.Empty);

        writer.WriteKey("award_top4_name");
        if (Top4AwardNames is not null)
        {
            writer.WriteArrayHeader(Top4AwardNames.Count);
            for (int i = 0; i < Top4AwardNames.Count; i++)
            {
                writer.WriteKey(i);
                writer.WriteString(Top4AwardNames[i] ?? string.Empty);
            }
            writer.WriteArrayFooter();
        }
        else
        {
            writer.WriteNull();
        }

        writer.WriteKey("award_top4_num");
        if (Top4AwardCounts is not null)
        {
            writer.WriteArrayHeader(Top4AwardCounts.Count);
            for (int i = 0; i < Top4AwardCounts.Count; i++)
            {
                writer.WriteKey(i);
                writer.WriteString(Top4AwardCounts[i] ?? string.Empty);
            }
            writer.WriteArrayFooter();
        }
        else
        {
            writer.WriteNull();
        }

        writer.WriteKey("slot_id");
        writer.WriteString(CustomIconSlotID ?? string.Empty);

        writer.WriteKey("my_upgrades");
        if (OwnedStoreItems is not null)
        {
            writer.WriteArrayHeader(OwnedStoreItems.Count);
            for (int i = 0; i < OwnedStoreItems.Count; i++)
            {
                writer.WriteKey(i);
                writer.WriteString(OwnedStoreItems[i] ?? string.Empty);
            }
            writer.WriteArrayFooter();
        }
        else
        {
            writer.WriteNull();
        }

        writer.WriteKey("selected_upgrades");
        if (SelectedStoreItems is not null)
        {
            writer.WriteArrayHeader(SelectedStoreItems.Count);
            for (int i = 0; i < SelectedStoreItems.Count; i++)
            {
                writer.WriteKey(i);
                writer.WriteString(SelectedStoreItems[i] ?? string.Empty);
            }
            writer.WriteArrayFooter();
        }
        else
        {
            writer.WriteNull();
        }

        writer.WriteKey("my_upgrades_info");
        if (OwnedStoreItemsData is not null)
        {
            writer.WriteArrayHeader(OwnedStoreItemsData.Count);
            foreach (KeyValuePair<string, OwnedStoreItemDTO> kvp in OwnedStoreItemsData)
            {
                writer.WriteKey(kvp.Key);
                PhpSerializer.SerializeValue(ref writer, kvp.Value);
            }
            writer.WriteArrayFooter();
        }
        else
        {
            writer.WriteNull();
        }

        writer.WriteKey("dice_tokens");
        writer.WriteString(DiceTokens ?? "1");

        writer.WriteKey("game_tokens");
        writer.WriteInt(GameTokens);

        writer.WriteKey("timestamp");
        writer.WriteLong(ServerTimestamp);

        writer.WriteKey("rank");
        if (Rank is not null) writer.WriteString(Rank); else writer.WriteNull();

        writer.WriteKey("current_level");
        if (CurrentRankTop is not null) writer.WriteString(CurrentRankTop); else writer.WriteNull();

        writer.WriteKey("smr");
        if (RankedRatingTop is not null) writer.WriteString(RankedRatingTop); else writer.WriteNull();

        writer.WriteKey("highest_level_current");
        if (HighestRankTop is not null) writer.WriteString(HighestRankTop); else writer.WriteNull();

        writer.WriteKey("con_reward");
        if (ConReward is not null) writer.WriteString(ConReward); else writer.WriteNull();

        writer.WriteKey("herokills");
        if (HeroKills is not null) writer.WriteString(HeroKills); else writer.WriteNull();

        writer.WriteKey("deaths");
        if (Deaths is not null) writer.WriteString(Deaths); else writer.WriteNull();

        writer.WriteKey("heroassists");
        if (HeroAssists is not null) writer.WriteString(HeroAssists); else writer.WriteNull();

        writer.WriteKey("k_d_a");
        if (KDA is not null) writer.WriteString(KDA); else writer.WriteNull();

        writer.WriteKey("avgGameLength");
        if (AvgGameLength is not null) writer.WriteString(AvgGameLength); else writer.WriteNull();

        writer.WriteKey("avgXP_min");
        if (AvgXPMin is not null) writer.WriteString(AvgXPMin); else writer.WriteNull();

        writer.WriteKey("avgDenies");
        if (AvgDenies is not null) writer.WriteString(AvgDenies); else writer.WriteNull();

        writer.WriteKey("avgCreepKills");
        if (AvgCreepKills is not null) writer.WriteString(AvgCreepKills); else writer.WriteNull();

        writer.WriteKey("avgNeutralKills");
        if (AvgNeutralKills is not null) writer.WriteString(AvgNeutralKills); else writer.WriteNull();

        writer.WriteKey("avgActions_min");
        if (AvgActionsMin is not null) writer.WriteString(AvgActionsMin); else writer.WriteNull();

        writer.WriteKey("avgWardsUsed");
        if (AvgWardsUsed is not null) writer.WriteString(AvgWardsUsed); else writer.WriteNull();

        writer.WriteKey("gold");
        if (Gold is not null) writer.WriteString(Gold); else writer.WriteNull();

        writer.WriteKey("secs");
        if (Secs is not null) writer.WriteString(Secs); else writer.WriteNull();

        writer.WriteKey("exp");
        if (Exp is not null) writer.WriteString(Exp); else writer.WriteNull();

        writer.WriteKey("humiliation");
        if (Humiliation is not null) writer.WriteString(Humiliation); else writer.WriteNull();

        writer.WriteKey("smackdown");
        if (Smackdown is not null) writer.WriteString(Smackdown); else writer.WriteNull();

        writer.WriteKey("nemesis");
        if (Nemesis is not null) writer.WriteString(Nemesis); else writer.WriteNull();

        writer.WriteKey("retribution");
        if (Retribution is not null) writer.WriteString(Retribution); else writer.WriteNull();

        writer.WriteKey("mastery_info");
        PhpSerializer.SerializeValue(ref writer, MasteryInfo);

        writer.WriteKey("mastery_rewards");
        PhpSerializer.SerializeValue(ref writer, MasteryRewards);

        writer.WriteArrayFooter();
    }
}
