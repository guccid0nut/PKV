// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace DAWNBRINGER.WebPortal.UI.Services;

public interface IMatchReplayService
{
    Task<MatchReplayTimelineDto?> GetMatchReplayTimelineAsync(int matchId,
        CancellationToken cancellationToken = default);

    string? FindReplayArchivePath(int matchId) => null;

    string? FindServerLogPath(int matchId) => null;
}
