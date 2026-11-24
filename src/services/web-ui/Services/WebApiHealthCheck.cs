// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DAWNBRINGER.WebPortal.UI.Services;

/// <summary>
///     Health check verifying downstream connectivity from Web UI to the ZORGATH Web API.
/// </summary>
public sealed class WebApiHealthCheck(IHttpClientFactory httpClientFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("ZORGATH");
            using HttpResponseMessage response = await client.GetAsync("/health/live", cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return HealthCheckResult.Healthy("[HEALTHY] ZORGATH Web API is reachable and alive.");
            }

            return HealthCheckResult.Degraded($"[DEGRADED] ZORGATH Web API returned HTTP {(int)response.StatusCode}.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Degraded($"[DEGRADED] ZORGATH Web API is unreachable: {ex.Message}");
        }
    }
}
