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
using Xunit;
using ZORGATH.WebPortal.API.Controllers;
using ZORGATH.WebPortal.API.Features.PlayerStats;

namespace ZORGATH.WebPortal.API.Tests;

public sealed class PlayerProfileMediatRTests
{
    [Fact]
    public async Task GetPlayerProfileHandler_WhenPlayerExists_ReturnsMappedSummaryDto()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();

        var account = new Account
        {
            ID = 101,
            Name = "Valkyrie",
            User = new User
            {
                ID = 501,
                EmailAddress = "valk@kongor.net",
                SRPPasswordSalt = "salt",
                SRPPasswordHash = "hash",
                PBKDF2PasswordHash = "pbkdf2_hash"
            }
        };
        db.Accounts.Add(account);
        db.PlayerStatistics.Add(new PlayerStatistics
        {
            ID = 1,
            AccountID = 101,
            HeroIdentifier = "Hero_Valkyrie",
            HeroKills = 15,
            HeroDeaths = 2,
            HeroAssists = 8,
            Win = 1,
            MatchID = 999
        });
        await db.SaveChangesAsync();

        GetPlayerProfileHandler handler = new(db, NullLogger<GetPlayerProfileHandler>.Instance);

        var result = await handler.Handle(new GetPlayerProfileQuery("Valkyrie"), CancellationToken.None);

        result.Should().NotBeNull();
        result!.Account.Name.Should().Be("Valkyrie");
        result.TopHeroes.Should().HaveCount(1);
        result.TopHeroes[0].HeroIdentifier.Should().Be("Hero_Valkyrie");
        result.TopHeroes[0].HeroKills.Should().Be(15);
    }

    [Fact]
    public async Task GetPlayerProfileHandler_WhenPlayerNotFound_ReturnsNull()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();

        GetPlayerProfileHandler handler = new(db, NullLogger<GetPlayerProfileHandler>.Instance);

        var result = await handler.Handle(new GetPlayerProfileQuery("GhostPlayer"), CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public void GetPlayerProfileValidator_ValidatesNameRequirements()
    {
        GetPlayerProfileValidator validator = new();

        ValidationResult emptyResult = validator.Validate(new GetPlayerProfileQuery(""));
        emptyResult.IsValid.Should().BeFalse();
        emptyResult.Errors.Should().Contain(e => e.PropertyName == "InGameName");

        ValidationResult validResult = validator.Validate(new GetPlayerProfileQuery("MoonQueen"));
        validResult.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Controller_WithInjectedMediator_DelegatesToMediatR()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        Mock<IMatchHistoryRepository> mockMatch = new();
        Mock<IAccountRepository> mockAccount = new();
        Mock<IMediator> mockMediator = new();

        PlayerProfileSummaryDto dummyDto = new(
            new AccountDto { ID = 1, Name = "TestHero" },
            new UserDto { ID = 1, EmailAddress = "testhero@kongor.net" },
            null, null, null, [], [], [], null, null,
            new HostTelemetryResultDto(false, false, [], [], [], 0, 0, 0, 0, null, null, "host"),
            []);

        mockMediator
            .Setup(m => m.Send(It.Is<GetPlayerProfileQuery>(q => q.InGameName == "TestHero"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(dummyDto);

        PlayerStatsApiController controller = new(mockMediator.Object);

        IActionResult actionResult = await controller.GetPlayerProfile("TestHero", CancellationToken.None);

        var okResult = actionResult.Should().BeOfType<OkObjectResult>().Subject;
        okResult.Value.Should().Be(dummyDto);
        mockMediator.Verify(m => m.Send(It.IsAny<GetPlayerProfileQuery>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
