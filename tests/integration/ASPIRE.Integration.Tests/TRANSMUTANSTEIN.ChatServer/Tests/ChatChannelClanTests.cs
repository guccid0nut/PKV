// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net.Sockets;
using ASPIRE.Tests.TRANSMUTANSTEIN.ChatServer.Infrastructure;
using TRANSMUTANSTEIN.ChatServer.Domain.Core;

namespace ASPIRE.Tests.TRANSMUTANSTEIN.ChatServer.Tests;

[Collection("TRANSMUTANSTEIN ChatServer Collection")]
public sealed class ChatChannelClanTests(TRANSMUTANSTEINTestFixture fixture)
{
    [Fact]
    public async Task JoinClanChannel_NotMember_Fails()
    {
        // Arrange
        TRANSMUTANSTEINServiceProvider app = fixture.Provider;

        // 1. Connect Alice (Member of "Clan Warriors")
        // She joins "Clan Warriors", creating the channel with CLAN flags.
        const string clanName = "Warriors";
        const string clanTag = "WAR";
        const string channelName = $"Clan {clanName}";

        using TcpClient clientAlice = await ChatTestHelpers.ConnectAndAuthenticateAsync(
            app, app.ClientPort, 1001, "Alice",
            clanName: clanName, clanTag: clanTag);

        // Alice joins channel to create it
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
            await clientAlice.GetStream().WriteAsync(r.ToArray());

            // Consume response (CHANGED_CHANNEL)
            using CancellationTokenSource ctsAlice = new(TimeSpan.FromSeconds(5));
            byte[] h = new byte[4];
            await clientAlice.GetStream().ReadExactlyAsync(h, ctsAlice.Token);
            ushort l = BitConverter.ToUInt16(h, 0);
            byte[] py = new byte[l - 2];
            int rd = 0;
            while (rd < py.Length)
                rd += await clientAlice.GetStream().ReadAsync(py, rd, py.Length - rd, ctsAlice.Token);
        }

        // 2. Connect Bob (No Clan)
        using TcpClient clientBob = await ChatTestHelpers.ConnectAndAuthenticateAsync(
            app, app.ClientPort, 1002, "Bob");
        NetworkStream streamBob = clientBob.GetStream();

        // Act - Bob tries to join "Clan Warriors"
        ChatBuffer joinBob = new();
        joinBob.WriteCommand(ChatProtocol.Command.CHAT_CMD_JOIN_CHANNEL);
        joinBob.WriteString(channelName);
        byte[] pBob = [.. joinBob.Data.AsSpan(0, (int)joinBob.Size)];
        List<byte> rBob =
        [
            .. BitConverter.GetBytes((ushort)pBob.Length),
            .. pBob
        ];
        await streamBob.WriteAsync(rBob.ToArray());

