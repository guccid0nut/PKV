// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Net;
using ASPIRE.Common.Extensions.Cache;
using ASPIRE.Common.Models.ServerManagement;
using ASPIRE.Common.Services;
using FluentAssertions;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Enumerations;
using MERRICK.DatabaseContext.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NEXUS.Contracts.Chat;
using NEXUS.Contracts.Domain;
using StackExchange.Redis;
using TRANSMUTANSTEIN.ChatServer.CommandProcessors.Communication;
using TRANSMUTANSTEIN.ChatServer.CommandProcessors.Connection;
using TRANSMUTANSTEIN.ChatServer.CommandProcessors.MatchState;
using TRANSMUTANSTEIN.ChatServer.CommandProcessors.Social;
using TRANSMUTANSTEIN.ChatServer.Domain.Clans;
using TRANSMUTANSTEIN.ChatServer.Domain.Communication;
using TRANSMUTANSTEIN.ChatServer.Domain.Core;
using TRANSMUTANSTEIN.ChatServer.Domain.Matchmaking;
using TRANSMUTANSTEIN.ChatServer.Infrastructure.Distributed;
using TRANSMUTANSTEIN.ChatServer.Infrastructure.Services;
using TRANSMUTANSTEIN.ChatServer.Internals;
using TRANSMUTANSTEIN.ChatServer.Services;
using TRANSMUTANSTEIN.ChatServer.Utilities;
using Xunit;
using DbRole = MERRICK.DatabaseContext.Entities.Utility.Role;

namespace TRANSMUTANSTEIN.ChatServer.Tests.Social;

public sealed class FriendshipsAndSpectateProcessorTests : IDisposable
{
    private readonly InMemoryRedisHashStore _redisStore = new();
    private readonly Mock<IDatabase> _mockRedis;
    private readonly MerrickContext _dbContext;
    private readonly Mock<IChatContext> _mockChatContext = new();
    private readonly Mock<IDiscordPresencePublisherService> _mockDiscord = new();
    private readonly Mock<IMatchmakingService> _mockMatchmaking = new();
    private readonly Mock<IDistributedChatStateService> _mockDistributedState = new();
    private readonly ConcurrentDictionary<string, ChatSession> _clientSessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, ChatSession> _matchServerSessions = new();
    private readonly ConcurrentDictionary<string, ChatChannel> _chatChannels = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, ChatChannel> _chatChannelsById = new();
    private readonly ServiceProvider _serviceProvider;
    private readonly TCPServer _tcpServer;

    public FriendshipsAndSpectateProcessorTests()
    {
        Log.Initialise(NullLogger.Instance);
        _mockRedis = _redisStore.CreateMockDatabase();

        DbContextOptions<MerrickContext> dbOptions = new DbContextOptionsBuilder<MerrickContext>()
            .UseInMemoryDatabase($"WaveBFriendshipsTests_{Guid.NewGuid():N}")
            .Options;
        _dbContext = new MerrickContext(dbOptions);

        _mockChatContext.SetupGet(c => c.ClientChatSessions).Returns(_clientSessions);
        _mockChatContext.SetupGet(c => c.MatchServerChatSessions).Returns(_matchServerSessions);
        _mockChatContext.SetupGet(c => c.ChatChannels).Returns(_chatChannels);
        _mockChatContext.SetupGet(c => c.ChatChannelsByID).Returns(_chatChannelsById);

        ServiceCollection services = new();
        services.AddSingleton(_mockRedis.Object);
        services.AddScoped(_ => new MerrickContext(dbOptions));
        services.AddSingleton(_mockChatContext.Object);
        services.AddSingleton(_mockDiscord.Object);
        services.AddSingleton(_mockMatchmaking.Object);
        services.AddSingleton(_mockDistributedState.Object);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<PeerInvalidationSubscriber>();
        _serviceProvider = services.BuildServiceProvider();

        _tcpServer = new TCPServer(IPAddress.Loopback, 0);
    }

    public void Dispose()
    {
        _tcpServer.Dispose();
        _dbContext.Dispose();
        _serviceProvider.Dispose();
    }

    private sealed class RecordingChatSession(TCPServer server, IServiceProvider serviceProvider)
        : ChatSession(server, serviceProvider)
    {
        public List<byte[]> SentPackets { get; } = [];

        public override bool SendAsync(ReadOnlySpan<byte> buffer)
        {
            SentPackets.Add(buffer.ToArray());
            return true;
        }
    }

    private static readonly DbRole SharedUserRole = new() { ID = 2, Name = "User" };

    private static Account CreateAccount(int id, string name, Clan? clan = null, AccountType type = AccountType.Normal, DbRole? role = null)
    {
        return new Account
        {
            ID = id,
            Name = name,
            Clan = clan,
            ClanID = clan?.ID,
            Type = type,
            AscensionLevel = 7,
            User = new User
            {
                ID = id,
                EmailAddress = $"{name.ToLowerInvariant()}@kongor.local",
                RoleID = (role ?? SharedUserRole).ID,
                Role = role ?? SharedUserRole,
                SRPPasswordSalt = "salt",
                SRPPasswordHash = "hash",
                PBKDF2PasswordHash = "hash"
            }
        };
    }

