// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using ASPIRE.Common.Constants;
using OneOf;

namespace ASPIRE.Common.Serialization.PHP;

[UnconditionalSuppressMessage("Trimming", "IL2070:TargetWithPublicProperties",
    Justification = "Legacy dynamic protocol formatter fallback")]
[UnconditionalSuppressMessage("Trimming", "IL2075:TargetWithPublicProperties",
    Justification = "Legacy dynamic protocol formatter fallback")]
[UnconditionalSuppressMessage("Trimming", "IL2026:MembersAnnotatedWithRequiresUnreferencedCode",
    Justification = "Legacy dynamic protocol formatter fallback")]
[UnconditionalSuppressMessage("AOT", "IL3050:MembersAnnotatedWithRequiresDynamicCode",
    Justification = "Legacy dynamic protocol formatter fallback")]
public class LegacyProtocolFormatter : ILegacyProtocolFormatter
{
    public static readonly ILegacyProtocolFormatter Instance = new LegacyProtocolFormatter();

    /// <summary>
    ///     Serializes an object directly into PHP wire format bytes, delegating to
    ///     <see cref="IPhpSerializable"/> when available to eliminate reflection and heap allocations.
    /// </summary>
    public byte[] Serialize(object? obj)
    {
        if (obj is IPhpSerializable phpSerializable)
        {
            return PhpSerializer.SerializeToUtf8Bytes(phpSerializable);
        }

        return PhpSerializer.SerializeToUtf8Bytes(obj);
    }

    /// <summary>
    ///     Serializes a <see cref="ShowSimpleStatsResponse"/> directly into PHP wire format bytes
    ///     with zero reflection and zero intermediate dictionary allocation.
    /// </summary>
    public byte[] Serialize(ShowSimpleStatsResponse props)
    {
        return PhpSerializer.SerializeToUtf8Bytes(props);
    }

    public Dictionary<object, object> ToHashtable(SimpleSeasonStats stats, bool isCasual = false)
    {
        Dictionary<object, object> t = new()
        {
            ["wins"] = stats.RankedMatchesWon.ToString(),
            ["losses"] = stats.RankedMatchesLost.ToString(),
            ["h_wins"] = stats.HardcoreWins.ToString(),
            ["h_losses"] = stats.HardcoreLosses.ToString(),
            ["con_wins"] = stats.CasualWins.ToString(),
            ["con_losses"] = stats.CasualLosses.ToString(),
            ["win_streak"] = stats.WinStreak.ToString(),
            ["rank"] = stats.CurrentRank,
            ["current_level"] = stats.LegacyLevel,
            ["smr"] = stats.RankedRating,
            ["highest_level_current"] = stats.HighestRank ?? "0",
            ["is_placement"] = stats.InPlacementPhase.ToString(),
            ["pub_skill"] = stats.PublicRating,
            ["kam_rating"] = stats.CasualRating
        };

        if (isCasual)
        {
            t["cs_wins"] = stats.CasualWins.ToString();
            t["cs_losses"] = stats.CasualLosses.ToString();
            t["cs_games_played"] = (stats.CasualWins + stats.CasualLosses).ToString();

            if (stats.GenericStats != null)
            {
                PlayerStatisticsAggregatedDTO g = stats.GenericStats;
                t["cs_denies"] = g.CasualDenies.ToString();
                t["cs_herodmg"] = g.CasualHeroDamage.ToString();
                t["cs_heroexp"] = "0";
                t["cs_herokillsgold"] = g.CasualHeroGold.ToString();
                t["cs_heroassists"] = g.CasualAssists.ToString();
                t["cs_deaths"] = g.CasualDeaths.ToString();
                t["cs_herokills"] = g.CasualKills.ToString();
                t["cs_goldlost2death"] = g.CasualGoldLost.ToString();
                t["cs_secs_dead"] = g.CasualSecondsDead.ToString();
                t["cs_teamcreepkills"] = g.CasualTeamCreepKills.ToString();
                t["cs_teamcreepdmg"] = g.CasualTeamCreepDmg.ToString();
                t["cs_teamcreepgold"] = g.CasualTeamCreepGold.ToString();
                t["cs_teamcreepexp"] = g.CasualTeamCreepExp.ToString();
                t["cs_neutralcreepkills"] = g.CasualNeutralCreepKills.ToString();
                t["cs_neutralcreepdmg"] = g.CasualNeutralCreepDmg.ToString();
                t["cs_neutralcreepgold"] = g.CasualNeutralCreepGold.ToString();
                t["cs_neutralcreepexp"] = g.CasualNeutralCreepExp.ToString();
                t["cs_bdmg"] = g.CasualBuildingDmg.ToString();
                t["cs_razed"] = g.CasualBuildingsRazed.ToString();
                t["cs_bdmgexp"] = g.CasualBuildingExp.ToString();
                t["cs_bgold"] = g.CasualBuildingGold.ToString();
                t["cs_exp_denied"] = g.CasualExpDenied.ToString();
                t["cs_gold"] = g.CasualGold.ToString();
                t["cs_gold_spend"] = g.CasualGoldSpent.ToString();
                t["cs_actions"] = g.CasualActions.ToString();
                t["cs_consumables"] = g.CasualConsumables.ToString();
                t["cs_wards"] = g.CasualWards.ToString();
                t["cs_bloodlust"] = g.CasualFirstBloods.ToString();
                t["cs_doublekill"] = g.CasualDoubleKills.ToString();
                t["cs_triplekill"] = g.CasualTripleKills.ToString();
                t["cs_quadkill"] = g.CasualQuadKills.ToString();
                t["cs_annihilation"] = g.CasualAnnihilations.ToString();
                t["cs_ks3"] = g.CasualKS3.ToString();
                t["cs_ks4"] = g.CasualKS4.ToString();
                t["cs_ks5"] = g.CasualKS5.ToString();
                t["cs_ks6"] = g.CasualKS6.ToString();
                t["cs_ks7"] = g.CasualKS7.ToString();
                t["cs_ks8"] = g.CasualKS8.ToString();
                t["cs_ks9"] = g.CasualKS9.ToString();
                t["cs_ks10"] = g.CasualKS10.ToString();
                t["cs_ks15"] = g.CasualKS15.ToString();
                t["cs_smackdown"] = g.CasualSmackdowns.ToString();
                t["cs_humiliation"] = g.CasualHumiliations.ToString();
                t["cs_nemesis"] = g.CasualNemesis.ToString();
                t["cs_retribution"] = g.CasualRetribution.ToString();
                t["cs_time_earning_exp"] = g.CasualTimeEarningExp.ToString();
                t["cs_buybacks"] = g.CasualBuybacks.ToString();

                t["cam_cs_denies"] = t["cs_denies"];
                t["cam_cs_herodmg"] = t["cs_herodmg"];
                t["cam_cs_herokills"] = t["cs_herokills"];
                t["cam_cs_deaths"] = t["cs_deaths"];
                t["cam_cs_gold"] = t["cs_gold"];
                t["cam_cs_exp"] = g.CasualExp.ToString();
                t["cam_cs_level"] = "0";

                t["cs_exp"] = g.CasualExp.ToString();
                t["cs_secs"] = g.CasualSeconds.ToString();
                t["cam_cs_secs"] = t["cs_secs"];
                t["cam_cs_games_played"] = t["cs_games_played"];
                t["cam_cs_wins"] = t["cs_wins"];
                t["cam_cs_losses"] = t["cs_losses"];
            }
        }
        else
        {
            t["rnk_wins"] = stats.RankedMatchesWon.ToString();
            t["rnk_losses"] = stats.RankedMatchesLost.ToString();
            t["rnk_games_played"] = (stats.RankedMatchesWon + stats.RankedMatchesLost).ToString();
            t["acc_wins"] = stats.RankedMatchesWon.ToString();
            t["acc_games_played"] = (stats.RankedMatchesWon + stats.RankedMatchesLost).ToString();

            if (stats.GenericStats != null)
            {
                PlayerStatisticsAggregatedDTO g = stats.GenericStats;
                t["rnk_denies"] = g.RankedDenies.ToString();
                t["rnk_herodmg"] = g.RankedHeroDamage.ToString();
                t["rnk_herokillsgold"] = g.RankedHeroGold.ToString();
                t["rnk_heroassists"] = g.RankedAssists.ToString();
                t["rnk_deaths"] = g.RankedDeaths.ToString();
                t["rnk_herokills"] = g.RankedKills.ToString();
                t["rnk_goldlost2death"] = g.RankedGoldLost.ToString();
                t["rnk_secs_dead"] = g.RankedSecondsDead.ToString();
                t["rnk_teamcreepkills"] = g.RankedTeamCreepKills.ToString();
                t["rnk_teamcreepdmg"] = g.RankedTeamCreepDmg.ToString();
                t["rnk_teamcreepgold"] = g.RankedTeamCreepGold.ToString();
                t["rnk_teamcreepexp"] = g.RankedTeamCreepExp.ToString();
                t["rnk_neutralcreepkills"] = g.RankedNeutralCreepKills.ToString();
                t["rnk_neutralcreepdmg"] = g.RankedNeutralCreepDmg.ToString();
                t["rnk_neutralcreepgold"] = g.RankedNeutralCreepGold.ToString();
                t["rnk_neutralcreepexp"] = g.RankedNeutralCreepExp.ToString();
                t["rnk_bdmg"] = g.RankedBuildingDmg.ToString();
                t["rnk_razed"] = g.RankedBuildingsRazed.ToString();
                t["rnk_bdmgexp"] = g.RankedBuildingExp.ToString();
                t["rnk_bgold"] = g.RankedBuildingGold.ToString();
                t["rnk_exp_denied"] = g.RankedExpDenied.ToString();
                t["rnk_gold"] = g.RankedGold.ToString();
                t["rnk_gold_spend"] = g.RankedGoldSpent.ToString();
                t["rnk_actions"] = g.RankedActions.ToString();
                t["rnk_consumables"] = g.RankedConsumables.ToString();
                t["rnk_wards"] = g.RankedWards.ToString();
                t["rnk_bloodlust"] = g.RankedFirstBloods.ToString();
                t["rnk_doublekill"] = g.RankedDoubleKills.ToString();
                t["rnk_triplekill"] = g.RankedTripleKills.ToString();
                t["rnk_quadkill"] = g.RankedQuadKills.ToString();
                t["rnk_annihilation"] = g.RankedAnnihilations.ToString();
                t["rnk_ks3"] = g.RankedKS3.ToString();
                t["rnk_ks4"] = g.RankedKS4.ToString();
                t["rnk_ks5"] = g.RankedKS5.ToString();
                t["rnk_ks6"] = g.RankedKS6.ToString();
                t["rnk_ks7"] = g.RankedKS7.ToString();
                t["rnk_ks8"] = g.RankedKS8.ToString();
                t["rnk_ks9"] = g.RankedKS9.ToString();
                t["rnk_ks10"] = g.RankedKS10.ToString();
                t["rnk_ks15"] = g.RankedKS15.ToString();
                t["rnk_smackdown"] = g.RankedSmackdowns.ToString();
                t["rnk_humiliation"] = g.RankedHumiliations.ToString();
                t["rnk_nemesis"] = g.RankedNemesis.ToString();
                t["rnk_retribution"] = g.RankedRetribution.ToString();
                t["rnk_time_earning_exp"] = g.RankedTimeEarningExp.ToString();
                t["rnk_buybacks"] = g.RankedBuybacks.ToString();

                t["cam_denies"] = t["rnk_denies"];
                t["cam_herodmg"] = t["rnk_herodmg"];
                t["cam_herokills"] = t["rnk_herokills"];
                t["cam_deaths"] = t["rnk_deaths"];
                t["cam_gold"] = t["rnk_gold"];
                t["cam_exp"] = g.RankedExp.ToString();
                t["cam_secs"] = g.RankedSeconds.ToString();

                t["rnk_exp"] = g.RankedExp.ToString();
                t["rnk_secs"] = g.RankedSeconds.ToString();

                t["cam_wins"] = t["rnk_wins"];
                t["cam_losses"] = t["rnk_losses"];
                t["cam_discos"] = (isCasual ? stats.GenericStats?.CasualDiscos : stats.GenericStats?.RankedDiscos)
                    ?.ToString() ?? "0";
                t["cam_concedes"] = "0";
                t["cam_concedevotes"] = "0";
                t["cam_buybacks"] = t["rnk_buybacks"];
                t["cam_herokills"] = t["rnk_herokills"];
                t["cam_heroassists"] = t["rnk_heroassists"];
                t["cam_deaths"] = t["rnk_deaths"];
                t["cam_gold"] = t["rnk_gold"];
                t["cam_exp"] = t["rnk_exp"];
                t["cam_secs"] = t["rnk_secs"];
                t["cam_consumables"] = t["rnk_consumables"];
                t["cam_wards"] = t["rnk_wards"];
                t["cam_bloodlust"] = t["rnk_bloodlust"];
                t["cam_doublekill"] = t["rnk_doublekill"];
                t["cam_triplekill"] = t["rnk_triplekill"];
                t["cam_quadkill"] = t["rnk_quadkill"];
                t["cam_annihilation"] = t["rnk_annihilation"];
                t["cam_ks3"] = t["rnk_ks3"];
                t["cam_ks4"] = t["rnk_ks4"];
                t["cam_ks5"] = t["rnk_ks5"];
                t["cam_ks6"] = t["rnk_ks6"];
                t["cam_ks7"] = t["rnk_ks7"];
                t["cam_ks8"] = t["rnk_ks8"];
                t["cam_ks9"] = t["rnk_ks9"];
                t["cam_ks10"] = t["rnk_ks10"];
                t["cam_ks15"] = t["rnk_ks15"];
                t["cam_smackdown"] = t["rnk_smackdown"];
                t["cam_humiliation"] = t["rnk_humiliation"];
                t["cam_nemesis"] = t["rnk_nemesis"];
                t["cam_retribution"] = t["rnk_retribution"];
                t["cam_teamkillexp"] = t["rnk_teamcreepexp"];
                t["cam_teamkillgold"] = t["rnk_teamcreepgold"];
                t["cam_actions"] = t["rnk_actions"];
                t["cam_amm_team_rating"] = "0.000";
                t["cam_level"] = "0";
            }
        }

        string levelStr = stats.LegacyLevel;
        t["con_reward"] = $"{levelStr},1,3,2,11,0,0";
        t["cam_con_reward"] = $"{levelStr},1,3,2,11,0,0";

        return t;
    }

