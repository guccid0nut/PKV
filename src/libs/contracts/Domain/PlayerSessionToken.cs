// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.Domain;

/// <summary>
///     Immutable record representing an active player session token.
/// </summary>
internal sealed record PlayerSessionToken(
    Guid SessionId,
    uint AccountId,
    string Nickname,
    string IpAddress,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    IReadOnlyList<string> Roles);