    private static ClientChatSessionMetadata CreateClientMetadata(
        string remoteIp = "192.168.1.50",
        ChatProtocol.ChatClientStatus state = ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_CONNECTED,
        ChatProtocol.ChatModeType mode = ChatProtocol.ChatModeType.CHAT_MODE_AVAILABLE,
        string? matchServerAddress = null,
        int? matchId = null,
        string? matchName = null)
    {
        return new ClientChatSessionMetadata
        {
            RemoteIP = remoteIp,
            SessionCookie = $"cookie-{Guid.NewGuid():N}",
            SessionAuthenticationHash = "hash",
            ChatProtocolVersion = 68,
            OperatingSystemIdentifier = 1,
            OperatingSystemVersionMajor = 10,
            OperatingSystemVersionMinor = 0,
            OperatingSystemVersionPatch = 0,
            OperatingSystemBuildCode = "Win",
            OperatingSystemArchitecture = "x64",
            ClientVersionMajor = 4,
            ClientVersionMinor = 10,
            ClientVersionPatch = 1,
            ClientVersionRevision = 0,
            LastKnownClientState = state,
            ClientChatModeState = mode,
            ClientRegion = "USE",
            ClientLanguage = "en",
            MatchServerAddress = matchServerAddress,
            MatchID = matchId,
            MatchName = matchName
        };
    }

