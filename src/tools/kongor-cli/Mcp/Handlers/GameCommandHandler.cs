// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Text.Json;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// MCP Tool Handler for 'kongor_game_command'.
/// Sends in-game console commands or cvar adjustments via Named Pipe IPC and returns execution output.
/// </summary>
public sealed class GameCommandHandler
{
    private readonly IGameIpcBridge _ipcBridge;
    private readonly GameServerManageHandler? _serverManageHandler;
    private readonly GameClientManageHandler? _clientManageHandler;
    private readonly IUiControlService _uiControlService;
    private readonly IClientCommandRemediationService _remediationService;
    private readonly IServerPortRegistry _portRegistry;
    private readonly TimeProvider _timeProvider;

    public GameCommandHandler(
        IGameIpcBridge? ipcBridge,
        GameServerManageHandler? serverManageHandler,
        TimeProvider? timeProvider)
        : this(ipcBridge, serverManageHandler, null, null, null, null, timeProvider)
    {
    }

    public GameCommandHandler(
        IGameIpcBridge? ipcBridge,
        GameServerManageHandler? serverManageHandler,
        GameClientManageHandler? clientManageHandler,
        IUiControlService? uiControlService,
        IClientCommandRemediationService? remediationService,
        TimeProvider? timeProvider)
        : this(ipcBridge, serverManageHandler, clientManageHandler, uiControlService, remediationService, null, timeProvider)
    {
    }

    public GameCommandHandler(
        IGameIpcBridge? ipcBridge = null,
        GameServerManageHandler? serverManageHandler = null,
        GameClientManageHandler? clientManageHandler = null,
        IUiControlService? uiControlService = null,
        IClientCommandRemediationService? remediationService = null,
        IServerPortRegistry? portRegistry = null,
        TimeProvider? timeProvider = null)
    {
        _ipcBridge = ipcBridge ?? new GameIpcBridge();
        _serverManageHandler = serverManageHandler;
        _clientManageHandler = clientManageHandler;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _uiControlService = uiControlService ?? new UiControlService(timeProvider: _timeProvider);
        _remediationService = remediationService ?? new ClientCommandRemediationService(timeProvider: _timeProvider);
        _portRegistry = portRegistry ?? serverManageHandler?.PortRegistry ?? ServerPortRegistry.Default;
    }

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (arguments.TryGetProperty("action", out JsonElement actEl) &&
            (actEl.GetString()?.Equals("errors", StringComparison.OrdinalIgnoreCase) == true ||
             actEl.GetString()?.Equals("history", StringComparison.OrdinalIgnoreCase) == true))
        {
            int limit = (arguments.TryGetProperty("limit", out JsonElement limEl) && limEl.TryGetInt32(out int lVal) && lVal > 0)
                ? lVal
                : 10;
            var errs = _remediationService.GetRecentErrors(limit);
            return ToolCallResult.Json(new
            {
                status = "success",
                action = "errors",
                totalErrors = errs.Count,
                errors = errs.Select(e => new
                {
                    id = e.Id,
                    timestamp = e.Timestamp.ToString("o"),
                    command = e.Command,
                    target = e.Target,
                    channel = e.Channel,
                    errorResponse = e.RawErrorResponse,
                    category = e.Remediation.ErrorCategory,
                    remediationHint = e.Remediation.RemediationHint,
                    improvedCommand = e.Remediation.ImprovedCommand,
                    selfHealed = e.SelfHealed
                }),
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            });
        }

        string? command = ResolveCommand(arguments, out string action);

        int timeoutMs = 3000;
        if (arguments.TryGetProperty("timeoutMs", out JsonElement toElem) && toElem.TryGetInt32(out int parsedTimeout) && parsedTimeout > 0)
        {
            timeoutMs = parsedTimeout;
        }

