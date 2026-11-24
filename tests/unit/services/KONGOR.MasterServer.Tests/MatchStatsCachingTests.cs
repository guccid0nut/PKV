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
using KONGOR.MasterServer.Features.Matches.SubmitMatchStats;
using KONGOR.MasterServer.Handlers.ClientRequester;
using KONGOR.MasterServer.Services;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Statistics;
using MERRICK.DatabaseContext.Entities.Utility;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Moq;
using NEXUS.Contracts.Data;
using NEXUS.Contracts.Domain;
using StackExchange.Redis;
using Xunit;
using Role = MERRICK.DatabaseContext.Entities.Utility.Role;

namespace KONGOR.MasterServer.Tests;

public sealed class MatchStatsCachingTests
{
    private static (Account account, User user) CreateTestAccount(string name = "testuser", int accountId = 100)
    {
        Role role = new() { ID = 2, Name = "User" };
        User user = new()
        {
            ID = accountId,
            EmailAddress = $"{name}@kongor.net",
            PBKDF2PasswordHash = "hash",
            SRPPasswordHash = "hash",
            SRPPasswordSalt = "salt",
            Role = role,
            GoldCoins = 50,
            SilverCoins = 250,
            InventoryItems = []
        };

        Account account = new()
        {
            ID = accountId,
            Name = name,
            User = user,
            Type = AccountType.Normal,
            IsMain = true,
            TimestampCreated = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)
        };

