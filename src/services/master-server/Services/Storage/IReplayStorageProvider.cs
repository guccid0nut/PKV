// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace KONGOR.MasterServer.Services.Storage;

public interface IReplayStorageProvider
{
    /// <summary>
    ///     Uploads a replay stream to storage and returns the download URL.
    /// </summary>
    Task<string> UploadReplayAsync(string matchId, Stream fileStream, string fileName,
        CancellationToken cancellationToken = default);
}
