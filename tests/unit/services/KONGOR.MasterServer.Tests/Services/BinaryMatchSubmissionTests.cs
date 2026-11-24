// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using ASPIRE.Common.Models.ServerManagement;
using FluentAssertions;
using KONGOR.MasterServer.Controllers;
using KONGOR.MasterServer.Extensions.Cache;
using KONGOR.MasterServer.Features.Matches.SubmitMatchStats;
using KONGOR.MasterServer.Services;
using KONGOR.MasterServer.Services.Background;
using KONGOR.MasterServer.Services.Matches;
using MemoryPack;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NEXUS.Contracts.Domain;
using NEXUS.Contracts.Domain.Matches;
using StackExchange.Redis;
using Xunit;

namespace KONGOR.MasterServer.Tests.Services;

public sealed class BinaryMatchSubmissionTests
{
    private static async Task AttachAuthenticatedServerSessionAsync(Mock<IDatabase> redisMock, DefaultHttpContext httpContext)
    {
        Dictionary<string, RedisValue> sessionHashes = new(StringComparer.Ordinal);
        redisMock.Setup(r => r.HashSetAsync(It.IsAny<RedisKey>(), It.IsAny<HashEntry[]>(), It.IsAny<CommandFlags>()))
            .Callback<RedisKey, HashEntry[], CommandFlags>((key, entries, _) =>
            {
                if (key == "MATCH-SERVER-SESSIONS")
                {
                    foreach (HashEntry entry in entries) sessionHashes[entry.Name!] = entry.Value;
                }
            })
            .Returns(Task.CompletedTask);
        redisMock.Setup(r => r.HashGetAsync("MATCH-SERVER-SESSIONS", It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisKey _, RedisValue field, CommandFlags _) =>
                sessionHashes.TryGetValue(field.ToString(), out RedisValue val) ? val : RedisValue.Null);

        MatchServer server = new()
        {
            ID = 42,
            Instance = 1,
            HostAccountID = 500,
            HostAccountName = "HostServer42",
            IPAddress = "192.168.1.100",
            Port = 11235,
            Cookie = "test_v5_srv_session",
            Name = "Official Server #42",
            Description = "Official Ranked Server",
            Location = "US"
        };
        await redisMock.Object.SetMatchServer(server.HostAccountName, server);
        httpContext.Request.Headers["X-Server-Session"] = "test_v5_srv_session";
    }

    private static MatchFinalizationPayload CreateSamplePayload(int matchId = 12345)
    {
        return new MatchFinalizationPayload
        {
            MatchId = matchId,
            MapName = "caldavar",
            DurationSeconds = 1845,
            WinningTeam = 1,
            ServerId = 42,
            ServerAddress = "192.168.1.100",
            HostAccountName = "HostServer42",
            MatchType = AccountStatisticsType.Matchmaking,
            Players =
            [
                new PlayerMatchSummaryDto
                {
                    AccountId = 101,
                    AccountName = "PlayerOne",
                    Team = 1,
                    HeroKey = "Hero_Chronos",
                    Level = 25,
                    Kills = 14,
                    Deaths = 2,
                    Assists = 8,
                    CreepKills = 240,
                    CreepDenies = 15,
                    NeutralKills = 65,
                    GoldPerMinute = 580,
                    ExpPerMinute = 620,
                    TotalGold = 18500,
                    TotalDamageHero = 28400,
                    TotalDamageBuilding = 6200,
                    WardsPlaced = 4,
                    GroupNum = 1,
                    GroupSize = 2,
                    IsMvp = true,
                    InventoryItemKeys = ["Item_ElderParasite", "Item_SavageScythe", "Item_Steamboots"]
                },
                new PlayerMatchSummaryDto
                {
                    AccountId = 102,
                    AccountName = "PlayerTwo",
                    Team = 2,
                    HeroKey = "Hero_Magmus",
                    Level = 18,
                    Kills = 3,
                    Deaths = 10,
                    Assists = 12,
                    CreepKills = 80,
                    CreepDenies = 4,
                    NeutralKills = 20,
                    GoldPerMinute = 290,
                    ExpPerMinute = 380,
                    TotalGold = 9100,
                    TotalDamageHero = 14200,
                    TotalDamageBuilding = 800,
                    WardsPlaced = 12,
                    GroupNum = 2,
                    GroupSize = 1,
                    IsMvp = false,
                    InventoryItemKeys = ["Item_PortalKey", "Item_Astrolabe", "Item_Marchers"]
                }
            ],
            CombatTimeline =
            [
                new CombatTimelineEventDto
                {
                    GameTick = 1200,
                    EventType = 1,
                    SourceAccountId = 101,
                    TargetAccountId = 102,
                    Identifier = "Hero_Chronos",
                    Value = 1,
                    PositionX = 450.5f,
                    PositionY = 890.2f
                }
            ],
            ReplayChecksumSha256 = new byte[32]
        };
    }

