// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net.Sockets;
using ASPIRE.Tests.TRANSMUTANSTEIN.ChatServer.Infrastructure;
using TRANSMUTANSTEIN.ChatServer.Domain.Clans;
using TRANSMUTANSTEIN.ChatServer.Domain.Core;

namespace ASPIRE.Tests.TRANSMUTANSTEIN.ChatServer.Tests;

[Collection("TRANSMUTANSTEIN ChatServer Collection")]
public sealed class ChatClanTests(TRANSMUTANSTEINTestFixture fixture)
{
    [Fact]
    public async Task ClanAdd_Accepted_Success()
    {
        // 1. Arrange
        TRANSMUTANSTEINServiceProvider app = fixture.Provider;

        int creatorId = Interlocked.Increment(ref ChatTestHelpers.s_accountIdCounter);
        int joinerId = Interlocked.Increment(ref ChatTestHelpers.s_accountIdCounter);
        int clanId = Interlocked.Increment(ref ChatTestHelpers.s_accountIdCounter);
        const string inviteKey = "pending_invite_key";

        // Seed DB and Redis
        await ChatTestHelpers.SeedLock.WaitAsync();
        try
        {
            using IServiceScope scope = app.Services.CreateScope();
            MerrickContext db = scope.ServiceProvider.GetRequiredService<MerrickContext>();
            IPendingClanService pendingService = scope.ServiceProvider.GetRequiredService<IPendingClanService>();

            // Ensure Roles & Users
            // Joiner (User 602)
            if (await db.Accounts.FindAsync(joinerId) == null)
            {
                Role role = await db.Roles.FindAsync(1) ?? new Role { ID = 1, Name = "User" };
                if (role.ID == 0) db.Roles.Add(role);

                User userJoiner = new()
                {
                    ID = joinerId,
                    EmailAddress = $"joiner{joinerId}@test.com",
                    SRPPasswordHash = "hash",
                    SRPPasswordSalt = "salt",
                    PBKDF2PasswordHash = "hash",
                    Role = role
                };
                Account acctJoiner = new() { ID = joinerId, Name = "Joiner", IsMain = true, User = userJoiner };
                userJoiner.Accounts = [acctJoiner];
                db.Users.Add(userJoiner);
                await db.SaveChangesAsync();
            }

            // Creator (User 601) & existing Clan
            if (await db.Accounts.FindAsync(creatorId) == null)
            {
                Role? role = await db.Roles.FindAsync(1); // Should exist now

                User userCreator = new()
                {
                    ID = creatorId,
                    EmailAddress = $"creator{creatorId}@test.com",
                    SRPPasswordHash = "hash",
                    SRPPasswordSalt = "salt",
                    PBKDF2PasswordHash = "hash",
                    Role = role!
                };
                Account acctCreator = new() { ID = creatorId, Name = "Creator", IsMain = true, User = userCreator };
                userCreator.Accounts = [acctCreator];
                db.Users.Add(userCreator);

                // Create Clan
                Clan clan = new()
                {
                    ID = clanId, Name = "TestClan", Tag = "TEST",
                    TimestampCreated = TimeProvider.System.GetUtcNow().UtcDateTime
                };
                db.Clans.Add(clan);

                acctCreator.Clan = clan;
                acctCreator.ClanTier = ClanTier.Leader;

                await db.SaveChangesAsync();
            }

            pendingService.InsertPendingClanInvite(inviteKey,
                new PendingClanInvite
                {
                    ClanId = clanId,
                    InitiatorAccountId = creatorId,
                    ClanName = "TestClan",
                    ClanTag = "TEST",
                    InvitedAccountId = joinerId,
                    CreationTime = TimeProvider.System.GetUtcNow().UtcDateTime
                });
        }
        finally
        {
            ChatTestHelpers.SeedLock.Release();
        }

        // 2. Act - Joiner Connects and sends CLAN_ADD_ACCEPTED
        using TcpClient joinerClient =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, joinerId, "Joiner");
        NetworkStream stream = joinerClient.GetStream();

