// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: CoopBotMatchmakingTests.cs
// System: TRANSMUTANSTEIN ChatServer Tests - Co-Op Bot Match Catalog & Wire-Packet Broker Verification
// Policy: Strict Explicit Typing (NO 'var')
//
// Value-Weighted Testing Gate:
//   protects: 19-map Co-Op allowlist parity, dynamic teamsize (3v3/4v4/5v5/10v10), 1:1 botdifficulty (1..3),
//             RandomizeBots ('Fill team with bots') ally bot math, DevourerBot single-hero rosters (omitting
//             bots1: when allyBots == 0), oversized-party rejection before server allocation, and Halloween
//             explicit map preservation across NET_CHAT_GS_CREATE_MATCH and NET_CHAT_CL_TMM_MATCH_FOUND_UPDATE.
//   fails_when: MatchmakingService reverts to hardcoded teamsize:5 / BotDifficulty-1, SeasonalMapResolver swaps
//               Co-Op 'caldavar' to 'caldavar_halloween', oversized parties allocate servers on 3v3/4v4 maps,
//               empty 'bots1:' corrupts single-hero bot vectors, or unvalidated map strings reach game servers.
//   seam: Real MatchmakingService.ProcessMatchForSimulationAsync + ConfirmMatch + binary ChatBuffer wire framing.
// ============================================================================

using System.Collections.Concurrent;
using System.Net;
using FluentAssertions;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Enumerations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NEXUS.Contracts.Chat;
using NEXUS.Contracts.Domain;
using TRANSMUTANSTEIN.ChatServer.Attributes;
using TRANSMUTANSTEIN.ChatServer.Domain.Communication;
using TRANSMUTANSTEIN.ChatServer.Domain.Core;
using TRANSMUTANSTEIN.ChatServer.Domain.Matchmaking;
using TRANSMUTANSTEIN.ChatServer.Internals;
using TRANSMUTANSTEIN.ChatServer.Services;
using TRANSMUTANSTEIN.ChatServer.Utilities;
using Xunit;
using DbRole = MERRICK.DatabaseContext.Entities.Utility.Role;

namespace TRANSMUTANSTEIN.ChatServer.Tests.Services;

public sealed class CoopBotMatchmakingTests : IDisposable
{
    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class SimulatedChatSession(TCPServer server, IServiceProvider serviceProvider)
        : ChatSession(server, serviceProvider)
    {
        private readonly object _packetLock = new();
        public List<byte[]> SentPackets { get; } = [];

        public override bool IsSessionConnected => true;

        public override bool SendAsync(ReadOnlySpan<byte> buffer)
        {
            byte[] copy = buffer.ToArray();
            lock (_packetLock)
            {
                SentPackets.Add(copy);
            }

            return true;
        }

        public List<byte[]> SnapshotPackets()
        {
            lock (_packetLock)
            {
                return [.. SentPackets];
            }
        }
    }

    private sealed record DecodedCreateMatch(
        byte MatchType,
        int MatchUpId,
        string MatchSettings,
        byte PlayerCount,
        IReadOnlyList<(int AccountId, byte Team, byte Slot)> Players);

    private sealed record DecodedMatchFound(
        string MapName,
        byte TeamSize,
        byte GameType);

    private readonly FixedTimeProvider _clock = new(new DateTimeOffset(2026, 10, 31, 20, 0, 0, TimeSpan.Zero));
    private readonly Mock<IChatContext> _mockChatContext = new();
    private readonly ConcurrentDictionary<string, ChatSession> _clientSessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, ChatSession> _matchServerSessions = new();
    private readonly ConcurrentDictionary<int, ChatSession> _managerSessions = new();
    private readonly ConcurrentDictionary<string, ChatChannel> _channelsByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, ChatChannel> _channelsById = new();
    private readonly FloodPreventionService _floodPrevention = new(NullLogger<FloodPreventionService>.Instance);
    private readonly TCPServer _tcpServer;
    private readonly ServiceProvider _serviceProvider;

