// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using ASPIRE.Common.Caching;
using ASPIRE.Common.Models.ServerManagement;
using ASPIRE.Common.Serialization.PHP;
using FluentAssertions;
using KONGOR.MasterServer.Controllers.ServerRequesterController;
using KONGOR.MasterServer.Extensions.Cache;
using KONGOR.MasterServer.Features.Matches.StartMatch;
using KONGOR.MasterServer.Handlers.ClientRequester;
using KONGOR.MasterServer.Models.Configuration;
using KONGOR.MasterServer.Serialization;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Enumerations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Moq;
using NEXUS.Contracts.Data;
using NEXUS.Contracts.Domain;
using StackExchange.Redis;
using Xunit;
using DbRole = MERRICK.DatabaseContext.Entities.Utility.Role;

namespace KONGOR.MasterServer.Tests;

public sealed class QueueArcadeIsolationTests
{
    private static MatchServer CreateStandardServer(
        int id,
        string name,
        int port = 11335,
        string location = "USE",
        ServerStatus status = ServerStatus.SERVER_STATUS_IDLE) => new()
    {
        ID = id,
        HostAccountID = 10,
        HostAccountName = "guccihost_dock",
        Name = name,
        Instance = 2001,
        IPAddress = "127.0.0.1",
        Port = port,
        LocalIPAddress = "127.0.0.1",
        LocalPort = port,
        Location = location,
        Description = "caldavar",
        Cookie = $"dock-cookie-{id}",
        Status = status
    };

    private static MatchServer CreateQueueArcadeServer(
        int id,
        string name = "QUEUE ARCADE #Q1",
        int port = 11485,
        int instance = 4001,
        string hostAccount = "guccihost_arc",
        string location = "USE",
        ServerStatus status = ServerStatus.SERVER_STATUS_IDLE) => new()
    {
        ID = id,
        HostAccountID = 11,
        HostAccountName = hostAccount,
        Name = name,
        Instance = instance,
        IPAddress = "127.0.0.1",
        Port = port,
        LocalIPAddress = "127.0.0.1",
        LocalPort = port,
        Location = location,
        Description = "midwars",
        Cookie = $"arc-cookie-{id}",
        Status = status
    };

