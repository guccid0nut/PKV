// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NEXUS.Contracts.CQRS.Telemetry;
using StackExchange.Redis;
using TRANSMUTANSTEIN.ChatServer.Domain.Core;
using TRANSMUTANSTEIN.ChatServer.Domain.Matchmaking;
using TRANSMUTANSTEIN.ChatServer.Internals;
using TRANSMUTANSTEIN.ChatServer.Serialization;
using TRANSMUTANSTEIN.ChatServer.Services;
using Xunit;

namespace TRANSMUTANSTEIN.ChatServer.Tests;

public sealed class PopulationBreakdownAggregatorTests
{
    private static readonly DateTimeOffset FixedUtcNow = new(2026, 11, 21, 1, 45, 0, TimeSpan.Zero);

    private static MatchmakingGroup CreateGroup(
        int memberCount,
        string[] regions,
        ChatProtocol.TMMGameType gameType = ChatProtocol.TMMGameType.TMM_GAME_TYPE_NORMAL,
        ChatProtocol.TMMType groupType = ChatProtocol.TMMType.TMM_TYPE_PVP,
        string mapName = "caldavar",
        bool isQueued = true)
    {
        List<MatchmakingGroupMember> members = new(memberCount);
        for (int i = 0; i < memberCount; i++)
        {
            Account account = new()
            {
                ID = 1000 + i,
                Name = $"Player_{i}",
                User = null!
            };
            members.Add(new MatchmakingGroupMember(account)
            {
                Slot = (byte)(i + 1),
                IsLeader = i == 0,
                IsReady = true
            });
        }

        return new MatchmakingGroup
        {
            Members = members,
            QueueStartTime = isQueued ? FixedUtcNow.AddSeconds(-15) : null,
            Information = new MatchmakingGroupInformation
            {
                ClientVersion = "5.0.2",
                GroupType = groupType,
                GameType = gameType,
                MapName = mapName,
                GameModes = ["ap"],
                GameRegions = regions,
                Ranked = true,
                MatchFidelity = 1,
                BotDifficulty = 0,
                RandomizeBots = false
            }
        };
    }

    private static MatchServerChatSessionMetadata CreateServer(
        int serverId,
        string location,
        ChatProtocol.ServerStatus status,
        bool isQueueArcade = false,
        int port = 11235)
    {
        return new MatchServerChatSessionMetadata
        {
            ServerID = serverId,
            SessionCookie = $"cookie-{serverId}",
            ChatProtocolVersion = (int)ChatProtocol.CHAT_PROTOCOL_EXTERNAL_VERSION,
            Location = location,
            Status = status,
            IsQueueArcade = isQueueArcade,
            Port = port,
            LocalPort = port
        };
    }

    // protects: Per-group USE+USW deduplication into "US (shared pool)" so multi-US selections never double-count players.
    // fails_when: Selecting both USE and USW counts a solo player as 2 or a 3-stack party as 6 in US (shared pool).
    [Fact]
    public void Compute_DeduplicatesUseAndUswPerGroupIntoUsSharedPool()
    {
        List<MatchmakingGroup> groups =
        [
            CreateGroup(memberCount: 1, regions: ["USE", "USW"]),
            CreateGroup(memberCount: 3, regions: ["use", "USW", "US_EAST", "US-WEST"])
        ];

        PopulationBreakdownResponseDto result = PopulationBreakdownAggregator.Compute(
            onlinePlayers: 42,
            servers: [],
            groups: groups,
            updatedAtUtc: FixedUtcNow);

        result.OnlinePlayers.Should().Be(42);
        result.UpdatedAtUtc.Should().Be(FixedUtcNow);

        TmmQueueBreakdownDto rankedNormal = result.TmmQueues
            .Single(q => q.Title == PopulationBreakdownDefaults.QueueTitleForestsOfCaldavarRankedNormal);

        rankedNormal.TotalQueued.Should().Be(4, "1 solo + 3-player party = 4 distinct queued players");
        rankedNormal.Regions.Select(r => r.Region).Should().ContainInOrder(
            PopulationBreakdownDefaults.RegionAustralia,
            PopulationBreakdownDefaults.RegionEurope,
            PopulationBreakdownDefaults.RegionLatinAmerica,
            PopulationBreakdownDefaults.TmmRegionAny,
            PopulationBreakdownDefaults.RegionSoutheastAsia,
            PopulationBreakdownDefaults.TmmRegionUsSharedPool);

        rankedNormal.Regions.Single(r => r.Region == PopulationBreakdownDefaults.TmmRegionUsSharedPool)
            .Count.Should().Be(4, "USE + USW must deduplicate per group (1 + 3 = 4, NOT 8)");
        rankedNormal.Regions.Single(r => r.Region == PopulationBreakdownDefaults.RegionAustralia).Count.Should().Be(0);
        rankedNormal.Regions.Single(r => r.Region == PopulationBreakdownDefaults.RegionEurope).Count.Should().Be(0);
    }

