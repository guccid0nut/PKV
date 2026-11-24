// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net.Sockets;
using ASPIRE.Tests.TRANSMUTANSTEIN.ChatServer.Infrastructure;
using MERRICK.DatabaseContext.Entities.Statistics;
using TRANSMUTANSTEIN.ChatServer.Domain.Core;
using TRANSMUTANSTEIN.ChatServer.Domain.Matchmaking;
using TRANSMUTANSTEIN.ChatServer.Internals;
using TRANSMUTANSTEIN.ChatServer.Services;

// Fixed Namespace

namespace ASPIRE.Tests.TRANSMUTANSTEIN.ChatServer.Tests;

[Collection("TRANSMUTANSTEIN ChatServer Collection")]
public class MatchmakingServiceTests(TRANSMUTANSTEINTestFixture fixture) : IAsyncLifetime
{
    // Integration Test Provider
    private TRANSMUTANSTEINServiceProvider _app = null!;
    private MerrickContext _context = null!;
    private IMatchmakingService _service = null!;


    public async Task InitializeAsync()
    {
        _app = fixture.Provider;
        _service = _app.Services.GetRequiredService<IMatchmakingService>();
        IServiceScope scope = _app.Services.CreateScope();
        _context = scope.ServiceProvider.GetRequiredService<MerrickContext>();
        await Task.CompletedTask;
    }