    // protects: 0x00B8 PlayerSpectate wire layout (FUN_18069c600), [CLAN] stripping, Mentor mutual-friend requirement, and spectator capacity limit
    // fails_when: PSRR byte is written before spectateeName, trailing server-address bytes are appended, or non-friends are allowed to Mentor
    // seam: Real PlayerSpectate processor + ChatBuffer binary wire serialization + MerrickContext
    [Fact]
    public async Task PlayerSpectate_EnforcesExactWireLayout_MentorMutualFriendship_AndFullServerCapacity()
    {
        Clan clan = new() { ID = 10, Name = "Guardians", Tag = "GUCI" };
        Account targetAcc = CreateAccount(101, "StarCarry", clan);
        Account friendAcc = CreateAccount(102, "MutualBuddy");
        Account strangerAcc = CreateAccount(103, "RandomViewer");

        targetAcc.Friendships = [new Friendship { AccountID = 101, FriendAccountID = 102, Status = FriendshipStatus.Accepted }];
        friendAcc.Friendships = [new Friendship { AccountID = 102, FriendAccountID = 101, Status = FriendshipStatus.Accepted }];

        MatchServer matchServer = new()
        {
            ID = 500,
            HostAccountID = 1,
            HostAccountName = "Host",
            Name = "USE-1",
            Instance = 1,
            IPAddress = "203.0.113.25",
            LocalIPAddress = "192.168.1.99",
            Port = 11235,
            LocalPort = 11235,
            Location = "USE",
            Description = "Arena"
        };
        await _mockRedis.Object.SetMatchServer("Host", matchServer);
        await _mockRedis.Object.SetMatchStartData(new MatchStartData
        {
            MatchID = 9001,
            MatchName = "Caldavar Showdown",
            ServerID = 500,
            ServerName = "USE-1",
            HostAccountName = "Host",
            Map = "caldavar",
            Version = "4.10.1.0",
            IsCasual = false,
            MatchType = 1,
            MatchMode = "normal",
            ConnectedPlayersCount = 2,
            MaximumPlayersCount = 10
        });

        RecordingChatSession targetSession = new(_tcpServer, _serviceProvider)
        {
            Account = targetAcc,
            ClientMetadata = CreateClientMetadata("192.168.1.10", ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME, matchServerAddress: "203.0.113.25:11235", matchId: 9001, matchName: "Caldavar Showdown")
        };
        RecordingChatSession friendSession = new(_tcpServer, _serviceProvider)
        {
            Account = friendAcc,
            ClientMetadata = CreateClientMetadata("192.168.1.20")
        };
        RecordingChatSession strangerSession = new(_tcpServer, _serviceProvider)
        {
            Account = strangerAcc,
            ClientMetadata = CreateClientMetadata("198.51.100.44")
        };
        RecordingChatSession matchServerSession = new(_tcpServer, _serviceProvider)
        {
            Account = CreateAccount(1, "Host", type: AccountType.ServerHost),
            ServerMetadata = new MatchServerChatSessionMetadata
            {
                ServerID = 500,
                SessionCookie = "gs-cookie-500",
                ChatProtocolVersion = 68,
                Status = ChatProtocol.ServerStatus.SERVER_STATUS_ACTIVE,
                Address = "203.0.113.25",
                Port = 11235,
                LocalAddress = "192.168.1.99",
                LocalPort = 11235,
                MatchID = 9001,
                GameName = "Caldavar Showdown",
                MapName = "caldavar",
                GameMode = "normal",
                TeamSize = 5,
                ClientCount = 2,
                MaxSpectators = 10
            }
        };

        _clientSessions[targetAcc.Name] = targetSession;
        _clientSessions[friendAcc.Name] = friendSession;
        _clientSessions[strangerAcc.Name] = strangerSession;

        PlayerSpectate processor = new(_mockChatContext.Object, _mockRedis.Object, _dbContext);

        // 0. Fail-closed when live MatchServer session is absent -> PSRR_DENY
        ChatBuffer strangerSpectateReq = new();
        strangerSpectateReq.WriteCommand(ChatProtocol.Command.CHAT_CMD_PLAYER_SPECTATE_REQUEST);
        strangerSpectateReq.WriteInt8(0);
        strangerSpectateReq.WriteString("[GUCI]StarCarry");
        strangerSpectateReq.WriteInt8(0);
        strangerSpectateReq.WriteString(string.Empty);
        await processor.Process(strangerSession, new ChatBuffer(strangerSpectateReq.Data));

        strangerSession.SentPackets.Should().HaveCount(1);
        ChatBuffer missingServerDeny = new(strangerSession.SentPackets[0].AsSpan(2));
        missingServerDeny.ReadCommand().Should().Be(ChatProtocol.Command.CHAT_CMD_PLAYER_SPECTATE_REQUEST);
        missingServerDeny.ReadInt8().Should().Be(1);
        missingServerDeny.ReadString().Should().Be("StarCarry");
        missingServerDeny.ReadInt8().Should().Be((byte)ChatProtocol.PlayerSpectateRequestResponse.PSRR_DENY);
        strangerSession.SentPackets.Clear();

        // Register live MatchServer TCP session
        _matchServerSessions[500] = matchServerSession;

        // 1. Stranger requests Spectate (mentor=0) using [GUCI]StarCarry -> relayed to MatchServer first (0 premature PSRR_ALLOW), then MatchServer SubType=1 PSRR_ALLOW -> 0x000C WAN address + exact 0 trailing bytes
        await processor.Process(strangerSession, new ChatBuffer(strangerSpectateReq.Data));

        strangerSession.SentPackets.Should().BeEmpty("ChatServer must wait for MatchServer IGame+0x6b0 approval before sending PSRR_ALLOW");
        matchServerSession.SentPackets.Should().HaveCount(1);
        ChatBuffer gsRelayReader = new(matchServerSession.SentPackets[0].AsSpan(2));
        gsRelayReader.ReadCommand().Should().Be(ChatProtocol.Command.CHAT_CMD_PLAYER_SPECTATE_REQUEST);
        gsRelayReader.ReadInt8().Should().Be((byte)ChatProtocol.PlayerSpectateRequest.PLAYER_SPECTATE_REQUEST);
        gsRelayReader.ReadString().Should().Be("randomviewer");
        gsRelayReader.ReadString().Should().Be("starcarry");
        gsRelayReader.ReadInt8().Should().Be(0);

        // Client spoofing SubType=1 must be rejected
        ChatBuffer spoofedApprove = new();
        spoofedApprove.WriteCommand(ChatProtocol.Command.CHAT_CMD_PLAYER_SPECTATE_REQUEST);
        spoofedApprove.WriteInt8((byte)ChatProtocol.PlayerSpectateRequest.PLAYER_SPECTATE_REQUEST_RESPONSE);
        spoofedApprove.WriteString("randomviewer");
        spoofedApprove.WriteString("starcarry");
        spoofedApprove.WriteInt8((byte)ChatProtocol.PlayerSpectateRequestResponse.PSRR_ALLOW);
        await processor.Process(strangerSession, new ChatBuffer(spoofedApprove.Data));
        strangerSession.SentPackets.Should().BeEmpty("Client sessions must not be permitted to forge SubType=1 responses");

        // MatchServer sends SubType=1 PSRR_ALLOW with lowercased names (matching k2_x64.dll 0x9841c0 LowerString behavior)
        await processor.Process(matchServerSession, new ChatBuffer(spoofedApprove.Data));

        strangerSession.SentPackets.Should().HaveCount(2);
        ChatBuffer statusHydration = new(strangerSession.SentPackets[0].AsSpan(2));
        statusHydration.ReadCommand().Should().Be(ChatProtocol.Command.CHAT_CMD_UPDATE_STATUS);

        byte[] allowPayload = strangerSession.SentPackets[1].AsSpan(2).ToArray();
        ChatBuffer allowReader = new(allowPayload);
        allowReader.ReadCommand().Should().Be(ChatProtocol.Command.CHAT_CMD_PLAYER_SPECTATE_REQUEST);
        allowReader.ReadInt8().Should().Be((byte)ChatProtocol.PlayerSpectateRequest.PLAYER_SPECTATE_REQUEST_RESPONSE);
        allowReader.ReadString().Should().Be("StarCarry");
        allowReader.ReadInt8().Should().Be((byte)ChatProtocol.PlayerSpectateRequestResponse.PSRR_ALLOW);
        // Exact wire length check: 2 (cmd) + 1 (subType) + ("StarCarry".Length + 1 null) + 1 (result) = 14 bytes (0 trailing bytes!)
        allowPayload.Length.Should().Be(2 + 1 + ("StarCarry".Length + 1) + 1);

        // 2. Stranger requests Mentor (mentor=1) -> immediate PSRR_DENY without relaying to MatchServer
        strangerSession.SentPackets.Clear();
        matchServerSession.SentPackets.Clear();
        ChatBuffer strangerMentorReq = new();
        strangerMentorReq.WriteCommand(ChatProtocol.Command.CHAT_CMD_PLAYER_SPECTATE_REQUEST);
        strangerMentorReq.WriteInt8(0);
        strangerMentorReq.WriteString("StarCarry");
        strangerMentorReq.WriteInt8(1);
        strangerMentorReq.WriteString(string.Empty);
        await processor.Process(strangerSession, new ChatBuffer(strangerMentorReq.Data));

        matchServerSession.SentPackets.Should().BeEmpty();
        strangerSession.SentPackets.Should().HaveCount(1);
        ChatBuffer denyReader = new(strangerSession.SentPackets[0].AsSpan(2));
        denyReader.ReadCommand().Should().Be(ChatProtocol.Command.CHAT_CMD_PLAYER_SPECTATE_REQUEST);
        denyReader.ReadInt8().Should().Be((byte)ChatProtocol.PlayerSpectateRequest.PLAYER_SPECTATE_REQUEST_RESPONSE);
        denyReader.ReadString().Should().Be("StarCarry");
        denyReader.ReadInt8().Should().Be((byte)ChatProtocol.PlayerSpectateRequestResponse.PSRR_DENY);

        // 3. Mutual friend requests Mentor (mentor=1) on same LAN using lowercase "[guci]starcarry" (exact native ChatSpectatePlayer wire behavior)
        // -> relayed with mentor=1 -> MatchServer PSRR_ALLOW -> LAN split-horizon 0x000C + exact caller casing "starcarry" echoed in 0x00B8 SubType=1!
        ChatBuffer friendMentorReq = new();
        friendMentorReq.WriteCommand(ChatProtocol.Command.CHAT_CMD_PLAYER_SPECTATE_REQUEST);
        friendMentorReq.WriteInt8(0);
        friendMentorReq.WriteString("[guci]starcarry");
        friendMentorReq.WriteInt8(1);
        friendMentorReq.WriteString(string.Empty);
        await processor.Process(friendSession, new ChatBuffer(friendMentorReq.Data));

        matchServerSession.SentPackets.Should().HaveCount(1);
        ChatBuffer mentorRelayReader = new(matchServerSession.SentPackets[0].AsSpan(2));
        mentorRelayReader.ReadCommand().Should().Be(ChatProtocol.Command.CHAT_CMD_PLAYER_SPECTATE_REQUEST);
        mentorRelayReader.ReadInt8().Should().Be(0);
        mentorRelayReader.ReadString().Should().Be("mutualbuddy");
        mentorRelayReader.ReadString().Should().Be("starcarry");
        mentorRelayReader.ReadInt8().Should().Be(1);

        ChatBuffer gsMentorAllow = new();
        gsMentorAllow.WriteCommand(ChatProtocol.Command.CHAT_CMD_PLAYER_SPECTATE_REQUEST);
        gsMentorAllow.WriteInt8(1);
        gsMentorAllow.WriteString("mutualbuddy");
        gsMentorAllow.WriteString("starcarry");
        gsMentorAllow.WriteInt8((byte)ChatProtocol.PlayerSpectateRequestResponse.PSRR_ALLOW);
        await processor.Process(matchServerSession, new ChatBuffer(gsMentorAllow.Data));

        friendSession.SentPackets.Should().HaveCount(2);
        ChatBuffer lanHydration = new(friendSession.SentPackets[0].AsSpan(2));
        lanHydration.ReadCommand().Should().Be(ChatProtocol.Command.CHAT_CMD_UPDATE_STATUS);
        lanHydration.ReadInt32().Should().Be(101);
        lanHydration.ReadInt8();
        lanHydration.ReadInt8();
        lanHydration.ReadInt32();
        lanHydration.ReadString();
        lanHydration.ReadString();
        lanHydration.ReadString();
        lanHydration.ReadString();
        lanHydration.ReadString().Should().Be("192.168.1.99:11235");

        ChatBuffer friendAllowReader = new(friendSession.SentPackets[1].AsSpan(2));
        friendAllowReader.ReadCommand().Should().Be(ChatProtocol.Command.CHAT_CMD_PLAYER_SPECTATE_REQUEST);
        friendAllowReader.ReadInt8().Should().Be(1);
        friendAllowReader.ReadString().Should().Be("starcarry", "k2_x64.dll 0x18069c600 requires exact caller-cased spectateeName to match CHost+0x150");
        friendAllowReader.ReadInt8().Should().Be((byte)ChatProtocol.PlayerSpectateRequestResponse.PSRR_ALLOW);

        // 4. Spectator capacity full -> PSRR_FULL_SERVER echoes exact caller casing "starcarry"
        await _mockRedis.Object.SetMatchStartData(new MatchStartData
        {
            MatchID = 9001,
            MatchName = "Caldavar Showdown",
            ServerID = 500,
            ServerName = "USE-1",
            HostAccountName = "Host",
            Map = "caldavar",
            Version = "4.10.1.0",
            IsCasual = false,
            MatchType = 1,
            MatchMode = "normal",
            ConnectedPlayersCount = 20, // 10 players + 10 max spectators
            MaximumPlayersCount = 10
        });
        friendSession.SentPackets.Clear();
        await processor.Process(friendSession, new ChatBuffer(friendMentorReq.Data));

        friendSession.SentPackets.Should().HaveCount(1);
        ChatBuffer fullReader = new(friendSession.SentPackets[0].AsSpan(2));
        fullReader.ReadCommand().Should().Be(ChatProtocol.Command.CHAT_CMD_PLAYER_SPECTATE_REQUEST);
        fullReader.ReadInt8().Should().Be(1);
        fullReader.ReadString().Should().Be("starcarry");
        fullReader.ReadInt8().Should().Be((byte)ChatProtocol.PlayerSpectateRequestResponse.PSRR_FULL_SERVER);
    }