        return (account, user);
    }

    private static HttpContext CreateHttpContext(Dictionary<string, string> queryOrFormFields, string? sessionAccountName = null)
    {
        DefaultHttpContext context = new();
        Dictionary<string, StringValues> dict = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> field in queryOrFormFields)
        {
            dict[field.Key] = new StringValues(field.Value);
        }

        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Form = new FormCollection(dict);
        if (!string.IsNullOrEmpty(sessionAccountName))
        {
            context.Items["SessionAccountName"] = sessionAccountName;
        }

        return context;
    }

    [Fact]
    public async Task HandleRequestAsync_WithHybridCacheHit_ReturnsCachedString_WithoutCallingRepository()
    {
        // Arrange
        const string expectedPayload = "a:1:{s:8:\"match_id\";i:4001;}";
        (Account account, User _) = CreateTestAccount("testuser", 100);

        Mock<IAccountRepository> mockAccountRepo = new();
        mockAccountRepo
            .Setup(r => r.GetByNicknameAsync("testuser", It.IsAny<CancellationToken>()))
            .ReturnsAsync(account);

        Mock<IMatchHistoryRepository> mockMatchRepo = new();
        Mock<IDatabase> mockDistributedCache = new();
        Mock<IHeroDefinitionService> mockHeroDefs = new();
        Mock<INexusHybridCache> mockHybridCache = new();

        mockHybridCache
            .Setup(c => c.GetOrCreateAsync<string>(
                "match:stats:4001:100",
                It.IsAny<Func<CancellationToken, Task<string?>>>(),
                It.Is<IReadOnlyCollection<string>?>(tags => tags != null && tags.Contains("match:4001") && tags.Contains("account:100")),
                It.Is<TimeSpan?>(t => t == TimeSpan.FromMinutes(5)),
                It.Is<TimeSpan?>(t => t == TimeSpan.FromHours(24)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedPayload);

        MatchStatsHandler handler = new(
            mockAccountRepo.Object,
            mockMatchRepo.Object,
            mockDistributedCache.Object,
            mockHeroDefs.Object,
            NullLogger<MatchStatsHandler>.Instance,
            mockHybridCache.Object);

        HttpContext context = CreateHttpContext(new Dictionary<string, string>
        {
            ["match_id"] = "4001"
        }, sessionAccountName: "testuser");

        // Act
        IActionResult result = await handler.HandleRequestAsync(context);

        // Assert
        PhpResult phpResult = result.Should().BeOfType<PhpResult>().Subject;
        phpResult.Data.Should().Be(expectedPayload);

        mockMatchRepo.Verify(r => r.GetMatchByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        mockMatchRepo.Verify(r => r.GetPlayerStatisticsForMatchAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleRequestAsync_WithHybridCacheMiss_ExecutesFactoryAndCachesPayload()
    {
        // Arrange
        (Account account, User _) = CreateTestAccount("testuser", 100);

        MatchStatistics matchStats = new()
        {
            MatchID = 4001,
            ServerID = 1,
            Map = "caldavar",
            GameMode = "caldavar",
            TimePlayed = 1800,
            HostAccountName = "testuser",
            Version = "4.10.1"
        };

        PlayerStatistics playerStats = new()
        {
            MatchID = 4001,
            AccountID = 100,
            AccountName = "testuser",
            HeroIdentifier = "Hero_Pyromancer",
            HeroProductID = 10,
            Win = 1,
            MVP = 1
        };

        Mock<IAccountRepository> mockAccountRepo = new();
        mockAccountRepo
            .Setup(r => r.GetByNicknameAsync("testuser", It.IsAny<CancellationToken>()))
            .ReturnsAsync(account);
        mockAccountRepo
            .Setup(r => r.GetAccountsByIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([account]);

        Mock<IMatchHistoryRepository> mockMatchRepo = new();
        mockMatchRepo
            .Setup(r => r.GetMatchByIdAsync(4001, It.IsAny<CancellationToken>()))
            .ReturnsAsync(matchStats);
        mockMatchRepo
            .Setup(r => r.GetPlayerStatisticsForMatchAsync(4001, It.IsAny<CancellationToken>()))
            .ReturnsAsync([playerStats]);
        mockMatchRepo
            .Setup(r => r.GetAccountStatisticsByIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        Mock<IDatabase> mockDistributedCache = new();
        Mock<IHeroDefinitionService> mockHeroDefs = new();
        mockHeroDefs
            .Setup(d => d.GetBaseHeroId(It.IsAny<string>()))
            .Returns(10u);
        mockHeroDefs
            .Setup(d => d.GetBaseHeroId(It.IsAny<uint>()))
            .Returns(10u);
        mockHeroDefs
            .Setup(d => d.GetHeroIdentifier(It.IsAny<uint>()))
            .Returns("Hero_Pyromancer");

        Mock<INexusHybridCache> mockHybridCache = new();
        mockHybridCache
            .Setup(c => c.GetOrCreateAsync<string>(
                "match:stats:4001:100",
                It.IsAny<Func<CancellationToken, Task<string?>>>(),
                It.IsAny<IReadOnlyCollection<string>?>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (string key, Func<CancellationToken, Task<string?>> factory, IReadOnlyCollection<string>? tags, TimeSpan? l1, TimeSpan? l2, CancellationToken ct) =>
            {
                return await factory(ct);
            });

        MatchStatsHandler handler = new(
            mockAccountRepo.Object,
            mockMatchRepo.Object,
            mockDistributedCache.Object,
            mockHeroDefs.Object,
            NullLogger<MatchStatsHandler>.Instance,
            mockHybridCache.Object);

        HttpContext context = CreateHttpContext(new Dictionary<string, string>
        {
            ["match_id"] = "4001"
        }, sessionAccountName: "testuser");

        // Act
        IActionResult result = await handler.HandleRequestAsync(context);

        // Assert
        PhpResult phpResult = result.Should().BeOfType<PhpResult>().Subject;
        string output = phpResult.Data.Should().BeOfType<string>().Subject;
        output.Should().Contain("4001");
        output.Should().Contain("match_summ");

        mockMatchRepo.Verify(r => r.GetMatchByIdAsync(4001, It.IsAny<CancellationToken>()), Times.Once);
        mockMatchRepo.Verify(r => r.GetPlayerStatisticsForMatchAsync(4001, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleRequestAsync_WithMemoryCacheHitOnCore_ReusesCoreWithoutCallingDatabase()
    {
        // Arrange
        (Account account1, User _) = CreateTestAccount("player1", 100);
        (Account account2, User _) = CreateTestAccount("player2", 200);

        MatchStatistics matchStats = new()
        {
            MatchID = 5001,
            ServerID = 1,
            Map = "caldavar",
            GameMode = "caldavar",
            TimePlayed = 1800,
            HostAccountName = "player1",
            Version = "4.10.1"
        };

        PlayerStatistics p1Stats = new()
        {
            MatchID = 5001,
            AccountID = 100,
            AccountName = "player1",
            HeroIdentifier = "Hero_Pyromancer",
            HeroProductID = 10,
            Win = 1
        };

        PlayerStatistics p2Stats = new()
        {
            MatchID = 5001,
            AccountID = 200,
            AccountName = "player2",
            HeroIdentifier = "Hero_Valkyrie",
            HeroProductID = 20,
            Win = 1
        };

        Mock<IAccountRepository> mockAccountRepo = new();
        mockAccountRepo
            .Setup(r => r.GetByNicknameAsync("player1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(account1);
        mockAccountRepo
            .Setup(r => r.GetByNicknameAsync("player2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(account2);
        mockAccountRepo
            .Setup(r => r.GetAccountsByIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([account1, account2]);

        Mock<IMatchHistoryRepository> mockMatchRepo = new();
        mockMatchRepo
            .Setup(r => r.GetMatchByIdAsync(5001, It.IsAny<CancellationToken>()))
            .ReturnsAsync(matchStats);
        mockMatchRepo
            .Setup(r => r.GetPlayerStatisticsForMatchAsync(5001, It.IsAny<CancellationToken>()))
            .ReturnsAsync([p1Stats, p2Stats]);
        mockMatchRepo
            .Setup(r => r.GetAccountStatisticsByIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        Mock<IDatabase> mockDistributedCache = new();
        Mock<IHeroDefinitionService> mockHeroDefs = new();
        mockHeroDefs
            .Setup(d => d.GetBaseHeroId(It.IsAny<string>()))
            .Returns(10u);
        mockHeroDefs
            .Setup(d => d.GetBaseHeroId(It.IsAny<uint>()))
            .Returns(10u);
        mockHeroDefs
            .Setup(d => d.GetHeroIdentifier(It.IsAny<uint>()))
            .Returns("Hero_Pyromancer");

        IMemoryCache memoryCache = new MemoryCache(new MemoryCacheOptions());

        MatchStatsHandler handler = new(
            mockAccountRepo.Object,
            mockMatchRepo.Object,
            mockDistributedCache.Object,
            mockHeroDefs.Object,
            NullLogger<MatchStatsHandler>.Instance,
            hybridCache: null,
            memoryCache: memoryCache);

        HttpContext context1 = CreateHttpContext(new Dictionary<string, string>
        {
            ["match_id"] = "5001"
        }, sessionAccountName: "player1");

        HttpContext context2 = CreateHttpContext(new Dictionary<string, string>
        {
            ["match_id"] = "5001"
        }, sessionAccountName: "player2");

        // Act - Request 1 for player 1
        IActionResult result1 = await handler.HandleRequestAsync(context1);

        // Assert 1 - First call queried the DB
        mockMatchRepo.Verify(r => r.GetMatchByIdAsync(5001, It.IsAny<CancellationToken>()), Times.Once);

        // Act - Request 2 for player 2
        IActionResult result2 = await handler.HandleRequestAsync(context2);

        // Assert 2 - Second call reused MatchCoreData from memory cache; GetMatchByIdAsync was NOT called again!
        mockMatchRepo.Verify(r => r.GetMatchByIdAsync(5001, It.IsAny<CancellationToken>()), Times.Once);
        result1.Should().NotBeNull();
        result2.Should().NotBeNull();
    }

    [Fact]
    public async Task HandleRequestAsync_WhenMatchNotFound_ReturnsSafeEmptyResponse()
    {
        // Arrange
        Mock<IAccountRepository> mockAccountRepo = new();
        Mock<IMatchHistoryRepository> mockMatchRepo = new();
        mockMatchRepo
            .Setup(r => r.GetMatchByIdAsync(9999, It.IsAny<CancellationToken>()))
            .ReturnsAsync((MatchStatistics?)null);

        Mock<IDatabase> mockDistributedCache = new();
        Mock<IHeroDefinitionService> mockHeroDefs = new();

        MatchStatsHandler handler = new(
            mockAccountRepo.Object,
            mockMatchRepo.Object,
            mockDistributedCache.Object,
            mockHeroDefs.Object,
            NullLogger<MatchStatsHandler>.Instance);

        HttpContext context = CreateHttpContext(new Dictionary<string, string>
        {
            ["match_id"] = "9999"
        });

        // Act
        IActionResult result = await handler.HandleRequestAsync(context);

        // Assert
        PhpResult phpResult = result.Should().BeOfType<PhpResult>().Subject;
        phpResult.ContentType.Should().Be("text/plain; charset=utf-8");
    }

    [Fact]
    public async Task HandleRequestAsync_MissingMatchId_ReturnsBadRequest()
    {
        // Arrange
        Mock<IAccountRepository> mockAccountRepo = new();
        Mock<IMatchHistoryRepository> mockMatchRepo = new();
        Mock<IDatabase> mockDistributedCache = new();
        Mock<IHeroDefinitionService> mockHeroDefs = new();

        MatchStatsHandler handler = new(
            mockAccountRepo.Object,
            mockMatchRepo.Object,
            mockDistributedCache.Object,
            mockHeroDefs.Object,
            NullLogger<MatchStatsHandler>.Instance);

        HttpContext context = CreateHttpContext(new Dictionary<string, string>());

        // Act
        IActionResult result = await handler.HandleRequestAsync(context);

        // Assert
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task HandleRequestAsync_InvalidMatchId_ReturnsBadRequest()
    {
        // Arrange
        Mock<IAccountRepository> mockAccountRepo = new();
        Mock<IMatchHistoryRepository> mockMatchRepo = new();
        Mock<IDatabase> mockDistributedCache = new();
        Mock<IHeroDefinitionService> mockHeroDefs = new();

        MatchStatsHandler handler = new(
            mockAccountRepo.Object,
            mockMatchRepo.Object,
            mockDistributedCache.Object,
            mockHeroDefs.Object,
            NullLogger<MatchStatsHandler>.Instance);

        HttpContext context = CreateHttpContext(new Dictionary<string, string>
        {
            ["match_id"] = "invalid_id"
        });

        // Act
        IActionResult result = await handler.HandleRequestAsync(context);

        // Assert
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task HandleRequestAsync_WhenMatchInFlight_WaitsForCompletionAndEvictsNullCacheEntries()
    {
        // protects: MatchSubmissionIdempotency.WaitForCompletionAsync + MatchStatsHandler non-negative caching contract so
        //           an immediate post-match GetMatchInfo(id) waits for background ingestion instead of returning and
        //           caching main_stats_does_not_exist (null) for 5 minutes.
        // fails_when: GetMatchInfo returns null mid-ingestion or FusionCache/HybridCache stores null after a miss.
        // seam: Real MatchSubmissionIdempotency.TryAcquireAsync/MarkCompletedAsync + MatchStatsHandler.HandleRequestAsync.
        const int matchId = 8814;
        (Account account, User _) = CreateTestAccount("testuser", 100);
        MatchStatistics committedMatch = new()
        {
            MatchID = matchId,
            ServerID = 1,
            Map = "enfos",
            GameMode = "normal",
            TimePlayed = 600,
            HostAccountName = "testuser",
            Version = "5.0.0"
        };

        Mock<IDatabase> mockDistributedCache = new();
        mockDistributedCache
            .Setup(d => d.StringSetAsync(
                MatchSubmissionIdempotency.Key(matchId),
                MatchSubmissionIdempotency.StateQueued,
                It.IsAny<TimeSpan?>(),
                false,
                When.NotExists,
                CommandFlags.None))
            .ReturnsAsync(true);
        mockDistributedCache
            .Setup(d => d.StringSetAsync(
                MatchSubmissionIdempotency.Key(matchId),
                MatchSubmissionIdempotency.StateCompleted,
                It.IsAny<TimeSpan?>(),
                false,
                When.Always,
                CommandFlags.None))
            .ReturnsAsync(true);

        bool ingested = false;
        Mock<IMatchHistoryRepository> mockMatchRepo = new();
        mockMatchRepo
            .Setup(r => r.GetMatchByIdAsync(matchId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ingested ? committedMatch : null);
        mockMatchRepo
            .Setup(r => r.GetPlayerStatisticsForMatchAsync(matchId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        mockMatchRepo
            .Setup(r => r.GetAccountStatisticsByIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        Mock<IAccountRepository> mockAccountRepo = new();
        mockAccountRepo
            .Setup(r => r.GetByNicknameAsync("testuser", It.IsAny<CancellationToken>()))
            .ReturnsAsync(account);
        mockAccountRepo
            .Setup(r => r.GetAccountsByIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([account]);

        Mock<IHeroDefinitionService> mockHeroDefs = new();
        Mock<INexusHybridCache> mockHybridCache = new();
        mockHybridCache
            .Setup(c => c.GetOrCreateAsync<string>(
                $"match:stats:{matchId}:100",
                It.IsAny<Func<CancellationToken, Task<string?>>>(),
                It.IsAny<IReadOnlyCollection<string>?>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<CancellationToken>()))
            .Returns((string _, Func<CancellationToken, Task<string?>> factory, IReadOnlyCollection<string>? _, TimeSpan? _, TimeSpan? _, CancellationToken ct) => factory(ct));

        bool acquired = await MatchSubmissionIdempotency.TryAcquireAsync(mockDistributedCache.Object, matchId);
        acquired.Should().BeTrue();

        MatchStatsHandler handler = new(
            mockAccountRepo.Object,
            mockMatchRepo.Object,
            mockDistributedCache.Object,
            mockHeroDefs.Object,
            NullLogger<MatchStatsHandler>.Instance,
            mockHybridCache.Object);

        HttpContext context = CreateHttpContext(new Dictionary<string, string>
        {
            ["match_id"] = matchId.ToString()
        }, sessionAccountName: "testuser");

        // Act: start GetMatchInfo while ingestion is still QUEUED, then commit + MarkCompletedAsync 30ms later.
        Task<IActionResult> requestTask = handler.HandleRequestAsync(context);
        await Task.Delay(30);
        ingested = true;
        await MatchSubmissionIdempotency.MarkCompletedAsync(mockDistributedCache.Object, matchId);

        IActionResult result = await requestTask;

        // Assert: GetMatchInfo waited for ingestion completion and returned serialized match data without erroring.
        PhpResult phpResult = result.Should().BeOfType<PhpResult>().Subject;
        string payload = phpResult.Data.Should().BeOfType<string>().Subject;
        payload.Should().Contain(matchId.ToString());
        payload.Should().Contain("match_summ");
        MatchSubmissionIdempotency.HasInFlightWaiter(matchId).Should().BeFalse();

        // Assert negative-cache eviction: querying a truly nonexistent match calls HybridCache.RemoveAsync.
        Mock<INexusHybridCache> mockMissCache = new();
        mockMissCache
            .Setup(c => c.GetOrCreateAsync<string>(
                "match:stats:99998:100",
                It.IsAny<Func<CancellationToken, Task<string?>>>(),
                It.IsAny<IReadOnlyCollection<string>?>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<CancellationToken>()))
            .Returns((string _, Func<CancellationToken, Task<string?>> factory, IReadOnlyCollection<string>? _, TimeSpan? _, TimeSpan? _, CancellationToken ct) => factory(ct));

        MatchStatsHandler missHandler = new(
            mockAccountRepo.Object,
            mockMatchRepo.Object,
            mockDistributedCache.Object,
            mockHeroDefs.Object,
            NullLogger<MatchStatsHandler>.Instance,
            mockMissCache.Object);
        HttpContext missContext = CreateHttpContext(new Dictionary<string, string>
        {
            ["match_id"] = "99998"
        }, sessionAccountName: "testuser");

        await missHandler.HandleRequestAsync(missContext);
        mockMissCache.Verify(c => c.RemoveAsync("match:stats:99998:100", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task WaitForCompletionAsync_WhenTimedOut_CleansUpInFlightWaiterAndSupportsRedisPollingFallback()
    {
        // protects: MatchSubmissionIdempotency.WaitForCompletionAsync bounded timeout cleanup + cross-instance Redis polling.
        // fails_when: Stalled waiters leak in InFlightWaiters after timeout, or cross-replica reads skip Redis QUEUED checks.
        // seam: Real MatchSubmissionIdempotency static waiter dictionary + mocked Redis StringGetAsync transition.
        const int stalledMatchId = 8815;
        const int remoteMatchId = 8816;

        Mock<IDatabase> mockRedis = new();
        mockRedis
            .Setup(d => d.StringSetAsync(
                MatchSubmissionIdempotency.Key(stalledMatchId),
                MatchSubmissionIdempotency.StateQueued,
                It.IsAny<TimeSpan?>(),
                false,
                When.NotExists,
                CommandFlags.None))
            .ReturnsAsync(true);

        (await MatchSubmissionIdempotency.TryAcquireAsync(mockRedis.Object, stalledMatchId)).Should().BeTrue();
        bool waitedStalled = await MatchSubmissionIdempotency.WaitForCompletionAsync(
            mockRedis.Object,
            stalledMatchId,
            TimeSpan.FromMilliseconds(40));

        waitedStalled.Should().BeTrue();
        MatchSubmissionIdempotency.HasInFlightWaiter(stalledMatchId).Should().BeFalse("timed-out in-process waiters must be removed cleanly");

        int pollCount = 0;
        mockRedis
            .Setup(d => d.StringGetAsync(MatchSubmissionIdempotency.Key(remoteMatchId), CommandFlags.None))
            .ReturnsAsync(() => (++pollCount == 1)
                ? (RedisValue)MatchSubmissionIdempotency.StateQueued
                : (RedisValue)MatchSubmissionIdempotency.StateCompleted);

        bool waitedRemote = await MatchSubmissionIdempotency.WaitForCompletionAsync(
            mockRedis.Object,
            remoteMatchId,
            TimeSpan.FromMilliseconds(500));

        waitedRemote.Should().BeTrue();
        pollCount.Should().BeGreaterThanOrEqualTo(2);
    }
}
