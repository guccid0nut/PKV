// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: NexusGrpcContractsIntegrationTests.cs
// System: NEXUS gRPC Contracts Integration Test Suite
// Policy: Strict Explicit Typing (NO 'var'), xUnit Framework
// ============================================================================

using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Grpc.Core;
using KONGOR.MasterServer.Services.Grpc;
using KONGOR.MasterServer.Services.Replay;
using MERRICK.DatabaseContext.Entities.Statistics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using NEXUS.Contracts.Auth.V1;
using NEXUS.Contracts.Cdn.V1;
using NEXUS.Contracts.Matchmaking.V1;
using NEXUS.Contracts.Security;
using NEXUS.Contracts.Telemetry.V1;
using SecureRemotePassword;
using TRANSMUTANSTEIN.ChatServer.Services;
using TRANSMUTANSTEIN.ChatServer.Services.Grpc;
using ZORGATH.WebPortal.API.Services.Grpc;

namespace ASPIRE.Tests.Services;

public sealed class TestServerCallContext : ServerCallContext
{
    protected override string MethodCore => "TestMethod";
    protected override string HostCore => "localhost";
    protected override string PeerCore => "127.0.0.1";
    protected override DateTime DeadlineCore => TimeProvider.System.GetUtcNow().UtcDateTime.AddMinutes(5);
    protected override Metadata RequestHeadersCore { get; } = [];

    protected override CancellationToken CancellationTokenCore => CancellationToken.None;
    protected override Metadata ResponseTrailersCore { get; } = [];

    protected override Status StatusCore { get; set; }

    protected override WriteOptions? WriteOptionsCore { get; set; }

    protected override AuthContext AuthContextCore => null!;

    protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders)
    {
        return Task.CompletedTask;
    }

    protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options)
    {
        return null!;
    }
}

public sealed class NexusGrpcContractsIntegrationTests
{
    private static ServerCallContext CreateMockServerCallContext()
    {
        return new TestServerCallContext();
    }

