// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Models.ServerManagement;
using FluentAssertions;
using KONGOR.MasterServer.Features.Matches.StartMatch;
using KONGOR.MasterServer.Serialization;
using MERRICK.DatabaseContext.Entities.Statistics;
using MERRICK.DatabaseContext.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NEXUS.Contracts.Data;
using StackExchange.Redis;
using Xunit;

namespace KONGOR.MasterServer.Tests.Features;

public class StartMatchSliceTests
{
    private readonly StartMatchValidator _validator = new();

    [Fact]
    public void Validator_WhenVersionInvalid_ReturnsInvalid()
    {
        StartMatchCommand command = new(
            Session: "sess1",
            Map: "caldavar",
            Version: "1.0.0.0",
            MatchName: "Test Match",
            HostAccountName: "HostUser",
            IsCasual: false,
            MatchType: 1,
            MatchMode: "normal");

        (bool isValid, string? error) = _validator.Validate(command);
        isValid.Should().BeFalse();
        error.Should().Contain("Unsupported Match Server Version");
    }

    [Theory]
    [InlineData("4.10.1.0")]
    [InlineData("5.0.0.0")]
    public void Validator_WhenValid_ReturnsTrue(string version)
    {
        StartMatchCommand command = new(
            Session: "sess1",
            Map: "caldavar",
            Version: version,
            MatchName: "Test Match",
            HostAccountName: "HostUser",
            IsCasual: false,
            MatchType: 1,
            MatchMode: "normal");

        (bool isValid, string? error) = _validator.Validate(command);
        isValid.Should().BeTrue();
        error.Should().BeNull();
    }

    [Fact]
    public async Task Handler_WhenSessionNotFound_ReturnsUnauthorized()
    {
        Mock<IDatabase> mockDb = new();
        mockDb.Setup(d => d.HashGetAllAsync(It.IsAny<RedisKey>(), CommandFlags.None))
            .ReturnsAsync([]);

        StartMatchHandler handler = new(mockDb.Object, NullLogger<StartMatchHandler>.Instance);

        StartMatchCommand command = new(
            Session: "invalid-sess",
            Map: "caldavar",
            Version: "4.10.1.0",
            MatchName: "Test Match",
            HostAccountName: "HostUser",
            IsCasual: false,
            MatchType: 1,
            MatchMode: "normal");

        StartMatchResponse response = await handler.HandleAsync(command);

        response.Success.Should().BeFalse();
        response.StatusCode.Should().Be(401);
    }

    [Fact]
    public async Task Handler_WhenValid_AllocatesMatchAndReturnsSucceeded()
    {
        Mock<IDatabase> mockDb = new();

        MatchServer server = new()
        {
            ID = 5,
            HostAccountID = 10,
            HostAccountName = "HostUser",
            Name = "Server-5",
            Instance = 1,
            IPAddress = "127.0.0.1",
            Port = 11235,
            Location = "Local",
            Description = "Test Server",
            Cookie = "valid-sess"
        };

        string json = System.Text.Json.JsonSerializer.Serialize(server, MasterServerJsonContext.Default.MatchServer);
        mockDb.Setup(d => d.HashGetAllAsync("MATCH-SERVERS", CommandFlags.None))
            .ReturnsAsync([new HashEntry("HostUser:1", json)]);

        Mock<IConnectionMultiplexer> mockMultiplexer = new();
        Mock<IServer> mockServer = new();
        System.Net.IPEndPoint endPoint = new(System.Net.IPAddress.Loopback, 6379);

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

        mockDb.Setup(d => d.StringIncrementAsync("MATCH-ID-COUNTER", 1, CommandFlags.None))
            .ReturnsAsync(4001);

        mockDb.Setup(d => d.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<bool>(),
                It.IsAny<When>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);

        StartMatchHandler handler = new(mockDb.Object, NullLogger<StartMatchHandler>.Instance);

        StartMatchCommand command = new(
            Session: "valid-sess",
            Map: "caldavar",
            Version: "4.10.1.0",
            MatchName: "Test Match",
            HostAccountName: "HostUser",
            IsCasual: false,
            MatchType: 1,
            MatchMode: "normal");

        StartMatchResponse response = await handler.HandleAsync(command);

        response.ErrorMessage.Should().BeNull();
        response.Success.Should().BeTrue();
        response.StatusCode.Should().Be(200);
        response.ResponseData.Should().ContainKey("match_id");
        response.ResponseData!["match_id"].Should().Be(4001);
    }

