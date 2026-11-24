// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Text.Json;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// MCP Tool Handler for 'kongor_server_manage'.
/// Provides programmatic lifecycle control (start, stop, restart, health status, port scanning, log retrieval)
/// over the Aspire microservice cluster and COMPEL dedicated game server fleet.
/// </summary>
public sealed class ServerManageHandler
{
    private readonly ILifecycleService _lifecycleService;
    private readonly INetcodeDiagnosticService _netcodeDiagnosticService;
    private readonly CompelManageHandler? _compelManageHandler;
    private readonly GameServerManageHandler? _gameServerManageHandler;
    private readonly TimeProvider _timeProvider;

    public ServerManageHandler(
        ILifecycleService? lifecycleService = null,
        TimeProvider? timeProvider = null,
        INetcodeDiagnosticService? netcodeDiagnosticService = null,
        CompelManageHandler? compelManageHandler = null,
        GameServerManageHandler? gameServerManageHandler = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lifecycleService = lifecycleService ?? new LifecycleService(_timeProvider);
        _netcodeDiagnosticService = netcodeDiagnosticService ?? new NetcodeDiagnosticService(_timeProvider);
        _compelManageHandler = compelManageHandler;
        _gameServerManageHandler = gameServerManageHandler;
    }

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        string action = "status";
        if (arguments.TryGetProperty("action", out JsonElement actionElem) && !string.IsNullOrWhiteSpace(actionElem.GetString()))
        {
            action = actionElem.GetString()!.ToLowerInvariant().Trim();
        }

        string target = arguments.TryGetProperty("target", out JsonElement targetElem) && !string.IsNullOrWhiteSpace(targetElem.GetString())
            ? targetElem.GetString()!.Trim()
            : "cluster";

        string normalizedTarget = target.ToLowerInvariant().Replace("_", "-");

        // Unified Routing: Forward to CompelManageHandler when target is compel or action is a COMPEL instance/proxy action
        if (_compelManageHandler != null && (normalizedTarget == "compel" || action == "proxy-restart" || action == "restart-proxy"))
        {
            if (action == "instances" || action == "servers" || action == "proxy-restart" || action == "restart-proxy")
            {
                return await _compelManageHandler.HandleAsync(arguments, cancellationToken).ConfigureAwait(false);
            }
        }

        // Unified Routing: Forward to GameServerManageHandler when target is a dedicated game server worker
        if (_gameServerManageHandler != null && (normalizedTarget == "game-server" || normalizedTarget == "gameserver" || normalizedTarget == "dedicated" || normalizedTarget == "worker"))
        {
            if (action == "launch" || action == "kill" || action == "discover" || action == "instances")
            {
                return await _gameServerManageHandler.HandleAsync(arguments, cancellationToken).ConfigureAwait(false);
            }
        }

        // Unified Routing: If action is instances/servers and compel handler is available, query compel instances
        if ((action == "instances" || action == "servers") && _compelManageHandler != null)
        {
            return await _compelManageHandler.HandleAsync(arguments, cancellationToken).ConfigureAwait(false);
        }

        bool withGameServers = arguments.TryGetProperty("withGameServers", out JsonElement wElem) && wElem.GetBoolean();

        int maxLines = (arguments.TryGetProperty("lines", out JsonElement lElem) && lElem.TryGetInt32(out int lVal) && lVal > 0)
            ? lVal
            : (arguments.TryGetProperty("maxLines", out JsonElement maxElem) && maxElem.TryGetInt32(out int ml) && ml > 0)
                ? ml
                : 50;

        string level = arguments.TryGetProperty("level", out JsonElement lvlElem) && !string.IsNullOrWhiteSpace(lvlElem.GetString())
            ? lvlElem.GetString()!.Trim().ToLowerInvariant()
            : "all";

