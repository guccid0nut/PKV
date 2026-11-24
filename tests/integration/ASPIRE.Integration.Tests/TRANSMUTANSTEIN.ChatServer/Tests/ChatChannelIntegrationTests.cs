// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net.Sockets;
using ASPIRE.Tests.TRANSMUTANSTEIN.ChatServer.Infrastructure;
using TRANSMUTANSTEIN.ChatServer.Domain.Core;

namespace ASPIRE.Tests.TRANSMUTANSTEIN.ChatServer.Tests;

[Collection("TRANSMUTANSTEIN ChatServer Collection")]
public sealed class ChatChannelIntegrationTests(TRANSMUTANSTEINTestFixture fixture)
{
    [Fact]
    public async Task ChatChannelIntegration_GetChannelList_Success()
    {
        TRANSMUTANSTEINServiceProvider app = fixture.Provider;

        const int userId = 9911;

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
                Account a1 = new() { ID = userId, Name = "ListUser", IsMain = true, User = u1 };
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
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, userId, "ListUser");

        ChatBuffer cmd = new();
        cmd.WriteCommand(0x0C01); // NET_CHAT_CL_GET_CHANNEL_LIST

        byte[] packet = [.. cmd.Data.AsSpan(0, (int)cmd.Size)];
        List<byte> rawBytes =
        [
            .. BitConverter.GetBytes((ushort)packet.Length),
            .. packet
        ];
        await client.GetStream().WriteAsync(rawBytes.ToArray());

