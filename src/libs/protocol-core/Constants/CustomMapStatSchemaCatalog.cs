// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ASPIRE.Common.Constants;

[JsonSerializable(typeof(Dictionary<string, double>))]
[JsonSerializable(typeof(Dictionary<string, int>))]
public sealed partial class CustomMapStatsJsonContext : JsonSerializerContext;

public enum CustomStatAggregation
{
    Sum = 0,
    Max = 1,
    Last = 2,
    RatioPercent = 3
}

public enum CustomStatFormat
{
    Int = 0,
    Float1 = 1,
    Percent1 = 2,
    DurationSeconds = 3,
    DifficultyCode = 4
}

public sealed record CustomStatFieldSpec(
    string Key,
    int? GameplayStatIndex,
    string DisplayLabel,
    string ShortHeader,
    CustomStatFormat Format = CustomStatFormat.Int,
    CustomStatAggregation Aggregation = CustomStatAggregation.Sum,
    bool ShowOnScoreboard = true,
    bool ShowOnProfileCard = true,
    bool HigherIsBetter = true,
    string? NumeratorKey = null,
    string? DenominatorKey = null,
    string? BuiltinMetricKey = null);

public sealed record CustomMapStatSchema(
    string SchemaKey,
    short SchemaVersion,
    string TeamScoreLabel,
    int DefaultTeamScoreGoal,
    bool Is10v10,
    bool IsCoOpPvE,
    string DefaultSortKey,
    IReadOnlyList<CustomStatFieldSpec> Fields);

/// <summary>
///     Single source of truth mapping every shipped Project Kongor map to its native <c>gameplaystat0..9</c>
///     slot definitions, derived accuracy percentages, JSONB custom stats, post-match scoreboard columns,
///     and lifetime player career summary cards.
/// </summary>
public static class CustomMapStatSchemaCatalog
{
    private static readonly CustomMapStatSchema SoccerSchema = new(
        SchemaKey: "soccer",
        SchemaVersion: 1,
        TeamScoreLabel: "Goals",
        DefaultTeamScoreGoal: 10,
        Is10v10: false,
        IsCoOpPvE: false,
        DefaultSortKey: "goals",
        Fields:
        [
            new("goals", 0, "Goals", "GLS", CustomStatFormat.Int, CustomStatAggregation.Sum),
            new("goal_assists", 1, "Goal Assists", "AST", CustomStatFormat.Int, CustomStatAggregation.Sum),
            new("steals", 3, "Steals", "STL", CustomStatFormat.Int, CustomStatAggregation.Sum),
            new("own_goals", 2, "Own Goals", "OG", CustomStatFormat.Int, CustomStatAggregation.Sum, HigherIsBetter: false),
            new("possession_sec", 4, "Possession", "POSS", CustomStatFormat.DurationSeconds, CustomStatAggregation.Sum),
            new("knockouts", 5, "Knockouts", "KO", CustomStatFormat.Int, CustomStatAggregation.Sum),
            new("knocked_out", 6, "Knocked Out", "KO'D", CustomStatFormat.Int, CustomStatAggregation.Sum, ShowOnScoreboard: false, ShowOnProfileCard: false, HigherIsBetter: false),
            new("runes_gathered", 7, "Energy Runes", "RUNE", CustomStatFormat.Int, CustomStatAggregation.Sum, ShowOnScoreboard: false, ShowOnProfileCard: true)
        ]);

    private static readonly CustomMapStatSchema CtfSchema = new(
        SchemaKey: "ctf",
        SchemaVersion: 1,
        TeamScoreLabel: "Captures",
        DefaultTeamScoreGoal: 5,
        Is10v10: false,
        IsCoOpPvE: false,
        DefaultSortKey: "flag_captures",
        Fields:
        [
            new("flag_captures", 2, "Flag Captures", "CAP", CustomStatFormat.Int, CustomStatAggregation.Sum),
            new("flag_returns", 3, "Flag Returns", "RET", CustomStatFormat.Int, CustomStatAggregation.Sum),
            new("carrier_kills", 4, "Carrier Kills", "FCK", CustomStatFormat.Int, CustomStatAggregation.Sum),
            new("flag_distance", 5, "Flag Distance", "DIST", CustomStatFormat.Int, CustomStatAggregation.Sum),
            new("carrier_damage", 6, "Carrier Damage", "CDMG", CustomStatFormat.Int, CustomStatAggregation.Sum)
        ]);

