// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Text.Json;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Kongor.Core.Analytics;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// MCP Tool Handler for 'kongor_match_analytics' (aliases: 'match_analytics', 'kongor_lakehouse_query').
/// Provides high-speed in-process DuckDB analytical SQL queries over Apache Parquet combat event archives.
/// Computes player summaries, 10-player match breakdowns, spatial minimap heatmaps, and hero matchups.
/// </summary>
public sealed class MatchAnalyticsHandler : IDisposable
{
    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _logger;
    private readonly DuckDbMatchAnalyticsService _analyticsService;

    public MatchAnalyticsHandler(
        TimeProvider? timeProvider = null,
        Action<string>? logger = null,
        DuckDbMatchAnalyticsService? analyticsService = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? (msg => Console.Error.WriteLine($"[MatchAnalytics] {msg}"));
        _analyticsService = analyticsService ?? new DuckDbMatchAnalyticsService(_logger);
    }

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        Stopwatch sw = Stopwatch.StartNew();

        string action = "list_files";
        if (arguments.TryGetProperty("action", out JsonElement actEl) && !string.IsNullOrWhiteSpace(actEl.GetString()))
        {
            action = actEl.GetString()!.Trim().ToLowerInvariant();
        }

        string? parquetPath = null;
        if ((arguments.TryGetProperty("parquet_path", out JsonElement pathEl) ||
             arguments.TryGetProperty("parquetPath", out pathEl) ||
             arguments.TryGetProperty("file_path", out pathEl) ||
             arguments.TryGetProperty("path", out pathEl)) &&
            !string.IsNullOrWhiteSpace(pathEl.GetString()))
        {
            parquetPath = pathEl.GetString()!.Trim();
        }

        _logger?.Invoke($"Match analytics requested (Action: '{action}', Path: '{parquetPath ?? "auto"}')...");

