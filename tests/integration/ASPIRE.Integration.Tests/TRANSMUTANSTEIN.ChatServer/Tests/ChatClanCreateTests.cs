// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net.Sockets;
using ASPIRE.Tests.TRANSMUTANSTEIN.ChatServer.Infrastructure;
using TRANSMUTANSTEIN.ChatServer.Domain.Core;

namespace ASPIRE.Tests.TRANSMUTANSTEIN.ChatServer.Tests;

[Collection("TRANSMUTANSTEIN ChatServer Collection")]
public sealed class ChatClanCreateTests(TRANSMUTANSTEINTestFixture fixture)
{
    [Fact]
    public async Task ClanCreate_QueriesMembers_AndFailsOnOffline()
    {
        // 1. Arrange
        TRANSMUTANSTEINServiceProvider app = fixture.Provider;

        string s = Guid.NewGuid().ToString("N")[..4];
        int creatorId = Random.Shared.Next(700000, 799999);
        string creatorName = $"Creator_{s}";
        List<string> memberNames = [$"Mem1_{s}", $"Mem2_{s}", $"Mem3_{s}", $"Mem4_{s}"];
        List<int> memberIds = [creatorId + 1, creatorId + 2, creatorId + 3, creatorId + 4];

        // Seed DB
        await ChatTestHelpers.SeedLock.WaitAsync();
        try
        {
            using IServiceScope scope = app.Services.CreateScope();
            MerrickContext db = scope.ServiceProvider.GetRequiredService<MerrickContext>();

            // Ensure Role
            Role role = await db.Roles.FindAsync(1) ?? new Role { ID = 1, Name = "User" };
            if (role.ID == 0) db.Roles.Add(role);

            // Seed Creator
            if (await db.Accounts.FindAsync(creatorId) == null)
            {
                User user = new()
                {
                    ID = creatorId,
                    EmailAddress = $"creator{creatorId}@test.com",
                    SRPPasswordHash = "hash",
                    SRPPasswordSalt = "salt",
                    PBKDF2PasswordHash = "hash",
                    Role = role
                };
                Account acct = new() { ID = creatorId, Name = creatorName, IsMain = true, User = user };
                user.Accounts = [acct];
                db.Users.Add(user);
            }

            // Seed Members
            for (int i = 0; i < 4; i++)
            {
                int mid = memberIds[i];
                string mname = memberNames[i];
                if (await db.Accounts.FindAsync(mid) == null)
                {
                    User user = new()
                    {
                        ID = mid,
                        EmailAddress = $"mem{mid}@test.com",
                        SRPPasswordHash = "hash",
                        SRPPasswordSalt = "salt",
                        PBKDF2PasswordHash = "hash",
                        Role = role
                    };
                    Account acct = new() { ID = mid, Name = mname, IsMain = true, User = user };
                    user.Accounts = [acct];
                    db.Users.Add(user);
                }
            }

            await db.SaveChangesAsync();
        }
        finally
        {
            ChatTestHelpers.SeedLock.Release();
        }

        // 2. Act
        using TcpClient client =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, creatorId, creatorName);
        NetworkStream stream = client.GetStream();

        string testId = Guid.NewGuid().ToString("N")[..4];
        string clanName = $"NewClan{testId}";
        string clanTag = $"N{testId[..3]}";

        ChatBuffer createCmd = new();
        createCmd.WriteCommand(ChatProtocol.Command.CHAT_CMD_CLAN_CREATE_REQUEST); // 0x0051
        createCmd.WriteString(clanName);
        createCmd.WriteString(clanTag);
        foreach (string m in memberNames) createCmd.WriteString(m);

        await ChatTestHelpers.SendPacketAsync(stream, createCmd);

        // 3. Assert
        // Expect CHAT_CMD_CLAN_CREATE_FAIL_INVITE (0x0056) because members are offline
        // This confirms MembersSanityCheck passed (it found them in DB).

