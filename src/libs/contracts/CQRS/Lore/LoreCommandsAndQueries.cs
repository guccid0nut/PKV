// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;
using NEXUS.Contracts.Clients;

namespace NEXUS.Contracts.CQRS.Lore;

/// <summary>
/// Result of rating a comic issue.
/// </summary>
public record RateComicResult(bool Success, int Score, string? Message = null, int StatusCode = 200);

/// <summary>
/// Result of saving or deleting a lore resource.
/// </summary>
public record LoreActionResult(bool Success, string? Message = null, int StatusCode = 200);

/// <summary>
/// Query to retrieve all story arcs with nested comic summaries.
/// </summary>
public sealed record GetArcsQuery(bool IncludeUnpublished = false) : IRequest<List<LoreArcDto>>;

/// <summary>
/// Query to retrieve a single story arc by its unique URL slug.
/// </summary>
public sealed record GetArcBySlugQuery(string Slug, bool IncludeUnpublished = false) : IRequest<LoreArcDto?>;

/// <summary>
/// Query to retrieve summary listings of comics, optionally filtered by story arc.
/// </summary>
public sealed record GetComicsQuery(int? ArcId = null, bool IncludeUnpublished = false) : IRequest<List<LoreComicSummaryDto>>;

/// <summary>
/// Query to retrieve full comic issue details, pages, credits, and user score.
/// </summary>
public sealed record GetComicDetailQuery(string IdOrSlug, int? CurrentAccountId = null, bool IsStaff = false) : IRequest<LoreComicDetailDto?>;

/// <summary>
/// Command to rate a comic issue from 1 to 5 stars.
/// </summary>
public sealed record RateComicCommand(int ComicId, int AccountId, int Score) : IRequest<RateComicResult>;

/// <summary>
/// Query to retrieve a specific account's rating for a comic issue.
/// </summary>
public sealed record GetUserRatingQuery(int ComicId, int AccountId) : IRequest<int?>;

/// <summary>
/// Query to retrieve staff rating distribution and metrics for a comic issue.
/// </summary>
public sealed record GetStaffMetricsQuery(int ComicId) : IRequest<LoreRatingMetricDto>;

/// <summary>
/// Command to create or update a digital comic issue with its pages and credits.
/// </summary>
public sealed record SaveComicCommand(SaveComicRequestDto Request, bool IsStaff) : IRequest<LoreComicSummaryDto?>;

/// <summary>
/// Command to delete a digital comic issue.
/// </summary>
public sealed record DeleteComicCommand(int ComicId, bool IsStaff) : IRequest<LoreActionResult>;

/// <summary>
/// Command to create or update a story arc.
/// </summary>
public sealed record SaveArcCommand(SaveArcRequestDto Request, bool IsStaff) : IRequest<LoreArcDto?>;

/// <summary>
/// Command to delete a story arc.
/// </summary>
public sealed record DeleteArcCommand(int ArcId, bool IsStaff) : IRequest<LoreActionResult>;