    private static MerrickContext CreateInMemoryDbContext(string dbName)
    {
        return TestDbContextFactory.CreateInMemoryContext(dbName);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task GrpcAuthService_CompleteAuthenticationLifecycle_Succeeds()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        string dbName = "GrpcAuthTestDb_" + Guid.NewGuid().ToString("N");
        using MerrickContext dbContext = CreateInMemoryDbContext(dbName);

        Role userRole = new() { ID = 10, Name = "User" };
        await dbContext.Roles.AddAsync(userRole);

        User testUser = new()
        {
            ID = 100,
            EmailAddress = "testplayer@kongor.net",
            Role = userRole,
            SRPPasswordSalt = "861c37ec6d049d92cc1c67d195b414f26b572a56358272af3e9c06fcd9bfa053",
            SRPPasswordHash = "9fb9cf192701aba7aaa38e6f15457082c93cfbe060bdbdc5fa67a986b355700c",
            PBKDF2PasswordHash = "AQAAAAIAAYagAAAAEMUkpLAr01NjkKRPaXCyTa17nlOdPKJucn5QYur+wQBTDKCpgsAcREenK+pGJPBCRw=="
        };
        await dbContext.Users.AddAsync(testUser);

        Account testAccount = new()
        {
            ID = 200,
            Name = "TestPlayer",
            User = testUser,
            IsMain = true,
            Type = AccountType.Normal
        };
        await dbContext.Accounts.AddAsync(testAccount);
        await dbContext.SaveChangesAsync();

        OperationalConfiguration opConfig = new()
        {
            JWT = new OperationalConfigurationJWT
            {
                SigningKey = "A_VERY_SECRET_KEY_FOR_TESTING_PURPOSES_AT_LEAST_32_BYTES_LONG!",
                Issuer = "https://portal.api.kongor.net",
                Audience = "https://kongor.net",
                DurationInHours = 24
            },
            Email = new OperationalConfigurationEmail
            {
                ApiKey = Guid.NewGuid().ToString("N"),
                FromEmail = "noreply@kongor.net",
                FromName = "Kongor"
            }
        };

        IOptions<OperationalConfiguration> options = Options.Create(opConfig);
        GrpcAuthService authService =
            new(new AccountRepository(dbContext), options, NullLogger<GrpcAuthService>.Instance);
        ServerCallContext context = CreateMockServerCallContext();

        // 1. PreAuth
        PreAuthRequest preAuthReq = new()
        {
            AccountName = "TestPlayer",
            ClientVersion = "4.10.1.0",
            IpAddress = "127.0.0.1"
        };
        PreAuthResponse preAuthResp = await authService.PreAuth(preAuthReq, context);
        Assert.NotNull(preAuthResp.Salt);
        Assert.NotEmpty(preAuthResp.SessionNonce);

        // 2. VerifySrpProof with genuine SRP-6a client ephemeral & proof
        SrpParameters parameters = SrpParameters.Create<SHA256>(
            NexusSrpConstants.SafePrimeNumber, NexusSrpConstants.MultiplicativeGroupGenerator);
        parameters.Generator = parameters.Pad(parameters.Generator);

        SrpClient srpClient = new(parameters);
        SrpEphemeral clientEphemeral = srpClient.GenerateEphemeral();
        string privateClientKey = srpClient.DerivePrivateKey(
            preAuthResp.Salt,
            testAccount.Name,
            testUser.SRPPasswordHash);
        SrpSession clientSession = srpClient.DeriveSession(
            clientEphemeral.Secret,
            preAuthResp.ServerEphemeralB,
            preAuthResp.Salt,
            testAccount.Name,
            privateClientKey);

        SrpProofRequest srpReq = new()
        {
            AccountName = "TestPlayer",
            ClientEphemeralA = clientEphemeral.Public,
            ClientProofM1 = clientSession.Proof,
            SessionNonce = preAuthResp.SessionNonce
        };
        SrpProofResponse srpResp = await authService.VerifySrpProof(srpReq, context);
        Assert.True(srpResp.Success);
        Assert.NotEmpty(srpResp.SessionToken);
        Assert.Equal((uint)200, srpResp.AccountId);
        Assert.Equal("TestPlayer", srpResp.Nickname);

        // 3. ValidateSessionToken
        ValidateTokenRequest valReq = new()
        {
            SessionToken = srpResp.SessionToken,
            RequiredPermission = ""
        };
        ValidateTokenResponse valResp = await authService.ValidateSessionToken(valReq, context);
        Assert.True(valResp.IsValid);
        Assert.Equal((uint)200, valResp.AccountId);
        Assert.Equal("TestPlayer", valResp.Nickname);

        // 4. ExchangeDiscordCode (must fail-closed when upstream Discord OAuth is disabled)
        DiscordExchangeRequest discordReq = new()
        {
            AuthorizationCode = "discord_auth_code_123",
            RedirectUri = "https://kongor.net/auth/callback",
            CodeVerifier = "pkce_verifier"
        };
        DiscordExchangeResponse discordResp = await authService.ExchangeDiscordCode(discordReq, context);
        Assert.False(discordResp.Success);
        Assert.Empty(discordResp.SessionToken);

        // 5. InvalidateSession (revokes issued JWT so subsequent ValidateSessionToken fails)
        InvalidateSessionRequest invReq = new()
        {
            SessionToken = srpResp.SessionToken
        };
        InvalidateSessionResponse invResp = await authService.InvalidateSession(invReq, context);
        Assert.True(invResp.Success);

        ValidateTokenResponse valAfterRevoke = await authService.ValidateSessionToken(valReq, context);
        Assert.False(valAfterRevoke.IsValid);
    }

    [Fact]
    public async Task GrpcMatchmakingService_TicketAndMatchFlow_Succeeds()
    {
        GrpcMatchmakingService mmService = new(
            NullLogger<GrpcMatchmakingService>.Instance
        );
        ServerCallContext context = CreateMockServerCallContext();

        // 1. Submit Ticket
        SubmitTicketRequest submitReq = new()
        {
            Player = new PlayerMatchCandidate
            {
                AccountId = 500,
                Nickname = "TopLaner",
                Mmr = 1750,
                RegionPreference = "USE"
            },
            GameMode = GameMode.AllPick
        };
        submitReq.AllowedRegions.Add("USE");
        submitReq.AllowedRegions.Add("USW");

        SubmitTicketResponse submitResp = await mmService.SubmitTicket(submitReq, context);
        Assert.NotEmpty(submitResp.TicketId);
        Assert.True(submitResp.EstimatedWaitSeconds > 0);

        // 2. Cancel Ticket
        CancelTicketRequest cancelReq = new()
        {
            TicketId = submitResp.TicketId,
            AccountId = 500
        };
        CancelTicketResponse cancelResp = await mmService.CancelTicket(cancelReq, context);
        Assert.True(cancelResp.Cancelled);

        // 3. Provision Match
        ProvisionMatchRequest provReq = new()
        {
            MatchId = 2060569001,
            MapName = "caldavar",
            GameMode = GameMode.AllPick,
            Region = "USE"
        };
        provReq.LegionTeam.Add(new PlayerMatchCandidate { AccountId = 500, Nickname = "TopLaner", Mmr = 1750 });
        provReq.HellbourneTeam.Add(new PlayerMatchCandidate { AccountId = 501, Nickname = "MidLaner", Mmr = 1780 });

        ProvisionMatchResponse provResp = await mmService.ProvisionMatch(provReq, context);
        Assert.True(provResp.ServerAllocated);
        Assert.NotEmpty(provResp.ServerIp);
        Assert.True(provResp.ServerPort > 0);
        Assert.NotEmpty(provResp.MatchAuthToken);

        // 4. Acknowledge Match Connect
        AcknowledgeConnectRequest ackReq = new()
        {
            MatchId = 2060569001,
            AccountId = 500,
            MatchAuthToken = provResp.MatchAuthToken
        };
        AcknowledgeConnectResponse ackResp = await mmService.AcknowledgeMatchConnect(ackReq, context);
        Assert.True(ackResp.Authorized);
        Assert.Equal(0, ackResp.SlotIndex);
    }

