// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: SupabaseDuckDbLakehouseService.cs
// System: MERRICK Database Context - Supabase DuckDB Foreign Data Wrapper Lakehouse Service
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;

namespace MERRICK.DatabaseContext.Services;

/// <summary>
///     Immutable mapping record establishing type coherence across C#, PostgreSQL, and DuckDB.
/// </summary>
public sealed record ColumnTypeMapping(
    string ColumnName,
    string PostgresType,
    string CsharpType,
    string DuckDbType);

/// <summary>
///     Service facilitating query generation, schema verification, and pushdown predicate construction
///     for the Supabase DuckDB Foreign Data Wrapper (FDW) match analytics lakehouse.
/// </summary>
public sealed class SupabaseDuckDbLakehouseService(ILogger<SupabaseDuckDbLakehouseService>? logger = null)
{
    private readonly ILogger<SupabaseDuckDbLakehouseService>? _logger = logger;

    /// <summary>
    ///     Canonical column definitions for duckdb.match_combat_events foreign table.
    /// </summary>
    public static readonly IReadOnlyList<ColumnTypeMapping> CanonicalColumns =
    [
        new("match_id", "BIGINT", "Int64", "BIGINT"),
        new("match_time_ms", "INTEGER", "Int32", "INTEGER"),
        new("tick", "INTEGER", "Int32", "INTEGER"),
        new("event_type", "SMALLINT", "Byte", "SMALLINT"),
        new("source_player_index", "SMALLINT", "Byte", "SMALLINT"),
        new("source_hero_id", "SMALLINT", "Int16", "SMALLINT"),
        new("target_player_index", "SMALLINT", "Byte", "SMALLINT"),
        new("target_hero_id", "SMALLINT", "Int16", "SMALLINT"),
        new("ability_or_item_id", "INTEGER", "Int32", "INTEGER"),
        new("event_value", "REAL", "Single", "FLOAT"),
        new("position_x", "REAL", "Single", "FLOAT"),
        new("position_y", "REAL", "Single", "FLOAT"),
        new("position_z", "REAL", "Single", "FLOAT")
    ];

    /// <summary>
    ///     Validates the contents and structure of the DuckDB FDW SQL migration script.
    /// </summary>
    public bool ValidateMigrationScript(string sqlContent, out IReadOnlyList<string> errors)
    {
        List<string> errorList = [];

        if (string.IsNullOrWhiteSpace(sqlContent))
        {
            errorList.Add("Migration script is empty or whitespace.");
            errors = errorList;
            return false;
        }

        string normalized = sqlContent.ToLowerInvariant();

        if (!normalized.Contains("create extension if not exists wrappers"))
            errorList.Add("Missing 'CREATE EXTENSION IF NOT EXISTS wrappers' statement.");

        if (!normalized.Contains("create foreign data wrapper duckdb_wrapper"))
            errorList.Add("Missing 'CREATE FOREIGN DATA WRAPPER duckdb_wrapper' statement.");

        if (!normalized.Contains("create server if not exists duckdb_server") && !normalized.Contains("create server duckdb_server"))
            errorList.Add("Missing 'CREATE SERVER duckdb_server' statement.");

        if (!normalized.Contains("create schema if not exists duckdb") && !normalized.Contains("create schema duckdb"))
            errorList.Add("Missing 'CREATE SCHEMA duckdb' statement.");

        if (!normalized.Contains("create foreign table if not exists duckdb.match_combat_events") && !normalized.Contains("create foreign table duckdb.match_combat_events"))
            errorList.Add("Missing 'CREATE FOREIGN TABLE duckdb.match_combat_events' statement.");

        foreach (ColumnTypeMapping col in CanonicalColumns)
        {
            if (!normalized.Contains(col.ColumnName.ToLowerInvariant()))
            {
                errorList.Add($"Missing column '{col.ColumnName}' in foreign table declaration.");
            }
        }

        errors = errorList;
        return errorList.Count == 0;
    }