    private static CustomMapStatSchema CreateDevoSchema(bool isTrueDevo, bool is10v10) => new(
        SchemaKey: isTrueDevo ? "truedevo" : "devowars",
        SchemaVersion: 1,
        TeamScoreLabel: isTrueDevo ? "Kills" : "Points",
        DefaultTeamScoreGoal: isTrueDevo ? (is10v10 ? 150 : 88) : (is10v10 ? 100 : 60),
        Is10v10: is10v10,
        IsCoOpPvE: false,
        DefaultSortKey: "hook_hits",
        Fields: isTrueDevo
            ?
            [
                new("hook_hits", 1, "Hooks Landed", "HIT", CustomStatFormat.Int, CustomStatAggregation.Sum),
                new("hook_attempts", 0, "Hooks Thrown", "ATT", CustomStatFormat.Int, CustomStatAggregation.Sum),
                new("hook_accuracy_pct", null, "Hook Accuracy", "HK%", CustomStatFormat.Percent1, CustomStatAggregation.RatioPercent, NumeratorKey: "hook_hits", DenominatorKey: "hook_attempts"),
                new("headshots", 2, "Headshots", "HS", CustomStatFormat.Int, CustomStatAggregation.Sum),
                new("team_lives_left", 3, "Lives Left", "LIFE", CustomStatFormat.Int, CustomStatAggregation.Last, ShowOnScoreboard: false, ShowOnProfileCard: false),
                new("hero_damage", null, "Hero Damage", "HDMG", CustomStatFormat.Int, CustomStatAggregation.Sum, BuiltinMetricKey: "hero_damage")
            ]
            :
            [
                new("hook_hits", 1, "Hooks Landed", "HIT", CustomStatFormat.Int, CustomStatAggregation.Sum),
                new("hook_attempts", 0, "Hooks Thrown", "ATT", CustomStatFormat.Int, CustomStatAggregation.Sum),
                new("hook_accuracy_pct", null, "Hook Accuracy", "HK%", CustomStatFormat.Percent1, CustomStatAggregation.RatioPercent, NumeratorKey: "hook_hits", DenominatorKey: "hook_attempts"),
                new("headshots", 2, "Headshots", "HS", CustomStatFormat.Int, CustomStatAggregation.Sum),
                new("hero_damage", null, "Hero Damage", "HDMG", CustomStatFormat.Int, CustomStatAggregation.Sum, BuiltinMetricKey: "hero_damage")
            ]);

    private static readonly CustomMapStatSchema ValkWarsSchema = new(
        SchemaKey: "valkwars",
        SchemaVersion: 2,
        TeamScoreLabel: "Kills",
        DefaultTeamScoreGoal: 50,
        Is10v10: false,
        IsCoOpPvE: false,
        DefaultSortKey: "javelins_hit",
        Fields:
        [
            new("javelins_hit", 1, "Javelins Landed", "HIT", CustomStatFormat.Int, CustomStatAggregation.Sum),
            new("javelins_thrown", 0, "Javelins Thrown", "ATT", CustomStatFormat.Int, CustomStatAggregation.Sum),
            new("javelin_accuracy_pct", null, "Javelin Accuracy", "JAV%", CustomStatFormat.Percent1, CustomStatAggregation.RatioPercent, NumeratorKey: "javelins_hit", DenominatorKey: "javelins_thrown"),
            new("hero_damage", null, "Hero Damage", "HDMG", CustomStatFormat.Int, CustomStatAggregation.Sum, BuiltinMetricKey: "hero_damage"),
            new("gpm", null, "GPM", "GPM", CustomStatFormat.Int, CustomStatAggregation.Last, ShowOnProfileCard: false, BuiltinMetricKey: "gpm")
        ]);

