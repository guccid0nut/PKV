// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Threading.Channels;
using ASPIRE.Common.Hosting;
using ASPIRE.Common.Resilience;
using KONGOR.MasterServer.Services.Storage;
using Polly;

namespace KONGOR.MasterServer.Services.Replay;

public sealed record ReplayUploadTask(int MatchId, string? CustomFileName, DateTimeOffset EnqueuedAt);

public sealed class ReplayAutoUploadService : ResilientBackgroundService, IReplayAutoUploadService
{
    private readonly Channel<ReplayUploadTask> _channel;
    private readonly ILogger<ReplayAutoUploadService> _logger;
    private readonly IReplayOfflineBufferService _offlineBufferService;
    private readonly ResiliencePipeline _resiliencePipeline;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IReplayStorageProvider _storageProvider;

    public ReplayAutoUploadService(
        IServiceScopeFactory scopeFactory,
        IReplayStorageProvider storageProvider,
        IReplayOfflineBufferService offlineBufferService,
        ILogger<ReplayAutoUploadService> logger,
        ResiliencePipeline? resiliencePipeline = null)
        : base(logger, nameof(ReplayAutoUploadService))
    {
        _scopeFactory = scopeFactory;
        _storageProvider = storageProvider;
        _offlineBufferService = offlineBufferService;
        _logger = logger;
        _resiliencePipeline = resiliencePipeline ?? NexusResiliencePipelines.CreateReplayUploadPipeline(_logger);

        BoundedChannelOptions options = new(500)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        };