    // protects: 0x00B5 RequestGameInfo 20-field wire contract (FUN_18069de90) and watch_user_v2.package empty-slot / texture invariants
    // fails_when: param0 mismatches stripped {user}, empty player slots omit 7-token pipe delimiters, or non-VFS hero tokens trigger $checker
    // seam: Real ServerStatus + RequestGameInfo processors + ChatBuffer binary wire roundtrip
    [Fact]
    public async Task RequestGameInfo_SerializesAllTwentyFields_AndNormalizesEmptyAndNonVfsPlayerSlots()
    {
        Clan clan = new() { ID = 5, Name = "Punks", Tag = "PUNK" };
        Account watchedAcc = CreateAccount(201, "WatchedHero", clan);
        Account watcherAcc = CreateAccount(202, "WatcherFriend");

        RecordingChatSession watchedSession = new(_tcpServer, _serviceProvider)
        {
            Account = watchedAcc,
            ClientMetadata = CreateClientMetadata(state: ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME, matchServerAddress: "127.0.0.1:11235", matchId: 4444, matchName: "MidWars Mayhem")
        };
        RecordingChatSession watcherSession = new(_tcpServer, _serviceProvider)
        {
            Account = watcherAcc,
            ClientMetadata = CreateClientMetadata()
        };
        RecordingChatSession matchServerSession = new(_tcpServer, _serviceProvider)
        {
            ServerMetadata = new MatchServerChatSessionMetadata
            {
                ServerID = 77,
                SessionCookie = "srv-cookie",
                ChatProtocolVersion = 68,
                MatchID = 4444,
                GameName = "MidWars Mayhem",
                MapName = "midwars",
                GameMode = "normal",
                CurrentGameTime = 185000, // 03:05
                CasualMode = true,
                Official = 2,
                Gated = false,
                LegionTeamInformation = "1|62|11|9|3|3|0.95|12|5|18",
                HellbourneTeamInformation = "2|38|11|7|3|2|0.80|5|12|9",
                PlayerInformation =
                [
                    "201|[PUNK]WatchedHero|Hero_Armadon|8|5|1|4",
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    "299|EnemyMid|/heroes/pyromancer/icon.tga|7|2|3|1",
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty
                ]
            }
        };

        _clientSessions[watchedAcc.Name] = watchedSession;
        _clientSessions[watcherAcc.Name] = watcherSession;
        _matchServerSessions[77] = matchServerSession;

        RequestGameInfo processor = new(_mockChatContext.Object, _mockRedis.Object);

        ChatBuffer req = new();
        req.WriteCommand(ChatProtocol.Command.CHAT_CMD_REQUEST_GAME_INFO);
        req.WriteString("[PUNK]WatchedHero");
        await processor.Process(watcherSession, new ChatBuffer(req.Data));

        watcherSession.SentPackets.Should().HaveCount(1);
        ChatBuffer resp = new(watcherSession.SentPackets[0].AsSpan(2));
        resp.ReadCommand().Should().Be(ChatProtocol.Command.CHAT_CMD_REQUEST_GAME_INFO);
        resp.ReadString().Should().Be("WatchedHero"); // param0 matches {user}
        resp.ReadString().Should().Be("MidWars Mayhem"); // param13
        resp.ReadString().Should().Be("midwars"); // param14
        resp.ReadInt8().Should().Be(1); // casual
        resp.ReadInt8().Should().Be(2); // official
        resp.ReadInt8().Should().Be(0); // gated
        resp.ReadString().Should().BeEmpty(); // param15 safe fallback
        resp.ReadString().Should().Be("03:05"); // param16
        resp.ReadString().Should().Be("1|62|11|9|3|3|0.95|12|5|18"); // param1
        resp.ReadString().Should().Be("2|38|11|7|3|2|0.80|5|12|9"); // param2

        string slot0 = resp.ReadString();
        slot0.Should().Be("201|[PUNK]WatchedHero|/ui/elements/crest_legion.tga|8|5|1|4");
        resp.ReadString().Should().Be("0|||0|0|0|0"); // slot1 hidden cleanly
        resp.ReadString().Should().Be("0|||0|0|0|0"); // slot2
        resp.ReadString().Should().Be("0|||0|0|0|0"); // slot3
        resp.ReadString().Should().Be("0|||0|0|0|0"); // slot4
        resp.ReadString().Should().Be("299|EnemyMid|/heroes/pyromancer/icon.tga|7|2|3|1"); // slot5 preserves valid VFS texture
        resp.ReadString().Should().Be("0|||0|0|0|0");
        resp.ReadString().Should().Be("0|||0|0|0|0");
        resp.ReadString().Should().Be("0|||0|0|0|0");
        resp.ReadString().Should().Be("0|||0|0|0|0");
    }

