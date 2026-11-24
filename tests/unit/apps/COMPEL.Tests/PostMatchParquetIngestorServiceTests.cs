// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: PostMatchParquetIngestorServiceTests.cs
// System: COMPEL Unit Tests - Post Match Parquet Ingestor Service
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using COMPEL.Configuration;
using COMPEL.Services.Supervision;
using COMPEL.Services.Telemetry;
using Kongor.Core.Analytics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace COMPEL.Tests;

public sealed class PostMatchParquetIngestorServiceTests
{
    private static ArtefactsLocator CreateArtefactsLocator(string directory)
    {
        return new ArtefactsLocator(Options.Create(new MatchServerManagerOptions
        {
            RuntimeArtefactsPath = directory
        }));
    }

    [Fact]
    public async Task ParseCombatLogAsync_SampleCombatLog_ParsesAllEventTypesCorrectly()
    {
        // Arrange
        string tempLogFile = Path.GetTempFileName();
        try
        {
            StringBuilder sb = new();
            sb.AppendLine("Sv: [12:16:50] Client #0 selected hero Hero_Witchslayer");
            sb.AppendLine("Sv: [12:16:51] Client #1 selected hero Hero_Pyromancer");
            sb.AppendLine("KILL time:60000 x:5000.0 y:5000.0 z:0.0 team:1 target:\"Hero_Pyromancer\" attacker:\"Hero_Witchslayer\"");
            sb.AppendLine("ABILITY_ACTIVATE time:45000 x:100.0 y:200.0 z:0.0 player:0 team:1 ability:\"Ability_WitchSlayer1\"");
            sb.AppendLine("ITEM_ACTIVATE time:90000 x:4000.0 y:12000.0 z:50.0 player:0 team:1 item:\"Item_FlamingEye\"");
            sb.AppendLine("ITEM_PURCHASE time:12000 player:1 team:2 item:\"Item_Marchers\"");
            sb.AppendLine("GOLD_EARNED time:34500 x:4500.0 y:6500.0 z:100.0 player:0 team:1 gold:250");

            await File.WriteAllTextAsync(tempLogFile, sb.ToString(), Encoding.UTF8);

            ArtefactsLocator artefacts = CreateArtefactsLocator(Path.GetTempPath());
            MatchParquetExportService exportService = new(NullLogger<MatchParquetExportService>.Instance);
            MatchLakehouseStorageUploader uploader = new((HttpClient?)null, NullLogger<MatchLakehouseStorageUploader>.Instance, LakehouseStorageSettings.Unconfigured);
            PostMatchParquetIngestorService ingestor = new(
                null,
                exportService,
                uploader,
                artefacts,
                NullLogger<PostMatchParquetIngestorService>.Instance);

            // Act
            List<MatchCombatEventRecord> events = await ingestor.ParseCombatLogAsync(123456, tempLogFile);

            // Assert
            Assert.NotEmpty(events);
            Assert.Equal(5, events.Count); // Kill, Ability, Item(Ward), ItemPurchased, GoldTick

            // Verify Kill event
            MatchCombatEventRecord killEvent = events[0];
            Assert.Equal(123456, killEvent.MatchId);
            Assert.Equal(60000, killEvent.MatchTimeMs);
            Assert.Equal(CombatEventType.HeroKill, killEvent.EventType);
            Assert.Equal(35, killEvent.SourceHeroId); // Witchslayer is 35
            Assert.Equal(42, killEvent.TargetHeroId); // Pyromancer is 42
            Assert.Equal(5000.0f, killEvent.PositionX);
            Assert.Equal(5000.0f, killEvent.PositionY);

            // Verify Ability event
            MatchCombatEventRecord abilityEvent = events[1];
            Assert.Equal(45000, abilityEvent.MatchTimeMs);
            Assert.Equal(CombatEventType.AbilityCast, abilityEvent.EventType);
            Assert.Equal(35, abilityEvent.SourceHeroId);

            // Verify Ward Placed event
            MatchCombatEventRecord wardEvent = events[2];
            Assert.Equal(90000, wardEvent.MatchTimeMs);
            Assert.Equal(CombatEventType.WardPlaced, wardEvent.EventType);
            Assert.Equal(4000.0f, wardEvent.PositionX);
            Assert.Equal(12000.0f, wardEvent.PositionY);

            // Verify Item Purchased event
            MatchCombatEventRecord itemEvent = events[3];
            Assert.Equal(12000, itemEvent.MatchTimeMs);
            Assert.Equal(CombatEventType.ItemPurchased, itemEvent.EventType);
            Assert.Equal(42, itemEvent.SourceHeroId); // player 1 is Pyromancer

            // Verify Gold event
            MatchCombatEventRecord goldEvent = events[4];
            Assert.Equal(34500, goldEvent.MatchTimeMs);
            Assert.Equal(CombatEventType.GoldTick, goldEvent.EventType);
            Assert.Equal(250f, goldEvent.EventValue);
        }
        finally
        {
            if (File.Exists(tempLogFile)) File.Delete(tempLogFile);
        }
    }