    public object[] CreateLegacyPositionalPlayerStats(ShowSimpleStatsResponse props)
    {
        PlayerStatisticsAggregatedDTO stats = props.SimpleSeasonStats.GenericStats ??
                                              new PlayerStatisticsAggregatedDTO();

        long totalXp = props.LevelExperience;
        int currentLevel = props.Level;

        int currentLevelBaseXp = 100 * (int)Math.Pow(currentLevel, 3);
        int nextLevelBaseXp = 100 * (int)Math.Pow(currentLevel + 1, 3);

        float levelPercent = 0;
        if (nextLevelBaseXp > currentLevelBaseXp)
        {
            levelPercent = (float)(totalXp - currentLevelBaseXp) / (nextLevelBaseXp - currentLevelBaseXp) * 100f;
            if (levelPercent < 0) levelPercent = 0;

            if (levelPercent > 100) levelPercent = 100;
        }

        object[] array =
        [
            props.NameWithClanTag,
            "0",
            props.CurrentRankTop ?? "0",
            props.SeasonLevel.ToString(),
            props.ID.Value.ToString() ?? "0",
            stats.RankedMatches.ToString(),
            props.RankedWins.ToString(),
            props.RankedLosses.ToString(),
            "0",
            "0",
            "0",
            stats.RankedDiscos.ToString(),
            "0",
            props.RankedRatingTop ?? "1500.000",
            "0",
            props.RankedRatingTop ?? "1500.000",
            stats.RankedMatches.ToString(),
            props.RankedRatingTop ?? "1500.000",
            stats.RankedMatches.ToString(),
            "0",
            stats.RankedKills.ToString(),
            stats.RankedHeroDamage.ToString(),
            "0",
            stats.RankedHeroGold.ToString(),
            stats.RankedAssists.ToString(),
            stats.RankedDeaths.ToString(),
            stats.RankedGoldLost.ToString(),
            stats.RankedSecondsDead.ToString(),
            stats.RankedTeamCreepKills.ToString(),
            stats.RankedTeamCreepDmg.ToString(),
            stats.RankedTeamCreepExp.ToString(),
            stats.RankedTeamCreepGold.ToString(),
            stats.RankedNeutralCreepKills.ToString(),
            stats.RankedNeutralCreepDmg.ToString(),
            stats.RankedNeutralCreepExp.ToString(),
            stats.RankedNeutralCreepGold.ToString(),
            stats.RankedBuildingDmg.ToString(),
            stats.RankedBuildingExp.ToString(),
            stats.RankedBuildingsRazed.ToString(),
            stats.RankedBuildingGold.ToString(),
            stats.RankedDenies.ToString(),
            stats.RankedExpDenied.ToString(),
            stats.RankedGold.ToString(),
            stats.RankedGoldSpent.ToString(),
            stats.RankedExp.ToString(),
            stats.RankedActions.ToString(),
            stats.RankedTimeEarningExp.ToString(),
            stats.RankedConsumables.ToString(),
            stats.RankedWards.ToString(),
            "0",
            "0",
            props.LastActivityDate,
            "0",
            "0",
            props.FavHero1,
            props.FavHero2,
            props.FavHero3,
            props.FavHero4,
            props.FavHero5,
            props.FavHero1Time.ToString(),
            props.FavHero2Time.ToString(),
            props.FavHero3Time.ToString(),
            props.FavHero4Time.ToString(),
            props.FavHero5Time.ToString(),
            "0",
            "0",
            "0",
            $"{stats.RankedKills}/{stats.RankedDeaths}/{stats.RankedAssists}",
            stats.RankedMatches > 0
                ? (stats.RankedTimeEarningExp / (float)stats.RankedMatches / 60.0f).ToString("0.0")
                : "0",
            stats.RankedTimeEarningExp > 0
                ? (stats.RankedExp / (stats.RankedTimeEarningExp / 60.0f)).ToString("0.0")
                : "0",
            stats.RankedMatches > 0
                ? (stats.RankedDenies / (float)stats.RankedMatches).ToString("0.0")
                : "0",
            stats.RankedMatches > 0
                ? (stats.RankedTeamCreepKills / (float)stats.RankedMatches).ToString("0.0")
                : "0",
            stats.RankedMatches > 0
                ? (stats.RankedNeutralCreepKills / (float)stats.RankedMatches).ToString("0.0")
                : "0",
            stats.RankedTimeEarningExp > 0
                ? (stats.RankedActions / (stats.RankedTimeEarningExp / 60.0f)).ToString("0.0")
                : "0",
            stats.RankedMatches > 0
                ? (stats.RankedWards / (float)stats.RankedMatches).ToString("0.0")
                : "0",
            props.AccountCreationDate,
            props.FavHero1_2,
            props.FavHero2_2,
            props.FavHero3_2,
            props.FavHero4_2,
            props.FavHero5_2,
            "0",
            "0",
            "0",
            "0",
            "0",
            "0",
            props.SeasonLevel.ToString(),
            "0",
            props.TotalMatchesPlayed.ToString(),
            stats.CasualMatches.ToString(),
            props.TotalDisconnects.ToString(),
            stats.CasualDiscos.ToString(),
            stats.RankedFirstBloods.ToString(),
            stats.RankedDoubleKills.ToString(),
            stats.RankedTripleKills.ToString(),
            stats.RankedQuadKills.ToString(),
            stats.RankedAnnihilations.ToString(),
            stats.RankedKS3.ToString(),
            stats.RankedKS4.ToString(),
            stats.RankedKS5.ToString(),
            stats.RankedKS6.ToString(),
            stats.RankedKS7.ToString(),
            stats.RankedKS8.ToString(),
            stats.RankedKS8.ToString(),
            stats.RankedKS10.ToString(),
            stats.RankedKS15.ToString(),
            stats.RankedSmackdowns.ToString(),
            stats.RankedHumiliations.ToString(),
            stats.RankedNemesis.ToString(),
            stats.RankedRetribution.ToString(),
            props.LevelExperience.ToString(),
            stats.RankedTimeEarningExp.ToString(),
            props.Level.ToString(),
            props.LevelExperience.ToString(),
            props.TotalDisconnects.ToString(),
            "0",
            props.TotalMatchesPlayed.ToString(),
            "1",
            "100",
            levelPercent.ToString(CultureInfo.InvariantCulture),
            nextLevelBaseXp.ToString(),
            currentLevelBaseXp.ToString(),
            stats.PublicMatches.ToString(),
            stats.PublicDiscos.ToString(),
            props.TotalMatchesPlayed.ToString(),
            props.TotalDisconnects.ToString(),
            "0",
            "0",
            "0",
            "0",
            "0",
            "0",
            "0",
            "0",
            props.CurrentRankTop ?? "0",
            props.HighestRankTop ?? "0",
            props.CurrentSeason.ToString(),
            props.RankedRatingTop ?? "1500.000",
            stats.RankedMatches.ToString(),
            stats.CasualMatches.ToString(),
            stats.RankedDiscos.ToString(),
            stats.CasualDiscos.ToString(),
            "0",
            "0",
            "0",
            "0",
            props.RankedRatingTop ?? "1500.000",
            props.ConReward ?? "0"
        ];
        return array;
    }

