// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net;
using System.Text.Json;
using ASPIRE.Common.Models.ServerManagement;
using Moq;
using TRANSMUTANSTEIN.ChatServer.Domain.Core;
using TRANSMUTANSTEIN.ChatServer.Domain.Matchmaking;
using TRANSMUTANSTEIN.ChatServer.Internals;
using TRANSMUTANSTEIN.ChatServer.Services;

namespace ASPIRE.Tests.TRANSMUTANSTEIN.ChatServer.Tests;

public class DiscordPresencePublisherServiceTests
{
    private static Account CreateTestAccount(int id, string name)
    {
        Role role = new() { ID = 1, Name = "User" };
        User user = new()
        {
            ID = id,
            EmailAddress = $"{name.ToLowerInvariant()}@example.com",
            Role = role,
            SRPPasswordSalt = "salt",
            SRPPasswordHash = "hash"
        };

        return new Account { ID = id, Name = name, IsMain = true, User = user };
    }

    private static ClientChatSessionMetadata CreateMetadata(ChatProtocol.ChatClientStatus status, int? matchId)
    {
        return new ClientChatSessionMetadata
        {
            RemoteIP = "127.0.0.1",
            SessionCookie = "cookie",
            SessionAuthenticationHash = "hash",
            ChatProtocolVersion = 1,
            OperatingSystemIdentifier = 1,
            OperatingSystemVersionMajor = 10,
            OperatingSystemVersionMinor = 0,
            OperatingSystemVersionPatch = 0,
            OperatingSystemBuildCode = "1000",
            OperatingSystemArchitecture = "x64",
            ClientVersionMajor = 4,
            ClientVersionMinor = 10,
            ClientVersionPatch = 1,
            ClientVersionRevision = 0,
            LastKnownClientState = status,
            ClientChatModeState = ChatProtocol.ChatModeType.CHAT_MODE_AVAILABLE,
            ClientRegion = "USE",
            ClientLanguage = "en",
            MatchID = matchId
        };
    }

    private static Mock<IServiceProvider> CreateMockServiceProvider(Mock<IChatContext> mockChatContext)
    {
        Mock<IServiceProvider> mockServiceProvider = new();
        Mock<ILogger<ChatSession>> mockSessionLogger = new();

        mockServiceProvider.Setup(sp => sp.GetService(typeof(ILogger<ChatSession>))).Returns(mockSessionLogger.Object);
        mockServiceProvider.Setup(sp => sp.GetService(typeof(IChatContext))).Returns(mockChatContext.Object);
        mockServiceProvider.Setup(sp => sp.GetService(typeof(IMatchmakingService)))
            .Returns(Mock.Of<IMatchmakingService>());
        mockServiceProvider.Setup(sp => sp.GetService(typeof(IDiscordPresencePublisherService)))
            .Returns(Mock.Of<IDiscordPresencePublisherService>());
        mockServiceProvider.Setup(sp => sp.GetService(typeof(IWebHostEnvironment)))
            .Returns(Mock.Of<IWebHostEnvironment>());

        return mockServiceProvider;
    }

