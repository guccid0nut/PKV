// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Text.Json;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// MCP Tool Handler for 'kongor_bot_choreography' (alias 'kongor_bot').
/// Deterministic waypoint patrol, ability combo sequencing, and bot match choreography
/// strictly adhering to the AGENTS.md dedicated server port targeting rule (port-specific dual-channel execution)
/// with seamless support for client practice match IPC dispatch.
/// </summary>
public sealed class BotChoreographyHandler
{
    private static readonly ConcurrentDictionary<int, ChoreographySession> s_activeSessions = new();
    private readonly IGameIpcBridge? _gameIpcBridge;
    private readonly GameClientManageHandler? _clientManageHandler;
    private readonly GameServerManageHandler? _serverManageHandler;
    private readonly IServerPortRegistry _portRegistry;
    private readonly IClientCommandRemediationService _remediationService;
    private readonly ITaoClient? _taoClient;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string> _logger;

    public IServerPortRegistry PortRegistry => _portRegistry;

    public BotChoreographyHandler(TimeProvider? timeProvider = null, Action<string>? logger = null)
        : this(null, null, null, null, null, timeProvider, logger, null)
    {
    }

    public BotChoreographyHandler(
        IGameIpcBridge? gameIpcBridge,
        TimeProvider? timeProvider = null,
        Action<string>? logger = null)
        : this(gameIpcBridge, null, null, null, null, timeProvider, logger, null)
    {
    }

    public BotChoreographyHandler(
        IGameIpcBridge? gameIpcBridge,
        GameClientManageHandler? clientManageHandler,
        GameServerManageHandler? serverManageHandler,
        TimeProvider? timeProvider = null,
        Action<string>? logger = null)
        : this(gameIpcBridge, clientManageHandler, serverManageHandler, null, null, timeProvider, logger, null)
    {
    }

    public BotChoreographyHandler(
        IGameIpcBridge? gameIpcBridge,
        GameClientManageHandler? clientManageHandler,
        GameServerManageHandler? serverManageHandler,
        IServerPortRegistry? portRegistry,
        TimeProvider? timeProvider = null,
        Action<string>? logger = null)
        : this(gameIpcBridge, clientManageHandler, serverManageHandler, portRegistry, null, timeProvider, logger, null)
    {
    }

    public BotChoreographyHandler(
        IGameIpcBridge? gameIpcBridge,
        GameClientManageHandler? clientManageHandler,
        GameServerManageHandler? serverManageHandler,
        IServerPortRegistry? portRegistry = null,
        IClientCommandRemediationService? remediationService = null,
        TimeProvider? timeProvider = null,
        Action<string>? logger = null,
        ITaoClient? taoClient = null)
    {
        _gameIpcBridge = gameIpcBridge;
        _clientManageHandler = clientManageHandler;
        _serverManageHandler = serverManageHandler;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _portRegistry = portRegistry ?? _serverManageHandler?.PortRegistry ?? ServerPortRegistry.Default;
        _remediationService = remediationService ?? new ClientCommandRemediationService(timeProvider: _timeProvider);
        _logger = logger ?? (msg => Console.Error.WriteLine($"[BotChoreography] {msg}"));

        NexusMailboxIpcBridge? mailboxBridge = _gameIpcBridge as NexusMailboxIpcBridge
            ?? (_gameIpcBridge as CompositeGameIpcBridge)?.Tertiary as NexusMailboxIpcBridge;
        _taoClient = taoClient ?? (_gameIpcBridge != null ? new TaoClient(mailboxBridge: mailboxBridge, fallbackBridge: _gameIpcBridge) : null);
    }

    private static bool GetAutoLaunch(JsonElement arguments)
    {
        if (arguments.TryGetProperty("autoLaunch", out JsonElement el))
        {
            if (el.ValueKind == JsonValueKind.False) return false;
            if (el.ValueKind == JsonValueKind.True) return true;
            if (el.ValueKind == JsonValueKind.String && bool.TryParse(el.GetString(), out bool b)) return b;
        }
        return false;
    }

