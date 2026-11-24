// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Models.Replay;
using KONGOR.MasterServer.Serialization;

namespace KONGOR.MasterServer.Services.Replay;

public partial class ReplayToVideoConverter(
    IOptions<ReplayConverterSettings> converterOptions,
    IOptions<VideoStorageSettings> storageOptions,
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory,
    MerrickContext dbContext,
    ILogger<ReplayToVideoConverter> logger) : IReplayToVideoConverter
{
    private readonly ReplayConverterSettings _settings = converterOptions.Value;
    private readonly VideoStorageSettings _storage = storageOptions.Value;

    public async Task<string?> ConvertReplayToVideoAsync(string matchId, string replayFilePath,
        CancellationToken cancellationToken = default)
    {
        LogStartConversion(matchId, replayFilePath);

        if (!int.TryParse(matchId, out int mid))
        {
            logger.LogWarning("[ReplayConverter] Invalid MatchId '{MatchId}' provided.", matchId);
            return null;
        }

        MatchStatistics? match =
            await dbContext.MatchStatistics.FirstOrDefaultAsync(m => m.MatchID == mid, cancellationToken);
        if (match == null)
            logger.LogWarning("[ReplayConverter] Match #{MatchId} not found in database; submitting render job anyway.",
                mid);

        string outputDir = _storage.LocalDisk.UploadDirectory;
        if (string.IsNullOrWhiteSpace(outputDir))
            outputDir = Path.Combine(AppContext.BaseDirectory, "artifacts", "videos");

        if (!Directory.Exists(outputDir)) Directory.CreateDirectory(outputDir);

        string outputMp4Path = Path.Combine(outputDir, $"M{matchId}.mp4");
        string relativeVideoUrl = $"/videos/M{matchId}.mp4";

        string rendererUrl = configuration["REPLAY_RENDERER_URL"]
                             ?? configuration["ReplayRenderer:Url"]
                             ?? Environment.GetEnvironmentVariable("REPLAY_RENDERER_URL")
                             ?? "http://localhost:5556";

        string masterServerUrl = configuration["MASTER_SERVER_ADDRESS"]
                                 ?? configuration["MasterServer:Address"]
                                 ?? Environment.GetEnvironmentVariable("MASTER_SERVER_ADDRESS")
                                 ?? "http://localhost:55555";

        if (!masterServerUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !masterServerUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            masterServerUrl = "http://" + masterServerUrl;

        string callbackUrl = $"{masterServerUrl.TrimEnd('/')}/api/replays/render-callback";

        ReplayRenderJobRequestDto requestPayload = new()
        {
            MatchId = mid,
            ReplayFilePath = replayFilePath,
            OutputPath = outputMp4Path,
            CallbackUrl = callbackUrl
        };

        try
        {
            using HttpClient client = httpClientFactory.CreateClient("ReplayRendererClient");
            client.BaseAddress = new Uri(rendererUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(15);

            logger.LogInformation(
                "[ReplayConverter] Submitting render job for Match #{MatchId} to {RendererUrl}/render with callback {CallbackUrl}",
                mid, rendererUrl, callbackUrl);

            HttpResponseMessage response = await client.PostAsJsonAsync("render", requestPayload, MasterServerJsonContext.Default.ReplayRenderJobRequestDto, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                ReplayRenderJobResponseDto? jobResponse =
                    await response.Content.ReadFromJsonAsync(MasterServerJsonContext.Default.ReplayRenderJobResponseDto, cancellationToken);
                logger.LogInformation(
                    "[ReplayConverter] Render job '{JobId}' submitted successfully to ReplayRenderer for Match #{MatchId} (Status {StatusCode}, JobStatus: '{JobStatus}')",
                    jobResponse?.JobId, mid, response.StatusCode, jobResponse?.Status);
                return relativeVideoUrl;
            }

            logger.LogWarning(
                "[ReplayConverter] ReplayRenderer returned non-success code {StatusCode} for Match #{MatchId}",
                response.StatusCode, mid);
            return relativeVideoUrl;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "[ReplayConverter] Could not reach ReplayRenderer at {RendererUrl} for Match #{MatchId}: {Message}",
                rendererUrl, matchId, ex.Message);
            return relativeVideoUrl;
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[ReplayConverter] Starting video conversion for Match #{MatchId} from replay {ReplayPath}")]
    private partial void LogStartConversion(string matchId, string replayPath);

    [LoggerMessage(Level = LogLevel.Warning,
        Message =
            "[ReplayConverter] HonExecutablePath is not configured in dotnet user-secrets or appsettings. Video conversion skipped.")]
    private partial void LogMissingHonExecutable();

    [LoggerMessage(Level = LogLevel.Error,
        Message = "[ReplayConverter] HonExecutablePath '{HonPath}' does not exist on disk.")]
    private partial void LogHonExecutableNotFound(string honPath);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[ReplayConverter] Queued conversion using {HonPath}. Source: {Source}, Output: {Output}")]
    private partial void LogConversionQueued(string honPath, string source, string output);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[ReplayConverter] Launching converter process: {FileName} {Arguments}")]
    private partial void LogProcessStarting(string fileName, string arguments);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[ReplayConverter] Updated database for Match #{MatchId} with VideoUrl: {VideoUrl}")]
    private partial void LogDatabaseUpdated(int matchId, string videoUrl);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "[ReplayConverter] Failed to convert replay to video for Match #{MatchId}")]
    private partial void LogConversionError(Exception ex, string matchId);
}
