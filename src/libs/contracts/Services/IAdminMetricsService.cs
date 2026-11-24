// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using NEXUS.Contracts.Admin;

namespace NEXUS.Contracts.Services;

/// <summary>
///     Service abstraction for aggregating ecosystem-wide performance and queue metrics.
/// </summary>
public interface IAdminMetricsService
{
    /// <summary>
    ///     Computes and returns the latest aggregated admin metrics snapshot.
    /// </summary>
    Task<AdminMetricsDTO> GetAdminMetricsAsync(CancellationToken ct = default);
}