    // protects: Distinct multi-region selections (e.g. AU + USE) increment each selected pool while keeping distinct totalQueued accurate.
    // fails_when: A group queued for AU and USE is omitted from either Australia or US (shared pool), or USE+USW leaks extra count.
    [Fact]
    public void Compute_MultiRegionAcrossDistinctPools_CountsInEachDistinctPool()
    {
        List<MatchmakingGroup> groups =
        [
            CreateGroup(memberCount: 1, regions: ["AU", "USE"]),
            CreateGroup(memberCount: 2, regions: ["AU", "USE", "USW", "EU"])
        ];

        PopulationBreakdownResponseDto result = PopulationBreakdownAggregator.Compute(
            onlinePlayers: 10,
            servers: [],
            groups: groups,
            updatedAtUtc: FixedUtcNow);

        TmmQueueBreakdownDto rankedNormal = result.TmmQueues
            .Single(q => q.Title == PopulationBreakdownDefaults.QueueTitleForestsOfCaldavarRankedNormal);

        rankedNormal.TotalQueued.Should().Be(3, "1 solo + 2-stack = 3 distinct players queued");
        rankedNormal.Regions.Single(r => r.Region == PopulationBreakdownDefaults.RegionAustralia).Count.Should().Be(3);
        rankedNormal.Regions.Single(r => r.Region == PopulationBreakdownDefaults.TmmRegionUsSharedPool).Count.Should().Be(3);
        rankedNormal.Regions.Single(r => r.Region == PopulationBreakdownDefaults.RegionEurope).Count.Should().Be(2);
        rankedNormal.Regions.Single(r => r.Region == PopulationBreakdownDefaults.RegionLatinAmerica).Count.Should().Be(0);
        rankedNormal.Regions.Single(r => r.Region == PopulationBreakdownDefaults.RegionSoutheastAsia).Count.Should().Be(0);
        rankedNormal.Regions.Single(r => r.Region == PopulationBreakdownDefaults.TmmRegionAny).Count.Should().Be(0);
    }

    // protects: Empty/ANY/ALL/AUTO GameRegions map to "Any region" and unqueued/malformed groups are safely ignored.
    // fails_when: Empty or wildcard region lists drop queued players or malformed groups throw NullReferenceException.
    [Fact]
    public void Compute_EmptyOrWildcardRegions_MapToAnyRegion_AndUnqueuedOrMalformedGroupsAreIgnored()
    {
        MatchmakingGroup malformedGroup = new()
        {
            QueueStartTime = FixedUtcNow,
            Information = null!,
            Members = []
        };

        List<MatchmakingGroup> groups =
        [
            CreateGroup(memberCount: 1, regions: []),
            CreateGroup(memberCount: 2, regions: ["ANY"]),
            CreateGroup(memberCount: 1, regions: ["ALL"]),
            CreateGroup(memberCount: 1, regions: ["AUTO"]),
            CreateGroup(memberCount: 1, regions: [" ", ""]),
            CreateGroup(memberCount: 5, regions: ["USE"], isQueued: false),
            malformedGroup
        ];

        PopulationBreakdownResponseDto result = PopulationBreakdownAggregator.Compute(
            onlinePlayers: 20,
            servers: [],
            groups: groups,
            updatedAtUtc: FixedUtcNow);

        TmmQueueBreakdownDto rankedNormal = result.TmmQueues
            .Single(q => q.Title == PopulationBreakdownDefaults.QueueTitleForestsOfCaldavarRankedNormal);

        rankedNormal.TotalQueued.Should().Be(6);
        rankedNormal.Regions.Single(r => r.Region == PopulationBreakdownDefaults.TmmRegionAny).Count.Should().Be(6);
    }

