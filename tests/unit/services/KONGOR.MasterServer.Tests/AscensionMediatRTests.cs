// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net;
using System.Text.Json;
using ASPIRE.Common.Models.ServerManagement;
using FluentAssertions;
using KONGOR.MasterServer.Controllers.Ascension;
using KONGOR.MasterServer.Features.Ascension;
using KONGOR.MasterServer.Serialization;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Moq;
using NEXUS.Contracts.CQRS.Ascension;
using NEXUS.Contracts.Data;
using StackExchange.Redis;
using Xunit;

namespace KONGOR.MasterServer.Tests;

public sealed class AscensionMediatRTests
{
    [Fact]
    public void CheckMatchValidator_EnforcesMatchIdGreaterThanZero()
    {
        CheckMatchValidator validator = new();
        validator.Validate(new CheckMatchQuery(0)).IsValid.Should().BeFalse();
        validator.Validate(new CheckMatchQuery(12345)).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task CheckMatchHandler_WhenMatchExistsInCache_ReturnsSeasonMatchTrue()
    {
        Mock<IConnectionMultiplexer> mockRedis = new();
        Mock<IDatabase> mockDb = new();
        mockRedis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(mockDb.Object);

        MatchStartData matchData = new()
        {
            MatchID = 999,
            ServerID = 1,
            ServerName = "Official_Server_1",
            MatchName = "Test Match",
            HostAccountName = "HostPlayer",
            Map = "caldavar",
            Version = "4.10.1.0",
            IsCasual = false,
            MatchType = 1,
            MatchMode = "nm",
            TimestampStarted = DateTimeOffset.UtcNow
        };
        string json = JsonSerializer.Serialize(matchData, MasterServerJsonContext.Default.MatchStartData);
        mockDb.Setup(db => db.StringGetAsync(It.Is<RedisKey>(k => k.ToString().Contains("999")), It.IsAny<CommandFlags>()))
            .ReturnsAsync(new RedisValue(json));

        CheckMatchHandler handler = new(mockRedis.Object, NullLogger<CheckMatchHandler>.Instance);
        var result = await handler.Handle(new CheckMatchQuery(999), CancellationToken.None);

        result.Should().NotBeNull();
        result.IsSeasonMatch.Should().BeTrue();
        result.ErrorCode.Should().Be(100);
    }

    [Fact]
    public async Task CheckMatchHandler_WhenMatchMissing_ReturnsSeasonMatchFalse()
    {
        Mock<IConnectionMultiplexer> mockRedis = new();
        Mock<IDatabase> mockDb = new();
        mockRedis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(mockDb.Object);
        mockDb.Setup(db => db.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisValue.Null);

        CheckMatchHandler handler = new(mockRedis.Object, NullLogger<CheckMatchHandler>.Instance);
        var result = await handler.Handle(new CheckMatchQuery(888), CancellationToken.None);

        result.Should().NotBeNull();
        result.IsSeasonMatch.Should().BeFalse();
        result.ErrorCode.Should().Be(100);
    }

    [Fact]
    public void SpectateMatchValidator_EnforcesMatchIdGreaterThanZero()
    {
        SpectateMatchValidator validator = new();
        validator.Validate(new SpectateMatchCommand(0, 10, "cookie")).IsValid.Should().BeFalse();
        validator.Validate(new SpectateMatchCommand(12345, 10, "cookie")).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task SpectateMatchHandler_ReturnsStarPlayerAndServerAddress()
    {
        Mock<IConnectionMultiplexer> mockRedis = new();
        Mock<IDatabase> mockDb = new();
        Mock<ISubscriber> mockSubscriber = new();
        mockRedis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(mockDb.Object);
        mockRedis.Setup(r => r.GetSubscriber(It.IsAny<object>())).Returns(mockSubscriber.Object);

        MatchStartData matchData = new()
        {
            MatchID = 500,
            ServerID = 10,
            ServerName = "Official_Server_10",
            MatchName = "Grand Final",
            HostAccountName = "CaptainHoN",
            Map = "caldavar",
            Version = "4.10.1.0",
            IsCasual = false,
            MatchType = 1,
            MatchMode = "nm",
            TimestampStarted = DateTimeOffset.UtcNow
        };
        string json = JsonSerializer.Serialize(matchData, MasterServerJsonContext.Default.MatchStartData);
        mockDb.Setup(db => db.StringGetAsync(It.Is<RedisKey>(k => k.ToString().Contains("500")), It.IsAny<CommandFlags>()))
            .ReturnsAsync(new RedisValue(json));

        MatchServer server = new()
        {
            ID = 10,
            Name = "Official_Server_10",
            HostAccountID = 1,
            HostAccountName = "HostAdmin",
            Instance = 1,
            IPAddress = "192.168.1.100",
            Port = 11235,
            Location = "USE",
            Description = "Dedicated Server"
        };
        string serverJson = JsonSerializer.Serialize(server, MasterServerJsonContext.Default.MatchServer);
        mockDb.Setup(db => db.HashGetAsync("MATCH-SERVER-INDEX", 10, It.IsAny<CommandFlags>()))
            .ReturnsAsync(new RedisValue("HostAdmin:11235"));
        mockDb.Setup(db => db.HashGetAsync("MATCH-SERVERS", "HostAdmin:11235", It.IsAny<CommandFlags>()))
            .ReturnsAsync(new RedisValue(serverJson));

        Mock<IAccountRepository> mockAccountRepo = new();

        SpectateMatchHandler handler = new(mockRedis.Object, mockAccountRepo.Object, NullLogger<SpectateMatchHandler>.Instance);
        var result = await handler.Handle(new SpectateMatchCommand(500, 12, "sess_cookie"), CancellationToken.None);

        result.Should().NotBeNull();
        result.Result.Should().Be(0);
        result.MatchId.Should().Be(500);
        result.SpectateDir.Should().Be("192.168.1.100:11235");
        result.StarPlayer.Should().Be("CaptainHoN");
    }

    [Fact]
    public async Task AscensionController_DelegatesCheckMatchToMediatR()
    {
        Mock<IConnectionMultiplexer> mockRedis = new();
        Mock<IDatabase> mockDb = new();
        mockRedis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(mockDb.Object);
        Mock<IMediator> mockMediator = new();

        var dummyData = new { error_code = 100, data = new { is_season_match = true } };
        CheckMatchResultDto dummyResult = new(true, 100, dummyData);
        mockMediator.Setup(m => m.Send(It.IsAny<CheckMatchQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(dummyResult);

        AscensionController controller = new(
            mockRedis.Object,
            null,
            NullLogger<AscensionController>.Instance,
            mediator: mockMediator.Object);

        DefaultHttpContext httpContext = new();
        httpContext.Request.QueryString = new QueryString("?r=api/match/checkmatch&match_id=123");
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        IActionResult actionResult = await controller.RouteAscensionRequest("api/match/checkmatch");

        actionResult.Should().BeOfType<OkObjectResult>();
        var okResult = (OkObjectResult)actionResult;
        okResult.Value.Should().Be(dummyData);
        mockMediator.Verify(m => m.Send(It.Is<CheckMatchQuery>(q => q.MatchId == 123), It.IsAny<CancellationToken>()), Times.Once);
    }
}
