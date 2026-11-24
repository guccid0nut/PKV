// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using KONGOR.MasterServer.Logging;
using KONGOR.MasterServer.Services.Replay;
using KONGOR.MasterServer.Services.Security;
using KONGOR.MasterServer.Services.Storage;
using Microsoft.Extensions.Primitives;
using NEXUS.Contracts.Data;

namespace KONGOR.MasterServer.Controllers.ReplayController;

[ApiController]
[Route("replays")]
public class ReplayController(
    ILogger<ReplayController> logger,
    IWebHostEnvironment environment,
    IReplayStorageProvider storageProvider,
    IMatchHistoryRepository matchHistoryRepository,
    IReplayAutoUploadService? autoUploadService = null,
    IReplayOfflineBufferService? offlineBufferService = null,
    IDatabase? distributedCache = null,
    IConfiguration? configuration = null)
    : ControllerBase
{
    public const long MaxReplayUploadBytes = ReplayUploadAuthorizer.MaxReplayUploadBytes;

    private readonly IWebHostEnvironment _environment = environment;

    private static bool IsSafeMatchIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        foreach (char c in value)
        {
            if (!char.IsLetterOrDigit(c) && c != '_' && c != '-')
                return false;
        }

        return true;
    }

    [HttpPost("upload")]
    [HttpPut("upload")]
    [HttpHead("upload")]
    [HttpPost("{accountId}/{matchId}.honreplay")]
    [HttpPut("{accountId}/{matchId}.honreplay")]
    [HttpHead("{accountId}/{matchId}.honreplay")]
    [RequestSizeLimit(MaxReplayUploadBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxReplayUploadBytes)]
    public async Task<IActionResult> UploadReplay(string? accountId = null, string? matchId = null,
        CancellationToken cancellationToken = default)
    {
        if (Request.Method == "HEAD") return Ok();

        if (Request.ContentLength is > MaxReplayUploadBytes)
        {
            logger.LogWarning(
                "[ReplayController] Rejected oversized replay upload ({ContentLength} bytes > {MaxBytes} bytes) from IP '{RemoteIp}'.",
                Request.ContentLength.Value, MaxReplayUploadBytes, HttpContext.Connection.RemoteIpAddress);
            return StatusCode(StatusCodes.Status413PayloadTooLarge, "Replay payload exceeds maximum allowed size.");
        }

        (bool isAuthorized, _) = await ReplayUploadAuthorizer.AuthorizeAsync(
            HttpContext,
            distributedCache,
            configuration,
            _environment.IsDevelopment(),
            logger,
            cancellationToken);
        if (!isAuthorized)
        {
            return Unauthorized("Unauthorized replay upload request.");
        }

        try
        {
            string? resolvedMatchId = matchId;

            // Handle Multipart Form (Standard Uploads from some clients)
            if (Request.HasFormContentType)
            {
                IFormCollection form = await Request.ReadFormAsync(cancellationToken);

                if (string.IsNullOrEmpty(resolvedMatchId) && form.TryGetValue("match_id", out StringValues formMatchId))
                    resolvedMatchId = formMatchId.ToString();

                if (string.IsNullOrEmpty(resolvedMatchId)) return BadRequest("Missing match ID.");
                if (!IsSafeMatchIdentifier(resolvedMatchId))
                {
                    logger.LogWarning(
                        "[ReplayController] Rejected replay form upload with unsafe match_id '{MatchId}'.",
                        resolvedMatchId);
                    return BadRequest("Invalid match ID.");
                }

                IFormFile? file = form.Files.Count > 0 ? form.Files[0] : null;
                if (file is null || file.Length == 0) return BadRequest("No file provided.");
                if (file.Length > MaxReplayUploadBytes)
                {
                    logger.LogWarning(
                        "[ReplayController] Rejected oversized multipart replay file ({FileLength} bytes > {MaxBytes} bytes) for Match '{MatchId}'.",
                        file.Length, MaxReplayUploadBytes, resolvedMatchId);
                    return StatusCode(StatusCodes.Status413PayloadTooLarge,
                        "Replay payload exceeds maximum allowed size.");
                }

                string finalFileName = $"M{resolvedMatchId}.honreplay";
                await using (Stream fileStream = file.OpenReadStream())
                {
                    try
                    {
                        string downloadUrl = await storageProvider.UploadReplayAsync(resolvedMatchId ?? "unknown",
                            fileStream, finalFileName, cancellationToken);

                        if (resolvedMatchId != null && int.TryParse(resolvedMatchId, out int mid))
                        {
                            await matchHistoryRepository.UpdateMatchReplayUrlAsync(mid, downloadUrl,
                                file.Length > 0 ? (int)file.Length : null, cancellationToken);
                        }

                        logger.LogSavedReplayForm(finalFileName + " -> " + downloadUrl);
                    }
                    catch (ArgumentException argEx)
                    {
                        logger.LogWarning(argEx,
                            "[ReplayController] Rejected invalid replay filename '{FileName}' for Match '{MatchId}'.",
                            finalFileName, resolvedMatchId);
                        return BadRequest("Invalid replay file name.");
                    }
                    catch (Exception ex) when (offlineBufferService != null && resolvedMatchId != null &&
                                               int.TryParse(resolvedMatchId, out int mid))
                    {
                        logger.LogWarning(ex,
                            "[ReplayController] Direct upload to cloud failed. Spooling to offline buffer for Match #{MatchId}",
                            mid);
                        if (fileStream.CanSeek) fileStream.Position = 0;
                        await offlineBufferService.EnqueuePendingReplayAsync(mid, finalFileName, fileStream, ex.Message,
                            cancellationToken);
                    }
                }

                return Ok();
            }

            // Handle Raw Binary Body (Client specific behavior)
            if (Request.ContentLength > 0 || Request.Body.CanRead)
            {
                if (resolvedMatchId != null && !IsSafeMatchIdentifier(resolvedMatchId))
                {
                    logger.LogWarning(
                        "[ReplayController] Rejected raw replay upload with unsafe matchId '{MatchId}'.",
                        resolvedMatchId);
                    return BadRequest("Invalid match ID.");
                }

                string finalFileName = resolvedMatchId != null
                    ? $"M{resolvedMatchId}.honreplay"
                    : $"upload_{TimeProvider.System.GetUtcNow().Ticks}.honreplay";

                try
                {
                    string downloadUrl =
                        await storageProvider.UploadReplayAsync(resolvedMatchId ?? "unknown", Request.Body,
                            finalFileName, cancellationToken);

                    if (resolvedMatchId != null && int.TryParse(resolvedMatchId, out int mid))
                    {
                        await matchHistoryRepository.UpdateMatchReplayUrlAsync(mid, downloadUrl,
                            Request.ContentLength is > 0 ? (int)Request.ContentLength.Value : null,
                            cancellationToken);
                    }

                    logger.LogSavedReplayRaw(finalFileName + " -> " + downloadUrl);
                }
                catch (ArgumentException argEx)
                {
                    logger.LogWarning(argEx,
                        "[ReplayController] Rejected invalid raw replay filename '{FileName}' for Match '{MatchId}'.",
                        finalFileName, resolvedMatchId);
                    return BadRequest("Invalid replay file name.");
                }
                catch (Exception ex) when (offlineBufferService != null && resolvedMatchId != null &&
                                           int.TryParse(resolvedMatchId, out int mid))
                {
                    logger.LogWarning(ex,
                        "[ReplayController] Direct upload to cloud failed. Spooling to offline buffer for Match #{MatchId}",
                        mid);
                    if (Request.Body.CanSeek)
                    {
                        Request.Body.Position = 0;
                        await offlineBufferService.EnqueuePendingReplayAsync(mid, finalFileName, Request.Body,
                            ex.Message, cancellationToken);
                    }
                }

                return Ok();
            }

            return BadRequest("No content received.");
        }
        catch (Exception ex)
        {
            logger.LogUploadFailed(ex);
            return StatusCode(StatusCodes.Status500InternalServerError, "Internal Server Error");
        }
    }

    [HttpGet("download/{matchId}")]
    [HttpHead("download/{matchId}")]
    [HttpPost("download/{matchId}")]
    public async Task<IActionResult> DownloadReplay(string matchId, CancellationToken cancellationToken = default)
    {
        if (!int.TryParse(matchId, out int mid)) return BadRequest("Invalid Match ID");

        MatchStatistics? match = await matchHistoryRepository.GetMatchByIdAsync(mid, cancellationToken);

        string fileName = match?.FileName ?? $"M{mid}.honreplay";
        if (!fileName.StartsWith("M", StringComparison.OrdinalIgnoreCase) ||
            !fileName.EndsWith(".honreplay", StringComparison.OrdinalIgnoreCase))
            fileName = $"M{mid}.honreplay";

        if (match != null && !string.IsNullOrEmpty(match.ReplayUrl) &&
            match.ReplayUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return Redirect(match.ReplayUrl);

        if (autoUploadService != null)
            _ = autoUploadService.QueueReplayUploadAsync(mid, fileName, CancellationToken.None);

        return Redirect($"/replays/{fileName}");
    }

    [HttpGet("{fileName}")]
    [HttpHead("{fileName}")]
    [HttpPost("{fileName}")]
    [HttpOptions("{fileName}")]
    public async Task<IActionResult> ServeReplayFile(string fileName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return BadRequest("Invalid replay file name");
        string safeFileName = Path.GetFileName(fileName);
        if (!string.Equals(safeFileName, fileName, StringComparison.Ordinal) ||
            fileName.Contains("..", StringComparison.Ordinal))
        {
            logger.LogWarning("[ReplayController] Blocked ServeReplayFile path traversal attempt: '{FileName}'.",
                fileName);
            return BadRequest("Invalid replay file name");
        }

        int matchId = 0;
        if (safeFileName.StartsWith("M", StringComparison.OrdinalIgnoreCase) &&
            safeFileName.EndsWith(".honreplay", StringComparison.OrdinalIgnoreCase))
        {
            string idPart = safeFileName.Substring(1, safeFileName.Length - 11);
            if (int.TryParse(idPart, out int parsedId)) matchId = parsedId;
        }

        if (MatchSummary.TryFindReplayFile(matchId, safeFileName, out string resolvedPath, out _))
        {
            if (matchId > 0 && autoUploadService != null)
                _ = autoUploadService.QueueReplayUploadAsync(matchId, safeFileName, CancellationToken.None);

            return PhysicalFile(resolvedPath, "application/octet-stream", safeFileName, true);
        }

        if (matchId > 0)
        {
            MatchStatistics? match = await matchHistoryRepository.GetMatchByIdAsync(matchId, cancellationToken);
            if (match != null && !string.IsNullOrEmpty(match.ReplayUrl) &&
                match.ReplayUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
                !match.ReplayUrl.EndsWith($"/replays/{safeFileName}", StringComparison.OrdinalIgnoreCase))
                return Redirect(match.ReplayUrl);
        }

        return NotFound();
    }
}
