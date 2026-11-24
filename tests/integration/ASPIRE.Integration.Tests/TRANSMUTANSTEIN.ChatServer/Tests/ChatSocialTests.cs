// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ReSharper disable All

using System.Net.Sockets;
using ASPIRE.Tests.TRANSMUTANSTEIN.ChatServer.Infrastructure;
using KONGOR.MasterServer.Extensions.Cache;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Relational;
using MERRICK.DatabaseContext.Enumerations;
using TRANSMUTANSTEIN.ChatServer.Domain.Core;
using TRANSMUTANSTEIN.ChatServer.Internals;

namespace ASPIRE.Tests.TRANSMUTANSTEIN.ChatServer.Tests;

[Collection("TRANSMUTANSTEIN ChatServer Collection")]
[Trait("Category", "Manual")]
[Trait("Category", "Integration")]
public sealed class ChatSocialTests(TRANSMUTANSTEINTestFixture fixture)
{
    [Fact]
    [Trait("Category", "Manual")]
    [Trait("Category", "Integration")]
    public async Task ChatSocial_AddFriend_RequestSent()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        // Arrange
        TRANSMUTANSTEINServiceProvider app = fixture.Provider;
        int targetId = Interlocked.Increment(ref ChatTestHelpers.s_accountIdCounter);
        int requesterId = Interlocked.Increment(ref ChatTestHelpers.s_accountIdCounter);
        string targetName = $"Tgt_{targetId}";
        string requesterName = $"Req_{requesterId}";

