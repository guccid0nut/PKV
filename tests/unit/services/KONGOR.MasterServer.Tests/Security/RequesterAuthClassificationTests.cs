// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections;
using System.Collections.Frozen;
using System.Net;
using System.Reflection;
using System.Text;
using ASPIRE.Common.Models.ServerManagement;
using ASPIRE.Common.Serialization.PHP;
using FluentAssertions;
using KONGOR.MasterServer.Controllers.ReplayController;
using KONGOR.MasterServer.Controllers.ServerRequesterController;
using KONGOR.MasterServer.Features.Matches.StartMatch;
using KONGOR.MasterServer.Handlers.ClientRequester;
using KONGOR.MasterServer.Models.Configuration;
using KONGOR.MasterServer.Services.Requester;
using KONGOR.MasterServer.Services.Security;
using KONGOR.MasterServer.Services.Storage;
using MemoryPack;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Moq;
using NEXUS.Contracts.Data;
using NEXUS.Contracts.Security;
using StackExchange.Redis;
using Xunit;

namespace KONGOR.MasterServer.Tests.Security;

public sealed class RequesterAuthClassificationTests
{
    private static readonly FrozenSet<string> PreAuthAndReadOnlyClientFunctions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Authentication & Session Bootstrap / Teardown
            "pre_auth",
            "srpauth",
            "auth",
            "logout",

            // Public / Read-Only Discovery, Catalog & Statistics Queries
            "get_server_list",
            "grab_server_list",
            "server_list",
            "get_all_heroes",
            "get_hero_list",
            "show_simple_stats",
            "show_stats",
            "get_initstats",
            "get_match_stats",
            "final_match_stats",
            "get_match_info",
            "multi_match_stats",
            "grab_match_info",
            "get_match",
            "match_stats",
            "grab_last_matches",
            "grab_last_matches_from_nick",
            "match_history_overview",
            "get_account_all_hero_stats",
            "get_hero_stats",
            "get_campaign_hero_stats",
            "get_account_mastery",
            "get_player_award_summ",
            "get_seasons",
            "get_ladder",
            "echotest",
            "get_products",
            "get_daily_special",
            "get_bundle_contents",
            "get_guide_list_filtered",
            "get_guide",
            "client_events_info",
            "get_special_messages",
            "get_clan_info",
            "clan_info",
            "get_clan_roster",
            "clan_roster",
            "autocomplete",
            "user_search",
            "search_user",
            "nick_search",
            "get_referrals",
            "get_hero_usage_list",
            "get_hero_usage_graph"
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> AuthenticatedClientFunctions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Session-bound reads & state-mutating actions
            "switch_auth",
            "aids2cookie",
            "get_upgrades",
            "debug_upgrades_diff",
            "selected_upgrades",
            "create_game",
            "new_game_available",
            "claim_season_rewards",
            "claim_mastery_reward",
            "take_mastery_reward",
            "use_mastery_boost",
            "apply_mastery_boost",
            "mastery_boost",
            "use_boost",
            "upload_replay",
            "guide_default_set",
            "guide_default_remove",
            "guide_favorite_set",
            "guide_favorite_remove",
            "guide_vote",
            "set_rank",
            "create_clan",
            "clan_create",
            "invite_clan_member",
            "clan_invite",
            "remove_clan_member",
            "clan_kick",
            "leave_clan",
            "clan_leave",
            "disband_clan",
            "clan_disband",
            "new_buddy",
            "add_buddy",
            "approve_buddy",
            "accept_buddy",
            "reject_buddy",
            "remove_buddy",
            "new_banned",
            "remove_banned",
            "new_ignored",
            "remove_ignored",
            "set_ingame_referrer",
            "get_points",
            "receive_revival_gift",
            "remove_all_notifications",
            "delete_notification",
            "test_create_notification"
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> CredentialBootstrapOrReadServerFunctions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "replay_auth",
            "new_session",
            "auth",
            "get_quickstats"
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> SessionAuthenticatedServerFunctions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "get_spectator_header",
            "set_replay_size",
            "accept_key",
            "c_conn",
            "set_online",
            "shutdown",
            "start_game",
            "abandon_match",
            "cancel_match",
            "live_tick",
            "match_telemetry",
            "aids2cookie",
            "get_upgrades"
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static IOptions<OperationalConfiguration> CreateDefaultOperationalConfig() =>
        Options.Create(new OperationalConfiguration
        {
            CDN = new OperationalConfigurationCDN
            {
                Host = "http://localhost:55555",
                PrimaryPatchURL = "http://localhost:55555/patch",
                SecondaryPatchURL = "http://localhost:55555/patch"
            }
        });