    private sealed record HybridPropertyMeta(
        string Name,
        Type PropertyType,
        bool IsOneOf,
        object PropertyKey,
        Func<object, object?> Getter);

    private static readonly HybridPropertyMeta[] _hybridStatsProperties = typeof(ShowSimpleStatsResponse)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Select(p =>
        {
            PHPPropertyAttribute? attr = p.GetCustomAttribute<PHPPropertyAttribute>();
            if (attr == null) return null;
            MethodInfo? getter = p.GetGetMethod();
            Func<object, object?> getterDelegate;
            if (getter != null)
            {
                try
                {
                    ParameterExpression param = Expression.Parameter(typeof(object), "x");
                    UnaryExpression cast = Expression.Convert(param, typeof(ShowSimpleStatsResponse));
                    MemberExpression propAccess = Expression.Property(cast, p);
                    UnaryExpression castResult = Expression.Convert(propAccess, typeof(object));
                    getterDelegate = Expression.Lambda<Func<object, object?>>(castResult, param).Compile();
                }
                catch
                {
                    getterDelegate = p.GetValue;
                }
            }
            else
            {
                getterDelegate = p.GetValue;
            }

            bool isOneOf = p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == typeof(OneOf<,>);
            return new HybridPropertyMeta(p.Name, p.PropertyType, isOneOf, attr.PropertyKey.Value, getterDelegate);
        })
        .Where(x => x != null)
        .Select(x => x!)
        .ToArray();

