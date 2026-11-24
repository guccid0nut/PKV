// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net.Sockets;
using ASPIRE.Tests.TRANSMUTANSTEIN.ChatServer.Infrastructure;
using TRANSMUTANSTEIN.ChatServer.Domain.Communication;
using TRANSMUTANSTEIN.ChatServer.Domain.Core;
using TRANSMUTANSTEIN.ChatServer.Internals;

// Added for GetRequiredService
// Added for ChatChannel

// Added for IChatContext

namespace ASPIRE.Tests.TRANSMUTANSTEIN.ChatServer.Tests;

[Collection("TRANSMUTANSTEIN ChatServer Collection")]
public sealed class ChatChannelTests(TRANSMUTANSTEINTestFixture fixture)
{
    [Fact]
    public async Task ChatChannel_JoinChannel_Success()
    {
        // Arrange
        TRANSMUTANSTEINServiceProvider app = fixture.Provider;

        int id = Random.Shared.Next(100, 200);
        using TcpClient client =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, id, "ChannelUser");
        NetworkStream stream = client.GetStream();

        // Act - Join Channel
        const string channelName = "TestChannel";
        ChatBuffer joinBuffer = new();
        joinBuffer.WriteCommand(ChatProtocol.Command.CHAT_CMD_JOIN_CHANNEL); // 0x001E
        joinBuffer.WriteString(channelName);

        // Send
        byte[] packet = [.. joinBuffer.Data.AsSpan(0, (int)joinBuffer.Size)];
        ushort packetLength = (ushort)packet.Length;
        List<byte> rawBytes =
        [
            .. BitConverter.GetBytes(packetLength),
            .. packet
        ];
        await stream.WriteAsync(rawBytes.ToArray());