    public async Task DisposeAsync()
    {
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Broker_ShouldMatch2SoloPlayers_WhenAllCompatible()
    {
        // Arrange
        List<ChatSession> sessions = [];
        List<TcpClient> clients = []; // Keep alive

        IChatContext chatContext = _app.Services.GetRequiredService<IChatContext>();

        // Seed and Connect Match Server with isolated port & region so 30s Redis slot leases do not collide across tests
        int hostId = Interlocked.Increment(ref ChatTestHelpers.s_accountIdCounter);
        int serverId = Interlocked.Increment(ref ChatTestHelpers.s_accountIdCounter);
        int gamePort = 20000 + (serverId % 30000);
        string region = $"USW_{serverId}";
        string serverCookie = $"cookie_host_{serverId}";

        await ChatTestHelpers.SeedGameServerHostAsync(
            _app, hostId, serverId, serverCookie, port: gamePort, region: region, location: region);
        using TcpClient serverClient =
            await ChatTestHelpers.ConnectAndAuthenticateGameServerAsync(
                _app, _app.MatchServerPort, serverId, serverCookie, gamePort: gamePort, location: region);

        int p1 = Interlocked.Increment(ref ChatTestHelpers.s_accountIdCounter);
        int p2 = Interlocked.Increment(ref ChatTestHelpers.s_accountIdCounter);
        int[] playerIds = [p1, p2];

        try
        {
            foreach (int i in playerIds)
            {
                // Connect Real Client
                TcpClient client =
                    await ChatTestHelpers.ConnectAndAuthenticateAsync(_app, _app.ClientPort, i, $"Player{i}");
                clients.Add(client);

                // Find session by Account ID
                // IChatContext uses ConcurrentDictionaries directly
                ChatSession? session = null;
                for (int wait = 0; wait < 20 && session == null; wait++)
                {
                    session = chatContext.ClientChatSessions.Values.FirstOrDefault(s => s.Account?.ID == i);
                    if (session == null) await Task.Delay(50);
                }

                Assert.NotNull(session);
                sessions.Add(session);

                // Seed Stats (using same context as app)
                if (await _context.AccountStatistics.SingleOrDefaultAsync(s =>
                        s.AccountID == i && s.Type == AccountStatisticsType.Matchmaking) == null)
                {
                    _context.AccountStatistics.Add(new AccountStatistics
                    {
                        AccountID = i,
                        Account = null!,
                        Type = AccountStatisticsType.Matchmaking,
                        SkillRating = 1500
                    });
                    await _context.SaveChangesAsync();
                }

                // Create Group
                MatchmakingGroupInformation info = new()
                {
                    // TeamSize is read-only computed from GameType
                    GameType = ChatProtocol.TMMGameType.TMM_GAME_TYPE_NORMAL,
                    GameModes = ["ap"],
                    GameRegions = [region],
                    MapName = "newerth",
                    ClientVersion = "4.10.1",
                    GroupType = ChatProtocol.TMMType.TMM_TYPE_PVP, // Correct Enum
                    Ranked = true,
                    MatchFidelity = 0,
                    BotDifficulty = 0,
                    RandomizeBots = false
                };

                MatchmakingGroup group = MatchmakingGroup.Create(_service, session, _context, info);

                // Join Queue
                group.SendPlayerReadinessStatusUpdate(session, ChatProtocol.TMMGameType.TMM_GAME_TYPE_NORMAL);
                group.SendLoadingStatusUpdate(session, 100);
            }

            Assert.Equal(2, sessions.Count);

            // Act & Assert
            // Manual "Eventually" Loop (scoped to this test's players so concurrent collection groups do not interfere)
            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
            bool success = false;
            while (!cts.Token.IsCancellationRequested)
            {
                if (_service.Groups.Values
                    .Where(g => g.Leader.Account.ID == p1 || g.Leader.Account.ID == p2)
                    .All(g => g.QueueStartTime == null))
                {
                    success = true;
                    break;
                }

                await Task.Delay(10, cts.Token);
            }

            Assert.True(success);
        }
        finally
        {
            foreach (int id in playerIds) _service.Groups.TryRemove(id, out _);
            chatContext.MatchServerChatSessions.TryRemove(serverId, out _);
            foreach (TcpClient client in clients) client.Dispose();
        }
    }

    [Fact]
    public async Task Broker_WhenNoIdleServersAvailable_RetainsQueueStateAndDoesNotEvict()
    {
        List<TcpClient> clients = [];
        int p1Id = Interlocked.Increment(ref ChatTestHelpers.s_accountIdCounter);
        int p2Id = Interlocked.Increment(ref ChatTestHelpers.s_accountIdCounter);
        string region = $"USW_NOIDLE_{p1Id}";

        try
        {
            // 2 Players in same isolated region without any idle match server
            clients.Add(await CreateQueuedGroupReal(p1Id, region, ["ap"]));
            clients.Add(await CreateQueuedGroupReal(p2Id, region, ["ap"]));

            // Allow broker time to attempt match formation
            await Task.Delay(100);

            // Assert: Groups should STILL be queued and NOT evicted or marked null despite no idle server
            List<MatchmakingGroup> groups =
            [
                .. _service.Groups.Values
                    .Where(g => g.Leader.Account.ID == p1Id || g.Leader.Account.ID == p2Id)
            ];
            Assert.Equal(2, groups.Count);
            Assert.All(groups, g => Assert.NotNull(g.QueueStartTime));
        }
        finally
        {
            _service.Groups.TryRemove(p1Id, out _);
            _service.Groups.TryRemove(p2Id, out _);
            foreach (TcpClient client in clients) client.Dispose();
        }
    }

    [Fact]
    public async Task Broker_ShouldNotMatch_DifferentRegions()
    {
        List<TcpClient> clients = [];
        int p1Id = Interlocked.Increment(ref ChatTestHelpers.s_accountIdCounter);
        int p2Id = Interlocked.Increment(ref ChatTestHelpers.s_accountIdCounter);
        string regionA = $"USW_DISJOINT_{p1Id}";
        string regionB = $"EU_DISJOINT_{p2Id}";

        try
        {
            // 1 Player USW
            clients.Add(await CreateQueuedGroupReal(p1Id, regionA, ["ap"]));

            // 1 Player EU
            clients.Add(await CreateQueuedGroupReal(p2Id, regionB, ["ap"]));

            await Task.Delay(20); // Give broker time

            // Assert: NO match should occur because buckets are disjoint
            // Check specifically that these two groups are still in queue
            List<MatchmakingGroup> groups =
            [
                .. _service.Groups.Values
                    .Where(g => g.Leader.Account.ID == p1Id || g.Leader.Account.ID == p2Id)
            ];
            Assert.Equal(2, groups.Count);
            Assert.True(groups.All(g => g.QueueStartTime != null));
        }
        finally
        {
            _service.Groups.TryRemove(p1Id, out _);
            _service.Groups.TryRemove(p2Id, out _);
            foreach (TcpClient client in clients) client.Dispose();
        }
    }

    private async Task<TcpClient> CreateQueuedGroupReal(int id, string region, string[] modes)
    {
        TcpClient client = await ChatTestHelpers.ConnectAndAuthenticateAsync(_app, _app.ClientPort, id, $"Player{id}");

        IChatContext chatContext = _app.Services.GetRequiredService<IChatContext>();
        // Retry-loop for session availability as ConnectAndAuthenticateAsync might return before Session is fully registered in Context
        ChatSession? session = null;
        for (int k = 0; k < 15; k++)
        {
            if (chatContext.ClientChatSessions.TryGetValue($"Player{id}", out session)) break;

            session = chatContext.ClientChatSessions.Values.FirstOrDefault(s =>
                s.Account.ID == id || s.Account.Name == $"Player{id}");
            if (session != null) break;

            await Task.Delay(10);
        }

        Assert.NotNull(session);

        MerrickContext context = _app.Services.CreateScope().ServiceProvider.GetRequiredService<MerrickContext>();

        if (await context.AccountStatistics.FindAsync(id) == null)
        {
            context.AccountStatistics.Add(new AccountStatistics
            {
                AccountID = id,
                Account = null!,
                Type = AccountStatisticsType.Matchmaking,
                SkillRating = 1500
            });
            await context.SaveChangesAsync();
        }

        MatchmakingGroupInformation info = new()
        {
            // TeamSize ReadOnly
            GameType = ChatProtocol.TMMGameType.TMM_GAME_TYPE_NORMAL,
            GameModes = modes,
            GameRegions = [region],
            MapName = "newerth",
            ClientVersion = "4.10.1",
            GroupType = ChatProtocol.TMMType.TMM_TYPE_PVP, // Correct Enum
            Ranked = true,
            MatchFidelity = 0,
            BotDifficulty = 0,
            RandomizeBots = false
        };

        MatchmakingGroup group = MatchmakingGroup.Create(_service, session, context, info);
        group.SendPlayerReadinessStatusUpdate(session, ChatProtocol.TMMGameType.TMM_GAME_TYPE_NORMAL);
        group.SendLoadingStatusUpdate(session, 100);

        return client;
    }

    [Fact]
    public async Task Broker_ShouldAllocateGameServer_WhenMatchFound()
    {
        // 0. Get Services
        IChatContext chatContext = _app.Services.GetRequiredService<IChatContext>();

        // 1. Seed Host Account & Match Server with isolated port & region
        int hostId = Interlocked.Increment(ref ChatTestHelpers.s_accountIdCounter);
        int serverId = Interlocked.Increment(ref ChatTestHelpers.s_accountIdCounter);
        int gamePort = 20000 + (serverId % 30000);
        string region = $"USW_{serverId}";
        string serverCookie = $"cookie_host_{serverId}";

        await ChatTestHelpers.SeedGameServerHostAsync(
            _app, hostId, serverId, serverCookie, port: gamePort, region: region, location: region);

        // 2. Connect Fake Match Server
        int matchPort = _app.MatchServerPort;

        using TcpClient serverClient =
            await ChatTestHelpers.ConnectAndAuthenticateGameServerAsync(
                _app, matchPort, serverId, serverCookie, gamePort: gamePort, location: region);

        // 3. Connect Players and Queue
        List<TcpClient> clients = [];
        int p1 = Interlocked.Increment(ref ChatTestHelpers.s_accountIdCounter);
        int p2 = Interlocked.Increment(ref ChatTestHelpers.s_accountIdCounter);

        try
        {
            // Create 2 Players in the isolated region
            clients.Add(await CreateQueuedGroupReal(p1, region, ["ap"]));
            clients.Add(await CreateQueuedGroupReal(p2, region, ["ap"]));

            // 4. Wait for Match Allocation
            // Our "Fake" Match Server should receive NET_CHAT_GS_CREATE_MATCH (0x1502)
            await ChatTestHelpers.ExpectCommandAsync(serverClient.GetStream(),
                ChatProtocol.ChatServerToGameServer.NET_CHAT_GS_CREATE_MATCH, 30);

            // If we reach here, the command was received successfully
        }
        finally
        {
            _service.Groups.TryRemove(p1, out _);
            _service.Groups.TryRemove(p2, out _);
            chatContext.MatchServerChatSessions.TryRemove(serverId, out _);
            foreach (TcpClient c in clients) c.Dispose();
        }
    }

    [Fact]
    public async Task Broker_ShouldAllocateGameServer_ImmediatelyForCoopParty()
    {
        // 0. Get Services
        IChatContext chatContext = _app.Services.GetRequiredService<IChatContext>();

        // 1. Seed Host Account & Match Server with isolated port & region
        int hostId = Interlocked.Increment(ref ChatTestHelpers.s_accountIdCounter);
        int serverId = Interlocked.Increment(ref ChatTestHelpers.s_accountIdCounter);
        int gamePort = 20000 + (serverId % 30000);
        string region = $"USW_{serverId}";
        string serverCookie = $"cookie_host_{serverId}";

        await ChatTestHelpers.SeedGameServerHostAsync(
            _app, hostId, serverId, serverCookie, port: gamePort, region: region, location: region);

        int matchPort = _app.MatchServerPort;
        using TcpClient serverClient =
            await ChatTestHelpers.ConnectAndAuthenticateGameServerAsync(
                _app, matchPort, serverId, serverCookie, gamePort: gamePort, location: region);

        List<TcpClient> clients = [];
        int p1 = Interlocked.Increment(ref ChatTestHelpers.s_accountIdCounter);
        int p2 = Interlocked.Increment(ref ChatTestHelpers.s_accountIdCounter);

        try
        {
            // 2. Connect 2 Party Players for Co-Op Bot Match
            TcpClient client1 = await ChatTestHelpers.ConnectAndAuthenticateAsync(_app, _app.ClientPort, p1, $"Player{p1}");
            TcpClient client2 = await ChatTestHelpers.ConnectAndAuthenticateAsync(_app, _app.ClientPort, p2, $"Player{p2}");
            clients.Add(client1);
            clients.Add(client2);

            ChatSession? session1 = null;
            ChatSession? session2 = null;
            for (int wait = 0; wait < 20 && (session1 == null || session2 == null); wait++)
            {
                session1 ??= chatContext.ClientChatSessions.Values.FirstOrDefault(s => s.Account?.ID == p1);
                session2 ??= chatContext.ClientChatSessions.Values.FirstOrDefault(s => s.Account?.ID == p2);
                if (session1 == null || session2 == null) await Task.Delay(50);
            }

            Assert.NotNull(session1);
            Assert.NotNull(session2);

            MerrickContext context = _app.Services.CreateScope().ServiceProvider.GetRequiredService<MerrickContext>();

            MatchmakingGroupInformation info = new()
            {
                GameType = ChatProtocol.TMMGameType.TMM_GAME_TYPE_CASUAL,
                GameModes = ["ap"],
                GameRegions = [region],
                MapName = "caldavar",
                ClientVersion = "4.10.1",
                GroupType = ChatProtocol.TMMType.TMM_TYPE_COOP,
                Ranked = false,
                MatchFidelity = 0,
                BotDifficulty = 2, // Medium (maps to botdifficulty:1 in engine)
                RandomizeBots = true
            };

            MatchmakingGroup group = MatchmakingGroup.Create(_service, session1, context, info);
            group.Join(session2, context);

            // Leader queues the group
            group.SendPlayerReadinessStatusUpdate(session1, ChatProtocol.TMMGameType.TMM_GAME_TYPE_CASUAL);
            group.SendLoadingStatusUpdate(session1, 100);
            group.SendLoadingStatusUpdate(session2, 100);

            // 3. Match Broker should immediately dispatch NET_CHAT_GS_CREATE_MATCH without waiting for humans
            ChatBuffer buffer;
            try
            {
                buffer = await ChatTestHelpers.ExpectCommandAsync(serverClient.GetStream(),
                    ChatProtocol.ChatServerToGameServer.NET_CHAT_GS_CREATE_MATCH, 30);
            }
            catch (Exception ex) when (ex is OperationCanceledException or EndOfStreamException)
            {
                bool hasServer = chatContext.MatchServerChatSessions.TryGetValue(serverId, out ChatSession? srvSession);
                throw new InvalidOperationException(
                    $"Co-op matchmaking did not dispatch NET_CHAT_GS_CREATE_MATCH within timeout (serverId={serverId}, region={region}, hasServerInContext={hasServer}, serverStatus={srvSession?.ServerMetadata.Status}, serverLocation={srvSession?.ServerMetadata.Location}, matchServerSessionCount={chatContext.MatchServerChatSessions.Count}, groupQueued={group.QueueStartTime != null}). Diagnose with: powershell -File scripts/Invoke-KongorTestLock.ps1 -Owner diag -- dotnet test tests/integration/ASPIRE.Integration.Tests/ASPIRE.Integration.Tests.csproj --filter FullyQualifiedName~MatchmakingServiceTests",
                    ex);
            }

            Assert.NotNull(buffer);

            // Parse createMatch packet (ExpectCommandAsync already consumed the 2-byte command)
            byte matchType = buffer.ReadInt8();
            Assert.Equal((byte)ChatProtocol.ArrangedMatchType.AM_MATCHMAKING_BOTMATCH, matchType);

            int matchUpId = buffer.ReadInt32();
            buffer.ReadInt32(); // Unknown1
            buffer.ReadInt32(); // Password
            string matchName = buffer.ReadString();
            string matchSettings = buffer.ReadString();

            Assert.Contains("mode:botmatch", matchSettings);
            Assert.Contains("map:caldavar", matchSettings);
            Assert.Contains("teamsize:5", matchSettings);
            Assert.Contains("randombots:3|5", matchSettings); // 2 humans => 3 ally bots, 5 enemy bots
            Assert.Contains("botdifficulty:2", matchSettings); // BotDifficulty 2 (Medium) maps 1:1 to engine nDifficulty=2

            buffer.ReadInt8(); // UseNewMmrSystem
            buffer.ReadInt8(); // Unknown3
            byte playerCount = buffer.ReadInt8();
            Assert.Equal(2, playerCount);

            // Both players must be on Team 1 (Legion)
            int p1Id = buffer.ReadInt32();
            byte p1Team = buffer.ReadInt8();
            byte p1Slot = buffer.ReadInt8();
            Assert.Equal(1, p1Team);
            Assert.Equal(0, p1Slot);

            // Skip remaining player 1 fields
            buffer.ReadInt8(); buffer.ReadInt32(); buffer.ReadInt32();
            buffer.ReadInt8(); buffer.ReadInt8(); buffer.ReadInt8();

            int p2Id = buffer.ReadInt32();
            byte p2Team = buffer.ReadInt8();
            byte p2Slot = buffer.ReadInt8();
            Assert.Equal(1, p2Team);
            Assert.Equal(1, p2Slot);
        }
        finally
        {
            _service.Groups.TryRemove(p1, out _);
            _service.Groups.TryRemove(p2, out _);
            chatContext.MatchServerChatSessions.TryRemove(serverId, out _);
            foreach (TcpClient c in clients) c.Dispose();
        }
    }
}
