// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json.Serialization;

namespace NEXUS.Contracts.Events;

/// <summary>
///     Domain event raised when a player purchases or unlocks a hero avatar or store cosmetic item.
/// </summary>
[method: JsonConstructor]
public sealed record HeroAvatarPurchasedEvent(
    Guid EventId,
    DateTimeOffset OccurredAt,
    int AccountId,
    int UserId,
    int ProductId,
    string ItemCode,
    int CurrencyType,
    int CostAmount,
    DateTimeOffset Timestamp
) : IDomainEvent
{
    /// <summary>
    ///     Convenience constructor generating a new <see cref="EventId" /> and UTC timestamp.
    /// </summary>
    public HeroAvatarPurchasedEvent(
        int accountId,
        int userId,
        int productId,
        string itemCode,
        int currencyType,
        int costAmount,
        DateTimeOffset? timestamp = null)
        : this(Guid.NewGuid(), TimeProvider.System.GetUtcNow(), accountId, userId, productId, itemCode, currencyType,
            costAmount, timestamp ?? TimeProvider.System.GetUtcNow())
    {
    }
}
