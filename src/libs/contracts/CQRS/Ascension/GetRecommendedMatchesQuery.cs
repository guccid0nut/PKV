// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;

namespace NEXUS.Contracts.CQRS.Ascension;

/// <summary>
///     Query to retrieve active live matches sorted descending by average MMR.
/// </summary>
public record GetRecommendedMatchesQuery : IRequest<RecommendedMatchesResultDto>;

/// <summary>
///     Result containing recommended matches pool.
/// </summary>
public record RecommendedMatchesResultDto(
    bool Success,
    int ErrorCode,
    IReadOnlyList<object> RecommendedPool,
    object ResponseData);
