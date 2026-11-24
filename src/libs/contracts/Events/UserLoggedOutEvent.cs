// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json.Serialization;

namespace NEXUS.Contracts.Events;

/// <summary>
///     Domain event raised when a player logs out of the Master Server.
/// </summary>
[method: JsonConstructor]
public sealed record UserLoggedOutEvent(
    Guid EventId,
    DateTimeOffset OccurredAt,
    string SessionCookie,
    string? AccountName = null
) : IDomainEvent
{
    /// <summary>
    ///     Convenience constructor generating a new <see cref="EventId"/> and UTC timestamp.
    /// </summary>
    public UserLoggedOutEvent(string sessionCookie, string? accountName = null)
        : this(Guid.NewGuid(), TimeProvider.System.GetUtcNow(), sessionCookie, accountName)
    {
    }
}
