// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net.Sockets;
using ASPIRE.Tests.TRANSMUTANSTEIN.ChatServer.Infrastructure;
using TRANSMUTANSTEIN.ChatServer.Domain.Clans;
using TRANSMUTANSTEIN.ChatServer.Domain.Core;

namespace ASPIRE.Tests.TRANSMUTANSTEIN.ChatServer.Tests;

[Collection("TRANSMUTANSTEIN ChatServer Collection")]
public sealed class ChatClanIntegrationTests(TRANSMUTANSTEINTestFixture fixture)
{
    [Fact]
    public async Task ChatClanIntegration_ClanAddMember_Success()
    {
        TRANSMUTANSTEINServiceProvider app = fixture.Provider;

        const int inviterId = 9701;
        const int targetId = 9702;
        const int clanId = 1200;

        await ChatTestHelpers.SeedLock.WaitAsync();
        try
        {
            using IServiceScope scope = app.Services.CreateScope();
            MerrickContext db = scope.ServiceProvider.GetRequiredService<MerrickContext>();
            Role role = await db.Roles.FindAsync(1) ?? new Role { ID = 1, Name = "User" };
            if (role.ID == 0) db.Roles.Add(role);

            if (await db.Accounts.FindAsync(inviterId) == null)
            {
                User u1 = new()
                {
                    ID = inviterId,
                    EmailAddress = $"inviter{inviterId}@test.com",
                    SRPPasswordHash = "h",
                    SRPPasswordSalt = "s",
                    PBKDF2PasswordHash = "h",
                    Role = role
                };
                Account a1 = new() { ID = inviterId, Name = "ClanInviter", IsMain = true, User = u1 };
                u1.Accounts = [a1];
                db.Users.Add(u1);

                Clan clan = new()
                {
                    ID = clanId, Name = "AddMemClan", Tag = "AMC",
                    TimestampCreated = TimeProvider.System.GetUtcNow().UtcDateTime
                };
                db.Clans.Add(clan);
                a1.Clan = clan;
                a1.ClanTier = ClanTier.Officer;

                User u2 = new()
                {
                    ID = targetId,
                    EmailAddress = $"target{targetId}@test.com",
                    SRPPasswordHash = "h",
                    SRPPasswordSalt = "s",
                    PBKDF2PasswordHash = "h",
                    Role = role
                };
                Account a2 = new() { ID = targetId, Name = "ClanTarget", IsMain = true, User = u2 };
                u2.Accounts = [a2];
                db.Users.Add(u2);
                await db.SaveChangesAsync();
            }
        }
        finally
        {
            ChatTestHelpers.SeedLock.Release();
        }

        using TcpClient inviterClient =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, inviterId, "ClanInviter");
        using TcpClient targetClient =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, targetId, "ClanTarget");

        ChatBuffer cmd = new();
        cmd.WriteCommand(ChatProtocol.Command.CHAT_CMD_CLAN_ADD_MEMBER);
        cmd.WriteString("ClanTarget");

        byte[] packet = [.. cmd.Data.AsSpan(0, (int)cmd.Size)];
        List<byte> rawBytes =
        [
            .. BitConverter.GetBytes((ushort)packet.Length),
            .. packet
        ];
        await inviterClient.GetStream().WriteAsync(rawBytes.ToArray());

        ChatBuffer resp = await ChatTestHelpers.ExpectCommandAsync(targetClient.GetStream(),
            ChatProtocol.Command.CHAT_CMD_CLAN_ADD_MEMBER, 5);
        string inviterName = resp.ReadString();
        string clanName = resp.ReadString();

        Assert.Equal("ClanInviter", inviterName);
        Assert.Equal("AddMemClan", clanName);
    }

    [Fact]
    public async Task ClanAdd_Accepted_Creator_Success()
    {
        TRANSMUTANSTEINServiceProvider app = fixture.Provider;

        const int creatorId = 9801;
        const int targetId = 9802;

        await ChatTestHelpers.SeedLock.WaitAsync();
        try
        {
            using IServiceScope scope = app.Services.CreateScope();
            MerrickContext db = scope.ServiceProvider.GetRequiredService<MerrickContext>();
            Role role = await db.Roles.FindAsync(1) ?? new Role { ID = 1, Name = "User" };
            if (role.ID == 0) db.Roles.Add(role);

            if (await db.Accounts.FindAsync(creatorId) == null)
            {
                User u1 = new()
                {
                    ID = creatorId,
                    EmailAddress = $"creator{creatorId}@test.com",
                    SRPPasswordHash = "h",
                    SRPPasswordSalt = "s",
                    PBKDF2PasswordHash = "h",
                    Role = role
                };
                Account a1 = new() { ID = creatorId, Name = "AddCreator", IsMain = true, User = u1 };
                u1.Accounts = [a1];
                db.Users.Add(u1);

                User u2 = new()
                {
                    ID = targetId,
                    EmailAddress = $"target{targetId}@test.com",
                    SRPPasswordHash = "h",
                    SRPPasswordSalt = "s",
                    PBKDF2PasswordHash = "h",
                    Role = role
                };
                Account a2 = new() { ID = targetId, Name = "AddTarget", IsMain = true, User = u2 };
                u2.Accounts = [a2];
                db.Users.Add(u2);
                await db.SaveChangesAsync();
            }

            IPendingClanService pendingService = scope.ServiceProvider.GetRequiredService<IPendingClanService>();
            PendingClan pending = new()
            {
                ClanName = "CreatorClan",
                ClanTag = "CRC",
                CreatorAccountId = creatorId,
                MembersAccountId = [targetId],
                Accepted = [false],
                CreationTime = TimeProvider.System.GetUtcNow().UtcDateTime
            };
            pendingService.InsertPendingClan(pending);
        }
        finally
        {
            ChatTestHelpers.SeedLock.Release();
        }

        using TcpClient creatorClient =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, creatorId, "AddCreator");
        using TcpClient targetClient =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, targetId, "AddTarget");

        ChatBuffer cmd = new();
        cmd.WriteCommand(ChatProtocol.Command.CHAT_CMD_CLAN_ADD_ACCEPTED);

        byte[] packet = [.. cmd.Data.AsSpan(0, (int)cmd.Size)];
        List<byte> rawBytes =
        [
            .. BitConverter.GetBytes((ushort)packet.Length),
            .. packet
        ];
        await targetClient.GetStream().WriteAsync(rawBytes.ToArray());

        ChatBuffer resp = await ChatTestHelpers.ExpectCommandAsync(targetClient.GetStream(),
            ChatProtocol.Command.CHAT_CMD_NEW_CLAN_MEMBER, 5);
        int targetIdResp = resp.ReadInt32();
        int clanIdResp = resp.ReadInt32();
        string nameResp = resp.ReadString();
        string tagResp = resp.ReadString();

        Assert.Equal(targetId, targetIdResp);
        Assert.Equal("CreatorClan", nameResp);
    }

    [Fact]
    public async Task ChatClanIntegration_TrackPlayerAction_Success()
    {
        TRANSMUTANSTEINServiceProvider app = fixture.Provider;

        const int userId = 9901;

        await ChatTestHelpers.SeedLock.WaitAsync();
        try
        {
            using IServiceScope scope = app.Services.CreateScope();
            MerrickContext db = scope.ServiceProvider.GetRequiredService<MerrickContext>();
            Role role = await db.Roles.FindAsync(1) ?? new Role { ID = 1, Name = "User" };
            if (role.ID == 0) db.Roles.Add(role);

            if (await db.Accounts.FindAsync(userId) == null)
            {
                User u1 = new()
                {
                    ID = userId,
                    EmailAddress = $"user{userId}@test.com",
                    SRPPasswordHash = "h",
                    SRPPasswordSalt = "s",
                    PBKDF2PasswordHash = "h",
                    Role = role
                };
                Account a1 = new() { ID = userId, Name = "TrackActionUser", IsMain = true, User = u1 };
                u1.Accounts = [a1];
                db.Users.Add(u1);
                await db.SaveChangesAsync();
            }
        }
        finally
        {
            ChatTestHelpers.SeedLock.Release();
        }

        using TcpClient client =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, userId, "TrackActionUser");

        ChatBuffer cmd = new();
        cmd.WriteCommand(0x00B9); // CHAT_CMD_TRACK_PLAYER_ACTION
        cmd.WriteInt8(0x01); // AC_DAILY_LOGINS or some dummy action

        byte[] packet = [.. cmd.Data.AsSpan(0, (int)cmd.Size)];
        List<byte> rawBytes =
        [
            .. BitConverter.GetBytes((ushort)packet.Length),
            .. packet
        ];
        await client.GetStream().WriteAsync(rawBytes.ToArray());

        await Task.Delay(50); // Allow server to process without crash
        Assert.True(client.Connected);
    }
}
