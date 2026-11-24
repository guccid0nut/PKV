// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json;
using ASPIRE.Common.Telemetry;
using FluentAssertions;
using Xunit;

namespace ASPIRE.Prebaking.Tests.Telemetry;

public sealed class EntityCacheTelemetryLoggerTests
{
    [Fact]
    public void EntityCacheTelemetryLogger_FormatAsNdjson_ProducesSingleLineValidJson()
    {
        // Arrange
        EntityTelemetryEvent telemetryEvent = new()
        {
            EventName = "TestEvent",
            EntityType = "Hero",
            EntityName = "Hero_Accursed",
            SourceTier = "PreBakedMemory",
            LatencyMicroseconds = 12.34,
            Found = true
        };

        // Act
        string ndjson = EntityCacheTelemetryLogger.FormatAsNdjson(telemetryEvent);

        // Assert
        ndjson.Should().NotContain("\r\n");
        ndjson.Should().NotContain("\n");

        using JsonDocument doc = JsonDocument.Parse(ndjson);
        doc.RootElement.GetProperty("event_name").GetString().Should().Be("TestEvent");
        doc.RootElement.GetProperty("entity_type").GetString().Should().Be("Hero");
        doc.RootElement.GetProperty("entity_name").GetString().Should().Be("Hero_Accursed");
        doc.RootElement.GetProperty("source_tier").GetString().Should().Be("PreBakedMemory");
        doc.RootElement.GetProperty("latency_us").GetDouble().Should().Be(12.34);
        doc.RootElement.GetProperty("found").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void EntityCacheTelemetryLogger_LogEvent_AppendsValidNdjsonLineToFile()
    {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), $"ndjson_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string logPath = Path.Combine(tempDir, "telemetry.ndjson");

        try
        {
            EntityTelemetryEvent ev1 = new() { EventName = "Event1", EntityName = "Hero_Accursed" };
            EntityTelemetryEvent ev2 = new() { EventName = "Event2", EntityName = "Hero_Pyromancer" };

            // Act
            EntityCacheTelemetryLogger.LogEvent(ev1, logPath);
            EntityCacheTelemetryLogger.LogEvent(ev2, logPath);

            // Assert
            File.Exists(logPath).Should().BeTrue();
            string[] lines = File.ReadAllLines(logPath);
            lines.Length.Should().Be(2);

            IReadOnlyList<EntityTelemetryEvent> events = EntityCacheTelemetryLogger.ReadEventsFromFile(logPath);
            events.Count.Should().Be(2);
            events[0].EventName.Should().Be("Event1");
            events[0].EntityName.Should().Be("Hero_Accursed");
            events[1].EventName.Should().Be("Event2");
            events[1].EntityName.Should().Be("Hero_Pyromancer");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task EntityCacheTelemetryLogger_LogEventAsync_AppendsValidNdjsonLineToFile()
    {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), $"ndjson_async_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string logPath = Path.Combine(tempDir, "telemetry_async.ndjson");

        try
        {
            EntityTelemetryEvent ev = new()
            {
                EventName = "AsyncEvent",
                TotalEntities = 140,
                ElapsedMilliseconds = 55.5
            };

            // Act
            await EntityCacheTelemetryLogger.LogEventAsync(ev, logPath);

            // Assert
            IReadOnlyList<EntityTelemetryEvent> events =
                await EntityCacheTelemetryLogger.ReadEventsFromFileAsync(logPath);
            events.Should().HaveCount(1);
            events[0].EventName.Should().Be("AsyncEvent");
            events[0].TotalEntities.Should().Be(140);
            events[0].ElapsedMilliseconds.Should().Be(55.5);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void LogHit_And_LogMiss_EmitExpectedJsonStructure()
    {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), $"ndjson_hitmiss_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string logPath = Path.Combine(tempDir, "hitmiss.ndjson");

        try
        {
            // Act
            EntityCacheTelemetryLogger.LogHit("Hero", "Hero_Accursed", "PreBakedMemory", 15.0, "models/accursed.glb",
                logPath);
            EntityCacheTelemetryLogger.LogMiss("Item", "Item_Unknown", "NotFound", 5.0, logPath);

            // Assert
            IReadOnlyList<EntityTelemetryEvent> events = EntityCacheTelemetryLogger.ReadEventsFromFile(logPath);
            events.Should().HaveCount(2);

            EntityTelemetryEvent hit = events[0];
            hit.EventName.Should().Be("EntityCacheHit");
            hit.EntityType.Should().Be("Hero");
            hit.EntityName.Should().Be("Hero_Accursed");
            hit.SourceTier.Should().Be("PreBakedMemory");
            hit.LatencyMicroseconds.Should().Be(15.0);
            hit.ResolvedPath.Should().Be("models/accursed.glb");
            hit.Found.Should().BeTrue();

            EntityTelemetryEvent miss = events[1];
            miss.EventName.Should().Be("EntityCacheMiss");
            miss.EntityType.Should().Be("Item");
            miss.EntityName.Should().Be("Item_Unknown");
            miss.SourceTier.Should().Be("NotFound");
            miss.LatencyMicroseconds.Should().Be(5.0);
            miss.Found.Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void EntityCacheTelemetryLogger_LogPrewarmCompleted_EmitsExpectedFields()
    {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), $"ndjson_prewarm_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string logPath = Path.Combine(tempDir, "prewarm.ndjson");

        try
        {
            // Act
            EntityCacheTelemetryLogger.LogPrewarmCompleted(999, 123.45, "override/entity_cache.bin", logPath);

            // Assert
            IReadOnlyList<EntityTelemetryEvent> events = EntityCacheTelemetryLogger.ReadEventsFromFile(logPath);
            events.Should().HaveCount(1);
            events[0].EventName.Should().Be("EntityCachePrewarmCompleted");
            events[0].TotalEntities.Should().Be(999);
            events[0].ElapsedMilliseconds.Should().Be(123.45);
            events[0].ResolvedPath.Should().Be("override/entity_cache.bin");
            events[0].Status.Should().Be("Success");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void EntityCacheTelemetryLogger_LogPipelineExecuted_EmitsCompleteSummaryFields()
    {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), $"ndjson_pipe_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string logPath = Path.Combine(tempDir, "pipeline.ndjson");

        try
        {
            // Act
            EntityCacheTelemetryLogger.LogPipelineExecuted(
                "games/client",
                "games/shared",
                "games/client/override",
                140,
                1499,
                560,
                120,
                300,
                250,
                2858,
                17955,
                1048576,
                524288,
                "0xABCDEF1234567890",
                250.0,
                "both",
                false,
                logPath);

            // Assert
            IReadOnlyList<EntityTelemetryEvent> events = EntityCacheTelemetryLogger.ReadEventsFromFile(logPath);
            events.Should().HaveCount(1);

            EntityTelemetryEvent ev = events[0];
            ev.EventName.Should().Be("PrebakePipelineExecuted");
            ev.HeroesCount.Should().Be(140);
            ev.AvatarsCount.Should().Be(1499);
            ev.AbilitiesCount.Should().Be(560);
            ev.StatesCount.Should().Be(120);
            ev.ProjectilesCount.Should().Be(300);
            ev.ItemsCount.Should().Be(250);
            ev.CosmeticsCount.Should().Be(2858);
            ev.PreloadResourcesCount.Should().Be(17955);
            ev.BinaryBytes.Should().Be(1048576);
            ev.LuaBytes.Should().Be(524288);
            ev.Checksum.Should().Be("0xABCDEF1234567890");
            ev.ElapsedMilliseconds.Should().Be(250.0);
            ev.TargetMode.Should().Be("both");
            ev.DryRun.Should().BeFalse();
            ev.Status.Should().Be("Completed");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task EntityCacheTelemetryLogger_ConcurrentLoggingToFile_DoesNotCorruptLinesOrThrow()
    {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), $"ndjson_concurrent_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string logPath = Path.Combine(tempDir, "concurrent.ndjson");

        const int taskCount = 15;
        const int iterations = 30;
        Task[] tasks = new Task[taskCount];

        try
        {
            // Act
            for (int i = 0; i < taskCount; i++)
            {
                int taskId = i;
                tasks[i] = Task.Run(() =>
                {
                    for (int j = 0; j < iterations; j++)
                        EntityCacheTelemetryLogger.LogHit(
                            "Hero",
                            $"Hero_{taskId}_{j}",
                            "PreBakedMemory",
                            j * 1.5,
                            targetFilePath: logPath);
                });
            }

            await Task.WhenAll(tasks);

            // Assert
            IReadOnlyList<EntityTelemetryEvent> events = EntityCacheTelemetryLogger.ReadEventsFromFile(logPath);
            events.Count.Should().Be(taskCount * iterations);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void EntityCacheTelemetryLogger_SpecialCharactersAndQuotes_SerializeAndDeserializeCleanly()
    {
        // Arrange
        EntityTelemetryEvent telemetryEvent = new()
        {
            EventName = "Escaped\"Event",
            EntityType = "Hero\\Avatar",
            EntityName = "Hero_Accursed.Alt/Special\nName\tWithTab",
            ResolvedPath = "c:\\games\\client\\override\\models\\\"hero.glb\""
        };

        // Act
        string ndjson = EntityCacheTelemetryLogger.FormatAsNdjson(telemetryEvent);
        EntityTelemetryEvent? deserialized = JsonSerializer.Deserialize<EntityTelemetryEvent>(ndjson);

        // Assert
        deserialized.Should().NotBeNull();
        deserialized!.EventName.Should().Be("Escaped\"Event");
        deserialized.EntityType.Should().Be("Hero\\Avatar");
        deserialized.EntityName.Should().Be("Hero_Accursed.Alt/Special\nName\tWithTab");
        deserialized.ResolvedPath.Should().Be("c:\\games\\client\\override\\models\\\"hero.glb\"");
    }
}
