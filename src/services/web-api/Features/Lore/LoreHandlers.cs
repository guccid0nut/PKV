// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Lore;
using MERRICK.DatabaseContext.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NEXUS.Contracts.Clients;
using NEXUS.Contracts.CQRS.Lore;

namespace ZORGATH.WebPortal.API.Features.Lore;

public static class LoreMappingHelpers
{
    public static LoreComicSummaryDto MapToSummaryDto(LoreComic c)
    {
        return new LoreComicSummaryDto
        {
            ID = c.ID,
            ArcID = c.ArcID,
            ArcTitle = c.Arc?.Title ?? string.Empty,
            ArcSlug = c.Arc?.Slug ?? string.Empty,
            Title = c.Title,
            Slug = c.Slug,
            IssueNumber = c.IssueNumber,
            Synopsis = c.Synopsis,
            CoverImageUrl = c.CoverImageUrl,
            CinematicVideoUrl = c.CinematicVideoUrl,
            IsPublished = c.IsPublished,
            ViewCount = c.ViewCount,
            PageCount = c.Pages?.Count ?? 0,
            TimestampCreated = c.TimestampCreated,
            TimestampPublished = c.TimestampPublished,
            Credits = c.Credits?.OrderBy(cr => cr.OrderIndex).Select(cr => new LoreCreditDto
            {
                ID = cr.ID,
                ComicID = cr.ComicID,
                AccountID = cr.AccountID,
                AccountName = cr.Account?.Name,
                Role = cr.Role,
                CustomNameOverride = cr.CustomNameOverride,
                OrderIndex = cr.OrderIndex
            }).ToList() ?? []
        };
    }
}

public sealed class GetArcsHandler(
    MerrickContext db) : IRequestHandler<GetArcsQuery, List<LoreArcDto>>
{
    public async Task<List<LoreArcDto>> Handle(GetArcsQuery request, CancellationToken ct)
    {
        IQueryable<LoreArc> query = db.LoreArcs.AsNoTracking();
        if (!request.IncludeUnpublished) query = query.Where(a => a.IsPublished);

        List<LoreArc> arcs = await query
            .OrderBy(a => a.OrderIndex)
            .Include(a => a.Comics.Where(c => request.IncludeUnpublished || c.IsPublished))
            .ThenInclude(c => c.Credits)
            .ThenInclude(cr => cr.Account)
            .ToListAsync(ct);

        return
        [
            .. arcs.Select(a => new LoreArcDto
            {
                ID = a.ID,
                Title = a.Title,
                Slug = a.Slug,
                Description = a.Description,
                BannerImageUrl = a.BannerImageUrl,
                OrderIndex = a.OrderIndex,
                IsPublished = a.IsPublished,
                ComicCount = a.Comics.Count,
                Comics = [.. a.Comics.OrderBy(c => c.IssueNumber).Select(LoreMappingHelpers.MapToSummaryDto)]
            })
        ];
    }
}

public sealed class GetArcBySlugHandler(
    MerrickContext db) : IRequestHandler<GetArcBySlugQuery, LoreArcDto?>
{
    public async Task<LoreArcDto?> Handle(GetArcBySlugQuery request, CancellationToken ct)
    {
        LoreArc? arc = await db.LoreArcs
            .AsNoTracking()
            .Include(a => a.Comics.Where(c => request.IncludeUnpublished || c.IsPublished))
            .ThenInclude(c => c.Credits)
            .ThenInclude(cr => cr.Account)
            .FirstOrDefaultAsync(a => a.Slug == request.Slug, ct);

        if (arc == null || (!request.IncludeUnpublished && !arc.IsPublished))
            return null;

        return new LoreArcDto
        {
            ID = arc.ID,
            Title = arc.Title,
            Slug = arc.Slug,
            Description = arc.Description,
            BannerImageUrl = arc.BannerImageUrl,
            OrderIndex = arc.OrderIndex,
            IsPublished = arc.IsPublished,
            ComicCount = arc.Comics.Count,
            Comics = [.. arc.Comics.OrderBy(c => c.IssueNumber).Select(LoreMappingHelpers.MapToSummaryDto)]
        };
    }
}

public sealed class GetComicsHandler(
    MerrickContext db) : IRequestHandler<GetComicsQuery, List<LoreComicSummaryDto>>
{
    public async Task<List<LoreComicSummaryDto>> Handle(GetComicsQuery request, CancellationToken ct)
    {
        IQueryable<LoreComic> query = db.LoreComics
            .Include(c => c.Arc)
            .Include(c => c.Credits)
            .ThenInclude(cr => cr.Account)
            .Include(c => c.Pages)
            .AsNoTracking();

        if (!request.IncludeUnpublished) query = query.Where(c => c.IsPublished && c.Arc.IsPublished);

        if (request.ArcId.HasValue) query = query.Where(c => c.ArcID == request.ArcId.Value);

        List<LoreComic> comics = await query
            .OrderBy(c => c.Arc.OrderIndex)
            .ThenBy(c => c.IssueNumber)
            .ToListAsync(ct);

        return [.. comics.Select(LoreMappingHelpers.MapToSummaryDto)];
    }
}

