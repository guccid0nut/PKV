// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: MatchParquetExportService.cs
// System: COMPEL Supervision - Dedicated Server Match Parquet Lakehouse Exporter
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;
using Kongor.Core.Analytics;
using Microsoft.Extensions.Logging;

namespace COMPEL.Services.Telemetry;

/// <summary>
///     Immutable report quantifying database bloat reduction by serializing raw combat
///     event streams into columnar compressed Parquet files rather than transactional SQL rows.
/// </summary>
public sealed record BloatReductionReport(
    int EventCount,
    long EstimatedUncompressedBytes,
    long CompressedParquetBytes,
    double ReductionPercentage,
    string FormattedUncompressedSize,
    string FormattedParquetSize);

/// <summary>
///     High-throughput columnar Parquet lakehouse exporter for match combat logs and replay telemetry.
///     Eliminates relational database bloat by serializing 50,000–200,000 match events into lean Parquet
///     files (~1.5 MB) destined for Cloudflare R2 or Supabase Storage with $0 egress fees.
/// </summary>
public sealed class MatchParquetExportService(ILogger<MatchParquetExportService>? logger = null)
{
    private static readonly byte[] ParquetMagic = [0x50, 0x41, 0x52, 0x31]; // "PAR1"

    private readonly ILogger<MatchParquetExportService>? _logger = logger;

    /// <summary>
    ///     Resolves the canonical S3/R2 storage URI for a specific match's Parquet archive.
    /// </summary>
    public string ResolveStorageUri(long matchId, string bucketName = "match-analytics", string? prefix = null)
    {
        string cleanBucket = bucketName.Trim('/');
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return $"s3://{cleanBucket}/match_{matchId}.parquet";
        }

