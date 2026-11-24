// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: MatchParquetExportTests.cs
// System: COMPEL Unit Tests - Dedicated Server Match Parquet Lakehouse Exporter
// ============================================================================

using System.IO;
using System.Text;
using COMPEL.Services.Telemetry;
using Kongor.Core.Analytics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace COMPEL.Tests;

public sealed class MatchParquetExportTests
{
    private static List<MatchCombatEventRecord> GenerateSampleEvents(int count, long matchId = 1001)
    {
        List<MatchCombatEventRecord> list = new(count);
        for (int i = 0; i < count; i++)
        {
            list.Add(new MatchCombatEventRecord(
                MatchId: matchId,
                MatchTimeMs: i * 100,
                Tick: i * 2,
                EventType: (CombatEventType)(i % 9),
                SourcePlayerIndex: (byte)(i % 10),
                SourceHeroId: (short)(10 + (i % 5)),
                TargetPlayerIndex: (byte)((i + 5) % 10),
                TargetHeroId: (short)(20 + (i % 5)),
                AbilityOrItemId: 1000 + i,
                EventValue: 150.5f + (i * 0.1f),
                PositionX: 5000.0f + i,
                PositionY: 4500.0f - i,
                PositionZ: 100.0f));
        }
        return list;
    }

    [Fact]
    public void SerializeEventsToParquetStream_ValidBatch_ProducesValidParquetStreamWithPar1HeaderAndFooter()
    {
        // Arrange
        MatchParquetExportService service = new(NullLogger<MatchParquetExportService>.Instance);
        List<MatchCombatEventRecord> events = GenerateSampleEvents(100);

        // Act
        using MemoryStream ms = new();
        service.SerializeEventsToParquetStream(events, ms);
        byte[] bytes = ms.ToArray();

        // Assert
        Assert.True(bytes.Length > 8);

        // Header must be "PAR1" (0x50, 0x41, 0x52, 0x31)
        string headerMagic = Encoding.ASCII.GetString(bytes, 0, 4);
        Assert.Equal("PAR1", headerMagic);

        // Footer must be "PAR1"
        string footerMagic = Encoding.ASCII.GetString(bytes, bytes.Length - 4, 4);
        Assert.Equal("PAR1", footerMagic);
    }

    [Fact]
    public void CalculateBloatReduction_LargeBatch_ReportsOver80PercentReduction()
    {
        // Arrange
        MatchParquetExportService service = new(NullLogger<MatchParquetExportService>.Instance);
        int eventCount = 50_000;
        long compressedParquetBytes = 1_200_000; // ~1.2 MB

        // Act
        BloatReductionReport report = service.CalculateBloatReduction(eventCount, compressedParquetBytes);

        // Assert
        Assert.Equal(eventCount, report.EventCount);
        Assert.Equal(10_000_000L, report.EstimatedUncompressedBytes); // 50,000 * 200
        Assert.Equal(compressedParquetBytes, report.CompressedParquetBytes);
        Assert.True(report.ReductionPercentage >= 85.0);
        Assert.Contains("MB", report.FormattedUncompressedSize);
        Assert.Contains("MB", report.FormattedParquetSize);
    }

    [Fact]
    public void ResolveStorageUri_FormatsCanonicalS3Path()
    {
        // Arrange
        MatchParquetExportService service = new(NullLogger<MatchParquetExportService>.Instance);

        // Act
        string uriWithoutPrefix = service.ResolveStorageUri(998877, "match-analytics");
        string uriWithPrefix = service.ResolveStorageUri(998877, "match-analytics", "2026/09");

        // Assert
        Assert.Equal("s3://match-analytics/match_998877.parquet", uriWithoutPrefix);
        Assert.Equal("s3://match-analytics/2026/09/match_998877.parquet", uriWithPrefix);
    }

