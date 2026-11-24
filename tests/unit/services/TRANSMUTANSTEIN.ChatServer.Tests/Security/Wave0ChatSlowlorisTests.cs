// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TRANSMUTANSTEIN.ChatServer.Contracts;
using TRANSMUTANSTEIN.ChatServer.Domain.Core;
using TRANSMUTANSTEIN.ChatServer.Internals;
using TRANSMUTANSTEIN.ChatServer.Services;
using Xunit;

namespace TRANSMUTANSTEIN.ChatServer.Tests.Security;

public sealed class Wave0ChatSlowlorisTests
{
    private sealed class CapturingChatLogger : ILogger<ChatSession>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }

    private sealed class DummyTcpServer(IPAddress address, int port) : TCPServer(address, port)
    {
        protected override TCPSession CreateSession() => throw new NotSupportedException();
    }

    // protects: Chat gateway TCP listener against pre-authentication slowloris / half-open socket exhaustion.
    // fails_when: Unauthenticated sessions stalling without sending a login handshake remain open past PreAuthTimeout.
    // seam: Real ChatSession.ProcessPipeAsync over stalled System.IO.Pipelines PipeReader.
    [Fact]
    public async Task ProcessPipeAsync_UnauthenticatedStalledPeer_DisconnectsAtPreAuthDeadline()
    {
        Utilities.Log.Initialise(NullLogger.Instance);
        CapturingChatLogger logger = new();

        ServiceCollection services = new();
        services.AddSingleton(Mock.Of<IChatContext>());
        services.AddSingleton(Mock.Of<IDiscordPresencePublisherService>());
        services.AddSingleton(Mock.Of<IMatchmakingService>());
        services.AddSingleton(Mock.Of<IWebHostEnvironment>());
        services.AddSingleton<ILogger<ChatSession>>(logger);
        using ServiceProvider provider = services.BuildServiceProvider();

        using DummyTcpServer server = new(IPAddress.Loopback, 0);
        using ChatSession session = new(server, provider)
        {
            PreAuthTimeoutOverride = TimeSpan.FromMilliseconds(80)
        };

        Pipe stalledPipe = new();
        // Write 1 partial byte (slowloris drip below 2-byte frame header) and hold writer open
        await stalledPipe.Writer.WriteAsync(new byte[] { 0x01 });

        using CancellationTokenSource safetyCts = new(TimeSpan.FromSeconds(5));
        await session.ProcessPipeAsync(stalledPipe.Reader, safetyCts.Token);

        logger.Messages.Should().Contain(m =>
            m.Contains("[SECURITY-ALERT]") &&
            m.Contains("slowloris"));
    }
}
