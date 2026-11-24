// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Threading;
using System.Threading.Tasks;
using DAWNBRINGER.WebPortal.UI.Infrastructure;
using DAWNBRINGER.WebPortal.UI.Models.DeveloperIssues;
using DAWNBRINGER.WebPortal.UI.Services;
using DAWNBRINGER.WebPortal.UI.Services.DeveloperIssues;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DAWNBRINGER.WebPortal.UI.Controllers;

public class SystemController : Controller
{
    private readonly IAdminMetricsService metricsService;
    private readonly IPublicPopulationTelemetryService? publicPopulationService;
    private readonly IDeveloperIssueAdminService? developerIssueAdminService;
    private readonly ILogger<SystemController>? logger;

    [ActivatorUtilitiesConstructor]
    public SystemController(
        IAdminMetricsService metricsService,
        IPublicPopulationTelemetryService? publicPopulationService = null,
        IDeveloperIssueAdminService? developerIssueAdminService = null,
        ILogger<SystemController>? logger = null)
    {
        this.metricsService = metricsService;
        this.publicPopulationService = publicPopulationService;
        this.developerIssueAdminService = developerIssueAdminService;
        this.logger = logger;
    }

    public SystemController(
        IAdminMetricsService metricsService,
        ILogger<SystemController> logger)
        : this(metricsService, null, null, logger)
    {
    }

    [HttpGet("System/Metrics")]
    public async Task<IActionResult> Metrics(CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = TimeProvider.System.GetUtcNow();
        AdminMetricsDTO metrics;
        try
        {
            metrics = await metricsService.GetAdminMetricsAsync(cancellationToken);
            if (metrics.PopulationBreakdown is null)
            {
                metrics = metrics with
                {
                    PopulationBreakdown = PopulationBreakdownDefaults.CreateUnavailable(now)
                };
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "[SystemController] Exception generating live metrics.");
            metrics = new AdminMetricsDTO(
                0,
                0,
                0.0,
                0,
                [],
                0,
                0,
                0,
                0.0,
                [],
                now,
                PopulationBreakdownDefaults.CreateUnavailable(now)
            );
        }

        return View(metrics);
    }