    // protects: Game invites (0x0023, 0x0024 -> 0x0025, 0x0026, 0x0027, 0x0028) zero-payload error frames & split-horizon address
    // fails_when: 0x0026 or 0x0027 writes trailing strings (corrupting CChatManager stream) or 0x0025 omits matchName/matchId while IN_GAME
    // seam: Real InviteUserByName, InviteUserById, RejectServerInvite + ChatBuffer framing
    [Fact]
    public async Task ServerInvites_DeliverExactInvitedPacket_ZeroByteFailures_AndRejectRelay()
    {
        Account inviterAcc = CreateAccount(301, "HostPlayer");
        Account inviteeAcc = CreateAccount(302, "InvitedBuddy");

        RecordingChatSession inviterSession = new(_tcpServer, _serviceProvider)
        {
            Account = inviterAcc,
            ClientMetadata = CreateClientMetadata("127.0.0.1") // Initially NOT in game
        };
        RecordingChatSession inviteeSession = new(_tcpServer, _serviceProvider)
        {
            Account = inviteeAcc,
            ClientMetadata = CreateClientMetadata("127.0.0.1")
        };

        _clientSessions[inviterAcc.Name] = inviterSession;
        _clientSessions[inviteeAcc.Name] = inviteeSession;

        InviteUserByName byName = new(_mockChatContext.Object, _mockRedis.Object);
        InviteUserById byId = new(_mockChatContext.Object, _mockRedis.Object);
        RejectServerInvite reject = new(_mockChatContext.Object);

        // 1. Inviter not in game -> 0x0027 (2 bytes total payload = uint16 command ONLY!)
        ChatBuffer reqNotInGame = new();
        reqNotInGame.WriteCommand(ChatProtocol.Command.CHAT_CMD_INVITE_USER_NAME);
        reqNotInGame.WriteString("InvitedBuddy");
        await byName.Process(inviterSession, new ChatBuffer(reqNotInGame.Data));

        inviterSession.SentPackets.Should().ContainSingle();
        byte[] failGamePayload = inviterSession.SentPackets[0].AsSpan(2).ToArray();
        failGamePayload.Length.Should().Be(2);
        new ChatBuffer(failGamePayload).ReadCommand().Should().Be(ChatProtocol.Command.CHAT_CMD_INVITE_FAILED_GAME);

        // 2. Inviter IN_GAME invites unknown user -> 0x0026 (2 bytes total payload = uint16 command ONLY!)
        inviterSession.SentPackets.Clear();
        inviterSession.ClientMetadata.LastKnownClientState = ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME;
        inviterSession.ClientMetadata.MatchServerAddress = "127.0.0.1:11235";
        inviterSession.ClientMetadata.MatchID = 8181;
        inviterSession.ClientMetadata.MatchName = "Hosted Lobby #1";

        ChatBuffer reqUnknownUser = new();
        reqUnknownUser.WriteCommand(ChatProtocol.Command.CHAT_CMD_INVITE_USER_ID);
        reqUnknownUser.WriteInt32(999999);
        await byId.Process(inviterSession, new ChatBuffer(reqUnknownUser.Data));

        inviterSession.SentPackets.Should().ContainSingle();
        byte[] failUserPayload = inviterSession.SentPackets[0].AsSpan(2).ToArray();
        failUserPayload.Length.Should().Be(2);
        new ChatBuffer(failUserPayload).ReadCommand().Should().Be(ChatProtocol.Command.CHAT_CMD_INVITE_FAILED_USER);

        // 3. Inviter IN_GAME invites online friend -> invitee receives 0x0025 with MatchName + MatchID + AscensionLevel
        ChatBuffer reqValid = new();
        reqValid.WriteCommand(ChatProtocol.Command.CHAT_CMD_INVITE_USER_NAME);
        reqValid.WriteString("InvitedBuddy");
        await byName.Process(inviterSession, new ChatBuffer(reqValid.Data));

        inviteeSession.SentPackets.Should().ContainSingle();
        ChatBuffer invitedReader = new(inviteeSession.SentPackets[0].AsSpan(2));
        invitedReader.ReadCommand().Should().Be(ChatProtocol.Command.CHAT_CMD_INVITED_TO_SERVER);
        invitedReader.ReadString().Should().Be("HostPlayer");
        invitedReader.ReadInt32().Should().Be(301);
        invitedReader.ReadInt8().Should().Be((byte)ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME);
        invitedReader.ReadInt8(); // flags
        invitedReader.ReadString(); // color
        invitedReader.ReadString(); // icon
        invitedReader.ReadString().Should().Be("127.0.0.1:11235");
        invitedReader.ReadString().Should().Be("Hosted Lobby #1");
        invitedReader.ReadInt32().Should().Be(8181);
        invitedReader.ReadInt32().Should().Be(7); // AscensionLevel

        // 4. Invitee rejects (0x0028) -> relayed to inviter
        inviterSession.SentPackets.Clear();
        ChatBuffer rejectReq = new();
        rejectReq.WriteCommand(ChatProtocol.Command.CHAT_CMD_INVITE_REJECTED);
        rejectReq.WriteInt32(301);
        reject.Process(inviteeSession, new ChatBuffer(rejectReq.Data));

        inviterSession.SentPackets.Should().ContainSingle();
        ChatBuffer rejectRelay = new(inviterSession.SentPackets[0].AsSpan(2));
        rejectRelay.ReadCommand().Should().Be(ChatProtocol.Command.CHAT_CMD_INVITE_REJECTED);
        rejectRelay.ReadString().Should().Be("InvitedBuddy");
        rejectRelay.ReadInt32().Should().Be(302);
    }

