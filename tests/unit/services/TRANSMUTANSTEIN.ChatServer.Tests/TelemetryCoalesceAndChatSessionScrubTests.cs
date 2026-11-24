// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using ASPIRE.Common.Telemetry;
using FluentAssertions;
using MERRICK.DatabaseContext.Entities.Game;
using MERRICK.DatabaseContext.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;
using TRANSMUTANSTEIN.ChatServer.CommandProcessors.Connection;
using TRANSMUTANSTEIN.ChatServer.Contracts;
using TRANSMUTANSTEIN.ChatServer.Domain.Communication;
using TRANSMUTANSTEIN.ChatServer.Domain.Core;
using TRANSMUTANSTEIN.ChatServer.Internals;
using TRANSMUTANSTEIN.ChatServer.Services;
using Xunit;

namespace TRANSMUTANSTEIN.ChatServer.Tests;

public sealed class TelemetryCoalesceAndChatSessionScrubTests
{
    private static IServiceProvider BuildTelemetryServiceProvider(
        string dbName,
        Action? onScopeCreated = null)
    {
        ServiceCollection services = new();
        services.AddDbContext<MerrickContext>(options =>
            options.UseInMemoryDatabase(dbName));
        ServiceProvider innerProvider = services.BuildServiceProvider();

        return onScopeCreated is null
            ? innerProvider
            : new CountingServiceProvider(innerProvider, onScopeCreated);
    }

    // protects: Zero idle SQL queries and zero idle Redis Pub/Sub writes during repeated MatchmakingService ticks.
    // fails_when: RecordQueueStateAsync(0) opens MerrickContext scopes or writes Redis HashSet/Publish when nothing changed.
    // seam: Real TelemetryPublisherService + real MerrickContext (InMemory) + Mock<IConnectionMultiplexer>/<IDatabase>/<ISubscriber>.
    [Fact]
    public async Task TelemetryPublisherService_IdleQueueTicks_IssueZeroSqlAndZeroRedis()
    {
        int dbScopeCount = 0;
        string dbName = $"TelemetryIdle_{Guid.NewGuid():N}";
        await using CountingServiceProvider sp = new(
            (ServiceProvider)BuildTelemetryServiceProvider(dbName),
            () => Interlocked.Increment(ref dbScopeCount));

        Mock<IDatabase> mockDb = new(MockBehavior.Strict);
        mockDb.Setup(d => d.HashSetAsync(
                TelemetryPublisherService.TelemetryHashKey,
                It.IsAny<HashEntry[]>(),
                It.IsAny<CommandFlags>()))
            .Returns(Task.CompletedTask);

        Mock<ISubscriber> mockSub = new(MockBehavior.Strict);
        mockSub.Setup(s => s.PublishAsync(
                It.IsAny<RedisChannel>(),
                It.IsAny<RedisValue>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(1L);

        Mock<IConnectionMultiplexer> mockMux = new();
        mockMux.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object?>())).Returns(mockDb.Object);
        mockMux.Setup(m => m.GetSubscriber(It.IsAny<object?>())).Returns(mockSub.Object);

        await using TelemetryPublisherService sut = new(
            sp,
            NullLogger<TelemetryPublisherService>.Instance,
            mockMux.Object,
            TimeProvider.System,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(5));

        await sut.ResetBaselineOnStartupAsync();
        int baselineScopes = Volatile.Read(ref dbScopeCount);
        baselineScopes.Should().Be(1);

        // Reset invocations after startup baseline
        mockDb.Invocations.Clear();
        mockSub.Invocations.Clear();

        // Simulate 10 consecutive idle matchmaking ticks with 0 queued groups
        for (int i = 0; i < 10; i++)
        {
            await sut.RecordQueueStateAsync(0);
        }

        Volatile.Read(ref dbScopeCount).Should().Be(baselineScopes, "unchanged idle ticks must skip SQL SELECT/UPDATE completely");
        mockDb.Invocations.Should().BeEmpty("unchanged idle ticks must skip Redis HashSetAsync");
        mockSub.Invocations.Should().BeEmpty("unchanged idle ticks must skip Redis Pub/Sub QueueStateChanged");
    }

