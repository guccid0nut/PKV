// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.RegularExpressions;
using ASPIRE.Common.Hosting;

namespace COMPEL.Services.Telemetry;

/// <summary>
///     Monitors and tails live dedicated game server slave log files (e.g. Slave2_M2060568749_console.clog)
///     in real time to capture live match events, player connections, team selections, hero picks,
///     and combat telemetry as they occur on the game servers.
/// </summary>
public sealed class GameServerLogWatcherService(
    ArtefactsLocator artefacts,
    CompelTelemetryRelayService telemetryRelay,
    ILogger<GameServerLogWatcherService> logger,
    IOptions<MatchServerManagerOptions>? options = null) : ResilientBackgroundService(logger, nameof(GameServerLogWatcherService))
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan DiscoveryInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FileInactivityTimeout = TimeSpan.FromMinutes(30);

    private readonly ArtefactsLocator _artefacts = artefacts;
    private readonly ILogger<GameServerLogWatcherService> _logger = logger;
    private readonly MatchServerManagerOptions _options = options?.Value ?? new MatchServerManagerOptions();

    private readonly DateTimeOffset _serviceStartTimeUtc = TimeProvider.System.GetUtcNow();
    private readonly CompelTelemetryRelayService _telemetryRelay = telemetryRelay;

    private readonly ConcurrentDictionary<string, LogFileTracker> _trackedFiles = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _lastDiscoveryUtc = DateTimeOffset.MinValue;

    protected override async Task ExecuteCoreAsync(CancellationToken stoppingToken)
    {
        if (!_options.EnableDiskLogWatcher)
        {
            _logger.LogInformation(
                "[GameServerLogWatcher] Background disk log watcher is disabled (EnableDiskLogWatcher=false). Dedicated server stdout is streamed directly in memory.");
            return;
        }

        _logger.LogInformation("[GameServerLogWatcher] Starting live game server log tailing service (scoped to ArtefactsDirectory)...");

        using PeriodicTimer timer = new(PollInterval);
        try
        {
            while (!stoppingToken.IsCancellationRequested &&
                   await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                try
                {
                    if (TimeProvider.System.GetUtcNow() - _lastDiscoveryUtc >= DiscoveryInterval)
                    {
                        _lastDiscoveryUtc = TimeProvider.System.GetUtcNow();
                        DiscoverNewLogFiles();
                        PurgeInactiveTrackers();
                    }

                    // High-frequency read of all currently tracked open streams
                    foreach (LogFileTracker tracker in _trackedFiles.Values) ReadNewLines(tracker);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[GameServerLogWatcher] Error polling game server log files");
                }
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogDebug(ex, "[GameServerLogWatcher] ExecuteAsync canceled.");
        }

        CleanupTrackedFiles();
        _logger.LogInformation("[GameServerLogWatcher] Game server log tailing service stopped.");
    }

    private void DiscoverNewLogFiles()
    {
        List<string> candidateDirectories = GetCandidateLogDirectories();

        List<FileInfo> files = [];
        foreach (string directoryPath in candidateDirectories)
        {
            if (!Directory.Exists(directoryPath)) continue;

            DirectoryInfo dirInfo = new(directoryPath);
            files.AddRange(dirInfo.GetFiles("Slave*.clog", SearchOption.TopDirectoryOnly));
            files.AddRange(dirInfo.GetFiles("Slave*.log", SearchOption.TopDirectoryOnly));
            files.AddRange(dirInfo.GetFiles("M*.log", SearchOption.TopDirectoryOnly));
        }

        IEnumerable<FileInfo> distinctFiles = files
            .GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First());

        foreach (FileInfo file in distinctFiles)
        {
            // Only track files active in the last 2 hours
            if (TimeProvider.System.GetUtcNow() - new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero) >
                TimeSpan.FromHours(2)) continue;

            if (!_trackedFiles.TryGetValue(file.FullName, out LogFileTracker? tracker))
                try
                {
                    FileStream fileStream = new(
                        file.FullName,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);

                    // K2 engine writes .clog and M*.log in UTF-16LE (Unicode) on Windows
                    bool isUnicode = file.Extension.Equals(".clog", StringComparison.OrdinalIgnoreCase) ||
                                     (file.Name.StartsWith("M", StringComparison.OrdinalIgnoreCase) &&
                                      !file.Name.Contains("manager", StringComparison.OrdinalIgnoreCase));

                    Encoding encoding = isUnicode
                        ? Encoding.Unicode
                        : Encoding.UTF8;

                    StreamReader streamReader = new(fileStream, encoding, true);

                    // If the file was last modified before this service started, seek to end to avoid replaying stale matches
                    if (new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero) < _serviceStartTimeUtc)
                    {
                        fileStream.Seek(0, SeekOrigin.End);
                        streamReader.DiscardBufferedData();
                    }

                    int matchId = ExtractMatchIdFromFileName(file.Name);

                    string localLogsDir = Path.Combine(AppContext.BaseDirectory, "logs");
                    if (file.DirectoryName != null && file.DirectoryName.Contains("manager", StringComparison.OrdinalIgnoreCase))
                    {
                        localLogsDir = Path.Combine(localLogsDir, "manager");
                    }
                    Directory.CreateDirectory(localLogsDir);

                    string localMirrorPath = Path.Combine(localLogsDir, file.Name);
                    FileStream mirrorStream = new(
                        localMirrorPath,
                        FileMode.Append,
                        FileAccess.Write,
                        FileShare.ReadWrite);
                    StreamWriter mirrorWriter = new(mirrorStream, Encoding.UTF8) { AutoFlush = true };

                    tracker = new LogFileTracker(file.FullName, fileStream, streamReader, mirrorStream, mirrorWriter, matchId);
                    _trackedFiles[file.FullName] = tracker;

                    _logger.LogInformation(
                        "[GameServerLogWatcher] Attached to active game server log: {FileName} (Mirroring to {MirrorPath}, Match #{MatchId}, Encoding: {Encoding})",
                        file.Name, localMirrorPath, matchId, encoding.EncodingName);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[GameServerLogWatcher] Could not open log file {FileName}", file.Name);
                }
        }
    }

    private static int ExtractMatchIdFromFileName(string fileName)
    {
        Match match = Regex.Match(fileName, @"(?:_M|M)(\d+)", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out int id) ? id : 0;
    }

    private void ReadNewLines(LogFileTracker tracker)
    {
        try
        {
            while (tracker.Reader.ReadLine() is string line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                tracker.LastActivityUtc = TimeProvider.System.GetUtcNow();
                try
                {
                    tracker.MirrorWriter?.WriteLine(line);
                }
                catch (Exception ex)
                {
                    _logger.LogTrace(ex, "[GameServerLogWatcher] Local mirror write failed for {FilePath}.", tracker.FilePath);
                }

                if (tracker.MatchId > 0)
                {
                    _logger.LogInformation("[GameServer #{MatchId}] {Line}", tracker.MatchId, line);
                }
                else
                {
                    _logger.LogInformation("[GameServerLog] {Line}", line);
                }
                _telemetryRelay.OnServerStdoutLine(tracker.MatchId, line);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[GameServerLogWatcher] Error reading from {FilePath}", tracker.FilePath);
        }
    }

    private void PurgeInactiveTrackers()
    {
        DateTimeOffset cutoff = TimeProvider.System.GetUtcNow() - FileInactivityTimeout;
        List<string> toRemove = [];

        foreach (KeyValuePair<string, LogFileTracker> entry in _trackedFiles)
            if (entry.Value.LastActivityUtc < cutoff || !File.Exists(entry.Key))
                toRemove.Add(entry.Key);

        foreach (string key in toRemove)
            if (_trackedFiles.TryRemove(key, out LogFileTracker? removed))
            {
                removed.Dispose();
                _logger.LogInformation("[GameServerLogWatcher] Detached from inactive log file: {FilePath}", key);
            }
    }

    private List<string> GetCandidateLogDirectories()
    {
        List<string> dirs = [];

        if (!string.IsNullOrWhiteSpace(_artefacts.ArtefactsDirectory))
        {
            dirs.Add(Path.Combine(_artefacts.ArtefactsDirectory, "game", "logs"));
            dirs.Add(Path.Combine(_artefacts.ArtefactsDirectory, "game", "logs", "manager"));
        }

        string currentDir = Directory.GetCurrentDirectory();
        dirs.Add(Path.Combine(currentDir, "games", "server", "logs"));
        dirs.Add(Path.Combine(currentDir, "userdata", "game", "logs"));

        return [.. dirs.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private void CleanupTrackedFiles()
    {
        foreach (KeyValuePair<string, LogFileTracker> entry in _trackedFiles) entry.Value.Dispose();
        _trackedFiles.Clear();
    }

    private sealed class LogFileTracker(
        string filePath,
        FileStream stream,
        StreamReader reader,
        FileStream? mirrorStream = null,
        StreamWriter? mirrorWriter = null,
        int matchId = 0)
        : IDisposable
    {
        public string FilePath { get; } = filePath;
        public FileStream Stream { get; } = stream;
        public StreamReader Reader { get; } = reader;
        public FileStream? MirrorStream { get; } = mirrorStream;
        public StreamWriter? MirrorWriter { get; } = mirrorWriter;
        public int MatchId { get; } = matchId;
        public DateTimeOffset LastActivityUtc { get; set; } = TimeProvider.System.GetUtcNow();

        public void Dispose()
        {
            try
            {
                Reader.Dispose();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LogFileTracker] Reader dispose error: {ex.Message}");
            }

            try
            {
                Stream.Dispose();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LogFileTracker] Stream dispose error: {ex.Message}");
            }

            try
            {
                MirrorWriter?.Dispose();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LogFileTracker] MirrorWriter dispose error: {ex.Message}");
            }

            try
            {
                MirrorStream?.Dispose();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LogFileTracker] MirrorStream dispose error: {ex.Message}");
            }
        }
    }
}
