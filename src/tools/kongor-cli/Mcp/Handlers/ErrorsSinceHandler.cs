// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

// ============================================================================
// File: ErrorsSinceHandler.cs
// System: Kongor MCP Server - Diagnostic Log Checkpoint & Error Audit Tool
// Purpose: Implements 'kongor_errors_since' (alias 'errors_since') to inspect
//          engine logs, Lua debug streams, and crash dumps since a checkpoint.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Kongor.Cli.Mcp.Protocol;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// MCP tool handler for 'kongor_errors_since' (aliases: 'errors_since', 'kongor_errors').
/// Interrogates engine console logs, client Lua debug logs, sentinel journals,
/// and crash minidump directories for any errors emitted after a specified checkpoint.
/// </summary>
public sealed class ErrorsSinceHandler
{
    private readonly string _repoRoot;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _logger;
    private readonly IReadOnlyList<Regex> _noiseBaselinePatterns;

    private static readonly Regex ErrorPattern = new(
        @"(?:\[ERROR\]|\[FATAL\]|Lua\s*Error|SCRIPT\s*ERROR|CRASH_GUARD|CRASH-PREVENTED|Unhandled\s+exception|Access\s+violation|Crash\s+dump|^(?:(?:Cl|Sv|Net|Resource):\s*)?\[[^\]]+\]\s*Error:|^Error:\s*\[)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex K2DiagnosticPattern = new(
        @"without a complete table entry|Could not find widget|GetWidget\s+.*?\s+failed|Invalid file type|attempt to (?:index|call|compare|perform|concatenate)|stack traceback|Failed to (?:load|find|open)|Could not open file|Missing operator before value|Syntax error|Script error|\[NEXUS_WARN\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex WarnPattern = new(
        @"\[WARN(?:ING)?\]|^Warn(?:ing)?:\s*\[",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BracketTimePattern = new(
        @"^(?:(?:Cl|Sv|Net|Resource|Warning|Warn|Error|Err):\s*)?\[(?<time>\d{2}:\d{2}:\d{2}(?:\.\d{1,3})?)\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex IsoDatePattern = new(
        @"^\[(?<date>\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2})",
        RegexOptions.Compiled);

    public ErrorsSinceHandler(
        string? repoRoot = null,
        TimeProvider? timeProvider = null,
        Action<string>? logger = null)
    {
        _repoRoot = repoRoot ?? FindRepoRoot();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
        _noiseBaselinePatterns = LoadNoiseBaselinePatterns(_repoRoot, _logger);
    }

    /// <summary>
    /// Generates a durable checkpoint token representing the current instant in UTC
    /// plus per-file byte offsets for deterministic log delta scanning.
    /// </summary>
    public static string CreateCheckpointToken(DateTimeOffset? time = null, string? repoRoot = null)
    {
        long ms = (time ?? TimeProvider.System.GetUtcNow()).ToUnixTimeMilliseconds();
        string resolvedRoot = repoRoot ?? FindRepoRoot();
        List<string> candidatePaths = GetCandidateLogPaths(resolvedRoot);
        List<string> offsetParts = new();
        for (int i = 0; i < candidatePaths.Count; i++)
        {
            string path = candidatePaths[i];
            if (!File.Exists(path)) continue;
            try
            {
                FileInfo fi = new(path);
                offsetParts.Add($"{i}:{fi.Length}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ErrorsSinceHandler] Checkpoint stat skipped for {path}: {ex.Message}");
            }
        }

        return offsetParts.Count > 0
            ? $"cp_{ms}|{string.Join(",", offsetParts)}"
            : $"cp_{ms}";
    }

    /// <summary>
    /// Captures a checkpoint token scoped to this handler's configured repository root.
    /// </summary>
    public string CaptureCheckpointToken(DateTimeOffset? time = null)
    {
        return CreateCheckpointToken(time ?? _timeProvider.GetUtcNow(), _repoRoot);
    }

    /// <summary>
    /// Parses a checkpoint token or ISO string into a DateTimeOffset in UTC.
    /// </summary>
    public static bool TryParseCheckpoint(string? token, out DateTimeOffset result)
    {
        return TryParseCheckpoint(token, out result, out _);
    }

    /// <summary>
    /// Parses a checkpoint token into a DateTimeOffset in UTC and optional per-file byte offsets.
    /// </summary>
    public static bool TryParseCheckpoint(string? token, out DateTimeOffset result, out Dictionary<int, long>? fileOffsets)
    {
        result = DateTimeOffset.MinValue;
        fileOffsets = null;
        if (string.IsNullOrWhiteSpace(token)) return false;

        string clean = token.Trim();
        if (clean.StartsWith("cp_", StringComparison.OrdinalIgnoreCase))
        {
            string payload = clean[3..];
            string numberPart = payload;
            int pipeIdx = payload.IndexOf('|');
            if (pipeIdx >= 0)
            {
                numberPart = payload[..pipeIdx];
                string offsetsPart = payload[(pipeIdx + 1)..];
                if (!string.IsNullOrWhiteSpace(offsetsPart))
                {
                    Dictionary<int, long> parsedOffsets = new();
                    string[] pairs = offsetsPart.Split(',', StringSplitOptions.RemoveEmptyEntries);
                    foreach (string pair in pairs)
                    {
                        int colonIdx = pair.IndexOf(':');
                        if (colonIdx > 0 &&
                            int.TryParse(pair[..colonIdx], NumberStyles.Integer, CultureInfo.InvariantCulture, out int fileIdx) &&
                            long.TryParse(pair[(colonIdx + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out long byteOffset) &&
                            byteOffset >= 0)
                        {
                            parsedOffsets[fileIdx] = byteOffset;
                        }
                    }

                    if (parsedOffsets.Count > 0)
                    {
                        fileOffsets = parsedOffsets;
                    }
                }
            }

            if (long.TryParse(numberPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out long ms))
            {
                result = DateTimeOffset.FromUnixTimeMilliseconds(ms);
                return true;
            }
        }

        if (DateTimeOffset.TryParse(clean, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset dto))
        {
            result = dto;
            return true;
        }

        return false;
    }

    public Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string? checkpointToken = null;
        if (arguments.TryGetProperty("checkpoint", out JsonElement cpEl) && cpEl.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(cpEl.GetString()))
        {
            checkpointToken = cpEl.GetString()!.Trim();
        }

        int sinceSeconds = 60;
        if (arguments.TryGetProperty("sinceSeconds", out JsonElement ssEl) && ssEl.TryGetInt32(out int ssVal) && ssVal > 0)
        {
            sinceSeconds = Math.Clamp(ssVal, 1, 86400);
        }
        else if (arguments.TryGetProperty("lookbackSeconds", out JsonElement lbEl) && lbEl.TryGetInt32(out int lbVal) && lbVal > 0)
        {
            sinceSeconds = Math.Clamp(lbVal, 1, 86400);
        }

        int limit = 100;
        if (arguments.TryGetProperty("limit", out JsonElement limEl) && limEl.TryGetInt32(out int lVal) && lVal > 0)
        {
            limit = Math.Clamp(lVal, 1, 1000);
        }

        string severityFilter = "error";
        if (arguments.TryGetProperty("severity", out JsonElement sevEl) && sevEl.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(sevEl.GetString()))
        {
            severityFilter = sevEl.GetString()!.Trim().ToLowerInvariant();
        }

        bool allowDiagnosticWarnings = false;
        if (arguments.TryGetProperty("allowDiagnosticWarnings", out JsonElement adwEl) &&
            (adwEl.ValueKind == JsonValueKind.True || adwEl.ValueKind == JsonValueKind.False))
        {
            allowDiagnosticWarnings = adwEl.GetBoolean();
        }

        string? reason = null;
        if (arguments.TryGetProperty("reason", out JsonElement reasonEl) && reasonEl.ValueKind == JsonValueKind.String)
        {
            reason = reasonEl.GetString()?.Trim();
        }

        if (allowDiagnosticWarnings && string.IsNullOrWhiteSpace(reason))
        {
            return Task.FromResult(ToolCallResult.Json(new
            {
                status = "error",
                success = false,
                verdict = "fail",
                errorCode = "MISSING_ESCAPE_HATCH_REASON",
                message = "allowDiagnosticWarnings=true requires a non-empty 'reason' parameter explaining why K2 diagnostic warnings are being waived.",
                remediation = "Provide a non-empty 'reason' string or fix the underlying K2 diagnostic warnings in console.log."
            }, isError: true));
        }

        bool includeBaselinedNoise = false;
        if (arguments.TryGetProperty("includeBaselinedNoise", out JsonElement ibnEl) &&
            (ibnEl.ValueKind == JsonValueKind.True || ibnEl.ValueKind == JsonValueKind.False))
        {
            includeBaselinedNoise = ibnEl.GetBoolean();
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        DateTimeOffset cutoffTime;
        Dictionary<int, long>? fileOffsets = null;
        if (!string.IsNullOrEmpty(checkpointToken) && TryParseCheckpoint(checkpointToken, out DateTimeOffset parsedCutoff, out fileOffsets))
        {
            cutoffTime = parsedCutoff;
        }
        else
        {
            cutoffTime = now.AddSeconds(-sinceSeconds);
        }

        string newCheckpoint = CaptureCheckpointToken(now);
        ErrorsScanReport report = ScanLogsCore(
            cutoffTime,
            severityFilter,
            limit,
            fileOffsets,
            allowDiagnosticWarnings,
            includeBaselinedNoise);

        _logger?.Invoke($"[ErrorsSinceHandler] Cutoff: {cutoffTime:o}, Errors: {report.Errors.Count}, K2Warn: {report.K2WarnCount}, Baselined: {report.BaselinedNoiseCount}, Minidumps: {report.RecentMinidumps.Count}, Clean: {report.IsClean}, Verdict: {report.Verdict}");

        return Task.FromResult(ToolCallResult.Json(new
        {
            status = "success",
            success = true,
            verdict = report.Verdict,
            checkpoint = newCheckpoint,
            checkpointTimeUtc = now.ToString("o"),
            sinceTimeUtc = cutoffTime.ToString("o"),
            clean = report.IsClean,
            liveClient = report.LiveClient,
            clientRunning = report.LiveClient,
            errorCount = report.Errors.Count,
            warningCount = report.WarningCount,
            k2WarnCount = report.K2WarnCount,
            baselinedNoiseCount = report.BaselinedNoiseCount,
            allowDiagnosticWarnings,
            escapeHatchReason = reason,
            minidumpCount = report.RecentMinidumps.Count,
            errors = report.Errors,
            warnings = report.Warnings,
            recentMinidumps = report.RecentMinidumps,
            logsScanned = report.LogsScanned
        }));
    }

    public ErrorsScanReport ScanLogs(
        DateTimeOffset cutoffTime,
        string severityFilter = "error",
        int limit = 100,
        bool allowDiagnosticWarnings = false,
        bool includeBaselinedNoise = false)
    {
        return ScanLogsCore(cutoffTime, severityFilter, limit, fileOffsets: null, allowDiagnosticWarnings, includeBaselinedNoise);
    }

    public ErrorsScanReport ScanLogsSinceCheckpoint(
        string? checkpointToken,
        string severityFilter = "error",
        int limit = 100,
        bool allowDiagnosticWarnings = false,
        bool includeBaselinedNoise = false)
    {
        DateTimeOffset cutoffTime = _timeProvider.GetUtcNow().AddSeconds(-60);
        Dictionary<int, long>? fileOffsets = null;
        if (!string.IsNullOrWhiteSpace(checkpointToken) &&
            TryParseCheckpoint(checkpointToken, out DateTimeOffset parsedCutoff, out fileOffsets))
        {
            cutoffTime = parsedCutoff;
        }

        return ScanLogsCore(cutoffTime, severityFilter, limit, fileOffsets, allowDiagnosticWarnings, includeBaselinedNoise);
    }

    private ErrorsScanReport ScanLogsCore(
        DateTimeOffset cutoffTime,
        string severityFilter,
        int limit,
        Dictionary<int, long>? fileOffsets,
        bool allowDiagnosticWarnings,
        bool includeBaselinedNoise)
    {
        List<string> scanned = new();
        List<ErrorDetailDto> errors = new();
        List<ErrorDetailDto> warnings = new();
        int warningCount = 0;
        int k2WarnCount = 0;
        int baselinedNoiseCount = 0;
        bool includeWarn = severityFilter is "warn" or "warning" or "all";

        bool isClientRunning = false;
        try
        {
            Process[] procs = Process.GetProcessesByName("hon_x64");
            isClientRunning = procs.Any(p => !p.HasExited);
        }
        catch (Exception ex)
        {
            _logger?.Invoke($"[ErrorsSinceHandler] Process probe warning: {ex.Message}");
        }

        List<string> candidatePaths = GetCandidateLogPaths(_repoRoot);

        for (int fileIdx = 0; fileIdx < candidatePaths.Count; fileIdx++)
        {
            string logPath = candidatePaths[fileIdx];
            if (!File.Exists(logPath)) continue;

            scanned.Add(logPath);
            try
            {
                FileInfo fi = new(logPath);
                bool hasFileOffset = fileOffsets != null && fileOffsets.TryGetValue(fileIdx, out long savedOffset);
                long checkpointOffset = hasFileOffset && fileOffsets != null ? fileOffsets[fileIdx] : -1L;

                if (hasFileOffset)
                {
                    // If file has not grown since the checkpoint (and wasn't truncated/rotated), nothing new was appended
                    if (fi.Length == checkpointOffset)
                    {
                        continue;
                    }
                }
                else
                {
                    // If log hasn't been modified since cutoff time (with 2s clock skew grace), skip line scanning
                    if (fi.LastWriteTimeUtc < cutoffTime.UtcDateTime.AddSeconds(-2))
                    {
                        continue;
                    }
                }

                using FileStream fs = new(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                (Encoding detectedEncoding, int bomLength, int charAlign) = DetectStreamEncoding(fs);

                bool discardFirstPartialLine = false;
                if (hasFileOffset && checkpointOffset >= 0 && fs.Length >= checkpointOffset)
                {
                    long targetOffset = Math.Max((long)bomLength, checkpointOffset);
                    if (charAlign == 2 && ((targetOffset - bomLength) % 2 != 0))
                    {
                        targetOffset++;
                    }
                    fs.Seek(Math.Min(targetOffset, fs.Length), SeekOrigin.Begin);
                }
                else if (!hasFileOffset && fs.Length > 256 * 1024)
                {
                    long targetOffset = fs.Length - (256 * 1024);
                    if (charAlign == 2 && ((targetOffset - bomLength) % 2 != 0))
                    {
                        targetOffset++;
                    }
                    fs.Seek(Math.Max((long)bomLength, targetOffset), SeekOrigin.Begin);
                    discardFirstPartialLine = true;
                }
                else
                {
                    fs.Seek(bomLength, SeekOrigin.Begin);
                }

                using StreamReader reader = new(fs, detectedEncoding, detectEncodingFromByteOrderMarks: false);
                if (discardFirstPartialLine)
                {
                    reader.ReadLine();
                }

                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    bool isError = ErrorPattern.IsMatch(line);
                    bool isK2Diagnostic = K2DiagnosticPattern.IsMatch(line);
                    bool isWarn = WarnPattern.IsMatch(line);

                    if (!isError && !isK2Diagnostic && !isWarn) continue;

                    DateTimeOffset lineTime = DateTimeOffset.MinValue;
                    bool hasLineTime = TryExtractLogTime(line, fi.LastWriteTimeUtc, out lineTime);

                    // When reading from an exact byte-offset checkpoint, all bytes after checkpointOffset are guaranteed new.
                    // Only filter by timestamp when scanning without a byte-offset checkpoint.
                    if (!hasFileOffset && hasLineTime && lineTime < cutoffTime)
                    {
                        continue;
                    }

                    if (!includeBaselinedNoise && IsBaselineNoise(line))
                    {
                        baselinedNoiseCount++;
                        continue;
                    }

                    string timestampIso = hasLineTime && lineTime != DateTimeOffset.MinValue
                        ? lineTime.ToString("o")
                        : fi.LastWriteTimeUtc.ToString("o");

                    if (isError)
                    {
                        errors.Add(new ErrorDetailDto
                        {
                            Source = Path.GetFileName(logPath),
                            Severity = "ERROR",
                            Timestamp = timestampIso,
                            Message = line.Trim()
                        });
                    }
                    else if (isK2Diagnostic)
                    {
                        k2WarnCount++;
                        warningCount++;
                        ErrorDetailDto diagDto = new()
                        {
                            Source = Path.GetFileName(logPath),
                            Severity = "K2_DIAGNOSTIC",
                            Timestamp = timestampIso,
                            Message = line.Trim()
                        };
                        warnings.Add(diagDto);
                        if (!allowDiagnosticWarnings)
                        {
                            errors.Add(diagDto);
                        }
                    }
                    else if (isWarn)
                    {
                        warningCount++;
                        ErrorDetailDto warnDto = new()
                        {
                            Source = Path.GetFileName(logPath),
                            Severity = "WARN",
                            Timestamp = timestampIso,
                            Message = line.Trim()
                        };
                        warnings.Add(warnDto);
                        if (includeWarn)
                        {
                            errors.Add(warnDto);
                        }
                    }

                    if (errors.Count >= limit) break;
                }
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"[ErrorsSinceHandler] Failed to read {logPath}: {ex.Message}");
            }

            if (errors.Count >= limit) break;
        }

        // Minidump directory inspection
        List<MinidumpDetailDto> recentDumps = new();
        string[] dumpDirs = [
            Path.Combine(_repoRoot, "games", "client", "logs", "crash"),
            Path.Combine(_repoRoot, "userdata", "logs", "crash"),
            Path.Combine(_repoRoot, "userdata")
        ];

        foreach (string dir in dumpDirs)
        {
            if (!Directory.Exists(dir)) continue;

            try
            {
                string[] dumps = Directory.GetFiles(dir, "*.dmp", SearchOption.TopDirectoryOnly);
                foreach (string dmp in dumps)
                {
                    FileInfo fi = new(dmp);
                    if (fi.CreationTimeUtc >= cutoffTime.UtcDateTime || fi.LastWriteTimeUtc >= cutoffTime.UtcDateTime)
                    {
                        recentDumps.Add(new MinidumpDetailDto
                        {
                            Path = fi.FullName,
                            CreatedTimeUtc = fi.CreationTimeUtc.ToString("o"),
                            SizeBytes = fi.Length
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"[ErrorsSinceHandler] Minidump scan warning for {dir}: {ex.Message}");
            }
        }

        bool isClean = errors.Count == 0 &&
                       recentDumps.Count == 0 &&
                       (allowDiagnosticWarnings || k2WarnCount == 0);

        return new ErrorsScanReport
        {
            IsClean = isClean,
            Verdict = isClean ? "pass" : "fail",
            AllowDiagnosticWarnings = allowDiagnosticWarnings,
            LiveClient = isClientRunning,
            Errors = errors,
            Warnings = warnings,
            WarningCount = warningCount,
            K2WarnCount = k2WarnCount,
            BaselinedNoiseCount = baselinedNoiseCount,
            RecentMinidumps = recentDumps,
            LogsScanned = scanned
        };
    }

    private bool IsBaselineNoise(string line)
    {
        for (int i = 0; i < _noiseBaselinePatterns.Count; i++)
        {
            if (_noiseBaselinePatterns[i].IsMatch(line))
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<Regex> LoadNoiseBaselinePatterns(string repoRoot, Action<string>? logger)
    {
        List<Regex> patterns = new();
        try
        {
            string baselinePath = Path.Combine(repoRoot, "data", "gate", "console_noise_baseline.json");
            if (!File.Exists(baselinePath))
            {
                string fallbackRoot = FindRepoRoot();
                baselinePath = Path.Combine(fallbackRoot, "data", "gate", "console_noise_baseline.json");
            }

            if (!File.Exists(baselinePath))
            {
                return patterns;
            }

            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(baselinePath));
            if (doc.RootElement.TryGetProperty("patterns", out JsonElement patternsEl) &&
                patternsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in patternsEl.EnumerateArray())
                {
                    if (item.TryGetProperty("regex", out JsonElement regexEl) &&
                        regexEl.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(regexEl.GetString()))
                    {
                        patterns.Add(new Regex(regexEl.GetString()!, RegexOptions.Compiled | RegexOptions.IgnoreCase));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger?.Invoke($"[ErrorsSinceHandler] Failed to load console_noise_baseline.json: {ex.Message}");
        }

        return patterns;
    }

    private static (Encoding Encoding, int BomLength, int CharAlign) DetectStreamEncoding(FileStream fs)
    {
        if (fs.Length < 2)
        {
            return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 0, 1);
        }

        long originalPos = fs.Position;
        fs.Seek(0, SeekOrigin.Begin);
        Span<byte> bom = stackalloc byte[3];
        int read = fs.Read(bom);
        fs.Seek(originalPos, SeekOrigin.Begin);

        if (read >= 2 && bom[0] == 0xFF && bom[1] == 0xFE)
        {
            return (Encoding.Unicode, 2, 2);
        }

        if (read >= 2 && bom[0] == 0xFE && bom[1] == 0xFF)
        {
            return (Encoding.BigEndianUnicode, 2, 2);
        }

        if (read >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF)
        {
            return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 3, 1);
        }

        return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 0, 1);
    }

    private static List<string> GetCandidateLogPaths(string repoRoot)
    {
        List<string> candidatePaths = new()
        {
            Path.Combine(repoRoot, "games", "client", "userdata", "Heroes of Newerth x64", "override", "console.log"),
            Path.Combine(repoRoot, "games", "client", "userdata", "Heroes of Newerth x64", "game", "console.log"),
            Path.Combine(repoRoot, "games", "client", "userdata", "Heroes of Newerth x64", "base", "console.log"),
            Path.Combine(repoRoot, "games", "client", "override", "logs", "client_lua_debug.log"),
            Path.Combine(repoRoot, "games", "client", "logs", "crash", "native_guard_sentinel.log"),
            Path.Combine(repoRoot, "games", "client", "logs", "crash", "engine_crashes.log"),
            Path.Combine(repoRoot, "userdata", "logs", "mailbox_idle_10m.log")
        };

        string myDocsHon = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Heroes of Newerth", "game", "logs", "client_lua_debug.log");
        if (File.Exists(myDocsHon))
        {
            candidatePaths.Add(myDocsHon);
        }

        return candidatePaths;
    }

    private static bool TryExtractLogTime(string line, DateTime logFileModifiedUtc, out DateTimeOffset time)
    {
        time = DateTimeOffset.MinValue;

        Match mIso = IsoDatePattern.Match(line);
        if (mIso.Success && DateTimeOffset.TryParse(mIso.Groups["date"].Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsedIso))
        {
            time = parsedIso;
            return true;
        }

        Match mBracket = BracketTimePattern.Match(line);
        if (mBracket.Success && TimeSpan.TryParse(mBracket.Groups["time"].Value, CultureInfo.InvariantCulture, out TimeSpan ts))
        {
            DateTimeOffset modUtc = new(DateTime.SpecifyKind(logFileModifiedUtc, DateTimeKind.Utc));

            // Candidate 1: UTC interpretation
            DateTimeOffset utcCandidate = new(logFileModifiedUtc.Date.Add(ts), TimeSpan.Zero);
            if (utcCandidate > modUtc.AddMinutes(5))
            {
                utcCandidate = utcCandidate.AddDays(-1);
            }

            // Candidate 2: Local timezone interpretation (K2 console.log writes [HH:mm:ss] in local time)
            DateTime localMod = modUtc.LocalDateTime;
            DateTime localCandidateDt = localMod.Date.Add(ts);
            TimeSpan localOffset = TimeZoneInfo.Local.GetUtcOffset(localCandidateDt);
            DateTimeOffset localCandidate = new DateTimeOffset(localCandidateDt, localOffset).ToUniversalTime();
            if (localCandidate > modUtc.AddMinutes(5))
            {
                localCandidate = localCandidate.AddDays(-1);
            }

            double utcDiff = Math.Abs((modUtc - utcCandidate).TotalSeconds);
            double localDiff = Math.Abs((modUtc - localCandidate).TotalSeconds);
            time = localDiff < utcDiff ? localCandidate : utcCandidate;
            return true;
        }

        return false;
    }

    private static string FindRepoRoot()
    {
        if (Kongor.Core.Configuration.KongorWorkspaceResolver.TryResolve(out var ws) && ws is not null)
        {
            return ws.Root;
        }

        return Directory.GetCurrentDirectory();
    }
}

public sealed class ErrorsScanReport
{
    public bool IsClean { get; set; }
    public string Verdict { get; set; } = "pass";
    public bool AllowDiagnosticWarnings { get; set; }
    public bool LiveClient { get; set; }
    public List<ErrorDetailDto> Errors { get; set; } = [];
    public List<ErrorDetailDto> Warnings { get; set; } = [];
    public int WarningCount { get; set; }
    public int K2WarnCount { get; set; }
    public int BaselinedNoiseCount { get; set; }
    public List<MinidumpDetailDto> RecentMinidumps { get; set; } = [];
    public List<string> LogsScanned { get; set; } = [];
}

public sealed class ErrorDetailDto
{
    public string Source { get; set; } = "";
    public string Severity { get; set; } = "";
    public string Timestamp { get; set; } = "";
    public string Message { get; set; } = "";
}

public sealed class MinidumpDetailDto
{
    public string Path { get; set; } = "";
    public string CreatedTimeUtc { get; set; } = "";
    public long SizeBytes { get; set; }
}
