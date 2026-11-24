// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using ASPIRE.Common.Hosting;

namespace COMPEL.Services.Maintenance;

/// <summary>
///     Periodically removes the working directories of old replays beneath the artefacts directory, faithfully reproducing
///     the legacy COMPEL's maintenance loop.
///     Unlike the original, the loop is bounded by the host's stopping token rather than running forever.
/// </summary>
public sealed class MaintenanceService(
    ArtefactsLocator artefacts,
    ILogger<MaintenanceService> logger,
    COMPEL.Services.Telemetry.PostMatchParquetIngestorService? parquetIngestor = null)
    : ResilientBackgroundService(logger, nameof(MaintenanceService))
{
    private static readonly TimeSpan FastSweepInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan DailySweepInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan ReplayRetention = TimeSpan.FromHours(24);
    private static readonly TimeSpan LogRetention = TimeSpan.FromDays(7);
    private static readonly TimeSpan CrashDumpRetention = TimeSpan.FromHours(2);

    private readonly ArtefactsLocator artefacts = artefacts;
    private readonly ILogger<MaintenanceService> logger = logger;
    private readonly COMPEL.Services.Telemetry.PostMatchParquetIngestorService? _parquetIngestor = parquetIngestor;
    private DateTimeOffset _lastDailySweep = DateTimeOffset.MinValue;

    protected override async Task ExecuteCoreAsync(CancellationToken stoppingToken)
    {
        // Run an initial daily maintenance pass on service startup
        await RunDailyMaintenancePassAsync(stoppingToken).ConfigureAwait(false);

        using PeriodicTimer timer = new(FastSweepInterval);
        try
        {
            while (stoppingToken.IsCancellationRequested is false &&
                   await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                // 1. Fast 15-minute sweep: crash dumps & temporary crash artifacts
                try
                {
                    CleanUpCrashDumps();
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Crash dump maintenance sweep encountered an error");
                }

                // 2. Daily sweep: old replays & historical logs older than 7 days
                if (TimeProvider.System.GetUtcNow() - _lastDailySweep >= DailySweepInterval)
                {
                    await RunDailyMaintenancePassAsync(stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown
        }
    }

    private async Task RunDailyMaintenancePassAsync(CancellationToken stoppingToken)
    {
        _lastDailySweep = TimeProvider.System.GetUtcNow();

        try
        {
            await ReconcileUnIngestedMatchesAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Lakehouse Parquet reconciliation maintenance sweep encountered an error");
        }

        try
        {
            CleanUpOldReplays();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Replay cleanup maintenance sweep encountered an error");
        }

        try
        {
            CleanUpOldLogs();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Log retention cleanup maintenance sweep encountered an error");
        }
    }

    private async Task ReconcileUnIngestedMatchesAsync(CancellationToken stoppingToken)
    {
        if (_parquetIngestor is null) return;
        string directory = artefacts.ArtefactsDirectory;
        if (!Directory.Exists(directory)) return;

        string[] replayFiles = Directory.GetFiles(directory, "*.honreplay", SearchOption.AllDirectories);
        string baseDir = AppContext.BaseDirectory;
        string parquetDir = Path.Combine(baseDir, "data", "analytics", "replays");

        foreach (string file in replayFiles)
        {
            if (stoppingToken.IsCancellationRequested) return;

            string fileName = Path.GetFileNameWithoutExtension(file);
            System.Text.RegularExpressions.Match match =
                System.Text.RegularExpressions.Regex.Match(fileName, @"(?:_M|M)(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success && long.TryParse(match.Groups[1].Value, out long matchId))
            {
                string expectedParquet = Path.Combine(parquetDir, $"match_{matchId}.parquet");
                if (!File.Exists(expectedParquet))
                {
                    try
                    {
                        await _parquetIngestor.IngestMatchAsync(matchId, cancellationToken: stoppingToken).ConfigureAwait(false);
                        logger.LogInformation("[MaintenanceService] Reconciled un-ingested match #{MatchId}", matchId);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "[MaintenanceService] Failed to reconcile match #{MatchId}", matchId);
                    }
                }
            }
        }
    }

    private void CleanUpCrashDumps()
    {
        string baseDir = AppContext.BaseDirectory;
        string artefactsDir = artefacts.ArtefactsDirectory;

        string[] searchDirs = [baseDir, artefactsDir];
        DateTimeOffset cutoff = TimeProvider.System.GetUtcNow() - CrashDumpRetention;

        foreach (string dir in searchDirs)
        {
            if (!Directory.Exists(dir)) continue;

            string[] dumpFiles = Directory.GetFiles(dir, "*.dmp", SearchOption.AllDirectories);
            foreach (string dump in dumpFiles)
            {
                try
                {
                    if (new DateTimeOffset(File.GetLastWriteTimeUtc(dump), TimeSpan.Zero) < cutoff)
                    {
                        File.Delete(dump);
                        logger.LogInformation("Purged expired crash dump: {Path}", dump);
                    }
                }
                catch
                {
                    // Ignore locked files
                }
            }
        }
    }

    private void CleanUpOldLogs()
    {
        string baseDir = AppContext.BaseDirectory;
        string artefactsDir = artefacts.ArtefactsDirectory;

        string[] searchDirs = [baseDir, artefactsDir];
        DateTimeOffset cutoff = TimeProvider.System.GetUtcNow() - LogRetention;

        foreach (string dir in searchDirs)
        {
            if (!Directory.Exists(dir)) continue;

            string[] logFiles = Directory.GetFiles(dir, "*.log", SearchOption.AllDirectories);
            foreach (string log in logFiles)
            {
                try
                {
                    if (new DateTimeOffset(File.GetLastWriteTimeUtc(log), TimeSpan.Zero) < cutoff)
                    {
                        File.Delete(log);
                        logger.LogInformation("Purged stale log file (>7 days): {Path}", log);
                    }
                }
                catch
                {
                    // Ignore active file handles
                }
            }
        }
    }

    private void CleanUpOldReplays()
    {
        string directory = artefacts.ArtefactsDirectory;

        if (Directory.Exists(directory) is false)
            return;

        string[] files = Directory.GetFiles(directory, "*.honreplay", SearchOption.AllDirectories);

        foreach (string file in files)
        {
            if (new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero).Add(ReplayRetention) >
                TimeProvider.System.GetUtcNow())
                continue;

            string? parent = Directory.GetParent(file)?.FullName;

            if (parent is null)
                continue;

            // The Replay's Working Directory Shares The File's Name With The "M" Characters Removed, As In The Original Maintenance Loop.
            string replayDirectory =
                Path.Combine(parent, Path.GetFileNameWithoutExtension(file).Replace("M", string.Empty));

            if (Directory.Exists(replayDirectory) is false)
                continue;

            try
            {
                Directory.Delete(replayDirectory, true);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Failed To Delete Replay Directory {Directory}", replayDirectory);
            }
        }
    }
}