        try
        {
            switch (action)
            {
                case "start":
                {
                    ClusterLifecycleResult result = await _lifecycleService.StartAsync(
                        target,
                        withGameServers,
                        null,
                        cancellationToken
                    ).ConfigureAwait(false);

                    return FormatResponse(result, "start", target);
                }

                case "stop":
                {
                    ClusterLifecycleResult result = await _lifecycleService.StopAsync(
                        target,
                        cancellationToken
                    ).ConfigureAwait(false);

                    return FormatResponse(result, "stop", target);
                }

                case "restart":
                {
                    ClusterLifecycleResult result = await _lifecycleService.RestartAsync(
                        target,
                        withGameServers,
                        null,
                        cancellationToken
                    ).ConfigureAwait(false);

                    return FormatResponse(result, "restart", target);
                }

                case "logs":
                {
                    IReadOnlyList<ServiceLogEntry> logs = await _lifecycleService.GetRecentLogsAsync(
                        target,
                        maxLines,
                        level,
                        cancellationToken).ConfigureAwait(false);

                    // Ensure final entries returned by any ILifecycleService implementation preserve newest-last order
                    IReadOnlyList<ServiceLogEntry> orderedLogs = logs
                        .OrderBy(l => l.Timestamp)
                        .TakeLast(maxLines)
                        .ToList();

                    ClusterLifecycleResult status = await _lifecycleService.GetStatusAsync(
                        target,
                        withGameServers,
                        cancellationToken
                    ).ConfigureAwait(false);

                    status.Action = "logs";
                    status.Target = target;
                    status.RecentLogs = orderedLogs;

                    return FormatResponse(status, "logs", target);
                }

                case "netcode_audit":
                case "netcode":
                {
                    int port = (arguments.TryGetProperty("port", out JsonElement pEl) && pEl.TryGetInt32(out int pVal) && pVal > 0)
                        ? pVal
                        : 11235;

                    var report = await _netcodeDiagnosticService.AuditPortNetcodeAsync(port, cancellationToken).ConfigureAwait(false);

                    return ToolCallResult.Json(new
                    {
                        status = "success",
                        action = "netcode_audit",
                        target = $"dedicated_server:{port}",
                        report = new
                        {
                            port = report.Port,
                            latencyMs = report.LatencyMs,
                            jitterMs = report.JitterMs,
                            packetLossPercentage = report.PacketLossPercentage,
                            healthLevel = report.HealthLevel.ToString(),
                            telemetrySource = report.TelemetrySource,
                            npcapAvailable = report.NpcapAvailable,
                            healthSummary = report.HealthSummary,
                            recommendedActions = report.RecommendedActions
                        },
                        timestamp = _timeProvider.GetUtcNow().ToString("o")
                    });
                }

                case "reconcile_lobby":
                {
                    string lobbyId = arguments.TryGetProperty("lobbyId", out JsonElement lIdEl) && !string.IsNullOrWhiteSpace(lIdEl.GetString())
                        ? lIdEl.GetString()!.Trim()
                        : "lobby_default";

                    var result = await _netcodeDiagnosticService.ReconcileLobbySessionAsync(lobbyId, cancellationToken).ConfigureAwait(false);

                    return ToolCallResult.Json(new
                    {
                        status = "success",
                        action = "reconcile_lobby",
                        target = $"redis_session:{lobbyId}",
                        result = new
                        {
                            lobbyId = result.LobbyId,
                            recovered = result.Recovered,
                            playerSlotsRestored = result.PlayerSlotsRestored,
                            draftState = result.DraftState,
                            recoverySummary = result.RecoverySummary
                        },
                        timestamp = _timeProvider.GetUtcNow().ToString("o")
                    });
                }

                case "arcade_idle_mode":
                case "arcade-idle-mode":
                {
                    string requestedMode = arguments.TryGetProperty("mode", out JsonElement modeEl) && !string.IsNullOrWhiteSpace(modeEl.GetString())
                        ? modeEl.GetString()!.Trim().ToLowerInvariant()
                        : (arguments.TryGetProperty("target", out JsonElement tModeEl) && !string.IsNullOrWhiteSpace(tModeEl.GetString())
                            ? tModeEl.GetString()!.Trim().ToLowerInvariant()
                            : "status");
                    (bool redisOk, string currentMode, string detail) =
                        await Kongor.Cli.Commands.RlCommand.SetOrGetIdleModeInRedisAsync(requestedMode, null, cancellationToken)
                            .ConfigureAwait(false);

                    return ToolCallResult.Json(new
                    {
                        status = redisOk ? "ok" : "error",
                        action = "arcade_idle_mode",
                        requestedMode,
                        idleMode = currentMode,
                        message = detail,
                        timestamp = _timeProvider.GetUtcNow().ToString("o")
                    });
                }

                case "status":
                default:
                {
                    ClusterLifecycleResult result = await _lifecycleService.GetStatusAsync(
                        target,
                        withGameServers,
                        cancellationToken
                    ).ConfigureAwait(false);

                    return FormatResponse(result, "status", target);
                }
            }
        }
        catch (Exception ex)
        {
            var errorResponse = new
            {
                status = "error",
                action,
                target,
                clusterState = "Faulted",
                dashboardUrl = "https://localhost:5550",
                message = $"Error executing '{action}' on target '{target}': {ex.Message}",
                services = new Dictionary<string, object>(),
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            };

            return ToolCallResult.Json(errorResponse);
        }
    }

    private ToolCallResult FormatResponse(ClusterLifecycleResult result, string action, string target)
    {
        var servicesDict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, ServiceStatusInfo> kvp in result.Services)
        {
            servicesDict[kvp.Key] = new
            {
                port = kvp.Value.Port,
                state = kvp.Value.State,
                processId = kvp.Value.ProcessId,
                responding = kvp.Value.Responding,
                additionalInfo = kvp.Value.AdditionalInfo
            };
        }

        string nextAction = result.ClusterState switch
        {
            "Running" => "Cluster is running and healthy.",
            "Starting" => "Cluster is starting up. Wait a few seconds and query 'kongor_server_manage action:status'.",
            "Degraded" => "Cluster is running with degraded services. Inspect offline services or restart via 'kongor_server_manage action:restart'.",
            "Partial" => "Auxiliary services are active, but core MasterServer/ChatServer are offline. Run 'kongor_server_manage action:start target:cluster'.",
            "Stopped" => "Cluster is stopped. Run 'kongor_server_manage action:start target:cluster' to launch all microservices.",
            _ => "Run 'kongor_server_manage action:start' or inspect recent logs if issues persist."
        };

        var response = new
        {
            status = result.Status,
            action = action,
            target = target,
            clusterState = result.ClusterState,
            dashboardUrl = result.DashboardUrl,
            services = servicesDict,
            recentLogs = result.RecentLogs,
            nextAction,
            message = result.Message,
            timestamp = !string.IsNullOrWhiteSpace(result.Timestamp) ? result.Timestamp : _timeProvider.GetUtcNow().ToString("o")
        };

        return ToolCallResult.Json(response);
    }
}
