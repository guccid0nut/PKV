// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text;
using System.Text.RegularExpressions;
using ASPIRE.Common.Hosting;
using DAWNBRINGER.WebPortal.UI.Services.LogParser;

namespace DAWNBRINGER.WebPortal.UI.Services;

public partial class HonLogWatcherService : ResilientBackgroundService
{
    private readonly Dictionary<string, long> _filePositions = [];
    private readonly ILiveMatchService _liveMatchService;
    private readonly ILogger<HonLogWatcherService> _logger;
    private readonly List<string> _searchDirectories = [];
    private readonly Dictionary<string, HonLogParsedState> _trackedStates = [];

    public HonLogWatcherService(ILiveMatchService liveMatchService, ILogger<HonLogWatcherService> logger)
        : base(logger, nameof(HonLogWatcherService))
    {
        _liveMatchService = liveMatchService;
        _logger = logger;

        string currentDir = Directory.GetCurrentDirectory();
        string parentDir = Directory.GetParent(currentDir)?.FullName ?? currentDir;
        string grandParentDir = Directory.GetParent(parentDir)?.FullName ?? parentDir;

        string[] candidateRoots = [currentDir, parentDir, grandParentDir];
        foreach (string root in candidateRoots)
        {
            AddSearchDirectory(Path.Combine(root, "games", "client", "userdata", "game", "logs"));
            AddSearchDirectory(Path.Combine(root, "games", "client", "userdata", "game", "logs", "diagnostics"));
            AddSearchDirectory(Path.Combine(root, "games", "client", "userdata", "game"));
            AddSearchDirectory(Path.Combine(root, "games", "client", "userdata"));
            AddSearchDirectory(Path.Combine(root, "userdata", "game", "logs"));
            AddSearchDirectory(Path.Combine(root, "userdata", "game"));
            AddSearchDirectory(Path.Combine(root, "games", "client", "logs"));
            AddSearchDirectory(Path.Combine(root, "games", "client", "game", "logs"));
            AddSearchDirectory(Path.Combine(root, "games", "client", "game"));
            AddSearchDirectory(Path.Combine(root, "games", "server", "logs"));
            AddSearchDirectory(Path.Combine(root, "games", "shared", "logs"));
        }

        string docsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Heroes of Newerth x64");
        string oneDriveDocsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "OneDrive", "Documents", "Heroes of Newerth x64");

