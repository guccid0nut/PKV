// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net.Sockets;
using ASPIRE.Tests.TRANSMUTANSTEIN.ChatServer.Infrastructure;
using TRANSMUTANSTEIN.ChatServer.Domain.Core;

namespace ASPIRE.Tests.TRANSMUTANSTEIN.ChatServer.Tests;

[Collection("TRANSMUTANSTEIN ChatServer Collection")]
public sealed class ChatChannelCommandsTests(TRANSMUTANSTEINTestFixture fixture)
{
    private static async Task SetStaffRoleAsync(TRANSMUTANSTEINServiceProvider app, int accountId)
    {
        await ChatTestHelpers.SeedLock.WaitAsync();
        try
        {
            using IServiceScope scope = app.Services.CreateScope();
            MerrickContext db = scope.ServiceProvider.GetRequiredService<MerrickContext>();
            Role? staffRole = await db.Roles.FirstOrDefaultAsync(r => r.Name == "Staff" || r.ID == 2);
            if (staffRole == null)
            {
                staffRole = new Role { ID = 2, Name = "Staff" };
                db.Roles.Add(staffRole);
                try
                {
                    await db.SaveChangesAsync();
                }
                catch (DbUpdateException)
                {
                    db.ChangeTracker.Clear();
                    staffRole = await db.Roles.FirstOrDefaultAsync(r => r.Name == "Staff" || r.ID == 2);
                }
            }

            Account? adminAcc = await db.Accounts.Include(a => a.User).FirstOrDefaultAsync(a => a.ID == accountId);
            if (adminAcc != null && staffRole != null)
            {
                adminAcc.User.Role = staffRole;
                db.Update(adminAcc.User);
                await db.SaveChangesAsync();
            }
        }
        finally
        {
            ChatTestHelpers.SeedLock.Release();
        }
    }

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