    private static readonly CustomMapStatSchema ArchersSchema = new(
        SchemaKey: "archers",
        SchemaVersion: 2,
        TeamScoreLabel: "Kills",
        DefaultTeamScoreGoal: 0,
        Is10v10: false,
        IsCoOpPvE: false,
        DefaultSortKey: "arrows_hit",
        Fields:
        [
            new("arrows_hit", 1, "Arrows Landed", "HIT", CustomStatFormat.Int, CustomStatAggregation.Sum),
            new("arrows_shot", 0, "Arrows Shot", "SHOT", CustomStatFormat.Int, CustomStatAggregation.Sum),
            new("arrow_accuracy_pct", null, "Arrow Accuracy", "ARR%", CustomStatFormat.Percent1, CustomStatAggregation.RatioPercent, NumeratorKey: "arrows_hit", DenominatorKey: "arrows_shot"),
            new("hero_damage", null, "Hero Damage", "HDMG", CustomStatFormat.Int, CustomStatAggregation.Sum, BuiltinMetricKey: "hero_damage"),
            new("gpm", null, "GPM", "GPM", CustomStatFormat.Int, CustomStatAggregation.Last, ShowOnProfileCard: false, BuiltinMetricKey: "gpm")
        ]);

    private static readonly CustomMapStatSchema EnfosSchema = new(
        SchemaKey: "enfos",
        SchemaVersion: 1,
        TeamScoreLabel: "Lives Left",
        DefaultTeamScoreGoal: 100,
        Is10v10: false,
        IsCoOpPvE: false,
        DefaultSortKey: "creep_kills",
        Fields:
        [
            new("creep_kills", null, "Wave Creeps Slain", "CREEP", CustomStatFormat.Int, CustomStatAggregation.Sum, BuiltinMetricKey: "creep_kills"),
            new("wave_reached", 0, "Wave Reached", "WAVE", CustomStatFormat.Int, CustomStatAggregation.Max),
            new("difficulty_code", 1, "Difficulty", "DIFF", CustomStatFormat.DifficultyCode, CustomStatAggregation.Last, ShowOnProfileCard: false),
            new("team_lives_left", 3, "Lives Left", "LIFE", CustomStatFormat.Int, CustomStatAggregation.Last),
            new("spellbringer_casts", 4, "Spellbringer", "SB", CustomStatFormat.Int, CustomStatAggregation.Sum),
            new("gold_earned", null, "Gold Earned", "GOLD", CustomStatFormat.Int, CustomStatAggregation.Sum, BuiltinMetricKey: "gold_earned"),
            new("team_leaks", 2, "Creeps Leaked", "LEAK", CustomStatFormat.Int, CustomStatAggregation.Sum, ShowOnScoreboard: false, ShowOnProfileCard: true, HigherIsBetter: false)
        ]);

    private static readonly CustomMapStatSchema ArenaSchema = new(
        SchemaKey: "arena",
        SchemaVersion: 2,
        TeamScoreLabel: "Rounds",
        DefaultTeamScoreGoal: 20,
        Is10v10: false,
        IsCoOpPvE: false,
        DefaultSortKey: "round_wins",
        Fields:
        [
            new("round_wins", 0, "Round Wins", "RW", CustomStatFormat.Int, CustomStatAggregation.Sum),
            new("rounds_played", 1, "Rounds Played", "RND", CustomStatFormat.Int, CustomStatAggregation.Sum),
            new("hero_damage", null, "Hero Damage", "HDMG", CustomStatFormat.Int, CustomStatAggregation.Sum, BuiltinMetricKey: "hero_damage"),
            new("gpm", null, "GPM", "GPM", CustomStatFormat.Int, CustomStatAggregation.Last, ShowOnProfileCard: false, BuiltinMetricKey: "gpm"),
            new("gold_earned", null, "Gold Earned", "GOLD", CustomStatFormat.Int, CustomStatAggregation.Sum, ShowOnScoreboard: false, ShowOnProfileCard: true, BuiltinMetricKey: "gold_earned")
        ]);