        AddSearchDirectory(Path.Combine(docsDir, "game", "logs"));
        AddSearchDirectory(Path.Combine(docsDir, "game", "logs", "diagnostics"));
        AddSearchDirectory(Path.Combine(docsDir, "game"));
        AddSearchDirectory(Path.Combine(oneDriveDocsDir, "game", "logs"));
        AddSearchDirectory(Path.Combine(oneDriveDocsDir, "game", "logs", "diagnostics"));
        AddSearchDirectory(Path.Combine(oneDriveDocsDir, "game"));
    }

    [GeneratedRegex(@"(?:Slave\d+_M|M)(\d{6,12})", RegexOptions.IgnoreCase)]
    private static partial Regex MatchIdRegex();

    private void AddSearchDirectory(string dir)
    {
        if (Directory.Exists(dir) && !_searchDirectories.Contains(dir)) _searchDirectories.Add(dir);
    }

    protected override async Task ExecuteCoreAsync(CancellationToken stoppingToken)
    {
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("[HonLogWatcher] Real-Time HoN MLog & CLog Watcher started.");

        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(100));
        try
        {
            while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
                try
                {
                    List<string> candidateFiles = FindCandidateLogFiles();
                    foreach (string file in candidateFiles) await TailLogFileAsync(file, stoppingToken);
                }
                catch (Exception ex)
                {
                    if (_logger.IsEnabled(LogLevel.Warning))
                        _logger.LogWarning(ex, "[HonLogWatcher] Error occurred while polling game logs.");
                }
        }
        catch (OperationCanceledException)
        {
            // Graceful exit
        }
    }

    private List<string> FindCandidateLogFiles()
    {
        List<string> files = [];
        DateTime cutoff = TimeProvider.System.GetUtcNow().UtcDateTime.AddMinutes(-60);

        foreach (string dir in _searchDirectories)
            try
            {
                if (!Directory.Exists(dir)) continue;

                // Priority 1: Console Logs (*.clog)
                files.AddRange(Directory.GetFiles(dir, "*.clog", SearchOption.AllDirectories)
                    .Where(f => new FileInfo(f).LastWriteTimeUtc >= cutoff));

                // Priority 2: Log files (*.log)
                files.AddRange(Directory.GetFiles(dir, "*.log", SearchOption.AllDirectories)
                    .Where(f => new FileInfo(f).LastWriteTimeUtc >= cutoff));
            }
            catch (Exception ex)
            {
                if (_logger.IsEnabled(LogLevel.Trace))
                    _logger.LogTrace(ex, "[HonLogWatcher] Error scanning directory {Dir}", dir);
            }

        return [.. files.Distinct().OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc).Take(10)];
    }

    private async Task TailLogFileAsync(string filePath, CancellationToken stoppingToken)
    {
        FileInfo fi = new(filePath);
        if (!fi.Exists || fi.Length == 0) return;

        if (!_filePositions.TryGetValue(filePath, out long lastPosition))
        {
            lastPosition = 0;
            _filePositions[filePath] = 0;
            HonLogParsedState parsedState = new();

            // Extract Match ID from file name if available (e.g. Slave2_M2060568754_console.clog or M2060568754.clog)
            string baseName = Path.GetFileName(filePath);
            Match fnMatch = MatchIdRegex().Match(baseName);
            if (fnMatch.Success && int.TryParse(fnMatch.Groups[1].Value, out int fnMatchId))
                parsedState.MatchID = fnMatchId;

            _trackedStates[filePath] = parsedState;
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("[HonLogWatcher] Started tailing active match log (Match #{MatchId}): {Path}",
                    parsedState.MatchID, filePath);
        }

        if (fi.Length < lastPosition)
        {
            lastPosition = 0;
            _filePositions[filePath] = 0;
        }

        if (fi.Length == lastPosition) return;

        HonLogParsedState state = _trackedStates[filePath];
        if (state.MatchID <= 0)
        {
            string baseName = Path.GetFileName(filePath);
            Match fnMatch = MatchIdRegex().Match(baseName);
            if (fnMatch.Success && int.TryParse(fnMatch.Groups[1].Value, out int fnMatchId)) state.MatchID = fnMatchId;
        }

        bool stateChanged = false;

        await using FileStream fs = new(filePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        fs.Seek(lastPosition, SeekOrigin.Begin);

        long bytesToRead = fs.Length - lastPosition;
        if (bytesToRead <= 0) return;

        byte[] buffer = new byte[bytesToRead];
        int readBytes = await fs.ReadAsync(buffer.AsMemory(0, (int)bytesToRead), stoppingToken);
        if (readBytes <= 0) return;

        string fileName = Path.GetFileName(filePath);
        bool isUnicodeLog = filePath.EndsWith(".clog", StringComparison.OrdinalIgnoreCase) ||
                            (fileName.StartsWith("M", StringComparison.OrdinalIgnoreCase) &&
                             !fileName.Contains("manager", StringComparison.OrdinalIgnoreCase));

        Encoding encoding = isUnicodeLog ? Encoding.Unicode : Encoding.UTF8;

        int lastNewlineIndex = -1;
        for (int i = readBytes - 1; i >= 0; i--)
            if (buffer[i] == (byte)'\n')
            {
                lastNewlineIndex = i;
                break;
            }

        int bytesConsumed = readBytes;
        if (lastNewlineIndex >= 0) bytesConsumed = lastNewlineIndex + 1;

        string chunkText = encoding.GetString(buffer, 0, bytesConsumed);
        string[] lines = chunkText.Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries);

        foreach (string line in lines)
            if (!string.IsNullOrWhiteSpace(line))
            {
                bool parsed = HonLogParser.ParseLine(line, state);
                if (parsed) stateChanged = true;
            }

        _filePositions[filePath] = lastPosition + bytesConsumed;

        if (stateChanged && state.MatchID > 0) await PushStateToLiveMatchServiceAsync(state, stoppingToken);
    }

    private async Task PushStateToLiveMatchServiceAsync(HonLogParsedState state,
        CancellationToken stoppingToken = default)
    {
        LiveServerTickPayloadDto payload = new()
        {
            MatchID = state.MatchID,
            ElapsedSeconds = state.ElapsedSeconds,
            ScoreLegion = state.ScoreLegion,
            ActiveWards = [.. state.ActiveWards.Select(w => (LiveWardTelemetryDto)w)],
            RecentEvents = [.. state.RecentEvents.Select(e => (LiveCombatEventTelemetryDto)e)],
            Structures = new Dictionary<string, bool>(state.Structures),
            Players =
            [
                .. state.PlayersBySlot.Select((kvp, index) => (LivePlayerTelemetryDto)new LiveServerPlayerTickDto
                {
                    Slot = kvp.Key,
                    AccountID = kvp.Value.AccountID,
                    AccountName = kvp.Value.AccountName,
                    Team = kvp.Value.Team,
                    HeroIdentifier = kvp.Value.HeroIdentifier,
                    Level = kvp.Value.Level,
                    PositionX = kvp.Value.PositionX,
                    PositionY = kvp.Value.PositionY,
                    HeadingAngle = kvp.Value.HeadingAngle,
                    HealthPercent = kvp.Value.HealthPercent,
                    ManaPercent = kvp.Value.ManaPercent,
                    IsAlive = kvp.Value.IsAlive,
                    CurrentGold = kvp.Value.CurrentGold,
                    Inventory = [.. kvp.Value.Inventory]
                })
            ]
        };

        await _liveMatchService.ApplyServerTickAsync(payload, stoppingToken);

        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation(
                "[HonLogWatcher] Synced live MLog state for Match #{MatchId}: {PlayerCount} players, {WardCount} wards, Elapsed: {Elapsed}s",
                state.MatchID, payload.Players.Count, payload.ActiveWards.Count, payload.ElapsedSeconds);
    }
}