    public Dictionary<object, object> CreateHybridSimpleStats(ShowSimpleStatsResponse props, string? table = null)
    {
        Dictionary<object, object> hybridData = [];

        foreach (HybridPropertyMeta meta in _hybridStatsProperties)
        {
            if (meta.Name == nameof(ShowSimpleStatsResponse.SimpleSeasonStats))
            {
                SimpleSeasonStats val = props.SimpleSeasonStats ?? new SimpleSeasonStats
                {
                    RankedMatchesWon = 0,
                    RankedMatchesLost = 0,
                    WinStreak = 0,
                    CurrentRank = "0.000",
                    RankedRating = "1500.000",
                    InPlacementPhase = 0
                };
                hybridData[meta.PropertyKey] = ToHashtable(val);
            }
            else if (meta.Name == nameof(ShowSimpleStatsResponse.SimpleCasualSeasonStats))
            {
                SimpleSeasonStats val = props.SimpleCasualSeasonStats ?? new SimpleSeasonStats
                {
                    RankedMatchesWon = 0,
                    RankedMatchesLost = 0,
                    WinStreak = 0,
                    CurrentRank = "0.000",
                    RankedRating = "1500.000",
                    InPlacementPhase = 0
                };
                hybridData[meta.PropertyKey] = ToHashtable(val, true);
            }
            else if (meta.Name == nameof(ShowSimpleStatsResponse.SimpleMidWarsSeasonStats))
            {
                SimpleSeasonStats val = props.SimpleMidWarsSeasonStats ?? new SimpleSeasonStats
                {
                    RankedMatchesWon = 0,
                    RankedMatchesLost = 0,
                    WinStreak = 0,
                    CurrentRank = "0.000",
                    RankedRating = "1500.000",
                    InPlacementPhase = 0
                };
                hybridData[meta.PropertyKey] = ToHashtable(val);
            }
            else if (meta.IsOneOf)
            {
                object? oneOfValue = meta.Getter(props);

                if (oneOfValue is OneOf.IOneOf oneOf)
                {
                    hybridData[meta.PropertyKey] = ConvertToPhp(oneOf.Value);
                }
                else
                {
                    hybridData[meta.PropertyKey] = "0";
                }
            }
            else
            {
                object? val = meta.Getter(props);
                hybridData[meta.PropertyKey] = ConvertToPhp(val);
            }
        }

        PlayerStatisticsAggregatedDTO? simpleStats = props.SimpleSeasonStats?.GenericStats;
        if (simpleStats != null)
        {
            string Sum(int r, int c, int p)
            {
                return (r + c + p).ToString();
            }

            string SumL(long r, long c, long p)
            {
                return (r + c + p).ToString();
            }

            hybridData["acc_denies"] =
                Sum(simpleStats.RankedDenies, simpleStats.CasualDenies, simpleStats.PublicDenies);
            hybridData["acc_herodmg"] = Sum(simpleStats.RankedHeroDamage, simpleStats.CasualHeroDamage,
                simpleStats.PublicHeroDamage);
            hybridData["acc_herokillsgold"] = Sum(simpleStats.RankedHeroGold, simpleStats.CasualHeroGold,
                simpleStats.PublicHeroGold);
            hybridData["acc_heroassists"] = Sum(simpleStats.RankedAssists, simpleStats.CasualAssists,
                simpleStats.PublicAssists);
            hybridData["acc_deaths"] =
                Sum(simpleStats.RankedDeaths, simpleStats.CasualDeaths, simpleStats.PublicDeaths);
            hybridData["acc_herokills"] =
                Sum(simpleStats.RankedKills, simpleStats.CasualKills, simpleStats.PublicKills);
            hybridData["acc_goldlost2death"] = Sum(simpleStats.RankedGoldLost, simpleStats.CasualGoldLost,
                simpleStats.PublicGoldLost);
            hybridData["acc_secs_dead"] = Sum(simpleStats.RankedSecondsDead, simpleStats.CasualSecondsDead,
                simpleStats.PublicSecondsDead);
            hybridData["acc_teamcreepkills"] = Sum(simpleStats.RankedTeamCreepKills, simpleStats.CasualTeamCreepKills,
                simpleStats.PublicTeamCreepKills);
            hybridData["acc_teamcreepdmg"] = Sum(simpleStats.RankedTeamCreepDmg, simpleStats.CasualTeamCreepDmg,
                simpleStats.PublicTeamCreepDmg);
            hybridData["acc_teamcreepgold"] = Sum(simpleStats.RankedTeamCreepGold, simpleStats.CasualTeamCreepGold,
                simpleStats.PublicTeamCreepGold);
            hybridData["acc_teamcreepexp"] = Sum(simpleStats.RankedTeamCreepExp, simpleStats.CasualTeamCreepExp,
                simpleStats.PublicTeamCreepExp);
            hybridData["acc_neutralcreepkills"] = Sum(simpleStats.RankedNeutralCreepKills,
                simpleStats.CasualNeutralCreepKills, simpleStats.PublicNeutralCreepKills);
            hybridData["acc_neutralcreepdmg"] = Sum(simpleStats.RankedNeutralCreepDmg,
                simpleStats.CasualNeutralCreepDmg, simpleStats.PublicNeutralCreepDmg);
            hybridData["acc_neutralcreepgold"] = Sum(simpleStats.RankedNeutralCreepGold,
                simpleStats.CasualNeutralCreepGold, simpleStats.PublicNeutralCreepGold);
            hybridData["acc_neutralcreepexp"] = Sum(simpleStats.RankedNeutralCreepExp,
                simpleStats.CasualNeutralCreepExp, simpleStats.PublicNeutralCreepExp);
            hybridData["acc_bdmg"] = Sum(simpleStats.RankedBuildingDmg, simpleStats.CasualBuildingDmg,
                simpleStats.PublicBuildingDmg);
            hybridData["acc_razed"] = Sum(simpleStats.RankedBuildingsRazed, simpleStats.CasualBuildingsRazed,
                simpleStats.PublicBuildingsRazed);
            hybridData["acc_bdmgexp"] = Sum(simpleStats.RankedBuildingExp, simpleStats.CasualBuildingExp,
                simpleStats.PublicBuildingExp);
            hybridData["acc_bgold"] = Sum(simpleStats.RankedBuildingGold, simpleStats.CasualBuildingGold,
                simpleStats.PublicBuildingGold);
            hybridData["acc_exp_denied"] = Sum(simpleStats.RankedExpDenied, simpleStats.CasualExpDenied,
                simpleStats.PublicExpDenied);
            hybridData["acc_gold"] = SumL(simpleStats.RankedGold, simpleStats.CasualGold, simpleStats.PublicGold);
            hybridData["acc_gold_spend"] = Sum(simpleStats.RankedGoldSpent, simpleStats.CasualGoldSpent,
                simpleStats.PublicGoldSpent);
            hybridData["acc_actions"] =
                Sum(simpleStats.RankedActions, simpleStats.CasualActions, simpleStats.PublicActions);
            hybridData["acc_consumables"] = Sum(simpleStats.RankedConsumables, simpleStats.CasualConsumables,
                simpleStats.PublicConsumables);
            hybridData["acc_wards"] = Sum(simpleStats.RankedWards, simpleStats.CasualWards, simpleStats.PublicWards);
            hybridData["acc_bloodlust"] = Sum(simpleStats.RankedFirstBloods, simpleStats.CasualFirstBloods,
                simpleStats.PublicFirstBloods);
            hybridData["acc_doublekill"] = Sum(simpleStats.RankedDoubleKills, simpleStats.CasualDoubleKills,
                simpleStats.PublicDoubleKills);
            hybridData["acc_triplekill"] = Sum(simpleStats.RankedTripleKills, simpleStats.CasualTripleKills,
                simpleStats.PublicTripleKills);
            hybridData["acc_quadkill"] = Sum(simpleStats.RankedQuadKills, simpleStats.CasualQuadKills,
                simpleStats.PublicQuadKills);
            hybridData["acc_annihilation"] = Sum(simpleStats.RankedAnnihilations, simpleStats.CasualAnnihilations,
                simpleStats.PublicAnnihilations);
            hybridData["acc_ks3"] = Sum(simpleStats.RankedKS3, simpleStats.CasualKS3, simpleStats.PublicKS3);
            hybridData["acc_ks4"] = Sum(simpleStats.RankedKS4, simpleStats.CasualKS4, simpleStats.PublicKS4);
            hybridData["acc_ks5"] = Sum(simpleStats.RankedKS5, simpleStats.CasualKS5, simpleStats.PublicKS5);
            hybridData["acc_ks6"] = Sum(simpleStats.RankedKS6, simpleStats.CasualKS6, simpleStats.PublicKS6);
            hybridData["acc_ks7"] = Sum(simpleStats.RankedKS7, simpleStats.CasualKS7, simpleStats.PublicKS7);
            hybridData["acc_ks8"] = Sum(simpleStats.RankedKS8, simpleStats.CasualKS8, simpleStats.PublicKS8);
            hybridData["acc_ks9"] = Sum(simpleStats.RankedKS9, simpleStats.CasualKS9, simpleStats.PublicKS9);
            hybridData["acc_ks10"] = Sum(simpleStats.RankedKS10, simpleStats.CasualKS10, simpleStats.PublicKS10);
            hybridData["acc_ks15"] = Sum(simpleStats.RankedKS15, simpleStats.CasualKS15, simpleStats.PublicKS15);
            hybridData["acc_smackdown"] = Sum(simpleStats.RankedSmackdowns, simpleStats.CasualSmackdowns,
                simpleStats.PublicSmackdowns);
            hybridData["acc_humiliation"] = Sum(simpleStats.RankedHumiliations, simpleStats.CasualHumiliations,
                simpleStats.PublicHumiliations);
            hybridData["acc_nemesis"] =
                Sum(simpleStats.RankedNemesis, simpleStats.CasualNemesis, simpleStats.PublicNemesis);
            hybridData["acc_retribution"] = Sum(simpleStats.RankedRetribution, simpleStats.CasualRetribution,
                simpleStats.PublicRetribution);
            hybridData["acc_time_earning_exp"] = Sum(simpleStats.RankedTimeEarningExp, simpleStats.CasualTimeEarningExp,
                simpleStats.PublicTimeEarningExp);
            hybridData["acc_buybacks"] = Sum(simpleStats.RankedBuybacks, simpleStats.CasualBuybacks,
                simpleStats.PublicBuybacks);

            hybridData["acc_exp"] = SumL(simpleStats.RankedExp, simpleStats.CasualExp, simpleStats.PublicExp);
            hybridData["acc_secs"] =
                SumL(simpleStats.RankedSeconds, simpleStats.CasualSeconds, simpleStats.PublicSeconds);
        }

        hybridData["possible_discos"] = "0";
        hybridData["account_type"] = "0";
        hybridData["standing"] = "0";
        hybridData["level_percent"] = "0";
        hybridData["max_exp"] = "0";
        hybridData["min_exp"] = "0";
        hybridData["mid_games_played"] = "0";
        hybridData["mid_discos"] = "0";
        hybridData["cur_games_played"] = props.CurrentGamesPlayed.ToString();
        hybridData["cur_discos"] = props.CurrentDisconnects.ToString();
        hybridData["secs"] = props.Secs ?? hybridData.GetValueOrDefault("acc_secs", "0")?.ToString() ?? "0";
        hybridData["exp"] = props.Exp ?? hybridData.GetValueOrDefault("acc_exp", "0")?.ToString() ?? "0";
        hybridData["event_id"] = "0";
        hybridData["events"] = "0";
        hybridData["season_id"] = props.CurrentSeason.ToString();

        SimpleSeasonStats stats = props.SimpleSeasonStats ?? new SimpleSeasonStats
        {
            RankedMatchesWon = 0,
            RankedMatchesLost = 0,
            WinStreak = 0,
            CurrentRank = "0.000",
            RankedRating = "1500.000",
            InPlacementPhase = 0
        };

        SimpleSeasonStats activeStats = stats;

        MatchFilterClassification classification = MapCatalog.ClassifyMatchFilter(table);

        if (classification.Kind is MatchFilterKind.AllPublic or MatchFilterKind.PublicMap)
        {
            PlayerStatisticsAggregatedDTO? pub = props.SimpleSeasonStats?.GenericStats;
            int pubWins = pub != null ? (pub.PublicWins + pub.CasualWins) : 0;
            int pubLosses = pub != null ? (pub.PublicLosses + pub.CasualLosses) : 0;
            if (pubWins == 0 && pubLosses == 0 && pub != null && pub.TotalMatches > 0)
            {
                pubWins = pub.RankedWins;
                pubLosses = pub.RankedLosses;
            }

            double ratingChange = 0;
            if (pub != null)
                ratingChange = pub.PublicRatingChange != 0
                    ? pub.PublicRatingChange
                    : pub.CasualRatingChange != 0
                        ? pub.CasualRatingChange
                        : pub.RankedRatingChange;

            activeStats = new SimpleSeasonStats
            {
                RankedMatchesWon = pubWins,
                RankedMatchesLost = pubLosses,
                RankedRating = (1500.0 + ratingChange).ToString(CultureInfo.InvariantCulture),
                CurrentRank = "0",
                HighestRank = "0",
                WinStreak = 0,
                InPlacementPhase = 0
            };
        }
        else if (classification.Kind is MatchFilterKind.RankedMidWars)
        {
            activeStats = props.SimpleMidWarsSeasonStats ?? stats;
            if (activeStats.RankedMatchesWon == 0 && activeStats.RankedMatchesLost == 0 && props.SimpleSeasonStats?.GenericStats != null)
            {
                PlayerStatisticsAggregatedDTO gen = props.SimpleSeasonStats.GenericStats;
                if (gen.RankedMatches > 0 || gen.TotalMatches > 0)
                {
                    activeStats = new SimpleSeasonStats
                    {
                        RankedMatchesWon = gen.RankedWins,
                        RankedMatchesLost = gen.RankedLosses,
                        RankedRating = (1500.0 + gen.RankedRatingChange).ToString(CultureInfo.InvariantCulture),
                        CurrentRank = "0.000",
                        HighestRank = "0.000",
                        WinStreak = 0,
                        InPlacementPhase = 0
                    };
                }
            }
        }
        else if (classification.Kind is MatchFilterKind.Quickmatch)
        {
            activeStats = props.SimpleCasualSeasonStats ?? stats;
            if (activeStats.RankedMatchesWon == 0 && activeStats.RankedMatchesLost == 0 && props.SimpleSeasonStats?.GenericStats != null)
            {
                PlayerStatisticsAggregatedDTO gen = props.SimpleSeasonStats.GenericStats;
                if (gen.RankedMatches > 0 || gen.CasualMatches > 0 || gen.TotalMatches > 0)
                {
                    int qmWins = gen.RankedWins > 0 || gen.RankedLosses > 0 ? gen.RankedWins : gen.CasualWins;
                    int qmLosses = gen.RankedWins > 0 || gen.RankedLosses > 0 ? gen.RankedLosses : gen.CasualLosses;
                    double qmRatingChange = gen.RankedRatingChange != 0 ? gen.RankedRatingChange : gen.CasualRatingChange;
                    activeStats = new SimpleSeasonStats
                    {
                        RankedMatchesWon = qmWins,
                        RankedMatchesLost = qmLosses,
                        RankedRating = (1500.0 + qmRatingChange).ToString(CultureInfo.InvariantCulture),
                        CurrentRank = "0.000",
                        HighestRank = "0.000",
                        WinStreak = 0,
                        InPlacementPhase = 0
                    };
                }
            }
        }
        else if (classification.Kind is MatchFilterKind.RankedRiftWars)
        {
            if (props.SimpleSeasonStats?.GenericStats != null)
            {
                PlayerStatisticsAggregatedDTO gen = props.SimpleSeasonStats.GenericStats;
                activeStats = new SimpleSeasonStats
                {
                    RankedMatchesWon = gen.RankedWins,
                    RankedMatchesLost = gen.RankedLosses,
                    RankedRating = (1500.0 + gen.RankedRatingChange).ToString(CultureInfo.InvariantCulture),
                    CurrentRank = "0.000",
                    HighestRank = "0.000",
                    WinStreak = 0,
                    InPlacementPhase = 0
                };
            }
        }

        int matchesPlayed = activeStats.RankedMatchesWon + activeStats.RankedMatchesLost;
        int totalAllMatches = simpleStats?.TotalMatches ?? matchesPlayed;
        int totalAllDiscos = simpleStats?.Disconnected ?? 0;

        hybridData["wins"] = activeStats.RankedMatchesWon.ToString();
        hybridData["rnk_wins"] = activeStats.RankedMatchesWon.ToString();
        hybridData["losses"] = activeStats.RankedMatchesLost.ToString();
        hybridData["rnk_losses"] = activeStats.RankedMatchesLost.ToString();
        hybridData["matches"] = totalAllMatches.ToString();
        hybridData["total_games_played"] = totalAllMatches.ToString();
        hybridData["rnk_games_played"] = matchesPlayed.ToString();
        hybridData["total_discos"] = totalAllDiscos.ToString();
        hybridData["disconnects"] = totalAllDiscos.ToString();
        hybridData["create_date"] = props.AccountCreationDate;
        hybridData["last_activity"] = props.LastActivityDate;
        hybridData["level"] = props.SeasonLevel.ToString();
        hybridData["con_reward"] = props.ConReward ?? "";
        hybridData["rank"] = props.Rank ?? "0";
        hybridData["highest_level_current"] = props.HighestRankTop ?? "0";
        if (props.MasteryInfo != null) hybridData["mastery_info"] = props.MasteryInfo.ToString()!;

        if (props.MasteryRewards != null) hybridData["mastery_rewards"] = props.MasteryRewards.ToString()!;

        hybridData["favHero1"] = props.FavHero1;
        hybridData["favHero2"] = props.FavHero2;
        hybridData["favHero3"] = props.FavHero3;
        hybridData["favHero4"] = props.FavHero4;
        hybridData["favHero5"] = props.FavHero5;

        hybridData["favHero1_2"] = props.FavHero1_2;
        hybridData["favHero2_2"] = props.FavHero2_2;
        hybridData["favHero3_2"] = props.FavHero3_2;
        hybridData["favHero4_2"] = props.FavHero4_2;
        hybridData["favHero5_2"] = props.FavHero5_2;

        hybridData["favHero1Time"] = props.FavHero1Time.ToString();
        hybridData["favHero2Time"] = props.FavHero2Time.ToString();
        hybridData["favHero3Time"] = props.FavHero3Time.ToString();
        hybridData["favHero4Time"] = props.FavHero4Time.ToString();
        hybridData["favHero5Time"] = props.FavHero5Time.ToString();

        PlayerStatisticsAggregatedDTO generic =
            props.SimpleSeasonStats?.GenericStats ?? new PlayerStatisticsAggregatedDTO();

        Set(1, props.NameWithClanTag);
        Set(2, props.NameWithClanTag);
        Set(3, props.Rank);
        Set(4, props.SeasonLevel);
        Set(5, props.ID.Value);
        Set(6, activeStats.RankedMatchesWon + activeStats.RankedMatchesLost);
        Set(7, activeStats.RankedMatchesWon);
        Set(8, activeStats.RankedMatchesLost);
        Set(9, 0);
        Set(10, 0);

        Set(11, props.MasteryInfo?.ToString() ?? "");
        Set(12, props.MasteryRewards?.ToString() ?? "");

        Set(13, 0);
        Set(14, activeStats.RankedRating);
        Set(15, 0);
        Set(16, 0);
        Set(17, 0);
        Set(18, activeStats.RankedRating);
        Set(19, 0);
        Set(20, 0);

        Set(21, generic.Kills);
        Set(22, 0);
        Set(23, generic.RankedExp + generic.CasualExp);
        Set(24, 0);
        Set(25, generic.Assists);
        Set(26, generic.Deaths);
        Set(27, 0);
        Set(28, 0);
        Set(29, 0);
        Set(30, 0);
        Set(31, 0);
        Set(32, 0);
        Set(33, 0);
        Set(34, 0);
        Set(35, 0);
        Set(36, 0);
        Set(37, 0);
        Set(38, 0);
        Set(39, 0);
        Set(40, 0);
        Set(41, 0);
        Set(42, 0);
        Set(43, generic.RankedGold + generic.CasualGold);
        Set(44, 0);
        Set(45, generic.RankedExp + generic.CasualExp);
        Set(46, 0);
        Set(47, generic.RankedSeconds + generic.CasualSeconds);
        Set(48, 0);
        Set(49, 0);
        Set(50, 0);
        Set(51, 0);

        Set(52, props.LastActivityDate);
        string packedMatchIds = string.Empty;
        string packedMatchDates = string.Empty;
        List<int> recentIds = generic.RecentMatchIDs.Count > 0
            ? generic.RecentMatchIDs
            : simpleStats?.RecentMatchIDs ?? [];
        List<DateTimeOffset> recentDates = generic.RecentMatchDates.Count > 0
            ? generic.RecentMatchDates
            : simpleStats?.RecentMatchDates ?? [];
        if (recentIds.Count > 0)
        {
            StringBuilder idsSb = new();
            StringBuilder datesSb = new();
            for (int i = 0; i < recentIds.Count && i < 20; i++)
            {
                idsSb.Append(recentIds[i].ToString(CultureInfo.InvariantCulture).PadRight(10));
                DateTimeOffset dt = i < recentDates.Count ? recentDates[i] : TimeProvider.System.GetUtcNow();
                datesSb.Append(dt.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture).PadRight(10));
            }

            packedMatchIds = idsSb.ToString();
            packedMatchDates = datesSb.ToString();
        }

