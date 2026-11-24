// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net.Sockets;
using ASPIRE.Tests.TRANSMUTANSTEIN.ChatServer.Infrastructure;
using TRANSMUTANSTEIN.ChatServer.Domain.Communication;
using TRANSMUTANSTEIN.ChatServer.Domain.Core;
using TRANSMUTANSTEIN.ChatServer.Internals;

namespace ASPIRE.Tests.TRANSMUTANSTEIN.ChatServer.Tests;

[Collection("TRANSMUTANSTEIN ChatServer Collection")]
public sealed class ChatChannelFullTests(TRANSMUTANSTEINTestFixture fixture)
{
    [Fact]
    public async Task JoinChannel_RejectsNonAdmin_WhenChannelIsFull()
    {
        // Arrange
        TRANSMUTANSTEINServiceProvider app = fixture.Provider;

        // 1. Connect Setup Client to create channel
        const int setupId = 1001;
        using TcpClient setupClient =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, setupId, "SetupUser");

        const string channelName = "FullChannel";
        int channelId = await ChatTestHelpers.JoinChannelAndGetId(setupClient, channelName);

        // 2. Fill Channel Members
        IChatContext chatContext = app.Services.GetRequiredService<IChatContext>();
        ChatChannel channel = chatContext.ChatChannels[channelName];

        // We need a member instance. We can grab the one we just added.
        ChatChannelMember existingMember = channel.Members.Values.First();

        // Fill up to 250 (ChatProtocol.MAX_USERS_PER_CHANNEL)
        // We already have 1 member.
        for (int i = 1; i < ChatProtocol.MAX_USERS_PER_CHANNEL; i++) channel.TryAddMember($"Dummy_{i}", existingMember);

        Assert.True(channel.IsFull);

        // 3. Connect Victim Client (Non-Admin)
        const int victimId = 1002;
        using TcpClient victimClient =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, victimId, "VictimUser");
        NetworkStream stream = victimClient.GetStream();

        // Act - Attempt to Join
        ChatBuffer joinBuffer = new();
        joinBuffer.WriteCommand(ChatProtocol.Command.CHAT_CMD_JOIN_CHANNEL);
        joinBuffer.WriteString(channelName);
        await ChatTestHelpers.SendPacketAsync(stream, joinBuffer);

        // Assert - Expect CHAT_CMD_WHISPER (0x0008)
        ChatBuffer response = await ChatTestHelpers.ExpectCommandAsync(stream, ChatProtocol.Command.CHAT_CMD_WHISPER);
        string sender = response.ReadString();
        string message = response.ReadString();

        Assert.Equal("Channel Service", sender);
        Assert.Equal("This channel is full.", message);

        // Verify not in channel
        Assert.False(channel.Members.ContainsKey("VictimUser"));
    }

    [Fact]
    public async Task JoinChannel_AllowsAdmin_WhenChannelIsFull()
    {
        // Arrange
        TRANSMUTANSTEINServiceProvider app = fixture.Provider;

        // 1. Connect Setup Client to create channel
        const int setupId = 2001;
        using TcpClient setupClient =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, setupId, "SetupUser2");

        const string channelName = "FullChannelAdmin";
        int channelId = await ChatTestHelpers.JoinChannelAndGetId(setupClient, channelName);

        // 2. Fill Channel Members
        IChatContext chatContext = app.Services.GetRequiredService<IChatContext>();
        ChatChannel channel = chatContext.ChatChannels[channelName];
        ChatChannelMember existingMember = channel.Members.Values.First();

        for (int i = 1; i < ChatProtocol.MAX_USERS_PER_CHANNEL; i++) channel.TryAddMember($"Dummy_{i}", existingMember);

        Assert.True(channel.IsFull);

        // 3. Connect Admin Client (Staff)
        const int adminId = 9999;
        // ConnectAndAuthenticateAsync has accountType param.
        using TcpClient adminClient = await ChatTestHelpers.ConnectAndAuthenticateAsync(
            app, app.ClientPort, adminId, "AdminUser", accountType: AccountType.Staff);
        NetworkStream stream = adminClient.GetStream();

        // Act - Attempt to Join
        ChatBuffer joinBuffer = new();
        joinBuffer.WriteCommand(ChatProtocol.Command.CHAT_CMD_JOIN_CHANNEL);
        joinBuffer.WriteString(channelName);
        await ChatTestHelpers.SendPacketAsync(stream, joinBuffer);

        // Assert - Expect CHAT_CMD_CHANGED_CHANNEL (0x0004)
        await ChatTestHelpers.ExpectCommandAsync(stream, ChatProtocol.Command.CHAT_CMD_CHANGED_CHANNEL);

        // Verify is in channel
        Assert.True(channel.Members.ContainsKey("AdminUser"));
    }
}
