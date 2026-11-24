// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Game;
using MERRICK.DatabaseContext.Persistence;
using NEXUS.Contracts.Clients;
using NEXUS.Contracts.CQRS.Guides;
using Xunit;
using ZORGATH.WebPortal.API.Controllers;
using ZORGATH.WebPortal.API.Features.Guides;

namespace ZORGATH.WebPortal.API.Tests;

public sealed class GuideMediatRTests
{
    [Fact]
    public async Task SearchGuidesHandler_FiltersByHeroAndMap_ReturnsPaginatedResults()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        User authorUser = new() { ID = 99, EmailAddress = "author@kongor.net", SRPPasswordHash = "h", SRPPasswordSalt = "s", PBKDF2PasswordHash = "p" };
        Account authorAccount = new() { ID = 99, Name = "GuideAuthor", User = authorUser };
        db.Users.Add(authorUser);
        db.Accounts.Add(authorAccount);

        HeroGuide guide1 = new()
        {
            ID = 1,
            Author = authorAccount,
            Name = "Pyromancer Mid Guide",
            HeroIdentifier = "Hero_Pyromancer",
            HeroName = "Pyromancer",
            TargetMap = "caldavar",
            Featured = true,
            Public = true,
            Rating = 4.8f,
            UpVotes = 10,
            DownVotes = 1,
            TimestampCreated = DateTimeOffset.UtcNow
        };
        HeroGuide guide2 = new()
        {
            ID = 2,
            Author = authorAccount,
            Name = "Valkyrie Carry",
            HeroIdentifier = "Hero_Valkyrie",
            HeroName = "Valkyrie",
            TargetMap = "caldavar",
            Featured = true,
            Public = true,
            Rating = 4.5f,
            UpVotes = 5,
            DownVotes = 0,
            TimestampCreated = DateTimeOffset.UtcNow
        };
        HeroGuide guide3 = new()
        {
            ID = 3,
            Author = authorAccount,
            Name = "Pyromancer Mid Wars Brawler",
            HeroIdentifier = "Hero_Pyromancer",
            HeroName = "Pyromancer",
            TargetMap = "midwars",
            Featured = false,
            Public = true,
            Rating = 4.0f,
            UpVotes = 2,
            DownVotes = 0,
            TimestampCreated = DateTimeOffset.UtcNow
        };

        db.HeroGuides.AddRange(guide1, guide2, guide3);
        await db.SaveChangesAsync();

        SearchGuidesHandler handler = new(db);

        // Filter by Hero_Pyromancer and caldavar
        var result = await handler.Handle(new SearchGuidesQuery(Hero: "Pyromancer", Map: "caldavar"), default);

