// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json;
using ASPIRE.Common.Telemetry;
using ASPIRE.Tests.ASPIRE.Common.EntityPrebake.Contracts;
using ASPIRE.Tests.ASPIRE.Common.EntityPrebake.Harness;
using FluentAssertions;

namespace ASPIRE.Tests.ASPIRE.Common.EntityPrebake.Tier1_FeatureCoverage;

public sealed class OpenTelemetryAndLoggingTests
{
    [Fact]
    public async Task OpenTelemetryAndLogging_PrebakeTelemetry_RecordsHitAndMissEvents()
    {
        // Arrange
        EntityCacheContainer container = TestPrebakeHarness.CreateSampleCatalog(5, 1);
        using TestPrebakedEntityRegistry registry = new(container);
        await registry.InitializeAsync();

        // Act
        registry.GetHeroByName("Hero_1");
        registry.GetHeroByName("Hero_NonExistent");

        // Assert
        IReadOnlyCollection<PrebakeTelemetryRecord> records = registry.TelemetryRecords;
        records.Should().Contain(r => r.EventType == "CacheHit" && r.EntityName == "Hero_1");
        records.Should().Contain(r => r.EventType == "CacheMiss" && r.EntityName == "Hero_NonExistent");
    }

    [Fact]
    public async Task OpenTelemetryAndLogging_PrebakeTelemetry_TracksMicrosecondResolutionTimings()
    {
        // Arrange
        EntityCacheContainer container = TestPrebakeHarness.CreateSampleCatalog(5, 1);
        using TestPrebakedEntityRegistry registry = new(container);
        await registry.InitializeAsync();

        // Act
        registry.GetHeroById(2);

        // Assert
        IReadOnlyCollection<PrebakeTelemetryRecord> records = registry.TelemetryRecords;
        PrebakeTelemetryRecord? hitRecord = null;
        foreach (PrebakeTelemetryRecord r in records)
            if (r.EventType == "CacheHit" && r.EntityName == "2")
            {
                hitRecord = r;
                break;
            }

        hitRecord.Should().NotBeNull();
        hitRecord!.DurationMicroseconds.Should().BeGreaterThanOrEqualTo(0);
        hitRecord.DurationMicroseconds.Should().BeLessThan(100000);
    }

    [Fact]
    public async Task PrebakeTelemetry_StructuredNdjsonFormat_ParsableAsJson()
    {
        // Arrange
        EntityCacheContainer container = TestPrebakeHarness.CreateSampleCatalog(3, 1);
        using TestPrebakedEntityRegistry registry = new(container);
        await registry.InitializeAsync();

        registry.GetHeroByName("Hero_1");

        // Act
        List<string> ndjsonLines = [];
        foreach (PrebakeTelemetryRecord record in registry.TelemetryRecords)
        {
            string jsonLine = JsonSerializer.Serialize(record);
            ndjsonLines.Add(jsonLine);
        }

        // Assert
        ndjsonLines.Should().NotBeEmpty();
        foreach (string line in ndjsonLines)
        {
            JsonDocument doc = JsonDocument.Parse(line);
            doc.RootElement.TryGetProperty("EventType", out JsonElement eventType).Should().BeTrue();
            eventType.GetString().Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public async Task OpenTelemetryAndLogging_PrebakeTelemetry_SupportsPowerShellConversion()
    {
        // Arrange
        PrebakeTelemetryRecord record = new()
        {
            TimestampUtc = TimeProvider.System.GetUtcNow().UtcDateTime,
            EventType = "CacheHit",
            EntityName = "Hero_Pyromancer",
            SourceTier = "PreBakedMemory",
            DurationMicroseconds = 24,
            Success = true
        };

        // Act
        string json = JsonSerializer.Serialize(record);
        using JsonDocument doc = JsonDocument.Parse(json);
        string extractedTier = doc.RootElement.GetProperty("SourceTier").GetString()!;

        // Assert
        extractedTier.Should().Be("PreBakedMemory");
        doc.RootElement.GetProperty("DurationMicroseconds").GetInt64().Should().Be(24);
    }

    [Fact]
    public async Task OpenTelemetryAndLogging_PrebakeTelemetry_RecordsPrewarmCompletedLifecycleEvent()
    {
        // Arrange
        EntityCacheContainer container = TestPrebakeHarness.CreateSampleCatalog(4, 1);
        using TestPrebakedEntityRegistry registry = new(container);

        // Act
        await registry.InitializeAsync();

        // Assert
        registry.TelemetryRecords.Should().Contain(r => r.EventType == "PrewarmCompleted" && r.Success);
    }

    [Fact]
    public void OpenTelemetryAndLogging_NexusMetrics_MetersAreConfiguredAndReportMetrics()
    {
        // Act & Assert
        NexusMetrics.MeterName.Should().Be("NEXUS.Telemetry");
        NexusMetrics.Meter.Should().NotBeNull();

        // Exercise telemetry counter calls
        Action act = () =>
        {
            NexusMetrics.RecordCcuEvent("connected");
            NexusMetrics.RecordMasterServerRequest("login");
            NexusMetrics.RecordCommandProcessed("get_hero_cache");
        };

        act.Should().NotThrow();
    }
}
