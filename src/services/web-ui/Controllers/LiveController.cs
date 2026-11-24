// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using DAWNBRINGER.WebPortal.UI.Hubs;
using DAWNBRINGER.WebPortal.UI.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace DAWNBRINGER.WebPortal.UI.Controllers;

public class LiveController(
    ILiveMatchService liveMatchService,
    ILogger<LiveController> logger,
    IConfiguration? configuration = null,
    IHostEnvironment? environment = null) : Controller
{
    [HttpGet("live")]
    [HttpGet("matches/live")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken = default)
    {
        if (!StaffAuthorizationHelper.IsStaff(User))
        {
            logger.LogWarning("[Live] Access to WIP Live Matches denied for non-staff user '{User}'", User?.Identity?.Name ?? "Anonymous");
            return Forbid();
        }

        List<LiveMatchData> liveMatches = await liveMatchService.GetActiveLiveMatchesAsync(cancellationToken);
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("[Live] Staff user '{User}' rendering Live Matches Overview ({Count} active matches)",
                User?.Identity?.Name ?? "Staff", liveMatches.Count);

        return View(liveMatches);
    }

    [HttpGet("live/{matchId:int}")]
    [HttpGet("matches/live/{matchId:int}")]
    public async Task<IActionResult> Spectate(int matchId, CancellationToken cancellationToken = default)
    {
        if (!StaffAuthorizationHelper.IsStaff(User))
        {
            logger.LogWarning("[Live] Access to WIP Spectate Live Match #{MatchId} denied for non-staff user '{User}'",
                matchId, User?.Identity?.Name ?? "Anonymous");
            return Forbid();
        }

        LiveMatchData? match = await liveMatchService.GetLiveMatchSnapshotAsync(matchId, cancellationToken);
        if (match == null) return NotFound($"Live match #{matchId} not found or concluded.");

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("[Live] Staff user '{User}' spectating Live Match #{MatchId} ({Map}, {PlayersCount} players)",
                User?.Identity?.Name ?? "Staff", matchId, match.Map, match.Players.Count);

        return View(match);
    }

    [HttpGet("api/live/matches")]
    public async Task<IActionResult> GetLiveMatchesApi(CancellationToken cancellationToken = default)
    {
        if (!StaffAuthorizationHelper.IsStaff(User))
        {
            logger.LogWarning("[Live] Unauthorized API query to /api/live/matches by user '{User}'", User?.Identity?.Name ?? "Anonymous");
            return StatusCode(StatusCodes.Status403Forbidden, new { success = false, message = "Staff authorization required." });
        }

        List<LiveMatchData> liveMatches = await liveMatchService.GetActiveLiveMatchesAsync(cancellationToken);
        return Json(new { success = true, count = liveMatches.Count, matches = liveMatches });
    }

    [HttpGet("api/live/matches/{matchId:int}")]
    public async Task<IActionResult> GetLiveMatchSnapshotApi(int matchId, CancellationToken cancellationToken = default)
    {
        if (!StaffAuthorizationHelper.IsStaff(User))
        {
            logger.LogWarning("[Live] Unauthorized API query to /api/live/matches/{MatchId} by user '{User}'", matchId, User?.Identity?.Name ?? "Anonymous");
            return StatusCode(StatusCodes.Status403Forbidden, new { success = false, message = "Staff authorization required." });
        }

        LiveMatchData? match = await liveMatchService.GetLiveMatchSnapshotAsync(matchId, cancellationToken);
        if (match == null) return NotFound(new { success = false, message = "Match not found" });

        return Json(new { success = true, match });
    }

    [HttpPost("api/live/telemetry/report")]
    public async Task<IActionResult> ReportTelemetryApi([FromBody] LiveTelemetryReportDto report,
        CancellationToken cancellationToken = default)
    {
        if (!StaffAuthorizationHelper.IsStaff(User) &&
            !LiveMatchesHub.HasTrustedInternalServiceKey(HttpContext, configuration, environment))
        {
            logger.LogWarning(
                "[SECURITY-ALERT] [Live] Unauthorized /api/live/telemetry/report rejected for user '{User}' from {RemoteIp}",
                User?.Identity?.Name ?? "Anonymous",
                HttpContext?.Connection.RemoteIpAddress?.ToString() ?? "unknown");
            return Unauthorized(new { success = false, message = "Internal service authentication required." });
        }

        if (report is null or { MatchID: <= 0 })
            return BadRequest(new { success = false, message = "Invalid telemetry payload" });

        bool applied = await liveMatchService.ApplyTelemetryReportAsync(report, cancellationToken);
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "[LiveTelemetry] Applied live telemetry for Match #{MatchId}, Player {Player} (Hero: {Hero}, Pos: {X:F2},{Y:F2})",
                report.MatchID, report.AccountName, report.HeroIdentifier, report.PositionX, report.PositionY);

        return Json(new { success = applied, matchId = report.MatchID });
    }

    [HttpPost("api/live/prune")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PruneStaleMatchesApi(CancellationToken cancellationToken = default)
    {
        if (!StaffAuthorizationHelper.IsStaff(User))
        {
            logger.LogWarning("[Live] Unauthorized API call to prune matches by user '{User}'", User?.Identity?.Name ?? "Anonymous");
            return StatusCode(StatusCodes.Status403Forbidden, new { success = false, message = "Staff authorization required." });
        }

        int count = await liveMatchService.PruneStaleMatchesAsync(cancellationToken);
        return Json(new { success = true, prunedCount = count });
    }
}
