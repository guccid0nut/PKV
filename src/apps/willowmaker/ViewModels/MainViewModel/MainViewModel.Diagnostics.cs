// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: MainViewModel.Diagnostics.cs
// System: WILLOWMAKER - Diagnostics & Crash Recovery Center
// Subsystem: 1-Click Discord Diagnostic Export, Cold-Boot & Post-Crash Supervision
// ============================================================================

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kongor.Core.Vfs;
using Kongor.Core.Diagnostics;
using WILLOWMAKER.Core.Constants;
using WILLOWMAKER.Core.Services;
using WILLOWMAKER.Core.Services.Diagnostics;

namespace WILLOWMAKER.Core.ViewModels;

public partial class MainViewModel
{
    [ObservableProperty] private bool _hasCrashReportBanner;
    [ObservableProperty] private string _crashReportBannerText = string.Empty;
    [ObservableProperty] private string _crashReportTargetText = string.Empty;
    [ObservableProperty] private string _lastCrashFaultSummary = string.Empty;
    [ObservableProperty] private bool _isExportingDiagnostics;
    [ObservableProperty] private string _diagnosticExportStatusText = string.Empty;

    private MinidumpTriageResult? _activeTriageResult;
    private string? _activeCrashDumpPath;
    private string? _lastAutoExportedCrashZipPath;
    private string? _lastAutoExportedMarkdownSnippet;
    private uint? _lastProcessExitCode;
    private string? _lastSilentExitZeroReason;
    private DateTimeOffset? _lastLaunchStartUtc;
    private DateTimeOffset? _coldBootSentinelUtc;
    private int? _coldBootOrphanPid;
    private int? _lastGameProcessId;
    private readonly System.Collections.Generic.Queue<string> _recentClientStderrLines = new();
    private readonly object _stderrBufferLock = new();
    private const int MaxCapturedStderrLines = 120;

    private static async Task RunOnUIThreadAsync(Func<Task> action)
    {
        if (Application.Current == null || Dispatcher.UIThread.CheckAccess())
        {
            await action();
        }
        else
        {
            await Dispatcher.UIThread.InvokeAsync(action);
        }
    }

    private static async Task RunOnUIThreadAsync(Action action)
    {
        if (Application.Current == null || Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            await Dispatcher.UIThread.InvokeAsync(action);
        }
    }

    public void ClearClientStderrBuffer()
    {
        lock (_stderrBufferLock)
        {
            _recentClientStderrLines.Clear();
        }
    }

