// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using NEXUS.Contracts.DTOs;

namespace NEXUS.Contracts.Data;

/// <summary>
///     Repository contract for reading structured custom-map match scoreboards
///     and per-map career profiles without exposing <c>MerrickContext</c> to controllers.
/// </summary>
public interface ICustomMapStatsRepository
{
    Task<CustomMatchStatsResponseDto?> GetMatchCustomStatsAsync(
        int matchId,
        Func<string, (string? DisplayName, string? IconVfs)>? heroResolver = null,
        CancellationToken ct = default);

    Task<PlayerMapStatsResponseDto?> GetPlayerMapStatsAsync(
        string nickname,
        string? rawMapSlug,
        Func<string, (string? DisplayName, string? IconVfs)>? heroResolver = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<PlayerMapOverviewItemDto>> GetPlayerMapOverviewAsync(
        string nickname,
        CancellationToken ct = default);
}
