// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using MediatR;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Game;
using MERRICK.DatabaseContext.Entities.Statistics;
using MERRICK.DatabaseContext.Entities.Utility;
using MERRICK.DatabaseContext.Enumerations;
using MERRICK.DatabaseContext.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NEXUS.Contracts.CQRS.PlayerStats;
using NEXUS.Contracts.Data;
using NEXUS.Contracts.Domain;
using NEXUS.Contracts.Fleet;
using Xunit;
using ZORGATH.WebPortal.API.Controllers;
using ZORGATH.WebPortal.API.Features.PlayerStats;

namespace ZORGATH.WebPortal.API.Tests;

public sealed class HostLeaderboardMediatRTests
{
    [Fact]
    public void GetHostLeaderboardValidator_PassesByDefault()
    {
        GetHostLeaderboardValidator validator = new();
        var result = validator.Validate(new GetHostLeaderboardQuery());
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task GetHostLeaderboardHandler_ReturnsRankedHostLeaderboardWithBadgeTiers()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();

        User user1 = new()
        {
            ID = 100,
            EmailAddress = "host1@kongor.net",
            SRPPasswordSalt = "salt",
            SRPPasswordHash = "hash",
            PBKDF2PasswordHash = "pbkdf2"
        };
        Account hostAcc1 = new()
        {
            ID = 201,
            Name = "USE_ServerHost_01",
            Type = AccountType.ServerHost,
            User = user1,
            UserID = 100
        };
        Account playerAcc1 = new()
        {
            ID = 202,
            Name = "ApexHostPlayer",
            Type = AccountType.Normal,
            User = user1,
            UserID = 100,
            IsMain = true
        };
        user1.Accounts = [hostAcc1, playerAcc1];

        HostApplication app1 = new()
        {
            ID = 1,
            UserID = 100,
            User = user1,
            MainAccount = playerAcc1,
            MainAccountID = 202,
            HostAccount = hostAcc1,
            HostAccountID = 201,
            HostUsername = "USE_ServerHost_01",
            Status = HostApplicationStatus.Approved,
            Location = "USE",
            Instances = 4
        };

        db.Users.Add(user1);
        db.HostApplications.Add(app1);
        await db.SaveChangesAsync();

        // Add 60 hosted matches for this host
        for (int i = 0; i < 60; i++)
        {
            db.MatchStatistics.Add(new MatchStatistics
            {
                MatchID = 1000 + i,
                HostAccountName = "USE_ServerHost_01",
                TimestampRecorded = DateTimeOffset.UtcNow
            });
        }

        // Add 20 played matches for player account (ratio = 60/20 = 3.0 -> "Patron of Newerth")
        for (int i = 0; i < 20; i++)
        {
            db.PlayerStatistics.Add(new PlayerStatistics
            {
                ID = 5000 + i,
                AccountID = 202,
                MatchID = 1000 + i,
                HeroIdentifier = "Hero_Chronos"
            });
        }

        await db.SaveChangesAsync();

        GetHostLeaderboardHandler handler = new(db, NullLogger<GetHostLeaderboardHandler>.Instance);
        var result = await handler.Handle(new GetHostLeaderboardQuery(), CancellationToken.None);

        result.Should().NotBeNull();
        result.Should().HaveCount(1);
        var hostEntry = result[0];
        hostEntry.AccountName.Should().Be("USE_ServerHost_01");
        hostEntry.GamesHosted.Should().Be(60);
        hostEntry.GamesPlayed.Should().Be(20);
        hostEntry.HostingRatio.Should().Be(3.0);
        hostEntry.HostBadgeTier.Should().Be("Patron of Newerth");
        hostEntry.Rank.Should().Be(1);
    }

    [Fact]
    public async Task Controller_GetHostLeaderboard_WithInjectedMediator_DelegatesToMediatR()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        Mock<IMatchHistoryRepository> mockMatch = new();
        Mock<IAccountRepository> mockAccount = new();
        Mock<IMediator> mockMediator = new();

        IReadOnlyList<HostLeaderboardEntryDto> dummyList =
        [
            new HostLeaderboardEntryDto
            {
                AccountId = 1,
                AccountName = "GrandPatron",
                HostAccountName = "GrandPatron",
                AvatarUrl = "/avatars/1",
                GamesHosted = 300,
                GamesPlayed = 100,
                HostingRatio = 3.0,
                HostBadgeTier = "Patron of Newerth",
                Rank = 1
            }
        ];

        mockMediator
            .Setup(m => m.Send(It.IsAny<GetHostLeaderboardQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(dummyList);

        PlayerStatsApiController controller = new(mockMediator.Object);

        IActionResult actionResult = await controller.GetHostLeaderboard(CancellationToken.None);

        var okResult = actionResult.Should().BeOfType<OkObjectResult>().Subject;
        okResult.Value.Should().Be(dummyList);
        mockMediator.Verify(m => m.Send(It.IsAny<GetHostLeaderboardQuery>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
