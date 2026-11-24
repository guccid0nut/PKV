// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json;
using KONGOR.MasterServer.Configuration;
using KONGOR.MasterServer.Services.Replay;
using KONGOR.MasterServer.Services.Storage;
using MERRICK.DatabaseContext.Entities.Statistics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ASPIRE.Tests.KONGOR.MasterServer.Tests;

public sealed class ReplayOfflineBufferTests : IDisposable

{
    private readonly ReplayOfflineBufferService _bufferService;

    private readonly string _tempDirectory;


    public ReplayOfflineBufferTests()

    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "nexus_offline_replay_test_" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(_tempDirectory);


        Mock<IWebHostEnvironment> mockEnv = new();

        mockEnv.Setup(e => e.ContentRootPath).Returns(_tempDirectory);


        ReplayStorageSettings settings = new()

        {
            LocalDisk = new LocalDiskSettings { UploadDirectory = _tempDirectory }
        };

        IOptions<ReplayStorageSettings> options = Options.Create(settings);


        _bufferService = new ReplayOfflineBufferService(
            mockEnv.Object,
            options,
            NullLogger<ReplayOfflineBufferService>.Instance);
    }


    public void Dispose()

    {
        try

        {
            if (Directory.Exists(_tempDirectory)) Directory.Delete(_tempDirectory, true);
        }

        catch

        {
            // Ignore cleanup failures in temp
        }
    }


    [Fact]
    public async Task ReplayOfflineBuffer_EnqueueAndRetrieve_PersistsReplayAndMetadataToDisk()

    {
        int matchId = 12345;

        string fileName = $"M{matchId}.honreplay";

        byte[] payload = [10, 20, 30, 40, 50];

        using MemoryStream stream = new(payload);


        string savedPath =
            await _bufferService.EnqueuePendingReplayAsync(matchId, fileName, stream, "Simulated Network Timeout");


        Assert.True(File.Exists(savedPath));

        Assert.True(File.Exists(savedPath + ".meta.json"));


        List<PendingReplayMetadata> pending = await _bufferService.GetPendingReplaysAsync();

        Assert.Single(pending);

        Assert.Equal(matchId, pending[0].MatchId);

        Assert.Equal(fileName, pending[0].FileName);

        Assert.Equal(payload.Length, pending[0].FileSizeBytes);

        Assert.Equal(1, pending[0].AttemptCount);

        Assert.Equal("Simulated Network Timeout", pending[0].LastError);


        using Stream? readStream = _bufferService.OpenPendingReplayStream(fileName);

        Assert.NotNull(readStream);

        byte[] readBuffer = new byte[payload.Length];

        int bytesRead = await readStream.ReadAsync(readBuffer);

        Assert.Equal(payload.Length, bytesRead);

        Assert.Equal(payload, readBuffer);
    }


    [Trait("Category", "Manual")]
    [Fact]
    public async Task ReplayOfflineBuffer_UpdatePendingAttempt_IncrementsAttemptCount()

    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        int matchId = 777;

        string fileName = $"M{matchId}.honreplay";

        using MemoryStream stream = new(new byte[100]);


        await _bufferService.EnqueuePendingReplayAsync(matchId, fileName, stream, "Initial failure");

        await _bufferService.UpdatePendingAttemptAsync(fileName, "HTTP 503 Service Unavailable");


        List<PendingReplayMetadata> pending = await _bufferService.GetPendingReplaysAsync();

        Assert.Single(pending);

        Assert.Equal(2, pending[0].AttemptCount);

        Assert.Equal("HTTP 503 Service Unavailable", pending[0].LastError);
    }


    [Fact]
    public async Task ReplayOfflineBuffer_MarkReplayCompleted_RemovesFilesFromDisk()

    {
        int matchId = 888;

        string fileName = $"M{matchId}.honreplay";

        using MemoryStream stream = new(new byte[100]);


        string savedPath = await _bufferService.EnqueuePendingReplayAsync(matchId, fileName, stream, "Initial failure");

        Assert.True(File.Exists(savedPath));


        await _bufferService.MarkReplayCompletedAsync(fileName);


        Assert.False(File.Exists(savedPath));

        Assert.False(File.Exists(savedPath + ".meta.json"));

        List<PendingReplayMetadata> pending = await _bufferService.GetPendingReplaysAsync();

        Assert.Empty(pending);
    }


    [Fact]
    public async Task ReplayAutoUploadService_DrainPendingOfflineReplays_UploadsAndUpdatesDatabase()

    {
        int matchId = 999;

        string fileName = $"M{matchId}.honreplay";

        byte[] payload = [1, 2, 3, 4, 5, 6, 7, 8];

        using MemoryStream stream = new(payload);

        await _bufferService.EnqueuePendingReplayAsync(matchId, fileName, stream, "Supabase unreachable");

        string dbName = "ReplayOfflineBufferTestDb_" + Guid.NewGuid().ToString("N");
        using MerrickContext dbContext = TestDbContextFactory.CreateInMemoryContext(dbName);

        dbContext.MatchStatistics.Add(new MatchStatistics

        {
            MatchID = matchId,

            ServerID = 1,

            HostAccountName = "HostPlayer",

            Map = "caldavar",

            MapVersion = "1.0",

            TimePlayed = 1200,

            FileSize = 0,

            FileName = fileName,

            ReplayUrl = string.Empty,

            ConnectionState = 1,

            Version = "4.10.1",

            AveragePSR = 1500,

            AveragePSRTeamOne = 1500,

            AveragePSRTeamTwo = 1500,

            GameMode = "Ranked",

            ScoreTeam1 = 25,

            ScoreTeam2 = 18,

            TeamScoreGoal = 0,

            PlayerScoreGoal = 0,

            NumberOfRounds = 1,

            ReleaseStage = "Live",

            BannedHeroes = null,

            AwardMostAnnihilations = null,

            AwardMostQuadKills = null,

            AwardLargestKillStreak = null,

            AwardMostSmackdowns = null,

            AwardMostKills = null,

            AwardMostAssists = null,

            AwardLeastDeaths = null,

            AwardMostBuildingDamage = null,

            AwardMostWardsKilled = null,

            AwardMostHeroDamageDealt = null,

            AwardHighestCreepScore = null
        });

        await dbContext.SaveChangesAsync();


        ServiceCollection services = new();

        services.AddScoped(_ => TestDbContextFactory.CreateInMemoryContext(dbName));

        ServiceProvider serviceProvider = services.BuildServiceProvider();

        IServiceScopeFactory scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();


        Mock<IReplayStorageProvider> mockStorage = new();

        mockStorage.Setup(s => s.UploadReplayAsync(matchId.ToString(), It.IsAny<Stream>(), fileName))
            .ReturnsAsync($"https://cloud.supabase.co/storage/v1/object/public/replays/{fileName}");


        ReplayAutoUploadService autoUploadService = new(
            scopeFactory,
            mockStorage.Object,
            _bufferService,
            NullLogger<ReplayAutoUploadService>.Instance);


        // Manually set LastAttemptUtc into the past to trigger exponential backoff readiness

        string metaPath = Path.Combine(_bufferService.BufferDirectory, fileName + ".meta.json");

        string metaJson = await File.ReadAllTextAsync(metaPath);

        PendingReplayMetadata? meta = JsonSerializer.Deserialize<PendingReplayMetadata>(metaJson);

        Assert.NotNull(meta);

        meta.LastAttemptUtc = TimeProvider.System.GetUtcNow().AddMinutes(-5);

        await File.WriteAllTextAsync(metaPath, JsonSerializer.Serialize(meta));


        int drainedCount = await autoUploadService.DrainPendingOfflineReplaysAsync();

        Assert.Equal(1, drainedCount);


        List<PendingReplayMetadata> remainingPending = await _bufferService.GetPendingReplaysAsync();

        Assert.Empty(remainingPending);


        using MerrickContext verifyContext = TestDbContextFactory.CreateInMemoryContext(dbName);

        MatchStatistics? updatedMatch = await verifyContext.MatchStatistics.AsNoTracking()
            .FirstOrDefaultAsync(m => m.MatchID == matchId);

        Assert.NotNull(updatedMatch);

        Assert.Equal($"https://cloud.supabase.co/storage/v1/object/public/replays/{fileName}", updatedMatch.ReplayUrl);

        Assert.Equal(payload.Length, updatedMatch.FileSize);
    }
}