public sealed class GetComicDetailHandler(
    MerrickContext db) : IRequestHandler<GetComicDetailQuery, LoreComicDetailDto?>
{
    public async Task<LoreComicDetailDto?> Handle(GetComicDetailQuery request, CancellationToken ct)
    {
        bool isNumeric = int.TryParse(request.IdOrSlug, out int comicId);

        IQueryable<LoreComic> query = db.LoreComics
            .Include(c => c.Arc)
            .Include(c => c.Pages)
            .Include(c => c.Credits)
            .ThenInclude(cr => cr.Account);

        LoreComic? comic = isNumeric
            ? await query.FirstOrDefaultAsync(c => c.ID == comicId, ct)
            : await query.FirstOrDefaultAsync(c => c.Slug == request.IdOrSlug, ct);

        if (comic == null || (!request.IsStaff && (!comic.IsPublished || (comic.Arc != null && !comic.Arc.IsPublished))))
            return null;

        comic.ViewCount++;
        await db.SaveChangesAsync(ct);

        int? userScore = null;
        if (request.CurrentAccountId.HasValue)
        {
            LoreRating? rating = await db.LoreRatings
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.ComicID == comic.ID && r.AccountID == request.CurrentAccountId.Value, ct);
            userScore = rating?.Score;
        }

        LoreRatingMetricDto? staffMetrics = null;
        if (request.IsStaff)
        {
            staffMetrics = await CalculateRatingMetricsAsync(comic.ID, ct);
        }

        return new LoreComicDetailDto
        {
            ID = comic.ID,
            ArcID = comic.ArcID,
            ArcTitle = comic.Arc?.Title ?? string.Empty,
            ArcSlug = comic.Arc?.Slug ?? string.Empty,
            Title = comic.Title,
            Slug = comic.Slug,
            IssueNumber = comic.IssueNumber,
            Synopsis = comic.Synopsis,
            CoverImageUrl = comic.CoverImageUrl,
            CinematicVideoUrl = comic.CinematicVideoUrl,
            IsPublished = comic.IsPublished,
            ViewCount = comic.ViewCount,
            PageCount = comic.Pages?.Count ?? 0,
            TimestampCreated = comic.TimestampCreated,
            TimestampPublished = comic.TimestampPublished,
            Credits =
            [
                .. (comic.Credits?.OrderBy(cr => cr.OrderIndex).Select(cr => new LoreCreditDto
                {
                    ID = cr.ID,
                    ComicID = cr.ComicID,
                    AccountID = cr.AccountID,
                    AccountName = cr.Account?.Name,
                    Role = cr.Role,
                    CustomNameOverride = cr.CustomNameOverride,
                    OrderIndex = cr.OrderIndex
                }) ?? [])
            ],
            Pages =
            [
                .. (comic.Pages?.OrderBy(p => p.PageNumber).Select(p => new LorePageDto
                {
                    ID = p.ID,
                    ComicID = p.ComicID,
                    PageNumber = p.PageNumber,
                    ImageUrl = p.ImageUrl,
                    PanelHotspotsJson = p.PanelHotspotsJson,
                    DialogueTranscript = p.DialogueTranscript
                }) ?? [])
            ],
            UserScore = userScore,
            StaffRatingMetrics = staffMetrics
        };
    }

    private async Task<LoreRatingMetricDto> CalculateRatingMetricsAsync(int comicId, CancellationToken ct)
    {
        List<int> ratings = await db.LoreRatings
            .Where(r => r.ComicID == comicId)
            .Select(r => r.Score)
            .ToListAsync(ct);

        Dictionary<int, int> distribution = new() { { 1, 0 }, { 2, 0 }, { 3, 0 }, { 4, 0 }, { 5, 0 } };
        foreach (int score in ratings)
            if (distribution.TryGetValue(score, out int currentCount))
                distribution[score] = currentCount + 1;

        double average = ratings.Count > 0 ? Math.Round(ratings.Average(), 2) : 0.0;

        return new LoreRatingMetricDto
        {
            ComicID = comicId,
            AverageScore = average,
            TotalRatings = ratings.Count,
            ScoreDistribution = distribution
        };
    }
}

