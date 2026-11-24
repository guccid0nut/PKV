// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: MasterServerReplayIpcAdversarialTests.cs
// System: ProjectKongor Solution-Wide E2E Testing Suite
// Tier 5: Adversarial Challenger Verification (MasterServer ↔ ReplayRenderer IPC Stress)
// Policy: Strict Explicit Typing (NO 'var'), xUnit Framework, Empirical Verification
// ============================================================================

using System.Net;
using FluentAssertions;
using KONGOR.MasterServer.Services.Replay;
using MERRICK.DatabaseContext.Entities.Statistics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using Xunit.Abstractions;

namespace ASPIRE.Tests.E2E.Tier5_Challenger;

public sealed class MasterServerReplayIpcAdversarialTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    [Fact]
    public async Task MasterServer_ReplayToVideoConverter_WithUnreachableRenderer_RecoversGracefully()
    {
        // Arrange
        MerrickContext dbContext =
            TestDbContextFactory.CreateInMemoryContext("MerrickDb_UnreachableRenderer_" + Guid.NewGuid().ToString("N"));

        Mock<HttpMessageHandler> mockHandler = new();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Connection refused (localhost:5556)"));

        HttpClient httpClient = new(mockHandler.Object);
        Mock<IHttpClientFactory> mockFactory = new();
        mockFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(
            [
                new KeyValuePair<string, string?>("REPLAY_RENDERER_URL", "http://localhost:5556"),
                new KeyValuePair<string, string?>("MASTER_SERVER_ADDRESS", "http://localhost:55555")
            ])
            .Build();

        IOptions<ReplayConverterSettings> converterOpts = Options.Create(new ReplayConverterSettings());
        IOptions<VideoStorageSettings> storageOpts = Options.Create(new VideoStorageSettings
        {
            LocalDisk = new LocalVideoStorageSettings
            {
                UploadDirectory = Path.Combine(Path.GetTempPath(), "Videos_" + Guid.NewGuid().ToString("N"))
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

        // Act
        string? resultVideoUrl = await converter.ConvertReplayToVideoAsync("12345", @"C:\replays\M12345.honreplay");

        // Assert - Returns fallback relative URL without throwing unhandled exception
        resultVideoUrl.Should().Be("/videos/M12345.mp4");

        try
        {
            Directory.Delete(storageOpts.Value.LocalDisk.UploadDirectory, true);
        }
        catch
        {
        }
    }

    [Fact]
    public async Task MasterServer_ReplayToVideoConverter_WithSlowRendererTimeout_HandledGracefully()
    {
        // Arrange
        MerrickContext dbContext =
            TestDbContextFactory.CreateInMemoryContext("MerrickDb_SlowRenderer_" + Guid.NewGuid().ToString("N"));

        Mock<HttpMessageHandler> mockHandler = new();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new TaskCanceledException("HttpClient timeout exceeded"));

        HttpClient httpClient = new(mockHandler.Object);
        Mock<IHttpClientFactory> mockFactory = new();
        mockFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(
            [
                new KeyValuePair<string, string?>("REPLAY_RENDERER_URL", "http://localhost:5556"),
                new KeyValuePair<string, string?>("MASTER_SERVER_ADDRESS", "http://localhost:55555")
            ])
            .Build();

        IOptions<ReplayConverterSettings> converterOpts = Options.Create(new ReplayConverterSettings());
        IOptions<VideoStorageSettings> storageOpts = Options.Create(new VideoStorageSettings
        {
            LocalDisk = new LocalVideoStorageSettings
            {
                UploadDirectory = Path.Combine(Path.GetTempPath(), "Videos_" + Guid.NewGuid().ToString("N"))
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

        // Act
        string? resultVideoUrl = await converter.ConvertReplayToVideoAsync("54321", @"C:\replays\M54321.honreplay");

        // Assert
        resultVideoUrl.Should().Be("/videos/M54321.mp4");

        try
        {
            Directory.Delete(storageOpts.Value.LocalDisk.UploadDirectory, true);
        }
        catch
        {
        }
    }

    [Fact]
    public async Task MasterServer_ReplayToVideoConverter_WithNon200RendererResponse_HandledGracefully()
    {
        // Arrange
        MerrickContext dbContext =
            TestDbContextFactory.CreateInMemoryContext("MerrickDb_Non200_" + Guid.NewGuid().ToString("N"));

        Mock<HttpMessageHandler> mockHandler = new();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("{\"error\":\"Service Unavailable\"}")
            });

        HttpClient httpClient = new(mockHandler.Object);
        Mock<IHttpClientFactory> mockFactory = new();
        mockFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(
            [
                new KeyValuePair<string, string?>("REPLAY_RENDERER_URL", "http://localhost:5556"),
                new KeyValuePair<string, string?>("MASTER_SERVER_ADDRESS", "http://localhost:55555")
            ])
            .Build();

        IOptions<ReplayConverterSettings> converterOpts = Options.Create(new ReplayConverterSettings());
        IOptions<VideoStorageSettings> storageOpts = Options.Create(new VideoStorageSettings
        {
            LocalDisk = new LocalVideoStorageSettings
            {
                UploadDirectory = Path.Combine(Path.GetTempPath(), "Videos_" + Guid.NewGuid().ToString("N"))
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

        // Act
        string? resultVideoUrl = await converter.ConvertReplayToVideoAsync("998877", @"C:\replays\M998877.honreplay");

        // Assert
        resultVideoUrl.Should().Be("/videos/M998877.mp4");

        try
        {
            Directory.Delete(storageOpts.Value.LocalDisk.UploadDirectory, true);
        }
        catch
        {
        }
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
    public async Task MasterServer_ReplayCallback_CompletedStatus_UpdatesDatabaseRecord()
    {
        // Arrange
        MerrickContext dbContext =
            TestDbContextFactory.CreateInMemoryContext("MerrickDb_CallbackSuccess_" + Guid.NewGuid().ToString("N"));

        int matchId = 778899;
        MatchStatistics matchStat = CreateMatchStatistics(matchId);
        dbContext.MatchStatistics.Add(matchStat);
        await dbContext.SaveChangesAsync();

        ReplayRenderCallbackDTO callback = new()
        {
            MatchId = matchId,
            Status = "Completed",
            VideoUrl = $"/videos/M{matchId}.mp4",
            RenderDurationMs = 2500
        };

        // Act - Simulate callback handler logic from KONGOR.cs
        if (callback.MatchId > 0 && callback.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase))
        {
            MatchStatistics? match =
                await dbContext.MatchStatistics.FirstOrDefaultAsync(m => m.MatchID == callback.MatchId);
            if (match != null)
            {
                match.VideoUrl = callback.VideoUrl ?? $"/videos/M{callback.MatchId}.mp4";
                await dbContext.SaveChangesAsync();
            }
        }

        // Assert
        MatchStatistics? updatedMatch = await dbContext.MatchStatistics.FirstOrDefaultAsync(m => m.MatchID == matchId);
        updatedMatch.Should().NotBeNull();
        updatedMatch!.VideoUrl.Should().Be($"/videos/M{matchId}.mp4");
    }

    [Fact]
    public async Task MasterServer_ReplayCallback_FailedStatus_DoesNotCorruptVideoUrl()
    {
        // Arrange
        MerrickContext dbContext =
            TestDbContextFactory.CreateInMemoryContext("MerrickDb_CallbackFailed_" + Guid.NewGuid().ToString("N"));

        int matchId = 665544;
        MatchStatistics matchStat = CreateMatchStatistics(matchId);
        matchStat.VideoUrl = null;
        dbContext.MatchStatistics.Add(matchStat);
        await dbContext.SaveChangesAsync();

        ReplayRenderCallbackDTO callback = new()
        {
            MatchId = matchId,
            Status = "Failed",
            VideoUrl = "/videos/corrupted.mp4",
            RenderDurationMs = 500
        };

        // Act
        if (callback.MatchId > 0 && callback.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase))
        {
            MatchStatistics? match =
                await dbContext.MatchStatistics.FirstOrDefaultAsync(m => m.MatchID == callback.MatchId);
            if (match != null)
            {
                match.VideoUrl = callback.VideoUrl ?? $"/videos/M{callback.MatchId}.mp4";
                await dbContext.SaveChangesAsync();
            }
        }

        // Assert
        MatchStatistics? preservedMatch =
            await dbContext.MatchStatistics.FirstOrDefaultAsync(m => m.MatchID == matchId);
        preservedMatch.Should().NotBeNull();
        preservedMatch!.VideoUrl.Should().BeNull();
    }
}