        // Assert - Expect CHAT_CMD_CHANGED_CHANNEL (0x0004)
        // Command (2) + String(Name) + Int32(ID) + ...

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));

        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, cts.Token);
        ushort length = BitConverter.ToUInt16(header, 0);
        ushort command = BitConverter.ToUInt16(header, 2);

        Assert.Equal(ChatProtocol.Command.CHAT_CMD_CHANGED_CHANNEL, command); // 0x0004

        // Read Payload to verify channel name
        int payloadSize = length - 2;
        byte[] payload = new byte[payloadSize];
        int read = 0;
        while (read < payloadSize) read += await stream.ReadAsync(payload, read, payloadSize - read, cts.Token);

        // Parse Payload
        ChatBuffer responseBuffer = new(payload);
        string name = responseBuffer.ReadString();
        int channelId = responseBuffer.ReadInt32();

        Assert.Equal(channelName, name);
        Assert.True(channelId > 0);
    }

    [Fact]
    public async Task ChatChannel_SendMessage_BroadcastsToChannel()
    {
        // Debug timeout to fail fast
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));

        // Arrange
        TRANSMUTANSTEINServiceProvider app = fixture.Provider;

        int p1 = Random.Shared.Next(1000, 2000);
        int p2 = Random.Shared.Next(2001, 3000);

        string aliceName = $"Alice_{Guid.NewGuid().ToString()[..8]}";
        string bobName = $"Bob_{Guid.NewGuid().ToString()[..8]}";

        using TcpClient client1 = await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, p1, aliceName);

        // Wait for Client 1 to create/join channel
        // Since we are mocking the server context (or strictly, using shared static context), ensure unique channel too?
        // But Channel ID is used.
        // Wait, Client 1 creates "ChatRoom"?
        // Helper `JoinChannelAndGetId` uses "ChatRoom".
        // If "ChatRoom" is static, messages might cross-talk?
        // But Broadcast uses IDs.
        // Let's stick to Names first.

        string channelName = $"Room_{Guid.NewGuid().ToString()[..8]}"; // Randomize
        int channelId = await ChatTestHelpers.JoinChannelAndGetId(client1, channelName);

        // Ensure p2 is different from p1
        p2 = Random.Shared.Next(1000, 5000);
        if (p2 == p1) p2++;

        using TcpClient client2 = await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, p2, bobName);

        // Client 2 Joins
        await ChatTestHelpers.JoinChannelAndGetId(client2, channelName);

        // Client 1 will also receive 0x0005 (JOINED_CHANNEL) because Client 2 joined
        while (!cts.IsCancellationRequested)
        {
            byte[] h1_notif = new byte[4];
            await client1.GetStream().ReadExactlyAsync(h1_notif, cts.Token);
            ushort l1_notif = BitConverter.ToUInt16(h1_notif, 0);
            ushort c1_notif = BitConverter.ToUInt16(h1_notif, 2);

            byte[] py1_notif = new byte[l1_notif - 2];
            int read1_notif = 0;
            while (read1_notif < py1_notif.Length)
                read1_notif += await client1.GetStream()
                    .ReadAsync(py1_notif, read1_notif, py1_notif.Length - read1_notif, cts.Token);

            if (c1_notif == ChatProtocol.Command.CHAT_CMD_JOINED_CHANNEL) // 0x0005
                break;

            if (c1_notif == ChatProtocol.Command.CHAT_CMD_LEFT_CHANNEL) // 0x0006
            {
                // Inspect who left
                ChatBuffer leftBuf = new(py1_notif);
                int leaverId = leftBuf.ReadInt32();
                int leftChanId = leftBuf.ReadInt32();
            }
        }

        // Act - Client 1 Sends Message
        const string messageText = "Hello World";
        ChatBuffer msgBuffer = new();
        msgBuffer.WriteCommand(ChatProtocol.Command.CHAT_CMD_CHANNEL_MSG); // 0x0003
        msgBuffer.WriteString(messageText);
        msgBuffer.WriteInt32(channelId); // Needs ID, not Name

        byte[] msgPacket = [.. msgBuffer.Data.AsSpan(0, (int)msgBuffer.Size)];
        List<byte> rawMsg =
        [
            .. BitConverter.GetBytes((ushort)msgPacket.Length),
            .. msgPacket
        ];

        await client1.GetStream().WriteAsync(rawMsg.ToArray(), cts.Token);

        // Assert - Client 2 Receives Message (0x0003)
        // Loop to find the message command, skipping JOINED_CHANNEL (0x0005) if received (e.g. self-join notif)
        ushort c_msg = 0;
        const int maxRetries = 20;
        byte[] py_msg = [];

        for (int i = 0; i < maxRetries; i++)
        {
            byte[] h_msg = new byte[4];
            await client2.GetStream().ReadExactlyAsync(h_msg, cts.Token);
            ushort l_msg = BitConverter.ToUInt16(h_msg, 0);
            c_msg = BitConverter.ToUInt16(h_msg, 2);

            // Read Payload
            py_msg = new byte[l_msg - 2];
            int r_msg = 0;
            while (r_msg < py_msg.Length)
                r_msg += await client2.GetStream().ReadAsync(py_msg, r_msg, py_msg.Length - r_msg, cts.Token);

            if (c_msg == ChatProtocol.Command.CHAT_CMD_CHANNEL_MSG) break;

            if (c_msg == ChatProtocol.Command.CHAT_CMD_LEFT_CHANNEL) // 0x0006
            {
                ChatBuffer leftBuf = new(py_msg);
                int leaverId = leftBuf.ReadInt32();
                int leftChanId = leftBuf.ReadInt32();
                continue;
            }

            // If it's something unrelated (JOINED/CHANGED), ignore and continue
            if (c_msg is ChatProtocol.Command.CHAT_CMD_JOINED_CHANNEL
                or ChatProtocol.Command.CHAT_CMD_CHANGED_CHANNEL) continue;

            // If it's something else unexpected, logic will break and Assert will fail
            break;
        }

        Assert.Equal(ChatProtocol.Command.CHAT_CMD_CHANNEL_MSG, c_msg);

        ChatBuffer msgPayload = new(py_msg);
        int senderId = msgPayload.ReadInt32();
        int recvChannelId = msgPayload.ReadInt32();
        string recvMessage = msgPayload.ReadString();

        Assert.Equal(p1, senderId); // Alice
        Assert.Equal(channelId, recvChannelId);
        Assert.Equal(messageText, recvMessage);
    }

    [Fact]
    public async Task ChatChannel_LeaveChannel_Success()
    {
        // Arrange
        TRANSMUTANSTEINServiceProvider app = fixture.Provider;

        int id = Random.Shared.Next(301, 399);
        using TcpClient client = await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, id, "Leaver");
        NetworkStream stream = client.GetStream();

        // Join First
        const string channelName = "LeaveMe";
        {
            ChatBuffer join = new();
            join.WriteCommand(ChatProtocol.Command.CHAT_CMD_JOIN_CHANNEL);
            join.WriteString(channelName);
            byte[] p = [.. join.Data.AsSpan(0, (int)join.Size)];
            List<byte> r =
            [
                .. BitConverter.GetBytes((ushort)p.Length),
                .. p
            ];
            await stream.WriteAsync(r.ToArray());

            // Consume Response
            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
            byte[] h = new byte[4];
            await stream.ReadExactlyAsync(h, cts.Token);

            ushort l = BitConverter.ToUInt16(h, 0);
            byte[] py = new byte[l - 2];
            int rd = 0;
            while (rd < py.Length) rd += await stream.ReadAsync(py, rd, py.Length - rd, cts.Token);

            ChatBuffer b = new(py);
            b.ReadString();
            _ = b.ReadInt32();
        }

        // Act - Leave
        ChatBuffer leaveBuffer = new();
        leaveBuffer.WriteCommand(ChatProtocol.Command.CHAT_CMD_LEAVE_CHANNEL); // 0x0022
        leaveBuffer.WriteString(channelName); // Client sends Name, not ID (checked LeaveChannel.cs)

        byte[] leavePacket = [.. leaveBuffer.Data.AsSpan(0, (int)leaveBuffer.Size)];
        List<byte> rawLeave =
        [
            .. BitConverter.GetBytes((ushort)leavePacket.Length),
            .. leavePacket
        ];
        await stream.WriteAsync(rawLeave.ToArray());

        // Assert - Expect CHAT_CMD_LEFT_CHANNEL (0x0006)
        // Code: ChatChannel.Leave -> sends 0x0006 to [member, .. Members] (so checks self)

        using CancellationTokenSource ctsLeave = new(TimeSpan.FromSeconds(5));
        byte[] h_leave = new byte[4];
        await stream.ReadExactlyAsync(h_leave, ctsLeave.Token);
        ushort c_leave = BitConverter.ToUInt16(h_leave, 2);

        Assert.Equal(ChatProtocol.Command.CHAT_CMD_LEFT_CHANNEL, c_leave);
    }

    [Fact]
    public async Task JoinChannel_Unjoinable_RejectsNonStaff()
    {
        // Arrange
        TRANSMUTANSTEINServiceProvider app = fixture.Provider;

        IChatContext chatContext = app.Services.GetRequiredService<IChatContext>();

        // Create Channel with Unjoinable Flag
        const string channelName = "UnjoinableChannel";
        ChatChannel channel = ChatChannel.GetOrCreate(chatContext, null, channelName);
        channel.Flags |= ChatProtocol.ChatChannelType.CHAT_CHANNEL_FLAG_UNJOINABLE;

        ChatBuffer joinBuffer = new();
        joinBuffer.WriteCommand(ChatProtocol.Command.CHAT_CMD_JOIN_CHANNEL);
        joinBuffer.WriteString(channelName);

        // --- Test 1: Regular User ---
        int regularId = Random.Shared.Next(4000, 5000);
        using TcpClient regularClient = await ChatTestHelpers.ConnectAndAuthenticateAsync(
            app, app.ClientPort, regularId, "RegularUser", accountType: AccountType.Normal);
        NetworkStream regularStream = regularClient.GetStream();

        // Act - Join
        await ChatTestHelpers.SendPacketAsync(regularStream, joinBuffer);

        // Assert - Expect Whisper Rejection (CHAT_CMD_WHISPER - 0x0001)
        // Staff mentioned: "Whisper is fine"
        ChatBuffer response =
            await ChatTestHelpers.ExpectCommandAsync(regularStream, ChatProtocol.Command.CHAT_CMD_WHISPER, 5);
        string sender = response.ReadString();
        string msg = response.ReadString();
        Assert.Equal("Channel Service", sender);
        Assert.Contains("unjoinable", msg);

        // --- Test 2: Staff User ---
        int staffId = Random.Shared.Next(5001, 6000);
        using TcpClient staffClient = await ChatTestHelpers.ConnectAndAuthenticateAsync(
            app, app.ClientPort, staffId, "StaffUser", accountType: AccountType.Staff);
        NetworkStream staffStream = staffClient.GetStream();

        // Act - Join
        await ChatTestHelpers.SendPacketAsync(staffStream, joinBuffer);

        // Assert - Expect Success (CHAT_CMD_CHANGED_CHANNEL - 0x0004)
        ChatBuffer staffResponse =
            await ChatTestHelpers.ExpectCommandAsync(staffStream, ChatProtocol.Command.CHAT_CMD_CHANGED_CHANNEL, 5);
        string name = staffResponse.ReadString();
        Assert.Equal(channelName, name);
    }

    [Fact]
    public async Task ChatChannel_SendMessage_TruncatesLongMessageAndNotifiesSender()
    {
        // Arrange
        TRANSMUTANSTEINServiceProvider app = fixture.Provider;

        int senderId = Random.Shared.Next(6001, 7000);
        string senderName = $"VerboseUser_{Guid.NewGuid().ToString()[..8]}";

        using TcpClient senderClient = await ChatTestHelpers.ConnectAndAuthenticateAsync(
            app, app.ClientPort, senderId, senderName, accountType: AccountType.Normal);
        NetworkStream senderStream = senderClient.GetStream();

        int receiverId = Random.Shared.Next(7001, 8000);
        string receiverName = $"ReceiverUser_{Guid.NewGuid().ToString()[..8]}";
        using TcpClient receiverClient = await ChatTestHelpers.ConnectAndAuthenticateAsync(
            app, app.ClientPort, receiverId, receiverName, accountType: AccountType.Normal);
        NetworkStream receiverStream = receiverClient.GetStream();

        string channelName = $"TruncationRoom_{Guid.NewGuid().ToString()[..8]}";
        int channelId = await ChatTestHelpers.JoinChannelAndGetId(senderClient, channelName);
        await ChatTestHelpers.JoinChannelAndGetId(receiverClient, channelName);

        // Act - Send a message exceeding the maximum length (250 chars)
        string longMessage = new('A', 300);

        ChatBuffer msgBuffer = new();
        msgBuffer.WriteCommand(ChatProtocol.Command.CHAT_CMD_CHANNEL_MSG);
        msgBuffer.WriteString(longMessage);
        msgBuffer.WriteInt32(channelId);

        await ChatTestHelpers.SendPacketAsync(senderStream, msgBuffer);

        // Assert - Expect a Whisper notification to the SENDER
        ChatBuffer whisperResponse =
            await ChatTestHelpers.ExpectCommandAsync(senderStream, ChatProtocol.Command.CHAT_CMD_WHISPER);

        string whisperSender = whisperResponse.ReadString();
        string whisperMsg = whisperResponse.ReadString();

        Assert.Equal("Channel Service", whisperSender);
        Assert.Contains("truncated", whisperMsg);

        // Expect the actual broadcast message to be received by the RECEIVER
        ChatBuffer broadcastResponse =
            await ChatTestHelpers.ExpectCommandAsync(receiverStream, ChatProtocol.Command.CHAT_CMD_CHANNEL_MSG);

        int broadcastSenderId = broadcastResponse.ReadInt32();
        int broadcastChannelId = broadcastResponse.ReadInt32();
        string broadcastMsg = broadcastResponse.ReadString();

        Assert.Equal(senderId, broadcastSenderId);
        Assert.Equal(channelId, broadcastChannelId);
        Assert.Equal(ChatProtocol.CHAT_MESSAGE_MAX_LENGTH, broadcastMsg.Length);
        Assert.Equal(new string('A', ChatProtocol.CHAT_MESSAGE_MAX_LENGTH), broadcastMsg);
    }
}