    private static IWebHostEnvironment CreateEnvironment(string environmentName)
    {
        Mock<IWebHostEnvironment> mockEnv = new();
        mockEnv.Setup(e => e.EnvironmentName).Returns(environmentName);
        return mockEnv.Object;
    }

    [Fact]
    public void ClientRequestDispatcher_AllHandlerMapFunctions_AreExplicitlyClassifiedWithZeroUnclassifiedDrift()
    {
        // protects: client_requester.php?f=* against unreviewed new endpoints bypassing security classification
        // fails_when: A new key is added to ClientRequestDispatcher.HandlerMap without explicit auth classification
        // seam: ClientRequestDispatcher.HandlerMap static routing table
        HashSet<string> overlap = new(PreAuthAndReadOnlyClientFunctions, StringComparer.OrdinalIgnoreCase);
        overlap.IntersectWith(AuthenticatedClientFunctions);
        overlap.Should().BeEmpty("no client function may be classified as both public/pre-auth and authenticated");

        HashSet<string> classifiedUnion = new(PreAuthAndReadOnlyClientFunctions, StringComparer.OrdinalIgnoreCase);
        classifiedUnion.UnionWith(AuthenticatedClientFunctions);

        HashSet<string> actualFunctions = new(ClientRequestDispatcher.HandlerMap.Keys, StringComparer.OrdinalIgnoreCase);

        IEnumerable<string> unclassified = actualFunctions.Except(classifiedUnion, StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> staleClassified = classifiedUnion.Except(actualFunctions, StringComparer.OrdinalIgnoreCase);

        unclassified.Should().BeEmpty(
            "every client_requester.php?f= function in ClientRequestDispatcher.HandlerMap must have an explicit auth classification in RequesterAuthClassificationTests");
        staleClassified.Should().BeEmpty(
            "classification sets must stay in 1:1 parity with ClientRequestDispatcher.HandlerMap");
    }

    [Fact]
    public void ServerRequesterController_AllSupportedFunctions_AreExplicitlyClassifiedWithZeroUnclassifiedDrift()
    {
        // protects: server_requester.php?f=* against unauthenticated dedicated-server endpoint additions
        // fails_when: A server function is added to ServerRequesterController without auth classification
        // seam: ServerRequesterController.SupportedServerFunctions
        HashSet<string> overlap = new(CredentialBootstrapOrReadServerFunctions, StringComparer.OrdinalIgnoreCase);
        overlap.IntersectWith(SessionAuthenticatedServerFunctions);
        overlap.Should().BeEmpty();

        HashSet<string> classifiedUnion = new(CredentialBootstrapOrReadServerFunctions, StringComparer.OrdinalIgnoreCase);
        classifiedUnion.UnionWith(SessionAuthenticatedServerFunctions);

        ServerRequesterController.SupportedServerFunctions.Should().BeEquivalentTo(classifiedUnion);
    }

    [Fact]
    public async Task ReplayController_UploadReplay_Enforces150MbCapAuthAndMasksInternalExceptions()
    {
        // protects: /replays/upload against unauthenticated disk exhaustion and 500 exception path leaks
        // fails_when: [DisableRequestSizeLimit] is present, unauthenticated uploads return 200, or 500 leaks ex.Message
        // seam: ReplayController.UploadReplay attribute metadata + action invocation
        MethodInfo? uploadMethod = typeof(ReplayController).GetMethod(nameof(ReplayController.UploadReplay));
        uploadMethod.Should().NotBeNull();

        uploadMethod!.GetCustomAttribute<DisableRequestSizeLimitAttribute>().Should().BeNull(
            "DisableRequestSizeLimit permits unbounded disk/memory exhaustion on replay upload");

        CustomAttributeData? sizeLimitAttrData = uploadMethod.CustomAttributes
            .SingleOrDefault(a => a.AttributeType == typeof(RequestSizeLimitAttribute));
        sizeLimitAttrData.Should().NotBeNull();
        sizeLimitAttrData!.ConstructorArguments.Should().ContainSingle();
        sizeLimitAttrData.ConstructorArguments[0].Value.Should().Be(ReplayController.MaxReplayUploadBytes);

        RequestFormLimitsAttribute? formLimitAttr = uploadMethod.GetCustomAttribute<RequestFormLimitsAttribute>();
        formLimitAttr.Should().NotBeNull();
        formLimitAttr!.MultipartBodyLengthLimit.Should().Be(ReplayController.MaxReplayUploadBytes);

        Mock<IReplayStorageProvider> mockStorage = new();
        Mock<IMatchHistoryRepository> mockMatchRepo = new();
        Mock<IDatabase> mockRedis = new();
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["InternalServiceKey"] = "prod-replay-test-secret-key-999"
            })
            .Build();

