// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using KONGOR.MasterServer.Constants;
using KONGOR.MasterServer.Controllers;
using KONGOR.MasterServer.Features.Telemetry;
using KONGOR.MasterServer.Hubs;
using KONGOR.MasterServer.Tests.Infrastructure;
using MediatR;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Statistics;
using MERRICK.DatabaseContext.Enumerations;
using MERRICK.DatabaseContext.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NEXUS.Contracts.CQRS.Telemetry;
using NEXUS.Contracts.Domain;
using StackExchange.Redis;
using Xunit;
using DbRole = MERRICK.DatabaseContext.Entities.Utility.Role;

namespace KONGOR.MasterServer.Tests;

public sealed class LiveTelemetryMediatRTests
{
    public LiveTelemetryMediatRTests()
    {
        GetLiveTelemetryHandler.ResetFallbackCacheForTests();
    }
    [Fact]
    public async Task GetLiveTelemetryHandler_ResolvesRedisMetricsAndWinrateLeaderboard()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();

        User user1 = new()
        {
            ID = 1,
            EmailAddress = "tele1@kongor.net",
            Role = new DbRole { ID = 1, Name = "User" },
            SRPPasswordSalt = "salt",
            SRPPasswordHash = "hash",
            PBKDF2PasswordHash = "p"
        };
        Account acc1 = new() { ID = 101, Name = "ProPlayer", Type = AccountType.Normal, User = user1 };
        user1.Accounts.Add(acc1);

        AccountStatistics stat1 = new()
        {
            ID = 1,
            AccountID = 101,
            Account = acc1,
            Type = AccountStatisticsType.Matchmaking,
            MatchesPlayed = 10,
            MatchesWon = 8
        };

        db.Users.Add(user1);
        db.Accounts.Add(acc1);
        db.AccountStatistics.Add(stat1);
        await db.SaveChangesAsync();

        Mock<IDatabase> redisMock = new();
        redisMock.Setup(r => r.HashGetAsync("TELEMETRY:METRICS", "CCU", CommandFlags.None))
            .ReturnsAsync(new RedisValue("1337"));
        redisMock.Setup(r => r.HashGetAsync("TELEMETRY:METRICS", "MatchesInProgress", CommandFlags.None))
            .ReturnsAsync(new RedisValue("42"));

        GetLiveTelemetryHandler handler = new(db, redisMock.Object, NullLogger<GetLiveTelemetryHandler>.Instance);

        var result = await handler.Handle(new GetLiveTelemetryQuery(), default);

