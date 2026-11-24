// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using ASPIRE.Common.Models.ServerManagement;
using FluentAssertions;
using KONGOR.MasterServer.Extensions.Cache;
using KONGOR.MasterServer.Handlers.ClientRequester;
using KONGOR.MasterServer.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Moq;
using NEXUS.Contracts.Data;
using StackExchange.Redis;
using Xunit;

namespace KONGOR.MasterServer.Tests.Handlers;

public sealed class GameHandlerTeamSizeTests
{
    [Theory]
    [InlineData("10", 20)]
    [InlineData("5", 10)]
    [InlineData("", 10)]
    public async Task HandleRequestAsync_CreateGame_PersistsMaximumPlayersCountFromTeamSize(string teamSizeFormValue, int expectedMaxPlayers)
    {
        // protects: 10v10 Hosted Game creation (midwars_10v10, devowars_10v10, truedevo_10v10) storing MaximumPlayersCount = 20.
        // fails_when: GameHandler ignores team_size form field and hardcodes default 10-player capacity on MatchStartData.
        // seam: Real GameHandler.HandleRequestAsync <-> SharedCache SetMatchStartData / GetMatchStartData round-trip.
        Mock<IDatabase> mockDb = new();
        MatchServer idleServer = new()
        {
            ID = 42,
            HostAccountID = 7,
            HostAccountName = "kongor_host_docker",
            Name = "Docker-11335",
            Instance = 2001,
            IPAddress = "127.0.0.1",
            Port = 11335,
            Location = "USE",
            Description = "Chaos 10v10",
            Cookie = "srv-cookie",
            Status = ServerStatus.SERVER_STATUS_IDLE
        };

        string serverJson = JsonSerializer.Serialize(idleServer, MasterServerJsonContext.Default.MatchServer);
        mockDb.Setup(d => d.HashGetAllAsync("MATCH-SERVERS", CommandFlags.None))
            .ReturnsAsync([new HashEntry("kongor_host_docker:2001", serverJson)]);

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
        mockDb.Setup(d => d.StringIncrementAsync("MATCH-ID-COUNTER", 1, CommandFlags.None)).ReturnsAsync(9001);

        Dictionary<string, RedisValue> kvStore = new(StringComparer.Ordinal);
        mockDb.Setup(d => d.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<When>(),
                It.IsAny<CommandFlags>()))
            .Callback<RedisKey, RedisValue, TimeSpan?, When, CommandFlags>((key, val, _, _, _) => kvStore[key.ToString()] = val)
            .ReturnsAsync(true);
        mockDb.Setup(d => d.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<bool>(),
                It.IsAny<When>(),
                It.IsAny<CommandFlags>()))
            .Callback<RedisKey, RedisValue, TimeSpan?, bool, When, CommandFlags>((key, val, _, _, _, _) => kvStore[key.ToString()] = val)
            .ReturnsAsync(true);
        mockDb.Setup(d => d.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<Expiration>(),
                It.IsAny<ValueCondition>(),
                It.IsAny<CommandFlags>()))
            .Callback<RedisKey, RedisValue, Expiration, ValueCondition, CommandFlags>((key, val, _, _, _) => kvStore[key.ToString()] = val)
            .ReturnsAsync(true);
        mockDb.Setup(d => d.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisKey key, CommandFlags _) => kvStore.TryGetValue(key.ToString(), out RedisValue v) ? v : RedisValue.Null);

        DefaultHttpContext httpContext = new();
        httpContext.Connection.RemoteIpAddress = IPAddress.Loopback;
        httpContext.Items["SessionAccountName"] = "KONGOR";
        Dictionary<string, StringValues> formFields = new()
        {
            ["f"] = "create_game",
            ["name"] = "Chaos 10v10 MidWars",
            ["map"] = "midwars_10v10",
            ["mode"] = "casual",
            ["team_size"] = teamSizeFormValue
        };
        httpContext.Request.ContentType = "application/x-www-form-urlencoded";
        httpContext.Request.Form = new FormCollection(formFields);

        GameHandler handler = new(mockDb.Object, NullLogger<GameHandler>.Instance);
        IActionResult result = await handler.HandleRequestAsync(httpContext);

        result.Should().NotBeNull();
        MatchStartData? saved = await mockDb.Object.GetMatchStartData(9001);
        saved.Should().NotBeNull();
        saved!.MaximumPlayersCount.Should().Be(expectedMaxPlayers);
        saved.Map.Should().Be("midwars_10v10");
    }
}
