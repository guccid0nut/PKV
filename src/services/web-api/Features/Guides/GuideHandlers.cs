// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Constants;
using ASPIRE.Common.Security;
using MediatR;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Game;
using MERRICK.DatabaseContext.Extensions;
using MERRICK.DatabaseContext.Persistence;
using Microsoft.EntityFrameworkCore;
using NEXUS.Contracts.Clients;
using NEXUS.Contracts.CQRS.Guides;

namespace ZORGATH.WebPortal.API.Features.Guides;

public sealed class SearchGuidesHandler(MerrickContext db) : IRequestHandler<SearchGuidesQuery, GuideSearchResultDto>
{
    public async Task<GuideSearchResultDto> Handle(SearchGuidesQuery request, CancellationToken ct)
    {
        Account? currentAccount = null;
        if (!string.IsNullOrWhiteSpace(request.CurrentAccountName))
            currentAccount = await db.Accounts.FirstOrDefaultAsync(a => a.Name == request.CurrentAccountName, ct);

        IQueryable<HeroGuide> query = db.HeroGuides
            .Include(g => g.Author)
            .Include(g => g.Items)
            .Include(g => g.Abilities)
            .AsNoTracking();

        if (!string.IsNullOrWhiteSpace(request.Hero) && request.Hero != "all")
        {
            string cleanHero = request.Hero.Trim();
            if (!cleanHero.StartsWith("Hero_", StringComparison.OrdinalIgnoreCase)) cleanHero = $"Hero_{cleanHero}";

            string cleanHeroName = request.Hero.Trim();
            query = query.Where(g => g.HeroIdentifier == cleanHero || g.HeroName == cleanHeroName);
        }

        if (!string.IsNullOrWhiteSpace(request.Map) && request.Map != "all")
            query = query.Where(g => g.TargetMap == request.Map);

        if (!string.IsNullOrWhiteSpace(request.Q))
        {
            string search = request.Q.Trim();
            query = query.Where(g => (g.Name != null && g.Name.Contains(search)) ||
                                     (g.HeroName != null && g.HeroName.Contains(search)) ||
                                     (g.Author != null && g.Author.Name != null && g.Author.Name.Contains(search)) ||
                                     (g.Intro != null && g.Intro.Contains(search)));
        }

        if (request.Sort == "myguides")
        {
            query = currentAccount != null
                ? query.Where(g => g.Author != null && g.Author.ID == currentAccount.ID)
                : query.Where(g => false);
        }
        else if (request.Sort == "favorites")
        {
            if (currentAccount is { FavoriteGuideIDs.Count: > 0 })
            {
                List<int> favIds = currentAccount.FavoriteGuideIDs;
                query = query.Where(g =>
                    favIds.Contains(g.ID) && (g.Featured || (g.Author != null && g.Author.ID == currentAccount.ID) ||
                                              (request.IsStaff && g.Public)));
            }
            else
            {
                query = query.Where(g => false);
            }
        }
        else if (request.Sort == "defaults")
        {
            if (currentAccount is { DefaultGuideIDs.Count: > 0 })
            {
                List<int> defIds = currentAccount.DefaultGuideIDs;
                query = query.Where(g =>
                    defIds.Contains(g.ID) && (g.Featured || (g.Author != null && g.Author.ID == currentAccount.ID) ||
                                              (request.IsStaff && g.Public)));
            }
            else
            {
                query = query.Where(g => false);
            }
        }
        else
        {
            query = query.Where(g =>
                g.Featured ||
                (currentAccount != null && g.Author != null && g.Author.ID == currentAccount.ID) ||
                (request.IsStaff && g.Public));
        }

        query = request.Sort switch
        {
            "rating" => query.OrderByDescending(g => g.Rating).ThenByDescending(g => g.UpVotes),
            "newest" => query.OrderByDescending(g => g.TimestampCreated),
            "official" => query.Where(g => g.Featured).OrderByDescending(g => g.Rating),
            "favorites" => query.OrderByDescending(g => g.Featured).ThenByDescending(g => g.Rating),
            "defaults" => query.OrderByDescending(g => g.Featured).ThenByDescending(g => g.Rating),
            "myguides" => query.OrderByDescending(g => g.TimestampCreated),
            _ => query.OrderByDescending(g => g.Featured).ThenByDescending(g => g.Rating)
                .ThenByDescending(g => g.UpVotes)
        };

        int clampedPageSize = Math.Clamp(request.PageSize, 1, 100);
        int clampedPage = Math.Max(1, request.Page);
        int totalCount = await query.CountAsync(ct);
        int totalPages = (int)Math.Ceiling(totalCount / (double)clampedPageSize);

        List<HeroGuide> guides = await query
            .Skip((clampedPage - 1) * clampedPageSize)
            .Take(clampedPageSize)
            .ToListAsync(ct);

        return new GuideSearchResultDto([.. guides.Select(g => g.ToDto())], totalCount, Math.Max(1, totalPages),
            clampedPage);
    }
}

