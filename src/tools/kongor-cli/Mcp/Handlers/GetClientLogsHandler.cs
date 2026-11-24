// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// MCP Tool Handler for 'kongor_get_client_logs' (alias: 'get_client_logs').
/// Interrogates KongorLogger's in-memory 300-entry ring buffer over Named Pipe IPC
/// with fallback to disk logs (client_lua_debug.log and UTF-16LE console.log).
/// Delivers real-time telemetry, trapped errors, and avatar resolution diagnostics with structured filtering.
/// </summary>
public sealed class GetClientLogsHandler
{
    private readonly IGameIpcBridge? _ipcBridge;
    private readonly string _repoRoot;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _logger;

    private static readonly Regex DiskLogRegex = new(
        @"^\[(?<time>[^\]]+)\](?:\[\+(?<uptime>[^\]]+)\])?\[(?<channel>[^\]]+)\]\[(?<severity>[^\]]+)\]\s*(?<msg>[^|]+?)(?:\s*\|\s*(?<details>.*))?$",
        RegexOptions.Compiled);

    public GetClientLogsHandler(
        IGameIpcBridge? ipcBridge = null,
        string? repoRoot = null,
        TimeProvider? timeProvider = null,
        Action<string>? logger = null)
    {
        _ipcBridge = ipcBridge;
        _repoRoot = repoRoot ?? FindRepoRoot();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        Stopwatch sw = Stopwatch.StartNew();

        bool fullDetail = false;
        if (arguments.ValueKind == JsonValueKind.Object)
        {
            if (arguments.TryGetProperty("detail", out JsonElement detEl) &&
                detEl.ValueKind == JsonValueKind.String &&
                string.Equals(detEl.GetString(), "full", StringComparison.OrdinalIgnoreCase))
            {
                fullDetail = true;
            }
            else if (arguments.TryGetProperty("verbose", out JsonElement verbEl) &&
                     verbEl.ValueKind == JsonValueKind.True)
            {
                fullDetail = true;
            }
        }

        string channel = "ALL";
        if (arguments.ValueKind == JsonValueKind.Object &&
            (arguments.TryGetProperty("channel", out JsonElement chEl) ||
             arguments.TryGetProperty("channel_filter", out chEl) ||
             arguments.TryGetProperty("channelFilter", out chEl)) &&
            chEl.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(chEl.GetString()))
        {
            channel = chEl.GetString()!.Trim().ToUpperInvariant();
        }

        string? textFilter = null;
        if (arguments.ValueKind == JsonValueKind.Object &&
            (arguments.TryGetProperty("filter", out JsonElement tfEl) ||
             arguments.TryGetProperty("textFilter", out tfEl) ||
             arguments.TryGetProperty("text_filter", out tfEl) ||
             arguments.TryGetProperty("search", out tfEl) ||
             arguments.TryGetProperty("query", out tfEl)) &&
            tfEl.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(tfEl.GetString()))
        {
            textFilter = tfEl.GetString()!.Trim();
        }

        bool errorsOnly = false;
        string? severityFilter = null;
        if (arguments.ValueKind == JsonValueKind.Object)
        {
            if ((arguments.TryGetProperty("errors_only", out JsonElement eoEl) ||
                 arguments.TryGetProperty("errorsOnly", out eoEl)) &&
                (eoEl.ValueKind == JsonValueKind.True || eoEl.ValueKind == JsonValueKind.False))
            {
                errorsOnly = eoEl.GetBoolean();
            }

            if ((arguments.TryGetProperty("severity", out JsonElement sevEl) ||
                 arguments.TryGetProperty("level", out sevEl) ||
                 arguments.TryGetProperty("min_severity", out sevEl) ||
                 arguments.TryGetProperty("minSeverity", out sevEl)) &&
                sevEl.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(sevEl.GetString()))
            {
                severityFilter = sevEl.GetString()!.Trim().ToUpperInvariant();
                if (severityFilter is "ERROR" or "ERRORS" or "FATAL")
                {
                    errorsOnly = true;
                }
            }
        }

        int limit = fullDetail ? 50 : 10;
        if (arguments.ValueKind == JsonValueKind.Object &&
            (arguments.TryGetProperty("limit", out JsonElement limEl) ||
             arguments.TryGetProperty("count", out limEl) ||
             arguments.TryGetProperty("lines", out limEl) ||
             arguments.TryGetProperty("tailLines", out limEl) ||
             arguments.TryGetProperty("tail_lines", out limEl) ||
             arguments.TryGetProperty("maxLines", out limEl) ||
             arguments.TryGetProperty("max_lines", out limEl)) &&
            limEl.TryGetInt32(out int parsedLimit) && parsedLimit > 0)
        {
            limit = Math.Clamp(parsedLimit, 1, 500);
        }

        string source = "auto";
        if (arguments.ValueKind == JsonValueKind.Object &&
            arguments.TryGetProperty("source", out JsonElement srcEl) &&
            srcEl.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(srcEl.GetString()))
        {
            string rawSource = srcEl.GetString()!.Trim().ToLowerInvariant();
            source = rawSource switch
            {
                "lua_ring" or "ring" or "ring_buffer" => "ring_buffer",
                "console" or "file" or "disk" => "disk",
                _ => "auto"
            };
        }

        List<KongorClientLogEntryDto> logs = [];
        string resolvedSource = "disk";

        // 1. Attempt live in-memory ring buffer over IPC if requested and bridge is connected
        if (source is "auto" or "ring_buffer" && _ipcBridge != null && _ipcBridge.IsConnected)
        {
            try
            {
                string channelArg = channel is "ALL" or "" ? "nil" : $"'{channel}'";
                string luaCode = $@"
local ch = {channelArg}
local rawLogs = (KongorLogger and KongorLogger.GetRecentLogs and KongorLogger.GetRecentLogs(ch, {limit * 2})) or {{}}
local items = {{}}
for _, l in ipairs(rawLogs) do
    local msg = tostring(l.message or ''):gsub('""', '\\""'):gsub('\r', ''):gsub('\n', ' ')
    local det = tostring(l.details or ''):gsub('""', '\\""'):gsub('\r', ''):gsub('\n', ' ')
    table.insert(items, string.format('{{""timestamp"":""%s"",""uptime"":""%s"",""channel"":""%s"",""severity"":""%s"",""message"":""%s"",""details"":""%s""}}',
        l.timestamp or '', l.uptime or '', l.channel or '', l.severity or '', msg, det))
end
local acc = {{}}
local len = 2
for i = #items, 1, -1 do
    local it = items[i]
    if len + #it + 1 <= 32000 then
        table.insert(acc, 1, it)
        len = len + #it + 1
    else
        break
    end
end
return '[' .. table.concat(acc, ',') .. ']'
";
                string? rawIpcResponse = await _ipcBridge.ExecuteLuaAsync(luaCode.Trim(), 3000, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(rawIpcResponse) || !rawIpcResponse.TrimStart().StartsWith('['))
                {
                    string singleLineLua = "Script " + luaCode.Replace("\r", " ").Replace("\n", " ").Trim();
                    rawIpcResponse = await _ipcBridge.ExecuteCommandAsync(singleLineLua, 3000, cancellationToken).ConfigureAwait(false);
                }

                if (!string.IsNullOrWhiteSpace(rawIpcResponse) && rawIpcResponse.TrimStart().StartsWith('['))
                {
                    using var parsedDoc = JsonDocument.Parse(rawIpcResponse);
                    foreach (var item in parsedDoc.RootElement.EnumerateArray())
                    {
                        var entry = new KongorClientLogEntryDto
                        {
                            Timestamp = item.TryGetProperty("timestamp", out var tEl) ? tEl.GetString() ?? "" : "",
                            Uptime = item.TryGetProperty("uptime", out var uEl) ? uEl.GetString() ?? "" : "",
                            Channel = item.TryGetProperty("channel", out var cEl) ? cEl.GetString() ?? "" : "",
                            Severity = item.TryGetProperty("severity", out var sEl) ? sEl.GetString() ?? "" : "",
                            Message = item.TryGetProperty("message", out var mEl) ? mEl.GetString() ?? "" : "",
                            Details = item.TryGetProperty("details", out var dEl) ? dEl.GetString() ?? "" : ""
                        };

                        if (errorsOnly && !IsErrorSeverity(entry.Severity))
                        {
                            continue;
                        }

                        if (!MatchesTextFilter(entry, textFilter))
                        {
                            continue;
                        }

                        logs.Add(FormatEntryForDetail(entry, fullDetail));
                        if (logs.Count >= limit) break;
                    }
                    if (logs.Count > 0 || source == "ring_buffer")
                    {
                        resolvedSource = "ring_buffer";
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"[GetClientLogsHandler] IPC ring buffer fetch failed ({ex.Message}), falling back to disk.");
            }
        }

        // 2. Read disk logs when source is 'disk' or 'auto' (merging native K2 Err:/Warn: lines from console.log when ring_buffer also returned entries)
        if (source is "auto" or "disk")
        {
            if (_ipcBridge != null && _ipcBridge.IsConnected)
            {
                try
                {
                    await _ipcBridge.ExecuteCommandAsync("FlushLogs", 1000, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger?.Invoke($"[GetClientLogsHandler] FlushLogs failed: {ex.Message}");
                }
            }

            List<KongorClientLogEntryDto> diskLogs = ReadDiskLogs(channel, errorsOnly, textFilter, limit, fullDetail);
            if (logs.Count == 0)
            {
                logs = diskLogs;
                resolvedSource = "disk";
            }
            else if (diskLogs.Count > 0)
            {
                HashSet<string> existingMessages = new(logs.Select(l => l.Message), StringComparer.OrdinalIgnoreCase);
                foreach (KongorClientLogEntryDto diskEntry in diskLogs)
                {
                    if (existingMessages.Contains(diskEntry.Message))
                    {
                        continue;
                    }

                    bool isHighPriority = IsErrorSeverity(diskEntry.Severity) || !string.IsNullOrWhiteSpace(textFilter);
                    if (logs.Count < limit)
                    {
                        logs.Add(diskEntry);
                        existingMessages.Add(diskEntry.Message);
                    }
                    else if (isHighPriority)
                    {
                        int nonErrorIdx = logs.FindIndex(l => !IsErrorSeverity(l.Severity));
                        if (nonErrorIdx >= 0)
                        {
                            logs.RemoveAt(nonErrorIdx);
                            logs.Add(diskEntry);
                            existingMessages.Add(diskEntry.Message);
                        }
                    }
                }
            }
        }

        sw.Stop();
        double elapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2);

        if (!string.IsNullOrWhiteSpace(severityFilter) && severityFilter is "WARN" or "WARNING")
        {
            logs = logs
                .Where(l => l.Severity.Equals("WARN", StringComparison.OrdinalIgnoreCase) ||
                            l.Severity.Equals("WARNING", StringComparison.OrdinalIgnoreCase) ||
                            l.Severity.Equals("ERROR", StringComparison.OrdinalIgnoreCase) ||
                            l.Severity.Equals("FATAL", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        else if (!string.IsNullOrWhiteSpace(severityFilter) && severityFilter is "INFO")
        {
            logs = logs
                .Where(l => l.Severity.Equals("INFO", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        int errorCount = logs.Count(l => l.Severity.Equals("ERROR", StringComparison.OrdinalIgnoreCase) || l.Severity.Equals("FATAL", StringComparison.OrdinalIgnoreCase));
        int warnCount = logs.Count(l => l.Severity.Equals("WARN", StringComparison.OrdinalIgnoreCase));

        _logger?.Invoke($"[GetClientLogsHandler] Retrieved {logs.Count} logs from '{resolvedSource}' in {elapsedMs}ms (channel={channel}, errorsOnly={errorsOnly}, severity={severityFilter ?? "ALL"}, filter={textFilter ?? "none"}).");

        return ToolCallResult.Json(new
        {
            status = "success",
            success = true,
            detail = fullDetail ? "full" : "concise",
            source = resolvedSource,
            channelFilter = channel,
            severityFilter = severityFilter ?? (errorsOnly ? "ERROR" : "ALL"),
            textFilter,
            errorsOnly,
            totalLogsReturned = logs.Count,
            summary = new
            {
                errorsCount = errorCount,
                warningsCount = warnCount,
                infoCount = Math.Max(0, logs.Count - errorCount - warnCount)
            },
            logs,
            executionTimeMs = elapsedMs,
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        });
    }

    private List<KongorClientLogEntryDto> ReadDiskLogs(string channel, bool errorsOnly, string? textFilter, int limit, bool fullDetail)
    {
        var entries = new List<KongorClientLogEntryDto>();

        string userDocsLogs = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Heroes of Newerth", "game", "logs");

        string[] candidatePaths = [
            Path.Combine(_repoRoot, "games", "client", "userdata", "Heroes of Newerth x64", "override", "console.log"),
            Path.Combine(_repoRoot, "games", "client", "override", "logs", "client_lua_debug.log"),
            Path.Combine(_repoRoot, "games", "client", "userdata", "Heroes of Newerth x64", "game", "console.log"),
            Path.Combine(_repoRoot, "games", "client", "userdata", "Heroes of Newerth x64", "console.log"),
            Path.Combine(_repoRoot, "games", "client", "userdata", "logs", "native_guard_hooks.log"),
            Path.Combine(_repoRoot, "games", "client", "game", "logs", "client_lua_debug.log")
        ];

        List<string> existingFiles = candidatePaths
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();

        if (existingFiles.Count == 0 && Directory.Exists(userDocsLogs))
        {
            string? mostRecent = Directory.GetFiles(userDocsLogs, "*.log")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (mostRecent != null)
            {
                existingFiles.Add(mostRecent);
            }
        }

        foreach (string targetFile in existingFiles)
        {
            if (entries.Count >= limit)
            {
                break;
            }

            try
            {
                List<string> lines = ReadLogLinesAutoEncoding(targetFile);

                // Read in reverse order to get most recent logs
                for (int i = lines.Count - 1; i >= 0 && entries.Count < limit; i--)
                {
                    string line = lines[i];
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var match = DiskLogRegex.Match(line);
                    if (match.Success)
                    {
                        string entryChannel = match.Groups["channel"].Value.Trim().ToUpperInvariant();
                        string entrySeverity = match.Groups["severity"].Value.Trim().ToUpperInvariant();

                        if (channel != "ALL" && !entryChannel.Equals(channel, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (errorsOnly && !IsErrorSeverity(entrySeverity))
                        {
                            continue;
                        }

                        var dto = new KongorClientLogEntryDto
                        {
                            Timestamp = match.Groups["time"].Value.Trim(),
                            Uptime = match.Groups["uptime"].Success ? match.Groups["uptime"].Value.Trim() : "",
                            Channel = entryChannel,
                            Severity = entrySeverity,
                            Message = match.Groups["msg"].Value.Trim(),
                            Details = match.Groups["details"].Success ? match.Groups["details"].Value.Trim() : ""
                        };

                        if (!MatchesTextFilter(dto, textFilter))
                        {
                            continue;
                        }

                        entries.Add(FormatEntryForDetail(dto, fullDetail));
                    }
                    else
                    {
                        string trimmed = line.Trim();
                        string severity = ClassifyConsoleLineSeverity(trimmed);
                        string inferredChannel = ClassifyConsoleLineChannel(trimmed);

                        if (channel != "ALL" && !inferredChannel.Equals(channel, StringComparison.OrdinalIgnoreCase) && !trimmed.Contains(channel, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (errorsOnly && !IsErrorSeverity(severity))
                        {
                            continue;
                        }

                        var rawDto = new KongorClientLogEntryDto
                        {
                            Timestamp = "",
                            Uptime = "",
                            Channel = inferredChannel,
                            Severity = severity,
                            Message = trimmed,
                            Details = ""
                        };

                        if (!MatchesTextFilter(rawDto, textFilter))
                        {
                            continue;
                        }

                        entries.Add(FormatEntryForDetail(rawDto, fullDetail));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"[GetClientLogsHandler] Error reading disk log '{targetFile}': {ex.Message}");
            }
        }

        return entries;
    }

    private static List<string> ReadLogLinesAutoEncoding(string filePath)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (fs.Length == 0)
        {
            return [];
        }

        // Read up to the last 256 KB for fast tail reading
        long maxBytes = 256 * 1024;
        if (fs.Length > maxBytes)
        {
            long offset = fs.Length - maxBytes;
            // Align to 2-byte boundary for UTF-16LE safety
            if (offset % 2 != 0) offset++;
            fs.Seek(offset, SeekOrigin.Begin);
        }

        byte[] buffer = new byte[fs.Length - fs.Position];
        int bytesRead = fs.Read(buffer, 0, buffer.Length);
        if (bytesRead <= 0)
        {
            return [];
        }

        Encoding encoding = DetectEncoding(buffer, bytesRead);
        string content = encoding.GetString(buffer, 0, bytesRead);
        string[] rawLines = content.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        return rawLines.ToList();
    }

    private static Encoding DetectEncoding(byte[] buffer, int length)
    {
        if (length >= 2 && buffer[0] == 0xFF && buffer[1] == 0xFE)
        {
            return Encoding.Unicode;
        }
        if (length >= 2 && buffer[0] == 0xFE && buffer[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode;
        }

        int sampleLen = Math.Min(length, 128);
        int oddZeroes = 0;
        int oddCount = 0;
        for (int i = 1; i < sampleLen; i += 2)
        {
            oddCount++;
            if (buffer[i] == 0) oddZeroes++;
        }

        if (oddCount >= 4 && oddZeroes * 2 >= oddCount)
        {
            return Encoding.Unicode;
        }

        return Encoding.UTF8;
    }

    private static string ClassifyConsoleLineSeverity(string line)
    {
        if (line.StartsWith("Err:", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("[ERROR]", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("error", StringComparison.OrdinalIgnoreCase))
        {
            return "ERROR";
        }

        if (line.StartsWith("Warn:", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("[WARN]", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("warning", StringComparison.OrdinalIgnoreCase))
        {
            return "WARN";
        }

        return "INFO";
    }

    private static string ClassifyConsoleLineChannel(string line)
    {
        if (line.StartsWith("Err:", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("Warn:", StringComparison.OrdinalIgnoreCase))
        {
            return "CONSOLE";
        }
        if (line.StartsWith("Net:", StringComparison.OrdinalIgnoreCase))
        {
            return "NET";
        }
        if (line.StartsWith("Lua:", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("Script:", StringComparison.OrdinalIgnoreCase))
        {
            return "LUA";
        }
        if (line.StartsWith("UI:", StringComparison.OrdinalIgnoreCase))
        {
            return "UI";
        }
        return "RAW";
    }

    private static bool MatchesTextFilter(KongorClientLogEntryDto entry, string? textFilter)
    {
        if (string.IsNullOrWhiteSpace(textFilter))
        {
            return true;
        }

        return entry.Message.Contains(textFilter, StringComparison.OrdinalIgnoreCase) ||
               entry.Details.Contains(textFilter, StringComparison.OrdinalIgnoreCase) ||
               entry.Channel.Contains(textFilter, StringComparison.OrdinalIgnoreCase) ||
               entry.Severity.Contains(textFilter, StringComparison.OrdinalIgnoreCase);
    }

    private static KongorClientLogEntryDto FormatEntryForDetail(KongorClientLogEntryDto entry, bool fullDetail)
    {
        if (fullDetail)
        {
            return entry;
        }

        string msg = entry.Message.Length > 140 ? entry.Message[..137] + "..." : entry.Message;
        string det = entry.Details.Length > 80 ? entry.Details[..77] + "..." : entry.Details;
        return entry with { Message = msg, Details = det };
    }

    private static bool IsErrorSeverity(string severity)
    {
        return severity.Equals("ERROR", StringComparison.OrdinalIgnoreCase) ||
               severity.Equals("WARN", StringComparison.OrdinalIgnoreCase) ||
               severity.Equals("FATAL", StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepoRoot()
    {
        string? current = Directory.GetCurrentDirectory();
        while (!string.IsNullOrEmpty(current))
        {
            if (Directory.Exists(Path.Combine(current, "games")) &&
                Directory.Exists(Path.Combine(current, "src")))
            {
                return current;
            }
            current = Directory.GetParent(current)?.FullName;
        }
        return Directory.GetCurrentDirectory();
    }
}

public sealed record KongorClientLogEntryDto
{
    public string Timestamp { get; init; } = string.Empty;
    public string Uptime { get; init; } = string.Empty;
    public string Channel { get; init; } = string.Empty;
    public string Severity { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string Details { get; init; } = string.Empty;
}