    [Fact]
    public async Task GrpcTelemetryStreamService_StreamingAndIngestion_Succeeds()
    {
        Mock<ITelemetryPublisherService> mockTelemetryPub = new();
        mockTelemetryPub.Setup(p => p.OnMatchEndedAsync(It.IsAny<int>())).Returns(Task.CompletedTask);

        GrpcTelemetryStreamService telemetryService = new(
            NullLogger<GrpcTelemetryStreamService>.Instance,
            mockTelemetryPub.Object
        );
        ServerCallContext context = CreateMockServerCallContext();

        // 1. Stream Combat Events
        List<CombatTelemetryFrame> frames =
        [
            new()
            {
                MatchId = 3001,
                FrameIndex = 1,
                GameTimeSeconds = 120,
                TimestampUtcMs = TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds()
            },
            new()
            {
                MatchId = 3001,
                FrameIndex = 2,
                GameTimeSeconds = 121,
                TimestampUtcMs = TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds()
            }
        ];

        MockAsyncStreamReader<CombatTelemetryFrame> streamReader = new(frames);
        StreamSummaryResponse streamSummary = await telemetryService.StreamCombatEvents(streamReader, context);
        Assert.Equal(2UL, streamSummary.TotalFramesReceived);
        Assert.True(streamSummary.IsHealthy);

        // 2. Ingest Match Summary
        MatchSummaryPayload summaryPayload = new()
        {
            MatchId = 3001,
            DurationSeconds = 1845,
            WinningTeam = 1,
            ReplayFileSha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            ReplayFileSizeBytes = 15728640
        };

        IngestSummaryResponse ingestResp = await telemetryService.IngestMatchSummary(summaryPayload, context);
        Assert.True(ingestResp.Success);
        Assert.Contains("3001", ingestResp.StorageUri);
        mockTelemetryPub.Verify(p => p.OnMatchEndedAsync(3001), Times.Once);
    }

    [Fact]
    public async Task GrpcCdnService_ManifestAndDelta_Succeeds()
    {
        Dictionary<string, string?> inMemorySettings = new()
        {
            ["Game:Version"] = "4.10.1.0",
            ["MASTER_SERVER_ADDRESS"] = "http://localhost:55555"
        };
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(inMemorySettings).Build();

        GrpcCdnService cdnService = new(NullLogger<GrpcCdnService>.Instance, config);
        ServerCallContext context = CreateMockServerCallContext();

        // 1. GetManifest
        GetManifestRequest manifestReq = new()
        {
            Variant = "client-win64",
            Channel = "release"
        };
        GetManifestResponse manifestResp = await cdnService.GetManifest(manifestReq, context);
        Assert.Equal("4.10.1.0", manifestResp.GameVersion);
        Assert.NotEmpty(manifestResp.ManifestJson);
        Assert.NotEmpty(manifestResp.Ed25519Signature);
        Assert.NotEmpty(manifestResp.SignerPublicKey);

        // 2. InquireDelta
        InquireDeltaRequest deltaReq = new()
        {
            FilePath = "hon_x64.exe",
            SourceFileSha256 = "aaaa1111",
            TargetFileSha256 = "bbbb2222"
        };
        InquireDeltaResponse deltaResp = await cdnService.InquireDelta(deltaReq, context);
        Assert.True(deltaResp.DeltaAvailable);
        Assert.NotEmpty(deltaResp.DeltaPatchUrl);
        Assert.Equal("zstd", deltaResp.CompressionAlgorithm);
    }

