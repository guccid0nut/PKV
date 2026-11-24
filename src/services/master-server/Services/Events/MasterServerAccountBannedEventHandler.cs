// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using NEXUS.Contracts.Events;
using DistributedCacheExtensions = ASPIRE.Common.Extensions.Cache.DistributedCacheExtensions;

namespace KONGOR.MasterServer.Services.Events;

/// <summary>
///     Handles <see cref="AccountBannedEvent" /> domain events in MasterServer by evicting active sessions from cache.
/// </summary>
public sealed class MasterServerAccountBannedEventHandler(
    IDatabase distributedCache,
    ILogger<MasterServerAccountBannedEventHandler> logger) : IEventHandler<AccountBannedEvent>
{
    /// <inheritdoc />
    public async Task HandleAsync(AccountBannedEvent @event, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(@event);

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "[MasterServerAccountBannedEventHandler] Received AccountBannedEvent for Account #{AccountId} ({AccountName}). Reason: '{Reason}', Expiration: {Expiration}",
                @event.AccountId, @event.AccountName, @event.BanReason ?? "None specified", @event.BanExpiration);

        try
        {
            // Evict SRP and session authentication data for the banned account
            await DistributedCacheExtensions.RemoveSRPAuthenticationSessionData(distributedCache, @event.AccountName);
            await DistributedCacheExtensions.RemoveSRPAuthenticationSystemInformation(distributedCache,
                @event.AccountName);
            await DistributedCacheExtensions.RevokeAllSessionsForAccount(distributedCache, @event.AccountName, logger);

            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation(
                    "[MasterServerAccountBannedEventHandler] Evicted session and SRP authentication cache for banned account #{AccountId} ({AccountName})",
                    @event.AccountId, @event.AccountName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "[MasterServerAccountBannedEventHandler] Error while evicting cache for banned account #{AccountId}",
                @event.AccountId);
        }
    }
}
