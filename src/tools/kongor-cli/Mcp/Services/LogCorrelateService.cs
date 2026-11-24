// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kongor.Cli.Mcp.Protocol;

namespace Kongor.Cli.Mcp.Services;

/// <summary>
/// Correlates telemetry, traces, and structured logs across all Project Kongor microservices:
/// MasterServer, ChatServer, WebApi, Dedicated Compel Servers, and the DuckDB lakehouse.
/// </summary>
public sealed class LogCorrelateService
{
    private readonly string _repoRoot;
    private readonly Action<string> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly DuckDbMatchAnalyticsService _lakehouseService;

    public LogCorrelateService(
        string? repoRoot = null,
        Action<string>? logger = null,
        TimeProvider? timeProvider = null,
        DuckDbMatchAnalyticsService? lakehouseService = null)
    {
        _repoRoot = repoRoot ?? Directory.GetCurrentDirectory();
        _logger = logger ?? (msg => Console.Error.WriteLine($"[LogCorrelate] {msg}"));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lakehouseService = lakehouseService ?? new DuckDbMatchAnalyticsService(_logger);
    }

    public async Task<ToolCallResult> CorrelateMatchAsync(int matchId, CancellationToken ct)
    {
        _logger($"Correlating cross-service telemetry and combat logs for Match #{matchId}...");
        var sb = new StringBuilder();
        sb.AppendLine($"# Unified Match Telemetry Report: Match #{matchId}\n");

        // 1. DuckDB Lakehouse Combat Breakdown
        try
        {
            var combatBreakdown = await _lakehouseService.GetMatchBreakdownAsync(matchId, cancellationToken: ct);
            if (combatBreakdown.Count > 0)
            {
                sb.AppendLine("### Combat Scorecard (DuckDB Lakehouse)\n");
                sb.AppendLine("| Slot | Hero ID | K/D/A | KDR | Damage | Wards | Towers |");
                sb.AppendLine("|------|---------|-------|-----|--------|-------|--------|");
                foreach (var p in combatBreakdown)
                {
                    sb.AppendLine($"| {p.PlayerIndex} | {p.HeroId} | {p.Kills}/{p.Deaths}/{p.Assists} | {p.KillDeathRatio:F2} | {p.TotalDamage:F0} | {p.WardsPlaced} | {p.TowersKilled} |");
                }
                sb.AppendLine();
            }
        }
        catch (Exception ex)
        {
            _logger($"Lakehouse combat query notice: {ex.Message}");
        }

        // 2. Cross-Service Log Correlation across logs/ directory
        string logsDir = Path.Combine(_repoRoot, "logs");
        var matchedLogEntries = new List<(string Service, string Timestamp, string Level, string Message)>();

        if (Directory.Exists(logsDir))
        {
            var logFiles = Directory.GetFiles(logsDir, "*.log");
            string matchIdStr = matchId.ToString();

            foreach (var logFile in logFiles)
            {
                string serviceName = Path.GetFileNameWithoutExtension(logFile);
                try
                {
                    using var stream = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream, Encoding.UTF8);

                    string? line;
                    while ((line = await reader.ReadLineAsync(ct)) != null)
                    {
                        if (line.Contains(matchIdStr, StringComparison.OrdinalIgnoreCase))
                        {
                            var parsed = ParseLogLine(line, serviceName);
                            matchedLogEntries.Add(parsed);
                        }
                    }
                }
                catch
                {
                    // Skip locked files
                }
            }
        }

        if (matchedLogEntries.Count > 0)
        {
            sb.AppendLine($"### Correlated Service Events ({matchedLogEntries.Count} entries found)\n");
            sb.AppendLine("| Service | Level | Message |");
            sb.AppendLine("|---------|-------|---------|");

            foreach (var entry in matchedLogEntries.Take(100))
            {
                string icon = entry.Level switch
                {
                    "ERROR" or "CRITICAL" => "[ERR]",
                    "WARN" or "WARNING" => "[WARN]",
                    _ => "[INFO]"
                };
                sb.AppendLine($"| {entry.Service} | {icon} {entry.Level} | {entry.Message} |");
            }
        }
        else
        {
            sb.AppendLine("No direct log entries found in `logs/*.log` explicitly referencing match ID.");
        }

