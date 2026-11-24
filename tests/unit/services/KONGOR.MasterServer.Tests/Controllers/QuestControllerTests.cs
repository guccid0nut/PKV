// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text;
using ASPIRE.Common.Extensions.Cache;
using ASPIRE.Common.Models.ServerManagement;
using FluentAssertions;
using KONGOR.MasterServer.Controllers.QuestController;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NEXUS.Contracts.Data;
using StackExchange.Redis;
using Xunit;

namespace KONGOR.MasterServer.Tests.Controllers;

public sealed class QuestControllerTests
{
    [Fact]
    public async Task GetPlayerQuests_WhenFormBodyContainsMalformedBinaryCookieBytes_RecoversWithoutReturningHttp400()
    {
        // Simulates unpatched Linux libgame_shared IGame::ProcessWebRequests reading CHostServer+0x208
        // (NUL + raw UTF-16/binary bytes in form-urlencoded body) — QuestController must never return 400.
        Mock<ISessionRepository> sessionRepoMock = new();
        QuestController controller = new(
            sessionRepoMock.Object,
            NullLogger<QuestController>.Instance);

        byte[] rawPayload = Encoding.Latin1.GetBytes("f=getplayerquests&cookie=%00%F4%BD%8D%88abc");
        DefaultHttpContext httpContext = new();
        httpContext.Request.Method = HttpMethods.Post;
        httpContext.Request.ContentType = "application/x-www-form-urlencoded";
        httpContext.Request.ContentLength = rawPayload.Length;
        httpContext.Request.Body = new MemoryStream(rawPayload);
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        IActionResult result = await controller.GetPlayerQuests(CancellationToken.None);

        OkObjectResult okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        string serialized = okResult.Value.Should().BeOfType<string>().Subject;
        serialized.Should().Contain("quests");
        sessionRepoMock.Verify(
            r => r.GetAccountNameForSessionCookieAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetPlayerQuests_WhenCookieBelongsToRegisteredMatchServer_ValidatesViaRedisMatchServerSessions()
    {
        const string serverCookie = "match-server-cookie-019d7777-aaaa-bbbb-cccc-000000000001";

        Mock<ISessionRepository> sessionRepoMock = new();
        sessionRepoMock
            .Setup(r => r.GetAccountNameForSessionCookieAsync(serverCookie, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        Dictionary<string, RedisValue> matchServerSessions = new(StringComparer.Ordinal);
        Mock<IDatabase> redisDbMock = new();
        redisDbMock
            .Setup(db => db.HashGetAsync("MATCH-SERVER-SESSIONS", serverCookie, It.IsAny<CommandFlags>()))
            .ReturnsAsync(() => matchServerSessions.TryGetValue(serverCookie, out RedisValue v) ? v : RedisValue.Null);
        redisDbMock
            .Setup(db => db.HashSetAsync(It.IsAny<RedisKey>(), It.IsAny<HashEntry[]>(), It.IsAny<CommandFlags>()))
            .Callback((RedisKey key, HashEntry[] entries, CommandFlags _) =>
            {
                if (key == "MATCH-SERVER-SESSIONS")
                {
                    foreach (HashEntry entry in entries)
                    {
                        matchServerSessions[entry.Name.ToString()] = entry.Value;
                    }
                }
            })
            .Returns(Task.CompletedTask);

        MatchServer matchServer = new()
        {
            ID = 1128928427,
            Name = "Slave2002",
            HostAccountName = "compel_slave_2002",
            HostAccountID = 9001,
            Instance = 4002,
            IPAddress = "127.0.0.1",
            Port = 11485,
            LocalPort = 11485,
            Location = "US",
            Description = "DevoWars 10v10",
            Cookie = serverCookie
        };
        await redisDbMock.Object.SetMatchServer(matchServer.HostAccountName, matchServer);

        QuestController controller = new(
            sessionRepoMock.Object,
            NullLogger<QuestController>.Instance,
            redisDbMock.Object);

        byte[] rawPayload = Encoding.UTF8.GetBytes($"f=getplayerquests&cookie={serverCookie}");
        DefaultHttpContext httpContext = new();
        httpContext.Request.Method = HttpMethods.Post;
        httpContext.Request.ContentType = "application/x-www-form-urlencoded";
        httpContext.Request.ContentLength = rawPayload.Length;
        httpContext.Request.Body = new MemoryStream(rawPayload);
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        IActionResult result = await controller.GetPlayerQuests(CancellationToken.None);

        OkObjectResult okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        okResult.Value.Should().BeOfType<string>().Subject.Should().Contain("quests");
        redisDbMock.Verify(
            db => db.HashGetAsync("MATCH-SERVER-SESSIONS", serverCookie, It.IsAny<CommandFlags>()),
            Times.Once);
    }
}