        ChatBuffer resp =
            await ChatTestHelpers.ExpectCommandAsync(client.GetStream(), 0x1C03, 5); // NET_CHAT_CL_CHANNEL_LIST_SYN
        Assert.NotNull(resp);
    }

    [Fact]
    public async Task ChatChannelIntegration_AddChannelAuthUser_Success()
    {
        TRANSMUTANSTEINServiceProvider app = fixture.Provider;

        const int userId = 9912;
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
                Account a1 = new() { ID = userId, Name = "AuthUser", IsMain = true, User = u1 };
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
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, userId, "AuthUser");
        int channelId = await ChatTestHelpers.JoinChannelAndGetId(client, "AuthChannel");

        ChatBuffer cmd = new();
        cmd.WriteCommand(0x0040); // CHAT_CMD_CHANNEL_ADD_AUTH_USER
        cmd.WriteInt32(channelId);
        cmd.WriteString("SomeUserToAdd");

        byte[] packet = [.. cmd.Data.AsSpan(0, (int)cmd.Size)];
        List<byte> rawBytes =
        [
            .. BitConverter.GetBytes((ushort)packet.Length),
            .. packet
        ];
        await client.GetStream().WriteAsync(rawBytes.ToArray());

        await Task.Delay(50);
        Assert.True(client.Connected);
    }

    [Fact]
    public async Task ChatChannelIntegration_RemoveChannelAuthUser_Success()
    {
        TRANSMUTANSTEINServiceProvider app = fixture.Provider;

        const int userId = 9913;
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
                Account a1 = new() { ID = userId, Name = "RemAuthUser", IsMain = true, User = u1 };
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
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, userId, "RemAuthUser");
        int channelId = await ChatTestHelpers.JoinChannelAndGetId(client, "RemAuthChannel");

        ChatBuffer cmd = new();
        cmd.WriteCommand(0x0041); // CHAT_CMD_CHANNEL_REMOVE_AUTH_USER
        cmd.WriteInt32(channelId);
        cmd.WriteString("SomeUserToRemove");

        byte[] packet = [.. cmd.Data.AsSpan(0, (int)cmd.Size)];
        List<byte> rawBytes =
        [
            .. BitConverter.GetBytes((ushort)packet.Length),
            .. packet
        ];
        await client.GetStream().WriteAsync(rawBytes.ToArray());

        await Task.Delay(50);
        Assert.True(client.Connected);
    }

    [Fact]
    public async Task ChatChannelIntegration_JoinPasswordProtectedChannel_Success()
    {
        TRANSMUTANSTEINServiceProvider app = fixture.Provider;

        const int userId = 9914;
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
                Account a1 = new() { ID = userId, Name = "JoinPwUser", IsMain = true, User = u1 };
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
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, userId, "JoinPwUser");

        ChatBuffer cmd = new();
        cmd.WriteCommand(0x0046); // CHAT_CMD_JOIN_CHANNEL_PASSWORD
        cmd.WriteString("SecretChannel");
        cmd.WriteString("MySecretPass");

        byte[] packet = [.. cmd.Data.AsSpan(0, (int)cmd.Size)];
        List<byte> rawBytes =
        [
            .. BitConverter.GetBytes((ushort)packet.Length),
            .. packet
        ];
        await client.GetStream().WriteAsync(rawBytes.ToArray());

        await Task.Delay(50);
        Assert.True(client.Connected);
    }

    [Fact]
    public async Task ChatChannelIntegration_SetChannelPassword_Success()
    {
        TRANSMUTANSTEINServiceProvider app = fixture.Provider;

        const int userId = 9915;
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
                Account a1 = new() { ID = userId, Name = "SetPwUser", IsMain = true, User = u1 };
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
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, userId, "SetPwUser");
        int channelId = await ChatTestHelpers.JoinChannelAndGetId(client, "SetPwChannel");

        ChatBuffer cmd = new();
        cmd.WriteCommand(0x0043); // CHAT_CMD_CHANNEL_SET_PASSWORD
        cmd.WriteInt32(channelId);
        cmd.WriteString("MySecretPass");

        byte[] packet = [.. cmd.Data.AsSpan(0, (int)cmd.Size)];
        List<byte> rawBytes =
        [
            .. BitConverter.GetBytes((ushort)packet.Length),
            .. packet
        ];
        await client.GetStream().WriteAsync(rawBytes.ToArray());

        await Task.Delay(50);
        Assert.True(client.Connected);
    }

    [Fact]
    public async Task ChatChannelIntegration_AddChannelAuthUser_OfflineFallback()
    {
        TRANSMUTANSTEINServiceProvider app = fixture.Provider;

        const int userId = 9916;
        const int targetUserId = 9917;
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
                Account a1 = new() { ID = userId, Name = "AddAuthFbkUsr", IsMain = true, User = u1 };
                u1.Accounts = [a1];
                db.Users.Add(u1);
                await db.SaveChangesAsync();
            }

            if (await db.Accounts.FindAsync(targetUserId) == null)
            {
                User u2 = new()
                {
                    ID = targetUserId,
                    EmailAddress = $"user{targetUserId}@test.com",
                    SRPPasswordHash = "h",
                    SRPPasswordSalt = "s",
                    PBKDF2PasswordHash = "h",
                    Role = role
                };
                Account a2 = new() { ID = targetUserId, Name = "AddAuthOffline", IsMain = true, User = u2 };
                u2.Accounts = [a2];
                db.Users.Add(u2);
                await db.SaveChangesAsync();
            }
        }
        finally
        {
            ChatTestHelpers.SeedLock.Release();
        }

        using TcpClient client =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, userId, "AddAuthFbkUsr");
        int channelId = await ChatTestHelpers.JoinChannelAndGetId(client, "AddAuthFbkChan");

        ChatBuffer cmd = new();
        cmd.WriteCommand(0x0040); // CHAT_CMD_CHANNEL_ADD_AUTH_USER
        cmd.WriteString("AddAuthFbkChan");
        cmd.WriteString("[tag]AddAuthOffline"); // DB fallback

        byte[] packet = [.. cmd.Data.AsSpan(0, (int)cmd.Size)];
        List<byte> rawBytes =
        [
            .. BitConverter.GetBytes((ushort)packet.Length),
            .. packet
        ];
        await client.GetStream().WriteAsync(rawBytes.ToArray());

        await Task.Delay(50);
        Assert.True(client.Connected);
    }

    [Fact]
    public async Task ChatChannelIntegration_AddChannelAuthUser_Fail()
    {
        TRANSMUTANSTEINServiceProvider app = fixture.Provider;

        const int userId = 9918;
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
                Account a1 = new() { ID = userId, Name = "AddAuthFailUsr", IsMain = true, User = u1 };
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
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, userId, "AddAuthFailUsr");
        int channelId = await ChatTestHelpers.JoinChannelAndGetId(client, "AddAuthFailChan");

        ChatBuffer cmd = new();
        cmd.WriteCommand(0x0040); // CHAT_CMD_CHANNEL_ADD_AUTH_USER
        cmd.WriteString("AddAuthFailChan");
        cmd.WriteString("[tag]NonExistentUser123"); // Should fail

        byte[] packet = [.. cmd.Data.AsSpan(0, (int)cmd.Size)];
        List<byte> rawBytes =
        [
            .. BitConverter.GetBytes((ushort)packet.Length),
            .. packet
        ];
        await client.GetStream().WriteAsync(rawBytes.ToArray());

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
        ushort command = 0;
        byte[] payload = [];
        while (true)
        {
            byte[] header = new byte[4];
            await client.GetStream().ReadExactlyAsync(header, cts.Token);
            ushort length = BitConverter.ToUInt16(header, 0);
            command = BitConverter.ToUInt16(header, 2);

            payload = new byte[length - 2];
            await client.GetStream().ReadExactlyAsync(payload, cts.Token);

            if (command == 0x0044) break; // CHAT_CMD_CHANNEL_ADD_AUTH_FAIL
        }

        Assert.Equal(0x0044, command);
        ChatBuffer response = new(payload);
        string respChanName = response.ReadString();
        string respTargetName = response.ReadString();

        Assert.Equal("AddAuthFailChan", respChanName);
        Assert.Equal("[tag]NonExistentUser123", respTargetName);
    }

    [Fact]
    public async Task ChatChannelIntegration_RemoveChannelAuthUser_OfflineFallback()
    {
        TRANSMUTANSTEINServiceProvider app = fixture.Provider;

        const int userId = 9919;
        const int targetUserId = 9920;
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
                Account a1 = new() { ID = userId, Name = "RemAuthFbkUsr", IsMain = true, User = u1 };
                u1.Accounts = [a1];
                db.Users.Add(u1);
                await db.SaveChangesAsync();
            }

            if (await db.Accounts.FindAsync(targetUserId) == null)
            {
                User u2 = new()
                {
                    ID = targetUserId,
                    EmailAddress = $"user{targetUserId}@test.com",
                    SRPPasswordHash = "h",
                    SRPPasswordSalt = "s",
                    PBKDF2PasswordHash = "h",
                    Role = role
                };
                Account a2 = new() { ID = targetUserId, Name = "RemAuthOffline", IsMain = true, User = u2 };
                u2.Accounts = [a2];
                db.Users.Add(u2);
                await db.SaveChangesAsync();
            }
        }
        finally
        {
            ChatTestHelpers.SeedLock.Release();
        }

        using TcpClient client =
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, userId, "RemAuthFbkUsr");
        int channelId = await ChatTestHelpers.JoinChannelAndGetId(client, "RemAuthFbkChan");

        ChatBuffer cmd = new();
        cmd.WriteCommand(0x0041); // CHAT_CMD_CHANNEL_REMOVE_AUTH_USER
        cmd.WriteString("RemAuthFbkChan");
        cmd.WriteString("[tag]RemAuthOffline"); // DB fallback

        byte[] packet = [.. cmd.Data.AsSpan(0, (int)cmd.Size)];
        List<byte> rawBytes =
        [
            .. BitConverter.GetBytes((ushort)packet.Length),
            .. packet
        ];
        await client.GetStream().WriteAsync(rawBytes.ToArray());

        await Task.Delay(50);
        Assert.True(client.Connected);
    }

    [Fact]
    public async Task ChatChannelIntegration_RemoveChannelAuthUser_Fail()
    {
        TRANSMUTANSTEINServiceProvider app = fixture.Provider;

        const int userId = 9921;
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
                Account a1 = new() { ID = userId, Name = "RemAuthFailUsr", IsMain = true, User = u1 };
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
            await ChatTestHelpers.ConnectAndAuthenticateAsync(app, app.ClientPort, userId, "RemAuthFailUsr");
        int channelId = await ChatTestHelpers.JoinChannelAndGetId(client, "RemAuthFailChan");

        ChatBuffer cmd = new();
        cmd.WriteCommand(0x0041); // CHAT_CMD_CHANNEL_REMOVE_AUTH_USER
        cmd.WriteString("RemAuthFailChan");
        cmd.WriteString("[tag]NonExistentUser123"); // Should fail

        byte[] packet = [.. cmd.Data.AsSpan(0, (int)cmd.Size)];
        List<byte> rawBytes =
        [
            .. BitConverter.GetBytes((ushort)packet.Length),
            .. packet
        ];
        await client.GetStream().WriteAsync(rawBytes.ToArray());

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
        ushort command = 0;
        byte[] payload = [];
        while (true)
        {
            byte[] header = new byte[4];
            await client.GetStream().ReadExactlyAsync(header, cts.Token);
            ushort length = BitConverter.ToUInt16(header, 0);
            command = BitConverter.ToUInt16(header, 2);

            payload = new byte[length - 2];
            await client.GetStream().ReadExactlyAsync(payload, cts.Token);

            if (command == 0x0045) break; // CHAT_CMD_CHANNEL_REMOVE_AUTH_FAIL
        }

        Assert.Equal(0x0045, command);
        ChatBuffer response = new(payload);
        string respChanName = response.ReadString();
        string respTargetName = response.ReadString();

        Assert.Equal("RemAuthFailChan", respChanName);
        Assert.Equal("[tag]NonExistentUser123", respTargetName);
    }
}
