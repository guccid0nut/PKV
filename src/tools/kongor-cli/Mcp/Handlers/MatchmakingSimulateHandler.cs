// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// MCP Tool Handler for 'kongor_matchmaking_simulate' (aliases: 'kongor_tmm_simulate', 'kongor_matchmaking_bench').
/// Executes headless TCP matchmaking simulation with up to 10 virtual clients against ChatServer,
/// tracking connection, handshake, and queue placement latencies, and reporting match assignment details.
/// </summary>
public sealed class MatchmakingSimulateHandler
{
    private readonly IMatchmakingSimulateService _simulationService;
    private readonly IClientCommandRemediationService _remediationService;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _logger;

    public MatchmakingSimulateHandler(
        IMatchmakingSimulateService? simulationService = null,
        IClientCommandRemediationService? remediationService = null,
        TimeProvider? timeProvider = null,
        Action<string>? logger = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
        _remediationService = remediationService ?? new ClientCommandRemediationService(timeProvider: _timeProvider);
        _simulationService = simulationService ?? new MatchmakingSimulateService(timeProvider: _timeProvider, logger: logger);
    }

    private void Log(string message)
    {
        _logger?.Invoke($"[MatchmakingSimulateHandler] {message}");
    }

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        Stopwatch sw = Stopwatch.StartNew();
        Log("Handling matchmaking simulate request");

        // 1. Flexible parameter extraction
        int clientCount = ExtractClientCount(arguments);
        int? serverPort = ExtractServerPort(arguments);
        string serverHost = ExtractServerHost(arguments);
        string queueType = ExtractQueueType(arguments);
        string gameType = ExtractGameType(arguments);
        string mapName = ExtractMapName(arguments);
        string gameMode = ExtractGameMode(arguments);
        string region = ExtractRegion(arguments);
        int timeoutSeconds = ExtractTimeoutSeconds(arguments);
        bool simulateMatchServer = ExtractSimulateMatchServer(arguments);

        // 2. Input validation with remediation
        if (serverPort.HasValue && (serverPort.Value < 1 || serverPort.Value > 65535))
        {
            string err = $"Invalid serverPort '{serverPort.Value}'. Port must be between 1 and 65535.";
            Log($"Validation failure: {err}");
            _remediationService.RecordAndRemediate(
                $"kongor_matchmaking_simulate port:{serverPort.Value}",
                err, "mcp", "server");

            return ToolCallResult.Json(new
            {
                status = "error",
                success = false,
                error = err,
                remediationCategory = "MalformedCommandArguments",
                remediationHint = "Provide a valid TCP port number between 1 and 65535, or omit 'serverPort' to utilize automatic tiered port discovery (Aspire environment -> fallback 11031).",
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            }, isError: true);
        }

        if (timeoutSeconds <= 0)
        {
            string err = $"Invalid timeoutSeconds '{timeoutSeconds}'. Timeout must be greater than 0.";
            Log($"Validation failure: {err}");
            _remediationService.RecordAndRemediate(
                $"kongor_matchmaking_simulate timeout:{timeoutSeconds}",
                err, "mcp", "server");

            return ToolCallResult.Json(new
            {
                status = "error",
                success = false,
                error = err,
                remediationCategory = "MalformedCommandArguments",
                remediationHint = "The 'timeoutSeconds' parameter must be a positive integer (default is 15 seconds).",
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            }, isError: true);
        }

        MatchmakingSimulateOptions options = new()
        {
            ClientCount = clientCount,
            ServerPort = serverPort,
            ServerHost = serverHost,
            QueueType = queueType,
            GameType = gameType,
            MapName = mapName,
            GameMode = gameMode,
            Region = region,
            TimeoutSeconds = timeoutSeconds,
            SimulateMatchServer = simulateMatchServer
        };