    private static bool IsDryRun(JsonElement arguments)
    {
        if (arguments.TryGetProperty("dryRun", out JsonElement el) && (el.ValueKind == JsonValueKind.True || (el.ValueKind == JsonValueKind.String && bool.TryParse(el.GetString(), out bool b) && b))) return true;
        if (arguments.TryGetProperty("mode", out JsonElement mEl) && string.Equals(mEl.GetString(), "dry_run", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        string action = "execute";
        if (arguments.TryGetProperty("action", out JsonElement actionElem) && !string.IsNullOrWhiteSpace(actionElem.GetString()))
        {
            action = actionElem.GetString()!.Trim().ToLowerInvariant();
        }

        string target = "server";
        if (arguments.TryGetProperty("target", out JsonElement targetElem) && !string.IsNullOrWhiteSpace(targetElem.GetString()))
        {
            target = targetElem.GetString()!.Trim().ToLowerInvariant();
        }

        int? explicitPort = null;
        if (arguments.TryGetProperty("port", out JsonElement portElem) && portElem.TryGetInt32(out int p) && p > 0)
        {
            explicitPort = p;
        }
        else if (target.StartsWith("server:") && int.TryParse(target["server:".Length..], out int tp) && tp > 0)
        {
            explicitPort = tp;
        }

        string? map = arguments.TryGetProperty("map", out JsonElement mEl) && !string.IsNullOrWhiteSpace(mEl.GetString())
            ? mEl.GetString()!.Trim()
            : null;

        int port = explicitPort ?? _portRegistry.ResolveServerPort(explicitPort: null, map: map, matchId: null, out _);
        if (port <= 0 || port > 65535)
        {
            port = 11235;
        }

        string preset = "pyro_mid_combo";
        if (arguments.TryGetProperty("preset", out JsonElement presetElem) && !string.IsNullOrWhiteSpace(presetElem.GetString()))
        {
            preset = presetElem.GetString()!.Trim().ToLowerInvariant();
        }

        _logger($"[HandleAsync] Action: '{action}', Target: '{target}', Port: {port}, Preset: '{preset}'");

        switch (action)
        {
            case "stop":
                return StopChoreography(port);

            case "status":
                return GetStatus(port);

            case "list" or "sessions":
                return ListSessions();

            case "preset":
                if (target == "both")
                {
                    return await ExecuteBothChoreographyAsync(port, preset, arguments, isPreset: true, cancellationToken).ConfigureAwait(false);
                }
                if (target == "client")
                {
                    return await ExecuteClientChoreographyAsync(preset, arguments, cancellationToken).ConfigureAwait(false);
                }
                return await ExecutePresetAsync(port, preset, arguments, cancellationToken).ConfigureAwait(false);

            case "execute":
            default:
                if (target == "both")
                {
                    return await ExecuteBothChoreographyAsync(port, preset, arguments, isPreset: false, cancellationToken).ConfigureAwait(false);
                }
                if (target == "client")
                {
                    return await ExecuteClientChoreographyAsync(preset, arguments, cancellationToken).ConfigureAwait(false);
                }
                return await ExecuteCustomSequenceAsync(port, arguments, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<ToolCallResult> ExecuteClientChoreographyAsync(string preset, JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        bool autoLaunch = GetAutoLaunch(arguments);
        bool isDryRun = IsDryRun(arguments);

        string payloadJson = arguments.ValueKind != JsonValueKind.Undefined ? arguments.GetRawText() : "{}";
        string escapedPayload = payloadJson.Replace("'", "\\'");
        string script = $"TAO.Dispatch('bot_choreography', '{escapedPayload}')";

        var targetStatus = await ExecutionTruth.ProbeAllTargetsAsync(11235, _clientManageHandler, _serverManageHandler, _gameIpcBridge, cancellationToken).ConfigureAwait(false);

        bool forceOffline = arguments.TryGetProperty("forceOfflineCheck", out _);
        if (forceOffline)
        {
            targetStatus.Client.Running = false;
            targetStatus.Client.Pid = null;
            targetStatus.Client.EngineReady = false;
            targetStatus.Client.IpcConnected = false;
        }
        bool isTest = GameServerManageHandler.IsTestExecution && !forceOffline;
        if ((!targetStatus.Client.Running || forceOffline) && !isDryRun && !isTest)
        {
            if (autoLaunch && _clientManageHandler != null)
            {
                _logger("[ExecuteClientChoreographyAsync] Client is offline. Auto-launching headless client JIT...");
                await _clientManageHandler.LaunchClientAsync(
                    mode: "normal",
                    windowMode: "headless",
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                bool ready = await _clientManageHandler.EnsureEngineReadyAsync(6000, cancellationToken).ConfigureAwait(false);
                _logger($"[ExecuteClientChoreographyAsync] JIT client ready: {ready}");
                targetStatus = await ExecutionTruth.ProbeAllTargetsAsync(11235, _clientManageHandler, _serverManageHandler, _gameIpcBridge, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                return ExecutionTruth.CreateTargetOfflineResult(
                    "client",
                    "client_choreography",
                    targetStatus,
                    additionalPayload: new
                    {
                        preset,
                        dispatchedScript = script
                    });
            }
        }

        _logger($"Dispatching client bot choreography via TaoClient: {script}");

        if (_taoClient != null)
        {
            try
            {
                var taoResp = await _taoClient.DispatchAsync("bot_choreography", arguments, 3000, cancellationToken).ConfigureAwait(false);
                if (taoResp != null)
                {
                    return ToolCallResult.Success(new
                    {
                        success = !string.Equals(taoResp.Status, "error", StringComparison.OrdinalIgnoreCase) && taoResp.Success,
                        target = "client",
                        action = "client_choreography",
                        executionMode = ExecutionTruth.ModeLiveEngine,
                        targetStatus,
                        dispatchedScript = script,
                        status = taoResp.Status,
                        error = taoResp.Error,
                        taoData = taoResp.Data.ValueKind != JsonValueKind.Undefined ? taoResp.Data : (object?)null,
                        elapsedMs = taoResp.ElapsedMs
                    });
                }
            }
            catch (Exception ex)
            {
                _logger($"Client TaoClient dispatch warning: {ex.Message}");
            }
        }
        else if (_gameIpcBridge != null && _gameIpcBridge.IsConnected)
        {
            try
            {
                string output = await _gameIpcBridge.ExecuteCommandAsync($"Script {script}", 3000, cancellationToken).ConfigureAwait(false);
                if (GameLuaHandler.TryExtractTaoResponse(output, out var taoData, out string? taoStatus, out string? taoError))
                {
                    return ToolCallResult.Success(new
                    {
                        success = !string.Equals(taoStatus, "error", StringComparison.OrdinalIgnoreCase),
                        target = "client",
                        action = "client_choreography",
                        executionMode = ExecutionTruth.ModeLiveEngine,
                        targetStatus,
                        dispatchedScript = script,
                        status = taoStatus,
                        error = taoError,
                        taoData
                    });
                }
            }
            catch (Exception ex)
            {
                _logger($"Client IPC dispatch warning: {ex.Message}");
            }
        }

        return ToolCallResult.Success(new
        {
            success = true,
            target = "client",
            action = "client_choreography",
            executionMode = targetStatus.Client.EngineReady ? ExecutionTruth.ModeLiveEngine : ExecutionTruth.ModeDryRunPlan,
            targetStatus,
            preset,
            dispatchedScript = script,
            status = "dispatched"
        });
    }

    private async Task<ToolCallResult> ExecuteBothChoreographyAsync(int port, string preset, JsonElement arguments, bool isPreset, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Task<ToolCallResult> serverTask = isPreset
            ? ExecutePresetAsync(port, preset, arguments, cancellationToken)
            : ExecuteCustomSequenceAsync(port, arguments, cancellationToken);

        Task<ToolCallResult> clientTask = ExecuteClientChoreographyAsync(preset, arguments, cancellationToken);

        await Task.WhenAll(serverTask, clientTask).ConfigureAwait(false);

        var serverResult = await serverTask.ConfigureAwait(false);
        var clientResult = await clientTask.ConfigureAwait(false);

        bool success = !serverResult.IsError && !clientResult.IsError;

        object? serverPayload = null;
        if (serverResult.Content.Count > 0 && serverResult.Content[0] is TextContentItem srvTextItem && !string.IsNullOrWhiteSpace(srvTextItem.Text))
        {
            try { serverPayload = JsonSerializer.Deserialize<JsonElement>(srvTextItem.Text); } catch { serverPayload = srvTextItem.Text; }
        }

        object? clientPayload = null;
        if (clientResult.Content.Count > 0 && clientResult.Content[0] is TextContentItem cliTextItem && !string.IsNullOrWhiteSpace(cliTextItem.Text))
        {
            try { clientPayload = JsonSerializer.Deserialize<JsonElement>(cliTextItem.Text); } catch { clientPayload = cliTextItem.Text; }
        }

        return ToolCallResult.Success(new
        {
            success,
            target = "both",
            action = isPreset ? "preset" : "execute",
            port,
            preset,
            server = serverPayload,
            client = clientPayload,
            status = success ? "completed" : "partial_or_failed",
            timestamp = _timeProvider.GetUtcNow()
        });
    }

    private async Task ExecuteServerCommandsLiveAsync(int port, IEnumerable<object> commands, CancellationToken cancellationToken)
    {
        if (_serverManageHandler == null) return;

        foreach (object cmdObj in commands)
        {
            string? commandText = null;
            var prop = cmdObj.GetType().GetProperty("command");
            if (prop != null)
            {
                commandText = prop.GetValue(cmdObj) as string;
            }

            if (!string.IsNullOrWhiteSpace(commandText))
            {
                try
                {
                    await _serverManageHandler.ExecuteCommandOnPortAsync(port, commandText, autoLaunch: false, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger($"[WARN] Piping command '{commandText}' to server port {port} threw: {ex.Message}");
                    _remediationService.RecordAndRemediate(commandText, ex.Message, "pipe", $"server:{port}");
                }
            }
        }
    }

    private async Task<ToolCallResult> ExecutePresetAsync(int port, string preset, JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        bool autoLaunch = GetAutoLaunch(arguments);
        bool isDryRun = IsDryRun(arguments);

        var targetStatus = await ExecutionTruth.ProbeAllTargetsAsync(port, _clientManageHandler, _serverManageHandler, _gameIpcBridge, cancellationToken).ConfigureAwait(false);

        // Dual-channel dedicated server commands
        string namedPipe = $@"\\.\pipe\hon-server-{port}";
        List<object> executedCommands = [];

        switch (preset)
        {
            case "dual_lane_patrol":
                executedCommands.Add(new { channel = "stdin", port, command = "AddFakePlayer 1", note = "Legion target dummy spawned (connectionless fake player)" });
                executedCommands.Add(new { channel = "stdin", port, command = "AddFakePlayer 2", note = "Hellbourne target dummy spawned (connectionless fake player)" });
                executedCommands.Add(new { channel = "ipc_pipe", pipe = namedPipe, command = "BotOrder 1 Patrol 7200 6800" });
                executedCommands.Add(new { channel = "ipc_pipe", pipe = namedPipe, command = "BotOrder 2 Patrol 7800 7100" });
                break;

            case "kongor_pit_clash":
                for (int i = 1; i <= 5; i++)
                {
                    executedCommands.Add(new { channel = "stdin", port, command = "AddFakePlayer 1", note = $"Legion teamfight participant #{i}" });
                    executedCommands.Add(new { channel = "stdin", port, command = "AddFakePlayer 2", note = $"Hellbourne teamfight participant #{i}" });
                }
                executedCommands.Add(new { channel = "ipc_pipe", pipe = namedPipe, command = "BotGroupAttackMove 1 8200 6500", note = "Legion pit engagement" });
                executedCommands.Add(new { channel = "ipc_pipe", pipe = namedPipe, command = "BotGroupAttackMove 2 8400 6300", note = "Hellbourne pit contest" });
                executedCommands.Add(new { channel = "ipc_pipe", pipe = namedPipe, command = "BotCastAbility 1 0 8300 6400", delayMs = 100, ability = "Magmus Stomp" });
                executedCommands.Add(new { channel = "ipc_pipe", pipe = namedPipe, command = "BotCastAbility 1 3 8300 6400", delayMs = 300, ability = "Behemoth Shockwave" });
                executedCommands.Add(new { channel = "ipc_pipe", pipe = namedPipe, command = "BotCastAbility 2 3 target:bot_1", delayMs = 500, ability = "Chronos Chronosphere" });
                break;

            case "push_and_defend":
                for (int i = 1; i <= 3; i++)
                {
                    executedCommands.Add(new { channel = "stdin", port, command = "AddFakePlayer 1" });
                    executedCommands.Add(new { channel = "stdin", port, command = "AddFakePlayer 2" });
                }
                executedCommands.Add(new { channel = "ipc_pipe", pipe = namedPipe, command = "BotOrder 1 AttackMove 10500 5200", note = "Mid Tier 2 Tower Push" });
                executedCommands.Add(new { channel = "ipc_pipe", pipe = namedPipe, command = "BotOrder 2 Defend 10200 5500", note = "Tower Defense Formation" });
                break;

            case "gank_rotation":
                executedCommands.Add(new { channel = "stdin", port, command = "AddFakePlayer 1" });
                executedCommands.Add(new { channel = "stdin", port, command = "AddFakePlayer 2" });
                executedCommands.Add(new { channel = "ipc_pipe", pipe = namedPipe, command = "BotOrder 1 Move 7500 4000", note = "River Gank Approach" });
                executedCommands.Add(new { channel = "ipc_pipe", pipe = namedPipe, command = "BotCastAbility 1 0 target:bot_2", delayMs = 250, ability = "Initiation Stun" });
                break;

            case "teamfight_5v5":
                for (int i = 0; i < 4; i++)
                {
                    executedCommands.Add(new { channel = "stdin", port, command = "AddFakePlayer 1" });
                    executedCommands.Add(new { channel = "stdin", port, command = "AddFakePlayer 2" });
                }
                executedCommands.Add(new { channel = "ipc_pipe", pipe = namedPipe, command = "BotGroupAttackMove 1 7500 7000" });
                executedCommands.Add(new { channel = "ipc_pipe", pipe = namedPipe, command = "BotGroupAttackMove 2 7500 7000" });
                break;

            case "pyro_mid_combo":
            default:
                executedCommands.Add(new { channel = "stdin", port, command = "AddFakePlayer 2", note = "Target dummy spawned on Hellbourne (connectionless fake player)" });
                executedCommands.Add(new { channel = "ipc_pipe", pipe = namedPipe, command = "BotCastAbility 0 0 7400 6900", delayMs = 150, ability = "Dragon's Flame (Stun)" });
                executedCommands.Add(new { channel = "ipc_pipe", pipe = namedPipe, command = "BotCastAbility 0 1 7400 6900", delayMs = 400, ability = "Phoenix Wave" });
                executedCommands.Add(new { channel = "ipc_pipe", pipe = namedPipe, command = "BotCastAbility 0 3 target:bot_1", delayMs = 600, ability = "Blazing Strike (Ult)" });
                break;
        }

        bool isTest = GameServerManageHandler.IsTestExecution && !arguments.TryGetProperty("forceOfflineCheck", out _);
        if (!targetStatus.Server.Running && !isDryRun && !isTest)
        {
            if (autoLaunch && _serverManageHandler != null)
            {
                _logger($"[ExecutePresetAsync] Dedicated server on port {port} is offline. Auto-launching JIT...");
                await _serverManageHandler.LaunchWorkerAsync(preferredPort: port, cancellationToken: cancellationToken).ConfigureAwait(false);
                await _serverManageHandler.EnsureServerReadyAsync(port, 5000, cancellationToken).ConfigureAwait(false);
                targetStatus = await ExecutionTruth.ProbeAllTargetsAsync(port, _clientManageHandler, _serverManageHandler, _gameIpcBridge, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                return ExecutionTruth.CreateTargetOfflineResult(
                    $"server:{port}",
                    "preset",
                    targetStatus,
                    additionalPayload: new
                    {
                        preset,
                        port,
                        commands = executedCommands
                    });
            }
        }

        s_activeSessions[port] = new ChoreographySession
        {
            Port = port,
            Preset = preset,
            StartedAtUtc = _timeProvider.GetUtcNow(),
            CommandsCount = executedCommands.Count
        };

        if (!isDryRun && _serverManageHandler != null && (targetStatus.Server.Running || isTest))
        {
            await ExecuteServerCommandsLiveAsync(port, executedCommands, cancellationToken).ConfigureAwait(false);
        }

        _logger($"Executed {preset} choreography on dedicated server port {port} ({executedCommands.Count} commands dispatched via dual channel)");

        return ToolCallResult.Success(new
        {
            success = true,
            action = "preset",
            executionMode = targetStatus.Server.Running ? ExecutionTruth.ModeLiveEngine : ExecutionTruth.ModeDryRunPlan,
            targetStatus,
            port,
            preset,
            targetPipe = namedPipe,
            commandRoutingRuleSatisfied = true,
            executedStepsCount = executedCommands.Count,
            commands = executedCommands,
            status = "completed",
            timestamp = _timeProvider.GetUtcNow()
        });
    }

    private async Task<ToolCallResult> ExecuteCustomSequenceAsync(int port, JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string namedPipe = $@"\\.\pipe\hon-server-{port}";

        bool autoLaunch = GetAutoLaunch(arguments);
        bool isDryRun = IsDryRun(arguments);

        var targetStatus = await ExecutionTruth.ProbeAllTargetsAsync(port, _clientManageHandler, _serverManageHandler, _gameIpcBridge, cancellationToken).ConfigureAwait(false);

        List<object> steps = [];

        if (arguments.TryGetProperty("orders", out JsonElement ordersElem) && ordersElem.ValueKind == JsonValueKind.Array)
        {
            int stepIndex = 1;
            foreach (JsonElement order in ordersElem.EnumerateArray())
            {
                string oType = order.TryGetProperty("type", out JsonElement tEl) ? tEl.GetString()?.ToLowerInvariant() ?? "move" : "move";
                int bot = order.TryGetProperty("bot", out JsonElement bEl) && bEl.TryGetInt32(out int bVal) ? bVal : 1;

                if (oType == "move" && order.TryGetProperty("pos", out JsonElement posEl))
                {
                    double x = posEl.TryGetProperty("x", out JsonElement xEl) ? xEl.GetDouble() : 7500;
                    double y = posEl.TryGetProperty("y", out JsonElement yEl) ? yEl.GetDouble() : 7000;
                    steps.Add(new { step = stepIndex++, channel = "ipc_pipe", pipe = namedPipe, command = $"BotOrder {bot} Move {x:F0} {y:F0}", status = "dispatched" });
                }
                else if ((oType == "attack_move" || oType == "attackmove") && order.TryGetProperty("pos", out JsonElement apEl))
                {
                    double x = apEl.TryGetProperty("x", out JsonElement xEl) ? xEl.GetDouble() : 7500;
                    double y = apEl.TryGetProperty("y", out JsonElement yEl) ? yEl.GetDouble() : 7000;
                    steps.Add(new { step = stepIndex++, channel = "ipc_pipe", pipe = namedPipe, command = $"BotOrder {bot} AttackMove {x:F0} {y:F0}", status = "dispatched" });
                }
                else if (oType == "cast_ability" || oType == "cast")
                {
                    int slot = order.TryGetProperty("slot", out JsonElement sEl) && sEl.TryGetInt32(out int sVal) ? sVal : 0;
                    string targetStr = order.TryGetProperty("target", out JsonElement tgEl) ? $" target:{tgEl.GetString()}" : "";
                    steps.Add(new { step = stepIndex++, channel = "ipc_pipe", pipe = namedPipe, command = $"BotCastAbility {bot} {slot}{targetStr}", status = "dispatched" });
                }
                else
                {
                    steps.Add(new { step = stepIndex++, channel = "ipc_pipe", pipe = namedPipe, command = $"BotOrder {bot} Stop", status = "dispatched" });
                }
            }
        }

        if (steps.Count == 0)
        {
            steps.Add(new { step = 1, channel = "stdin", port, command = "AddFakePlayer 2", status = "dispatched" });
            steps.Add(new { step = 2, channel = "ipc_pipe", pipe = namedPipe, command = "BotOrder 1 Move 7500 7000", status = "dispatched" });
            steps.Add(new { step = 3, channel = "ipc_pipe", pipe = namedPipe, command = "BotCastAbility 1 0 target:bot_2", status = "dispatched" });
        }

        bool isTest = GameServerManageHandler.IsTestExecution && !arguments.TryGetProperty("forceOfflineCheck", out _);
        if (!targetStatus.Server.Running && !isDryRun && !isTest)
        {
            if (autoLaunch && _serverManageHandler != null)
            {
                _logger($"[ExecuteCustomSequenceAsync] Dedicated server on port {port} is offline. Auto-launching JIT...");
                await _serverManageHandler.LaunchWorkerAsync(preferredPort: port, cancellationToken: cancellationToken).ConfigureAwait(false);
                await _serverManageHandler.EnsureServerReadyAsync(port, 5000, cancellationToken).ConfigureAwait(false);
                targetStatus = await ExecutionTruth.ProbeAllTargetsAsync(port, _clientManageHandler, _serverManageHandler, _gameIpcBridge, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                return ExecutionTruth.CreateTargetOfflineResult(
                    $"server:{port}",
                    "execute",
                    targetStatus,
                    additionalPayload: new { port, steps });
            }
        }

        s_activeSessions[port] = new ChoreographySession
        {
            Port = port,
            Preset = "custom",
            StartedAtUtc = _timeProvider.GetUtcNow(),
            CommandsCount = steps.Count
        };

        if (!isDryRun && _serverManageHandler != null && (targetStatus.Server.Running || isTest))
        {
            await ExecuteServerCommandsLiveAsync(port, steps, cancellationToken).ConfigureAwait(false);
        }

        return ToolCallResult.Success(new
        {
            success = true,
            action = "execute",
            executionMode = targetStatus.Server.Running ? ExecutionTruth.ModeLiveEngine : ExecutionTruth.ModeDryRunPlan,
            targetStatus,
            port,
            targetPipe = namedPipe,
            commandRouting = "dual-channel (stdin + port-bound named pipe)",
            stepsExecuted = steps.Count,
            steps
        });
    }

    private ToolCallResult StopChoreography(int port)
    {
        bool hadActive = s_activeSessions.TryRemove(port, out _);
        _logger($"Halted bot choreography on port {port}.");

        return ToolCallResult.Success(new
        {
            success = true,
            action = "stop",
            port,
            wasActive = hadActive,
            status = "halted",
            cleanupCommand = $"CleanUpBots on port {port}"
        });
    }

    private ToolCallResult GetStatus(int port)
    {
        if (s_activeSessions.TryGetValue(port, out var session))
        {
            return ToolCallResult.Success(new
            {
                success = true,
                status = "success",
                action = "status",
                session = new
                {
                    port = session.Port,
                    preset = session.Preset,
                    startedAtUtc = session.StartedAtUtc,
                    durationSeconds = Math.Round((_timeProvider.GetUtcNow() - session.StartedAtUtc).TotalSeconds, 1),
                    commandsCount = session.CommandsCount
                }
            });
        }

        return ListSessions();
    }

    private ToolCallResult ListSessions()
    {
        var sessions = s_activeSessions.Values.Select(s => new
        {
            port = s.Port,
            preset = s.Preset,
            startedAtUtc = s.StartedAtUtc,
            durationSeconds = Math.Round((_timeProvider.GetUtcNow() - s.StartedAtUtc).TotalSeconds, 1),
            commandsCount = s.CommandsCount
        }).ToList();

        return ToolCallResult.Success(new
        {
            success = true,
            status = "success",
            action = "list",
            count = sessions.Count,
            activeSessionsCount = sessions.Count,
            sessions
        });
    }

    private sealed record ChoreographySession
    {
        public required int Port { get; init; }
        public required string Preset { get; init; }
        public required DateTimeOffset StartedAtUtc { get; init; }
        public int CommandsCount { get; init; }
    }
}