        // Assert - Expect CHAT_CMD_CLAN_ADD_FAIL_PERMS (0x004C)
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));

        byte[] hBob = new byte[4];

        try
        {
            await streamBob.ReadExactlyAsync(hBob, cts.Token);
            ushort cBob = BitConverter.ToUInt16(hBob, 2);
            ushort lBob = BitConverter.ToUInt16(hBob, 0);

            Assert.Equal(ChatProtocol.Command.CHAT_CMD_WHISPER, cBob);

            // Validate payload
            int payloadSize = lBob - 2;
            byte[] payload = new byte[payloadSize];
            int readTotal = 0;
            while (readTotal < payloadSize)
                readTotal += await streamBob.ReadAsync(payload, readTotal, payloadSize - readTotal, cts.Token);

            ChatBuffer reader = new(payload);
            string sender = reader.ReadString();
            string msg = reader.ReadString();

            Assert.Equal("Channel Service", sender);
            Assert.Equal("You do not have the correct permission to access this channel.", msg);
        }
        catch (OperationCanceledException)
        {
            // Timeout implies silent failure (current behavior) or just no response
            // We expect this to happen BEFORE the fix.
            // After the fix, it should NOT throw.
            throw new Exception("Timed out waiting for response - Confirms Silent Rejection");
        }
    }

    [Fact]
    public async Task JoinClanChannel_NotMember_PrivateWhisper_Is_Not_Broadcast()
    {
        // Arrange
        TRANSMUTANSTEINServiceProvider app = fixture.Provider;

        // 1. Connect Alice (Member of "Clan Warriors") - Leader/Owner
        const string clanName = "Broadcasters";
        const string clanTag = "CAST";
        const string channelName = $"Clan {clanName}";

        using TcpClient clientAlice = await ChatTestHelpers.ConnectAndAuthenticateAsync(
            app, app.ClientPort, 1001, "Alice",
            clanName: clanName, clanTag: clanTag);

        // Alice creates the channel
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
            await clientAlice.GetStream().WriteAsync(r.ToArray());

            // Consume CHANGED_CHANNEL
            await ChatTestHelpers.ExpectCommandAsync(clientAlice.GetStream(),
                ChatProtocol.Command.CHAT_CMD_CHANGED_CHANNEL);
        }

        // 2. Connect Charlie (Alice's Clan Mate) - He joins the channel successfully
        // We need Charlie to be in the channel to see if HE gets the whisper meant for Bob
        using TcpClient clientCharlie = await ChatTestHelpers.ConnectAndAuthenticateAsync(
            app, app.ClientPort, 1003, "Charlie",
            clanName: clanName, clanTag: clanTag);

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
            await clientCharlie.GetStream().WriteAsync(r.ToArray());

            // Consume CHANGED_CHANNEL for Charlie
            await ChatTestHelpers.ExpectCommandAsync(clientCharlie.GetStream(),
                ChatProtocol.Command.CHAT_CMD_CHANGED_CHANNEL);

            // Alice also gets JOINED_CHANNEL for Charlie, consume it to keep Alice clean (optional)
        }

        // 3. Connect Bob (No Clan) - The Intruder
        using TcpClient clientBob = await ChatTestHelpers.ConnectAndAuthenticateAsync(
            app, app.ClientPort, 1002, "Bob");

        // Act - Bob tries to join "Clan Broadcasters"
        ChatBuffer joinBob = new();
        joinBob.WriteCommand(ChatProtocol.Command.CHAT_CMD_JOIN_CHANNEL);
        joinBob.WriteString(channelName);
        byte[] pBob = [.. joinBob.Data.AsSpan(0, (int)joinBob.Size)];
        List<byte> rBob =
        [
            .. BitConverter.GetBytes((ushort)pBob.Length),
            .. pBob
        ];
        await clientBob.GetStream().WriteAsync(rBob.ToArray());

        // Assert - Bob should get the Whisper
        await ChatTestHelpers.ExpectCommandAsync(clientBob.GetStream(), ChatProtocol.Command.CHAT_CMD_WHISPER);

        // Assert - Charlie (in the channel) should NOT get anything
        // Check Charlie's stream for data. It should be empty or definitely NOT a whisper.
        // We wait a bit to ensure potential broadcast would definitely arrive
        await Task.Delay(20);

        if (clientCharlie.Available > 0)
        {
            // If data is available, read it and verify it's NOT a whisper
            // Actually, strictly speaking, Charlie shouldn't receive ANYTHING regarding Bob's failed attempt.
            // But keep in mind heartbeats or other noise might occur.
            // For this specific test, getting a WHISPER is the failure condition.
            byte[] buffer = new byte[1024];
            int read = await clientCharlie.GetStream().ReadAsync(buffer);
            if (read > 2)
            {
                ushort cmd = BitConverter.ToUInt16(buffer, 2);
                if (cmd == ChatProtocol.Command.CHAT_CMD_WHISPER)
                {
                    ChatBuffer reader = new(buffer.AsSpan(0, read).ToArray());
                    reader.ReadInt16(); // Length
                    reader.ReadInt16(); // Cmd
                    string sender = reader.ReadString();
                    string msg = reader.ReadString();
                    Assert.Fail($"Charlie received broadcast whisper from {sender}: {msg}");
                }
            }
        }
    }
}
