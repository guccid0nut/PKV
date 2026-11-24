// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: SupabaseDuckDbLakehouseTests.cs
// System: MERRICK Unit Tests - Supabase DuckDB Foreign Data Wrapper Lakehouse
// ============================================================================

using System.IO;
using System.Reflection;
using Kongor.Core.Analytics;
using MERRICK.DatabaseContext.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MERRICK.DatabaseContext.Tests;

public sealed class SupabaseDuckDbLakehouseTests
{
    private static string FindRepositoryRoot()
    {
        string? current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "ProjectKongor.slnx")) ||
                File.Exists(Path.Combine(current, "ProjectKongor.sln")) ||
                Directory.Exists(Path.Combine(current, ".git")))
            {
                return current;
            }

            current = Directory.GetParent(current)?.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate repository root directory.");
    }

    [Fact]
    public void MigrationScript_IsValidAndContainsAllRequiredStatements()
    {
        // Arrange
        SupabaseDuckDbLakehouseService service = new(NullLogger<SupabaseDuckDbLakehouseService>.Instance);
        string repoRoot = FindRepositoryRoot();
        string sqlPath = Path.Combine(repoRoot, "src", "libs", "database-context", "Sql", "001_setup_duckdb_fdw.sql");

        Assert.True(File.Exists(sqlPath), $"Migration file not found at {sqlPath}");
        string sql = File.ReadAllText(sqlPath);

        // Act
        bool isValid = service.ValidateMigrationScript(sql, out IReadOnlyList<string> errors);

        // Assert
        Assert.True(isValid, $"Migration validation failed: {string.Join("; ", errors)}");
        Assert.Empty(errors);
    }

    [Fact]
    public void ValidateMigrationScript_MissingTable_ReturnsFalseWithDescriptiveError()
    {
        // Arrange
        SupabaseDuckDbLakehouseService service = new(NullLogger<SupabaseDuckDbLakehouseService>.Instance);
        string invalidSql = """
                            CREATE EXTENSION IF NOT EXISTS wrappers WITH SCHEMA extensions;
                            CREATE FOREIGN DATA WRAPPER duckdb_wrapper HANDLER duckdb_fdw_handler VALIDATOR duckdb_fdw_validator;
                            CREATE SERVER duckdb_server FOREIGN DATA WRAPPER duckdb_wrapper OPTIONS (type 's3');
                            CREATE SCHEMA duckdb;
                            """;

        // Act
        bool isValid = service.ValidateMigrationScript(invalidSql, out IReadOnlyList<string> errors);

        // Assert
        Assert.False(isValid);
        Assert.Contains(errors, e => e.Contains("create foreign table duckdb.match_combat_events", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CanonicalColumns_MatchesCombatEventSchema()
    {
        // Assert that all canonical database columns have matching properties on MatchCombatEventRecord
        PropertyInfo[] properties = typeof(MatchCombatEventRecord).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        HashSet<string> propertyNames = properties.Select(p => p.Name.ToLowerInvariant()).ToHashSet();

        foreach (ColumnTypeMapping mapping in SupabaseDuckDbLakehouseService.CanonicalColumns)
        {
            // Normalize snake_case column name to camelCase / PascalCase comparison
            string stripped = mapping.ColumnName.Replace("_", string.Empty).ToLowerInvariant();
            bool hasMatchingProperty = propertyNames.Any(p => p.Equals(stripped, StringComparison.OrdinalIgnoreCase));
            Assert.True(hasMatchingProperty, $"Property matching column '{mapping.ColumnName}' not found on MatchCombatEventRecord.");
        }
    }

    [Fact]
    public void GenerateMatchTimelineQuery_FormatsPushdownPredicateAndOrdering()
    {
        // Arrange
        SupabaseDuckDbLakehouseService service = new(NullLogger<SupabaseDuckDbLakehouseService>.Instance);

        // Act
        string sql = service.GenerateMatchTimelineQuery(556677, limit: 100);

        // Assert
        Assert.Contains("WHERE match_id = 556677", sql);
        Assert.Contains("ORDER BY match_time_ms ASC", sql);
        Assert.Contains("LIMIT 100", sql);
        Assert.Contains("FROM duckdb.match_combat_events", sql);
    }

    [Fact]
    public void GenerateHeroCombatMetricsQuery_FormatsAggregationFilters()
    {
        // Arrange
        SupabaseDuckDbLakehouseService service = new(NullLogger<SupabaseDuckDbLakehouseService>.Instance);

        // Act
        string sql = service.GenerateHeroCombatMetricsQuery(35);

        // Assert
        Assert.Contains("WHERE source_hero_id = 35", sql);
        Assert.Contains("GROUP BY source_hero_id", sql);
        Assert.Contains("FROM duckdb.match_combat_events", sql);
    }

    [Fact]
    public void GeneratePlayerCombatMetricsQuery_FormatsValidSql()
    {
        // Arrange
        SupabaseDuckDbLakehouseService service = new(NullLogger<SupabaseDuckDbLakehouseService>.Instance);

        // Act
        string sql = service.GeneratePlayerCombatMetricsQuery(playerIndex: 3, matchId: 998877);

        // Assert
        Assert.Contains("WHERE source_player_index = 3 AND match_id = 998877", sql);
        Assert.Contains("COUNT(*) FILTER (WHERE event_type = 0) AS kills", sql);
        Assert.Contains("COUNT(*) FILTER (WHERE event_type = 6) AS wards_placed", sql);
        Assert.Contains("FROM duckdb.match_combat_events", sql);
    }

    [Fact]
    public void GenerateMatchCombatBreakdownQuery_GroupsByPlayer()
    {
        // Arrange
        SupabaseDuckDbLakehouseService service = new(NullLogger<SupabaseDuckDbLakehouseService>.Instance);

        // Act
        string sql = service.GenerateMatchCombatBreakdownQuery(998877);

        // Assert
        Assert.Contains("WHERE match_id = 998877", sql);
        Assert.Contains("GROUP BY source_player_index, source_hero_id", sql);
        Assert.Contains("ORDER BY source_player_index ASC", sql);
        Assert.Contains("COUNT(*) FILTER (WHERE event_type = 7) AS towers_killed", sql);
    }

    [Fact]
    public void GenerateHeroMatchupWinrateQuery_ComputesHeadToHead()
    {
        // Arrange
        SupabaseDuckDbLakehouseService service = new(NullLogger<SupabaseDuckDbLakehouseService>.Instance);

        // Act
        string sql = service.GenerateHeroMatchupWinrateQuery(heroId: 10, enemyHeroId: 45);

        // Assert
        Assert.Contains("WHERE source_hero_id = 10 AND target_hero_id = 45", sql);
        Assert.Contains("COUNT(*) FILTER (WHERE event_type = 0 AND target_hero_id = 45) AS kills_on_enemy", sql);
        Assert.Contains("COUNT(*) FILTER (WHERE event_type = 2 AND target_hero_id = 45) AS deaths_to_enemy", sql);
    }

    [Fact]
    public void GenerateCombatHeatmapQuery_IncludesFilterAndGrouping()
    {
        // Arrange
        SupabaseDuckDbLakehouseService service = new(NullLogger<SupabaseDuckDbLakehouseService>.Instance);

        // Act
        string sql = service.GenerateCombatHeatmapQuery(1001);

        // Assert
        Assert.Contains("WHERE match_id = 1001 AND event_type IN (0, 2, 6, 7)", sql);
        Assert.Contains("GROUP BY event_type, position_x, position_y", sql);
        Assert.Contains("ORDER BY frequency DESC", sql);
    }

    [Fact]
    public void GenerateHeroMatchupMatrixQuery_GroupsByOpponent()
    {
        // Arrange
        SupabaseDuckDbLakehouseService service = new(NullLogger<SupabaseDuckDbLakehouseService>.Instance);

        // Act
        string sql = service.GenerateHeroMatchupMatrixQuery(42);

        // Assert
        Assert.Contains("WHERE source_hero_id = 42 AND target_hero_id > 0 AND target_hero_id != 42", sql);
        Assert.Contains("GROUP BY target_hero_id", sql);
        Assert.Contains("ORDER BY total_matches DESC, kills_against DESC", sql);
    }

    [Fact]
    public void GenerateItemCombatImpactQuery_CorrelatesItemAcquisitionWithCombatSpikes()
    {
        // Arrange
        SupabaseDuckDbLakehouseService service = new(NullLogger<SupabaseDuckDbLakehouseService>.Instance);

        // Act
        string sql = service.GenerateItemCombatImpactQuery(123456, "Item_PortalKey");

        // Assert
        Assert.Contains("stat.\"MatchItemEvents\"", sql);
        Assert.Contains("\"MatchID\" = 123456", sql);
        Assert.Contains("\"ItemIdentifier\" = 'Item_PortalKey'", sql);
        Assert.Contains("duckdb.match_combat_events c", sql);
        Assert.Contains("c.match_time_ms >= ia.item_obtained_ms", sql);
        Assert.Contains("GROUP BY c.source_hero_id, c.source_player_index", sql);
    }

    [Fact]
    public void GenerateHeroItemTimingLakehouseQuery_AggregatesPurchasesAndDamageByMinute()
    {
        // Arrange
        SupabaseDuckDbLakehouseService service = new(NullLogger<SupabaseDuckDbLakehouseService>.Instance);

        // Act
        string sql = service.GenerateHeroItemTimingLakehouseQuery(15, "Item_Nullstone");

        // Assert
        Assert.Contains("stat.\"MatchItemEvents\"", sql);
        Assert.Contains("\"ItemIdentifier\" = 'Item_Nullstone'", sql);
        Assert.Contains("duckdb.match_combat_events", sql);
        Assert.Contains("source_hero_id = 15", sql);
        Assert.Contains("GROUP BY p.buy_time_seconds / 60", sql);
        Assert.Contains("ORDER BY minute_bucket ASC", sql);
    }
}
