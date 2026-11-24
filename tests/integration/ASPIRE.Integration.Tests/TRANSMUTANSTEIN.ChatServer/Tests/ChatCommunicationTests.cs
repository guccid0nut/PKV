// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net.Sockets;
using ASPIRE.Tests.TRANSMUTANSTEIN.ChatServer.Infrastructure;
using TRANSMUTANSTEIN.ChatServer.Domain.Core;

namespace ASPIRE.Tests.TRANSMUTANSTEIN.ChatServer.Tests;

[Collection("TRANSMUTANSTEIN ChatServer Collection")]
public sealed class ChatCommunicationTests(TRANSMUTANSTEINTestFixture fixture)
{
    [Fact]
    public async Task ChatCommunication_Whisper_Delivered()
    {
        // Arrange
        TRANSMUTANSTEINServiceProvider app = fixture.Provider;

        int senderId = Interlocked.Increment(ref ChatTestHelpers.s_accountIdCounter);
        int recipientId = Interlocked.Increment(ref ChatTestHelpers.s_accountIdCounter);
        string senderName = $"WpS_{senderId}";
        string recipientName = $"WpR_{recipientId}";

        using TcpClient sender =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, senderId, senderName);
        using TcpClient recipient =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, recipientId, recipientName);

        // Act - Send Whisper
        const string message = "Psst, this is a secret.";
        ChatBuffer whisperBuffer = new();
        whisperBuffer.WriteCommand(ChatProtocol.Command.CHAT_CMD_WHISPER); // 0x0008
        whisperBuffer.WriteString(recipientName); // Target Name
        whisperBuffer.WriteString(message); // Message

        byte[] packet = [.. whisperBuffer.Data.AsSpan(0, (int)whisperBuffer.Size)];
        List<byte> rawBytes =
        [
            .. BitConverter.GetBytes((ushort)packet.Length),
            .. packet
        ];
        await sender.GetStream().WriteAsync(rawBytes.ToArray());

        // Assert - Recipient receives 0x0008
        NetworkStream recvStream = recipient.GetStream();
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));

        byte[] header = new byte[4];
        await recvStream.ReadExactlyAsync(header, cts.Token);
        ushort length = BitConverter.ToUInt16(header, 0);
        ushort command = BitConverter.ToUInt16(header, 2);

        Assert.Equal(ChatProtocol.Command.CHAT_CMD_WHISPER, command); // 0x0008

        int payloadSize = length - 2;
        byte[] payload = new byte[payloadSize];
        int read = 0;
        while (read < payloadSize) read += await recvStream.ReadAsync(payload, read, payloadSize - read, cts.Token);

        ChatBuffer b = new(payload);
        string recvSenderName = b.ReadString();
        string receivedMsg = b.ReadString();

        Assert.Equal(senderName, recvSenderName);
        Assert.Equal(message, receivedMsg);
    }

    [Fact]
    public async Task Whisper_RecipientNotFound_SendsWhisperFailed()
    {
        // Arrange
        TRANSMUTANSTEINServiceProvider app = fixture.Provider;

        using TcpClient sender =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, 603, "WhisperSender_603");

        // Act - Send Whisper to non-existent target
        const string message = "Hello Ghost";
        ChatBuffer whisperBuffer = new();
        whisperBuffer.WriteCommand(ChatProtocol.Command.CHAT_CMD_WHISPER);
        whisperBuffer.WriteString("NonExistentTarget_999");
        whisperBuffer.WriteString(message);

        byte[] packet = [.. whisperBuffer.Data.AsSpan(0, (int)whisperBuffer.Size)];
        List<byte> rawBytes =
        [
            .. BitConverter.GetBytes((ushort)packet.Length),
            .. packet
        ];
        await sender.GetStream().WriteAsync(rawBytes.ToArray());

        // Assert - Expect CHAT_CMD_WHISPER_FAILED (0x0009) back to sender
        NetworkStream sendStream = sender.GetStream();
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));

        byte[] header = new byte[4];
        await sendStream.ReadExactlyAsync(header, cts.Token);
        ushort length = BitConverter.ToUInt16(header, 0);
        ushort command = BitConverter.ToUInt16(header, 2);

        Assert.Equal(ChatProtocol.Command.CHAT_CMD_WHISPER_FAILED, command);
    }

    [Fact]
    public async Task Whisper_RecipientDND_SendsWhisperFailed()
    {
        // Arrange
        TRANSMUTANSTEINServiceProvider app = fixture.Provider;

        using TcpClient sender =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, 604, "WhisperSender_604");
        using TcpClient recipient =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, 605, "WhisperRecipient_605");

        // Set Recipient Chat Mode to DND
        ChatBuffer dndBuffer = new();
        dndBuffer.WriteCommand(ChatProtocol.Command.CHAT_CMD_SET_CHAT_MODE_TYPE);
        dndBuffer.WriteInt8((byte)ChatProtocol.ChatModeType.CHAT_MODE_DND);
        dndBuffer.WriteString("Busy gaming");
        byte[] modePacket = [.. dndBuffer.Data.AsSpan(0, (int)dndBuffer.Size)];
        List<byte> modeRaw =
        [
            .. BitConverter.GetBytes((ushort)modePacket.Length),
            .. modePacket
        ];
        await recipient.GetStream().WriteAsync(modeRaw.ToArray());

        // Wait brief moment for set chat mode response on recipient
        using CancellationTokenSource modeCts = new(TimeSpan.FromSeconds(2));
        byte[] modeHeader = new byte[4];
        await recipient.GetStream().ReadExactlyAsync(modeHeader, modeCts.Token);

        // Act - Send Whisper
        const string message = "DND test message";
        ChatBuffer whisperBuffer = new();
        whisperBuffer.WriteCommand(ChatProtocol.Command.CHAT_CMD_WHISPER);
        whisperBuffer.WriteString("WhisperRecipient_605");
        whisperBuffer.WriteString(message);

        byte[] packet = [.. whisperBuffer.Data.AsSpan(0, (int)whisperBuffer.Size)];
        List<byte> rawBytes =
        [
            .. BitConverter.GetBytes((ushort)packet.Length),
            .. packet
        ];
        await sender.GetStream().WriteAsync(rawBytes.ToArray(), modeCts.Token);

        // Assert - Expect response on sender stream
        NetworkStream sendStream = sender.GetStream();
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));

        byte[] header = new byte[4];
        await sendStream.ReadExactlyAsync(header, cts.Token);
        ushort length = BitConverter.ToUInt16(header, 0);
        ushort command = BitConverter.ToUInt16(header, 2);

        Assert.True(command is ChatProtocol.Command.CHAT_CMD_WHISPER_FAILED
            or ChatProtocol.Command.CHAT_CMD_CHAT_MODE_AUTO_RESPONSE);
    }

    [Fact]
    public async Task ChatCommunication_ConcurrentGuestAccounts_CanExchangeWhispers()
    {
        // Arrange
        TRANSMUTANSTEINServiceProvider app = fixture.Provider;

        int guest1Id = Interlocked.Increment(ref ChatTestHelpers.s_accountIdCounter);
        int guest2Id = Interlocked.Increment(ref ChatTestHelpers.s_accountIdCounter);
        string guest1Name = $"GUEST_{guest1Id}";
        string guest2Name = $"GUEST_{guest2Id}";

        using TcpClient client1 =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, guest1Id, guest1Name, accountType: global::NEXUS.Contracts.Domain.AccountType.Guest);
        using TcpClient client2 =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, guest2Id, guest2Name, accountType: global::NEXUS.Contracts.Domain.AccountType.Guest);

        // Act - Guest 1 whispers to Guest 2
        const string message1 = "Hello from Guest 1!";
        ChatBuffer whisperBuffer1 = new();
        whisperBuffer1.WriteCommand(ChatProtocol.Command.CHAT_CMD_WHISPER);
        whisperBuffer1.WriteString(guest2Name);
        whisperBuffer1.WriteString(message1);

        byte[] packet1 = [.. whisperBuffer1.Data.AsSpan(0, (int)whisperBuffer1.Size)];
        List<byte> rawBytes1 =
        [
            .. BitConverter.GetBytes((ushort)packet1.Length),
            .. packet1
        ];
        await client1.GetStream().WriteAsync(rawBytes1.ToArray());

        // Assert - Guest 2 receives whisper
        NetworkStream recvStream2 = client2.GetStream();
        ChatBuffer b2 = await ChatTestHelpers.ExpectCommandAsync(recvStream2, ChatProtocol.Command.CHAT_CMD_WHISPER);
        string senderName = b2.ReadString();
        string receivedMsg = b2.ReadString();

        Assert.Equal(guest1Name, senderName);
        Assert.Equal(message1, receivedMsg);

        // Act - Guest 2 replies to Guest 1
        const string message2 = "Hello back from Guest 2!";
        ChatBuffer whisperBuffer2 = new();
        whisperBuffer2.WriteCommand(ChatProtocol.Command.CHAT_CMD_WHISPER);
        whisperBuffer2.WriteString(guest1Name);
        whisperBuffer2.WriteString(message2);

        byte[] packet2 = [.. whisperBuffer2.Data.AsSpan(0, (int)whisperBuffer2.Size)];
        List<byte> rawBytes2 =
        [
            .. BitConverter.GetBytes((ushort)packet2.Length),
            .. packet2
        ];
        await client2.GetStream().WriteAsync(rawBytes2.ToArray());

        // Assert - Guest 1 receives reply
        NetworkStream recvStream1 = client1.GetStream();
        ChatBuffer b1 = await ChatTestHelpers.ExpectCommandAsync(recvStream1, ChatProtocol.Command.CHAT_CMD_WHISPER);
        string replySender = b1.ReadString();
        string replyMsg = b1.ReadString();

        Assert.Equal(guest2Name, replySender);
        Assert.Equal(message2, replyMsg);
    }
}
