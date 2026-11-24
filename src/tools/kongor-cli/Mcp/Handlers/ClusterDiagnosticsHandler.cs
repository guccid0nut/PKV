// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Text.Json;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// MCP Tool Handler for 'kongor_cluster_diagnostics' and 'kongor_diagnostics'.
/// Probes Redis (port 28298, ping latency, auth), Supabase/MERRICK PostgreSQL DB,
/// service endpoints (55555, 11031, 5200, 5266, 11234), and aggregates recent fatal/error logs.
/// </summary>
public sealed class ClusterDiagnosticsHandler
{
    private readonly IClusterDiagnosticsService _diagnosticsService;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _logAction;

    public ClusterDiagnosticsHandler(
        IClusterDiagnosticsService? diagnosticsService = null,
        TimeProvider? timeProvider = null,
        Action<string>? logAction = null)
    {
        _diagnosticsService = diagnosticsService ?? new ClusterDiagnosticsService(logAction);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logAction = logAction;
    }

    private void Log(string message)
    {
        _logAction?.Invoke($"[ClusterDiagnosticsHandler] {message}");
    }

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        string? redisHost = arguments.TryGetProperty("redisHost", out JsonElement rh) ? rh.GetString() : null;

        int? redisPort = null;
        if (arguments.TryGetProperty("redisPort", out JsonElement rp) && rp.TryGetInt32(out int parsedPort) && parsedPort > 0)
        {
            redisPort = parsedPort;
        }

        string? dbConnectionString = arguments.TryGetProperty("dbConnectionString", out JsonElement db) ? db.GetString() : null;

        int maxLogErrors = 5;
        if (arguments.TryGetProperty("maxLogErrors", out JsonElement mle) && mle.TryGetInt32(out int parsedMax) && parsedMax > 0)
        {
            maxLogErrors = parsedMax;
        }

        Log($"Running cluster diagnostics: redis={redisHost ?? "127.0.0.1"}:{redisPort ?? 28298}, maxErrors={maxLogErrors}");

        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            ClusterDiagnosticsReport report = await _diagnosticsService.RunDiagnosticsAsync(
                redisHost,
                redisPort,
                dbConnectionString,
                maxLogErrors,
                repoRoot: null,
                cancellationToken).ConfigureAwait(false);
            sw.Stop();

            var response = new
            {
                overallStatus = report.OverallStatus,
                timestamp = _timeProvider.GetUtcNow().ToString("o"),
                totalProbeDurationMs = report.TotalProbeDurationMs,
                nextAction = report.NextAction,
                remediationHints = report.RemediationHints,
                redis = new
                {
                    connected = report.Redis.Connected,
                    host = report.Redis.Host,
                    port = report.Redis.Port,
                    latencyMs = report.Redis.LatencyMs,
                    authState = report.Redis.AuthState,
                    version = report.Redis.Version,
                    connectedClients = report.Redis.ConnectedClients,
                    usedMemoryHuman = report.Redis.UsedMemoryHuman,
                    totalKeys = report.Redis.TotalKeys,
                    evictedKeys = report.Redis.EvictedKeys,
                    usedMemoryBytes = report.Redis.UsedMemoryBytes,
                    maxMemoryBytes = report.Redis.MaxMemoryBytes,
                    evictionWarning = report.Redis.EvictionWarning,
                    error = report.Redis.Error
                },
                masterServer = new
                {
                    connected = report.MasterServer.Connected,
                    endpoint = report.MasterServer.Endpoint,
                    authenticatedSessions = report.MasterServer.AuthenticatedSessions,
                    loginApiResponding = report.MasterServer.LoginApiResponding,
                    latencyMs = report.MasterServer.LatencyMs,
                    error = report.MasterServer.Error
                },
                compel = new
                {
                    connected = report.CompelPool.Connected,
                    endpoint = report.CompelPool.Endpoint,
                    supervisorRunning = report.CompelPool.SupervisorRunning,
                    totalInstances = report.CompelPool.TotalInstances,
                    idleWorkerSlots = report.CompelPool.IdleWorkerSlots,
                    activeMatchIds = report.CompelPool.ActiveMatchIds,
                    averageTickLatencyMs = report.CompelPool.AverageTickLatencyMs,
                    tickRateHz = report.CompelPool.TickRateHz,
                    latencyMs = report.CompelPool.LatencyMs,
                    error = report.CompelPool.Error
                },
                database = new
                {
                    connected = report.Database.Connected,
                    host = report.Database.Host,
                    port = report.Database.Port,
                    database = report.Database.Database,
                    isSupabase = report.Database.IsSupabase,
                    latencyMs = report.Database.LatencyMs,
                    error = report.Database.Error
                },
                endpoints = report.Endpoints.Select(e => new
                {
                    name = e.Name,
                    url = e.Url,
                    port = e.Port,
                    connected = e.Connected,
                    httpStatusCode = e.HttpStatusCode,
                    latencyMs = e.LatencyMs,
                    error = e.Error
                }).ToList(),
                recentErrors = report.RecentErrors.Select(err => new
                {
                    sourceFile = err.SourceFile,
                    level = err.Level,
                    timestamp = err.Timestamp,
                    message = err.Message
                }).ToList(),
                executionTimeMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2)
            };

            bool enableSampling = (arguments.TryGetProperty("enable_sampling", out JsonElement esEl) && esEl.ValueKind == JsonValueKind.True) ||
                                  (arguments.TryGetProperty("ai_diagnose", out JsonElement adEl) && adEl.ValueKind == JsonValueKind.True);

            string? aiDiagnosis = null;
            if (enableSampling && McpSamplingContext.Current?.IsSamplingSupported == true)
            {
                string errorSummary = report.RecentErrors.Count > 0
                    ? string.Join("\n", report.RecentErrors.Select(e => $"[{e.Level}] {e.SourceFile}: {e.Message}"))
                    : $"Cluster Status: {report.OverallStatus}, NextAction: {report.NextAction}";
                aiDiagnosis = await McpSamplingContext.Current.RequestCrashAnalysisAsync(
                    report.OverallStatus,
                    errorSummary,
                    cancellationToken).ConfigureAwait(false);
            }

            var enrichedResponse = new
            {
                response.overallStatus,
                response.timestamp,
                response.totalProbeDurationMs,
                response.nextAction,
                response.remediationHints,
                response.redis,
                response.masterServer,
                response.compel,
                response.database,
                response.endpoints,
                response.recentErrors,
                aiDiagnosis,
                samplingRequested = enableSampling,
                samplingSupported = McpSamplingContext.Current?.IsSamplingSupported ?? false,
                response.executionTimeMs
            };

            return ToolCallResult.Json(enrichedResponse);
        }
        catch (Exception ex)
        {
            sw.Stop();
            Log($"Error executing cluster diagnostics: {ex.Message}");
            return ToolCallResult.Json(new
            {
                overallStatus = "unhealthy",
                error = ex.Message,
                executionTimeMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            });
        }
    }
}
