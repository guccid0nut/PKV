// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using KONGOR.MasterServer.Serialization;
using NEXUS.Contracts.Security;

namespace KONGOR.MasterServer.Extensions.Cache;

public static partial class DistributedCacheExtensions
{
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

    public static async Task SetSRPAuthenticationSessionData(this IDatabase distributedCacheStore, string accountName,
        SRPAuthenticationSessionDataStageOne data)
    {
        byte[] serializedData = JsonSerializer.SerializeToUtf8Bytes(data, MasterServerJsonContext.Default.SRPAuthenticationSessionDataStageOne);

        await distributedCacheStore.StringSetAsync(ConstructSRPAuthenticationSessionDataKey(accountName),
            serializedData, TimeSpan.FromSeconds(30));
    }

    public static async Task<SRPAuthenticationSessionDataStageOne?> GetSRPAuthenticationSessionData(
        this IDatabase distributedCacheStore, string accountName)
    {
        RedisValue cachedValue =
            await distributedCacheStore.StringGetAsync(ConstructSRPAuthenticationSessionDataKey(accountName));

        if (cachedValue.IsNullOrEmpty) return null;

        byte[] bytes = (byte[]?)cachedValue ?? Encoding.UTF8.GetBytes(cachedValue.ToString());
        return JsonSerializer.Deserialize(bytes, MasterServerJsonContext.Default.SRPAuthenticationSessionDataStageOne);
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

    // The Cookie Is Stored In The Key Because Most Requests From HoN Are Made With Just A Cookie But No Account Information
    // Additionally, Cached Values Cannot Be Retrieved Without Their Respective Key, So Iterating Just The Values Is Not Possible Without Complex Reflection
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

    public static async Task SetInvalidSessionCookie(this IDatabase distributedCacheStore, string cookie)
    {
        await distributedCacheStore.StringSetAsync(ConstructAccountSessionCookieKey(cookie), InvalidSessionSentinel,
            NegativeCacheExpiration);
    }

    public static async Task SetAccountNameForSessionCookie(this IDatabase distributedCacheStore, string cookie,
        string accountName)
    {
        await distributedCacheStore.StringSetAsync(ConstructAccountSessionCookieKey(cookie), accountName,
            TimeSpan.FromHours(24));
    }

    public static async Task<string?> GetAccountNameForSessionCookie(this IDatabase distributedCacheStore,
        string cookie)
    {
        RedisValue cachedValue = await distributedCacheStore.StringGetAsync(ConstructAccountSessionCookieKey(cookie));

        if (cachedValue.IsNullOrEmpty || cachedValue == InvalidSessionSentinel)
            return null;

        return cachedValue.ToString();
    }

    public static async Task RemoveAccountNameForSessionCookie(this IDatabase distributedCacheStore, string cookie)
    {
        await distributedCacheStore.KeyDeleteAsync(ConstructAccountSessionCookieKey(cookie));
    }

    public static async Task<(bool IsValid, string? AccountName)> ValidateAccountSessionCookie(
        this IDatabase distributedCacheStore, string cookie)
    {
        string? accountName = await distributedCacheStore.GetAccountNameForSessionCookie(cookie);
        return (accountName is not null, accountName);
    }

    public static async Task<(bool IsValid, string? AccountName)> ValidateAccountSessionCookie(
        this IDatabase distributedCacheStore, string cookie, MerrickContext dbContext, ILogger? logger = null,
        string source = "DistributedCacheExtensions", string fallbackTarget = "Database")
    {
        // 1. Check Redis (Fast Path)
        try
        {
            RedisValue cachedValue = await distributedCacheStore.StringGetAsync(ConstructAccountSessionCookieKey(cookie));
            if (cachedValue == InvalidSessionSentinel)
            {
                // Negative cache hit: Known invalid cookie. Short-circuit immediately without querying database.
                return (false, null);
            }

            if (!cachedValue.IsNullOrEmpty)
            {
                string accountName = cachedValue.ToString()!;
                // Refresh TTL
                await distributedCacheStore.SetAccountNameForSessionCookie(cookie, accountName);
                return (true, accountName);
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex,
                "[{Source}] Redis operation failed for cookie prefix '{CookiePrefix}' (Account: '{AccountName}'). Fallback: '{FallbackTarget}'",
                source,
                SecurityMasking.MaskToken(cookie),
                "<unknown>",
                fallbackTarget);
        }

        // 2. Fallback to Relational Database Context
        try
        {
            AccountSession? session = await dbContext.AccountSessions
                .AsNoTracking()
                .Include(s => s.Account)
                .FirstOrDefaultAsync(s => s.SessionCookie == cookie && s.IsActive);

            if (session?.Account is not null)
            {
                if (logger?.IsEnabled(LogLevel.Information) == true)
                    logger.LogInformation(
                        "[{Source}] Database fallback resolved account '{AccountName}' for session cookie prefix '{CookiePrefix}'",
                        source,
                        session.Account.Name,
                        SecurityMasking.MaskToken(cookie));

                // Attempt to re-populate Redis cache
                try
                {
                    await distributedCacheStore.SetAccountNameForSessionCookie(cookie, session.Account.Name);
                }
                catch
                {
                    // Ignore cache write error during fallback
                }

                return (true, session.Account.Name);
            }

            // Session not found in Database: cache negative sentinel to prevent DB pool exhaustion
            try
            {
                await distributedCacheStore.SetInvalidSessionCookie(cookie);
            }
            catch
            {
                // Ignore cache write error during fallback
            }
        }
        catch (Exception dbEx)
        {
            logger?.LogError(dbEx,
                "[DistributedCacheExtensions] Database fallback query failed for cookie prefix '{CookiePrefix}'",
                SecurityMasking.MaskToken(cookie));
        }

        // Session not found in Redis or Database
        return (false, null);
    }

    /// <summary>
    ///     Purges all session cookies from the cache.
    ///     This should be called at application startup to clear stale session data.
    /// </summary>
    public static async Task PurgeSessionCookies(this IConnectionMultiplexer distributedCacheProvider)
    {
        IDatabase distributedCacheStore = distributedCacheProvider.GetDatabase();

        long cursor = 0;
        const int pageSize = 1000;

        do
        {
            RedisResult redisResult = await distributedCacheStore.ExecuteAsync("SCAN", cursor.ToString(), "MATCH",
                "ACCOUNT-SESSION-COOKIE:*", "COUNT", pageSize.ToString());

            RedisResult[]? innerResult = (RedisResult[]?)redisResult;
            if (innerResult is null || innerResult.Length < 2) break;

            string? cursorStr = (string?)innerResult[0];
            if (cursorStr is null) break;

            cursor = long.Parse(cursorStr);

            RedisKey[]? keys = (RedisKey[]?)innerResult[1];
            if (keys is not null && keys.Length > 0) await distributedCacheStore.KeyDeleteAsync(keys);
        } while (cursor != 0);
    }
}
