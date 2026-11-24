// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Text.Json;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// MCP Tool Handler for 'kongor_log_correlate' (alias: 'log_correlate').
/// Cross-Service Telemetry & Log Correlation Backplane: Correlates match events,
/// traces, errors, and latency bottlenecks across microservices and DuckDB lakehouse.
/// </summary>
public sealed class LogCorrelateHandler
{
    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _logger;
    private readonly LogCorrelateService _service;

    public LogCorrelateHandler(
        TimeProvider? timeProvider = null,
        Action<string>? logger = null,
        LogCorrelateService? service = null,
        string? repoRoot = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? (msg => Console.Error.WriteLine($"[LogCorrelate] {msg}"));
        _service = service ?? new LogCorrelateService(repoRoot, _logger, timeProvider: _timeProvider);
    }

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        Stopwatch sw = Stopwatch.StartNew();

        string action = "errors";
        if (arguments.TryGetProperty("action", out JsonElement actEl) && !string.IsNullOrWhiteSpace(actEl.GetString()))
        {
            action = actEl.GetString()!.Trim().ToLowerInvariant();
        }

        int matchId = 0;
        if ((arguments.TryGetProperty("match_id", out JsonElement mEl) ||
             arguments.TryGetProperty("matchId", out mEl)) &&
            mEl.TryGetInt32(out int mVal))
        {
            matchId = mVal;
        }

        string traceId = string.Empty;
        if ((arguments.TryGetProperty("trace_id", out JsonElement tEl) ||
             arguments.TryGetProperty("traceId", out tEl)) &&
            !string.IsNullOrWhiteSpace(tEl.GetString()))
        {
            traceId = tEl.GetString()!.Trim();
        }

        int minutes = 15;
        if ((arguments.TryGetProperty("minutes", out JsonElement minEl) ||
             arguments.TryGetProperty("window_minutes", out minEl)) &&
            minEl.TryGetInt32(out int minVal) && minVal > 0)
        {
            minutes = minVal;
        }

        int limit = 20;
        if (arguments.TryGetProperty("limit", out JsonElement limEl) && limEl.TryGetInt32(out int limVal) && limVal > 0)
        {
            limit = limVal;
        }

        _logger?.Invoke($"LogCorrelate action '{action}' requested (matchId={matchId}, traceId='{traceId}', minutes={minutes})...");

        ToolCallResult result = action switch
        {
            "match" => matchId > 0
                ? await _service.CorrelateMatchAsync(matchId, cancellationToken)
                : ToolCallResult.Error("'matchId' is required when action='match'."),
            "errors" => await _service.GetRecentErrorsAsync(minutes, cancellationToken),
            "trace" => !string.IsNullOrEmpty(traceId)
                ? await _service.GetTraceDetailsAsync(traceId, cancellationToken)
                : ToolCallResult.Error("'traceId' is required when action='trace'."),
            "latency" => await _service.GetSlowestOperationsAsync(limit, cancellationToken),
            _ => ToolCallResult.Error($"Unknown action '{action}'. Supported: match, errors, trace, latency.")
        };

        _logger?.Invoke($"LogCorrelate action '{action}' completed in {sw.ElapsedMilliseconds}ms (isError={result.IsError})");
        return result;
    }
}