    [Fact]
    public async Task IngestMatchAsync_WithExplicitLogFile_CreatesParquetAndEnqueuesUpload()
    {
        // Arrange
        string tempLogFile = Path.GetTempFileName();
        string baseDir = AppContext.BaseDirectory;
        string expectedParquetFile = Path.Combine(baseDir, "data", "analytics", "replays", "match_998811.parquet");

        try
        {
            StringBuilder sb = new();
            sb.AppendLine("Sv: [12:16:50] Client #0 selected hero Hero_Devourer");
            sb.AppendLine("KILL time:50000 x:3000.0 y:4000.0 z:0.0 team:1 target:\"Hero_Flint\" attacker:\"Hero_Devourer\"");
            await File.WriteAllTextAsync(tempLogFile, sb.ToString(), Encoding.UTF8);

            ArtefactsLocator artefacts = CreateArtefactsLocator(Path.GetTempPath());
            MatchParquetExportService exportService = new(NullLogger<MatchParquetExportService>.Instance);
            MatchLakehouseStorageUploader uploader = new((HttpClient?)null, NullLogger<MatchLakehouseStorageUploader>.Instance, LakehouseStorageSettings.Unconfigured);
            PostMatchParquetIngestorService ingestor = new(
                null,
                exportService,
                uploader,
                artefacts,
                NullLogger<PostMatchParquetIngestorService>.Instance);

            // Act
            bool result = await ingestor.IngestMatchAsync(998811, explicitLogPath: tempLogFile);

            // Assert
            Assert.True(result);
            Assert.True(File.Exists(expectedParquetFile), $"Expected Parquet file does not exist at {expectedParquetFile}");

            byte[] parquetBytes = await File.ReadAllBytesAsync(expectedParquetFile);
            Assert.True(parquetBytes.Length > 8);
            // Parquet magic check "PAR1"
            Assert.Equal((byte)'P', parquetBytes[0]);
            Assert.Equal((byte)'A', parquetBytes[1]);
            Assert.Equal((byte)'R', parquetBytes[2]);
            Assert.Equal((byte)'1', parquetBytes[3]);

            // Attempting to ingest again should be deduplicated and return true without exception
            bool secondIngest = await ingestor.IngestMatchAsync(998811, explicitLogPath: tempLogFile);
            Assert.True(secondIngest);
        }
        finally
        {
            if (File.Exists(tempLogFile)) File.Delete(tempLogFile);
            if (File.Exists(expectedParquetFile)) File.Delete(expectedParquetFile);
        }
    }

    [Fact]
    public async Task HandleMatchEndedAsync_ValidWorkerWithMatchId_TriggersIngestion()
    {
        // Arrange
        ArtefactsLocator artefacts = CreateArtefactsLocator(Path.GetTempPath());
        MatchParquetExportService exportService = new(NullLogger<MatchParquetExportService>.Instance);
        MatchLakehouseStorageUploader uploader = new((HttpClient?)null, NullLogger<MatchLakehouseStorageUploader>.Instance, LakehouseStorageSettings.Unconfigured);
        PostMatchParquetIngestorService ingestor = new(
            null,
            exportService,
            uploader,
            artefacts,
            NullLogger<PostMatchParquetIngestorService>.Instance);

        DedicatedServerWorker worker = new(
            instanceIndex: 1,
            port: 11235,
            voicePort: 11435,
            publicGamePort: 11235,
            publicVoicePort: 11435,
            serverName: "TestWorkerServer");

        worker.MatchId = 776655;

        string baseDir = AppContext.BaseDirectory;
        string expectedParquetFile = Path.Combine(baseDir, "data", "analytics", "replays", "match_776655.parquet");

        try
        {
            // Act - Ingest with fallback minimal event since no log file exists on disk
            await ingestor.HandleMatchEndedAsync(worker);

            // Assert
            Assert.True(File.Exists(expectedParquetFile));
            byte[] bytes = await File.ReadAllBytesAsync(expectedParquetFile);
            Assert.True(bytes.Length > 0);
        }
        finally
        {
            if (File.Exists(expectedParquetFile)) File.Delete(expectedParquetFile);
        }
    }

