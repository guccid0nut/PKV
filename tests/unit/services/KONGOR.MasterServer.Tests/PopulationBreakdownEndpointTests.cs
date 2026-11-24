// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using ASPIRE.Common.Caching;
using ASPIRE.Common.Models.ServerManagement;
using FluentAssertions;
using KONGOR.MasterServer.Constants;
using KONGOR.MasterServer.Controllers;
using KONGOR.MasterServer.Features.Telemetry;
using KONGOR.MasterServer.Serialization;
using KONGOR.MasterServer.Tests.Infrastructure;
using MediatR;
using MemoryPack;
using MERRICK.DatabaseContext.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NEXUS.Contracts.CQRS.Telemetry;
using StackExchange.Redis;
using Xunit;

namespace KONGOR.MasterServer.Tests;

public sealed class PopulationBreakdownEndpointTests
{
    private static readonly DateTimeOffset FixedUtcNow = new(2026, 11, 21, 2, 0, 0, TimeSpan.Zero);

    private static MatchServer CreateMatchServer(
        int id,
        string hostAccountName,
        int instance,
        int port,
        string location,
        ServerStatus status,
        DateTimeOffset now)
    {
        return new MatchServer
        {
            ID = id,
            HostAccountID = 100 + id,
            HostAccountName = hostAccountName,
            Name = $"Server-{id}",
            Instance = instance,
            IPAddress = "127.0.0.1",
            Port = port,
            Location = location,
            Description = "Test server",
            Status = status,
            TimestampRegistered = now.AddMinutes(-1),
            TimestampLastHeartbeat = now
        };
    }

