// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using StackExchange.Redis;

namespace ASPIRE.Common.Services.Leaderboard;

public class RedisLeaderboardCacheService(
    IConnectionMultiplexer? redisMuxer = null,
    ILogger<RedisLeaderboardCacheService>? logger = null)
    : ILeaderboardCacheService
{
    public async Task RecordScoreAsync(
        string season,
        string mode,
        int accountId,
        double score,
        CancellationToken cancellationToken = default)
    {
        if (redisMuxer is not { IsConnected: true }) return;

        try
        {
            IDatabase db = redisMuxer.GetDatabase();
            string key = GetLeaderboardKey(season, mode);
            await db.SortedSetAddAsync(key, accountId.ToString(), score).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (logger?.IsEnabled(LogLevel.Warning) == true)
                logger.LogWarning(ex,
                    "[RedisLeaderboard] Failed to record score for Account {AccountId} in {Season}/{Mode}",
                    accountId, season, mode);
        }
    }

    public async Task RecordScoresBatchAsync(
        string season,
        string mode,
        IEnumerable<(int AccountId, double Score)> scores,
        CancellationToken cancellationToken = default)
    {
        if (redisMuxer is not { IsConnected: true }) return;

        try
        {
            IDatabase db = redisMuxer.GetDatabase();
            string key = GetLeaderboardKey(season, mode);
            SortedSetEntry[] entries = [.. scores.Select(s => new SortedSetEntry(s.AccountId.ToString(), s.Score))];

            if (entries.Length > 0) await db.SortedSetAddAsync(key, entries).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (logger?.IsEnabled(LogLevel.Warning) == true)
                logger.LogWarning(ex, "[RedisLeaderboard] Failed to record batch scores in {Season}/{Mode}", season,
                    mode);
        }
    }

    public async Task<List<(int AccountId, double Score, long Rank)>> GetTopRankedAsync(
        string season,
        string mode,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        List<(int AccountId, double Score, long Rank)> results = [];

        if (redisMuxer is not { IsConnected: true } || limit <= 0) return results;

        try
        {
            IDatabase db = redisMuxer.GetDatabase();
            string key = GetLeaderboardKey(season, mode);
            SortedSetEntry[] entries = await db.SortedSetRangeByRankWithScoresAsync(
                key,
                0,
                limit - 1,
                Order.Descending).ConfigureAwait(false);

            long currentRank = 1;
            foreach (SortedSetEntry entry in entries)
                if (int.TryParse(entry.Element.ToString(), out int accountId))
                {
                    results.Add((accountId, entry.Score, currentRank));
                    currentRank++;
                }
        }
        catch (Exception ex)
        {
            if (logger?.IsEnabled(LogLevel.Warning) == true)
                logger.LogWarning(ex, "[RedisLeaderboard] Failed to get top ranked players for {Season}/{Mode}", season,
                    mode);
        }

        return results;
    }

    public async Task<long?> GetPlayerRankAsync(
        string season,
        string mode,
        int accountId,
        CancellationToken cancellationToken = default)
    {
        if (redisMuxer is not { IsConnected: true }) return null;

        try
        {
            IDatabase db = redisMuxer.GetDatabase();
            string key = GetLeaderboardKey(season, mode);
            long? zeroBasedRank = await db.SortedSetRankAsync(key, accountId.ToString(), Order.Descending)
                .ConfigureAwait(false);
            return zeroBasedRank + 1;
        }
        catch (Exception ex)
        {
            if (logger?.IsEnabled(LogLevel.Warning) == true)
                logger.LogWarning(ex,
                    "[RedisLeaderboard] Failed to get rank for Account {AccountId} in {Season}/{Mode}",
                    accountId, season, mode);

            return null;
        }
    }

    public async Task<double?> GetPlayerScoreAsync(
        string season,
        string mode,
        int accountId,
        CancellationToken cancellationToken = default)
    {
        if (redisMuxer is not { IsConnected: true }) return null;

        try
        {
            IDatabase db = redisMuxer.GetDatabase();
            string key = GetLeaderboardKey(season, mode);
            return await db.SortedSetScoreAsync(key, accountId.ToString()).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (logger?.IsEnabled(LogLevel.Warning) == true)
                logger.LogWarning(ex,
                    "[RedisLeaderboard] Failed to get score for Account {AccountId} in {Season}/{Mode}",
                    accountId, season, mode);

            return null;
        }
    }

    private static string GetLeaderboardKey(string season, string mode)
    {
        string cleanSeason = string.IsNullOrWhiteSpace(season) ? "global" : season.Trim().ToLowerInvariant();
        string cleanMode = string.IsNullOrWhiteSpace(mode) ? "all" : mode.Trim().ToLowerInvariant();
        return $"leaderboard:{cleanSeason}:{cleanMode}";
    }
}
