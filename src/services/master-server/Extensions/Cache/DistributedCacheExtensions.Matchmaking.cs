// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using Microsoft.EntityFrameworkCore;
using SharedCache = ASPIRE.Common.Extensions.Cache.DistributedCacheExtensions;

namespace KONGOR.MasterServer.Extensions.Cache;

// =====================================================================================================================
//  Match Server / Server Manager / Match Start Data cache access.
//
//  SINGLE SOURCE OF TRUTH: ASPIRE.Common.Extensions.Cache.DistributedCacheExtensions (src/libs/protocol-core).
//  The chat server and the master server read and write the SAME Redis keys (MATCH-SERVERS, MATCH-SERVER-INDEX,
//  MATCH-SERVER-SESSIONS, MATCH-SERVER-MANAGERS, MATCH-START-DATA:*, MATCH-PLAYERS:*, MATCH-ID-COUNTER, MATCH-LEASE:*).
//  Previously this file was a hand-maintained copy of the protocol-core implementation and the two had already
//  drifted (e.g. the session-cookie lookup). These members are now thin delegates so existing master-server call
//  sites (which resolve via the KONGOR.MasterServer.Extensions.Cache global using) keep compiling unchanged.
//
//  DO NOT re-implement logic here. Change protocol-core instead.
// =====================================================================================================================
public static partial class DistributedCacheExtensions
{
    private const string MatchIdCounterKey = "MATCH-ID-COUNTER";

    #region Match Server Managers

    public static Task SetMatchServerManager(this IDatabase distributedCacheStore, string hostAccountName,
        MatchServerManager matchServerManager) =>
        SharedCache.SetMatchServerManager(distributedCacheStore, hostAccountName, matchServerManager);

    public static Task<List<MatchServerManager>> GetMatchServerManagers(this IDatabase distributedCacheStore) =>
        SharedCache.GetMatchServerManagers(distributedCacheStore);

    public static Task<MatchServerManager?> GetMatchServerManagerByIPAddress(this IDatabase distributedCacheStore,
        string ipAddress) =>
        SharedCache.GetMatchServerManagerByIPAddress(distributedCacheStore, ipAddress);

    public static Task<MatchServerManager?> GetMatchServerManagerBySessionCookie(
        this IDatabase distributedCacheStore, string sessionCookie) =>
        SharedCache.GetMatchServerManagerBySessionCookie(distributedCacheStore, sessionCookie);

    public static Task<List<MatchServerManager>> GetMatchServerManagersByAccountName(
        this IDatabase distributedCacheStore, string hostAccountName) =>
        SharedCache.GetMatchServerManagersByAccountName(distributedCacheStore, hostAccountName);

    public static Task<MatchServerManager?> GetMatchServerManagerByID(this IDatabase distributedCacheStore,
        int serverManagerID) =>
        SharedCache.GetMatchServerManagerByID(distributedCacheStore, serverManagerID);

    public static Task RemoveMatchServerManagerByID(this IDatabase distributedCacheStore, int serverManagerID) =>
        SharedCache.RemoveMatchServerManagerByID(distributedCacheStore, serverManagerID);

    #endregion

    #region Match Servers

    public static Task SetMatchServer(this IDatabase distributedCacheStore, string hostAccountName,
        MatchServer matchServer) =>
        SharedCache.SetMatchServer(distributedCacheStore, hostAccountName, matchServer);

    public static Task<List<MatchServer>> GetMatchServers(this IDatabase distributedCacheStore) =>
        SharedCache.GetMatchServers(distributedCacheStore);

    public static Task<MatchServer?> GetMatchServerByIPAddressAndPort(this IDatabase distributedCacheStore,
        string ipAddress, int port) =>
        SharedCache.GetMatchServerByIPAddressAndPort(distributedCacheStore, ipAddress, port);

    /// <summary>
    ///     O(1) MATCH-SERVER-SESSIONS index lookup first; on index miss performs ONE raw scan of MATCH-SERVERS
    ///     (no stale filtering) and backfills the index. See protocol-core for details.
    /// </summary>
    public static Task<MatchServer?> GetMatchServerBySessionCookie(this IDatabase distributedCacheStore,
        string sessionCookie) =>
        SharedCache.GetMatchServerBySessionCookie(distributedCacheStore, sessionCookie);

    public static Task<MatchServer?> ResurrectMatchServerBySession(this IDatabase distributedCacheStore,
        string sessionCookie) =>
        SharedCache.ResurrectMatchServerBySession(distributedCacheStore, sessionCookie);

    public static Task<List<MatchServer>> GetMatchServersByAccountName(this IDatabase distributedCacheStore,
        string hostAccountName) =>
        SharedCache.GetMatchServersByAccountName(distributedCacheStore, hostAccountName);

    public static Task<MatchServer?> GetMatchServerByID(this IDatabase distributedCacheStore, int serverID) =>
        SharedCache.GetMatchServerByID(distributedCacheStore, serverID);

    public static Task RemoveMatchServerSessionCookie(this IDatabase distributedCacheStore, string? sessionCookie) =>
        SharedCache.RemoveMatchServerSessionCookie(distributedCacheStore, sessionCookie);

    public static Task RemoveMatchServerByID(this IDatabase distributedCacheStore, int serverID) =>
        SharedCache.RemoveMatchServerByID(distributedCacheStore, serverID);

    #endregion

    #region Match Start Data & Connected Players

    /// <summary>
    ///     Stores match start data in the cache (6 hour TTL).
    ///     This data will be used to populate match statistics when the match ends.
    /// </summary>
    public static Task SetMatchStartData(this IDatabase distributedCacheStore, MatchStartData matchStartData) =>
        SharedCache.SetMatchStartData(distributedCacheStore, matchStartData);

