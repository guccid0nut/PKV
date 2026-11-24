// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text;
using System.Text.Json;
using ASPIRE.Common.Enumerations;
using ASPIRE.Common.Models.ServerManagement;
using FluentAssertions;
using KONGOR.MasterServer.Extensions.Cache;
using KONGOR.MasterServer.Serialization;
using MemoryPack;
using Moq;
using StackExchange.Redis;
using Xunit;

namespace KONGOR.MasterServer.Tests;

public sealed class MatchmakingMemoryPackCacheTests
{
    [Fact]
    public void MatchServerManager_MemoryPack_Roundtrip_PreservesAllFields()
    {
        MatchServerManager original = new()
        {
            HostAccountID = 1001,
            HostAccountName = "ServerHostAdmin",
            ID = 42,
            IPAddress = "192.168.1.50",
            MatchServerIDs = [101, 102, 103],
            Cookie = Guid.NewGuid().ToString(),
            TimestampRegistered = DateTimeOffset.UtcNow
        };

        byte[] bytes = MemoryPackSerializer.Serialize(original);
        bytes.Should().NotBeNullOrEmpty();
        // MemoryPack should not start with '{' (JSON)
        bytes[0].Should().NotBe((byte)'{');

        MatchServerManager? deserialized = MemoryPackSerializer.Deserialize<MatchServerManager>(bytes);
        deserialized.Should().NotBeNull();
        deserialized!.HostAccountID.Should().Be(original.HostAccountID);
        deserialized.HostAccountName.Should().Be(original.HostAccountName);
        deserialized.ID.Should().Be(original.ID);
        deserialized.IPAddress.Should().Be(original.IPAddress);
        deserialized.MatchServerIDs.Should().Equal(original.MatchServerIDs);
        deserialized.Cookie.Should().Be(original.Cookie);
        deserialized.TimestampRegistered.Should().BeCloseTo(original.TimestampRegistered, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void MatchServer_MemoryPack_Roundtrip_PreservesAllFields()
    {
        MatchServer original = new()
        {
            HostAccountID = 2002,
            HostAccountName = "DedicatedCluster01",
            ID = 777,
            Name = "USE_US_East_Public_01",
            MatchServerManagerID = 42,
            Instance = 1,
            IPAddress = "10.0.0.15",
            TruePublicIPAddress = "54.210.10.20",
            LocalIPAddress = "127.0.0.1",
            Port = 11235,
            LocalPort = 11235,
            Location = "us-east",
            Description = "Standard Competitive 5v5 Caldavar",
            IsPrivate = false,
            Status = ServerStatus.SERVER_STATUS_ACTIVE,
            Cookie = Guid.NewGuid().ToString(),
            TimestampRegistered = DateTimeOffset.UtcNow
        };

        byte[] bytes = MemoryPackSerializer.Serialize(original);
        bytes.Should().NotBeNullOrEmpty();
        bytes[0].Should().NotBe((byte)'{');

        MatchServer? deserialized = MemoryPackSerializer.Deserialize<MatchServer>(bytes);
        deserialized.Should().NotBeNull();
        deserialized!.HostAccountID.Should().Be(original.HostAccountID);
        deserialized.HostAccountName.Should().Be(original.HostAccountName);
        deserialized.ID.Should().Be(original.ID);
        deserialized.Name.Should().Be(original.Name);
        deserialized.MatchServerManagerID.Should().Be(original.MatchServerManagerID);
        deserialized.Instance.Should().Be(original.Instance);
        deserialized.IPAddress.Should().Be(original.IPAddress);
        deserialized.TruePublicIPAddress.Should().Be(original.TruePublicIPAddress);
        deserialized.LocalIPAddress.Should().Be(original.LocalIPAddress);
        deserialized.Port.Should().Be(original.Port);
        deserialized.LocalPort.Should().Be(original.LocalPort);
        deserialized.Location.Should().Be(original.Location);
        deserialized.Description.Should().Be(original.Description);
        deserialized.IsPrivate.Should().Be(original.IsPrivate);
        deserialized.Status.Should().Be(original.Status);
        deserialized.Cookie.Should().Be(original.Cookie);
        deserialized.TimestampRegistered.Should().BeCloseTo(original.TimestampRegistered, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void MatchStartData_MemoryPack_Roundtrip_PreservesAllFields()
    {
        MatchStartData original = new()
        {
            MatchID = 99999,
            MatchName = "GrandFinals_Game3",
            ServerID = 777,
            ServerName = "TournamentServerA",
            HostAccountName = "TournamentDirector",
            Map = "caldavar",
            Version = "4.10.1",
            IsCasual = false,
            MatchType = 1,
            MatchMode = "ranked",
            TimestampStarted = DateTimeOffset.UtcNow,
            ConnectedPlayersCount = 10,
            MaximumPlayersCount = 10,
            Tier = 3,
            League = 2,
            Options = MatchOptions.CasualMode,
            IsStarted = true,
            Phase = 4
        };

        byte[] bytes = MemoryPackSerializer.Serialize(original);
        bytes.Should().NotBeNullOrEmpty();
        bytes[0].Should().NotBe((byte)'{');

        MatchStartData? deserialized = MemoryPackSerializer.Deserialize<MatchStartData>(bytes);
        deserialized.Should().NotBeNull();
        deserialized!.MatchID.Should().Be(original.MatchID);
        deserialized.MatchName.Should().Be(original.MatchName);
        deserialized.ServerID.Should().Be(original.ServerID);
        deserialized.ServerName.Should().Be(original.ServerName);
        deserialized.HostAccountName.Should().Be(original.HostAccountName);
        deserialized.Map.Should().Be(original.Map);
        deserialized.Version.Should().Be(original.Version);
        deserialized.IsCasual.Should().Be(original.IsCasual);
        deserialized.MatchType.Should().Be(original.MatchType);
        deserialized.MatchMode.Should().Be(original.MatchMode);
        deserialized.ConnectedPlayersCount.Should().Be(original.ConnectedPlayersCount);
        deserialized.MaximumPlayersCount.Should().Be(original.MaximumPlayersCount);
        deserialized.Tier.Should().Be(original.Tier);
        deserialized.League.Should().Be(original.League);
        deserialized.Options.Should().Be(original.Options);
        deserialized.IsStarted.Should().Be(original.IsStarted);
        deserialized.Phase.Should().Be(original.Phase);
        deserialized.TimestampStarted.Should().BeCloseTo(original.TimestampStarted, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task DistributedCache_GetMatchServerManagers_DualFormat_HandlesMemoryPackAndLegacyJson()
    {
        // Arrange
        MatchServerManager modernManager = new()
        {
            HostAccountID = 100,
            HostAccountName = "ModernHost",
            ID = 1,
            IPAddress = "10.0.0.1",
            MatchServerIDs = [1, 2]
        };
        byte[] memoryPackBytes = MemoryPackSerializer.Serialize(modernManager);

        MatchServerManager legacyManager = new()
        {
            HostAccountID = 200,
            HostAccountName = "LegacyHost",
            ID = 2,
            IPAddress = "10.0.0.2",
            MatchServerIDs = [3, 4]
        };
        byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(legacyManager, MasterServerJsonContext.Default.MatchServerManager);

        // Verify JSON bytes start with '{' and MemoryPack does not
        jsonBytes[0].Should().Be((byte)'{');
        memoryPackBytes[0].Should().NotBe((byte)'{');

        Mock<IDatabase> mockDb = new();
        mockDb.Setup(db => db.HashGetAllAsync("MATCH-SERVER-MANAGERS", CommandFlags.None))
            .ReturnsAsync([
                new HashEntry("ModernHost", memoryPackBytes),
                new HashEntry("LegacyHost", jsonBytes)
            ]);

        // Act
        List<MatchServerManager> result = await mockDb.Object.GetMatchServerManagers();

        // Assert
        result.Should().HaveCount(2);
        MatchServerManager retrievedModern = result.Single(m => m.HostAccountName == "ModernHost");
        retrievedModern.ID.Should().Be(1);
        retrievedModern.IPAddress.Should().Be("10.0.0.1");

        MatchServerManager retrievedLegacy = result.Single(m => m.HostAccountName == "LegacyHost");
        retrievedLegacy.ID.Should().Be(2);
        retrievedLegacy.IPAddress.Should().Be("10.0.0.2");
    }

    [Fact]
    public async Task DistributedCache_GetMatchServers_DualFormat_HandlesMemoryPackAndLegacyJson()
    {
        // Arrange
        MatchServer modernServer = new()
        {
            HostAccountID = 10,
            HostAccountName = "Host1",
            ID = 101,
            Name = "ModernServer",
            Instance = 1,
            IPAddress = "127.0.0.1",
            Port = 11235,
            Location = "USE",
            Description = "Modern MemoryPack Server"
        };
        byte[] memoryPackBytes = MemoryPackSerializer.Serialize(modernServer);

        MatchServer legacyServer = new()
        {
            HostAccountID = 20,
            HostAccountName = "Host2",
            ID = 102,
            Name = "LegacyServer",
            Instance = 2,
            IPAddress = "127.0.0.1",
            Port = 11236,
            Location = "EUW",
            Description = "Legacy JSON Server"
        };
        byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(legacyServer, MasterServerJsonContext.Default.MatchServer);

        Mock<IDatabase> mockDb = new();
        mockDb.Setup(db => db.HashGetAllAsync("MATCH-SERVERS", CommandFlags.None))
            .ReturnsAsync([
                new HashEntry("101", memoryPackBytes),
                new HashEntry("102", jsonBytes)
            ]);

        // Act
        List<MatchServer> result = await mockDb.Object.GetMatchServers();

        // Assert
        result.Should().HaveCount(2);
        result.Single(s => s.ID == 101).Name.Should().Be("ModernServer");
        result.Single(s => s.ID == 102).Name.Should().Be("LegacyServer");
    }

    [Fact]
    public async Task DistributedCache_GetMatchServerManagers_CorruptedBytes_DoesNotThrow_AndReturnsValidEntries()
    {
        // Arrange: valid entry + corrupted entry starting with 0x07 (binary header byte) followed by garbage
        MatchServerManager validManager = new()
        {
            HostAccountID = 300,
            HostAccountName = "HealthyHost",
            ID = 3,
            IPAddress = "10.0.0.3",
            MatchServerIDs = [5]
        };
        byte[] validBytes = MemoryPackSerializer.Serialize(validManager);
        byte[] corruptedBytes = [0x07, 0xFF, 0xFE, 0xFD, 0xFC, 0x00, 0x01]; // Starts with 0x07, corrupted payload

        Mock<IDatabase> mockDb = new();
        mockDb.Setup(db => db.HashGetAllAsync("MATCH-SERVER-MANAGERS", CommandFlags.None))
            .ReturnsAsync([
                new HashEntry("HealthyHost", validBytes),
                new HashEntry("CorruptHost", corruptedBytes)
            ]);

        // Act
        List<MatchServerManager> result = await mockDb.Object.GetMatchServerManagers();

        // Assert: HealthyHost survived, corrupted entry safely ignored
        result.Should().HaveCount(1);
        result[0].HostAccountName.Should().Be("HealthyHost");
        result[0].ID.Should().Be(3);
    }

    [Fact]
    public async Task DistributedCache_GetMatchServerManagersByAccountName_CorruptedBytes_DoesNotThrow()
    {
        // Arrange
        byte[] corruptedBytes = [0x07, 0x01, 0x02, 0x03];

        Mock<IDatabase> mockDb = new();
        mockDb.Setup(db => db.HashScanAsync(
                "MATCH-SERVER-MANAGERS",
                It.IsAny<RedisValue>(),
                It.IsAny<int>(),
                It.IsAny<long>(),
                It.IsAny<int>(),
                It.IsAny<CommandFlags>()))
            .Returns(ToAsyncEnumerable(new HashEntry("CorruptHost", corruptedBytes)));

        // Act
        List<MatchServerManager> result = await mockDb.Object.GetMatchServerManagersByAccountName("CorruptHost");

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task DistributedCache_GetMatchServers_CorruptedBytes_DoesNotThrow_AndReturnsValidEntries()
    {
        // Arrange
        MatchServer validServer = new()
        {
            HostAccountID = 40,
            HostAccountName = "HealthyHost",
            ID = 201,
            Name = "HealthyServer",
            Instance = 1,
            IPAddress = "127.0.0.1",
            Port = 11235,
            Location = "USE",
            Description = "Healthy Test Server"
        };
        byte[] validBytes = MemoryPackSerializer.Serialize(validServer);
        byte[] corruptedBytes = [0x07, 0xDE, 0xAD, 0xBE, 0xEF];

        Mock<IDatabase> mockDb = new();
        mockDb.Setup(db => db.HashGetAllAsync("MATCH-SERVERS", CommandFlags.None))
            .ReturnsAsync([
                new HashEntry("201", validBytes),
                new HashEntry("202", corruptedBytes)
            ]);

        // Act
        List<MatchServer> result = await mockDb.Object.GetMatchServers();

        // Assert
        result.Should().HaveCount(1);
        result[0].ID.Should().Be(201);
        result[0].Name.Should().Be("HealthyServer");
    }

    [Fact]
    public async Task DistributedCache_GetMatchServersByAccountName_CorruptedBytes_DoesNotThrow()
    {
        // Arrange
        byte[] corruptedBytes = [0x07, 0x00, 0x00, 0x00];

        Mock<IDatabase> mockDb = new();
        mockDb.Setup(db => db.HashScanAsync(
                "MATCH-SERVERS",
                It.IsAny<RedisValue>(),
                It.IsAny<int>(),
                It.IsAny<long>(),
                It.IsAny<int>(),
                It.IsAny<CommandFlags>()))
            .Returns(ToAsyncEnumerable(new HashEntry("999", corruptedBytes)));

        // Act
        List<MatchServer> result = await mockDb.Object.GetMatchServersByAccountName("AnyHost");

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task DistributedCache_SetMatchServer_IndexesInMatchServerSessions()
    {
        MatchServer server = new()
        {
            HostAccountID = 50,
            HostAccountName = "FlexHost",
            ID = 301,
            Name = "FlexServer",
            Instance = 1,
            IPAddress = "127.0.0.1",
            Location = "USE",
            Description = "Flex Test",
            Port = 11235,
            Cookie = "test-session-cookie-123",
            TimestampRegistered = DateTimeOffset.UtcNow,
            TimestampLastHeartbeat = DateTimeOffset.UtcNow
        };

        Mock<IDatabase> mockDb = new();
        mockDb.Setup(db => db.HashSetAsync("MATCH-SERVERS", It.IsAny<HashEntry[]>(), CommandFlags.None))
            .Returns(Task.CompletedTask);
        mockDb.Setup(db => db.HashSetAsync("MATCH-SERVER-INDEX", It.IsAny<HashEntry[]>(), CommandFlags.None))
            .Returns(Task.CompletedTask);
        mockDb.Setup(db => db.HashSetAsync("MATCH-SERVER-SESSIONS", It.IsAny<HashEntry[]>(), CommandFlags.None))
            .Returns(Task.CompletedTask);

        await mockDb.Object.SetMatchServer("FlexHost", server);

        mockDb.Verify(db => db.HashSetAsync("MATCH-SERVER-SESSIONS",
            It.Is<HashEntry[]>(entries => entries.Length == 1 && entries[0].Name == "test-session-cookie-123"),
            CommandFlags.None), Times.Once);
    }

    [Fact]
    public async Task DistributedCache_GetMatchServerBySessionCookie_StaleInActiveList_ResurrectsFromSessionsIndex()
    {
        MatchServer server = new()
        {
            HostAccountID = 50,
            HostAccountName = "FlexHost",
            ID = 302,
            Name = "ResurrectedServer",
            Instance = 1,
            IPAddress = "127.0.0.1",
            Location = "USE",
            Description = "Flex Test",
            Port = 11236,
            Cookie = "stale-session-cookie-456",
            // Stale: last heartbeat was 5 minutes ago (exceeds 2-minute active threshold, but below 30-minute dead threshold)
            TimestampRegistered = DateTimeOffset.UtcNow.AddMinutes(-5),
            TimestampLastHeartbeat = DateTimeOffset.UtcNow.AddMinutes(-5)
        };
        byte[] serialized = MemoryPackSerializer.Serialize(server);

        Mock<IDatabase> mockDb = new();
        // Active list returns empty because server is stale
        mockDb.Setup(db => db.HashGetAllAsync("MATCH-SERVERS", CommandFlags.None))
            .ReturnsAsync([new HashEntry("FlexHost:11236", serialized)]);
        // Session lookup finds the server
        mockDb.Setup(db => db.HashGetAsync("MATCH-SERVER-SESSIONS", "stale-session-cookie-456", CommandFlags.None))
            .ReturnsAsync(serialized);

        MatchServer? result = await mockDb.Object.GetMatchServerBySessionCookie("stale-session-cookie-456");

        result.Should().NotBeNull();
        result!.ID.Should().Be(302);
        result.Name.Should().Be("ResurrectedServer");
    }

    [Fact]
    public async Task DistributedCache_GetMatchServerBySessionCookie_FastPathHitsIndexWithoutFullScan()
    {
        MatchServer server = new()
        {
            HostAccountID = 99,
            HostAccountName = "FastHost",
            ID = 505,
            Name = "FastServer",
            Instance = 1,
            IPAddress = "127.0.0.1",
            Port = 11235,
            Location = "USE",
            Description = "Fast Test",
            Cookie = "fast-session-cookie-789"
        };
        byte[] serialized = MemoryPackSerializer.Serialize(server);

        Mock<IDatabase> mockDb = new();
        mockDb.Setup(db => db.HashGetAsync("MATCH-SERVER-SESSIONS", "fast-session-cookie-789", CommandFlags.None))
            .ReturnsAsync(serialized);

        MatchServer? result = await mockDb.Object.GetMatchServerBySessionCookie("fast-session-cookie-789");

        result.Should().NotBeNull();
        result!.ID.Should().Be(505);
        result.Cookie.Should().Be("fast-session-cookie-789");

        // Verify O(1) efficiency: HashGetAllAsync on MATCH-SERVERS was NEVER called
        mockDb.Verify(db => db.HashGetAllAsync("MATCH-SERVERS", It.IsAny<CommandFlags>()), Times.Never);
    }

    [Fact]
    public async Task DistributedCache_RemoveMatchServerByID_RemovesFromMatchServerSessions()
    {
        MatchServer server = new()
        {
            HostAccountID = 50,
            HostAccountName = "FlexHost",
            ID = 303,
            Name = "ToRemoveServer",
            Instance = 1,
            IPAddress = "127.0.0.1",
            Location = "USE",
            Description = "Flex Test",
            Port = 11237,
            Cookie = "cookie-to-remove-789",
            TimestampRegistered = DateTimeOffset.UtcNow,
            TimestampLastHeartbeat = DateTimeOffset.UtcNow
        };
        byte[] serialized = MemoryPackSerializer.Serialize(server);

        Mock<IDatabase> mockDb = new();
        mockDb.Setup(db => db.HashGetAsync("MATCH-SERVER-INDEX", 303, CommandFlags.None))
            .ReturnsAsync("FlexHost:11237");
        mockDb.Setup(db => db.HashGetAsync("MATCH-SERVERS", "FlexHost:11237", CommandFlags.None))
            .ReturnsAsync(serialized);
        mockDb.Setup(db => db.HashDeleteAsync("MATCH-SERVER-INDEX", 303, CommandFlags.None))
            .ReturnsAsync(true);
        mockDb.Setup(db => db.HashDeleteAsync("MATCH-SERVERS", "FlexHost:11237", CommandFlags.None))
            .ReturnsAsync(true);
        mockDb.Setup(db => db.HashDeleteAsync("MATCH-SERVER-SESSIONS", "cookie-to-remove-789", CommandFlags.None))
            .ReturnsAsync(true);
        mockDb.Setup(db => db.HashGetAllAsync("MATCH-SERVER-MANAGERS", CommandFlags.None))
            .ReturnsAsync([]);

        await mockDb.Object.RemoveMatchServerByID(303);

        mockDb.Verify(db => db.HashDeleteAsync("MATCH-SERVER-SESSIONS", "cookie-to-remove-789", CommandFlags.None),
            Times.Once);
    }

    [Fact]
    public async Task DistributedCache_GetMatchServerBySessionCookie_IndexMiss_ScansOnceAndBackfillsIndex()
    {
        MatchServer server = new()
        {
            HostAccountID = 77,
            HostAccountName = "MissHost",
            ID = 606,
            Name = "MissServer",
            Instance = 1,
            IPAddress = "127.0.0.1",
            Port = 11238,
            Location = "USE",
            Description = "Miss Test",
            Cookie = "missing-from-index-cookie",
            TimestampRegistered = DateTimeOffset.UtcNow,
            TimestampLastHeartbeat = DateTimeOffset.UtcNow
        };
        byte[] serialized = MemoryPackSerializer.Serialize(server);

        Mock<IDatabase> mockDb = new();
        // Index miss (default RedisValue.Null), raw hash contains the server.
        mockDb.Setup(db => db.HashGetAllAsync("MATCH-SERVERS", CommandFlags.None))
            .ReturnsAsync([new HashEntry("MissHost:11238", serialized)]);
        mockDb.Setup(db => db.HashSetAsync("MATCH-SERVER-SESSIONS", It.IsAny<HashEntry[]>(), CommandFlags.None))
            .Returns(Task.CompletedTask);

        MatchServer? result = await mockDb.Object.GetMatchServerBySessionCookie("missing-from-index-cookie");

        result.Should().NotBeNull();
        result!.ID.Should().Be(606);

        // Exactly ONE full scan (previously the miss path issued two HGETALLs).
        mockDb.Verify(db => db.HashGetAllAsync("MATCH-SERVERS", It.IsAny<CommandFlags>()), Times.Once);

        // The index was backfilled so the next lookup is O(1).
        mockDb.Verify(db => db.HashSetAsync("MATCH-SERVER-SESSIONS",
            It.Is<HashEntry[]>(entries => entries.Length == 1 && entries[0].Name == "missing-from-index-cookie"),
            CommandFlags.None), Times.Once);
    }

    private static async IAsyncEnumerable<HashEntry> ToAsyncEnumerable(params HashEntry[] entries)
    {
        foreach (HashEntry entry in entries)
        {
            yield return entry;
        }
        await Task.Yield();
    }
}