    // protects: QueueArcade exclusion, ACTIVE/LOADING vs IDLE/SLEEPING classification, canonical region names, and "All locations" sum.
    // fails_when: Arcade servers leak into counts, LOADING/SLEEPING are misclassified, or All locations != sum of active regions.
    [Fact]
    public void Compute_ExcludesQueueArcade_SplitsActiveLoadingVsIdleSleeping_AndSumsAllLocations()
    {
        List<MatchServerChatSessionMetadata> servers =
        [
            CreateServer(1, "USE", ChatProtocol.ServerStatus.SERVER_STATUS_ACTIVE),
            CreateServer(2, "US_EAST", ChatProtocol.ServerStatus.SERVER_STATUS_LOADING),
            CreateServer(3, "USW", ChatProtocol.ServerStatus.SERVER_STATUS_ACTIVE),
            CreateServer(4, "EU", ChatProtocol.ServerStatus.SERVER_STATUS_LOADING),
            CreateServer(5, "SA", ChatProtocol.ServerStatus.SERVER_STATUS_ACTIVE),

            CreateServer(6, "AU", ChatProtocol.ServerStatus.SERVER_STATUS_IDLE),
            CreateServer(7, "SEA", ChatProtocol.ServerStatus.SERVER_STATUS_SLEEPING),
            CreateServer(8, "LAT", ChatProtocol.ServerStatus.SERVER_STATUS_IDLE),
            CreateServer(9, "BR", ChatProtocol.ServerStatus.SERVER_STATUS_SLEEPING),

            // Must be excluded: QueueArcade via flag or Tier Q port
            CreateServer(10, "USE", ChatProtocol.ServerStatus.SERVER_STATUS_ACTIVE, isQueueArcade: true),
            CreateServer(11, "EU", ChatProtocol.ServerStatus.SERVER_STATUS_IDLE, isQueueArcade: false, port: 11485),

            // Must be excluded: non-operational server states
            CreateServer(12, "USW", ChatProtocol.ServerStatus.SERVER_STATUS_CRASHED),
            CreateServer(13, "USW", ChatProtocol.ServerStatus.SERVER_STATUS_KILLED),
            CreateServer(14, "USW", ChatProtocol.ServerStatus.SERVER_STATUS_UNKNOWN)
        ];

        PopulationBreakdownResponseDto result = PopulationBreakdownAggregator.Compute(
            onlinePlayers: 120,
            servers: servers,
            groups: [],
            updatedAtUtc: FixedUtcNow);

        result.GamesInProgressTotal.Should().Be(5);
        result.GamesInProgressByRegion.Should().HaveCount(5);
        result.GamesInProgressByRegion[0].Region.Should().Be(PopulationBreakdownDefaults.AllLocationsLabel);
        result.GamesInProgressByRegion[0].Count.Should().Be(5);

        List<RegionCountDto> perRegionGames = result.GamesInProgressByRegion.Skip(1).ToList();
        perRegionGames.Sum(r => r.Count).Should().Be(result.GamesInProgressTotal);
        perRegionGames.Should().BeEquivalentTo(
            [
                new RegionCountDto { Region = "Europe", Count = 1 },
                new RegionCountDto { Region = "Latin America", Count = 1 },
                new RegionCountDto { Region = "US East", Count = 2 },
                new RegionCountDto { Region = "US West", Count = 1 }
            ],
            options => options.WithStrictOrdering());

        result.ServersAvailableByRegion.Should().BeEquivalentTo(
            [
                new RegionCountDto { Region = "Australia", Count = 1 },
                new RegionCountDto { Region = "Latin America", Count = 2 },
                new RegionCountDto { Region = "Southeast Asia", Count = 1 }
            ],
            options => options.WithStrictOrdering());
    }

    // protects: Default TMM queue sections always present, Mid Wars routed properly, and custom/other queues included only when > 0.
    // fails_when: Default sections disappear when empty or active Mid Wars / Rift Wars queues are omitted.
    [Fact]
    public void Compute_IncludesDefaultQueuesAndActiveCustomQueues()
    {
        List<MatchmakingGroup> groups =
        [
            CreateGroup(memberCount: 2, regions: ["EU"], gameType: ChatProtocol.TMMGameType.TMM_GAME_TYPE_MIDWARS, mapName: "midwars"),
            CreateGroup(memberCount: 1, regions: ["LAT", "BR"], gameType: ChatProtocol.TMMGameType.TMM_GAME_TYPE_RIFTWARS, mapName: "riftwars")
        ];

        PopulationBreakdownResponseDto result = PopulationBreakdownAggregator.Compute(
            onlinePlayers: 15,
            servers: [],
            groups: groups,
            updatedAtUtc: FixedUtcNow);

        result.TmmQueues.Select(q => q.Title).Should().ContainInOrder(
            PopulationBreakdownDefaults.QueueTitleForestsOfCaldavarRankedNormal,
            PopulationBreakdownDefaults.QueueTitleMidWars,
            "Rift Wars");

        result.TmmQueues[0].TotalQueued.Should().Be(0);
        result.TmmQueues[1].TotalQueued.Should().Be(2);
        result.TmmQueues[1].Regions.Single(r => r.Region == "Europe").Count.Should().Be(2);

        result.TmmQueues[2].TotalQueued.Should().Be(1);
        result.TmmQueues[2].Regions.Single(r => r.Region == "Latin America").Count.Should().Be(1, "LAT + BR deduplicate per group into Latin America");
    }