    // protects: Zero-loss telemetry for throttled non-queue metric transitions + trailing-edge flush + DisposeAsync final flush.
    // fails_when: RecordQueueStateAsync early-return skips dirty non-queue fields, throttled updates inside 1s are dropped, or DisposeAsync leaves dirty state unsynced.
    // seam: Real TelemetryPublisherService with real MerrickContext + trailing-edge timer + IAsyncDisposable shutdown.
    [Fact]
    public async Task TelemetryPublisherService_ThrottledChanges_FlushViaTrailingEdgeAndDisposeAsync()
    {
        string dbName = $"TelemetryCoalesce_{Guid.NewGuid():N}";
        await using ServiceProvider sp = (ServiceProvider)BuildTelemetryServiceProvider(dbName);

        TelemetryPublisherService sut = new(
            sp,
            NullLogger<TelemetryPublisherService>.Instance,
            multiplexer: null,
            TimeProvider.System,
            databaseSyncThrottle: TimeSpan.FromMilliseconds(80),
            commandFlushInterval: TimeSpan.FromSeconds(30));

        await sut.ResetBaselineOnStartupAsync();

        // Wait past initial throttle window so p1 flushes immediately
        await Task.Delay(110);
        await sut.OnClientConnectedAsync("player1");

        using (IServiceScope verify1 = sp.CreateScope())
        {
            MerrickContext db = verify1.ServiceProvider.GetRequiredService<MerrickContext>();
            (await db.SystemTelemetryMetrics.FirstAsync(m => m.ID == 1)).ConcurrentConnectedUsers.Should().Be(1);
        }

        // p2 connects inside throttle window -> must schedule one coalesced trailing-edge catch-up flush
        await sut.OnClientConnectedAsync("player2");
        sut.HasScheduledTrailingFlush.Should().BeTrue();

        // Repeated idle queue tick while p2 is pending must NOT clear or lose CCU=2
        await sut.RecordQueueStateAsync(0);

        // Wait for trailing-edge flush to fire and persist CCU=2
        await Task.Delay(180);
        using (IServiceScope verify2 = sp.CreateScope())
        {
            MerrickContext db = verify2.ServiceProvider.GetRequiredService<MerrickContext>();
            (await db.SystemTelemetryMetrics.FirstAsync(m => m.ID == 1)).ConcurrentConnectedUsers.Should().Be(2);
        }

        // p3 connects inside throttle window, followed immediately by DisposeAsync -> shutdown flush must persist CCU=3
        await sut.OnClientConnectedAsync("player3");
        await sut.DisposeAsync();

        using (IServiceScope verify3 = sp.CreateScope())
        {
            MerrickContext db = verify3.ServiceProvider.GetRequiredService<MerrickContext>();
            (await db.SystemTelemetryMetrics.FirstAsync(m => m.ID == 1)).ConcurrentConnectedUsers.Should().Be(3);
        }
    }