    // protects: Invisible mode privacy across SetChatMode (0x0066), GetUserStatus (0x0C05), and GetUserInfo (0x002A) + restoration to Available
    // fails_when: Going Invisible overwrites LastKnownClientState with DISCONNECTED or leaks online state in 0x0C05 / 0x002A
    // seam: Real SetChatMode + GetUserStatus + GetUserInfo + ChatSession.BroadcastInvisibleDisconnect
    [Fact]
    public async Task InvisibleMode_MasksPresenceInStatusAndWhois_AndRestoresRealStateOnAvailable()
    {
        Account aliceAcc = CreateAccount(401, "AlicePriv");
        Account bobAcc = CreateAccount(402, "BobObserver");
        bobAcc.Friendships = [new Friendship { AccountID = 402, FriendAccountID = 401, Status = FriendshipStatus.Accepted }];
        aliceAcc.Friendships = [new Friendship { AccountID = 401, FriendAccountID = 402, Status = FriendshipStatus.Accepted }];

        RecordingChatSession aliceSession = new(_tcpServer, _serviceProvider)
        {
            Account = aliceAcc,
            ClientMetadata = CreateClientMetadata(state: ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_CONNECTED)
        };
        RecordingChatSession bobSession = new(_tcpServer, _serviceProvider)
        {
            Account = bobAcc,
            ClientMetadata = CreateClientMetadata()
        };

        _clientSessions[aliceAcc.Name] = aliceSession;
        _clientSessions[bobAcc.Name] = bobSession;

        ChatChannel general = ChatChannel.GetOrCreate(_mockChatContext.Object, aliceSession, "General");
        general.Join(aliceSession);
        aliceSession.SentPackets.Clear();
        bobSession.SentPackets.Clear();

        SetChatMode setMode = new();
        GetUserStatus getStatus = new(_mockChatContext.Object, _mockDistributedState.Object);
        GetUserInfo getUserInfo = new(_mockChatContext.Object, _dbContext, _mockRedis.Object);

        // 1. Online /whois returns 0x002D with General channel
        ChatBuffer whoisReq = new();
        whoisReq.WriteCommand(ChatProtocol.Command.CHAT_CMD_USER_INFO);
        whoisReq.WriteString("AlicePriv");
        await getUserInfo.Process(bobSession, new ChatBuffer(whoisReq.Data));

        ChatBuffer whoisOnline = new(bobSession.SentPackets[^1].AsSpan(2));
        whoisOnline.ReadCommand().Should().Be(ChatProtocol.Command.CHAT_CMD_USER_INFO_ONLINE);
        whoisOnline.ReadString().Should().Be("AlicePriv");
        whoisOnline.ReadInt32().Should().Be(1);
        whoisOnline.ReadString().Should().Be("General");

        // 2. Alice enters CHAT_MODE_INVISIBLE -> Bob gets DISCONNECTED, Alice's LastKnownClientState stays CONNECTED
        bobSession.SentPackets.Clear();
        ChatBuffer invisReq = new();
        invisReq.WriteCommand(ChatProtocol.Command.CHAT_CMD_SET_CHAT_MODE_TYPE);
        invisReq.WriteInt8((byte)ChatProtocol.ChatModeType.CHAT_MODE_INVISIBLE);
        invisReq.WriteString(string.Empty);
        setMode.Process(aliceSession, new ChatBuffer(invisReq.Data));

        aliceSession.ClientMetadata.LastKnownClientState.Should().Be(ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_CONNECTED);
        bobSession.SentPackets.Should().ContainSingle();
        ChatBuffer bobDisconnect = new(bobSession.SentPackets[0].AsSpan(2));
        bobDisconnect.ReadCommand().Should().Be(ChatProtocol.Command.CHAT_CMD_UPDATE_STATUS);
        bobDisconnect.ReadInt32().Should().Be(401);
        bobDisconnect.ReadInt8().Should().Be((byte)ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_DISCONNECTED);

        // 3. GetUserStatus & GetUserInfo from Bob both mask Alice as offline/disconnected
        bobSession.SentPackets.Clear();
        ChatBuffer statusReq = new();
        statusReq.WriteCommand(ChatProtocol.ClientToChatServer.NET_CHAT_CL_GET_USER_STATUS);
        statusReq.WriteInt32(401);
        await getStatus.Process(bobSession, new ChatBuffer(statusReq.Data));

        ChatBuffer statusResp = new(bobSession.SentPackets[0].AsSpan(2));
        statusResp.ReadCommand().Should().Be(ChatProtocol.ChatServerToClient.NET_CHAT_CL_USER_STATUS);
        statusResp.ReadInt32().Should().Be(401);
        statusResp.ReadInt8().Should().Be((byte)ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_DISCONNECTED);

        bobSession.SentPackets.Clear();
        await getUserInfo.Process(bobSession, new ChatBuffer(whoisReq.Data));
        new ChatBuffer(bobSession.SentPackets[0].AsSpan(2)).ReadCommand()
            .Should().Be(ChatProtocol.Command.CHAT_CMD_USER_INFO_OFFLINE);

        // 4. Alice returns to CHAT_MODE_AVAILABLE -> Bob immediately receives CONNECTED (3), NOT DISCONNECTED (0)!
        bobSession.SentPackets.Clear();
        ChatBuffer availReq = new();
        availReq.WriteCommand(ChatProtocol.Command.CHAT_CMD_SET_CHAT_MODE_TYPE);
        availReq.WriteInt8((byte)ChatProtocol.ChatModeType.CHAT_MODE_AVAILABLE);
        availReq.WriteString(string.Empty);
        setMode.Process(aliceSession, new ChatBuffer(availReq.Data));

        bobSession.SentPackets.Should().ContainSingle();
        ChatBuffer bobReconnect = new(bobSession.SentPackets[0].AsSpan(2));
        bobReconnect.ReadCommand().Should().Be(ChatProtocol.Command.CHAT_CMD_UPDATE_STATUS);
        bobReconnect.ReadInt32().Should().Be(401);
        bobReconnect.ReadInt8().Should().Be((byte)ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_CONNECTED);
    }