        string target = "client";
        int? explicitServerPort = null;
        int? explicitClientPid = null;
        if (arguments.TryGetProperty("target", out JsonElement targetElem) && !string.IsNullOrWhiteSpace(targetElem.GetString()))
        {
            target = targetElem.GetString()!.Trim().ToLowerInvariant();
            if (target.StartsWith("server:", StringComparison.OrdinalIgnoreCase))
            {
                string portPart = target.Substring("server:".Length).Trim();
                if (int.TryParse(portPart, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int parsedPort) && parsedPort > 0)
                {
                    explicitServerPort = parsedPort;
                }
                target = "server";
            }
            else if (target.StartsWith("client:", StringComparison.OrdinalIgnoreCase))
            {
                string pidPart = target.Substring("client:".Length).Trim();
                if (int.TryParse(pidPart, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int parsedPid) && parsedPid > 0)
                {
                    explicitClientPid = parsedPid;
                }
                target = "client";
            }
        }

        if (string.IsNullOrWhiteSpace(command))
        {
            if (action is "status" or "state" or "inspect")
            {
                if ((target == "server" || explicitServerPort.HasValue) && _serverManageHandler != null)
                {
                    return await _serverManageHandler.HandleAsync(arguments, cancellationToken).ConfigureAwait(false);
                }
                if (_clientManageHandler != null)
                {
                    return await _clientManageHandler.HandleAsync(arguments, cancellationToken).ConfigureAwait(false);
                }
            }
            return ToolCallResult.Error("Missing required parameter 'command'. (For status or state queries without a command, use action: 'status' or 'kongor_game_client_manage').");
        }

        if (!explicitClientPid.HasValue)
        {
            if ((arguments.TryGetProperty("process_id", out JsonElement pidEl) ||
                 arguments.TryGetProperty("processId", out pidEl) ||
                 arguments.TryGetProperty("pid", out pidEl)) &&
                pidEl.TryGetInt32(out int parsedPidVal) && parsedPidVal > 0)
            {
                explicitClientPid = parsedPidVal;
            }
        }

        bool autoLaunch = GetAutoLaunch(arguments);

        string channel = "auto";
        if (arguments.TryGetProperty("channel", out JsonElement chElem) && !string.IsNullOrWhiteSpace(chElem.GetString()))
        {
            channel = chElem.GetString()!.Trim().ToLowerInvariant();
        }

        string? mapFilter = arguments.TryGetProperty("map", out JsonElement mEl) ? mEl.GetString() : null;
        long? matchIdFilter = arguments.TryGetProperty("matchId", out JsonElement miEl) && miEl.TryGetInt64(out long mi) ? mi : null;
        int? rawPort = explicitServerPort ?? ((arguments.TryGetProperty("port", out JsonElement pEl) && pEl.TryGetInt32(out int pt) && pt > 0) ? pt : null);

        // 1. Multiplexed target: Both client and dedicated server worker
        if (target is "both" or "all")
        {
            int targetPort = _portRegistry.ResolveServerPort(rawPort, mapFilter, matchIdFilter, out string resolutionReason);
            bool autoLaunchServer = (targetPort == 11235) && autoLaunch;

            var clientTask = HandleClientCommandAsync(arguments, cancellationToken);
            var serverTask = ExecuteServerCommandAsync(targetPort, command, timeoutMs, autoLaunchServer, channel, resolutionReason, cancellationToken);

            await Task.WhenAll(clientTask, serverTask).ConfigureAwait(false);
            var clientRes = await clientTask.ConfigureAwait(false);
            var serverRes = await serverTask.ConfigureAwait(false);

            object clientPayload = UnpackResultContent(clientRes);
            object serverPayload = UnpackResultContent(serverRes);

            bool clientOk = IsResultSuccessful(clientRes, clientPayload);
            bool serverOk = IsResultSuccessful(serverRes, serverPayload);

            string overallStatus = (clientOk && serverOk)
                ? "success"
                : (clientOk || serverOk)
                    ? "partial_success"
                    : "error";

            return ToolCallResult.Json(new
            {
                status = overallStatus,
                target = "both",
                command,
                action = string.IsNullOrEmpty(action) ? null : action,
                multiplexed = true,
                port = targetPort,
                resolvedPort = targetPort,
                resolutionReason,
                client = clientPayload,
                server = serverPayload,
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            }, isError: !clientOk && !serverOk);
        }