    public CoopBotMatchmakingTests()
    {
        Log.Initialise(NullLogger.Instance);

        _mockChatContext.SetupGet(c => c.ClientChatSessions).Returns(_clientSessions);
        _mockChatContext.SetupGet(c => c.MatchServerChatSessions).Returns(_matchServerSessions);
        _mockChatContext.SetupGet(c => c.MatchServerManagerChatSessions).Returns(_managerSessions);
        _mockChatContext.SetupGet(c => c.ChatChannels).Returns(_channelsByName);
        _mockChatContext.SetupGet(c => c.ChatChannelsByID).Returns(_channelsById);

        ServiceCollection services = new();
        services.AddSingleton(_mockChatContext.Object);
        services.AddSingleton(Mock.Of<IDiscordPresencePublisherService>());
        services.AddSingleton(Mock.Of<IMatchmakingService>());
        services.AddSingleton(_floodPrevention);
        services.AddSingleton<ILogger<ChatSession>>(NullLogger<ChatSession>.Instance);
        _serviceProvider = services.BuildServiceProvider();

        _tcpServer = new TCPServer(IPAddress.Loopback, 0);
    }

    public void Dispose()
    {
        _floodPrevention.Dispose();
        _serviceProvider.Dispose();
    }

    [Fact]
    public void BotMatchMapCatalog_ContainsAll20Maps_AndFallsBackUnknownMapsToCaldavar()
    {
        string[] expectedIds =
        [
            "caldavar",
            "midwars",
            "team_deathmatch",
            "devowars",
            "devowars_10v10",
            "soccer",
            "midwars_10v10",
            "grimmscrossing",
            "thegrimmhunt",
            "darkwoodvale",
            "persian_pass",
            "watchtower",
            "three_kingdoms",
            "mids_omg",
            "caldavar_wtf",
            "caldavar_reborn",
            "midwars_reborn",
            "caldavar_old",
            "caldavar_halloween",
            "midwars_halloween"
        ];

        BotMatchMapCatalog.All.Select(m => m.Id).Should().Equal(expectedIds);
        BotMatchMapCatalog.SoccerBotPool.Should().HaveCount(29);
        BotMatchMapCatalog.ResolveOrFallback("soccer").TeamSize.Should().Be(5);
        BotMatchMapCatalog.ResolveOrFallback("soccer").CustomBotPool.Should().BeSameAs(BotMatchMapCatalog.SoccerBotPool);
        BotMatchMapCatalog.ResolveOrFallback("grimmscrossing").TeamSize.Should().Be(3);
        BotMatchMapCatalog.ResolveOrFallback("darkwoodvale").TeamSize.Should().Be(4);
        BotMatchMapCatalog.ResolveOrFallback("persian_pass").TeamSize.Should().Be(4);
        BotMatchMapCatalog.ResolveOrFallback("devowars").SingleHeroBot.Should().Be("DevourerBot");
        BotMatchMapCatalog.ResolveOrFallback("devowars_10v10").Should().Be(new BotMatchMapSpec("devowars_10v10", 10, "DevourerBot"));
        BotMatchMapCatalog.ResolveOrFallback("midwars_10v10").TeamSize.Should().Be(10);

        BotMatchMapCatalog.ResolveOrFallback("evil_dev_map").Id.Should().Be("caldavar");
        BotMatchMapCatalog.ResolveOrFallback("").Id.Should().Be("caldavar");
        BotMatchMapCatalog.ResolveOrFallback(null).Id.Should().Be("caldavar");
    }

