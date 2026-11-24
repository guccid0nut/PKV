// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;

namespace NEXUS.Contracts.CQRS.AccountManagement;

/// <summary>
///     Query to check if a player nickname is available.
/// </summary>
public record CheckNicknameAvailabilityQuery(string Nickname) : IRequest<NicknameAvailabilityResultDto>;

/// <summary>
///     Result of nickname availability check, containing golden PHP response structure.
/// </summary>
public record NicknameAvailabilityResultDto(
    bool Available,
    int ErrorCode,
    string ErrorMessage,
    Dictionary<string, object> RawResponse);