        while (true)
        {
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
                ChatBuffer buffer = new(payload);
                string chName = buffer.ReadString();
                int channelId = buffer.ReadInt32();
                byte topicFlags = buffer.ReadInt8();
                string topic = buffer.ReadString();

                return (channelId, topicFlags);
            }
        }
    }

    [Fact]
    public async Task JoinChannel_BroadcastsUserJoinedChannel_ToExistingUsers()
    {
        TRANSMUTANSTEINServiceProvider app = fixture.Provider;

        int id1 = Interlocked.Increment(ref ChatTestHelpers.s_accountIdCounter);
        int id2 = Interlocked.Increment(ref ChatTestHelpers.s_accountIdCounter);

        using TcpClient client1 =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, id1, "Alice");
        using TcpClient client2 =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, id2, "Bob");

        (int channelId1, _) = await JoinChannelAndGetId(client1, "PublicGeneral");
        (int channelId2, _) = await JoinChannelAndGetId(client2, "PublicGeneral");

        Assert.Equal(channelId1, channelId2);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        ushort command = 0;
        byte[] payload = [];
        while (true)
        {
            byte[] header = new byte[4];
            await client1.GetStream().ReadExactlyAsync(header, cts.Token);
            ushort length = BitConverter.ToUInt16(header, 0);
            command = BitConverter.ToUInt16(header, 2);

            payload = new byte[length - 2];
            await client1.GetStream().ReadExactlyAsync(payload, cts.Token);

            if (command == ChatProtocol.Command.CHAT_CMD_JOINED_CHANNEL) break;
        }

        Assert.Equal(ChatProtocol.Command.CHAT_CMD_JOINED_CHANNEL, command);
        ChatBuffer response = new(payload);
        int recChanId = response.ReadInt32();
        string recAccountName = response.ReadString();
        int recAccountId = response.ReadInt32();

        Assert.Equal(channelId1, recChanId);
        Assert.Equal(id2, recAccountId);
    }

    [Fact]
    public async Task ChatChannelCommands_Promote_BroadcastsPromotion()
    {
        TRANSMUTANSTEINServiceProvider app = fixture.Provider;

        int idAdmin = Interlocked.Increment(ref ChatTestHelpers.s_accountIdCounter);
        int idUser = Interlocked.Increment(ref ChatTestHelpers.s_accountIdCounter);

        using TcpClient adminClient =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, idAdmin, $"AdminUser_{idAdmin}");
        using TcpClient userClient =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, idUser, $"NormalUser_{idUser}");

        await SetStaffRoleAsync(app, idAdmin);

        // Admin Creates/Joins Channel
        (int channelId, _) = await JoinChannelAndGetId(adminClient, "PromoteRoom");

        // User Joins Channel
        await JoinChannelAndGetId(userClient, "PromoteRoom");

        // Act - Promote
        ChatBuffer buffer = new();
        buffer.WriteCommand(ChatProtocol.Command.CHAT_CMD_CHANNEL_PROMOTE); // 0x003A
        buffer.WriteInt32(channelId);
        buffer.WriteInt32(idUser); // Target ID

        byte[] packet = [.. buffer.Data.AsSpan(0, (int)buffer.Size)];
        List<byte> rawBytes =
        [
            .. BitConverter.GetBytes((ushort)packet.Length),
            .. packet
        ];
        await adminClient.GetStream().WriteAsync(rawBytes.ToArray());

        // Assert - Receive 0x003A on user client
        ChatBuffer response =
            await ChatTestHelpers.ExpectCommandAsync(userClient.GetStream(),
                ChatProtocol.Command.CHAT_CMD_CHANNEL_PROMOTE);

        int respChanId = response.ReadInt32();
        int respTargetId = response.ReadInt32();
        byte respRank = response.ReadInt8();

        Assert.Equal(channelId, respChanId);
        Assert.Equal(idUser, respTargetId);
        Assert.Equal((byte)ChatProtocol.AdminLevel.CHAT_CLIENT_ADMIN_OFFICER, respRank);
    }

    [Fact]
    public async Task ChatChannelCommands_Ban_BroadcastsBanAndKicksUser()
    {
        TRANSMUTANSTEINServiceProvider app = fixture.Provider;

        int idAdmin = Interlocked.Increment(ref ChatTestHelpers.s_accountIdCounter);
        int idVictim = Interlocked.Increment(ref ChatTestHelpers.s_accountIdCounter);
        int idObserver = Interlocked.Increment(ref ChatTestHelpers.s_accountIdCounter);
        string victimName = $"BanVic_{idVictim}";
        string adminName = $"Admin_{idAdmin}";
        string obsName = $"Obs_{idObserver}";

        using TcpClient adminClient =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, idAdmin, adminName);
        using TcpClient userClient =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, idVictim, victimName);
        using TcpClient observerClient =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, idObserver, obsName);

        await SetStaffRoleAsync(app, idAdmin);

        // Join
        (int channelId, _) = await JoinChannelAndGetId(adminClient, "BanRoom");
        await JoinChannelAndGetId(userClient, "BanRoom");
        await JoinChannelAndGetId(observerClient, "BanRoom");

        // Act - Ban
        ChatBuffer buffer = new();
        buffer.WriteCommand(ChatProtocol.Command.CHAT_CMD_CHANNEL_BAN);
        buffer.WriteInt32(channelId);
        buffer.WriteString(victimName);
        buffer.WriteString("Testing Ban");

        byte[] packet = [.. buffer.Data.AsSpan(0, (int)buffer.Size)];
        List<byte> rawBytes =
        [
            .. BitConverter.GetBytes((ushort)packet.Length),
            .. packet
        ];
        await adminClient.GetStream().WriteAsync(rawBytes.ToArray());

        // Assert - Observer sees Ban Event (0x003D)
        ChatBuffer banEvent =
            await ChatTestHelpers.ExpectCommandAsync(observerClient.GetStream(),
                ChatProtocol.Command.CHAT_CMD_CHANNEL_BAN);
        int banChanId = banEvent.ReadInt32();
        int bannerAccountId = banEvent.ReadInt32();
        string bannedAccountName = banEvent.ReadString();

        Assert.Equal(channelId, banChanId);
        Assert.Equal(idAdmin, bannerAccountId);
        Assert.Equal(victimName, bannedAccountName);

        // Assert - User also sees Kicked Event (0x003C)
        ChatBuffer kickEvent =
            await ChatTestHelpers.ExpectCommandAsync(userClient.GetStream(),
                ChatProtocol.Command.CHAT_CMD_CHANNEL_KICK);
        int kickChanId = kickEvent.ReadInt32();
        int kickerId = kickEvent.ReadInt32();
        int kickTargetId = kickEvent.ReadInt32();

        Assert.Equal(channelId, kickChanId);
        Assert.Equal(idAdmin, kickerId);
        Assert.Equal(idVictim, kickTargetId);
    }

    [Fact]
    public async Task ChatChannelCommands_Unban_BroadcastsUnban()
    {
        TRANSMUTANSTEINServiceProvider app = fixture.Provider;

        int idAdmin = Interlocked.Increment(ref ChatTestHelpers.s_accountIdCounter);
        int idVictim = Interlocked.Increment(ref ChatTestHelpers.s_accountIdCounter);
        int idObserver = Interlocked.Increment(ref ChatTestHelpers.s_accountIdCounter);
        string victimName = $"UnbVic_{idVictim}";
        string adminName = $"Admin_{idAdmin}";
        string obsName = $"Obs_{idObserver}";

        using TcpClient adminClient =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, idAdmin, adminName);
        using TcpClient userClient =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, idVictim, victimName);
        using TcpClient observerClient =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, idObserver, obsName);

        await SetStaffRoleAsync(app, idAdmin);

        (int channelId, _) = await JoinChannelAndGetId(adminClient, "UnbanRoom");
        await JoinChannelAndGetId(userClient, "UnbanRoom");
        await JoinChannelAndGetId(observerClient, "UnbanRoom");

        // Ban first
        ChatBuffer banBuffer = new();
        banBuffer.WriteCommand(ChatProtocol.Command.CHAT_CMD_CHANNEL_BAN);
        banBuffer.WriteInt32(channelId);
        banBuffer.WriteString(victimName);
        banBuffer.WriteString("Ban reason");

        byte[] banPacket = [.. banBuffer.Data.AsSpan(0, (int)banBuffer.Size)];
        List<byte> banRawBytes =
        [
            .. BitConverter.GetBytes((ushort)banPacket.Length),
            .. banPacket
        ];
        await adminClient.GetStream().WriteAsync(banRawBytes.ToArray());

        await ChatTestHelpers.ExpectCommandAsync(observerClient.GetStream(), ChatProtocol.Command.CHAT_CMD_CHANNEL_BAN);

        // Act - Unban
        ChatBuffer unbanBuffer = new();
        unbanBuffer.WriteCommand(ChatProtocol.Command.CHAT_CMD_CHANNEL_UNBAN);
        unbanBuffer.WriteInt32(channelId);
        unbanBuffer.WriteString(victimName);

        byte[] unbanPacket = [.. unbanBuffer.Data.AsSpan(0, (int)unbanBuffer.Size)];
        List<byte> unbanRawBytes =
        [
            .. BitConverter.GetBytes((ushort)unbanPacket.Length),
            .. unbanPacket
        ];
        await adminClient.GetStream().WriteAsync(unbanRawBytes.ToArray());

        // Assert - Observer sees Unban Event (0x003E)
        ChatBuffer unbanEvent =
            await ChatTestHelpers.ExpectCommandAsync(observerClient.GetStream(),
                ChatProtocol.Command.CHAT_CMD_CHANNEL_UNBAN);
        int unbanChanId = unbanEvent.ReadInt32();
        int unbannerAccountId = unbanEvent.ReadInt32();
        string unbannedAccountName = unbanEvent.ReadString();

        Assert.Equal(channelId, unbanChanId);
        Assert.Equal(idAdmin, unbannerAccountId);
        Assert.Equal(victimName, unbannedAccountName);
    }

    [Fact]
    public async Task ChatChannelCommands_Demote_BroadcastsDemotion()
    {
        TRANSMUTANSTEINServiceProvider app = fixture.Provider;

        int idAdmin = Interlocked.Increment(ref ChatTestHelpers.s_accountIdCounter);
        int idUser = Interlocked.Increment(ref ChatTestHelpers.s_accountIdCounter);

        using TcpClient adminClient =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, idAdmin, $"AdminUser_{idAdmin}");
        using TcpClient userClient =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, idUser, $"NormalUser_{idUser}");

        await SetStaffRoleAsync(app, idAdmin);

        (int channelId, _) = await JoinChannelAndGetId(adminClient, "DemoteRoom");
        await JoinChannelAndGetId(userClient, "DemoteRoom");

        // Act - Promote User first so they are in CustomAdmins
        ChatBuffer promoteBuffer = new();
        promoteBuffer.WriteCommand(ChatProtocol.Command.CHAT_CMD_CHANNEL_PROMOTE);
        promoteBuffer.WriteInt32(channelId);
        promoteBuffer.WriteInt32(idUser);

        byte[] promotePacket = [.. promoteBuffer.Data.AsSpan(0, (int)promoteBuffer.Size)];
        List<byte> promoteRawBytes =
        [
            .. BitConverter.GetBytes((ushort)promotePacket.Length),
            .. promotePacket
        ];
        await adminClient.GetStream().WriteAsync(promoteRawBytes.ToArray());

        // Wait for promote to complete
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        while (true)
        {
            byte[] header = new byte[4];
            await userClient.GetStream().ReadExactlyAsync(header, cts.Token);
            ushort length = BitConverter.ToUInt16(header, 0);
            ushort command = BitConverter.ToUInt16(header, 2);
            byte[] payload = new byte[length - 2];
            await userClient.GetStream().ReadExactlyAsync(payload, cts.Token);
            if (command == ChatProtocol.Command.CHAT_CMD_CHANNEL_PROMOTE) break;
        }

        // Act - Demote
        ChatBuffer buffer = new();
        buffer.WriteCommand(ChatProtocol.Command.CHAT_CMD_CHANNEL_DEMOTE);
        buffer.WriteInt32(channelId);
        buffer.WriteInt32(idUser);

        byte[] packet = [.. buffer.Data.AsSpan(0, (int)buffer.Size)];
        List<byte> rawBytes =
        [
            .. BitConverter.GetBytes((ushort)packet.Length),
            .. packet
        ];
        await adminClient.GetStream().WriteAsync(rawBytes.ToArray(), cts.Token);

        // Assert - Receive 0x003B on user client
        ushort finalCommand = 0;
        byte[] finalPayload = [];
        while (true)
        {
            byte[] header = new byte[4];
            await userClient.GetStream().ReadExactlyAsync(header, cts.Token);
            ushort length = BitConverter.ToUInt16(header, 0);
            finalCommand = BitConverter.ToUInt16(header, 2);

            finalPayload = new byte[length - 2];
            await userClient.GetStream().ReadExactlyAsync(finalPayload, cts.Token);

            if (finalCommand == ChatProtocol.Command.CHAT_CMD_CHANNEL_DEMOTE) break;
        }

        Assert.Equal(ChatProtocol.Command.CHAT_CMD_CHANNEL_DEMOTE, finalCommand);
        ChatBuffer response = new(finalPayload);
        int respChanId = response.ReadInt32();
        int respTargetId = response.ReadInt32();

        Assert.Equal(channelId, respChanId);
        Assert.Equal(idUser, respTargetId);
    }

    [Fact]
    public async Task ChatChannelCommands_Kick_BroadcastsKick()
    {
        TRANSMUTANSTEINServiceProvider app = fixture.Provider;

        int idAdmin = Interlocked.Increment(ref ChatTestHelpers.s_accountIdCounter);
        int idUser = Interlocked.Increment(ref ChatTestHelpers.s_accountIdCounter);

        using TcpClient adminClient =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, idAdmin, $"AdminUser_{idAdmin}");
        using TcpClient userClient =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, idUser, $"NormalUser_{idUser}");

        await SetStaffRoleAsync(app, idAdmin);

        (int channelId, _) = await JoinChannelAndGetId(adminClient, "KickRoom");
        await JoinChannelAndGetId(userClient, "KickRoom");

        ChatBuffer buffer = new();
        buffer.WriteCommand(ChatProtocol.Command.CHAT_CMD_CHANNEL_KICK);
        buffer.WriteInt32(channelId);
        buffer.WriteInt32(idUser);

        byte[] packet = [.. buffer.Data.AsSpan(0, (int)buffer.Size)];
        List<byte> rawBytes =
        [
            .. BitConverter.GetBytes((ushort)packet.Length),
            .. packet
        ];
        await adminClient.GetStream().WriteAsync(rawBytes.ToArray());

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        ushort command = 0;
        byte[] payload = [];
        while (true)
        {
            byte[] header = new byte[4];
            await userClient.GetStream().ReadExactlyAsync(header, cts.Token);
            ushort length = BitConverter.ToUInt16(header, 0);
            command = BitConverter.ToUInt16(header, 2);

            payload = new byte[length - 2];
            await userClient.GetStream().ReadExactlyAsync(payload, cts.Token);

            if (command == ChatProtocol.Command.CHAT_CMD_CHANNEL_KICK) break;
        }

        Assert.Equal(ChatProtocol.Command.CHAT_CMD_CHANNEL_KICK, command);
        ChatBuffer response = new(payload);
        int respChanId = response.ReadInt32();
        int respKickerId = response.ReadInt32();
        int respTargetId = response.ReadInt32();

        Assert.Equal(channelId, respChanId);
        Assert.Equal(idAdmin, respKickerId);
        Assert.Equal(idUser, respTargetId);
    }

    [Fact]
    public async Task ChatChannelCommands_SetTopic_BroadcastsTopic()
    {
        TRANSMUTANSTEINServiceProvider app = fixture.Provider;

        int idAdmin = Random.Shared.Next(7000, 7999);
        using TcpClient adminClient =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, idAdmin, $"AdminUser_{idAdmin}");

        await SetStaffRoleAsync(app, idAdmin);

        (int channelId, _) = await JoinChannelAndGetId(adminClient, "TopicRoom");

        ChatBuffer buffer = new();
        buffer.WriteCommand(ChatProtocol.Command.CHAT_CMD_CHANNEL_TOPIC);
        buffer.WriteInt32(channelId);
        buffer.WriteString("New Topic");

        byte[] packet = [.. buffer.Data.AsSpan(0, (int)buffer.Size)];
        List<byte> rawBytes =
        [
            .. BitConverter.GetBytes((ushort)packet.Length),
            .. packet
        ];
        await adminClient.GetStream().WriteAsync(rawBytes.ToArray());

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        ushort command = 0;
        byte[] payload = [];
        while (true)
        {
            byte[] header = new byte[4];
            await adminClient.GetStream().ReadExactlyAsync(header, cts.Token);
            ushort length = BitConverter.ToUInt16(header, 0);
            command = BitConverter.ToUInt16(header, 2);

            payload = new byte[length - 2];
            await adminClient.GetStream().ReadExactlyAsync(payload, cts.Token);

            if (command == ChatProtocol.Command.CHAT_CMD_CHANNEL_TOPIC) break;
        }

        Assert.Equal(ChatProtocol.Command.CHAT_CMD_CHANNEL_TOPIC, command);
        ChatBuffer response = new(payload);
        int respChanId = response.ReadInt32();
        string topic = response.ReadString();

        Assert.Equal(channelId, respChanId);
        Assert.Equal("New Topic", topic);
    }

    [Fact]
    public async Task ChatChannelCommands_Silence_BroadcastsSilence()
    {
        TRANSMUTANSTEINServiceProvider app = fixture.Provider;

        int idAdmin = Random.Shared.Next(7000, 7999);
        int idUser = Random.Shared.Next(8000, 8999);

        using TcpClient adminClient =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, idAdmin, $"AdminUser_{idAdmin}");
        using TcpClient userClient =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, idUser, $"NormalUser_{idUser}");

        await SetStaffRoleAsync(app, idAdmin);

        (int channelId, _) = await JoinChannelAndGetId(adminClient, "SilenceRoom");
        await JoinChannelAndGetId(userClient, "SilenceRoom");

        ChatBuffer buffer = new();
        buffer.WriteCommand(ChatProtocol.Command.CHAT_CMD_CHANNEL_SILENCE_USER);
        buffer.WriteInt32(channelId);
        buffer.WriteString($"NormalUser_{idUser}"); // Command takes username
        buffer.WriteInt32(60000); // duration in ms

        byte[] packet = [.. buffer.Data.AsSpan(0, (int)buffer.Size)];
        List<byte> rawBytes =
        [
            .. BitConverter.GetBytes((ushort)packet.Length),
            .. packet
        ];
        await adminClient.GetStream().WriteAsync(rawBytes.ToArray());

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        ushort command = 0;
        byte[] payload = [];
        while (true)
        {
            byte[] header = new byte[4];
            await userClient.GetStream().ReadExactlyAsync(header, cts.Token);
            ushort length = BitConverter.ToUInt16(header, 0);
            command = BitConverter.ToUInt16(header, 2);

            payload = new byte[length - 2];
            await userClient.GetStream().ReadExactlyAsync(payload, cts.Token);

            if (command == ChatProtocol.Command.CHAT_CMD_CHANNEL_SILENCE_PLACED) break;
        }

        Assert.Equal(ChatProtocol.Command.CHAT_CMD_CHANNEL_SILENCE_PLACED, command);
        ChatBuffer response = new(payload);
        string respChanName = response.ReadString();
        string respRequesterName = response.ReadString();
        string respTargetName = response.ReadString();
        int respDuration = response.ReadInt32();

        Assert.Equal("SilenceRoom", respChanName);
        Assert.Equal($"AdminUser_{idAdmin}", respRequesterName);
        Assert.Equal($"NormalUser_{idUser}", respTargetName);
        Assert.Equal(60000, respDuration);
    }
}
