// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ReSharper disable LogMessageIsConditional

using System.Collections.Concurrent;
using KONGOR.MasterServer.Logging;
using NEXUS.Contracts.Data;

namespace KONGOR.MasterServer.Controllers.IconsController;

[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
[Route("icons")]
[Route("icon")]
[Route("cai")]
[Route("account_icons")]
[Route("account_icon")]
[Route("avatars")]
[Route("avatar")]
[Route("c_icon")]
[Route("c_icons")]
[Route("custom_icon")]
[Route("custom_icons")]
public class IconsController(
    ILogger<IconsController> logger,
    IWebHostEnvironment environment,
    IAccountRepository accountRepository,
    IHttpClientFactory httpClientFactory,
    IDatabase? distributedCache = null)
    : ControllerBase
{
    private static readonly ConcurrentDictionary<string, (byte[] Data, string ContentType, DateTimeOffset Expiry)>
        AvatarCache = new();

    private static readonly byte[] FallbackIconBytes = Convert.FromBase64String(
        "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");

    [HttpGet("/getAvatar.php")]
    [HttpGet("/get_avatar.php")]
    [HttpGet("/avatar.php")]
    [HttpGet("/c_get_account_icon.php")]
    [HttpGet("/get_account_icon.php")]
    [HttpGet("/account_icon.php")]
    [HttpGet("/icon.php")]
    [HttpGet("/icons.php")]
    [HttpGet("/c_icon.php")]
    [HttpGet("/cai.php")]
    public async Task<IActionResult> GetAvatar(
        [FromQuery] int? id,
        [FromQuery] string? name,
        [FromQuery(Name = "account_id")] int? accountIdQuery,
        [FromQuery(Name = "account_name")] string? accountNameQuery,
        [FromQuery(Name = "nickname")] string? nicknameQuery,
        CancellationToken cancellationToken = default)
    {
        int? targetId = id ?? accountIdQuery;
        string? targetName = name ?? accountNameQuery ?? nicknameQuery;

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("[IconsController] GetAvatar request received: ID={Id}, Name={Name}", targetId,
                targetName ?? "NULL");

        Account? account = null;
        if (targetId.HasValue && targetId.Value > 0)
            account = await accountRepository.GetByIdAsync(targetId.Value, cancellationToken);
        else if (!string.IsNullOrWhiteSpace(targetName))
            account = await accountRepository.GetByNicknameAsync(targetName, cancellationToken);

        // If not found yet, try session cookie fallback
        if (account is null && distributedCache is not null)
        {
            string? cookie = ClientRequestHelper.GetCookie(Request);
            if (!string.IsNullOrWhiteSpace(cookie) && !cookie.Equals("NULL", StringComparison.OrdinalIgnoreCase))
            {
                string? sessionAccountName = await distributedCache.GetAccountNameForSessionCookie(cookie);
                if (!string.IsNullOrWhiteSpace(sessionAccountName))
                {
                    if (logger.IsEnabled(LogLevel.Information))
                        logger.LogInformation("[IconsController] GetAvatar resolving account '{AccountName}' from session cookie", sessionAccountName);
                    account = await accountRepository.GetByNicknameAsync(sessionAccountName, cancellationToken);
                }
            }
        }

        if (account is { User: { } user })
        {
            string? discordId = user.DiscordId;
            string? avatarHash = user.DiscordAvatar;

            string? discordUrl = null;
            if (!string.IsNullOrWhiteSpace(discordId) && !string.IsNullOrWhiteSpace(avatarHash))
            {
                discordUrl = $"https://cdn.discordapp.com/avatars/{discordId}/{avatarHash}.png?size=256";
            }
            else if (!string.IsNullOrWhiteSpace(discordId))
            {
                ulong dId = ulong.TryParse(discordId, out ulong parsedId) ? parsedId : 0;
                discordUrl = $"https://cdn.discordapp.com/embed/avatars/{(dId >> 22) % 6}.png";
            }

            if (discordUrl != null)
            {
                string cacheKey = $"avatar:{account.ID}:{discordUrl}";
                if (AvatarCache.TryGetValue(cacheKey,
                        out (byte[] Data, string ContentType, DateTimeOffset Expiry) cached) &&
                    cached.Expiry > TimeProvider.System.GetUtcNow())
                {
                    if (logger.IsEnabled(LogLevel.Information))
                        logger.LogInformation(
                            "[IconsController] Returning cached Discord avatar for Account #{AccountId} ('{AccountName}')",
                            account.ID, account.Name);
                    PersistAvatarToClientVfs(account.Name, cached.Data);
                    return File(cached.Data, cached.ContentType);
                }

                try
                {
                    HttpClient client = httpClientFactory.CreateClient();
                    using HttpRequestMessage request = new(HttpMethod.Get, discordUrl);
                    request.Headers.UserAgent.ParseAdd(
                        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

                    HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
                    if (response.IsSuccessStatusCode)
                    {
                        byte[] data = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                        string contentType = response.Content.Headers.ContentType?.MediaType ?? "image/png";

                        AvatarCache[cacheKey] = (data, contentType, TimeProvider.System.GetUtcNow().AddMinutes(30));

                        if (logger.IsEnabled(LogLevel.Information))
                            logger.LogInformation(
                                "[IconsController] Successfully fetched Discord avatar from CDN for Account #{AccountId} ('{AccountName}', Discord: {DiscordId}/{AvatarHash})",
                                account.ID, account.Name, discordId, avatarHash ?? "default");

                        PersistAvatarToClientVfs(account.Name, data);
                        return File(data, contentType);
                    }
                }
                catch (Exception ex)
                {
                    if (logger.IsEnabled(LogLevel.Warning))
                        logger.LogWarning(ex,
                            "[IconsController] Failed to proxy Discord avatar for Account ID #{AccountId}", account.ID);
                }
            }
        }

        string defaultAvatarPath = Path.Combine(environment.ContentRootPath, "Resources", "Icons", "default.png");
        if (System.IO.File.Exists(defaultAvatarPath)) return PhysicalFile(defaultAvatarPath, "image/png");

        return File(FallbackIconBytes, "image/png");
    }

    [HttpGet("{*path}")]
    public async Task<IActionResult> GetIcon(string path, CancellationToken cancellationToken = default)
    {
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("[IconsController] GetIcon requested path: '{Path}'", path);

        // Sanitize path to prevent traversal
        if (string.IsNullOrWhiteSpace(path) || path.Contains("..")) return BadRequest();

        // Path formats supported:
        // - "0/0/123/1.cai" or "0/0/0/123/1.cai" (HoN K2 partitioned path)
        // - "123/1.cai"
        // - "123.cai" or "123.png" or "123.tga"
        // - "guccidonut/1.cai" or "guccidonut.cai" or "guccidonut.png"
        // - "1.cai" (slot ID requested directly by client with session cookie)

        string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        int? resolvedAccountId = null;
        string? resolvedAccountName = null;

        if (parts.Length >= 2)
        {
            string parentPart = parts[^2];
            if (int.TryParse(parentPart, out int accIdFromParent))
            {
                resolvedAccountId = accIdFromParent;
            }
            else
            {
                resolvedAccountName = parentPart;
            }
        }
        else if (parts.Length == 1)
        {
            string filePartWithoutExt = Path.GetFileNameWithoutExtension(parts[0]);
            if (int.TryParse(filePartWithoutExt, out int accIdFromFile))
            {
                if (accIdFromFile <= 10 && distributedCache is not null)
                {
                    string? cookie = ClientRequestHelper.GetCookie(Request);
                    if (!string.IsNullOrWhiteSpace(cookie) &&
                        !cookie.Equals("NULL", StringComparison.OrdinalIgnoreCase))
                        resolvedAccountName = await distributedCache.GetAccountNameForSessionCookie(cookie);
                }

                if (resolvedAccountName is null)
                {
                    resolvedAccountId = accIdFromFile;
                }
            }
            else if (!string.IsNullOrWhiteSpace(filePartWithoutExt))
            {
                resolvedAccountName = filePartWithoutExt;
            }
        }

        Account? account = null;
        if (resolvedAccountId.HasValue)
            account = await accountRepository.GetByIdAsync(resolvedAccountId.Value, cancellationToken);
        else if (!string.IsNullOrWhiteSpace(resolvedAccountName))
            account = await accountRepository.GetByNicknameAsync(resolvedAccountName, cancellationToken);

        // Fallback: If still not resolved and session cookie exists, try session account
        if (account is null && distributedCache is not null)
        {
            string? cookie = ClientRequestHelper.GetCookie(Request);
            if (!string.IsNullOrWhiteSpace(cookie) && !cookie.Equals("NULL", StringComparison.OrdinalIgnoreCase))
            {
                string? sessionAccountName = await distributedCache.GetAccountNameForSessionCookie(cookie);
                if (!string.IsNullOrWhiteSpace(sessionAccountName))
                    account = await accountRepository.GetByNicknameAsync(sessionAccountName, cancellationToken);
            }
        }

        if (account is { User: { } user })
        {
            string? discordId = user.DiscordId;
            string? avatarHash = user.DiscordAvatar;

            string? discordUrl = null;
            if (!string.IsNullOrWhiteSpace(discordId) && !string.IsNullOrWhiteSpace(avatarHash))
            {
                discordUrl = $"https://cdn.discordapp.com/avatars/{discordId}/{avatarHash}.png?size=256";
            }
            else if (!string.IsNullOrWhiteSpace(discordId))
            {
                ulong id = ulong.TryParse(discordId, out ulong parsedId) ? parsedId : 0;
                discordUrl = $"https://cdn.discordapp.com/embed/avatars/{(id >> 22) % 6}.png";
            }

            if (discordUrl != null)
            {
                string cacheKey = $"avatar:{account.ID}:{discordUrl}";
                if (AvatarCache.TryGetValue(cacheKey,
                        out (byte[] Data, string ContentType, DateTimeOffset Expiry) cached) &&
                    cached.Expiry > TimeProvider.System.GetUtcNow())
                {
                    if (logger.IsEnabled(LogLevel.Information))
                        logger.LogInformation(
                            "[IconsController] Returning cached Discord avatar for path '{Path}' (Account #{AccountId} '{AccountName}')",
                            path, account.ID, account.Name);
                    return File(cached.Data, cached.ContentType);
                }

                try
                {
                    HttpClient client = httpClientFactory.CreateClient();
                    using HttpRequestMessage request = new(HttpMethod.Get, discordUrl);
                    request.Headers.UserAgent.ParseAdd(
                        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

                    HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
                    if (response.IsSuccessStatusCode)
                    {
                        byte[] data = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                        string contentType = response.Content.Headers.ContentType?.MediaType ?? "image/png";

                        AvatarCache[cacheKey] = (data, contentType, TimeProvider.System.GetUtcNow().AddMinutes(30));

                        if (logger.IsEnabled(LogLevel.Information))
                            logger.LogInformation(
                                "[IconsController] Successfully proxied Discord avatar PNG for Account #{AccountId} ('{AccountName}', Discord: {DiscordId}/{AvatarHash}) on path '{Path}'",
                                account.ID, account.Name, discordId, avatarHash ?? "default", path);

                        return File(data, contentType);
                    }
                }
                catch (Exception ex)
                {
                    if (logger.IsEnabled(LogLevel.Warning))
                        logger.LogWarning(ex,
                            "[IconsController] Failed to proxy Discord avatar from CDN for Account #{AccountId} on path '{Path}'",
                            account.ID, path);
                }
            }
        }

        string fullPath = Path.Combine(environment.ContentRootPath, "Resources", "Icons", path);
        if (System.IO.File.Exists(fullPath)) return PhysicalFile(fullPath, "image/png");

        // Fallback 1: Try replacing .cai extension with .png
        if (path.EndsWith(".cai", StringComparison.OrdinalIgnoreCase))
        {
            string pngPath = Path.Combine(environment.ContentRootPath, "Resources", "Icons", path[..^4] + ".png");
            if (System.IO.File.Exists(pngPath)) return PhysicalFile(pngPath, "image/png");
        }

        // Fallback 2: Try resolving local avatar file by Account ID
        if (resolvedAccountId.HasValue)
        {
            string avatarPath = Path.Combine(environment.ContentRootPath, "Resources", "Icons", "avatars",
                $"{resolvedAccountId.Value}.png");
            if (System.IO.File.Exists(avatarPath)) return PhysicalFile(avatarPath, "image/png");
        }

        // Fallback 3: Return default avatar if account was resolved but had no avatar
        if (account != null)
        {
            string defaultAvatarPath = Path.Combine(environment.ContentRootPath, "Resources", "Icons", "default.png");
            if (System.IO.File.Exists(defaultAvatarPath)) return PhysicalFile(defaultAvatarPath, "image/png");
            return File(FallbackIconBytes, "image/png");
        }

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("[IconsController] Icon not found for path '{Path}'", path);

        return NotFound();
    }

    private void PersistAvatarToClientVfs(string accountName, byte[] data)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(accountName) || data.Length == 0)
                return;

            string trimmedName = accountName.Trim();
            if (trimmedName is "." or ".." ||
                trimmedName.Contains('/') ||
                trimmedName.Contains('\\') ||
                trimmedName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                if (logger.IsEnabled(LogLevel.Warning))
                    logger.LogWarning(
                        "[IconsController] Rejected unsafe avatar filename for account '{AccountName}'",
                        accountName);
                return;
            }

            DirectoryInfo? cursor = new(environment.ContentRootPath);
            string? repoRoot = null;
            while (cursor != null)
            {
                if (System.IO.File.Exists(Path.Combine(cursor.FullName, "ProjectKongor.slnx")))
                {
                    repoRoot = cursor.FullName;
                    break;
                }
                cursor = cursor.Parent;
            }

            if (repoRoot == null || !Directory.Exists(Path.Combine(repoRoot, "games", "client")))
            {
                if (logger.IsEnabled(LogLevel.Debug))
                    logger.LogDebug(
                        "[IconsController] Local client VFS tree not present under '{ContentRoot}'; skipping avatar file sync",
                        environment.ContentRootPath);
                return;
            }

            string safeFileName = $"{Path.GetFileName(trimmedName).ToLowerInvariant()}.png";
            string[] mountRoots =
            [
                Path.Combine(repoRoot, "games", "client", "override"),
                Path.Combine(repoRoot, "games", "client", "game"),
                Path.Combine(repoRoot, "games", "shared", "override"),
                Path.Combine(repoRoot, "games", "shared", "ui"),
                Path.Combine(repoRoot, "games", "client", "ClientPackageFiles")
            ];

            foreach (string mountRoot in mountRoots)
            {
                if (!Directory.Exists(mountRoot))
                    continue;

                string dir = Path.GetFullPath(Path.Combine(mountRoot, "ui", "fe2", "avatars"));
                Directory.CreateDirectory(dir);
                string filePath = Path.GetFullPath(Path.Combine(dir, safeFileName));
                if (!filePath.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    continue;

                System.IO.File.WriteAllBytes(filePath, data);
            }
        }
        catch (Exception ex)
        {
            if (logger.IsEnabled(LogLevel.Debug))
                logger.LogDebug(ex, "[IconsController] Failed to persist avatar to client VFS");
        }
    }
}