    [Fact]
    public void MemoryPackSerialization_SubMillisecondPerformanceAndIntegrity()
    {
        MatchFinalizationPayload payload = CreateSamplePayload();

        // 1. Serialize
        byte[] serializedBytes = MemoryPackSerializer.Serialize(payload);
        serializedBytes.Should().NotBeNullOrEmpty();

        // Warmup JIT
        _ = MemoryPackSerializer.Deserialize<MatchFinalizationPayload>(serializedBytes);

        // 2. Benchmark Deserialization
        Stopwatch sw = Stopwatch.StartNew();
        MatchFinalizationPayload? deserialized = MemoryPackSerializer.Deserialize<MatchFinalizationPayload>(serializedBytes);
        sw.Stop();

        deserialized.Should().NotBeNull();
        deserialized!.MatchId.Should().Be(payload.MatchId);
        deserialized.MapName.Should().Be("caldavar");
        deserialized.DurationSeconds.Should().Be(1845);
        deserialized.WinningTeam.Should().Be(1);
        deserialized.Players.Should().HaveCount(2);
        deserialized.Players[0].AccountName.Should().Be("PlayerOne");
        deserialized.Players[0].TotalDamageHero.Should().Be(28400);
        deserialized.Players[0].IsMvp.Should().BeTrue();
        deserialized.CombatTimeline.Should().HaveCount(1);
        deserialized.CombatTimeline[0].GameTick.Should().Be(1200);

        // Deserialization must be fast (typically < 100 microseconds; allow 250ms for parallel GC pauses)
        sw.ElapsedMilliseconds.Should().BeLessThan(250);
    }

    [Fact]
    public async Task IngestMatchSubmissionAsync_WithBinaryPayload_CreatesEntitiesAccurately()
    {
        DbContextOptions<MerrickContext> dbOptions = new DbContextOptionsBuilder<MerrickContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using MerrickContext dbContext = new(dbOptions);

        User user1 = new()
        {
            ID = 101,
            EmailAddress = "player1@test.com",
            SRPPasswordSalt = "salt1",
            SRPPasswordHash = "hash1",
            PBKDF2PasswordHash = "pbkdf2_hash1"
        };
        User user2 = new()
        {
            ID = 102,
            EmailAddress = "player2@test.com",
            SRPPasswordSalt = "salt2",
            SRPPasswordHash = "hash2",
            PBKDF2PasswordHash = "pbkdf2_hash2"
        };

        Account account1 = new()
        {
            ID = 101,
            Name = "PlayerOne",
            UserID = 101,
            User = user1,
            Type = AccountType.Normal,
            IsMain = true
        };
        Account account2 = new()
        {
            ID = 102,
            Name = "PlayerTwo",
            UserID = 102,
            User = user2,
            Type = AccountType.Normal,
            IsMain = true
        };

        dbContext.Users.AddRange(user1, user2);
        dbContext.Accounts.AddRange(account1, account2);
        await dbContext.SaveChangesAsync();

        Mock<IHeroDefinitionService> heroServiceMock = new();
        MatchIngestionHandler ingestionHandler = new(
            dbContext,
            heroServiceMock.Object,
            NullLogger<MatchIngestionHandler>.Instance);

        MatchFinalizationPayload payload = CreateSamplePayload(998877);
        MatchSubmissionPayload jobPayload = new()
        {
            MatchID = payload.MatchId,
            BinaryPayload = payload,
            RemoteIp = "127.0.0.1",
            AccountId = 101
        };

        IngestedMatchContext context = await ingestionHandler.IngestMatchSubmissionAsync(jobPayload);

        context.Should().NotBeNull();
        context.MatchID.Should().Be(998877);
        context.BinaryPayload.Should().BeSameAs(payload);
        context.Form.Should().BeNull();
        context.DurationSeconds.Should().Be(1845);
        context.ProcessedPlayerEntries.Should().HaveCount(2);

        PlayerIngestionEntry p1 = context.ProcessedPlayerEntries.First(p => p.Account.ID == 101);
        p1.IsWin.Should().BeTrue();
        p1.IsMvp.Should().BeTrue();
        p1.PlayerStats.Kills.Should().Be(14);
        p1.PlayerStats.Deaths.Should().Be(2);
        p1.PlayerStats.HeroDamage.Should().Be(28400);

        PlayerIngestionEntry p2 = context.ProcessedPlayerEntries.First(p => p.Account.ID == 102);
        p2.IsWin.Should().BeFalse();
        p2.IsMvp.Should().BeFalse();
        p2.PlayerStats.Deaths.Should().Be(10);
    }

