// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using NEXUS.Contracts.Services;
using ZORGATH.WebPortal.API.Infrastructure.Attributes;

namespace ZORGATH.WebPortal.API.Controllers;

/// <summary>
/// Controller for administrative metrics, providing real-time system performance, operational health, and server telemetry.
/// </summary>
[ApiController]
[Route("api/admin")]
[Produces("application/json")]
[EnableRateLimiting(RateLimiterPolicies.Relaxed)]
public class AdminMetricsController(IAdminMetricsService metricsService) : ControllerBase
{
    /// <summary>
    /// Retrieves administrative system metrics, including telemetry, performance statistics, and service health.
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>An <see cref="IActionResult"/> containing the system administrative metrics DTO.</returns>
    /// <response code="200">The administrative metrics were retrieved successfully.</response>
    [HttpGet("metrics", Name = "Get Admin System Metrics")]
    [RequireInternalServiceKey]
    [ProducesResponseType(typeof(AdminMetricsDTO), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMetrics(CancellationToken cancellationToken = default)
    {
        AdminMetricsDTO metrics = await metricsService.GetAdminMetricsAsync(cancellationToken);
        return Ok(metrics);
    }
}