    [Theory]
    [InlineData("grimmscrossing", 2, true, 3, "grimmscrossing", 3, "randombots:1|3", "botdifficulty:3")]
    [InlineData("darkwoodvale", 3, true, 2, "darkwoodvale", 4, "randombots:1|4", "botdifficulty:2")]
    [InlineData("persian_pass", 2, false, 1, "persian_pass", 4, "randombots:0|4", "botdifficulty:1")]
    [InlineData("caldavar", 2, false, 2, "caldavar", 5, "randombots:0|5", "botdifficulty:2")]
    [InlineData("midwars_10v10", 4, true, 3, "midwars_10v10", 10, "randombots:6|10", "botdifficulty:3")]
    [InlineData("mids_omg", 2, true, 2, "mids_omg", 5, "randombots:3|5", "botdifficulty:2")]
    [InlineData("midwars_halloween", 3, true, 1, "midwars_halloween", 5, "randombots:2|5", "botdifficulty:1")]
    [InlineData("evil_dev_map", 2, true, 2, "caldavar", 5, "randombots:3|5", "botdifficulty:2")]
    public async Task CoopBroker_StandardAndCustomMaps_EmitDynamicTeamSizeBotCountsDifficulty_AndConfirmMatchPacket(
        string requestedMap,
        int humanCount,
        bool fillTeamWithBots,
        byte botDifficulty,
        string expectedMap,
        byte expectedTeamSize,
        string expectedBotToken,
        string expectedDifficultyToken)
    {
        IConfiguration config = BuildConfig(forceSeason: "halloween");
        using MatchmakingService service = new(config, _mockChatContext.Object, timeProvider: _clock);
        SimulatedChatSession server = RegisterIdleGameServer(serverId: 501, location: "USW");
        (List<SimulatedChatSession> clients, MatchmakingGroup group) = CreateCoopGroup(
            requestedMap,
            humanCount,
            botDifficulty,
            fillTeamWithBots);

        bool dispatched = await service.ProcessMatchForSimulationAsync([group]);
        dispatched.Should().BeTrue();

        DecodedCreateMatch createPacket = ExtractSingleCreateMatch(server);
        createPacket.MatchType.Should().Be((byte)ChatProtocol.ArrangedMatchType.AM_MATCHMAKING_BOTMATCH);
        createPacket.PlayerCount.Should().Be((byte)humanCount);
        createPacket.Players.Should().OnlyContain(p => p.Team == 1);
        createPacket.MatchSettings.Should().Contain("mode:botmatch");
        createPacket.MatchSettings.Should().Contain($"map:{expectedMap}");
        createPacket.MatchSettings.Should().Contain($"teamsize:{expectedTeamSize}");
        createPacket.MatchSettings.Should().Contain(expectedBotToken);
        createPacket.MatchSettings.Should().Contain(expectedDifficultyToken);
        createPacket.MatchSettings.Should().NotContain("allowduplicate:true");

        await service.ConfirmMatch(createPacket.MatchUpId, 77001, "127.0.0.1", 11235, "127.0.0.1", 11235);

        foreach (SimulatedChatSession client in clients)
        {
            DecodedMatchFound found = ExtractSingleMatchFound(client);
            found.MapName.Should().Be(expectedMap);
            found.TeamSize.Should().Be(expectedTeamSize);
        }
    }

    [Theory]
    [InlineData("devowars", 2, true, 5, 3, 5)]
    [InlineData("devowars", 5, true, 5, 0, 5)]
    [InlineData("devowars", 2, false, 5, 0, 5)]
    [InlineData("devowars_10v10", 3, true, 10, 7, 10)]
    [InlineData("devowars_10v10", 4, false, 10, 0, 10)]
    public async Task CoopBroker_SingleHeroDevourerMaps_EmitAllowDuplicateAndDevourerRosters_OmittingBots1WhenZero(
        string mapName,
        int humanCount,
        bool fillTeamWithBots,
        int expectedTeamSize,
        int expectedAllyBots,
        int expectedEnemyBots)
    {
        IConfiguration config = BuildConfig();
        using MatchmakingService service = new(config, _mockChatContext.Object, timeProvider: _clock);
        SimulatedChatSession server = RegisterIdleGameServer(serverId: 502, location: "USW");
        (_, MatchmakingGroup group) = CreateCoopGroup(mapName, humanCount, botDifficulty: 3, fillTeamWithBots);

        bool dispatched = await service.ProcessMatchForSimulationAsync([group]);
        dispatched.Should().BeTrue();

        DecodedCreateMatch createPacket = ExtractSingleCreateMatch(server);
        createPacket.MatchSettings.Should().Contain($"map:{mapName}");
        createPacket.MatchSettings.Should().Contain($"teamsize:{expectedTeamSize}");
        createPacket.MatchSettings.Should().Contain("allowduplicate:true");
        createPacket.MatchSettings.Should().NotContain("randombots:");

        string expectedEnemyRoster = string.Join('|', Enumerable.Repeat("DevourerBot", expectedEnemyBots));
        createPacket.MatchSettings.Should().Contain($" bots2:{expectedEnemyRoster} ");

        if (expectedAllyBots > 0)
        {
            string expectedAllyRoster = string.Join('|', Enumerable.Repeat("DevourerBot", expectedAllyBots));
            createPacket.MatchSettings.Should().Contain($" bots1:{expectedAllyRoster} ");
        }
        else
        {
            createPacket.MatchSettings.Should().NotContain("bots1:");
        }
    }

