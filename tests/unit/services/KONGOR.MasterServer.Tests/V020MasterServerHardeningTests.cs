// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using KONGOR.MasterServer.Hubs;
using KONGOR.MasterServer.Serialization;
using KONGOR.MasterServer.Services;
using KONGOR.MasterServer.Services.Network;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Game;
using MERRICK.DatabaseContext.Persistence;
using MERRICK.DatabaseContext.Repositories;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NEXUS.Contracts.Telemetry;
using StackExchange.Redis;
using Xunit;

namespace KONGOR.MasterServer.Tests;

public sealed class V020MasterServerHardeningTests
{
    [Fact]
    public async Task DiscordPresenceRedisSubscriber_DispatchesExclusivelyToOwningAccountSignalRGroup()
    {
        // protects: Multi-user Discord Rich Presence privacy and isolation on MasterServer SignalR PresenceHub.
        // fails_when: DiscordPresenceRedisSubscriber broadcasts to Clients.All instead of Clients.Group(accountId.ToString()).
        // seam: Real DiscordPresenceRedisSubscriber message processor + SignalR IHubClients mock verification.
        Mock<IConnectionMultiplexer> redisMock = new();
        Mock<IDatabase> dbMock = new();
        redisMock.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(dbMock.Object);

        Mock<IHubContext<PresenceHub>> hubContextMock = new();
        Mock<IHubClients> hubClientsMock = new();
        Mock<IClientProxy> groupProxyMock = new();
        Mock<IClientProxy> allProxyMock = new();

        hubClientsMock.Setup(c => c.Group("777")).Returns(groupProxyMock.Object);
        hubClientsMock.Setup(c => c.All).Returns(allProxyMock.Object);
        hubContextMock.SetupGet(h => h.Clients).Returns(hubClientsMock.Object);

        DiscordPresenceRedisSubscriber subscriber = new(
            redisMock.Object,
            hubContextMock.Object,
            NullLogger<DiscordPresenceRedisSubscriber>.Instance);

        DiscordPresenceUpdateDto payload = new()
        {
            AccountId = 777,
            State = "In Game Lobby",
            Details = "Mid Wars"
        };
        string json = JsonSerializer.Serialize(payload, MasterServerJsonContext.Default.DiscordPresenceUpdateDto);

        MethodInfo? processMethod = typeof(DiscordPresenceRedisSubscriber)
            .GetMethod("ProcessMessageAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        processMethod.Should().NotBeNull();

        Task invokeTask = (Task)processMethod!.Invoke(subscriber, [(RedisValue)json, CancellationToken.None])!;
        await invokeTask;

        groupProxyMock.Verify(
            p => p.SendCoreAsync("PresenceUpdated", It.Is<object?[]>(args => args.Length == 1), It.IsAny<CancellationToken>()),
            Times.Once);
        allProxyMock.Verify(
            p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public void NetworkTopologyResolver_ChatServerRouting_RespectsPublicClientPortAndSplitHorizon()
    {
        // protects: Self-hosted LAN/Hairpin NAT vs WAN chat-server endpoint resolution and CHAT_SERVER_PUBLIC_CLIENT_PORT precedence.
        // fails_when: WAN clients receive legacy internal port instead of CHAT_SERVER_PUBLIC_CLIENT_PORT or LAN clients receive unroutable WAN host.
        // seam: Real NetworkTopologyResolverService.ResolveChatServer with injected environment map.
        NetworkTopologyResolverService.ResetCachedPublicIPForTesting();

        Dictionary<string, string?> env = new(StringComparer.OrdinalIgnoreCase)
        {
            ["CHAT_SERVER_HOST"] = "127.0.0.1",
            ["CHAT_SERVER_PORT_CLIENT_LOCAL"] = "11031",
            ["CHAT_SERVER_PUBLIC_HOST"] = "203.0.113.25",
            ["CHAT_SERVER_PUBLIC_CLIENT_PORT"] = "21031",
            ["CHAT_SERVER_PORT_CLIENT"] = "11031",
            ["CHAT_SERVER_LAN_HOST"] = "192.168.1.75"
        };

        NetworkTopologyResolverService resolver = new(
            NullLogger<NetworkTopologyResolverService>.Instance,
            environmentReader: key => env.TryGetValue(key, out string? v) ? v : null);

        resolver.ResolveChatServer(IPAddress.Loopback, "localhost:55555", "HostPlayer", "Regular")
            .Should().Be(("127.0.0.1", 11031));

        resolver.ResolveChatServer(IPAddress.Parse("192.168.1.99"), "192.168.1.75:55555", "LanFriend", "Regular")
            .Should().Be(("192.168.1.75", 11031));

        resolver.ResolveChatServer(IPAddress.Parse("198.51.100.88"), "203.0.113.25:55555", "WanFriend", "Regular")
            .Should().Be(("203.0.113.25", 21031));
    }

    [Fact]
    public async Task AccountRepository_GetAuthIdentityByNicknameAsync_ResolvesCaseInsensitivelyWithUserCredentials()
    {
        // protects: Fast single-query credential lookup during client login without loading heavy friendship/inventory graphs.
        // fails_when: GetAuthIdentityByNicknameAsync misses mixed-case nicknames or omits the User navigation required for password verification.
        // seam: Real AccountRepository + EF Core InMemory MerrickContext.
        DbContextOptions<MerrickContext> options = new DbContextOptionsBuilder<MerrickContext>()
            .UseInMemoryDatabase("merrick_auth_v020_" + Guid.NewGuid().ToString("N"))
            .Options;

        await using MerrickContext db = new(options);
        User user = new()
        {
            ID = 10,
            EmailAddress = "tester@example.local",
            SRPPasswordSalt = "salt123",
            SRPPasswordHash = "hash123",
            PBKDF2PasswordHash = "pbkdf2_123"
        };
        Account account = new()
        {
            ID = 25,
            Name = "EvergreenTester",
            User = user,
            IsMain = true,
            TimestampCreated = TimeProvider.System.GetUtcNow()
        };
        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        AccountRepository repo = new(db);
        Account? resolved = await repo.GetAuthIdentityByNicknameAsync("  evergreentester  ");

        resolved.Should().NotBeNull();
        resolved!.ID.Should().Be(25);
        resolved.User.Should().NotBeNull();
        resolved.User.EmailAddress.Should().Be("tester@example.local");
    }
}
