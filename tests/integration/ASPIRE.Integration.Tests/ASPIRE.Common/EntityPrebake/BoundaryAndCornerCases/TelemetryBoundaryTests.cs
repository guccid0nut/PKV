// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json;
using ASPIRE.Common.Telemetry;
using ASPIRE.Tests.ASPIRE.Common.EntityPrebake.Contracts;
using ASPIRE.Tests.ASPIRE.Common.EntityPrebake.Harness;
using FluentAssertions;

namespace ASPIRE.Tests.ASPIRE.Common.EntityPrebake.Tier2_BoundaryAndCornerCases;

public sealed class TelemetryBoundaryTests
{
    [Fact]
    public async Task Telemetry_ExtremeConcurrencyEmission_ThreadSafeAndLossless()
    {
        // Arrange
        EntityCacheContainer container = TestPrebakeHarness.CreateSampleCatalog(10, 1);
        using TestPrebakedEntityRegistry registry = new(container);
        await registry.InitializeAsync();

        List<Task> tasks = [];
        for (int i = 0; i < 50; i++)
            tasks.Add(Task.Run(() =>
            {
                for (int j = 0; j < 100; j++) registry.GetHeroByName("Hero_1");
            }));

        // Act
        await Task.WhenAll(tasks);

        // Assert: 50 * 100 = 5000 lookups + 1 prewarm record = 5001 total records
        registry.TelemetryRecords.Should().HaveCount(5001);
    }

    [Fact]
    public void Telemetry_RecordWithNullFields_SerializesToValidJson()
    {
        // Arrange
        PrebakeTelemetryRecord record = new()
        {
            ErrorMessage = null,
            EntityName = "",
            SourceTier = ""
        };

        // Act
        string json = JsonSerializer.Serialize(record);
        using JsonDocument doc = JsonDocument.Parse(json);

        // Assert
        doc.RootElement.TryGetProperty("ErrorMessage", out JsonElement errElem).Should().BeTrue();
        errElem.ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public void Telemetry_MaxDurationValue_HandlesLargeMicrosecondCounts()
    {
        // Arrange
        PrebakeTelemetryRecord record = new()
        {
            DurationMicroseconds = long.MaxValue
        };

        // Act
        string json = JsonSerializer.Serialize(record);
        PrebakeTelemetryRecord? restored = JsonSerializer.Deserialize<PrebakeTelemetryRecord>(json);

        // Assert
        restored.Should().NotBeNull();
        restored!.DurationMicroseconds.Should().Be(long.MaxValue);
    }

    [Fact]
    public async Task Telemetry_ClearAndDispose_CleansUpAllBufferedRecords()
    {
        // Arrange
        EntityCacheContainer container = TestPrebakeHarness.CreateSampleCatalog(3, 1);
        TestPrebakedEntityRegistry registry = new(container);
        await registry.InitializeAsync();

        registry.GetHeroByName("Hero_1");
        registry.TelemetryRecords.Should().NotBeEmpty();

        // Act
        registry.Dispose();

        // Assert
        registry.TelemetryRecords.Should().BeEmpty();
    }

    [Theory]
    [InlineData("connected")]
    [InlineData("disconnected")]
    [InlineData("reconnected")]
    [InlineData("UNKNOWN_STATUS_VALUE")]
    [InlineData("")]
    public void NexusMetrics_BoundedStatusValues_DoesNotThrow(string status)
    {
        // Act
        Action act = () => NexusMetrics.RecordCcuEvent(status);

        // Assert
        act.Should().NotThrow();
    }
}