        result.Guides.Should().HaveCount(1);
        result.Guides[0].ID.Should().Be(1);
        result.TotalCount.Should().Be(1);
    }

    [Fact]
    public async Task GetGuideByIdHandler_ReturnsGuideDto_WhenFound()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        User user = new()
        {
            ID = 1,
            EmailAddress = "author@kongor.net",
            SRPPasswordHash = "h",
            SRPPasswordSalt = "s",
            PBKDF2PasswordHash = "p"
        };
        Account account = new() { ID = 10, Name = "GuideAuthor", User = user };
        HeroGuide guide = new()
        {
            ID = 42,
            Name = "Valkyrie Pro Guide",
            HeroIdentifier = "Hero_Valkyrie",
            HeroName = "Valkyrie",
            TargetMap = "caldavar",
            Intro = "Intro text",
            Content = "Content text",
            Author = account,
            Featured = true,
            Public = true,
            Rating = 5.0f,
            TimestampCreated = DateTimeOffset.UtcNow
        };

        db.Users.Add(user);
        db.Accounts.Add(account);
        db.HeroGuides.Add(guide);
        await db.SaveChangesAsync();

        GetGuideByIdHandler handler = new(db);

        var result = await handler.Handle(new GetGuideByIdQuery(42), default);

        result.Should().NotBeNull();
        result!.ID.Should().Be(42);
        result.Name.Should().Be("Valkyrie Pro Guide");
        result.AuthorAccountName.Should().Be("GuideAuthor");
    }

    [Fact]
    public async Task SaveGuideHandler_SanitizesInput_AndCreatesNewGuide()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        User user = new()
        {
            ID = 2,
            EmailAddress = "creator@kongor.net",
            SRPPasswordHash = "h",
            SRPPasswordSalt = "s",
            PBKDF2PasswordHash = "p"
        };
        Account account = new() { ID = 20, Name = "GuideCreator", User = user };
        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        SaveGuideHandler handler = new(db);

        var requestDto = new SaveHeroGuideRequestDto
        {
            Id = null,
            Name = "<b>Test Guide</b>",
            HeroIdentifier = "Hero_Pyromancer",
            TargetMap = "caldavar",
            Intro = "<script>alert('xss')</script>Clean intro",
            Content = "Detailed build instructions",
            IsPublic = true,
            StartingItems = "Item_RunesOfTheFallen",
            EarlyGameItems = "",
            CoreItems = "",
            LuxuryItems = "",
            AbilityQueue = "1,2,1,3"
        };

        var response = await handler.Handle(new SaveGuideCommand(new SaveGuideApiRequestDto(requestDto, "GuideCreator", false)), default);

        response.Status.Should().Be(GuideSaveStatus.Success);
        response.Dto.Success.Should().BeTrue();
        response.Dto.GuideId.Should().BeGreaterThan(0);

        HeroGuide? created = db.HeroGuides.FirstOrDefault(g => g.ID == response.Dto.GuideId);
        created.Should().NotBeNull();
        created!.Name.Should().NotContain("<script>");
        created.Intro.Should().NotContain("<script>");
    }

    [Fact]
    public async Task SaveGuideHandler_WhenNonAuthorAndNotStaff_ReturnsForbidden()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        User authorUser = new() { ID = 1, EmailAddress = "author@kongor.net", SRPPasswordHash = "h", SRPPasswordSalt = "s", PBKDF2PasswordHash = "p" };
        User otherUser = new() { ID = 2, EmailAddress = "other@kongor.net", SRPPasswordHash = "h", SRPPasswordSalt = "s", PBKDF2PasswordHash = "p" };
        Account authorAcc = new() { ID = 1, Name = "Author", User = authorUser };
        Account otherAcc = new() { ID = 2, Name = "Other", User = otherUser };

        HeroGuide guide = new()
        {
            ID = 5,
            Name = "Author's Guide",
            HeroIdentifier = "Hero_Valkyrie",
            Author = authorAcc,
            Public = true,
            TimestampCreated = DateTimeOffset.UtcNow
        };

        db.Users.AddRange(authorUser, otherUser);
        db.Accounts.AddRange(authorAcc, otherAcc);
        db.HeroGuides.Add(guide);
        await db.SaveChangesAsync();

        SaveGuideHandler handler = new(db);

        var requestDto = new SaveHeroGuideRequestDto
        {
            Id = 5,
            Name = "Hacked Guide",
            HeroIdentifier = "Hero_Valkyrie"
        };

        var response = await handler.Handle(new SaveGuideCommand(new SaveGuideApiRequestDto(requestDto, "Other", false)), default);

        response.Status.Should().Be(GuideSaveStatus.Forbidden);
    }

    [Fact]
    public async Task ToggleDefaultGuideHandler_SetsAndUnsetsDefaultGuide()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        User user = new() { ID = 1, EmailAddress = "player@kongor.net", SRPPasswordHash = "h", SRPPasswordSalt = "s", PBKDF2PasswordHash = "p" };
        Account account = new() { ID = 1, Name = "ActivePlayer", User = user };
        HeroGuide guide = new()
        {
            ID = 10,
            Name = "Default Guide",
            HeroIdentifier = "Hero_Pyromancer",
            HeroName = "Pyromancer",
            Public = true,
            TimestampCreated = DateTimeOffset.UtcNow
        };

        db.Users.Add(user);
        db.Accounts.Add(account);
        db.HeroGuides.Add(guide);
        await db.SaveChangesAsync();

        ToggleDefaultGuideHandler handler = new(db);

        // 1. Set as default
        var setRes = await handler.Handle(new ToggleDefaultGuideCommand(10, "ActivePlayer"), default);
        setRes.Status.Should().Be(GuidePreferenceStatus.Success);
        setRes.Dto!.IsDefault.Should().BeTrue();
        account.DefaultGuideIDs.Should().Contain(10);

        // 2. Unset as default
        var unsetRes = await handler.Handle(new ToggleDefaultGuideCommand(10, "ActivePlayer"), default);
        unsetRes.Status.Should().Be(GuidePreferenceStatus.Success);
        unsetRes.Dto!.IsDefault.Should().BeFalse();
        account.DefaultGuideIDs.Should().NotContain(10);
    }

    [Fact]
    public async Task VoteGuideHandler_RecalculatesFiveStarRating()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        HeroGuide guide = new()
        {
            ID = 20,
            Name = "Votable Guide",
            HeroIdentifier = "Hero_Valkyrie",
            UpVotes = 3,
            DownVotes = 1,
            Rating = 3.8f,
            TimestampCreated = DateTimeOffset.UtcNow
        };
        db.HeroGuides.Add(guide);
        await db.SaveChangesAsync();

        VoteGuideHandler handler = new(db);

        var result = await handler.Handle(new VoteGuideCommand(20, IsUpvote: true), default);

        result.Should().NotBeNull();
        result!.UpVotes.Should().Be(4);
        result.DownVotes.Should().Be(1);
        // 4 up / 5 total * 5.0 = 4.0
        result.Rating.Should().Be(4.0f);
    }

    [Fact]
    public async Task VoteHeroDifficultyHandler_ComputesAverageDifficulty()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        User u1 = new() { ID = 1, EmailAddress = "voter1@kongor.net", SRPPasswordHash = "h", SRPPasswordSalt = "s", PBKDF2PasswordHash = "p" };
        User u2 = new() { ID = 2, EmailAddress = "voter2@kongor.net", SRPPasswordHash = "h", SRPPasswordSalt = "s", PBKDF2PasswordHash = "p" };
        Account a1 = new() { ID = 1, Name = "Voter1", User = u1 };
        Account a2 = new() { ID = 2, Name = "Voter2", User = u2 };

        db.Users.AddRange(u1, u2);
        db.Accounts.AddRange(a1, a2);
        await db.SaveChangesAsync();

        VoteHeroDifficultyHandler handler = new(db);

        var res1 = await handler.Handle(new VoteHeroDifficultyCommand("Hero_Pyromancer", "Voter1", 2), default);
        res1.Success.Should().BeTrue();
        res1.AverageDifficulty.Should().Be(2.0);
        res1.TotalVotes.Should().Be(1);

        var res2 = await handler.Handle(new VoteHeroDifficultyCommand("Hero_Pyromancer", "Voter2", 4), default);
        res2.Success.Should().BeTrue();
        res2.AverageDifficulty.Should().Be(3.0);
        res2.TotalVotes.Should().Be(2);
    }

    [Fact]
    public async Task GuideApiController_DirectControllerIntegration_RoutesViaMediator()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        User u = new() { ID = 1, EmailAddress = "ctrl@kongor.net", SRPPasswordHash = "h", SRPPasswordSalt = "s", PBKDF2PasswordHash = "p" };
        Account acc = new() { ID = 1, Name = "ControllerTester", User = u };
        HeroGuide guide = new()
        {
            ID = 100,
            Name = "Official Pyro Guide",
            HeroIdentifier = "Hero_Pyromancer",
            HeroName = "Pyromancer",
            Author = acc,
            Featured = true,
            Public = true,
            Rating = 5.0f,
            TimestampCreated = DateTimeOffset.UtcNow
        };

        db.Users.Add(u);
        db.Accounts.Add(acc);
        db.HeroGuides.Add(guide);
        await db.SaveChangesAsync();

        // Testing the standalone LocalGuideMediator
        GuideApiController controller = new(new LocalGuideMediator(db));

        IActionResult getByIdRes = await controller.GetGuideById(100);
        var okResult = getByIdRes as OkObjectResult;
        okResult.Should().NotBeNull();
        var dto = okResult!.Value as HeroGuideDto;
        dto.Should().NotBeNull();
        dto!.Name.Should().Be("Official Pyro Guide");

        IActionResult listRes = await controller.GetGuides();
        var listOk = listRes as OkObjectResult;
        listOk.Should().NotBeNull();
        var searchDto = listOk!.Value as GuideSearchResultDto;
        searchDto.Should().NotBeNull();
        searchDto!.TotalCount.Should().Be(1);
    }
}