        ChatBuffer response =
            await ChatTestHelpers.ExpectCommandAsync(stream, ChatProtocol.Command.CHAT_CMD_CLAN_CREATE_FAIL_INVITE, 5);
        string failedMember = response.ReadString();
        Assert.Contains(failedMember, memberNames);
    }

    [Fact]
    public async Task ClanCreate_WhenExecuted_RejectsInvalidNameAndTag()
    {
        // 1. Arrange
        TRANSMUTANSTEINServiceProvider app = fixture.Provider;

        const int creatorId = 2000;
        const string creatorName = "Creator2";

        // Seed DB
        await ChatTestHelpers.SeedLock.WaitAsync();
        try
        {
            using IServiceScope scope = app.Services.CreateScope();
            MerrickContext db = scope.ServiceProvider.GetRequiredService<MerrickContext>();

            // Ensure Role
            Role role = await db.Roles.FindAsync(1) ?? new Role { ID = 1, Name = "User" };
            if (role.ID == 0) db.Roles.Add(role);

            // Seed Creator
            if (await db.Accounts.FindAsync(creatorId) == null)
            {
                User user = new()
                {
                    ID = creatorId,
                    EmailAddress = $"creator{creatorId}@test.com",
                    SRPPasswordHash = "hash",
                    SRPPasswordSalt = "salt",
                    PBKDF2PasswordHash = "hash",
                    Role = role
                };
                Account acct = new() { ID = creatorId, Name = creatorName, IsMain = true, User = user };
                user.Accounts = [acct];
                db.Users.Add(user);
                await db.SaveChangesAsync();
            }
        }
        finally
        {
            ChatTestHelpers.SeedLock.Release();
        }

        // 2. Act & Assert
        using TcpClient client =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, creatorId, creatorName);
        NetworkStream stream = client.GetStream();

        // Test Invalid Name
        ChatBuffer invalidNameCmd = new();
        invalidNameCmd.WriteCommand(ChatProtocol.Command.CHAT_CMD_CLAN_CREATE_REQUEST);
        invalidNameCmd.WriteString("Invalid@Name"); // Invalid char
        invalidNameCmd.WriteString("TAG");
        invalidNameCmd.WriteString("M1");
        invalidNameCmd.WriteString("M2");
        invalidNameCmd.WriteString("M3");
        invalidNameCmd.WriteString("M4");

        await ChatTestHelpers.SendPacketAsync(stream, invalidNameCmd);
        await ChatTestHelpers.ExpectCommandAsync(stream, ChatProtocol.Command.CHAT_CMD_CLAN_CREATE_FAIL_NAME);

        // Test Invalid Tag
        ChatBuffer invalidTagCmd = new();
        invalidTagCmd.WriteCommand(ChatProtocol.Command.CHAT_CMD_CLAN_CREATE_REQUEST);
        invalidTagCmd.WriteString("Valid Name");
        invalidTagCmd.WriteString("T@G"); // Invalid char
        invalidTagCmd.WriteString("M1");
        invalidTagCmd.WriteString("M2");
        invalidTagCmd.WriteString("M3");
        invalidTagCmd.WriteString("M4");

        await ChatTestHelpers.SendPacketAsync(stream, invalidTagCmd);
        await ChatTestHelpers.ExpectCommandAsync(stream, ChatProtocol.Command.CHAT_CMD_CLAN_CREATE_FAIL_TAG);
    }

    [Fact]
    public async Task ClanCreate_RejectsDuplicateTagCaseInsensitively_WithFailTagError()
    {
        TRANSMUTANSTEINServiceProvider app = fixture.Provider;

        string suffix = Guid.NewGuid().ToString("N")[..3];
        int creatorId = Random.Shared.Next(710000, 719999);
        string creatorName = $"DupTagCr_{suffix}";
        string takenTag = $"T{suffix}".ToUpperInvariant();

        await ChatTestHelpers.SeedLock.WaitAsync();
        try
        {
            using IServiceScope scope = app.Services.CreateScope();
            MerrickContext db = scope.ServiceProvider.GetRequiredService<MerrickContext>();

            Role role = await db.Roles.FindAsync(1) ?? new Role { ID = 1, Name = "User" };
            if (role.ID == 0) db.Roles.Add(role);

            if (await db.Accounts.FindAsync(creatorId) == null)
            {
                User user = new()
                {
                    ID = creatorId,
                    EmailAddress = $"duptag{creatorId}@test.com",
                    SRPPasswordHash = "hash",
                    SRPPasswordSalt = "salt",
                    PBKDF2PasswordHash = "hash",
                    Role = role
                };
                Account acct = new() { ID = creatorId, Name = creatorName, IsMain = true, User = user };
                user.Accounts = [acct];
                db.Users.Add(user);
            }

            db.Clans.Add(new Clan { Name = $"ExistingClan{suffix}", Tag = takenTag });
            await db.SaveChangesAsync();
        }
        finally
        {
            ChatTestHelpers.SeedLock.Release();
        }

        using TcpClient client =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, creatorId, creatorName);
        NetworkStream stream = client.GetStream();

        // Attempt to create a brand-new clan name using the same tag in lowercase
        ChatBuffer dupTagCmd = new();
        dupTagCmd.WriteCommand(ChatProtocol.Command.CHAT_CMD_CLAN_CREATE_REQUEST);
        dupTagCmd.WriteString($"BrandNewClan{suffix}");
        dupTagCmd.WriteString(takenTag.ToLowerInvariant());
        dupTagCmd.WriteString("M1");
        dupTagCmd.WriteString("M2");
        dupTagCmd.WriteString("M3");
        dupTagCmd.WriteString("M4");

        await ChatTestHelpers.SendPacketAsync(stream, dupTagCmd);
        await ChatTestHelpers.ExpectCommandAsync(stream, ChatProtocol.Command.CHAT_CMD_CLAN_CREATE_FAIL_TAG, 5);
    }
}