    [Theory]
    [InlineData(2, true, 3, 5)]
    [InlineData(5, true, 0, 5)]
    [InlineData(2, false, 0, 5)]
    public async Task CoopBroker_SoccerCustomPool_Emits29PoolNonOverlappingRosters_OmittingBots1WhenZero(
        int humanCount,
        bool fillTeamWithBots,
        int expectedAllyBots,
        int expectedEnemyBots)
    {
        IConfiguration config = BuildConfig();
        using MatchmakingService service = new(config, _mockChatContext.Object, timeProvider: _clock);
        SimulatedChatSession server = RegisterIdleGameServer(serverId: 509, location: "USW");
        (_, MatchmakingGroup group) = CreateCoopGroup("soccer", humanCount, botDifficulty: 3, fillTeamWithBots);

        bool dispatched = await service.ProcessMatchForSimulationAsync([group]);
        dispatched.Should().BeTrue();

        DecodedCreateMatch createPacket = ExtractSingleCreateMatch(server);
        createPacket.MatchSettings.Should().Contain("mode:botmatch");
        createPacket.MatchSettings.Should().Contain("map:soccer");
        createPacket.MatchSettings.Should().Contain("teamsize:5");
        createPacket.MatchSettings.Should().Contain("botdifficulty:3");
        createPacket.MatchSettings.Should().NotContain("randombots:");
        createPacket.MatchSettings.Should().NotContain("allowduplicate:true");

        string[] tokens = createPacket.MatchSettings.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        List<string> assignedBots = [];

        string? bots1Token = tokens.SingleOrDefault(t => t.StartsWith("bots1:", StringComparison.Ordinal));
        if (expectedAllyBots > 0)
        {
            bots1Token.Should().NotBeNull();
            string[] allyBots = bots1Token!["bots1:".Length..].Split('|');
            allyBots.Should().HaveCount(expectedAllyBots);
            assignedBots.AddRange(allyBots);
        }
        else
        {
            bots1Token.Should().BeNull();
        }

        string? bots2Token = tokens.SingleOrDefault(t => t.StartsWith("bots2:", StringComparison.Ordinal));
        bots2Token.Should().NotBeNull();
        string[] enemyBots = bots2Token!["bots2:".Length..].Split('|');
        enemyBots.Should().HaveCount(expectedEnemyBots);
        assignedBots.AddRange(enemyBots);

        assignedBots.Should().OnlyContain(b => BotMatchMapCatalog.SoccerBotPool.Contains(b));
        assignedBots.Distinct(StringComparer.Ordinal).Should().HaveCount(assignedBots.Count);
    }

    [Fact]
    public async Task CoopBroker_OversizedPartyFor3v3Map_RejectsBeforeServerAllocation_AndBroadcastsLeaveQueue()
    {
        IConfiguration config = BuildConfig();
        using MatchmakingService service = new(config, _mockChatContext.Object, timeProvider: _clock);
        SimulatedChatSession server = RegisterIdleGameServer(serverId: 503, location: "USW");
        (List<SimulatedChatSession> clients, MatchmakingGroup group) = CreateCoopGroup(
            mapName: "grimmscrossing",
            humanCount: 4,
            botDifficulty: 2,
            fillTeamWithBots: true);

        bool dispatched = await service.ProcessMatchForSimulationAsync([group]);

        dispatched.Should().BeFalse("4 players exceed Grimm's Crossing 3v3 team size");
        server.SnapshotPackets().Should().BeEmpty("No NET_CHAT_GS_CREATE_MATCH may be sent for an oversized Co-Op party");
        server.ServerMetadata.Status.Should().Be(ChatProtocol.ServerStatus.SERVER_STATUS_IDLE);
        group.QueueStartTime.Should().BeNull("Oversized Co-Op group must be dequeued so it does not spin every broker tick");

        foreach (SimulatedChatSession client in clients)
        {
            HasCommand(client, (ushort)ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_GROUP_LEAVE_QUEUE)
                .Should().BeTrue("Every party member must receive NET_CHAT_CL_TMM_GROUP_LEAVE_QUEUE when dequeued");
        }
    }

