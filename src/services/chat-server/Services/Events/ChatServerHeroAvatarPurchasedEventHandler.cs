// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using NEXUS.Contracts.Events;

namespace TRANSMUTANSTEIN.ChatServer.Services.Events;

/// <summary>
///     Handles <see cref="HeroAvatarPurchasedEvent" /> in ChatServer for cache invalidation and notification.
/// </summary>
public sealed class ChatServerHeroAvatarPurchasedEventHandler(
    ILogger<ChatServerHeroAvatarPurchasedEventHandler> logger) : IEventHandler<HeroAvatarPurchasedEvent>
{
    /// <inheritdoc />
    public Task HandleAsync(HeroAvatarPurchasedEvent @event, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(@event);

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "[ChatServerHeroAvatarPurchasedEventHandler] Received HeroAvatarPurchasedEvent: Account #{AccountId}, Product #{ProductId} ({ItemCode}), Cost: {Cost}",
                @event.AccountId, @event.ProductId, @event.ItemCode, @event.CostAmount);

        return Task.CompletedTask;
    }
}
