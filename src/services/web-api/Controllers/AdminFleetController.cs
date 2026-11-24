// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using NEXUS.Contracts.Data;
using ZORGATH.WebPortal.API.Infrastructure.Attributes;

namespace ZORGATH.WebPortal.API.Controllers;

/// <summary>
/// Administrative controller for inspecting and managing the dedicated game server host fleet and their active network configurations.
/// </summary>
[ApiController]
[Route("api/admin/fleet")]
[Produces("application/json")]
[EnableRateLimiting(RateLimiterPolicies.Relaxed)]
public class AdminFleetController(IAccountRepository accountRepository, ILogger<AdminFleetController> logger)
    : ControllerBase
{
    /// <summary>
    /// Retrieves all approved game server host applications and their active network configurations.
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>An <see cref="IActionResult"/> containing a list of approved host configurations.</returns>
    /// <response code="200">The approved host configurations were successfully retrieved.</response>
    /// <response code="500">An unexpected server error occurred while retrieving approved hosts.</response>
    [HttpGet("hosts")]
    [RequireInternalServiceKey]
    [ProducesResponseType(typeof(List<ApprovedHostDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetApprovedHosts(CancellationToken cancellationToken = default)
    {
        try
        {
            IReadOnlyList<HostApplication> approvedHosts =
                await accountRepository.GetApprovedHostApplicationsAsync(cancellationToken);

            List<ApprovedHostDto> dtos =
            [
                .. approvedHosts.Select(h =>
                {
                    string username = h.HostAccount?.Name ?? h.HostUsername;
                    int port = 11234;
                    if (username.Contains("dock", StringComparison.OrdinalIgnoreCase))
                    {
                        port = 11334;
                    }
                    else if (username.Contains("k3s", StringComparison.OrdinalIgnoreCase) || username.Contains("k8s", StringComparison.OrdinalIgnoreCase))
                    {
                        port = 11434;
                    }

                    return new ApprovedHostDto
                    {
                        ApplicationId = h.ID,
                        UserId = h.UserID,
                        HostAccountId = h.HostAccountID,
                        HostAccountName = username,
                        ServerName = h.ServerNamePrefix,
                        ServerLocation = h.Location,
                        HostIpAddress = "127.0.0.1",
                        Port = port
                    };
                })
            ];

            return Ok(dtos);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error getting approved host applications");
            return StatusCode(StatusCodes.Status500InternalServerError, ex.Message);
        }
    }
}