        result.OnlinePlayers.Should().Be(1337);
        result.MatchesInProgress.Should().Be(42);
        result.TopWinratePlayers.Should().HaveCount(1);
        result.TopWinratePlayers[0].AccountName.Should().Be("ProPlayer");
        result.TopWinratePlayers[0].WinRate.Should().Be(80.0);
        result.TopWinratePlayers[0].RankTier.Should().Be("Immortal");
    }

    [Fact]
    public async Task LiveTelemetryController_ReturnsOkResult()
    {
        Mock<IMediator> mediatorMock = new();
        mediatorMock.Setup(m => m.Send(It.IsAny<GetLiveTelemetryQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LiveTelemetryResponseDto
            {
                OnlinePlayers = 100,
                MatchesInProgress = 10
            });

        LiveTelemetryController controller = new(mediatorMock.Object, NullLogger<LiveTelemetryController>.Instance);

        var actionResult = await controller.GetLiveTelemetry(default);

        var okResult = actionResult.Should().BeOfType<OkObjectResult>().Subject;
        var dto = okResult.Value.Should().BeOfType<LiveTelemetryResponseDto>().Subject;
        dto.OnlinePlayers.Should().Be(100);
        dto.MatchesInProgress.Should().Be(10);
    }

    [Fact]
    public void LiveTelemetryController_AppliesRelaxedRateLimitingPolicy()
    {
        EnableRateLimitingAttribute? attr =
            Attribute.GetCustomAttribute(
                typeof(LiveTelemetryController),
                typeof(EnableRateLimitingAttribute))
                as EnableRateLimitingAttribute;

        attr.Should().NotBeNull();
        attr!.PolicyName.Should().Be(RateLimiterPolicies.Relaxed);
    }

    [Fact]
    public async Task GetLiveTelemetryHandler_SingleProjectionRanksFiveMatchVeteransAboveUnqualifiedAndFallsBackCleanly()
    {
        GetLiveTelemetryHandler.ResetFallbackCacheForTests();
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();

        DbRole userRole = new() { ID = 1, Name = "User" };
        User u1 = new() { ID = 1, EmailAddress = "u1@kongor.net", Role = userRole, SRPPasswordSalt = "s", SRPPasswordHash = "h", PBKDF2PasswordHash = "p" };
        User u2 = new() { ID = 2, EmailAddress = "u2@kongor.net", Role = userRole, SRPPasswordSalt = "s", SRPPasswordHash = "h", PBKDF2PasswordHash = "p" };
        Account smurfOneGame = new() { ID = 201, Name = "OneGameSmurf", Type = AccountType.Normal, User = u1 };
        Account veteran = new() { ID = 202, Name = "SeasonedVeteran", Type = AccountType.Normal, User = u2 };

        db.Users.AddRange(u1, u2);
        db.Accounts.AddRange(smurfOneGame, veteran);
        db.AccountStatistics.AddRange(
            new AccountStatistics { ID = 1, AccountID = 201, Account = smurfOneGame, Type = AccountStatisticsType.Matchmaking, MatchesPlayed = 1, MatchesWon = 1 },
            new AccountStatistics { ID = 2, AccountID = 202, Account = veteran, Type = AccountStatisticsType.Matchmaking, MatchesPlayed = 10, MatchesWon = 7 });
        await db.SaveChangesAsync();

        Mock<IDatabase> redisMock = new();
        GetLiveTelemetryHandler handler = new(db, redisMock.Object, NullLogger<GetLiveTelemetryHandler>.Instance);

        LiveTelemetryResponseDto response = await handler.Handle(new GetLiveTelemetryQuery(), default);
        response.TopWinratePlayers.Should().ContainSingle();
        response.TopWinratePlayers[0].AccountName.Should().Be("SeasonedVeteran");
        response.TopWinratePlayers[0].WinRate.Should().Be(70.0);
    }

    [Fact]
    public async Task PresenceHub_GuardsUintMinusOneSentinel_AndAuthorizesHeaderOrAuthenticateSessionCookie()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        DbRole role = new() { ID = 1, Name = "User" };
        User user = new() { ID = 59, EmailAddress = "p59@kongor.net", Role = role, SRPPasswordSalt = "s", SRPPasswordHash = "h", PBKDF2PasswordHash = "p" };
        Account account = new() { ID = 59, Name = "guccidonut", Type = AccountType.Normal, User = user };
        const string validCookie = "0123456789abcdef0123456789abcdef";
        AccountSession session = new()
        {
            ID = 1,
            AccountID = 59,
            Account = account,
            SessionCookie = validCookie,
            IPAddress = "127.0.0.1",
            IsActive = true,
            TimestampLogin = DateTimeOffset.UtcNow,
            TimestampLastSeen = DateTimeOffset.UtcNow
        };
        db.Users.Add(user);
        db.Accounts.Add(account);
        db.AccountSessions.Add(session);
        await db.SaveChangesAsync();

        Mock<IDatabase> redisDb = new();
        Mock<IConnectionMultiplexer> mux = new();
        mux.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object?>())).Returns(redisDb.Object);

        Dictionary<object, object?> items = new();
        Mock<HubCallerContext> callerCtx = new();
        callerCtx.SetupGet(c => c.ConnectionId).Returns("conn-presence-auth");
        callerCtx.SetupGet(c => c.User).Returns(new System.Security.Claims.ClaimsPrincipal());
        callerCtx.SetupGet(c => c.Items).Returns(items);
        callerCtx.SetupGet(c => c.Features).Returns(new Microsoft.AspNetCore.Http.Features.FeatureCollection());

        Mock<IGroupManager> groups = new();
        Mock<IHubCallerClients> clients = new();
        clients.SetupGet(c => c.Caller).Returns(Mock.Of<ISingleClientProxy>());

        PresenceHub hub = new(db, mux.Object, NullLogger<PresenceHub>.Instance)
        {
            Context = callerCtx.Object,
            Groups = groups.Object,
            Clients = clients.Object
        };

        // 1. Out-of-range (uint)-1 = 4294967295 and 0 must be ignored cleanly
        await hub.SubscribeToPresence(4294967295L);
        await hub.SubscribeToPresence(0L);
        groups.Verify(g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        // 2. AuthenticateSessionCookie (with DB fallback when Redis cache misses) authorizes subsequent SubscribeToPresence(59)
        bool authed = await hub.AuthenticateSessionCookie($"[{validCookie}:deadbeef]");
        authed.Should().BeTrue();

        await hub.SubscribeToPresence(59L);
        groups.Verify(g => g.AddToGroupAsync("conn-presence-auth", "59", It.IsAny<CancellationToken>()), Times.Once);
    }
}

