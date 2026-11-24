// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using ASPIRE.Common.Models.ServerManagement;
using ASPIRE.Tests.TRANSMUTANSTEIN.ChatServer.Infrastructure;
using KONGOR.MasterServer.Extensions.Cache;
using TRANSMUTANSTEIN.ChatServer.Domain.Core;
using TRANSMUTANSTEIN.ChatServer.Internals;

namespace ASPIRE.Tests.TRANSMUTANSTEIN.ChatServer.Tests;

[SuppressMessage("Design", "CA1051:Do not declare visible instance fields")]
[SuppressMessage("Design", "CA2211:Non-constant fields should not be visible")]
[SuppressMessage("ReSharper", "NonConstantFieldsShouldNotBeVisible")]
public static class ChatTestHelpers
{
    public static readonly SemaphoreSlim SeedLock = new(1, 1);
    public static int s_accountIdCounter = 90000;

    public static int GetAvailablePort()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public static async Task<TcpClient> ConnectAndAuthenticateAsync(
        TRANSMUTANSTEINServiceProvider app,
        int port,
        int accountId,
        string accountName = "TestUser",
        string region = "USE",
        string language = "en",
        string? clanName = null,
        string? clanTag = null,
        AccountType accountType = AccountType.Normal)
    {
        if (accountName.Length > 15)
        {
            string idStr = accountId.ToString();
            int prefixLen = Math.Max(1, 15 - idStr.Length);
            accountName = $"{accountName[..Math.Min(accountName.Length, prefixLen)]}{idStr}";
            if (accountName.Length > 15) accountName = accountName[..15];
        }
        // Seed Database and Cache
        // Serialize seeding to avoid race conditions in InMemory DB
        int targetAccountId = accountId;
        await SeedLock.WaitAsync();
        try
        {
            using IServiceScope scope = app.Services.CreateScope();
            MerrickContext db = scope.ServiceProvider.GetRequiredService<MerrickContext>();
            IDatabase cache = scope.ServiceProvider.GetRequiredService<IDatabase>();

            db.ChangeTracker.Clear();

            // 1. Ensure Role
            Role? role = await db.Roles.FindAsync(1);
            if (role == null)
            {
                role = new Role { ID = 1, Name = "User" };
                db.Roles.Add(role);
                await db.SaveChangesAsync();
            }

            // 2. Ensure User/Account
            Account? existingAccount = await db.Accounts
                .Include(a => a.Clan)
                .FirstOrDefaultAsync(a => a.ID == accountId);
            if (existingAccount == null)
            {
                role = await db.Roles.FindAsync(1);

                User user = new()
                {
                    ID = accountId,
                    EmailAddress = $"test{accountId}@test.com",
                    SRPPasswordHash = "hash",
                    SRPPasswordSalt = "salt",
                    PBKDF2PasswordHash = "hash",
                    Role = role!
                };

                Account account = new()
                {
                    ID = accountId,
                    Name = accountName,
                    IsMain = true,
                    User = user,
                    Type = accountType
                };

                user.Accounts = [account];

                db.Users.Add(user);
                db.Accounts.Add(account);

                db.Entry(user).Property(u => u.ID).IsTemporary = false;
                db.Entry(account).Property(a => a.ID).IsTemporary = false;

                await db.SaveChangesAsync();

                existingAccount = account;
            }
            else
            {
                // Update name/type if different (handling reruns)
                bool changed = false;
                if (existingAccount.Name != accountName)
                {
                    existingAccount.Name = accountName;
                    changed = true;
                }

                if (existingAccount.Type != accountType)
                {
                    existingAccount.Type = accountType;
                    changed = true;
                }

                if (changed)
                {
                    db.Accounts.Update(existingAccount);
                    await db.SaveChangesAsync();
                }
            }

            // 3. Ensure Clan
            if (clanName is not null && clanTag is not null)
            {
                Clan? clan = await db.Clans.FirstOrDefaultAsync(c => c.Name == clanName);
                if (clan is null)
                {
                    clan = new Clan { Name = clanName, Tag = clanTag };
                    db.Clans.Add(clan);
                    await db.SaveChangesAsync();
                }

                if (existingAccount.Clan?.ID != clan.ID)
                {
                    existingAccount.Clan = clan;
                    existingAccount.ClanTier = ClanTier.Leader; // Assume leader if creating/joining for test simplicity
                    db.Accounts.Update(existingAccount);
                    await db.SaveChangesAsync();
                }
            }

            targetAccountId = existingAccount.ID;
            await cache.SetAccountNameForSessionCookie($"cookie_{targetAccountId}", accountName);
        }
        finally
        {
            SeedLock.Release();
        }

        IChatContext chatContext = app.Services.GetRequiredService<IChatContext>();

        const int maxRetries = 3;
        Exception? lastException = null;

        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            // Evict lingering stale sessions for this account ID on every attempt to prevent 0x1C01 ECR_ACCOUNT_SHARING
            List<KeyValuePair<string, ChatSession>> staleSessions =
            [
                .. chatContext.ClientChatSessions
                    .Where(pair => (pair.Value.Account is { } sessionAccount && sessionAccount.ID == targetAccountId) ||
                                   pair.Key.Equals(accountName, StringComparison.OrdinalIgnoreCase))
            ];

            foreach (KeyValuePair<string, ChatSession> stale in staleSessions)
            {
                chatContext.ClientChatSessions.TryRemove(stale.Key, out _);
                try
                {
                    stale.Value.Socket.Close();
                }
                catch
                {
                    // Ignore socket cleanup exceptions for stale test sessions
                }
            }

            TcpClient? client = null;
            try
            {
                client = new TcpClient();
                // Use 127.0.0.1 to avoid IPv6/DNS issues when server is bound to 0.0.0.0
                await client.ConnectAsync("127.0.0.1", port);
                NetworkStream stream = client.GetStream();

                const string ip = "127.0.0.1";
                string cookie = $"cookie_{targetAccountId}";
                string hash = SRPAuthenticationHandlers.ComputeChatServerCookieHash(targetAccountId, ip, cookie);

                // Send Login Packet (0x0C00)
                ChatBuffer loginBuffer = new();
                loginBuffer.WriteCommand(ChatProtocol.ClientToChatServer.NET_CHAT_CL_CONNECT);
                loginBuffer.WriteInt32(targetAccountId);
                loginBuffer.WriteString(cookie);
                loginBuffer.WriteString(ip);
                loginBuffer.WriteString(hash);
                // ... (rest of handshake payload remains same, simplified context for brevity but keeping full logic)
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
                loginBuffer.WriteString(region);
                loginBuffer.WriteString(language);

                // Prepend Length
                byte[] loginPacket = [.. loginBuffer.Data.AsSpan(0, (int)loginBuffer.Size)];
                ushort packetLength = (ushort)loginPacket.Length;
                List<byte> rawBytes =
                [
                    .. BitConverter.GetBytes(packetLength),
                    .. loginPacket
                ];

                await stream.WriteAsync(rawBytes.ToArray());

                // Read through the handshake to ensure we are connected
                using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
                bool handshakeComplete = false;

                while (!handshakeComplete)
                {
                    // Header
                    byte[] header = new byte[4];
                    int bytesRead = 0;
                    while (bytesRead < 4)
                    {
                        int read = await stream.ReadAsync(header, bytesRead, 4 - bytesRead, cts.Token);
                        if (read == 0) throw new Exception("Disconnected during handshake");

                        bytesRead += read;
                    }

                    ushort length = BitConverter.ToUInt16(header, 0);
                    ushort command = BitConverter.ToUInt16(header, 2);

                    if (command == ChatProtocol.ChatServerToClient.NET_CHAT_CL_REJECT)
                    {
                        byte[] payload = new byte[length - 2];
                        int payloadRead = 0;
                        while (payloadRead < payload.Length)
                        {
                            int read = await stream.ReadAsync(payload, payloadRead, payload.Length - payloadRead,
                                cts.Token);
                            if (read == 0) break;

                            payloadRead += read;
                        }

                        byte reason = payload.Length > 0 ? payload[0] : (byte)0;
                        throw new Exception($"Connection Rejected (0x1C01) with Reason Code: 0x{reason:X2}");
                    }

                    if (command == ChatProtocol.Command.CHAT_CMD_UPDATE_STATUS)
                        handshakeComplete =
                            true; // Drains NET_CHAT_CL_ACCEPT and CHAT_CMD_OPTIONS (0x00C0) before completing

                    // Consume Payload
                    int payloadSize = length - 2;
                    if (payloadSize > 0)
                    {
                        byte[] payload = new byte[payloadSize];
                        int payloadRead = 0;
                        while (payloadRead < payloadSize)
                        {
                            int read = await stream.ReadAsync(payload, payloadRead, payloadSize - payloadRead,
                                cts.Token);
                            if (read == 0) throw new Exception("Disconnected reading payload");

                            payloadRead += read;
                        }
                    }
                }

                // If success, return the connected client
                return client;
            }
            catch (Exception ex)
            {
                lastException = ex;
                client?.Dispose(); // CLEANUP FAILED CLIENT

                if (attempt < maxRetries - 1) await Task.Delay(20); // Wait before retry
            }
        }

