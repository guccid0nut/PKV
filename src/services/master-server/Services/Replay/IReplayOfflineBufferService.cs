// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace KONGOR.MasterServer.Services.Replay;

public sealed class PendingReplayMetadata
{
    public int MatchId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public DateTimeOffset FirstAttemptUtc { get; set; } = TimeProvider.System.GetUtcNow();
    public DateTimeOffset LastAttemptUtc { get; set; } = TimeProvider.System.GetUtcNow();
    public int AttemptCount { get; set; } = 1;
    public string? LastError { get; set; }
    public long FileSizeBytes { get; set; }
}

public interface IReplayOfflineBufferService
{
    string BufferDirectory { get; }

    Task<string> EnqueuePendingReplayAsync(int matchId, string fileName, Stream stream, string? error,
        CancellationToken cancellationToken = default);

    Task<string> EnqueuePendingReplayFromDiskAsync(int matchId, string fileName, string sourceFilePath, string? error,
        CancellationToken cancellationToken = default);

    Task<List<PendingReplayMetadata>> GetPendingReplaysAsync(CancellationToken cancellationToken = default);

    Task MarkReplayCompletedAsync(string fileName, CancellationToken cancellationToken = default);

    Task UpdatePendingAttemptAsync(string fileName, string error, CancellationToken cancellationToken = default);

    Stream? OpenPendingReplayStream(string fileName);
}
