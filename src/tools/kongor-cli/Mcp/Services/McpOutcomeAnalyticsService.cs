// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Globalization;
using System.Text.Json;
using DuckDB.NET.Data;
using Parquet;
using Parquet.Serialization;

namespace Kongor.Cli.Mcp.Services;

public sealed record McpOutcomeHotspot(
    string Server,
    string Tool,
    long TotalCalls,
    long ErrorCalls,
    double SuccessRatePercent,
    double P50DurationMs,
    double P95DurationMs,
    long UncodifiedErrors);

public sealed record McpOutcomeIngestionSummary(
    bool Success,
    string ParquetOutputPath,
    long IngestedRows,
    long SkippedTestRows,
    long TotalErrors,
    double FirstCallSuccessRatePercent,
    double OverallP50DurationMs,
    double OverallP95DurationMs,
    IReadOnlyList<string> SourceFiles,
    IReadOnlyList<McpOutcomeHotspot> TopHotspots,
    string? Error = null);

/// <summary>
///     Ingests C# TAO (<c>mcp_tool_outcomes.jsonl</c>) and Python sidecar (<c>mcp_python_outcomes.ndjson</c>)
///     NDJSON telemetry streams into an Apache Parquet snapshot via <c>Parquet.Net</c> and queries reliability
///     hotspots and latency percentiles in-process using <c>DuckDB.NET</c>.
/// </summary>
public static class McpOutcomeAnalyticsService
{
    public sealed class NormalizedOutcomeRow
    {
        public string TimestampUtc { get; set; } = string.Empty;
        public string Server { get; set; } = string.Empty;
        public string Tool { get; set; } = string.Empty;
        public string Origin { get; set; } = string.Empty;
        public bool IsError { get; set; }
        public double DurationMs { get; set; }
        public string MatchedIssueId { get; set; } = string.Empty;
        public string ErrorClass { get; set; } = string.Empty;
        public string ErrorMessage { get; set; } = string.Empty;
    }

    public static async Task<McpOutcomeIngestionSummary> IngestAndAnalyzeAsync(
        string inputDirectory,
        string parquetOutputPath,
        bool includeTestOrigin = false,
        int topHotspotsLimit = 15,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(inputDirectory) || !Directory.Exists(inputDirectory))
        {
            return new McpOutcomeIngestionSummary(
                false, parquetOutputPath, 0, 0, 0, 100.0, 0, 0, [], [],
                $"Input telemetry directory '{inputDirectory}' does not exist.");
        }

        List<string> sourceFiles = [];
        foreach (string pattern in new[] { "mcp_tool_outcomes.jsonl", "mcp_python_outcomes.ndjson" })
        {
            string candidate = Path.Combine(inputDirectory, pattern);
            if (File.Exists(candidate))
            {
                sourceFiles.Add(candidate);
            }
        }

        List<NormalizedOutcomeRow> rows = [];
        long skippedTestRows = 0;

        foreach (string file in sourceFiles)
        {
            string defaultServer = file.EndsWith(".ndjson", StringComparison.OrdinalIgnoreCase) ? "python-sidecar" : "kongor";
            using StreamReader reader = new(File.Open(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            string? line;
            while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (!TryParseOutcomeLine(line, defaultServer, out NormalizedOutcomeRow? row) || row is null)
                {
                    continue;
                }

                if (!includeTestOrigin && string.Equals(row.Origin, "test", StringComparison.OrdinalIgnoreCase))
                {
                    skippedTestRows++;
                    continue;
                }

                rows.Add(row);
            }
        }

        string fullParquetPath = Path.GetFullPath(parquetOutputPath);
        string? parquetDir = Path.GetDirectoryName(fullParquetPath);
        if (!string.IsNullOrWhiteSpace(parquetDir))
        {
            Directory.CreateDirectory(parquetDir);
        }

        await WriteParquetSnapshotAsync(rows, fullParquetPath, cancellationToken).ConfigureAwait(false);

        if (rows.Count == 0)
        {
            return new McpOutcomeIngestionSummary(
                true, fullParquetPath, 0, skippedTestRows, 0, 100.0, 0, 0, sourceFiles, []);
        }

        return await QueryParquetWithDuckDbAsync(
            fullParquetPath,
            skippedTestRows,
            sourceFiles,
            Math.Clamp(topHotspotsLimit, 1, 100),
            cancellationToken).ConfigureAwait(false);
    }