    [Fact]
    public async Task PublishPresenceUpdate_PublicGameInLobby_PublishesJoiningGame()
    {
        // Arrange
        Mock<IConnectionMultiplexer> mockRedis = new();
        Mock<ISubscriber> mockSubscriber = new();
        Mock<IDatabase> mockDatabase = new();
        Mock<IChatContext> mockChatContext = new();

        mockRedis.Setup(r => r.GetSubscriber(It.IsAny<object>())).Returns(mockSubscriber.Object);
        mockRedis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(mockDatabase.Object);

        const int matchId = 12345;
        MatchStartData matchData = new()
        {
            MatchID = matchId,
            MatchName = "Devo Wars Lobby",
            ServerID = 1,
            ServerName = "US East Server 1",
            HostAccountName = "HostPlayer",
            Map = "devo_wars",
            Version = "4.10.1",
            IsCasual = false,
            MatchType = 0,
            MatchMode = "nm",
            ConnectedPlayersCount = 1,
            MaximumPlayersCount = 10,
            IsStarted = false,
            Phase = 0
        };

        mockDatabase
            .Setup(db => db.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(JsonSerializer.Serialize(matchData));

        Mock<IServiceProvider> mockServiceProvider = CreateMockServiceProvider(mockChatContext);
        Mock<TCPServer> mockTcpServer = new(IPAddress.Loopback, 8000);

        ChatSession session = new(mockTcpServer.Object, mockServiceProvider.Object)
        {
            Account = CreateTestAccount(100, "HostPlayer"),
            ClientMetadata = CreateMetadata(ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME, matchId)
        };

        Mock<IMatchmakingService> mockMatchmaking = new();
        DiscordPresencePublisherService publisher = new(mockRedis.Object,
            mockMatchmaking.Object);

        TaskCompletionSource<string> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        mockSubscriber
            .Setup(s => s.PublishAsync(It.IsAny<RedisChannel>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
            .Callback<RedisChannel, RedisValue, CommandFlags>((channel, value, flags) =>
            {
                tcs.TrySetResult(value.ToString());
            })
            .ReturnsAsync(1);

        // Act
        publisher.PublishPresenceUpdate(session);

        // Await completion directly without arbitrary delays
        string capturedMessage = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        Assert.NotNull(capturedMessage);
        DiscordPresenceUpdateDto? dto = JsonSerializer.Deserialize<DiscordPresenceUpdateDto>(capturedMessage);
        Assert.NotNull(dto);
        Assert.Equal("In Game Lobby", dto.State);
        Assert.Equal("Public - Devo Wars", dto.Details);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task PublishPresenceUpdate_PublicGameStarted_PublishesInAGame()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange
        Mock<IConnectionMultiplexer> mockRedis = new();
        Mock<ISubscriber> mockSubscriber = new();
        Mock<IDatabase> mockDatabase = new();
        Mock<IChatContext> mockChatContext = new();

        mockRedis.Setup(r => r.GetSubscriber(It.IsAny<object>())).Returns(mockSubscriber.Object);
        mockRedis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(mockDatabase.Object);

        const int matchId = 12345;
        MatchStartData matchData = new()
        {
            MatchID = matchId,
            MatchName = "Devo Wars Match",
            ServerID = 1,
            ServerName = "US East Server 1",
            HostAccountName = "HostPlayer",
            Map = "devo_wars",
            Version = "4.10.1",
            IsCasual = false,
            MatchType = 0,
            MatchMode = "nm",
            ConnectedPlayersCount = 10,
            MaximumPlayersCount = 10,
            IsStarted = true,
            Phase = 2
        };

        mockDatabase
            .Setup(db => db.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(JsonSerializer.Serialize(matchData));

        Mock<IServiceProvider> mockServiceProvider = CreateMockServiceProvider(mockChatContext);
        Mock<TCPServer> mockTcpServer = new(IPAddress.Loopback, 8000);

        ChatSession session = new(mockTcpServer.Object, mockServiceProvider.Object)
        {
            Account = CreateTestAccount(100, "HostPlayer"),
            ClientMetadata = CreateMetadata(ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME, matchId)
        };

        Mock<IMatchmakingService> mockMatchmaking = new();
        DiscordPresencePublisherService publisher = new(mockRedis.Object,
            mockMatchmaking.Object);

        TaskCompletionSource<string> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        mockSubscriber
            .Setup(s => s.PublishAsync(It.IsAny<RedisChannel>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
            .Callback<RedisChannel, RedisValue, CommandFlags>((channel, value, flags) =>
            {
                tcs.TrySetResult(value.ToString());
            })
            .ReturnsAsync(1);

        // Act
        publisher.PublishPresenceUpdate(session);

        // Await completion directly without arbitrary delays
        string capturedMessage = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        Assert.NotNull(capturedMessage);
        DiscordPresenceUpdateDto? dto = JsonSerializer.Deserialize<DiscordPresenceUpdateDto>(capturedMessage);
        Assert.NotNull(dto);
        Assert.Equal("In a Game", dto.State);
        Assert.Equal("Public - Devo Wars", dto.Details);
        Assert.Equal("HostPlayer", dto.SmallImageText);
    }

    [Fact]
    public async Task PublishPresenceUpdate_DisconnectedSession_PublishesOfflineAndDefaultSmallImageText()
    {
        // Arrange
        Mock<IConnectionMultiplexer> mockRedis = new();
        Mock<ISubscriber> mockSubscriber = new();
        Mock<IDatabase> mockDatabase = new();
        Mock<IChatContext> mockChatContext = new();

        mockRedis.Setup(r => r.GetSubscriber(It.IsAny<object>())).Returns(mockSubscriber.Object);
        mockRedis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(mockDatabase.Object);

        Mock<IServiceProvider> mockServiceProvider = CreateMockServiceProvider(mockChatContext);
        Mock<TCPServer> mockTcpServer = new(IPAddress.Loopback, 8000);

        ChatSession session = new(mockTcpServer.Object, mockServiceProvider.Object)
        {
            Account = CreateTestAccount(102, "LoggedOutPlayer"),
            ClientMetadata = CreateMetadata(ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_DISCONNECTED, null)
        };

        Mock<IMatchmakingService> mockMatchmaking = new();
        DiscordPresencePublisherService publisher = new(mockRedis.Object,
            mockMatchmaking.Object);

        TaskCompletionSource<string> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        mockSubscriber
            .Setup(s => s.PublishAsync(It.IsAny<RedisChannel>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
            .Callback<RedisChannel, RedisValue, CommandFlags>((channel, value, flags) =>
            {
                tcs.TrySetResult(value.ToString());
            })
            .ReturnsAsync(1);

        // Act
        publisher.PublishPresenceUpdate(session);

        // Await completion directly without arbitrary delays
        string capturedMessage = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        Assert.NotNull(capturedMessage);
        DiscordPresenceUpdateDto? dto = JsonSerializer.Deserialize<DiscordPresenceUpdateDto>(capturedMessage);
        Assert.NotNull(dto);
        Assert.Equal("Offline", dto.State);
        Assert.Equal("Logged Out", dto.Details);
        Assert.Equal("KONGOR Player", dto.SmallImageText);
    }

    [Fact]
    public async Task PublishPresenceUpdate_SessionAccountClearedImmediatelyAfter_PublishesOfflineSuccessfully()
    {
        // Arrange
        Mock<IConnectionMultiplexer> mockRedis = new();
        Mock<ISubscriber> mockSubscriber = new();
        Mock<IDatabase> mockDatabase = new();
        Mock<IChatContext> mockChatContext = new();

        mockRedis.Setup(r => r.GetSubscriber(It.IsAny<object>())).Returns(mockSubscriber.Object);
        mockRedis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(mockDatabase.Object);

        Mock<IServiceProvider> mockServiceProvider = CreateMockServiceProvider(mockChatContext);
        Mock<TCPServer> mockTcpServer = new(IPAddress.Loopback, 8000);

        ChatSession session = new(mockTcpServer.Object, mockServiceProvider.Object)
        {
            Account = CreateTestAccount(103, "RaceConditionPlayer"),
            ClientMetadata = CreateMetadata(ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_DISCONNECTED, null)
        };

        Mock<IMatchmakingService> mockMatchmaking = new();
        DiscordPresencePublisherService publisher = new(mockRedis.Object,
            mockMatchmaking.Object);

        TaskCompletionSource<string> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        mockSubscriber
            .Setup(s => s.PublishAsync(It.IsAny<RedisChannel>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
            .Callback<RedisChannel, RedisValue, CommandFlags>((channel, value, flags) =>
            {
                tcs.TrySetResult(value.ToString());
            })
            .ReturnsAsync(1);

        // Act - Simulate Cleanup setting Account = null! immediately after PublishPresenceUpdate
        publisher.PublishPresenceUpdate(session, null, true);
        session.Account = null!;

        // Await completion directly without arbitrary delays
        string capturedMessage = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        Assert.NotNull(capturedMessage);
        DiscordPresenceUpdateDto? dto = JsonSerializer.Deserialize<DiscordPresenceUpdateDto>(capturedMessage);
        Assert.NotNull(dto);
        Assert.Equal(103, dto.AccountId);
        Assert.Equal("Offline", dto.State);
        Assert.Equal("Logged Out", dto.Details);
    }

    [Fact]
    public async Task PublishPresenceUpdate_MatchmakingQueue_MidWars_PublishesInMatchmakingQueue()
    {
        // Arrange
        Mock<IConnectionMultiplexer> mockRedis = new();
        Mock<ISubscriber> mockSubscriber = new();
        Mock<IDatabase> mockDatabase = new();
        Mock<IChatContext> mockChatContext = new();

        mockRedis.Setup(r => r.GetSubscriber(It.IsAny<object>())).Returns(mockSubscriber.Object);
        mockRedis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(mockDatabase.Object);

        Mock<IServiceProvider> mockServiceProvider = CreateMockServiceProvider(mockChatContext);
        Mock<TCPServer> mockTcpServer = new(IPAddress.Loopback, 8000);

        ChatSession session = new(mockTcpServer.Object, mockServiceProvider.Object)
        {
            Account = CreateTestAccount(104, "guccidonut"),
            ClientMetadata = CreateMetadata(ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_CONNECTED, null)
        };

        MatchmakingGroupInformation groupInfo = new()
        {
            ClientVersion = "4.10.1",
            GroupType = ChatProtocol.TMMType.TMM_TYPE_SOLO,
            GameType = ChatProtocol.TMMGameType.TMM_GAME_TYPE_MIDWARS,
            MapName = "midwars",
            GameModes = ["midwars"],
            GameRegions = ["use"],
            Ranked = false,
            MatchFidelity = 0,
            BotDifficulty = 0,
            RandomizeBots = false
        };

        MatchmakingGroupMember member = new(session)
        {
            Slot = 1,
            IsLeader = true,
            IsReady = true,
            IsInGame = false,
            IsEligibleForMatchmaking = true,
            LoadingPercent = 100,
            Rating = 1500.0,
            GameModeAccess = "true"
        };

        MatchmakingGroup group = new()
        {
            Members = [member],
            Information = groupInfo,
            QueueStartTime = TimeProvider.System.GetUtcNow()
        };

        Mock<IMatchmakingService> mockMatchmaking = new();
        mockMatchmaking.Setup(m => m.GetMatchmakingGroupByMemberID(104)).Returns(group);

        DiscordPresencePublisherService publisher = new(mockRedis.Object,
            mockMatchmaking.Object);

        TaskCompletionSource<string> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        mockSubscriber
            .Setup(s => s.PublishAsync(It.IsAny<RedisChannel>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
            .Callback<RedisChannel, RedisValue, CommandFlags>((channel, value, flags) =>
            {
                tcs.TrySetResult(value.ToString());
            })
            .ReturnsAsync(1);

        // Act
        publisher.PublishGroupPresenceUpdate(group);

        // Await completion directly without arbitrary delays
        string capturedMessage = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        Assert.NotNull(capturedMessage);
        DiscordPresenceUpdateDto? dto = JsonSerializer.Deserialize<DiscordPresenceUpdateDto>(capturedMessage);
        Assert.NotNull(dto);
        Assert.Equal(104, dto.AccountId);
        Assert.Equal("In Matchmaking Queue", dto.State);
        Assert.Equal("Queueing Mid Wars", dto.Details);
        Assert.Equal("map_midwars", dto.LargeImageKey);
        Assert.True(dto.StartTimestamp > 0);
    }

    [Fact]
    public async Task PublishPresenceUpdate_QueueArcadeInGame_PublishesInQueueWithOriginalQueueStartTimeAndArcadeMap()
    {
        // protects: Server-side Queue Arcade Rich Presence contract when a queued player joins an unscheduled Queue Arcade warm-up server.
        // fails_when: CHAT_CLIENT_STATUS_IN_GAME overwrites "In Matchmaking Queue" with "In a Game" or replaces group.QueueStartTime with match start time.
        // seam: DiscordPresencePublisherService.PublishPresenceUpdate -> Redis DiscordPresenceUpdateDto payload.

        Mock<IConnectionMultiplexer> mockRedis = new();
        Mock<ISubscriber> mockSubscriber = new();
        Mock<IDatabase> mockDatabase = new();
        Mock<IChatContext> mockChatContext = new();

        mockRedis.Setup(r => r.GetSubscriber(It.IsAny<object>())).Returns(mockSubscriber.Object);
        mockRedis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(mockDatabase.Object);

        const int arcadeMatchId = 9001;
        MatchStartData arcadeMatchData = new()
        {
            MatchID = arcadeMatchId,
            MatchName = "QUEUE ARCADE #Q2",
            ServerID = 42,
            ServerName = "US East Arcade 2",
            HostAccountName = "KongorArcade",
            Map = "devowars_10v10",
            Version = "4.10.1",
            IsCasual = true,
            MatchType = 1,
            MatchMode = "ap",
            ConnectedPlayersCount = 6,
            MaximumPlayersCount = 20,
            IsStarted = true,
            Phase = 6
        };

        mockDatabase
            .Setup(db => db.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(JsonSerializer.Serialize(arcadeMatchData));

        Mock<IServiceProvider> mockServiceProvider = CreateMockServiceProvider(mockChatContext);
        Mock<TCPServer> mockTcpServer = new(IPAddress.Loopback, 8000);

        ClientChatSessionMetadata metadata = CreateMetadata(ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME, arcadeMatchId);
        metadata.ActiveArcadeMatchId = arcadeMatchId;

        ChatSession session = new(mockTcpServer.Object, mockServiceProvider.Object)
        {
            Account = CreateTestAccount(105, "guccidonut"),
            ClientMetadata = metadata
        };

        DateTimeOffset originalQueueStart = new(2026, 11, 15, 7, 45, 0, TimeSpan.Zero);
        MatchmakingGroupInformation groupInfo = new()
        {
            ClientVersion = "4.10.1",
            GroupType = ChatProtocol.TMMType.TMM_TYPE_SOLO,
            GameType = ChatProtocol.TMMGameType.TMM_GAME_TYPE_NORMAL,
            MapName = "caldavar",
            GameModes = ["ap"],
            GameRegions = ["use"],
            Ranked = true,
            MatchFidelity = 0,
            BotDifficulty = 0,
            RandomizeBots = false
        };

        MatchmakingGroupMember member = new(session)
        {
            Slot = 1,
            IsLeader = true,
            IsReady = true,
            IsInGame = true,
            IsEligibleForMatchmaking = true,
            LoadingPercent = 100,
            Rating = 1650.0,
            GameModeAccess = "true"
        };

        MatchmakingGroup group = new()
        {
            Members = [member],
            Information = groupInfo,
            QueueStartTime = originalQueueStart
        };

        Mock<IMatchmakingService> mockMatchmaking = new();
        mockMatchmaking.Setup(m => m.GetMatchmakingGroupByMemberID(105)).Returns(group);

        DiscordPresencePublisherService publisher = new(mockRedis.Object, mockMatchmaking.Object);

        TaskCompletionSource<string> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        mockSubscriber
            .Setup(s => s.PublishAsync(It.IsAny<RedisChannel>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
            .Callback<RedisChannel, RedisValue, CommandFlags>((channel, value, flags) =>
            {
                tcs.TrySetResult(value.ToString());
            })
            .ReturnsAsync(1);

        publisher.PublishPresenceUpdate(session);

        string capturedMessage = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(capturedMessage);
        DiscordPresenceUpdateDto? dto = JsonSerializer.Deserialize<DiscordPresenceUpdateDto>(capturedMessage);
        Assert.NotNull(dto);
        Assert.Equal(105, dto.AccountId);
        Assert.Equal("In Matchmaking Queue", dto.State);
        Assert.Equal("In Queue • Devo Wars (10v10 Chaos)", dto.Details);
        Assert.Equal("map_devowars", dto.LargeImageKey);
        Assert.Equal("Devo Wars (10v10 Chaos)", dto.LargeImageText);
        Assert.Equal(originalQueueStart.ToUnixTimeSeconds(), dto.StartTimestamp);
    }

    [Fact]
    public void GetFriendlyMapName_And_GetMapAssetKey_ResolveAll35ShippedMapsAndVariants()
    {
        // protects: Parity between TRANSMUTANSTEIN.ChatServer DiscordPresencePublisherService and WILLOWMAKER across all 35 shipped maps and variants.
        // fails_when: Public/TMM matches on custom or variant maps (e.g. midwars_reborn, archers, enfos, truedevo_10v10, pebblesduel) publish raw slugs or fall back to 'large' instead of their map asset key.
        // seam: DiscordPresencePublisherService.GetFriendlyMapName and DiscordPresencePublisherService.GetMapAssetKey.

        (string RawMap, string ExpectedFriendlyName, string ExpectedAssetKey)[] cases =
        [
            ("archers", "Archer Wars", "map_archers"),
            ("arena", "HoN Arena", "map_arena"),
            ("caldavar", "Forests of Caldavar", "map_caldavar"),
            ("caldavar_halloween", "Forests of Caldavar (Halloween)", "map_caldavar"),
            ("caldavar_old", "Forests of Caldavar (Classic)", "map_caldavar"),
            ("caldavar_reborn", "Forests of Caldavar Reborn", "map_caldavar"),
            ("caldavar_wtf", "Forests of Caldavar (WTF)", "map_caldavar"),
            ("capturetheflag", "Capture the Flag", "map_capturetheflag"),
            ("colosseum_duel", "Colosseum Duel", "map_colosseumduel"),
            ("darkwoodvale", "Darkwood Vale", "map_darkwoodvale"),
            ("devowars", "Devo Wars", "map_devowars"),
            ("devowars_10v10", "Devo Wars (10v10 Chaos)", "map_devowars"),
            ("empath_maze", "Empath Maze", "map_empathmaze"),
            ("enfos", "Enfo's Team Survival", "map_enfos"),
            ("grimmscrossing", "Grimm's Crossing", "map_grimmscrossing"),
            ("mids_omg", "Mid Wars Classic", "map_midwars"),
            ("midwars", "Mid Wars", "map_midwars"),
            ("midwars_10v10", "Mid Wars (10v10 Chaos)", "map_midwars"),
            ("midwars_halloween", "Mid Wars (Halloween)", "map_midwars"),
            ("midwars_reborn", "Mid Wars Reborn", "map_midwars"),
            ("nightmarish_coop", "Nightmarish Co-Op", "map_nightmarishcoop"),
            ("pebbles_duel", "Pebbles Duel", "map_pebblesduel"),
            ("persian_pass", "Persian Pass", "map_persianpass"),
            ("prophets", "Prophets", "map_prophets"),
            ("riftwars", "Rift Wars", "map_riftwars"),
            ("soccer", "Strikers", "map_strikers"),
            ("spotlight", "Spotlight", "map_spotlight"),
            ("team_deathmatch", "Team Death Match", "map_teamdeathmatch"),
            ("thegrimmhunt", "The Grimm Hunt", "map_thegrimmhunt"),
            ("three_kingdoms", "Three Kingdoms", "map_threekingdoms"),
            ("truedevo", "True Devo Wars", "map_truedevo"),
            ("truedevo_10v10", "True Devo Wars (10v10 Chaos)", "map_truedevo"),
            ("tutorial", "Tutorial", "map_tutorial"),
            ("valkwars", "Valkyrie Wars", "map_valkwars"),
            ("watchtower", "Watchtower", "map_watchtower")
        ];

        Assert.Equal(35, cases.Length);

        foreach ((string rawMap, string expectedFriendly, string expectedAsset) in cases)
        {
            Assert.Equal(expectedFriendly, DiscordPresencePublisherService.GetFriendlyMapName(rawMap));
            Assert.Equal(expectedAsset, DiscordPresencePublisherService.GetMapAssetKey(rawMap));
        }
    }

    [Fact]
    public async Task PublishPresenceUpdate_InGameWithQueueStartTimeOrMismatchedArcadeId_DoesNotMisclassifyAsQueueArcade()
    {
        // protects: Queued player entering a non-arcade/practice/hosted game or carrying stale ActiveArcadeMatchId never broadcasts as "In Matchmaking Queue".
        // fails_when: group.QueueStartTime != null or !matchId.HasValue forces Queue Arcade DRP outside an actual Queue Arcade match.
        // seam: DiscordPresencePublisherService.PublishPresenceUpdate -> Redis DiscordPresenceUpdateDto payload.

        Mock<IConnectionMultiplexer> mockRedis = new();
        Mock<ISubscriber> mockSubscriber = new();
        Mock<IDatabase> mockDatabase = new();
        Mock<IChatContext> mockChatContext = new();

        mockRedis.Setup(r => r.GetSubscriber(It.IsAny<object>())).Returns(mockSubscriber.Object);
        mockRedis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(mockDatabase.Object);

        MatchStartData practiceOrPublicMatch = new()
        {
            MatchID = 7777,
            MatchName = "DONUT ARENA #D1",
            ServerID = 12,
            ServerName = "DONUT ARENA #D1",
            HostAccountName = "guccidonut",
            Map = "soccer",
            Version = "4.10.1",
            IsCasual = false,
            MatchType = 0,
            MatchMode = "ap",
            ConnectedPlayersCount = 2,
            MaximumPlayersCount = 10,
            IsStarted = true,
            Phase = 6
        };

        mockDatabase
            .Setup(db => db.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(JsonSerializer.Serialize(practiceOrPublicMatch));

        Mock<IServiceProvider> mockServiceProvider = CreateMockServiceProvider(mockChatContext);
        Mock<TCPServer> mockTcpServer = new(IPAddress.Loopback, 8000);

        // Player is queued AND has a stale ActiveArcadeMatchId=9001, but is in matchId=7777 (non-arcade)
        ClientChatSessionMetadata metadata = CreateMetadata(ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME, 7777);
        metadata.ActiveArcadeMatchId = 9001;

        ChatSession session = new(mockTcpServer.Object, mockServiceProvider.Object)
        {
            Account = CreateTestAccount(106, "guccidonut"),
            ClientMetadata = metadata
        };

        MatchmakingGroupInformation groupInfo = new()
        {
            ClientVersion = "4.10.1",
            GroupType = ChatProtocol.TMMType.TMM_TYPE_SOLO,
            GameType = ChatProtocol.TMMGameType.TMM_GAME_TYPE_NORMAL,
            MapName = "caldavar",
            GameModes = ["ap"],
            GameRegions = ["use"],
            Ranked = true,
            MatchFidelity = 0,
            BotDifficulty = 0,
            RandomizeBots = false
        };

        MatchmakingGroupMember member = new(session)
        {
            Slot = 1,
            IsLeader = true,
            IsReady = true,
            IsInGame = true,
            IsEligibleForMatchmaking = true,
            LoadingPercent = 100,
            Rating = 1650.0,
            GameModeAccess = "true"
        };

        MatchmakingGroup group = new()
        {
            Members = [member],
            Information = groupInfo,
            QueueStartTime = new DateTimeOffset(2026, 11, 16, 10, 0, 0, TimeSpan.Zero)
        };

        Mock<IMatchmakingService> mockMatchmaking = new();
        mockMatchmaking.Setup(m => m.GetMatchmakingGroupByMemberID(106)).Returns(group);

        DiscordPresencePublisherService publisher = new(mockRedis.Object, mockMatchmaking.Object);

        TaskCompletionSource<string> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        mockSubscriber
            .Setup(s => s.PublishAsync(It.IsAny<RedisChannel>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
            .Callback<RedisChannel, RedisValue, CommandFlags>((_, value, _) => tcs.TrySetResult(value.ToString()))
            .ReturnsAsync(1);

        publisher.PublishPresenceUpdate(session);

        string capturedMessage = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        DiscordPresenceUpdateDto? dto = JsonSerializer.Deserialize<DiscordPresenceUpdateDto>(capturedMessage);
        Assert.NotNull(dto);
        Assert.Equal("Playing Match", dto.State);
        Assert.DoesNotContain("Queue", dto.State, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Queue", dto.Details, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("map_caldavar", dto.LargeImageKey);
    }
}
