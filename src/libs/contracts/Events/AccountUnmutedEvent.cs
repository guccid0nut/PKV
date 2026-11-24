// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Text.Json.Serialization;

namespace NEXUS.Contracts.Events;

/// <summary>
///     Domain event raised when a player account's mute is cleared or pardoned.
/// </summary>
[method: JsonConstructor]
public sealed record AccountUnmutedEvent(
    Guid EventId,
    DateTimeOffset OccurredAt,
    int AccountId,
    string AccountName,
    int? UnmutedByAccountId = null
) : IDomainEvent
{
    /// <summary>
    ///     Convenience constructor generating a new <see cref="EventId" /> and UTC timestamp.
    /// </summary>
    public AccountUnmutedEvent(
        int accountId,
        string accountName,
        int? unmutedByAccountId = null)
        : this(Guid.NewGuid(), TimeProvider.System.GetUtcNow(), accountId, accountName, unmutedByAccountId)
    {
    }
}
