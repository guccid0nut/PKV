// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using FluentValidation.Results;
using MediatR;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Game;
using MERRICK.DatabaseContext.Entities.Statistics;
using MERRICK.DatabaseContext.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NEXUS.Contracts.Clients;
using NEXUS.Contracts.CQRS.PlayerStats;
using NEXUS.Contracts.Data;
using NEXUS.Contracts.Domain;
using Xunit;
using ZORGATH.WebPortal.API.Controllers;
using ZORGATH.WebPortal.API.Features.PlayerStats;

namespace ZORGATH.WebPortal.API.Tests;

public sealed class LeaderboardMediatRTests
{
    [Fact]
    public async Task GetLeaderboardHandler_ReturnsAggregatedLeaderboard()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();

        var acc1 = new Account
        {
            ID = 10,
            Name = "TopPlayer",
            Type = AccountType.Normal,
            User = new User
            {
                ID = 10,
                EmailAddress = "top@kongor.net",
                SRPPasswordSalt = "s",
                SRPPasswordHash = "h",
                PBKDF2PasswordHash = "p"
            }
        };
        var acc2 = new Account
        {
            ID = 20,
            Name = "SecondPlayer",
            Type = AccountType.Normal,
            User = new User
            {
                ID = 20,
                EmailAddress = "second@kongor.net",
                SRPPasswordSalt = "s",
                SRPPasswordHash = "h",
                PBKDF2PasswordHash = "p"
            }
        };
        db.Accounts.AddRange(acc1, acc2);

        db.PlayerStatistics.AddRange(
            new PlayerStatistics
            {
                ID = 1,
                AccountID = 10,
                HeroIdentifier = "Hero_Pyromancer",
                HeroKills = 10,
                HeroDeaths = 2,
                HeroAssists = 5,
                Win = 1,
                MatchID = 100
            },
            new PlayerStatistics
            {
                ID = 2,
                AccountID = 20,
                HeroIdentifier = "Hero_Devourer",
                HeroKills = 2,
                HeroDeaths = 8,
                HeroAssists = 1,
                Loss = 1,
                MatchID = 100
            }
        );
        await db.SaveChangesAsync();

        GetLeaderboardHandler handler = new(db, NullLogger<GetLeaderboardHandler>.Instance);

        var result = await handler.Handle(new GetLeaderboardQuery(Page: 1, PageSize: 10), CancellationToken.None);

        result.Should().NotBeNull();
        result.TotalCount.Should().Be(2);
        result.Entries.Should().HaveCount(2);
        result.Entries[0].AccountId.Should().Be(10);
        result.Entries[0].Wins.Should().Be(1);
        result.Entries[0].WinRate.Should().Be(100.0);
        result.Entries[0].FavoriteHero.Should().Be("Hero_Pyromancer");
    }

    [Fact]
    public void GetLeaderboardValidator_EnforcesPagingBounds()
    {
        GetLeaderboardValidator validator = new();

        ValidationResult badPage = validator.Validate(new GetLeaderboardQuery(Page: 0, PageSize: 50));
        badPage.IsValid.Should().BeFalse();
        badPage.Errors.Should().Contain(e => e.PropertyName == "Page");

        ValidationResult badPageSize = validator.Validate(new GetLeaderboardQuery(Page: 1, PageSize: 500));
        badPageSize.IsValid.Should().BeFalse();
        badPageSize.Errors.Should().Contain(e => e.PropertyName == "PageSize");

        ValidationResult valid = validator.Validate(new GetLeaderboardQuery(Page: 1, PageSize: 50));
        valid.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Controller_GetLeaderboard_WithInjectedMediator_DelegatesToMediatR()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        Mock<IMatchHistoryRepository> mockMatch = new();
        Mock<IAccountRepository> mockAccount = new();
        Mock<IMediator> mockMediator = new();

        LeaderboardResultDto dummyDto = new([], 0, 1, 1);

        mockMediator
            .Setup(m => m.Send(It.IsAny<GetLeaderboardQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(dummyDto);

        PlayerStatsApiController controller = new(mockMediator.Object);

        IActionResult actionResult = await controller.GetLeaderboard(page: 1, pageSize: 25, ct: default);

        var okResult = actionResult.Should().BeOfType<OkObjectResult>().Subject;
        okResult.Value.Should().Be(dummyDto);
        mockMediator.Verify(m => m.Send(It.IsAny<GetLeaderboardQuery>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