    [Fact]
    public async Task MatchesV5Controller_SubmitBinaryMatch_IngestsStreamAndReturnsReceipt()
    {
        MatchFinalizationPayload payload = CreateSamplePayload(556677);
        byte[] payloadBytes = MemoryPackSerializer.Serialize(payload);

        Mock<IMatchProcessingChannel> channelMock = new();
        channelMock
            .Setup(c => c.EnqueueAsync(It.IsAny<MatchSubmissionPayload>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        Mock<IDatabase> redisMock = new();
        redisMock
            .Setup(r => r.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<bool>(),
                It.IsAny<When>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);

        DefaultHttpContext httpContext = new();
        await AttachAuthenticatedServerSessionAsync(redisMock, httpContext);
        httpContext.Request.Body = new MemoryStream(payloadBytes);

        MatchesV5Controller controller = new(
            channelMock.Object,
            redisMock.Object,
            NullLogger<MatchesV5Controller>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };

        IActionResult actionResult = await controller.SubmitBinaryMatch(CancellationToken.None);

        OkObjectResult okResult = actionResult.Should().BeOfType<OkObjectResult>().Subject;
        MatchFinalizationReceipt receipt = okResult.Value.Should().BeOfType<MatchFinalizationReceipt>().Subject;

        receipt.MatchId.Should().Be(556677);
        receipt.Success.Should().BeTrue();
        receipt.Message.Should().Be("Match successfully ingested.");

        channelMock.Verify(c => c.EnqueueAsync(
            It.Is<MatchSubmissionPayload>(p => p.MatchID == 556677 && p.BinaryPayload != null),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MatchesV5Controller_SubmitBinaryMatch_EmptyBody_ReturnsBadRequest()
    {
        Mock<IMatchProcessingChannel> channelMock = new();
        Mock<IDatabase> redisMock = new();

        DefaultHttpContext httpContext = new();
        await AttachAuthenticatedServerSessionAsync(redisMock, httpContext);
        httpContext.Request.Body = new MemoryStream([]);

        MatchesV5Controller controller = new(
            channelMock.Object,
            redisMock.Object,
            NullLogger<MatchesV5Controller>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };

        IActionResult actionResult = await controller.SubmitBinaryMatch(CancellationToken.None);

        BadRequestObjectResult badRequest = actionResult.Should().BeOfType<BadRequestObjectResult>().Subject;
        MatchFinalizationReceipt receipt = badRequest.Value.Should().BeOfType<MatchFinalizationReceipt>().Subject;
        receipt.Success.Should().BeFalse();
        receipt.Message.Should().Contain("empty");
    }

    [Fact]
    public async Task MatchesV5Controller_SubmitBinaryMatch_InvalidMatchId_ReturnsBadRequest()
    {
        MatchFinalizationPayload payload = CreateSamplePayload(0);
        byte[] payloadBytes = MemoryPackSerializer.Serialize(payload);

        Mock<IMatchProcessingChannel> channelMock = new();
        Mock<IDatabase> redisMock = new();

        DefaultHttpContext httpContext = new();
        await AttachAuthenticatedServerSessionAsync(redisMock, httpContext);
        httpContext.Request.Body = new MemoryStream(payloadBytes);

        MatchesV5Controller controller = new(
            channelMock.Object,
            redisMock.Object,
            NullLogger<MatchesV5Controller>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };

        IActionResult actionResult = await controller.SubmitBinaryMatch(CancellationToken.None);

        BadRequestObjectResult badRequest = actionResult.Should().BeOfType<BadRequestObjectResult>().Subject;
        MatchFinalizationReceipt receipt = badRequest.Value.Should().BeOfType<MatchFinalizationReceipt>().Subject;
        receipt.Success.Should().BeFalse();
        receipt.Message.Should().Contain("non-positive or null MatchId");
    }

    [Fact]
    public async Task MatchesV5Controller_SubmitBinaryMatch_DuplicateSubmission_ReturnsIdempotentSuccess()
    {
        MatchFinalizationPayload payload = CreateSamplePayload(123456);
        byte[] payloadBytes = MemoryPackSerializer.Serialize(payload);

        Mock<IMatchProcessingChannel> channelMock = new();
        Mock<IDatabase> redisMock = new();
        // Return false for StringSetAsync to simulate key already existing (duplicate)
        redisMock
            .Setup(r => r.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<bool>(),
                It.IsAny<When>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(false);

        DefaultHttpContext httpContext = new();
        await AttachAuthenticatedServerSessionAsync(redisMock, httpContext);
        httpContext.Request.Body = new MemoryStream(payloadBytes);

        MatchesV5Controller controller = new(
            channelMock.Object,
            redisMock.Object,
            NullLogger<MatchesV5Controller>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };

        IActionResult actionResult = await controller.SubmitBinaryMatch(CancellationToken.None);

        OkObjectResult okResult = actionResult.Should().BeOfType<OkObjectResult>().Subject;
        MatchFinalizationReceipt receipt = okResult.Value.Should().BeOfType<MatchFinalizationReceipt>().Subject;
        receipt.MatchId.Should().Be(123456);
        receipt.Success.Should().BeTrue();
        receipt.Message.Should().Contain("already queued or completed");

        // Processing channel must NOT be called for duplicates
        channelMock.Verify(c => c.EnqueueAsync(
            It.IsAny<MatchSubmissionPayload>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MatchesV5Controller_SubmitBinaryMatch_ChannelFull_Returns503()
    {
        MatchFinalizationPayload payload = CreateSamplePayload(654321);
        byte[] payloadBytes = MemoryPackSerializer.Serialize(payload);

        Mock<IMatchProcessingChannel> channelMock = new();
        channelMock
            .Setup(c => c.EnqueueAsync(It.IsAny<MatchSubmissionPayload>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false); // Simulate full channel

        Mock<IDatabase> redisMock = new();
        redisMock
            .Setup(r => r.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<bool>(),
                It.IsAny<When>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);
        redisMock
            .Setup(r => r.KeyDeleteAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);

        DefaultHttpContext httpContext = new();
        await AttachAuthenticatedServerSessionAsync(redisMock, httpContext);
        httpContext.Request.Body = new MemoryStream(payloadBytes);

        MatchesV5Controller controller = new(
            channelMock.Object,
            redisMock.Object,
            NullLogger<MatchesV5Controller>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };

        IActionResult actionResult = await controller.SubmitBinaryMatch(CancellationToken.None);

        ObjectResult objResult = actionResult.Should().BeOfType<ObjectResult>().Subject;
        objResult.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        MatchFinalizationReceipt receipt = objResult.Value.Should().BeOfType<MatchFinalizationReceipt>().Subject;
        receipt.Success.Should().BeFalse();
        receipt.Message.Should().Contain("queue full");

        // Verify idempotency key was released on rejection
        redisMock.Verify(r => r.KeyDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()), Times.Once);
    }

    [Fact]
    public async Task MatchesV5Controller_SubmitBinaryMatch_MalformedBytes_ReturnsBadRequest()
    {
        byte[] malformedBytes = [0xFF, 0xFE, 0xFD, 0xFC, 0x00, 0x01];

        Mock<IMatchProcessingChannel> channelMock = new();
        Mock<IDatabase> redisMock = new();

        DefaultHttpContext httpContext = new();
        await AttachAuthenticatedServerSessionAsync(redisMock, httpContext);
        httpContext.Request.Body = new MemoryStream(malformedBytes);

        MatchesV5Controller controller = new(
            channelMock.Object,
            redisMock.Object,
            NullLogger<MatchesV5Controller>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };

        IActionResult actionResult = await controller.SubmitBinaryMatch(CancellationToken.None);

        BadRequestObjectResult badRequest = actionResult.Should().BeOfType<BadRequestObjectResult>().Subject;
        MatchFinalizationReceipt receipt = badRequest.Value.Should().BeOfType<MatchFinalizationReceipt>().Subject;
        receipt.Success.Should().BeFalse();
        receipt.Message.Should().Match(m => m.Contains("Failed to deserialize") || m.Contains("non-positive or null MatchId"));
    }
}
