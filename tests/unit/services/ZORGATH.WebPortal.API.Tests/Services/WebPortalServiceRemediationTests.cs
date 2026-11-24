// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Game;
using MERRICK.DatabaseContext.Entities.Utility;
using MERRICK.DatabaseContext.Extensions;
using MERRICK.DatabaseContext.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.Admin;
using NEXUS.Contracts.Clients;
using NEXUS.Contracts.Domain;
using Xunit;
using ZORGATH.WebPortal.API.Controllers;
using ZORGATH.WebPortal.API.Features.Admin.Users;
using ZORGATH.WebPortal.API.Features.Guides;

namespace ZORGATH.WebPortal.API.Tests.Services;

public sealed class WebPortalServiceRemediationTests
{
    private static MerrickContext CreateInMemoryContext()
    {
        return TestDbContextFactory.CreateInMemoryContext();
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task AdminUserController_UpdateUserEconomy_LogsEconomyTransactionAudit()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange
        using MerrickContext db = CreateInMemoryContext();
        Role userRole = new() { ID = 1, Name = "User" };
        db.Roles.Add(userRole);

        User user = new()
        {
            EmailAddress = "admin_econ_target@kongor.test",
            PBKDF2PasswordHash = "h",
            SRPPasswordHash = "h",
            SRPPasswordSalt = "s",
            Role = userRole,
            GoldCoins = 100,
            SilverCoins = 500,
            PlinkoTickets = 2
        };
        Account account = new()
        {
            Name = "EconTarget",
            User = user,
            Type = AccountType.Normal,
            IsMain = true
        };
        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        AdminUserController controller = new(new LocalAdminUserMediator(db), NullLogger<AdminUserController>.Instance);

        // Act
        UpdateUserEconomyRequest req = new() { GoldCoins = 500, SilverCoins = 1200, PlinkoTickets = 10 };
        IActionResult result = await controller.UpdateUserEconomy(user.ID, req);

        // Assert
        result.Should().BeOfType<OkObjectResult>();

        User? updatedUser = await db.Users.FindAsync(user.ID);
        updatedUser!.GoldCoins.Should().Be(500);
        updatedUser.SilverCoins.Should().Be(1200);
        updatedUser.PlinkoTickets.Should().Be(10);

        EconomyTransaction? audit = await db.EconomyTransactions.FirstOrDefaultAsync(t =>
            t.UserID == user.ID && t.TransactionType == "AdminUpdateEconomy");
        audit.Should().NotBeNull();
        audit!.GoldCoinsDelta.Should().Be(400); // 500 - 100
        audit.SilverCoinsDelta.Should().Be(700); // 1200 - 500
        audit.PlinkoTicketsDelta.Should().Be(8); // 10 - 2
        audit.GoldCoinsBalanceAfter.Should().Be(500);
        audit.SilverCoinsBalanceAfter.Should().Be(1200);
        audit.PlinkoTicketsBalanceAfter.Should().Be(10);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task AdminUserController_AdjustUserBalance_LogsEconomyTransactionAudit()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange
        using MerrickContext db = CreateInMemoryContext();
        Role userRole = new() { ID = 1, Name = "User" };
        db.Roles.Add(userRole);

        User user = new()
        {
            EmailAddress = "adjust_balance_target@kongor.test",
            PBKDF2PasswordHash = "h",
            SRPPasswordHash = "h",
            SRPPasswordSalt = "s",
            Role = userRole,
            GoldCoins = 200,
            SilverCoins = 1000,
            PlinkoTickets = 5
        };
        Account account = new()
        {
            Name = "AdjustTarget",
            User = user,
            Type = AccountType.Normal,
            IsMain = true
        };
        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        AdminUserController controller = new(new LocalAdminUserMediator(db), NullLogger<AdminUserController>.Instance);

        // Act
        AdjustUserBalanceRequest req = new() { GoldDelta = -50, SilverDelta = 250, TicketsDelta = 3 };
        IActionResult result = await controller.AdjustUserBalance(user.ID, req);

        // Assert
        result.Should().BeOfType<OkObjectResult>();

        User? updatedUser = await db.Users.FindAsync(user.ID);
        updatedUser!.GoldCoins.Should().Be(150);
        updatedUser.SilverCoins.Should().Be(1250);
        updatedUser.PlinkoTickets.Should().Be(8);

        EconomyTransaction? audit = await db.EconomyTransactions.FirstOrDefaultAsync(t =>
            t.UserID == user.ID && t.TransactionType == "AdminAdjustBalance");
        audit.Should().NotBeNull();
        audit!.GoldCoinsDelta.Should().Be(-50);
        audit.SilverCoinsDelta.Should().Be(250);
        audit.PlinkoTicketsDelta.Should().Be(3);
        audit.GoldCoinsBalanceAfter.Should().Be(150);
        audit.SilverCoinsBalanceAfter.Should().Be(1250);
        audit.PlinkoTicketsBalanceAfter.Should().Be(8);
    }

    [Fact]
    public async Task GuideApiController_SearchGuides_HandlesGuideWithAuthorCorrectly()
    {
        using MerrickContext db = CreateInMemoryContext();
        Role userRole = new() { ID = 1, Name = "User" };
        db.Roles.Add(userRole);

        User user = new()
        {
            EmailAddress = "guide_author@kongor.test",
            PBKDF2PasswordHash = "h",
            SRPPasswordHash = "h",
            SRPPasswordSalt = "s",
            Role = userRole
        };
        Account account = new()
        {
            Name = "AuthorUser",
            User = user,
            Type = AccountType.Normal,
            IsMain = true
        };
        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        HeroGuide guide = new()
        {
            Name = "Pyromancer Mid Guide",
            HeroIdentifier = "Hero_Pyromancer",
            HeroName = "Pyromancer",
            TargetMap = "caldavar",
            Intro = "Pyromancer burning flames guide",
            Content = "Content text",
            Author = account,
            AuthorID = account.ID,
            Featured = true,
            Public = true,
            Rating = 4.5f,
            UpVotes = 10,
            DownVotes = 1,
            TimestampCreated = TimeProvider.System.GetUtcNow()
        };
        db.HeroGuides.Add(guide);
        await db.SaveChangesAsync();

        GuideApiController controller = new(new LocalGuideMediator(db));

        // Act
        IActionResult result = await controller.GetGuides("Pyromancer", q: "burning");

        // Assert
        OkObjectResult okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        GuideSearchResultDto searchResult = okResult.Value.Should().BeAssignableTo<GuideSearchResultDto>().Subject;
        searchResult.Guides.Should().HaveCount(1);
        searchResult.Guides[0].Name.Should().Be("Pyromancer Mid Guide");
        searchResult.Guides[0].AuthorAccountName.Should().Be("AuthorUser");
    }

    [Fact]
    public void HeroGuide_ToDto_HandlesNullAuthorResiliently()
    {
        // Direct unit test for DEF-01 DTO mapping resilience
        HeroGuide orphanGuide = new()
        {
            ID = 100,
            Name = "Orphan Guide",
            HeroIdentifier = "Hero_WitchSlayer",
            HeroName = "Witch Slayer",
            Author = null!,
            Featured = true,
            Public = true
        };

        HeroGuideDto dto = orphanGuide.ToDto();
        dto.Name.Should().Be("Orphan Guide");
        dto.AuthorAccountName.Should().Be(string.Empty);
    }
}
