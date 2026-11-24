// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Net;
using FluentAssertions;
using Kongor.Core.Services.Events;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Enumerations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NEXUS.Contracts.Events;
using StackExchange.Redis;
using TRANSMUTANSTEIN.ChatServer.Domain.Clans;
using TRANSMUTANSTEIN.ChatServer.Domain.Communication;
using TRANSMUTANSTEIN.ChatServer.Domain.Core;
using TRANSMUTANSTEIN.ChatServer.Domain.Matchmaking;
using TRANSMUTANSTEIN.ChatServer.Extensions;
using TRANSMUTANSTEIN.ChatServer.Internals;
using TRANSMUTANSTEIN.ChatServer.Services;
using TRANSMUTANSTEIN.ChatServer.Services.Events;
using TRANSMUTANSTEIN.ChatServer.Utilities;
using Xunit;
using DbRole = MERRICK.DatabaseContext.Entities.Utility.Role;

namespace TRANSMUTANSTEIN.ChatServer.Tests.Events;

public sealed class ChatServerEventHandlerTests
{
    private static Account CreateAccount(int id, string name)
    {
        return new Account
        {
            ID = id,
            Name = name,
            User = new User
            {
                ID = id,
                EmailAddress = $"{name.ToLowerInvariant()}@kongor.net",
                Role = new DbRole { ID = 2, Name = "User" },
                SRPPasswordSalt = "salt",
                SRPPasswordHash = "hash",
                PBKDF2PasswordHash = "hash"
            }
        };
    }

    private static ClientChatSessionMetadata CreateMetadata(string sessionCookie)
    {
        return new ClientChatSessionMetadata
        {
            RemoteIP = "127.0.0.1",
            SessionCookie = sessionCookie,
            SessionAuthenticationHash = "hash123",
            ChatProtocolVersion = 68,
            OperatingSystemIdentifier = 1,
            OperatingSystemVersionMajor = 10,
            OperatingSystemVersionMinor = 0,
            OperatingSystemVersionPatch = 0,
            OperatingSystemBuildCode = "Windows",
            OperatingSystemArchitecture = "x64",
            ClientVersionMajor = 4,
            ClientVersionMinor = 10,
            ClientVersionPatch = 1,
            ClientVersionRevision = 0,
            LastKnownClientState = ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_CONNECTED,
            ClientChatModeState = ChatProtocol.ChatModeType.CHAT_MODE_AVAILABLE,
            ClientRegion = "en",
            ClientLanguage = "en"
        };
    }

    private static (IServiceProvider ServiceProvider, TCPServer TcpServer, ConcurrentDictionary<string, ChatSession> ClientSessions) CreateSessionHarness()
    {
        Log.Initialise(NullLogger.Instance);

        ConcurrentDictionary<string, ChatSession> clientSessions = new();
        Mock<IChatContext> mockChatContext = new();
        mockChatContext.SetupGet(c => c.ClientChatSessions).Returns(clientSessions);
        mockChatContext.SetupGet(c => c.ChatChannels).Returns(new ConcurrentDictionary<string, ChatChannel>());
        mockChatContext.SetupGet(c => c.ChatChannelsByID).Returns(new ConcurrentDictionary<int, ChatChannel>());

        Mock<IDiscordPresencePublisherService> mockDiscord = new();
        Mock<IMatchmakingService> mockMatchmaking = new();

        ServiceCollection services = new();
        services.AddSingleton(mockChatContext.Object);
        services.AddSingleton(mockDiscord.Object);
        services.AddSingleton(mockMatchmaking.Object);
        services.AddSingleton<ILogger<ChatSession>>(NullLogger<ChatSession>.Instance);

        return (services.BuildServiceProvider(), new TCPServer(IPAddress.Loopback, 0), clientSessions);
    }

