// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using KONGOR.MasterServer.Services;
using KONGOR.MasterServer.Services.Background;
using KONGOR.MasterServer.Services.Matches;
using Xunit;
using KONGOR.MasterServer.Services.Replay;
using KONGOR.MasterServer.Services.Stats;
using KONGOR.MasterServer.Services.Storage;
using KONGOR.MasterServer.Tests.Infrastructure;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Statistics;
using MERRICK.DatabaseContext.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NEXUS.Contracts.Domain;
using NEXUS.Contracts.Services;
using Polly;
using StackExchange.Redis;

namespace KONGOR.MasterServer.Tests.Services;

public sealed class ReplayZeroByteGuardTests
{
    // protects: Aborted / Linux unrecorded 0-byte matches must not enqueue cloud replay uploads or throw FileNotFoundException into Polly
    // fails_when: ReplayPersistenceWorker calls QueueReplayUploadAsync when FileSize <= 0 and no .honreplay exists on disk
    // seam: Real ReplayPersistenceWorker.FinalizeSideEffectsAsync + MatchSummary disk check
    [Fact]
    public async Task FinalizeSideEffectsAsync_ZeroByteAbortedMatch_SkipsQueueReplayUpload()
    {
        await using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        Mock<IPlayerStatisticsService> statsMock = new();
        Mock<IDatabase> redisMock = new();
        Mock<IReplayAutoUploadService> uploadMock = new();

        ReplayPersistenceWorker worker = new(
            db,
            statsMock.Object,
            redisMock.Object,
            NullLogger<ReplayPersistenceWorker>.Instance,
            uploadMock.Object);

        IngestedMatchContext context = new()
        {
            MatchID = 999888777,
            MatchStatistics = new MatchStatistics
            {
                MatchID = 999888777,
                Map = "midwars",
                FileSize = 0
            },
            MatchType = AccountStatisticsType.MidWars,
            DurationSeconds = 42,
            PlayerAccountNames = [],
            AccountMap = new Dictionary<string, Account>(),
            ProcessedPlayerEntries = []
        };

        await worker.FinalizeSideEffectsAsync(context, matchServer: null);

        uploadMock.Verify(
            u => u.QueueReplayUploadAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // protects: ReplayAutoUploadService.UploadSingleReplayAsync must fast-exit (< 250ms) without throwing into ResiliencePipeline when DB FileSize == 0
    // fails_when: Missing disk replay on a 0-byte match executes _resiliencePipeline or throws FileNotFoundException
    // seam: Real ReplayAutoUploadService + real MerrickContext via IServiceScopeFactory + instrumented ResiliencePipeline
    [Fact]
    public async Task UploadSingleReplayAsync_ZeroByteMatchInDb_FastExitsWithoutEnteringResiliencePipeline()
    {
        string dbName = $"ReplayZeroByte_{Guid.NewGuid():N}";
        await using (MerrickContext seedDb = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            seedDb.MatchStatistics.AddRange(
                new MatchStatistics
                {
                    MatchID = 999888776,
                    Map = "midwars",
                    FileSize = 0
                },
                new MatchStatistics
                {
                    MatchID = 999888778,
                    Map = "midwars",
                    FileSize = 0
                });
            await seedDb.SaveChangesAsync();
        }

        ServiceCollection services = new();
        services.AddScoped(_ => TestDbContextFactory.CreateInMemoryContext(dbName));
        await using ServiceProvider provider = services.BuildServiceProvider();

        int pipelineInvocations = 0;
        ResiliencePipeline spyPipeline = new ResiliencePipelineBuilder()
            .AddRetry(new Polly.Retry.RetryStrategyOptions
            {
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromMilliseconds(10),
                OnRetry = _ =>
                {
                    Interlocked.Increment(ref pipelineInvocations);
                    return ValueTask.CompletedTask;
                }
            })
            .Build();

        ReplayAutoUploadService sut = new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Mock.Of<IReplayStorageProvider>(),
            Mock.Of<IReplayOfflineBufferService>(),
            NullLogger<ReplayAutoUploadService>.Instance,
            spyPipeline);

        // Warm up cold EF Core InMemory LINQ expression compilation before timing steady-state fast-exit
        _ = await sut.UploadSingleReplayAsync(999888776);
        Assert.Equal(0, pipelineInvocations);

        Stopwatch sw = Stopwatch.StartNew();
        bool uploaded = await sut.UploadSingleReplayAsync(999888778);
        sw.Stop();

        Assert.False(uploaded);
        Assert.Equal(0, pipelineInvocations);
        Assert.True(sw.ElapsedMilliseconds < 250, $"Expected fast-exit < 250ms, took {sw.ElapsedMilliseconds}ms");
    }
}
