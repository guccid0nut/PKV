// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ASPIRE.Common.Caching;
using ASPIRE.Common.Constants;
using ASPIRE.Common.Serialization.PHP;
using FluentAssertions;
using KONGOR.MasterServer.Handlers.ClientRequester;
using KONGOR.MasterServer.Models.Configuration;
using KONGOR.MasterServer.Services;
using KONGOR.MasterServer.Services.Security;
using KONGOR.MasterServer.Tests.Infrastructure;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Statistics;
using MERRICK.DatabaseContext.Entities.Utility;
using MERRICK.DatabaseContext.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Moq;
using NEXUS.Contracts.Data;
using NEXUS.Contracts.Domain;
using Xunit;

namespace KONGOR.MasterServer.Tests;

public sealed class SimpleStatsCachingTests
{
    private static (Account account, User user) CreateTestAccount(string name = "testuser", int accountId = 100)
    {
        User user = new()
        {
            ID = accountId,
            EmailAddress = $"{name}@kongor.net",
            PBKDF2PasswordHash = "hash",
            SRPPasswordHash = "hash",
            SRPPasswordSalt = "salt",
            DiscordId = "123456789012345678",
            TotalLevel = 5,
            TotalExperience = 1250,
            InventoryItems =
            [
                new UserInventory { UserID = accountId, ItemCode = "ai.custom_icon:1", Quantity = 1, IsPermanent = true },
                new UserInventory { UserID = accountId, ItemCode = "cs.zephyr", Quantity = 1, IsPermanent = true }
            ],
            Role = new Role { ID = 2, Name = "User" }
        };

        Account account = new()
        {
            ID = accountId,
            Name = name,
            User = user,
            Type = AccountType.Normal,
            IsMain = true,
            TimestampCreated = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            TimestampLastActive = new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero)
        };