    // protects: Fresh Redis PopulationBreakdownJson is returned directly with reconciled live CCU and zero MATCH-SERVERS scan.
    // fails_when: Handler ignores Redis snapshot, fails to reconcile CCU, or unnecessarily scans MATCH-SERVERS.
    [Fact]
    public async Task GetPopulationBreakdownHandler_ReadsFreshRedisSnapshot_AndReconcilesLiveCcu()
    {
        ManualTimeProvider timeProvider = new(FixedUtcNow);
        ListCapturingLogger<GetPopulationBreakdownHandler> logger = new(LogLevel.Information);

        PopulationBreakdownResponseDto storedSnapshot = new()
        {
            OnlinePlayers = 100,
            GamesInProgressTotal = 3,
            GamesInProgressByRegion =
            [
                new RegionCountDto { Region = PopulationBreakdownDefaults.AllLocationsLabel, Count = 3 },
                new RegionCountDto { Region = "Europe", Count = 1 },
                new RegionCountDto { Region = "US East", Count = 2 }
            ],
            ServersAvailableByRegion =
            [
                new RegionCountDto { Region = "Australia", Count = 4 },
                new RegionCountDto { Region = "US West", Count = 2 }
            ],
            TmmQueues =
            [
                new TmmQueueBreakdownDto
                {
                    Title = PopulationBreakdownDefaults.QueueTitleForestsOfCaldavarRankedNormal,
                    TotalQueued = 5,
                    Regions =
                    [
                        new RegionCountDto { Region = "Australia", Count = 1 },
                        new RegionCountDto { Region = "Europe", Count = 0 },
                        new RegionCountDto { Region = "Latin America", Count = 0 },
                        new RegionCountDto { Region = "Any region", Count = 1 },
                        new RegionCountDto { Region = "Southeast Asia", Count = 0 },
                        new RegionCountDto { Region = "US (shared pool)", Count = 3 }
                    ]
                },
                new TmmQueueBreakdownDto
                {
                    Title = PopulationBreakdownDefaults.QueueTitleMidWars,
                    TotalQueued = 2,
                    Regions =
                    [
                        new RegionCountDto { Region = "Australia", Count = 0 },
                        new RegionCountDto { Region = "Europe", Count = 2 },
                        new RegionCountDto { Region = "Latin America", Count = 0 },
                        new RegionCountDto { Region = "Any region", Count = 0 },
                        new RegionCountDto { Region = "Southeast Asia", Count = 0 },
                        new RegionCountDto { Region = "US (shared pool)", Count = 0 }
                    ]
                }
            ],
            UpdatedAtUtc = FixedUtcNow.AddSeconds(-10)
        };

        string snapshotJson = JsonSerializer.Serialize(
            storedSnapshot,
            MasterServerJsonContext.Default.PopulationBreakdownResponseDto);

        Mock<IDatabase> redisMock = new();
        redisMock.Setup(r => r.HashGetAsync(
                PopulationBreakdownDefaults.TelemetryRedisHashKey,
                "CCU",
                CommandFlags.None))
            .ReturnsAsync(new RedisValue("128"));
        redisMock.Setup(r => r.HashGetAsync(
                PopulationBreakdownDefaults.TelemetryRedisHashKey,
                PopulationBreakdownDefaults.PopulationBreakdownRedisField,
                CommandFlags.None))
            .ReturnsAsync(new RedisValue(snapshotJson));

        GetPopulationBreakdownHandler handler = new(
            redisMock.Object,
            logger,
            hybridCache: null,
            timeProvider: timeProvider);

        PopulationBreakdownResponseDto result = await handler.Handle(new GetPopulationBreakdownQuery(), default);

        result.OnlinePlayers.Should().Be(128, "live TELEMETRY:METRICS:CCU should reconcile onlinePlayers");
        result.GamesInProgressTotal.Should().Be(3);
        result.GamesInProgressByRegion.Should().HaveCount(3);
        result.ServersAvailableByRegion.Should().HaveCount(2);
        result.TmmQueues.Should().HaveCount(2);
        result.TmmQueues[0].Regions.Single(r => r.Region == "US (shared pool)").Count.Should().Be(3);

        redisMock.Verify(r => r.HashGetAllAsync("MATCH-SERVERS", It.IsAny<CommandFlags>()), Times.Never);
        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Information &&
            e.Message.Contains("[LiveTelemetry][Population]") &&
            e.Message.Contains("Source=redis_snapshot") &&
            e.Message.Contains("OnlinePlayers=128") &&
            e.Message.Contains("GamesInProgressTotal=3") &&
            e.Message.Contains("AvailableServersTotal=6") &&
            e.Message.Contains("TotalQueuedPlayers=7"));
    }

    // protects: Graceful fallback to MATCH-SERVERS when PopulationBreakdownJson is missing, excluding QueueArcade servers and returning valid default TMM queues.
    // fails_when: Missing snapshot returns null/broken JSON, includes QueueArcade servers, or omits default TMM sections.
    [Fact]
    public async Task GetPopulationBreakdownHandler_MissingSnapshot_FallsBackToLiveMatchServers_ExcludingQueueArcade()
    {
        DateTimeOffset now = TimeProvider.System.GetUtcNow();
        ManualTimeProvider timeProvider = new(now);
        ListCapturingLogger<GetPopulationBreakdownHandler> logger = new(LogLevel.Information);

        MatchServer[] cachedServers =
        [
            CreateMatchServer(1, "host_use", 1, 11235, "USE", ServerStatus.SERVER_STATUS_ACTIVE, now),
            CreateMatchServer(2, "host_usw", 2, 11236, "USW", ServerStatus.SERVER_STATUS_LOADING, now),
            CreateMatchServer(3, "host_br", 3, 11237, "BR", ServerStatus.SERVER_STATUS_ACTIVE, now),
            CreateMatchServer(4, "host_eu", 4, 11238, "EU", ServerStatus.SERVER_STATUS_IDLE, now),
            CreateMatchServer(5, "host_au", 5, 11239, "AU", ServerStatus.SERVER_STATUS_SLEEPING, now),
            // Excluded QueueArcade server (host suffix + Tier Q instance + Tier Q port)
            CreateMatchServer(6, "kongor_arcade_use_arc", 4001, 11485, "USE", ServerStatus.SERVER_STATUS_ACTIVE, now),
            // Excluded crashed server
            CreateMatchServer(7, "host_dead", 7, 11240, "EU", ServerStatus.SERVER_STATUS_CRASHED, now)
        ];

        HashEntry[] matchServerEntries =
        [
            .. cachedServers.Select(s => new HashEntry(
                $"{s.HostAccountName}:{s.Port}",
                MemoryPackSerializer.Serialize(s)))
        ];

        Mock<IDatabase> redisMock = new();
        redisMock.Setup(r => r.HashGetAsync(
                PopulationBreakdownDefaults.TelemetryRedisHashKey,
                "CCU",
                CommandFlags.None))
            .ReturnsAsync(new RedisValue("77"));
        redisMock.Setup(r => r.HashGetAsync(
                PopulationBreakdownDefaults.TelemetryRedisHashKey,
                PopulationBreakdownDefaults.PopulationBreakdownRedisField,
                CommandFlags.None))
            .ReturnsAsync(RedisValue.Null);
        redisMock.Setup(r => r.HashGetAllAsync("MATCH-SERVERS", CommandFlags.None))
            .ReturnsAsync(matchServerEntries);

        GetPopulationBreakdownHandler handler = new(
            redisMock.Object,
            logger,
            hybridCache: null,
            timeProvider: timeProvider);

        PopulationBreakdownResponseDto result = await handler.Handle(new GetPopulationBreakdownQuery(), default);

        result.OnlinePlayers.Should().Be(77);
        result.GamesInProgressTotal.Should().Be(3, "QueueArcade and crashed servers must be excluded");
        result.GamesInProgressByRegion.Should().BeEquivalentTo(
            [
                new RegionCountDto { Region = "All locations", Count = 3 },
                new RegionCountDto { Region = "Latin America", Count = 1 },
                new RegionCountDto { Region = "US East", Count = 1 },
                new RegionCountDto { Region = "US West", Count = 1 }
            ],
            options => options.WithStrictOrdering());

        result.ServersAvailableByRegion.Should().BeEquivalentTo(
            [
                new RegionCountDto { Region = "Australia", Count = 1 },
                new RegionCountDto { Region = "Europe", Count = 1 }
            ],
            options => options.WithStrictOrdering());

        result.TmmQueues.Should().HaveCount(2);
        result.TmmQueues[0].Title.Should().Be(PopulationBreakdownDefaults.QueueTitleForestsOfCaldavarRankedNormal);
        result.TmmQueues[0].TotalQueued.Should().Be(0);
        result.TmmQueues[0].Regions.Should().HaveCount(6);
        result.TmmQueues[0].Regions.Should().OnlyContain(r => r.Count == 0);
        result.TmmQueues[1].Title.Should().Be(PopulationBreakdownDefaults.QueueTitleMidWars);
        result.TmmQueues[1].TotalQueued.Should().Be(0);
        result.TmmQueues[1].Regions.Should().HaveCount(6);

        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Information &&
            e.Message.Contains("[LiveTelemetry][Population]") &&
            e.Message.Contains("Source=live_server_fallback") &&
            e.Message.Contains("OnlinePlayers=77") &&
            e.Message.Contains("GamesInProgressTotal=3") &&
            e.Message.Contains("AvailableServersTotal=2") &&
            e.Message.Contains("TotalQueuedPlayers=0"));
    }

    // protects: Snapshots older than 60 seconds trigger live MATCH-SERVERS fallback rather than serving stale counts.
    // fails_when: A 61-second-old Redis snapshot is served verbatim instead of falling back.
    [Fact]
    public async Task GetPopulationBreakdownHandler_StaleSnapshotOlderThan60Seconds_FallsBackToLiveMatchServers()
    {
        DateTimeOffset now = TimeProvider.System.GetUtcNow();
        ManualTimeProvider timeProvider = new(now);
        ListCapturingLogger<GetPopulationBreakdownHandler> logger = new(LogLevel.Information);

        PopulationBreakdownResponseDto staleSnapshot = PopulationBreakdownDefaults.CreateEmpty(now.AddSeconds(-61), onlinePlayers: 999);
        staleSnapshot.GamesInProgressTotal = 99;
        string staleJson = JsonSerializer.Serialize(
            staleSnapshot,
            MasterServerJsonContext.Default.PopulationBreakdownResponseDto);

        MatchServer freshActiveServer = CreateMatchServer(1, "host_eu", 1, 11235, "EU", ServerStatus.SERVER_STATUS_ACTIVE, now);

        Mock<IDatabase> redisMock = new();
        redisMock.Setup(r => r.HashGetAsync(
                PopulationBreakdownDefaults.TelemetryRedisHashKey,
                "CCU",
                CommandFlags.None))
            .ReturnsAsync(new RedisValue("14"));
        redisMock.Setup(r => r.HashGetAsync(
                PopulationBreakdownDefaults.TelemetryRedisHashKey,
                PopulationBreakdownDefaults.PopulationBreakdownRedisField,
                CommandFlags.None))
            .ReturnsAsync(new RedisValue(staleJson));
        redisMock.Setup(r => r.HashGetAllAsync("MATCH-SERVERS", CommandFlags.None))
            .ReturnsAsync([new HashEntry("host_eu:11235", MemoryPackSerializer.Serialize(freshActiveServer))]);

        GetPopulationBreakdownHandler handler = new(
            redisMock.Object,
            logger,
            hybridCache: null,
            timeProvider: timeProvider);

        PopulationBreakdownResponseDto result = await handler.Handle(new GetPopulationBreakdownQuery(), default);

        result.OnlinePlayers.Should().Be(14);
        result.GamesInProgressTotal.Should().Be(1, "must reflect live MATCH-SERVERS fallback instead of stale 99");
        result.GamesInProgressByRegion.Should().Contain(r => r.Region == "Europe" && r.Count == 1);
        logger.Entries.Should().Contain(e => e.Message.Contains("Source=live_server_fallback") && e.Message.Contains("stale_age_"));
    }

    // protects: INexusHybridCache wiring uses key "TELEMETRY:POPULATION" with 5-second L1/L2 TTL.
    // fails_when: Cache key or 5-second TTL drifts.
    [Fact]
    public async Task GetPopulationBreakdownHandler_UsesHybridCacheWith5SecondTtlUnderTelemetryPopulationKey()
    {
        Mock<IDatabase> redisMock = new();
        Mock<INexusHybridCache> hybridCacheMock = new();

        PopulationBreakdownResponseDto expected = PopulationBreakdownDefaults.CreateEmpty(FixedUtcNow, onlinePlayers: 55);

        hybridCacheMock
            .Setup(c => c.GetOrCreateAsync(
                GetPopulationBreakdownHandler.PopulationCacheKey,
                It.IsAny<Func<CancellationToken, Task<PopulationBreakdownResponseDto?>>>(),
                It.Is<IReadOnlyCollection<string>?>(tags => tags != null && tags.Contains("telemetry")),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        GetPopulationBreakdownHandler handler = new(
            redisMock.Object,
            NullLogger<GetPopulationBreakdownHandler>.Instance,
            hybridCache: hybridCacheMock.Object,
            timeProvider: new ManualTimeProvider(FixedUtcNow));

        PopulationBreakdownResponseDto actual = await handler.Handle(new GetPopulationBreakdownQuery(), default);

        actual.Should().BeSameAs(expected);
        hybridCacheMock.VerifyAll();
    }

    // protects: Controller route/attributes, LocalLiveTelemetryMediator dispatch, camelCase JSON schema for Lua lib_json.decode, and MemoryPack round-trip.
    // fails_when: Route/rate-limit/Produces attributes drift, JSON property names diverge from camelCase Lua contract, or MemoryPack serialization breaks.
    [Fact]
    public async Task LiveTelemetryController_GetPopulationBreakdown_ReturnsExpectedCamelCaseJsonAndAttributes()
    {
        MethodInfo method = typeof(LiveTelemetryController).GetMethod(nameof(LiveTelemetryController.GetPopulationBreakdown))!;
        method.Should().NotBeNull();

        HttpGetAttribute? httpGet = method.GetCustomAttribute<HttpGetAttribute>();
        httpGet.Should().NotBeNull();
        httpGet!.Template.Should().Be("population");

        EnableRateLimitingAttribute? rateLimit = method.GetCustomAttribute<EnableRateLimitingAttribute>();
        rateLimit.Should().NotBeNull();
        rateLimit!.PolicyName.Should().Be(RateLimiterPolicies.Relaxed);

        ProducesAttribute? produces = method.GetCustomAttribute<ProducesAttribute>();
        produces.Should().NotBeNull();
        produces!.ContentTypes.Should().Contain("application/json");

        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        Mock<IDatabase> redisMock = new();
        PopulationBreakdownResponseDto snapshot = new()
        {
            OnlinePlayers = 312,
            GamesInProgressTotal = 4,
            GamesInProgressByRegion =
            [
                new RegionCountDto { Region = "All locations", Count = 4 },
                new RegionCountDto { Region = "US East", Count = 4 }
            ],
            ServersAvailableByRegion =
            [
                new RegionCountDto { Region = "US East", Count = 8 }
            ],
            TmmQueues = PopulationBreakdownDefaults.CreateDefaultTmmQueues(),
            UpdatedAtUtc = FixedUtcNow
        };
        snapshot.TmmQueues[0].TotalQueued = 2;
        snapshot.TmmQueues[0].Regions.Single(r => r.Region == "US (shared pool)").Count = 2;

        string storedJson = JsonSerializer.Serialize(
            snapshot,
            MasterServerJsonContext.Default.PopulationBreakdownResponseDto);

        redisMock.Setup(r => r.HashGetAsync(
                PopulationBreakdownDefaults.TelemetryRedisHashKey,
                "CCU",
                CommandFlags.None))
            .ReturnsAsync(new RedisValue("312"));
        redisMock.Setup(r => r.HashGetAsync(
                PopulationBreakdownDefaults.TelemetryRedisHashKey,
                PopulationBreakdownDefaults.PopulationBreakdownRedisField,
                CommandFlags.None))
            .ReturnsAsync(new RedisValue(storedJson));

        GetLiveTelemetryHandler liveHandler = new(db, redisMock.Object, NullLogger<GetLiveTelemetryHandler>.Instance);
        GetPopulationBreakdownHandler popHandler = new(
            redisMock.Object,
            NullLogger<GetPopulationBreakdownHandler>.Instance,
            hybridCache: null,
            timeProvider: new ManualTimeProvider(FixedUtcNow));
        LocalLiveTelemetryMediator localMediator = new(liveHandler, popHandler);

        LiveTelemetryController controller = new(localMediator, NullLogger<LiveTelemetryController>.Instance);

        IActionResult actionResult = await controller.GetPopulationBreakdown(default);

        OkObjectResult ok = actionResult.Should().BeOfType<OkObjectResult>().Subject;
        PopulationBreakdownResponseDto dto = ok.Value.Should().BeOfType<PopulationBreakdownResponseDto>().Subject;

        // Verify JSON serialization shape (both source-gen context and ASP.NET Core web defaults) for Lua lib_json.decode
        string aotJson = JsonSerializer.Serialize(dto, MasterServerJsonContext.Default.PopulationBreakdownResponseDto);
        string webJson = JsonSerializer.Serialize(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        foreach (string json in new[] { aotJson, webJson })
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            root.GetProperty("onlinePlayers").GetInt32().Should().Be(312);
            root.GetProperty("gamesInProgressTotal").GetInt32().Should().Be(4);
            root.GetProperty("updatedAtUtc").GetDateTimeOffset().Should().Be(FixedUtcNow);

            JsonElement gamesByRegion = root.GetProperty("gamesInProgressByRegion");
            gamesByRegion.GetArrayLength().Should().Be(2);
            gamesByRegion[0].GetProperty("region").GetString().Should().Be("All locations");
            gamesByRegion[0].GetProperty("count").GetInt32().Should().Be(4);
            gamesByRegion[1].GetProperty("region").GetString().Should().Be("US East");
            gamesByRegion[1].GetProperty("count").GetInt32().Should().Be(4);

            JsonElement serversByRegion = root.GetProperty("serversAvailableByRegion");
            serversByRegion.GetArrayLength().Should().Be(1);
            serversByRegion[0].GetProperty("region").GetString().Should().Be("US East");
            serversByRegion[0].GetProperty("count").GetInt32().Should().Be(8);

            JsonElement tmmQueues = root.GetProperty("tmmQueues");
            tmmQueues.GetArrayLength().Should().Be(2);
            tmmQueues[0].GetProperty("title").GetString().Should().Be(PopulationBreakdownDefaults.QueueTitleForestsOfCaldavarRankedNormal);
            tmmQueues[0].GetProperty("totalQueued").GetInt32().Should().Be(2);
            tmmQueues[0].GetProperty("regions").GetArrayLength().Should().Be(6);
            tmmQueues[0].GetProperty("regions")[5].GetProperty("region").GetString().Should().Be("US (shared pool)");
            tmmQueues[0].GetProperty("regions")[5].GetProperty("count").GetInt32().Should().Be(2);
        }

        // Verify MemoryPack round-trip (used by INexusHybridCache L2 serializer)
        byte[] packed = MemoryPackSerializer.Serialize(dto);
        PopulationBreakdownResponseDto? unpacked = MemoryPackSerializer.Deserialize<PopulationBreakdownResponseDto>(packed);
        unpacked.Should().BeEquivalentTo(dto);
    }

    // protects: Real MediatR assembly scanning in KONGOR.MasterServer resolves GetPopulationBreakdownHandler cleanly without missing DI deps.
    // fails_when: MediatR cannot construct GetPopulationBreakdownHandler or resolve GetPopulationBreakdownQuery from KONGOR.MasterServer assembly.
    [Fact]
    public async Task RealMediatR_ResolvesGetPopulationBreakdownQuery_FromMasterServerAssembly()
    {
        Mock<IDatabase> redisMock = new();
        redisMock.Setup(r => r.HashGetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisValue.Null);
        redisMock.Setup(r => r.HashGetAllAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync([]);

        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(redisMock.Object);
        services.AddSingleton<TimeProvider>(new ManualTimeProvider(FixedUtcNow));
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(LiveTelemetryController).Assembly));

        await using ServiceProvider sp = services.BuildServiceProvider();
        IMediator mediator = sp.GetRequiredService<IMediator>();

        PopulationBreakdownResponseDto result = await mediator.Send(new GetPopulationBreakdownQuery());

        result.Should().NotBeNull();
        result.GamesInProgressByRegion.Should().ContainSingle(r => r.Region == "All locations" && r.Count == 0);
        result.TmmQueues.Should().HaveCount(2);
    }

    // protects: Corrupted Redis JSON or Redis exceptions never fail the endpoint and always return valid zero-filled fallback JSON.
    // fails_when: JsonException or RedisException propagates out of GetPopulationBreakdownHandler.
    [Fact]
    public async Task GetPopulationBreakdownHandler_CorruptedJsonOrRedisException_ReturnsValidZeroFilledFallback()
    {
        Mock<IDatabase> redisMock = new();
        redisMock.Setup(r => r.HashGetAsync(
                PopulationBreakdownDefaults.TelemetryRedisHashKey,
                "CCU",
                CommandFlags.None))
            .ReturnsAsync(new RedisValue("19"));
        redisMock.Setup(r => r.HashGetAsync(
                PopulationBreakdownDefaults.TelemetryRedisHashKey,
                PopulationBreakdownDefaults.PopulationBreakdownRedisField,
                CommandFlags.None))
            .ReturnsAsync(new RedisValue("{not-valid-json"));
        redisMock.Setup(r => r.HashGetAllAsync("MATCH-SERVERS", CommandFlags.None))
            .ThrowsAsync(new InvalidOperationException("Simulated Redis outage"));

        GetPopulationBreakdownHandler handler = new(
            redisMock.Object,
            NullLogger<GetPopulationBreakdownHandler>.Instance,
            hybridCache: null,
            timeProvider: new ManualTimeProvider(FixedUtcNow));

        PopulationBreakdownResponseDto result = await handler.Handle(new GetPopulationBreakdownQuery(), default);

        result.OnlinePlayers.Should().Be(19);
        result.GamesInProgressTotal.Should().Be(0);
        result.GamesInProgressByRegion.Should().ContainSingle(r => r.Region == "All locations" && r.Count == 0);
        result.ServersAvailableByRegion.Should().BeEmpty();
        result.TmmQueues.Should().HaveCount(2);
        result.TmmQueues.Should().OnlyContain(q => q.TotalQueued == 0 && q.Regions.Count == 6);
    }

    private sealed class ManualTimeProvider(DateTimeOffset initialUtcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = initialUtcNow;

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