    private static readonly CustomMapStatSchema ProphetsSchema = new(
        SchemaKey: "prophets",
        SchemaVersion: 2,
        TeamScoreLabel: "Rounds",
        DefaultTeamScoreGoal: 0,
        Is10v10: false,
        IsCoOpPvE: false,
        DefaultSortKey: "rounds_survived",
        Fields:
        [
            new("rounds_survived", 0, "Rounds Survived", "SURV", CustomStatFormat.Int, CustomStatAggregation.Sum),
            new("rounds_played", 1, "Rounds Played", "RND", CustomStatFormat.Int, CustomStatAggregation.Sum),
            new("hero_damage", null, "Hero Damage", "HDMG", CustomStatFormat.Int, CustomStatAggregation.Sum, BuiltinMetricKey: "hero_damage"),
            new("gpm", null, "GPM", "GPM", CustomStatFormat.Int, CustomStatAggregation.Last, ShowOnProfileCard: false, BuiltinMetricKey: "gpm"),
            new("gold_earned", null, "Gold Earned", "GOLD", CustomStatFormat.Int, CustomStatAggregation.Sum, ShowOnScoreboard: false, ShowOnProfileCard: true, BuiltinMetricKey: "gold_earned")
        ]);

    private static readonly CustomMapStatSchema GrimmHuntSchema = new(
        SchemaKey: "thegrimmhunt",
        SchemaVersion: 1,
        TeamScoreLabel: "Points",
        DefaultTeamScoreGoal: 5000,
        Is10v10: false,
        IsCoOpPvE: false,
        DefaultSortKey: "hunt_points",
        Fields:
        [
            new("hunt_points", 0, "Hunt Points", "PTS", CustomStatFormat.Int, CustomStatAggregation.Sum),
            new("boss_sheep_kills", 1, "Boss Critters", "BOSS", CustomStatFormat.Int, CustomStatAggregation.Sum),
            new("flock_kills", 2, "Flock Slain", "FLK", CustomStatFormat.Int, CustomStatAggregation.Sum),
            new("hero_damage", null, "Hero Damage", "HDMG", CustomStatFormat.Int, CustomStatAggregation.Sum, BuiltinMetricKey: "hero_damage"),
            new("gpm", null, "GPM", "GPM", CustomStatFormat.Int, CustomStatAggregation.Last, ShowOnProfileCard: false, BuiltinMetricKey: "gpm")
        ]);

    private static readonly CustomMapStatSchema TeamDeathmatchSchema = new(
        SchemaKey: "tdm",
        SchemaVersion: 2,
        TeamScoreLabel: "Points",
        DefaultTeamScoreGoal: 0,
        Is10v10: false,
        IsCoOpPvE: false,
        DefaultSortKey: "rounds_survived",
        Fields:
        [
            new("rounds_survived", 0, "Rounds Survived", "SURV", CustomStatFormat.Int, CustomStatAggregation.Sum),
            new("clutch_wins", 1, "Clutch Wins", "CLUTCH", CustomStatFormat.Int, CustomStatAggregation.Sum),
            new("rounds_played", 2, "Rounds Played", "RND", CustomStatFormat.Int, CustomStatAggregation.Sum),
            new("hero_damage", null, "Hero Damage", "HDMG", CustomStatFormat.Int, CustomStatAggregation.Sum, BuiltinMetricKey: "hero_damage")
        ]);

