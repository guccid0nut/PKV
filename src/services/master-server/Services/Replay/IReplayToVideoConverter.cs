// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace KONGOR.MasterServer.Services.Replay;

public interface IReplayToVideoConverter
{
    Task<string?> ConvertReplayToVideoAsync(string matchId, string replayFilePath,
        CancellationToken cancellationToken = default);
}
