// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Diagnostics;
using DAWNBRINGER.WebPortal.UI.Infrastructure;
using DAWNBRINGER.WebPortal.UI.Services.Replay;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using StaffStoreItemDto = DAWNBRINGER.WebPortal.UI.Models.StaffStoreItemDto;

namespace DAWNBRINGER.WebPortal.UI.Controllers;

#pragma warning disable CS9113
#pragma warning disable CA1848
#pragma warning disable CA2254
// ReSharper disable All
[Route("tools")]
public class ToolsController(
    IStaffStoreService storeService,
    ILogger<ToolsController> logger,
    IMatchReplayService? replayService = null,
    IHonReplayParser? replayParser = null,
    IPlayerStatsApiClient? statsClient = null,
    IWebHostEnvironment? env = null,
    IHttpClientFactory? httpClientFactory = null,
    IConfiguration? configuration = null) : Controller
{
    private static readonly List<ReplayRenderJobItemDto> s_webQueue = [];
    private static readonly List<ReplayRenderJobItemDto> s_webCompleted = [];
    private static readonly ConcurrentDictionary<int, MatchReplayTimelineDto> s_uploadedTimelines = new();
    private static ReplayRenderJobItemDto? s_webActiveJob = null;
    private static CancellationTokenSource? s_webJobCts = null;
    private static readonly Lock s_queueLock = new();

    [HttpGet("message-generator")]
    public IActionResult MessageGenerator()
    {
        ViewData["Title"] = "HoN Color Message Generator";
        return View();
    }

    [HttpGet("model-inspector")]
    public IActionResult ModelInspector([FromQuery] string? hero = "krixi", [FromQuery] string? alt = "sexy")
    {
        ViewData["InitialHero"] = string.IsNullOrWhiteSpace(hero) ? "krixi" : hero;
        ViewData["InitialAlt"] = string.IsNullOrWhiteSpace(alt) ? "sexy" : alt;
        return View();
    }

    [HttpGet("empath-ar")]
    [Authorize]
    public IActionResult EmpathAR() => RedirectPermanent("/ar-mini-games/empath");

    [HttpGet("devowarsar")]
    [HttpGet("devowars-ar")]
    [HttpGet("devo-wars-ar")]
    [HttpGet("devo-wars")]
    [HttpGet("hook-wars")]
    [Authorize]
    public IActionResult DevoWars([FromQuery] string? room = null) =>
        RedirectPermanent($"/ar-mini-games/devo-wars{(string.IsNullOrWhiteSpace(room) ? "" : $"?room={room}")}");

    [HttpGet("monkey-checkers")]
    [HttpGet("monkey-checkers-ar")]
    [HttpGet("checkers-ar")]
    [HttpGet("checkers")]
    [Authorize]
    public IActionResult MonkeyCheckers([FromQuery] string? room = null) =>
        RedirectPermanent($"/ar-mini-games/monkey-checkers{(string.IsNullOrWhiteSpace(room) ? "" : $"?room={room}")}");

    [HttpGet("replays")]
    [HttpGet("replay-studio")]
    [HttpGet("/replays")]
    [HttpGet("/replay-studio")]
    [Authorize(Policy = StaffAuthorizationHelper.PolicyName)]
    public IActionResult ReplayStudio([FromQuery] int? matchId = null)
    {
        if (!StaffAuthorizationHelper.IsStaff(User))
        {
            logger.LogWarning("[ReplayStudio] Access to Replay Studio denied for non-staff user '{User}'",
                User.Identity?.Name ?? "Anonymous");
            return Forbid();
        }

        ViewData["Title"] = "NEXUS Replay Studio & POV Video Renderer";
        ViewData["InitialMatchId"] = matchId;
        return View("ReplayStudio");
    }

    [HttpGet("replays/recent")]
    [Authorize(Policy = StaffAuthorizationHelper.PolicyName)]
    public async Task<IActionResult> GetRecentStudioMatches(CancellationToken cancellationToken = default)
    {
        if (!StaffAuthorizationHelper.IsStaff(User))
        {
            return StatusCode(StatusCodes.Status403Forbidden,
                new { error = "Staff authorization required to access Replay Studio." });
        }

        List<object> items = [];
        HashSet<int> seenIds = [];

        // 1. Local K2 server M*.log and M*.honreplay files
        IReadOnlyList<(int MatchId, string FilePath, string Kind, DateTime LastWriteUtc)> localArtifacts =
            MatchReplayService.DiscoverLocalReplayArtifacts(configuration, 14);
        foreach ((int mid, string filePath, string kind, DateTime lastWriteUtc) in localArtifacts)
        {
            if (!seenIds.Add(mid))
            {
                continue;
            }

            items.Add(new
            {
                matchId = mid,
                source = kind,
                badge = kind == "server_log" ? "Server Log" : ".honreplay",
                fileName = Path.GetFileName(filePath),
                recordedUtc = lastWriteUtc
            });
        }

        // 2. Recent database matches for the staff user's IGN (if stats API available)
        if (statsClient != null)
        {
            try
            {
                string? ign = User.FindFirst("ign")?.Value ?? User.Identity?.Name;
                if (!string.IsNullOrWhiteSpace(ign))
                {
                    AccountDto? acct = await statsClient.GetAccountByIgnAsync(ign, cancellationToken);
                    if (acct != null)
                    {
                        IReadOnlyList<MatchStatisticsDto> dbMatches =
                            await statsClient.GetMatchesForAccountAsync(acct.ID, 10, cancellationToken);
                        foreach (MatchStatisticsDto m in dbMatches)
                        {
                            if (!seenIds.Add(m.MatchID))
                            {
                                continue;
                            }

                            items.Add(new
                            {
                                matchId = m.MatchID,
                                source = "database",
                                badge = K2MapCoordinateRegistry.GetMapSpec(m.Map).DisplayName,
                                fileName = $"Match #{m.MatchID}",
                                recordedUtc = TimeProvider.System.GetUtcNow().UtcDateTime
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "[ReplayStudio] Optional recent DB match query skipped");
            }
        }

        bool nativeConfigured = !string.IsNullOrWhiteSpace(configuration?["ReplayRenderer:BaseUrl"]);
        return Json(new
        {
            matches = items.Take(16),
            isNativeRendererConfigured = nativeConfigured
        });
    }

    [HttpGet("replays/match/{matchId:int}")]
    [Authorize(Policy = StaffAuthorizationHelper.PolicyName)]
    public async Task<IActionResult> LoadStudioMatch(int matchId, CancellationToken cancellationToken = default)
    {
        if (!StaffAuthorizationHelper.IsStaff(User))
        {
            return StatusCode(StatusCodes.Status403Forbidden,
                new { error = "Staff authorization required to access Replay Studio." });
        }

        if (matchId <= 0)
        {
            return BadRequest(new { error = "Provide a positive Match ID (e.g. 1052 or 1205)." });
        }

        MatchReplayTimelineDto? timeline = null;
        if (s_uploadedTimelines.TryGetValue(matchId, out MatchReplayTimelineDto? uploaded))
        {
            timeline = uploaded;
        }
        else if (replayService != null)
        {
            timeline = await replayService.GetMatchReplayTimelineAsync(matchId, cancellationToken);
        }

        if (timeline == null)
        {
            return NotFound(new { error = $"Match #{matchId} was not found in local server logs, .honreplay archives, or database." });
        }

        logger.LogInformation(
            "[ReplayStudio] Loaded Match #{MatchId} for studio inspection (Source={Source}, Players={PlayerCount}, Duration={Duration}s)",
            timeline.MatchID, timeline.TelemetrySource, timeline.Players.Count, timeline.TotalDurationSeconds);

        return Json(BuildStudioMatchResponse(timeline));
    }

    [HttpPost("replays/upload")]
    [Authorize(Policy = StaffAuthorizationHelper.PolicyName)]
    [RequestSizeLimit(104_857_600)]
    public async Task<IActionResult> UploadReplayFile(IFormFile? replayFile,
        CancellationToken cancellationToken = default)
    {
        if (!StaffAuthorizationHelper.IsStaff(User))
        {
            logger.LogWarning("[ReplayStudio] Denied replay upload attempt for non-staff user '{User}'",
                User.Identity?.Name ?? "Anonymous");
            return StatusCode(StatusCodes.Status403Forbidden,
                new { error = "Staff authorization required to access Replay Studio." });
        }

        if (replayFile == null || replayFile.Length == 0)
        {
            return BadRequest(new { error = "No replay or server log file provided." });
        }

        try
        {
            int inferredMatchId = InferMatchIdFromFileName(replayFile.FileName);
            using MemoryStream ms = new();
            await replayFile.CopyToAsync(ms, cancellationToken);
            ms.Position = 0;

            IHonReplayParser parser = replayParser ?? new HonReplayParser(NullLogger<HonReplayParser>.Instance);
            MatchReplayTimelineDto? timeline = await parser.ParseReplayAsync(ms, inferredMatchId, cancellationToken);
            if (timeline == null || timeline.Players.Count == 0)
            {
                return BadRequest(new
                {
                    error = "Uploaded file could not be parsed as a valid .honreplay archive, .zip log bundle, or K2 M*.log match recording."
                });
            }

            if (timeline.MatchID <= 0)
            {
                timeline.MatchID = inferredMatchId > 0 ? inferredMatchId : Random.Shared.Next(900000, 999999);
            }

            s_uploadedTimelines[timeline.MatchID] = timeline;

            logger.LogInformation(
                "[ReplayStudio] Parsed uploaded file '{FileName}' -> Match #{MatchId} ({Source}, {PlayerCount} players, {Duration}s)",
                replayFile.FileName, timeline.MatchID, timeline.TelemetrySource, timeline.Players.Count,
                timeline.TotalDurationSeconds);

            return Json(BuildStudioMatchResponse(timeline));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[ReplayStudio] Error parsing uploaded replay file '{FileName}'", replayFile.FileName);
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { error = $"Failed to parse replay file: {ex.Message}" });
        }
    }

    [HttpPost("replays/enqueue")]
    [Authorize(Policy = StaffAuthorizationHelper.PolicyName)]
    public IActionResult EnqueueRenderJob([FromBody] ReplayRenderJobRequestDto? request)
    {
        if (!StaffAuthorizationHelper.IsStaff(User))
        {
            logger.LogWarning("[ReplayStudio] Denied replay enqueue attempt for non-staff user '{User}'",
                User.Identity?.Name ?? "Anonymous");
            return StatusCode(StatusCodes.Status403Forbidden,
                new { error = "Staff authorization required to access Replay Studio." });
        }

        if (request == null || request.MatchId <= 0)
        {
            return BadRequest(new { error = "Select or upload a valid match before starting a render job." });
        }

        string jobId = Guid.NewGuid().ToString("N")[..8];
        string safeCamera = string.IsNullOrWhiteSpace(request.CameraMode) ? "PlayerPOV" : request.CameraMode;
        bool nativeConfigured = !string.IsNullOrWhiteSpace(configuration?["ReplayRenderer:BaseUrl"]);

        ReplayRenderJobItemDto item = new()
        {
            JobId = jobId,
            MatchId = request.MatchId,
            TargetAccountId = request.TargetAccountId,
            TargetPlayerIndex = request.TargetPlayerIndex,
            TargetPlayerName = !string.IsNullOrWhiteSpace(request.TargetPlayerName)
                ? request.TargetPlayerName
                : (request.TargetAccountId != 0 ? $"Player #{request.TargetAccountId}" : "Spectator Director"),
            CameraMode = safeCamera,
            Resolution = string.IsNullOrWhiteSpace(request.Resolution) ? "1080p" : request.Resolution,
            Framerate = request.Framerate is 30 or 60 or 120 ? request.Framerate : 60,
            ShowHud = request.ShowHud,
            StartMs = Math.Max(0, request.StartMs),
            DurationMs = Math.Max(0, request.DurationMs),
            Status = "Processing",
            ProgressPercent = 2,
            Fps = request.Framerate > 0 ? request.Framerate : 60,
            OutputPath = $"/videos/renders/Match_{request.MatchId}_{jobId}.webm",
            CreatedAt = TimeProvider.System.GetUtcNow()
        };

        lock (s_queueLock)
        {
            if (s_webActiveJob != null && s_webActiveJob.Status == "Processing")
            {
                s_webQueue.Add(item);
                item.Status = "Queued";
            }
            else
            {
                s_webActiveJob = item;
                s_webJobCts?.Dispose();
                s_webJobCts = new CancellationTokenSource();
            }
        }

        logger.LogInformation(
            "[ReplayStudio] Enqueued render job {JobId} for Match #{MatchId} (Camera={Camera}, Target={Target}, Clip={StartMs}ms+{DurationMs}ms, Engine={Engine})",
            item.JobId, item.MatchId, item.CameraMode, item.TargetPlayerName, item.StartMs, item.DurationMs,
            nativeConfigured ? "native_3d_vulkan" : "tactical_2d_canvas");

        return Json(new
        {
            success = true,
            jobId = item.JobId,
            renderEngine = nativeConfigured ? "native_3d_vulkan" : "tactical_2d_canvas",
            job = item
        });
    }

    [HttpPost("replays/jobs/{jobId}/progress")]
    [Authorize(Policy = StaffAuthorizationHelper.PolicyName)]
    public IActionResult UpdateJobProgress(string jobId, [FromBody] StudioJobProgressUpdateDto? update)
    {
        if (!StaffAuthorizationHelper.IsStaff(User))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Staff authorization required." });
        }

        if (update == null)
        {
            return BadRequest(new { error = "Missing progress payload." });
        }

        lock (s_queueLock)
        {
            ReplayRenderJobItemDto? target = FindJobLocked(jobId);
            if (target == null)
            {
                return NotFound(new { error = $"Render job '{jobId}' not found." });
            }

            if (target.Status == "Cancelled")
            {
                return Json(new { success = false, cancelled = true });
            }

            target.Status = "Processing";
            target.ProgressPercent = Math.Clamp(update.ProgressPercent, 0, 99);
            target.Fps = update.Fps > 0 ? Math.Round(update.Fps, 1) : target.Framerate;
            target.ElapsedSeconds = Math.Round(Math.Max(0, update.ElapsedSeconds), 1);
            target.EstimatedRemainingSeconds = Math.Round(Math.Max(0, update.EstimatedRemainingSeconds), 1);
        }

        return Json(new { success = true, cancelled = false });
    }

    [HttpPost("replays/jobs/{jobId}/upload-video")]
    [Authorize(Policy = StaffAuthorizationHelper.PolicyName)]
    [RequestSizeLimit(104_857_600)]
    public async Task<IActionResult> UploadRenderedVideo(string jobId, IFormFile? videoFile,
        CancellationToken cancellationToken = default)
    {
        if (!StaffAuthorizationHelper.IsStaff(User))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Staff authorization required." });
        }

        if (videoFile == null || videoFile.Length == 0)
        {
            return BadRequest(new { error = "Rendered video stream was empty." });
        }

        string safeJobId = new([.. jobId.Where(char.IsLetterOrDigit)]);
        if (string.IsNullOrWhiteSpace(safeJobId))
        {
            return BadRequest(new { error = "Invalid job ID." });
        }

        ReplayRenderJobItemDto? job;
        lock (s_queueLock)
        {
            job = FindJobLocked(safeJobId);
        }

        if (job == null)
        {
            return NotFound(new { error = $"Render job '{safeJobId}' was not found." });
        }

        string ext = Path.GetExtension(videoFile.FileName).ToLowerInvariant();
        if (ext is not ".webm" and not ".mp4")
        {
            ext = ".webm";
        }

        string? webRoot = env?.WebRootPath;
        if (string.IsNullOrWhiteSpace(webRoot))
        {
            webRoot = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
        }

        string rendersDir = Path.Combine(webRoot, "videos", "renders");
        Directory.CreateDirectory(rendersDir);

        string fileName = $"Match_{job.MatchId}_{safeJobId}{ext}";
        string fullPath = Path.Combine(rendersDir, fileName);

        await using (FileStream fs = new(fullPath, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            await videoFile.CopyToAsync(fs, cancellationToken);
        }

        string publicUrl = $"/videos/renders/{fileName}";

        lock (s_queueLock)
        {
            job.Status = "Completed";
            job.ProgressPercent = 100;
            job.EstimatedRemainingSeconds = 0;
            job.VideoUrl = publicUrl;
            job.OutputPath = publicUrl;
            job.CompletedAt = TimeProvider.System.GetUtcNow();

            s_webQueue.Remove(job);
            if (!s_webCompleted.Contains(job))
            {
                s_webCompleted.Insert(0, job);
                if (s_webCompleted.Count > 20)
                {
                    s_webCompleted.RemoveAt(s_webCompleted.Count - 1);
                }
            }

            if (ReferenceEquals(s_webActiveJob, job))
            {
                s_webActiveJob = s_webQueue.Count > 0 ? s_webQueue[0] : null;
                if (s_webActiveJob != null)
                {
                    s_webQueue.RemoveAt(0);
                    s_webActiveJob.Status = "Processing";
                }
            }
        }

        logger.LogInformation(
            "[ReplayStudio] Saved rendered video for job {JobId} (Match #{MatchId}): {PublicUrl} ({Bytes} bytes)",
            safeJobId, job.MatchId, publicUrl, videoFile.Length);

        return Json(new
        {
            success = true,
            jobId = job.JobId,
            videoUrl = publicUrl,
            downloadUrl = $"/tools/replays/jobs/{job.JobId}/download",
            bytes = videoFile.Length
        });
    }

    [HttpGet("replays/jobs/{jobId}/download")]
    [Authorize(Policy = StaffAuthorizationHelper.PolicyName)]
    public IActionResult DownloadRenderedVideo(string jobId)
    {
        if (!StaffAuthorizationHelper.IsStaff(User))
        {
            return Forbid();
        }

        string safeJobId = new([.. jobId.Where(char.IsLetterOrDigit)]);
        ReplayRenderJobItemDto? job;
        lock (s_queueLock)
        {
            job = FindJobLocked(safeJobId);
        }

        if (job == null || string.IsNullOrWhiteSpace(job.VideoUrl))
        {
            return NotFound("Rendered video file not found.");
        }

        string? webRoot = env?.WebRootPath;
        if (string.IsNullOrWhiteSpace(webRoot))
        {
            webRoot = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
        }

        string relative = job.VideoUrl.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        string physicalPath = Path.GetFullPath(Path.Combine(webRoot, relative));
        if (!System.IO.File.Exists(physicalPath))
        {
            return NotFound("Rendered video file no longer exists on disk.");
        }

        string ext = Path.GetExtension(physicalPath);
        string contentType = ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ? "video/mp4" : "video/webm";
        string downloadName = $"PKV_Match_{job.MatchId}_{job.CameraMode}_{job.JobId}{ext}";
        return PhysicalFile(physicalPath, contentType, downloadName);
    }

    [HttpGet("replays/queue-status")]
    [Authorize(Policy = StaffAuthorizationHelper.PolicyName)]
    public IActionResult GetQueueStatus()
    {
        if (!StaffAuthorizationHelper.IsStaff(User))
        {
            return StatusCode(StatusCodes.Status403Forbidden,
                new { error = "Staff authorization required to access Replay Studio." });
        }

        bool nativeOnline = !string.IsNullOrWhiteSpace(configuration?["ReplayRenderer:BaseUrl"]);

        lock (s_queueLock)
        {
            return Json(new ReplayQueueStatusDto
            {
                IsRendererOnline = nativeOnline,
                ActiveJob = s_webActiveJob,
                QueuedJobs = [.. s_webQueue],
                CompletedJobs = [.. s_webCompleted]
            });
        }
    }

    [HttpPost("replays/cancel")]
    [Authorize(Policy = StaffAuthorizationHelper.PolicyName)]
    public IActionResult CancelJob()
    {
        if (!StaffAuthorizationHelper.IsStaff(User))
        {
            return StatusCode(StatusCodes.Status403Forbidden,
                new { error = "Staff authorization required to access Replay Studio." });
        }

        lock (s_queueLock)
        {
            if (s_webActiveJob != null)
            {
                logger.LogInformation("[ReplayStudio] Cancelled active render job {JobId} for Match #{MatchId}",
                    s_webActiveJob.JobId, s_webActiveJob.MatchId);
                s_webActiveJob.Status = "Cancelled";
                s_webJobCts?.Cancel();
                s_webCompleted.Insert(0, s_webActiveJob);
                s_webActiveJob = s_webQueue.Count > 0 ? s_webQueue[0] : null;
                if (s_webActiveJob != null)
                {
                    s_webQueue.RemoveAt(0);
                    s_webActiveJob.Status = "Processing";
                }

                return Json(new { success = true });
            }
        }

        return Json(new { success = false, message = "No active job" });
    }

    [HttpGet("avatars-catalog")]
    public IActionResult GetAvatarsCatalog()
    {
        IReadOnlyList<StaffStoreItemDto> avatars = storeService.GetSearchableAvatars();
        IEnumerable<object> result = avatars.Select(a => new
        {
            id = a.Id,
            name = a.Name,
            code = a.Code,
            hero = a.HeroName,
            alt = a.AltAvatarSuffix,
            resource = a.Resource
        });
        return Json(result);
    }

    [HttpGet("inventory")]
    [HttpGet("/inventory")]
    [Authorize(Policy = StaffAuthorizationHelper.PolicyName)]
    public IActionResult Inventory(string? accountName)
    {
        if (ControllerContext?.HttpContext != null && !StaffAuthorizationHelper.IsStaff(User))
        {
            logger.LogWarning("[ToolsController.Inventory] Non-staff user '{User}' denied access to /tools/inventory",
                User.Identity?.Name ?? "Anonymous");
            return Forbid();
        }

        return RedirectToAction("Inventory", "Staff", new { accountName });
    }

    [HttpPost("inventory/add")]
    [Authorize(Policy = StaffAuthorizationHelper.PolicyName)]
    public IActionResult AddItem(string accountName, string productCode)
    {
        if (ControllerContext?.HttpContext != null && !StaffAuthorizationHelper.IsStaff(User))
        {
            return Forbid();
        }

        return RedirectToAction("Inventory", "Staff", new { accountName });
    }

    [HttpPost("inventory/remove")]
    [Authorize(Policy = StaffAuthorizationHelper.PolicyName)]
    public IActionResult RemoveItem(string accountName, string productCode)
    {
        if (ControllerContext?.HttpContext != null && !StaffAuthorizationHelper.IsStaff(User))
        {
            return Forbid();
        }

        return RedirectToAction("Inventory", "Staff", new { accountName });
    }

    [HttpPost("inventory/reset")]
    [Authorize(Policy = StaffAuthorizationHelper.PolicyName)]
    public IActionResult ResetInventory(string accountName)
    {
        if (ControllerContext?.HttpContext != null && !StaffAuthorizationHelper.IsStaff(User))
        {
            return Forbid();
        }

        return RedirectToAction("Inventory", "Staff", new { accountName });
    }

    private static ReplayRenderJobItemDto? FindJobLocked(string jobId)
    {
        if (s_webActiveJob != null &&
            string.Equals(s_webActiveJob.JobId, jobId, StringComparison.OrdinalIgnoreCase))
        {
            return s_webActiveJob;
        }

        return s_webQueue.FirstOrDefault(j => string.Equals(j.JobId, jobId, StringComparison.OrdinalIgnoreCase))
               ?? s_webCompleted.FirstOrDefault(j => string.Equals(j.JobId, jobId, StringComparison.OrdinalIgnoreCase));
    }

    private static int InferMatchIdFromFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return 0;
        }

        string stem = Path.GetFileNameWithoutExtension(fileName);
        string digits = new([.. stem.Where(char.IsDigit)]);
        return int.TryParse(digits, out int parsed) && parsed > 0 ? parsed : 0;
    }

    private static object BuildStudioMatchResponse(MatchReplayTimelineDto timeline)
    {
        K2MapSpec spec = K2MapCoordinateRegistry.GetMapSpec(timeline.Map);
        TimeSpan dur = TimeSpan.FromSeconds(Math.Max(1, timeline.TotalDurationSeconds));
        string durationFormatted = dur.Hours > 0
            ? dur.ToString(@"h\:mm\:ss")
            : dur.ToString(@"mm\:ss");

        List<object> legion = [];
        List<object> hellbourne = [];
        for (int i = 0; i < timeline.Players.Count; i++)
        {
            ReplayPlayerSummary p = timeline.Players[i];
            var dto = new
            {
                playerIndex = i,
                accountId = p.AccountID,
                name = p.AccountName,
                heroName = p.HeroIdentifier,
                heroDisplayName = p.HeroDisplayName,
                heroIconUrl = DAWNBRINGER.WebPortal.UI.Services.HeroAndMapAssetResolver.ResolveHeroIconUrl(p.HeroIdentifier),
                team = p.Team,
                level = p.FinalLevel,
                kills = p.FinalKills,
                deaths = p.FinalDeaths,
                assists = p.FinalAssists,
                gpm = p.FinalGpm,
                xpm = p.FinalXpm,
                gold = p.FinalGold,
                items = p.FinalItems
            };

            if (p.Team == 1) legion.Add(dto);
            else hellbourne.Add(dto);
        }

        return new
        {
            matchId = timeline.MatchID,
            mapSlug = spec.Slug,
            mapName = spec.DisplayName,
            minimapUrl = DAWNBRINGER.WebPortal.UI.Services.HeroAndMapAssetResolver.ResolveMinimapUrl(spec.Slug),
            durationSeconds = timeline.TotalDurationSeconds,
            durationDisplay = durationFormatted,
            gameMode = timeline.GameMode,
            winnerTeam = timeline.WinnerTeam,
            scoreLegion = timeline.ScoreLegion,
            scoreHellbourne = timeline.ScoreHellbourne,
            telemetrySource = timeline.TelemetrySource,
            telemetryLabel = timeline.TelemetryLabel,
            legionPlayers = legion,
            hellbournePlayers = hellbourne,
            timeline
        };
    }

    public sealed class StudioJobProgressUpdateDto
    {
        public double ProgressPercent { get; set; }
        public double Fps { get; set; }
        public double ElapsedSeconds { get; set; }
        public double EstimatedRemainingSeconds { get; set; }
    }
}
