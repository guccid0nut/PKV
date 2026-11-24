// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ASPIRE.Common.Extensions;
using ASPIRE.Common.Models.ServerManagement;
using FluentAssertions;
using KONGOR.MasterServer.Controllers;
using KONGOR.MasterServer.Extensions.Cache;
using KONGOR.MasterServer.Hubs;
using KONGOR.MasterServer.Services;
using KONGOR.MasterServer.Services.Background;
using KONGOR.MasterServer.Services.Matches;
using MemoryPack;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NEXUS.Contracts.Data;
using NEXUS.Contracts.Domain;
using NEXUS.Contracts.Domain.Matches;
using StackExchange.Redis;
using Xunit;

namespace KONGOR.MasterServer.Tests.Security;

public sealed class Wave0BackendSecurityTests
{
    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }

    private sealed class TestHttpConnectionFeature(HttpContext context) : IHttpContextFeature
    {
        public HttpContext? HttpContext { get; set; } = context;
    }

    // protects: MatchesV5Controller query-string secret rejection, HostAccountID>0 enforcement, missing/banned ServerHost denial, and Shadow vs Enforce missing-MatchStartData handling.
    // fails_when: '?session=' query token authenticates a server, HostAccountID<=0 bypasses role validation, or OwnershipCheckMode=Enforce allows unissued MatchIds.
    // seam: Real MatchesV5Controller.SubmitBinaryMatch + MemoryPack wire payload + Redis session hash.
    [Fact]
    public async Task MatchesV5Controller_RejectsQueryStringSession_InvalidHostAccount_AndEnforcesOwnershipMode()
    {
        Dictionary<string, RedisValue> sessionHashes = new(StringComparer.Ordinal);
        Mock<IDatabase> redisMock = new();
        redisMock.Setup(r => r.HashSetAsync(It.IsAny<RedisKey>(), It.IsAny<HashEntry[]>(), It.IsAny<CommandFlags>()))
            .Callback<RedisKey, HashEntry[], CommandFlags>((key, entries, _) =>
            {
                if (key == "MATCH-SERVER-SESSIONS")
                {
                    foreach (HashEntry entry in entries)
                    {
                        sessionHashes[entry.Name!] = entry.Value;
                    }
                }
            })
            .Returns(Task.CompletedTask);
        redisMock.Setup(r => r.HashGetAsync("MATCH-SERVER-SESSIONS", It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisKey _, RedisValue field, CommandFlags _) =>
                sessionHashes.TryGetValue(field.ToString(), out RedisValue val) ? val : RedisValue.Null);
        redisMock.Setup(r => r.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<bool>(),
                It.IsAny<When>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);

        Mock<IMatchProcessingChannel> channelMock = new();
        channelMock.Setup(c => c.EnqueueAsync(It.IsAny<MatchSubmissionPayload>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        MatchFinalizationPayload samplePayload = new()
        {
            MatchId = 880011,
            MapName = "caldavar",
            DurationSeconds = 1200,
            WinningTeam = 1,
            ServerId = 77,
            ServerAddress = "198.51.100.42",
            HostAccountName = "Host77",
            MatchType = AccountStatisticsType.Matchmaking,
            Players =
            [
                new PlayerMatchSummaryDto
                {
                    AccountId = 10,
                    AccountName = "RealPlayer",
                    Team = 1,
                    HeroKey = "Hero_Pyromancer",
                    Level = 16
                }
            ]
        };
        byte[] payloadBytes = MemoryPackSerializer.Serialize(samplePayload);

        MatchServer validServer = new()
        {
            ID = 77,
            Instance = 1,
            HostAccountID = 900,
            HostAccountName = "Host77",
            IPAddress = "198.51.100.42",
            Port = 11235,
            Cookie = "valid_header_only_cookie",
            Name = "EU #77",
            Description = "Ranked",
            Location = "EU"
        };
        await redisMock.Object.SetMatchServer(validServer.HostAccountName, validServer);

        // 1. Query-string ?session= MUST be rejected with 401
        ListLogger<MatchesV5Controller> queryLogger = new();
        DefaultHttpContext queryCtx = new();
        queryCtx.Request.QueryString = new QueryString("?session=valid_header_only_cookie");
        queryCtx.Request.Body = new MemoryStream(payloadBytes);

        MatchesV5Controller queryController = new(channelMock.Object, redisMock.Object, queryLogger)
        {
            ControllerContext = new ControllerContext { HttpContext = queryCtx }
        };
        ObjectResult queryRes = (await queryController.SubmitBinaryMatch(CancellationToken.None))
            .Should().BeAssignableTo<ObjectResult>().Subject;
        queryRes.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        queryLogger.Messages.Should().Contain(m => m.Contains("[SECURITY-ALERT]") && m.Contains("?session="));

        // 2. HostAccountID <= 0 MUST be rejected with 403
        MatchServer zeroHostServer = new()
        {
            ID = 78,
            Instance = 1,
            HostAccountID = 0,
            HostAccountName = "ZeroHost",
            IPAddress = "198.51.100.43",
            Port = 11236,
            Cookie = "zero_host_cookie",
            Name = "Rogue",
            Description = "Rogue",
            Location = "EU"
        };
        await redisMock.Object.SetMatchServer(zeroHostServer.HostAccountName, zeroHostServer);

        DefaultHttpContext zeroCtx = new();
        zeroCtx.Request.Headers["X-Server-Session"] = "zero_host_cookie";
        zeroCtx.Request.Body = new MemoryStream(payloadBytes);

        MatchesV5Controller zeroController = new(channelMock.Object, redisMock.Object, NullLogger<MatchesV5Controller>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = zeroCtx }
        };
        ObjectResult zeroRes = (await zeroController.SubmitBinaryMatch(CancellationToken.None))
            .Should().BeAssignableTo<ObjectResult>().Subject;
        zeroRes.StatusCode.Should().Be(StatusCodes.Status403Forbidden);

        // 3. OwnershipCheckMode = Enforce rejects missing MatchStartData with 403
        IConfiguration enforceConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Matches:OwnershipCheckMode"] = "Enforce"
            })
            .Build();
        User hostOwner = new()
        {
            ID = 900,
            EmailAddress = "host77@kongor.local",
            SRPPasswordSalt = "s",
            SRPPasswordHash = "h",
            PBKDF2PasswordHash = "p"
        };
        Mock<IAccountRepository> accountRepoMock = new();
        accountRepoMock.Setup(r => r.GetByIdAsync(900, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Account
            {
                ID = 900,
                Name = "Host77",
                UserID = 900,
                User = hostOwner,
                Type = AccountType.ServerHost
            });

        ListLogger<MatchesV5Controller> enforceLogger = new();
        DefaultHttpContext enforceCtx = new();
        enforceCtx.Request.Headers["X-Server-Session"] = "valid_header_only_cookie";
        enforceCtx.Request.Body = new MemoryStream(payloadBytes);

        MatchesV5Controller enforceController = new(
            channelMock.Object,
            redisMock.Object,
            enforceLogger,
            accountRepoMock.Object,
            enforceConfig)
        {
            ControllerContext = new ControllerContext { HttpContext = enforceCtx }
        };
        ObjectResult enforceRes = (await enforceController.SubmitBinaryMatch(CancellationToken.None))
            .Should().BeAssignableTo<ObjectResult>().Subject;
        enforceRes.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        enforceLogger.Messages.Should().Contain(m => m.Contains("OwnershipCheckMode=Enforce"));
    }

    // protects: User #2 / first-user account-slot integrity during binary match ingestion.
    // fails_when: Unregistered/forged player names in binary payloads create Guest Account rows attached to existing User #2 or User #1.
    // seam: Real MatchIngestionHandler + EF Core InMemory MerrickContext.
    [Fact]
    public async Task MatchIngestionHandler_UnknownBinaryPlayer_DoesNotHijackUserTwoWithGuestAccount()
    {
        DbContextOptions<MerrickContext> options = new DbContextOptionsBuilder<MerrickContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using MerrickContext db = new(options);

        User realUserTwo = new()
        {
            ID = 2,
            EmailAddress = "realuser2@kongor.local",
            SRPPasswordSalt = "s",
            SRPPasswordHash = "h",
            PBKDF2PasswordHash = "p"
        };
        Account realAccount = new()
        {
            ID = 20,
            Name = "LegitCarry",
            UserID = 2,
            User = realUserTwo,
            Type = AccountType.Normal,
            IsMain = true
        };
        db.Users.Add(realUserTwo);
        db.Accounts.Add(realAccount);
        await db.SaveChangesAsync();

        Mock<IHeroDefinitionService> heroService = new();
        ListLogger<MatchIngestionHandler> logger = new();
        MatchIngestionHandler handler = new(db, heroService.Object, logger);

        MatchFinalizationPayload payload = new()
        {
            MatchId = 445566,
            MapName = "caldavar",
            DurationSeconds = 900,
            WinningTeam = 1,
            ServerId = 1,
            MatchType = AccountStatisticsType.Matchmaking,
            Players =
            [
                new PlayerMatchSummaryDto
                {
                    AccountId = 20,
                    AccountName = "LegitCarry",
                    Team = 1,
                    HeroKey = "Hero_Swiftblade",
                    Level = 15
                },
                new PlayerMatchSummaryDto
                {
                    AccountId = 999999,
                    AccountName = "ForgedGhost",
                    Team = 2,
                    HeroKey = "Hero_Scout",
                    Level = 12
                }
            ]
        };

        IngestedMatchContext ctx = await handler.IngestMatchSubmissionAsync(new MatchSubmissionPayload
        {
            MatchID = 445566,
            BinaryPayload = payload,
            RemoteIp = "127.0.0.1",
            AccountId = 20
        });

        ctx.ProcessedPlayerEntries.Should().ContainSingle(e => e.Account.Name == "LegitCarry");
        (await db.Accounts.AnyAsync(a => a.Name == "ForgedGhost")).Should().BeFalse();
        (await db.Accounts.CountAsync(a => a.UserID == 2)).Should().Be(1);
        logger.Messages.Should().Contain(m => m.Contains("[SECURITY-ALERT]") && m.Contains("ForgedGhost"));
    }

    // protects: PresenceHub query-string '?cookie=' secret rejection, PostgreSQL TLS verification default, and Production Security log level configuration.
    // fails_when: PresenceHub accepts '?cookie=' from URL, ResolveConnectionString appends 'Trust Server Certificate=true' by default, or appsettings.Production.json suppresses KONGOR.Security warnings.
    // seam: Real PresenceHub + ConfigurationExtensions.ResolveConnectionString + appsettings.Production.json file.
    [Fact]
    public async Task PresenceHub_TlsDefaults_AndProductionLogging_AreHardened()
    {
        DbContextOptions<MerrickContext> options = new DbContextOptionsBuilder<MerrickContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using MerrickContext db = new(options);
        User targetOwner = new()
        {
            ID = 55,
            EmailAddress = "target@kongor.local",
            SRPPasswordSalt = "s",
            SRPPasswordHash = "h",
            PBKDF2PasswordHash = "p"
        };
        db.Users.Add(targetOwner);
        db.Accounts.Add(new Account { ID = 55, Name = "TargetUser", UserID = 55, User = targetOwner, Type = AccountType.Normal, IsMain = true });
        await db.SaveChangesAsync();

        Mock<IDatabase> redisDb = new();
        redisDb.Setup(d => d.HashGetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync("TargetUser");
        Mock<IConnectionMultiplexer> mux = new();
        mux.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object?>())).Returns(redisDb.Object);

        DefaultHttpContext httpCtx = new();
        httpCtx.Request.QueryString = new QueryString("?cookie=leaked_query_cookie");
        FeatureCollection features = new();
        features.Set<IHttpContextFeature>(new TestHttpConnectionFeature(httpCtx));

        Mock<HubCallerContext> callerCtx = new();
        callerCtx.SetupGet(c => c.ConnectionId).Returns("conn-query-test");
        callerCtx.SetupGet(c => c.User).Returns(new ClaimsPrincipal());
        callerCtx.SetupGet(c => c.Features).Returns(features);

        Mock<IGroupManager> groups = new();
        ListLogger<PresenceHub> hubLogger = new();
        PresenceHub hub = new(db, mux.Object, hubLogger)
        {
            Context = callerCtx.Object,
            Groups = groups.Object
        };

        await hub.SubscribeToPresence(55);
        groups.Verify(g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        hubLogger.Messages.Should().Contain(m => m.Contains("?cookie="));

        // PostgreSQL URI normalization must NOT inject Trust Server Certificate=true unless env var explicitly opts in
        string? prevEnv = Environment.GetEnvironmentVariable("KONGOR_DB_TRUST_SERVER_CERTIFICATE");
        try
        {
            Environment.SetEnvironmentVariable("KONGOR_DB_TRUST_SERVER_CERTIFICATE", null);
            string resolved = ASPIRE.Common.Extensions.ConfigurationExtensions.NormalizePostgreSqlConnectionString(
                "postgresql://pguser:testpass@db.xyzcompany.supabase.co:5432/postgres");
            resolved.Should().Contain("SSL Mode=Require");
            resolved.Should().NotContain("Trust Server Certificate=true");
        }
        finally
        {
            Environment.SetEnvironmentVariable("KONGOR_DB_TRUST_SERVER_CERTIFICATE", prevEnv);
        }

        // Production logging config must retain Warning level for KONGOR.Security
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ProjectKongor.slnx")))
        {
            dir = dir.Parent;
        }
        dir.Should().NotBeNull();
        string prodConfigPath = Path.Combine(dir!.FullName, "src", "services", "master-server", "appsettings.Production.json");
        using JsonDocument doc = JsonDocument.Parse(await File.ReadAllTextAsync(prodConfigPath));
        string? secLevel = doc.RootElement
            .GetProperty("Logging")
            .GetProperty("LogLevel")
            .GetProperty("KONGOR.Security")
            .GetString();
        secLevel.Should().Be("Warning");
    }
}
