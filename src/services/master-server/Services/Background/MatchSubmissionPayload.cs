// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MemoryPack;
using NEXUS.Contracts.Domain.Matches;

namespace KONGOR.MasterServer.Services.Background;

/// <summary>
///     Encapsulates an asynchronous match submission processing job.
/// </summary>
[MemoryPackable]
public partial class MatchProcessingJob
{
    public required int MatchID { get; init; }
    public StatsForSubmissionRequestForm? Form { get; init; }
    public MatchFinalizationPayload? BinaryPayload { get; init; }
    public MatchServer? MatchServer { get; init; }
    public string? RemoteIp { get; init; }
    public DateTimeOffset EnqueuedAt { get; init; } = TimeProvider.System.GetUtcNow();

    /// <summary>
    ///     Primary account ID associated with this match job used for partition routing.
    /// </summary>
    public int AccountId { get; init; }

    /// <summary>
    ///     Message ID if ingested from Redis Streams.
    /// </summary>
    public string? RedisStreamMessageId { get; set; }

    /// <summary>
    ///     True when submitted by a verified participant's P2P Listen Server session (persists match history only; 0 MMR delta and 0 coin rewards).
    /// </summary>
    public bool IsUnrankedListenServer { get; init; }
}

/// <summary>
///     Domain job model for sharded match processing worker channels.
/// </summary>
[MemoryPackable]
public partial class MatchJob : MatchProcessingJob
{
}

/// <summary>
///     Legacy alias for <see cref="MatchJob" /> / <see cref="MatchProcessingJob" />.
/// </summary>
[MemoryPackable]
public sealed partial class MatchSubmissionPayload : MatchJob
{
}
