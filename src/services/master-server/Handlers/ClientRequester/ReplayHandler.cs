// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using KONGOR.MasterServer.Extensions;
using KONGOR.MasterServer.Models.RequestResponse.ClientRequester;
using KONGOR.MasterServer.Services.Replay;
using KONGOR.MasterServer.Services.Security;
using KONGOR.MasterServer.Services.Storage;
using NEXUS.Contracts.Data;
using NEXUS.Contracts.Security;

namespace KONGOR.MasterServer.Handlers.ClientRequester;

public partial class ReplayHandler(
    ILogger<ReplayHandler> logger,
    IReplayStorageProvider storageProvider,
    IMatchHistoryRepository matchHistoryRepository,
    IReplayToVideoConverter? videoConverter = null,
    IDatabase? distributedCache = null,
    IConfiguration? configuration = null,
    IHostEnvironment? hostEnvironment = null) : IClientRequestHandler
{
    private ILogger Logger { get; } = logger;
    private IReplayStorageProvider StorageProvider { get; } = storageProvider;
    private IMatchHistoryRepository MatchHistoryRepository { get; } = matchHistoryRepository;
    private IReplayToVideoConverter? VideoConverter { get; } = videoConverter;
    private IDatabase? DistributedCache { get; } = distributedCache;
    private IConfiguration? Configuration { get; } = configuration;
    private IHostEnvironment? HostEnvironment { get; } = hostEnvironment;

    private static bool IsSafeMatchId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        foreach (char c in value)
        {
            if (!char.IsLetterOrDigit(c) && c != '_' && c != '-')
                return false;
        }

        return true;
    }

    public async Task<IActionResult> HandleRequestAsync(HttpContext context,
        CancellationToken cancellationToken = default)
    {
        HttpRequest Request = context.Request;

        LogReceiveUploadRequest();

        if (Request.ContentLength is > ReplayUploadAuthorizer.MaxReplayUploadBytes)
        {
            Logger.LogWarning(
                "[UploadReplay] Rejected oversized replay upload ({ContentLength} bytes > {MaxBytes} bytes).",
                Request.ContentLength.Value, ReplayUploadAuthorizer.MaxReplayUploadBytes);
            return new ObjectResult("Replay payload exceeds maximum allowed size.")
            {
                StatusCode = StatusCodes.Status413PayloadTooLarge
            };
        }

        (bool isAuthorized, _) = await ReplayUploadAuthorizer.AuthorizeAsync(
            context,
            DistributedCache,
            Configuration,
            HostEnvironment?.IsDevelopment() ?? false,
            Logger,
            cancellationToken);
        if (!isAuthorized)
        {
            return new UnauthorizedObjectResult("Unauthorized replay upload request.");
        }

        try
        {
            IFormFile? file = Request.TryGetFormFile("file");
            string? matchID = Request.GetFormOrQueryValue("match_id");
            string? cookie = ClientRequestHelper.GetCookie(Request);

            LogUploadDetails(matchID ?? "NULL", SecurityMasking.MaskToken(cookie), file?.FileName ?? "NULL",
                file?.Length);

            if (file is null || file.Length == 0)
            {
                LogNoFileUploaded();
                return new BadRequestObjectResult("No file uploaded");
            }

            if (file.Length > ReplayUploadAuthorizer.MaxReplayUploadBytes)
            {
                Logger.LogWarning(
                    "[UploadReplay] Rejected oversized replay file '{FileName}' ({Length} bytes > {MaxBytes} bytes).",
                    file.FileName, file.Length, ReplayUploadAuthorizer.MaxReplayUploadBytes);
                return new ObjectResult("Replay payload exceeds maximum allowed size.")
                {
                    StatusCode = StatusCodes.Status413PayloadTooLarge
                };
            }

            if (string.IsNullOrEmpty(matchID))
            {
                LogMissingMatchId();
            }
            else if (!IsSafeMatchId(matchID))
            {
                Logger.LogWarning("[UploadReplay] Rejected unsafe match_id parameter '{MatchID}'.", matchID);
                return new BadRequestObjectResult("Invalid match_id parameter");
            }

            // Sanitize file name to pure basename before passing to storage provider
            string rawCandidate = !string.IsNullOrEmpty(matchID)
                ? $"M{matchID}.honreplay"
                : Path.GetFileName(file.FileName ?? string.Empty);

            if (string.IsNullOrWhiteSpace(rawCandidate) ||
                rawCandidate.Contains("..", StringComparison.Ordinal) ||
                (!rawCandidate.EndsWith(".honreplay", StringComparison.OrdinalIgnoreCase) &&
                 !rawCandidate.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
            {
                Logger.LogWarning(
                    "[UploadReplay] Rejected disallowed replay filename '{RawFileName}' (MatchID: '{MatchID}').",
                    file.FileName, matchID ?? "NULL");
                return new BadRequestObjectResult("Invalid replay file name");
            }

            string fileName = rawCandidate;

            // Upload using provider
            string downloadUrl;
            await using (Stream fileStream = file.OpenReadStream())
            {
                downloadUrl =
                    await StorageProvider.UploadReplayAsync(matchID ?? "unknown", fileStream, fileName,
                        cancellationToken);
            }

            // Update database match statistics
            if (!string.IsNullOrEmpty(matchID) && int.TryParse(matchID, out int mid))
            {
                int? fileSize = file.Length > 0 ? (int)file.Length : null;
                await MatchHistoryRepository.UpdateMatchReplayUrlAsync(mid, downloadUrl, fileSize, cancellationToken);

                // Trigger background video conversion if converter is available
                if (VideoConverter != null)
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            string localUploadDir = Configuration?["ReplayStorage:LocalDisk:UploadDirectory"]
                                ?? Path.Combine(AppContext.BaseDirectory, "data", "replays");
                            string localReplayFile = Path.Combine(localUploadDir, fileName);
                            string replayTarget = File.Exists(localReplayFile) ? localReplayFile : downloadUrl;

                            await VideoConverter.ConvertReplayToVideoAsync(matchID, replayTarget);
                        }
                        catch (Exception ex)
                        {
                            if (Logger.IsEnabled(LogLevel.Error))
                                Logger.LogError(ex,
                                    "[UploadReplay] Background video conversion failed for match #{MatchId}", matchID);
                        }
                    }, CancellationToken.None);
            }

            LogReplaySaved(fileName + " -> " + downloadUrl);

            ReplayResponseDTO response = new() { Success = true };

            return new PhpResult(response);
        }
        catch (ArgumentException argEx)
        {
            Logger.LogWarning(argEx, "[UploadReplay] Validation rejected replay upload.");
            return new BadRequestObjectResult("Invalid replay file name");
        }
        catch (Exception ex)
        {
            LogUploadFailure(ex);
            return new ObjectResult("Internal Server Error") { StatusCode = 500 };
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "[UploadReplay] Received Replay Upload Request.")]
    private partial void LogReceiveUploadRequest();

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[UploadReplay] MatchID: {MatchID}, Cookie: {CookiePrefix}, File: {FileName} ({Length} bytes)")]
    private partial void LogUploadDetails(string matchID, string cookiePrefix, string fileName, long? length);

    [LoggerMessage(Level = LogLevel.Error, Message = "[UploadReplay] No file uploaded.")]
    private partial void LogNoFileUploaded();

    [LoggerMessage(Level = LogLevel.Warning, Message = "[UploadReplay] Missing match_id param.")]
    private partial void LogMissingMatchId();

    [LoggerMessage(Level = LogLevel.Information, Message = "[UploadReplay] Saved replay to {FilePath}")]
    private partial void LogReplaySaved(string filePath);

    [LoggerMessage(Level = LogLevel.Error, Message = "[UploadReplay] Failed to handle upload.")]
    private partial void LogUploadFailure(Exception ex);
}
