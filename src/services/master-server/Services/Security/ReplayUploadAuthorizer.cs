// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Models.ServerManagement;
using Microsoft.Extensions.Primitives;
using NEXUS.Contracts.Security;

namespace KONGOR.MasterServer.Services.Security;

internal static class ReplayUploadAuthorizer
{
    public const long MaxReplayUploadBytes = 157_286_400L; // 150 MiB
    public const string ReplayUploadTokenHeader = "X-Replay-Upload-Token";
    public const string ServerSessionHeader = "X-Server-Session";
    public static readonly TimeSpan UploadTokenTtl = TimeSpan.FromMinutes(15);

    public static string ConstructUploadTokenKey(string token) => $"REPLAY-UPLOAD-TOKEN:{token}";

    public static async Task<string> MintUploadTokenAsync(IDatabase distributedCache)
    {
        ArgumentNullException.ThrowIfNull(distributedCache);

        string token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        await distributedCache.StringSetAsync(ConstructUploadTokenKey(token), "1", UploadTokenTtl);
        return token;
    }

    private static bool IsSafeTokenFormat(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length is < 8 or > 128)
            return false;

        foreach (char c in token)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_')
                return false;
        }

        return true;
    }

    public static async Task<(bool IsAuthorized, string AuthSource)> AuthorizeAsync(
        HttpContext context,
        IDatabase? distributedCache,
        IConfiguration? configuration,
        bool isDevelopment,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        HttpRequest request = context.Request;
        string remoteIp = context.Connection.RemoteIpAddress?.ToString() ?? "<unknown>";

        // 1. Pre-validated client session set on HttpContext.Items (e.g. ClientRequesterController)
        if (context.Items.TryGetValue("SessionAccountName", out object? accountObj) &&
            accountObj is string preValidatedAccount &&
            !string.IsNullOrWhiteSpace(preValidatedAccount))
        {
            logger?.LogDebug(
                "[ReplayUploadAuth] Authorized replay upload via pre-validated client session for Account '{AccountName}' from IP '{RemoteIp}'.",
                preValidatedAccount, remoteIp);
            return (true, $"ClientSessionItems:{preValidatedAccount}");
        }

        // 2. Inter-service key (X-Internal-Service-Key) validated in constant time
        if (request.Headers.TryGetValue(SecurityConstants.InternalServiceKeyHeader, out StringValues internalKeyValues) &&
            !StringValues.IsNullOrEmpty(internalKeyValues))
        {
            if (KONGOR.IsAuthorizedRenderCallback(
                    internalKeyValues.ToString(),
                    configuration?["InternalServiceKey"],
                    isDevelopment,
                    logger,
                    remoteIp))
            {
                logger?.LogDebug(
                    "[ReplayUploadAuth] Authorized replay upload via '{Header}' from IP '{RemoteIp}'.",
                    SecurityConstants.InternalServiceKeyHeader, remoteIp);
                return (true, "InternalServiceKey");
            }
        }

        // 3. Short-lived upload token minted by server_requester.php?f=get_spectator_header (s3_header)
        string? uploadToken = null;
        if (request.Headers.TryGetValue(ReplayUploadTokenHeader, out StringValues uploadTokenValues) &&
            !StringValues.IsNullOrEmpty(uploadTokenValues))
        {
            uploadToken = uploadTokenValues.ToString().Trim();
        }
        else if (request.Query.TryGetValue("upload_token", out StringValues queryUploadToken) &&
                 !StringValues.IsNullOrEmpty(queryUploadToken))
        {
            uploadToken = queryUploadToken.ToString().Trim();
        }

        if (distributedCache is not null && !string.IsNullOrWhiteSpace(uploadToken) && IsSafeTokenFormat(uploadToken))
        {
            RedisValue cachedUploadToken = await distributedCache.StringGetAsync(ConstructUploadTokenKey(uploadToken));
            if (!cachedUploadToken.IsNullOrEmpty && cachedUploadToken == "1")
            {
                logger?.LogInformation(
                    "[ReplayUploadAuth] Authorized replay upload via '{Header}' ('{MaskedToken}') from IP '{RemoteIp}'.",
                    ReplayUploadTokenHeader, SecurityMasking.MaskToken(uploadToken), remoteIp);
                return (true, "SpectatorUploadToken");
            }
        }

        // 4. Dedicated server session or authenticated player session cookie (Header -> Query -> Form)
        string? sessionCandidate = null;
        if (request.Headers.TryGetValue(ServerSessionHeader, out StringValues headerSession) &&
            !StringValues.IsNullOrEmpty(headerSession))
        {
            sessionCandidate = headerSession.ToString();
        }
        else if (request.Query.TryGetValue("session", out StringValues querySession) &&
                 !StringValues.IsNullOrEmpty(querySession))
        {
            sessionCandidate = querySession.ToString();
        }

        string? cookieCandidate = ClientRequestHelper.GetCookie(request);

        if ((string.IsNullOrWhiteSpace(sessionCandidate) || string.IsNullOrWhiteSpace(cookieCandidate)) &&
            request.HasFormContentType &&
            (request.ContentLength is null || request.ContentLength.Value <= MaxReplayUploadBytes))
        {
            try
            {
                IFormCollection form = await request.ReadFormAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(sessionCandidate) &&
                    form.TryGetValue("session", out StringValues formSession) &&
                    !StringValues.IsNullOrEmpty(formSession))
                {
                    sessionCandidate = formSession.ToString();
                }

                if (string.IsNullOrWhiteSpace(cookieCandidate) &&
                    form.TryGetValue("cookie", out StringValues formCookie) &&
                    !StringValues.IsNullOrEmpty(formCookie))
                {
                    cookieCandidate = formCookie.ToString();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogWarning(ex,
                    "[ReplayUploadAuth] Failed reading form credentials during replay upload auth check from IP '{RemoteIp}'.",
                    remoteIp);
            }
        }

        sessionCandidate = sessionCandidate?.Trim('"', '\'', ' ');
        cookieCandidate = cookieCandidate?.Trim('"', '\'', ' ');

        if (distributedCache is not null)
        {
            if (!string.IsNullOrWhiteSpace(sessionCandidate))
            {
                MatchServer? matchServer = await distributedCache.GetMatchServerBySessionCookie(sessionCandidate)
                                           ?? await distributedCache.ResurrectMatchServerBySession(sessionCandidate);
                if (matchServer is not null)
                {
                    logger?.LogInformation(
                        "[ReplayUploadAuth] Authorized replay upload for MatchServer #{ServerId} ('{MaskedSession}') from IP '{RemoteIp}'.",
                        matchServer.ID, SecurityMasking.MaskToken(sessionCandidate), remoteIp);
                    return (true, $"MatchServer:{matchServer.ID}");
                }

                MatchServerManager? serverManager =
                    await distributedCache.GetMatchServerManagerBySessionCookie(sessionCandidate);
                if (serverManager is not null)
                {
                    logger?.LogInformation(
                        "[ReplayUploadAuth] Authorized replay upload for MatchServerManager #{ManagerId} ('{MaskedSession}') from IP '{RemoteIp}'.",
                        serverManager.ID, SecurityMasking.MaskToken(sessionCandidate), remoteIp);
                    return (true, $"MatchServerManager:{serverManager.ID}");
                }

                (bool validSessionAsCookie, string? sessionAccount) =
                    await distributedCache.ValidateAccountSessionCookie(sessionCandidate);
                if (validSessionAsCookie && !string.IsNullOrWhiteSpace(sessionAccount))
                {
                    logger?.LogInformation(
                        "[ReplayUploadAuth] Authorized replay upload via account session '{AccountName}' ('{MaskedSession}') from IP '{RemoteIp}'.",
                        sessionAccount, SecurityMasking.MaskToken(sessionCandidate), remoteIp);
                    return (true, $"AccountSession:{sessionAccount}");
                }
            }

            if (!string.IsNullOrWhiteSpace(cookieCandidate) &&
                !string.Equals(cookieCandidate, sessionCandidate, StringComparison.Ordinal))
            {
                (bool validAccountCookie, string? cookieAccount) =
                    await distributedCache.ValidateAccountSessionCookie(cookieCandidate);
                if (validAccountCookie && !string.IsNullOrWhiteSpace(cookieAccount))
                {
                    logger?.LogInformation(
                        "[ReplayUploadAuth] Authorized replay upload for Account '{AccountName}' ('{MaskedCookie}') from IP '{RemoteIp}'.",
                        cookieAccount, SecurityMasking.MaskToken(cookieCandidate), remoteIp);
                    return (true, $"AccountCookie:{cookieAccount}");
                }

                MatchServer? serverByCookie = await distributedCache.GetMatchServerBySessionCookie(cookieCandidate)
                                              ?? await distributedCache.ResurrectMatchServerBySession(cookieCandidate);
                if (serverByCookie is not null)
                {
                    logger?.LogInformation(
                        "[ReplayUploadAuth] Authorized replay upload via MatchServer cookie #{ServerId} ('{MaskedCookie}') from IP '{RemoteIp}'.",
                        serverByCookie.ID, SecurityMasking.MaskToken(cookieCandidate), remoteIp);
                    return (true, $"MatchServerCookie:{serverByCookie.ID}");
                }
            }
        }

        logger?.LogWarning(
            "[ReplayUploadAuth] REJECTED unauthorized replay upload on '{Path}' from IP '{RemoteIp}' (Session: '{MaskedSession}', Cookie: '{MaskedCookie}', UploadToken: '{MaskedUploadToken}').",
            request.Path,
            remoteIp,
            SecurityMasking.MaskToken(sessionCandidate),
            SecurityMasking.MaskToken(cookieCandidate),
            SecurityMasking.MaskToken(uploadToken));

        return (false, "Unauthorized");
    }
}