        return ToolCallResult.Success(sb.ToString());
    }

    public async Task<ToolCallResult> GetRecentErrorsAsync(int minutes, CancellationToken ct)
    {
        _logger($"Scanning for system errors across microservices in last {minutes} minutes...");

        string logsDir = Path.Combine(_repoRoot, "logs");
        var errors = new List<(string Service, string Timestamp, string Message)>();
        DateTime cutoff = _timeProvider.GetUtcNow().UtcDateTime.AddMinutes(-minutes);

        List<string> candidateFiles = [];
        if (Directory.Exists(logsDir))
        {
            candidateFiles.AddRange(Directory.GetFiles(logsDir, "*.log"));
        }

        string willowmakerLog = Path.Combine(_repoRoot, "WILLOWMAKER.log");
        if (File.Exists(willowmakerLog)) candidateFiles.Add(willowmakerLog);

        foreach (var file in candidateFiles)
        {
            string service = Path.GetFileNameWithoutExtension(file);
            try
            {
                var fileInfo = new FileInfo(file);
                if (fileInfo.LastWriteTimeUtc < cutoff.AddMinutes(-60)) continue;

                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream, Encoding.UTF8);

                string? line;
                while ((line = await reader.ReadLineAsync(ct)) != null)
                {
                    if (line.Contains("[Error]", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("[Critical]", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("fail:", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("Exception:", StringComparison.OrdinalIgnoreCase))
                    {
                        errors.Add((service, _timeProvider.GetUtcNow().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture), line.Trim()));
                    }
                }
            }
            catch
            {
                // Skip inaccessible logs
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine($"# Recent Microservice Errors (Last {minutes}m: {errors.Count} occurrences)\n");

        if (errors.Count == 0)
        {
            sb.AppendLine("No recent error-level logs detected across services. System is healthy.");
            return ToolCallResult.Success(sb.ToString());
        }

        var grouped = errors.GroupBy(e => e.Service);
        foreach (var group in grouped)
        {
            sb.AppendLine($"### Service: `{group.Key}` ({group.Count()} errors)");
            foreach (var item in group.TakeLast(10))
            {
                sb.AppendLine($"- `{item.Message}`");
            }
            sb.AppendLine();
        }

        return ToolCallResult.Success(sb.ToString());
    }

    public async Task<ToolCallResult> GetTraceDetailsAsync(string traceId, CancellationToken ct)
    {
        _logger($"Fetching distributed trace details for '{traceId}'...");

        // 1. Try Aspire CLI
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"aspire otel spans --filter \"traceId eq '{traceId}'\" --format Json",
                WorkingDirectory = _repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc != null)
            {
                string output = await proc.StandardOutput.ReadToEndAsync(ct);
                await proc.WaitForExitAsync(ct);
                if (proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                {
                    return ToolCallResult.Success($"Distributed Trace Spans via Aspire CLI:\n\n```json\n{output}\n```");
                }
            }
        }
        catch
        {
            // Fall back to log search
        }

        // 2. Fallback: Search logs for traceId
        string logsDir = Path.Combine(_repoRoot, "logs");
        var matched = new List<string>();

        if (Directory.Exists(logsDir))
        {
            foreach (var file in Directory.GetFiles(logsDir, "*.log"))
            {
                try
                {
                    string text = await File.ReadAllTextAsync(file, ct);
                    if (text.Contains(traceId, StringComparison.OrdinalIgnoreCase))
                    {
                        var matchingLines = text.Split('\n')
                            .Where(l => l.Contains(traceId, StringComparison.OrdinalIgnoreCase))
                            .Take(20);
                        matched.AddRange(matchingLines);
                    }
                }
                catch { }
            }
        }

        if (matched.Count > 0)
        {
            return ToolCallResult.Success(
                $"Trace '{traceId}' events correlated from service logs:\n\n" +
                string.Join("\n", matched));
        }

        return ToolCallResult.Success($"No active spans or log lines found for Trace ID '{traceId}'.");
    }

    public async Task<ToolCallResult> GetSlowestOperationsAsync(int limit, CancellationToken ct)
    {
        _logger("Scanning for high-latency operations and slow spans...");

        var slowSpans = new List<(string Source, double DurationMs, string Operation)>();

        // Check logs for duration patterns: e.g. "in 123.4ms" or "executed in 450ms"
        string logsDir = Path.Combine(_repoRoot, "logs");
        if (Directory.Exists(logsDir))
        {
            var regex = new Regex(@"([0-9\.]+)\s*(ms|seconds|sec)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

            foreach (var file in Directory.GetFiles(logsDir, "*.log"))
            {
                string svc = Path.GetFileNameWithoutExtension(file);
                try
                {
                    var lines = await File.ReadAllLinesAsync(file, ct);
                    foreach (var line in lines.TakeLast(500))
                    {
                        var match = regex.Match(line);
                        if (match.Success && double.TryParse(match.Groups[1].Value, out double val))
                        {
                            string unit = match.Groups[2].Value.ToLowerInvariant();
                            double ms = unit.StartsWith("sec") ? val * 1000.0 : val;
                            if (ms > 50.0) // Filter for operations taking > 50ms
                            {
                                slowSpans.Add((svc, ms, line.Trim()));
                            }
                        }
                    }
                }
                catch { }
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine($"# High Latency Operations (>50ms)\n");

        if (slowSpans.Count == 0)
        {
            sb.AppendLine("No high latency operations detected in recent log streams.");
            return ToolCallResult.Success(sb.ToString());
        }

        sb.AppendLine("| Service | Duration (ms) | Log Excerpt |");
        sb.AppendLine("|---------|---------------|-------------|");

        foreach (var span in slowSpans.OrderByDescending(s => s.DurationMs).Take(limit))
        {
            string cleanOp = span.Operation.Length > 80 ? span.Operation[..80] + "..." : span.Operation;
            sb.AppendLine($"| {span.Source} | {span.DurationMs:F1} | {cleanOp} |");
        }

        return ToolCallResult.Success(sb.ToString());
    }

    private (string Service, string Timestamp, string Level, string Message) ParseLogLine(string line, string service)
    {
        string level = "INFO";
        if (line.Contains("[Error]", StringComparison.OrdinalIgnoreCase) || line.Contains("fail:", StringComparison.OrdinalIgnoreCase))
            level = "ERROR";
        else if (line.Contains("[Warn]", StringComparison.OrdinalIgnoreCase) || line.Contains("warn:", StringComparison.OrdinalIgnoreCase))
            level = "WARN";

        return (service, _timeProvider.GetUtcNow().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture), level, line.Trim());
    }
}