        Set(53, packedMatchIds);
        Set(54, packedMatchDates);

        long matches = 0, kills = 0, deaths = 0, assists = 0, secs = 0, exp = 0;
        int denies = 0, actions = 0, wards = 0, creeps = 0, neutrals = 0;
        long firstBloods = 0, doubleKills = 0, tripleKills = 0, quadKills = 0, annihilations = 0;
        long ks3 = 0, ks4 = 0, ks5 = 0, ks6 = 0, ks7 = 0, ks8 = 0, ks9 = 0, ks10 = 0, ks15 = 0;
        long smackdowns = 0, humiliations = 0, nemesis = 0, retribution = 0;

        PlayerStatisticsAggregatedDTO? filterStats = classification.Kind is MatchFilterKind.Quickmatch
            ? (props.SimpleCasualSeasonStats?.GenericStats ?? simpleStats)
            : simpleStats;

        if (filterStats != null)
        {
            if (classification.Kind is MatchFilterKind.AllPublic or MatchFilterKind.PublicMap)
            {
                matches = filterStats.PublicMatches > 0 ? filterStats.PublicMatches :
                    filterStats.CasualMatches > 0 ? filterStats.CasualMatches : filterStats.TotalMatches;
                kills = filterStats.PublicKills > 0 ? filterStats.PublicKills :
                    filterStats.CasualKills > 0 ? filterStats.CasualKills : filterStats.Kills;
                deaths = filterStats.PublicDeaths > 0 ? filterStats.PublicDeaths :
                    filterStats.CasualDeaths > 0 ? filterStats.CasualDeaths : filterStats.Deaths;
                assists = filterStats.PublicAssists > 0 ? filterStats.PublicAssists :
                    filterStats.CasualAssists > 0 ? filterStats.CasualAssists : filterStats.Assists;
                secs = filterStats.PublicSeconds > 0 ? filterStats.PublicSeconds :
                    filterStats.CasualSeconds > 0 ? filterStats.CasualSeconds :
                    filterStats.RankedSeconds + filterStats.CasualSeconds;
                exp = filterStats.PublicExp > 0 ? filterStats.PublicExp :
                    filterStats.CasualExp > 0 ? filterStats.CasualExp : filterStats.RankedExp + filterStats.CasualExp;
                denies = filterStats.PublicDenies > 0 ? filterStats.PublicDenies :
                    filterStats.CasualDenies > 0 ? filterStats.CasualDenies : filterStats.RankedDenies;
                actions = filterStats.PublicActions > 0 ? filterStats.PublicActions :
                    filterStats.CasualActions > 0 ? filterStats.CasualActions : filterStats.RankedActions;
                wards = filterStats.PublicWards > 0 ? filterStats.PublicWards :
                    filterStats.CasualWards > 0 ? filterStats.CasualWards : filterStats.RankedWards;
                creeps = filterStats.PublicTeamCreepKills > 0 ? filterStats.PublicTeamCreepKills :
                    filterStats.CasualTeamCreepKills > 0 ? filterStats.CasualTeamCreepKills :
                    filterStats.RankedTeamCreepKills;
                neutrals = filterStats.PublicNeutralCreepKills > 0 ? filterStats.PublicNeutralCreepKills :
                    filterStats.CasualNeutralCreepKills > 0 ? filterStats.CasualNeutralCreepKills :
                    filterStats.RankedNeutralCreepKills;
                firstBloods = filterStats.PublicFirstBloods > 0 ? filterStats.PublicFirstBloods :
                    filterStats.CasualFirstBloods > 0 ? filterStats.CasualFirstBloods : filterStats.RankedFirstBloods;
                doubleKills = filterStats.PublicDoubleKills > 0 ? filterStats.PublicDoubleKills :
                    filterStats.CasualDoubleKills > 0 ? filterStats.CasualDoubleKills : filterStats.RankedDoubleKills;
                tripleKills = filterStats.PublicTripleKills > 0 ? filterStats.PublicTripleKills :
                    filterStats.CasualTripleKills > 0 ? filterStats.CasualTripleKills : filterStats.RankedTripleKills;
                quadKills = filterStats.PublicQuadKills > 0 ? filterStats.PublicQuadKills :
                    filterStats.CasualQuadKills > 0 ? filterStats.CasualQuadKills : filterStats.RankedQuadKills;
                annihilations = filterStats.PublicAnnihilations > 0 ? filterStats.PublicAnnihilations :
                    filterStats.CasualAnnihilations > 0 ? filterStats.CasualAnnihilations :
                    filterStats.RankedAnnihilations;
                ks3 = filterStats.PublicKS3 > 0 ? filterStats.PublicKS3 :
                    filterStats.CasualKS3 > 0 ? filterStats.CasualKS3 : filterStats.RankedKS3;
                ks4 = filterStats.PublicKS4 > 0 ? filterStats.PublicKS4 :
                    filterStats.CasualKS4 > 0 ? filterStats.CasualKS4 : filterStats.RankedKS4;
                ks5 = filterStats.PublicKS5 > 0 ? filterStats.PublicKS5 :
                    filterStats.CasualKS5 > 0 ? filterStats.CasualKS5 : filterStats.RankedKS5;
                ks6 = filterStats.PublicKS6 > 0 ? filterStats.PublicKS6 :
                    filterStats.CasualKS6 > 0 ? filterStats.CasualKS6 : filterStats.RankedKS6;
                ks7 = filterStats.PublicKS7 > 0 ? filterStats.PublicKS7 :
                    filterStats.CasualKS7 > 0 ? filterStats.CasualKS7 : filterStats.RankedKS7;
                ks8 = filterStats.PublicKS8 > 0 ? filterStats.PublicKS8 :
                    filterStats.CasualKS8 > 0 ? filterStats.CasualKS8 : filterStats.RankedKS8;
                ks9 = filterStats.PublicKS9 > 0 ? filterStats.PublicKS9 :
                    filterStats.CasualKS9 > 0 ? filterStats.CasualKS9 : filterStats.RankedKS9;
                ks10 = filterStats.PublicKS10 > 0 ? filterStats.PublicKS10 :
                    filterStats.CasualKS10 > 0 ? filterStats.CasualKS10 : filterStats.RankedKS10;
                ks15 = filterStats.PublicKS15 > 0 ? filterStats.PublicKS15 :
                    filterStats.CasualKS15 > 0 ? filterStats.CasualKS15 : filterStats.RankedKS15;
                smackdowns = filterStats.PublicSmackdowns > 0 ? filterStats.PublicSmackdowns :
                    filterStats.CasualSmackdowns > 0 ? filterStats.CasualSmackdowns : filterStats.RankedSmackdowns;
                humiliations = filterStats.PublicHumiliations > 0 ? filterStats.PublicHumiliations :
                    filterStats.CasualHumiliations > 0 ? filterStats.CasualHumiliations :
                    filterStats.RankedHumiliations;
                nemesis = filterStats.PublicNemesis > 0 ? filterStats.PublicNemesis :
                    filterStats.CasualNemesis > 0 ? filterStats.CasualNemesis : filterStats.RankedNemesis;
                retribution = filterStats.PublicRetribution > 0 ? filterStats.PublicRetribution :
                    filterStats.CasualRetribution > 0 ? filterStats.CasualRetribution : filterStats.RankedRetribution;
            }
            else if (classification.Kind is MatchFilterKind.Quickmatch)
            {
                matches = filterStats.CasualMatches > 0 ? filterStats.CasualMatches :
                    filterStats.RankedMatches > 0 ? filterStats.RankedMatches :
                    activeStats.RankedMatchesWon + activeStats.RankedMatchesLost;
                kills = filterStats.CasualKills > 0 ? filterStats.CasualKills : filterStats.RankedKills;
                deaths = filterStats.CasualDeaths > 0 ? filterStats.CasualDeaths : filterStats.RankedDeaths;
                assists = filterStats.CasualAssists > 0 ? filterStats.CasualAssists : filterStats.RankedAssists;
                secs = filterStats.CasualSeconds > 0 ? filterStats.CasualSeconds : filterStats.RankedSeconds;
                exp = filterStats.CasualExp > 0 ? filterStats.CasualExp : filterStats.RankedExp;
                denies = filterStats.CasualDenies > 0 ? filterStats.CasualDenies : filterStats.RankedDenies;
                actions = filterStats.CasualActions > 0 ? filterStats.CasualActions : filterStats.RankedActions;
                wards = filterStats.CasualWards > 0 ? filterStats.CasualWards : filterStats.RankedWards;
                creeps = filterStats.CasualTeamCreepKills > 0 ? filterStats.CasualTeamCreepKills : filterStats.RankedTeamCreepKills;
                neutrals = filterStats.CasualNeutralCreepKills > 0 ? filterStats.CasualNeutralCreepKills : filterStats.RankedNeutralCreepKills;
                firstBloods = filterStats.CasualFirstBloods > 0 ? filterStats.CasualFirstBloods : filterStats.RankedFirstBloods;
                doubleKills = filterStats.CasualDoubleKills > 0 ? filterStats.CasualDoubleKills : filterStats.RankedDoubleKills;
                tripleKills = filterStats.CasualTripleKills > 0 ? filterStats.CasualTripleKills : filterStats.RankedTripleKills;
                quadKills = filterStats.CasualQuadKills > 0 ? filterStats.CasualQuadKills : filterStats.RankedQuadKills;
                annihilations = filterStats.CasualAnnihilations > 0 ? filterStats.CasualAnnihilations : filterStats.RankedAnnihilations;
                ks3 = filterStats.CasualKS3 > 0 ? filterStats.CasualKS3 : filterStats.RankedKS3;
                ks4 = filterStats.CasualKS4 > 0 ? filterStats.CasualKS4 : filterStats.RankedKS4;
                ks5 = filterStats.CasualKS5 > 0 ? filterStats.CasualKS5 : filterStats.RankedKS5;
                ks6 = filterStats.CasualKS6 > 0 ? filterStats.CasualKS6 : filterStats.RankedKS6;
                ks7 = filterStats.CasualKS7 > 0 ? filterStats.CasualKS7 : filterStats.RankedKS7;
                ks8 = filterStats.CasualKS8 > 0 ? filterStats.CasualKS8 : filterStats.RankedKS8;
                ks9 = filterStats.CasualKS9 > 0 ? filterStats.CasualKS9 : filterStats.RankedKS9;
                ks10 = filterStats.CasualKS10 > 0 ? filterStats.CasualKS10 : filterStats.RankedKS10;
                ks15 = filterStats.CasualKS15 > 0 ? filterStats.CasualKS15 : filterStats.RankedKS15;
                smackdowns = filterStats.CasualSmackdowns > 0 ? filterStats.CasualSmackdowns : filterStats.RankedSmackdowns;
                humiliations = filterStats.CasualHumiliations > 0 ? filterStats.CasualHumiliations : filterStats.RankedHumiliations;
                nemesis = filterStats.CasualNemesis > 0 ? filterStats.CasualNemesis : filterStats.RankedNemesis;
                retribution = filterStats.CasualRetribution > 0 ? filterStats.CasualRetribution : filterStats.RankedRetribution;
            }
            else
            {
                matches = filterStats.RankedMatches;
                kills = filterStats.RankedKills;
                deaths = filterStats.RankedDeaths;
                assists = filterStats.RankedAssists;
                secs = filterStats.RankedSeconds;
                exp = filterStats.RankedExp;
                denies = filterStats.RankedDenies;
                actions = filterStats.RankedActions;
                wards = filterStats.RankedWards;
                creeps = filterStats.RankedTeamCreepKills;
                neutrals = filterStats.RankedNeutralCreepKills;
                firstBloods = filterStats.RankedFirstBloods;
                doubleKills = filterStats.RankedDoubleKills;
                tripleKills = filterStats.RankedTripleKills;
                quadKills = filterStats.RankedQuadKills;
                annihilations = filterStats.RankedAnnihilations;
                ks3 = filterStats.RankedKS3;
                ks4 = filterStats.RankedKS4;
                ks5 = filterStats.RankedKS5;
                ks6 = filterStats.RankedKS6;
                ks7 = filterStats.RankedKS7;
                ks8 = filterStats.RankedKS8;
                ks9 = filterStats.RankedKS9;
                ks10 = filterStats.RankedKS10;
                ks15 = filterStats.RankedKS15;
                smackdowns = filterStats.RankedSmackdowns;
                humiliations = filterStats.RankedHumiliations;
                nemesis = filterStats.RankedNemesis;
                retribution = filterStats.RankedRetribution;
            }
        }