        throw lastException ?? new Exception("Failed to connect to Chat Server after retries.");
    }

    public static async Task<ChatBuffer> ExpectCommandAsync(NetworkStream stream, ushort expectedCommand,
        int timeoutSeconds = 15)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(timeoutSeconds));

        // Read until we find the command or timeout
        // Note: In a real scenario we might need to buffer and parse fully if commands are interleaved,
        // but for strict test sequences, we expect it to be the next one.
        // However, we must skip potential heartbeats/status updates if necessary, or just fail if strict.
        // For this test, we assume strict ordering or simple noise filtering.

        while (true)
        {
            byte[] h = new byte[4];
            int read = 0;
            while (read < 4)
            {
                int chunk = await stream.ReadAsync(h, read, 4 - read, cts.Token);
                if (chunk == 0)
                    throw new EndOfStreamException(
                        $"Chat stream closed cleanly (EOF after {read}/4 header bytes) while awaiting command 0x{expectedCommand:X4}.");

                read += chunk;
            }

            ushort len = BitConverter.ToUInt16(h, 0);
            ushort cmd = BitConverter.ToUInt16(h, 2);

            if (cmd == expectedCommand)
            {
                // Consume payload
                int payloadSize = len - 2;
                byte[] payload = [];
                if (payloadSize > 0)
                {
                    payload = new byte[payloadSize];
                    int pread = 0;
                    while (pread < payloadSize)
                    {
                        int chunk = await stream.ReadAsync(payload, pread, payloadSize - pread, cts.Token);
                        if (chunk == 0)
                            throw new EndOfStreamException(
                                $"Chat stream closed cleanly (EOF after {pread}/{payloadSize} payload bytes) for command 0x{expectedCommand:X4}.");

                        pread += chunk;
                    }
                }

                return new ChatBuffer(payload);
            }

            // Consume unexpected packet payload and continue waiting for expectedCommand
            int unexpectedSize = len - 2;
            if (unexpectedSize > 0)
            {
                byte[] junk = new byte[unexpectedSize];
                int pread = 0;
                while (pread < unexpectedSize)
                {
                    int chunk = await stream.ReadAsync(junk, pread, unexpectedSize - pread, cts.Token);
                    if (chunk == 0)
                        throw new EndOfStreamException(
                            $"Chat stream closed cleanly (EOF after {pread}/{unexpectedSize} payload bytes of unexpected command 0x{cmd:X4}) while awaiting 0x{expectedCommand:X4}.");

                    pread += chunk;
                }
            }
        }
    }

    public static async Task<int> JoinChannelAndGetId(TcpClient client, string channelName)
    {
        ChatBuffer join = new();
        join.WriteCommand(ChatProtocol.Command.CHAT_CMD_JOIN_CHANNEL);
        join.WriteString(channelName);

        byte[] packetBytes = [.. join.Data.AsSpan(0, (int)join.Size)];
        List<byte> r =
        [
            .. BitConverter.GetBytes((ushort)packetBytes.Length),
            .. packetBytes
        ];

        await client.GetStream().WriteAsync(r.ToArray());

        // Read 0x0004 response to get ID
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        while (true)
        {
            byte[] h = new byte[4];
            int readTotal = 0;
            while (readTotal < 4)
            {
                int rread = await client.GetStream().ReadAsync(h, readTotal, 4 - readTotal, cts.Token);
                if (rread == 0) throw new Exception("Disconnected while waiting for Join response");

                readTotal += rread;
            }

            ushort l = BitConverter.ToUInt16(h, 0);
            ushort c = BitConverter.ToUInt16(h, 2);

            byte[] py = new byte[l - 2];
            int readPy = 0;
            while (readPy < py.Length)
                readPy += await client.GetStream().ReadAsync(py, readPy, py.Length - readPy, cts.Token);

            if (c == ChatProtocol.Command.CHAT_CMD_CHANGED_CHANNEL) // 0x0004
            {
                ChatBuffer b = new(py);
                b.ReadString(); // Name
                return b.ReadInt32(); // ID
            }

            // Ignore noise (e.g. 0x0006 LEFT, 0x0005 JOINED)
            // But if we get LEFT for SELF, we are kicked?
            // Assuming noise for now.
        }
    }

    public static async Task SendPacketAsync(NetworkStream stream, ChatBuffer buffer)
    {
        byte[] packet = [.. buffer.Data.AsSpan(0, (int)buffer.Size)];
        List<byte> rawPacket =
        [
            .. BitConverter.GetBytes((ushort)packet.Length),
            .. packet
        ];
        await stream.WriteAsync(rawPacket.ToArray());
    }

    public static async Task<TcpClient> ConnectAndAuthenticateGameServerAsync(
        TRANSMUTANSTEINServiceProvider app,
        int port,
        int serverId,
        string sessionCookie,
        string ipAddress = "127.0.0.1",
        int gamePort = 11235,
        string location = "Local")
    {
        TcpClient? client = null;
        try
        {
            client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", port);
            NetworkStream stream = client.GetStream();

            // Send Connect Packet (0x0500)
            ChatBuffer buffer = new();
            buffer.WriteCommand(ChatProtocol.GameServerToChatServer.NET_CHAT_GS_CONNECT);
            buffer.WriteInt32(serverId);
            buffer.WriteString(sessionCookie);
            buffer.WriteInt32((int)ChatProtocol.CHAT_PROTOCOL_EXTERNAL_VERSION);

            await SendPacketAsync(stream, buffer);

            // Expect NET_CHAT_GS_ACCEPT (0x1500)
            await ExpectCommandAsync(stream, ChatProtocol.ChatServerToGameServer.NET_CHAT_GS_ACCEPT);

            // Expect NET_CHAT_GS_REMOTE_COMMAND (0x1504) - injected config
            await ExpectCommandAsync(stream, ChatProtocol.ChatServerToGameServer.NET_CHAT_GS_REMOTE_COMMAND);

            // Send Status Update (IDLE) to make it available for matchmaking
            // NET_CHAT_GS_STATUS (0x0502)
            ChatBuffer statusBuffer = new();
            // ServerStatusRequestData reads CommandBytes first (2 bytes)
            statusBuffer.WriteCommand(ChatProtocol.GameServerToChatServer.NET_CHAT_GS_STATUS);
            statusBuffer.WriteInt32(serverId);
            statusBuffer.WriteString(ipAddress);
            statusBuffer.WriteInt16((short)gamePort); // Port
            statusBuffer.WriteString(location); // Location
            statusBuffer.WriteString("MockServer"); // Name
            statusBuffer.WriteInt32(-1); // SlaveID
            statusBuffer.WriteInt32(-1); // MatchID
            statusBuffer.WriteInt8((byte)ChatProtocol.ServerStatus.SERVER_STATUS_IDLE); // Status
            statusBuffer.WriteInt8(0); // ArrangedMatchType
            statusBuffer.WriteInt16((short)ChatProtocol.ServerType.SSF_OFFICIAL); // Flags
            statusBuffer.WriteString("newerth"); // MapName
            statusBuffer.WriteString("HoN"); // GameName
            statusBuffer.WriteString("normal"); // GameMode
            statusBuffer.WriteInt8(5); // TeamSize
            statusBuffer.WriteInt16(1500); // MinRating
            statusBuffer.WriteInt16(1500); // MaxRating
            statusBuffer.WriteInt32(0); // CurrentGameTime
            statusBuffer.WriteInt32(0); // CurrentGamePhase
            statusBuffer.WriteString(""); // LegionTeamInfo
            statusBuffer.WriteString(""); // HellbourneTeamInfo
            // 10 PlayerInfo strings
            for (int i = 0; i < 10; i++) statusBuffer.WriteString("");

            statusBuffer.WriteInt32(0); // ServerLoad
            statusBuffer.WriteInt32(0); // LongServerFrameCount
            statusBuffer.WriteInt64(0); // FreePhysicalMemory
            statusBuffer.WriteInt64(0); // TotalPhysicalMemory
            statusBuffer.WriteInt64(0); // DriveFreeSpace
            statusBuffer.WriteInt64(0); // DriveTotalSpace
            statusBuffer.WriteString("4.10.1.0"); // Version
            statusBuffer.WriteInt32(0); // ClientCount
            statusBuffer.WriteFloat32(0f); // LoadAverage
            statusBuffer.WriteString("MockHost"); // HostName

            // ClientNetworkStatistics loop (ClientCount is 0, so 0 iterations)

            await SendPacketAsync(stream, statusBuffer);

            IChatContext chatContext = app.Services.GetRequiredService<IChatContext>();
            bool registeredIdle = false;
            for (int k = 0; k < 100; k++)
            {
                if (chatContext.MatchServerChatSessions.TryGetValue(serverId, out ChatSession? s) &&
                    s.ServerMetadata.Status == ChatProtocol.ServerStatus.SERVER_STATUS_IDLE &&
                    string.Equals(s.ServerMetadata.Location, location, StringComparison.Ordinal))
                {
                    registeredIdle = true;
                    break;
                }

                await Task.Delay(20);
            }

            if (!registeredIdle)
            {
                throw new InvalidOperationException(
                    $"GameServer {serverId} (location={location}, port={gamePort}) did not reach SERVER_STATUS_IDLE in MatchServerChatSessions within 2000ms (active count={chatContext.MatchServerChatSessions.Count}).");
            }

            return client;
        }
        catch
        {
            client?.Dispose();
            throw;
        }
    }

    public static async Task SeedGameServerHostAsync(
        TRANSMUTANSTEINServiceProvider app,
        int hostAccountId,
        int serverId,
        string sessionCookie,
        string serverName = "MockServer",
        string ipAddress = "127.0.0.1",
        int port = 11235,
        string region = "USE",
        string location = "Local")
    {
        await SeedLock.WaitAsync();
        try
        {
            using IServiceScope scope = app.Services.CreateScope();
            MerrickContext db = scope.ServiceProvider.GetRequiredService<MerrickContext>();
            IDatabase cache = scope.ServiceProvider.GetRequiredService<IDatabase>();

            // 1. Ensure ServerHost Role
            // Assuming AccountType enum handles role checks usually, but let's just make sure account exists.

            // 2. Ensure Host Account
            Account? existingAccount = await db.Accounts.FindAsync(hostAccountId);
            if (existingAccount == null)
            {
                Role role = await db.Roles.FindAsync(1) ?? new Role { ID = 1, Name = "User" }; // Fallback

                User user = new()
                {
                    ID = hostAccountId,
                    EmailAddress = $"host{hostAccountId}@test.com",
                    SRPPasswordHash = "hash",
                    SRPPasswordSalt = "salt",
                    PBKDF2PasswordHash = "hash",
                    Role = role
                };

                Account account = new()
                {
                    ID = hostAccountId,
                    Name = $"Host_{hostAccountId}",
                    IsMain = true,
                    User = user,
                    Type = AccountType.ServerHost // CRITICAL
                };

                user.Accounts = [account];
                db.Users.Add(user);
                await db.SaveChangesAsync();
            }
            else
            {
                if (existingAccount.Type != AccountType.ServerHost)
                {
                    existingAccount.Type = AccountType.ServerHost;
                    db.Accounts.Update(existingAccount);
                    await db.SaveChangesAsync();
                }
            }

            Account hostAcc = existingAccount ?? await db.Accounts.FirstAsync(a => a.User.ID == hostAccountId);

            MatchServer server = newMatchServer(
                serverId,
                sessionCookie,
                serverName,
                ipAddress,
                port,
                location,
                hostAcc.ID,
                hostAcc.Name,
                region
            );

            await cache.SetMatchServer(hostAcc.Name, server);
        }
        finally
        {
            SeedLock.Release();
        }
    }

    // Helper to construct MatchServer object since we might not have access to full constructor or setters depending on visibility
    private static MatchServer newMatchServer(int id, string cookie, string name, string ip, int port, string loc,
        int hostId, string hostName, string region)
    {
        return new MatchServer
        {
            ID = id,
            Cookie = cookie,
            Name = name,
            IPAddress = ip,
            Port = port,
            Location = loc,
            HostAccountID = hostId,
            HostAccountName = hostName,
            Instance = 1, // Default Instance
            Description = "Mock Server", // Default Description
            TimestampRegistered = TimeProvider.System.GetUtcNow()
            // Type and Region removed as they don't exist in the definition I read. Status is defaulted to UNKNOWN.
        };
    }
}
