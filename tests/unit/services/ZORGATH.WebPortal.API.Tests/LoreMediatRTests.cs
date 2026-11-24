// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Lore;
using MERRICK.DatabaseContext.Persistence;
using NEXUS.Contracts.Clients;
using NEXUS.Contracts.CQRS.Lore;
using Xunit;
using ZORGATH.WebPortal.API.Features.Lore;

namespace ZORGATH.WebPortal.API.Tests;

public sealed class LoreMediatRTests
{
    [Fact]
    public async Task GetArcsHandler_ReturnsOnlyPublishedArcs_WhenNotIncludingUnpublished()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        LoreArc publishedArc = new() { ID = 1, Title = "The Great Scourge", Slug = "great-scourge", Description = "War", OrderIndex = 1, IsPublished = true };
        LoreArc draftArc = new() { ID = 2, Title = "Unreleased Arc", Slug = "unreleased", Description = "Draft", OrderIndex = 2, IsPublished = false };
        db.LoreArcs.AddRange(publishedArc, draftArc);
        await db.SaveChangesAsync();

        GetArcsHandler handler = new(db);

        var result = await handler.Handle(new GetArcsQuery(IncludeUnpublished: false), default);

        result.Should().HaveCount(1);
        result[0].Slug.Should().Be("great-scourge");
    }

    [Fact]
    public async Task GetArcBySlugHandler_ReturnsArc_WhenFound()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        LoreArc arc = new() { ID = 10, Title = "Beastwood Chronicles", Slug = "beastwood", Description = "Tales", OrderIndex = 1, IsPublished = true };
        db.LoreArcs.Add(arc);
        await db.SaveChangesAsync();

        GetArcBySlugHandler handler = new(db);

        var result = await handler.Handle(new GetArcBySlugQuery("beastwood"), default);

        result.Should().NotBeNull();
        result!.Title.Should().Be("Beastwood Chronicles");
    }

    [Fact]
    public async Task RateComicHandler_RecordsRating_CalculatesScore()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        User user = new()
        {
            ID = 1,
            EmailAddress = "lorerater@kongor.net",
            SRPPasswordHash = "h",
            SRPPasswordSalt = "s",
            PBKDF2PasswordHash = "p"
        };
        Account account = new() { ID = 10, Name = "ComicFan", User = user };
        LoreComic comic = new()
        {
            ID = 5,
            Title = "Issue #1",
            Slug = "issue-1",
            ArcID = 1,
            Synopsis = "First issue",
            CoverImageUrl = "https://cdn.kongor.net/cover1.jpg"
        };

        db.Users.Add(user);
        db.Accounts.Add(account);
        db.LoreComics.Add(comic);
        await db.SaveChangesAsync();

        RateComicHandler handler = new(db);

        var result = await handler.Handle(new RateComicCommand(5, 10, 5), default);

        result.Success.Should().BeTrue();
        result.Score.Should().Be(5);

        LoreRating? savedRating = db.LoreRatings.FirstOrDefault(r => r.ComicID == 5 && r.AccountID == 10);
        savedRating.Should().NotBeNull();
        savedRating!.Score.Should().Be(5);
    }

    [Fact]
    public async Task SaveComicHandler_WhenStaff_CreatesComicWithPagesAndCredits()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        LoreArc arc = new() { ID = 1, Title = "Arc 1", Slug = "arc-1", Description = "Desc", IsPublished = true };
        db.LoreArcs.Add(arc);
        await db.SaveChangesAsync();

        SaveComicHandler handler = new(db);

        var request = new SaveComicRequestDto(
            ID: null,
            ArcID: 1,
            Title: "Chapter 1",
            Slug: "chapter-1",
            IssueNumber: 1,
            Synopsis: "Beginning",
            CoverImageUrl: "https://cdn.kongor.net/cover.jpg",
            CinematicVideoUrl: null,
            IsPublished: true,
            Pages: [new SavePageRequestDto(null, 1, "https://cdn.kongor.net/p1.jpg", null, null)],
            Credits: [new SaveCreditRequestDto(null, null, "AuthorBob", "Writer", null, 1)]
        );

        var result = await handler.Handle(new SaveComicCommand(request, IsStaff: true), default);

        result.Should().NotBeNull();
        result!.Title.Should().Be("Chapter 1");
        result.PageCount.Should().Be(1);

        LoreComic? savedComic = db.LoreComics.FirstOrDefault(c => c.Slug == "chapter-1");
        savedComic.Should().NotBeNull();
    }

    [Fact]
    public async Task DeleteComicHandler_WhenStaff_DeletesComic()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        LoreComic comic = new()
        {
            ID = 20,
            Title = "ToDelete",
            Slug = "to-delete",
            Synopsis = "Short",
            CoverImageUrl = "https://cdn.kongor.net/cover20.jpg"
        };
        db.LoreComics.Add(comic);
        await db.SaveChangesAsync();

        DeleteComicHandler handler = new(db);

        var result = await handler.Handle(new DeleteComicCommand(20, IsStaff: true), default);

        result.Success.Should().BeTrue();
        db.LoreComics.Any(c => c.ID == 20).Should().BeFalse();
    }
}