        string kda = "0.00:1";
        if (deaths > 0)
            kda = $"{(double)(kills + assists) / deaths:0.00}:1";
        else if (kills + assists > 0) kda = $"{kills + assists}:0";

        int discos = 0;
        double ratingOffset = 0.0;
        long heroDamage = 0;
        long heroGold = 0;

        if (filterStats != null)
        {
            discos = filterStats.PublicDiscos > 0
                ? filterStats.PublicDiscos
                : filterStats.CasualDiscos > 0
                    ? filterStats.CasualDiscos
                    : filterStats.Disconnected;

            ratingOffset = filterStats.PublicRatingChange != 0
                ? filterStats.PublicRatingChange
                : filterStats.CasualRatingChange != 0
                    ? filterStats.CasualRatingChange
                    : filterStats.RankedRatingChange;

            heroDamage = filterStats.PublicHeroDamage > 0
                ? filterStats.PublicHeroDamage
                : filterStats.CasualHeroDamage > 0
                    ? filterStats.CasualHeroDamage
                    : filterStats.RankedHeroDamage;

            heroGold = filterStats.RankedHeroGold + filterStats.CasualHeroGold + filterStats.PublicHeroGold;
        }

        string formattedRating = (1500.0 + ratingOffset).ToString(CultureInfo.InvariantCulture);