    [Fact]
    public async Task HandleMatchEndedAsync_WorkerWithZeroMatchId_IgnoredSafely()
    {
        // Arrange
        ArtefactsLocator artefacts = CreateArtefactsLocator(Path.GetTempPath());
        MatchParquetExportService exportService = new(NullLogger<MatchParquetExportService>.Instance);
        MatchLakehouseStorageUploader uploader = new((HttpClient?)null, NullLogger<MatchLakehouseStorageUploader>.Instance, LakehouseStorageSettings.Unconfigured);
        PostMatchParquetIngestorService ingestor = new(
            null,
            exportService,
            uploader,
            artefacts,
            NullLogger<PostMatchParquetIngestorService>.Instance);

        DedicatedServerWorker worker = new(
            instanceIndex: 1,
            port: 11235,
            voicePort: 11435,
            publicGamePort: 11235,
            publicVoicePort: 11435,
            serverName: "TestWorkerServer");

        worker.MatchId = 0; // Unassigned match

        // Act & Assert (Should not throw and should not produce any output)
        await ingestor.HandleMatchEndedAsync(worker);
    }

    [Fact]
    public async Task QueueArcadeMode_SkipsRankedParquetIngest_AndSkipsStartupWalRecoveryScan()
    {
        // protects: Ranked WAL journal integrity and zero S3 Lakehouse upload noise when COMPEL runs in QueueArcadeMode.
        // fails_when: QueueArcadeMode COMPEL instances scavenge unfinalized Ranked .wal files or export/upload bot warm-up matches.
        // seam: Real PostMatchParquetIngestorService + ReplayWalRecoveryService with QueueArcadeMode=true on temp WAL/Parquet directories.
        string tempRepoRoot = Path.Combine(Path.GetTempPath(), "CompelArcadeIsolation_" + Guid.NewGuid().ToString("N"));
        string walDir = Path.Combine(tempRepoRoot, "data", "analytics", "wal");
        Directory.CreateDirectory(walDir);
        string rankedWalFile = Path.Combine(walDir, "match_554433_port11235.wal");
        await File.WriteAllBytesAsync(rankedWalFile, [0x4B, 0x57, 0x41, 0x4C, 0x01, 0x02]);

        string expectedArcadeParquet = Path.Combine(AppContext.BaseDirectory, "data", "analytics", "replays", "match_887766.parquet");
        if (File.Exists(expectedArcadeParquet))
        {
            File.Delete(expectedArcadeParquet);
        }

        try
        {
            IOptions<MatchServerManagerOptions> arcadeOptions = Options.Create(new MatchServerManagerOptions
            {
                QueueArcadeMode = true,
                EnableReplayWal = true,
                RuntimeArtefactsPath = tempRepoRoot
            });

            ArtefactsLocator artefacts = new(arcadeOptions);
            MatchParquetExportService exportService = new(NullLogger<MatchParquetExportService>.Instance);
            MatchLakehouseStorageUploader uploader = new((HttpClient?)null, NullLogger<MatchLakehouseStorageUploader>.Instance, LakehouseStorageSettings.Unconfigured);
            PostMatchParquetIngestorService ingestor = new(
                null,
                exportService,
                uploader,
                artefacts,
                NullLogger<PostMatchParquetIngestorService>.Instance,
                arcadeOptions);

            DedicatedServerWorker arcadeWorker = new(
                instanceIndex: 1,
                port: 11485,
                voicePort: 11585,
                publicGamePort: 11485,
                publicVoicePort: 11585,
                serverName: "QUEUE ARCADE #Q1")
            {
                MatchId = 887766
            };

            await ingestor.StartAsync(CancellationToken.None);
            await ingestor.HandleMatchEndedAsync(arcadeWorker);
            await ingestor.StopAsync(CancellationToken.None);

            Assert.False(File.Exists(expectedArcadeParquet));

            using ReplayWalRecoveryService walRecovery = new(
                arcadeOptions,
                supervisor: null,
                ingestor: ingestor,
                logger: NullLogger<ReplayWalRecoveryService>.Instance,
                repoRoot: tempRepoRoot);

            await walRecovery.StartAsync(CancellationToken.None);
            await Task.Delay(50);
            await walRecovery.StopAsync(CancellationToken.None);

            // Ranked .wal file on port 11235 must remain completely untouched by QueueArcadeMode recovery!
            Assert.True(File.Exists(rankedWalFile));
        }
        finally
        {
            if (File.Exists(expectedArcadeParquet)) File.Delete(expectedArcadeParquet);
            if (Directory.Exists(tempRepoRoot)) Directory.Delete(tempRepoRoot, true);
        }
    }
}
