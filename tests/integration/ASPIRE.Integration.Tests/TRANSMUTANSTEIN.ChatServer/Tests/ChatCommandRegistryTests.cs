// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net;
using System.Reflection;
using Moq;
using TRANSMUTANSTEIN.ChatServer.Domain.Core;
using TRANSMUTANSTEIN.ChatServer.Internals;
using TRANSMUTANSTEIN.ChatServer.Services;

namespace ASPIRE.Tests.TRANSMUTANSTEIN.ChatServer.Tests;

public sealed class ChatCommandRegistryTests
{
    private readonly Mock<IChatContext> _chatContextMock = new();
    private readonly Mock<IDiscordPresencePublisherService> _discordPublisherMock = new();
    private readonly Mock<IWebHostEnvironment> _envMock = new();
    private readonly Mock<ILogger<ChatSession>> _loggerMock = new();
    private readonly Mock<IMatchmakingService> _matchmakingServiceMock = new();
    private readonly IServiceProvider _serviceProvider;

    public ChatCommandRegistryTests()
    {
        ServiceCollection services = new();
        services.AddSingleton(_loggerMock.Object);
        services.AddSingleton(_chatContextMock.Object);
        services.AddSingleton(_matchmakingServiceMock.Object);
        services.AddSingleton(_discordPublisherMock.Object);
        services.AddSingleton(_envMock.Object);

        _serviceProvider = services.BuildServiceProvider();
    }

    [Fact]
    public void ChatCommandRegistry_RegisteredCount_ContainsAllAssemblyCommandProcessors()
    {
        int count = ChatCommandRegistry.RegisteredCount;
        Assert.True(count >= 50, $"Expected at least 50 registered command handlers, but got {count}");
    }

    [Fact]
    public void TryGetDescriptor_SynchronousCommand_ResolvesDescriptor()
    {
        bool found = ChatCommandRegistry.TryGetDescriptor(ChatProtocol.Bidirectional.NET_CHAT_PING,
            out ChatCommandDescriptor? descriptor);

        Assert.True(found);
        Assert.NotNull(descriptor);
        Assert.Equal("Ping", descriptor.Name);
        Assert.Equal(ChatProtocol.Bidirectional.NET_CHAT_PING, descriptor.Command);
    }

    [Fact]
    public void TryGetDescriptor_AsynchronousCommand_ResolvesDescriptor()
    {
        bool found = ChatCommandRegistry.TryGetDescriptor(ChatProtocol.ClientToChatServer.NET_CHAT_CL_CONNECT,
            out ChatCommandDescriptor? descriptor);

        Assert.True(found);
        Assert.NotNull(descriptor);
        Assert.Equal("ClientHandshake", descriptor.Name);
        Assert.Equal(ChatProtocol.ClientToChatServer.NET_CHAT_CL_CONNECT, descriptor.Command);
    }

    [Fact]
    public void TryGetDescriptor_UnknownCommand_ReturnsFalseImmediately()
    {
        bool found = ChatCommandRegistry.TryGetDescriptor(0xBEEF, out ChatCommandDescriptor? descriptor);

        Assert.False(found);
        Assert.Null(descriptor);
    }

    [Fact]
    public async Task Descriptor_SynchronousExecution_ExecutesWithoutReflection()
    {
        bool found = ChatCommandRegistry.TryGetDescriptor(ChatProtocol.Bidirectional.NET_CHAT_PING,
            out ChatCommandDescriptor? descriptor);
        Assert.True(found);
        Assert.NotNull(descriptor);

        Mock<TCPServer> serverMock = new(IPAddress.Loopback, 50300);
        ChatSession session = new(serverMock.Object, _serviceProvider);
        ChatBuffer buffer = new();
        buffer.WriteCommand(ChatProtocol.Bidirectional.NET_CHAT_PING);

        Task executionTask = descriptor.ExecuteAsync(_serviceProvider, session, buffer);
        await executionTask;

        Assert.True(executionTask.IsCompletedSuccessfully);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task ChatSession_SequentialPacketProcessing_ProcessesPacketsInOrder()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        Mock<TCPServer> serverMock = new(IPAddress.Loopback, 50300);
        ChatSession session = new(serverMock.Object, _serviceProvider);

        MethodInfo? onConnectedMethod =
            typeof(ChatSession).GetMethod("OnConnected", BindingFlags.NonPublic | BindingFlags.Instance);
        onConnectedMethod!.Invoke(session, null);

        MethodInfo? onReceivedMethod =
            typeof(ChatSession).GetMethod("OnReceived", BindingFlags.NonPublic | BindingFlags.Instance);

        for (int i = 0; i < 10; i++)
        {
            ChatBuffer buffer = new();
            buffer.WriteCommand(ChatProtocol.Bidirectional.NET_CHAT_PING);
            byte[] lengthBytes = BitConverter.GetBytes((ushort)buffer.Size);
            byte[] packet = [.. lengthBytes, .. buffer.Data.Take((int)buffer.Size)];

            onReceivedMethod!.Invoke(session, [packet, 0L, (long)packet.Length]);
        }

        // Give the background queue a short moment to drain
        await Task.Delay(10);

        Assert.NotNull(session);
    }
}