    private static IConfiguration BuildConfig(string forceSeason = "auto")
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Matchmaking:SeasonalMaps:Enabled"] = "true",
                ["Matchmaking:SeasonalMaps:ForceSeason"] = forceSeason,
                ["Matchmaking:SeasonalMaps:IncludeRankedQueues"] = "true"
            })
            .Build();
    }

    private SimulatedChatSession RegisterIdleGameServer(int serverId, string location)
    {
        SimulatedChatSession session = new(_tcpServer, _serviceProvider)
        {
            Account = new Account
            {
                ID = 9000 + serverId,
                Name = $"HOST_{serverId}",
                Type = AccountType.ServerHost,
                User = new User
                {
                    ID = 9000 + serverId,
                    EmailAddress = $"host_{serverId}@kongor.test",
                    Role = new DbRole { ID = 2, Name = "User" },
                    SRPPasswordSalt = "salt",
                    SRPPasswordHash = "hash",
                    PBKDF2PasswordHash = "hash"
                }
            },
            ServerMetadata = new MatchServerChatSessionMetadata
            {
                ServerID = serverId,
                SessionCookie = $"srv_cookie_{serverId}",
                ChatProtocolVersion = (int)ChatProtocol.CHAT_PROTOCOL_EXTERNAL_VERSION,
                Status = ChatProtocol.ServerStatus.SERVER_STATUS_IDLE,
                Address = "127.0.0.1",
                Port = 11235,
                LocalAddress = "127.0.0.1",
                LocalPort = 11235,
                Location = location,
                IsQueueArcade = false
            }
        };
        session.MarkAuthenticated(ChatPeerRole.MatchServer);
        _matchServerSessions[serverId] = session;
        return session;
    }

    private (List<SimulatedChatSession> Clients, MatchmakingGroup Group) CreateCoopGroup(
        string mapName,
        int humanCount,
        byte botDifficulty,
        bool fillTeamWithBots)
    {
        List<SimulatedChatSession> sessions = new(humanCount);
        List<MatchmakingGroupMember> members = new(humanCount);

        for (int i = 0; i < humanCount; i++)
        {
            int accountId = 2000 + i;
            SimulatedChatSession client = new(_tcpServer, _serviceProvider)
            {
                Account = new Account
                {
                    ID = accountId,
                    Name = $"CoopPlayer{i + 1}",
                    User = new User
                    {
                        ID = accountId,
                        EmailAddress = $"coop{i + 1}@kongor.test",
                        Role = new DbRole { ID = 2, Name = "User" },
                        SRPPasswordSalt = "salt",
                        SRPPasswordHash = "hash",
                        PBKDF2PasswordHash = "hash"
                    }
                },
                ClientMetadata = new ClientChatSessionMetadata
                {
                    RemoteIP = "127.0.0.1",
                    SessionCookie = $"coop_cookie_{accountId}",
                    SessionAuthenticationHash = $"coop_hash_{accountId}",
                    ChatProtocolVersion = (int)ChatProtocol.CHAT_PROTOCOL_EXTERNAL_VERSION,
                    OperatingSystemIdentifier = 1,
                    OperatingSystemVersionMajor = 10,
                    OperatingSystemVersionMinor = 0,
                    OperatingSystemVersionPatch = 0,
                    OperatingSystemBuildCode = "Windows",
                    OperatingSystemArchitecture = "x64",
                    ClientVersionMajor = 4,
                    ClientVersionMinor = 10,
                    ClientVersionPatch = 1,
                    ClientVersionRevision = 0,
                    LastKnownClientState = ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_CONNECTED,
                    ClientChatModeState = ChatProtocol.ChatModeType.CHAT_MODE_AVAILABLE,
                    ClientRegion = "USW",
                    ClientLanguage = "en"
                }
            };
            client.MarkAuthenticated(ChatPeerRole.Client);
            _clientSessions[$"coop_cookie_{accountId}"] = client;
            sessions.Add(client);

            members.Add(new MatchmakingGroupMember(client)
            {
                Slot = (byte)i,
                IsLeader = i == 0,
                IsReady = true,
                IsInGame = false,
                IsEligibleForMatchmaking = true,
                LoadingPercent = 100,
                Rating = 1500.0
            });
        }

        MatchmakingGroup group = new()
        {
            QueueStartTime = _clock.GetUtcNow(),
            Members = members,
            Information = new MatchmakingGroupInformation
            {
                ClientVersion = "4.10.1.0",
                GroupType = ChatProtocol.TMMType.TMM_TYPE_COOP,
                GameType = ChatProtocol.TMMGameType.TMM_GAME_TYPE_CASUAL,
                MapName = mapName,
                GameModes = ["ap"],
                GameRegions = ["USW"],
                Ranked = false,
                MatchFidelity = 0,
                BotDifficulty = botDifficulty,
                RandomizeBots = fillTeamWithBots
            }
        };

        return (sessions, group);
    }

    private static DecodedCreateMatch ExtractSingleCreateMatch(SimulatedChatSession serverSession)
    {
        List<DecodedCreateMatch> found = [];
        foreach (byte[] raw in serverSession.SnapshotPackets())
        {
            if (raw.Length < 4) continue;
            ChatBuffer reader = new(raw.AsSpan(2));
            ushort cmd = reader.ReadCommand();
            if (cmd != (ushort)ChatProtocol.ChatServerToGameServer.NET_CHAT_GS_CREATE_MATCH) continue;

            byte matchType = (byte)reader.ReadInt8();
            int matchUpId = reader.ReadInt32();
            _ = reader.ReadInt32();
            _ = reader.ReadInt32();
            _ = reader.ReadString();
            string settings = reader.ReadString();
            _ = reader.ReadInt8();
            _ = reader.ReadInt8();
            byte playerCount = (byte)reader.ReadInt8();

            List<(int AccountId, byte Team, byte Slot)> players = new(playerCount);
            for (int p = 0; p < playerCount; p++)
            {
                int accountId = reader.ReadInt32();
                byte team = (byte)reader.ReadInt8();
                byte slot = (byte)reader.ReadInt8();
                _ = reader.ReadInt8();
                _ = reader.ReadInt32();
                _ = reader.ReadInt32();
                _ = reader.ReadInt8();
                _ = reader.ReadInt8();
                _ = reader.ReadInt8();
                players.Add((accountId, team, slot));
            }

            int groupIdsCount = reader.ReadInt32();
            groupIdsCount.Should().Be(0);
            reader.HasRemainingData().Should().BeFalse();
            found.Add(new DecodedCreateMatch(matchType, matchUpId, settings, playerCount, players));
        }

        found.Should().ContainSingle();
        return found[0];
    }

    private static DecodedMatchFound ExtractSingleMatchFound(SimulatedChatSession clientSession)
    {
        List<DecodedMatchFound> found = [];
        foreach (byte[] raw in clientSession.SnapshotPackets())
        {
            if (raw.Length < 4) continue;
            ChatBuffer reader = new(raw.AsSpan(2));
            ushort cmd = reader.ReadCommand();
            if (cmd != (ushort)ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_MATCH_FOUND_UPDATE) continue;

            string mapName = reader.ReadString();
            byte teamSize = (byte)reader.ReadInt8();
            byte gameType = (byte)reader.ReadInt8();
            found.Add(new DecodedMatchFound(mapName, teamSize, gameType));
        }

        found.Should().ContainSingle();
        return found[0];
    }

    private static bool HasCommand(SimulatedChatSession session, ushort expectedCmd)
    {
        foreach (byte[] raw in session.SnapshotPackets())
        {
            if (raw.Length < 4) continue;
            ChatBuffer reader = new(raw.AsSpan(2));
            if (reader.ReadCommand() == expectedCmd)
            {
                return true;
            }
        }

        return false;
    }
}