public sealed class RateComicHandler(
    MerrickContext db,
    TimeProvider? timeProvider = null) : IRequestHandler<RateComicCommand, RateComicResult>
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<RateComicResult> Handle(RateComicCommand request, CancellationToken ct)
    {
        if (request.Score is < 1 or > 5)
            return new RateComicResult(false, 0, "Score must be between 1 and 5.", 400);

        LoreComic? comic = await db.LoreComics.FirstOrDefaultAsync(c => c.ID == request.ComicId, ct);
        if (comic == null)
            return new RateComicResult(false, 0, "Comic not found.", 404);

        Account? account = await db.Accounts.FirstOrDefaultAsync(a => a.ID == request.AccountId, ct);
        if (account == null)
            return new RateComicResult(false, 0, "Valid account required to submit a rating.", 401);

        LoreRating? existing = await db.LoreRatings.FirstOrDefaultAsync(r => r.ComicID == request.ComicId && r.AccountID == request.AccountId, ct);
        if (existing != null)
        {
            existing.Score = request.Score;
            existing.Timestamp = _timeProvider.GetUtcNow();
        }
        else
        {
            db.LoreRatings.Add(new LoreRating
            {
                ComicID = request.ComicId,
                AccountID = request.AccountId,
                Score = request.Score,
                Timestamp = _timeProvider.GetUtcNow()
            });
        }

        await db.SaveChangesAsync(ct);
        return new RateComicResult(true, request.Score, null, 200);
    }
}

public sealed class GetUserRatingHandler(
    MerrickContext db) : IRequestHandler<GetUserRatingQuery, int?>
{
    public async Task<int?> Handle(GetUserRatingQuery request, CancellationToken ct)
    {
        LoreRating? rating = await db.LoreRatings
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.ComicID == request.ComicId && r.AccountID == request.AccountId, ct);

        return rating?.Score;
    }
}

public sealed class GetStaffMetricsHandler(
    MerrickContext db) : IRequestHandler<GetStaffMetricsQuery, LoreRatingMetricDto>
{
    public async Task<LoreRatingMetricDto> Handle(GetStaffMetricsQuery request, CancellationToken ct)
    {
        List<int> ratings = await db.LoreRatings
            .Where(r => r.ComicID == request.ComicId)
            .Select(r => r.Score)
            .ToListAsync(ct);

        Dictionary<int, int> distribution = new() { { 1, 0 }, { 2, 0 }, { 3, 0 }, { 4, 0 }, { 5, 0 } };
        foreach (int score in ratings)
            if (distribution.TryGetValue(score, out int currentCount))
                distribution[score] = currentCount + 1;

        double average = ratings.Count > 0 ? Math.Round(ratings.Average(), 2) : 0.0;

        return new LoreRatingMetricDto
        {
            ComicID = request.ComicId,
            AverageScore = average,
            TotalRatings = ratings.Count,
            ScoreDistribution = distribution
        };
    }
}

public sealed class SaveComicHandler(
    MerrickContext db,
    TimeProvider? timeProvider = null) : IRequestHandler<SaveComicCommand, LoreComicSummaryDto?>
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<LoreComicSummaryDto?> Handle(SaveComicCommand command, CancellationToken ct)
    {
        if (!command.IsStaff) return null;

        var request = command.Request;
        LoreComic? comic = request.ID.HasValue
            ? await db.LoreComics.Include(c => c.Pages).Include(c => c.Credits)
                .FirstOrDefaultAsync(c => c.ID == request.ID.Value, ct)
            : null;

        if (comic == null)
        {
            comic = new LoreComic
            {
                ArcID = request.ArcID,
                Title = request.Title,
                Slug = request.Slug,
                IssueNumber = request.IssueNumber,
                Synopsis = request.Synopsis,
                CoverImageUrl = request.CoverImageUrl,
                CinematicVideoUrl = request.CinematicVideoUrl,
                IsPublished = request.IsPublished,
                TimestampCreated = _timeProvider.GetUtcNow(),
                TimestampPublished = request.IsPublished ? _timeProvider.GetUtcNow() : null
            };
            db.LoreComics.Add(comic);
        }
        else
        {
            comic.ArcID = request.ArcID;
            comic.Title = request.Title;
            comic.Slug = request.Slug;
            comic.IssueNumber = request.IssueNumber;
            comic.Synopsis = request.Synopsis;
            comic.CoverImageUrl = request.CoverImageUrl;
            comic.CinematicVideoUrl = request.CinematicVideoUrl;
            if (request.IsPublished && !comic.IsPublished) comic.TimestampPublished = _timeProvider.GetUtcNow();
            comic.IsPublished = request.IsPublished;
        }

        await db.SaveChangesAsync(ct);

        // Sync Pages
        db.LorePages.RemoveRange(comic.Pages);
        if (request.Pages != null)
        {
            foreach (SavePageRequestDto p in request.Pages)
            {
                db.LorePages.Add(new LorePage
                {
                    ComicID = comic.ID,
                    PageNumber = p.PageNumber,
                    ImageUrl = p.ImageUrl,
                    PanelHotspotsJson = p.PanelHotspotsJson,
                    DialogueTranscript = p.DialogueTranscript
                });
            }
        }

        // Sync Credits
        db.LoreCredits.RemoveRange(comic.Credits);
        if (request.Credits != null)
        {
            foreach (SaveCreditRequestDto cr in request.Credits)
            {
                int? accId = cr.AccountID;
                if (!accId.HasValue && !string.IsNullOrWhiteSpace(cr.AccountName))
                {
                    Account? acc = await db.Accounts.FirstOrDefaultAsync(a => a.Name == cr.AccountName, ct);
                    accId = acc?.ID;
                }

                db.LoreCredits.Add(new LoreCredit
                {
                    ComicID = comic.ID,
                    AccountID = accId,
                    Role = cr.Role,
                    CustomNameOverride = cr.CustomNameOverride,
                    OrderIndex = cr.OrderIndex
                });
            }
        }

        await db.SaveChangesAsync(ct);

        LoreComic saved = await db.LoreComics
            .Include(c => c.Arc)
            .Include(c => c.Credits).ThenInclude(cr => cr.Account)
            .Include(c => c.Pages)
            .FirstAsync(c => c.ID == comic.ID, ct);

        return LoreMappingHelpers.MapToSummaryDto(saved);
    }
}