    /// <summary>
    ///     Retrieves match start data from the cache by match ID.
    ///     Returns NULL if no match start data is found for the given match ID.
    /// </summary>
    public static Task<MatchStartData?> GetMatchStartData(this IDatabase distributedCacheStore, int matchID) =>
        SharedCache.GetMatchStartData(distributedCacheStore, matchID);

    public static Task<MatchStartData?> GetMatchStartDataByMatchServerID(this IDatabase distributedCacheStore,
        int serverID) =>
        SharedCache.GetMatchStartDataByMatchServerID(distributedCacheStore, serverID);

    public static Task<MatchStartData?> GetMatchStartDataByMatchServerSessionCookie(
        this IDatabase distributedCacheStore, string sessionCookie) =>
        SharedCache.GetMatchStartDataByMatchServerSessionCookie(distributedCacheStore, sessionCookie);

    /// <summary>
    ///     Removes match start data from the cache.
    ///     This should be called after full match statistics have been created.
    /// </summary>
    public static Task RemoveMatchStartData(this IDatabase distributedCacheStore, int matchID) =>
        SharedCache.RemoveMatchStartData(distributedCacheStore, matchID);

    public static Task AddMatchConnectedPlayer(this IDatabase distributedCacheStore, int matchID,
        string accountName) =>
        SharedCache.AddMatchConnectedPlayer(distributedCacheStore, matchID, accountName);

    public static Task<List<string>> GetMatchConnectedPlayers(this IDatabase distributedCacheStore, int matchID) =>
        SharedCache.GetMatchConnectedPlayers(distributedCacheStore, matchID);

    #endregion

    #region Match IDs & Leases

    /// <summary>
    ///     Initializes the match ID counter in Redis from the maximum value in the database.
    ///     This ensures that generated match IDs continue from the last known ID and remain unique.
    ///     NOTE: Stays in master-server because it depends on <see cref="MerrickContext" />.
    /// </summary>
    public static async Task InitializeMatchIdCounter(this IDatabase distributedCacheStore,
        MerrickContext databaseContext)
    {
        int maxRecordId = 0;
        int maxStatsId = 0;

        if (databaseContext.Database.IsNpgsql())
        {
            bool matchRecordsExist = await databaseContext.Database
                .SqlQueryRaw<bool>("SELECT to_regclass('stat.\"MatchRecords\"') IS NOT NULL AS \"Value\"")
                .SingleAsync();

            bool matchStatsExist = await databaseContext.Database
                .SqlQueryRaw<bool>("SELECT to_regclass('stat.\"MatchStatistics\"') IS NOT NULL AS \"Value\"")
                .SingleAsync();

            if (!matchRecordsExist || !matchStatsExist)
            {
                Console.WriteLine("[Standalone] Match tables not yet created; seeding Redis match counter baseline=1000");
            }

            if (matchRecordsExist)
            {
                maxRecordId = await databaseContext.MatchRecords.MaxAsync(m => (int?)m.MatchID) ?? 0;
            }

            if (matchStatsExist)
            {
                maxStatsId = await databaseContext.MatchStatistics.MaxAsync(m => (int?)m.MatchID) ?? 0;
            }
        }
        else
        {
            maxRecordId = await databaseContext.MatchRecords.MaxAsync(m => (int?)m.MatchID) ?? 0;
            maxStatsId = await databaseContext.MatchStatistics.MaxAsync(m => (int?)m.MatchID) ?? 0;
        }

        int maxDbId = Math.Max(maxRecordId, maxStatsId);

        // Ensure baseline of at least 1000 so fresh deployments start with clean 4-digit IDs
        if (maxDbId < 1000) maxDbId = 1000;

        // Get the current value from Redis
        RedisValue cachedValue = await distributedCacheStore.StringGetAsync(MatchIdCounterKey);
        int currentRedisId = cachedValue.HasValue ? (int)cachedValue : 0;

        // If Redis is behind the DB (or empty), update it to match the DB
        if (maxDbId > currentRedisId) await distributedCacheStore.StringSetAsync(MatchIdCounterKey, maxDbId);
    }

    /// <summary>
    ///     Generates the next unique, incremental match ID using Redis atomic increment.
    /// </summary>
    public static Task<int> GenerateNextMatchID(this IDatabase distributedCacheStore) =>
        SharedCache.GenerateNextMatchID(distributedCacheStore);

    /// <summary>
    ///     Gets the active Match ID leased to a server/session/map composite key.
    /// </summary>
    public static Task<int?> GetActiveMatchLease(this IDatabase distributedCacheStore, string leaseKey) =>
        SharedCache.GetActiveMatchLease(distributedCacheStore, leaseKey);

    /// <summary>
    ///     Stores an active Match ID lease with a 15-minute sliding TTL to prevent dual-assignment.
    /// </summary>
    public static Task SetActiveMatchLease(this IDatabase distributedCacheStore, string leaseKey, int matchId,
        TimeSpan? expiry = null) =>
        SharedCache.SetActiveMatchLease(distributedCacheStore, leaseKey, matchId, expiry);

    /// <summary>
    ///     Explicitly releases an active match lease when a match completes, aborts, or is recycled.
    /// </summary>
    public static Task RemoveActiveMatchLease(this IDatabase distributedCacheStore, string leaseKey) =>
        SharedCache.RemoveActiveMatchLease(distributedCacheStore, leaseKey);

    #endregion
}