    [Fact]
    public async Task ChatServerEventHandler_ChatServerMatchCompletedEventHandler_ShouldQueryMatchmakingGroupsForAccounts()
    {
        // Arrange
        Mock<IMatchmakingService> mockMatchmaking = new();
        mockMatchmaking
            .Setup(m => m.GetMatchmakingGroupByMemberID(It.IsAny<int>()))
            .Returns((MatchmakingGroup?)null);

        ChatServerMatchCompletedEventHandler handler = new(
            mockMatchmaking.Object,
            NullLogger<ChatServerMatchCompletedEventHandler>.Instance);

        MatchCompletedEvent matchEvent = new(
            8888,
            [100, 200, 300],
            1,
            TimeSpan.FromMinutes(30),
            "ranked"
        );

        // Act
        Func<Task> act = async () => await handler.HandleAsync(matchEvent);

        // Assert
        await act.Should().NotThrowAsync();
        mockMatchmaking.Verify(m => m.GetMatchmakingGroupByMemberID(100), Times.Once);
        mockMatchmaking.Verify(m => m.GetMatchmakingGroupByMemberID(200), Times.Once);
        mockMatchmaking.Verify(m => m.GetMatchmakingGroupByMemberID(300), Times.Once);
    }

    [Fact]
    public async Task ChatServerEventHandler_ChatServerAccountBannedEventHandler_ShouldHandleGracefully()
    {
        // Arrange
        Mock<IChatContext> mockChatContext = new();
        mockChatContext
            .Setup(c => c.ClientChatSessions)
            .Returns(new ConcurrentDictionary<string, ChatSession>());

        ChatServerAccountBannedEventHandler handler = new(
            mockChatContext.Object,
            NullLogger<ChatServerAccountBannedEventHandler>.Instance);

        AccountBannedEvent banEvent = new(
            404,
            "BannedPlayer",
            TimeProvider.System.GetUtcNow().AddDays(7),
            "Harassment"
        );

        // Act
        Func<Task> act = async () => await handler.HandleAsync(banEvent);

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ChatServerEventHandler_ChatServerHeroAvatarPurchasedEventHandler_ShouldHandleWithoutThrowing()
    {
        // Arrange
        ChatServerHeroAvatarPurchasedEventHandler handler = new(
            NullLogger<ChatServerHeroAvatarPurchasedEventHandler>.Instance);

        HeroAvatarPurchasedEvent purchaseEvent = new(
            707,
            303,
            9901,
            "Hero_Chronos.Alt1",
            0,
            400
        );

        // Act
        Func<Task> act = async () => await handler.HandleAsync(purchaseEvent);

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void AddChatServerServices_RegistersUserLoggedOutEventHandler_InDependencyInjection()
    {
        // Protects: EventBusSubscriptionInitializerHostedService requires explicit IEventHandler<UserLoggedOutEvent> DI registration.
        // Fails when: AddChatServerServices omits services.AddEventHandler<ChatServerUserLoggedOutEventHandler>().
        ServiceCollection services = new();
        services.AddSingleton(Mock.Of<IConnectionMultiplexer>());

        services.AddChatServerServices();

        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IEventHandler<UserLoggedOutEvent>));
    }

    [Fact]
    public async Task ChatServerUserLoggedOutEventHandler_TerminatesSessionWithMatchingCookie_IncludingGuidFormatNormalization()
    {
        // Protects: HTTP logout must terminate the matching TCP chat session even if one cookie is dashed GUID and the other is dash-less ("N").
        // Fails when: Handler does not remove/terminate matching session or compares raw strings without GUID normalization.
        (IServiceProvider sp, TCPServer tcpServer, ConcurrentDictionary<string, ChatSession> clientSessions) = CreateSessionHarness();
        using TCPServer serverGuard = tcpServer;

        Guid rawGuid = Guid.NewGuid();
        string dashlessCookie = rawGuid.ToString("N");
        string dashedCookie = rawGuid.ToString("D");

        Account account = CreateAccount(501, "LogoutTarget");
        using ChatSession session = new(tcpServer, sp)
        {
            Account = account,
            ClientMetadata = CreateMetadata(dashlessCookie)
        };
        clientSessions[account.Name] = session;

        Mock<IChatContext> mockChatContext = sp.GetRequiredService<IChatContext>() is var ctx
            ? new Mock<IChatContext>()
            : new Mock<IChatContext>();
        mockChatContext.SetupGet(c => c.ClientChatSessions).Returns(clientSessions);

        ChatServerUserLoggedOutEventHandler handler = new(
            mockChatContext.Object,
            NullLogger<ChatServerUserLoggedOutEventHandler>.Instance);

        await handler.HandleAsync(new UserLoggedOutEvent(dashedCookie, account.Name));

        clientSessions.ContainsKey(account.Name).Should().BeFalse();
        ((object?)session.Account).Should().BeNull();
    }