        _channel = Channel.CreateBounded<ReplayUploadTask>(options);
    }

    public async Task QueueReplayUploadAsync(int matchId, string? customFileName = null,
        CancellationToken cancellationToken = default)
    {
        ReplayUploadTask task = new(matchId, customFileName, TimeProvider.System.GetUtcNow());
        await _channel.Writer.WriteAsync(task, cancellationToken);

        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("[ReplayAutoUpload] Queued background cloud upload for Match #{MatchId}", matchId);
    }

    public async Task<bool> UploadSingleReplayAsync(int matchId, string? customFileName = null,
        CancellationToken cancellationToken = default)
    {
        string standardFileName = $"M{matchId}.honreplay";
        string targetFileName = !string.IsNullOrWhiteSpace(customFileName) ? customFileName : standardFileName;

        bool found = false;
        string resolvedPath = string.Empty;
        int foundFileSize = 0;

        found = MatchSummary.TryFindReplayFile(matchId, targetFileName, out resolvedPath, out foundFileSize);
        if (!found)
        {
            using (IServiceScope probeScope = _scopeFactory.CreateScope())
            {
                MerrickContext probeDb = probeScope.ServiceProvider.GetRequiredService<MerrickContext>();
                MatchStatistics? probeStats = await probeDb.MatchStatistics
                    .AsNoTracking()
                    .FirstOrDefaultAsync(m => m.MatchID == matchId, cancellationToken)
                    .ConfigureAwait(false);
                if (probeStats is { FileSize: <= 0 })
                {
                    if (_logger.IsEnabled(LogLevel.Information))
                        _logger.LogInformation(
                            "[ReplayAutoUpload] Match #{MatchId} reported FileSize={FileSize} and has no replay file on disk; skipping cloud upload cleanly.",
                            matchId, probeStats.FileSize);
                    return false;
                }
            }

            for (int attempt = 1; attempt <= 4 && !found; attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
                found = MatchSummary.TryFindReplayFile(matchId, targetFileName, out resolvedPath, out foundFileSize);
            }

            if (!found)
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                    _logger.LogWarning(
                        "[ReplayAutoUpload] Replay file for Match #{MatchId} ({FileName}) not found on disk after bounded readiness poll; skipping without tripping upload circuit breaker.",
                        matchId, targetFileName);
                return false;
            }
        }

        try
        {
            await _resiliencePipeline.ExecuteAsync(async _ =>
            {
                if (MatchSummary.TryFindReplayFile(matchId, targetFileName, out resolvedPath, out foundFileSize))
                {
                    found = true;
                    return;
                }

                return; // Guarded above; never throw FileNotFoundException into the shared circuit breaker pipeline.
                    // Replay readiness verified prior to storage resilience pipeline.
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Warning))
                _logger.LogWarning(
                    "[ReplayAutoUpload] Replay file for Match #{MatchId} ({FileName}) not found on disk after resilient discovery retries.",
                    matchId, targetFileName);
            return false;
        }

        if (!found || string.IsNullOrWhiteSpace(resolvedPath))
        {
            return false;
        }

        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {
            string downloadUrl = await _resiliencePipeline.ExecuteAsync(async ct =>
            {
                await using FileStream fileStream = new(resolvedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                return await _storageProvider.UploadReplayAsync(matchId.ToString(), fileStream, targetFileName,
                    ct).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);

            stopwatch.Stop();

            using IServiceScope scope = _scopeFactory.CreateScope();
            MerrickContext dbContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();

            MatchStatistics? matchStats =
                await dbContext.MatchStatistics.FirstOrDefaultAsync(m => m.MatchID == matchId, cancellationToken);
            if (matchStats != null)
            {
                matchStats.ReplayUrl = downloadUrl;
                if (foundFileSize > 0) matchStats.FileSize = foundFileSize;

                await dbContext.SaveChangesAsync(cancellationToken);
            }

            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation(
                    "[ReplayAutoUpload] Successfully uploaded Match #{MatchId} ({FileSize} bytes) in {ElapsedMs}ms -> {DownloadUrl}",
                    matchId, foundFileSize, stopwatch.ElapsedMilliseconds, downloadUrl);

            return true;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            if (_logger.IsEnabled(LogLevel.Error))
                _logger.LogError(ex,
                    "[ReplayAutoUpload] Failed uploading replay for Match #{MatchId} from {Path}. Spooling to offline buffer.",
                    matchId, resolvedPath);

            try
            {
                await _offlineBufferService.EnqueuePendingReplayFromDiskAsync(matchId, targetFileName, resolvedPath,
                    ex.Message, cancellationToken);
            }
            catch (Exception bufferEx)
            {
                if (_logger.IsEnabled(LogLevel.Error))
                    _logger.LogError(bufferEx,
                        "[ReplayAutoUpload] Failed spooling replay for Match #{MatchId} to offline buffer", matchId);
            }

            return false;
        }
    }

    public async Task<int> SyncHistoricalReplaysAsync(IEnumerable<string>? customDirectories = null,
        CancellationToken cancellationToken = default)
    {
        List<string> directories = customDirectories != null
            ? [.. customDirectories]
            : MatchSummary.GetAllReplayDirectories();
        if (directories.Count == 0) return 0;

        List<string> replayFiles = [];
        foreach (string dir in directories)
            if (Directory.Exists(dir))
                try
                {
                    string[] files = Directory.GetFiles(dir, "M*.honreplay", SearchOption.TopDirectoryOnly);
                    foreach (string f in files)
                        if (!replayFiles.Contains(f, StringComparer.OrdinalIgnoreCase))
                            replayFiles.Add(f);
                }
                catch
                {
                    // Ignore inaccessible directories
                }

        if (replayFiles.Count == 0) return 0;

        using IServiceScope scope = _scopeFactory.CreateScope();
        MerrickContext dbContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();

        int syncedCount = 0;
        SemaphoreSlim throttle = new(3, 3);
        List<Task> uploadTasks = [];

        foreach (string filePath in replayFiles)
        {
            string fileName = Path.GetFileName(filePath);
            if (!fileName.StartsWith("M", StringComparison.OrdinalIgnoreCase) ||
                !fileName.EndsWith(".honreplay", StringComparison.OrdinalIgnoreCase))
                continue;

            string idPart = fileName.Substring(1, fileName.Length - 11);
            if (!int.TryParse(idPart, out int matchId)) continue;

            MatchStatistics? match = await dbContext.MatchStatistics
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.MatchID == matchId, cancellationToken);

            // If match exists in DB and replay URL is either missing or pointing to localhost/relative path, sync it!
            bool needsUpload = match != null && (string.IsNullOrWhiteSpace(match.ReplayUrl) ||
                               match.ReplayUrl.Contains("localhost") || match.ReplayUrl.Contains("127.0.0.1") ||
                               match.ReplayUrl.StartsWith("/replays", StringComparison.OrdinalIgnoreCase));

            if (needsUpload)
                uploadTasks.Add(Task.Run(async () =>
                {
                    await throttle.WaitAsync(cancellationToken);
                    try
                    {
                        bool success = await UploadSingleReplayAsync(matchId, fileName, cancellationToken);
                        if (success) Interlocked.Increment(ref syncedCount);
                    }
                    finally
                    {
                        throttle.Release();
                    }
                }, cancellationToken));
        }

        if (uploadTasks.Count > 0)
        {
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("[ReplayAutoUpload] Syncing {Count} historical replays to cloud storage...",
                    uploadTasks.Count);

            await Task.WhenAll(uploadTasks);

            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation(
                    "[ReplayAutoUpload] Completed historical replay sync. Uploaded {SyncedCount} replays.",
                    syncedCount);
        }

        return syncedCount;
    }

    protected override async Task ExecuteCoreAsync(CancellationToken stoppingToken)
    {
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("[ReplayAutoUpload] Background replay upload worker started.");

        // Run a lightweight historical replay sync on startup in the background
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(5000, stoppingToken);
                await SyncHistoricalReplaysAsync(cancellationToken: stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Graceful exit
            }
            catch (Exception ex)
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                    _logger.LogWarning(ex,
                        "[ReplayAutoUpload] Historical replay sync encountered an error during startup.");
            }
        }, stoppingToken);

        // Run the persistent offline replay buffer drain worker
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(10000, stoppingToken);
                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        await DrainPendingOfflineReplaysAsync(stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        if (_logger.IsEnabled(LogLevel.Warning))
                            _logger.LogWarning(ex, "[ReplayAutoUpload] Error during offline replay buffer drain cycle");
                    }

                    await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Graceful exit
            }
        }, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
            try
            {
                ReplayUploadTask task = await _channel.Reader.ReadAsync(stoppingToken);
                await ProcessReplayUploadTaskAsync(task, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (_logger.IsEnabled(LogLevel.Error))
                    _logger.LogError(ex, "[ReplayAutoUpload] Unexpected error processing replay upload task.");
            }
    }

    public async Task<int> DrainPendingOfflineReplaysAsync(CancellationToken cancellationToken = default)
    {
        List<PendingReplayMetadata> pending = await _offlineBufferService.GetPendingReplaysAsync(cancellationToken);
        if (pending.Count == 0) return 0;

        int recoveredCount = 0;

        foreach (PendingReplayMetadata item in pending)
        {
            if (cancellationToken.IsCancellationRequested) break;

            // Exponential backoff check: 1m, 2m, 5m, 15m, 30m, 60m
            TimeSpan backoff = item.AttemptCount switch
            {
                <= 1 => TimeSpan.FromMinutes(1),
                2 => TimeSpan.FromMinutes(2),
                3 => TimeSpan.FromMinutes(5),
                4 => TimeSpan.FromMinutes(15),
                5 => TimeSpan.FromMinutes(30),
                _ => TimeSpan.FromHours(1)
            };

            if (TimeProvider.System.GetUtcNow() - item.LastAttemptUtc < backoff) continue;

            try
            {
                Stopwatch stopwatch;
                string downloadUrl;

                await using (Stream? stream = _offlineBufferService.OpenPendingReplayStream(item.FileName))
                {
                    if (stream == null)
                    {
                        await _offlineBufferService.MarkReplayCompletedAsync(item.FileName, cancellationToken);
                        continue;
                    }

                    stopwatch = Stopwatch.StartNew();
                    downloadUrl = await _storageProvider.UploadReplayAsync(item.MatchId.ToString(), stream,
                        item.FileName, cancellationToken);
                    stopwatch.Stop();
                }

                using IServiceScope scope = _scopeFactory.CreateScope();
                MerrickContext dbContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();

                MatchStatistics? matchStats =
                    await dbContext.MatchStatistics.FirstOrDefaultAsync(m => m.MatchID == item.MatchId,
                        cancellationToken);
                if (matchStats != null)
                {
                    matchStats.ReplayUrl = downloadUrl;
                    if (item.FileSizeBytes > 0) matchStats.FileSize = (int)item.FileSizeBytes;

                    await dbContext.SaveChangesAsync(cancellationToken);
                }

                await _offlineBufferService.MarkReplayCompletedAsync(item.FileName, cancellationToken);
                recoveredCount++;

                if (_logger.IsEnabled(LogLevel.Information))
                    _logger.LogInformation(
                        "[ReplayOfflineBuffer] Successfully recovered Match #{MatchId} ({Bytes} bytes) after {Attempts} attempts in {ElapsedMs}ms -> {Url}",
                        item.MatchId, item.FileSizeBytes, item.AttemptCount, stopwatch.ElapsedMilliseconds,
                        downloadUrl);
            }
            catch (Exception ex)
            {
                await _offlineBufferService.UpdatePendingAttemptAsync(item.FileName, ex.Message, cancellationToken);
            }
        }

        return recoveredCount;
    }

    private async Task ProcessReplayUploadTaskAsync(ReplayUploadTask task, CancellationToken cancellationToken)
    {
        await UploadSingleReplayAsync(task.MatchId, task.CustomFileName, cancellationToken);
    }
}