        Set(6, matches);
        Set(7, activeStats.RankedMatchesWon);
        Set(8, activeStats.RankedMatchesLost);
        Set(13, discos);
        Set(14, formattedRating);
        Set(15, matches);
        Set(16, "1500.000");
        Set(17, 0);
        Set(18, formattedRating);
        Set(19, 0);
        Set(20, 0);

        Set(21, kills);
        Set(22, heroDamage);
        Set(23, exp);
        Set(24, heroGold);
        Set(25, assists);
        Set(26, deaths);
        Set(27, 0);
        Set(28, 0);
        Set(29, creeps);
        Set(30, 0);
        Set(31, 0);
        Set(32, 0);
        Set(33, neutrals);
        Set(34, 0);
        Set(35, 0);
        Set(36, 0);
        Set(37, 0);
        Set(38, 0);
        Set(39, 0);
        Set(40, 0);
        Set(41, denies);
        Set(42, 0);
        Set(44, 0);
        Set(45, exp);
        Set(46, actions);
        Set(47, secs);
        Set(48, 0);
        Set(49, wards);
        Set(50, 0);
        Set(51, 0);

        long minutes = secs / 60;
        string avgCreepKillsStr = avg(creeps, matches).ToString("0.0", CultureInfo.InvariantCulture);
        string avgDeniesStr = avg(denies, matches).ToString("0.0", CultureInfo.InvariantCulture);
        string avgXpMinStr = avg(exp, minutes).ToString("0.0", CultureInfo.InvariantCulture);

        long gold = 0;
        if (filterStats != null)
        {
            if (classification.Kind is MatchFilterKind.Quickmatch || table is "casual" or "campaign_casual" or "midwars")
                gold = filterStats.CasualGold > 0 ? filterStats.CasualGold : filterStats.RankedGold;
            else if (table is "player" or "public")
                gold = filterStats.PublicGold > 0 ? filterStats.PublicGold :
                    filterStats.CasualGold > 0 ? filterStats.CasualGold : filterStats.RankedGold;
            else
                gold = filterStats.RankedGold;
        }

        Set(43, gold);

        string avgGoldMinStr = avg(gold, minutes).ToString("0.0", CultureInfo.InvariantCulture);
        string avgActionsMinStr = avg(actions, minutes).ToString("0.0", CultureInfo.InvariantCulture);
        string avgWardsUsedStr = avg(wards, matches).ToString("0.0", CultureInfo.InvariantCulture);
        string avgGameLengthStr =
            matches > 0 ? ((float)secs / matches).ToString("0.0", CultureInfo.InvariantCulture) : "0";
        string avgNeutralKillsStr = avg(neutrals, matches).ToString("0.0", CultureInfo.InvariantCulture);

        hybridData["avgCreepKills"] = avgCreepKillsStr;
        hybridData["avgDenies"] = avgDeniesStr;
        hybridData["avgXP_min"] = avgXpMinStr;
        hybridData["avgGold_min"] = avgGoldMinStr;
        hybridData["avgActions_min"] = avgActionsMinStr;
        hybridData["avgWardsUsed"] = avgWardsUsedStr;
        hybridData["k_d_a"] = kda;
        hybridData["avgGameLength"] = avgGameLengthStr;
        hybridData["avgNeutralKills"] = avgNeutralKillsStr;

        hybridData["herokills"] = kills.ToString();
        hybridData["heroassists"] = assists.ToString();
        hybridData["deaths"] = deaths.ToString();
        hybridData["gold"] = gold.ToString();
        hybridData["exp"] = exp.ToString();
        hybridData["secs"] = secs.ToString();

        hybridData["ks3"] = ks3.ToString();
        hybridData["ks4"] = ks4.ToString();
        hybridData["ks5"] = ks5.ToString();
        hybridData["ks6"] = ks6.ToString();
        hybridData["ks7"] = ks7.ToString();
        hybridData["ks8"] = ks8.ToString();
        hybridData["ks9"] = ks9.ToString();
        hybridData["ks10"] = ks10.ToString();
        hybridData["ks15"] = ks15.ToString();
        hybridData["doublekill"] = doubleKills.ToString();
        hybridData["triplekill"] = tripleKills.ToString();
        hybridData["quadkill"] = quadKills.ToString();
        hybridData["annihilation"] = annihilations.ToString();
        hybridData["bloodlust"] = firstBloods.ToString();

        hybridData["smackdown"] = smackdowns.ToString();
        hybridData["humiliation"] = humiliations.ToString();
        hybridData["nemesis"] = nemesis.ToString();
        hybridData["retribution"] = retribution.ToString();

        long currentLevel = props.Level;
        long currentLevelMinXp = (long)(100 * Math.Pow(currentLevel, 3));
        long nextLevelMinXp = (long)(100 * Math.Pow(currentLevel + 1, 3));

        double percent = 0.0;
        if (props.LevelExperience >= currentLevelMinXp && nextLevelMinXp > currentLevelMinXp)
        {
            long xpIntoLevel = props.LevelExperience - currentLevelMinXp;
            long xpNeeded = nextLevelMinXp - currentLevelMinXp;
            percent = xpIntoLevel / (double)xpNeeded;
            if (percent > 1.0) percent = 1.0;

            if (percent < 0.0) percent = 0.0;
        }

        string pctStr = (percent * 100.0).ToString("0.00", CultureInfo.InvariantCulture);

        Set(55, props.FavHero1);
        Set(56, props.FavHero2);
        Set(57, props.FavHero3);
        Set(58, props.FavHero4);
        Set(59, props.FavHero5);
        Set(60, props.FavHero1Time);
        Set(61, props.FavHero2Time);
        Set(62, props.FavHero3Time);
        Set(63, props.FavHero4Time);
        Set(64, props.FavHero5Time);

        Set(65, nextLevelMinXp.ToString());
        Set(66, pctStr);
        Set(67, "0");
        Set(68, kda);
        Set(69, avgGameLengthStr);
        Set(70, avgXpMinStr);
        Set(71, avgDeniesStr);
        Set(72, avgCreepKillsStr);
        Set(73, avgNeutralKillsStr);
        Set(74, avgActionsMinStr);
        Set(75, avgWardsUsedStr);
        Set(76, props.AccountCreationDate);
        Set(77, props.FavHero1_2);
        Set(78, props.FavHero2_2);
        Set(79, props.FavHero3_2);
        Set(80, props.FavHero4_2);
        Set(81, props.FavHero5_2);
        Set(82, props.FavHero1);
        Set(83, props.FavHero2);
        Set(84, props.FavHero3);
        Set(85, props.FavHero4);

        Set(86, props.FavHero5);
        Set(87, string.IsNullOrWhiteSpace(props.CurrentRankTop) ? "0" : props.CurrentRankTop);
        Set(88, props.SeasonLevel.ToString());
        Set(89, string.Join("|", props.SelectedStoreItems));
        Set(90, totalAllMatches);
        Set(91, generic.RankedMatches);
        Set(92, totalAllDiscos);
        Set(93, generic.CasualDiscos);

        Set(94, firstBloods.ToString());
        Set(95, doubleKills.ToString());
        Set(96, tripleKills.ToString());
        Set(97, quadKills.ToString());
        Set(98, annihilations.ToString());
        Set(99, ks3.ToString());
        Set(100, ks4.ToString());
        Set(101, ks5.ToString());

        Set(102, ks6.ToString());
        Set(103, ks7.ToString());
        Set(104, ks8.ToString());
        Set(105, ks9.ToString());
        Set(106, ks10.ToString());
        Set(107, ks15.ToString());
        Set(108, smackdowns.ToString());
        Set(109, humiliations.ToString());
        Set(110, nemesis.ToString());
        Set(111, retribution.ToString());

        Set(112, props.LevelExperience.ToString());
        Set(113,
            (generic.RankedTimeEarningExp + generic.CasualTimeEarningExp + generic.PublicTimeEarningExp).ToString());
        string rankBadgeId = string.IsNullOrWhiteSpace(props.CurrentRankTop) ? "0" : props.CurrentRankTop;

        if (table is "player" or "public")
        {
            Set(114, props.Level);
            hybridData["level"] = props.Level.ToString();
        }
        else
        {
            Set(114, rankBadgeId);
            hybridData["level"] = rankBadgeId;
            hybridData["current_level"] = rankBadgeId;
        }

