// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net.Sockets;
using ASPIRE.Tests.TRANSMUTANSTEIN.ChatServer.Infrastructure;
using TRANSMUTANSTEIN.ChatServer.Domain.Core;

namespace ASPIRE.Tests.TRANSMUTANSTEIN.ChatServer.Tests;

[Collection("TRANSMUTANSTEIN ChatServer Collection")]
public sealed class ChatModerationTagTests(TRANSMUTANSTEINTestFixture fixture)
{
    private static async Task<(int, int)> JoinChannelAndGetId(TcpClient client, string channelName)
    {
        ChatBuffer joinBuffer = new();
        joinBuffer.WriteCommand(ChatProtocol.Command.CHAT_CMD_JOIN_CHANNEL);
        joinBuffer.WriteString(channelName);

        byte[] packet = [.. joinBuffer.Data.AsSpan(0, (int)joinBuffer.Size)];
        List<byte> rawBytes =
        [
            .. BitConverter.GetBytes((ushort)packet.Length),
            .. packet
        ];
        await client.GetStream().WriteAsync(rawBytes.ToArray());

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        byte[] header = new byte[4];
        await client.GetStream().ReadExactlyAsync(header, cts.Token);
        ushort length = BitConverter.ToUInt16(header, 0);
        ushort command = BitConverter.ToUInt16(header, 2);

        int payloadSize = length - 2;
        byte[] payload = new byte[payloadSize];
        int read = 0;
        while (read < payloadSize)
            read += await client.GetStream().ReadAsync(payload, read, payloadSize - read, cts.Token);

        if (command == ChatProtocol.Command.CHAT_CMD_CHANGED_CHANNEL)
        {
            ChatBuffer b = new(payload);
            b.ReadString(); // Name
            int id = b.ReadInt32(); // ID
            return (id, command);
        }

        return (0, command);
    }

