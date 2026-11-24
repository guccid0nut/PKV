// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.Services;

/// <summary>
///     High-performance Redis SortedSet Leaderboard Cache for sub-millisecond ranking queries.
/// </summary>
public interface ILeaderboardCacheService
{
    /// <summary>
    ///     Records or updates an account's score in the specified season and mode leaderboard.
    /// </summary>
    Task RecordScoreAsync(
        string season,
        string mode,
        int accountId,
        double score,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Records or updates multiple accounts' scores in a single atomic pipelined batch.
    /// </summary>
    Task RecordScoresBatchAsync(
        string season,
        string mode,
        IEnumerable<(int AccountId, double Score)> scores,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Retrieves the top ranked accounts from the sorted set in descending order.
    /// </summary>
    Task<List<(int AccountId, double Score, long Rank)>> GetTopRankedAsync(
        string season,
        string mode,
        int limit = 100,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Gets a player's 1-based rank position on the specified leaderboard.
    /// </summary>
    Task<long?> GetPlayerRankAsync(
        string season,
        string mode,
        int accountId,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Gets a player's current recorded score on the specified leaderboard.
    /// </summary>
    Task<double?> GetPlayerScoreAsync(
        string season,
        string mode,
        int accountId,
        CancellationToken cancellationToken = default);
}