        Set(115, props.LevelExperience);
        Set(116, totalAllDiscos);
        Set(117, "0");
        Set(118, totalAllMatches);
        Set(119, "0");
        Set(120, "0");
        Set(121, pctStr);
        hybridData["level_percent"] = pctStr;
        Set(122, nextLevelMinXp.ToString());
        Set(123, currentLevelMinXp.ToString());
        Set(124, "0");
        Set(125, "0");
        Set(126, totalAllMatches);
        Set(127, totalAllDiscos);
        Set(128, "0");
        Set(129, "0");
        Set(130, generic.CasualDiscos.ToString());
        Set(131, generic.RankedDiscos.ToString());
        Set(132, generic.CasualMatches.ToString());
        Set(133, generic.RankedMatches.ToString());
        Set(134, "0");
        Set(135, "0");

        hybridData["rnk_wins"] = stats.RankedMatchesWon.ToString();
        hybridData["rnk_losses"] = stats.RankedMatchesLost.ToString();
        hybridData["rnk_games_played"] = (stats.RankedMatchesWon + stats.RankedMatchesLost).ToString();
        hybridData["acc_wins"] = stats.RankedMatchesWon.ToString();
        hybridData["acc_games_played"] = (stats.RankedMatchesWon + stats.RankedMatchesLost).ToString();

        if (table == "player")
        {
            hybridData["wins"] = simpleStats?.PublicWins.ToString() ?? "0";
            hybridData["losses"] = simpleStats?.PublicLosses.ToString() ?? "0";
            hybridData["matches"] = simpleStats?.PublicMatches.ToString() ?? "0";
            hybridData["total_games_played"] = totalAllMatches.ToString();
            hybridData["acc_games_played"] = simpleStats?.PublicMatches.ToString() ?? "0";
            hybridData["acc_wins"] = simpleStats?.PublicWins.ToString() ?? "0";
            hybridData["acc_losses"] = simpleStats?.PublicLosses.ToString() ?? "0";
            hybridData["acc_discos"] = (simpleStats?.PublicDiscos ?? 0).ToString();
            hybridData["acc_pub_skill"] =
                (1500.0 + (simpleStats?.PublicRatingChange ?? 0)).ToString(CultureInfo.InvariantCulture);

            Set(6, simpleStats?.PublicMatches ?? 0);
            Set(7, simpleStats?.PublicWins ?? 0);
            Set(8, simpleStats?.PublicLosses ?? 0);
            Set(12, simpleStats?.PublicDiscos ?? 0);
            Set(14, (1500.0 + (simpleStats?.PublicRatingChange ?? 0)).ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            hybridData["wins"] = activeStats.RankedMatchesWon.ToString();
            hybridData["losses"] = activeStats.RankedMatchesLost.ToString();
        }

        SimpleSeasonStats? casualStats = props.SimpleCasualSeasonStats;
        hybridData["cs_wins"] = (casualStats?.RankedMatchesWon ?? 0).ToString();
        hybridData["cs_losses"] = (casualStats?.RankedMatchesLost ?? 0).ToString();
        hybridData["cs_games_played"] =
            ((casualStats?.RankedMatchesWon ?? 0) + (casualStats?.RankedMatchesLost ?? 0)).ToString();

        Set(136, string.IsNullOrWhiteSpace(props.CurrentRankTop) ? "0" : props.CurrentRankTop);
        Set(137, string.IsNullOrWhiteSpace(props.HighestRankTop) ? "0" : props.HighestRankTop);
        Set(138, props.CurrentSeason.ToString());

        Set(139, stats.RankedRating);
        Set(140, stats.RankedMatchesWon + stats.RankedMatchesLost);
        Set(141, (casualStats?.RankedMatchesWon ?? 0) + (casualStats?.RankedMatchesLost ?? 0));
        Set(142, generic.RankedDiscos);
        Set(143, generic.CasualDiscos);
        Set(144, "0");
        Set(145, "0");
        Set(146, "0");
        Set(147, "0");
        Set(148, string.IsNullOrWhiteSpace(props.HighestRankTop) ? "0" : props.HighestRankTop);
        Set(149, props.ConReward ?? "");

        return hybridData;

        void Set(int luaIndex, object? value)
        {
            int phpIndex = luaIndex - 1;

            if (value == null)
            {
                hybridData[phpIndex] = "0";
                return;
            }

            if (value is bool b)
            {
                hybridData[phpIndex] = b ? "1" : "0";
                return;
            }

            if (value is int or long)
            {
                hybridData[phpIndex] = value;
                return;
            }

            if (value is string or double or float or decimal)
            {
                hybridData[phpIndex] = value.ToString()!;
                return;
            }

            hybridData[phpIndex] = ConvertToPhp(value);
        }

        static double avg(long total, long divisor)
        {
            return divisor > 0 ? (double)total / divisor : 0.0;
        }
    }

    public object ConvertToPhp(object? value)
    {
        if (value == null) return "0";

        if (value is string or int or long or float or double or bool or decimal)
        {
            if (value is bool b) return b ? "1" : "0";

            return value.ToString()!;
        }

        if (value is JsonElement je)
            switch (je.ValueKind)
            {
                case JsonValueKind.Object:
                    Dictionary<object, object> hash = [];
                    foreach (JsonProperty prop in je.EnumerateObject())
                    {
                        object k = int.TryParse(prop.Name, out int i) ? i : prop.Name;
                        hash[k] = ConvertToPhp(prop.Value);
                    }

                    return hash;
                case JsonValueKind.Array:
                    ArrayList arr = [];
                    foreach (JsonElement el in je.EnumerateArray()) arr.Add(ConvertToPhp(el));

                    return arr;
                case JsonValueKind.String:
                    return je.GetString() ?? "0";
                case JsonValueKind.Number:
                    return je.GetRawText();
                case JsonValueKind.True: return "1";
                case JsonValueKind.False:
                default: return "0";
            }

        Type type = value.GetType();
        bool isDictionary = typeof(IDictionary).IsAssignableFrom(type) ||
                            (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>));

        if (isDictionary)
        {
            Dictionary<object, object> phpDict = [];
            IEnumerable enumerableDict = (IEnumerable)value;
            foreach (object entry in enumerableDict)
            {
                object? k = null;
                object? v = null;

                if (entry is DictionaryEntry de)
                {
                    k = de.Key;
                    v = de.Value;
                }
                else
                {
                    Type entryType = entry.GetType();
                    k = entryType.GetProperty("Key")?.GetValue(entry, null);
                    v = entryType.GetProperty("Value")?.GetValue(entry, null);
                }

                if (k != null)
                {
                    object parsedKey = k;
                    if (k is string strKey && int.TryParse(strKey, out int intK))
                        parsedKey = intK;
                    else if (k is string sKey) parsedKey = sKey;

                    phpDict[parsedKey] = ConvertToPhp(v);
                }
            }

            return phpDict;
        }

        if (value is IEnumerable list and not string)
        {
            ArrayList phpList = [];
            foreach (object? item in list) phpList.Add(ConvertToPhp(item));

            return phpList;
        }

        if (type != typeof(object))
        {
            CachedPropertyAccessor[] accessors = _propertyAccessorCache.GetOrAdd(type, static t => BuildPropertyAccessors(t));
            if (accessors.Length > 0)
            {
                Dictionary<object, object> objDict = new(accessors.Length);
                for (int i = 0; i < accessors.Length; i++)
                {
                    CachedPropertyAccessor acc = accessors[i];
                    object? propVal = acc.Getter(value);
                    objDict[acc.Key] = ConvertToPhp(propVal);
                }

                return objDict;
            }
        }

        return value.ToString()!;
    }

    private sealed class CachedPropertyAccessor
    {
        public required object Key { get; init; }
        public required Func<object, object?> Getter { get; init; }
    }

    private static readonly ConcurrentDictionary<Type, CachedPropertyAccessor[]> _propertyAccessorCache = new();

    [UnconditionalSuppressMessage("Trimming", "IL2070:TargetWithPublicProperties",
        Justification = "Legacy dynamic protocol formatter fallback")]
    private static CachedPropertyAccessor[] BuildPropertyAccessors(Type type)
    {
        PropertyInfo[] props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        if (props.Length == 0 || type == typeof(object)) return [];

        List<CachedPropertyAccessor> accessors = new(props.Length);
        foreach (PropertyInfo prop in props)
        {
            if (!prop.CanRead) continue;

            PHPPropertyAttribute? attr = prop.GetCustomAttribute<PHPPropertyAttribute>();
            object key = attr?.PropertyKey.Value ?? prop.Name;
            if (key is string strKey && int.TryParse(strKey, out int intKey)) key = intKey;

            MethodInfo? getter = prop.GetGetMethod();
            Func<object, object?> getterDelegate;
            if (getter != null)
            {
                try
                {
                    Type declaringType = prop.DeclaringType ?? getter.DeclaringType ?? typeof(object);
                    ParameterExpression instanceParam = Expression.Parameter(typeof(object), "instance");
                    UnaryExpression castInstance = Expression.Convert(instanceParam, declaringType);
                    MemberExpression propertyAccess = Expression.Property(castInstance, prop);
                    UnaryExpression castResult = Expression.Convert(propertyAccess, typeof(object));
                    getterDelegate = Expression.Lambda<Func<object, object?>>(castResult, instanceParam).Compile();
                }
                catch
                {
                    getterDelegate = prop.GetValue;
                }
            }
            else
            {
                getterDelegate = prop.GetValue;
            }

            accessors.Add(new CachedPropertyAccessor
            {
                Key = key,
                Getter = getterDelegate
            });
        }

        return accessors.ToArray();
    }
}