        ChatBuffer acceptedBuffer = new();
        acceptedBuffer.WriteCommand(ChatProtocol.Command.CHAT_CMD_CLAN_ADD_ACCEPTED); // 0x004F

        byte[] packet = [.. acceptedBuffer.Data.AsSpan(0, (int)acceptedBuffer.Size)];
        List<byte> rawBytes =
        [
            .. BitConverter.GetBytes((ushort)packet.Length),
            .. packet
        ];
        await stream.WriteAsync(rawBytes.ToArray());

        // 3. Assert - Expect CHAT_CMD_NEW_CLAN_MEMBER (0x004E) logic

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        bool foundNewMember = false;

        while (!foundNewMember)
        {
            byte[] header = new byte[4];
            int read = 0;
            while (read < 4)
            {
                int r = await stream.ReadAsync(header, read, 4 - read, cts.Token);
                if (r == 0) break;

                read += r;
            }

            if (read < 4) break;

            ushort length = BitConverter.ToUInt16(header, 0);
            ushort command = BitConverter.ToUInt16(header, 2);

            if (command == ChatProtocol.Command.CHAT_CMD_NEW_CLAN_MEMBER) // 0x004E
            {
                foundNewMember = true;

                int payloadSize = length - 2;
                byte[] payload = new byte[payloadSize];
                int pr = 0;
                while (pr < payloadSize) pr += await stream.ReadAsync(payload, pr, payloadSize - pr, cts.Token);

                ChatBuffer b = new(payload);
                int rxId = b.ReadInt32();
                int rxClanId = b.ReadInt32();
                string rxName = b.ReadString();
                string rxTag = b.ReadString();

                Assert.Equal(joinerId, rxId);
                Assert.Equal(clanId, rxClanId);
                Assert.Equal("TestClan", rxName);
                Assert.Equal("TEST", rxTag);
            }
            else
            {
                int skip = length - 2;
                if (skip > 0)
                {
                    byte[] junk = new byte[skip];
                    int jr = 0;
                    while (jr < skip) jr += await stream.ReadAsync(junk, jr, skip - jr, cts.Token);
                }
            }
        }

        Assert.True(foundNewMember);

