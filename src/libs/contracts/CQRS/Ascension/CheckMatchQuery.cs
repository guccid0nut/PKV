// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;

namespace NEXUS.Contracts.CQRS.Ascension;

/// <summary>
///     Query to check if a match is an active season match before it starts.
/// </summary>
public record CheckMatchQuery(int MatchId) : IRequest<CheckMatchResultDto>;

/// <summary>
///     Result of season match check.
/// </summary>
public record CheckMatchResultDto(
    bool IsSeasonMatch,
    int ErrorCode,
    object ResponseData);
