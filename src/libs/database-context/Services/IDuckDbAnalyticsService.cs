// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: IDuckDbAnalyticsService.cs
// System: MERRICK Database Context - DuckDB Lakehouse Analytics Service Interface
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

namespace MERRICK.DatabaseContext.Services;

/// <summary>
///     Combat analytics summary for a player aggregated across Lakehouse Parquet files.
/// </summary>
public sealed record PlayerCombatSummaryDto(
    int AccountId,
    byte PlayerIndex,
    short HeroId,
    int Kills,
    int Assists,
    int Deaths,
    float TotalDamage,
    int WardsPlaced,
    int TotalMatches);

/// <summary>
///     Combat analytics breakdown for a single player within a 10-player match.
/// </summary>
public sealed record MatchCombatPlayerBreakdownDto(
    byte PlayerIndex,
    short HeroId,
    int Kills,
    int Assists,
    int Deaths,
    float TotalDamage,
    int WardsPlaced,
    int TowersKilled);

/// <summary>
///     Head-to-head combat and matchup statistics between two heroes.
/// </summary>
public sealed record HeroMatchupWinrateDto(
    short HeroId,
    short EnemyHeroId,
    int KillsOnEnemy,
    int DeathsToEnemy,
    float DamageToEnemy,
    int SharedMatches,
    double KillDeathRatio);

/// <summary>
///     Spatial heatmap point representing combat activity coordinates and event frequency.
/// </summary>
public sealed record CombatHeatmapPointDto(
    byte EventType,
    float PositionX,
    float PositionY,
    int Frequency);

/// <summary>
///     Aggregated matchup performance metric of a hero against a specific opponent hero across all Lakehouse matches.
/// </summary>
public sealed record HeroMatchupMatrixEntryDto(
    short HeroId,
    short OpponentHeroId,
    int KillsAgainst,
    int DeathsTo,
    float DamageDealt,
    int TotalMatches,
    double KillDeathRatio,
    double WinRatePercent);

/// <summary>
///     Contract for executing high-throughput DuckDB Lakehouse queries with automatic relational fallback.
/// </summary>
public interface IDuckDbAnalyticsService
{
    /// <summary>
    ///     Retrieves aggregated combat metrics for a player, optionally filtered by match.
    /// </summary>
    Task<PlayerCombatSummaryDto?> GetPlayerCombatSummaryAsync(int accountId, long? matchId = null, CancellationToken ct = default);

    /// <summary>
    ///     Retrieves the full 10-player combat breakdown for a specific match.
    /// </summary>
    Task<IReadOnlyList<MatchCombatPlayerBreakdownDto>> GetMatchCombatBreakdownAsync(long matchId, CancellationToken ct = default);

    /// <summary>
    ///     Retrieves head-to-head matchup statistics between a hero and an enemy hero.
    /// </summary>
    Task<HeroMatchupWinrateDto?> GetHeroMatchupAsync(short heroId, short enemyHeroId, CancellationToken ct = default);

    /// <summary>
    ///     Retrieves spatial coordinate clusters for kills, deaths, and wards for 2D minimap heatmap rendering.
    /// </summary>
    Task<IReadOnlyList<CombatHeatmapPointDto>> GetCombatHeatmapAsync(long matchId, CancellationToken ct = default);

    /// <summary>
    ///     Retrieves the full hero matchup performance matrix against all opponent heroes across stored matches.
    /// </summary>
    Task<IReadOnlyList<HeroMatchupMatrixEntryDto>> GetHeroMatchupMatrixAsync(short heroId, CancellationToken ct = default);
}
