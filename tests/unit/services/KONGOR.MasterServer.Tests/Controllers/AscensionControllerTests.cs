// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net;
using System.Text.Json;
using ASPIRE.Common.Enumerations.Statistics;
using ASPIRE.Common.Models.ServerManagement;
using FluentAssertions;
using KONGOR.MasterServer.Controllers.Ascension;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Statistics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Moq;
using NEXUS.Contracts.Data;
using NEXUS.Contracts.Domain;
using StackExchange.Redis;
using Xunit;
using DbRole = MERRICK.DatabaseContext.Entities.Utility.Role;

namespace KONGOR.MasterServer.Tests.Controllers;

public sealed class AscensionControllerTests
{
    private readonly Mock<IConnectionMultiplexer> _mockRedis = new();
    private readonly Mock<IDatabase> _mockDatabase = new();
    private readonly Mock<IServer> _mockServer = new();
    private readonly Mock<ISubscriber> _mockSubscriber = new();
    private readonly Mock<IAccountRepository> _mockAccountRepo = new();

    public AscensionControllerTests()
    {
        EndPoint dummyEndPoint = new IPEndPoint(IPAddress.Loopback, 6379);
        _mockRedis.Setup(r => r.GetEndPoints(It.IsAny<bool>())).Returns([dummyEndPoint]);
        _mockRedis.Setup(r => r.GetServer(dummyEndPoint, It.IsAny<object>())).Returns(_mockServer.Object);
        _mockRedis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(_mockDatabase.Object);
        _mockRedis.Setup(r => r.GetSubscriber(It.IsAny<object>())).Returns(_mockSubscriber.Object);
    }

    private AscensionController CreateController()
    {
        return new AscensionController(
            _mockRedis.Object,
            _mockAccountRepo.Object,
            NullLogger<AscensionController>.Instance);
    }

