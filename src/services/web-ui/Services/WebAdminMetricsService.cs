// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace DAWNBRINGER.WebPortal.UI.Services;

public class WebAdminMetricsService(IAdminApiClient adminApiClient) : IAdminMetricsService
{
    public async Task<AdminMetricsDTO> GetAdminMetricsAsync(CancellationToken ct = default)
    {
        return await adminApiClient.GetStaffMetricsAsync(ct);
    }
}
