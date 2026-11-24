// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace DAWNBRINGER.WebPortal.UI.Services;

public interface ILiveMatchService
{
    Task<List<LiveMatchData>> GetActiveLiveMatchesAsync(CancellationToken cancellationToken = default);
    Task<LiveMatchData?> GetLiveMatchSnapshotAsync(int matchId, CancellationToken cancellationToken = default);
    Task<bool> ApplyTelemetryReportAsync(LiveTelemetryReportDto report, CancellationToken cancellationToken = default);
    Task<bool> ApplyServerTickAsync(LiveServerTickPayloadDto tick, CancellationToken cancellationToken = default);
    Task<int> PruneStaleMatchesAsync(CancellationToken cancellationToken = default);
}