        using TcpClient target =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, targetId, targetName);

        using TcpClient requester =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, requesterId, requesterName);
        NetworkStream stream = requester.GetStream();

        // Act - Send Add Friend Request
        ChatBuffer addBuffer = new();
        addBuffer.WriteCommand(ChatProtocol.Command.CHAT_CMD_REQUEST_BUDDY_ADD); // 0x000D
        addBuffer.WriteString(targetName);

        byte[] packet = [.. addBuffer.Data.AsSpan(0, (int)addBuffer.Size)];
        List<byte> rawBytes =
        [
            .. BitConverter.GetBytes((ushort)packet.Length),
            .. packet
        ];
        await stream.WriteAsync(rawBytes.ToArray());

        // Assert - Expect CHAT_CMD_REQUEST_BUDDY_ADD_RESPONSE (0x00B2) with Success Status for Requester
        ChatBuffer responseBuffer =
            await ChatTestHelpers.ExpectCommandAsync(stream, ChatProtocol.Command.CHAT_CMD_REQUEST_BUDDY_ADD_RESPONSE);
        byte status = responseBuffer.ReadInt8();
        int notifId = responseBuffer.ReadInt32();
        string friendName = responseBuffer.ReadString();
        int friendAccountId = responseBuffer.ReadInt32();
        byte friendStatus = responseBuffer.ReadInt8();
        byte friendFlags = responseBuffer.ReadInt8();
        int clanId = responseBuffer.ReadInt32();
        string clanName = responseBuffer.ReadString();
        string chatSymbol = responseBuffer.ReadString();
        string chatNameColour = responseBuffer.ReadString();
        string accountIcon = responseBuffer.ReadString();
        int ascensionLevel = responseBuffer.ReadInt32();

        Assert.Equal((byte)ChatProtocol.FriendAddStatus.SUCCESS_REQUESTER, status); // 1
        Assert.Equal(targetName, friendName);
        Assert.Equal(targetId, friendAccountId);
        Assert.NotEqual(0, notifId);

        // Also assert Target received SUCCESS_REQUESTEE (0x02) notification with correct fields
        NetworkStream targetStream = target.GetStream();
        ChatBuffer targetResponseBuffer =
            await ChatTestHelpers.ExpectCommandAsync(targetStream,
                ChatProtocol.Command.CHAT_CMD_REQUEST_BUDDY_ADD_RESPONSE);
        byte targetStatus = targetResponseBuffer.ReadInt8();
        int targetNotifId = targetResponseBuffer.ReadInt32();
        string targetRequesterName = targetResponseBuffer.ReadString();

        Assert.Equal((byte)ChatProtocol.FriendAddStatus.SUCCESS_REQUESTEE, targetStatus); // 2
        Assert.Equal(requesterName, targetRequesterName);
        Assert.NotEqual(0, targetNotifId);
    }

    [Fact]
    [Trait("Category", "Manual")]
    [Trait("Category", "Integration")]
    public async Task MatchState_BroadcastsStatusUpdates_ToFriends()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        // Arrange
        TRANSMUTANSTEINServiceProvider app = fixture.Provider;
        const int accountIdPlayer = 601;
        const int accountIdObserver = 602;

        // Seed Friendship manually
        await ChatTestHelpers.SeedLock.WaitAsync();
        try
        {
            using IServiceScope scope = app.Services.CreateScope();
            MerrickContext db = scope.ServiceProvider.GetRequiredService<MerrickContext>();
            db.ChangeTracker.Clear();

            Role? role = await db.Roles.FindAsync(1);
            if (role == null)
            {
                role = new Role { ID = 1, Name = "User" };
                db.Roles.Add(role);
                await db.SaveChangesAsync();
            }

            Account? playerAcc = await db.Accounts.FindAsync(accountIdPlayer);
            if (playerAcc == null)
            {
                User user = new()
                {
                    ID = accountIdPlayer,
                    EmailAddress = "player601@example.com",
                    SRPPasswordHash = "hash",
                    SRPPasswordSalt = "salt",
                    PBKDF2PasswordHash = "hash",
                    Role = role!
                };
                playerAcc = new Account
                {
                    ID = accountIdPlayer,
                    User = user,
                    Name = "Player601",
                    IsMain = true,
                    Type = AccountType.Normal,
                    AscensionLevel = 10
                };
                user.Accounts = [playerAcc];
                db.Users.Add(user);
                db.Accounts.Add(playerAcc);
                await db.SaveChangesAsync();
            }

            Account? observerAcc = await db.Accounts.FindAsync(accountIdObserver);
            if (observerAcc == null)
            {
                User user = new()
                {
                    ID = accountIdObserver,
                    EmailAddress = "observer602@example.com",
                    SRPPasswordHash = "hash",
                    SRPPasswordSalt = "salt",
                    PBKDF2PasswordHash = "hash",
                    Role = role!
                };
                observerAcc = new Account
                {
                    ID = accountIdObserver,
                    User = user,
                    Name = "Observer602",
                    IsMain = true,
                    Type = AccountType.Normal,
                    AscensionLevel = 5
                };
                user.Accounts = [observerAcc];
                db.Users.Add(user);
                db.Accounts.Add(observerAcc);
                await db.SaveChangesAsync();
            }

            // Observer has Player as friend
            bool alreadyFriended = await db.Friendships.AnyAsync(f => f.AccountID == accountIdObserver && f.FriendAccountID == accountIdPlayer);
            if (!alreadyFriended)
            {
                db.Friendships.Add(new Friendship
                {
                    AccountID = accountIdObserver,
                    FriendAccountID = accountIdPlayer,
                    FriendGroup = "DEFAULT",
                    Status = FriendshipStatus.Accepted
                });
                await db.SaveChangesAsync();
            }
        }
        finally
        {
            ChatTestHelpers.SeedLock.Release();
        }

        using TcpClient observer =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, accountIdObserver, "Observer602");
        NetworkStream observerStream = observer.GetStream();

        using TcpClient player =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, accountIdPlayer, "Player601");
        NetworkStream playerStream = player.GetStream();

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(15));

        // Consume initial connection status broadcast on observer stream
        byte[] initHeader = new byte[4];
        await observerStream.ReadExactlyAsync(initHeader, cts.Token);
        ushort initLen = BitConverter.ToUInt16(initHeader, 0);
        byte[] initPayload = new byte[initLen - 2];
        await observerStream.ReadExactlyAsync(initPayload, cts.Token);

        // Act 1: Player sends CHAT_CMD_JOINING_GAME (0x000F)
        ChatBuffer joiningBuffer = new();
        joiningBuffer.WriteCommand(ChatProtocol.Command.CHAT_CMD_JOINING_GAME);
        joiningBuffer.WriteString("127.0.0.1:11235");

        byte[] joinPkt = [.. joiningBuffer.Data.AsSpan(0, (int)joiningBuffer.Size)];
        List<byte> rawJoinBytes = [.. BitConverter.GetBytes((ushort)joinPkt.Length), .. joinPkt];
        await playerStream.WriteAsync(rawJoinBytes.ToArray());

        // Assert 1: Observer receives CHAT_CMD_UPDATE_STATUS with JoiningGame
        byte[] status1Header = new byte[4];
        await observerStream.ReadExactlyAsync(status1Header, cts.Token);
        ushort status1Len = BitConverter.ToUInt16(status1Header, 0);
        ushort status1Cmd = BitConverter.ToUInt16(status1Header, 2);

        Assert.Equal(ChatProtocol.Command.CHAT_CMD_UPDATE_STATUS, status1Cmd); // 0x00B6
        byte[] status1Payload = new byte[status1Len - 2];
        await observerStream.ReadExactlyAsync(status1Payload, cts.Token);

        ChatBuffer buf1 = new(status1Payload);
        int accId1 = buf1.ReadInt32();
        byte clientStatus1 = buf1.ReadInt8();
        byte flags1 = buf1.ReadInt8();
        int clanId1 = buf1.ReadInt32();
        string clanName1 = buf1.ReadString();
        string sym1 = buf1.ReadString();
        string col1 = buf1.ReadString();
        string ico1 = buf1.ReadString();
        string srvAddr1 = buf1.ReadString();
        int asc1 = buf1.ReadInt32();

        Assert.Equal(accountIdPlayer, accId1);
        Assert.Equal((byte)ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_JOINING_GAME, clientStatus1);
        Assert.Equal("127.0.0.1:11235", srvAddr1);

        // Act 2: Player sends CHAT_CMD_JOINED_GAME (0x0010)
        ChatBuffer joinedBuffer = new();
        joinedBuffer.WriteCommand(ChatProtocol.Command.CHAT_CMD_JOINED_GAME);
        joinedBuffer.WriteString("Casual AP");
        joinedBuffer.WriteInt32(778899);
        joinedBuffer.WriteInt8(1); // joinMatchChannel

        byte[] joinedPkt = [.. joinedBuffer.Data.AsSpan(0, (int)joinedBuffer.Size)];
        List<byte> rawJoinedBytes = [.. BitConverter.GetBytes((ushort)joinedPkt.Length), .. joinedPkt];
        await playerStream.WriteAsync(rawJoinedBytes.ToArray());

        // Assert 2: Observer receives CHAT_CMD_UPDATE_STATUS with InGame + match details
        byte[] status2Header = new byte[4];
        await observerStream.ReadExactlyAsync(status2Header, cts.Token);
        ushort status2Len = BitConverter.ToUInt16(status2Header, 0);
        ushort status2Cmd = BitConverter.ToUInt16(status2Header, 2);

        Assert.Equal(ChatProtocol.Command.CHAT_CMD_UPDATE_STATUS, status2Cmd);
        byte[] status2Payload = new byte[status2Len - 2];
        await observerStream.ReadExactlyAsync(status2Payload, cts.Token);

        ChatBuffer buf2 = new(status2Payload);
        int accId2 = buf2.ReadInt32();
        byte clientStatus2 = buf2.ReadInt8();
        byte flags2 = buf2.ReadInt8();
        int clanId2 = buf2.ReadInt32();
        string clanName2 = buf2.ReadString();
        string sym2 = buf2.ReadString();
        string col2 = buf2.ReadString();
        string ico2 = buf2.ReadString();
        string srvAddr2 = buf2.ReadString();
        string gameName2 = buf2.ReadString();
        int matchId2 = buf2.ReadInt32();
        byte moreData2 = buf2.ReadInt8();
        int asc2 = buf2.ReadInt32();

        Assert.Equal(accountIdPlayer, accId2);
        Assert.Equal((byte)ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME, clientStatus2);
        Assert.Equal("127.0.0.1:11235", srvAddr2);
        Assert.Equal("Casual AP", gameName2);
        Assert.Equal(778899, matchId2);
        Assert.Equal(0, moreData2);

        // Act 3: Player sends CHAT_CMD_LEFT_GAME (0x0012)
        ChatBuffer leftBuffer = new();
        leftBuffer.WriteCommand(ChatProtocol.Command.CHAT_CMD_LEFT_GAME);

        byte[] leftPkt = [.. leftBuffer.Data.AsSpan(0, (int)leftBuffer.Size)];
        List<byte> rawLeftBytes = [.. BitConverter.GetBytes((ushort)leftPkt.Length), .. leftPkt];
        await playerStream.WriteAsync(rawLeftBytes.ToArray());

        // Assert 3: Observer receives CHAT_CMD_UPDATE_STATUS with Connected (3)
        byte[] status3Header = new byte[4];
        await observerStream.ReadExactlyAsync(status3Header, cts.Token);
        ushort status3Len = BitConverter.ToUInt16(status3Header, 0);
        ushort status3Cmd = BitConverter.ToUInt16(status3Header, 2);

        Assert.Equal(ChatProtocol.Command.CHAT_CMD_UPDATE_STATUS, status3Cmd);
        byte[] status3Payload = new byte[status3Len - 2];
        await observerStream.ReadExactlyAsync(status3Payload, cts.Token);

        ChatBuffer buf3 = new(status3Payload);
        int accId3 = buf3.ReadInt32();
        byte clientStatus3 = buf3.ReadInt8();

        Assert.Equal(accountIdPlayer, accId3);
        Assert.Equal((byte)ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_CONNECTED, clientStatus3);
    }

    [Fact]
    [Trait("Category", "Manual")]
    [Trait("Category", "Integration")]
    public async Task ChatSocial_BuddyList_InitialStatus()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        // Arrange
        // 1. Start Application using Dynamic Port
        TRANSMUTANSTEINServiceProvider app = fixture.Provider;

        // Create Users
        const int accountIdMain = 501;
        const int accountIdFriend = 502;

        // Seed Friendship manually
        await ChatTestHelpers.SeedLock.WaitAsync();
        try
        {
            using IServiceScope scope = app.Services.CreateScope();
            MerrickContext db = scope.ServiceProvider.GetRequiredService<MerrickContext>();
            IDatabase cache = scope.ServiceProvider.GetRequiredService<IDatabase>();

            db.ChangeTracker.Clear();

            // Ensure Role
            Role? role = await db.Roles.FindAsync(1);
            if (role == null)
            {
                role = new Role { ID = 1, Name = "User" };
                db.Roles.Add(role);
                await db.SaveChangesAsync();
            }

            await db.Roles.FindAsync(1);


            // Ensure Friend User (502)
            Account? existingFriend = await db.Accounts.FindAsync(accountIdFriend);
            if (existingFriend == null)
            {
                // Re-fetch role
                role = await db.Roles.FindAsync(1);

                User userFriend = new()
                {
                    ID = accountIdFriend,
                    EmailAddress = $"friend{accountIdFriend}@test.com",
                    SRPPasswordHash = "hash",
                    SRPPasswordSalt = "salt",
                    PBKDF2PasswordHash = "hash",
                    Role = role!
                };
                Account friendAcct = new()
                {
                    ID = accountIdFriend,
                    Name = "FriendGuy",
                    IsMain = true,
                    User = userFriend
                };
                userFriend.Accounts = [friendAcct];

                db.Users.Add(userFriend);
                await db.SaveChangesAsync();
            }

            // Ensure Main User (501) with Friendship
            if (await db.Accounts.FindAsync(accountIdMain) == null)
            {
                role = await db.Roles.FindAsync(1);

                User userMain = new()
                {
                    ID = accountIdMain,
                    EmailAddress = $"main{accountIdMain}@test.com",
                    SRPPasswordHash = "hash",
                    SRPPasswordSalt = "salt",
                    PBKDF2PasswordHash = "hash",
                    Role = role!
                };

                Account mainAcct = new() { ID = accountIdMain, Name = "MainGuy", IsMain = true, User = userMain };
                userMain.Accounts = [mainAcct];

                db.Users.Add(userMain);
                db.Friendships.Add(new Friendship
                {
                    AccountID = accountIdMain,
                    FriendAccountID = accountIdFriend,
                    FriendGroup = "DEFAULT",
                    Status = FriendshipStatus.Accepted
                });
                await db.SaveChangesAsync();
            }

            // Set Session Cookie for MainGuy (so helper can log in)
            await cache.SetAccountNameForSessionCookie($"cookie_{accountIdMain}", "MainGuy");
            await cache.SetAccountNameForSessionCookie($"cookie_{accountIdFriend}", "FriendGuy");
        }
        finally
        {
            ChatTestHelpers.SeedLock.Release();
        }

        // Act
        // 1. Connect Friend (502) so they are "Online"
        using TcpClient friendClient =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, accountIdFriend, "FriendGuy");

        // 2. Connect MainGuy (501) manually to inspect handshake
        TcpClient client = new();
        await client.ConnectAsync("127.0.0.1", app.ClientPort);
        NetworkStream stream = client.GetStream();

        const string ip = "127.0.0.1";
        string cookie = $"cookie_{accountIdMain}";
        string hash = SRPAuthenticationHandlers.ComputeChatServerCookieHash(accountIdMain, ip, cookie);

        // Send Login Packet (0x0C00)
        ChatBuffer loginBuffer = new();
        loginBuffer.WriteCommand(ChatProtocol.ClientToChatServer.NET_CHAT_CL_CONNECT);
        loginBuffer.WriteInt32(accountIdMain);
        loginBuffer.WriteString(cookie);
        loginBuffer.WriteString(ip);
        loginBuffer.WriteString(hash);
        loginBuffer.WriteInt32(1); // Protocol Version
        loginBuffer.WriteInt8(1); // OS ID
        loginBuffer.WriteInt8(1); // OS Major
        loginBuffer.WriteInt8(0); // OS Minor
        loginBuffer.WriteInt8(0); // OS Patch
        loginBuffer.WriteString("build");
        loginBuffer.WriteString("arch");
        loginBuffer.WriteInt8(4); // Major
        loginBuffer.WriteInt8(10); // Minor
        loginBuffer.WriteInt8(1); // Patch
        loginBuffer.WriteInt8(0); // Revision
        loginBuffer.WriteInt8((byte)ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_CONNECTED);
        loginBuffer.WriteInt8((byte)ChatProtocol.ChatModeType.CHAT_MODE_AVAILABLE);
        loginBuffer.WriteString("USE");
        loginBuffer.WriteString("en");

        byte[] loginPacket = [.. loginBuffer.Data.AsSpan(0, (int)loginBuffer.Size)];
        ushort packetLength = (ushort)loginPacket.Length;
        List<byte> rawBytes =
        [
            .. BitConverter.GetBytes(packetLength),
            .. loginPacket
        ];
        await stream.WriteAsync(rawBytes.ToArray());

        // Read until we find INITIAL_STATUS (0x000B)
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(15));
        bool foundBuddyList = false;

        while (!foundBuddyList)
        {
            byte[] header = new byte[4];
            await stream.ReadExactlyAsync(header, 0, 4, cts.Token);

            ushort length = BitConverter.ToUInt16(header, 0);
            ushort command = BitConverter.ToUInt16(header, 2);

            if (command == ChatProtocol.Command.CHAT_CMD_INITIAL_STATUS) // 0x000B
            {
                foundBuddyList = true;

                // Parse Payload
                // Payload = Length - 2
                int pSize = length - 2;
                byte[] p = new byte[pSize];
                int pr = 0;
                while (pr < pSize)
                {
                    pr += await stream.ReadAsync(p, pr, pSize - pr, cts.Token);
                }

                ChatBuffer b = new(p);

                // Structure: [Count: Int32] [List...]
                int buddyCount = b.ReadInt32();

                Assert.True(buddyCount > 0);

                // Verify first buddy is "FriendGuy" (AccountID 502)
                // Loop: ID(4), Status(1), Flags(1), NameColor(String), Icon(String), [If InGame: Server(String), MatchID(4)], Level(4)

                int fId = b.ReadInt32();
                // Status (1 byte)
                b.ReadInt8();
                // Flags (1 byte)
                b.ReadInt8();
                // NameColor (String)
                b.ReadString(); // Name Color
                // Icon (String)
                b.ReadString(); // Icon

                // Check if we need to read extended info?
                // Current server implementation:
                // WriteInt32(ID)
                // WriteInt8(Status)
                // WriteInt8(Flags)
                // WriteString(NameColor)
                // WriteString(Icon)
                // If InGame...
                // WriteInt32(Level)

                Assert.Equal(502, fId);
            }
            else
            {
                // Consume unknown packet payload
                int skip = length - 2;
                if (skip > 0)
                {
                    byte[] junk = new byte[skip];
                    int jr = 0;
                    while (jr < skip)
                    {
                        jr += await stream.ReadAsync(junk, jr, skip - jr, cts.Token);
                    }
                }
            }
        }

        Assert.True(foundBuddyList);
    }

    [Fact]
    [Trait("Category", "Manual")]
    [Trait("Category", "Integration")]
    public async Task AddFriend_TargetNotFound_ReturnsGenericFailure()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        // Arrange
        TRANSMUTANSTEINServiceProvider app = fixture.Provider;

        using TcpClient requester =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, 410, "RequesterUser410");
        NetworkStream stream = requester.GetStream();

        // Act - Send Add Friend Request to non-existent account
        ChatBuffer addBuffer = new();
        addBuffer.WriteCommand(ChatProtocol.Command.CHAT_CMD_REQUEST_BUDDY_ADD);
        addBuffer.WriteString("NonExistentTargetUser_9999");

        byte[] packet = [.. addBuffer.Data.AsSpan(0, (int)addBuffer.Size)];
        List<byte> rawBytes =
        [
            .. BitConverter.GetBytes((ushort)packet.Length),
            .. packet
        ];
        await stream.WriteAsync(rawBytes.ToArray());

        // Assert - Expect CHAT_CMD_REQUEST_BUDDY_ADD_RESPONSE with GENERIC_FAILURE (status 0)
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));

        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, cts.Token);
        ushort length = BitConverter.ToUInt16(header, 0);
        ushort command = BitConverter.ToUInt16(header, 2);

        Assert.Equal(ChatProtocol.Command.CHAT_CMD_REQUEST_BUDDY_ADD_RESPONSE, command);

        int payloadSize = length - 2;
        byte[] payload = new byte[payloadSize];
        int read = 0;
        while (read < payloadSize)
        {
            read += await stream.ReadAsync(payload, read, payloadSize - read, cts.Token);
        }

        ChatBuffer responseBuffer = new(payload);
        byte status = responseBuffer.ReadInt8();

        Assert.Equal((byte)ChatProtocol.FriendAddStatus.GENERIC_FAILURE, status);
    }

    [Fact]
    [Trait("Category", "Manual")]
    [Trait("Category", "Integration")]
    public async Task ApproveFriend_WhenPendingRequestNotInCache_CreatesFriendshipSuccessfully()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        // Arrange
        TRANSMUTANSTEINServiceProvider app = fixture.Provider;

        using TcpClient targetUser =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, 412, "TargetUser412");

        using TcpClient approver =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, 411, "ApproverUser411");
        NetworkStream stream = approver.GetStream();

        // Act - Send Approve Friend Request without a pending request in cache
        ChatBuffer approveBuffer = new();
        approveBuffer.WriteCommand(ChatProtocol.Command.CHAT_CMD_REQUEST_BUDDY_APPROVE);
        approveBuffer.WriteString("TargetUser412");

        byte[] packet = [.. approveBuffer.Data.AsSpan(0, (int)approveBuffer.Size)];
        List<byte> rawBytes =
        [
            .. BitConverter.GetBytes((ushort)packet.Length),
            .. packet
        ];
        await stream.WriteAsync(rawBytes.ToArray());

        // Assert - Expect CHAT_CMD_REQUEST_BUDDY_APPROVE_RESPONSE with SUCCESS_APPROVER
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));

        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, cts.Token);
        ushort length = BitConverter.ToUInt16(header, 0);
        ushort command = BitConverter.ToUInt16(header, 2);

        Assert.Equal(ChatProtocol.Command.CHAT_CMD_REQUEST_BUDDY_APPROVE_RESPONSE, command);

        int payloadSize = length - 2;
        byte[] payload = new byte[payloadSize];
        int read = 0;
        while (read < payloadSize)
        {
            read += await stream.ReadAsync(payload, read, payloadSize - read, cts.Token);
        }

        ChatBuffer responseBuffer = new(payload);
        byte status = responseBuffer.ReadInt8();

        Assert.Equal((byte)ChatProtocol.FriendApproveStatus.SUCCESS_APPROVER, status);
    }

    [Fact]
    [Trait("Category", "Manual")]
    [Trait("Category", "Integration")]
    public async Task PlayerSpectate_ReturnsAllowAndServerAddress_WhenTargetIsInGame()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        // Arrange
        TRANSMUTANSTEINServiceProvider app = fixture.Provider;
        IChatContext chatContext = app.Services.GetRequiredService<IChatContext>();

        using TcpClient target =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, 951, "SpectateePlayer");

        using TcpClient requester =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, 952, "SpectatorPlayer");
        NetworkStream stream = requester.GetStream();

        // Mark target session as in-game with server address
        ChatSession? targetSession = null;
        for (int wait = 0; wait < 20 && targetSession == null; wait++)
        {
            targetSession = chatContext.ClientChatSessions.Values.FirstOrDefault(s => s.Account?.ID == 951);
            if (targetSession == null)
            {
                await Task.Delay(50);
            }
        }

        Assert.NotNull(targetSession);
        targetSession.ClientMetadata.LastKnownClientState = ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME;
        targetSession.ClientMetadata.MatchServerAddress = "127.0.0.1:11235";

        // Act - Send Spectate Request
        ChatBuffer spectateBuffer = new();
        spectateBuffer.WriteCommand(ChatProtocol.Command.CHAT_CMD_PLAYER_SPECTATE_REQUEST); // 0x00B8
        spectateBuffer.WriteInt8((byte)ChatProtocol.PlayerSpectateRequest.PLAYER_SPECTATE_REQUEST);
        spectateBuffer.WriteString("SpectateePlayer");
        spectateBuffer.WriteInt8(0); // mentor flag
        spectateBuffer.WriteString(string.Empty); // server info

        byte[] packet = [.. spectateBuffer.Data.AsSpan(0, (int)spectateBuffer.Size)];
        List<byte> rawBytes =
        [
            .. BitConverter.GetBytes((ushort)packet.Length),
            .. packet
        ];
        await stream.WriteAsync(rawBytes.ToArray());

        // Assert - Expect CHAT_CMD_PLAYER_SPECTATE_REQUEST (0x00B8) response with PSRR_ALLOW
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));

        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, cts.Token);
        ushort length = BitConverter.ToUInt16(header, 0);
        ushort command = BitConverter.ToUInt16(header, 2);

        Assert.Equal(ChatProtocol.Command.CHAT_CMD_PLAYER_SPECTATE_REQUEST, command);

        int payloadSize = length - 2;
        byte[] payload = new byte[payloadSize];
        await stream.ReadExactlyAsync(payload, cts.Token);

        ChatBuffer responseBuffer = new(payload);
        byte subType = responseBuffer.ReadInt8();
        byte status = responseBuffer.ReadInt8();
        string name = responseBuffer.ReadString();
        string server = responseBuffer.ReadString();

        Assert.Equal((byte)ChatProtocol.PlayerSpectateRequest.PLAYER_SPECTATE_REQUEST_RESPONSE, subType);
        Assert.Equal((byte)ChatProtocol.PlayerSpectateRequestResponse.PSRR_ALLOW, status);
        Assert.Equal("SpectateePlayer", name);
        Assert.Equal("127.0.0.1:11235", server);
    }
}
