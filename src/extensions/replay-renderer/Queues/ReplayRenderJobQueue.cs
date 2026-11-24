// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: ReplayRenderJobQueue.cs
// System: NEXUS Replay Video Renderer Service
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Collections.Concurrent;
using System.Threading.Channels;
using ReplayRenderer.Service.Models;

namespace ReplayRenderer.Service.Queues;

/// <summary>
///     Thread-safe in-memory channel queue and job registry for replay rendering tasks.
/// </summary>
public sealed class ReplayRenderJobQueue
{
    private readonly Channel<ReplayRenderJob> _channel = Channel.CreateUnbounded<ReplayRenderJob>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

    private readonly ConcurrentDictionary<string, ReplayRenderJob> _jobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, string> _matchJobIndex = new();

    public bool EnqueueJob(ReplayRenderJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        _jobs[job.JobId] = job;
        _matchJobIndex[job.MatchId] = job.JobId;
        return _channel.Writer.TryWrite(job);
    }

    public ValueTask<ReplayRenderJob> DequeueAsync(CancellationToken cancellationToken)
    {
        return _channel.Reader.ReadAsync(cancellationToken);
    }

    public IReadOnlyCollection<ReplayRenderJob> GetAllJobs()
    {
        return _jobs.Values.ToList().AsReadOnly();
    }

    public ReplayRenderJob? GetJob(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId)) return null;
        return _jobs.TryGetValue(jobId, out ReplayRenderJob? job) ? job : null;
    }

    public ReplayRenderJob? GetJobByMatchId(int matchId)
    {
        if (_matchJobIndex.TryGetValue(matchId, out string? jobId) && !string.IsNullOrWhiteSpace(jobId))
            return GetJob(jobId);
        return null;
    }
}