        return (account, user);
    }

    private static IOptions<OperationalConfiguration> CreateOperationalOptions()
    {
        return Options.Create(new OperationalConfiguration
        {
            CurrentSeason = "1",
            CDN = new OperationalConfigurationCDN
            {
                Host = "http://cdn.kongor.net",
                PrimaryPatchURL = "http://cdn.kongor.net/patch",
                SecondaryPatchURL = "http://cdn.kongor.net/patch2"
            }
        });
    }

    private static HttpContext CreateHttpContext(Dictionary<string, string> formFields)
    {
        DefaultHttpContext context = new();
        Dictionary<string, StringValues> formDict = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> field in formFields)
        {
            formDict[field.Key] = new StringValues(field.Value);
        }

        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Form = new FormCollection(formDict);
        return context;
    }

    [Fact]
    public async Task HandleRequestAsync_WithHybridCacheHit_ReturnsCachedString_WithoutHittingDatabase()
    {
        // Arrange
        const string expectedCachedPayload = "a:1:{s:8:\"nickname\";s:8:\"testuser\";}";

        Mock<IAccountRepository> mockAccountRepo = new();
        Mock<IMatchHistoryRepository> mockMatchRepo = new();
        Mock<IPlayerStatisticsService> mockStatsService = new();
        Mock<IHeroDefinitionService> mockHeroDefs = new();
        Mock<ISessionContextResolver> mockSessionResolver = new();
        Mock<INexusHybridCache> mockHybridCache = new();
        IOptions<OperationalConfiguration> options = CreateOperationalOptions();

        mockHybridCache
            .Setup(c => c.GetOrCreateAsync<string>(
                "profile:simple_stats:testuser:ranked",
                It.IsAny<Func<CancellationToken, Task<string?>>>(),
                It.Is<IReadOnlyCollection<string>?>(tags => tags != null && tags.Contains("account:nick:testuser")),
                It.Is<TimeSpan?>(t => t == TimeSpan.FromSeconds(30)),
                It.Is<TimeSpan?>(t => t == TimeSpan.FromMinutes(5)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedCachedPayload);

        SimpleStatsHandler handler = new(
            mockAccountRepo.Object,
            mockMatchRepo.Object,
            mockStatsService.Object,
            mockHeroDefs.Object,
            mockSessionResolver.Object,
            options,
            NullLogger<SimpleStatsHandler>.Instance,
            mockHybridCache.Object);

        HttpContext context = CreateHttpContext(new Dictionary<string, string>
        {
            ["nickname"] = "testuser",
            ["table"] = "ranked"
        });

        // Act
        IActionResult result = await handler.HandleRequestAsync(context);

        // Assert
        PhpResult phpResult = result.Should().BeOfType<PhpResult>().Subject;
        phpResult.Data.Should().Be(expectedCachedPayload);

        // Verify database and stats services were never called on cache hit
        mockAccountRepo.Verify(r => r.GetByNicknameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        mockMatchRepo.Verify(r => r.GetMasteryByAccountIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        mockStatsService.Verify(s => s.GetAggregatedStatisticsAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleRequestAsync_WithHybridCacheMiss_ExecutesFactory_ComputesAndCachesPayload()
    {
        // Arrange
        (Account account, User _) = CreateTestAccount("testuser", 100);

        Mock<IAccountRepository> mockAccountRepo = new();
        mockAccountRepo
            .Setup(r => r.GetByNicknameAsync("testuser", It.IsAny<CancellationToken>()))
            .ReturnsAsync(account);

        Mock<IMatchHistoryRepository> mockMatchRepo = new();
        mockMatchRepo
            .Setup(r => r.GetMasteryByAccountIdAsync(100, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Mastery?)null);
        mockMatchRepo
            .Setup(r => r.GetAllPlayerStatisticsForAccountAsync(100, "testuser", It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<PlayerStatistics>)[]);

        Mock<IPlayerStatisticsService> mockStatsService = new();
        mockStatsService
            .Setup(s => s.GetAggregatedStatisticsAsync(100, "ranked", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlayerStatisticsAggregatedDTO
            {
                TotalMatches = 10,
                RankedWins = 7,
                RankedLosses = 3
            });

        Mock<IHeroDefinitionService> mockHeroDefs = new();
        Mock<ISessionContextResolver> mockSessionResolver = new();
        Mock<INexusHybridCache> mockHybridCache = new();
        IOptions<OperationalConfiguration> options = CreateOperationalOptions();

        mockHybridCache
            .Setup(c => c.GetOrCreateAsync<string>(
                "profile:simple_stats:testuser:ranked",
                It.IsAny<Func<CancellationToken, Task<string?>>>(),
                It.Is<IReadOnlyCollection<string>?>(tags => tags != null && tags.Contains("account:nick:testuser")),
                It.Is<TimeSpan?>(t => t == TimeSpan.FromSeconds(30)),
                It.Is<TimeSpan?>(t => t == TimeSpan.FromMinutes(5)),
                It.IsAny<CancellationToken>()))
            .Returns<string, Func<CancellationToken, Task<string?>>, IReadOnlyCollection<string>?, TimeSpan?, TimeSpan?, CancellationToken>(
                async (key, factory, tags, l1, l2, ct) => await factory(ct));

        SimpleStatsHandler handler = new(
            mockAccountRepo.Object,
            mockMatchRepo.Object,
            mockStatsService.Object,
            mockHeroDefs.Object,
            mockSessionResolver.Object,
            options,
            NullLogger<SimpleStatsHandler>.Instance,
            mockHybridCache.Object);

        HttpContext context = CreateHttpContext(new Dictionary<string, string>
        {
            ["nickname"] = "testuser",
            ["table"] = "ranked"
        });

        // Act
        IActionResult result = await handler.HandleRequestAsync(context);

        // Assert
        PhpResult phpResult = result.Should().BeOfType<PhpResult>().Subject;
        string? payload = phpResult.Data as string;
        payload.Should().NotBeNullOrEmpty();
        payload.Should().StartWith("a:");

        mockAccountRepo.Verify(r => r.GetByNicknameAsync("testuser", It.IsAny<CancellationToken>()), Times.Once);
        mockStatsService.Verify(s => s.GetAggregatedStatisticsAsync(100, "ranked", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleRequestAsync_WhenHybridCacheIsNull_FallsBackToDirectDatabaseExecution()
    {
        // Arrange
        (Account account, User _) = CreateTestAccount("fallbackuser", 101);

        Mock<IAccountRepository> mockAccountRepo = new();
        mockAccountRepo
            .Setup(r => r.GetByNicknameAsync("fallbackuser", It.IsAny<CancellationToken>()))
            .ReturnsAsync(account);

        Mock<IMatchHistoryRepository> mockMatchRepo = new();
        mockMatchRepo
            .Setup(r => r.GetMasteryByAccountIdAsync(101, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Mastery?)null);
        mockMatchRepo
            .Setup(r => r.GetAllPlayerStatisticsForAccountAsync(101, "fallbackuser", It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<PlayerStatistics>)[]);

        Mock<IPlayerStatisticsService> mockStatsService = new();
        mockStatsService
            .Setup(s => s.GetAggregatedStatisticsAsync(101, "ranked", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlayerStatisticsAggregatedDTO { TotalMatches = 5 });

        Mock<IHeroDefinitionService> mockHeroDefs = new();
        Mock<ISessionContextResolver> mockSessionResolver = new();
        IOptions<OperationalConfiguration> options = CreateOperationalOptions();

        SimpleStatsHandler handler = new(
            mockAccountRepo.Object,
            mockMatchRepo.Object,
            mockStatsService.Object,
            mockHeroDefs.Object,
            mockSessionResolver.Object,
            options,
            NullLogger<SimpleStatsHandler>.Instance,
            hybridCache: null);

        HttpContext context = CreateHttpContext(new Dictionary<string, string>
        {
            ["nickname"] = "fallbackuser",
            ["table"] = "ranked"
        });

        // Act
        IActionResult result = await handler.HandleRequestAsync(context);

        // Assert
        PhpResult phpResult = result.Should().BeOfType<PhpResult>().Subject;
        string? payload = phpResult.Data as string;
        payload.Should().NotBeNullOrEmpty();
        payload.Should().StartWith("a:");

        mockAccountRepo.Verify(r => r.GetByNicknameAsync("fallbackuser", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleRequestAsync_WithMissingAccount_ReturnsNotFoundResult()
    {
        // Arrange
        Mock<IAccountRepository> mockAccountRepo = new();
        mockAccountRepo
            .Setup(r => r.GetByNicknameAsync("unknownuser", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Account?)null);

        Mock<IMatchHistoryRepository> mockMatchRepo = new();
        Mock<IPlayerStatisticsService> mockStatsService = new();
        Mock<IHeroDefinitionService> mockHeroDefs = new();
        Mock<ISessionContextResolver> mockSessionResolver = new();
        Mock<INexusHybridCache> mockHybridCache = new();
        IOptions<OperationalConfiguration> options = CreateOperationalOptions();

        mockHybridCache
            .Setup(c => c.GetOrCreateAsync<string>(
                "profile:simple_stats:unknownuser:ranked",
                It.IsAny<Func<CancellationToken, Task<string?>>>(),
                It.IsAny<IReadOnlyCollection<string>?>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, Func<CancellationToken, Task<string?>>, IReadOnlyCollection<string>?, TimeSpan?, TimeSpan?, CancellationToken>(
                async (key, factory, tags, l1, l2, ct) => await factory(ct));

        SimpleStatsHandler handler = new(
            mockAccountRepo.Object,
            mockMatchRepo.Object,
            mockStatsService.Object,
            mockHeroDefs.Object,
            mockSessionResolver.Object,
            options,
            NullLogger<SimpleStatsHandler>.Instance,
            mockHybridCache.Object);

        HttpContext context = CreateHttpContext(new Dictionary<string, string>
        {
            ["nickname"] = "unknownuser"
        });

        // Act
        IActionResult result = await handler.HandleRequestAsync(context);

        // Assert
        NotFoundObjectResult notFound = result.Should().BeOfType<NotFoundObjectResult>().Subject;
        notFound.Value.Should().Be(@"Account With Name ""unknownuser"" Was Not Found");
    }

    [Fact]
    public async Task SelectedUpgradesHandler_WhenEquippingItem_InvalidatesProfileCacheTags()
    {
        // Arrange
        (Account account, User _) = CreateTestAccount("upgrader", 200);

        Mock<IAccountRepository> mockAccountRepo = new();
        mockAccountRepo
            .Setup(r => r.GetByNicknameAsync("upgrader", It.IsAny<CancellationToken>()))
            .ReturnsAsync(account);

        Mock<ISessionRepository> mockSessionRepo = new();
        mockSessionRepo
            .Setup(s => s.GetAccountNameForSessionCookieAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("upgrader");

        Mock<INexusHybridCache> mockHybridCache = new();

        SelectedUpgradesHandler handler = new(
            mockAccountRepo.Object,
            mockSessionRepo.Object,
            NullLogger<SelectedUpgradesHandler>.Instance,
            mockHybridCache.Object);

        DefaultHttpContext context = new();
        Dictionary<string, StringValues> formDict = new(StringComparer.OrdinalIgnoreCase)
        {
            ["cookie"] = new StringValues("valid_session_cookie"),
            ["selected_upgrades[]"] = new StringValues("cs.zephyr")
        };
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Form = new FormCollection(formDict);

        // Act
        IActionResult result = await handler.HandleRequestAsync(context);

        // Assert
        result.Should().BeOfType<PhpResult>();
        mockHybridCache.Verify(c => c.RemoveByTagAsync("account:200", It.IsAny<CancellationToken>()), Times.Once);
        mockHybridCache.Verify(c => c.RemoveByTagAsync("account:nick:upgrader", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SelectedUpgradesHandler_WhenClearingSelections_InvalidatesProfileCacheTags()
    {
        // Arrange
        (Account account, User _) = CreateTestAccount("clearuser", 201);

        Mock<IAccountRepository> mockAccountRepo = new();
        mockAccountRepo
            .Setup(r => r.GetByNicknameAsync("clearuser", It.IsAny<CancellationToken>()))
            .ReturnsAsync(account);

        Mock<ISessionRepository> mockSessionRepo = new();
        mockSessionRepo
            .Setup(s => s.GetAccountNameForSessionCookieAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("clearuser");

        Mock<INexusHybridCache> mockHybridCache = new();

        SelectedUpgradesHandler handler = new(
            mockAccountRepo.Object,
            mockSessionRepo.Object,
            NullLogger<SelectedUpgradesHandler>.Instance,
            mockHybridCache.Object);

        DefaultHttpContext context = new();
        Dictionary<string, StringValues> formDict = new(StringComparer.OrdinalIgnoreCase)
        {
            ["cookie"] = new StringValues("valid_session_cookie")
            // No selected_upgrades -> triggers clearing all selections
        };
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Form = new FormCollection(formDict);

        // Act
        IActionResult result = await handler.HandleRequestAsync(context);

        // Assert
        result.Should().BeOfType<PhpResult>();
        mockHybridCache.Verify(c => c.RemoveByTagAsync("account:201", It.IsAny<CancellationToken>()), Times.Once);
        mockHybridCache.Verify(c => c.RemoveByTagAsync("account:nick:clearuser", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PlayerStatisticsService_InvalidateAggregatedStatisticsAsync_InvalidatesBothIdAndNickTags()
    {
        // Arrange
        Mock<INexusHybridCache> mockHybridCache = new();
        Mock<ILogger<PlayerStatisticsService>> mockLogger = new();

        using MerrickContext dbContext = TestDbContextFactory.CreateInMemoryContext();

        PlayerStatisticsService statsService = new(dbContext, mockHybridCache.Object, mockLogger.Object);

        // Act
        await statsService.InvalidateAggregatedStatisticsAsync(100, "testuser", CancellationToken.None);

        // Assert
        mockHybridCache.Verify(c => c.RemoveByTagAsync("account:100", It.IsAny<CancellationToken>()), Times.Once);
        mockHybridCache.Verify(c => c.RemoveByTagAsync("account:nick:testuser", It.IsAny<CancellationToken>()), Times.Once);
        mockHybridCache.Verify(c => c.RemoveAsync("stats:aggregated:100", It.IsAny<CancellationToken>()), Times.Once);
        mockHybridCache.Verify(c => c.RemoveAsync("stats:mastery:100", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleRequestAsync_MasteryTableWithPopulatedHeroExperiences_SkipsAggregatedPlayerStatisticsScan()
    {
        // protects: Hero-pick entry latency when lobby_v2.lua submits PlayerStatsMastery (table=mastery), avoiding an
        //           unused full-table PlayerStatistics GroupBy scan when Mastery.HeroExperiences is already populated.
        // fails_when: SimpleStatsHandler calls IPlayerStatisticsService.GetAggregatedStatisticsAsync for table=mastery
        //             despite populated Mastery.HeroExperiences.
        // seam: Real SimpleStatsHandler + PHP response serialization on table=mastery.
        (Account account, User _) = CreateTestAccount("valkplayer", 305);
        Mastery mastery = new()
        {
            AccountID = 305,
            Account = account,
            HeroExperiences =
            [
                new HeroMasteryExperience { HeroIdentifier = "Hero_Valkyrie", Experience = 4200 }
            ]
        };
        account.Mastery = mastery;

        Mock<IAccountRepository> mockAccountRepo = new();
        mockAccountRepo
            .Setup(r => r.GetByNicknameAsync("valkplayer", It.IsAny<CancellationToken>()))
            .ReturnsAsync(account);

        Mock<IMatchHistoryRepository> mockMatchRepo = new();
        mockMatchRepo
            .Setup(r => r.GetMasteryByAccountIdAsync(305, It.IsAny<CancellationToken>()))
            .ReturnsAsync(mastery);

        Mock<IPlayerStatisticsService> mockStatsService = new();
        Mock<IHeroDefinitionService> mockHeroDefs = new();
        mockHeroDefs.Setup(h => h.GetBaseHeroId("Hero_Valkyrie")).Returns(38u);
        mockHeroDefs.Setup(h => h.GetHeroIdentifier(38u)).Returns("Hero_Valkyrie");
        mockHeroDefs.Setup(h => h.GetAllHeroIds()).Returns([38u]);

        Mock<ISessionContextResolver> mockSessionResolver = new();
        IOptions<OperationalConfiguration> options = CreateOperationalOptions();

        SimpleStatsHandler handler = new(
            mockAccountRepo.Object,
            mockMatchRepo.Object,
            mockStatsService.Object,
            mockHeroDefs.Object,
            mockSessionResolver.Object,
            options,
            NullLogger<SimpleStatsHandler>.Instance,
            hybridCache: null);

        HttpContext context = CreateHttpContext(new Dictionary<string, string>
        {
            ["nickname"] = "valkplayer",
            ["table"] = "mastery"
        });

        IActionResult result = await handler.HandleRequestAsync(context);

        PhpResult phpResult = result.Should().BeOfType<PhpResult>().Subject;
        string? payload = phpResult.Data as string;
        payload.Should().NotBeNullOrEmpty();
        payload.Should().Contain("Hero_Valkyrie");

        mockStatsService.Verify(
            s => s.GetAggregatedStatisticsAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
