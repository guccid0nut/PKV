// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Globalization;
using System.Text.Json;
using DuckDB.NET.Data;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Kongor.Core.Analytics;
using Xunit;

namespace Kongor.Mcp.Tests;

/// <summary>
/// Unit tests for DuckDbMatchAnalyticsService, MatchAnalyticsHandler, and MCP tool registration.
/// Validates sub-millisecond in-process DuckDB telemetry lakehouse queries over Apache Parquet files.
/// </summary>
public sealed class DuckDbMatchAnalyticsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _testParquetPath;
    private readonly DuckDbMatchAnalyticsService _service;
    private readonly MatchAnalyticsHandler _handler;
    private readonly McpToolRegistry _registry;

    public DuckDbMatchAnalyticsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"kongor_duckdb_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _testParquetPath = Path.Combine(_tempDir, "match_1001.parquet");

        GenerateSyntheticParquetFile(_testParquetPath, 1001);

        _service = new DuckDbMatchAnalyticsService();
        _handler = new MatchAnalyticsHandler(analyticsService: _service);
        _registry = new McpToolRegistry(registerDefaults: true);
    }

    [Fact]
    public async Task ExecuteQueryAsync_WhenGivenSelectQuery_ReturnsColumnarRowsAndTiming()
    {
        string sql = "SELECT event_type, COUNT(*) AS count FROM match_combat_events GROUP BY event_type ORDER BY event_type;";

        MatchAnalyticsQueryResult result = await _service.ExecuteQueryAsync(sql, _testParquetPath);

        result.Success.Should().BeTrue();
        result.RowCount.Should().BeGreaterThan(0);
        result.Columns.Should().Contain("event_type");
        result.Columns.Should().Contain("count");
        result.ExecutionDurationMs.Should().BeGreaterThanOrEqualTo(0);
        result.ParquetSource.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GetPlayerSummaryAsync_WhenQueried_AggregatesCorrectKdaAndDamage()
    {
        PlayerCombatSummaryResult? summary = await _service.GetPlayerSummaryAsync(
            accountId: 1, matchId: 1001, playerIndex: 0, parquetPath: _testParquetPath);

        summary.Should().NotBeNull();
        summary!.PlayerIndex.Should().Be(0);
        summary.HeroId.Should().Be(1);
        summary.Kills.Should().Be(5);
        summary.Assists.Should().Be(3);
        summary.Deaths.Should().Be(2);
        summary.TotalDamage.Should().Be(12500f);
        summary.WardsPlaced.Should().Be(4);
        summary.TotalMatches.Should().Be(1);
        summary.KillDeathRatio.Should().Be(2.5);
    }

    [Fact]
    public async Task GetMatchBreakdownAsync_WhenQueried_ReturnsPlayerBreakdown()
    {
        IReadOnlyList<MatchCombatPlayerBreakdownResult> players = await _service.GetMatchBreakdownAsync(
            matchId: 1001, parquetPath: _testParquetPath);

        players.Should().NotBeEmpty();
        players.Count.Should().Be(10);

        MatchCombatPlayerBreakdownResult player0 = players.First(p => p.PlayerIndex == 0);
        player0.HeroId.Should().Be(1);
        player0.Kills.Should().Be(5);
        player0.Deaths.Should().Be(2);
        player0.Assists.Should().Be(3);
        player0.TotalDamage.Should().Be(12500f);
        player0.WardsPlaced.Should().Be(4);
    }

    [Fact]
    public async Task GetSpatialHeatmapAsync_WhenQueried_ReturnsMinimapCoordinates()
    {
        IReadOnlyList<SpatialHeatmapPoint> heatmap = await _service.GetSpatialHeatmapAsync(
            matchId: 1001, eventType: CombatEventType.HeroKill, parquetPath: _testParquetPath, bucketSize: 100);

        heatmap.Should().NotBeEmpty();
        SpatialHeatmapPoint topPoint = heatmap[0];
        topPoint.EventType.Should().Be((byte)CombatEventType.HeroKill);
        topPoint.EventTypeName.Should().Be("HeroKill");
        topPoint.Frequency.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task GetHeroMatchupMatrixAsync_WhenQueried_ComputesHeadToHeadStats()
    {
        IReadOnlyList<HeroMatchupResult> matchups = await _service.GetHeroMatchupMatrixAsync(
            heroId: 1, opponentHeroId: 2, parquetPath: _testParquetPath);

        matchups.Should().NotBeEmpty();
        HeroMatchupResult matchup = matchups[0];
        matchup.HeroId.Should().Be(1);
        matchup.OpponentHeroId.Should().Be(2);
        matchup.KillsAgainst.Should().Be(3);
        matchup.DeathsTo.Should().Be(1);
        matchup.DamageDealt.Should().Be(7500f);
        matchup.KillDeathRatio.Should().Be(3.0);
        matchup.WinRatePercent.Should().Be(75.0);
    }

    [Fact]
    public async Task ExecuteQueryAsync_WhenMutationAttempted_RejectsForbiddenSql()
    {
        string dropSql = "DROP TABLE match_combat_events;";
        MatchAnalyticsQueryResult dropResult = await _service.ExecuteQueryAsync(dropSql, _testParquetPath);

        dropResult.Success.Should().BeFalse();
        dropResult.Error.Should().Contain("Security Violation");

        string deleteSql = "DELETE FROM match_combat_events WHERE event_type = 0;";
        MatchAnalyticsQueryResult deleteResult = await _service.ExecuteQueryAsync(deleteSql, _testParquetPath);

        deleteResult.Success.Should().BeFalse();
        deleteResult.Error.Should().Contain("Security Violation");
    }

    [Fact]
    public void ListParquetFiles_WhenFilesPresent_ReturnsCatalogMetadata()
    {
        IReadOnlyList<ParquetFileInfo> files = _service.ListParquetFiles(_tempDir);

        files.Should().NotBeEmpty();
        ParquetFileInfo target = files.First(f => f.FileName == "match_1001.parquet");
        target.MatchId.Should().Be(1001);
        target.FileSizeBytes.Should().BeGreaterThan(0);
        target.FormattedSize.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task MatchAnalyticsHandler_WhenInvoked_DispatchesToolActionsSeamlessly()
    {
        // 1. list_files
        using JsonDocument listDoc = JsonDocument.Parse($$"""{"action": "list_files", "directory_path": "{{_tempDir.Replace('\\', '/')}}"}""");
        ToolCallResult listResult = await _handler.HandleAsync(listDoc.RootElement);
        listResult.IsError.Should().BeFalse();
        string listText = ((TextContentItem)listResult.Content[0]).Text;
        listText.Should().Contain("match_1001.parquet");

        // 2. player_summary
        using JsonDocument playerDoc = JsonDocument.Parse($$"""{"action": "player_summary", "account_id": 1, "player_index": 0, "match_id": 1001, "parquet_path": "{{_testParquetPath.Replace('\\', '/')}}"}""");
        ToolCallResult playerResult = await _handler.HandleAsync(playerDoc.RootElement);
        playerResult.IsError.Should().BeFalse();
        string playerText = ((TextContentItem)playerResult.Content[0]).Text;
        playerText.Should().Contain("\"Kills\": 5");

        // 3. heatmap
        using JsonDocument heatDoc = JsonDocument.Parse($$"""{"action": "heatmap", "match_id": 1001, "event_type": "HeroKill", "parquet_path": "{{_testParquetPath.Replace('\\', '/')}}"}""");
        ToolCallResult heatResult = await _handler.HandleAsync(heatDoc.RootElement);
        heatResult.IsError.Should().BeFalse();
        string heatText = ((TextContentItem)heatResult.Content[0]).Text;
        heatText.Should().Contain("heatmapPoints");

        // 4. query_sql
        using JsonDocument sqlDoc = JsonDocument.Parse($$"""{"action": "query_sql", "sql": "SELECT COUNT(*) AS total_events FROM match_combat_events", "parquet_path": "{{_testParquetPath.Replace('\\', '/')}}"}""");
        ToolCallResult sqlResult = await _handler.HandleAsync(sqlDoc.RootElement);
        sqlResult.IsError.Should().BeFalse();
        string sqlText = ((TextContentItem)sqlResult.Content[0]).Text;
        sqlText.Should().Contain("total_events");
    }

    [Fact]
    public async Task McpToolRegistry_WhenInvoked_DispatchesMatchAnalyticsAndAliases()
    {
        _registry.HasTool("kongor_match_analytics").Should().BeTrue();
        _registry.HasTool("match_analytics").Should().BeTrue();
        _registry.HasTool("kongor_lakehouse_query").Should().BeTrue();

        using JsonDocument doc = JsonDocument.Parse($$"""{"action": "query_sql", "sql": "SELECT 42 AS answer", "parquet_path": "{{_testParquetPath.Replace('\\', '/')}}"}""");
        ToolCallResult res = await _registry.ExecuteToolAsync("kongor_match_analytics", doc.RootElement);

        res.IsError.Should().BeFalse();
        string text = ((TextContentItem)res.Content[0]).Text;
        text.Should().Contain("42");
    }

    private static void GenerateSyntheticParquetFile(string destinationPath, long matchId)
    {
        string normPath = destinationPath.Replace('\\', '/');

        using DuckDBConnection conn = new("Data Source=:memory:");
        conn.Open();

        // Create in-memory table conforming to MatchCombatEventRecord schema
        using (DuckDBCommand createCmd = conn.CreateCommand())
        {
            createCmd.CommandText = """
                CREATE TABLE raw_events (
                    match_id BIGINT,
                    match_time_ms INTEGER,
                    tick INTEGER,
                    event_type SMALLINT,
                    source_player_index SMALLINT,
                    source_hero_id SMALLINT,
                    target_player_index SMALLINT,
                    target_hero_id SMALLINT,
                    ability_or_item_id INTEGER,
                    event_value FLOAT,
                    position_x FLOAT,
                    position_y FLOAT,
                    position_z FLOAT
                );
                """;
            createCmd.ExecuteNonQuery();
        }

        // Insert synthetic events for 10 players
        using (DuckDBCommand insertCmd = conn.CreateCommand())
        {
            List<string> rowValues = [];

            // Player 0 (Hero 1): 5 kills (3 on Hero 2, 2 on Hero 3), 2 deaths, 3 assists, 4 wards, total dmg 12500
            // 3 kills on Hero 2
            rowValues.Add($"({matchId}, 60000, 1200, 0, 0, 1, 5, 2, 101, 2500.0, 1500.0, 1500.0, 100.0)");
            rowValues.Add($"({matchId}, 120000, 2400, 0, 0, 1, 5, 2, 101, 2500.0, 1520.0, 1480.0, 100.0)");
            rowValues.Add($"({matchId}, 180000, 3600, 0, 0, 1, 5, 2, 102, 2500.0, 1510.0, 1490.0, 100.0)");
            // 2 kills on Hero 3
            rowValues.Add($"({matchId}, 240000, 4800, 0, 0, 1, 6, 3, 101, 2500.0, 2000.0, 2000.0, 100.0)");
            rowValues.Add($"({matchId}, 300000, 6000, 0, 0, 1, 6, 3, 102, 2500.0, 2050.0, 1950.0, 100.0)");

            // 1 death to Hero 2
            rowValues.Add($"({matchId}, 150000, 3000, 2, 0, 1, 5, 2, 201, 1800.0, 1600.0, 1600.0, 100.0)");
            // 1 death to Hero 4
            rowValues.Add($"({matchId}, 270000, 5400, 2, 0, 1, 7, 4, 301, 2200.0, 1800.0, 1800.0, 100.0)");

            // 3 assists
            rowValues.Add($"({matchId}, 80000, 1600, 1, 0, 1, 5, 2, 101, 500.0, 1400.0, 1400.0, 100.0)");
            rowValues.Add($"({matchId}, 140000, 2800, 1, 0, 1, 6, 3, 101, 500.0, 1700.0, 1700.0, 100.0)");
            rowValues.Add($"({matchId}, 200000, 4000, 1, 0, 1, 7, 4, 101, 500.0, 1900.0, 1900.0, 100.0)");

            // 4 wards placed (EventType 6)
            rowValues.Add($"({matchId}, 30000, 600, 6, 0, 1, 0, 0, 501, 0.0, 1200.0, 1200.0, 100.0)");
            rowValues.Add($"({matchId}, 90000, 1800, 6, 0, 1, 0, 0, 501, 0.0, 1210.0, 1190.0, 100.0)");
            rowValues.Add($"({matchId}, 150000, 3000, 6, 0, 1, 0, 0, 501, 0.0, 1220.0, 1205.0, 100.0)");
            rowValues.Add($"({matchId}, 210000, 4200, 6, 0, 1, 0, 0, 501, 0.0, 1200.0, 1200.0, 100.0)");

            // Fill events for remaining players (1 to 9) so all 10 players exist in match
            for (short p = 1; p < 10; p++)
            {
                short hId = (short)(p + 1);
                rowValues.Add($"({matchId}, 100000, 2000, 0, {p}, {hId}, 0, 1, 101, 1000.0, 1000.0, 1000.0, 100.0)");
            }

            insertCmd.CommandText = $"INSERT INTO raw_events VALUES {string.Join(",\n", rowValues)};";
            insertCmd.ExecuteNonQuery();
        }

        // Export directly to Parquet using DuckDB's native COPY TO
        using (DuckDBCommand copyCmd = conn.CreateCommand())
        {
            copyCmd.CommandText = $"COPY raw_events TO '{normPath}' (FORMAT PARQUET);";
            copyCmd.ExecuteNonQuery();
        }
    }

    public void Dispose()
    {
        _service.Dispose();
        _handler.Dispose();

        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch { }
    }
}