public sealed class DeleteComicHandler(
    MerrickContext db) : IRequestHandler<DeleteComicCommand, LoreActionResult>
{
    public async Task<LoreActionResult> Handle(DeleteComicCommand request, CancellationToken ct)
    {
        if (!request.IsStaff) return new LoreActionResult(false, "Forbidden", 403);

        LoreComic? comic = await db.LoreComics.FirstOrDefaultAsync(c => c.ID == request.ComicId, ct);
        if (comic == null) return new LoreActionResult(false, "Not Found", 404);

        db.LoreComics.Remove(comic);
        await db.SaveChangesAsync(ct);
        return new LoreActionResult(true, null, 200);
    }
}

public sealed class SaveArcHandler(
    MerrickContext db,
    TimeProvider? timeProvider = null) : IRequestHandler<SaveArcCommand, LoreArcDto?>
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<LoreArcDto?> Handle(SaveArcCommand command, CancellationToken ct)
    {
        if (!command.IsStaff) return null;

        var request = command.Request;
        LoreArc? arc = request.ID.HasValue
            ? await db.LoreArcs.FirstOrDefaultAsync(a => a.ID == request.ID.Value, ct)
            : null;

        if (arc == null)
        {
            arc = new LoreArc
            {
                Title = request.Title,
                Slug = request.Slug,
                Description = request.Description,
                BannerImageUrl = request.BannerImageUrl,
                OrderIndex = request.OrderIndex,
                IsPublished = request.IsPublished,
                TimestampCreated = _timeProvider.GetUtcNow()
            };
            db.LoreArcs.Add(arc);
        }
        else
        {
            arc.Title = request.Title;
            arc.Slug = request.Slug;
            arc.Description = request.Description;
            arc.BannerImageUrl = request.BannerImageUrl;
            arc.OrderIndex = request.OrderIndex;
            arc.IsPublished = request.IsPublished;
        }

        await db.SaveChangesAsync(ct);

        return new LoreArcDto
        {
            ID = arc.ID,
            Title = arc.Title,
            Slug = arc.Slug,
            Description = arc.Description,
            BannerImageUrl = arc.BannerImageUrl,
            OrderIndex = arc.OrderIndex,
            IsPublished = arc.IsPublished,
            ComicCount = 0
        };
    }
}

public sealed class DeleteArcHandler(
    MerrickContext db) : IRequestHandler<DeleteArcCommand, LoreActionResult>
{
    public async Task<LoreActionResult> Handle(DeleteArcCommand request, CancellationToken ct)
    {
        if (!request.IsStaff) return new LoreActionResult(false, "Forbidden", 403);

        LoreArc? arc = await db.LoreArcs.FirstOrDefaultAsync(a => a.ID == request.ArcId, ct);
        if (arc == null) return new LoreActionResult(false, "Not Found", 404);

        db.LoreArcs.Remove(arc);
        await db.SaveChangesAsync(ct);
        return new LoreActionResult(true, null, 200);
    }
}