public sealed class GetGuideByIdHandler(MerrickContext db) : IRequestHandler<GetGuideByIdQuery, HeroGuideDto?>
{
    public async Task<HeroGuideDto?> Handle(GetGuideByIdQuery request, CancellationToken ct)
    {
        HeroGuide? guide = await db.HeroGuides
            .Include(g => g.Author)
            .Include(g => g.Items)
            .Include(g => g.Abilities)
            .FirstOrDefaultAsync(g => g.ID == request.Id, ct);

        return guide?.ToDto();
    }
}

public sealed class GetGuidesForHeroHandler(MerrickContext db) : IRequestHandler<GetGuidesForHeroQuery, List<HeroGuideDto>>
{
    public async Task<List<HeroGuideDto>> Handle(GetGuidesForHeroQuery request, CancellationToken ct)
    {
        List<HeroGuide> guides = await db.HeroGuides
            .Include(g => g.Author)
            .Include(g => g.Items)
            .Include(g => g.Abilities)
            .Where(g => g.HeroIdentifier == request.HeroIdentifier && g.Featured)
            .OrderByDescending(g => g.Rating)
            .ToListAsync(ct);

        return [.. guides.Select(g => g.ToDto())];
    }
}

public sealed class SaveGuideHandler(
    MerrickContext db,
    TimeProvider? timeProvider = null) : IRequestHandler<SaveGuideCommand, SaveGuideCommandResponse>
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<SaveGuideCommandResponse> Handle(SaveGuideCommand command, CancellationToken ct)
    {
        var req = command.Request;
        Account? currentAccount = await db.Accounts.FirstOrDefaultAsync(a => a.Name == req.AuthorAccountName, ct);
        if (currentAccount == null)
            return new SaveGuideCommandResponse(GuideSaveStatus.Unauthorized, new SaveGuideResultDto(false, 0, "Author account not found"));

        string sanitizedName = PerimeterSecuritySanitizer.SanitizeGuideText(req.Request.Name, PerimeterSecuritySanitizer.MaxGuideNameLength);
        if (string.IsNullOrWhiteSpace(sanitizedName)) sanitizedName = "Community Guide";

        string sanitizedIntro = PerimeterSecuritySanitizer.SanitizeGuideText(req.Request.Intro, PerimeterSecuritySanitizer.MaxGuideIntroLength);
        string sanitizedContent = PerimeterSecuritySanitizer.SanitizeGuideText(req.Request.Content, PerimeterSecuritySanitizer.MaxGuideContentLength);

        string sanitizedStartingItems = PerimeterSecuritySanitizer.SanitizeItemList(req.Request.StartingItems);
        string sanitizedEarlyGameItems = PerimeterSecuritySanitizer.SanitizeItemList(req.Request.EarlyGameItems);
        string sanitizedCoreItems = PerimeterSecuritySanitizer.SanitizeItemList(req.Request.CoreItems);
        string sanitizedLuxuryItems = PerimeterSecuritySanitizer.SanitizeItemList(req.Request.LuxuryItems);
        string sanitizedAbilityQueue = PerimeterSecuritySanitizer.SanitizeAbilityQueue(req.Request.AbilityQueue);

        HeroGuide? guide;
        if (req.Request.Id is > 0)
        {
            guide = await db.HeroGuides
                .Include(g => g.Author)
                .Include(g => g.Items)
                .Include(g => g.Abilities)
                .FirstOrDefaultAsync(g => g.ID == req.Request.Id.Value, ct);

            if (guide != null)
            {
                bool isAuthor = (guide.Author != null && guide.Author.ID == currentAccount.ID) ||
                                string.Equals(guide.Author?.Name, currentAccount.Name,
                                    StringComparison.OrdinalIgnoreCase);
                if (!isAuthor && !req.IsStaff)
                    return new SaveGuideCommandResponse(GuideSaveStatus.Forbidden, new SaveGuideResultDto(false, 0, "Forbidden"));

                guide.Name = sanitizedName;
                guide.HeroName = Heroes.ResolveHeroDisplayName(req.Request.HeroIdentifier);
                guide.HeroIdentifier = req.Request.HeroIdentifier;
                guide.TargetMap = (req.Request.TargetMap ?? "").Trim().ToLowerInvariant() == "midwars"
                    ? "midwars"
                    : "caldavar";
                guide.Intro = sanitizedIntro;
                guide.Content = sanitizedContent;

                if (guide.Items.Count > 0)
                {
                    db.HeroGuideItems.RemoveRange(guide.Items);
                    guide.Items.Clear();
                }
                if (guide.Abilities.Count > 0)
                {
                    db.HeroGuideAbilities.RemoveRange(guide.Abilities);
                    guide.Abilities.Clear();
                }

                guide.StartingItems = sanitizedStartingItems;
                guide.EarlyGameItems = sanitizedEarlyGameItems;
                guide.CoreItems = sanitizedCoreItems;
                guide.LuxuryItems = sanitizedLuxuryItems;
                guide.AbilityQueue = sanitizedAbilityQueue;
                guide.Public = req.Request.IsPublic;
                guide.TimestampLastUpdated = _timeProvider.GetUtcNow();

                await db.SaveChangesAsync(ct);
                return new SaveGuideCommandResponse(GuideSaveStatus.Success, new SaveGuideResultDto(true, guide.ID, "Guide updated successfully!"));
            }
        }

        guide = new HeroGuide
        {
            Name = sanitizedName,
            HeroName = Heroes.ResolveHeroDisplayName(req.Request.HeroIdentifier),
            HeroIdentifier = req.Request.HeroIdentifier,
            TargetMap = (req.Request.TargetMap ?? "").Trim().ToLowerInvariant() == "midwars" ? "midwars" : "caldavar",
            Intro = sanitizedIntro,
            Content = sanitizedContent,
            StartingItems = sanitizedStartingItems,
            EarlyGameItems = sanitizedEarlyGameItems,
            CoreItems = sanitizedCoreItems,
            LuxuryItems = sanitizedLuxuryItems,
            AbilityQueue = sanitizedAbilityQueue,
            Author = currentAccount,
            Rating = 5.0f,
            UpVotes = 1,
            DownVotes = 0,
            Public = req.Request.IsPublic,
            Featured = false,
            TimestampCreated = _timeProvider.GetUtcNow()
        };

        db.HeroGuides.Add(guide);
        await db.SaveChangesAsync(ct);
        return new SaveGuideCommandResponse(GuideSaveStatus.Success, new SaveGuideResultDto(true, guide.ID, "Guide created successfully!"));
    }
}

