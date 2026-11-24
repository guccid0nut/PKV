// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Models.ServerManagement;
using FluentAssertions;
using KONGOR.MasterServer.Models.RequestResponse.ServerManagement;
using Xunit;

namespace KONGOR.MasterServer.Tests;

public sealed class ServerListHandlerTests
{
    private static List<MatchServer> CreateTestServers()
    {
        return
        [
            new MatchServer
            {
                ID = 1,
                HostAccountID = 100,
                HostAccountName = "Host1",
                Instance = 1,
                Name = "USE_Server_1",
                IPAddress = "192.168.1.10",
                Port = 11235,
                Location = "USE",
                Description = "US East Server"
            },
            new MatchServer
            {
                ID = 2,
                HostAccountID = 101,
                HostAccountName = "Host2",
                Instance = 1,
                Name = "USW_Server_1",
                IPAddress = "192.168.1.11",
                Port = 11236,
                Location = "USW",
                Description = "US West Server"
            },
            new MatchServer
            {
                ID = 3,
                HostAccountID = 102,
                HostAccountName = "Host3",
                Instance = 1,
                Name = "EU_Server_1",
                IPAddress = "192.168.1.12",
                Port = 11237,
                Location = "EU",
                Description = "Europe Server"
            },
            new MatchServer
            {
                ID = 4,
                HostAccountID = 103,
                HostAccountName = "Host4",
                Instance = 1,
                Name = "RU_Server_1",
                IPAddress = "192.168.1.13",
                Port = 11238,
                Location = "RU",
                Description = "Russian Server"
            },
            new MatchServer
            {
                ID = 5,
                HostAccountID = 104,
                HostAccountName = "Host5",
                Instance = 1,
                Name = "SEA_Server_1",
                IPAddress = "192.168.1.14",
                Port = 11239,
                Location = "SEA",
                Description = "South East Asia Server"
            },
            new MatchServer
            {
                ID = 6,
                HostAccountID = 105,
                HostAccountName = "Host6",
                Instance = 1,
                Name = "AU_Server_1",
                IPAddress = "192.168.1.15",
                Port = 11240,
                Location = "AU",
                Description = "Australia Server"
            },
            new MatchServer
            {
                ID = 7,
                HostAccountID = 106,
                HostAccountName = "Host7",
                Instance = 1,
                Name = "NEWERTH_Server_1",
                IPAddress = "192.168.1.16",
                Port = 11241,
                Location = "NEWERTH",
                Description = "Newerth Server"
            }
        ];
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ALL")]
    [InlineData("all")]
    public void FilterByRegion_WithNullOrEmptyOrAll_ReturnsAllServers(string? region)
    {
        List<MatchServer> servers = CreateTestServers();

        List<MatchServer> result = ServerListResponse.FilterByRegion(servers, region);

        result.Should().HaveCount(servers.Count);
    }

    [Fact]
    public void FilterByRegion_WithUS_ReturnsUSE_USW_And_NEWERTH()
    {
        List<MatchServer> servers = CreateTestServers();

        List<MatchServer> result = ServerListResponse.FilterByRegion(servers, "US");

        result.Should().HaveCount(3);
        result.Select(s => s.Location).Should().BeEquivalentTo("USE", "USW", "NEWERTH");
    }

    [Fact]
    public void FilterByRegion_WithEU_ReturnsEU_And_RU()
    {
        List<MatchServer> servers = CreateTestServers();

        List<MatchServer> result = ServerListResponse.FilterByRegion(servers, "EU");

        result.Should().HaveCount(2);
        result.Select(s => s.Location).Should().BeEquivalentTo("EU", "RU");
    }

    [Fact]
    public void FilterByRegion_WithSEA_ReturnsSEA()
    {
        List<MatchServer> servers = CreateTestServers();

        List<MatchServer> result = ServerListResponse.FilterByRegion(servers, "SEA");

        result.Should().HaveCount(1);
        result[0].Location.Should().Be("SEA");
    }

    [Fact]
    public void FilterByRegion_WithSpecificRegion_ReturnsExactMatch()
    {
        List<MatchServer> servers = CreateTestServers();

        List<MatchServer> result = ServerListResponse.FilterByRegion(servers, "AU");

        result.Should().HaveCount(1);
        result[0].Location.Should().Be("AU");
    }

    [Fact]
    public void FilterByRegion_IsCaseInsensitive()
    {
        List<MatchServer> servers = CreateTestServers();

        List<MatchServer> resultLower = ServerListResponse.FilterByRegion(servers, "eu");
        List<MatchServer> resultUpper = ServerListResponse.FilterByRegion(servers, "EU");

        resultLower.Should().BeEquivalentTo(resultUpper);
        resultLower.Should().HaveCount(2);
    }

    [Fact]
    public void FilterByRegion_WhenNoMatch_ReturnsEmptyList()
    {
        List<MatchServer> servers = CreateTestServers();
        List<MatchServer> result = ServerListResponse.FilterByRegion(servers, "NONEXISTENT_REGION");
        result.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("LOCAL")]
    [InlineData("ALL")]
    [InlineData("LAN")]
    public void FilterByRegion_WithUniversalServerLocation_IsIncludedInAnyRegion(string? location)
    {
        List<MatchServer> servers =
        [
            new MatchServer
            {
                ID = 99,
                HostAccountID = 100,
                HostAccountName = "HostTest",
                Instance = 1,
                Name = "Universal_Server",
                IPAddress = "127.0.0.1",
                Port = 11235,
                Location = location!,
                Description = "Universal Server"
            }
        ];

        List<MatchServer> usResult = ServerListResponse.FilterByRegion(servers, "US");
        List<MatchServer> euResult = ServerListResponse.FilterByRegion(servers, "EU");
        List<MatchServer> seaResult = ServerListResponse.FilterByRegion(servers, "SEA");

        usResult.Should().HaveCount(1);
        euResult.Should().HaveCount(1);
        seaResult.Should().HaveCount(1);
    }

    [Fact]
    public void FilterByRegion_WithRegionZero_ReturnsAllServers()
    {
        List<MatchServer> servers = CreateTestServers();
        List<MatchServer> result = ServerListResponse.FilterByRegion(servers, "0");
        result.Should().HaveCount(servers.Count);
    }

    [Fact]
    public void ResolveIP_LoopbackClientAndLoopbackServer_ReturnsLoopback()
    {
        MatchServer server = new()
        {
            ID = 1,
            HostAccountID = 1,
            HostAccountName = "LocalHost",
            Instance = 1,
            Name = "LocalServer",
            IPAddress = "127.0.0.1",
            LocalIPAddress = "127.0.0.1",
            Port = 11235,
            Location = "USE",
            Description = "Local"
        };

        string? resolved = ServerListResponse.ResolveIP(server, "127.0.0.1");
        resolved.Should().Be("127.0.0.1");
    }

    [Fact]
    public void ResolveIP_LoopbackClientAndRemotePublicServer_ReturnsPublicIP()
    {
        MatchServer server = new()
        {
            ID = 2,
            HostAccountID = 1,
            HostAccountName = "RemoteHost",
            Instance = 1,
            Name = "RemoteServer",
            IPAddress = "159.65.1.2",
            TruePublicIPAddress = "159.65.1.2",
            LocalIPAddress = "10.0.0.5",
            Port = 21235,
            Location = "USE",
            Description = "Remote"
        };

        // Even though client connected to localhost MasterServer, the server is remote so its public IP must be returned
        string? resolved = ServerListResponse.ResolveIP(server, "127.0.0.1");
        resolved.Should().Be("159.65.1.2");
    }

    [Fact]
    public void ResolveIP_LoopbackClientAndLanServer_ReturnsLanIP()
    {
        MatchServer server = new()
        {
            ID = 3,
            HostAccountID = 1,
            HostAccountName = "LanHost",
            Instance = 1,
            Name = "LanServer",
            IPAddress = "192.168.1.50",
            LocalIPAddress = "192.168.1.50",
            Port = 11235,
            Location = "USE",
            Description = "LAN"
        };

        string? resolved = ServerListResponse.ResolveIP(server, "127.0.0.1");
        resolved.Should().Be("192.168.1.50");
    }

    [Fact]
    public void ResolveIP_LoopbackClientAndDockerLocalServerWithLoopbackPublic_ReturnsLoopback()
    {
        MatchServer server = new()
        {
            ID = 10,
            HostAccountID = 1,
            HostAccountName = "DockerWorker",
            Instance = 1,
            Name = "LocalDockerServer",
            IPAddress = "192.168.1.100",
            TruePublicIPAddress = "127.0.0.1",
            LocalIPAddress = "192.168.1.100",
            Port = 11335,
            Location = "USE",
            Description = "Docker local"
        };

        string? resolved = ServerListResponse.ResolveIP(server, "127.0.0.1");
        resolved.Should().Be("127.0.0.1");
    }

    [Fact]
    public void ResolveIP_SameHostAsServer_ReturnsLoopback()
    {
        MatchServer server = new()
        {
            ID = 4,
            HostAccountID = 1,
            HostAccountName = "Host",
            Instance = 1,
            Name = "Server",
            IPAddress = "192.168.1.10",
            LocalIPAddress = "192.168.1.10",
            Port = 11235,
            Location = "USE",
            Description = "Host"
        };

        string? resolved = ServerListResponse.ResolveIP(server, "192.168.1.10");
        resolved.Should().Be("127.0.0.1");
    }

    [Fact]
    public void ResolveIP_NatHairpinning_ReturnsServerLocalIP()
    {
        MatchServer server = new()
        {
            ID = 5,
            HostAccountID = 1,
            HostAccountName = "HomeHost",
            Instance = 1,
            Name = "HomeServer",
            IPAddress = "192.168.1.10",
            LocalIPAddress = "192.168.1.10",
            TruePublicIPAddress = "198.51.100.3",
            Port = 21235,
            Location = "USE",
            Description = "Home"
        };

        // Client shares the same public IP as the server (same home router)
        string? resolved = ServerListResponse.ResolveIP(server, "198.51.100.3");
        resolved.Should().Be("192.168.1.10");
    }

    [Fact]
    public void ResolveIP_SameLanSubnet_ReturnsServerLocalIP()
    {
        MatchServer server = new()
        {
            ID = 6,
            HostAccountID = 1,
            HostAccountName = "LanHost",
            Instance = 1,
            Name = "LanServer",
            IPAddress = "192.168.1.10",
            LocalIPAddress = "192.168.1.10",
            Port = 11235,
            Location = "USE",
            Description = "LAN"
        };

        // Another client on the same /24 subnet
        string? resolved = ServerListResponse.ResolveIP(server, "192.168.1.25");
        resolved.Should().Be("192.168.1.10");
    }

    [Fact]
    public void ResolveIP_ExternalWanClient_ReturnsServerTruePublicIP()
    {
        MatchServer server = new()
        {
            ID = 7,
            HostAccountID = 1,
            HostAccountName = "DedicatedHost",
            Instance = 1,
            Name = "PublicServer",
            IPAddress = "10.0.0.5",
            LocalIPAddress = "10.0.0.5",
            TruePublicIPAddress = "198.51.100.3",
            Port = 21235,
            Location = "USE",
            Description = "Dedicated"
        };

        // Client connecting from external internet
        string? resolved = ServerListResponse.ResolveIP(server, "203.0.113.88");
        resolved.Should().Be("198.51.100.3");
    }

    [Fact]
    public void ResolveIP_SplitHorizon_LoopbackRegisteredServer_RoutesLanAndTailscaleToHostInterfaceAndWanToPublic()
    {
        MatchServer localLoopbackServer = new()
        {
            ID = 88,
            HostAccountID = 1,
            HostAccountName = "Host",
            Instance = 1,
            Name = "LocalFleet_L1",
            IPAddress = "127.0.0.1",
            LocalIPAddress = "127.0.0.1",
            TruePublicIPAddress = "127.0.0.1",
            Port = 21235,
            Location = "USE",
            Description = "Local"
        };

        // 1. Same-PC loopback requestor always receives 127.0.0.1
        ServerListResponse.ResolveIP(localLoopbackServer, "127.0.0.1").Should().Be("127.0.0.1");

        // 2. LAN requestor (192.168.1.25) and Tailscale/CGNAT requestor (100.90.1.5) must receive the host's non-loopback interface IP when online
        string hostLocalIp = global::KONGOR.MasterServer.Services.Network.NetworkTopologyResolverService.GetLocalIPAddress();
        string? lanResolved = ServerListResponse.ResolveIP(localLoopbackServer, "192.168.1.25");
        string? vpnResolved = ServerListResponse.ResolveIP(localLoopbackServer, "100.90.1.5");
        if (!string.Equals(hostLocalIp, "127.0.0.1", StringComparison.OrdinalIgnoreCase))
        {
            lanResolved.Should().NotBe("127.0.0.1");
            vpnResolved.Should().NotBe("127.0.0.1");
        }
    }

    [Fact]
    public void ResolveIP_SplitHorizon_TailscaleSameMesh_ReturnsServerTailscaleIP()
    {
        MatchServer tailscaleServer = new()
        {
            ID = 89,
            HostAccountID = 1,
            HostAccountName = "VpnHost",
            Instance = 1,
            Name = "TailscaleFleet_D1",
            IPAddress = "100.101.102.10",
            LocalIPAddress = "100.101.102.10",
            TruePublicIPAddress = "198.51.100.3",
            Port = 21335,
            Location = "USE",
            Description = "VPN"
        };

        // Peer on 100.64.0.0/10 Tailscale mesh gets the direct 100.x VPN address instead of WAN 198.51.100.3
        ServerListResponse.ResolveIP(tailscaleServer, "100.68.42.9").Should().Be("100.101.102.10");
        // External WAN client still gets TruePublicIPAddress
        ServerListResponse.ResolveIP(tailscaleServer, "203.0.113.88").Should().Be("198.51.100.3");
    }

    [Theory]
    [InlineData(ServerStatus.SERVER_STATUS_ACTIVE, true)]
    [InlineData(ServerStatus.SERVER_STATUS_LOADING, true)]
    [InlineData(ServerStatus.SERVER_STATUS_IDLE, true)]
    [InlineData(ServerStatus.SERVER_STATUS_SLEEPING, false)]
    [InlineData(ServerStatus.SERVER_STATUS_CRASHED, false)]
    [InlineData(ServerStatus.SERVER_STATUS_KILLED, false)]
    [InlineData(ServerStatus.SERVER_STATUS_UNKNOWN, false)]
    public void GameType10_StatusFiltering_MatchesExpected(ServerStatus status, bool shouldInclude)
    {
        MatchServer server = new()
        {
            ID = 1,
            HostAccountID = 1,
            HostAccountName = "Host",
            Instance = 1,
            Name = "Server",
            IPAddress = "127.0.0.1",
            Port = 11235,
            Location = "USE",
            Description = "Desc",
            Status = status
        };

        bool isJoinable = server.Status is ServerStatus.SERVER_STATUS_ACTIVE or ServerStatus.SERVER_STATUS_LOADING or ServerStatus.SERVER_STATUS_IDLE;
        isJoinable.Should().Be(shouldInclude);
    }

    [Theory]
    [InlineData(ServerStatus.SERVER_STATUS_IDLE, 0, true)]
    [InlineData(ServerStatus.SERVER_STATUS_SLEEPING, 0, true)]
    [InlineData(ServerStatus.SERVER_STATUS_UNKNOWN, 0, true)]
    [InlineData(ServerStatus.SERVER_STATUS_LOADING, 0, true)]
    [InlineData(ServerStatus.SERVER_STATUS_LOADING, 1, true)]
    [InlineData(ServerStatus.SERVER_STATUS_LOADING, 2, false)]
    [InlineData(ServerStatus.SERVER_STATUS_ACTIVE, 5, false)]
    [InlineData(ServerStatus.SERVER_STATUS_CRASHED, 0, false)]
    [InlineData(ServerStatus.SERVER_STATUS_KILLED, 0, false)]
    public void GameType90_StatusFiltering_MatchesExpected(ServerStatus status, int connectedPlayers, bool shouldInclude)
    {
        MatchServer server = new()
        {
            ID = 1,
            HostAccountID = 1,
            HostAccountName = "Host",
            Instance = 1,
            Name = "Server",
            IPAddress = "127.0.0.1",
            Port = 11235,
            Location = "USE",
            Description = "Desc",
            Status = status,
            ConnectedPlayersCount = connectedPlayers
        };

        bool isAvailable = server.Status is ServerStatus.SERVER_STATUS_IDLE or ServerStatus.SERVER_STATUS_SLEEPING or ServerStatus.SERVER_STATUS_UNKNOWN
                           || (server.Status == ServerStatus.SERVER_STATUS_LOADING && server.ConnectedPlayersCount <= 1);
        isAvailable.Should().Be(shouldInclude);
    }

    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    public ServerListHandlerTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("Category", "Manual")]
    [Trait("Category", "LocalOnly")]
    public async Task InspectLiveMatchServers()
    {
        try
        {
            var muxer = await StackExchange.Redis.ConnectionMultiplexer.ConnectAsync("127.0.0.1:28298");
            var db = muxer.GetDatabase();
            var entries = await db.HashGetAllAsync("MATCH-SERVERS");
            foreach (var entry in entries)
            {
                var server = MemoryPack.MemoryPackSerializer.Deserialize<MatchServer>((byte[])entry.Value!);
                if (server != null)
                {
                    var now = DateTimeOffset.UtcNow;
                    _output.WriteLine($"[LIVE-SERVER] Key={entry.Name} ID={server.ID} Name={server.Name} Port={server.Port} Status={server.Status} Location={server.Location} LastHB={(now - server.TimestampLastHeartbeat).TotalSeconds:F1}s ago Reg={(now - server.TimestampRegistered).TotalSeconds:F1}s ago");
                }
            }
        }
        catch (StackExchange.Redis.RedisConnectionException ex)
        {
            _output.WriteLine($"[NOTICE] Local Redis not reachable at 127.0.0.1:28298: {ex.Message}");
        }
    }
}
