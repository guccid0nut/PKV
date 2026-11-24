// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using KONGOR.MasterServer.Serialization;
using MERRICK.DatabaseContext.Repositories;
using NEXUS.Contracts.Data;
using NEXUS.Contracts.Events;

namespace KONGOR.MasterServer.Services.Events;

/// <summary>
///     Handles <see cref="UserLoggedInEvent"/> and <see cref="UserSwitchedAccountEvent"/> asynchronously,
///     updating Redis Discord presence state, telemetry, and syncing Discord avatars without blocking the client response pipeline.
/// </summary>
public sealed class MasterServerUserLoggedInEventHandler(
    IDatabase distributedCache,
    IAccountRepository accountRepository,
    IHttpClientFactory httpClientFactory,
    IWebHostEnvironment environment,
    ILogger<MasterServerUserLoggedInEventHandler> logger)
    : IEventHandler<UserLoggedInEvent>, IEventHandler<UserSwitchedAccountEvent>
{
    private const string PresenceChannel = "discord-presence-updates";

    /// <inheritdoc />
    public async Task HandleAsync(UserLoggedInEvent @event, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(@event);

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "[MasterServerUserLoggedInEventHandler] Processing login event for Account #{AccountId} ({AccountName}) from IP {RemoteIP}",
                @event.AccountId, @event.AccountName, @event.RemoteIpAddress);
        }

        try
        {
            DiscordPresenceUpdateDto loginPresence = new()
            {
                AccountId = @event.AccountId,
                State = "Online in Menus",
                Details = "Idle",
                LargeImageKey = "large",
                LargeImageText = "Heroes of Newerth",
                SmallImageKey = string.Empty,
                SmallImageText = @event.AccountName
            };

            string loginPresenceJson = JsonSerializer.Serialize(loginPresence, MasterServerJsonContext.Default.DiscordPresenceUpdateDto);
            await distributedCache.StringSetAsync(
                $"presence:dto:{@event.AccountId}",
                loginPresenceJson,
                TimeSpan.FromHours(24)).ConfigureAwait(false);

            if (distributedCache.Multiplexer is { } multiplexer)
            {
                await multiplexer.GetSubscriber()
                    .PublishAsync(RedisChannel.Literal(PresenceChannel), loginPresenceJson)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            if (logger.IsEnabled(LogLevel.Warning))
            {
                logger.LogWarning(ex,
                    "[MasterServerUserLoggedInEventHandler] Failed to publish initial login presence for Account #{AccountId}",
                    @event.AccountId);
            }
        }

        await SyncDiscordAvatarAsync(@event.AccountId, @event.AccountName, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task HandleAsync(UserSwitchedAccountEvent @event, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(@event);

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "[MasterServerUserLoggedInEventHandler] Processing account switch event for User #{UserId}: '{OldAccountName}' -> '{NewAccountName}' (ID: #{NewAccountId})",
                @event.UserId, @event.OldAccountName, @event.NewAccountName, @event.NewAccountId);
        }

        try
        {
            DiscordPresenceUpdateDto switchPresence = new()
            {
                AccountId = @event.NewAccountId,
                State = "Online in Menus",
                Details = "Idle",
                LargeImageKey = "large",
                LargeImageText = "Heroes of Newerth",
                SmallImageKey = string.Empty,
                SmallImageText = @event.NewAccountName
            };

            string switchPresenceJson = JsonSerializer.Serialize(switchPresence, MasterServerJsonContext.Default.DiscordPresenceUpdateDto);
            await distributedCache.StringSetAsync(
                $"presence:dto:{@event.NewAccountId}",
                switchPresenceJson,
                TimeSpan.FromHours(24)).ConfigureAwait(false);

            if (distributedCache.Multiplexer is { } multiplexer)
            {
                await multiplexer.GetSubscriber()
                    .PublishAsync(RedisChannel.Literal(PresenceChannel), switchPresenceJson)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            if (logger.IsEnabled(LogLevel.Warning))
            {
                logger.LogWarning(ex,
                    "[MasterServerUserLoggedInEventHandler] Failed to publish switch presence for target Account #{AccountId}",
                    @event.NewAccountId);
            }
        }

        await SyncDiscordAvatarAsync(@event.NewAccountId, @event.NewAccountName, cancellationToken).ConfigureAwait(false);
    }

    private async Task SyncDiscordAvatarAsync(int accountId, string accountName, CancellationToken cancellationToken)
    {
        try
        {
            Account? account = await accountRepository.GetByIdAsync(accountId, cancellationToken);
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
                    HttpClient client = httpClientFactory.CreateClient();
                    using HttpRequestMessage request = new(HttpMethod.Get, discordUrl);
                    request.Headers.UserAgent.ParseAdd(
                        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

                    HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
                    if (response.IsSuccessStatusCode)
                    {
                        byte[] data = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                        PersistAvatarToClientVfs(account.Name, data);

                        if (logger.IsEnabled(LogLevel.Information))
                        {
                            logger.LogInformation(
                                "[MasterServerUserLoggedInEventHandler] Synced Discord avatar for Account #{AccountId} ('{AccountName}') to local VFS ({ByteCount} bytes)",
                                account.ID, account.Name, data.Length);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            if (logger.IsEnabled(LogLevel.Warning))
            {
                logger.LogWarning(ex,
                    "[MasterServerUserLoggedInEventHandler] Failed to sync Discord avatar for Account #{AccountId} ('{AccountName}')",
                    accountId, accountName);
            }
        }
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
                        "[MasterServerUserLoggedInEventHandler] Rejected unsafe avatar filename for account '{AccountName}'",
                        accountName);
                return;
            }

            DirectoryInfo? cursor = new(environment.ContentRootPath);
            string? repoRoot = null;
            while (cursor != null)
            {
                if (File.Exists(Path.Combine(cursor.FullName, "ProjectKongor.slnx")))
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
                        "[MasterServerUserLoggedInEventHandler] Local client VFS tree not present under '{ContentRoot}'; skipping avatar file sync",
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

                File.WriteAllBytes(filePath, data);
            }
        }
        catch (Exception ex)
        {
            if (logger.IsEnabled(LogLevel.Debug))
                logger.LogDebug(ex, "[MasterServerUserLoggedInEventHandler] Failed to persist avatar to client VFS");
        }
    }
}