    private static readonly CustomMapStatSchema DuelLivesSchema = new(
        SchemaKey: "duel_lives",
        SchemaVersion: 2,
        TeamScoreLabel: "Lives",
        DefaultTeamScoreGoal: 0,
        Is10v10: false,
        IsCoOpPvE: false,
        DefaultSortKey: "net_life_delta",
        Fields:
        [
            new("net_life_delta", null, "Net Life +/-", "+/-", CustomStatFormat.Int, CustomStatAggregation.Sum, BuiltinMetricKey: "net_life_delta"),
            new("team_lives_left", 0, "Lives Left", "LIFE", CustomStatFormat.Int, CustomStatAggregation.Max),
            new("hero_damage", null, "Hero Damage", "HDMG", CustomStatFormat.Int, CustomStatAggregation.Sum, BuiltinMetricKey: "hero_damage"),
            new("gpm", null, "GPM", "GPM", CustomStatFormat.Int, CustomStatAggregation.Last, ShowOnProfileCard: false, BuiltinMetricKey: "gpm")
        ]);

    private static readonly CustomMapStatSchema CoopSurvivalSchema = new(
        SchemaKey: "coop_survival",
        SchemaVersion: 1,
        TeamScoreLabel: "Lives",
        DefaultTeamScoreGoal: 0,
        Is10v10: false,
        IsCoOpPvE: true,
        DefaultSortKey: "creep_kills",
        Fields:
        [
            new("creep_kills", null, "Monsters Slain", "MONS", CustomStatFormat.Int, CustomStatAggregation.Sum, BuiltinMetricKey: "creep_kills"),
            new("building_damage", null, "Boss Damage", "BDMG", CustomStatFormat.Int, CustomStatAggregation.Sum, BuiltinMetricKey: "building_damage"),
            new("deaths_taken", 0, "Lives Used", "LOST", CustomStatFormat.Int, CustomStatAggregation.Sum, HigherIsBetter: false),
            new("team_lives_left", 1, "Lives Left", "LIFE", CustomStatFormat.Int, CustomStatAggregation.Last),
            new("boss_stage", 2, "Stage Reached", "STG", CustomStatFormat.Int, CustomStatAggregation.Max),
            new("gold_earned", null, "Gold Earned", "GOLD", CustomStatFormat.Int, CustomStatAggregation.Sum, BuiltinMetricKey: "gold_earned")
        ]);

    private static readonly CustomMapStatSchema RiftWarsSchema = new(
        SchemaKey: "riftwars",
        SchemaVersion: 1,
        TeamScoreLabel: "Tickets",
        DefaultTeamScoreGoal: 0,
        Is10v10: false,
        IsCoOpPvE: false,
        DefaultSortKey: "tickets_burned",
        Fields:
        [
            new("tickets_burned", 0, "Tickets Burned", "TKT", CustomStatFormat.Int, CustomStatAggregation.Sum),
            new("hero_damage", null, "Hero Damage", "HDMG", CustomStatFormat.Int, CustomStatAggregation.Sum, BuiltinMetricKey: "hero_damage"),
            new("gpm", null, "GPM", "GPM", CustomStatFormat.Int, CustomStatAggregation.Last, ShowOnProfileCard: false, BuiltinMetricKey: "gpm"),
            new("xpm", null, "XPM", "XPM", CustomStatFormat.Int, CustomStatAggregation.Last, ShowOnProfileCard: false, BuiltinMetricKey: "xpm")
        ]);

