// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.Data;
using NEXUS.Contracts.Security;
using StackExchange.Redis;
using IRedisDatabase = StackExchange.Redis.IDatabase;

namespace MERRICK.DatabaseContext.Repositories;

public class SessionRepository(
    MerrickContext db,
    IConnectionMultiplexer? connectionMultiplexer = null,
    IRedisDatabase? redisDatabase = null,
    ILogger<SessionRepository>? logger = null,
    TimeProvider? timeProvider = null) : ISessionRepository
{
    private readonly ILogger<SessionRepository> _logger = logger ?? NullLogger<SessionRepository>.Instance;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private IRedisDatabase? Cache => redisDatabase ?? connectionMultiplexer?.GetDatabase();

    public const string InvalidSessionSentinel = "__INVALID__";
    public static readonly TimeSpan NegativeCacheExpiration = TimeSpan.FromSeconds(60);

    public async Task<string?> GetAccountNameForSessionCookieAsync(string cookie, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cookie)) return null;

        IRedisDatabase? cache = Cache;
        if (cache is not null)
            try
            {
                string key = ConstructAccountSessionCookieKey(cookie);
                RedisValue value = await cache.StringGetAsync(key);
                if (value == InvalidSessionSentinel) return null;

                if (value.IsNullOrEmpty)
                    // Fallback to direct key (e.g. for unit tests with direct mock setups)
                    value = await cache.StringGetAsync(cookie);
                if (value == InvalidSessionSentinel) return null;

                if (value.IsNullOrEmpty && cookie.Length == 32 && Guid.TryParse(cookie, out Guid guid1))
                {
                    value = await cache.StringGetAsync(ConstructAccountSessionCookieKey(guid1.ToString()));
                    if (value == InvalidSessionSentinel) return null;
                }

                if (value.IsNullOrEmpty && cookie.Contains('-') && Guid.TryParse(cookie, out Guid guid2))
                {
                    value = await cache.StringGetAsync(ConstructAccountSessionCookieKey(guid2.ToString("N")));
                    if (value == InvalidSessionSentinel) return null;
                }

                if (value.HasValue && !value.IsNullOrEmpty)
                {
                    string accountName = value.ToString()!;
                    if (_logger.IsEnabled(LogLevel.Debug))
                        _logger.LogDebug(
                            "[SessionRepository] Session cookie lookup returned '{AccountName}' for cookie prefix '{CookiePrefix}'",
                            accountName, SecurityMasking.MaskToken(cookie));
                    return accountName;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[SessionRepository] Redis operation failed for cookie prefix '{CookiePrefix}' (Account: '{AccountName}'). Fallback: '{FallbackTarget}'",
                    SecurityMasking.MaskToken(cookie),
                    "<unknown>",
                    "Database");
            }
        else if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug(
                "[SessionRepository] Redis cache unavailable; attempting database fallback for cookie prefix '{CookiePrefix}'",
                SecurityMasking.MaskToken(cookie));

        // Database Fallback
        try
        {
            AccountSession? session = await db.AccountSessions
                .AsNoTracking()
                .Include(s => s.Account)
                .FirstOrDefaultAsync(s => s.SessionCookie == cookie && s.IsActive, ct);

            if (session?.Account is not null)
            {
                if (_logger.IsEnabled(LogLevel.Information))
                    _logger.LogInformation(
                        "[SessionRepository] Database fallback resolved account '{AccountName}' for session cookie prefix '{CookiePrefix}'",
                        session.Account.Name,
                        SecurityMasking.MaskToken(cookie));

                if (cache is not null)
                    try
                    {
                        string key = ConstructAccountSessionCookieKey(cookie);
                        await cache.StringSetAsync(key, session.Account.Name, TimeSpan.FromDays(30));
                    }
                    catch
                    {
                        // Suppress background re-hydration error
                    }

                return session.Account.Name;
            }

            // Session not found or inactive in database: cache negative sentinel to defend against pool exhaustion
            if (cache is not null)
            {
                try
                {
                    string key = ConstructAccountSessionCookieKey(cookie);
                    await cache.StringSetAsync(key, InvalidSessionSentinel, NegativeCacheExpiration);
                }
                catch
                {
                    // Suppress background cache write error
                }
            }
        }
        catch (Exception dbEx)
        {
            _logger.LogError(dbEx,
                "[SessionRepository] Database fallback query failed for cookie prefix '{CookiePrefix}'",
                SecurityMasking.MaskToken(cookie));
        }

        return null;
    }

    public async Task SetSessionCookieAsync(string cookie, string accountName, TimeSpan ttl,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cookie) || string.IsNullOrWhiteSpace(accountName)) return;

        IRedisDatabase? cache = Cache;
        if (cache is not null)
            try
            {
                string key = ConstructAccountSessionCookieKey(cookie);
                await cache.StringSetAsync(key, accountName, ttl);
                string userCookiesKey = ConstructAccountActiveCookiesKey(accountName);
                await cache.SetAddAsync(userCookiesKey, cookie);
                try { await cache.KeyExpireAsync(userCookiesKey, ttl); } catch { }

                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug(
                        "[SessionRepository] Cached session cookie for account {AccountName} with TTL {TTL}",
                        accountName, ttl);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[SessionRepository] Redis operation failed for cookie prefix '{CookiePrefix}' (Account: '{AccountName}'). Fallback: '{FallbackTarget}'",
                    SecurityMasking.MaskToken(cookie),
                    accountName,
                    "None");
            }
        else if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug(
                "[SessionRepository] Redis cache unavailable to store session cookie for account {AccountName}",
                accountName);
    }

    public async Task RemoveSessionCookieAsync(string cookie, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cookie)) return;

        IRedisDatabase? cache = Cache;
        string? resolvedAccountName = null;
        if (cache is not null)
            try
            {
                string key = ConstructAccountSessionCookieKey(cookie);
                RedisValue val = await cache.StringGetAsync(key);
                if (val.HasValue) resolvedAccountName = val.ToString();
                await cache.KeyDeleteAsync(key);
                if (!string.IsNullOrWhiteSpace(resolvedAccountName))
                {
                    string userCookiesKey = ConstructAccountActiveCookiesKey(resolvedAccountName);
                    await cache.SetRemoveAsync(userCookiesKey, cookie);
                }

                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug("[SessionRepository] Removed session cookie from Redis cache");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[SessionRepository] Redis operation failed for cookie prefix '{CookiePrefix}' (Account: '{AccountName}'). Fallback: '{FallbackTarget}'",
                    SecurityMasking.MaskToken(cookie),
                    "<unknown>",
                    "None");
            }

        // Deactivate in Database if present
        try
        {
            AccountSession? session = await db.AccountSessions
                .FirstOrDefaultAsync(s => s.SessionCookie == cookie && s.IsActive, ct);
            if (session is not null)
            {
                session.IsActive = false;
                session.TimestampLastSeen = _timeProvider.GetUtcNow();
                await db.SaveChangesAsync(ct);
            }
        }
        catch (Exception dbEx)
        {
            _logger.LogWarning(dbEx,
                "[SessionRepository] Failed to deactivate database session for cookie prefix '{CookiePrefix}'",
                SecurityMasking.MaskToken(cookie));
        }
    }

    public async Task RevokeAllSessionsForAccountAsync(string accountName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accountName)) return;

        IRedisDatabase? cache = Cache;
        if (cache is not null)
        {
            try
            {
                string userCookiesKey = ConstructAccountActiveCookiesKey(accountName);
                RedisValue[] cookies = await cache.SetMembersAsync(userCookiesKey);
                foreach (RedisValue cookieVal in cookies)
                {
                    if (cookieVal.HasValue)
                    {
                        string cookie = cookieVal.ToString();
                        string key = ConstructAccountSessionCookieKey(cookie);
                        await cache.KeyDeleteAsync(key);
                        await cache.KeyDeleteAsync(cookie);
                    }
                }
                await cache.KeyDeleteAsync(userCookiesKey);

                if (_logger.IsEnabled(LogLevel.Information))
                    _logger.LogInformation(
                        "[SessionRepository] Revoked {Count} active Redis session cookies for account {AccountName}",
                        cookies.Length, accountName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[SessionRepository] Failed to revoke active session cookies from Redis for account '{AccountName}'",
                    accountName);
            }
        }

        // Deactivate in Database for this account
        try
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            List<AccountSession> activeSessions = await db.AccountSessions
                .Where(s => s.Account != null && s.Account.Name == accountName && s.IsActive)
                .ToListAsync(ct);

            foreach (AccountSession session in activeSessions)
            {
                session.IsActive = false;
                session.TimestampLastSeen = now;
            }

            if (activeSessions.Count > 0)
            {
                await db.SaveChangesAsync(ct);
                if (_logger.IsEnabled(LogLevel.Information))
                    _logger.LogInformation(
                        "[SessionRepository] Deactivated {Count} active database sessions for account {AccountName}",
                        activeSessions.Count, accountName);
            }
        }
        catch (Exception dbEx)
        {
            _logger.LogWarning(dbEx,
                "[SessionRepository] Failed to deactivate database sessions for account '{AccountName}'",
                accountName);
        }
    }

    public async Task<Token?> ValidateTokenAsync(Guid tokenGuid, TokenPurpose purpose, CancellationToken ct = default)
    {
        Token? token = await db.Tokens.FirstOrDefaultAsync(t =>
            t.Value == tokenGuid &&
            t.Purpose == purpose &&
            t.TimestampConsumed == null, ct);

        return token;
    }

    public async Task<Token?> ValidateTokenAsync(string tokenString, TokenPurpose purpose,
        CancellationToken ct = default)
    {
        if (!Guid.TryParse(tokenString, out Guid guid)) return null;

        return await ValidateTokenAsync(guid, purpose, ct);
    }

    public async Task<Token> IssueTokenAsync(int userId, TokenPurpose purpose, TimeSpan ttl,
        CancellationToken ct = default)
    {
        User? user = await db.Users.FirstOrDefaultAsync(u => u.ID == userId, ct);
        string email = user?.EmailAddress ?? $"user_{userId}@kongor.net";
        DateTimeOffset now = _timeProvider.GetUtcNow();
        Token token = new()
        {
            UserID = userId,
            Purpose = purpose,
            EmailAddress = email,
            Data = userId.ToString(),
            Value = Guid.NewGuid(),
            TimestampCreated = now,
            Expires = now.UtcDateTime.Add(ttl)
        };

        await db.Tokens.AddAsync(token, ct);
        await db.SaveChangesAsync(ct);
        return token;
    }

    public async Task InvalidateTokenAsync(Guid tokenGuid, CancellationToken ct = default)
    {
        Token? token = await db.Tokens.FirstOrDefaultAsync(t => t.Value == tokenGuid, ct);
        if (token is not null)
        {
            token.TimestampConsumed = _timeProvider.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task InvalidateTokenAsync(string tokenString, CancellationToken ct = default)
    {
        if (Guid.TryParse(tokenString, out Guid guid)) await InvalidateTokenAsync(guid, ct);
    }

    public async Task<Token?> GetTokenAsync(Guid tokenGuid, CancellationToken ct = default)
    {
        return await db.Tokens.FirstOrDefaultAsync(t => t.Value == tokenGuid, ct);
    }

    public async Task<Token?> GetTokenAsync(string tokenString, CancellationToken ct = default)
    {
        if (Guid.TryParse(tokenString, out Guid guid)) return await GetTokenAsync(guid, ct);

        return null;
    }

    public async Task CleanExpiredTokensAsync(CancellationToken ct = default)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        try
        {
            await db.Tokens
                .Where(t => t.TimestampConsumed != null || (t.Expires != null && t.Expires < now.UtcDateTime))
                .ExecuteDeleteAsync(ct);
        }
        catch (InvalidOperationException)
        {
            List<Token> tokens = await db.Tokens
                .Where(t => t.TimestampConsumed != null || (t.Expires != null && t.Expires < now.UtcDateTime))
                .ToListAsync(ct);
            db.Tokens.RemoveRange(tokens);
            await db.SaveChangesAsync(ct);
        }
    }

    private static string ConstructAccountSessionCookieKey(string cookie)
    {
        return $"ACCOUNT-SESSION-COOKIE:[\"{cookie}\"]";
    }

    private static string ConstructAccountActiveCookiesKey(string accountName)
    {
        return $"ACCOUNT-ACTIVE-COOKIES:{accountName.ToUpperInvariant()}";
    }
}
