// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json.Serialization;

namespace NEXUS.Contracts.Events;

/// <summary>
///     Domain event raised when a player account is suspended or banned by administration/moderation.
/// </summary>
[method: JsonConstructor]
public sealed record AccountBannedEvent(
    Guid EventId,
    DateTimeOffset OccurredAt,
    int AccountId,
    string AccountName,
    DateTimeOffset? BanExpiration,
    string? BanReason,
    int? BannedByAccountId = null
) : IDomainEvent
{
    /// <summary>
    ///     Convenience constructor generating a new <see cref="EventId" /> and UTC timestamp.
    /// </summary>
    public AccountBannedEvent(
        int accountId,
        string accountName,
        DateTimeOffset? banExpiration,
        string? banReason,
        int? bannedByAccountId = null)
        : this(Guid.NewGuid(), TimeProvider.System.GetUtcNow(), accountId, accountName, banExpiration, banReason,
            bannedByAccountId)
    {
    }
}
