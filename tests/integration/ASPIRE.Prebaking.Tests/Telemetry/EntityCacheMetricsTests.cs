// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics.Metrics;
using ASPIRE.Common.Telemetry;
using FluentAssertions;
using Xunit;

namespace ASPIRE.Prebaking.Tests.Telemetry;

public sealed class EntityCacheMetricsTests : IDisposable
{
    private readonly List<(string InstrumentName, long Value, Dictionary<string, object?> Tags)>
        _counterRecordings = [];

    private readonly List<(string InstrumentName, double Value, Dictionary<string, object?> Tags)>
        _histogramRecordings = [];

    private readonly MeterListener _listener;
    private readonly object _recordLock = new();
    private readonly string _tagSuffix = Guid.NewGuid().ToString("N")[..8];

    public EntityCacheMetricsTests()
    {
        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == NexusMetrics.MeterName) listener.EnableMeasurementEvents(instrument);
            }
        };

        _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            lock (_recordLock)
            {
                Dictionary<string, object?> tagDict = [];
                foreach (KeyValuePair<string, object?> tag in tags) tagDict[tag.Key] = tag.Value;
                _counterRecordings.Add((instrument.Name, measurement, tagDict));
            }
        });

        _listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, _) =>
        {
            lock (_recordLock)
            {
                Dictionary<string, object?> tagDict = [];
                foreach (KeyValuePair<string, object?> tag in tags) tagDict[tag.Key] = tag.Value;
                _histogramRecordings.Add((instrument.Name, measurement, tagDict));
            }
        });

        _listener.Start();
    }

    public void Dispose()
    {
        _listener.Dispose();
    }

    [Fact]
    public void EntityCacheMetrics_RecordHit_IncrementsHitCounterWithCorrectTags()
    {
        string entityType = $"Hero_{_tagSuffix}";

        // Act
        EntityCacheMetrics.RecordHit(entityType);

        // Assert
        lock (_recordLock)
        {
            (string InstrumentName, long Value, Dictionary<string, object?> Tags) match = _counterRecordings.Find(r =>
                r.InstrumentName == "nexus_entity_cache_hits_total" &&
                r.Tags.TryGetValue("entity_type", out object? type) && (string?)type == entityType &&
                r.Tags.TryGetValue("source_tier", out object? tier) && (string?)tier == "PreBakedMemory");

            match.InstrumentName.Should().NotBeNull();
            match.Value.Should().Be(1);
        }
    }

    [Fact]
    public void EntityCacheMetrics_RecordMiss_IncrementsMissCounterWithCorrectTags()
    {
        string entityType = $"Ability_{_tagSuffix}";

        // Act
        EntityCacheMetrics.RecordMiss(entityType);

        // Assert
        lock (_recordLock)
        {
            (string InstrumentName, long Value, Dictionary<string, object?> Tags) match = _counterRecordings.Find(r =>
                r.InstrumentName == "nexus_entity_cache_misses_total" &&
                r.Tags.TryGetValue("entity_type", out object? type) && (string?)type == entityType &&
                r.Tags.TryGetValue("source_tier", out object? tier) && (string?)tier == "NotFound");

            match.InstrumentName.Should().NotBeNull();
            match.Value.Should().Be(1);
        }
    }

    [Fact]
    public void EntityCacheMetrics_RecordResolutionDuration_RecordsValuesToHistogram()
    {
        string entityType = $"Hero_{_tagSuffix}";

        // Act
        EntityCacheMetrics.RecordResolutionDuration(42.5, entityType, "PreBakedMemory", true);

        // Assert
        lock (_recordLock)
        {
            (string InstrumentName, double Value, Dictionary<string, object?> Tags) match =
                _histogramRecordings.Find(r =>
                    r.InstrumentName == "nexus_entity_resolution_duration_microseconds" &&
                    r.Tags.TryGetValue("entity_type", out object? type) && (string?)type == entityType &&
                    r.Tags.TryGetValue("source_tier", out object? tier) && (string?)tier == "PreBakedMemory" &&
                    r.Tags.TryGetValue("found", out object? found) && (bool)found!);

            match.InstrumentName.Should().NotBeNull();
            match.Value.Should().Be(42.5);
        }
    }

    [Fact]
    public void ResolutionScope_WhenCompletedTrue_RecordsHitAndDuration()
    {
        string entityType = $"Item_{_tagSuffix}";

        // Act
        EntityCacheMetrics.ResolutionScope scope = new(entityType);
        scope.Complete(true);

        // Assert
        lock (_recordLock)
        {
            _counterRecordings.Should().Contain(r =>
                r.InstrumentName == "nexus_entity_cache_hits_total" &&
                (string?)r.Tags["entity_type"] == entityType);

            _histogramRecordings.Should().Contain(r =>
                r.InstrumentName == "nexus_entity_resolution_duration_microseconds" &&
                (string?)r.Tags["entity_type"] == entityType &&
                (bool)r.Tags["found"]! == true);
        }
    }

    [Fact]
    public void ResolutionScope_WhenCompletedFalse_RecordsMissAndDuration()
    {
        string entityType = $"Projectile_{_tagSuffix}";

        // Act
        EntityCacheMetrics.ResolutionScope scope = new(entityType, "NotFound");
        scope.Complete(false);

        // Assert
        lock (_recordLock)
        {
            _counterRecordings.Should().Contain(r =>
                r.InstrumentName == "nexus_entity_cache_misses_total" &&
                (string?)r.Tags["entity_type"] == entityType);

            _histogramRecordings.Should().Contain(r =>
                r.InstrumentName == "nexus_entity_resolution_duration_microseconds" &&
                (string?)r.Tags["entity_type"] == entityType &&
                (bool)r.Tags["found"]! == false);
        }
    }

    [Fact]
    public void EntityCacheMetrics_NullOrEmptyTags_FallbackGracefullyToBoundedDefaults()
    {
        string isolatedTier = $"Tier_{_tagSuffix}";
        string isolatedType = $"Type_{_tagSuffix}";

        // Act
        EntityCacheMetrics.RecordHit("", "");
        EntityCacheMetrics.RecordHit("   ", isolatedTier);
        EntityCacheMetrics.RecordMiss("", "");
        EntityCacheMetrics.RecordMiss(isolatedType, "   ");
        EntityCacheMetrics.RecordResolutionDuration(10.0, "", "", false);
        EntityCacheMetrics.RecordResolutionDuration(12.5, "", isolatedTier, false);

        // Assert
        lock (_recordLock)
        {
            _counterRecordings.Should().Contain(r =>
                r.InstrumentName == "nexus_entity_cache_hits_total" &&
                (string?)r.Tags["entity_type"] == "Unknown" &&
                (string?)r.Tags["source_tier"] == "PreBakedMemory");

            _counterRecordings.Should().ContainSingle(r =>
                r.InstrumentName == "nexus_entity_cache_hits_total" &&
                (string?)r.Tags["entity_type"] == "Unknown" &&
                (string?)r.Tags["source_tier"] == isolatedTier);

            _counterRecordings.Should().Contain(r =>
                r.InstrumentName == "nexus_entity_cache_misses_total" &&
                (string?)r.Tags["entity_type"] == "Unknown" &&
                (string?)r.Tags["source_tier"] == "NotFound");

            _counterRecordings.Should().ContainSingle(r =>
                r.InstrumentName == "nexus_entity_cache_misses_total" &&
                (string?)r.Tags["entity_type"] == isolatedType &&
                (string?)r.Tags["source_tier"] == "NotFound");

            _histogramRecordings.Should().Contain(r =>
                r.InstrumentName == "nexus_entity_resolution_duration_microseconds" &&
                (string?)r.Tags["entity_type"] == "Unknown" &&
                (string?)r.Tags["source_tier"] == "PreBakedMemory");

            _histogramRecordings.Should().ContainSingle(r =>
                r.InstrumentName == "nexus_entity_resolution_duration_microseconds" &&
                (string?)r.Tags["entity_type"] == "Unknown" &&
                (string?)r.Tags["source_tier"] == isolatedTier &&
                r.Value.Equals(12.5));
        }
    }

    [Fact]
    public async Task EntityCacheMetrics_ConcurrentMetricRecordings_MaintainAccurateCounts()
    {
        // Arrange
        string hitType = $"HeroConcurrent_{_tagSuffix}";
        string missType = $"StateConcurrent_{_tagSuffix}";

        lock (_recordLock)
        {
            _counterRecordings.Clear();
        }

        const int taskCount = 20;
        const int iterationsPerTask = 50;
        Task[] tasks = new Task[taskCount];

        // Act
        for (int i = 0; i < taskCount; i++)
        {
            int taskId = i;
            tasks[i] = Task.Run(() =>
            {
                for (int j = 0; j < iterationsPerTask; j++)
                    if (taskId % 2 == 0)
                        EntityCacheMetrics.RecordHit(hitType);
                    else
                        EntityCacheMetrics.RecordMiss(missType);
            });
        }

        await Task.WhenAll(tasks);

        // Assert
        lock (_recordLock)
        {
            int hitCount = 0;
            int missCount = 0;
            foreach ((string InstrumentName, long Value, Dictionary<string, object?> Tags) recording in
                     _counterRecordings)
            {
                if (recording.InstrumentName == "nexus_entity_cache_hits_total" &&
                    recording.Tags.TryGetValue("entity_type", out object? hType) &&
                    (string?)hType == hitType)
                {
                    hitCount += (int)recording.Value;
                }

                if (recording.InstrumentName == "nexus_entity_cache_misses_total" &&
                    recording.Tags.TryGetValue("entity_type", out object? mType) &&
                    (string?)mType == missType)
                {
                    missCount += (int)recording.Value;
                }
            }

            hitCount.Should().Be(taskCount / 2 * iterationsPerTask);
            missCount.Should().Be(taskCount / 2 * iterationsPerTask);
        }
    }
}