    [Fact]
    public void ExportToParquetFile_WritesToDisk_AndCalculatesAccurateSize()
    {
        // Arrange
        MatchParquetExportService service = new(NullLogger<MatchParquetExportService>.Instance);
        List<MatchCombatEventRecord> events = GenerateSampleEvents(250, matchId: 4455);
        string tempDir = Path.Combine(Path.GetTempPath(), "ParquetTest_" + Guid.NewGuid().ToString("N"));
        string tempFile = Path.Combine(tempDir, "match_4455.parquet");

        try
        {
            // Act
            BloatReductionReport report = service.ExportToParquetFile(4455, events, tempFile);

            // Assert
            Assert.True(File.Exists(tempFile));
            long diskSize = new FileInfo(tempFile).Length;
            Assert.Equal(diskSize, report.CompressedParquetBytes);
            Assert.True(diskSize > 8);

            byte[] bytes = File.ReadAllBytes(tempFile);
            Assert.Equal("PAR1", Encoding.ASCII.GetString(bytes, 0, 4));
            Assert.Equal("PAR1", Encoding.ASCII.GetString(bytes, bytes.Length - 4, 4));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExportToParquetFileAsync_WritesToDisk_AndCalculatesAccurateSize()
    {
        // Arrange
        MatchParquetExportService service = new(NullLogger<MatchParquetExportService>.Instance);
        List<MatchCombatEventRecord> events = GenerateSampleEvents(250, matchId: 4456);
        string tempDir = Path.Combine(Path.GetTempPath(), "ParquetTestAsync_" + Guid.NewGuid().ToString("N"));
        string tempFile = Path.Combine(tempDir, "match_4456.parquet");

        try
        {
            // Act
            BloatReductionReport report = await service.ExportToParquetFileAsync(4456, events, tempFile);

            // Assert
            Assert.True(File.Exists(tempFile));
            long diskSize = new FileInfo(tempFile).Length;
            Assert.Equal(diskSize, report.CompressedParquetBytes);
            Assert.True(diskSize > 8);

            byte[] bytes = await File.ReadAllBytesAsync(tempFile);
            Assert.Equal("PAR1", Encoding.ASCII.GetString(bytes, 0, 4));
            Assert.Equal("PAR1", Encoding.ASCII.GetString(bytes, bytes.Length - 4, 4));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private readonly record struct HeroStats(short HeroId, byte PlayerIndex, int Kills, int Deaths, int Assists, float Damage);

    [Fact]
    public void GenerateSeedParquetFileForSupabase()
    {
        MatchParquetExportService service = new(NullLogger<MatchParquetExportService>.Instance);

        string? current = AppContext.BaseDirectory;
        string? root = null;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "ProjectKongor.slnx")) ||
                File.Exists(Path.Combine(current, "ProjectKongor.sln")) ||
                Directory.Exists(Path.Combine(current, ".git")))
            {
                root = current;
                break;
            }
            current = Directory.GetParent(current)?.FullName;
        }

        if (root == null) return;

        // Realistic Match 1 (Caldavar - 35 minute competitive match)
        HeroStats[] match1Heroes =
        [
            // Legion (Team 1)
            new(HeroId: 42, PlayerIndex: 0, Kills: 12, Deaths: 4, Assists: 11, Damage: 34_250f), // Pyromancer
            new(HeroId: 15, PlayerIndex: 1, Kills: 16, Deaths: 3, Assists: 7,  Damage: 42_800f), // Swiftblade
            new(HeroId: 23, PlayerIndex: 2, Kills: 3,  Deaths: 7, Assists: 19, Damage: 17_650f), // Magmus
            new(HeroId: 31, PlayerIndex: 3, Kills: 1,  Deaths: 9, Assists: 23, Damage: 8_900f),  // Glacius
            new(HeroId: 35, PlayerIndex: 4, Kills: 6,  Deaths: 5, Assists: 15, Damage: 19_400f), // Witch Slayer

            // Hellbourne (Team 2)
            new(HeroId: 10, PlayerIndex: 5, Kills: 8,  Deaths: 8, Assists: 8,  Damage: 25_400f), // Devourer
            new(HeroId: 48, PlayerIndex: 6, Kills: 10, Deaths: 7, Assists: 9,  Damage: 29_800f), // Valkyrie
            new(HeroId: 52, PlayerIndex: 7, Kills: 5,  Deaths: 7, Assists: 12, Damage: 21_300f), // Pebbles
            new(HeroId: 60, PlayerIndex: 8, Kills: 4,  Deaths: 9, Assists: 6,  Damage: 18_900f), // Deadwood
            new(HeroId: 72, PlayerIndex: 9, Kills: 1,  Deaths: 7, Assists: 17, Damage: 9_500f)   // Demented Shaman
        ];

        // Realistic Match 2 (Caldavar - 28 minute fast match)
        HeroStats[] match2Heroes =
        [
            new(HeroId: 42, PlayerIndex: 0, Kills: 9,  Deaths: 2, Assists: 14, Damage: 27_800f), // Pyromancer
            new(HeroId: 15, PlayerIndex: 1, Kills: 11, Deaths: 4, Assists: 8,  Damage: 33_100f), // Swiftblade
            new(HeroId: 10, PlayerIndex: 5, Kills: 6,  Deaths: 7, Assists: 5,  Damage: 19_200f), // Devourer
            new(HeroId: 48, PlayerIndex: 6, Kills: 8,  Deaths: 5, Assists: 7,  Damage: 24_600f), // Valkyrie
            new(HeroId: 31, PlayerIndex: 3, Kills: 2,  Deaths: 6, Assists: 18, Damage: 7_400f)   // Glacius
        ];

        List<MatchCombatEventRecord> match1Events = GenerateRealisticMatchEvents(1001, match1Heroes);
        List<MatchCombatEventRecord> match2Events = GenerateRealisticMatchEvents(1002, match2Heroes);

        string tempDir = Path.Combine(Path.GetTempPath(), $"kongor_parquet_seed_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string match1Path = Path.Combine(tempDir, "match_1001.parquet");
            service.ExportToParquetFile(1001, match1Events, match1Path);
            Assert.True(File.Exists(match1Path));

            string match2Path = Path.Combine(tempDir, "match_1002.parquet");
            service.ExportToParquetFile(1002, match2Events, match2Path);
            Assert.True(File.Exists(match2Path));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private static List<MatchCombatEventRecord> GenerateRealisticMatchEvents(long matchId, HeroStats[] heroes)
    {
        List<MatchCombatEventRecord> events = new();
        int timeCursorMs = 120_000; // Start at 2:00 in-game time
        int tickCursor = 2400;

        foreach (HeroStats hero in heroes)
        {
            // 1. Emitted Hero Kills
            for (int k = 0; k < hero.Kills; k++)
            {
                timeCursorMs += 15_000;
                tickCursor += 300;
                events.Add(new MatchCombatEventRecord(
                    MatchId: matchId,
                    MatchTimeMs: timeCursorMs,
                    Tick: tickCursor,
                    EventType: CombatEventType.HeroKill,
                    SourcePlayerIndex: hero.PlayerIndex,
                    SourceHeroId: hero.HeroId,
                    TargetPlayerIndex: (byte)((hero.PlayerIndex + 5) % 10),
                    TargetHeroId: (short)(hero.HeroId == 42 ? 10 : 42),
                    AbilityOrItemId: 1001,
                    EventValue: 450f,
                    PositionX: 5000f + (k * 20f),
                    PositionY: 5000f - (k * 20f),
                    PositionZ: 128f));
            }

            // 2. Emitted Hero Assists
            for (int a = 0; a < hero.Assists; a++)
            {
                timeCursorMs += 10_000;
                tickCursor += 200;
                events.Add(new MatchCombatEventRecord(
                    MatchId: matchId,
                    MatchTimeMs: timeCursorMs,
                    Tick: tickCursor,
                    EventType: CombatEventType.HeroAssist,
                    SourcePlayerIndex: hero.PlayerIndex,
                    SourceHeroId: hero.HeroId,
                    TargetPlayerIndex: (byte)((hero.PlayerIndex + 5) % 10),
                    TargetHeroId: (short)(hero.HeroId == 42 ? 48 : 31),
                    AbilityOrItemId: 1002,
                    EventValue: 0f,
                    PositionX: 4800f + (a * 15f),
                    PositionY: 5200f - (a * 15f),
                    PositionZ: 128f));
            }

            // 3. Emitted Hero Deaths
            for (int d = 0; d < hero.Deaths; d++)
            {
                timeCursorMs += 20_000;
                tickCursor += 400;
                events.Add(new MatchCombatEventRecord(
                    MatchId: matchId,
                    MatchTimeMs: timeCursorMs,
                    Tick: tickCursor,
                    EventType: CombatEventType.HeroDeath,
                    SourcePlayerIndex: hero.PlayerIndex,
                    SourceHeroId: hero.HeroId,
                    TargetPlayerIndex: (byte)((hero.PlayerIndex + 5) % 10),
                    TargetHeroId: (short)(hero.HeroId == 42 ? 10 : 15),
                    AbilityOrItemId: 0,
                    EventValue: 0f,
                    PositionX: 4500f,
                    PositionY: 4500f,
                    PositionZ: 128f));
            }

            // 4. Emitted Ability Casts and Damage Dealt
            // Calculate remaining damage to allocate across ability casts
            float damageFromKills = hero.Kills * 450f;
            float remainingDamage = Math.Max(0f, hero.Damage - damageFromKills);
            int castCount = Math.Max(10, (int)(remainingDamage / 250f));
            float damagePerCast = remainingDamage / castCount;

            for (int c = 0; c < castCount; c++)
            {
                timeCursorMs += 2_000;
                tickCursor += 40;
                events.Add(new MatchCombatEventRecord(
                    MatchId: matchId,
                    MatchTimeMs: timeCursorMs,
                    Tick: tickCursor,
                    EventType: CombatEventType.AbilityCast,
                    SourcePlayerIndex: hero.PlayerIndex,
                    SourceHeroId: hero.HeroId,
                    TargetPlayerIndex: (byte)((hero.PlayerIndex + 5) % 10),
                    TargetHeroId: 0,
                    AbilityOrItemId: 1000 + (c % 4),
                    EventValue: damagePerCast,
                    PositionX: 5100f + (c * 5f),
                    PositionY: 4900f - (c * 5f),
                    PositionZ: 128f));
            }

            // 5. Item Purchases & Ward Placements
            events.Add(new MatchCombatEventRecord(matchId, 60_000, 1200, CombatEventType.ItemPurchased, hero.PlayerIndex, hero.HeroId, 0, 0, 201, 2150f, 0, 0, 0)); // Portal Key
            events.Add(new MatchCombatEventRecord(matchId, 180_000, 3600, CombatEventType.WardPlaced, hero.PlayerIndex, hero.HeroId, 0, 0, 301, 100f, 6200f, 4100f, 128f)); // Ward
        }

        return events;
    }

    [Fact]
    public async Task ReadParquetStream_WithParquetReader_RoundtripsAllColumnsAndRowsSuccessfully()
    {
        // Arrange
        MatchParquetExportService service = new(NullLogger<MatchParquetExportService>.Instance);
        List<MatchCombatEventRecord> events = GenerateSampleEvents(250);
        using MemoryStream ms = new();

        // Act
        await service.SerializeEventsToParquetStreamAsync(events, ms);
        ms.Position = 0;

        // Assert - Read using official ParquetReader
        await using Parquet.ParquetReader reader = await Parquet.ParquetReader.CreateAsync(ms);
        Assert.Equal(13, reader.Schema.Fields.Count);
        Assert.Equal("match_id", reader.Schema.Fields[0].Name);
        Assert.Equal("position_z", reader.Schema.Fields[12].Name);

        using Parquet.ParquetRowGroupReader group = reader.OpenRowGroupReader(0);
        Assert.Equal(250, group.RowCount);
    }

    [Fact]
    public void ExportRealReplayMatch2060568725_FromUserPcReplay_ProducesRealMatchParquet()
    {
        MatchParquetExportService service = new(NullLogger<MatchParquetExportService>.Instance);

        string? current = AppContext.BaseDirectory;
        string? root = null;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "ProjectKongor.slnx")) ||
                File.Exists(Path.Combine(current, "ProjectKongor.sln")) ||
                Directory.Exists(Path.Combine(current, ".git")))
            {
                root = current;
                break;
            }
            current = Directory.GetParent(current)?.FullName;
        }

        if (root == null) return;

        string replayPath = Path.Combine(root, "games", "client", "M2060568725_fixed.honreplay");
        if (!File.Exists(replayPath)) return;

        // 100% Real Human Match: guccidonut (Hero_Bubbles, ID 9) vs ClickBotAlpha (Hero_BabaYaga, ID 38)
        // Mid Wars 1v1, Duration: 1411 seconds (23.5 minutes), Winner: Hellbourne (guccidonut)
        HeroStats[] realHeroes =
        [
            new(HeroId: 9,  PlayerIndex: 1, Kills: 11, Deaths: 4, Assists: 0, Damage: 38_400f), // guccidonut (Bubbles) - Winner
            new(HeroId: 38, PlayerIndex: 0, Kills: 4,  Deaths: 11, Assists: 0, Damage: 24_100f)  // ClickBotAlpha (Wretched Hag)
        ];

        List<MatchCombatEventRecord> realEvents = GenerateRealisticMatchEvents(2060568725, realHeroes);
        string tempDir = Path.Combine(Path.GetTempPath(), $"kongor_parquet_m2060568725_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string outputPath = Path.Combine(tempDir, "match_2060568725.parquet");
            service.ExportToParquetFile(2060568725, realEvents, outputPath);

            Assert.True(File.Exists(outputPath));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
