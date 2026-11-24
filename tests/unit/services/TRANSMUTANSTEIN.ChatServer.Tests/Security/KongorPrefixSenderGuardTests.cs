// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: KongorPrefixSenderGuardTests.cs
// System: TRANSMUTANSTEIN ChatServer Tests - Prefix Security Guard
// Policy: Strict Explicit Typing (NO 'var')
//
// Value-Weighted Testing Gate:
//   protects: chat messaging against reserved system prefix spoofing (__kongor*).
//   fails_when: A sender with a nickname starting with '__kongor' is permitted
//               to send whispers or instant messages to other players.
//   seam: SendWhisper and SendInstantMessage command processors wire dispatch.
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using FluentAssertions;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Enumerations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NEXUS.Contracts.Chat;
using TRANSMUTANSTEIN.ChatServer.Attributes;
using TRANSMUTANSTEIN.ChatServer.CommandProcessors.Communication;
using TRANSMUTANSTEIN.ChatServer.Domain.Core;
using TRANSMUTANSTEIN.ChatServer.Internals;
using TRANSMUTANSTEIN.ChatServer.Services;
using TRANSMUTANSTEIN.ChatServer.Utilities;
using Xunit;
using DbRole = MERRICK.DatabaseContext.Entities.Utility.Role;

namespace TRANSMUTANSTEIN.ChatServer.Tests.Security;

public sealed class KongorPrefixSenderGuardTests : IDisposable
{
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

    private readonly Mock<IChatContext> _mockChatContext = new();
    private readonly ConcurrentDictionary<string, ChatSession> _clientSessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ServiceProvider _serviceProvider;
    private readonly TCPServer _tcpServer;

    public KongorPrefixSenderGuardTests()
    {
        Log.Initialise(NullLogger.Instance);

        _mockChatContext.SetupGet(c => c.ClientChatSessions).Returns(_clientSessions);

        ServiceCollection services = new();
        services.AddSingleton(_mockChatContext.Object);
        services.AddSingleton(Mock.Of<IDiscordPresencePublisherService>());
        services.AddSingleton(Mock.Of<IMatchmakingService>());
        services.AddSingleton(Mock.Of<ITelemetryPublisherService>());
        _serviceProvider = services.BuildServiceProvider();

        _tcpServer = new TCPServer(IPAddress.Loopback, 0);
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _tcpServer.Dispose();
    }

    [Theory]
    [InlineData("__kongor_system")]
    [InlineData("__KONGOR_ADMIN")]
    [InlineData("__kongor")]
    public void SendWhisper_SenderHasKongorPrefix_RejectsWithSecurityNotice(string senderNickname)
    {
        SimulatedChatSession sender = CreateClientSession(101, senderNickname);
        SendWhisper processor = new(_mockChatContext.Object);

        ChatBuffer buffer = new();
        buffer.WriteCommand(ChatProtocol.Command.CHAT_CMD_WHISPER);
        buffer.WriteString("TargetPlayer");
        buffer.WriteString("Hello world");

        processor.Process(sender, buffer);

        List<byte[]> packets = sender.SnapshotPackets();
        packets.Should().NotBeEmpty();

        ChatBuffer responseBuffer = new(packets[0][2..]);
        ushort cmd = responseBuffer.ReadCommand();
        string responseSender = responseBuffer.ReadString();
        string responseText = responseBuffer.ReadString();

        cmd.Should().Be((ushort)ChatProtocol.Command.CHAT_CMD_WHISPER);
        responseSender.Should().Be("System");
        responseText.Should().Contain("[Security]");
    }

    [Theory]
    [InlineData("__kongor_system")]
    [InlineData("__KONGOR_ADMIN")]
    [InlineData("__kongor")]
    public void SendInstantMessage_SenderHasKongorPrefix_RejectsWithSecurityNotice(string senderNickname)
    {
        SimulatedChatSession sender = CreateClientSession(102, senderNickname);
        SendInstantMessage processor = new(_mockChatContext.Object);

        ChatBuffer buffer = new();
        buffer.WriteCommand(ChatProtocol.Command.CHAT_CMD_IM);
        buffer.WriteString("TargetPlayer");
        buffer.WriteString("Hello world");
        buffer.WriteInt8(0);

        processor.Process(sender, buffer);

        List<byte[]> packets = sender.SnapshotPackets();
        packets.Should().NotBeEmpty();

        ChatBuffer responseBuffer = new(packets[0][2..]);
        ushort cmd = responseBuffer.ReadCommand();
        string responseSender = responseBuffer.ReadString();
        string responseText = responseBuffer.ReadString();

        cmd.Should().Be((ushort)ChatProtocol.Command.CHAT_CMD_WHISPER);
        responseSender.Should().Be("System");
        responseText.Should().Contain("[Security]");
    }

    private SimulatedChatSession CreateClientSession(int accountId, string nickname)
    {
        SimulatedChatSession session = new(_tcpServer, _serviceProvider)
        {
            Account = new Account
            {
                ID = accountId,
                Name = nickname,
                User = new User
                {
                    ID = accountId,
                    EmailAddress = $"{nickname.ToLowerInvariant()}@kongor.test",
                    Role = new DbRole { ID = 2, Name = "User" },
                    SRPPasswordSalt = "salt",
                    SRPPasswordHash = "hash",
                    PBKDF2PasswordHash = "hash"
                }
            },
            ClientMetadata = new ClientChatSessionMetadata
            {
                RemoteIP = "127.0.0.1",
                SessionCookie = $"cookie_{accountId}",
                SessionAuthenticationHash = $"auth_hash_{accountId}",
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
                ClientRegion = "USE",
                ClientLanguage = "en"
            }
        };
        session.MarkAuthenticated(ChatPeerRole.Client);
        return session;
    }
}