    // protects: Live PeerInvalidationSubscriber AsNoTracking() reload + RemoveFriend (0x000E) bidirectional eviction
    // fails_when: Ignoring a user or removing a friend leaves stale Account.Peers / Account.Friendships on active ChatSessions
    // seam: Real PeerInvalidationSubscriber + RemoveFriend + InviteUserByName + MerrickContext
    [Fact]
    public async Task PeerInvalidationAndRemoveFriend_ReloadLiveCollections_BlockInvites_AndUnlinkFriends()
    {
        DbRole trackedRole = new() { ID = 2, Name = "User" };
        Account accA = CreateAccount(501, "PlayerAlpha", role: trackedRole);
        Account accB = CreateAccount(502, "PlayerBravo", role: trackedRole);
        _dbContext.Accounts.AddRange(accA, accB);
        _dbContext.Friendships.AddRange(
            new Friendship { AccountID = 501, FriendAccountID = 502, FriendGroup = "General", Status = FriendshipStatus.Accepted },
            new Friendship { AccountID = 502, FriendAccountID = 501, FriendGroup = "General", Status = FriendshipStatus.Accepted });
        await _dbContext.SaveChangesAsync();

        RecordingChatSession sessionA = new(_tcpServer, _serviceProvider)
        {
            Account = CreateAccount(501, "PlayerAlpha"),
            ClientMetadata = CreateClientMetadata(state: ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME, matchServerAddress: "127.0.0.1:11235", matchId: 700, matchName: "Match 700")
        };
        RecordingChatSession sessionB = new(_tcpServer, _serviceProvider)
        {
            Account = CreateAccount(502, "PlayerBravo"),
            ClientMetadata = CreateClientMetadata()
        };
        _clientSessions[sessionA.Account.Name] = sessionA;
        _clientSessions[sessionB.Account.Name] = sessionB;

        PeerInvalidationSubscriber subscriber = _serviceProvider.GetRequiredService<PeerInvalidationSubscriber>();

        // 1. Friend Added invalidation populates both live sessions' Account.Friendships and pushes mutual 0x000C
        await subscriber.ProcessInvalidationPayloadAsync(
            """{"AccountId":501,"PeerAccountId":502,"Type":"Friend","Action":"Added"}""");

        sessionA.Account.Friendships.Should().ContainSingle(f => f.FriendAccountID == 502);
        sessionB.Account.Friendships.Should().ContainSingle(f => f.FriendAccountID == 501);
        sessionB.SentPackets.Should().ContainSingle();

        // 2. Bravo ignores Alpha in DB and publishes Ignored Added -> live sessionB.Account.Peers reloads immediately and blocks Alpha's invite
        _dbContext.AccountPeers.Add(new AccountPeer
        {
            AccountID = 502,
            PeerAccountID = 501,
            Type = PeerType.Ignored,
            TimestampCreated = TimeProvider.System.GetUtcNow(),
            TimestampExpires = TimeProvider.System.GetUtcNow().AddMonths(6)
        });
        await _dbContext.SaveChangesAsync();

        await subscriber.ProcessInvalidationPayloadAsync(
            """{"AccountId":502,"PeerAccountId":501,"Type":"Ignored","Action":"Added"}""");

        sessionB.Account.HasIgnoredPeer(501).Should().BeTrue();

        sessionA.SentPackets.Clear();
        InviteUserByName inviteProcessor = new(_mockChatContext.Object, _mockRedis.Object);
        ChatBuffer inviteBuf = new();
        inviteBuf.WriteCommand(ChatProtocol.Command.CHAT_CMD_INVITE_USER_NAME);
        inviteBuf.WriteString("PlayerBravo");
        await inviteProcessor.Process(sessionA, new ChatBuffer(inviteBuf.Data));

        sessionA.SentPackets.Should().ContainSingle();
        new ChatBuffer(sessionA.SentPackets[0].AsSpan(2)).ReadCommand()
            .Should().Be(ChatProtocol.Command.CHAT_CMD_INVITE_FAILED_USER);

        // 3. RemoveFriend (0x000E) removes DB rows, evicts in-memory Friendships, and pushes DISCONNECTED to both
        sessionA.SentPackets.Clear();
        sessionB.SentPackets.Clear();
        RemoveFriend removeProcessor = new(_mockChatContext.Object, _dbContext);
        ChatBuffer removeBuf = new();
        removeBuf.WriteCommand(ChatProtocol.Command.CHAT_CMD_NOTIFY_BUDDY_REMOVE);
        removeBuf.WriteInt32(502);
        removeBuf.WriteInt32(111);
        removeBuf.WriteInt32(222);
        await removeProcessor.Process(sessionA, new ChatBuffer(removeBuf.Data));

        sessionA.Account.Friendships.Should().BeEmpty();
        sessionB.Account.Friendships.Should().BeEmpty();
        (await _dbContext.Friendships.CountAsync()).Should().Be(0);
        sessionA.SentPackets.Should().ContainSingle();
        sessionB.SentPackets.Should().ContainSingle();
    }

