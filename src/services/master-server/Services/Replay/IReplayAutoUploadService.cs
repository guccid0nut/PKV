// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace KONGOR.MasterServer.Services.Replay;

public interface IReplayAutoUploadService
{
    /// <summary>
    ///     Enqueues an asynchronous background upload for a match replay upon match conclusion.
    /// </summary>
    Task QueueReplayUploadAsync(int matchId, string? customFileName = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Immediately searches for and uploads a single match replay to the configured storage provider.
    ///     Returns true if the replay was found and uploaded/verified.
    /// </summary>
    Task<bool> UploadSingleReplayAsync(int matchId, string? customFileName = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Scans all local replay directories and synchronizes any un-uploaded match replays to cloud storage.
    ///     Returns the number of replays synchronized.
    /// </summary>
    Task<int> SyncHistoricalReplaysAsync(IEnumerable<string>? customDirectories = null,
        CancellationToken cancellationToken = default);
}