        try
        {
            MatchmakingSimulateResult result = await _simulationService.SimulateMatchmakingAsync(options, cancellationToken).ConfigureAwait(false);
            sw.Stop();

            if (result.Status != "success")
            {
                string failedTarget = $"kongor_matchmaking_simulate host:{serverHost} port:{result.ResolvedPort}";
                string errMsg = result.ErrorMessage ?? "Matchmaking simulation failed.";
                var rec = _remediationService.RecordAndRemediate(failedTarget, errMsg, "tcp", "server");

                return ToolCallResult.Json(new
                {
                    status = "error",
                    success = false,
                    resolvedPort = result.ResolvedPort,
                    discoveryTier = result.DiscoveryTier,
                    clientCount = result.ClientCount,
                    successfulConnections = result.SuccessfulConnections,
                    handshakeCount = result.HandshakeCount,
                    queueJoinedCount = result.QueueJoinedCount,
                    latency = result.Latency,
                    clients = result.Clients,
                    socketsCleaned = result.SocketsCleaned,
                    error = errMsg,
                    remediationCategory = result.RemediationCategory ?? rec.Remediation.ErrorCategory,
                    remediationHint = result.RemediationHint ?? rec.Remediation.RemediationHint,
                    executionTimeMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                    timestamp = _timeProvider.GetUtcNow().ToString("o")
                }, isError: true);
            }

            return ToolCallResult.Json(new
            {
                status = "success",
                success = true,
                resolvedPort = result.ResolvedPort,
                discoveryTier = result.DiscoveryTier,
                clientCount = result.ClientCount,
                successfulConnections = result.SuccessfulConnections,
                handshakeCount = result.HandshakeCount,
                queueJoinedCount = result.QueueJoinedCount,
                latency = result.Latency,
                matchAssignment = result.MatchAssignment,
                clients = result.Clients,
                socketsCleaned = result.SocketsCleaned,
                totalDurationMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                executionTimeMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                message = $"Successfully connected {result.SuccessfulConnections}/{result.ClientCount} clients, completed {result.HandshakeCount} handshakes, and queued {result.QueueJoinedCount} players.",
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            });
        }
        catch (SocketException sex)
        {
            sw.Stop();
            Log($"Socket error during simulation: {sex.Message}");
            (int resolvedPort, string discoveryTier) = _simulationService.ResolveChatServerPort(serverPort);
            string err = $"Unable to connect to ChatServer at {serverHost}:{resolvedPort}. {sex.Message}";
            _remediationService.RecordAndRemediate(
                $"kongor_matchmaking_simulate host:{serverHost} port:{resolvedPort}",
                sex.Message, "tcp", "server");

            return ToolCallResult.Json(new
            {
                status = "error",
                success = false,
                resolvedPort,
                discoveryTier,
                clientCount,
                successfulConnections = 0,
                handshakeCount = 0,
                queueJoinedCount = 0,
                error = err,
                remediationCategory = "ChatServerOffline",
                remediationHint = $"ChatServer is not listening on {serverHost}:{resolvedPort}. Verify ChatServer is running via 'kongor_server_manage action:status' or start it with 'kongor_server_manage action:start target:chat-server'.",
                executionTimeMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            }, isError: true);
        }
        catch (TimeoutException tex)
        {
            sw.Stop();
            Log($"Timeout during simulation: {tex.Message}");
            (int resolvedPort, string discoveryTier) = _simulationService.ResolveChatServerPort(serverPort);
            string err = $"Matchmaking simulation timed out after {timeoutSeconds}s waiting for handshake or queue responses.";
            _remediationService.RecordAndRemediate(
                $"kongor_matchmaking_simulate timeout:{timeoutSeconds}",
                tex.Message, "tcp", "server");

            return ToolCallResult.Json(new
            {
                status = "error",
                success = false,
                resolvedPort,
                discoveryTier,
                clientCount,
                error = err,
                remediationCategory = "SimulationTimeout",
                remediationHint = $"The simulation timed out after {timeoutSeconds}s. Inspect ChatServer logs via 'kongor_server_manage action:logs target:chat-server' or increase 'timeoutSeconds'.",
                executionTimeMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            }, isError: true);
        }
        catch (Exception ex)
        {
            sw.Stop();
            Log($"Unhandled exception during simulation: {ex.Message}");
            (int resolvedPort, string discoveryTier) = _simulationService.ResolveChatServerPort(serverPort);
            string err = $"Simulation failed: {ex.Message}";
            var rec = _remediationService.RecordAndRemediate(
                $"kongor_matchmaking_simulate clients:{clientCount}",
                ex.Message, "mcp", "server");

            return ToolCallResult.Json(new
            {
                status = "error",
                success = false,
                resolvedPort,
                discoveryTier,
                clientCount,
                error = err,
                remediationCategory = rec.Remediation.ErrorCategory,
                remediationHint = rec.Remediation.RemediationHint,
                executionTimeMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            }, isError: true);
        }
    }

    #region Parameter Extraction Helpers

    private static int ExtractClientCount(JsonElement arguments)
    {
        string[] candidateKeys = ["clientCount", "client_count", "clients", "Clients", "count", "numClients"];
        foreach (string key in candidateKeys)
        {
            if (arguments.TryGetProperty(key, out JsonElement elem))
            {
                if (elem.ValueKind == JsonValueKind.Number && elem.TryGetInt32(out int val))
                {
                    return Math.Clamp(val, 1, 10);
                }
                if (elem.ValueKind == JsonValueKind.String && int.TryParse(elem.GetString(), out int parsed))
                {
                    return Math.Clamp(parsed, 1, 10);
                }
            }
        }
        return 10;
    }

    private static int? ExtractServerPort(JsonElement arguments)
    {
        string[] candidateKeys = ["serverPort", "server_port", "port", "Port", "chatServerPort", "chat_server_port"];
        foreach (string key in candidateKeys)
        {
            if (arguments.TryGetProperty(key, out JsonElement elem))
            {
                if (elem.ValueKind == JsonValueKind.Number && elem.TryGetInt32(out int val))
                {
                    return val;
                }
                if (elem.ValueKind == JsonValueKind.String && int.TryParse(elem.GetString(), out int parsed))
                {
                    return parsed;
                }
            }
        }
        return null;
    }

    private static string ExtractServerHost(JsonElement arguments)
    {
        string[] candidateKeys = ["serverHost", "server_host", "host", "Host", "chatServerHost", "chat_server_host"];
        foreach (string key in candidateKeys)
        {
            if (arguments.TryGetProperty(key, out JsonElement elem) && elem.ValueKind == JsonValueKind.String)
            {
                string? s = elem.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                {
                    return s.Trim();
                }
            }
        }
        return "127.0.0.1";
    }

    private static string ExtractQueueType(JsonElement arguments)
    {
        string[] candidateKeys = ["queueType", "queue_type", "queue", "QueueType"];
        foreach (string key in candidateKeys)
        {
            if (arguments.TryGetProperty(key, out JsonElement elem) && elem.ValueKind == JsonValueKind.String)
            {
                string? s = elem.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                {
                    return s.Trim().ToLowerInvariant();
                }
            }
        }
        return "pvp";
    }

    private static string ExtractGameType(JsonElement arguments)
    {
        string[] candidateKeys = ["gameType", "game_type", "type", "GameType"];
        foreach (string key in candidateKeys)
        {
            if (arguments.TryGetProperty(key, out JsonElement elem) && elem.ValueKind == JsonValueKind.String)
            {
                string? s = elem.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                {
                    return s.Trim().ToLowerInvariant();
                }
            }
        }
        return "normal";
    }

    private static string ExtractMapName(JsonElement arguments)
    {
        string[] candidateKeys = ["mapName", "map_name", "map", "MapName", "Map"];
        foreach (string key in candidateKeys)
        {
            if (arguments.TryGetProperty(key, out JsonElement elem) && elem.ValueKind == JsonValueKind.String)
            {
                string? s = elem.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                {
                    return s.Trim().ToLowerInvariant();
                }
            }
        }
        return "caldavar";
    }

    private static string ExtractGameMode(JsonElement arguments)
    {
        string[] candidateKeys = ["gameMode", "game_mode", "mode", "Mode"];
        foreach (string key in candidateKeys)
        {
            if (arguments.TryGetProperty(key, out JsonElement elem) && elem.ValueKind == JsonValueKind.String)
            {
                string? s = elem.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                {
                    return s.Trim().ToLowerInvariant();
                }
            }
        }
        return "ap";
    }

    private static string ExtractRegion(JsonElement arguments)
    {
        string[] candidateKeys = ["region", "regions", "gameRegions", "Region"];
        foreach (string key in candidateKeys)
        {
            if (arguments.TryGetProperty(key, out JsonElement elem) && elem.ValueKind == JsonValueKind.String)
            {
                string? s = elem.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                {
                    return s.Trim().ToUpperInvariant();
                }
            }
        }
        return "USE";
    }

    private static int ExtractTimeoutSeconds(JsonElement arguments)
    {
        string[] candidateKeys = ["timeoutSeconds", "timeout_seconds", "timeout", "timeoutSec", "TimeoutSeconds"];
        foreach (string key in candidateKeys)
        {
            if (arguments.TryGetProperty(key, out JsonElement elem))
            {
                if (elem.ValueKind == JsonValueKind.Number && elem.TryGetInt32(out int val))
                {
                    return val;
                }
                if (elem.ValueKind == JsonValueKind.String && int.TryParse(elem.GetString(), out int parsed))
                {
                    return parsed;
                }
            }
        }
        return 15;
    }

    private static bool ExtractSimulateMatchServer(JsonElement arguments)
    {
        string[] candidateKeys = ["simulateMatchServer", "simulate_match_server", "simMatchServer", "simulateMatch"];
        foreach (string key in candidateKeys)
        {
            if (arguments.TryGetProperty(key, out JsonElement elem))
            {
                if (elem.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    return elem.GetBoolean();
                }
                if (elem.ValueKind == JsonValueKind.String && bool.TryParse(elem.GetString(), out bool parsed))
                {
                    return parsed;
                }
            }
        }
        return false;
    }

    #endregion
}