        // Verify DB
        using (IServiceScope scope = app.Services.CreateScope())
        {
            MerrickContext db = scope.ServiceProvider.GetRequiredService<MerrickContext>();
            Account? joiner = await db.Accounts
                .Include(a => a.Clan)
                .FirstOrDefaultAsync(a => a.ID == joinerId, cts.Token);

            Assert.NotNull(joiner);
            Assert.NotNull(joiner.Clan);
            Assert.Equal(clanId, joiner.Clan!.ID);
            Assert.Equal(ClanTier.Member, joiner.ClanTier);
        }
    }

    [Fact]
    public async Task ClanRemove_Notify_Passive_NoCrash()
    {
        // 1. Arrange
        TRANSMUTANSTEINServiceProvider app = fixture.Provider;

        int userId = Interlocked.Increment(ref ChatTestHelpers.s_accountIdCounter);

        using TcpClient client =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, userId, "RemoveTestUser");
        NetworkStream stream = client.GetStream();

        // 2. Act - Send CHAT_CMD_CLAN_REMOVE_NOTIFY (0x0017)
        ChatBuffer cmd = new();
        cmd.WriteCommand(ChatProtocol.Command.CHAT_CMD_CLAN_REMOVE_NOTIFY); // 0x0017
        cmd.WriteInt32(userId); // Random payload (ID)

        byte[] packet = [.. cmd.Data.AsSpan(0, (int)cmd.Size)];
        List<byte> rawBytes =
        [
            .. BitConverter.GetBytes((ushort)packet.Length),
            .. packet
        ];
        await stream.WriteAsync(rawBytes.ToArray());

        // 3. Assert - Client stays connected (No server crash/disconnect)
        await Task.Delay(20);

        Assert.True(client.Connected);
    }

    [Fact]
    public async Task ChatClan_ClanAddRejected_Success()
    {
        // 1. Arrange
        TRANSMUTANSTEINServiceProvider app = fixture.Provider;

        int inviterId = Interlocked.Increment(ref ChatTestHelpers.s_accountIdCounter);
        int rejectorId = Interlocked.Increment(ref ChatTestHelpers.s_accountIdCounter);
        int clanId = Interlocked.Increment(ref ChatTestHelpers.s_accountIdCounter);
        const string inviteKey = "pending_invite_key_reject";

        await ChatTestHelpers.SeedLock.WaitAsync();
        try
        {
            using IServiceScope scope = app.Services.CreateScope();
            MerrickContext db = scope.ServiceProvider.GetRequiredService<MerrickContext>();
            IPendingClanService pendingService = scope.ServiceProvider.GetRequiredService<IPendingClanService>();

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
                Account a1 = new() { ID = inviterId, Name = "Inviter", IsMain = true, User = u1 };
                u1.Accounts = [a1];
                db.Users.Add(u1);

                Clan clan = new()
                {
                    ID = clanId, Name = "RejectClan", Tag = "REJ",
                    TimestampCreated = TimeProvider.System.GetUtcNow().UtcDateTime
                };
                db.Clans.Add(clan);
                a1.Clan = clan;
                a1.ClanTier = ClanTier.Leader;
                await db.SaveChangesAsync();
            }

            if (await db.Accounts.FindAsync(rejectorId) == null)
            {
                User u2 = new()
                {
                    ID = rejectorId,
                    EmailAddress = $"rejector{rejectorId}@test.com",
                    SRPPasswordHash = "h",
                    SRPPasswordSalt = "s",
                    PBKDF2PasswordHash = "h",
                    Role = role
                };
                Account a2 = new() { ID = rejectorId, Name = "Rejector", IsMain = true, User = u2 };
                u2.Accounts = [a2];
                db.Users.Add(u2);
                await db.SaveChangesAsync();
            }

            pendingService.InsertPendingClanInvite(inviteKey,
                new PendingClanInvite
                {
                    ClanId = clanId,
                    InitiatorAccountId = inviterId,
                    ClanName = "RejectClan",
                    ClanTag = "REJ",
                    InvitedAccountId = rejectorId,
                    CreationTime = TimeProvider.System.GetUtcNow().UtcDateTime
                });
        }
        finally
        {
            ChatTestHelpers.SeedLock.Release();
        }

        using TcpClient inviterClient =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, inviterId, "Inviter");
        using TcpClient rejectorClient =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, rejectorId, "Rejector");

        // 2. Act - Rejector sends CHAT_CMD_CLAN_ADD_REJECTED (0x0033)
        ChatBuffer rejectedBuffer = new();
        rejectedBuffer.WriteCommand(ChatProtocol.Command.CHAT_CMD_CLAN_ADD_REJECTED);

        byte[] packet = [.. rejectedBuffer.Data.AsSpan(0, (int)rejectedBuffer.Size)];
        List<byte> rawBytes =
        [
            .. BitConverter.GetBytes((ushort)packet.Length),
            .. packet
        ];
        await rejectorClient.GetStream().WriteAsync(rawBytes.ToArray());

        // 3. Assert - Inviter receives CHAT_CMD_CLAN_ADD_REJECTED (0x0033)
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        bool found = false;
        while (!found)
        {
            byte[] header = new byte[4];
            int read = 0;
            while (read < 4)
            {
                int r = await inviterClient.GetStream().ReadAsync(header, read, 4 - read, cts.Token);
                if (r == 0) break;

                read += r;
            }

            if (read < 4) break;

            ushort length = BitConverter.ToUInt16(header, 0);
            ushort command = BitConverter.ToUInt16(header, 2);
            byte[] payload = new byte[length - 2];
            int pr = 0;
            while (pr < payload.Length)
                pr += await inviterClient.GetStream().ReadAsync(payload, pr, payload.Length - pr, cts.Token);

            if (command == ChatProtocol.Command.CHAT_CMD_CLAN_ADD_REJECTED)
            {
                ChatBuffer resp = new(payload);
                string rejectedName = resp.ReadString();
                Assert.Equal("Rejector", rejectedName);
                found = true;
            }
        }

        Assert.True(found);
    }

    [Fact]
    public async Task ChatClan_ClanPromoteNotify_Success()
    {
        // 1. Arrange
        TRANSMUTANSTEINServiceProvider app = fixture.Provider;

        const int leaderId = 9603;
        const int memberId = 9604;
        const int clanId = 1102;

        await ChatTestHelpers.SeedLock.WaitAsync();
        try
        {
            using IServiceScope scope = app.Services.CreateScope();
            MerrickContext db = scope.ServiceProvider.GetRequiredService<MerrickContext>();
            Role role = await db.Roles.FindAsync(1) ?? new Role { ID = 1, Name = "User" };
            if (role.ID == 0) db.Roles.Add(role);

            if (await db.Accounts.FindAsync(leaderId) == null)
            {
                User u1 = new()
                {
                    ID = leaderId,
                    EmailAddress = $"leader{leaderId}@test.com",
                    SRPPasswordHash = "h",
                    SRPPasswordSalt = "s",
                    PBKDF2PasswordHash = "h",
                    Role = role
                };
                Account a1 = new() { ID = leaderId, Name = "PromoLeader", IsMain = true, User = u1 };
                u1.Accounts = [a1];
                db.Users.Add(u1);

                User u2 = new()
                {
                    ID = memberId,
                    EmailAddress = $"member{memberId}@test.com",
                    SRPPasswordHash = "h",
                    SRPPasswordSalt = "s",
                    PBKDF2PasswordHash = "h",
                    Role = role
                };
                Account a2 = new() { ID = memberId, Name = "PromoMember", IsMain = true, User = u2 };
                u2.Accounts = [a2];
                db.Users.Add(u2);

                Clan clan = new()
                {
                    ID = clanId, Name = "PromoClan", Tag = "PRM",
                    TimestampCreated = TimeProvider.System.GetUtcNow().UtcDateTime
                };
                db.Clans.Add(clan);
                a1.Clan = clan;
                a1.ClanTier = ClanTier.Leader;
                a2.Clan = clan;
                a2.ClanTier = ClanTier.Member;
                await db.SaveChangesAsync();
            }
        }
        finally
        {
            ChatTestHelpers.SeedLock.Release();
        }

        using TcpClient leaderClient =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, leaderId, "PromoLeader");
        using TcpClient memberClient =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, memberId, "PromoMember");

        await ChatTestHelpers.JoinChannelAndGetId(leaderClient, "Clan PromoClan");
        await ChatTestHelpers.JoinChannelAndGetId(memberClient, "Clan PromoClan");

        // 2. Act - Leader sends CHAT_CMD_CLAN_PROMOTE_NOTIFY
        ChatBuffer cmd = new();
        cmd.WriteCommand(ChatProtocol.Command.CHAT_CMD_CLAN_PROMOTE_NOTIFY);
        cmd.WriteInt32(memberId);

        byte[] packet = [.. cmd.Data.AsSpan(0, (int)cmd.Size)];
        List<byte> rawBytes =
        [
            .. BitConverter.GetBytes((ushort)packet.Length),
            .. packet
        ];
        await leaderClient.GetStream().WriteAsync(rawBytes.ToArray());

        // 3. Assert - Member receives CHAT_CMD_CLAN_RANK_CHANGE
        ChatBuffer resp = await ChatTestHelpers.ExpectCommandAsync(memberClient.GetStream(),
            ChatProtocol.Command.CHAT_CMD_CLAN_RANK_CHANGE, 5);
        int targetId = resp.ReadInt32();
        byte rank = resp.ReadInt8();
        int promoterId = resp.ReadInt32();

        Assert.Equal(memberId, targetId);
        Assert.Equal((byte)ClanTier.Officer, rank);
        Assert.Equal(leaderId, promoterId);
    }

    [Fact]
    public async Task ChatClan_ClanDemoteNotify_Success()
    {
        // 1. Arrange
        TRANSMUTANSTEINServiceProvider app = fixture.Provider;

        const int leaderId = 9605;
        const int officerId = 9606;
        const int clanId = 1103;

        await ChatTestHelpers.SeedLock.WaitAsync();
        try
        {
            using IServiceScope scope = app.Services.CreateScope();
            MerrickContext db = scope.ServiceProvider.GetRequiredService<MerrickContext>();
            Role role = await db.Roles.FindAsync(1) ?? new Role { ID = 1, Name = "User" };
            if (role.ID == 0) db.Roles.Add(role);

            if (await db.Accounts.FindAsync(leaderId) == null)
            {
                User u1 = new()
                {
                    ID = leaderId,
                    EmailAddress = $"leader{leaderId}@test.com",
                    SRPPasswordHash = "h",
                    SRPPasswordSalt = "s",
                    PBKDF2PasswordHash = "h",
                    Role = role
                };
                Account a1 = new() { ID = leaderId, Name = "DemoLeader", IsMain = true, User = u1 };
                u1.Accounts = [a1];
                db.Users.Add(u1);

                User u2 = new()
                {
                    ID = officerId,
                    EmailAddress = $"officer{officerId}@test.com",
                    SRPPasswordHash = "h",
                    SRPPasswordSalt = "s",
                    PBKDF2PasswordHash = "h",
                    Role = role
                };
                Account a2 = new() { ID = officerId, Name = "DemoOfficer", IsMain = true, User = u2 };
                u2.Accounts = [a2];
                db.Users.Add(u2);

                Clan clan = new()
                {
                    ID = clanId, Name = "DemoClan", Tag = "DMO",
                    TimestampCreated = TimeProvider.System.GetUtcNow().UtcDateTime
                };
                db.Clans.Add(clan);
                a1.Clan = clan;
                a1.ClanTier = ClanTier.Leader;
                a2.Clan = clan;
                a2.ClanTier = ClanTier.Officer;
                await db.SaveChangesAsync();
            }
        }
        finally
        {
            ChatTestHelpers.SeedLock.Release();
        }

        using TcpClient leaderClient =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, leaderId, "DemoLeader");
        using TcpClient officerClient =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, officerId, "DemoOfficer");

        await ChatTestHelpers.JoinChannelAndGetId(leaderClient, "Clan DemoClan");
        await ChatTestHelpers.JoinChannelAndGetId(officerClient, "Clan DemoClan");

        // 2. Act - Leader sends CHAT_CMD_CLAN_DEMOTE_NOTIFY
        ChatBuffer cmd = new();
        cmd.WriteCommand(ChatProtocol.Command.CHAT_CMD_CLAN_DEMOTE_NOTIFY);
        cmd.WriteInt32(officerId);

        byte[] packet = [.. cmd.Data.AsSpan(0, (int)cmd.Size)];
        List<byte> rawBytes =
        [
            .. BitConverter.GetBytes((ushort)packet.Length),
            .. packet
        ];
        await leaderClient.GetStream().WriteAsync(rawBytes.ToArray());

        // 3. Assert - Officer receives CHAT_CMD_CLAN_RANK_CHANGE
        ChatBuffer resp = await ChatTestHelpers.ExpectCommandAsync(officerClient.GetStream(),
            ChatProtocol.Command.CHAT_CMD_CLAN_RANK_CHANGE, 5);
        int targetId = resp.ReadInt32();
        byte rank = resp.ReadInt8();
        int promoterId = resp.ReadInt32();

        Assert.Equal(officerId, targetId);
        Assert.Equal((byte)ClanTier.Member, rank);
        Assert.Equal(leaderId, promoterId);
    }
}