public sealed class DeleteGuideHandler(MerrickContext db) : IRequestHandler<DeleteGuideCommand, DeleteGuideCommandResponse>
{
    public async Task<DeleteGuideCommandResponse> Handle(DeleteGuideCommand command, CancellationToken ct)
    {
        HeroGuide? guide = await db.HeroGuides.Include(g => g.Author).FirstOrDefaultAsync(g => g.ID == command.Id, ct);
        if (guide == null)
            return new DeleteGuideCommandResponse(GuideDeleteStatus.NotFound, "Guide not found");

        if (guide.Featured)
            return new DeleteGuideCommandResponse(GuideDeleteStatus.BadRequest, "Official guides cannot be deleted");

        if (!string.Equals(guide.Author?.Name ?? "Anonymous", command.AccountName, StringComparison.OrdinalIgnoreCase))
            return new DeleteGuideCommandResponse(GuideDeleteStatus.Forbidden);

        db.HeroGuides.Remove(guide);
        await db.SaveChangesAsync(ct);
        return new DeleteGuideCommandResponse(GuideDeleteStatus.Success);
    }
}

public sealed class VoteGuideHandler(MerrickContext db) : IRequestHandler<VoteGuideCommand, VoteGuideResultDto?>
{
    public async Task<VoteGuideResultDto?> Handle(VoteGuideCommand command, CancellationToken ct)
    {
        HeroGuide? guide = await db.HeroGuides.FirstOrDefaultAsync(g => g.ID == command.Id, ct);
        if (guide == null) return null;

        if (command.IsUpvote) guide.UpVotes++;
        else guide.DownVotes++;

        int totalVotes = guide.UpVotes + guide.DownVotes;
        guide.Rating = totalVotes > 0 ? (float)Math.Round(guide.UpVotes / (double)totalVotes * 5.0, 1) : 5.0f;

        await db.SaveChangesAsync(ct);
        return new VoteGuideResultDto(true, guide.UpVotes, guide.DownVotes, guide.Rating, "Vote recorded");
    }
}