    [Fact]
    public async Task Ban_WithClanTag_StripsTagAndBansUser()
    {
        TRANSMUTANSTEINServiceProvider app = fixture.Provider;

        int adminId = Interlocked.Increment(ref ChatTestHelpers.s_accountIdCounter);
        int targetId = Interlocked.Increment(ref ChatTestHelpers.s_accountIdCounter);
        string adminName = $"Adm_{adminId}";
        string targetName = $"Tgt_{targetId}";

        using TcpClient adminClient =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, adminId, adminName,
                accountType: AccountType.Staff);
        using TcpClient targetClient =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, targetId, targetName);

        // Join Channel
        string roomName = $"BanRm_{adminId}";
        (int channelId, _) = await JoinChannelAndGetId(adminClient, roomName);
        await JoinChannelAndGetId(targetClient, roomName);

        // Act - Ban with [TAG] prefix
        ChatBuffer buffer = new();
        buffer.WriteCommand(ChatProtocol.Command.CHAT_CMD_CHANNEL_BAN);
        buffer.WriteInt32(channelId);
        buffer.WriteString($"[PK]{targetName}"); // <--- The Test Case

        byte[] packet = [.. buffer.Data.AsSpan(0, (int)buffer.Size)];
        List<byte> rawBytes =
        [
            .. BitConverter.GetBytes((ushort)packet.Length),
            .. packet
        ];
        await adminClient.GetStream().WriteAsync(rawBytes.ToArray());

        // Assert - Target should receive KICK (0x0031), skipping preceding channel-join frames (0x0005)
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        ushort c1 = 0;
        const int maxRetries = 10;
        for (int i = 0; i < maxRetries; i++)
        {
            byte[] h1 = new byte[4];
            await targetClient.GetStream().ReadExactlyAsync(h1, cts.Token);
            c1 = BitConverter.ToUInt16(h1, 2);
            ushort l1 = BitConverter.ToUInt16(h1, 0);
            byte[] p1 = new byte[l1 - 2];
            await targetClient.GetStream().ReadExactlyAsync(p1, cts.Token);

            if (c1 == ChatProtocol.Command.CHAT_CMD_CHANNEL_KICK) break;
        }

        Assert.Equal(ChatProtocol.Command.CHAT_CMD_CHANNEL_KICK, c1);
    }

    [Fact]
    public async Task Silence_WithClanTag_StripsTagAndSilencesUser()
    {
        TRANSMUTANSTEINServiceProvider app = fixture.Provider;

        int adminId = Random.Shared.Next(2000, 3000);
        int targetId = adminId + 1;
        string adminName = $"Adm_{adminId}";
        string targetName = $"Tgt_{targetId}";

        using TcpClient adminClient =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, adminId, adminName,
                accountType: AccountType.Staff);
        using TcpClient targetClient =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, targetId, targetName);

        string roomName = $"SilRm_{adminId}";
        (int channelId, _) = await JoinChannelAndGetId(adminClient, roomName);
        await JoinChannelAndGetId(targetClient, roomName);

        // Act - Silence with [TAG]
        ChatBuffer buffer = new();
        buffer.WriteCommand(ChatProtocol.Command.CHAT_CMD_CHANNEL_SILENCE_USER);
        buffer.WriteInt32(channelId);
        buffer.WriteString($"[PK]{targetName}");
        buffer.WriteInt32(60000); // 1 min

        byte[] packet = [.. buffer.Data.AsSpan(0, (int)buffer.Size)];
        List<byte> rawBytes =
        [
            .. BitConverter.GetBytes((ushort)packet.Length),
            .. packet
        ];
        await adminClient.GetStream().WriteAsync(rawBytes.ToArray());

        // Assert - Target should receive SILENCED (0x0037)
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        ushort c1 = 0;
        const int maxRetries = 10;
        for (int i = 0; i < maxRetries; i++)
        {
            byte[] h1 = new byte[4];
            await targetClient.GetStream().ReadExactlyAsync(h1, cts.Token);
            c1 = BitConverter.ToUInt16(h1, 2);
            ushort l1 = BitConverter.ToUInt16(h1, 0);
            byte[] p1 = new byte[l1 - 2];
            int r = 0;
            while (r < p1.Length) r += await targetClient.GetStream().ReadAsync(p1, r, p1.Length - r, cts.Token);

            if (c1 == ChatProtocol.Command.CHAT_CMD_CHANNEL_SILENCE_PLACED) break;
        }

        Assert.Equal(ChatProtocol.Command.CHAT_CMD_CHANNEL_SILENCE_PLACED, c1);
    }

    [Fact]
    public async Task Unban_WithClanTag_StripsTagAndUnbansUser()
    {
        // Arrange
        TRANSMUTANSTEINServiceProvider app = fixture.Provider;

        // 1. Admin connects
        int adminId = Random.Shared.Next(100, 1000);
        int observerId = adminId + 1;
        int targetId = observerId + 1;

        string adminName = $"Adm_{adminId}";
        string observerName = $"Obs_{observerId}";
        string targetName = $"Tgt_{targetId}";

        using TcpClient adminClient = await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, adminId,
            adminName,
            accountType: AccountType.Staff); // Officer can Ban

        // 2. Observer connects (To verify Unban broadcast)
        using TcpClient observerClient =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, observerId, observerName);

        // Create "Target" user with REAL Clan Tag in DB
        string targetDisplay = $"[PK]{targetName}";
        using TcpClient targetClient = await ChatTestHelpers.ConnectAndAuthenticateAsync(
            app, app.ClientPort, targetId, targetName, // Use CLEAN name for Account
            clanName: "PhantomKind", clanTag: "PK"); // Assign Clan

        // All join "TagUnbanRoom"
        string roomName = $"UnbRm_{adminId}";
        int channelId = await ChatTestHelpers.JoinChannelAndGetId(adminClient, roomName);
        await ChatTestHelpers.JoinChannelAndGetId(observerClient, roomName);
        await ChatTestHelpers.JoinChannelAndGetId(targetClient, roomName);

        // 1. Ban "TargetUnban" (using clean name to ensure ban works)
        ChatBuffer banBuffer = new();
        banBuffer.WriteCommand(ChatProtocol.Command.CHAT_CMD_CHANNEL_BAN);
        banBuffer.WriteInt32(channelId);
        banBuffer.WriteString(targetName); // Use dynamic Name (Exact Match)

        byte[] banPacket = [.. banBuffer.Data.AsSpan(0, (int)banBuffer.Size)];
        List<byte> banRaw =
        [
            .. BitConverter.GetBytes((ushort)banPacket.Length),
            .. banPacket
        ];
        await adminClient.GetStream().WriteAsync(banRaw.ToArray());

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));

        // Read Obs: Ban Notification
        ushort c1 = 0;
        const int maxRetries = 10;
        for (int i = 0; i < maxRetries; i++)
        {
            byte[] h = new byte[4];
            await observerClient.GetStream().ReadExactlyAsync(h, cts.Token);
            c1 = BitConverter.ToUInt16(h, 2);
            ushort len = BitConverter.ToUInt16(h, 0);
            byte[] p = new byte[len - 2];
            int r = 0;
            while (r < p.Length) r += await observerClient.GetStream().ReadAsync(p, r, p.Length - r, cts.Token);

            if (c1 == ChatProtocol.Command.CHAT_CMD_CHANNEL_BAN) break;
            // Continue if not BAN
        }

        Assert.Equal(ChatProtocol.Command.CHAT_CMD_CHANNEL_BAN, c1);

        // 2. Act - Unban with [TAG]
        ChatBuffer unbanBuffer = new();
        unbanBuffer.WriteCommand(ChatProtocol.Command.CHAT_CMD_CHANNEL_UNBAN);
        unbanBuffer.WriteInt32(channelId);
        unbanBuffer.WriteString(targetDisplay); // Use dynamic Name with Tag ([PK])

        byte[] unbanPacket = [.. unbanBuffer.Data.AsSpan(0, (int)unbanBuffer.Size)];
        List<byte> unbanRaw =
        [
            .. BitConverter.GetBytes((ushort)unbanPacket.Length),
            .. unbanPacket
        ];
        await adminClient.GetStream().WriteAsync(unbanRaw.ToArray(), cts.Token);

        // Assert - Observer receives UNBAN (0x0033)
        ushort c2 = 0;
        for (int i = 0; i < maxRetries; i++)
        {
            byte[] h = new byte[4];
            await observerClient.GetStream().ReadExactlyAsync(h, cts.Token);
            c2 = BitConverter.ToUInt16(h, 2);
            ushort len = BitConverter.ToUInt16(h, 0);
            byte[] p = new byte[len - 2];
            int r = 0;
            while (r < p.Length) r += await observerClient.GetStream().ReadAsync(p, r, p.Length - r, cts.Token);

            if (c2 == ChatProtocol.Command.CHAT_CMD_CHANNEL_UNBAN) break;
        }

        Assert.Equal(ChatProtocol.Command.CHAT_CMD_CHANNEL_UNBAN, c2);
    }
}