    private static bool TryParseOutcomeLine(string line, string defaultServer, out NormalizedOutcomeRow? row)
    {
        row = null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            string ts = GetStringProp(root, "timestamp", "timestampUtc", "ts") ?? TimeProvider.System.GetUtcNow().ToString("O");
            string server = GetStringProp(root, "server") ?? defaultServer;
            string tool = GetStringProp(root, "targetTool", "tool", "canonicalTool", "toolName") ?? "unknown";
            string origin = GetStringProp(root, "origin") ?? "production";
            bool isError = GetBoolProp(root, "isError", "is_error") ?? false;
            double durationMs = GetDoubleProp(root, "durationMs", "duration_ms", "elapsedMs") ?? 0.0;
            string matchedIssue = GetStringProp(root, "matchedIssueId", "issueId", "issue_id") ?? string.Empty;
            string errorClass = GetStringProp(root, "errorClass", "error_class") ?? (isError ? "runtime_error" : "none");
            string errorMsg = GetStringProp(root, "errorMessage", "error", "error_message") ?? string.Empty;
            if (errorMsg.Length > 400) errorMsg = errorMsg[..400];

            row = new NormalizedOutcomeRow
            {
                TimestampUtc = ts,
                Server = server,
                Tool = tool,
                Origin = origin,
                IsError = isError,
                DurationMs = durationMs,
                MatchedIssueId = matchedIssue,
                ErrorClass = errorClass,
                ErrorMessage = errorMsg
            };
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task WriteParquetSnapshotAsync(
        IReadOnlyList<NormalizedOutcomeRow> rows,
        string parquetPath,
        CancellationToken cancellationToken)
    {
        string tempPath = parquetPath + ".tmp";
        await using (FileStream fs = File.Create(tempPath))
        {
            await ParquetSerializer.SerializeAsync(rows, fs, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        File.Move(tempPath, parquetPath, overwrite: true);
    }

    private static async Task<McpOutcomeIngestionSummary> QueryParquetWithDuckDbAsync(
        string parquetPath,
        long skippedTestRows,
        IReadOnlyList<string> sourceFiles,
        int topLimit,
        CancellationToken cancellationToken)
    {
        string escapedPath = parquetPath.Replace("\\", "/").Replace("'", "''");
        await using DuckDBConnection conn = new("DataSource=:memory:");
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using (DuckDBCommand hardenCmd = conn.CreateCommand())
        {
            hardenCmd.CommandText = "SET allow_community_extensions = false;";
            await hardenCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        long totalRows = 0;
        long totalErrors = 0;
        double overallP50 = 0;
        double overallP95 = 0;

        await using (DuckDBCommand summaryCmd = conn.CreateCommand())
        {
            summaryCmd.CommandText = $"""
                SELECT
                    COUNT(*)::BIGINT AS total_rows,
                    SUM(CASE WHEN IsError THEN 1 ELSE 0 END)::BIGINT AS error_rows,
                    COALESCE(quantile_cont(DurationMs, 0.50), 0.0) AS p50_ms,
                    COALESCE(quantile_cont(DurationMs, 0.95), 0.0) AS p95_ms
                FROM read_parquet('{escapedPath}');
                """;
            await using var reader = await summaryCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                totalRows = reader.GetInt64(0);
                totalErrors = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
                overallP50 = Math.Round(reader.GetDouble(2), 2);
                overallP95 = Math.Round(reader.GetDouble(3), 2);
            }
        }

        List<McpOutcomeHotspot> hotspots = [];
        await using (DuckDBCommand hotCmd = conn.CreateCommand())
        {
            hotCmd.CommandText = $"""
                SELECT
                    Server,
                    Tool,
                    COUNT(*)::BIGINT AS total_calls,
                    SUM(CASE WHEN IsError THEN 1 ELSE 0 END)::BIGINT AS error_calls,
                    COALESCE(quantile_cont(DurationMs, 0.50), 0.0) AS p50_ms,
                    COALESCE(quantile_cont(DurationMs, 0.95), 0.0) AS p95_ms,
                    SUM(CASE WHEN IsError AND (MatchedIssueId IS NULL OR MatchedIssueId = '') THEN 1 ELSE 0 END)::BIGINT AS uncodified_errors
                FROM read_parquet('{escapedPath}')
                GROUP BY Server, Tool
                ORDER BY error_calls DESC, total_calls DESC
                LIMIT {topLimit.ToString(CultureInfo.InvariantCulture)};
                """;
            await using var reader = await hotCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                string srv = reader.GetString(0);
                string tool = reader.GetString(1);
                long calls = reader.GetInt64(2);
                long errs = reader.IsDBNull(3) ? 0 : reader.GetInt64(3);
                double p50 = Math.Round(reader.GetDouble(4), 2);
                double p95 = Math.Round(reader.GetDouble(5), 2);
                long uncodified = reader.IsDBNull(6) ? 0 : reader.GetInt64(6);
                double rate = calls > 0 ? Math.Round(100.0 * (calls - errs) / calls, 2) : 100.0;
                hotspots.Add(new McpOutcomeHotspot(srv, tool, calls, errs, rate, p50, p95, uncodified));
            }
        }

        double successRate = totalRows > 0 ? Math.Round(100.0 * (totalRows - totalErrors) / totalRows, 2) : 100.0;
        return new McpOutcomeIngestionSummary(
            true,
            parquetPath,
            totalRows,
            skippedTestRows,
            totalErrors,
            successRate,
            overallP50,
            overallP95,
            sourceFiles,
            hotspots);
    }

    private static string? GetStringProp(JsonElement root, params string[] names)
    {
        foreach (string n in names)
        {
            if (root.TryGetProperty(n, out JsonElement el) && el.ValueKind == JsonValueKind.String)
            {
                string? val = el.GetString();
                if (!string.IsNullOrWhiteSpace(val)) return val;
            }
        }
        return null;
    }

    private static bool? GetBoolProp(JsonElement root, params string[] names)
    {
        foreach (string n in names)
        {
            if (root.TryGetProperty(n, out JsonElement el))
            {
                if (el.ValueKind == JsonValueKind.True) return true;
                if (el.ValueKind == JsonValueKind.False) return false;
            }
        }
        return null;
    }

    private static double? GetDoubleProp(JsonElement root, params string[] names)
    {
        foreach (string n in names)
        {
            if (root.TryGetProperty(n, out JsonElement el) && el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out double d))
            {
                return d;
            }
        }
        return null;
    }
}