public sealed class ToggleGuidePrivacyHandler(
    MerrickContext db,
    TimeProvider? timeProvider = null) : IRequestHandler<ToggleGuidePrivacyCommand, TogglePrivacyCommandResponse>
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<TogglePrivacyCommandResponse> Handle(ToggleGuidePrivacyCommand command, CancellationToken ct)
    {
        HeroGuide? guide = await db.HeroGuides.Include(g => g.Author).FirstOrDefaultAsync(g => g.ID == command.Id, ct);
        if (guide == null)
            return new TogglePrivacyCommandResponse(GuideToggleStatus.NotFound, ErrorMessage: "Guide not found");

        if (!command.IsStaff && !string.Equals(guide.Author?.Name ?? "Anonymous", command.AccountName,
                StringComparison.OrdinalIgnoreCase))
            return new TogglePrivacyCommandResponse(GuideToggleStatus.Forbidden);

        guide.Public = !guide.Public;
        guide.TimestampLastUpdated = _timeProvider.GetUtcNow();
        await db.SaveChangesAsync(ct);

        return new TogglePrivacyCommandResponse(GuideToggleStatus.Success,
            new TogglePrivacyResultDto(true, guide.Public,
                guide.Public ? "Guide is now public" : "Guide is now private"));
    }
}

public sealed class ToggleGuideOfficialHandler(
    MerrickContext db,
    TimeProvider? timeProvider = null) : IRequestHandler<ToggleGuideOfficialCommand, ToggleOfficialResultDto?>
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<ToggleOfficialResultDto?> Handle(ToggleGuideOfficialCommand command, CancellationToken ct)
    {
        HeroGuide? guide = await db.HeroGuides.Include(g => g.Author).FirstOrDefaultAsync(g => g.ID == command.Id, ct);
        if (guide == null) return null;

        guide.Featured = !guide.Featured;
        guide.TimestampLastUpdated = _timeProvider.GetUtcNow();
        await db.SaveChangesAsync(ct);

        return new ToggleOfficialResultDto(true, guide.Featured,
            guide.Featured ? "Guide is now official" : "Guide is now community");
    }
}

public sealed class ToggleDefaultGuideHandler(MerrickContext db) : IRequestHandler<ToggleDefaultGuideCommand, ToggleDefaultGuideCommandResponse>
{
    public async Task<ToggleDefaultGuideCommandResponse> Handle(ToggleDefaultGuideCommand command, CancellationToken ct)
    {
        Account? currentAccount = await db.Accounts.Include(a => a.GuidePreferences).FirstOrDefaultAsync(a => a.Name == command.AccountName, ct);
        if (currentAccount == null)
            return new ToggleDefaultGuideCommandResponse(GuidePreferenceStatus.AccountNotFound);

        HeroGuide? guide = await db.HeroGuides.FirstOrDefaultAsync(g => g.ID == command.Id, ct);
        if (guide == null)
            return new ToggleDefaultGuideCommandResponse(GuidePreferenceStatus.GuideNotFound);

        bool isCurrentlyDefault = currentAccount.DefaultGuideIDs.Contains(command.Id);
        if (isCurrentlyDefault)
        {
            currentAccount.DefaultGuideIDs.Remove(command.Id);
            currentAccount.SyncDefaultGuidesFromList(currentAccount.DefaultGuideIDs);
            await db.SaveChangesAsync(ct);
            return new ToggleDefaultGuideCommandResponse(GuidePreferenceStatus.Success,
                new ToggleDefaultGuideResultDto(true, false, command.Id, guide.HeroName,
                    $"Removed default guide for {guide.HeroName}"));
        }

        List<int> sameHeroGuideIds = await db.HeroGuides
            .Where(g => g.HeroIdentifier == guide.HeroIdentifier)
            .Select(g => g.ID)
            .ToListAsync(ct);

        HashSet<int> sameHeroGuideIdSet = [.. sameHeroGuideIds];
        currentAccount.DefaultGuideIDs.RemoveAll(sameHeroGuideIdSet.Contains);
        currentAccount.DefaultGuideIDs.Add(command.Id);
        currentAccount.SyncDefaultGuidesFromList(currentAccount.DefaultGuideIDs);
        await db.SaveChangesAsync(ct);

        return new ToggleDefaultGuideCommandResponse(GuidePreferenceStatus.Success,
            new ToggleDefaultGuideResultDto(true, true, command.Id, guide.HeroName,
                $"Set '{guide.Name}' as default guide for {guide.HeroName}"));
    }
}

