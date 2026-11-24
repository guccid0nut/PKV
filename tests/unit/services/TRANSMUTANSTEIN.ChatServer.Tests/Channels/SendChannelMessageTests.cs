// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Net;
using FluentAssertions;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Enumerations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NEXUS.Contracts.Chat;
using TRANSMUTANSTEIN.ChatServer.Domain.Communication;
using TRANSMUTANSTEIN.ChatServer.Domain.Core;
using TRANSMUTANSTEIN.ChatServer.Internals;
using TRANSMUTANSTEIN.ChatServer.Services;
using TRANSMUTANSTEIN.ChatServer.Utilities;
using Xunit;
using DbRole = MERRICK.DatabaseContext.Entities.Utility.Role;

namespace TRANSMUTANSTEIN.ChatServer.Tests.Channels;

/// <summary>
///     Verifies K2 0x0003 (CHAT_CMD_CHANNEL_MSG) sender-exclusion wire invariants:
///     k2_x64.dll CChatManager::SubmitChatMessage (0x1806a6060) locally echoes chat_channel_message_sent (isSelf=true)
///     immediately on send, and inbound 0x0003 handler (0x1806c9c70) does not filter self AccountID.
/// </summary>
public sealed class SendChannelMessageTests : IDisposable
{
    private readonly Mock<IChatContext> _mockChatContext = new();
    private readonly Mock<IDiscordPresencePublisherService> _mockDiscord = new();
    private readonly Mock<IMatchmakingService> _mockMatchmaking = new();
    private readonly ConcurrentDictionary<string, ChatSession> _clientSessions = new();
    private readonly ConcurrentDictionary<string, ChatChannel> _chatChannels = new();
    private readonly ConcurrentDictionary<int, ChatChannel> _chatChannelsById = new();
    private readonly FloodPreventionService _floodPrevention = new(NullLogger<FloodPreventionService>.Instance);
    private readonly TCPServer _tcpServer;
    private readonly ServiceProvider _serviceProvider;

    public SendChannelMessageTests()
    {
        Log.Initialise(NullLogger.Instance);

        _mockChatContext.SetupGet(c => c.ClientChatSessions).Returns(_clientSessions);
        _mockChatContext.SetupGet(c => c.ChatChannels).Returns(_chatChannels);
        _mockChatContext.SetupGet(c => c.ChatChannelsByID).Returns(_chatChannelsById);

        ServiceCollection services = new();
        services.AddSingleton(_mockChatContext.Object);
        services.AddSingleton(_mockDiscord.Object);
        services.AddSingleton(_mockMatchmaking.Object);
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

    private sealed class MockChatSession(TCPServer server, IServiceProvider serviceProvider)
        : ChatSession(server, serviceProvider)
    {
        public List<byte[]> SentPackets { get; } = [];

        public override bool SendAsync(ReadOnlySpan<byte> buffer)
        {
            SentPackets.Add(buffer.ToArray());
            return true;
        }
    }

    private MockChatSession CreateSession(int id, string name)
    {
        return new MockChatSession(_tcpServer, _serviceProvider)
        {
            Account = new Account
            {
                ID = id,
                Name = name,
                User = new User
                {
                    ID = id,
                    EmailAddress = $"{name.ToLowerInvariant()}@kongor.net",
                    Role = new DbRole { ID = 2, Name = "User" },
                    SRPPasswordSalt = "salt",
                    SRPPasswordHash = "hash",
                    PBKDF2PasswordHash = "hash"
                }
            },
            ClientMetadata = new ClientChatSessionMetadata
            {
                RemoteIP = "127.0.0.1",
                SessionCookie = $"cookie_{id}",
                SessionAuthenticationHash = $"hash_{id}",
                ChatProtocolVersion = 68,
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
                ClientRegion = "US",
                ClientLanguage = "en"
            }
        };
    }

    [Fact]
    public async Task Process_WhenMemberSendsChannelMessage_DeliversToOtherMembersAndExcludesSender()
    {
        // Arrange
        MockChatSession alice = CreateSession(100, "guccidonut");
        MockChatSession bob = CreateSession(200, "teammate");

        ChatChannel channel = ChatChannel.GetOrCreate(_mockChatContext.Object, alice, "KONGOR");
        channel.TryAddMember(alice.Account.Name, new ChatChannelMember(alice, channel));
        channel.TryAddMember(bob.Account.Name, new ChatChannelMember(bob, channel));

        ChatBuffer requestPayload = new();
        requestPayload.WriteCommand(ChatProtocol.Command.CHAT_CMD_CHANNEL_MSG);
        requestPayload.WriteString("test");
        requestPayload.WriteInt32(channel.ID);

        ChatCommandRegistry.TryGetDescriptor(ChatProtocol.Command.CHAT_CMD_CHANNEL_MSG, out ChatCommandDescriptor? descriptor)
            .Should().BeTrue();

        // Act
        await descriptor!.ExecuteAsync(_serviceProvider, alice, requestPayload);

        // Assert: sender must receive zero 0x0003 self-echo packets; peer receives exactly 1
        alice.SentPackets.Should().BeEmpty("K2 locally echoes chat_channel_message_sent (isSelf=true) on send");
        bob.SentPackets.Should().HaveCount(1);

        ChatBuffer bobReader = new(bob.SentPackets[0].AsSpan(2));
        bobReader.ReadCommand().Should().Be((ushort)ChatProtocol.Command.CHAT_CMD_CHANNEL_MSG);
        bobReader.ReadInt32().Should().Be(100);
        bobReader.ReadInt32().Should().Be(channel.ID);
        bobReader.ReadString().Should().Be("test");
    }

    [Fact]
    public void DeliverRemoteClusterMessage_WhenSenderAlsoPresentLocally_ExcludesSenderSession()
    {
        // Arrange
        MockChatSession alice = CreateSession(100, "guccidonut");
        MockChatSession bob = CreateSession(200, "teammate");

        ChatChannel channel = ChatChannel.GetOrCreate(_mockChatContext.Object, alice, "KONGOR");
        channel.TryAddMember(alice.Account.Name, new ChatChannelMember(alice, channel));
        channel.TryAddMember(bob.Account.Name, new ChatChannelMember(bob, channel));

        // Act
        channel.DeliverRemoteClusterMessage(senderAccountId: 100, senderName: "guccidonut", message: "cluster hello");

        // Assert
        alice.SentPackets.Should().BeEmpty();
        bob.SentPackets.Should().HaveCount(1);
    }
}