    private static (Mock<IDatabase> MockDb, ConcurrentDictionary<string, RedisValue> KvStore) CreateRedisMock(
        IReadOnlyList<MatchServer> servers)
    {
        Mock<IDatabase> mockDb = new();
        ConcurrentDictionary<string, RedisValue> kvStore = new(StringComparer.Ordinal);

        HashEntry[] hashEntries = servers
            .Select(s => new HashEntry(
                $"{s.HostAccountName}:{s.Instance}",
                JsonSerializer.Serialize(s, MasterServerJsonContext.Default.MatchServer)))
            .ToArray();

        mockDb.Setup(d => d.HashGetAllAsync("MATCH-SERVERS", CommandFlags.None))
            .ReturnsAsync(hashEntries);

        Mock<IConnectionMultiplexer> mockMultiplexer = new();
        Mock<IServer> mockServer = new();
        IPEndPoint endPoint = new(IPAddress.Loopback, 6379);
        mockMultiplexer.Setup(m => m.GetEndPoints(It.IsAny<bool>())).Returns([endPoint]);
        mockMultiplexer.Setup(m => m.GetServer(endPoint, It.IsAny<object>())).Returns(mockServer.Object);
        mockServer.Setup(s => s.Keys(
                It.IsAny<int>(),
                It.IsAny<RedisValue>(),
                It.IsAny<int>(),
                It.IsAny<long>(),
                It.IsAny<int>(),
                It.IsAny<CommandFlags>()))
            .Returns([]);
        mockDb.SetupGet(d => d.Multiplexer).Returns(mockMultiplexer.Object);
        mockDb.Setup(d => d.StringIncrementAsync("MATCH-ID-COUNTER", 1, CommandFlags.None)).ReturnsAsync(7701);

        mockDb.Setup(d => d.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<When>(),
                It.IsAny<CommandFlags>()))
            .Callback<RedisKey, RedisValue, TimeSpan?, When, CommandFlags>((k, v, _, _, _) => kvStore[k.ToString()] = v)
            .ReturnsAsync(true);
        mockDb.Setup(d => d.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<bool>(),
                It.IsAny<When>(),
                It.IsAny<CommandFlags>()))
            .Callback<RedisKey, RedisValue, TimeSpan?, bool, When, CommandFlags>((k, v, _, _, _, _) => kvStore[k.ToString()] = v)
            .ReturnsAsync(true);
        mockDb.Setup(d => d.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<Expiration>(),
                It.IsAny<ValueCondition>(),
                It.IsAny<CommandFlags>()))
            .Callback<RedisKey, RedisValue, Expiration, ValueCondition, CommandFlags>((k, v, _, _, _) => kvStore[k.ToString()] = v)
            .ReturnsAsync(true);
        mockDb.Setup(d => d.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisKey k, CommandFlags _) => kvStore.TryGetValue(k.ToString(), out RedisValue v) ? v : RedisValue.Null);
        mockDb.Setup(d => d.KeyExistsAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisKey k, CommandFlags _) => kvStore.ContainsKey(k.ToString()));

        return (mockDb, kvStore);
    }

    [Fact]
    public void MatchServer_IsQueueArcadeServer_ClassifiesByHostInstanceAndPort_NeverBySpoofedLobbyName()
    {
        // protects: Spoof-proof Queue Arcade classification so player-named "QUEUE ARCADE" lobbies on Tier D stay visible and keep stats.
        // fails_when: MatchServer.IsQueueArcadeServer inspects mutable Name/Description or misses _arc / Instance 4000..4999 / Tier Q ports.
        // seam: Real MatchServer.IsQueueArcadeServer model contract across all three server-authenticated signals.
        CreateQueueArcadeServer(1, name: "Custom Renamed Server", port: 30000, instance: 1, hostAccount: "guccihost_arc")
            .IsQueueArcadeServer().Should().BeTrue("authenticated _arc host account must always classify as Queue Arcade");

        CreateQueueArcadeServer(2, name: "K3s Arcade Pod", port: 31000, instance: 4001, hostAccount: "guccihost_k3s")
            .IsQueueArcadeServer().Should().BeTrue("COMPEL Tier Q instance 4000..4999 must classify as Queue Arcade even behind NAT");

        CreateQueueArcadeServer(3, name: "Port Only Arcade", port: 11485, instance: 1, hostAccount: "generic_host")
            .IsQueueArcadeServer().Should().BeTrue("Tier Q UDP port 11485..11534 must classify as Queue Arcade");

        CreateQueueArcadeServer(4, name: "Proxy Port Arcade", port: 21486, instance: 1, hostAccount: "generic_host")
            .IsQueueArcadeServer().Should().BeTrue("Tier Q proxy port 21485..21534 must classify as Queue Arcade");

        MatchServer spoofedPublicLobby = CreateStandardServer(5, name: "QUEUE ARCADE SPOOF LOBBY", port: 11335);
        spoofedPublicLobby.Description = "QUEUE ARCADE";
        spoofedPublicLobby.IsQueueArcadeServer()
            .Should().BeFalse("player-mutable MatchName/Description on a Tier D server (guccihost_dock:11335, Instance 2001) must never spoof Queue Arcade");
    }

    [Theory]
    [InlineData("10", true)]
    [InlineData("10", false)]
    [InlineData("90", true)]
    [InlineData("90", false)]
    public async Task ServerListHandler_ExcludesQueueArcadeServers_FromJoinListAndCreateList_AcrossBothCachePaths(
        string gameType,
        bool useHybridCache)
    {
        // protects: Public Game List (gameType=10) and Create Game Select Server picker (gameType=90) never leaking #Q1/#Q2.
        // fails_when: Idle #Q2 (11486) or loading #Q1 (11485) leaks in HybridCache or MemoryCache, or a player lobby named "QUEUE ARCADE" on #D2 is hidden.
        // seam: Real ServerListHandler.HandleRequestAsync -> PhpSerializer wire payload.
        MatchServer d1Idle = CreateStandardServer(101, "KONGOR BATTLEGROUNDS #D1", port: 11335, status: ServerStatus.SERVER_STATUS_IDLE);
        MatchServer d2SpoofName = CreateStandardServer(102, "QUEUE ARCADE CUSTOM 5v5", port: 11336, status: ServerStatus.SERVER_STATUS_LOADING);
        MatchServer q1Loading = CreateQueueArcadeServer(201, "QUEUE ARCADE [midwars] #", port: 11485, status: ServerStatus.SERVER_STATUS_LOADING);
        MatchServer q2Idle = CreateQueueArcadeServer(202, "QUEUE ARCADE #Q2", port: 11486, status: ServerStatus.SERVER_STATUS_IDLE);

        (Mock<IDatabase> mockDb, _) = CreateRedisMock([d1Idle, d2SpoofName, q1Loading, q2Idle]);
        using IMemoryCache memoryCache = new MemoryCache(new MemoryCacheOptions());

        INexusHybridCache? hybridCache = null;
        if (useHybridCache)
        {
            Mock<INexusHybridCache> mockHybrid = new();
            mockHybrid
                .Setup(c => c.GetOrCreateAsync<string>(
                    It.IsAny<string>(),
                    It.IsAny<Func<CancellationToken, Task<string?>>>(),
                    It.IsAny<IReadOnlyCollection<string>?>(),
                    It.IsAny<TimeSpan?>(),
                    It.IsAny<TimeSpan?>(),
                    It.IsAny<CancellationToken>()))
                .Returns((string _, Func<CancellationToken, Task<string?>> factory, IReadOnlyCollection<string>? _, TimeSpan? _, TimeSpan? _, CancellationToken ct) =>
                    factory(ct));
            hybridCache = mockHybrid.Object;
        }

        ServerListHandler handler = new(
            mockDb.Object,
            memoryCache,
            NullLogger<ServerListHandler>.Instance,
            hybridCache);

        DefaultHttpContext ctx = new();
        ctx.Connection.RemoteIpAddress = IPAddress.Loopback;
        Dictionary<string, StringValues> query = new()
        {
            ["cookie"] = new("player_cookie_abc"),
            ["gametype"] = new(gameType),
            ["region"] = new("USE")
        };
        ctx.Request.QueryString = new QueryString($"?cookie=player_cookie_abc&gametype={gameType}&region=USE");
        ctx.Request.Query = new QueryCollection(query);

        IActionResult actionResult = await handler.HandleRequestAsync(ctx);
        PhpResult phpResult = actionResult.Should().BeOfType<PhpResult>().Subject;
        string serialized = phpResult.Data as string ?? PhpSerializer.SerializeToString(phpResult.Data!);

        serialized.Should().Contain("11335", "idle Tier D server #D1 must appear in public server lists");
        serialized.Should().Contain("11336", "Tier D server #D2 hosting a lobby named 'QUEUE ARCADE CUSTOM 5v5' must remain visible");
        serialized.Should().NotContain("11485", "Queue Arcade #Q1 (11485) must never leak into client server_list");
        serialized.Should().NotContain("11486", "Idle Queue Arcade #Q2 (11486) must never leak into client server_list");
    }

    [Fact]
    public async Task GameHandler_CreateGame_ReturnsNoServersAvailable_WhenOnlyQueueArcadeServersAreIdle_IncludingCrossRegionFallback()
    {
        // protects: Automatic server selection (f=create_game) never hijacking warm/idle Tier Q servers when Tier D is full.
        // fails_when: Either regional selection or the cross-region fallback in GameHandler picks #Q1 (USE) or #Q2 (EU).
        // seam: Real GameHandler.HandleRequestAsync against active Tier D + idle Tier Q servers across multiple regions.
        MatchServer d1BusyUse = CreateStandardServer(101, "Busy USE #D1", port: 11335, location: "USE", status: ServerStatus.SERVER_STATUS_ACTIVE);
        MatchServer d2BusyEu = CreateStandardServer(102, "Busy EU #D2", port: 11336, location: "EU", status: ServerStatus.SERVER_STATUS_ACTIVE);
        MatchServer q1IdleUse = CreateQueueArcadeServer(201, "QUEUE ARCADE #Q1", port: 11485, location: "USE", status: ServerStatus.SERVER_STATUS_IDLE);
        MatchServer q2IdleEu = CreateQueueArcadeServer(202, "QUEUE ARCADE EU #Q2", port: 11486, location: "EU", status: ServerStatus.SERVER_STATUS_IDLE);

        (Mock<IDatabase> mockDb, _) = CreateRedisMock([d1BusyUse, d2BusyEu, q1IdleUse, q2IdleEu]);
        GameHandler handler = new(mockDb.Object, NullLogger<GameHandler>.Instance);

        DefaultHttpContext ctx = new();
        ctx.Connection.RemoteIpAddress = IPAddress.Loopback;
        ctx.Items["SessionAccountName"] = "TestHostPlayer";
        ctx.Request.ContentType = "application/x-www-form-urlencoded";
        ctx.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["f"] = new("create_game"),
            ["name"] = new("My Custom Lobby"),
            ["map"] = new("caldavar"),
            ["mode"] = new("normal"),
            ["region"] = new("USE")
        });

        IActionResult result = await handler.HandleRequestAsync(ctx);
        PhpResult phpResult = result.Should().BeOfType<PhpResult>().Subject;
        string serialized = PhpSerializer.SerializeToString(phpResult.Data!);

        serialized.Should().Contain("No Servers Available");
        serialized.Should().NotContain("11485");
        serialized.Should().NotContain("11486");
    }

    [Fact]
    public async Task StartMatchHandler_BlocksPlayerHostedCustomGame_OnQueueArcadeServer_WhileAllowingCoordinatorWarmupMatch()
    {
        // protects: Idle #Q servers against direct SendCreateGameRequest (MatchType == 0) hijack while preserving Coordinator MatchType == 3.
        // fails_when: StartMatchHandler allows MatchType == 0 on a Queue Arcade server or blocks MatchType == 3 warmup matches.
        // seam: Real StartMatchHandler.HandleAsync + Redis MatchServer session lookup.
        MatchServer q1 = CreateQueueArcadeServer(201, "QUEUE ARCADE #Q1", port: 11485, status: ServerStatus.SERVER_STATUS_IDLE);
        (Mock<IDatabase> mockDb, _) = CreateRedisMock([q1]);
        StartMatchHandler handler = new(mockDb.Object, NullLogger<StartMatchHandler>.Instance);

        StartMatchResponse blockedPublic = await handler.HandleAsync(new StartMatchCommand(
            Session: q1.Cookie,
            Map: "caldavar",
            Version: "4.10.1.0",
            MatchName: "Hijacked Public Game",
            HostAccountName: "MaliciousPlayer",
            IsCasual: false,
            MatchType: 0,
            MatchMode: "normal"));

        blockedPublic.Success.Should().BeFalse();
        blockedPublic.StatusCode.Should().Be(403);
        blockedPublic.ErrorMessage.Should().Contain("Queue Arcade");

        StartMatchResponse allowedCoordinator = await handler.HandleAsync(new StartMatchCommand(
            Session: q1.Cookie,
            Map: "midwars",
            Version: "4.10.1.0",
            MatchName: "QUEUE ARCADE [midwars] #",
            HostAccountName: "DedicatedHost",
            IsCasual: true,
            MatchType: 3,
            MatchMode: "normal"));

        allowedCoordinator.Success.Should().BeTrue();
        allowedCoordinator.StatusCode.Should().Be(200);
    }

    [Fact]
    public async Task ServerRequester_ConnectClient_EnforcesArcadeAllowTicketAndBlocksPublicJoin_WhileExemptingStaff()
    {
        // protects: Server-side c_conn barrier against console Connect('ip:11485') (new=1 or missing ARCADE-ALLOW ticket).
        // fails_when: Unassigned players pass c_conn on Queue Arcade, or Coordinator-pushed players (new=4 + ticket) / Staff are blocked.
        // seam: Real ServerRequesterController (f=c_conn) <-> Redis session + ARCADE-ALLOW key validation.
        MatchServer q1 = CreateQueueArcadeServer(201, "QUEUE ARCADE #Q1", port: 11485, status: ServerStatus.SERVER_STATUS_ACTIVE);
        (Mock<IDatabase> mockDb, ConcurrentDictionary<string, RedisValue> kvStore) = CreateRedisMock([q1]);

        Account normalPlayer = new()
        {
            ID = 501,
            Name = "SoloQueuer",
            Type = AccountType.Normal,
            User = new User
            {
                ID = 501,
                EmailAddress = "solo@kongor.test",
                Role = new DbRole { ID = 2, Name = "User" },
                SRPPasswordSalt = "s",
                SRPPasswordHash = "h",
                PBKDF2PasswordHash = "p",
                Accounts = []
            }
        };

        Account staffPlayer = new()
        {
            ID = 777,
            Name = "AdminObserver",
            Type = AccountType.Staff,
            User = new User
            {
                ID = 777,
                EmailAddress = "staff@kongor.test",
                Role = new DbRole { ID = 1, Name = "Staff" },
                SRPPasswordSalt = "s",
                SRPPasswordHash = "h",
                PBKDF2PasswordHash = "p",
                Accounts = []
            }
        };

        await mockDb.Object.SetAccountNameForSessionCookie("cookie_normal", normalPlayer.Name);
        await mockDb.Object.SetAccountNameForSessionCookie("cookie_staff", staffPlayer.Name);

        Mock<IAccountRepository> mockAccounts = new();
        mockAccounts.Setup(r => r.GetByNicknameAsync("SoloQueuer", It.IsAny<CancellationToken>())).ReturnsAsync(normalPlayer);
        mockAccounts.Setup(r => r.GetByNicknameAsync("AdminObserver", It.IsAny<CancellationToken>())).ReturnsAsync(staffPlayer);

        Mock<IMatchHistoryRepository> mockHistory = new();
        IOptions<OperationalConfiguration> config = Options.Create(new OperationalConfiguration
        {
            CDN = new OperationalConfigurationCDN
            {
                Host = "localhost",
                PrimaryPatchURL = "http://localhost",
                SecondaryPatchURL = "http://localhost"
            }
        });

        async Task<IActionResult> SendConnectClientAsync(string clientCookie, string newParam)
        {
            ServerRequesterController controller = new(
                mockAccounts.Object,
                mockHistory.Object,
                mockDb.Object,
                NullLogger<ServerRequesterController>.Instance,
                config);

            DefaultHttpContext httpContext = new();
            httpContext.Request.ContentType = "application/x-www-form-urlencoded";
            httpContext.Request.Form = new FormCollection(new Dictionary<string, StringValues>
            {
                ["f"] = new("c_conn"),
                ["session"] = new(q1.Cookie),
                ["cookie"] = new(clientCookie),
                ["ip"] = new("127.0.0.1"),
                ["cas"] = new("1"),
                ["new"] = new(newParam)
            });
            controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
            return await controller.ServerRequester();
        }

        // 1. Direct console Connect() (new=1 AM_PUBLIC) without ticket -> 401 Unauthorized
        (await SendConnectClientAsync("cookie_normal", "1")).Should().BeOfType<UnauthorizedObjectResult>();

        // 2. Forged new=4 without Coordinator ARCADE-ALLOW ticket -> 401 Unauthorized
        (await SendConnectClientAsync("cookie_normal", "4")).Should().BeOfType<UnauthorizedObjectResult>();

        // 3. Grant Coordinator ARCADE-ALLOW ticket -> new=1 STILL blocked, new=4 SUCCEEDS (200 OK)!
        kvStore[MatchServer.GetQueueArcadeAllowKey(q1.ID, normalPlayer.ID)] = "8801";
        (await SendConnectClientAsync("cookie_normal", "1")).Should().BeOfType<UnauthorizedObjectResult>();
        (await SendConnectClientAsync("cookie_normal", "4")).Should().BeOfType<OkObjectResult>();

        // 4. Staff observer succeeds without ARCADE-ALLOW ticket
        (await SendConnectClientAsync("cookie_staff", "1")).Should().BeOfType<OkObjectResult>();
    }
}