        ReplayController controller = new(
            NullLogger<ReplayController>.Instance,
            CreateEnvironment(Environments.Production),
            mockStorage.Object,
            mockMatchRepo.Object,
            distributedCache: mockRedis.Object,
            configuration: config);

        // 1. Unauthenticated raw upload -> 401 Unauthorized, storage never invoked
        DefaultHttpContext unauthContext = new();
        unauthContext.Request.Method = "POST";
        byte[] payloadBytes = Encoding.UTF8.GetBytes("FAKE_REPLAY");
        unauthContext.Request.Body = new MemoryStream(payloadBytes);
        unauthContext.Request.ContentLength = payloadBytes.Length;
        controller.ControllerContext = new ControllerContext { HttpContext = unauthContext };

        IActionResult unauthResult = await controller.UploadReplay(matchId: "12345");
        unauthResult.Should().BeOfType<UnauthorizedObjectResult>();
        mockStorage.Verify(
            s => s.UploadReplayAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);

        // 2. Oversized ContentLength (> 150 MiB) -> 413 PayloadTooLarge
        DefaultHttpContext oversizedContext = new();
        oversizedContext.Request.Method = "POST";
        oversizedContext.Request.ContentLength = ReplayController.MaxReplayUploadBytes + 1;
        oversizedContext.Request.Headers[SecurityConstants.InternalServiceKeyHeader] = "prod-replay-test-secret-key-999";
        controller.ControllerContext = new ControllerContext { HttpContext = oversizedContext };