    public void RecordClientStderrLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        lock (_stderrBufferLock)
        {
            while (_recentClientStderrLines.Count >= MaxCapturedStderrLines)
            {
                _recentClientStderrLines.Dequeue();
            }

            _recentClientStderrLines.Enqueue(line.TrimEnd('\r', '\n'));
        }
    }

    public string? GetCapturedClientStderr()
    {
        lock (_stderrBufferLock)
        {
            if (_recentClientStderrLines.Count == 0)
            {
                return null;
            }

            return string.Join(Environment.NewLine, _recentClientStderrLines);
        }
    }

    internal static string ResolveCanonicalDiagnosticsSettingsDir(string clientRoot)
    {
        if (string.IsNullOrWhiteSpace(clientRoot))
        {
            return clientRoot;
        }

        string userdataKongorDir = Path.Combine(clientRoot, "userdataKONGOR");
        if (Directory.Exists(userdataKongorDir) ||
            File.Exists(Path.Combine(clientRoot, "hon_x64.exe")) ||
            File.Exists(Path.Combine(clientRoot, "hon-x86_64")))
        {
            return userdataKongorDir;
        }

        return clientRoot;
    }

    internal static string ResolvePlayerSessionsLogPath(string clientRoot)
    {
        string logsDir = Path.Combine(clientRoot, "logs");
        DirectoryInfo? probeDir = new(clientRoot);
        for (int depth = 0; depth < 4 && probeDir is not null; depth++)
        {
            if (File.Exists(Path.Combine(probeDir.FullName, "ProjectKongor.slnx")))
            {
                logsDir = Path.Combine(probeDir.FullName, "logs");
                break;
            }
            probeDir = probeDir.Parent;
        }

        return Path.Combine(logsDir, "player_sessions.jsonl");
    }

    internal static bool HasConfirmedCleanExitInPlayerSessions(
        string clientRoot,
        int pid,
        DateTimeOffset? sentinelWriteTimeUtc = null)
    {
        if (pid <= 0 || string.IsNullOrWhiteSpace(clientRoot))
        {
            return false;
        }

        try
        {
            string sessionsPath = ResolvePlayerSessionsLogPath(clientRoot);
            if (!File.Exists(sessionsPath))
            {
                return false;
            }

            string tail = ReadSharedFileTailUtf8(sessionsPath, maxBytes: 131072);
            string pidDigits = pid.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string[] lines = tail.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string line = lines[i].Trim();
                if (!line.StartsWith('{') || !line.Contains(pidDigits, StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    using JsonDocument doc = JsonDocument.Parse(line);
                    JsonElement root = doc.RootElement;
                    if (!root.TryGetProperty("processId", out JsonElement pidElem) ||
                        !pidElem.TryGetInt32(out int recordPid) ||
                        recordPid != pid)
                    {
                        continue;
                    }

                    if (sentinelWriteTimeUtc.HasValue &&
                        root.TryGetProperty("timestampUtc", out JsonElement tsElem) &&
                        tsElem.ValueKind == JsonValueKind.String &&
                        DateTimeOffset.TryParse(tsElem.GetString(), out DateTimeOffset recordUtc) &&
                        recordUtc < sentinelWriteTimeUtc.Value.AddMinutes(-2))
                    {
                        // Older record from a recycled Windows PID — do not suppress a newer sentinel.
                        return false;
                    }

                    bool exitZero = root.TryGetProperty("exitCode", out JsonElement exitElem) &&
                                    exitElem.TryGetInt32(out int exitCode) &&
                                    exitCode == 0;
                    bool notCrashed = root.TryGetProperty("crashed", out JsonElement crashElem) &&
                                      crashElem.ValueKind == JsonValueKind.False;
                    return exitZero && notCrashed;
                }
                catch (JsonException)
                {
                    // Skip truncated head line when tail-reading a large JSONL file.
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[CRASH_SUPERVISION] player_sessions.jsonl lookup notice for PID {pid}: {ex.Message}");
        }

        return false;
    }

    /// <summary>
    /// Checks for an unacknowledged crash sentinel or K2 CCrashReporter dump on cold boot.
    /// If a previous crash was saved locally, surfaces the recovery banner.
    /// </summary>
    public void CheckColdBootCrashSentinel(string clientRoot)
    {
        try
        {
            string settingsDir = ResolveCanonicalDiagnosticsSettingsDir(clientRoot);
            var initialSettings = LauncherSettingsService.Load(settingsDir);
            DateTimeOffset? ackThreshold = DateTimeOffset.TryParse(initialSettings.LastAcknowledgedCrashUtc, out DateTimeOffset parsedAck)
                ? parsedAck
                : null;
            if (!string.Equals(settingsDir, clientRoot, StringComparison.OrdinalIgnoreCase))
            {
                string legacySettingsPath = Path.Combine(clientRoot, "launcher_settings.json");
                if (File.Exists(legacySettingsPath))
                {
                    var legacySettings = LauncherSettingsService.Load(clientRoot);
                    if (DateTimeOffset.TryParse(legacySettings.LastAcknowledgedCrashUtc, out DateTimeOffset parsedLegacyAck) &&
                        (!ackThreshold.HasValue || parsedLegacyAck > ackThreshold.Value))
                    {
                        ackThreshold = parsedLegacyAck;
                        initialSettings.LastAcknowledgedCrashUtc = parsedLegacyAck.ToString("O");
                        LauncherSettingsService.Save(initialSettings, settingsDir);
                        Log(LogCategory.Executable,
                            $"[CRASH_SUPERVISION] Migrated newer LastAcknowledgedCrashUtc ({parsedLegacyAck:O}) from legacy clientRoot launcher_settings.json into '{settingsDir}'.");
                    }
                }
            }
            DiagnosticBundleService.EnforceLocalCrashRetention(clientRoot, ackThreshold);

            string crashDir = Path.Combine(clientRoot, "logs", "crash");
            string sentinelPath = Path.Combine(crashDir, "last-crashed.sentinel");
            if (!File.Exists(sentinelPath))
            {
                sentinelPath = Path.Combine(clientRoot, "userdata", "logs", "last-crashed.sentinel");
            }

            bool hasUnackedSentinel = false;
            string? coldBootSentinelSummary = null;
            DateTimeOffset? coldBootSentinelUtc = null;
            int? coldBootOrphanPid = null;
            if (File.Exists(sentinelPath))
            {
                DateTimeOffset sentinelTimeUtc = new(File.GetLastWriteTimeUtc(sentinelPath), TimeSpan.Zero);
                if (!ackThreshold.HasValue || sentinelTimeUtc > ackThreshold.Value)
                {
                    hasUnackedSentinel = true;
                    coldBootSentinelUtc = sentinelTimeUtc;
                    coldBootOrphanPid = TryParsePidFromSentinelFile(sentinelPath);
                    coldBootSentinelSummary = coldBootOrphanPid.HasValue
                        ? $"Unexpected Client Exit (PID {coldBootOrphanPid.Value})"
                        : "Unexpected Client Exit";
                }
            }

            if (Directory.Exists(crashDir))
            {
                foreach (string pidCrashFile in Directory.EnumerateFiles(crashDir, "last-crashed.*.sentinel"))
                {
                    DateTimeOffset pidCrashTimeUtc = new(File.GetLastWriteTimeUtc(pidCrashFile), TimeSpan.Zero);
                    if ((!ackThreshold.HasValue || pidCrashTimeUtc > ackThreshold.Value) &&
                        (!coldBootSentinelUtc.HasValue || pidCrashTimeUtc > coldBootSentinelUtc.Value))
                    {
                        hasUnackedSentinel = true;
                        coldBootSentinelUtc = pidCrashTimeUtc;
                        string fileName = Path.GetFileName(pidCrashFile);
                        ReadOnlySpan<char> middle = fileName.AsSpan("last-crashed.".Length);
                        int dotIdx = middle.IndexOf('.');
                        coldBootOrphanPid = dotIdx > 0 && int.TryParse(middle[..dotIdx], out int parsedPid) && parsedPid > 0
                            ? parsedPid
                            : TryParsePidFromSentinelFile(pidCrashFile);
                        coldBootSentinelSummary = coldBootOrphanPid.HasValue
                            ? $"Unexpected Client Exit (PID {coldBootOrphanPid.Value})"
                            : "Unexpected Client Exit";
                    }
                }

                foreach (string activeSentFile in Directory.EnumerateFiles(crashDir, "session-active.*.sentinel"))
                {
                    string fileName = Path.GetFileName(activeSentFile);
                    ReadOnlySpan<char> middle = fileName.AsSpan("session-active.".Length);
                    int dotIdx = middle.IndexOf('.');
                    if (dotIdx <= 0 || !int.TryParse(middle[..dotIdx], out int orphanPid) || orphanPid <= 0)
                    {
                        continue;
                    }

                    string matchingClean = Path.Combine(crashDir, $"clean-exit.{orphanPid}.sentinel");
                    if (File.Exists(matchingClean) || IsProcessCurrentlyRunning(orphanPid))
                    {
                        continue;
                    }

                    DateTimeOffset activeTimeUtc = new(File.GetLastWriteTimeUtc(activeSentFile), TimeSpan.Zero);
                    if (HasConfirmedCleanExitInPlayerSessions(clientRoot, orphanPid, activeTimeUtc))
                    {
                        try { File.Delete(activeSentFile); }
                        catch (Exception pruneEx) { Log(LogCategory.Executable, $"[CRASH_SUPERVISION] Stale sentinel prune notice for '{fileName}': {pruneEx.Message}"); }
                        Log(LogCategory.Executable,
                            $"[CRASH_SUPERVISION] Pruned orphaned '{fileName}' (PID {orphanPid} already confirmed clean ExitCode 0 in player_sessions.jsonl).");
                        continue;
                    }

                    if ((!ackThreshold.HasValue || activeTimeUtc > ackThreshold.Value) &&
                        (!coldBootSentinelUtc.HasValue || activeTimeUtc > coldBootSentinelUtc.Value))
                    {
                        hasUnackedSentinel = true;
                        coldBootSentinelUtc = activeTimeUtc;
                        coldBootOrphanPid = orphanPid;
                        coldBootSentinelSummary = $"Unexpected Client Exit (PID {orphanPid})";
                    }
                }

                foreach (string quitSentFile in Directory.EnumerateFiles(crashDir, "quit-initiated.*.sentinel"))
                {
                    string fileName = Path.GetFileName(quitSentFile);
                    ReadOnlySpan<char> middle = fileName.AsSpan("quit-initiated.".Length);
                    int dotIdx = middle.IndexOf('.');
                    if (dotIdx <= 0 || !int.TryParse(middle[..dotIdx], out int orphanPid) || orphanPid <= 0)
                    {
                        continue;
                    }

                    string matchingClean = Path.Combine(crashDir, $"clean-exit.{orphanPid}.sentinel");
                    if (File.Exists(matchingClean) || IsProcessCurrentlyRunning(orphanPid))
                    {
                        continue;
                    }

                    DateTimeOffset quitTimeUtc = new(File.GetLastWriteTimeUtc(quitSentFile), TimeSpan.Zero);
                    if (HasConfirmedCleanExitInPlayerSessions(clientRoot, orphanPid, quitTimeUtc))
                    {
                        try { File.Delete(quitSentFile); }
                        catch (Exception pruneEx) { Log(LogCategory.Executable, $"[CRASH_SUPERVISION] Stale sentinel prune notice for '{fileName}': {pruneEx.Message}"); }
                        Log(LogCategory.Executable,
                            $"[CRASH_SUPERVISION] Pruned orphaned '{fileName}' (PID {orphanPid} already confirmed clean ExitCode 0 in player_sessions.jsonl).");
                        continue;
                    }

                    if ((!ackThreshold.HasValue || quitTimeUtc > ackThreshold.Value) &&
                        (!coldBootSentinelUtc.HasValue || quitTimeUtc > coldBootSentinelUtc.Value))
                    {
                        hasUnackedSentinel = true;
                        coldBootSentinelUtc = quitTimeUtc;
                        coldBootOrphanPid = orphanPid;
                        coldBootSentinelSummary = $"Incomplete Client Shutdown (PID {orphanPid})";
                    }
                }
            }

            DateTimeOffset? dumpThreshold = ackThreshold;
            if (coldBootSentinelUtc.HasValue)
            {
                DateTimeOffset sentinelWindow = coldBootSentinelUtc.Value.AddSeconds(-5);
                dumpThreshold = dumpThreshold.HasValue && dumpThreshold.Value > sentinelWindow
                    ? dumpThreshold
                    : sentinelWindow;
            }

            string? latestDmp = DiagnosticBundleService.LocateLatestCrashDumpPath(clientRoot, minWriteTimeUtc: dumpThreshold);
            bool hasUnackedDump = false;
            DateTimeOffset? latestDumpTimeUtc = null;
            if (!string.IsNullOrEmpty(latestDmp) && File.Exists(latestDmp))
            {
                DateTimeOffset dmpTimeUtc = new(File.GetLastWriteTimeUtc(latestDmp), TimeSpan.Zero);
                hasUnackedDump = !dumpThreshold.HasValue || dmpTimeUtc > dumpThreshold.Value;
                if (hasUnackedDump)
                {
                    latestDumpTimeUtc = dmpTimeUtc;
                }
                else
                {
                    Log(LogCategory.Executable,
                        $"[CRASH_SUPERVISION] Ignored stale dump '{latestDmp}' (writtenUtc={dmpTimeUtc:O}, threshold={dumpThreshold?.ToString("O") ?? "none"}).");
                    latestDmp = null;
                }
            }

            if (hasUnackedSentinel || hasUnackedDump)
            {
                _coldBootSentinelUtc = coldBootSentinelUtc ?? latestDumpTimeUtc;
                _coldBootOrphanPid = hasUnackedSentinel ? coldBootOrphanPid : null;
                _activeCrashDumpPath = latestDmp;
                _activeTriageResult = !string.IsNullOrEmpty(latestDmp)
                    ? MinidumpReader.Inspect(latestDmp)
                    : null;

                string target = _activeTriageResult?.IsValidDump == true
                    ? _activeTriageResult.TargetFormatted
                    : coldBootSentinelSummary ?? "previous session";

                Action applyBanner = () =>
                {
                    CrashReportTargetText = target;
                    CrashReportBannerText = $"Crash report from previous session saved locally ({target}). Nothing was uploaded.";
                    LastCrashFaultSummary = target;
                    HasCrashReportBanner = true;
                };

                if (Dispatcher.UIThread.CheckAccess())
                {
                    applyBanner();
                }
                else
                {
                    Dispatcher.UIThread.Post(applyBanner);
                }

                Log(LogCategory.Executable,
                    $"[CRASH_SUPERVISION] Cold boot detected previous unacknowledged crash ({target}, dump='{latestDmp ?? "none"}', sentinelUtc={_coldBootSentinelUtc?.ToString("O") ?? "null"}, orphanPid={_coldBootOrphanPid?.ToString() ?? "null"}).");
            }
            else
            {
                _coldBootSentinelUtc = null;
                _coldBootOrphanPid = null;
            }
        }
        catch (Exception ex)
        {
            Log(LogCategory.Executable, $"[CRASH_SUPERVISION] Cold boot sentinel check warning: {ex.Message}");
        }
    }

    public string? LastAutoExportedCrashZipPath => _lastAutoExportedCrashZipPath;
    public string? LastActiveCrashDumpPath => _activeCrashDumpPath;
    public TimeSpan? CrashDumpSettlePollIntervalOverride { get; set; }

    /// <summary>
    /// Evaluates the game process exit. If a crash or abnormal exit occurred,
    /// catches it, keeps WILLOWMAKER open, automatically generates the scrubbed
    /// diagnostic .zip bundle, and surfaces the recovery banner.
    /// </summary>
    public async Task HandleGameProcessTerminationAsync(
        uint exitCode,
        DateTimeOffset launchStartUtc,
        string clientRoot,
        string? outputDirectoryOverride = null,
        int? gameProcessId = null)
    {
        _lastProcessExitCode = exitCode;
        _lastSilentExitZeroReason = null;
        _lastLaunchStartUtc = launchStartUtc;
        _lastGameProcessId = gameProcessId;
        _coldBootSentinelUtc = null;
        _coldBootOrphanPid = null;
        _lastAutoExportedCrashZipPath = null;
        _lastAutoExportedMarkdownSnippet = null;
        string pidTag = gameProcessId.HasValue ? $"PID {gameProcessId.Value}" : "PID unknown";
        Log(LogCategory.Executable, $"Evaluating game process termination ({pidTag}, ExitCode: 0x{exitCode:X8})...");

        // Allow up to 2.5s settle window for NativeGuard, K2 CCrashReporter (userdata/<mod>/*.dmp), or WER dumps to flush
        string crashDir = Path.Combine(clientRoot, "logs", "crash");
        PruneStaleSessionSentinels(crashDir, msg => Log(LogCategory.Executable, msg));

        string legacyCrashSentinelPath = Path.Combine(crashDir, "last-crashed.sentinel");
        string? pidCrashSentinelPath = gameProcessId is int gPid and > 0
            ? Path.Combine(crashDir, $"last-crashed.{gPid}.sentinel")
            : null;

        bool isCrash = exitCode != 0;
        string? silentExitZeroReason = null;
        DateTimeOffset threshold = launchStartUtc - TimeSpan.FromSeconds(5);
        DateTimeOffset exitObservedUtc = TimeProvider.System.GetUtcNow();
        string? latestDmp = DiagnosticBundleService.LocateLatestCrashDumpPath(clientRoot, minWriteTimeUtc: threshold);
        bool initialSentinelHit = IsSessionCrashSentinelHit(pidCrashSentinelPath, legacyCrashSentinelPath, gameProcessId, threshold);
        DateTimeOffset? recoveredMidSessionDumpUtc = null;
        while (!initialSentinelHit && !string.IsNullOrEmpty(latestDmp))
        {
            string dmpFileName = Path.GetFileName(latestDmp);
            DateTimeOffset dmpWriteUtc = new(File.GetLastWriteTimeUtc(latestDmp), TimeSpan.Zero);
            TimeSpan ageBeforeExit = exitObservedUtc - dmpWriteUtc;
            if (dmpFileName.StartsWith("minidump_", StringComparison.OrdinalIgnoreCase) &&
                ageBeforeExit > TimeSpan.FromSeconds(10))
            {
                Log(LogCategory.Executable,
                    $"[CRASH_SUPERVISION] Ignoring non-fatal recovered mid-session VEH minidump '{dmpFileName}' written {ageBeforeExit.TotalSeconds:F1}s before ExitCode 0x{exitCode:X8} ({pidTag}).");
                if (!recoveredMidSessionDumpUtc.HasValue || dmpWriteUtc > recoveredMidSessionDumpUtc.Value)
                {
                    recoveredMidSessionDumpUtc = dmpWriteUtc;
                }
                if (dmpWriteUtc >= threshold)
                {
                    threshold = dmpWriteUtc.AddTicks(1);
                }
                latestDmp = DiagnosticBundleService.LocateLatestCrashDumpPath(clientRoot, minWriteTimeUtc: threshold);
                continue;
            }
            break;
        }
        if (initialSentinelHit || !string.IsNullOrEmpty(latestDmp))
        {
            isCrash = true;
        }

        // Cross-platform + Per-PID Silent ExitCode 0 Detection (catches CSystem::Exit(0), TerminateProcess(0), or unhandled silent aborts)
        if (!isCrash && exitCode == 0)
        {
            (bool isSilentCrash, string? silentReason) = await EvaluateSilentExitZeroCrashAsync(
                clientRoot,
                crashDir,
                launchStartUtc,
                gameProcessId).ConfigureAwait(false);
            if (isSilentCrash)
            {
                isCrash = true;
                silentExitZeroReason = silentReason;
                _lastSilentExitZeroReason = silentReason;
                Log(LogCategory.Executable,
                    $"[CRASH_SUPERVISION] Silent ExitCode 0 termination detected ({pidTag}): {silentExitZeroReason}");
            }
        }

        // Only poll the 2.5s minidump flush settle window when a non-zero exit or explicit crash sentinel was detected
        if (isCrash && string.IsNullOrEmpty(latestDmp) && (exitCode != 0 || initialSentinelHit))
        {
            TimeSpan settlePollDelay = CrashDumpSettlePollIntervalOverride ?? TimeSpan.FromMilliseconds(250);
            for (int i = 0; i < 10; i++)
            {
                await Task.Delay(settlePollDelay).ConfigureAwait(false);
                bool sentinelHit = IsSessionCrashSentinelHit(pidCrashSentinelPath, legacyCrashSentinelPath, gameProcessId, threshold);
                latestDmp = DiagnosticBundleService.LocateLatestCrashDumpPath(clientRoot, minWriteTimeUtc: threshold);
                if (sentinelHit || !string.IsNullOrEmpty(latestDmp))
                {
                    isCrash = true;
                    if (!string.IsNullOrEmpty(latestDmp))
                    {
                        break;
                    }
                }
            }
        }

        CleanupSessionActiveSentinels(crashDir, gameProcessId, msg => Log(LogCategory.Executable, msg), clientRoot);

        string diagSettingsDir = ResolveCanonicalDiagnosticsSettingsDir(clientRoot);
        if (!isCrash)
        {
            try
            {
                var cleanSettings = LauncherSettingsService.Load(diagSettingsDir);
                bool settingsDirty = false;
                if (cleanSettings.ConsecutiveCrashCount > 0)
                {
                    cleanSettings.ConsecutiveCrashCount = 0;
                    settingsDirty = true;
                }
                if (recoveredMidSessionDumpUtc.HasValue &&
                    (!DateTimeOffset.TryParse(cleanSettings.LastAcknowledgedCrashUtc, out DateTimeOffset prevAckUtc) ||
                     recoveredMidSessionDumpUtc.Value > prevAckUtc))
                {
                    cleanSettings.LastAcknowledgedCrashUtc = recoveredMidSessionDumpUtc.Value.ToString("O");
                    settingsDirty = true;
                }
                if (settingsDirty)
                {
                    LauncherSettingsService.Save(cleanSettings, diagSettingsDir);
                }
            }
            catch (Exception ex)
            {
                Log(LogCategory.Executable, $"Reset consecutive crash count notice: {ex.Message}");
            }

            Log(LogCategory.Executable, "Clean game client exit confirmed (ExitCode: 0). Exiting launcher session...");
            AppendPlayerSessionRecord(clientRoot, gameProcessId, launchStartUtc, exitCode, isCrash: false, crashReason: null);
            ShutdownLauncherCleanly(0);
            return;
        }

        SnapshotActiveConsoleLogForCrashReport(clientRoot, crashDir, launchStartUtc, gameProcessId, msg => Log(LogCategory.Executable, msg));

        TimeSpan sessionDuration = TimeProvider.System.GetUtcNow() - launchStartUtc;
        bool isMidMatch = sessionDuration >= TimeSpan.FromSeconds(15);

        latestDmp ??= DiagnosticBundleService.LocateLatestCrashDumpPath(
            clientRoot,
            minWriteTimeUtc: threshold,
            includeWerFallback: true);

        // Crash or abnormal exit detected: keep Willowmaker alive!
        Log(LogCategory.Executable, $"[CRASH_SUPERVISION] Game crash detected (ExitCode: 0x{exitCode:X8}, SessionDump: '{latestDmp ?? "none"}', SilentReason: '{silentExitZeroReason ?? "n/a"}', Duration={sessionDuration.TotalSeconds:F1}s). Preserving launcher session.");

        _activeCrashDumpPath = latestDmp;
        _activeTriageResult = !string.IsNullOrEmpty(latestDmp)
            ? MinidumpReader.Inspect(latestDmp)
            : null;

        string faultDesc = _activeTriageResult?.IsValidDump == true
            ? $"{_activeTriageResult.TargetFormatted} • {_activeTriageResult.ExceptionCodeHex}"
            : !string.IsNullOrWhiteSpace(silentExitZeroReason)
                ? "Unexpected Client Exit (0x00000000 • No Quit Marker)"
                : $"Process Exit 0x{exitCode:X8}";

        AppendPlayerSessionRecord(clientRoot, gameProcessId, launchStartUtc, exitCode, isCrash: true, crashReason: silentExitZeroReason ?? faultDesc);

        // Immediately restore launch/editor buttons and surface recovery banner on UI thread
        await RunOnUIThreadAsync(() =>
        {
            LaunchIsInProgress = false;
            CrashReportTargetText = faultDesc;
            CrashReportBannerText = $"Game crash detected ({faultDesc}). Saving crash diagnostic zip...";
            LastCrashFaultSummary = faultDesc;
            HasCrashReportBanner = true;
        });

        // Automatically package scrubbed diagnostic .zip immediately on crash so details are never lost
        try
        {
            string modStr = ClientModMountSpec.GetCanonicalModString(includeOverride: !PureCompatibilityMode);
            string masterServer = BuildMasterServerAddress();
            string cdnHost = BuildCDNAddress();

            await Logger.FlushPendingEntriesAsync();

            DiagnosticBundleResult autoBundle = await DiagnosticBundleService.ExportBundleAsync(
                clientRoot: clientRoot,
                isCrashTriggered: true,
                triageResult: _activeTriageResult,
                activeModString: modStr,
                masterServerHost: masterServer,
                cdnHost: cdnHost,
                outputDirectoryOverride: outputDirectoryOverride,
                revealInFileManager: false,
                preferredDumpPath: latestDmp,
                minDumpWriteTimeUtc: threshold,
                processExitCode: exitCode,
                capturedFatalStderr: GetCapturedClientStderr(),
                activeLauncherLogPath: Logger.FilePath,
                expectedProcessId: gameProcessId,
                silentExitZeroReason: silentExitZeroReason);

            _lastAutoExportedCrashZipPath = autoBundle.ZipPath;
            _lastAutoExportedMarkdownSnippet = autoBundle.MarkdownSnippet;
            string zipName = Path.GetFileName(autoBundle.ZipPath);
            long sizeKb = Math.Max(1L, autoBundle.ZipSizeBytes / 1024L);

            await RunOnUIThreadAsync(() =>
            {
                CrashReportBannerText = $"Game crash detected ({faultDesc}). Saved {zipName} ({sizeKb} KB) locally.";
                DiagnosticExportStatusText = $"✓ Crash zip saved: {zipName} ({sizeKb} KB)";
            });

            Log(LogCategory.Executable,
                $"[CRASH_SUPERVISION] Automatically generated crash diagnostic zip '{autoBundle.ZipPath}' ({sizeKb} KB, redacted={autoBundle.RedactionCounts.TotalRedactions}).");

            // Acknowledge supervised crash immediately after saving its zip so cold-boot sentinel does not re-trigger on next launch
            try
            {
                DateTimeOffset ackUtc = TimeProvider.System.GetUtcNow();
                if (!string.IsNullOrEmpty(latestDmp) && File.Exists(latestDmp))
                {
                    DateTimeOffset dmpUtc = new(File.GetLastWriteTimeUtc(latestDmp), TimeSpan.Zero);
                    if (dmpUtc > ackUtc) ackUtc = dmpUtc;
                }
                if (!string.IsNullOrWhiteSpace(pidCrashSentinelPath) && File.Exists(pidCrashSentinelPath))
                {
                    DateTimeOffset pidSentUtc = new(File.GetLastWriteTimeUtc(pidCrashSentinelPath), TimeSpan.Zero);
                    if (pidSentUtc > ackUtc) ackUtc = pidSentUtc;
                }
                if (File.Exists(legacyCrashSentinelPath))
                {
                    DateTimeOffset sentUtc = new(File.GetLastWriteTimeUtc(legacyCrashSentinelPath), TimeSpan.Zero);
                    if (sentUtc > ackUtc) ackUtc = sentUtc;
                }

                var ackSettings = LauncherSettingsService.Load(diagSettingsDir);
                ackSettings.LastAcknowledgedCrashUtc = ackUtc.ToString("O");
                ackSettings.LastCrashFaultSummary = faultDesc;
                ackSettings.LastCrashTimeUtc = ackUtc.ToString("O");
                ackSettings.ConsecutiveCrashCount++;
                LauncherSettingsService.Save(ackSettings, diagSettingsDir);
                Log(LogCategory.Executable,
                    $"[CRASH_SUPERVISION] Preserved crash metadata in settings: Fault='{faultDesc}', ConsecutiveCrashes={ackSettings.ConsecutiveCrashCount}.");
            }
            catch (Exception ackEx)
            {
                Log(LogCategory.Executable, $"[CRASH_SUPERVISION] Crash acknowledgment save notice: {ackEx.Message}");
            }
        }
        catch (Exception ex)
        {
            await RunOnUIThreadAsync(() =>
            {
                CrashReportBannerText = $"Game crash detected ({faultDesc}). Saved locally, nothing was uploaded.";
            });
            Log(LogCategory.Executable,
                $"[CRASH_SUPERVISION] Automatic crash diagnostic zip export warning: {ex.Message}. Manual ExportDiagnosticBundle remains available.");
        }

        // Bring main window to front safely on UI thread
        await RunOnUIThreadAsync(() =>
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
                desktop.MainWindow != null)
            {
                desktop.MainWindow.Show();
                desktop.MainWindow.Activate();
            }
        });

        // Watchdog Circuit-Breaker: Check for opt-in mid-match auto-restart in Safe Mode
        var settingsAfterCrash = LauncherSettingsService.Load(diagSettingsDir);
        if (settingsAfterCrash.AutoRestartMidMatchCrash && isMidMatch && settingsAfterCrash.ConsecutiveCrashCount < 2)
        {
            Log(LogCategory.Executable,
                $"[CRASH_WATCHDOG] Auto-restarting mid-match crash session in Safe Mode (SessionDuration={sessionDuration.TotalSeconds:F1}s, CrashCount={settingsAfterCrash.ConsecutiveCrashCount})...");
            _ = Task.Run(async () =>
            {
                await Task.Delay(1000);
                await RunOnUIThreadAsync(async () =>
                {
                    await LaunchGameClientCore(skipSynchronisation: true, extraArguments: null, isSafeMode: true);
                });
            });
        }
        else if (settingsAfterCrash.AutoRestartMidMatchCrash)
        {
            if (!isMidMatch)
            {
                Log(LogCategory.Executable,
                    $"[CRASH_WATCHDOG] Circuit breaker tripped: Crash occurred during boot phase ({sessionDuration.TotalSeconds:F1}s < 15s). Halting auto-restart.");
            }
            else
            {
                Log(LogCategory.Executable,
                    $"[CRASH_WATCHDOG] Circuit breaker tripped: Consecutive crash count reached limit ({settingsAfterCrash.ConsecutiveCrashCount} >= 2). Halting auto-restart.");
            }
        }
    }

    [RelayCommand]
    private async Task ExportDiagnosticBundleAsync()
    {
        await ExportDiagnosticBundleForRootAsync();
    }

    public async Task<DiagnosticBundleResult?> ExportDiagnosticBundleForRootAsync(
        string? clientRootOverride = null,
        string? outputDirectoryOverride = null,
        bool revealInFileManager = true)
    {
        if (IsExportingDiagnostics) return null;

        DiagnosticBundleResult? exportedResult = null;
        try
        {
            IsExportingDiagnostics = true;
            string root = !string.IsNullOrWhiteSpace(clientRootOverride)
                ? clientRootOverride
                : (LocationGuard.ResolveGameClientDirectory() ?? Environment.CurrentDirectory);

            // If this crash was already auto-packaged, reveal the saved .zip and copy its Discord summary instead of writing a duplicate archive
            if (HasCrashReportBanner &&
                !string.IsNullOrWhiteSpace(_lastAutoExportedCrashZipPath) &&
                File.Exists(_lastAutoExportedCrashZipPath))
            {
                long existingKb = Math.Max(1L, new FileInfo(_lastAutoExportedCrashZipPath).Length / 1024L);
                if (!string.IsNullOrWhiteSpace(_lastAutoExportedMarkdownSnippet) &&
                    Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime existingDesktop &&
                    existingDesktop.MainWindow?.Clipboard is { } existingClipboard)
                {
                    await existingClipboard.SetTextAsync(_lastAutoExportedMarkdownSnippet);
                }

                if (revealInFileManager)
                {
                    DiagnosticBundleService.RevealFileInExplorer(_lastAutoExportedCrashZipPath);
                }
                DiagnosticExportStatusText = $"✓ Crash Zip Opened ({existingKb} KB) & Summary Copied!";
                Log(LogCategory.General, $"[DIAGNOSTICS] Revealed already-saved crash bundle '{_lastAutoExportedCrashZipPath}'.");
            }
            else
            {
                DiagnosticExportStatusText = "Scrubbing & packing diagnostic zip...";

                string modStr = ClientModMountSpec.GetCanonicalModString(includeOverride: !PureCompatibilityMode);
                string masterServer = BuildMasterServerAddress();
                string cdnHost = BuildCDNAddress();

                await Logger.FlushPendingEntriesAsync();

                DateTimeOffset? exportMinDumpWriteTimeUtc = (_lastLaunchStartUtc ?? _coldBootSentinelUtc)?.AddSeconds(-5);
                int? exportExpectedProcessId = _lastGameProcessId ?? _coldBootOrphanPid;
                Log(LogCategory.General,
                    $"[DIAGNOSTICS] Exporting diagnostic bundle (isCrashTriggered={HasCrashReportBanner}, minDumpWriteTimeUtc={exportMinDumpWriteTimeUtc?.ToString("O") ?? "null"}, expectedProcessId={exportExpectedProcessId?.ToString() ?? "null"}, preferredDump='{_activeCrashDumpPath ?? "none"}').");

                DiagnosticBundleResult result = await DiagnosticBundleService.ExportBundleAsync(
                    clientRoot: root,
                    isCrashTriggered: HasCrashReportBanner,
                    triageResult: _activeTriageResult,
                    activeModString: modStr,
                    masterServerHost: masterServer,
                    cdnHost: cdnHost,
                    outputDirectoryOverride: outputDirectoryOverride,
                    revealInFileManager: revealInFileManager,
                    preferredDumpPath: HasCrashReportBanner ? _activeCrashDumpPath : null,
                    minDumpWriteTimeUtc: exportMinDumpWriteTimeUtc,
                    processExitCode: _lastProcessExitCode,
                    capturedFatalStderr: GetCapturedClientStderr(),
                    activeLauncherLogPath: Logger.FilePath,
                    expectedProcessId: exportExpectedProcessId,
                    silentExitZeroReason: _lastSilentExitZeroReason);

                exportedResult = result;

                // Copy 6-line summary card to clipboard
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
                    desktop.MainWindow?.Clipboard is { } clipboard)
                {
                    await clipboard.SetTextAsync(result.MarkdownSnippet);
                }

                if (HasCrashReportBanner)
                {
                    _lastAutoExportedCrashZipPath = result.ZipPath;
                    _lastAutoExportedMarkdownSnippet = result.MarkdownSnippet;
                }

                DiagnosticExportStatusText = $"✓ Zip Ready ({result.ZipSizeBytes / 1024} KB) & Summary Copied!";
                Log(LogCategory.General, $"[DIAGNOSTICS] Exported Discord bundle to '{result.ZipPath}'. Scrubbed {result.RedactionCounts.TotalRedactions} tokens.");

                // Acknowledge crash so banner does not repeat on next launch
                string exportSettingsDir = ResolveCanonicalDiagnosticsSettingsDir(root);
                var settings = LauncherSettingsService.Load(exportSettingsDir);
                settings.LastAcknowledgedCrashUtc = TimeProvider.System.GetUtcNow().ToString("O");
                LauncherSettingsService.Save(settings, exportSettingsDir);
            }

            _ = Task.Delay(6000).ContinueWith(_ =>
            {
                if (Application.Current != null)
                {
                    Dispatcher.UIThread.Post(() => DiagnosticExportStatusText = string.Empty);
                }
                else
                {
                    DiagnosticExportStatusText = string.Empty;
                }
            }, TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            DiagnosticExportStatusText = $"Export error: {ex.Message}";
            Log(LogCategory.General, $"[DIAGNOSTICS] Failed to export bundle: {ex.Message}");
        }
        finally
        {
            IsExportingDiagnostics = false;
        }

        return exportedResult;
    }

    private int? TryParsePidFromSentinelFile(string sentinelPath)
    {
        try
        {
            if (!File.Exists(sentinelPath))
            {
                return null;
            }

            string content = File.ReadAllText(sentinelPath);
            Match match = Regex.Match(content, @"\bPID\s*=\s*(\d+)\b", RegexOptions.IgnoreCase);
            if (match.Success && int.TryParse(match.Groups[1].Value, out int pid) && pid > 0)
            {
                return pid;
            }
        }
        catch (Exception ex)
        {
            Log(LogCategory.Executable, $"[CRASH_SUPERVISION] Failed reading sentinel PID from '{sentinelPath}': {ex.Message}");
        }

        return null;
    }

    [RelayCommand]
    private async Task RelaunchInSafeModeAsync()
    {
        Log(LogCategory.Executable, "[SAFE_MODE] User requested Relaunch in Safe Mode.");
        DismissCrashBanner();
        await LaunchGameClientCore(skipSynchronisation: true, extraArguments: null, isSafeMode: true);
    }

    [RelayCommand]
    private void DismissCrashBanner()
    {
        HasCrashReportBanner = false;
        LastCrashFaultSummary = string.Empty;
        _activeCrashDumpPath = null;
        _activeTriageResult = null;
        _lastAutoExportedCrashZipPath = null;
        _lastAutoExportedMarkdownSnippet = null;
        _lastProcessExitCode = null;
        _lastSilentExitZeroReason = null;
        _lastLaunchStartUtc = null;
        int? dismissedOrphanPid = _coldBootOrphanPid;
        _coldBootSentinelUtc = null;
        _coldBootOrphanPid = null;
        _lastGameProcessId = null;
        ClearClientStderrBuffer();
        string root = LocationGuard.ResolveGameClientDirectory() ?? Environment.CurrentDirectory;
        string dismissSettingsDir = ResolveCanonicalDiagnosticsSettingsDir(root);
        var settings = LauncherSettingsService.Load(dismissSettingsDir);
        settings.LastAcknowledgedCrashUtc = TimeProvider.System.GetUtcNow().ToString("O");
        LauncherSettingsService.Save(settings, dismissSettingsDir);
        if (dismissedOrphanPid is int orphanPid and > 0)
        {
            CleanupSessionActiveSentinels(Path.Combine(root, "logs", "crash"), orphanPid, msg => Log(LogCategory.Executable, msg), root);
        }
    }

    private static readonly string[] CleanClientShutdownMarkers =
    [
        "[KONGOR-CLEAN-QUIT]",
        "[BANJO_QUIT]",
        "CHost::Shutdown",
        "CSystem::Shutdown",
        "Host_Shutdown",
        "Shutting down...",
        "K2Updater",
        "Executing updater",
        "Applying update"
    ];

    public static bool IsSessionCrashSentinelHit(
        string? pidCrashSentinelPath,
        string legacyCrashSentinelPath,
        int? gameProcessId,
        DateTimeOffset threshold)
    {
        if (!string.IsNullOrWhiteSpace(pidCrashSentinelPath) &&
            File.Exists(pidCrashSentinelPath) &&
            File.GetLastWriteTimeUtc(pidCrashSentinelPath) >= threshold.UtcDateTime)
        {
            return true;
        }

        if (!File.Exists(legacyCrashSentinelPath) ||
            File.GetLastWriteTimeUtc(legacyCrashSentinelPath) < threshold.UtcDateTime)
        {
            return false;
        }

        if (gameProcessId is not (int pid and > 0))
        {
            return true;
        }

        try
        {
            string text = ReadSharedFileTailUtf8(legacyCrashSentinelPath, maxBytes: 4096);
            if (text.Contains("PID=", StringComparison.OrdinalIgnoreCase) &&
                !text.Contains($"PID={pid}", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[CRASH_SUPERVISION] Legacy sentinel read notice: {ex.Message}");
        }

        return true;
    }

    public static async Task<(bool IsSilentCrash, string? Reason)> EvaluateSilentExitZeroCrashAsync(
        string clientRoot,
        string crashDir,
        DateTimeOffset launchStartUtc,
        int? gameProcessId)
    {
        DateTimeOffset sentinelThreshold = launchStartUtc - TimeSpan.FromSeconds(5);

        // Layer 1: Windows Per-PID NativeGuard Two-Phase Session Handshake
        // (session-active.<pid>.sentinel -> quit-initiated.<pid>.sentinel -> clean-exit.<pid>.sentinel)
        if (gameProcessId is int pid and > 0)
        {
            string cleanExitPidPath = Path.Combine(crashDir, $"clean-exit.{pid}.sentinel");
            string sessionActivePidPath = Path.Combine(crashDir, $"session-active.{pid}.sentinel");
            string quitInitiatedPidPath = Path.Combine(crashDir, $"quit-initiated.{pid}.sentinel");

            bool hasValidCleanExit = File.Exists(cleanExitPidPath) &&
                File.GetLastWriteTimeUtc(cleanExitPidPath) >= sentinelThreshold.UtcDateTime;
            bool hasValidQuitInitiated = File.Exists(quitInitiatedPidPath) &&
                File.GetLastWriteTimeUtc(quitInitiatedPidPath) >= sentinelThreshold.UtcDateTime;
            bool hasValidSessionActive = File.Exists(sessionActivePidPath) &&
                File.GetLastWriteTimeUtc(sessionActivePidPath) >= sentinelThreshold.UtcDateTime;

            if (hasValidCleanExit)
            {
                if (hasValidQuitInitiated)
                {
                    DateTimeOffset quitWriteUtc = new(File.GetLastWriteTimeUtc(quitInitiatedPidPath), TimeSpan.Zero);
                    TimeSpan shutdownElapsed = TimeProvider.System.GetUtcNow() - quitWriteUtc;
                    if (shutdownElapsed > TimeSpan.FromSeconds(10))
                    {
                        return (true, $"Abnormal shutdown stall detected ({shutdownElapsed.TotalSeconds:F1}s between 'quit-initiated.{pid}.sentinel' and process exit).");
                    }
                }
                return (false, null);
            }

            if (hasValidSessionActive || hasValidQuitInitiated)
            {
                // Allow a brief 120ms flush window in case CHost::Shutdown is finishing its clean-exit write
                await Task.Delay(120).ConfigureAwait(false);
                if (File.Exists(cleanExitPidPath) &&
                    File.GetLastWriteTimeUtc(cleanExitPidPath) >= sentinelThreshold.UtcDateTime)
                {
                    return (false, null);
                }

                // Windows Per-PID Sentinel Precedence: When NativeGuard wrote session-active/quit-initiated
                // without clean-exit.<pid>.sentinel, DO NOT let early pre-teardown [KONGOR-CLEAN-QUIT]
                // lines in console.log mask a crash that happened inside CVid::Shutdown / CHost::Shutdown!
                if (hasValidQuitInitiated)
                {
                    return (true, $"Client initiated quit ('quit-initiated.{pid}.sentinel') but exited without completing CHost::Shutdown ('clean-exit.{pid}.sentinel' missing).");
                }

                return (true, $"Active session sentinel 'session-active.{pid}.sentinel' remained open without 'clean-exit.{pid}.sentinel'.");
            }
        }

        // Layer 2: Cross-Platform console.log Clean-Quit Marker Inspection (Windows, Linux, and macOS)
        (bool foundActiveLog, bool foundCleanQuitMarker, string? activeLogPath) =
            InspectSessionConsoleLogShutdownState(clientRoot, launchStartUtc);
        if (foundActiveLog && !foundCleanQuitMarker)
        {
            await Task.Delay(100).ConfigureAwait(false);
            (foundActiveLog, foundCleanQuitMarker, activeLogPath) =
                InspectSessionConsoleLogShutdownState(clientRoot, launchStartUtc);
            if (foundActiveLog && !foundCleanQuitMarker)
            {
                return (true, $"Session console log '{activeLogPath}' modified during this launch exited with code 0 without any Quit/Shutdown/Updater marker.");
            }
        }

        return (false, null);
    }

    public static (bool FoundSessionConsoleLog, bool HasCleanShutdownMarker, string? ConsoleLogPath) InspectSessionConsoleLogShutdownState(
        string clientRoot,
        DateTimeOffset launchStartUtc)
    {
        string[] candidateConsoleLogs =
        [
            Path.Combine(clientRoot, "userdata", "Heroes of Newerth x64", "override", "console.log"),
            Path.Combine(clientRoot, "userdata", "Heroes of Newerth x64", "game", "console.log"),
            Path.Combine(clientRoot, "userdata", ".Heroes of Newerth", "override", "console.log"),
            Path.Combine(clientRoot, "userdata", ".Heroes of Newerth", "game", "console.log"),
            Path.Combine(clientRoot, "userdata", ".Heroes of Newerth x64", "override", "console.log"),
            Path.Combine(clientRoot, "userdata", ".Heroes of Newerth x64", "game", "console.log"),
            Path.Combine(clientRoot, "userdata", "Library", "Application Support", "Heroes of Newerth", "override", "console.log"),
            Path.Combine(clientRoot, "userdata", "Library", "Application Support", "Heroes of Newerth", "game", "console.log"),
            Path.Combine(clientRoot, "userdata", "override", "console.log"),
            Path.Combine(clientRoot, "userdata", "game", "console.log")
        ];

        string? newestPath = null;
        DateTime newestWriteUtc = DateTime.MinValue;
        // Require LastWriteTimeUtc >= launchStartUtc (-500ms clock skew tolerance) so stale logs from prior sessions are never inspected
        DateTime minSessionWriteUtc = launchStartUtc.UtcDateTime.AddMilliseconds(-500);

        foreach (string candidate in candidateConsoleLogs)
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                FileInfo fi = new(candidate);
                if (fi.Length < 64 || fi.LastWriteTimeUtc < minSessionWriteUtc) continue;
                if (fi.LastWriteTimeUtc > newestWriteUtc)
                {
                    newestWriteUtc = fi.LastWriteTimeUtc;
                    newestPath = candidate;
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[CRASH_SUPERVISION] Console log stat notice for '{candidate}': {ex.Message}");
            }
        }

        if (string.IsNullOrWhiteSpace(newestPath))
        {
            return (false, false, null);
        }

        try
        {
            string head = ReadSharedFileHeadUtf8(newestPath, maxBytes: 4096);
            string tail = ReadSharedFileTailUtf8(newestPath, maxBytes: 65536);

            bool looksLikeK2Session =
                head.Contains("S2 Games", StringComparison.OrdinalIgnoreCase) ||
                head.Contains("Heroes of Newerth", StringComparison.OrdinalIgnoreCase) ||
                head.Contains("K2 -", StringComparison.OrdinalIgnoreCase) ||
                tail.Contains("[MAIN_LUA]", StringComparison.OrdinalIgnoreCase) ||
                tail.Contains("CHost", StringComparison.OrdinalIgnoreCase) ||
                tail.Contains("CSystem", StringComparison.OrdinalIgnoreCase);

            if (!looksLikeK2Session)
            {
                return (false, false, newestPath);
            }

            foreach (string marker in CleanClientShutdownMarkers)
            {
                if (tail.Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    return (true, true, newestPath);
                }
            }

            return (true, false, newestPath);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[CRASH_SUPERVISION] Console log read notice for '{newestPath}': {ex.Message}");
            return (false, false, newestPath);
        }
    }

    private static string ReadSharedFileHeadUtf8(string path, int maxBytes)
    {
        using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        int toRead = (int)Math.Min(maxBytes, fs.Length);
        if (toRead <= 0) return string.Empty;
        byte[] buffer = new byte[toRead];
        int totalRead = 0;
        while (totalRead < toRead)
        {
            int n = fs.Read(buffer, totalRead, toRead - totalRead);
            if (n <= 0) break;
            totalRead += n;
        }
        return System.Text.Encoding.UTF8.GetString(buffer, 0, totalRead);
    }

    private static string ReadSharedFileTailUtf8(string path, int maxBytes)
    {
        using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long len = fs.Length;
        if (len <= 0) return string.Empty;
        int toRead = (int)Math.Min(maxBytes, len);
        fs.Seek(len - toRead, SeekOrigin.Begin);
        byte[] buffer = new byte[toRead];
        int totalRead = 0;
        while (totalRead < toRead)
        {
            int n = fs.Read(buffer, totalRead, toRead - totalRead);
            if (n <= 0) break;
            totalRead += n;
        }
        return System.Text.Encoding.UTF8.GetString(buffer, 0, totalRead);
    }

    private static bool IsProcessCurrentlyRunning(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using var proc = System.Diagnostics.Process.GetProcessById(pid);
            return !proc.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static void PruneStaleSessionSentinels(string crashDir, Action<string> log)
    {
        try
        {
            if (!Directory.Exists(crashDir)) return;
            DateTime cutoffUtc = TimeProvider.System.GetUtcNow().UtcDateTime.AddHours(-24);
            foreach (string file in Directory.EnumerateFiles(crashDir, "*.sentinel"))
            {
                string name = Path.GetFileName(file);
                if (!name.StartsWith("session-active.", StringComparison.OrdinalIgnoreCase) &&
                    !name.StartsWith("quit-initiated.", StringComparison.OrdinalIgnoreCase) &&
                    !name.StartsWith("clean-exit.", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoffUtc)
                    {
                        File.Delete(file);
                    }
                }
                catch (Exception ex)
                {
                    log($"[CRASH_SUPERVISION] Stale sentinel prune notice for '{name}': {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            log($"[CRASH_SUPERVISION] Stale sentinel scan notice: {ex.Message}");
        }
    }

    internal static void CleanupSessionActiveSentinels(
        string crashDir,
        int? gameProcessId,
        Action<string> log,
        string? clientRoot = null)
    {
        if (gameProcessId is not (int pid and > 0))
        {
            return;
        }

        List<string> dirsToClean = [];
        if (!string.IsNullOrWhiteSpace(crashDir) && Directory.Exists(crashDir))
        {
            dirsToClean.Add(crashDir);
        }

        if (!string.IsNullOrWhiteSpace(clientRoot))
        {
            string mirrorCrashDir = Path.Combine(clientRoot, "userdata", "logs", "crash");
            if (Directory.Exists(mirrorCrashDir) &&
                !dirsToClean.Any(d => string.Equals(d, mirrorCrashDir, StringComparison.OrdinalIgnoreCase)))
            {
                dirsToClean.Add(mirrorCrashDir);
            }
        }

        foreach (string dir in dirsToClean)
        {
            // Delete active & quit-initiated sentinels FIRST. Never delete clean-exit.<pid>.sentinel
            // if either active/quit sentinel failed to delete (prevents manufacturing a false-positive
            // SHUTDOWN_HANG_TIMEOUT on the next cold boot).
            bool allPreCleanDeleted = true;
            foreach (string name in new[] { $"session-active.{pid}.sentinel", $"quit-initiated.{pid}.sentinel" })
            {
                string full = Path.Combine(dir, name);
                if (!File.Exists(full)) continue;
                try
                {
                    File.Delete(full);
                }
                catch (Exception ex)
                {
                    allPreCleanDeleted = false;
                    log($"[CRASH_SUPERVISION] Sentinel cleanup notice for '{name}': {ex.Message}");
                }
            }

            if (!allPreCleanDeleted)
            {
                log($"[CRASH_SUPERVISION] Preserving clean-exit.{pid}.sentinel in '{dir}' because an active/quit sentinel remained locked.");
                continue;
            }

            string cleanFull = Path.Combine(dir, $"clean-exit.{pid}.sentinel");
            if (!File.Exists(cleanFull)) continue;
            try
            {
                File.Delete(cleanFull);
            }
            catch (Exception ex)
            {
                log($"[CRASH_SUPERVISION] Sentinel cleanup notice for 'clean-exit.{pid}.sentinel': {ex.Message}");
            }
        }
    }

    private static void SnapshotActiveConsoleLogForCrashReport(
        string clientRoot,
        string crashDir,
        DateTimeOffset launchStartUtc,
        int? gameProcessId,
        Action<string> log)
    {
        try
        {
            (bool found, _, string? consoleLogPath) = InspectSessionConsoleLogShutdownState(clientRoot, launchStartUtc);
            if (!found || string.IsNullOrWhiteSpace(consoleLogPath) || !File.Exists(consoleLogPath))
            {
                return;
            }

            Directory.CreateDirectory(crashDir);
            string pidSuffix = gameProcessId is int pid and > 0 ? $"_pid{pid}" : string.Empty;
            string snapshotName = $"crash_console_{TimeProvider.System.GetUtcNow().UtcDateTime:yyyyMMdd_HHmmss}{pidSuffix}.log";
            string destPath = Path.Combine(crashDir, snapshotName);

            using FileStream src = new(consoleLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using FileStream dst = new(destPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            src.CopyTo(dst);
            dst.Flush(flushToDisk: true);
            log($"[CRASH_SUPERVISION] Preserved zombie-safe console.log snapshot ({src.Length} bytes) -> '{destPath}'");
        }
        catch (Exception ex)
        {
            log($"[CRASH_SUPERVISION] Console log snapshot warning: {ex.Message}");
        }
    }

    /// <summary>
    /// Appends a real session telemetry record to logs/player_sessions.jsonl for SLO 6 compliance.
    /// </summary>
    public static void AppendPlayerSessionRecord(
        string clientRoot,
        int? processId,
        DateTimeOffset launchStartUtc,
        uint exitCode,
        bool isCrash,
        string? crashReason)
    {
        try
        {
            string sessionsPath = ResolvePlayerSessionsLogPath(clientRoot);
            string? logsDir = Path.GetDirectoryName(sessionsPath);
            if (!string.IsNullOrWhiteSpace(logsDir))
            {
                Directory.CreateDirectory(logsDir);
            }

            TimeSpan duration = TimeProvider.System.GetUtcNow() - launchStartUtc;
            string reasonJson = isCrash && !string.IsNullOrWhiteSpace(crashReason)
                ? $"\"{crashReason.Replace("\\", "\\\\").Replace("\"", "\\\"")}\""
                : "null";

            string record = $"{{\"timestampUtc\":\"{TimeProvider.System.GetUtcNow():o}\",\"sessionId\":\"{Guid.NewGuid():N}\",\"processId\":{processId ?? 0},\"durationMs\":{Math.Max(0, (long)duration.TotalMilliseconds)},\"exitCode\":{unchecked((int)exitCode)},\"crashed\":{(isCrash ? "true" : "false")},\"crashReason\":{reasonJson}}}\n";
            File.AppendAllText(sessionsPath, record);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Telemetry] AppendPlayerSessionRecord notice: {ex.Message}");
        }
    }
}