    private sealed class InMemoryRedisHashStore
    {
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, RedisValue>> _hashes = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, RedisValue> _strings = new(StringComparer.Ordinal);

        public Mock<IDatabase> CreateMockDatabase()
        {
            Mock<IDatabase> mock = new();

            mock.Setup(db => db.HashSetAsync(It.IsAny<RedisKey>(), It.IsAny<HashEntry[]>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey key, HashEntry[] entries, CommandFlags _) =>
                {
                    ConcurrentDictionary<string, RedisValue> hash = _hashes.GetOrAdd(key.ToString(), _ => new ConcurrentDictionary<string, RedisValue>(StringComparer.Ordinal));
                    foreach (HashEntry entry in entries)
                    {
                        hash[entry.Name.ToString()] = entry.Value;
                    }
                    return Task.CompletedTask;
                });

            mock.Setup(db => db.HashSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey key, RedisValue field, RedisValue val, When _, CommandFlags _) =>
                {
                    ConcurrentDictionary<string, RedisValue> hash = _hashes.GetOrAdd(key.ToString(), _ => new ConcurrentDictionary<string, RedisValue>(StringComparer.Ordinal));
                    hash[field.ToString()] = val;
                    return Task.FromResult(true);
                });

            mock.Setup(db => db.HashGetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey key, RedisValue field, CommandFlags _) =>
                {
                    if (_hashes.TryGetValue(key.ToString(), out ConcurrentDictionary<string, RedisValue>? hash) &&
                        hash.TryGetValue(field.ToString(), out RedisValue val))
                    {
                        return Task.FromResult(val);
                    }
                    return Task.FromResult(RedisValue.Null);
                });

            mock.Setup(db => db.HashGetAllAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey key, CommandFlags _) =>
                {
                    if (!_hashes.TryGetValue(key.ToString(), out ConcurrentDictionary<string, RedisValue>? hash))
                    {
                        return Task.FromResult(Array.Empty<HashEntry>());
                    }

                    HashEntry[] entries = hash.Select(kvp => new HashEntry(kvp.Key, kvp.Value)).ToArray();
                    return Task.FromResult(entries);
                });

            mock.Setup(db => db.HashDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey key, RedisValue field, CommandFlags _) =>
                {
                    bool removed = _hashes.TryGetValue(key.ToString(), out ConcurrentDictionary<string, RedisValue>? hash) &&
                                   hash.TryRemove(field.ToString(), out RedisValue _);
                    return Task.FromResult(removed);
                });

            mock.Setup(db => db.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey key, RedisValue val, TimeSpan? _, When _, CommandFlags _) =>
                {
                    _strings[key.ToString()] = val;
                    return Task.FromResult(true);
                });

            mock.Setup(db => db.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey key, RedisValue val, TimeSpan? _, bool _, When _, CommandFlags _) =>
                {
                    _strings[key.ToString()] = val;
                    return Task.FromResult(true);
                });

            mock.Setup(db => db.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<Expiration>(), It.IsAny<ValueCondition>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey key, RedisValue val, Expiration _, ValueCondition _, CommandFlags _) =>
                {
                    _strings[key.ToString()] = val;
                    return Task.FromResult(true);
                });

            mock.Setup(db => db.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey key, CommandFlags _) =>
                {
                    return Task.FromResult(_strings.TryGetValue(key.ToString(), out RedisValue val) ? val : RedisValue.Null);
                });

            return mock;
        }
    }
}
