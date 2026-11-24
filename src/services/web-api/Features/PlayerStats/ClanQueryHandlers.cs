// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using ASPIRE.Common.Caching;
using MediatR;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.CQRS.PlayerStats;
using NEXUS.Contracts.Domain;
using NEXUS.Contracts.DTOs;

namespace ZORGATH.WebPortal.API.Features.PlayerStats;

/// <summary>
///     MediatR handler for <see cref="GetClanDirectoryQuery"/>.
///     Projects active clans, computes canonical competitive standings, applies provider-portable
///     search/sort filtering, and returns paginated summaries.
/// </summary>
public sealed class GetClanDirectoryHandler(
    MerrickContext db,
    ILogger<GetClanDirectoryHandler>? logger = null,
    TimeProvider? timeProvider = null)
    : IRequestHandler<GetClanDirectoryQuery, ClanDirectoryResultDto>
{
    private readonly ILogger<GetClanDirectoryHandler> _logger = logger ?? NullLogger<GetClanDirectoryHandler>.Instance;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<ClanDirectoryResultDto> Handle(
        GetClanDirectoryQuery request,
        CancellationToken cancellationToken)
    {
        Stopwatch sw = Stopwatch.StartNew();
        int safePage = Math.Max(1, request.Page);
        int safePageSize = Math.Clamp(request.PageSize, 1, 500);
        string cleanSort = (request.Sort ?? "mmr").Trim().ToLowerInvariant();
        string rawSearch = (request.Search ?? string.Empty).Trim();
        string normalizedSearch = rawSearch.TrimStart('[').TrimEnd(']').Trim().ToLowerInvariant();

        _logger.LogInformation(
            "[ClanQueryHandlers] GetClanDirectory start: page={Page}, pageSize={PageSize}, search='{Search}', sort='{Sort}'",
            safePage, safePageSize, rawSearch, cleanSort);

        // shortcut: bounded in-memory projection/sort suitable up to ~5000 active clans, push SQL GROUP BY aggregation when core.Clans exceeds 5000 rows
        List<Clan> clans = await db.Clans
            .AsNoTracking()
            .Include(c => c.Members)
                .ThenInclude(m => m.Statistics)
            .ToListAsync(cancellationToken);

        DateTimeOffset nowUtc = _timeProvider.GetUtcNow();
        List<ClanSummaryDto> activeSummaries = new(clans.Count);
        int totalActiveMembers = 0;

        foreach (Clan clan in clans)
        {
            List<Account> activeMembers = [.. clan.Members.Where(ClanStatsCalculator.IsActiveClanAccount)];
            if (activeMembers.Count == 0)
            {
                _logger.LogDebug(
                    "[ClanQueryHandlers] Excluding clan #{ClanId} '[{Tag}] {Name}' with 0 active members (raw members={RawCount})",
                    clan.ID, clan.Tag, clan.Name, clan.Members.Count);
                continue;
            }

            totalActiveMembers += activeMembers.Count;
            activeSummaries.Add(ClanStatsCalculator.BuildClanSummary(clan, activeMembers, nowUtc));
        }

        double? highestQualifiedAvgMmr = activeSummaries
            .Where(c => c.IsCompetitiveQualified && c.AverageMmr.HasValue)
            .Select(c => c.AverageMmr)
            .OrderByDescending(m => m)
            .FirstOrDefault();

        IEnumerable<ClanSummaryDto> filtered = activeSummaries;
        if (!string.IsNullOrEmpty(normalizedSearch))
        {
            filtered = filtered.Where(c =>
                c.Name.Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase) ||
                c.Tag.Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(c.LeaderName) &&
                 c.LeaderName.Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase)));
        }

        List<ClanSummaryDto> sorted = cleanSort switch
        {
            "members" =>
            [
                .. filtered
                    .OrderByDescending(c => c.MemberCount)
                    .ThenByDescending(c => c.AverageMmr ?? -1.0)
                    .ThenBy(c => c.Id)
            ],
            "winrate" =>
            [
                .. filtered
                    .OrderByDescending(c => c.IsCompetitiveQualified)
                    .ThenByDescending(c => c.CombinedWinRate)
                    .ThenByDescending(c => c.CombinedMemberMatches)
                    .ThenBy(c => c.Id)
            ],
            "matches" or "games" =>
            [
                .. filtered
                    .OrderByDescending(c => c.CombinedMemberMatches)
                    .ThenByDescending(c => c.CombinedWinRate)
                    .ThenBy(c => c.Id)
            ],
            "newest" =>
            [
                .. filtered
                    .OrderByDescending(c => c.TimestampCreated)
                    .ThenByDescending(c => c.Id)
            ],
            _ => // default: "mmr"
            [
                .. filtered
                    .OrderByDescending(c => c.IsCompetitiveQualified)
                    .ThenByDescending(c => c.AverageMmr ?? -1.0)
                    .ThenByDescending(c => c.MemberCount)
                    .ThenBy(c => c.Id)
            ]
        };

        int totalCount = sorted.Count;
        int totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)safePageSize));
        int clampedPage = Math.Min(safePage, totalPages);
        List<ClanSummaryDto> pageItems = [.. sorted.Skip((clampedPage - 1) * safePageSize).Take(safePageSize)];

        sw.Stop();
        _logger.LogInformation(
            "[ClanQueryHandlers] GetClanDirectory complete: rawClans={RawClans}, activeClans={ActiveClans}, matched={Matched}, returned={Returned}, page={Page}/{TotalPages}, elapsedMs={ElapsedMs}",
            clans.Count, activeSummaries.Count, totalCount, pageItems.Count, clampedPage, totalPages, sw.ElapsedMilliseconds);

        return new ClanDirectoryResultDto(
            Items: pageItems,
            TotalCount: totalCount,
            Page: clampedPage,
            TotalPages: totalPages,
            TotalActiveMembers: totalActiveMembers,
            HighestQualifiedAvgMmr: highestQualifiedAvgMmr
        );
    }
}