    [Fact]
    public async Task Handler_WhenDatabaseProvided_CreatesMatchRecordInLobbyState()
    {
        Mock<IDatabase> mockDb = new();

        MatchServer server = new()
        {
            ID = 5,
            HostAccountID = 10,
            HostAccountName = "HostUser",
            Name = "Server-5",
            Instance = 1,
            IPAddress = "127.0.0.1",
            Port = 11235,
            Location = "Local",
            Description = "Test Server",
            Cookie = "valid-sess"
        };

        string json = System.Text.Json.JsonSerializer.Serialize(server, MasterServerJsonContext.Default.MatchServer);
        mockDb.Setup(d => d.HashGetAllAsync("MATCH-SERVERS", CommandFlags.None))
            .ReturnsAsync([new HashEntry("HostUser:1", json)]);

        Mock<IConnectionMultiplexer> mockMultiplexer = new();
        Mock<IServer> mockServer = new();
        System.Net.IPEndPoint endPoint = new(System.Net.IPAddress.Loopback, 6379);

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

        mockDb.Setup(d => d.StringIncrementAsync("MATCH-ID-COUNTER", 1, CommandFlags.None))
            .ReturnsAsync(5001);

        mockDb.Setup(d => d.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<bool>(),
                It.IsAny<When>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);

        DbContextOptions<MerrickContext> dbOptions = new DbContextOptionsBuilder<MerrickContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        using MerrickContext db = new(dbOptions);

        StartMatchHandler handler = new(mockDb.Object, NullLogger<StartMatchHandler>.Instance, db);

        StartMatchCommand command = new(
            Session: "valid-sess",
            Map: "caldavar",
            Version: "5.0.0.0",
            MatchName: "Test Match",
            HostAccountName: "HostUser",
            IsCasual: false,
            MatchType: 1,
            MatchMode: "normal");

        StartMatchResponse response = await handler.HandleAsync(command);

        response.Success.Should().BeTrue();
        response.ResponseData!["match_id"].Should().Be(5001);

        MatchRecord? record = await db.MatchRecords.FirstOrDefaultAsync(m => m.MatchID == 5001);
        record.Should().NotBeNull();
        record!.State.Should().Be(MatchLifecycleState.Lobby);
        record.ServerID.Should().Be(5);
        record.ServerPort.Should().Be(11235);
        record.HostAccountName.Should().Be("HostUser");
        record.Map.Should().Be("caldavar");

        // Test abandonment
        await handler.AbandonMatchAsync(5001, "TestLobbyCancelled");
        record = await db.MatchRecords.FirstOrDefaultAsync(m => m.MatchID == 5001);
        record!.State.Should().Be(MatchLifecycleState.Cancelled);
        record.TerminationReason.Should().Be("TestLobbyCancelled");
        record.EndedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Handler_WhenIdempotencyLeaseHit_ReusesMatchIdWithoutIncrementing()
    {
        Mock<IDatabase> mockDb = new();

        MatchServer server = new()
        {
            ID = 7,
            HostAccountID = 11,
            HostAccountName = "HostUser",
            Name = "Server-7",
            Instance = 1,
            IPAddress = "127.0.0.1",
            Port = 11237,
            Location = "Local",
            Description = "Test Server",
            Cookie = "valid-sess"
        };

        string json = System.Text.Json.JsonSerializer.Serialize(server, MasterServerJsonContext.Default.MatchServer);
        mockDb.Setup(d => d.HashGetAllAsync("MATCH-SERVERS", CommandFlags.None))
            .ReturnsAsync([new HashEntry("HostUser:1", json)]);

        DbContextOptions<MerrickContext> dbOptions = new DbContextOptionsBuilder<MerrickContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        using MerrickContext db = new(dbOptions);

        // Pre-insert an existing leased match
        db.MatchRecords.Add(new MatchRecord
        {
            MatchID = 6001,
            ServerID = 7,
            ServerPort = 11237,
            ServerName = "Server-7",
            HostAccountName = "HostUser",
            Map = "caldavar",
            MatchMode = "normal",
            State = MatchLifecycleState.Lobby,
            CreatedAt = TimeProvider.System.GetUtcNow()
        });
        await db.SaveChangesAsync();

        // Setup Redis to return the existing lease ID
        mockDb.Setup(d => d.StringGetAsync("MATCH-LEASE:7:caldavar:Test Match", CommandFlags.None))
            .ReturnsAsync((RedisValue)"6001");

        StartMatchHandler handler = new(mockDb.Object, NullLogger<StartMatchHandler>.Instance, db);

        StartMatchCommand command = new(
            Session: "valid-sess",
            Map: "caldavar",
            Version: "5.0.0.0",
            MatchName: "Test Match",
            HostAccountName: "HostUser",
            IsCasual: false,
            MatchType: 1,
            MatchMode: "normal");

        StartMatchResponse response = await handler.HandleAsync(command);

        response.Success.Should().BeTrue();
        response.ResponseData!["match_id"].Should().Be(6001);

        // Verify StringIncrementAsync was NEVER called (idempotency prevented burning a new ID)
        mockDb.Verify(d => d.StringIncrementAsync("MATCH-ID-COUNTER", It.IsAny<long>(), It.IsAny<CommandFlags>()), Times.Never);
    }

    [Fact]
    public async Task AbandonMatchAsync_WithoutSessionCookie_ResolvesServerByIdAndScrubsLobbyMetadata()
    {
        // protects: PLAYER HOSTED GAMES list hygiene when COMPEL reaps an empty 0/10 lobby via InternalServiceKey (session == null)
        // fails_when: AbandonMatchAsync skips MatchServer update when session is empty or leaves stale lobby Name ('LinuxTexSoccer'), Description, or ConnectedPlayersCount on the recycled worker
        // seam: Real StartMatchHandler.AbandonMatchAsync + protocol-core DistributedCacheExtensions MemoryPack serialization + MerrickContext
        Mock<IDatabase> mockDb = new();
        Dictionary<string, RedisValue> matchServersHash = new(StringComparer.Ordinal);
        Dictionary<string, RedisValue> matchStartKv = new(StringComparer.Ordinal);

        MatchServer worker = new()
        {
            ID = 15,
            HostAccountID = 200,
            HostAccountName = "DedicatedHost",
            Name = "LinuxTexSoccer",
            Instance = 1,
            IPAddress = "127.0.0.1",
            Port = 11335,
            Location = "LOCAL",
            Description = "Abandoned 0/10 lobby",
            Status = ServerStatus.SERVER_STATUS_LOADING,
            ConnectedPlayersCount = 2,
            Cookie = "worker-sess-15"
        };
        byte[] workerBytes = MemoryPack.MemoryPackSerializer.Serialize(worker);
        matchServersHash["DedicatedHost:11335"] = workerBytes;

        MatchStartData startData = new()
        {
            MatchID = 7001,
            ServerID = 15,
            ServerName = "Slave2001",
            MatchName = "LinuxTexSoccer",
            HostAccountName = "PlayerAlice",
            Map = "soccer",
            Version = "4.10.1.0",
            IsCasual = false,
            MatchType = 0,
            MatchMode = "ap",
            ConnectedPlayersCount = 0
        };
        matchStartKv["MATCH-START-DATA:[\"7001\"]"] = MemoryPack.MemoryPackSerializer.Serialize(startData);

        mockDb.Setup(d => d.StringGetAsync(It.IsAny<RedisKey>(), CommandFlags.None))
            .ReturnsAsync((RedisKey key, CommandFlags _) =>
                matchStartKv.TryGetValue(key.ToString(), out RedisValue val) ? val : RedisValue.Null);
        mockDb.Setup(d => d.HashGetAsync("MATCH-SERVER-INDEX", 15, CommandFlags.None))
            .ReturnsAsync((RedisValue)"DedicatedHost:11335");
        mockDb.Setup(d => d.HashGetAsync("MATCH-SERVERS", "DedicatedHost:11335", CommandFlags.None))
            .ReturnsAsync(() => matchServersHash["DedicatedHost:11335"]);
        mockDb.Setup(d => d.HashSetAsync("MATCH-SERVERS", It.IsAny<HashEntry[]>(), CommandFlags.None))
            .Callback<RedisKey, HashEntry[], CommandFlags>((_, entries, _) =>
            {
                foreach (HashEntry entry in entries)
                {
                    matchServersHash[entry.Name.ToString()] = entry.Value;
                }
            })
            .Returns(Task.CompletedTask);

        DbContextOptions<MerrickContext> dbOptions = new DbContextOptionsBuilder<MerrickContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using MerrickContext db = new(dbOptions);
        db.MatchRecords.Add(new MatchRecord
        {
            MatchID = 7001,
            ServerID = 15,
            ServerPort = 11335,
            ServerName = "LinuxTexSoccer",
            HostAccountName = "PlayerAlice",
            Map = "soccer",
            MatchMode = "ap",
            MatchType = 0,
            State = MatchLifecycleState.Lobby,
            CreatedAt = TimeProvider.System.GetUtcNow()
        });
        await db.SaveChangesAsync();

        StartMatchHandler handler = new(mockDb.Object, NullLogger<StartMatchHandler>.Instance, db);

        bool abandoned = await handler.AbandonMatchAsync(7001, "EmptyLobbyTimeout", session: null);
        abandoned.Should().BeTrue();

        MatchServer? updated = MemoryPack.MemoryPackSerializer.Deserialize<MatchServer>((byte[])matchServersHash["DedicatedHost:11335"]!);
        updated.Should().NotBeNull();
        updated!.Status.Should().Be(ServerStatus.SERVER_STATUS_IDLE);
        updated.ConnectedPlayersCount.Should().Be(0);
        updated.Description.Should().BeEmpty();
        updated.Name.Should().Be("Slave2001");

        MatchRecord? record = await db.MatchRecords.FirstOrDefaultAsync(m => m.MatchID == 7001);
        record!.State.Should().Be(MatchLifecycleState.Cancelled);
        record.TerminationReason.Should().Be("EmptyLobbyTimeout");
    }

    [Fact]
    public async Task ReclaimEmptyHostedLobbiesForPlayerAsync_WhenHostMovesToAnotherServer_AbandonsZeroPlayerLobbyOnly()
    {
        // protects: Immediate reclaim of orphaned 0/10 public lobbies when the host player connects to or opens a match on another server
        // fails_when: Orphaned 0/10 lobby on Server A remains in SERVER_STATUS_LOADING with stale lobby name, OR an active lobby with >= 1 connected player is mistakenly reaped
        // seam: Real StartMatchHandler.ReclaimEmptyHostedLobbiesForPlayerAsync + AbandonMatchAsync + MemoryPack Redis payloads
        Mock<IDatabase> mockDb = new();
        Dictionary<string, RedisValue> matchServersHash = new(StringComparer.Ordinal);
        Dictionary<string, RedisValue> matchStartKv = new(StringComparer.Ordinal);

        MatchServer staleEmptyServer = new()
        {
            ID = 21,
            HostAccountID = 200,
            HostAccountName = "DedicatedHost",
            Name = "CrossOSSoccer",
            Instance = 1,
            IPAddress = "127.0.0.1",
            Port = 11236,
            Location = "LOCAL",
            Description = "0/10 orphan",
            Status = ServerStatus.SERVER_STATUS_LOADING,
            ConnectedPlayersCount = 0,
            Cookie = "sess-21"
        };
        MatchServer populatedServer = new()
        {
            ID = 22,
            HostAccountID = 200,
            HostAccountName = "DedicatedHost",
            Name = "PopulatedCaldavar",
            Instance = 2,
            IPAddress = "127.0.0.1",
            Port = 11237,
            Location = "LOCAL",
            Description = "Has 1 player",
            Status = ServerStatus.SERVER_STATUS_LOADING,
            ConnectedPlayersCount = 0,
            Cookie = "sess-22"
        };
        matchServersHash["DedicatedHost:11236"] = MemoryPack.MemoryPackSerializer.Serialize(staleEmptyServer);
        matchServersHash["DedicatedHost:11237"] = MemoryPack.MemoryPackSerializer.Serialize(populatedServer);

        MatchStartData staleStart = new()
        {
            MatchID = 8001,
            ServerID = 21,
            ServerName = "Slave1002",
            MatchName = "CrossOSSoccer",
            HostAccountName = "PlayerAlice",
            Map = "soccer",
            Version = "4.10.1.0",
            IsCasual = false,
            MatchType = 0,
            MatchMode = "ap",
            ConnectedPlayersCount = 0
        };
        MatchStartData populatedStart = new()
        {
            MatchID = 8002,
            ServerID = 22,
            ServerName = "Slave1003",
            MatchName = "PopulatedCaldavar",
            HostAccountName = "PlayerAlice",
            Map = "caldavar",
            Version = "4.10.1.0",
            IsCasual = false,
            MatchType = 0,
            MatchMode = "ap",
            ConnectedPlayersCount = 0
        };
        matchStartKv["MATCH-START-DATA:[\"8001\"]"] = MemoryPack.MemoryPackSerializer.Serialize(staleStart);
        matchStartKv["MATCH-START-DATA:[\"8002\"]"] = MemoryPack.MemoryPackSerializer.Serialize(populatedStart);

        Mock<IConnectionMultiplexer> mockMultiplexer = new();
        Mock<IServer> mockServer = new();
        System.Net.IPEndPoint endPoint = new(System.Net.IPAddress.Loopback, 6379);
        mockMultiplexer.Setup(m => m.GetEndPoints(It.IsAny<bool>())).Returns([endPoint]);
        mockMultiplexer.Setup(m => m.GetServer(endPoint, It.IsAny<object>())).Returns(mockServer.Object);
        mockServer.Setup(s => s.Keys(
                It.IsAny<int>(),
                It.IsAny<RedisValue>(),
                It.IsAny<int>(),
                It.IsAny<long>(),
                It.IsAny<int>(),
                It.IsAny<CommandFlags>()))
            .Returns([.. matchStartKv.Keys.Select(k => (RedisKey)k)]);
        mockDb.SetupGet(d => d.Multiplexer).Returns(mockMultiplexer.Object);

        mockDb.Setup(d => d.HashGetAllAsync("MATCH-SERVERS", CommandFlags.None))
            .ReturnsAsync(() => [.. matchServersHash.Select(kv => new HashEntry(kv.Key, kv.Value))]);
        mockDb.Setup(d => d.HashGetAsync("MATCH-SERVER-SESSIONS", "sess-21", CommandFlags.None))
            .ReturnsAsync(() => matchServersHash["DedicatedHost:11236"]);
        mockDb.Setup(d => d.StringGetAsync(It.IsAny<RedisKey>(), CommandFlags.None))
            .ReturnsAsync((RedisKey key, CommandFlags _) =>
                matchStartKv.TryGetValue(key.ToString(), out RedisValue val) ? val : RedisValue.Null);
        mockDb.Setup(d => d.SetMembersAsync("MATCH-PLAYERS:[\"8001\"]", CommandFlags.None))
            .ReturnsAsync([]);
        mockDb.Setup(d => d.SetMembersAsync("MATCH-PLAYERS:[\"8002\"]", CommandFlags.None))
            .ReturnsAsync([(RedisValue)"PlayerBob"]);
        mockDb.Setup(d => d.HashSetAsync("MATCH-SERVERS", It.IsAny<HashEntry[]>(), CommandFlags.None))
            .Callback<RedisKey, HashEntry[], CommandFlags>((_, entries, _) =>
            {
                foreach (HashEntry entry in entries)
                {
                    matchServersHash[entry.Name.ToString()] = entry.Value;
                }
            })
            .Returns(Task.CompletedTask);

        StartMatchHandler handler = new(mockDb.Object, NullLogger<StartMatchHandler>.Instance);

        int reclaimed = await handler.ReclaimEmptyHostedLobbiesForPlayerAsync("PlayerAlice", currentServerId: 99);
        reclaimed.Should().Be(1);

        MatchServer? server21 = MemoryPack.MemoryPackSerializer.Deserialize<MatchServer>((byte[])matchServersHash["DedicatedHost:11236"]!);
        server21!.Status.Should().Be(ServerStatus.SERVER_STATUS_IDLE);
        server21.Name.Should().Be("Slave1002");

        MatchServer? server22 = MemoryPack.MemoryPackSerializer.Deserialize<MatchServer>((byte[])matchServersHash["DedicatedHost:11237"]!);
        server22!.Status.Should().Be(ServerStatus.SERVER_STATUS_LOADING);
        server22.Name.Should().Be("PopulatedCaldavar");
    }
}
