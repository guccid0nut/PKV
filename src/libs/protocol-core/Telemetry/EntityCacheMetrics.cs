// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ASPIRE.Common.Telemetry;

/// <summary>
///     Dedicated OpenTelemetry metric instruments and helpers for the pre-baked entity caching engine.
/// </summary>
internal static class EntityCacheMetrics
{
    public const string MeterName = NexusMetrics.MeterName;
    public static readonly Meter Meter = NexusMetrics.Meter;

    public static Counter<long> HitsTotal => NexusMetrics.EntityCacheHitsTotal;
    public static Counter<long> MissesTotal => NexusMetrics.EntityCacheMissesTotal;
    public static Histogram<double> ResolutionDuration => NexusMetrics.EntityResolutionDuration;

    /// <summary>
    ///     Records a pre-baked entity cache hit with entity_type and source_tier tags.
    /// </summary>
    public static void RecordHit(string entityType, string sourceTier = "PreBakedMemory")
    {
        NexusMetrics.RecordEntityCacheHit(entityType, sourceTier);
    }

    /// <summary>
    ///     Records a pre-baked entity cache miss with entity_type and source_tier tags.
    /// </summary>
    public static void RecordMiss(string entityType, string sourceTier = "NotFound")
    {
        NexusMetrics.RecordEntityCacheMiss(entityType, sourceTier);
    }

    /// <summary>
    ///     Records entity resolution latency in microseconds.
    /// </summary>
    public static void RecordResolutionDuration(double durationMicroseconds, string entityType, string sourceTier,
        bool found)
    {
        NexusMetrics.RecordEntityResolutionDuration(durationMicroseconds, entityType, sourceTier, found);
    }

    /// <summary>
    ///     Helper struct for measuring resolution scope and automatically recording metrics on disposal.
    /// </summary>
    public ref struct ResolutionScope(string entityType, string sourceTier = "PreBakedMemory")
    {
        private readonly long _startTimestamp = Stopwatch.GetTimestamp();
        private readonly string _entityType = entityType;
        private readonly string _sourceTier = sourceTier;
        private bool _completed = false;

        public void Complete(bool found, string? actualTier = null)
        {
            if (_completed) return;
            _completed = true;

            TimeSpan elapsed = Stopwatch.GetElapsedTime(_startTimestamp);
            double microseconds = elapsed.TotalMilliseconds * 1000.0;
            string tier = actualTier ?? _sourceTier;

            if (found)
                RecordHit(_entityType, tier);
            else
                RecordMiss(_entityType, tier);

            RecordResolutionDuration(microseconds, _entityType, tier, found);
        }
    }
}
