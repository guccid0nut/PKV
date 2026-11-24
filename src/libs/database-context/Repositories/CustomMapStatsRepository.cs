// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MERRICK.DatabaseContext.Persistence;
using NEXUS.Contracts.Data;
using NEXUS.Contracts.DTOs;

namespace MERRICK.DatabaseContext.Repositories;

/// <summary>
///     EF Core repository delegating custom-map scoreboard and career reads to <see cref="CustomMapStatsReader" />.
/// </summary>
public sealed class CustomMapStatsRepository(MerrickContext db) : ICustomMapStatsRepository
{
    public Task<CustomMatchStatsResponseDto?> GetMatchCustomStatsAsync(
        int matchId,
        Func<string, (string? DisplayName, string? IconVfs)>? heroResolver = null,
        CancellationToken ct = default) =>
        CustomMapStatsReader.GetMatchCustomStatsAsync(db, matchId, heroResolver, ct);

    public Task<PlayerMapStatsResponseDto?> GetPlayerMapStatsAsync(
        string nickname,
        string? rawMapSlug,
        Func<string, (string? DisplayName, string? IconVfs)>? heroResolver = null,
        CancellationToken ct = default) =>
        CustomMapStatsReader.GetPlayerMapStatsAsync(db, nickname, rawMapSlug, heroResolver, ct);

    public Task<IReadOnlyList<PlayerMapOverviewItemDto>> GetPlayerMapOverviewAsync(
        string nickname,
        CancellationToken ct = default) =>
        CustomMapStatsReader.GetPlayerMapOverviewAsync(db, nickname, ct);
}
