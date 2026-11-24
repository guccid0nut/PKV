// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Security.Claims;
using KONGOR.MasterServer.Serialization;
using Microsoft.AspNetCore.SignalR;
using NEXUS.Contracts.Security;

namespace KONGOR.MasterServer.Hubs;

public class PresenceHub(MerrickContext merrick, IConnectionMultiplexer redis, ILogger<PresenceHub>? logger = null) : Hub
{
    private const string AuthenticatedAccountNameItemKey = "Kongor.AuthenticatedAccountName";

    public static string? NormalizeSessionCookie(string? rawCookie)
    {
        if (string.IsNullOrWhiteSpace(rawCookie))
        {
            return null;
        }

        string trimmed = rawCookie.Trim().Trim('[', ']', '"', '\'');
        int colonIdx = trimmed.IndexOf(':');
        if (colonIdx > 0)
        {
            trimmed = trimmed[..colonIdx].Trim();
        }

        if (trimmed.Length is < 8 or > 128)
        {
            return null;
        }

        foreach (char c in trimmed)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                return null;
            }
        }

        return trimmed;
    }

    public async Task<bool> AuthenticateSessionCookie(string sessionCookie)
    {
        string? normalizedCookie = NormalizeSessionCookie(sessionCookie);
        if (string.IsNullOrWhiteSpace(normalizedCookie))
        {
            logger?.LogWarning(
                "[SECURITY-ALERT] [PresenceHub] Rejected malformed session cookie in AuthenticateSessionCookie on ConnectionId={ConnectionId}.",
                Context.ConnectionId);
            return false;
        }

        try
        {
            IDatabase authDb = redis.GetDatabase();
            (bool isValidCookie, string? sessionAccountName) = await authDb.ValidateAccountSessionCookie(
                normalizedCookie,
                merrick,
                logger,
                source: "PresenceHub");
            if (isValidCookie && !string.IsNullOrWhiteSpace(sessionAccountName))
            {
                if (Context.Items is not null)
                {
                    Context.Items[AuthenticatedAccountNameItemKey] = sessionAccountName;
                }

                logger?.LogDebug(
                    "[PresenceHub] Authenticated session cookie prefix '{CookiePrefix}' for Account='{AccountName}' on ConnectionId={ConnectionId}.",
                    SecurityMasking.MaskToken(normalizedCookie),
                    sessionAccountName,
                    Context.ConnectionId);
                return true;
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(
                ex,
                "[PresenceHub] Failed validating session cookie prefix '{CookiePrefix}' in AuthenticateSessionCookie on ConnectionId={ConnectionId}.",
                SecurityMasking.MaskToken(normalizedCookie),
                Context.ConnectionId);
        }

        logger?.LogWarning(
            "[SECURITY-ALERT] [PresenceHub] Blocked invalid session cookie prefix '{CookiePrefix}' on ConnectionId={ConnectionId}.",
            SecurityMasking.MaskToken(normalizedCookie),
            Context.ConnectionId);
        return false;
    }

    public async Task SubscribeToPresence(long accountId)
    {
        if (accountId <= 0 || accountId > int.MaxValue)
        {
            logger?.LogDebug(
                "[PresenceHub] Ignored out-of-range PresenceHub.SubscribeToPresence({AccountId}) from ConnectionId={ConnectionId}.",
                accountId,
                Context.ConnectionId);
            return;
        }

        Account? account = await merrick.Accounts.FindAsync((int)accountId);
        bool isAuthorized = IsClaimAuthorizedForAccount(Context.User, accountId, account?.Name);

        if (!isAuthorized &&
            account != null &&
            Context.Items is not null &&
            Context.Items.TryGetValue(AuthenticatedAccountNameItemKey, out object? cachedAuthObj) &&
            cachedAuthObj is string authenticatedAccountName &&
            string.Equals(authenticatedAccountName, account.Name, StringComparison.OrdinalIgnoreCase))
        {
            isAuthorized = true;
        }

        if (!isAuthorized)
        {
            HttpContext? httpContext = Context.Features is not null ? Context.GetHttpContext() : null;
            if (httpContext?.Request.Query.ContainsKey("cookie") == true)
            {
                logger?.LogWarning(
                    "[SECURITY-ALERT] [PresenceHub] Rejected URL query-string '?cookie=' secret on ConnectionId={ConnectionId} (AccountId={AccountId}); pass 'X-Kongor-Cookie' header instead.",
                    Context.ConnectionId, accountId);
            }

            string? rawHeaderCookie = httpContext?.Request.Headers["X-Kongor-Cookie"].FirstOrDefault();
            string? cookie = NormalizeSessionCookie(rawHeaderCookie);

            if (!string.IsNullOrWhiteSpace(cookie) && account != null)
            {
                try
                {
                    IDatabase authDb = redis.GetDatabase();
                    (bool isValidCookie, string? sessionAccountName) = await authDb.ValidateAccountSessionCookie(
                        cookie,
                        merrick,
                        logger,
                        source: "PresenceHub");
                    if (isValidCookie &&
                        !string.IsNullOrWhiteSpace(sessionAccountName) &&
                        string.Equals(sessionAccountName, account.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        isAuthorized = true;
                        if (Context.Items is not null)
                        {
                            Context.Items[AuthenticatedAccountNameItemKey] = sessionAccountName;
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "[PresenceHub] Failed validating session cookie for AccountId={AccountId} on ConnectionId={ConnectionId}.",
                        accountId, Context.ConnectionId);
                }
            }
        }

        if (!isAuthorized)
        {
            logger?.LogWarning(
                "[SECURITY-ALERT] Blocked unauthenticated PresenceHub.SubscribeToPresence({AccountId}) from ConnectionId={ConnectionId}.",
                accountId, Context.ConnectionId);
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, accountId.ToString());

        string displayName = account != null && !string.IsNullOrWhiteSpace(account.Name)
            ? account.Name
            : "KONGOR Player";

        DiscordPresenceUpdateDto defaultPresence = new()
        {
            AccountId = accountId,
            State = "Online in Menus",
            Details = "Idle",
            LargeImageKey = "large",
            LargeImageText = "Heroes of Newerth",
            SmallImageKey = string.Empty,
            SmallImageText = displayName
        };

        // Check if real active presence is cached in Redis
        try
        {
            IDatabase db = redis.GetDatabase();
            RedisValue cached = await db.StringGetAsync($"presence:dto:{accountId}");
            if (cached is { HasValue: true, IsNullOrEmpty: false })
            {
                DiscordPresenceUpdateDto? cachedDto =
                    JsonSerializer.Deserialize(cached.ToString(), MasterServerJsonContext.Default.DiscordPresenceUpdateDto);
                if (cachedDto != null && cachedDto.State != "Offline")
                {
                    await Clients.Caller.SendAsync("PresenceUpdated", cachedDto);
                    return;
                }
            }

            // Cache active menu presence since the client has now connected/subscribed
            string defaultJson = JsonSerializer.Serialize(defaultPresence, MasterServerJsonContext.Default.DiscordPresenceUpdateDto);
            await db.StringSetAsync($"presence:dto:{accountId}", defaultJson, TimeSpan.FromHours(24));
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "[PresenceHub] Redis presence cache unavailable for AccountId={AccountId}; sending fallback presence.", accountId);
        }

        // Send default initial presence for active connected player
        await Clients.Caller.SendAsync("PresenceUpdated", defaultPresence);
    }

    private static bool IsClaimAuthorizedForAccount(ClaimsPrincipal? principal, long accountId, string? accountName)
    {
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        string? claimAccId = principal.FindFirstValue("account_id")
                             ?? principal.FindFirstValue(ClaimTypes.NameIdentifier)
                             ?? principal.FindFirstValue("sub");
        if (long.TryParse(claimAccId, out long parsedClaimId) && parsedClaimId == accountId)
        {
            return true;
        }

        string? claimName = principal.Identity.Name ?? principal.FindFirstValue(ClaimTypes.Name);
        return !string.IsNullOrWhiteSpace(accountName) &&
               !string.IsNullOrWhiteSpace(claimName) &&
               string.Equals(claimName, accountName, StringComparison.OrdinalIgnoreCase);
    }
}
