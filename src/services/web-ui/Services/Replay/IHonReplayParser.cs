// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace DAWNBRINGER.WebPortal.UI.Services.Replay;

public interface IHonReplayParser
{
    Task<MatchReplayTimelineDto?> ParseReplayAsync(Stream replayStream, int matchId,
        CancellationToken cancellationToken = default);

    Task<MatchReplayTimelineDto?> ParseReplayFileAsync(string filePath, int matchId,
        CancellationToken cancellationToken = default);
}
