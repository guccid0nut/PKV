// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace KONGOR.MasterServer.Services.Background;

/// <summary>
///     Provides a resilient buffer and quarantine facility for unparseable, malformed,
///     or faulted match submissions (Dead-Letter Queue).
/// </summary>
public interface IMatchDeadLetterQueue
{
    /// <summary>
    ///     Gets the current number of failed match jobs retained in the in-memory dead-letter buffer.
    /// </summary>
    int Count { get; }

    /// <summary>
    ///     Asynchronously enqueues a failed match job into the dead-letter queue,
    ///     records telemetry metrics, and writes forensic audit dumps.
    /// </summary>
    ValueTask EnqueueAsync(DeadLetterMatchJob job, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Retrieves the most recent failed jobs from the dead-letter buffer for diagnostics and inspection.
    /// </summary>
    IReadOnlyList<DeadLetterMatchJob> GetRecentFailedJobs(int count = 50);

    /// <summary>
    ///     Replays a dead-lettered match job back into the ingestion channel.
    /// </summary>
    Task<bool> ReplayJobAsync(int matchId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Clears the in-memory dead-letter buffer.
    /// </summary>
    Task ClearAsync();
}
