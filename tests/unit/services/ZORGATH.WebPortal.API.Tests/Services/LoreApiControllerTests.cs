// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Lore;
using MERRICK.DatabaseContext.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NEXUS.Contracts.Clients;
using Xunit;
using ZORGATH.WebPortal.API.Controllers;
using ZORGATH.WebPortal.API.Features.Lore;

namespace ZORGATH.WebPortal.API.Tests.Services;

public sealed class LoreApiControllerTests
{
    private static MerrickContext CreateInMemoryContext()
    {
        return TestDbContextFactory.CreateInMemoryContext();
    }

    [Fact]
    public async Task LoreApiController_GetArcs_ReturnsPublishedArcs()
    {
        using MerrickContext db = CreateInMemoryContext();
        LoreArc arc1 = new()
            { Title = "Arc 1", Slug = "arc-1", Description = "Desc 1", OrderIndex = 1, IsPublished = true };
        LoreArc arc2 = new()
            { Title = "Arc 2", Slug = "arc-2", Description = "Desc 2", OrderIndex = 2, IsPublished = false };
        db.LoreArcs.AddRange(arc1, arc2);
        await db.SaveChangesAsync();

        LoreApiController controller = new(new LocalLoreMediator(db));
        IActionResult result = await controller.GetArcs(false);

        OkObjectResult okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        List<LoreArcDto> arcs = okResult.Value.Should().BeAssignableTo<List<LoreArcDto>>().Subject;
        arcs.Should().HaveCount(1);
        arcs[0].Title.Should().Be("Arc 1");
    }

    [Fact]
    public async Task LoreApiController_RateComic_RecordsRatingAndUpdatesScore()
    {
        using MerrickContext db = CreateInMemoryContext();
        User user = new()
        {
            EmailAddress = "test@kongor.net", SRPPasswordSalt = "s", SRPPasswordHash = "h", PBKDF2PasswordHash = "p"
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        Account account = new() { Name = "TestUser", User = user, IsMain = true };
        db.Accounts.Add(account);

        LoreArc arc = new()
            { Title = "Arc 1", Slug = "arc-1", Description = "Desc 1", OrderIndex = 1, IsPublished = true };
        db.LoreArcs.Add(arc);
        await db.SaveChangesAsync();

        LoreComic comic = new()
        {
            ArcID = arc.ID,
            Title = "Comic 1",
            Slug = "comic-1",
            Synopsis = "Syn",
            CoverImageUrl = "/cov.webp",
            IsPublished = true
        };
        db.LoreComics.Add(comic);
        await db.SaveChangesAsync();

        LoreApiController controller = new(new LocalLoreMediator(db));

        // Rate 5 stars
        RateComicRequestDto request = new(comic.ID, account.ID, 5);
        IActionResult rateResult = await controller.RateComic(comic.ID, request);
        rateResult.Should().BeOfType<OkObjectResult>();

        // Verify rating was recorded
        LoreRating? rating =
            await db.LoreRatings.FirstOrDefaultAsync(r => r.ComicID == comic.ID && r.AccountID == account.ID);
        rating.Should().NotBeNull();
        rating!.Score.Should().Be(5);

        // Update to 4 stars
        RateComicRequestDto updateRequest = new(comic.ID, account.ID, 4);
        await controller.RateComic(comic.ID, updateRequest);

        LoreRating? updatedRating =
            await db.LoreRatings.FirstOrDefaultAsync(r => r.ComicID == comic.ID && r.AccountID == account.ID);
        updatedRating!.Score.Should().Be(4);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task LoreApiController_GetStaffMetrics_CalculatesAverageAndDistribution()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        using MerrickContext db = CreateInMemoryContext();
        User user = new()
        {
            EmailAddress = "staff@kongor.net", SRPPasswordSalt = "s", SRPPasswordHash = "h", PBKDF2PasswordHash = "p"
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        Account acc1 = new() { Name = "User1", User = user, IsMain = true };
        Account acc2 = new() { Name = "User2", User = user, IsMain = false };
        db.Accounts.AddRange(acc1, acc2);

        LoreArc arc = new()
            { Title = "Arc 1", Slug = "arc-1", Description = "Desc 1", OrderIndex = 1, IsPublished = true };
        db.LoreArcs.Add(arc);
        await db.SaveChangesAsync();

        LoreComic comic = new()
        {
            ArcID = arc.ID, Title = "Comic 1", Slug = "comic-1", Synopsis = "Syn", CoverImageUrl = "/cov.webp",
            IsPublished = true
        };
        db.LoreComics.Add(comic);
        await db.SaveChangesAsync();

        db.LoreRatings.AddRange(
            new LoreRating { ComicID = comic.ID, AccountID = acc1.ID, Score = 5 },
            new LoreRating { ComicID = comic.ID, AccountID = acc2.ID, Score = 4 }
        );
        await db.SaveChangesAsync();

        LoreApiController controller = new(new LocalLoreMediator(db));
        IActionResult result = await controller.GetStaffMetrics(comic.ID);

        OkObjectResult okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        LoreRatingMetricDto metrics = okResult.Value.Should().BeOfType<LoreRatingMetricDto>().Subject;
        metrics.AverageScore.Should().Be(4.5);
        metrics.TotalRatings.Should().Be(2);
        metrics.ScoreDistribution[5].Should().Be(1);
        metrics.ScoreDistribution[4].Should().Be(1);
    }
}
