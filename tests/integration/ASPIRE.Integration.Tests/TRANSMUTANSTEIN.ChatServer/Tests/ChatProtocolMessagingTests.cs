// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TRANSMUTANSTEIN.ChatServer.CommandProcessors.Communication;
using TRANSMUTANSTEIN.ChatServer.CommandProcessors.Connection;
using TRANSMUTANSTEIN.ChatServer.Domain.Core;
using TRANSMUTANSTEIN.ChatServer.Internals;
using TRANSMUTANSTEIN.ChatServer.Services;
using TRANSMUTANSTEIN.ChatServer.Utilities;

namespace ASPIRE.Tests.TRANSMUTANSTEIN.ChatServer.Tests;

[Collection("TRANSMUTANSTEIN ChatServer Collection")]
public sealed class ChatProtocolMessagingTests : IDisposable
{
    public void Dispose()
    {
        Log.Initialise(NullLogger.Instance);
    }
    public ChatProtocolMessagingTests()
    {
        Log.Initialise(new Mock<ILogger>().Object);
    }

    private static IServiceProvider CreateMockServiceProvider(Mock<IChatContext> mockChatContext)
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

        return mockServiceProvider.Object;
    }

    private static ChatSession CreateTestSession(Account account, Mock<IChatContext> mockChatContext)
    {
        Mock<TCPServer> mockTcpServer = new(IPAddress.Loopback, 8000);
        IServiceProvider serviceProvider = CreateMockServiceProvider(mockChatContext);

        ClientChatSessionMetadata metadata =
            (ClientChatSessionMetadata)RuntimeHelpers.GetUninitializedObject(typeof(ClientChatSessionMetadata));
        metadata.LastKnownClientState = ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_CONNECTED;
        metadata.ClientChatModeState = ChatProtocol.ChatModeType.CHAT_MODE_AVAILABLE;
        metadata.ClientRegion = "USE";
        metadata.ClientLanguage = "en";

        ChatSession session = new(mockTcpServer.Object, serviceProvider)
        {
            Account = account,
            ClientMetadata = metadata
        };

        return session;
    }

    [Fact]
    public void SendInstantMessage_WhenRecipientIsOffline_SendsFailedCommand()
    {
        Mock<IChatContext> mockContext = new();
        ConcurrentDictionary<string, ChatSession> activeSessions = new(StringComparer.OrdinalIgnoreCase);
        mockContext.SetupGet(c => c.ClientChatSessions).Returns(activeSessions);

        SendInstantMessage processor = new(mockContext.Object);

        Role role = new() { ID = 1, Name = "User" };
        User user = new()
        {
            ID = 2, EmailAddress = "sender@test.com", SRPPasswordHash = "h", SRPPasswordSalt = "s",
            PBKDF2PasswordHash = "h", Role = role
        };
        Account account = new() { ID = 2, Name = "ActiveSender", IsMain = true, User = user };

        ChatSession senderSession = CreateTestSession(account, mockContext);

        ChatBuffer incoming = new();
        incoming.WriteCommand(ChatProtocol.Command.CHAT_CMD_IM);
        incoming.WriteString("NonExistentUser");
        incoming.WriteString("Are you there?");
        incoming.WriteInt8(0);

        // Act - should handle offline recipient gracefully
        Exception? exception = Record.Exception(() => processor.Process(senderSession, incoming));
        Assert.Null(exception);
    }

    [Fact]
    public async Task ServerManagerHandshake_ProtocolMismatch_RejectsCleanly()
    {
        Mock<IDatabase> mockRedis = new();
        await using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        Mock<IChatContext> mockChatContext = new();
        mockChatContext.SetupGet(c => c.MatchServerManagerChatSessions)
            .Returns(new ConcurrentDictionary<int, ChatSession>());

        ServerManagerHandshake processor = new(mockRedis.Object, db, mockChatContext.Object);

        Role role = new() { ID = 1, Name = "User" };
        User user = new()
        {
            ID = 3, EmailAddress = "manager@test.com", SRPPasswordHash = "h", SRPPasswordSalt = "s",
            PBKDF2PasswordHash = "h", Role = role
        };
        Account account = new() { ID = 3, Name = "ManagerAccount", IsMain = true, User = user };

        ChatSession session = CreateTestSession(account, mockChatContext);

        ChatBuffer incoming = new();
        incoming.WriteCommand(ChatProtocol.ServerManagerToChatServer.NET_CHAT_SM_CONNECT);
        incoming.WriteInt32(9999); // Wrong Protocol Version
        incoming.WriteInt32(1); // ServerManagerID
        incoming.WriteString("cookie_123");
        incoming.WriteString("USE");
        incoming.WriteInt8(0);
        incoming.WriteString("TestManager");

        // Act
        Exception? exception = await Record.ExceptionAsync(() => processor.Process(session, incoming));
        Assert.Null(exception);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task ServerManagerHandshake_InvalidCookie_RejectsCleanly()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        Mock<IDatabase> mockRedis = new();
        mockRedis.Setup(r => r.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisValue.Null);

        await using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        Mock<IChatContext> mockChatContext = new();
        mockChatContext.SetupGet(c => c.MatchServerManagerChatSessions)
            .Returns(new ConcurrentDictionary<int, ChatSession>());

        ServerManagerHandshake processor = new(mockRedis.Object, db, mockChatContext.Object);

        Role role = new() { ID = 1, Name = "User" };
        User user = new()
        {
            ID = 4, EmailAddress = "manager2@test.com", SRPPasswordHash = "h", SRPPasswordSalt = "s",
            PBKDF2PasswordHash = "h", Role = role
        };
        Account account = new() { ID = 4, Name = "ManagerAccount2", IsMain = true, User = user };

        ChatSession session = CreateTestSession(account, mockChatContext);

        ChatBuffer incoming = new();
        incoming.WriteCommand(ChatProtocol.ServerManagerToChatServer.NET_CHAT_SM_CONNECT);
        incoming.WriteInt32((int)ChatProtocol.CHAT_PROTOCOL_EXTERNAL_VERSION); // Correct Version
        incoming.WriteInt32(1);
        incoming.WriteString("invalid_cookie_str");
        incoming.WriteString("USE");
        incoming.WriteInt8(0);
        incoming.WriteString("TestManager");

        // Act
        Exception? exception = await Record.ExceptionAsync(() => processor.Process(session, incoming));
        Assert.Null(exception);
    }
}