public sealed class ToggleFavoriteGuideHandler(MerrickContext db) : IRequestHandler<ToggleFavoriteGuideCommand, ToggleFavoriteGuideCommandResponse>
{
    public async Task<ToggleFavoriteGuideCommandResponse> Handle(ToggleFavoriteGuideCommand command, CancellationToken ct)
    {
        Account? currentAccount = await db.Accounts.Include(a => a.GuidePreferences).FirstOrDefaultAsync(a => a.Name == command.AccountName, ct);
        if (currentAccount == null)
            return new ToggleFavoriteGuideCommandResponse(GuidePreferenceStatus.AccountNotFound);

        HeroGuide? guide = await db.HeroGuides.FirstOrDefaultAsync(g => g.ID == command.Id, ct);
        if (guide == null)
            return new ToggleFavoriteGuideCommandResponse(GuidePreferenceStatus.GuideNotFound);

        bool isFavorite = currentAccount.FavoriteGuideIDs.Contains(command.Id);
        if (isFavorite)
        {
            currentAccount.FavoriteGuideIDs.Remove(command.Id);
            currentAccount.SyncFavoriteGuidesFromList(currentAccount.FavoriteGuideIDs);
            await db.SaveChangesAsync(ct);
            return new ToggleFavoriteGuideCommandResponse(GuidePreferenceStatus.Success,
                new ToggleFavoriteGuideResultDto(true, false, command.Id, $"Removed '{guide.Name}' from favorites"));
        }

        currentAccount.FavoriteGuideIDs.Add(command.Id);
        currentAccount.SyncFavoriteGuidesFromList(currentAccount.FavoriteGuideIDs);
        await db.SaveChangesAsync(ct);
        return new ToggleFavoriteGuideCommandResponse(GuidePreferenceStatus.Success,
            new ToggleFavoriteGuideResultDto(true, true, command.Id, $"Added '{guide.Name}' to favorites"));
    }
}

public sealed class GetHeroDifficultyVotesHandler(MerrickContext db) : IRequestHandler<GetHeroDifficultyVotesQuery, List<HeroDifficultyVoteDto>>
{
    public async Task<List<HeroDifficultyVoteDto>> Handle(GetHeroDifficultyVotesQuery request, CancellationToken ct)
    {
        List<HeroDifficultyVote> votes = await db.HeroDifficultyVotes
            .Where(v => v.HeroIdentifier == request.HeroIdentifier)
            .ToListAsync(ct);

        return [.. votes.Select(v => v.ToDto())];
    }
}

public sealed class VoteHeroDifficultyHandler(
    MerrickContext db,
    TimeProvider? timeProvider = null) : IRequestHandler<VoteHeroDifficultyCommand, HeroDifficultyVoteResultDto>
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<HeroDifficultyVoteResultDto> Handle(VoteHeroDifficultyCommand command, CancellationToken ct)
    {
        Account? account = await db.Accounts.FirstOrDefaultAsync(a => a.Name == command.Ign, ct);
        if (account == null)
            return new HeroDifficultyVoteResultDto(false, 0, 0, "Account not found");

        HeroDifficultyVote? existing = await db.HeroDifficultyVotes
            .FirstOrDefaultAsync(v => v.HeroIdentifier == command.HeroIdentifier && v.AccountID == account.ID, ct);

        if (existing != null)
        {
            existing.Rating = command.Difficulty;
            existing.Timestamp = _timeProvider.GetUtcNow();
        }
        else
        {
            db.HeroDifficultyVotes.Add(new HeroDifficultyVote
            {
                HeroIdentifier = command.HeroIdentifier,
                AccountID = account.ID,
                Rating = command.Difficulty,
                Timestamp = _timeProvider.GetUtcNow()
            });
        }

        await db.SaveChangesAsync(ct);

        List<HeroDifficultyVote> allVotes = await db.HeroDifficultyVotes
            .Where(v => v.HeroIdentifier == command.HeroIdentifier)
            .ToListAsync(ct);

        double avg = allVotes.Count > 0 ? Math.Round(allVotes.Average(v => (double)v.Rating), 1) : command.Difficulty;
        return new HeroDifficultyVoteResultDto(true, avg, allVotes.Count, "Difficulty vote recorded");
    }
}
