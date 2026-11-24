// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace KONGOR.MasterServer.Services.Background;

/// <summary>
///     Encapsulates a match processing job that failed execution and has been isolated in the Dead-Letter Queue.
/// </summary>
public sealed class DeadLetterMatchJob
{
    public required int MatchId { get; init; }
    public required int PartitionIndex { get; init; }
    public required MatchSubmissionPayload Payload { get; init; }
    public required string FailureReason { get; init; }
    public required string ExceptionType { get; init; }
    public string? StackTrace { get; init; }
    public required DateTimeOffset FailedAt { get; init; }
    public int AttemptCount { get; set; } = 1;
}
