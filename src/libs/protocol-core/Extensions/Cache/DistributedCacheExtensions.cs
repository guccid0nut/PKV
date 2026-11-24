// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ASPIRE.Common.Models.ServerManagement;
using ASPIRE.Common.Network;
using Kongor.Core.Serialization.JSON;
using NEXUS.Contracts.Security;
using StackExchange.Redis;
using MemoryPack;

namespace ASPIRE.Common.Extensions.Cache;

// ReSharper disable UnusedMember.Global
// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable ConvertToExtensionBlock
// ReSharper disable InconsistentNaming
// ReSharper disable SimplifyConditionalTernaryExpression
// ReSharper disable RedundantSuppressNullableWarningExpression
// ReSharper disable RedundantNameQualifier
// ReSharper disable UseIndexFromEndExpression
// ReSharper disable UseNullPropagation
public static class DistributedCacheExtensions
{
    #region SRP Authentication Session Data

    private static string ConstructSRPAuthenticationSessionDataKey(string accountName)
    {
        string normalized = accountName.ToLowerInvariant();
        const string prefix = "SRP-SESSION-DATA:[\"";
        const string suffix = "\"]";
        int totalLength = prefix.Length + normalized.Length + suffix.Length;

        return string.Create(totalLength, (prefix, normalized, suffix), static (span, state) =>
        {
            state.prefix.AsSpan().CopyTo(span);
            int pos = state.prefix.Length;
            state.normalized.AsSpan().CopyTo(span[pos..]);
            pos += state.normalized.Length;
            state.suffix.AsSpan().CopyTo(span[pos..]);
        });
    }

    public static async Task SetSRPAuthenticationSessionData<T>(this IDatabase distributedCacheStore,
        string accountName,
        T data,
        JsonTypeInfo<T> jsonTypeInfo)
    {
        byte[] serializedData = JsonSerializer.SerializeToUtf8Bytes(data, jsonTypeInfo);

        await distributedCacheStore.StringSetAsync(ConstructSRPAuthenticationSessionDataKey(accountName),
            serializedData, TimeSpan.FromSeconds(30));
    }

    public static async Task<T?> GetSRPAuthenticationSessionData<T>(
        this IDatabase distributedCacheStore, string accountName,
        JsonTypeInfo<T> jsonTypeInfo)
    {
        RedisValue cachedValue =
            await distributedCacheStore.StringGetAsync(ConstructSRPAuthenticationSessionDataKey(accountName));

        if (cachedValue.IsNullOrEmpty) return default;

        byte[] bytes = (byte[]?)cachedValue ?? Encoding.UTF8.GetBytes(cachedValue.ToString()!);
        return JsonSerializer.Deserialize(bytes, jsonTypeInfo);
    }

    public static async Task RemoveSRPAuthenticationSessionData(this IDatabase distributedCacheStore,
        string accountName)
    {
        await distributedCacheStore.KeyDeleteAsync(ConstructSRPAuthenticationSessionDataKey(accountName));
    }

    private static string ConstructSRPAuthenticationSystemInformationKey(string accountName)
    {
        string normalized = accountName.ToLowerInvariant();
        const string prefix = "SYSTEM-INFORMATION:[\"";
        const string suffix = "\"]";
        int totalLength = prefix.Length + normalized.Length + suffix.Length;

        return string.Create(totalLength, (prefix, normalized, suffix), static (span, state) =>
        {
            state.prefix.AsSpan().CopyTo(span);
            int pos = state.prefix.Length;
            state.normalized.AsSpan().CopyTo(span[pos..]);
            pos += state.normalized.Length;
            state.suffix.AsSpan().CopyTo(span[pos..]);
        });
    }

    public static async Task SetSRPAuthenticationSystemInformation(this IDatabase distributedCacheStore,
        string accountName, string systemInformation)
    {
        await distributedCacheStore.StringSetAsync(ConstructSRPAuthenticationSystemInformationKey(accountName),
            systemInformation, TimeSpan.FromSeconds(30));
    }

    public static async Task<string?> GetSRPAuthenticationSystemInformation(this IDatabase distributedCacheStore,
        string accountName)
    {
        RedisValue cachedValue =
            await distributedCacheStore.StringGetAsync(ConstructSRPAuthenticationSystemInformationKey(accountName));

        return cachedValue.IsNullOrEmpty ? null : cachedValue.ToString();
    }

    public static async Task RemoveSRPAuthenticationSystemInformation(this IDatabase distributedCacheStore,
        string accountName)
    {
        await distributedCacheStore.KeyDeleteAsync(ConstructSRPAuthenticationSystemInformationKey(accountName));
    }

    #endregion

    # region Account Session Cookies

