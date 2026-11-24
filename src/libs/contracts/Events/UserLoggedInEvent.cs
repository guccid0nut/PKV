// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json.Serialization;

namespace NEXUS.Contracts.Events;

/// <summary>
///     Domain event raised when a player successfully authenticates with the Master Server via SRP.
/// </summary>
[method: JsonConstructor]
public sealed record UserLoggedInEvent(
    Guid EventId,
    DateTimeOffset OccurredAt,
    int AccountId,
    string AccountName,
    string ClientVersion,
    string RemoteIpAddress,
    string SessionCookie
) : IDomainEvent
{
    /// <summary>
    ///     Convenience constructor generating a new <see cref="EventId"/> and UTC timestamp.
    /// </summary>
    public UserLoggedInEvent(
        int accountId,
        string accountName,
        string clientVersion,
        string remoteIpAddress,
        string sessionCookie)
        : this(Guid.NewGuid(), TimeProvider.System.GetUtcNow(), accountId, accountName, clientVersion, remoteIpAddress, sessionCookie)
    {
    }
}