        // 2. Explicit server target or port targeting
        if (target == "server" || explicitServerPort.HasValue || (arguments.TryGetProperty("port", out JsonElement portElem) && portElem.TryGetInt32(out int parsedPortVal) && parsedPortVal > 0))
        {
            int targetPort = _portRegistry.ResolveServerPort(rawPort, mapFilter, matchIdFilter, out string resolutionReason);
            bool autoLaunchServer = (target == "server" || targetPort == 11235) && autoLaunch;
            return await ExecuteServerCommandAsync(targetPort, command, timeoutMs, autoLaunchServer, channel, resolutionReason: resolutionReason, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        // 3. Client (default)
        return await HandleClientCommandAsync(arguments, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves executable command from raw 'command' or deterministic simulation actions
    /// (set_timescale, step_frames, pause, resume).
    /// </summary>
    public static string? ResolveCommand(JsonElement arguments, out string action)
    {
        action = string.Empty;
        if (arguments.TryGetProperty("action", out JsonElement actEl) && !string.IsNullOrWhiteSpace(actEl.GetString()))
        {
            action = actEl.GetString()!.Trim().ToLowerInvariant();
        }

        if (arguments.TryGetProperty("command", out JsonElement cmdElem) && !string.IsNullOrWhiteSpace(cmdElem.GetString()))
        {
            return cmdElem.GetString()!.Trim();
        }

        switch (action)
        {
            case "set_timescale" or "timescale":
            {
                double scale = 1.0;
                if ((arguments.TryGetProperty("scale", out JsonElement scEl) ||
                     arguments.TryGetProperty("timeScale", out scEl) ||
                     arguments.TryGetProperty("timescale", out scEl) ||
                     arguments.TryGetProperty("factor", out scEl) ||
                     arguments.TryGetProperty("value", out scEl)) &&
                    scEl.TryGetDouble(out double scVal))
                {
                    scale = scVal;
                }
                scale = Math.Clamp(scale, 0.01, 100.0);
                return FormattableString.Invariant($"Set host_timeScale {scale:F4}");
            }

            case "step_frames" or "step":
            {
                int frames = 1;
                if ((arguments.TryGetProperty("frames", out JsonElement frEl) ||
                     arguments.TryGetProperty("ticks", out frEl) ||
                     arguments.TryGetProperty("count", out frEl) ||
                     arguments.TryGetProperty("step", out frEl)) &&
                    frEl.TryGetInt32(out int frVal))
                {
                    frames = frVal;
                }
                frames = Math.Clamp(frames, 1, 1000);
                bool uncapped = arguments.TryGetProperty("uncapped", out JsonElement unEl) && unEl.ValueKind == JsonValueKind.True;
                if (uncapped)
                {
                    return $"Set host_maxfps 0; Set cl_maxfps 0; ReplayIncFrame {frames}";
                }
                return $"ReplayIncFrame {frames}";
            }

            case "pause":
                return "Pause";

            case "resume" or "unpause":
                return "Unpause";

            default:
                return null;
        }
    }

    private static bool GetAutoLaunch(JsonElement arguments)
    {
        if (arguments.TryGetProperty("autoLaunch", out JsonElement el))
        {
            if (el.ValueKind == JsonValueKind.False) return false;
            if (el.ValueKind == JsonValueKind.True) return true;
            if (el.ValueKind == JsonValueKind.String && bool.TryParse(el.GetString(), out bool b)) return b;
        }
        return true;
    }

    /// <summary>
    /// Handler dedicated specifically for 'kongor_game_client_command'.
    /// Routes commands to the game client via authentic dual-channel execution (stdin or in-game console).
    /// </summary>
    public async Task<ToolCallResult> HandleClientCommandAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        string? command = ResolveCommand(arguments, out string action);
        if (string.IsNullOrWhiteSpace(command))
        {
            if (action is "status" or "state" or "inspect" && _clientManageHandler != null)
            {
                return await _clientManageHandler.HandleAsync(arguments, cancellationToken).ConfigureAwait(false);
            }
            return ToolCallResult.Error("Missing required parameter 'command'. (For status or state queries without a command, use action: 'status' or 'kongor_game_client_manage').");
        }

        int timeoutMs = (arguments.TryGetProperty("timeoutMs", out JsonElement toElem) && toElem.TryGetInt32(out int parsedTimeout) && parsedTimeout > 0)
            ? parsedTimeout
            : 3000;

        bool autoLaunch = GetAutoLaunch(arguments);
        bool allowKeystroke = (arguments.TryGetProperty("allow_keystroke_fallback", out JsonElement akfEl) || arguments.TryGetProperty("allowKeystrokeFallback", out akfEl)) && akfEl.GetBoolean();

        string? explicitAgent = null;
        if ((arguments.TryGetProperty("agent_id", out JsonElement aEl) ||
             arguments.TryGetProperty("agentId", out aEl) ||
             arguments.TryGetProperty("agentOwner", out aEl) ||
             arguments.TryGetProperty("agent_owner", out aEl)) &&
            !string.IsNullOrWhiteSpace(aEl.GetString()))
        {
            explicitAgent = aEl.GetString()!.Trim();
            McpAgentContext.CurrentAgentId = explicitAgent;
        }

        int? processId = null;
        if ((arguments.TryGetProperty("process_id", out JsonElement pEl) ||
             arguments.TryGetProperty("processId", out pEl) ||
             arguments.TryGetProperty("pid", out pEl)) &&
            pEl.TryGetInt32(out int pVal) && pVal > 0)
        {
            processId = pVal;
        }
        else if (arguments.TryGetProperty("target", out JsonElement targetEl) && targetEl.GetString()?.StartsWith("client:", StringComparison.OrdinalIgnoreCase) == true)
        {
            string pidStr = targetEl.GetString()!.Substring("client:".Length).Trim();
            if (int.TryParse(pidStr, out int parsedPid) && parsedPid > 0)
            {
                processId = parsedPid;
            }
        }

        // 1. If GameClientManageHandler is available, route directly to it with processId targeting
        if (_clientManageHandler != null)
        {
            return await _clientManageHandler.ExecuteClientCommandAsync(command, timeoutMs, autoLaunch, allowKeystroke, processId, cancellationToken).ConfigureAwait(false);
        }

        // 3. If IPC bridge is connected, dispatch via IPC
        if (_ipcBridge != null && _ipcBridge.IsConnected)
        {
            try
            {
                Stopwatch sw = Stopwatch.StartNew();
                string output = await _ipcBridge.ExecuteCommandAsync(command, timeoutMs, cancellationToken).ConfigureAwait(false);
                sw.Stop();

                bool isEngineError = output.Contains("Unknown command", StringComparison.OrdinalIgnoreCase) ||
                                     output.Contains("attempt to index", StringComparison.OrdinalIgnoreCase) ||
                                     output.Contains("syntax error", StringComparison.OrdinalIgnoreCase) ||
                                     output.Contains("is read only", StringComparison.OrdinalIgnoreCase);

                if (isEngineError)
                {
                    var rec = _remediationService.RecordAndRemediate(command, output, "ipc", "client");
                    return ToolCallResult.Json(new
                    {
                        status = "engine_error",
                        executionMode = ExecutionTruth.ModeLiveEngine,
                        success = false,
                        target = "client",
                        channel = "ipc",
                        command,
                        response = output,
                        remediationCategory = rec.Remediation.ErrorCategory,
                        remediationHint = rec.Remediation.RemediationHint,
                        improvedCommand = rec.Remediation.ImprovedCommand,
                        executionTimeMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                        timestamp = _timeProvider.GetUtcNow().ToString("o")
                    });
                }

                return ToolCallResult.Json(new
                {
                    status = "success",
                    executionMode = ExecutionTruth.ModeLiveEngine,
                    success = true,
                    target = "client",
                    channel = "ipc",
                    command,
                    response = output,
                    executionTimeMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                    timestamp = _timeProvider.GetUtcNow().ToString("o")
                });
            }
            catch (Exception ex)
            {
                var rec = _remediationService.RecordAndRemediate(command, ex.Message, "ipc", "client");
                return ToolCallResult.Json(new
                {
                    status = "error",
                    executionMode = ExecutionTruth.ModeLiveEngine,
                    success = false,
                    target = "client",
                    channel = "ipc",
                    command,
                    response = $"IPC Error: {ex.Message}",
                    remediationCategory = rec.Remediation.ErrorCategory,
                    remediationHint = rec.Remediation.RemediationHint,
                    improvedCommand = rec.Remediation.ImprovedCommand,
                    executionTimeMs = 0,
                    error = ex.Message,
                    timestamp = _timeProvider.GetUtcNow().ToString("o")
                });
            }
        }

            // 3. Fallback: Neither client nor connected pipe is available. Return immediately without hanging.
            string offlineMsg = "Game client is not currently running or has no active console window. Launch client first with 'kongor_game_client_manage action:launch'.";
            var offlineRec = _remediationService.RecordAndRemediate(command, offlineMsg, "none", "client");
            return ToolCallResult.Json(new
            {
                status = ExecutionTruth.StatusTargetOffline,
                executionMode = ExecutionTruth.ModeOfflineSimulation,
                success = false,
                target = "client",
                channel = "none",
                command,
                response = offlineMsg,
                remediationCategory = offlineRec.Remediation.ErrorCategory,
                remediationHint = offlineRec.Remediation.RemediationHint,
                targetStatus = new
                {
                    client = new { running = false, pid = (int?)null, engineReady = false, ipcConnected = false },
                    server = new { running = false, pid = (int?)null, port = 11235, pipeConnected = false, engineReady = false }
                },
                remediation = new
                {
                    hint = offlineRec.Remediation.RemediationHint,
                    suggestedAction = "kongor_game_client_manage",
                    suggestedParams = new { action = "launch", mode = "headless" }
                },
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            });
        }

    /// <summary>
    /// Handler dedicated specifically for 'kongor_game_server_command'.
    /// Requires port and routes commands to the dedicated game server worker on that port.
    /// </summary>
    public async Task<ToolCallResult> HandleServerCommandAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (!arguments.TryGetProperty("command", out JsonElement cmdElem) || string.IsNullOrWhiteSpace(cmdElem.GetString()))
        {
            return ToolCallResult.Error("Missing required parameter 'command'.");
        }

        string command = cmdElem.GetString()!.Trim();
        int? rawPort = (arguments.TryGetProperty("port", out JsonElement pEl) && pEl.TryGetInt32(out int pt) && pt > 0)
            ? pt
            : null;

        int timeoutMs = (arguments.TryGetProperty("timeoutMs", out JsonElement toElem) && toElem.TryGetInt32(out int parsedTimeout) && parsedTimeout > 0)
            ? parsedTimeout
            : 3000;

        string target = "server";
        if (arguments.TryGetProperty("target", out JsonElement tEl) && !string.IsNullOrWhiteSpace(tEl.GetString()))
        {
            target = tEl.GetString()!.Trim().ToLowerInvariant();
            if (target.StartsWith("server:", StringComparison.OrdinalIgnoreCase))
            {
                string portPart = target.Substring("server:".Length).Trim();
                if (int.TryParse(portPart, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int parsedPort) && parsedPort > 0)
                {
                    rawPort = parsedPort;
                }
                target = "server";
            }
        }

        string? mapFilter = arguments.TryGetProperty("map", out JsonElement mEl) ? mEl.GetString() : null;
        long? matchIdFilter = arguments.TryGetProperty("matchId", out JsonElement miEl) && miEl.TryGetInt64(out long mi) ? mi : null;

        int targetPort = _portRegistry.ResolveServerPort(rawPort, mapFilter, matchIdFilter, out string resolutionReason);

        string channel = "auto";
        if (arguments.TryGetProperty("channel", out JsonElement chElem) && !string.IsNullOrWhiteSpace(chElem.GetString()))
        {
            channel = chElem.GetString()!.Trim().ToLowerInvariant();
        }

        bool autoLaunch = GetAutoLaunch(arguments);
        bool autoLaunchServer = (target == "server" && targetPort == 11235) && autoLaunch;

        return await ExecuteServerCommandAsync(targetPort, command, timeoutMs, autoLaunchServer, channel, resolutionReason, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ToolCallResult> ExecuteServerCommandAsync(
        int port,
        string command,
        int timeoutMs,
        bool autoLaunch,
        string channel = "auto",
        string? resolutionReason = null,
        CancellationToken cancellationToken = default)
    {
        if (_serverManageHandler != null)
        {
            var res = await _serverManageHandler.ExecuteCommandOnPortAsync(port, command, autoLaunch, channel, resolutionReason, cancellationToken).ConfigureAwait(false);
            if (res.IsError && res.Content.FirstOrDefault() is TextContentItem tc && !string.IsNullOrWhiteSpace(tc.Text))
            {
                _remediationService.RecordAndRemediate(command, tc.Text, channel, $"server:{port}");
            }
            return res;
        }

        try
        {
            await using var bridge = new GameIpcBridge($"hon-server-{port}");
            await bridge.ConnectAsync(timeoutMs, cancellationToken).ConfigureAwait(false);
            string serverOutput = await bridge.ExecuteCommandAsync(command, timeoutMs, cancellationToken).ConfigureAwait(false);

            bool isEngineError = serverOutput.Contains("Unknown command", StringComparison.OrdinalIgnoreCase) ||
                                 serverOutput.Contains("attempt to index", StringComparison.OrdinalIgnoreCase) ||
                                 serverOutput.Contains("syntax error", StringComparison.OrdinalIgnoreCase) ||
                                 serverOutput.Contains("is read only", StringComparison.OrdinalIgnoreCase);

            if (isEngineError)
            {
                var rec = _remediationService.RecordAndRemediate(command, serverOutput, channel, $"server:{port}");
                return ToolCallResult.Json(new
                {
                    status = "engine_error",
                    target = $"server:{port}",
                    port,
                    resolvedPort = port,
                    resolutionReason = resolutionReason ?? "explicit_port",
                    command,
                    response = serverOutput,
                    remediationCategory = rec.Remediation.ErrorCategory,
                    remediationHint = rec.Remediation.RemediationHint,
                    improvedCommand = rec.Remediation.ImprovedCommand,
                    timestamp = _timeProvider.GetUtcNow().ToString("o")
                });
            }

            return ToolCallResult.Json(new
            {
                status = "success",
                target = $"server:{port}",
                port,
                resolvedPort = port,
                resolutionReason = resolutionReason ?? "explicit_port",
                command,
                response = serverOutput,
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            });
        }
        catch (Exception ex)
        {
            var rec = _remediationService.RecordAndRemediate(command, ex.Message, channel, $"server:{port}");
            return ToolCallResult.Json(new
            {
                status = "error",
                target = $"server:{port}",
                port,
                resolvedPort = port,
                resolutionReason = resolutionReason ?? "explicit_port",
                command,
                message = $"Failed to execute command on server port {port}: {ex.Message}",
                remediationHint = rec.Remediation.RemediationHint,
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            }, isError: true);
        }
    }

    private static object UnpackResultContent(ToolCallResult result)
    {
        if (result.Content.Count > 0 && result.Content[0] is TextContentItem tc && !string.IsNullOrWhiteSpace(tc.Text))
        {
            try
            {
                using var doc = JsonDocument.Parse(tc.Text);
                return doc.RootElement.Clone();
            }
            catch
            {
                return new { text = tc.Text, isError = result.IsError };
            }
        }
        return new { isError = result.IsError };
    }

    private static bool IsResultSuccessful(ToolCallResult result, object unpacked)
    {
        if (result.IsError) return false;
        if (unpacked is JsonElement json)
        {
            if (json.TryGetProperty("status", out var sEl))
            {
                string? status = sEl.GetString();
                if (status is "error" or "engine_error" or "target_offline") return false;
                if (status is "skipped") return true;
            }
            if (json.TryGetProperty("success", out var succEl) && succEl.ValueKind == JsonValueKind.False)
            {
                return false;
            }
        }
        return true;
    }
}