/// <summary>
///     MediatR handler for <see cref="GetClanDetailQuery"/>.
///     Returns clan summary + ordered active member roster, cached under tag <c>clan:{ClanId}</c>
///     so mutations via <see cref="MERRICK.DatabaseContext.Repositories.ClanRepository"/> evict it immediately.
/// </summary>
public sealed class GetClanDetailHandler(
    MerrickContext db,
    INexusHybridCache? hybridCache = null,
    ILogger<GetClanDetailHandler>? logger = null,
    TimeProvider? timeProvider = null)
    : IRequestHandler<GetClanDetailQuery, ClanDetailDto?>
{
    private readonly ILogger<GetClanDetailHandler> _logger = logger ?? NullLogger<GetClanDetailHandler>.Instance;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<ClanDetailDto?> Handle(
        GetClanDetailQuery request,
        CancellationToken cancellationToken)
    {
        if (request.ClanId <= 0)
        {
            _logger.LogWarning("[ClanQueryHandlers] GetClanDetail rejected non-positive ClanId={ClanId}", request.ClanId);
            return null;
        }

        if (hybridCache is not null)
        {
            string cacheKey = $"clan:detail:{request.ClanId}";
            _logger.LogDebug("[ClanQueryHandlers] Resolving ClanDetail via HybridCache key='{CacheKey}' (tag='clan:{ClanId}')",
                cacheKey, request.ClanId);

            return await hybridCache.GetOrCreateAsync(
                cacheKey,
                async ct => await LoadFromDatabaseAsync(request.ClanId, ct),
                tags: [$"clan:{request.ClanId}"],
                l1Expiration: TimeSpan.FromSeconds(30),
                l2Expiration: TimeSpan.FromMinutes(3),
                cancellationToken: cancellationToken);
        }

        return await LoadFromDatabaseAsync(request.ClanId, cancellationToken);
    }

    private async Task<ClanDetailDto?> LoadFromDatabaseAsync(int clanId, CancellationToken cancellationToken)
    {
        Stopwatch sw = Stopwatch.StartNew();
        _logger.LogInformation("[ClanQueryHandlers] Loading ClanDetail #{ClanId} from database", clanId);

        Clan? clan = await db.Clans
            .AsNoTracking()
            .Include(c => c.Members)
                .ThenInclude(m => m.Statistics)
            .Include(c => c.Members)
                .ThenInclude(m => m.User)
            .FirstOrDefaultAsync(c => c.ID == clanId, cancellationToken);

        if (clan is null)
        {
            _logger.LogWarning("[ClanQueryHandlers] Clan #{ClanId} not found", clanId);
            return null;
        }

        List<Account> activeMembers = [.. clan.Members.Where(ClanStatsCalculator.IsActiveClanAccount)];
        if (activeMembers.Count == 0)
        {
            _logger.LogWarning(
                "[ClanQueryHandlers] Clan #{ClanId} '[{Tag}] {Name}' has 0 active members (raw={RawCount}); treating as unavailable",
                clan.ID, clan.Tag, clan.Name, clan.Members.Count);
            return null;
        }

        DateTimeOffset nowUtc = _timeProvider.GetUtcNow();
        ClanSummaryDto summary = ClanStatsCalculator.BuildClanSummary(clan, activeMembers, nowUtc);

        List<ClanMemberDto> memberDtos =
        [
            .. activeMembers
                .Select(m => ClanStatsCalculator.ToClanMemberDto(m, nowUtc))
                .OrderBy(m => m.Tier == ClanTier.Leader ? 0 : m.Tier == ClanTier.Officer ? 1 : 2)
                .ThenByDescending(m => m.SkillRating ?? -1.0)
                .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
        ];

        sw.Stop();
        _logger.LogInformation(
            "[ClanQueryHandlers] Loaded ClanDetail #{ClanId} '[{Tag}] {Name}': activeMembers={ActiveMembers}, ranked={Ranked}, avgMmr={AvgMmr}, elapsedMs={ElapsedMs}",
            clan.ID, clan.Tag, clan.Name, memberDtos.Count, summary.RankedMemberCount, summary.AverageMmr, sw.ElapsedMilliseconds);

        return new ClanDetailDto(summary, memberDtos);
    }
}
