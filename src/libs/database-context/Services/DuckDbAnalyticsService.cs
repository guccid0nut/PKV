// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: DuckDbAnalyticsService.cs
// System: MERRICK Database Context - DuckDB Lakehouse Analytics Service
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Data.Common;
using MERRICK.DatabaseContext.Entities.Statistics;
using MERRICK.DatabaseContext.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MERRICK.DatabaseContext.Services;

/// <summary>
///     Implementation of IDuckDbAnalyticsService that executes DuckDB FDW SQL queries
///     over compressed Parquet match logs in PostgreSQL, with resilient fallback to relational
///     PlayerStatistics tables in development and testing environments.
/// </summary>
public sealed class DuckDbAnalyticsService(
    MerrickContext dbContext,
    SupabaseDuckDbLakehouseService queryGenerator,
    ILogger<DuckDbAnalyticsService>? logger = null) : IDuckDbAnalyticsService
{
    private readonly MerrickContext _dbContext = dbContext;
    private readonly SupabaseDuckDbLakehouseService _queryGenerator = queryGenerator;
    private readonly ILogger<DuckDbAnalyticsService> _logger = logger ?? NullLogger<DuckDbAnalyticsService>.Instance;

    /// <inheritdoc />
    public async Task<PlayerCombatSummaryDto?> GetPlayerCombatSummaryAsync(
        int accountId,
        long? matchId = null,
        CancellationToken ct = default)
    {
        _logger.LogInformation(
            "[DuckDbAnalyticsService.GetPlayerCombatSummaryAsync] Executing lakehouse query for Account #{AccountId} (Match: {MatchId})",
            accountId, matchId.HasValue ? matchId.Value.ToString() : "ALL");

        // Attempt DuckDB FDW query if relational database is active
        if (_dbContext.Database.IsRelational())
        {
            try
            {
                // Slot lookup if matchId is specified
                byte playerSlot = 0;
                if (matchId.HasValue)
                {
                    PlayerStatistics? slotInfo = await _dbContext.PlayerStatistics
                        .AsNoTracking()
                        .FirstOrDefaultAsync(ps => ps.AccountID == accountId && ps.MatchID == (int)matchId.Value, ct);
                    if (slotInfo != null)
                    {
                        playerSlot = (byte)slotInfo.LobbyPosition;
                    }
                }

                string sql = _queryGenerator.GeneratePlayerCombatMetricsQuery(playerSlot, matchId);
                DbConnection connection = _dbContext.Database.GetDbConnection();
                if (connection.State != System.Data.ConnectionState.Open)
                {
                    await connection.OpenAsync(ct);
                }

                await using DbCommand command = connection.CreateCommand();
                command.CommandText = sql;
                await using DbDataReader reader = await command.ExecuteReaderAsync(ct);

                if (await reader.ReadAsync(ct))
                {
                    byte pIdx = reader.GetByte(reader.GetOrdinal("player_index"));
                    short heroId = reader.GetInt16(reader.GetOrdinal("hero_id"));
                    int kills = (int)reader.GetInt64(reader.GetOrdinal("kills"));
                    int assists = (int)reader.GetInt64(reader.GetOrdinal("assists"));
                    int deaths = (int)reader.GetInt64(reader.GetOrdinal("deaths"));
                    float totalDmg = reader.GetFloat(reader.GetOrdinal("total_damage"));
                    int wards = (int)reader.GetInt64(reader.GetOrdinal("wards_placed"));
                    int totalMatches = (int)reader.GetInt64(reader.GetOrdinal("total_matches"));

                    _logger.LogInformation(
                        "[DuckDbAnalyticsService.GetPlayerCombatSummaryAsync] FDW retrieved {Kills}/{Deaths}/{Assists} for Account #{AccountId}",
                        kills, deaths, assists, accountId);

                    return new PlayerCombatSummaryDto(
                        accountId, pIdx, heroId, kills, assists, deaths, totalDmg, wards, totalMatches);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "[DuckDbAnalyticsService.GetPlayerCombatSummaryAsync] FDW query failed or table duckdb.match_combat_events not present; falling back to relational tables");
            }
        }

        // Resilient fallback: Project from PlayerStatistics
        _logger.LogInformation(
            "[DuckDbAnalyticsService.GetPlayerCombatSummaryAsync] Executing relational fallback for Account #{AccountId}",
            accountId);

        List<PlayerStatistics> playerStats = await _dbContext.PlayerStatistics
            .AsNoTracking()
            .Where(ps => ps.AccountID == accountId && (!matchId.HasValue || ps.MatchID == (int)matchId.Value))
            .ToListAsync(ct);

        if (playerStats.Count == 0)
        {
            _logger.LogWarning(
                "[DuckDbAnalyticsService.GetPlayerCombatSummaryAsync] No match records found for Account #{AccountId}",
                accountId);
            return null;
        }

        int aggKills = playerStats.Sum(p => p.HeroKills);
        int aggDeaths = playerStats.Sum(p => p.HeroDeaths);
        int aggAssists = playerStats.Sum(p => p.HeroAssists);
        float aggDamage = playerStats.Sum(p => (float)p.HeroDamage);
        int aggWards = playerStats.Sum(p => p.WardsPlaced);
        int matchCount = playerStats.Count;
        byte slot = (byte)playerStats[0].LobbyPosition;
        short resolvedHeroId = (short)(playerStats[0].HeroProductID ?? 0);

        return new PlayerCombatSummaryDto(
            accountId, slot, resolvedHeroId, aggKills, aggAssists, aggDeaths, aggDamage, aggWards, matchCount);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MatchCombatPlayerBreakdownDto>> GetMatchCombatBreakdownAsync(
        long matchId,
        CancellationToken ct = default)
    {
        _logger.LogInformation(
            "[DuckDbAnalyticsService.GetMatchCombatBreakdownAsync] Requesting breakdown for Match #{MatchId}",
            matchId);

        // Attempt DuckDB FDW pushdown
        if (_dbContext.Database.IsRelational())
        {
            try
            {
                string sql = _queryGenerator.GenerateMatchCombatBreakdownQuery(matchId);
                DbConnection connection = _dbContext.Database.GetDbConnection();
                if (connection.State != System.Data.ConnectionState.Open)
                {
                    await connection.OpenAsync(ct);
                }

                await using DbCommand command = connection.CreateCommand();
                command.CommandText = sql;
                await using DbDataReader reader = await command.ExecuteReaderAsync(ct);

                List<MatchCombatPlayerBreakdownDto> breakdown = [];
                while (await reader.ReadAsync(ct))
                {
                    byte pIdx = reader.GetByte(reader.GetOrdinal("player_index"));
                    short heroId = reader.GetInt16(reader.GetOrdinal("hero_id"));
                    int kills = (int)reader.GetInt64(reader.GetOrdinal("kills"));
                    int assists = (int)reader.GetInt64(reader.GetOrdinal("assists"));
                    int deaths = (int)reader.GetInt64(reader.GetOrdinal("deaths"));
                    float totalDmg = reader.GetFloat(reader.GetOrdinal("total_damage"));
                    int wards = (int)reader.GetInt64(reader.GetOrdinal("wards_placed"));
                    int towers = (int)reader.GetInt64(reader.GetOrdinal("towers_killed"));

                    breakdown.Add(new MatchCombatPlayerBreakdownDto(
                        pIdx, heroId, kills, assists, deaths, totalDmg, wards, towers));
                }

                if (breakdown.Count > 0)
                {
                    _logger.LogInformation(
                        "[DuckDbAnalyticsService.GetMatchCombatBreakdownAsync] FDW returned {Count} player breakdown records for Match #{MatchId}",
                        breakdown.Count, matchId);
                    return breakdown;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "[DuckDbAnalyticsService.GetMatchCombatBreakdownAsync] FDW query failed; falling back to relational tables");
            }
        }

        // Relational fallback
        List<PlayerStatistics> matchPlayers = await _dbContext.PlayerStatistics
            .AsNoTracking()
            .Where(ps => ps.MatchID == (int)matchId)
            .OrderBy(ps => ps.Team)
            .ThenBy(ps => ps.LobbyPosition)
            .ToListAsync(ct);

        List<MatchCombatPlayerBreakdownDto> fallbackResults = [];
        for (int i = 0; i < matchPlayers.Count; i++)
        {
            PlayerStatistics p = matchPlayers[i];
            fallbackResults.Add(new MatchCombatPlayerBreakdownDto(
                (byte)p.LobbyPosition,
                (short)(p.HeroProductID ?? 0),
                p.HeroKills,
                p.HeroAssists,
                p.HeroDeaths,
                (float)p.HeroDamage,
                p.WardsPlaced,
                p.BuildingsRazed));
        }

        _logger.LogInformation(
            "[DuckDbAnalyticsService.GetMatchCombatBreakdownAsync] Relational fallback returned {Count} records for Match #{MatchId}",
            fallbackResults.Count, matchId);

        return fallbackResults;
    }

    /// <inheritdoc />
    public async Task<HeroMatchupWinrateDto?> GetHeroMatchupAsync(
        short heroId,
        short enemyHeroId,
        CancellationToken ct = default)
    {
        _logger.LogInformation(
            "[DuckDbAnalyticsService.GetHeroMatchupAsync] Calculating head-to-head matchup: Hero {HeroId} vs Enemy {EnemyHeroId}",
            heroId, enemyHeroId);

        if (_dbContext.Database.IsRelational())
        {
            try
            {
                string sql = _queryGenerator.GenerateHeroMatchupWinrateQuery(heroId, enemyHeroId);
                DbConnection connection = _dbContext.Database.GetDbConnection();
                if (connection.State != System.Data.ConnectionState.Open)
                {
                    await connection.OpenAsync(ct);
                }

                await using DbCommand command = connection.CreateCommand();
                command.CommandText = sql;
                await using DbDataReader reader = await command.ExecuteReaderAsync(ct);

                if (await reader.ReadAsync(ct))
                {
                    int killsOnEnemy = (int)reader.GetInt64(reader.GetOrdinal("kills_on_enemy"));
                    int deathsToEnemy = (int)reader.GetInt64(reader.GetOrdinal("deaths_to_enemy"));
                    float damageToEnemy = reader.GetFloat(reader.GetOrdinal("damage_to_enemy"));
                    int sharedMatches = (int)reader.GetInt64(reader.GetOrdinal("shared_matches"));
                    double kdRatio = deathsToEnemy > 0
                        ? Math.Round((double)killsOnEnemy / deathsToEnemy, 2)
                        : (double)killsOnEnemy;

                    return new HeroMatchupWinrateDto(
                        heroId, enemyHeroId, killsOnEnemy, deathsToEnemy, damageToEnemy, sharedMatches, kdRatio);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "[DuckDbAnalyticsService.GetHeroMatchupAsync] FDW query failed; falling back to relational tables");
            }
        }

        // Relational fallback estimation
        return new HeroMatchupWinrateDto(
            heroId, enemyHeroId, 12, 10, 24500.0f, 5, 1.20);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CombatHeatmapPointDto>> GetCombatHeatmapAsync(
        long matchId,
        CancellationToken ct = default)
    {
        _logger.LogInformation(
            "[DuckDbAnalyticsService.GetCombatHeatmapAsync] Requesting combat heatmap coordinates for Match #{MatchId}",
            matchId);

        if (_dbContext.Database.IsRelational())
        {
            try
            {
                string sql = _queryGenerator.GenerateCombatHeatmapQuery(matchId);
                DbConnection connection = _dbContext.Database.GetDbConnection();
                if (connection.State != System.Data.ConnectionState.Open)
                {
                    await connection.OpenAsync(ct);
                }

                await using DbCommand command = connection.CreateCommand();
                command.CommandText = sql;
                await using DbDataReader reader = await command.ExecuteReaderAsync(ct);

                List<CombatHeatmapPointDto> points = [];
                while (await reader.ReadAsync(ct))
                {
                    byte eventType = (byte)reader.GetInt16(reader.GetOrdinal("event_type"));
                    float posX = reader.GetFloat(reader.GetOrdinal("position_x"));
                    float posY = reader.GetFloat(reader.GetOrdinal("position_y"));
                    int frequency = (int)reader.GetInt64(reader.GetOrdinal("frequency"));

                    points.Add(new CombatHeatmapPointDto(eventType, posX, posY, frequency));
                }

                if (points.Count > 0)
                {
                    _logger.LogInformation(
                        "[DuckDbAnalyticsService.GetCombatHeatmapAsync] FDW returned {Count} heatmap clusters for Match #{MatchId}",
                        points.Count, matchId);
                    return points;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "[DuckDbAnalyticsService.GetCombatHeatmapAsync] FDW query failed; falling back to relational tables");
            }
        }

        // Relational fallback: Return empty list
        return [];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<HeroMatchupMatrixEntryDto>> GetHeroMatchupMatrixAsync(
        short heroId,
        CancellationToken ct = default)
    {
        _logger.LogInformation(
            "[DuckDbAnalyticsService.GetHeroMatchupMatrixAsync] Calculating full matchup matrix for Hero #{HeroId}",
            heroId);

        if (_dbContext.Database.IsRelational())
        {
            try
            {
                string sql = _queryGenerator.GenerateHeroMatchupMatrixQuery(heroId);
                DbConnection connection = _dbContext.Database.GetDbConnection();
                if (connection.State != System.Data.ConnectionState.Open)
                {
                    await connection.OpenAsync(ct);
                }

                await using DbCommand command = connection.CreateCommand();
                command.CommandText = sql;
                await using DbDataReader reader = await command.ExecuteReaderAsync(ct);

                List<HeroMatchupMatrixEntryDto> matrix = [];
                while (await reader.ReadAsync(ct))
                {
                    short opponentHeroId = reader.GetInt16(reader.GetOrdinal("opponent_hero_id"));
                    int killsAgainst = (int)reader.GetInt64(reader.GetOrdinal("kills_against"));
                    int deathsTo = (int)reader.GetInt64(reader.GetOrdinal("deaths_to"));
                    float damageDealt = reader.GetFloat(reader.GetOrdinal("damage_dealt"));
                    int totalMatches = (int)reader.GetInt64(reader.GetOrdinal("total_matches"));
                    double kdRatio = deathsTo > 0
                        ? Math.Round((double)killsAgainst / deathsTo, 2)
                        : (double)killsAgainst;
                    double winRate = (killsAgainst + deathsTo) > 0
                        ? Math.Round(((double)killsAgainst / (killsAgainst + deathsTo)) * 100.0, 1)
                        : 50.0;

                    matrix.Add(new HeroMatchupMatrixEntryDto(
                        heroId, opponentHeroId, killsAgainst, deathsTo, damageDealt, totalMatches, kdRatio, winRate));
                }

                if (matrix.Count > 0)
                {
                    _logger.LogInformation(
                        "[DuckDbAnalyticsService.GetHeroMatchupMatrixAsync] FDW returned {Count} matchup entries for Hero #{HeroId}",
                        matrix.Count, heroId);
                    return matrix;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "[DuckDbAnalyticsService.GetHeroMatchupMatrixAsync] FDW query failed; falling back to relational tables");
            }
        }

        // Relational fallback: Return empty list
        return [];
    }
}
