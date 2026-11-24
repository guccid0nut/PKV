// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using KONGOR.MasterServer.Models.RequestResponse.Stats;
using KONGOR.MasterServer.Services.Replay;
using KONGOR.MasterServer.Services.Storage;
using MERRICK.DatabaseContext.Entities.Statistics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ASPIRE.Tests.KONGOR.MasterServer.Services;

public sealed class ReplayAutoUploadServiceTests : IDisposable
{
    private readonly string _tempReplayDir =
        Path.Combine(Path.GetTempPath(), "NexusReplayTests_" + Guid.NewGuid().ToString("N"));

    public ReplayAutoUploadServiceTests()
    {
        if (NexusTestEnvironment.IsEnabledRaw)
        {
            Directory.CreateDirectory(_tempReplayDir);
            MatchSummary.SetReplayDirectories(_tempReplayDir);
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempReplayDir)) Directory.Delete(_tempReplayDir, true);
        }
        catch
        {
            // Ignore cleanup failures
        }
    }

    private (IServiceScopeFactory scopeFactory, MerrickContext context) CreateInMemoryScope()
    {
        ServiceCollection services = new();
        string dbName = Guid.NewGuid().ToString("N");
        services.AddDbContext<MerrickContext>(opt =>
        {
            if (TestDbContextFactory.IsContainerAvailable && !string.IsNullOrWhiteSpace(TestDbContextFactory.ConnectionString))
            {
                opt.UseNpgsql(TestDbContextFactory.ConnectionString);
            }
            else
            {
                opt.UseInMemoryDatabase(dbName);
                opt.UseModel(TestDbContextFactory.SharedModel);
            }
        });
        ServiceProvider sp = services.BuildServiceProvider();
        IServiceScopeFactory scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        MerrickContext context = sp.GetRequiredService<MerrickContext>();
        context.Database.EnsureCreated();
        return (scopeFactory, context);
    }

    private static MatchStatistics CreateDefaultMatchStatistics(int matchId, string fileName, string? replayUrl = null)
    {
        return new MatchStatistics
        {
            MatchID = matchId,
            ServerID = 1,
            HostAccountName = "HostPlayer",
            Map = "caldavar",
            MapVersion = "1.0",
            TimePlayed = 1200,
            FileSize = 0,
            FileName = fileName,
            ReplayUrl = replayUrl,
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
        };
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task UploadSingleReplayAsync_FindsReplay_UploadsAndUpdatesDatabase()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        // Arrange
        int matchId = 55001;
        string replayFileName = $"M{matchId}.honreplay";
        string replayPath = Path.Combine(_tempReplayDir, replayFileName);
        byte[] dummyBytes = [1, 2, 3, 4, 5, 6, 7, 8];
        await File.WriteAllBytesAsync(replayPath, dummyBytes);

        (IServiceScopeFactory scopeFactory, MerrickContext context) = CreateInMemoryScope();
        MatchStatistics match = CreateDefaultMatchStatistics(matchId, replayFileName);
        await context.MatchStatistics.AddAsync(match);
        await context.SaveChangesAsync();

        string expectedCdnUrl =
            $"https://example.supabase.co/storage/v1/object/public/replays/{replayFileName}";
        MockStorageProvider mockStorage = new(expectedCdnUrl);
        Mock<IReplayOfflineBufferService> mockBuffer = new();
        ReplayAutoUploadService service = new(scopeFactory, mockStorage, mockBuffer.Object,
            NullLogger<ReplayAutoUploadService>.Instance);

        // Act
        bool success = await service.UploadSingleReplayAsync(matchId, replayFileName);

        // Assert
        Assert.True(success);
        Assert.Equal(1, mockStorage.UploadCallCount);
        Assert.Equal(matchId.ToString(), mockStorage.LastMatchId);
        Assert.Equal(replayFileName, mockStorage.LastFileName);

        using IServiceScope verifyScope = scopeFactory.CreateScope();
        MerrickContext verifyContext = verifyScope.ServiceProvider.GetRequiredService<MerrickContext>();
        MatchStatistics? updated = await verifyContext.MatchStatistics.AsNoTracking()
            .FirstOrDefaultAsync(m => m.MatchID == matchId);
        Assert.NotNull(updated);
        Assert.Equal(expectedCdnUrl, updated.ReplayUrl);
        Assert.Equal(dummyBytes.Length, updated.FileSize);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task UploadSingleReplayAsync_ReturnsFalse_WhenReplayNotFound()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        // Arrange
        int matchId = 99999;
        (IServiceScopeFactory scopeFactory, _) = CreateInMemoryScope();
        MockStorageProvider mockStorage = new("https://cdn.example.com/replay.honreplay");
        Mock<IReplayOfflineBufferService> mockBuffer = new();
        ReplayAutoUploadService service = new(scopeFactory, mockStorage, mockBuffer.Object,
            NullLogger<ReplayAutoUploadService>.Instance);

        // Act
        bool success = await service.UploadSingleReplayAsync(matchId);

        // Assert
        Assert.False(success);
        Assert.Equal(0, mockStorage.UploadCallCount);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task ReplayAutoUploadService_QueueReplayUploadAsync_WritesToChannelWithoutExceptions()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        // Arrange
        (IServiceScopeFactory scopeFactory, _) = CreateInMemoryScope();
        MockStorageProvider mockStorage = new("https://cdn.example.com/replay.honreplay");
        Mock<IReplayOfflineBufferService> mockBuffer = new();
        ReplayAutoUploadService service = new(scopeFactory, mockStorage, mockBuffer.Object,
            NullLogger<ReplayAutoUploadService>.Instance);

        // Act & Assert
        await service.QueueReplayUploadAsync(12345, "M12345.honreplay");
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task ReplayAutoUploadService_SyncHistoricalReplaysAsync_UploadsMissingReplays()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        // Arrange
        int matchId1 = 60001;
        int matchId2 = 60002;
        string file1 = $"M{matchId1}.honreplay";
        string file2 = $"M{matchId2}.honreplay";

        await File.WriteAllBytesAsync(Path.Combine(_tempReplayDir, file1), [10, 20]);
        await File.WriteAllBytesAsync(Path.Combine(_tempReplayDir, file2), [30, 40]);

        (IServiceScopeFactory scopeFactory, MerrickContext context) = CreateInMemoryScope();
        await context.MatchStatistics.AddRangeAsync(
            CreateDefaultMatchStatistics(matchId1, file1, "http://localhost:55555/replays/M60001.honreplay"),
            CreateDefaultMatchStatistics(matchId2, file2)
        );
        await context.SaveChangesAsync();

        MockStorageProvider mockStorage = new("https://supabase.co/replays/synced.honreplay");
        Mock<IReplayOfflineBufferService> mockBuffer = new();
        ReplayAutoUploadService service = new(scopeFactory, mockStorage, mockBuffer.Object,
            NullLogger<ReplayAutoUploadService>.Instance);

        // Act
        int synced = await service.SyncHistoricalReplaysAsync([_tempReplayDir]);

        // Assert
        Assert.Equal(2, synced);
        Assert.Equal(2, mockStorage.UploadCallCount);
    }

    private sealed class MockStorageProvider(string returnUrl) : IReplayStorageProvider
    {
        public int UploadCallCount { get; private set; }
        public string? LastMatchId { get; private set; }
        public string? LastFileName { get; private set; }

        public Task<string> UploadReplayAsync(string matchId, Stream fileStream, string fileName,
            CancellationToken cancellationToken = default)
        {
            UploadCallCount++;
            LastMatchId = matchId;
            LastFileName = fileName;
            return Task.FromResult(returnUrl);
        }
    }
}