    private static MatchStatistics CreateMatchStatistics(int matchId)
    {
        return new MatchStatistics
        {
            MatchID = matchId,
            ServerID = 1,
            HostAccountName = "HOST",
            Map = "caldavar",
            MapVersion = "1.0",
            TimePlayed = 1200,
            FileSize = 1048576,
            FileName = $"M{matchId}.honreplay",
            ConnectionState = 1,
            Version = "4.10.1",
            AveragePSR = 1500,
            AveragePSRTeamOne = 1500,
            AveragePSRTeamTwo = 1500,
            GameMode = "AllPick",
            ScoreTeam1 = 30,
            ScoreTeam2 = 25,
            TeamScoreGoal = 0,
            PlayerScoreGoal = 0,
            NumberOfRounds = 1,
            ReleaseStage = "release",
            BannedHeroes = "",
            AwardMostAnnihilations = 0,
            AwardMostQuadKills = 0,
            AwardLargestKillStreak = 0,
            AwardMostSmackdowns = 0,
            AwardMostKills = 0,
            AwardMostAssists = 0,
            AwardLeastDeaths = 0,
            AwardMostBuildingDamage = 0,
            AwardMostWardsKilled = 0,
            AwardMostHeroDamageDealt = 0,
            AwardHighestCreepScore = 0
        };
    }

    [Fact]
    public async Task MasterServer_ReplayConverter_SubmitsJobToReplayRenderer_WithCallback()
    {
        string dbName = "ReplayConverterTestDb_" + Guid.NewGuid().ToString("N");
        using MerrickContext dbContext = CreateInMemoryDbContext(dbName);

        MatchStatistics match = CreateMatchStatistics(8888);
        await dbContext.MatchStatistics.AddAsync(match);
        await dbContext.SaveChangesAsync();

        bool renderJobReceived = false;
        int renderJobMatchId = 0;
        string? renderCallbackUrl = null;

        Mock<HttpMessageHandler> mockHandler = new();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage request, CancellationToken token) =>
            {
                if (request.RequestUri?.ToString().Contains("render") == true)
                {
                    renderJobReceived = true;
                    string content = request.Content?.ReadAsStringAsync(token).GetAwaiter().GetResult() ?? "{}";
                    using JsonDocument doc = JsonDocument.Parse(content);
                    if (doc.RootElement.TryGetProperty("matchId", out JsonElement matchElem))
                        renderJobMatchId = matchElem.GetInt32();
                    if (doc.RootElement.TryGetProperty("callbackUrl", out JsonElement cbElem))
                        renderCallbackUrl = cbElem.GetString();

                    return new HttpResponseMessage(HttpStatusCode.Accepted)
                    {
                        Content = new StringContent("{\"status\":\"Queued\"}")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

        HttpClient httpClient = new(mockHandler.Object);
        Mock<IHttpClientFactory> mockFactory = new();
        mockFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        Dictionary<string, string?> inMemorySettings = new()
        {
            ["REPLAY_RENDERER_URL"] = "http://localhost:5556",
            ["MASTER_SERVER_ADDRESS"] = "http://localhost:55555"
        };
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(inMemorySettings).Build();

        IOptions<ReplayConverterSettings> converterOpts = Options.Create(new ReplayConverterSettings());
        IOptions<VideoStorageSettings> storageOpts = Options.Create(new VideoStorageSettings
        {
            LocalDisk = new LocalVideoStorageSettings
            {
                UploadDirectory = Path.Combine(Path.GetTempPath(), "ReplayVideosTest")
            }
        });

        ReplayToVideoConverter converter = new(
            converterOpts,
            storageOpts,
            config,
            mockFactory.Object,
            dbContext,
            NullLogger<ReplayToVideoConverter>.Instance
        );

        string? videoUrl = await converter.ConvertReplayToVideoAsync("8888", "C:\\replays\\M8888.honreplay");

        Assert.Equal("/videos/M8888.mp4", videoUrl);
        Assert.True(renderJobReceived);
        Assert.Equal(8888, renderJobMatchId);
        Assert.Equal("http://localhost:55555/api/replays/render-callback", renderCallbackUrl);
    }

    private class MockAsyncStreamReader<T>(IEnumerable<T> items) : IAsyncStreamReader<T>
    {
        private readonly IEnumerator<T> _enumerator = items.GetEnumerator();

        public T Current => _enumerator.Current;

        public Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            return Task.FromResult(_enumerator.MoveNext());
        }
    }
}