    private static CustomMapStatSchema CreateMobaSchema(bool is10v10) => new(
        SchemaKey: is10v10 ? "moba_10v10" : "moba",
        SchemaVersion: 1,
        TeamScoreLabel: "Kills",
        DefaultTeamScoreGoal: 0,
        Is10v10: is10v10,
        IsCoOpPvE: false,
        DefaultSortKey: "gpm",
        Fields:
        [
            new("creep_kills", null, "Creep Kills", "CS", CustomStatFormat.Int, CustomStatAggregation.Sum, BuiltinMetricKey: "creep_kills"),
            new("denies", null, "Denies", "DN", CustomStatFormat.Int, CustomStatAggregation.Sum, BuiltinMetricKey: "denies"),
            new("gpm", null, "Gold / Min", "GPM", CustomStatFormat.Int, CustomStatAggregation.Last, ShowOnProfileCard: true, BuiltinMetricKey: "gpm"),
            new("xpm", null, "XP / Min", "XPM", CustomStatFormat.Int, CustomStatAggregation.Last, ShowOnProfileCard: true, BuiltinMetricKey: "xpm"),
            new("hero_damage", null, "Hero Damage", "HDMG", CustomStatFormat.Int, CustomStatAggregation.Sum, BuiltinMetricKey: "hero_damage"),
            new("wards", null, "Wards Placed", "WARD", CustomStatFormat.Int, CustomStatAggregation.Sum, BuiltinMetricKey: "wards")
        ]);

    private static readonly CustomMapStatSchema Moba5v5Schema = CreateMobaSchema(is10v10: false);
    private static readonly CustomMapStatSchema Moba10v10Schema = CreateMobaSchema(is10v10: true);
    private static readonly CustomMapStatSchema DevoWars5v5Schema = CreateDevoSchema(isTrueDevo: false, is10v10: false);
    private static readonly CustomMapStatSchema DevoWars10v10Schema = CreateDevoSchema(isTrueDevo: false, is10v10: true);
    private static readonly CustomMapStatSchema TrueDevo5v5Schema = CreateDevoSchema(isTrueDevo: true, is10v10: false);
    private static readonly CustomMapStatSchema TrueDevo10v10Schema = CreateDevoSchema(isTrueDevo: true, is10v10: true);