    [Fact]
    public async Task ChatServerUserLoggedOutEventHandler_PreservesActiveSession_WhenSessionHasNewerDifferentCookie()
    {
        // Protects: Re-authenticated client holding a newer session cookie must NOT be disconnected by a delayed logout event for an older cookie.
        // Fails when: Logout handler matches by AccountName instead of SessionCookie.
        (IServiceProvider sp, TCPServer tcpServer, ConcurrentDictionary<string, ChatSession> clientSessions) = CreateSessionHarness();
        using TCPServer serverGuard = tcpServer;

        string oldCookie = Guid.NewGuid().ToString("N");
        string newCookie = Guid.NewGuid().ToString("N");

        Account account = CreateAccount(502, "RelogPlayer");
        using ChatSession activeNewSession = new(tcpServer, sp)
        {
            Account = account,
            ClientMetadata = CreateMetadata(newCookie)
        };
        clientSessions[account.Name] = activeNewSession;

        Mock<IChatContext> mockChatContext = new();
        mockChatContext.SetupGet(c => c.ClientChatSessions).Returns(clientSessions);

        ChatServerUserLoggedOutEventHandler handler = new(
            mockChatContext.Object,
            NullLogger<ChatServerUserLoggedOutEventHandler>.Instance);

        await handler.HandleAsync(new UserLoggedOutEvent(oldCookie, account.Name));

        clientSessions.ContainsKey(account.Name).Should().BeTrue();
        ((object?)activeNewSession.Account).Should().NotBeNull();
    }

    [Fact]
    public void MatchmakingGroup_RemoveMember_ReplacesMembersSnapshot_WithoutBreakingConcurrentReader()
    {
        // Protects: Lock-free readers (Leader, AverageRating, UpdateBot) iterating MatchmakingGroup.Members concurrently with RemoveMember.
        // Fails when: RemoveMember mutates Members in-place (Members.Remove), throwing InvalidOperationException on active enumerators.
        (IServiceProvider sp, TCPServer tcpServer, _) = CreateSessionHarness();
        using TCPServer serverGuard = tcpServer;

        Account leaderAccount = CreateAccount(1, "LeaderOne");
        Account memberAccount = CreateAccount(2, "MemberTwo");

        using ChatSession leaderSession = new(tcpServer, sp) { Account = leaderAccount, ClientMetadata = CreateMetadata("cookie_leader") };
        using ChatSession memberSession = new(tcpServer, sp) { Account = memberAccount, ClientMetadata = CreateMetadata("cookie_member") };

        MatchmakingGroup group = new()
        {
            Information = new MatchmakingGroupInformation
            {
                ClientVersion = "4.10.1.0",
                GroupType = ChatProtocol.TMMType.TMM_TYPE_PVP,
                GameType = ChatProtocol.TMMGameType.TMM_GAME_TYPE_NORMAL,
                MapName = "caldavar",
                GameModes = ["ap"],
                GameRegions = ["use"],
                Ranked = true,
                MatchFidelity = 1,
                BotDifficulty = 0,
                RandomizeBots = false
            },
            Members =
            [
                new MatchmakingGroupMember(leaderSession) { Slot = 1, IsLeader = true, Rating = 1500 },
                new MatchmakingGroupMember(memberSession) { Slot = 2, IsLeader = false, Rating = 1500 }
            ]
        };

        Mock<IMatchmakingService> mockMatchmaking = new();
        mockMatchmaking.Setup(m => m.Groups).Returns(new ConcurrentDictionary<int, MatchmakingGroup>());

        using IEnumerator<MatchmakingGroupMember> concurrentReader = group.Members.GetEnumerator();
        concurrentReader.MoveNext().Should().BeTrue();

        // Act: mutate group membership while concurrentReader is mid-enumeration
        group.RemoveMember(mockMatchmaking.Object, memberAccount.ID);

        // Assert: advancing the in-flight reader must NOT throw InvalidOperationException ("Collection was modified")
        Action advanceReader = () => concurrentReader.MoveNext();
        advanceReader.Should().NotThrow<InvalidOperationException>();
        group.Members.Should().ContainSingle(m => m.Account.ID == leaderAccount.ID);
    }
}