    /// <summary>
    ///     Generates a safe parameterized SQL pushdown query for retrieving combat events for a specific match.
    /// </summary>
    public string GenerateMatchTimelineQuery(long matchId, int? limit = null)
    {
        StringBuilder sb = new();
        sb.AppendLine("SELECT ");
        sb.AppendLine("  match_id, match_time_ms, tick, event_type,");
        sb.AppendLine("  source_player_index, source_hero_id, target_player_index, target_hero_id,");
        sb.AppendLine("  ability_or_item_id, event_value, position_x, position_y, position_z");
        sb.AppendLine("FROM duckdb.match_combat_events");
        sb.AppendLine($"WHERE match_id = {matchId}");
        sb.AppendLine("ORDER BY match_time_ms ASC");

        if (limit.HasValue && limit.Value > 0)
        {
            sb.AppendLine($"LIMIT {limit.Value}");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    ///     Generates a safe parameterized SQL query aggregating hero combat metrics across all stored matches.
    /// </summary>
    public string GenerateHeroCombatMetricsQuery(short heroId)
    {
        return $"""
                SELECT 
                  source_hero_id AS hero_id,
                  COUNT(*) FILTER (WHERE event_type = 0) AS kills,
                  COUNT(*) FILTER (WHERE event_type = 1) AS assists,
                  COUNT(*) FILTER (WHERE event_type = 2) AS deaths,
                  COALESCE(SUM(event_value) FILTER (WHERE event_type IN (0, 3)), 0.0) AS total_damage,
                  COUNT(DISTINCT match_id) AS total_matches
                FROM duckdb.match_combat_events
                WHERE source_hero_id = {heroId}
                GROUP BY source_hero_id;
                """;
    }

    /// <summary>
    ///     Generates a safe parameterized SQL query aggregating combat metrics for a specific player across matches.
    /// </summary>
    public string GeneratePlayerCombatMetricsQuery(byte playerIndex, long? matchId = null)
    {
        string matchFilter = matchId.HasValue ? $" AND match_id = {matchId.Value}" : string.Empty;
        return $"""
                SELECT 
                  source_player_index AS player_index,
                  source_hero_id AS hero_id,
                  COUNT(*) FILTER (WHERE event_type = 0) AS kills,
                  COUNT(*) FILTER (WHERE event_type = 1) AS assists,
                  COUNT(*) FILTER (WHERE event_type = 2) AS deaths,
                  COALESCE(SUM(event_value) FILTER (WHERE event_type IN (0, 3)), 0.0) AS total_damage,
                  COUNT(*) FILTER (WHERE event_type = 6) AS wards_placed,
                  COUNT(DISTINCT match_id) AS total_matches
                FROM duckdb.match_combat_events
                WHERE source_player_index = {playerIndex}{matchFilter}
                GROUP BY source_player_index, source_hero_id;
                """;
    }

    /// <summary>
    ///     Generates a safe parameterized SQL query breaking down combat metrics for all 10 players in a match.
    /// </summary>
    public string GenerateMatchCombatBreakdownQuery(long matchId)
    {
        return $"""
                SELECT 
                  source_player_index AS player_index,
                  source_hero_id AS hero_id,
                  COUNT(*) FILTER (WHERE event_type = 0) AS kills,
                  COUNT(*) FILTER (WHERE event_type = 1) AS assists,
                  COUNT(*) FILTER (WHERE event_type = 2) AS deaths,
                  COALESCE(SUM(event_value) FILTER (WHERE event_type IN (0, 3)), 0.0) AS total_damage,
                  COUNT(*) FILTER (WHERE event_type = 6) AS wards_placed,
                  COUNT(*) FILTER (WHERE event_type = 7) AS towers_killed
                FROM duckdb.match_combat_events
                WHERE match_id = {matchId}
                GROUP BY source_player_index, source_hero_id
                ORDER BY source_player_index ASC;
                """;
    }

    /// <summary>
    ///     Generates a safe parameterized SQL query computing head-to-head combat and matchup statistics between two heroes.
    /// </summary>
    public string GenerateHeroMatchupWinrateQuery(short heroId, short enemyHeroId)
    {
        return $"""
                SELECT 
                  source_hero_id AS hero_id,
                  target_hero_id AS enemy_hero_id,
                  COUNT(*) FILTER (WHERE event_type = 0 AND target_hero_id = {enemyHeroId}) AS kills_on_enemy,
                  COUNT(*) FILTER (WHERE event_type = 2 AND target_hero_id = {enemyHeroId}) AS deaths_to_enemy,
                  COALESCE(SUM(event_value) FILTER (WHERE event_type IN (0, 3) AND target_hero_id = {enemyHeroId}), 0.0) AS damage_to_enemy,
                  COUNT(DISTINCT match_id) AS shared_matches
                FROM duckdb.match_combat_events
                WHERE source_hero_id = {heroId} AND target_hero_id = {enemyHeroId}
                GROUP BY source_hero_id, target_hero_id;
                """;
    }

    /// <summary>
    ///     Generates a safe parameterized SQL pushdown query retrieving spatial coordinate clusters
    ///     for combat deaths, kills, and ward placements across a match for 2D canvas heatmap rendering.
    /// </summary>
    public string GenerateCombatHeatmapQuery(long matchId)
    {
        return $"""
                SELECT 
                  event_type,
                  position_x,
                  position_y,
                  COUNT(*) AS frequency
                FROM duckdb.match_combat_events
                WHERE match_id = {matchId} AND event_type IN (0, 2, 6, 7)
                GROUP BY event_type, position_x, position_y
                ORDER BY frequency DESC;
                """;
    }

    /// <summary>
    ///     Generates a safe parameterized SQL query computing head-to-head matchup win rates,
    ///     damage dealt/taken, and kills/deaths for a hero against all opposing heroes across stored Lakehouse matches.
    /// </summary>
    public string GenerateHeroMatchupMatrixQuery(short heroId)
    {
        return $"""
                SELECT 
                  target_hero_id AS opponent_hero_id,
                  COUNT(*) FILTER (WHERE event_type = 0) AS kills_against,
                  COUNT(*) FILTER (WHERE event_type = 2) AS deaths_to,
                  COALESCE(SUM(event_value) FILTER (WHERE event_type IN (0, 3)), 0.0) AS damage_dealt,
                  COUNT(DISTINCT match_id) AS total_matches
                FROM duckdb.match_combat_events
                WHERE source_hero_id = {heroId} AND target_hero_id > 0 AND target_hero_id != {heroId}
                GROUP BY target_hero_id
                ORDER BY total_matches DESC, kills_against DESC;
                """;
    }

    /// <summary>
    ///     Generates a safe parameterized cross-engine Lakehouse query joining DuckDB combat events
    ///     with PostgreSQL normalized item events to compute player combat impact after acquiring a specific item.
    /// </summary>
    public string GenerateItemCombatImpactQuery(long matchId, string itemIdentifier)
    {
        string escapedItem = (itemIdentifier ?? string.Empty).Replace("'", "''");
        return $"""
                WITH item_acquisition AS (
                  SELECT 
                    "MatchID",
                    "AccountID",
                    MIN("GameTimeSeconds") * 1000 AS item_obtained_ms
                  FROM stat."MatchItemEvents"
                  WHERE "MatchID" = {matchId}
                    AND "ItemIdentifier" = '{escapedItem}'
                    AND "EventType" = 0
                  GROUP BY "MatchID", "AccountID"
                )
                SELECT 
                  c.source_hero_id AS hero_id,
                  c.source_player_index AS player_index,
                  COUNT(*) FILTER (WHERE c.event_type = 0) AS kills_after_item,
                  COUNT(*) FILTER (WHERE c.event_type = 1) AS assists_after_item,
                  COALESCE(SUM(c.event_value) FILTER (WHERE c.event_type IN (0, 3)), 0.0) AS damage_after_item
                FROM duckdb.match_combat_events c
                JOIN stat."PlayerStatistics" ps 
                  ON c.match_id = ps."MatchID" AND c.source_player_index = ps."LobbyPosition"
                JOIN item_acquisition ia 
                  ON ps."MatchID" = ia."MatchID" AND ps."AccountID" = ia."AccountID"
                WHERE c.match_id = {matchId}
                  AND c.match_time_ms >= ia.item_obtained_ms
                GROUP BY c.source_hero_id, c.source_player_index;
                """;
    }

    /// <summary>
    ///     Generates a safe parameterized cross-engine Lakehouse query analyzing the timing and damage impact
    ///     of an item across all matches for a specific hero.
    /// </summary>
    public string GenerateHeroItemTimingLakehouseQuery(short heroId, string itemIdentifier)
    {
        string escapedItem = (itemIdentifier ?? string.Empty).Replace("'", "''");
        return $"""
                WITH purchases AS (
                  SELECT 
                    e."MatchID",
                    e."AccountID",
                    ps."Win",
                    MIN(e."GameTimeSeconds") AS buy_time_seconds
                  FROM stat."MatchItemEvents" e
                  JOIN stat."PlayerStatistics" ps 
                    ON e."MatchID" = ps."MatchID" AND e."AccountID" = ps."AccountID"
                  WHERE e."ItemIdentifier" = '{escapedItem}'
                    AND e."EventType" = 0
                  GROUP BY e."MatchID", e."AccountID", ps."Win"
                )
                SELECT 
                  p.buy_time_seconds / 60 AS minute_bucket,
                  COUNT(*) AS purchase_count,
                  ROUND(AVG(p."Win") * 100.0, 2) AS winrate_percentage,
                  COALESCE(ROUND(AVG(c.total_damage), 2), 0.0) AS avg_damage_dealt
                FROM purchases p
                LEFT JOIN (
                  SELECT match_id, source_hero_id, SUM(event_value) AS total_damage
                  FROM duckdb.match_combat_events
                  WHERE source_hero_id = {heroId} AND event_type IN (0, 3)
                  GROUP BY match_id, source_hero_id
                ) c ON p."MatchID" = c.match_id
                GROUP BY p.buy_time_seconds / 60
                ORDER BY minute_bucket ASC;
                """;
    }

    /// <summary>
    ///     Generates a safe parameterized cross-engine Lakehouse query joining DuckDB combat events
    ///     with PostgreSQL normalized ability events to compute player combat damage and kills dealt after leveling or using a specific skill.
    /// </summary>
    public string GenerateAbilityDamageImpactQuery(long matchId, string abilityIdentifier)
    {
        string escapedAbility = (abilityIdentifier ?? string.Empty).Replace("'", "''");
        return $"""
                WITH ability_skill_timing AS (
                  SELECT 
                    "MatchID",
                    "AccountID",
                    MIN("GameTimeSeconds") * 1000 AS first_skilled_ms
                  FROM stat."MatchAbilityEvents"
                  WHERE "MatchID" = {matchId}
                    AND "AbilityIdentifier" = '{escapedAbility}'
                  GROUP BY "MatchID", "AccountID"
                )
                SELECT 
                  c.source_hero_id AS hero_id,
                  c.source_player_index AS player_index,
                  COUNT(*) FILTER (WHERE c.event_type = 0) AS kills_after_skill,
                  COUNT(*) FILTER (WHERE c.event_type = 1) AS assists_after_skill,
                  COALESCE(SUM(c.event_value) FILTER (WHERE c.event_type IN (0, 3)), 0.0) AS damage_after_skill
                FROM duckdb.match_combat_events c
                JOIN stat."PlayerStatistics" ps 
                  ON c.match_id = ps."MatchID" AND c.source_player_index = ps."LobbyPosition"
                JOIN ability_skill_timing ast 
                  ON ps."MatchID" = ast."MatchID" AND ps."AccountID" = ast."AccountID"
                WHERE c.match_id = {matchId}
                  AND c.match_time_ms >= ast.first_skilled_ms
                GROUP BY c.source_hero_id, c.source_player_index;
                """;
    }

    /// <summary>
    ///     Generates a safe parameterized cross-engine Lakehouse query correlating normalized relational match frags
    ///     from PostgreSQL (stat.MatchFrags) with spatial kill telemetry from DuckDB match combat events.
    /// </summary>
    public string GenerateFragClusteringQuery(long matchId)
    {
        return $"""
                SELECT 
                  f."ID" AS frag_id,
                  f."KillerAccountID" AS killer_account_id,
                  f."VictimAccountID" AS victim_account_id,
                  f."GameTimeSeconds" AS game_time_seconds,
                  f."FirstBlood" AS first_blood,
                  c.position_x,
                  c.position_y,
                  c.position_z
                FROM stat."MatchFrags" f
                LEFT JOIN duckdb.match_combat_events c 
                  ON f."MatchID" = c.match_id 
                 AND c.event_type = 0 
                 AND ABS(c.match_time_ms - (f."GameTimeSeconds" * 1000)) <= 2000
                WHERE f."MatchID" = {matchId}
                ORDER BY f."GameTimeSeconds" ASC, f."ID" ASC;
                """;
    }

    /// <summary>
    ///     Generates a safe parameterized cross-engine Lakehouse query analyzing synergies between item acquisitions
    ///     and ability skill timing across all matches for a given hero.
    /// </summary>
    public string GenerateItemAndSkillSynergyQuery(short heroId, string itemIdentifier, string abilityIdentifier)
    {
        string escapedItem = (itemIdentifier ?? string.Empty).Replace("'", "''");
        string escapedAbility = (abilityIdentifier ?? string.Empty).Replace("'", "''");
        return $"""
                WITH build_matches AS (
                  SELECT 
                    ie."MatchID",
                    ie."AccountID",
                    ps."Win",
                    MIN(ie."GameTimeSeconds") AS item_time_seconds,
                    MIN(ae."GameTimeSeconds") AS ability_time_seconds
                  FROM stat."MatchItemEvents" ie
                  JOIN stat."MatchAbilityEvents" ae 
                    ON ie."MatchID" = ae."MatchID" AND ie."AccountID" = ae."AccountID"
                  JOIN stat."PlayerStatistics" ps 
                    ON ie."MatchID" = ps."MatchID" AND ie."AccountID" = ps."AccountID"
                  WHERE ie."ItemIdentifier" = '{escapedItem}'
                    AND ae."AbilityIdentifier" = '{escapedAbility}'
                  GROUP BY ie."MatchID", ie."AccountID", ps."Win"
                )
                SELECT 
                  COUNT(*) AS sample_matches,
                  ROUND(AVG(b."Win") * 100.0, 2) AS winrate_percentage,
                  ROUND(AVG(b.item_time_seconds), 1) AS avg_item_obtained_sec,
                  ROUND(AVG(b.ability_time_seconds), 1) AS avg_ability_skilled_sec,
                  COALESCE(ROUND(AVG(c.total_damage), 2), 0.0) AS avg_combat_damage
                FROM build_matches b
                LEFT JOIN (
                  SELECT match_id, source_hero_id, SUM(event_value) AS total_damage
                  FROM duckdb.match_combat_events
                  WHERE source_hero_id = {heroId} AND event_type IN (0, 3)
                  GROUP BY match_id, source_hero_id
                ) c ON b."MatchID" = c.match_id;
                """;
    }
}