    private static string ConstructAccountSessionCookieKey(string cookie)
    {
        const string prefix = "ACCOUNT-SESSION-COOKIE:[\"";
        const string suffix = "\"]";
        int totalLength = prefix.Length + cookie.Length + suffix.Length;

        return string.Create(totalLength, (prefix, cookie, suffix), static (span, state) =>
        {
            state.prefix.AsSpan().CopyTo(span);
            int pos = state.prefix.Length;
            state.cookie.AsSpan().CopyTo(span[pos..]);
            pos += state.cookie.Length;
            state.suffix.AsSpan().CopyTo(span[pos..]);
        });
    }

    public const string InvalidSessionSentinel = "__INVALID__";
    public static readonly TimeSpan NegativeCacheExpiration = TimeSpan.FromSeconds(60);

    public static async Task SetInvalidSessionCookie(this IDatabase distributedCacheStore, string cookie,
        ILogger? logger = null)
    {
        try
        {
            await distributedCacheStore.StringSetAsync(ConstructAccountSessionCookieKey(cookie), InvalidSessionSentinel,
                NegativeCacheExpiration);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex,
                "[DistributedCacheExtensions] Redis operation failed to record invalid session cookie prefix '{CookiePrefix}'",
                SecurityMasking.MaskToken(cookie));
        }
    }

    public static async Task SetAccountNameForSessionCookie(this IDatabase distributedCacheStore, string cookie,
        string accountName, ILogger? logger = null)
    {
        try
        {
            await distributedCacheStore.StringSetAsync(ConstructAccountSessionCookieKey(cookie), accountName,
                TimeSpan.FromHours(24));

            if (Guid.TryParse(cookie, out Guid guid))
            {
                string dashedKey = ConstructAccountSessionCookieKey(guid.ToString());
                string noDashKey = ConstructAccountSessionCookieKey(guid.ToString("N"));

                await distributedCacheStore.StringSetAsync(dashedKey, accountName, TimeSpan.FromHours(24));
                await distributedCacheStore.StringSetAsync(noDashKey, accountName, TimeSpan.FromHours(24));
            }

            string activeCookiesKey = ConstructAccountActiveCookiesKey(accountName);
            await distributedCacheStore.SetAddAsync(activeCookiesKey, cookie);
            SafeKeyExpire(distributedCacheStore, activeCookiesKey);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex,
                "[DistributedCacheExtensions] Redis operation failed for cookie prefix '{CookiePrefix}' (Account: '{AccountName}'). Fallback: '{FallbackTarget}'",
                SecurityMasking.MaskToken(cookie),
                accountName,
                "None");
        }
    }

    public static async Task<string?> GetAccountNameForSessionCookie(this IDatabase distributedCacheStore,
        string cookie, ILogger? logger = null)
    {
        try
        {
            string primaryKey = ConstructAccountSessionCookieKey(cookie);
            RedisValue cachedValue = await distributedCacheStore.StringGetAsync(primaryKey);
            if (cachedValue == InvalidSessionSentinel)
            {
                return null;
            }

            if (!cachedValue.IsNullOrEmpty)
            {
                SafeKeyExpire(distributedCacheStore, primaryKey);
                return cachedValue.ToString();
            }

            // Fuzzy 1: If 32-char dash-less GUID, try dashed GUID format
            if (cookie.Length == 32 && Guid.TryParse(cookie, out Guid guid1))
            {
                string dashedKey = ConstructAccountSessionCookieKey(guid1.ToString());
                RedisValue dashedVal = await distributedCacheStore.StringGetAsync(dashedKey);
                if (dashedVal == InvalidSessionSentinel)
                {
                    return null;
                }

                if (!dashedVal.IsNullOrEmpty)
                {
                    SafeKeyExpire(distributedCacheStore, dashedKey);
                    return dashedVal.ToString();
                }
            }

            // Fuzzy 2: If dashed GUID, try dash-less format
            if (cookie.Contains('-') && Guid.TryParse(cookie, out Guid guid2))
            {
                string noDashKey = ConstructAccountSessionCookieKey(guid2.ToString("N"));
                RedisValue noDashVal = await distributedCacheStore.StringGetAsync(noDashKey);
                if (noDashVal == InvalidSessionSentinel)
                {
                    return null;
                }

                if (!noDashVal.IsNullOrEmpty)
                {
                    SafeKeyExpire(distributedCacheStore, noDashKey);
                    return noDashVal.ToString();
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex,
                "[DistributedCacheExtensions] Redis operation failed for cookie prefix '{CookiePrefix}' (Account: '{AccountName}'). Fallback: '{FallbackTarget}'",
                SecurityMasking.MaskToken(cookie),
                "<unknown>",
                "None");
            return null;
        }
    }

    private static void SafeKeyExpire(IDatabase db, string key)
    {
        try
        {
            _ = db.KeyExpireAsync(key, TimeSpan.FromHours(24));
        }
        catch
        {
            // Ignored for test stubs that do not support KeyExpireAsync
        }
    }

    public static async Task RemoveAccountNameForSessionCookie(this IDatabase distributedCacheStore, string cookie,
        ILogger? logger = null)
    {
        try
        {
            await distributedCacheStore.KeyDeleteAsync(ConstructAccountSessionCookieKey(cookie));

            if (Guid.TryParse(cookie, out Guid guid))
            {
                await distributedCacheStore.KeyDeleteAsync(ConstructAccountSessionCookieKey(guid.ToString()));
                await distributedCacheStore.KeyDeleteAsync(ConstructAccountSessionCookieKey(guid.ToString("N")));
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex,
                "[DistributedCacheExtensions] Redis operation failed for cookie prefix '{CookiePrefix}' (Account: '{AccountName}'). Fallback: '{FallbackTarget}'",
                SecurityMasking.MaskToken(cookie),
                "<unknown>",
                "None");
        }
    }

    public static async Task RevokeAllSessionsForAccount(this IDatabase distributedCacheStore, string accountName,
        ILogger? logger = null)
    {
        try
        {
            string activeCookiesKey = ConstructAccountActiveCookiesKey(accountName);
            RedisValue[] cookies = await distributedCacheStore.SetMembersAsync(activeCookiesKey);
            foreach (RedisValue c in cookies)
            {
                if (!c.IsNullOrEmpty)
                {
                    await distributedCacheStore.RemoveAccountNameForSessionCookie(c.ToString()!, logger);
                }
            }

            await distributedCacheStore.KeyDeleteAsync(activeCookiesKey);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex,
                "[DistributedCacheExtensions] Redis operation failed to revoke active sessions for account '{AccountName}'",
                accountName);
        }
    }

    private static string ConstructAccountActiveCookiesKey(string accountName)
    {
        return $"ACCOUNT-ACTIVE-COOKIES:[\"{accountName.ToLowerInvariant()}\"]";
    }

    public static async Task<(bool IsValid, string? AccountName)> ValidateAccountSessionCookie(
        this IDatabase distributedCacheStore, string cookie, ILogger? logger = null)
    {
        string? accountName = await distributedCacheStore.GetAccountNameForSessionCookie(cookie, logger);
        return (accountName is not null, accountName);
    }

    public static async Task PurgeSessionCookies(this IConnectionMultiplexer distributedCacheProvider)
    {
        IDatabase distributedCacheStore = distributedCacheProvider.GetDatabase();

        long cursor = 0;
        const int pageSize = 1000;

        do
        {
            RedisResult redisResult = await distributedCacheStore.ExecuteAsync("SCAN",
                cursor.ToString(CultureInfo.InvariantCulture), "MATCH", "ACCOUNT-SESSION-COOKIE:*", "COUNT",
                pageSize.ToString(CultureInfo.InvariantCulture));

            RedisResult[]? innerResult = (RedisResult[]?)redisResult;
            if (innerResult is null || innerResult.Length < 2) break;

            string? cursorStr = (string?)innerResult[0];
            if (cursorStr is null) break;

            cursor = long.Parse(cursorStr, CultureInfo.InvariantCulture);

            RedisKey[]? keys = (RedisKey[]?)innerResult[1];
            if (keys is not null && keys.Length > 0) await distributedCacheStore.KeyDeleteAsync(keys);
        } while (cursor != 0);
    }

    #endregion

    # region Match Server & Server Manager Management

    private static byte[] SerializeMatchServerManager(MatchServerManager manager) =>
        MemoryPackSerializer.Serialize(manager);

    private static MatchServerManager? DeserializeMatchServerManager(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return null;
        try
        {
            if (bytes[0] == (byte)'{')
                return JsonSerializer.Deserialize(bytes, KongorCoreJsonContext.Default.MatchServerManager);
            return MemoryPackSerializer.Deserialize<MatchServerManager>(bytes);
        }
        catch
        {
            return null;
        }
    }

    private static byte[] SerializeMatchServer(MatchServer server) =>
        MemoryPackSerializer.Serialize(server);

    private static MatchServer? DeserializeMatchServer(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return null;
        try
        {
            if (bytes[0] == (byte)'{')
                return JsonSerializer.Deserialize(bytes, KongorCoreJsonContext.Default.MatchServer);
            return MemoryPackSerializer.Deserialize<MatchServer>(bytes);
        }
        catch
        {
            return null;
        }
    }

    private static byte[] SerializeMatchStartData(MatchStartData data) =>
        MemoryPackSerializer.Serialize(data);

    private static MatchStartData? DeserializeMatchStartData(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return null;
        try
        {
            if (bytes[0] == (byte)'{')
                return JsonSerializer.Deserialize(bytes, KongorCoreJsonContext.Default.MatchStartData);
            return MemoryPackSerializer.Deserialize<MatchStartData>(bytes);
        }
        catch
        {
            return null;
        }
    }

    private const string MatchServerManagersKey = "MATCH-SERVER-MANAGERS";
    private const string MatchServersKey = "MATCH-SERVERS";
    private const string MatchServerIndexKey = "MATCH-SERVER-INDEX";
    private const string MatchServerSessionsKey = "MATCH-SERVER-SESSIONS";
    private const string MatchIdCounterKey = "MATCH-ID-COUNTER";

    public static async Task SetMatchServerManager(this IDatabase distributedCacheStore, string hostAccountName,
        MatchServerManager matchServerManager)
    {
        byte[] serializedMatchServerManager = SerializeMatchServerManager(matchServerManager);

        await distributedCacheStore.HashSetAsync(MatchServerManagersKey,
            [new HashEntry(hostAccountName, serializedMatchServerManager)]);
    }

    public static async Task<List<MatchServerManager>> GetMatchServerManagers(this IDatabase distributedCacheStore)
    {
        HashEntry[] serializedMatchServerManagers = await distributedCacheStore.HashGetAllAsync(MatchServerManagersKey);

        List<MatchServerManager> matchServerManagers = [];
        foreach (HashEntry entry in serializedMatchServerManagers)
        {
            if (entry.Value.HasValue)
            {
                try
                {
                    MatchServerManager? manager = DeserializeMatchServerManager((byte[])entry.Value!);
                    if (manager != null) matchServerManagers.Add(manager);
                }
                catch
                {
                    // Ignore invalid cache entries
                }
            }
        }

        return matchServerManagers;
    }

    public static async Task<MatchServerManager?> GetMatchServerManagerByIPAddress(this IDatabase distributedCacheStore,
        string ipAddress)
    {
        return (await distributedCacheStore.GetMatchServerManagers()).FirstOrDefault(manager =>
            manager.IPAddress.Equals(ipAddress, StringComparison.Ordinal));
    }

    public static async Task<MatchServerManager?> GetMatchServerManagerBySessionCookie(
        this IDatabase distributedCacheStore, string sessionCookie)
    {
        return (await distributedCacheStore.GetMatchServerManagers()).FirstOrDefault(manager =>
            manager.Cookie.Equals(sessionCookie, StringComparison.Ordinal));
    }

    public static async Task<List<MatchServerManager>> GetMatchServerManagersByAccountName(
        this IDatabase distributedCacheStore, string hostAccountName)
    {
        List<MatchServerManager> matchServerManagers = [];

        IAsyncEnumerable<HashEntry>? scanResult =
            distributedCacheStore.HashScanAsync(MatchServerManagersKey, hostAccountName, int.MaxValue);

        // NOTE: Mocked IDatabase instances (unit tests) may return null here.
        if (scanResult is null) return matchServerManagers;

        await foreach (HashEntry entry in scanResult)
        {
            if (entry.Value.IsNullOrEmpty) continue;

            MatchServerManager? matchServerManager = DeserializeMatchServerManager((byte[])entry.Value!);
            if (matchServerManager != null)
                matchServerManagers.Add(matchServerManager);
        }

        return matchServerManagers;
    }

    public static async Task<MatchServerManager?> GetMatchServerManagerByID(this IDatabase distributedCacheStore,
        int serverManagerID)
    {
        return (await distributedCacheStore.GetMatchServerManagers()).FirstOrDefault(manager =>
            manager.ID == serverManagerID);
    }

    public static async Task RemoveMatchServerManagerByID(this IDatabase distributedCacheStore, int serverManagerID)
    {
        MatchServerManager? matchServerManager = await distributedCacheStore.GetMatchServerManagerByID(serverManagerID);

        if (matchServerManager is not null)
        {
            string hostAccountName = matchServerManager.HostAccountName;

            await distributedCacheStore.HashDeleteAsync(MatchServerManagersKey, hostAccountName);

            foreach (int matchServerID in matchServerManager.MatchServerIDs)
            {
                MatchServer? matchServer = await distributedCacheStore.GetMatchServerByID(matchServerID);

                if (matchServer is null) continue;

                matchServer.MatchServerManagerID = null;
                await distributedCacheStore.RemoveMatchServerByID(matchServer.ID);
            }

            matchServerManager.MatchServerIDs.Clear();
        }
    }

    public static async Task SetMatchServer(this IDatabase distributedCacheStore, string hostAccountName,
        MatchServer matchServer)
    {
        byte[] serializedMatchServer = SerializeMatchServer(matchServer);
        string hashField = $"{hostAccountName}:{matchServer.Port}";

        await distributedCacheStore.HashSetAsync(MatchServersKey,
            [new HashEntry(hashField, serializedMatchServer)]);

        await distributedCacheStore.HashSetAsync(MatchServerIndexKey,
            [new HashEntry(matchServer.ID, hashField)]);

        if (!string.IsNullOrEmpty(matchServer.Cookie))
        {
            await distributedCacheStore.HashSetAsync(MatchServerSessionsKey,
                [new HashEntry(matchServer.Cookie, serializedMatchServer)]);
        }
    }

    public static async Task<List<MatchServer>> GetMatchServers(this IDatabase distributedCacheStore)
    {
        HashEntry[] serializedMatchServers = await distributedCacheStore.HashGetAllAsync(MatchServersKey);

        List<MatchServer> matchServers = [];
        DateTimeOffset now = TimeProvider.System.GetUtcNow();
        TimeSpan staleThreshold = TimeSpan.FromMinutes(2);
        TimeSpan deadPurgeThreshold = TimeSpan.FromMinutes(30);
        List<RedisValue> staleFields = [];

        foreach (HashEntry entry in serializedMatchServers)
        {
            if (entry.Value.HasValue)
            {
                try
                {
                    MatchServer? server = DeserializeMatchServer((byte[])entry.Value!);
                    if (server != null)
                    {
                        bool isStale = (now - server.TimestampLastHeartbeat > staleThreshold) &&
                                       (now - server.TimestampRegistered > staleThreshold);
                        bool isDead = (now - server.TimestampLastHeartbeat > deadPurgeThreshold) &&
                                      (now - server.TimestampRegistered > deadPurgeThreshold);
                        if (isDead)
                        {
                            staleFields.Add(entry.Name);
                        }
                        else if (!isStale)
                        {
                            matchServers.Add(server);
                        }
                    }
                }
                catch
                {
                    // Ignore and mark invalid cache entries for cleanup
                    staleFields.Add(entry.Name);
                }
            }
        }

        if (staleFields.Count > 0)
        {
            _ = distributedCacheStore.HashDeleteAsync(MatchServersKey, [.. staleFields]);
        }

        return matchServers;
    }

    public static async Task<MatchServer?> GetMatchServerByIPAddressAndPort(this IDatabase distributedCacheStore,
        string ipAddress, int port)
    {
        List<MatchServer> activeServers = await distributedCacheStore.GetMatchServers();

        // 1. Exact match on registered IPAddress, LocalIPAddress, or TruePublicIPAddress
        MatchServer? exactMatch = activeServers.FirstOrDefault(server =>
            (string.Equals(server.IPAddress, ipAddress, StringComparison.OrdinalIgnoreCase) && server.Port == port) ||
            (!string.IsNullOrWhiteSpace(server.LocalIPAddress) &&
             string.Equals(server.LocalIPAddress, ipAddress, StringComparison.OrdinalIgnoreCase) &&
             (server.LocalPort ?? server.Port) == port) ||
            (!string.IsNullOrWhiteSpace(server.TruePublicIPAddress) &&
             string.Equals(server.TruePublicIPAddress, ipAddress, StringComparison.OrdinalIgnoreCase) &&
             server.Port == port));

        if (exactMatch is not null)
        {
            return exactMatch;
        }

        // 2. Split-horizon fallback for self-hosted servers registered on loopback or RFC1918/VPN IP
        //    when a LAN or external WAN client connects via the resolved LAN/WAN address on the same port.
        List<MatchServer> selfHostedCandidates =
        [
            .. activeServers.Where(server =>
                (server.Port == port || server.LocalPort == port) &&
                (SplitHorizonEndpointResolver.IsLoopbackOrLocalhost(server.IPAddress) ||
                 SplitHorizonEndpointResolver.IsPrivateOrVpnIPString(server.IPAddress)))
        ];

        if (selfHostedCandidates.Count == 1)
        {
            MatchServer matched = selfHostedCandidates[0];
            Debug.WriteLine(
                $"[ChatRouting] Matched split-horizon client target {ipAddress}:{port} -> MatchServer #{matched.ID} ({matched.IPAddress}:{matched.Port})");
            return matched;
        }

        return null;
    }

    public static async Task<MatchServer?> GetMatchServerBySessionCookie(this IDatabase distributedCacheStore,
        string sessionCookie)
    {
        if (string.IsNullOrWhiteSpace(sessionCookie))
            return null;

        // 1. O(1) fast path: MATCH-SERVER-SESSIONS secondary index (cookie -> serialized MatchServer).
        //    NOTE: This index intentionally outlives the stale/dead filtering applied to MATCH-SERVERS so that
        //    servers which stopped heart-beating mid-match can still submit stats ("resurrection").
        //    SetMatchServer() rewrites the index blob on every update, so it is as fresh as MATCH-SERVERS.
        MatchServer? indexed = await distributedCacheStore.TryGetMatchServerFromSessionIndex(sessionCookie);
        if (indexed != null)
            return indexed;

        // 2. Slow path (index miss): ONE raw scan of MATCH-SERVERS without stale filtering. This covers both live
        //    and stale-but-not-yet-purged servers (previously this path issued two full HGETALL scans).
        MatchServer? scanned = await distributedCacheStore.ScanMatchServersForSessionCookie(sessionCookie);
        if (scanned != null && !string.IsNullOrEmpty(scanned.Cookie))
        {
            // Backfill the index so the next lookup for this cookie is O(1).
            await distributedCacheStore.HashSetAsync(MatchServerSessionsKey,
                [new HashEntry(scanned.Cookie, SerializeMatchServer(scanned))]);
        }

        return scanned;
    }

    public static async Task<MatchServer?> ResurrectMatchServerBySession(this IDatabase distributedCacheStore,
        string sessionCookie)
    {
        if (string.IsNullOrWhiteSpace(sessionCookie))
            return null;

        // 1. Check MATCH-SERVER-SESSIONS persistent index
        MatchServer? indexed = await distributedCacheStore.TryGetMatchServerFromSessionIndex(sessionCookie);
        if (indexed != null)
            return indexed;

        // 2. Scan raw MATCH-SERVERS entries directly without stale threshold filtering
        return await distributedCacheStore.ScanMatchServersForSessionCookie(sessionCookie);
    }

    private static async Task<MatchServer?> TryGetMatchServerFromSessionIndex(this IDatabase distributedCacheStore,
        string sessionCookie)
    {
        RedisValue sessionEntry = await distributedCacheStore.HashGetAsync(MatchServerSessionsKey, sessionCookie);
        return sessionEntry.IsNullOrEmpty ? null : DeserializeMatchServer((byte[])sessionEntry!);
    }

    private static async Task<MatchServer?> ScanMatchServersForSessionCookie(this IDatabase distributedCacheStore,
        string sessionCookie)
    {
        HashEntry[]? rawEntries = await distributedCacheStore.HashGetAllAsync(MatchServersKey);
        if (rawEntries is null)
            return null;

        foreach (HashEntry entry in rawEntries)
        {
            if (!entry.Value.HasValue)
                continue;

            MatchServer? server = DeserializeMatchServer((byte[])entry.Value!);
            if (server != null && string.Equals(server.Cookie, sessionCookie, StringComparison.OrdinalIgnoreCase))
                return server;
        }

        return null;
    }

    public static async Task<List<MatchServer>> GetMatchServersByAccountName(this IDatabase distributedCacheStore,
        string hostAccountName)
    {
        List<MatchServer> matchServers = [];

        IAsyncEnumerable<HashEntry>? scanResult =
            distributedCacheStore.HashScanAsync(MatchServersKey, $"{hostAccountName}:*", int.MaxValue);

        // NOTE: Mocked IDatabase instances (unit tests) may return null here.
        if (scanResult is null) return matchServers;

        await foreach (HashEntry entry in scanResult)
        {
            if (entry.Value.IsNullOrEmpty) continue;
            byte[] serializedMatchServer = (byte[])entry.Value!;

            MatchServer? matchServer = DeserializeMatchServer(serializedMatchServer);
            if (matchServer != null)
                matchServers.Add(matchServer);
        }

        return matchServers;
    }

    public static async Task<MatchServer?> GetMatchServerByID(this IDatabase distributedCacheStore, int serverID)
    {
        RedisValue hashField = await distributedCacheStore.HashGetAsync(MatchServerIndexKey, serverID);

        if (!hashField.IsNullOrEmpty)
        {
            RedisValue serializedMatchServer = await distributedCacheStore.HashGetAsync(MatchServersKey, hashField);

            if (!serializedMatchServer.IsNullOrEmpty)
            {
                try
                {
                    MatchServer? server = DeserializeMatchServer((byte[])serializedMatchServer!);
                    if (server != null) return server;
                }
                catch
                {
                    // Fall back to scanning if cache entry format is invalid
                }
            }
        }

        List<MatchServer> matchServers = await distributedCacheStore.GetMatchServers();
        MatchServer? matchServer = matchServers.FirstOrDefault(server => server.ID == serverID);

        if (matchServer is not null)
            await distributedCacheStore.HashSetAsync(MatchServerIndexKey,
                [new HashEntry(matchServer.ID, $"{matchServer.HostAccountName}:{matchServer.Port}")]);

        return matchServer;
    }

    public static async Task RemoveMatchServerSessionCookie(this IDatabase distributedCacheStore, string? sessionCookie)
    {
        if (string.IsNullOrWhiteSpace(sessionCookie))
        {
            return;
        }

        await distributedCacheStore.HashDeleteAsync(MatchServerSessionsKey, sessionCookie);
    }

    public static async Task RemoveMatchServerByID(this IDatabase distributedCacheStore, int serverID)
    {
        MatchServer? matchServer = await distributedCacheStore.GetMatchServerByID(serverID);

        if (matchServer is not null)
        {
            await distributedCacheStore.HashDeleteAsync(MatchServerIndexKey, serverID);

            string hashField = $"{matchServer.HostAccountName}:{matchServer.Port}";

            await distributedCacheStore.HashDeleteAsync(MatchServersKey, hashField);

            if (!string.IsNullOrEmpty(matchServer.Cookie))
            {
                await distributedCacheStore.HashDeleteAsync(MatchServerSessionsKey, matchServer.Cookie);
            }

            MatchServerManager? matchServerManager =
                await distributedCacheStore.GetMatchServerManagerByID(matchServer.MatchServerManagerID ?? 0);

            if (matchServerManager is not null)
            {
                matchServerManager.MatchServerIDs.Remove(matchServer.ID);

                if (matchServerManager.MatchServerIDs.Count == 0)
                    await distributedCacheStore.RemoveMatchServerManagerByID(matchServerManager.ID);
                else
                    await distributedCacheStore.SetMatchServerManager(matchServerManager.HostAccountName, matchServerManager);
            }

            matchServer.MatchServerManagerID = null;
        }
    }

    private static string ConstructMatchStartDataKey(int matchID)
    {
        const string prefix = "MATCH-START-DATA:[\"";
        const string suffix = "\"]";
        string matchIdStr = matchID.ToString(CultureInfo.InvariantCulture);
        int totalLength = prefix.Length + matchIdStr.Length + suffix.Length;

        return string.Create(totalLength, (prefix, matchIdStr, suffix), static (span, state) =>
        {
            state.prefix.AsSpan().CopyTo(span);
            int pos = state.prefix.Length;
            state.matchIdStr.AsSpan().CopyTo(span[pos..]);
            pos += state.matchIdStr.Length;
            state.suffix.AsSpan().CopyTo(span[pos..]);
        });
    }

    public static async Task SetMatchStartData(this IDatabase distributedCacheStore, MatchStartData matchStartData)
    {
        byte[] serializedMatchStartData = SerializeMatchStartData(matchStartData);

        await distributedCacheStore.StringSetAsync(ConstructMatchStartDataKey(matchStartData.MatchID),
            serializedMatchStartData, TimeSpan.FromHours(6));
    }

    public static async Task<MatchStartData?> GetMatchStartData(this IDatabase distributedCacheStore, int matchID)
    {
        RedisValue cachedValue = await distributedCacheStore.StringGetAsync(ConstructMatchStartDataKey(matchID));

        if (cachedValue.IsNullOrEmpty) return null;

        byte[] bytes = (byte[]?)cachedValue ?? Encoding.UTF8.GetBytes(cachedValue.ToString()!);
        return DeserializeMatchStartData(bytes);
    }

    public static async Task<MatchStartData?> GetMatchStartDataByMatchServerID(this IDatabase distributedCacheStore,
        int serverID)
    {
        EndPoint endPoint = distributedCacheStore.Multiplexer.GetEndPoints().Single();
        IServer server = distributedCacheStore.Multiplexer.GetServer(endPoint);

        List<MatchStartData> matches = [];

        foreach (RedisKey key in server.Keys(pattern: "MATCH-START-DATA:*"))
        {
            RedisValue cachedValue = await distributedCacheStore.StringGetAsync(key);

            if (cachedValue.IsNullOrEmpty) continue;

            byte[] bytes = (byte[]?)cachedValue ?? Encoding.UTF8.GetBytes(cachedValue.ToString()!);
            MatchStartData? matchStartData = DeserializeMatchStartData(bytes);

            if (matchStartData is not null && matchStartData.ServerID == serverID) matches.Add(matchStartData);
        }

        return matches.OrderByDescending(m => m.MatchID).FirstOrDefault();
    }

    public static async Task<MatchStartData?> GetMatchStartDataByMatchServerSessionCookie(
        this IDatabase distributedCacheStore, string sessionCookie)
    {
        MatchServer? matchServer = await distributedCacheStore.GetMatchServerBySessionCookie(sessionCookie);

        if (matchServer is null) return null;

        return await distributedCacheStore.GetMatchStartDataByMatchServerID(matchServer.ID);
    }

    public static async Task RemoveMatchStartData(this IDatabase distributedCacheStore, int matchID)
    {
        await distributedCacheStore.KeyDeleteAsync(ConstructMatchStartDataKey(matchID));
    }

    public static async Task AddMatchConnectedPlayer(this IDatabase distributedCacheStore, int matchID,
        string accountName)
    {
        string key = $"MATCH-PLAYERS:[\"{matchID}\"]";
        await distributedCacheStore.SetAddAsync(key, accountName);
        await distributedCacheStore.KeyExpireAsync(key, TimeSpan.FromHours(4));
    }

    public static async Task<List<string>> GetMatchConnectedPlayers(this IDatabase distributedCacheStore, int matchID)
    {
        string key = $"MATCH-PLAYERS:[\"{matchID}\"]";
        RedisValue[] values = await distributedCacheStore.SetMembersAsync(key);
        return [.. values.Select(value => value.ToString()).Where(s => !string.IsNullOrEmpty(s))];
    }

    public static async Task<int> GenerateNextMatchID(this IDatabase distributedCacheStore)
    {
        long newId = await distributedCacheStore.StringIncrementAsync(MatchIdCounterKey);

        if (newId > int.MaxValue) throw new OverflowException("Match ID counter has exceeded Int32.MaxValue.");

        return (int)newId;
    }

    private const string MatchLeasePrefix = "MATCH-LEASE:";

    /// <summary>
    ///     Gets the active Match ID leased to a server/session/map composite key.
    /// </summary>
    public static async Task<int?> GetActiveMatchLease(this IDatabase distributedCacheStore, string leaseKey)
    {
        RedisValue val = await distributedCacheStore.StringGetAsync(MatchLeasePrefix + leaseKey);
        return val.HasValue && int.TryParse(val.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture,
            out int matchId)
            ? matchId
            : null;
    }

    /// <summary>
    ///     Stores an active Match ID lease with a 15-minute sliding TTL to prevent dual-assignment.
    /// </summary>
    public static async Task SetActiveMatchLease(this IDatabase distributedCacheStore, string leaseKey, int matchId,
        TimeSpan? expiry = null)
    {
        await distributedCacheStore.StringSetAsync(MatchLeasePrefix + leaseKey, matchId,
            expiry ?? TimeSpan.FromMinutes(15));
    }

    /// <summary>
    ///     Explicitly releases an active match lease when a match completes, aborts, or is recycled.
    /// </summary>
    public static async Task RemoveActiveMatchLease(this IDatabase distributedCacheStore, string leaseKey)
    {
        await distributedCacheStore.KeyDeleteAsync(MatchLeasePrefix + leaseKey);
    }

    #endregion

    #region Friend Requests

    private static string ConstructFriendRequestKey(int requesterID, int targetID)
    {
        return $"""FRIEND-REQUEST:["{requesterID}:{targetID}"]""";
    }

    public static async Task SetFriendRequest(this IDatabase distributedCacheStore, int requesterID, int targetID,
        int requesterNotificationID, int targetNotificationID)
    {
        string notificationIDsPair = $"{requesterNotificationID}:{targetNotificationID}";

        await distributedCacheStore.StringSetAsync(ConstructFriendRequestKey(requesterID, targetID),
            notificationIDsPair, TimeSpan.FromHours(24));
    }

    public static async Task<(int RequesterNotificationID, int TargetNotificationID)?> GetFriendRequest(
        this IDatabase distributedCacheStore, int requesterID, int targetID)
    {
        RedisValue cachedValue =
            await distributedCacheStore.StringGetAsync(ConstructFriendRequestKey(requesterID, targetID));

        if (cachedValue.IsNullOrEmpty) return null;

        string[] parts = cachedValue.ToString().Split(':');

        if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture,
                out int requesterNotificationID) ||
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int targetNotificationID))
            return null;

        return (requesterNotificationID, targetNotificationID);
    }

    public static async Task RemoveFriendRequest(this IDatabase distributedCacheStore, int requesterID, int targetID)
    {
        await distributedCacheStore.KeyDeleteAsync(ConstructFriendRequestKey(requesterID, targetID));
    }

    public static async Task<bool> PendingFriendRequestExists(this IDatabase distributedCacheStore, int requesterID,
        int targetID)
    {
        return await distributedCacheStore.KeyExistsAsync(ConstructFriendRequestKey(requesterID, targetID));
    }

    #endregion

    #region Live Match Server Telemetry

    public static readonly RedisChannel LiveMatchTelemetryChannel =
        new("live-match-telemetry-channel", RedisChannel.PatternMode.Literal);

    private static string ConstructLiveMatchTelemetryKey(int matchID)
    {
        string idStr = matchID.ToString(CultureInfo.InvariantCulture);
        const string prefix = "MATCH-TELEMETRY:[\"";
        const string suffix = "\"]";
        int totalLength = prefix.Length + idStr.Length + suffix.Length;

        return string.Create(totalLength, (prefix, idStr, suffix), static (span, state) =>
        {
            state.prefix.AsSpan().CopyTo(span);
            int pos = state.prefix.Length;
            state.idStr.AsSpan().CopyTo(span[pos..]);
            pos += state.idStr.Length;
            state.suffix.AsSpan().CopyTo(span[pos..]);
        });
    }

    public static async Task SetLiveMatchTelemetry(this IDatabase distributedCacheStore, int matchID,
        LiveServerTickPayloadDto payload)
    {
        byte[] serializedData =
            JsonSerializer.SerializeToUtf8Bytes(payload, KongorCoreJsonContext.Default.LiveServerTickPayloadDto);
        await distributedCacheStore.StringSetAsync(ConstructLiveMatchTelemetryKey(matchID), serializedData,
            TimeSpan.FromMinutes(2));
        await distributedCacheStore.PublishAsync(LiveMatchTelemetryChannel, serializedData);
    }

    public static async Task<LiveServerTickPayloadDto?> GetLiveMatchTelemetry(this IDatabase distributedCacheStore,
        int matchID)
    {
        RedisValue cachedValue = await distributedCacheStore.StringGetAsync(ConstructLiveMatchTelemetryKey(matchID));
        if (cachedValue.IsNullOrEmpty) return null;

        byte[] bytes = (byte[]?)cachedValue ?? Encoding.UTF8.GetBytes(cachedValue.ToString()!);
        return JsonSerializer.Deserialize(bytes, KongorCoreJsonContext.Default.LiveServerTickPayloadDto);
    }

    #endregion
}
