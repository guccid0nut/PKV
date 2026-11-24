// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ASPIRE.Common.Serialization.PHP;
using FluentAssertions;
using KONGOR.MasterServer.Handlers.ClientRequester;
using KONGOR.MasterServer.Models.RequestResponse.GameData;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Game;
using NEXUS.Contracts.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Moq;
using StackExchange.Redis;
using Xunit;

namespace KONGOR.MasterServer.Tests;

public sealed class PerimeterShieldingTests
{
    [Fact]
    public async Task GuideHandler_GetGuideList_WhenDatabaseThrowsException_ReturnsSyntheticFallback200Ok()
    {
        // Arrange
        Mock<IGuideRepository> guideRepoMock = new();
        Mock<IAccountRepository> accountRepoMock = new();
        Mock<ISessionRepository> sessionRepoMock = new();
        Mock<IDatabase> redisMock = new();

        // Simulate database query failure (e.g. PostgresException)
        guideRepoMock
            .Setup(r => r.GetGuidesForHeroAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("column h1.AbilitySlot does not exist"));

        GuideHandler handler = new(
            guideRepoMock.Object,
            accountRepoMock.Object,
            sessionRepoMock.Object,
            redisMock.Object,
            NullLogger<GuideHandler>.Instance);

        DefaultHttpContext context = new();
        context.Request.Query = new QueryCollection(new Dictionary<string, StringValues>
        {
            ["f"] = new("get_guide_list_filtered"),
            ["hero"] = new("Hero_Nomad"),
            ["account"] = new("59")
        });

        // Act
        IActionResult result = await handler.HandleRequestAsync(context);

        // Assert
        result.Should().BeOfType<PhpResult>();
        PhpResult phpResult = (PhpResult)result;
        phpResult.Value.Should().BeOfType<GuideListResponse>();

        GuideListResponse listResponse = (GuideListResponse)phpResult.Value;
        listResponse.Success.Should().Be(1);
        listResponse.GuideList.Should().Contain("Featured Nomad Guide");
        listResponse.GuideList.Should().Contain("99999");
    }

    [Fact]
    public async Task GuideHandler_GetGuide_WhenDatabaseThrowsException_ReturnsSyntheticFallback200Ok()
    {
        // Arrange
        Mock<IGuideRepository> guideRepoMock = new();
        Mock<IAccountRepository> accountRepoMock = new();
        Mock<ISessionRepository> sessionRepoMock = new();
        Mock<IDatabase> redisMock = new();

        guideRepoMock
            .Setup(r => r.GetByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("Database connection timed out"));

        guideRepoMock
            .Setup(r => r.GetActiveGuideForHeroAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<IEnumerable<int>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("Database connection timed out"));

        GuideHandler handler = new(
            guideRepoMock.Object,
            accountRepoMock.Object,
            sessionRepoMock.Object,
            redisMock.Object,
            NullLogger<GuideHandler>.Instance);

        DefaultHttpContext context = new();
        context.Request.Query = new QueryCollection(new Dictionary<string, StringValues>
        {
            ["f"] = new("get_guide"),
            ["hero"] = new("Hero_Nomad"),
            ["gid"] = new("171")
        });

        // Act
        IActionResult result = await handler.HandleRequestAsync(context);

        // Assert
        result.Should().BeOfType<PhpResult>();
        PhpResult phpResult = (PhpResult)result;
        phpResult.Value.Should().BeOfType<GuideResponseSuccess>();

        GuideResponseSuccess response = (GuideResponseSuccess)phpResult.Value;
        response.Success.Should().Be(1);
        response.Name.Should().Be("Featured Nomad Guide");
        response.StartingItems.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void GuideResponseSuccess_PerimeterShielding_EnforcesMaxItemLimitsAndSanitizesText()
    {
        // Arrange: Guide with > 10 items, control characters, quotes, and > 25 abilities
        HeroGuide rawGuide = new()
        {
            ID = 100,
            Name = "Pro 'Nomad' `Guide`\0\x07",
            HeroName = "Nomad",
            HeroIdentifier = "Hero_Nomad",
            Author = new Account { ID = 1, Name = "Gucci'Donut`\x1B", User = null!, IsMain = true },
            Intro = "Intro with \0 dangerous 'quotes' and `backticks`",
            Content = "Detailed content \x01\x02 sanitized",
            StartingItems = "marchers|marchers|marchers|marchers|marchers|marchers|marchers|marchers|marchers|marchers|marchers|marchers",
            AbilityQueue = string.Join('|', Enumerable.Repeat("1", 30))
        };

        // Act
        GuideResponseSuccess response = new(rawGuide, 123456);

        // Assert: Names and texts sanitized
        response.Name.Should().Be("Pro Nomad Guide");
        response.AuthorName.Should().Be("GucciDonut");
        response.Intro.Should().NotContain("\0").And.NotContain("'").And.NotContain("`");

        // Assert: Starting items bounded to 10 max
        string[] startingItems = response.StartingItems.Split('|', StringSplitOptions.RemoveEmptyEntries);
        startingItems.Length.Should().BeLessThanOrEqualTo(10);
        startingItems[0].Should().Be("Item_Marchers");

        // Assert: Ability queue bounded to 25 steps max
        string[] abilities = response.AbilityQueue.Split('|', StringSplitOptions.RemoveEmptyEntries);
        abilities.Length.Should().BeLessThanOrEqualTo(25);
    }

    [Fact]
    public async Task ClanHandler_RankSanitization_RejectsInvalidOrMaliciousRank()
    {
        // Arrange
        Mock<IClanRepository> clanRepoMock = new();
        Mock<IAccountRepository> accountRepoMock = new();
        Mock<IDatabase> redisMock = new();

        ClanHandler handler = new(
            clanRepoMock.Object,
            accountRepoMock.Object,
            redisMock.Object,
            NullLogger<ClanHandler>.Instance);

        DefaultHttpContext context = new();
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["cookie"] = new("valid_cookie"),
            ["target_id"] = new("10"),
            ["clan_id"] = new("1"),
            ["rank"] = new("Officer; DROP TABLE users;--")
        });

        // Act
        IActionResult result = await handler.HandleRequestAsync(context);

        // Assert
        result.Should().BeOfType<BadRequestObjectResult>();
        BadRequestObjectResult badReq = (BadRequestObjectResult)result;
        badReq.Value.Should().Be("Invalid rank parameter");
    }
}