    private static readonly FrozenDictionary<string, CustomMapStatSchema> ByMapSlug =
        new Dictionary<string, CustomMapStatSchema>(StringComparer.OrdinalIgnoreCase)
        {
            ["soccer"] = SoccerSchema,
            ["capturetheflag"] = CtfSchema,
            ["devowars"] = DevoWars5v5Schema,
            ["devowars_10v10"] = DevoWars10v10Schema,
            ["truedevo"] = TrueDevo5v5Schema,
            ["truedevo_10v10"] = TrueDevo10v10Schema,
            ["valkwars"] = ValkWarsSchema,
            ["archers"] = ArchersSchema,
            ["enfos"] = EnfosSchema,
            ["arena"] = ArenaSchema,
            ["prophets"] = ProphetsSchema,
            ["thegrimmhunt"] = GrimmHuntSchema,
            ["team_deathmatch"] = TeamDeathmatchSchema,
            ["pebbles_duel"] = DuelLivesSchema,
            ["colosseum_duel"] = DuelLivesSchema,
            ["empath_maze"] = DuelLivesSchema,
            ["nightmarish_coop"] = CoopSurvivalSchema,
            ["riftwars"] = RiftWarsSchema,
            ["midwars_10v10"] = Moba10v10Schema
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public static CustomMapStatSchema ResolveForMap(string? rawMap, int observedPlayerCount = 0)
    {
        string canonical = MapCatalog.NormalizeSlug(rawMap);
        if (ByMapSlug.TryGetValue(canonical, out CustomMapStatSchema? schema))
        {
            if (observedPlayerCount > 10 && !schema.Is10v10)
            {
                return schema with { Is10v10 = true };
            }

            return schema;
        }

        return observedPlayerCount > 10 || MapCatalog.Is10v10Map(canonical)
            ? Moba10v10Schema
            : Moba5v5Schema;
    }

    public static bool RequiresCustomScoreboard(CustomMapStatSchema schema, int playerCount = 0) =>
        schema.Is10v10 ||
        playerCount > 10 ||
        schema.SchemaKey is not ("moba" or "soccer" or "ctf");

    public static double ExtractSlotValue(
        int slotIndex,
        double s0, double s1, double s2, double s3, double s4,
        double s5, double s6, double s7, double s8, double s9) =>
        slotIndex switch
        {
            0 => s0,
            1 => s1,
            2 => s2,
            3 => s3,
            4 => s4,
            5 => s5,
            6 => s6,
            7 => s7,
            8 => s8,
            9 => s9,
            _ => 0.0
        };

    public static Dictionary<string, double> BuildPlayerCustomStats(
        CustomMapStatSchema schema,
        double s0, double s1, double s2, double s3, double s4,
        double s5, double s6, double s7, double s8, double s9,
        int heroKills, int heroDeaths, int teamCreepKills, int neutralCreepKills,
        int denies, int heroDamage, int buildingDamage, int gold, int experience,
        int wardsPlaced, int secondsPlayed)
    {
        Dictionary<string, double> result = new(StringComparer.Ordinal);
        double mins = Math.Max(secondsPlayed / 60.0, 1.0);

        foreach (CustomStatFieldSpec field in schema.Fields)
        {
            if (field.GameplayStatIndex is int idx)
            {
                double v = ExtractSlotValue(idx, s0, s1, s2, s3, s4, s5, s6, s7, s8, s9);
                if (!double.IsFinite(v)) v = 0.0;
                result[field.Key] = Math.Round(v, 2);
            }
            else if (!string.IsNullOrEmpty(field.BuiltinMetricKey))
            {
                double val = field.BuiltinMetricKey switch
                {
                    "creep_kills" => teamCreepKills + neutralCreepKills,
                    "denies" => denies,
                    "hero_damage" => heroDamage,
                    "building_damage" => buildingDamage,
                    "gold_earned" => gold,
                    "gpm" => Math.Round(gold / mins, 1),
                    "xpm" => Math.Round(experience / mins, 1),
                    "wards" => wardsPlaced,
                    "net_life_delta" => heroKills - heroDeaths,
                    _ => 0.0
                };
                result[field.Key] = val;
            }
        }

        // Compute derived ratio percentages once raw numerators/denominators are present.
        foreach (CustomStatFieldSpec field in schema.Fields)
        {
            if (field.Aggregation == CustomStatAggregation.RatioPercent &&
                !string.IsNullOrEmpty(field.NumeratorKey) &&
                !string.IsNullOrEmpty(field.DenominatorKey))
            {
                double num = result.GetValueOrDefault(field.NumeratorKey);
                double den = result.GetValueOrDefault(field.DenominatorKey);
                result[field.Key] = den > 0.0 ? Math.Round(num * 100.0 / den, 1) : 0.0;
            }
        }

        return result;
    }

    public static string SerializePlayerCustomStatsJson(Dictionary<string, double> values) =>
        JsonSerializer.Serialize(values, CustomMapStatsJsonContext.Default.DictionaryStringDouble);

    public static Dictionary<string, double>? DeserializePlayerCustomStatsJson(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize(json, CustomMapStatsJsonContext.Default.DictionaryStringDouble);

    public static Dictionary<string, int>? DeserializeHeroPlayCountsJson(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize(json, CustomMapStatsJsonContext.Default.DictionaryStringInt32);

    public static string FormatDifficultyCode(double code) =>
        (int)Math.Round(code) switch
        {
            1 => "Hard",
            2 => "Easy",
            _ => "Normal"
        };

    public static string FormatStatValue(double value, CustomStatFormat format)
    {
        if (!double.IsFinite(value))
        {
            return "0";
        }

        return format switch
        {
            CustomStatFormat.Percent1 => $"{value.ToString("0.0", CultureInfo.InvariantCulture)}%",
            CustomStatFormat.Float1 => value.ToString("0.0", CultureInfo.InvariantCulture),
            CustomStatFormat.DurationSeconds => $"{(int)Math.Floor(value / 60.0)}:{((int)Math.Floor(value) % 60):D2}",
            CustomStatFormat.DifficultyCode => FormatDifficultyCode(value),
            _ => ((long)Math.Round(value)).ToString(CultureInfo.InvariantCulture)
        };
    }
}
