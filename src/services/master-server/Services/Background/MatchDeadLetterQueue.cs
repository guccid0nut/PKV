// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Text.Json;
using ASPIRE.Common.Telemetry;
using Microsoft.Extensions.Logging;

namespace KONGOR.MasterServer.Services.Background;

/// <summary>
///     Thread-safe in-memory circular dead-letter queue with forensic disk auditing and replay capability.
/// </summary>
public sealed class MatchDeadLetterQueue : IMatchDeadLetterQueue
{
    private const int DefaultMaxCapacity = 100;
    private readonly ConcurrentQueue<DeadLetterMatchJob> _queue = new();
    private readonly IMatchProcessingChannel _channel;
    private readonly ILogger<MatchDeadLetterQueue> _logger;
    private readonly int _maxCapacity;
    private readonly string _dumpDirectory;

    public int Count => _queue.Count;

    public MatchDeadLetterQueue(
        IMatchProcessingChannel channel,
        ILogger<MatchDeadLetterQueue> logger,
        int maxCapacity = DefaultMaxCapacity,
        string? dumpDirectory = null)
    {
        _channel = channel;
        _logger = logger;
        _maxCapacity = Math.Max(1, maxCapacity);
        _dumpDirectory = dumpDirectory ?? Path.Combine(AppContext.BaseDirectory, "logs", "dead_letter_matches");
    }

    public async ValueTask EnqueueAsync(DeadLetterMatchJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        _queue.Enqueue(job);

        // Enforce maximum buffer capacity (FIFO eviction)
        while (_queue.Count > _maxCapacity && _queue.TryDequeue(out _))
        {
        }

        // Record telemetry metrics
        NexusMetrics.RecordMatchDeadLetter(job.FailureReason);

        if (_logger.IsEnabled(LogLevel.Warning))
        {
            _logger.LogWarning(
                "[MatchDeadLetterQueue] Enqueued Match #{MatchId} into Dead-Letter Queue (Partition: {PartitionIndex}, Reason: {FailureReason}, Exception: {ExceptionType})",
                job.MatchId, job.PartitionIndex, job.FailureReason, job.ExceptionType);
        }

        // Persist forensic audit dump to disk
        await PersistForensicDumpAsync(job, cancellationToken).ConfigureAwait(false);
    }

    public IReadOnlyList<DeadLetterMatchJob> GetRecentFailedJobs(int count = 50)
    {
        int clampedCount = Math.Clamp(count, 1, _maxCapacity);
        return _queue.ToArray().TakeLast(clampedCount).Reverse().ToList();
    }

    public async Task<bool> ReplayJobAsync(int matchId, CancellationToken cancellationToken = default)
    {
        DeadLetterMatchJob? matchJob = _queue.FirstOrDefault(j => j.MatchId == matchId);
        if (matchJob is null)
        {
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning("[MatchDeadLetterQueue] Cannot replay Match #{MatchId}: Not found in dead-letter queue.", matchId);
            }
            return false;
        }

        matchJob.AttemptCount++;
        bool enqueued = await _channel.EnqueueAsync(matchJob.Payload, cancellationToken).ConfigureAwait(false);

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "[MatchDeadLetterQueue] Replayed Match #{MatchId} back into ingestion channel (Success: {Success}, Attempt: #{Attempt})",
                matchId, enqueued, matchJob.AttemptCount);
        }

        return enqueued;
    }

    public Task ClearAsync()
    {
        _queue.Clear();
        return Task.CompletedTask;
    }

    private async Task PersistForensicDumpAsync(DeadLetterMatchJob job, CancellationToken cancellationToken)
    {
        try
        {
            if (!Directory.Exists(_dumpDirectory))
            {
                Directory.CreateDirectory(_dumpDirectory);
            }

            string timestamp = job.FailedAt.UtcDateTime.ToString("yyyyMMdd_HHmmssfff");
            string filename = $"dead_letter_match_{job.MatchId}_{timestamp}.json";
            string filePath = Path.Combine(_dumpDirectory, filename);

            var dumpData = new
            {
                job.MatchId,
                job.PartitionIndex,
                job.FailureReason,
                job.ExceptionType,
                job.StackTrace,
                job.FailedAt,
                job.AttemptCount,
                Payload = new
                {
                    job.Payload.MatchID,
                    job.Payload.RemoteIp,
                    job.Payload.AccountId,
                    job.Payload.EnqueuedAt,
                    MatchServer = job.Payload.MatchServer is not null
                        ? new
                        {
                            job.Payload.MatchServer.ID,
                            job.Payload.MatchServer.Name,
                            job.Payload.MatchServer.HostAccountName,
                            job.Payload.MatchServer.IPAddress
                        }
                        : null,
                    MatchStats = job.Payload.Form?.MatchStats,
                    TeamStats = job.Payload.Form?.TeamStats,
                    PlayerStats = job.Payload.Form?.PlayerStats
                }
            };

            string json = JsonSerializer.Serialize(dumpData, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(filePath, json, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "[MatchDeadLetterQueue] Could not persist forensic dump for Match #{MatchId}", job.MatchId);
            }
        }
    }
}
