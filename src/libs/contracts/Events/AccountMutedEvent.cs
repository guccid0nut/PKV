// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Text.Json.Serialization;

namespace NEXUS.Contracts.Events;

/// <summary>
///     Domain event raised when a player account is muted by administration/moderation or automated penalty systems.
/// </summary>
[method: JsonConstructor]
public sealed record AccountMutedEvent(
    Guid EventId,
    DateTimeOffset OccurredAt,
    int AccountId,
    string AccountName,
    DateTimeOffset? MuteExpiration,
    string? MuteReason,
    int? MutedByAccountId = null
) : IDomainEvent
{
    /// <summary>
    ///     Convenience constructor generating a new <see cref="EventId" /> and UTC timestamp.
    /// </summary>
    public AccountMutedEvent(
        int accountId,
        string accountName,
        DateTimeOffset? muteExpiration,
        string? muteReason,
        int? mutedByAccountId = null)
        : this(Guid.NewGuid(), TimeProvider.System.GetUtcNow(), accountId, accountName, muteExpiration, muteReason,
            mutedByAccountId)
    {
    }
}