        IActionResult oversizedResult = await controller.UploadReplay(matchId: "12345");
        ObjectResult payloadTooLarge = oversizedResult.Should().BeOfType<ObjectResult>().Subject;
        payloadTooLarge.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);

        // 3. Authenticated upload throwing internal storage IOException -> 500 sanitized ("Internal Server Error", no secret path leak)
        mockStorage
            .Setup(s => s.UploadReplayAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException(@"Access denied to C:\private\server\volumes\replays\M12345.honreplay"));

        DefaultHttpContext errorContext = new();
        errorContext.Request.Method = "POST";
        errorContext.Request.Body = new MemoryStream(payloadBytes);
        errorContext.Request.ContentLength = payloadBytes.Length;
        errorContext.Request.Headers[SecurityConstants.InternalServiceKeyHeader] = "prod-replay-test-secret-key-999";
        controller.ControllerContext = new ControllerContext { HttpContext = errorContext };

        IActionResult sanitized500 = await controller.UploadReplay(matchId: "12345");
        ObjectResult internalErr = sanitized500.Should().BeOfType<ObjectResult>().Subject;
        internalErr.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        string errBody = internalErr.Value.Should().BeOfType<string>().Subject;
        errBody.Should().Be("Internal Server Error");
        errBody.Should().NotContain(@"C:\private");
    }

    [Fact]
    public async Task SpectatorHeaderUploadToken_EndToEndWithReplayControllerAndReplayHandler_EnforcesAuth()
    {
        // protects: Dedicated server get_spectator_header -> /replays/upload flow and client_requester.php?f=upload_replay
        // fails_when: ReplayHandler allows unauthenticated uploads or get_spectator_header fails to mint usable X-Replay-Upload-Token
        // seam: ServerRequesterController.HandleGetSpectatorHeader -> ReplayController.UploadReplay + ReplayHandler
        Dictionary<string, string> tokenStore = new(StringComparer.Ordinal);
        Mock<IDatabase> mockRedis = new();

        MatchServer registeredServer = new()
        {
            ID = 42,
            HostAccountID = 7,
            HostAccountName = "DedicatedHost",
            Name = "US-East-42",
            Instance = 1,
            IPAddress = "203.0.113.10",
            Port = 11235,
            Location = "US",
            Description = "Official",
            Cookie = "dserver_valid_session_42"
        };
        byte[] serializedServer = MemoryPackSerializer.Serialize(registeredServer);

        mockRedis
            .Setup(r => r.HashGetAsync("MATCH-SERVER-SESSIONS", "dserver_valid_session_42", It.IsAny<CommandFlags>()))
            .ReturnsAsync(serializedServer);

        mockRedis
            .Setup(r => r.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<bool>(),
                It.IsAny<When>(),
                It.IsAny<CommandFlags>()))
            .Callback<RedisKey, RedisValue, TimeSpan?, bool, When, CommandFlags>((k, v, _, _, _, _) =>
            {
                tokenStore[k.ToString()] = v.ToString()!;
            })
            .ReturnsAsync(true);

        mockRedis
            .Setup(r => r.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<Expiration>(),
                It.IsAny<ValueCondition>(),
                It.IsAny<CommandFlags>()))
            .Callback<RedisKey, RedisValue, Expiration, ValueCondition, CommandFlags>((k, v, _, _, _) =>
            {
                tokenStore[k.ToString()] = v.ToString()!;
            })
            .ReturnsAsync(true);

        mockRedis
            .Setup(r => r.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisKey k, CommandFlags _) =>
                tokenStore.TryGetValue(k.ToString(), out string? val) ? (RedisValue)val : RedisValue.Null);

        Mock<IAccountRepository> mockAccountRepo = new();
        Mock<IMatchHistoryRepository> mockMatchRepo = new();
        Mock<IReplayStorageProvider> mockStorage = new();
        mockStorage
            .Setup(s => s.UploadReplayAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("https://cdn.kongor.net/replays/M777.honreplay");

        ServerRequesterController serverController = new(
            mockAccountRepo.Object,
            mockMatchRepo.Object,
            mockRedis.Object,
            NullLogger<ServerRequesterController>.Instance,
            CreateDefaultOperationalConfig(),
            hostEnvironment: CreateEnvironment(Environments.Production));

        // 1. Unauthenticated get_spectator_header in Production -> 401
        DefaultHttpContext unauthHeaderCtx = new();
        unauthHeaderCtx.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.9");
        unauthHeaderCtx.Request.ContentType = "application/x-www-form-urlencoded";
        unauthHeaderCtx.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["f"] = "get_spectator_header"
        });
        serverController.ControllerContext = new ControllerContext { HttpContext = unauthHeaderCtx };
        (await serverController.ServerRequester()).Should().BeOfType<UnauthorizedObjectResult>();

        // 2. Authenticated get_spectator_header -> mints X-Replay-Upload-Token in s3_header
        DefaultHttpContext authHeaderCtx = new();
        authHeaderCtx.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.10");
        authHeaderCtx.Request.ContentType = "application/x-www-form-urlencoded";
        authHeaderCtx.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["f"] = "get_spectator_header",
            ["session"] = "dserver_valid_session_42"
        });
        serverController.ControllerContext = new ControllerContext { HttpContext = authHeaderCtx };

        OkObjectResult headerOk = (await serverController.ServerRequester()).Should().BeOfType<OkObjectResult>().Subject;
        string phpPayload = headerOk.Value.Should().BeOfType<string>().Subject;
        IDictionary parsed = PhpSerializer.Deserialize(phpPayload).Should().BeAssignableTo<IDictionary>().Subject;
        IDictionary s3Headers = parsed["s3_header"].Should().BeAssignableTo<IDictionary>().Subject;
        string mintedToken = s3Headers[ReplayUploadAuthorizer.ReplayUploadTokenHeader].Should().BeOfType<string>().Subject;
        mintedToken.Length.Should().Be(64);

        // 3. ReplayHandler rejects unauthenticated upload, then accepts upload with minted X-Replay-Upload-Token
        ReplayHandler replayHandler = new(
            NullLogger<ReplayHandler>.Instance,
            mockStorage.Object,
            mockMatchRepo.Object,
            distributedCache: mockRedis.Object,
            hostEnvironment: CreateEnvironment(Environments.Production));

        byte[] replayBytes = [0x50, 0x4B, 0x03, 0x04];
        FormFile replayFile = new(new MemoryStream(replayBytes), 0, replayBytes.Length, "file", "M777.honreplay");

        DefaultHttpContext unauthClientUploadCtx = new();
        unauthClientUploadCtx.Request.ContentType = "multipart/form-data";
        unauthClientUploadCtx.Request.Form = new FormCollection(
            new Dictionary<string, StringValues>
            {
                ["f"] = "upload_replay",
                ["match_id"] = "777",
                ["cookie"] = "forged_cookie_value"
            },
            new FormFileCollection { replayFile });

        (await replayHandler.HandleRequestAsync(unauthClientUploadCtx))
            .Should().BeOfType<UnauthorizedObjectResult>();

        DefaultHttpContext tokenUploadCtx = new();
        tokenUploadCtx.Request.ContentType = "multipart/form-data";
        tokenUploadCtx.Request.Headers[ReplayUploadAuthorizer.ReplayUploadTokenHeader] = mintedToken;
        tokenUploadCtx.Request.Form = new FormCollection(
            new Dictionary<string, StringValues>
            {
                ["f"] = "upload_replay",
                ["match_id"] = "777"
            },
            new FormFileCollection
            {
                new FormFile(new MemoryStream(replayBytes), 0, replayBytes.Length, "file", "M777.honreplay")
            });

        (await replayHandler.HandleRequestAsync(tokenUploadCtx)).Should().BeOfType<PhpResult>();

        // 4. ReplayController (/replays/upload) also accepts the minted X-Replay-Upload-Token
        ReplayController replayController = new(
            NullLogger<ReplayController>.Instance,
            CreateEnvironment(Environments.Production),
            mockStorage.Object,
            mockMatchRepo.Object,
            distributedCache: mockRedis.Object);

        DefaultHttpContext controllerTokenCtx = new();
        controllerTokenCtx.Request.Method = HttpMethods.Put;
        controllerTokenCtx.Request.ContentType = "application/zip";
        controllerTokenCtx.Request.ContentLength = replayBytes.Length;
        controllerTokenCtx.Request.Body = new MemoryStream(replayBytes);
        controllerTokenCtx.Request.Headers[ReplayUploadAuthorizer.ReplayUploadTokenHeader] = mintedToken;
        replayController.ControllerContext = new ControllerContext { HttpContext = controllerTokenCtx };

        (await replayController.UploadReplay(accountId: "10", matchId: "777")).Should().BeOfType<OkResult>();
    }

    [Fact]
    public async Task ServerRequester_HandleMatchStart_NeverHijacksIdleServerCookie_AndBlocksNonLoopbackPortFallback()
    {
        // protects: server_requester.php?f=start_game against unauthenticated idle server session hijacking and fake match creation
        // fails_when: HandleMatchStart falls back to scanning IDLE/LOADING MatchServers or allows remote port lookup without session
        // seam: ServerRequesterController -> StartMatchHandler
        Mock<IDatabase> mockRedis = new();
        MatchServer idleServer = new()
        {
            ID = 99,
            HostAccountID = 1,
            HostAccountName = "OfficialFleetHost",
            Name = "Idle-Arena-99",
            Instance = 1,
            IPAddress = "127.0.0.1",
            Port = 11235,
            Location = "US",
            Description = "Idle",
            Cookie = "secret_idle_server_cookie_99",
            Status = ServerStatus.SERVER_STATUS_IDLE
        };
        byte[] serializedIdle = MemoryPackSerializer.Serialize(idleServer);

        mockRedis
            .Setup(r => r.HashGetAllAsync("MATCH-SERVERS", It.IsAny<CommandFlags>()))
            .ReturnsAsync([new HashEntry("OfficialFleetHost:11235", serializedIdle)]);

        Mock<IStartMatchHandler> mockStartHandler = new();

        // Even in Development, a non-loopback caller sending port=11235 without session MUST be rejected (400)
        ServerRequesterController devController = new(
            Mock.Of<IAccountRepository>(),
            Mock.Of<IMatchHistoryRepository>(),
            mockRedis.Object,
            NullLogger<ServerRequesterController>.Instance,
            CreateDefaultOperationalConfig(),
            startMatchHandler: mockStartHandler.Object,
            hostEnvironment: CreateEnvironment(Environments.Development));

        DefaultHttpContext remoteNoSessionCtx = new();
        remoteNoSessionCtx.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.77");
        remoteNoSessionCtx.Request.ContentType = "application/x-www-form-urlencoded";
        remoteNoSessionCtx.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["f"] = "start_game",
            ["port"] = "11235",
            ["map"] = "caldavar"
        });
        devController.ControllerContext = new ControllerContext { HttpContext = remoteNoSessionCtx };

        (await devController.ServerRequester()).Should().BeOfType<BadRequestObjectResult>();
        mockStartHandler.Verify(h => h.HandleAsync(It.IsAny<StartMatchCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // Forged session with an IDLE server sitting in Redis MUST return 401 (never hijack idleServer.Cookie)
        const string forgedSession = "attacker_forged_session_token_xyz";
        DefaultHttpContext forgedSessionCtx = new();
        forgedSessionCtx.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.77");
        forgedSessionCtx.Request.ContentType = "application/x-www-form-urlencoded";
        forgedSessionCtx.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["f"] = "start_game",
            ["session"] = forgedSession,
            ["map"] = "caldavar"
        });
        devController.ControllerContext = new ControllerContext { HttpContext = forgedSessionCtx };

        UnauthorizedObjectResult unauth =
            (await devController.ServerRequester()).Should().BeOfType<UnauthorizedObjectResult>().Subject;
        string body = unauth.Value.Should().BeOfType<string>().Subject;
        body.Should().NotContain(forgedSession);
        body.Should().NotContain("auth_failure");
        mockStartHandler.Verify(h => h.HandleAsync(It.IsAny<StartMatchCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ServerRequester_HandleMatchAbandon_EnforcesSessionOwnershipAndInternalKey()
    {
        // protects: server_requester.php?f=abandon_match|cancel_match against unauthenticated or cross-server match cancellation
        // fails_when: HandleMatchAbandon cancels matches without session/internal-key auth or allows Server #2 to abandon Server #1's match
        // seam: ServerRequesterController.HandleMatchAbandon
        Mock<IDatabase> mockRedis = new();

        MatchServer ownerServer = new()
        {
            ID = 101,
            HostAccountID = 1,
            HostAccountName = "HostA",
            Name = "Arena-101",
            Instance = 1,
            IPAddress = "10.0.0.1",
            Port = 11235,
            Location = "US",
            Description = "Owner",
            Cookie = "owner_server_session_101"
        };
        MatchServer otherServer = new()
        {
            ID = 202,
            HostAccountID = 2,
            HostAccountName = "HostB",
            Name = "Arena-202",
            Instance = 1,
            IPAddress = "10.0.0.2",
            Port = 11236,
            Location = "US",
            Description = "Other",
            Cookie = "other_server_session_202"
        };
        MatchStartData activeMatch = new()
        {
            MatchID = 5005,
            ServerID = 101,
            ServerName = "Arena-101",
            Map = "caldavar",
            Version = "4.10.1.0",
            MatchName = "Tournament Lobby",
            HostAccountName = "HostA",
            IsCasual = false,
            MatchType = 1,
            MatchMode = "ap"
        };

        mockRedis
            .Setup(r => r.HashGetAsync("MATCH-SERVER-SESSIONS", "owner_server_session_101", It.IsAny<CommandFlags>()))
            .ReturnsAsync(MemoryPackSerializer.Serialize(ownerServer));
        mockRedis
            .Setup(r => r.HashGetAsync("MATCH-SERVER-SESSIONS", "other_server_session_202", It.IsAny<CommandFlags>()))
            .ReturnsAsync(MemoryPackSerializer.Serialize(otherServer));
        mockRedis
            .Setup(r => r.StringGetAsync("MATCH-START-DATA:[\"5005\"]", It.IsAny<CommandFlags>()))
            .ReturnsAsync(MemoryPackSerializer.Serialize(activeMatch));

        Mock<IStartMatchHandler> mockStartHandler = new();
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["InternalServiceKey"] = "compel-internal-key-secret-2026"
            })
            .Build();

        ServerRequesterController controller = new(
            Mock.Of<IAccountRepository>(),
            Mock.Of<IMatchHistoryRepository>(),
            mockRedis.Object,
            NullLogger<ServerRequesterController>.Instance,
            CreateDefaultOperationalConfig(),
            startMatchHandler: mockStartHandler.Object,
            hostEnvironment: CreateEnvironment(Environments.Production),
            rawConfiguration: config);

        // 1. Missing session & missing internal key -> 400 BadRequest
        DefaultHttpContext noAuthCtx = new();
        noAuthCtx.Request.ContentType = "application/x-www-form-urlencoded";
        noAuthCtx.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["f"] = "abandon_match",
            ["match_id"] = "5005"
        });
        controller.ControllerContext = new ControllerContext { HttpContext = noAuthCtx };
        (await controller.ServerRequester()).Should().BeOfType<BadRequestObjectResult>();

        // 2. Forged session -> 401 Unauthorized
        DefaultHttpContext forgedCtx = new();
        forgedCtx.Request.ContentType = "application/x-www-form-urlencoded";
        forgedCtx.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["f"] = "abandon_match",
            ["match_id"] = "5005",
            ["session"] = "forged_session_abc"
        });
        controller.ControllerContext = new ControllerContext { HttpContext = forgedCtx };
        (await controller.ServerRequester()).Should().BeOfType<UnauthorizedObjectResult>();

        // 3. Cross-server session (Server #202 trying to cancel Server #101's Match #5005) -> 401 Unauthorized
        DefaultHttpContext crossServerCtx = new();
        crossServerCtx.Request.ContentType = "application/x-www-form-urlencoded";
        crossServerCtx.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["f"] = "cancel_match",
            ["match_id"] = "5005",
            ["session"] = "other_server_session_202"
        });
        controller.ControllerContext = new ControllerContext { HttpContext = crossServerCtx };
        (await controller.ServerRequester()).Should().BeOfType<UnauthorizedObjectResult>();

        mockStartHandler.Verify(
            h => h.AbandonMatchAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);

        // 4. Owning server session -> 200 OK
        DefaultHttpContext ownerCtx = new();
        ownerCtx.Request.ContentType = "application/x-www-form-urlencoded";
        ownerCtx.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["f"] = "abandon_match",
            ["match_id"] = "5005",
            ["session"] = "owner_server_session_101",
            ["reason"] = "EmptyLobby"
        });
        controller.ControllerContext = new ControllerContext { HttpContext = ownerCtx };
        (await controller.ServerRequester()).Should().BeOfType<OkObjectResult>();

        // 5. Authenticated COMPEL InternalServiceKey -> 200 OK
        DefaultHttpContext internalKeyCtx = new();
        internalKeyCtx.Request.ContentType = "application/x-www-form-urlencoded";
        internalKeyCtx.Request.Headers[SecurityConstants.InternalServiceKeyHeader] = "compel-internal-key-secret-2026";
        internalKeyCtx.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["f"] = "abandon_match",
            ["match_id"] = "5005",
            ["reason"] = "SupervisorRecycle"
        });
        controller.ControllerContext = new ControllerContext { HttpContext = internalKeyCtx };
        (await controller.ServerRequester()).Should().BeOfType<OkObjectResult>();

        mockStartHandler.Verify(
            h => h.AbandonMatchAsync(5005, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task ServerRequester_ShutdownIsIdempotentForEvictedCookies_AndLiveTickRejectsUnauthenticatedRequests()
    {
        // protects: idempotent f=shutdown acknowledgement for already-evicted server sessions, missing-session validation, and f=live_tick auth boundary
        // fails_when: late dedicated server shutdown returns 401 instead of 200 OK, missing session returns 200 OK, or unauthenticated live_tick telemetry is accepted in Production
        // seam: ServerRequesterController.HandleShutdown + HandleLiveTick
        ServerRequesterController controller = new(
            Mock.Of<IAccountRepository>(),
            Mock.Of<IMatchHistoryRepository>(),
            Mock.Of<IDatabase>(),
            NullLogger<ServerRequesterController>.Instance,
            CreateDefaultOperationalConfig(),
            hostEnvironment: CreateEnvironment(Environments.Production));

        DefaultHttpContext missingSessionShutdownCtx = new();
        missingSessionShutdownCtx.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.55");
        missingSessionShutdownCtx.Request.ContentType = "application/x-www-form-urlencoded";
        missingSessionShutdownCtx.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["f"] = "shutdown"
        });
        controller.ControllerContext = new ControllerContext { HttpContext = missingSessionShutdownCtx };
        (await controller.ServerRequester()).Should().BeOfType<BadRequestObjectResult>();

        DefaultHttpContext shutdownCtx = new();
        shutdownCtx.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.55");
        shutdownCtx.Request.ContentType = "application/x-www-form-urlencoded";
        shutdownCtx.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["f"] = "shutdown",
            ["session"] = "unregistered_shutdown_session"
        });
        controller.ControllerContext = new ControllerContext { HttpContext = shutdownCtx };
        (await controller.ServerRequester()).Should().BeOfType<OkResult>();

        DefaultHttpContext liveTickCtx = new();
        liveTickCtx.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.55");
        liveTickCtx.Request.ContentType = "application/json";
        liveTickCtx.Request.Query = new QueryCollection(new Dictionary<string, StringValues>
        {
            ["f"] = "live_tick",
            ["match_id"] = "9999"
        });
        liveTickCtx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"MatchID\":9999}"));
        controller.ControllerContext = new ControllerContext { HttpContext = liveTickCtx };
        (await controller.ServerRequester()).Should().BeOfType<UnauthorizedObjectResult>();
    }

    [Fact]
    public async Task PlayerPointsHandler_ReceiveRevivalGift_RejectsUnauthenticatedSession()
    {
        // protects: client_requester.php?f=receive_revival_gift against unauthenticated reward claims
        // fails_when: HandleReceiveRevivalGiftAsync returns ["0"] = true when ResolveAccountAsync returns null
        // seam: PlayerPointsHandler.HandleRequestAsync
        Mock<ISessionContextResolver> mockResolver = new();
        mockResolver
            .Setup(r => r.ResolveAccountAsync(It.IsAny<HttpContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MERRICK.DatabaseContext.Entities.Core.Account?)null);

        PlayerPointsHandler handler = new(mockResolver.Object, NullLogger<PlayerPointsHandler>.Instance);

        DefaultHttpContext httpContext = new();
        httpContext.Request.ContentType = "application/x-www-form-urlencoded";
        httpContext.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["f"] = "receive_revival_gift",
            ["cookie"] = "invalid_cookie"
        });

        PhpResult phpResult = (await handler.HandleRequestAsync(httpContext)).Should().BeOfType<PhpResult>().Subject;
        IDictionary payload = phpResult.Value.Should().BeAssignableTo<IDictionary>().Subject;
        payload["0"].Should().Be(false);
        payload["error_code"].Should().Be(1);
    }
}