        string cleanPrefix = prefix.Trim('/');
        return $"s3://{cleanBucket}/{cleanPrefix}/match_{matchId}.parquet";
    }

    /// <summary>
    ///     Calculates the database bloat reduction achieved by storing events in Parquet rather than PostgreSQL rows.
    /// </summary>
    public BloatReductionReport CalculateBloatReduction(int eventCount, long compressedParquetSizeBytes)
    {
        long uncompressedEstimatedBytes = (long)eventCount * MatchCombatEventRecord.EstimatedRawJsonSizeBytes;
        long reductionBytes = Math.Max(0, uncompressedEstimatedBytes - compressedParquetSizeBytes);
        double reductionPercentage = uncompressedEstimatedBytes > 0
            ? ((double)reductionBytes / uncompressedEstimatedBytes) * 100.0
            : 0.0;

        return new BloatReductionReport(
            EventCount: eventCount,
            EstimatedUncompressedBytes: uncompressedEstimatedBytes,
            CompressedParquetBytes: compressedParquetSizeBytes,
            ReductionPercentage: reductionPercentage,
            FormattedUncompressedSize: FormatBytes(uncompressedEstimatedBytes),
            FormattedParquetSize: FormatBytes(compressedParquetSizeBytes));
    }

    /// <summary>
    ///     Serializes a batch of match combat events into a spec-compliant Apache Parquet container stream.
    /// </summary>
    public async Task SerializeEventsToParquetStreamAsync(IReadOnlyList<MatchCombatEventRecord> events, Stream outputStream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(outputStream);

        Parquet.Schema.DataField matchIdField = new("match_id", typeof(long));
        Parquet.Schema.DataField matchTimeMsField = new("match_time_ms", typeof(int));
        Parquet.Schema.DataField tickField = new("tick", typeof(int));
        Parquet.Schema.DataField eventTypeField = new("event_type", typeof(short));
        Parquet.Schema.DataField sourcePlayerIndexField = new("source_player_index", typeof(short));
        Parquet.Schema.DataField sourceHeroIdField = new("source_hero_id", typeof(short));
        Parquet.Schema.DataField targetPlayerIndexField = new("target_player_index", typeof(short));
        Parquet.Schema.DataField targetHeroIdField = new("target_hero_id", typeof(short));
        Parquet.Schema.DataField abilityOrItemIdField = new("ability_or_item_id", typeof(int));
        Parquet.Schema.DataField eventValueField = new("event_value", typeof(float));
        Parquet.Schema.DataField positionXField = new("position_x", typeof(float));
        Parquet.Schema.DataField positionYField = new("position_y", typeof(float));
        Parquet.Schema.DataField positionZField = new("position_z", typeof(float));

        Parquet.Schema.ParquetSchema schema = new(
            matchIdField,
            matchTimeMsField,
            tickField,
            eventTypeField,
            sourcePlayerIndexField,
            sourceHeroIdField,
            targetPlayerIndexField,
            targetHeroIdField,
            abilityOrItemIdField,
            eventValueField,
            positionXField,
            positionYField,
            positionZField);

        long[] matchIds = new long[events.Count];
        int[] matchTimes = new int[events.Count];
        int[] ticks = new int[events.Count];
        short[] eventTypes = new short[events.Count];
        short[] sourcePlayerIndices = new short[events.Count];
        short[] sourceHeroIds = new short[events.Count];
        short[] targetPlayerIndices = new short[events.Count];
        short[] targetHeroIds = new short[events.Count];
        int[] abilityOrItemIds = new int[events.Count];
        float[] eventValues = new float[events.Count];
        float[] positionXs = new float[events.Count];
        float[] positionYs = new float[events.Count];
        float[] positionZs = new float[events.Count];

        for (int i = 0; i < events.Count; i++)
        {
            MatchCombatEventRecord ev = events[i];
            matchIds[i] = ev.MatchId;
            matchTimes[i] = ev.MatchTimeMs;
            ticks[i] = ev.Tick;
            eventTypes[i] = (short)ev.EventType;
            sourcePlayerIndices[i] = (short)ev.SourcePlayerIndex;
            sourceHeroIds[i] = ev.SourceHeroId;
            targetPlayerIndices[i] = (short)ev.TargetPlayerIndex;
            targetHeroIds[i] = ev.TargetHeroId;
            abilityOrItemIds[i] = ev.AbilityOrItemId;
            eventValues[i] = ev.EventValue;
            positionXs[i] = ev.PositionX;
            positionYs[i] = ev.PositionY;
            positionZs[i] = ev.PositionZ;
        }

        Parquet.ParquetOptions formatOptions = new() { CompressionMethod = Parquet.CompressionMethod.Snappy };
        await using Parquet.ParquetWriter writer = await Parquet.ParquetWriter.CreateAsync(schema, outputStream, formatOptions, cancellationToken: cancellationToken);

        using Parquet.ParquetRowGroupWriter group = writer.CreateRowGroup();
        await group.WriteAsync(matchIdField, (ReadOnlyMemory<long>)matchIds, cancellationToken: cancellationToken);
        await group.WriteAsync(matchTimeMsField, (ReadOnlyMemory<int>)matchTimes, cancellationToken: cancellationToken);
        await group.WriteAsync(tickField, (ReadOnlyMemory<int>)ticks, cancellationToken: cancellationToken);
        await group.WriteAsync(eventTypeField, (ReadOnlyMemory<short>)eventTypes, cancellationToken: cancellationToken);
        await group.WriteAsync(sourcePlayerIndexField, (ReadOnlyMemory<short>)sourcePlayerIndices, cancellationToken: cancellationToken);
        await group.WriteAsync(sourceHeroIdField, (ReadOnlyMemory<short>)sourceHeroIds, cancellationToken: cancellationToken);
        await group.WriteAsync(targetPlayerIndexField, (ReadOnlyMemory<short>)targetPlayerIndices, cancellationToken: cancellationToken);
        await group.WriteAsync(targetHeroIdField, (ReadOnlyMemory<short>)targetHeroIds, cancellationToken: cancellationToken);
        await group.WriteAsync(abilityOrItemIdField, (ReadOnlyMemory<int>)abilityOrItemIds, cancellationToken: cancellationToken);
        await group.WriteAsync(eventValueField, (ReadOnlyMemory<float>)eventValues, cancellationToken: cancellationToken);
        await group.WriteAsync(positionXField, (ReadOnlyMemory<float>)positionXs, cancellationToken: cancellationToken);
        await group.WriteAsync(positionYField, (ReadOnlyMemory<float>)positionYs, cancellationToken: cancellationToken);
        await group.WriteAsync(positionZField, (ReadOnlyMemory<float>)positionZs, cancellationToken: cancellationToken);
    }

    /// <summary>
    ///     Synchronously serializes a batch of match combat events into a spec-compliant Apache Parquet container stream.
    /// </summary>
    public void SerializeEventsToParquetStream(IReadOnlyList<MatchCombatEventRecord> events, Stream outputStream)
    {
#pragma warning disable RS0030 // Justification: Synchronous wrapper for parquet export
        SerializeEventsToParquetStreamAsync(events, outputStream).GetAwaiter().GetResult();
#pragma warning restore RS0030
    }

    /// <summary>
    ///     Asynchronously exports match events to a local file destination and computes the resulting bloat reduction report.
    /// </summary>
    public async Task<BloatReductionReport> ExportToParquetFileAsync(
        long matchId,
        IReadOnlyList<MatchCombatEventRecord> events,
        string destinationFilePath,
        CancellationToken cancellationToken = default)
    {
        string? directory = Path.GetDirectoryName(destinationFilePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using (FileStream fs = new(destinationFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
        {
            await SerializeEventsToParquetStreamAsync(events, fs, cancellationToken).ConfigureAwait(false);
        }

        long fileSizeBytes = new FileInfo(destinationFilePath).Length;
        BloatReductionReport report = CalculateBloatReduction(events.Count, fileSizeBytes);

        _logger?.LogInformation(
            "[PARQUET_LAKEHOUSE] Exported Match {MatchId} ({Events:N0} events) to {Path}. Size: {Size} (Raw SQL avoided: {RawSize}, Saved: {Saved:F1}%)",
            matchId, events.Count, destinationFilePath, report.FormattedParquetSize, report.FormattedUncompressedSize, report.ReductionPercentage);

        return report;
    }

    /// <summary>
    ///     Exports match events to a local file destination and computes the resulting bloat reduction report.
    /// </summary>
    public BloatReductionReport ExportToParquetFile(
        long matchId,
        IReadOnlyList<MatchCombatEventRecord> events,
        string destinationFilePath)
    {
        string? directory = Path.GetDirectoryName(destinationFilePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using (FileStream fs = new(destinationFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            SerializeEventsToParquetStream(events, fs);
        }

        long fileSizeBytes = new FileInfo(destinationFilePath).Length;
        BloatReductionReport report = CalculateBloatReduction(events.Count, fileSizeBytes);

        _logger?.LogInformation(
            "[PARQUET_LAKEHOUSE] Exported Match {MatchId} ({Events:N0} events) to {Path}. Size: {Size} (Raw SQL avoided: {RawSize}, Saved: {Saved:F1}%)",
            matchId, events.Count, destinationFilePath, report.FormattedParquetSize, report.FormattedUncompressedSize, report.ReductionPercentage);

        return report;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F2} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
    }
}