        switch (action)
        {
            case "query_sql":
            case "sql":
            case "query":
                return await HandleQuerySqlAsync(arguments, parquetPath, sw, cancellationToken).ConfigureAwait(false);

            case "player_summary":
            case "player":
            case "summary":
                return await HandlePlayerSummaryAsync(arguments, parquetPath, sw, cancellationToken).ConfigureAwait(false);

            case "match_breakdown":
            case "breakdown":
            case "match":
                return await HandleMatchBreakdownAsync(arguments, parquetPath, sw, cancellationToken).ConfigureAwait(false);

            case "heatmap":
            case "spatial":
                return await HandleSpatialHeatmapAsync(arguments, parquetPath, sw, cancellationToken).ConfigureAwait(false);

            case "hero_matchup":
            case "matchup":
            case "matrix":
                return await HandleHeroMatchupAsync(arguments, parquetPath, sw, cancellationToken).ConfigureAwait(false);

            case "list_files":
            case "files":
            case "list":
                return HandleListFiles(arguments, sw);

            default:
                sw.Stop();
                return ToolCallResult.Error(
                    $"Unknown action '{action}'. Valid actions are: 'query_sql', 'player_summary', 'match_breakdown', 'heatmap', 'hero_matchup', 'list_files'.");
        }
    }

    private async Task<ToolCallResult> HandleQuerySqlAsync(
        JsonElement arguments,
        string? parquetPath,
        Stopwatch sw,
        CancellationToken cancellationToken)
    {
        string sql = string.Empty;
        if ((arguments.TryGetProperty("sql", out JsonElement sqlEl) ||
             arguments.TryGetProperty("query", out sqlEl)) &&
            !string.IsNullOrWhiteSpace(sqlEl.GetString()))
        {
            sql = sqlEl.GetString()!.Trim();
        }

        if (string.IsNullOrWhiteSpace(sql))
        {
            sw.Stop();
            return ToolCallResult.Error("Action 'query_sql' requires a non-empty 'sql' parameter.");
        }

        int maxRows = 100;
        if ((arguments.TryGetProperty("limit", out JsonElement limEl) ||
             arguments.TryGetProperty("max_rows", out limEl) ||
             arguments.TryGetProperty("count", out limEl)) &&
            limEl.TryGetInt32(out int l) && l > 0)
        {
            maxRows = Math.Clamp(l, 1, 5000);
        }

        MatchAnalyticsQueryResult queryResult = await _analyticsService.ExecuteQueryAsync(
            sql, parquetPath, maxRows, cancellationToken).ConfigureAwait(false);

        sw.Stop();

        if (!queryResult.Success)
        {
            return ToolCallResult.Error($"DuckDB query execution failed: {queryResult.Error}");
        }

        return ExecutionTruthEnvelope.CreateJsonResult(
            status: "success",
            executionMode: ExecutionMode.OfflineDirect,
            targetStatus: new TargetStatus
            {
                Client = new ClientLivenessStatus { Running = true, EngineReady = true },
                Server = new ServerLivenessStatus { Running = true, Port = 11235 }
            },
            metrics: new ExecutionMetrics
            {
                RoundtripLatencyMs = sw.Elapsed.TotalMilliseconds,
                EngineRenderFrame = 0,
                Timestamp = _timeProvider.GetUtcNow().ToString("o")
            },
            data: new
            {
                action = "query_sql",
                sql = queryResult.Query,
                rowCount = queryResult.RowCount,
                columns = queryResult.Columns,
                rows = queryResult.Rows,
                duckDbDurationMs = queryResult.ExecutionDurationMs,
                parquetSource = queryResult.ParquetSource
            });
    }

    private async Task<ToolCallResult> HandlePlayerSummaryAsync(
        JsonElement arguments,
        string? parquetPath,
        Stopwatch sw,
        CancellationToken cancellationToken)
    {
        int accountId = 1;
        if ((arguments.TryGetProperty("account_id", out JsonElement accEl) ||
             arguments.TryGetProperty("accountId", out accEl)) &&
            accEl.TryGetInt32(out int aId))
        {
            accountId = aId;
        }

        long? matchId = null;
        if ((arguments.TryGetProperty("match_id", out JsonElement mEl) ||
             arguments.TryGetProperty("matchId", out mEl)) &&
            mEl.TryGetInt64(out long mVal))
        {
            matchId = mVal;
        }

        byte? playerIndex = null;
        if ((arguments.TryGetProperty("player_index", out JsonElement pEl) ||
             arguments.TryGetProperty("playerIndex", out pEl) ||
             arguments.TryGetProperty("slot", out pEl)) &&
            pEl.TryGetByte(out byte pVal))
        {
            playerIndex = pVal;
        }

        short? heroId = null;
        if ((arguments.TryGetProperty("hero_id", out JsonElement hEl) ||
             arguments.TryGetProperty("heroId", out hEl)) &&
            hEl.TryGetInt16(out short hVal))
        {
            heroId = hVal;
        }

        PlayerCombatSummaryResult? summary = await _analyticsService.GetPlayerSummaryAsync(
            accountId, matchId, playerIndex, heroId, parquetPath, cancellationToken).ConfigureAwait(false);

        sw.Stop();

        if (summary == null)
        {
            return ToolCallResult.Error(
                $"No combat telemetry found for Account #{accountId}" +
                (matchId.HasValue ? $" in Match #{matchId.Value}." : "."));
        }

        List<ToolHandoff> handoffs =
        [
            ToolHandoff.Create(
                server: "redis",
                tool: "hgetall",
                reason: $"Cross-verify cached career statistics for Account #{accountId}",
                arguments: new { name = $"player:{accountId}:stats" })
        ];

        return ExecutionTruthEnvelope.CreateJsonResult(
            status: "success",
            executionMode: ExecutionMode.OfflineDirect,
            targetStatus: new TargetStatus
            {
                Client = new ClientLivenessStatus { Running = true, EngineReady = true },
                Server = new ServerLivenessStatus { Running = true, Port = 11235 }
            },
            metrics: new ExecutionMetrics
            {
                RoundtripLatencyMs = sw.Elapsed.TotalMilliseconds,
                EngineRenderFrame = 0,
                Timestamp = _timeProvider.GetUtcNow().ToString("o")
            },
            data: new
            {
                action = "player_summary",
                summary
            },
            handoffs: handoffs);
    }

    private async Task<ToolCallResult> HandleMatchBreakdownAsync(
        JsonElement arguments,
        string? parquetPath,
        Stopwatch sw,
        CancellationToken cancellationToken)
    {
        long matchId = 1001;
        if ((arguments.TryGetProperty("match_id", out JsonElement mEl) ||
             arguments.TryGetProperty("matchId", out mEl)) &&
            mEl.TryGetInt64(out long mVal))
        {
            matchId = mVal;
        }

        IReadOnlyList<MatchCombatPlayerBreakdownResult> players = await _analyticsService.GetMatchBreakdownAsync(
            matchId, parquetPath, cancellationToken).ConfigureAwait(false);

        sw.Stop();

        List<ToolHandoff> handoffs =
        [
            ToolHandoff.Create(
                server: "playwright",
                tool: "browser_navigate",
                reason: $"View full match scorecards and replay timeline for Match #{matchId}",
                arguments: new { url = $"http://localhost:5266/analytics/match/{matchId}" })
        ];

        return ExecutionTruthEnvelope.CreateJsonResult(
            status: "success",
            executionMode: ExecutionMode.OfflineDirect,
            targetStatus: new TargetStatus
            {
                Client = new ClientLivenessStatus { Running = true, EngineReady = true },
                Server = new ServerLivenessStatus { Running = true, Port = 11235 }
            },
            metrics: new ExecutionMetrics
            {
                RoundtripLatencyMs = sw.Elapsed.TotalMilliseconds,
                EngineRenderFrame = 0,
                Timestamp = _timeProvider.GetUtcNow().ToString("o")
            },
            data: new
            {
                action = "match_breakdown",
                matchId,
                playerCount = players.Count,
                players
            },
            handoffs: handoffs);
    }

    private async Task<ToolCallResult> HandleSpatialHeatmapAsync(
        JsonElement arguments,
        string? parquetPath,
        Stopwatch sw,
        CancellationToken cancellationToken)
    {
        long matchId = 1001;
        if ((arguments.TryGetProperty("match_id", out JsonElement mEl) ||
             arguments.TryGetProperty("matchId", out mEl)) &&
            mEl.TryGetInt64(out long mVal))
        {
            matchId = mVal;
        }

        CombatEventType? eventType = null;
        if (arguments.TryGetProperty("event_type", out JsonElement etEl) ||
            arguments.TryGetProperty("eventType", out etEl))
        {
            if (etEl.ValueKind == JsonValueKind.Number && etEl.TryGetInt32(out int etVal) && Enum.IsDefined(typeof(CombatEventType), (byte)etVal))
            {
                eventType = (CombatEventType)(byte)etVal;
            }
            else if (etEl.ValueKind == JsonValueKind.String &&
                     Enum.TryParse<CombatEventType>(etEl.GetString(), ignoreCase: true, out CombatEventType parsed))
            {
                eventType = parsed;
            }
        }

        int bucketSize = 100;
        if ((arguments.TryGetProperty("bucket_size", out JsonElement bsEl) ||
             arguments.TryGetProperty("bucketSize", out bsEl)) &&
            bsEl.TryGetInt32(out int bsVal) && bsVal > 0)
        {
            bucketSize = Math.Clamp(bsVal, 20, 1000);
        }

        IReadOnlyList<SpatialHeatmapPoint> points = await _analyticsService.GetSpatialHeatmapAsync(
            matchId, eventType, parquetPath, bucketSize, cancellationToken).ConfigureAwait(false);

        sw.Stop();

        List<ToolHandoff> handoffs =
        [
            ToolHandoff.Create(
                server: "playwright",
                tool: "browser_snapshot",
                reason: "Capture DOM snapshot of minimap canvas to verify spatial heatmap alignment",
                arguments: new { selector = "#minimap" })
        ];

        return ExecutionTruthEnvelope.CreateJsonResult(
            status: "success",
            executionMode: ExecutionMode.OfflineDirect,
            targetStatus: new TargetStatus
            {
                Client = new ClientLivenessStatus { Running = true, EngineReady = true },
                Server = new ServerLivenessStatus { Running = true, Port = 11235 }
            },
            metrics: new ExecutionMetrics
            {
                RoundtripLatencyMs = sw.Elapsed.TotalMilliseconds,
                EngineRenderFrame = 0,
                Timestamp = _timeProvider.GetUtcNow().ToString("o")
            },
            data: new
            {
                action = "heatmap",
                matchId,
                eventType = eventType?.ToString() ?? "AllCombatEvents",
                bucketSize,
                pointCount = points.Count,
                heatmapPoints = points
            },
            handoffs: handoffs);
    }

    private async Task<ToolCallResult> HandleHeroMatchupAsync(
        JsonElement arguments,
        string? parquetPath,
        Stopwatch sw,
        CancellationToken cancellationToken)
    {
        short heroId = 1;
        if ((arguments.TryGetProperty("hero_id", out JsonElement hEl) ||
             arguments.TryGetProperty("heroId", out hEl)) &&
            hEl.TryGetInt16(out short hVal))
        {
            heroId = hVal;
        }

        short? opponentHeroId = null;
        if ((arguments.TryGetProperty("opponent_hero_id", out JsonElement ohEl) ||
             arguments.TryGetProperty("opponentHeroId", out ohEl)) &&
            ohEl.TryGetInt16(out short ohVal))
        {
            opponentHeroId = ohVal;
        }

        IReadOnlyList<HeroMatchupResult> matchups = await _analyticsService.GetHeroMatchupMatrixAsync(
            heroId, opponentHeroId, parquetPath, cancellationToken).ConfigureAwait(false);

        sw.Stop();

        return ExecutionTruthEnvelope.CreateJsonResult(
            status: "success",
            executionMode: ExecutionMode.OfflineDirect,
            targetStatus: new TargetStatus
            {
                Client = new ClientLivenessStatus { Running = true, EngineReady = true },
                Server = new ServerLivenessStatus { Running = true, Port = 11235 }
            },
            metrics: new ExecutionMetrics
            {
                RoundtripLatencyMs = sw.Elapsed.TotalMilliseconds,
                EngineRenderFrame = 0,
                Timestamp = _timeProvider.GetUtcNow().ToString("o")
            },
            data: new
            {
                action = "hero_matchup",
                heroId,
                opponentHeroId,
                matchupCount = matchups.Count,
                matchups
            });
    }

    private ToolCallResult HandleListFiles(JsonElement arguments, Stopwatch sw)
    {
        string? directoryPath = null;
        if ((arguments.TryGetProperty("directory_path", out JsonElement dirEl) ||
             arguments.TryGetProperty("directory", out dirEl)) &&
            !string.IsNullOrWhiteSpace(dirEl.GetString()))
        {
            directoryPath = dirEl.GetString()!.Trim();
        }

        IReadOnlyList<ParquetFileInfo> files = _analyticsService.ListParquetFiles(directoryPath);
        sw.Stop();

        return ExecutionTruthEnvelope.CreateJsonResult(
            status: "success",
            executionMode: ExecutionMode.OfflineDirect,
            targetStatus: new TargetStatus
            {
                Client = new ClientLivenessStatus { Running = true, EngineReady = true },
                Server = new ServerLivenessStatus { Running = true, Port = 11235 }
            },
            metrics: new ExecutionMetrics
            {
                RoundtripLatencyMs = sw.Elapsed.TotalMilliseconds,
                EngineRenderFrame = 0,
                Timestamp = _timeProvider.GetUtcNow().ToString("o")
            },
            data: new
            {
                action = "list_files",
                fileCount = files.Count,
                files
            });
    }

    public void Dispose()
    {
        _analyticsService.Dispose();
    }
}
