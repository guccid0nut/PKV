// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json.Serialization;

namespace NEXUS.Contracts.Events;

/// <summary>
///     Domain event raised when an authenticated player switches between sub-accounts.
/// </summary>
[method: JsonConstructor]
public sealed record UserSwitchedAccountEvent(
    Guid EventId,
    DateTimeOffset OccurredAt,
    int UserId,
    int OldAccountId,
    string OldAccountName,
    int NewAccountId,
    string NewAccountName,
    string RemoteIpAddress,
    string NewSessionCookie
) : IDomainEvent
{
    /// <summary>
    ///     Convenience constructor generating a new <see cref="EventId"/> and UTC timestamp.
    /// </summary>
    public UserSwitchedAccountEvent(
        int userId,
        int oldAccountId,
        string oldAccountName,
        int newAccountId,
        string newAccountName,
        string remoteIpAddress,
        string newSessionCookie)
        : this(Guid.NewGuid(), TimeProvider.System.GetUtcNow(), userId, oldAccountId, oldAccountName, newAccountId, newAccountName, remoteIpAddress, newSessionCookie)
    {
    }
}
