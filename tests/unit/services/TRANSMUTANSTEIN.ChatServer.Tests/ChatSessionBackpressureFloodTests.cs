// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TRANSMUTANSTEIN.ChatServer.Contracts;
using TRANSMUTANSTEIN.ChatServer.Domain.Communication;
using TRANSMUTANSTEIN.ChatServer.Domain.Core;
using TRANSMUTANSTEIN.ChatServer.Internals;
using TRANSMUTANSTEIN.ChatServer.Services;
using Xunit;

namespace TRANSMUTANSTEIN.ChatServer.Tests;

public sealed class ChatSessionBackpressureFloodTests
{
    // protects: Default 1 MiB per-session OptionSendBufferLimit, soft broadcast shedding at BroadcastSoftWatermarkBytes,
    //           control-frame headroom reservation on stalled sockets, and zero drops on healthy concurrent consumers.
    // fails_when: Unbounded _sendBufferMain grows past OptionSendBufferLimit on non-reading sockets, droppable chat floods
    //             exhaust the hard limit so control packets (0x1502) fail, or healthy consumers drop packets under concurrency.
    // seam: Real loopback TCPServer + real OS TCP sockets (stalled vs draining) + concurrent N-producer ChatSession.Send flood.
    [Fact]
    public async Task ConcurrentBroadcastFlood_ShedsDroppableFramesOnSlowConsumer_PreservesControlHeadroom_AndDeliversAllToFastConsumer()
    {
        Utilities.Log.Initialise(NullLogger.Instance);
        CapturingLogger<ChatSession> logger = new();

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

        await using ServiceProvider sp = services.BuildServiceProvider();

        // 1. Default OptionSendBufferLimit guard when server leaves limit unset (0)
        using (TCPServer defaultServer = new(IPAddress.Loopback, 0))
        using (ChatSession defaultSession = new(defaultServer, sp))
        {
            defaultSession.OptionSendBufferLimit.Should().Be(1024 * 1024, "unset OptionSendBufferLimit must default to 1 MiB to bound slow-consumer memory");
        }

        const int softWatermarkBytes = 16 * 1024;
        const int hardLimitBytes = 32 * 1024;
        using LoopbackChatTestServer server = new(sp, softWatermarkBytes, hardLimitBytes);
        server.Start().Should().BeTrue();
        int port = ((IPEndPoint)server.Endpoint).Port;

        using Socket slowClientSocket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            ReceiveBufferSize = 4096
        };
        using Socket fastClientSocket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            ReceiveBufferSize = 65536
        };

        await slowClientSocket.ConnectAsync(IPAddress.Loopback, port);
        ChatSession slowSession = await server.WaitForSessionCountAsync(1);

        await fastClientSocket.ConnectAsync(IPAddress.Loopback, port);
        ChatSession fastSession = await server.WaitForSessionCountAsync(2);
        ReferenceEquals(slowSession, fastSession).Should().BeFalse();

        using CancellationTokenSource drainCts = new();
        TaskCompletionSource drainReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        long fastDrainedBytes = 0;
        Task drainFastClientTask = Task.Run(async () =>
        {
            byte[] buf = new byte[16384];
            try
            {
                ValueTask<int> firstRead = fastClientSocket.ReceiveAsync(buf, SocketFlags.None, drainCts.Token);
                drainReady.TrySetResult();
                int read = await firstRead;
                if (read > 0)
                {
                    Interlocked.Add(ref fastDrainedBytes, read);
                    while (!drainCts.IsCancellationRequested)
                    {
                        read = await fastClientSocket.ReceiveAsync(buf, SocketFlags.None, drainCts.Token);
                        if (read <= 0) break;
                        Interlocked.Add(ref fastDrainedBytes, read);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (SocketException)
            {
            }
        });
        await drainReady.Task;

        // 2. Concurrent N-producer x M-message broadcast flood (8 producers x 80 messages x ~520B = ~332 KB > OS socket buffers + 16 KB watermark)
        const int producerCount = 8;
        const int messagesPerProducer = 80;
        string chatText = new('X', 500);

        Task[] floodTasks = Enumerable.Range(0, producerCount)
            .Select(producerId => Task.Run(() =>
            {
                for (int i = 0; i < messagesPerProducer; i++)
                {
                    ChatBuffer broadcastMsg = new();
                    broadcastMsg.WriteCommand(ChatProtocol.Command.CHAT_CMD_CHANNEL_MSG);
                    broadcastMsg.WriteInt32(producerId);
                    broadcastMsg.WriteInt32(i);
                    broadcastMsg.WriteString(chatText);

                    slowSession.Send(broadcastMsg);
                    fastSession.Send(broadcastMsg);
                }
            }))
            .ToArray();

        await Task.WhenAll(floodTasks);

        // Allow fast consumer drain loop to catch up
        int totalExpectedFrames = producerCount * messagesPerProducer;
        long expectedFrameBytesPerClient = 0;
        {
            ChatBuffer sample = new();
            sample.WriteCommand(ChatProtocol.Command.CHAT_CMD_CHANNEL_MSG);
            sample.WriteInt32(0);
            sample.WriteInt32(0);
            sample.WriteString(chatText);
            expectedFrameBytesPerClient = totalExpectedFrames * (2 + sample.Size);
        }

        DateTime deadline = DateTime.UtcNow.AddSeconds(3.0);
        while (Interlocked.Read(ref fastDrainedBytes) < expectedFrameBytesPerClient && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        // 3. Assert slow non-reading consumer shed droppable frames above soft watermark, bounded memory, and preserved headroom for control packet!
        slowSession.OutboundBackpressureDrops.Should().BeGreaterThan(0, "stalled non-reading socket must shed CHAT_CMD_CHANNEL_MSG frames once queued bytes exceed soft watermark");
        slowSession.BytesPending.Should().BeLessThanOrEqualTo(hardLimitBytes, "pending main send buffer must never exceed OptionSendBufferLimit");
        logger.Warnings.Should().Contain(m => m.Contains("[Backpressure] Shedding outbound broadcast packet 0x", StringComparison.Ordinal));

        ChatBuffer controlPacket = new();
        controlPacket.WriteCommand(ChatProtocol.ChatServerToGameServer.NET_CHAT_GS_CREATE_MATCH);
        controlPacket.WriteInt32(424242);
        bool controlAccepted = slowSession.Send(controlPacket);
        controlAccepted.Should().BeTrue("non-droppable control frame (0x1502) must succeed using headroom reserved between soft watermark and OptionSendBufferLimit");

        // 4. Assert fast consumer experienced 0 backpressure drops and received 100% of concurrent frames
        fastSession.OutboundBackpressureDrops.Should().Be(0, "actively draining consumer must not shed broadcast frames");
        Interlocked.Read(ref fastDrainedBytes).Should().Be(expectedFrameBytesPerClient);

        await drainCts.CancelAsync();
        slowClientSocket.Close();
        fastClientSocket.Close();
        await drainFastClientTask;
        server.Stop();
    }

    private sealed class LoopbackChatTestServer : TCPServer
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly int _softWatermarkBytes;
        private readonly int _hardSendBufferLimitBytes;
        private readonly List<ChatSession> _orderedSessions = [];
        private readonly Lock _gate = new();

        public LoopbackChatTestServer(
            IServiceProvider serviceProvider,
            int softWatermarkBytes,
            int hardSendBufferLimitBytes)
            : base(IPAddress.Loopback, 0)
        {
            _serviceProvider = serviceProvider;
            _softWatermarkBytes = softWatermarkBytes;
            _hardSendBufferLimitBytes = hardSendBufferLimitBytes;
            OptionSendBufferLimit = hardSendBufferLimitBytes;
            OptionSendBufferSize = 4096;
        }

        protected override TCPSession CreateSession()
        {
            ChatSession session;
            lock (_gate)
            {
                bool isSlowConstrainedConsumer = _orderedSessions.Count == 0;
                session = new ChatSession(this, _serviceProvider)
                {
                    BroadcastSoftWatermarkBytesOverride = isSlowConstrainedConsumer
                        ? _softWatermarkBytes
                        : ChatSession.DefaultBroadcastSoftWatermarkBytes,
                    OptionSendBufferLimit = isSlowConstrainedConsumer
                        ? _hardSendBufferLimitBytes
                        : 1024 * 1024
                };
                _orderedSessions.Add(session);
            }
            return session;
        }

        protected override void OnConnecting(TCPSession session)
        {
            bool isSlowSession;
            lock (_gate)
            {
                isSlowSession = _orderedSessions.Count > 0 && ReferenceEquals(_orderedSessions[0], session);
            }
            session.Socket.SendBufferSize = isSlowSession ? 4096 : 65536;
        }

        public async Task<ChatSession> WaitForSessionCountAsync(int expectedCount)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(5.0);
            while (DateTime.UtcNow < deadline)
            {
                lock (_gate)
                {
                    if (_orderedSessions.Count >= expectedCount && _orderedSessions[expectedCount - 1].IsConnected)
                    {
                        return _orderedSessions[expectedCount - 1];
                    }
                }
                await Task.Delay(5);
            }

            throw new TimeoutException($"Timed out waiting for connected ChatSession #{expectedCount}; ensure LoopbackChatTestServer.Start() succeeded.");
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<string> Warnings { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
            {
                Warnings.Enqueue(formatter(state, exception));
            }
        }
    }
}