    // protects: 5-second throttle for unchanged snapshots, immediate Redis flush + [TelemetryPublisher][Population] log on count change.
    // fails_when: Unchanged ticks spam Redis within 5s, changes wait 5s, or JSON keys fail AOT ChatServerJsonContext round-trip.
    [Fact]
    public async Task TelemetryPublisherService_PublishPopulationBreakdownAsync_ThrottlesUnchangedTo5sAndFlushesImmediatelyOnChange()
    {
        string dbName = $"TelemetryPop_{Guid.NewGuid():N}";
        ServiceCollection services = new();
        services.AddDbContext<MerrickContext>(options => options.UseInMemoryDatabase(dbName));
        await using ServiceProvider sp = services.BuildServiceProvider();

        ManualTimeProvider timeProvider = new(FixedUtcNow);
        ListCapturingLogger<TelemetryPublisherService> logger = new(LogLevel.Debug);

        List<string> publishedPayloads = [];
        Mock<IDatabase> mockDb = new();
        mockDb.Setup(d => d.HashSetAsync(
                TelemetryPublisherService.TelemetryHashKey,
                It.IsAny<HashEntry[]>(),
                It.IsAny<CommandFlags>()))
            .Returns(Task.CompletedTask);
        mockDb.Setup(d => d.HashSetAsync(
                TelemetryPublisherService.TelemetryHashKey,
                TelemetryPublisherService.PopulationBreakdownField,
                It.IsAny<RedisValue>(),
                It.IsAny<When>(),
                It.IsAny<CommandFlags>()))
            .Callback<RedisKey, RedisValue, RedisValue, When, CommandFlags>((_, _, val, _, _) =>
                publishedPayloads.Add(val.ToString()))
            .ReturnsAsync(true);

        Mock<IConnectionMultiplexer> mockMux = new();
        mockMux.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object?>())).Returns(mockDb.Object);
        mockMux.Setup(m => m.GetSubscriber(It.IsAny<object?>())).Returns((ISubscriber)null!);

        await using TelemetryPublisherService sut = new(
            sp,
            logger,
            mockMux.Object,
            timeProvider,
            databaseSyncThrottle: TimeSpan.FromSeconds(1),
            commandFlushInterval: TimeSpan.FromSeconds(30),
            populationPublishInterval: TimeSpan.FromSeconds(5));

        await sut.ResetBaselineOnStartupAsync();
        publishedPayloads.Clear();
        logger.Entries.Clear();

        // 1. Unchanged empty snapshot at t+1s -> throttled (zero Redis writes)
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        PopulationBreakdownResponseDto emptyAt1s = PopulationBreakdownAggregator.CreateEmpty(timeProvider.GetUtcNow(), 0);
        await sut.PublishPopulationBreakdownAsync(emptyAt1s);
        publishedPayloads.Should().BeEmpty("unchanged snapshot within 5s window must be throttled");

        // 2. Count change at t+2s -> IMMEDIATE flush + [TelemetryPublisher][Population] Info log
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        PopulationBreakdownResponseDto activeAt2s = PopulationBreakdownAggregator.Compute(
            onlinePlayers: 25,
            servers: [CreateServer(1, "USE", ChatProtocol.ServerStatus.SERVER_STATUS_ACTIVE)],
            groups: [CreateGroup(1, ["USE", "USW"])],
            updatedAtUtc: timeProvider.GetUtcNow());

        await sut.PublishPopulationBreakdownAsync(activeAt2s);
        publishedPayloads.Should().ContainSingle("queue/server change must flush immediately without waiting 5s");
        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Information &&
            e.Message.Contains("[TelemetryPublisher][Population]") &&
            e.Message.Contains("OnlinePlayers=25") &&
            e.Message.Contains("GamesInProgress=1") &&
            e.Message.Contains("QueuedPlayers=1"));

        PopulationBreakdownResponseDto? deserialized = JsonSerializer.Deserialize(
            publishedPayloads[0],
            ChatServerJsonContext.Default.PopulationBreakdownResponseDto);
        deserialized.Should().NotBeNull();
        deserialized!.OnlinePlayers.Should().Be(25);
        deserialized.GamesInProgressTotal.Should().Be(1);

        // 3. Unchanged active snapshot at t+4s (2s after last publish) -> throttled
        timeProvider.Advance(TimeSpan.FromSeconds(2));
        PopulationBreakdownResponseDto sameAt4s = PopulationBreakdownAggregator.Compute(
            onlinePlayers: 25,
            servers: [CreateServer(1, "USE", ChatProtocol.ServerStatus.SERVER_STATUS_ACTIVE)],
            groups: [CreateGroup(1, ["USE", "USW"])],
            updatedAtUtc: timeProvider.GetUtcNow());
        await sut.PublishPopulationBreakdownAsync(sameAt4s);
        publishedPayloads.Should().HaveCount(1);

        // 4. Unchanged active snapshot at t+7.5s (5.5s after last publish) -> 5s heartbeat refresh (Debug log only)
        logger.Entries.Clear();
        timeProvider.Advance(TimeSpan.FromMilliseconds(3500));
        PopulationBreakdownResponseDto sameAt75s = PopulationBreakdownAggregator.Compute(
            onlinePlayers: 25,
            servers: [CreateServer(1, "USE", ChatProtocol.ServerStatus.SERVER_STATUS_ACTIVE)],
            groups: [CreateGroup(1, ["USE", "USW"])],
            updatedAtUtc: timeProvider.GetUtcNow());
        await sut.PublishPopulationBreakdownAsync(sameAt75s);

        publishedPayloads.Should().HaveCount(2, "5s throttle elapsed must refresh heartbeat timestamp in Redis");
        logger.Entries.Should().NotContain(e => e.Level == LogLevel.Information && e.Message.Contains("[TelemetryPublisher][Population]"));
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Debug && e.Message.Contains("[TelemetryPublisher][Population]"));
    }

    // protects: MatchmakingService broker tick seam computes and forwards queued groups to ITelemetryPublisherService.PublishPopulationBreakdownAsync.
    // fails_when: MatchmakingService.PublishPopulationBreakdownSnapshotAsync fails to deduplicate USE+USW or does not invoke telemetry publisher.
    [Fact]
    public async Task MatchmakingService_PublishPopulationBreakdownSnapshotAsync_ForwardsComputedSnapshotToTelemetryPublisher()
    {
        Mock<IChatContext> mockChatContext = new();
        mockChatContext.SetupGet(c => c.ClientChatSessions).Returns(new ConcurrentDictionary<string, ChatSession>());
        mockChatContext.SetupGet(c => c.MatchServerChatSessions).Returns(new ConcurrentDictionary<int, ChatSession>());

        PopulationBreakdownResponseDto? captured = null;
        Mock<ITelemetryPublisherService> mockPublisher = new();
        mockPublisher
            .Setup(p => p.PublishPopulationBreakdownAsync(It.IsAny<PopulationBreakdownResponseDto>(), It.IsAny<bool>()))
            .Callback<PopulationBreakdownResponseDto, bool>((dto, _) => captured = dto)
            .Returns(Task.CompletedTask);

        IConfiguration config = new ConfigurationBuilder().Build();
        MatchmakingService service = new(
            config,
            mockChatContext.Object,
            telemetryPublisher: mockPublisher.Object,
            timeProvider: new ManualTimeProvider(FixedUtcNow));

        service.Groups[1] = CreateGroup(memberCount: 2, regions: ["USE", "USW"]);
        service.Groups[2] = CreateGroup(memberCount: 1, regions: ["AU", "USE"], gameType: ChatProtocol.TMMGameType.TMM_GAME_TYPE_MIDWARS, mapName: "midwars");

        await service.PublishPopulationBreakdownSnapshotAsync(timestampUtc: FixedUtcNow);

        captured.Should().NotBeNull();
        captured!.UpdatedAtUtc.Should().Be(FixedUtcNow);
        captured.TmmQueues[0].TotalQueued.Should().Be(2);
        captured.TmmQueues[0].Regions.Single(r => r.Region == "US (shared pool)").Count.Should().Be(2);
        captured.TmmQueues[1].Title.Should().Be("Mid Wars");
        captured.TmmQueues[1].TotalQueued.Should().Be(1);
        captured.TmmQueues[1].Regions.Single(r => r.Region == "Australia").Count.Should().Be(1);
        captured.TmmQueues[1].Regions.Single(r => r.Region == "US (shared pool)").Count.Should().Be(1);
    }

    private sealed class ManualTimeProvider(DateTimeOffset initialUtcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = initialUtcNow;

        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public override long GetTimestamp() => _utcNow.UtcTicks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }

    private sealed class ListCapturingLogger<T>(LogLevel minLevel) : ILogger<T>
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= minLevel;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            Entries.Enqueue((logLevel, formatter(state, exception)));
        }
    }
}
