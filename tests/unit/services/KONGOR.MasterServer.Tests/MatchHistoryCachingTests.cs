// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ASPIRE.Common.Caching;
using ASPIRE.Common.Serialization.PHP;
using FluentAssertions;
using KONGOR.MasterServer.Features.Matches.GetMatchHistory;
using KONGOR.MasterServer.Handlers.ClientRequester;
using KONGOR.MasterServer.Services;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Utility;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Moq;
using NEXUS.Contracts.Data;
using NEXUS.Contracts.Domain;
using StackExchange.Redis;
using Xunit;
using Role = MERRICK.DatabaseContext.Entities.Utility.Role;

namespace KONGOR.MasterServer.Tests;

public sealed class MatchHistoryCachingTests
{
    private static Account CreateTestAccount(string name = "testuser", int accountId = 100)
    {
        Role role = new() { ID = 2, Name = "User" };
        User user = new()
        {
            ID = accountId,
            EmailAddress = $"{name}@kongor.net",
            PBKDF2PasswordHash = "hash",
            SRPPasswordHash = "hash",
            SRPPasswordSalt = "salt",
            Role = role
        };

        return new Account
        {
            ID = accountId,
            Name = name,
            User = user,
            Type = AccountType.Normal,
            IsMain = true,
            TimestampCreated = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)
        };
    }

    private static HttpContext CreateHttpContext(Dictionary<string, string> queryOrFormFields)
    {
        DefaultHttpContext context = new();
        Dictionary<string, StringValues> dict = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> field in queryOrFormFields)
        {
            dict[field.Key] = new StringValues(field.Value);
        }

        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Form = new FormCollection(dict);
        return context;
    }

    [Fact]
    public async Task HandleGrabLastMatches_WithHybridCacheHit_ReturnsCachedString_WithoutCallingRepository()
    {
        // Arrange
        const string expectedPayload = "a:2:{s:7:\"matches\";s:9:\"1001,1002\";s:7:\"success\";s:4:\"True\";}";

        Mock<IMatchHistoryRepository> mockMatchRepo = new();
        Mock<IAccountRepository> mockAccountRepo = new();
        Mock<IDatabase> mockDistributedCache = new();
        Mock<IHeroDefinitionService> mockHeroDefs = new();
        Mock<IGetMatchHistoryHandler> mockGetHistoryHandler = new();
        Mock<INexusHybridCache> mockHybridCache = new();

        mockHybridCache
            .Setup(c => c.GetOrCreateAsync<string>(
                "match:history:recent:testuser",
                It.IsAny<Func<CancellationToken, Task<string?>>>(),
                It.Is<IReadOnlyCollection<string>?>(tags => tags != null && tags.Contains("account:nick:testuser")),
                It.Is<TimeSpan?>(t => t == TimeSpan.FromSeconds(30)),
                It.Is<TimeSpan?>(t => t == TimeSpan.FromMinutes(5)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedPayload);

        MatchHistoryHandler handler = new(
            mockMatchRepo.Object,
            mockAccountRepo.Object,
            mockDistributedCache.Object,
            mockHeroDefs.Object,
            NullLogger<MatchHistoryHandler>.Instance,
            mockGetHistoryHandler.Object,
            mockHybridCache.Object);

        HttpContext context = CreateHttpContext(new Dictionary<string, string>
        {
            ["f"] = "grab_last_matches",
            ["nickname"] = "testuser"
        });

        // Act
        IActionResult result = await handler.HandleRequestAsync(context);

        // Assert
        PhpResult phpResult = result.Should().BeOfType<PhpResult>().Subject;
        phpResult.Data.Should().Be(expectedPayload);

        mockGetHistoryHandler.Verify(h => h.HandleAsync(It.IsAny<GetMatchHistoryQuery>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleGrabLastMatches_WithHybridCacheMiss_ExecutesQueryAndCachesPayload()
    {
        // Arrange
        Mock<IMatchHistoryRepository> mockMatchRepo = new();
        Mock<IAccountRepository> mockAccountRepo = new();
        Mock<IDatabase> mockDistributedCache = new();
        Mock<IHeroDefinitionService> mockHeroDefs = new();
        Mock<IGetMatchHistoryHandler> mockGetHistoryHandler = new();
        Mock<INexusHybridCache> mockHybridCache = new();

        Dictionary<object, object> historyData = new()
        {
            { "matches", "4001,4002" },
            { "success", "True" }
        };

        mockGetHistoryHandler
            .Setup(h => h.HandleAsync(It.IsAny<GetMatchHistoryQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(GetMatchHistoryResponse.Succeeded(historyData));

        mockHybridCache
            .Setup(c => c.GetOrCreateAsync<string>(
                "match:history:recent:testuser",
                It.IsAny<Func<CancellationToken, Task<string?>>>(),
                It.IsAny<IReadOnlyCollection<string>?>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (string key, Func<CancellationToken, Task<string?>> factory, IReadOnlyCollection<string>? tags, TimeSpan? l1, TimeSpan? l2, CancellationToken ct) =>
            {
                return await factory(ct);
            });

        MatchHistoryHandler handler = new(
            mockMatchRepo.Object,
            mockAccountRepo.Object,
            mockDistributedCache.Object,
            mockHeroDefs.Object,
            NullLogger<MatchHistoryHandler>.Instance,
            mockGetHistoryHandler.Object,
            mockHybridCache.Object);

        HttpContext context = CreateHttpContext(new Dictionary<string, string>
        {
            ["f"] = "grab_last_matches",
            ["nickname"] = "testuser"
        });

        // Act
        IActionResult result = await handler.HandleRequestAsync(context);

        // Assert
        PhpResult phpResult = result.Should().BeOfType<PhpResult>().Subject;
        string output = phpResult.Data.Should().BeOfType<string>().Subject;
        output.Should().Contain("4001,4002");

        mockGetHistoryHandler.Verify(h => h.HandleAsync(It.IsAny<GetMatchHistoryQuery>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleMatchHistoryOverview_WithHybridCacheHit_ReturnsCachedOverviewString()
    {
        // Arrange
        const string expectedOverviewPayload = "a:1:{s:7:\"entries\";a:1:{i:0;a:1:{s:8:\"match_id\";s:4:\"5001\";}}}";
        Account account = CreateTestAccount("testuser", 100);

        Mock<IMatchHistoryRepository> mockMatchRepo = new();
        Mock<IAccountRepository> mockAccountRepo = new();
        mockAccountRepo
            .Setup(r => r.GetByNicknameAsync("testuser", It.IsAny<CancellationToken>()))
            .ReturnsAsync(account);

        Mock<IDatabase> mockDistributedCache = new();
        Mock<IHeroDefinitionService> mockHeroDefs = new();
        Mock<IGetMatchHistoryHandler> mockGetHistoryHandler = new();
        Mock<INexusHybridCache> mockHybridCache = new();

        mockHybridCache
            .Setup(c => c.GetOrCreateAsync<string>(
                "match:history:overview:100:ranked:current",
                It.IsAny<Func<CancellationToken, Task<string?>>>(),
                It.Is<IReadOnlyCollection<string>?>(tags => tags != null && tags.Contains("account:100") && tags.Contains("account:nick:testuser")),
                It.Is<TimeSpan?>(t => t == TimeSpan.FromSeconds(30)),
                It.Is<TimeSpan?>(t => t == TimeSpan.FromMinutes(5)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedOverviewPayload);

        MatchHistoryHandler handler = new(
            mockMatchRepo.Object,
            mockAccountRepo.Object,
            mockDistributedCache.Object,
            mockHeroDefs.Object,
            NullLogger<MatchHistoryHandler>.Instance,
            mockGetHistoryHandler.Object,
            mockHybridCache.Object);

        HttpContext context = CreateHttpContext(new Dictionary<string, string>
        {
            ["f"] = "match_history_overview",
            ["nickname"] = "testuser",
            ["table"] = "ranked"
        });

        // Act
        IActionResult result = await handler.HandleRequestAsync(context);

        // Assert
        PhpResult phpResult = result.Should().BeOfType<PhpResult>().Subject;
        phpResult.Data.Should().Be(expectedOverviewPayload);

        mockMatchRepo.Verify(r => r.GetPlayerMatchHistoryOverviewAsync(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleMatchHistoryOverview_WithHybridCacheMiss_QueriesRepositoryAndSerializesResponse()
    {
        // Arrange
        Account account = CreateTestAccount("testuser", 100);

        Mock<IMatchHistoryRepository> mockMatchRepo = new();
        List<PlayerMatchRecordDTO> records =
        [
            new PlayerMatchRecordDTO
            {
                MatchID = 6001,
                Team = 1,
                HeroKills = 10,
                HeroDeaths = 2,
                HeroAssists = 8,
                HeroIdentifier = "Hero_Pyromancer",
                HeroProductID = 10,
                Win = 1,
                Map = "caldavar",
                TimePlayed = 1500,
                TimestampRecorded = new DateTimeOffset(2025, 6, 1, 12, 0, 0, TimeSpan.Zero)
            }
        ];

        mockMatchRepo
            .Setup(r => r.GetPlayerMatchHistoryOverviewAsync(100, "testuser", It.IsAny<string?>(), It.IsAny<string?>(), 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(records);

        Mock<IAccountRepository> mockAccountRepo = new();
        mockAccountRepo
            .Setup(r => r.GetByNicknameAsync("testuser", It.IsAny<CancellationToken>()))
            .ReturnsAsync(account);

        Mock<IDatabase> mockDistributedCache = new();
        Mock<IHeroDefinitionService> mockHeroDefs = new();
        mockHeroDefs
            .Setup(d => d.CleanHeroIdentifier(It.IsAny<string?>()))
            .Returns<string?>(s => s ?? string.Empty);
        mockHeroDefs
            .Setup(d => d.GetBaseHeroId(It.IsAny<string>()))
            .Returns(10u);
        mockHeroDefs
            .Setup(d => d.GetBaseHeroId(It.IsAny<uint>()))
            .Returns(10u);
        mockHeroDefs
            .Setup(d => d.GetHeroIdentifier(It.IsAny<uint>()))
            .Returns("Hero_Pyromancer");

        Mock<IGetMatchHistoryHandler> mockGetHistoryHandler = new();
        Mock<INexusHybridCache> mockHybridCache = new();

        mockHybridCache
            .Setup(c => c.GetOrCreateAsync<string>(
                "match:history:overview:100:all:current",
                It.IsAny<Func<CancellationToken, Task<string?>>>(),
                It.IsAny<IReadOnlyCollection<string>?>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (string key, Func<CancellationToken, Task<string?>> factory, IReadOnlyCollection<string>? tags, TimeSpan? l1, TimeSpan? l2, CancellationToken ct) =>
            {
                return await factory(ct);
            });

        MatchHistoryHandler handler = new(
            mockMatchRepo.Object,
            mockAccountRepo.Object,
            mockDistributedCache.Object,
            mockHeroDefs.Object,
            NullLogger<MatchHistoryHandler>.Instance,
            mockGetHistoryHandler.Object,
            mockHybridCache.Object);

        HttpContext context = CreateHttpContext(new Dictionary<string, string>
        {
            ["f"] = "match_history_overview",
            ["nickname"] = "testuser"
        });

        // Act
        IActionResult result = await handler.HandleRequestAsync(context);

        // Assert
        PhpResult phpResult = result.Should().BeOfType<PhpResult>().Subject;
        string output = phpResult.Data.Should().BeOfType<string>().Subject;
        output.Should().Contain("6001");
        output.Should().Contain("Hero_Pyromancer");

        mockMatchRepo.Verify(r => r.GetPlayerMatchHistoryOverviewAsync(100, "testuser", null, null, 100, It.IsAny<CancellationToken>()), Times.Once);
    }
}