    // protects: Eliminating per-packet Redis HashIncrement/WorkingSet64 syscalls and double-counting NexusMetrics.CommandsProcessedTotal.
    // fails_when: RecordChatCommandAsync executes Redis I/O per packet or increments NexusMetrics.CommandsProcessedTotal.
    // seam: Real TelemetryPublisherService + Mock<IDatabase> + FlushPendingChatCommandsAsync.
    [Fact]
    public async Task TelemetryPublisherService_RecordChatCommandAsync_BatchesRedisIncrementsWithoutPerPacketSyscalls()
    {
        string dbName = $"TelemetryBatch_{Guid.NewGuid():N}";
        await using ServiceProvider sp = (ServiceProvider)BuildTelemetryServiceProvider(dbName);

        Mock<IDatabase> mockDb = new();
        mockDb.Setup(d => d.HashIncrementAsync(
                TelemetryPublisherService.TelemetryHashKey,
                "ChatCommandsTotal",
                It.IsAny<long>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(5L);
        mockDb.Setup(d => d.HashSetAsync(
                TelemetryPublisherService.TelemetryHashKey,
                "MEM_TRANSMUTANSTEIN",
                It.IsAny<RedisValue>(),
                It.IsAny<When>(),
                It.IsAny<CommandFlags>()))
            .Returns(Task.FromResult(true));

        Mock<IConnectionMultiplexer> mockMux = new();
        mockMux.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object?>())).Returns(mockDb.Object);
        mockMux.Setup(m => m.GetSubscriber(It.IsAny<object?>())).Returns((ISubscriber)null!);

        await using TelemetryPublisherService sut = new(
            sp,
            NullLogger<TelemetryPublisherService>.Instance,
            mockMux.Object,
            TimeProvider.System,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(30));

        for (int i = 0; i < 5; i++)
        {
            await sut.RecordChatCommandAsync();
        }

        sut.PendingChatCommands.Should().Be(5);
        mockDb.Verify(d => d.HashIncrementAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<long>(), It.IsAny<CommandFlags>()), Times.Never);
        mockDb.Verify(d => d.HashSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(), It.IsAny<When>(), It.IsAny<CommandFlags>()), Times.Never);

        await sut.FlushPendingChatCommandsAsync();

        sut.PendingChatCommands.Should().Be(0);
        mockDb.Verify(d => d.HashIncrementAsync(
            TelemetryPublisherService.TelemetryHashKey,
            "ChatCommandsTotal",
            5L,
            CommandFlags.None), Times.Once);
        mockDb.Verify(d => d.HashSetAsync(
            TelemetryPublisherService.TelemetryHashKey,
            "MEM_TRANSMUTANSTEIN",
            It.Is<RedisValue>(v => (long)v > 0),
            When.Always,
            CommandFlags.None), Times.Once);
    }

    // protects: Credential hex redaction across all 7 auth opcodes, heartbeat demotion (preserving 0x1502 at Information), verbose opt-in, and 60s [HeartbeatSummary].
    // fails_when: Auth payloads leak hex, 0x1502 NET_CHAT_GS_CREATE_MATCH is demoted as heartbeat, or 60s [HeartbeatSummary] fails to emit.
    // seam: Real ChatSession + ChatPacketDispatcher + ListCapturingLogger<ChatSession> + ManualTimeProvider.
    [Fact]
    public async Task ChatSession_RedactsCredentialOpcodes_DemotesHeartbeats_Preserves0x1502_AndEmits60sSummary()
    {
        // 1. Verify protocol-constant classification (including 0x1502 != 0x1602!)
        ChatSession.IsRoutineHeartbeatPacket(ChatProtocol.Bidirectional.NET_CHAT_PING).Should().BeTrue();
        ChatSession.IsRoutineHeartbeatPacket(ChatProtocol.Bidirectional.NET_CHAT_PONG).Should().BeTrue();
        ChatSession.IsRoutineHeartbeatPacket(ChatProtocol.GameServerToChatServer.NET_CHAT_GS_STATUS).Should().BeTrue();
        ChatSession.IsRoutineHeartbeatPacket(ChatProtocol.ServerManagerToChatServer.NET_CHAT_SM_STATUS).Should().BeTrue();
        ChatSession.IsRoutineHeartbeatPacket(ChatProtocol.ChatServerToGameServer.NET_CHAT_GS_CREATE_MATCH).Should().BeFalse("0x1502 is match creation, never a heartbeat");

        ushort[] credentialOpcodes =
        [
            ChatProtocol.ClientToChatServer.NET_CHAT_CL_CONNECT,
            ChatProtocol.GameServerToChatServer.NET_CHAT_GS_CONNECT,
            ChatProtocol.ServerManagerToChatServer.NET_CHAT_SM_CONNECT,
            ChatProtocol.ChatServerToGameServer.NET_CHAT_GS_REMOTE_COMMAND,
            ChatProtocol.ChatServerToServerManager.NET_CHAT_SM_REMOTE_COMMAND,
            ChatProtocol.Command.CHAT_CMD_CHANNEL_SET_PASSWORD,
            ChatProtocol.Command.CHAT_CMD_JOIN_CHANNEL_PASSWORD
        ];

        byte[] secretBytes = [0xDE, 0xAD, 0xBE, 0xEF, 0xCA, 0xFE];
        foreach (ushort op in credentialOpcodes)
        {
            ChatSession.IsCredentialBearingCommand(op).Should().BeTrue();
            ChatSession.FormatPacketPayloadForLog(op, secretBytes).Should().Be("[REDACTED-AUTH-PAYLOAD]");

            byte[] rawFrame = [0x08, 0x00, (byte)(op & 0xFF), (byte)(op >> 8), 0xAA, 0xBB, 0xCC, 0xDD];
            ChatSession.FormatRawReceivedForLog(rawFrame).Should().Be("[REDACTED-AUTH-PAYLOAD]");
        }

        ChatSession.FormatPacketPayloadForLog(ChatProtocol.Bidirectional.NET_CHAT_PING, [0x00, 0x2A]).Should().Be("002A");
        ChatSession.IsVerboseHeartbeatsEnabled("1").Should().BeTrue();
        ChatSession.IsVerboseHeartbeatsEnabled("0").Should().BeFalse();

        // 2. Live ChatSession pipeline checks with isolated TimeProvider + HeartbeatRollupTracker
        Utilities.Log.Initialise(NullLogger.Instance);
        ManualTimeProvider timeProvider = new(new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero));
        ListCapturingLogger<ChatSession> logger = new(LogLevel.Trace);
        ChatSession.HeartbeatRollupTracker tracker = new();

        Mock<IChatContext> mockChatContext = new();
        mockChatContext.SetupGet(c => c.ClientChatSessions).Returns(new ConcurrentDictionary<string, ChatSession>());
        mockChatContext.SetupGet(c => c.MatchServerChatSessions).Returns(new ConcurrentDictionary<int, ChatSession>());
        mockChatContext.SetupGet(c => c.MatchServerManagerChatSessions).Returns(new ConcurrentDictionary<int, ChatSession>());
        mockChatContext.SetupGet(c => c.ChatChannels).Returns(new ConcurrentDictionary<string, ChatChannel>());
        mockChatContext.SetupGet(c => c.ChatChannelsByID).Returns(new ConcurrentDictionary<int, ChatChannel>());

        ServiceCollection services = new();
        services.AddSingleton(mockChatContext.Object);
        services.AddSingleton(new Mock<IDiscordPresencePublisherService>().Object);
        services.AddSingleton(new Mock<IMatchmakingService>().Object);
        services.AddSingleton<ILogger<ChatSession>>(logger);
        services.AddSingleton<TimeProvider>(timeProvider);
        services.AddSingleton(tracker);
        services.AddTransient<Ping>();

        await using ServiceProvider sp = services.BuildServiceProvider();
        using TCPServer server = new(IPAddress.Loopback, 0);
        using ChatSession session = new(server, sp) { VerboseHeartbeatsOverride = false };

        // Send a NET_CHAT_PING (0x2A00) -> Ping + Pong must log at Debug (NOT Information), and payload logged at Debug
        byte[] pingSegment = [(byte)(ChatProtocol.Bidirectional.NET_CHAT_PING & 0xFF), (byte)(ChatProtocol.Bidirectional.NET_CHAT_PING >> 8), 0x11, 0x22];
        await session.ProcessDataSegmentAsync(sp, pingSegment, pingSegment.Length);

        logger.Entries.Should().Contain(e => e.Level == LogLevel.Debug && e.Message.Contains("0x2A00") && e.Message.Contains("[IN]"));
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Debug && e.Message.Contains("0x2A01") && e.Message.Contains("[OUT]"));
        logger.Entries.Should().NotContain(e => e.Level == LogLevel.Information && (e.Message.Contains("0x2A00") || e.Message.Contains("0x2A01")));

        // Verify 0x1502 (NET_CHAT_GS_CREATE_MATCH) Send stays at Information!
        ChatBuffer createMatchBuffer = new();
        createMatchBuffer.WriteCommand(ChatProtocol.ChatServerToGameServer.NET_CHAT_GS_CREATE_MATCH);
        createMatchBuffer.WriteInt32(-10001);
        session.Send(createMatchBuffer);

        logger.Entries.Should().Contain(e => e.Level == LogLevel.Information && e.Message.Contains("0x1502") && e.Message.Contains("[OUT]"));

        // Verify credential-bearing opcode (0x0043 CHAT_CMD_CHANNEL_SET_PASSWORD) redacts payload hex even at Debug level
        byte[] secretPasswordSegment =
        [
            (byte)(ChatProtocol.Command.CHAT_CMD_CHANNEL_SET_PASSWORD & 0xFF),
            (byte)(ChatProtocol.Command.CHAT_CMD_CHANNEL_SET_PASSWORD >> 8),
            .. Encoding.ASCII.GetBytes("SUPER_SECRET_COOKIE_XYZ")
        ];
        await session.ProcessDataSegmentAsync(sp, secretPasswordSegment, secretPasswordSegment.Length);

        logger.Entries.Should().Contain(e => e.Level == LogLevel.Debug && e.Message.Contains("0x0043") && e.Message.Contains("[REDACTED-AUTH-PAYLOAD]"));
        logger.Entries.Should().NotContain(e => e.Message.Contains("53555045525F", StringComparison.OrdinalIgnoreCase));

        // Verify VerboseHeartbeatsOverride = true promotes heartbeat [IN]/[OUT] to Information
        logger.Entries.Clear();
        session.VerboseHeartbeatsOverride = true;
        await session.ProcessDataSegmentAsync(sp, pingSegment, pingSegment.Length);
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Information && e.Message.Contains("0x2A00") && e.Message.Contains("[IN]"));
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Information && e.Message.Contains("0x2A01") && e.Message.Contains("[OUT]"));

        // Verify once-per-60s [HeartbeatSummary] [INF] rollup after advancing 61s
        session.VerboseHeartbeatsOverride = false;
        timeProvider.Advance(TimeSpan.FromSeconds(61));
        await session.ProcessDataSegmentAsync(sp, pingSegment, pingSegment.Length);

        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Information &&
            e.Message.Contains("[HeartbeatSummary] 60s:") &&
            e.Message.Contains("pings="));
    }

    private sealed class CountingServiceProvider(ServiceProvider innerProvider, Action onCreateScope)
        : IServiceProvider, IServiceScopeFactory, IAsyncDisposable
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IServiceScopeFactory)
                ? this
                : innerProvider.GetService(serviceType);

        public IServiceScope CreateScope()
        {
            onCreateScope();
            return innerProvider.CreateScope();
        }

        public ValueTask DisposeAsync() => innerProvider.DisposeAsync();
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