    private static void SetQueryString(AscensionController controller, string queryString)
    {
        DefaultHttpContext httpContext = new();
        httpContext.Request.QueryString = new QueryString(queryString);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };
    }

    private static void SetFormPayload(AscensionController controller, Dictionary<string, string> formFields)
    {
        DefaultHttpContext httpContext = new();
        Dictionary<string, StringValues> formDict = [];
        foreach (KeyValuePair<string, string> field in formFields)
        {
            formDict[field.Key] = new StringValues(field.Value);
        }

        httpContext.Request.ContentType = "application/x-www-form-urlencoded";
        httpContext.Request.Form = new FormCollection(formDict);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };
    }

    private static Account CreateAccount(int id, string name, double mmr)
    {
        return new Account
        {
            ID = id,
            Name = name,
            User = new User
            {
                ID = id,
                EmailAddress = $"{name.ToLowerInvariant()}@kongor.net",
                Role = new DbRole { ID = 2, Name = "User" },
                SRPPasswordSalt = "salt",
                SRPPasswordHash = "hash",
                PBKDF2PasswordHash = "hash"
            },
            Statistics =
            [
                new AccountStatistics
                {
                    Type = AccountStatisticsType.Matchmaking,
                    SkillRating = mmr
                }
            ]
        };
    }

    private static MatchStartData CreateMatchStartData(
        int matchId,
        string map = "caldavar",
        string matchMode = "bot",
        int serverId = 1,
        int tier = 1,
        bool isCasual = false,
        string matchName = "Match",
        string hostAccountName = "HostPlayer")
    {
        return new MatchStartData
        {
            MatchID = matchId,
            MatchName = matchName,
            ServerID = serverId,
            ServerName = $"Server_{serverId}",
            HostAccountName = hostAccountName,
            Map = map,
            Version = "4.10.1.0",
            IsCasual = isCasual,
            MatchType = 1,
            MatchMode = matchMode,
            Tier = tier,
            TimestampStarted = TimeProvider.System.GetUtcNow().AddMinutes(-5)
        };
    }

    private static MatchServer CreateMatchServer(
        int serverId = 1,
        string ipAddress = "192.168.1.100",
        int port = 11235,
        string hostAccountName = "ServerHost")
    {
        return new MatchServer
        {
            ID = serverId,
            HostAccountID = 1,
            HostAccountName = hostAccountName,
            Name = $"Server_{serverId}",
            Instance = 0,
            IPAddress = ipAddress,
            Port = port,
            Location = "US",
            Description = "Match Server"
        };
    }

    [Fact]
    public async Task RouteAscensionRequest_WithGetRecommendedMatches_ReturnsMatchesOrderedByMmrDescending()
    {
        // Arrange
        AscensionController controller = CreateController();
        SetQueryString(controller, "?r=api/match/getrecommendedmatches");

        MatchStartData midwarsMatch = CreateMatchStartData(101, map: "midwars", matchMode: "mw", serverId: 1, hostAccountName: "HostMW");
        MatchStartData rankedMatch = CreateMatchStartData(102, map: "caldavar", matchMode: "bot", serverId: 2, tier: 1, hostAccountName: "HostRanked");

        RedisKey key1 = "MATCH-START-DATA:[\"101\"]";
        RedisKey key2 = "MATCH-START-DATA:[\"102\"]";

        _mockServer.Setup(s => s.Keys(
                It.IsAny<int>(),
                It.IsAny<RedisValue>(),
                It.IsAny<int>(),
                It.IsAny<long>(),
                It.IsAny<int>(),
                It.IsAny<CommandFlags>()))
            .Returns([key1, key2]);

        _mockDatabase.Setup(d => d.StringGetAsync(key1, It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisValue)JsonSerializer.SerializeToUtf8Bytes(midwarsMatch));
        _mockDatabase.Setup(d => d.StringGetAsync(key2, It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisValue)JsonSerializer.SerializeToUtf8Bytes(rankedMatch));

        // Players for Match 101 (Midwars) -> avg MMR (1400 + 1600) / 2 = 1500
        _mockDatabase.Setup(d => d.SetMembersAsync($"MATCH-PLAYERS:[\"101\"]", It.IsAny<CommandFlags>()))
            .ReturnsAsync([(RedisValue)"MW_Player1", (RedisValue)"MW_Player2"]);

        // Players for Match 102 (Ranked) -> avg MMR (1800 + 2000) / 2 = 1900
        _mockDatabase.Setup(d => d.SetMembersAsync($"MATCH-PLAYERS:[\"102\"]", It.IsAny<CommandFlags>()))
            .ReturnsAsync([(RedisValue)"Ranked_Player1", (RedisValue)"Ranked_Player2"]);

        Account mw1 = CreateAccount(11, "MW_Player1", 1400);
        Account mw2 = CreateAccount(12, "MW_Player2", 1600);
        Account rk1 = CreateAccount(21, "Ranked_Player1", 1800);
        Account rk2 = CreateAccount(22, "Ranked_Player2", 2000);

        _mockAccountRepo.Setup(a => a.GetAccountsByNamesAsync(
                It.Is<IEnumerable<string>>(n => n.Contains("MW_Player1")),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([mw1, mw2]);

        _mockAccountRepo.Setup(a => a.GetAccountsByNamesAsync(
                It.Is<IEnumerable<string>>(n => n.Contains("Ranked_Player1")),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([rk1, rk2]);

        // Act
        IActionResult result = await controller.RouteAscensionRequest("api/match/getrecommendedmatches");

        // Assert
        result.Should().BeOfType<OkObjectResult>();
        OkObjectResult okResult = (OkObjectResult)result;
        string json = JsonSerializer.Serialize(okResult.Value);
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        root.GetProperty("error_code").GetInt32().Should().Be(100);
        root.GetProperty("success").GetBoolean().Should().BeTrue();

        JsonElement pool = root.GetProperty("recommended_pool");
        pool.GetArrayLength().Should().Be(2);

        // First match must be Ranked 102 (avg MMR 1900 > 1500)
        JsonElement firstMatch = pool[0];
        firstMatch.GetProperty("match_id").GetInt32().Should().Be(102);
        firstMatch.GetProperty("avg_mmr").GetInt32().Should().Be(1900);
        firstMatch.GetProperty("rule_type").GetInt32().Should().Be(1); // Normal ranked
        firstMatch.GetProperty("map").GetString().Should().Be("caldavar");

        // Star player check in first match: Ranked_Player2 (2000 MMR) should be star
        JsonElement rkPlayers = firstMatch.GetProperty("account_list");
        rkPlayers.GetArrayLength().Should().Be(2);
        bool foundStarRk = false;
        foreach (JsonElement p in rkPlayers.EnumerateArray())
        {
            if (p.GetProperty("nickname").GetString() == "Ranked_Player2")
            {
                p.GetProperty("is_star").GetBoolean().Should().BeTrue();
                foundStarRk = true;
            }
            else
            {
                p.GetProperty("is_star").GetBoolean().Should().BeFalse();
            }
        }
        foundStarRk.Should().BeTrue();

        // Second match must be Midwars 101 (avg MMR 1500)
        JsonElement secondMatch = pool[1];
        secondMatch.GetProperty("match_id").GetInt32().Should().Be(101);
        secondMatch.GetProperty("avg_mmr").GetInt32().Should().Be(1500);
        secondMatch.GetProperty("rule_type").GetInt32().Should().Be(3); // Midwars
        secondMatch.GetProperty("map").GetString().Should().Be("midwars");

        // Star player check in second match: MW_Player2 (1600 MMR) should be star
        JsonElement mwPlayers = secondMatch.GetProperty("account_list");
        bool foundStarMw = false;
        foreach (JsonElement p in mwPlayers.EnumerateArray())
        {
            if (p.GetProperty("nickname").GetString() == "MW_Player2")
            {
                p.GetProperty("is_star").GetBoolean().Should().BeTrue();
                foundStarMw = true;
            }
            else
            {
                p.GetProperty("is_star").GetBoolean().Should().BeFalse();
            }
        }
        foundStarMw.Should().BeTrue();
    }

    [Fact]
    public async Task RouteAscensionRequest_WithGetRecommendedMatches_WhenEmpty_ReturnsEmptyPool()
    {
        // Arrange
        AscensionController controller = CreateController();
        SetQueryString(controller, "?r=api/match/getrecommendedmatches");

        _mockServer.Setup(s => s.Keys(
                It.IsAny<int>(),
                It.IsAny<RedisValue>(),
                It.IsAny<int>(),
                It.IsAny<long>(),
                It.IsAny<int>(),
                It.IsAny<CommandFlags>()))
            .Returns([]);

        // Act
        IActionResult result = await controller.RouteAscensionRequest("api/match/getrecommendedmatches");

        // Assert
        result.Should().BeOfType<OkObjectResult>();
        OkObjectResult okResult = (OkObjectResult)result;
        string json = JsonSerializer.Serialize(okResult.Value);
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        root.GetProperty("error_code").GetInt32().Should().Be(100);
        root.GetProperty("success").GetBoolean().Should().BeTrue();
        root.GetProperty("recommended_pool").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task RouteAscensionRequest_WithSpectateMatch_MissingMatchId_ReturnsBadRequest()
    {
        // Arrange
        AscensionController controller = CreateController();
        SetQueryString(controller, "?r=api/MasterServer/SpectateMatch");

        // Act
        IActionResult result = await controller.RouteAscensionRequest("api/MasterServer/SpectateMatch");

        // Assert
        result.Should().BeOfType<BadRequestObjectResult>();
        BadRequestObjectResult badResult = (BadRequestObjectResult)badResultAction();
        IActionResult badResultAction() => result;
        string json = JsonSerializer.Serialize(badResult.Value);
        using JsonDocument doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("error_code").GetInt32().Should().Be(400);
    }

    [Fact]
    public async Task RouteAscensionRequest_WithSpectateMatch_ValidMatch_PublishesRedisEventAndReturnsStarPlayer()
    {
        // Arrange
        AscensionController controller = CreateController();
        SetQueryString(controller, "?r=api/MasterServer/SpectateMatch&match_id=500&hongameclientcookie=secret_spectator_cookie|12345");

        // Mock Spectator Cookie Resolution
        _mockDatabase.Setup(d => d.StringGetAsync("ACCOUNT-SESSION-COOKIE:[\"secret_spectator_cookie\"]", It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisValue)"SpectatorNick");

        Account spectatorAccount = CreateAccount(77, "SpectatorNick", 1500);
        _mockAccountRepo.Setup(a => a.GetByNicknameAsync("SpectatorNick", It.IsAny<CancellationToken>()))
            .ReturnsAsync(spectatorAccount);

        // Mock Match Data
        MatchStartData matchData = CreateMatchStartData(500, matchName: "Championship Finals", serverId: 42);
        _mockDatabase.Setup(d => d.StringGetAsync("MATCH-START-DATA:[\"500\"]", It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisValue)JsonSerializer.SerializeToUtf8Bytes(matchData));

        // Mock Match Server Data
        MatchServer matchServer = CreateMatchServer(serverId: 42, ipAddress: "192.168.1.200", port: 11235, hostAccountName: "ServerHost");
        _mockDatabase.Setup(d => d.HashGetAsync("MATCH-SERVERS", "ServerHost:0", It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisValue.Null);
        _mockDatabase.Setup(d => d.HashGetAllAsync("MATCH-SERVERS", It.IsAny<CommandFlags>()))
            .ReturnsAsync([new HashEntry("ServerHost:0", JsonSerializer.SerializeToUtf8Bytes(matchServer))]);

        // Mock Match Connected Players
        _mockDatabase.Setup(d => d.SetMembersAsync("MATCH-PLAYERS:[\"500\"]", It.IsAny<CommandFlags>()))
            .ReturnsAsync([(RedisValue)"PlayerAlpha", (RedisValue)"PlayerOmega"]);

        Account alpha = CreateAccount(101, "PlayerAlpha", 1600);
        Account omega = CreateAccount(102, "PlayerOmega", 2100);

        _mockAccountRepo.Setup(a => a.GetAccountsByNamesAsync(
                It.Is<IEnumerable<string>>(n => n.Contains("PlayerAlpha")),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([alpha, omega]);

        string? publishedPayload = null;
        RedisChannel? publishedChannel = null;

        _mockSubscriber.Setup(s => s.PublishAsync(
                It.IsAny<RedisChannel>(),
                It.IsAny<RedisValue>(),
                It.IsAny<CommandFlags>()))
            .Callback<RedisChannel, RedisValue, CommandFlags>((ch, val, flags) =>
            {
                publishedChannel = ch;
                publishedPayload = val.ToString();
            })
            .ReturnsAsync(1);

        // Act
        IActionResult result = await controller.RouteAscensionRequest("api/MasterServer/SpectateMatch");

        // Assert
        result.Should().BeOfType<OkObjectResult>();
        OkObjectResult okResult = (OkObjectResult)result;
        string json = JsonSerializer.Serialize(okResult.Value);
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        root.GetProperty("result").GetInt32().Should().Be(0);
        root.GetProperty("star_player").GetString().Should().Be("PlayerOmega");
        root.GetProperty("match_id").GetInt32().Should().Be(500);
        root.GetProperty("spectate_dir").GetString().Should().Be("192.168.1.200:11235");
        root.GetProperty("match_version").GetString().Should().Be("4.10.1.0");

        // Verify Redis Publish
        publishedChannel.Should().NotBeNull();
        publishedChannel!.Value.ToString().Should().Be("ChatServer:HydratePlayerStatus");
        publishedPayload.Should().NotBeNullOrEmpty();

        using JsonDocument eventDoc = JsonDocument.Parse(publishedPayload!);
        JsonElement evRoot = eventDoc.RootElement;
        evRoot.GetProperty("spectator_account_id").GetInt32().Should().Be(77);
        evRoot.GetProperty("star_player_account_id").GetInt32().Should().Be(102);
        evRoot.GetProperty("match_id").GetInt32().Should().Be(500);
        evRoot.GetProperty("server_address").GetString().Should().Be("192.168.1.200:11235");
        evRoot.GetProperty("match_name").GetString().Should().Be("Championship Finals");
    }

    [Fact]
    public async Task RouteAscensionRequest_ViaFormBody_CorrectlyRoutes()
    {
        // Arrange
        AscensionController controller = CreateController();
        SetFormPayload(controller, new Dictionary<string, string>
        {
            ["r"] = "api/match/getrecommendedmatches"
        });

        _mockServer.Setup(s => s.Keys(
                It.IsAny<int>(),
                It.IsAny<RedisValue>(),
                It.IsAny<int>(),
                It.IsAny<long>(),
                It.IsAny<int>(),
                It.IsAny<CommandFlags>()))
            .Returns([]);

        // Act
        IActionResult result = await controller.RouteAscensionRequest(null);

        // Assert
        result.Should().BeOfType<OkObjectResult>();
        OkObjectResult okResult = (OkObjectResult)result;
        string json = JsonSerializer.Serialize(okResult.Value);
        using JsonDocument doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("error_code").GetInt32().Should().Be(100);
        doc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task RouteAscensionRequest_WithGetRecommendedMatches_WithCasualChampionsMatch_MapsToRuleType6()
    {
        // Arrange
        AscensionController controller = CreateController();
        SetQueryString(controller, "?r=api/match/getrecommendedmatches");

        MatchStartData casualTierMatch = CreateMatchStartData(201, map: "caldavar", matchMode: "bot", serverId: 3, tier: 2, isCasual: true);
        RedisKey key = "MATCH-START-DATA:[\"201\"]";

        _mockServer.Setup(s => s.Keys(
                It.IsAny<int>(),
                It.IsAny<RedisValue>(),
                It.IsAny<int>(),
                It.IsAny<long>(),
                It.IsAny<int>(),
                It.IsAny<CommandFlags>()))
            .Returns([key]);

        _mockDatabase.Setup(d => d.StringGetAsync(key, It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisValue)JsonSerializer.SerializeToUtf8Bytes(casualTierMatch));

        _mockDatabase.Setup(d => d.SetMembersAsync("MATCH-PLAYERS:[\"201\"]", It.IsAny<CommandFlags>()))
            .ReturnsAsync([(RedisValue)"PlayerTierCasual"]);

        Account tierPlayer = CreateAccount(31, "PlayerTierCasual", 1750);
        _mockAccountRepo.Setup(a => a.GetAccountsByNamesAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([tierPlayer]);

        // Act
        IActionResult result = await controller.RouteAscensionRequest("api/match/getrecommendedmatches");

        // Assert
        result.Should().BeOfType<OkObjectResult>();
        OkObjectResult okResult = (OkObjectResult)result;
        string json = JsonSerializer.Serialize(okResult.Value);
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        JsonElement pool = root.GetProperty("recommended_pool");
        pool.GetArrayLength().Should().Be(1);
        pool[0].GetProperty("rule_type").GetInt32().Should().Be(6); // Casual Champions
    }

    [Fact]
    public async Task RouteAscensionRequest_WithGetRecommendedMatches_WithDuplicateAccountNames_DoesNotThrow()
    {
        // Arrange
        AscensionController controller = CreateController();
        SetQueryString(controller, "?r=api/match/getrecommendedmatches");

        MatchStartData match = CreateMatchStartData(301, map: "caldavar", matchMode: "bot", serverId: 4);
        RedisKey key = "MATCH-START-DATA:[\"301\"]";

        _mockServer.Setup(s => s.Keys(
                It.IsAny<int>(),
                It.IsAny<RedisValue>(),
                It.IsAny<int>(),
                It.IsAny<long>(),
                It.IsAny<int>(),
                It.IsAny<CommandFlags>()))
            .Returns([key]);

        _mockDatabase.Setup(d => d.StringGetAsync(key, It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisValue)JsonSerializer.SerializeToUtf8Bytes(match));

        _mockDatabase.Setup(d => d.SetMembersAsync("MATCH-PLAYERS:[\"301\"]", It.IsAny<CommandFlags>()))
            .ReturnsAsync([(RedisValue)"DupPlayer", (RedisValue)"dupplayer"]);

        Account p1 = CreateAccount(41, "DupPlayer", 1600);
        Account p2 = CreateAccount(42, "DupPlayer", 1600);
        _mockAccountRepo.Setup(a => a.GetAccountsByNamesAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([p1, p2]);

        // Act
        Func<Task> act = async () => await controller.RouteAscensionRequest("api/match/getrecommendedmatches");

        // Assert: must not throw ArgumentException from ToDictionary duplicate key
        await act.Should().NotThrowAsync();
    }
}