    /// <summary>
    /// Public, non-PII aggregate live population, active games, available servers, and TMM queue breakdown.
    /// Rate-limited per client IP and backed by a 5-second single-flight Valkey cache (zero ZORGATH admin API hop).
    /// </summary>
    [AllowAnonymous]
    [EnableRateLimiting(PublicPopulationTelemetryService.RateLimitPolicyName)]
    [HttpGet("api/telemetry/population")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(PublicPopulationTelemetryDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPublicPopulation(CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = TimeProvider.System.GetUtcNow();
        PublicPopulationTelemetryDto payload;
        try
        {
            if (publicPopulationService != null)
            {
                payload = await publicPopulationService.GetPublicPopulationAsync(cancellationToken);
            }
            else
            {
                AdminMetricsDTO metrics = await metricsService.GetAdminMetricsAsync(cancellationToken);
                payload = PublicPopulationTelemetryDto.FromBreakdown(metrics.PopulationBreakdown, now);
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "[SystemController] Exception resolving public population breakdown.");
            payload = PublicPopulationTelemetryDto.CreateUnavailable(now);
        }

        if (HttpContext?.Response != null)
        {
            Response.Headers.CacheControl = "public, max-age=5, stale-while-revalidate=10";
        }

        return Ok(payload);
    }

    [HttpGet("api/telemetry/live")]
    [HttpGet("api/telemetry")]
    public async Task<IActionResult> GetLiveTelemetry(CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = TimeProvider.System.GetUtcNow();
        AdminMetricsDTO metrics;
        try
        {
            metrics = await metricsService.GetAdminMetricsAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "[SystemController] Exception generating live metrics.");
            metrics = new AdminMetricsDTO(
                0,
                0,
                0,
                0,
                [],
                0,
                0,
                0,
                0,
                [],
                now,
                PopulationBreakdownDefaults.CreateUnavailable(now)
            );
        }

        return Ok(new
        {
            onlinePlayers = metrics.ConcurrentConnectedUsers,
            matchesInProgress = metrics.PopulationBreakdown?.GamesInProgressTotal ?? metrics.ActiveMatchServers,
            serverStatus = 1,
            topWinratePlayers = Array.Empty<object>(),
            populationBreakdown = PublicPopulationTelemetryDto.FromBreakdown(metrics.PopulationBreakdown, now)
        });
    }

    /// <summary>
    /// Developer Issues & Autonomous Healing Dashboard.
    /// Strictly restricted to authenticated staff and administrators.
    /// </summary>
    [Authorize(Policy = StaffAuthorizationHelper.PolicyName)]
    [HttpGet("admin/issues")]
    [HttpGet("System/Issues")]
    [HttpGet("staff/issues")]
    public async Task<IActionResult> Issues(
        [FromQuery] string? subsystem = null,
        [FromQuery] string? severity = null,
        [FromQuery] string? status = null,
        [FromQuery] string? search = null,
        CancellationToken cancellationToken = default)
    {
        if (!StaffAuthorizationHelper.IsStaff(User))
        {
            logger?.LogWarning("[SystemController] Forbidden access to Issues dashboard by non-staff user: {User}",
                User.Identity?.Name ?? "Anonymous");
            return Forbid();
        }

        if (developerIssueAdminService == null)
        {
            logger?.LogWarning("[SystemController] Issues requested but developerIssueAdminService is null");
            return NotFound();
        }

        DeveloperIssuesDashboardViewModel model = await developerIssueAdminService.GetDashboardViewModelAsync(
            subsystem, severity, status, search, cancellationToken);

        logger?.LogInformation(
            "[SystemController] Staff member {User} accessed Developer Issues Dashboard ({Count} tracked issues, Subsystem: {Subsystem}, Severity: {Severity}, Status: {Status})",
            User.Identity?.Name ?? "Staff", model.Issues.Count, subsystem ?? "All", severity ?? "All", status ?? "All");

        return View(model);
    }

    /// <summary>
    /// Filtered developer issues query endpoint for AJAX and real-time updates.
    /// Strictly restricted to authenticated staff and administrators.
    /// </summary>
    [Authorize(Policy = StaffAuthorizationHelper.PolicyName)]
    [HttpGet("api/admin/issues")]
    public async Task<IActionResult> GetIssuesApi(
        [FromQuery] string? subsystem = null,
        [FromQuery] string? severity = null,
        [FromQuery] string? status = null,
        [FromQuery] string? search = null,
        CancellationToken cancellationToken = default)
    {
        if (!StaffAuthorizationHelper.IsStaff(User))
        {
            logger?.LogWarning("[SystemController] Forbidden API request to /api/admin/issues by non-staff user: {User}",
                User.Identity?.Name ?? "Anonymous");
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Staff authorization required." });
        }

        if (developerIssueAdminService == null)
        {
            return NotFound();
        }

        var model = await developerIssueAdminService.GetDashboardViewModelAsync(
            subsystem, severity, status, search, cancellationToken);

        logger?.LogInformation("[SystemController] Staff member {User} queried /api/admin/issues",
            User.Identity?.Name ?? "Staff");

        return Ok(model);
    }

    /// <summary>
    /// Triggers an immediate autonomous reflex self-healing recipe for the specified issue fingerprint.
    /// Strictly restricted to authenticated staff and administrators.
    /// </summary>
    [Authorize(Policy = StaffAuthorizationHelper.PolicyName)]
    [HttpPost("api/admin/issues/{fingerprint}/heal")]
    public async Task<IActionResult> TriggerReflexHeal(
        string fingerprint,
        CancellationToken cancellationToken = default)
    {
        if (!StaffAuthorizationHelper.IsStaff(User))
        {
            logger?.LogWarning("[SystemController] Forbidden attempt to trigger reflex heal on '{Fingerprint}' by non-staff user: {User}",
                fingerprint, User.Identity?.Name ?? "Anonymous");
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Staff authorization required." });
        }

        if (developerIssueAdminService == null)
        {
            return NotFound();
        }

        logger?.LogInformation("[SystemController] Staff member {User} triggered reflex self-healing for fingerprint '{Fingerprint}'",
            User.Identity?.Name ?? "Staff", fingerprint);

        ReflexTriggerResultDto result = await developerIssueAdminService.TriggerReflexHealAsync(
            fingerprint, cancellationToken);

        return Ok(result);
    }

    public sealed record ManualResolveRequest(string? Notes);

    /// <summary>
    /// Manually marks a developer issue as resolved with staff forensics notes.
    /// Strictly restricted to authenticated staff and administrators.
    /// </summary>
    [Authorize(Policy = StaffAuthorizationHelper.PolicyName)]
    [HttpPost("api/admin/issues/{fingerprint}/resolve")]
    public async Task<IActionResult> ResolveIssue(
        string fingerprint,
        [FromBody] ManualResolveRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (!StaffAuthorizationHelper.IsStaff(User))
        {
            logger?.LogWarning("[SystemController] Forbidden attempt to manually resolve issue '{Fingerprint}' by non-staff user: {User}",
                fingerprint, User.Identity?.Name ?? "Anonymous");
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Staff authorization required." });
        }

        if (developerIssueAdminService == null)
        {
            return NotFound();
        }

        string staffName = User.Identity?.Name ?? "Staff";
        string notes = !string.IsNullOrWhiteSpace(request?.Notes) 
            ? request.Notes 
            : $"Marked resolved manually via Staff Admin Dashboard by {staffName}.";

        logger?.LogInformation("[SystemController] Staff member {User} manually resolving issue '{Fingerprint}' with notes: '{Notes}'",
            staffName, fingerprint, notes);

        bool success = await developerIssueAdminService.ResolveIssueManuallyAsync(
            fingerprint, notes, cancellationToken);

        return Ok(new { success, fingerprint });
    }
}
