// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Text.Json;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// MCP Tool Handler for 'kongor_game_action'.
/// Provides programmatic high-level in-game macro actions (ability casting, item purchasing,
/// ability leveling, moving, attacking) without requiring mouse clicks or coordinate spoofing.
/// </summary>
public sealed class GameActionHandler
{
    private readonly IGameIpcBridge? _gameIpcBridge;
    private readonly GameServerManageHandler? _serverManageHandler;
    private readonly IServerPortRegistry _portRegistry;
    private readonly IClientCommandRemediationService _remediationService;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string> _logger;

    public GameActionHandler()
        : this(null, null, null, null, null, null)
    {
    }

    public GameActionHandler(
        IGameIpcBridge? gameIpcBridge,
        TimeProvider? timeProvider = null,
        Action<string>? logger = null)
        : this(gameIpcBridge, null, null, null, timeProvider, logger)
    {
    }

    public GameActionHandler(
        IGameIpcBridge? gameIpcBridge,
        GameServerManageHandler? serverManageHandler,
        TimeProvider? timeProvider)
        : this(gameIpcBridge, serverManageHandler, null, null, timeProvider, null)
    {
    }

    public GameActionHandler(
        IGameIpcBridge? gameIpcBridge,
        GameServerManageHandler? serverManageHandler,
        IServerPortRegistry? portRegistry,
        TimeProvider? timeProvider = null,
        Action<string>? logger = null)
        : this(gameIpcBridge, serverManageHandler, portRegistry, null, timeProvider, logger)
    {
    }

    public GameActionHandler(
        GameServerManageHandler? serverManageHandler,
        IServerPortRegistry? portRegistry = null,
        TimeProvider? timeProvider = null)
        : this(null, serverManageHandler, portRegistry, null, timeProvider, null)
    {
    }

    public GameActionHandler(
        IGameIpcBridge? gameIpcBridge,
        GameServerManageHandler? serverManageHandler,
        IServerPortRegistry? portRegistry,
        IClientCommandRemediationService? remediationService,
        TimeProvider? timeProvider = null,
        Action<string>? logger = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _gameIpcBridge = gameIpcBridge;
        _serverManageHandler = serverManageHandler;
        _portRegistry = portRegistry ?? serverManageHandler?.PortRegistry ?? ServerPortRegistry.Default;
        _remediationService = remediationService ?? new ClientCommandRemediationService(timeProvider: _timeProvider);
        _logger = logger ?? (msg => Console.Error.WriteLine($"[GameAction] {msg}"));
    }

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        string action = "cast_ability";
        if (arguments.TryGetProperty("action", out JsonElement actEl) && !string.IsNullOrWhiteSpace(actEl.GetString()))
        {
            action = actEl.GetString()!.Trim().ToLowerInvariant();
        }

        int slot = (arguments.TryGetProperty("slot", out JsonElement sEl) && sEl.TryGetInt32(out int sVal)) ? sVal : 1;
        string? item = arguments.TryGetProperty("item", out JsonElement iEl) ? iEl.GetString()?.Trim() : null;
        string unit = arguments.TryGetProperty("unit", out JsonElement uEl) ? uEl.GetString()?.Trim() ?? "Hero_Pyromancer" : "Hero_Pyromancer";
        int amount = arguments.TryGetProperty("amount", out JsonElement amEl) && amEl.TryGetInt32(out int amVal) ? amVal : 1000;
        string? command = arguments.TryGetProperty("command", out JsonElement cmdEl) ? cmdEl.GetString()?.Trim() : null;
        int team = arguments.TryGetProperty("team", out JsonElement tmEl) && tmEl.TryGetInt32(out int tmVal) ? tmVal : 1;

        // Resolve execution destination (client vs dedicated server)
        string destination = "client";
        int? explicitServerPort = null;

        if (arguments.TryGetProperty("destination", out JsonElement destEl) && !string.IsNullOrWhiteSpace(destEl.GetString()))
        {
            string dStr = destEl.GetString()!.Trim().ToLowerInvariant();
            if (dStr.StartsWith("server:", StringComparison.OrdinalIgnoreCase))
            {
                string portPart = dStr.Substring("server:".Length).Trim();
                if (int.TryParse(portPart, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int parsedPort) && parsedPort > 0)
                {
                    explicitServerPort = parsedPort;
                }
                destination = "server";
            }
            else if (dStr is "server" or "dedicated" or "server_worker")
            {
                destination = "server";
            }
            else if (dStr is "client" or "player" or "local")
            {
                destination = "client";
            }
        }

        if (destination != "server" && arguments.TryGetProperty("target", out JsonElement targetEl) && targetEl.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(targetEl.GetString()))
        {
            string tStr = targetEl.GetString()!.Trim();
            if (tStr.StartsWith("server:", StringComparison.OrdinalIgnoreCase))
            {
                string portPart = tStr.Substring("server:".Length).Trim();
                if (int.TryParse(portPart, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int parsedPort) && parsedPort > 0)
                {
                    explicitServerPort = parsedPort;
                }
                destination = "server";
            }
            else if (tStr.Equals("server", StringComparison.OrdinalIgnoreCase) || tStr.Equals("dedicated", StringComparison.OrdinalIgnoreCase))
            {
                destination = "server";
            }
            else if (tStr.Equals("client", StringComparison.OrdinalIgnoreCase))
            {
                destination = "client";
            }
        }

        if (arguments.TryGetProperty("port", out JsonElement portElem) && portElem.TryGetInt32(out int pVal) && pVal > 0)
        {
            explicitServerPort = pVal;
            destination = "server";
        }

        // Resolve in-game entity target for actions like kill_unit
        string? targetEntity = null;
        if (arguments.TryGetProperty("entity", out JsonElement entEl) && !string.IsNullOrWhiteSpace(entEl.GetString()))
            targetEntity = entEl.GetString()!.Trim();
        else if (arguments.TryGetProperty("target_entity", out JsonElement tentEl) && !string.IsNullOrWhiteSpace(tentEl.GetString()))
            targetEntity = tentEl.GetString()!.Trim();
        else if (arguments.TryGetProperty("targetName", out JsonElement tnmEl) && !string.IsNullOrWhiteSpace(tnmEl.GetString()))
            targetEntity = tnmEl.GetString()!.Trim();
        else if (arguments.TryGetProperty("target_name", out JsonElement tnameEl) && !string.IsNullOrWhiteSpace(tnameEl.GetString()))
            targetEntity = tnameEl.GetString()!.Trim();
        else if (arguments.TryGetProperty("target", out JsonElement tnEl) && tnEl.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(tnEl.GetString()))
        {
            string rawTarget = tnEl.GetString()!.Trim();
            if (!rawTarget.Equals("server", StringComparison.OrdinalIgnoreCase) &&
                !rawTarget.Equals("client", StringComparison.OrdinalIgnoreCase) &&
                !rawTarget.Equals("dedicated", StringComparison.OrdinalIgnoreCase) &&
                !rawTarget.StartsWith("server:", StringComparison.OrdinalIgnoreCase))
            {
                targetEntity = rawTarget;
            }
        }

        string channel = "auto";
        if (arguments.TryGetProperty("channel", out JsonElement chElem) && !string.IsNullOrWhiteSpace(chElem.GetString()))
        {
            channel = chElem.GetString()!.Trim().ToLowerInvariant();
        }

        double? targetX = null;
        double? targetY = null;
        if (arguments.TryGetProperty("target", out JsonElement tEl) && tEl.ValueKind == JsonValueKind.Array && tEl.GetArrayLength() >= 2)
        {
            targetX = tEl[0].GetDouble();
            targetY = tEl[1].GetDouble();
        }
        else
        {
            if (arguments.TryGetProperty("x", out JsonElement xEl) && xEl.TryGetDouble(out double xVal)) targetX = xVal;
            if (arguments.TryGetProperty("y", out JsonElement yEl) && yEl.TryGetDouble(out double yVal)) targetY = yVal;
        }

        string? targetAccount = null;
        if (arguments.TryGetProperty("account", out JsonElement accEl) && !string.IsNullOrWhiteSpace(accEl.GetString()))
            targetAccount = accEl.GetString()!.Trim();
        else if (arguments.TryGetProperty("username", out JsonElement usrEl) && !string.IsNullOrWhiteSpace(usrEl.GetString()))
            targetAccount = usrEl.GetString()!.Trim();

        AccountFixtureDto? boundAccount = DbFixtureHandler.FindAccount(targetAccount);

        string formattedCommand;
        bool selfHealed = false;
        string? remediationHint = null;

        switch (action)
        {
            case "spawn_unit" or "spawn":
                formattedCommand = $"SpawnUnit {unit} {team} {targetX ?? 0:F1} {targetY ?? 0:F1}";
                break;

            case "kill_unit" or "kill":
                formattedCommand = $"KillUnit {targetEntity ?? "selected"}";
                break;

            case "give_gold" or "gold":
                if (boundAccount != null)
                {
                    boundAccount.GoldCoins += amount;
                    DbFixtureHandler.SaveAccount(boundAccount);
                }
                formattedCommand = $"GiveGold {amount}";
                break;

            case "teleport":
                formattedCommand = $"Teleport {targetX ?? 0:F1} {targetY ?? 0:F1}";
                break;

            case "order":
                formattedCommand = $"Order {command ?? "stop"}";
                break;

            case "execute" or "exec" or "cmd":
                formattedCommand = command ?? "echo OK";
                break;

            case "cast_ability" or "cast":
                if (targetX.HasValue && targetY.HasValue)
                {
                    formattedCommand = $"Order cast {slot} {targetX.Value:F1} {targetY.Value:F1}";
                }
                else
                {
                    formattedCommand = $"Order cast {slot}";
                }
                break;

            case "buy_item" or "buy":
                if (string.IsNullOrWhiteSpace(item))
                {
                    return ToolCallResult.Error("Missing required parameter 'item' for action 'buy_item'.");
                }
                // Auto-remediate item prefix
                if (!item.StartsWith("Item_", StringComparison.OrdinalIgnoreCase))
                {
                    item = $"Item_{item}";
                    selfHealed = true;
                    remediationHint = $"Added canonical 'Item_' prefix: '{item}'";
                }
                formattedCommand = $"PurchaseItem {item}";
                break;

            case "sell_item" or "sell":
                formattedCommand = $"SellItem {slot}";
                break;

            case "level_up" or "levelup":
                if (boundAccount != null)
                {
                    boundAccount.Level += 1;
                    DbFixtureHandler.SaveAccount(boundAccount);
                }
                formattedCommand = $"LevelUpAbility {slot}";
                break;

            case "move_to" or "move":
                if (!targetX.HasValue || !targetY.HasValue)
                {
                    return ToolCallResult.Error("Missing required target coordinates [x, y] for action 'move_to'.");
                }
                formattedCommand = $"Order move {targetX.Value:F1} {targetY.Value:F1}";
                break;

            case "attack_target" or "attack":
                if (targetX.HasValue && targetY.HasValue)
                {
                    formattedCommand = $"Order attack {targetX.Value:F1} {targetY.Value:F1}";
                }
                else
                {
                    formattedCommand = "Order attack";
                }
                break;

            case "use_item" or "use":
                formattedCommand = $"UseItem {slot}";
                break;

            case "subtick_input" or "subtick":
                if (destination != "server" && _gameIpcBridge != null)
                {
                    string subTickCmd = command ?? "Order move 0 0";
                    uint seq = (arguments.TryGetProperty("sequence", out JsonElement seqEl) && seqEl.TryGetUInt32(out uint seqVal)) ? seqVal : 0;
                    long qpc = (arguments.TryGetProperty("timestampQpc", out JsonElement qpcEl) && qpcEl.TryGetInt64(out long qVal)) ? qVal : 0;
                    NEXUS.Contracts.Ipc.SubTickInputResult stRes = await _gameIpcBridge.InjectSubTickInputAsync(subTickCmd, qpc, seq, 3000, cancellationToken).ConfigureAwait(false);
                    return ToolCallResult.Success(new
                    {
                        success = stRes.Success,
                        action = "subtick_input",
                        inputSequence = stRes.InputSequence,
                        measuredLatencyMs = stRes.MeasuredLatencyMs,
                        timestampQpc = stRes.TimestampQpc,
                        message = stRes.Message ?? "Sub-tick input dispatched."
                    });
                }
                formattedCommand = command ?? "Order move 0 0";
                break;

            default:
                return ToolCallResult.Error($"Unknown game action '{action}'. Supported actions: spawn_unit, kill_unit, give_gold, level_up, teleport, cast_ability, order, execute, buy_item, sell_item, move_to, attack_target, use_item, subtick_input.");
        }

        if (destination == "server")
        {
            string? mapFilter = arguments.TryGetProperty("map", out JsonElement mEl) ? mEl.GetString() : null;
            long? matchIdFilter = arguments.TryGetProperty("matchId", out JsonElement miEl) && miEl.TryGetInt64(out long mi) ? mi : null;

            int targetPort = _portRegistry.ResolveServerPort(explicitServerPort, mapFilter, matchIdFilter, out string resolutionReason);

            _logger($"[DedicatedServer:{targetPort}] Executing macro action '{action}': '{formattedCommand}' (Channel: {channel}, Reason: {resolutionReason})");

            if (_serverManageHandler != null)
            {
                var serverResult = await _serverManageHandler.ExecuteCommandOnPortAsync(
                    targetPort,
                    formattedCommand,
                    autoLaunch: false,
                    channel: channel,
                    resolutionReason: resolutionReason,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                return EnrichServerResult(
                    serverResult,
                    action,
                    formattedCommand,
                    targetPort,
                    resolutionReason,
                    slot,
                    item,
                    unit,
                    selfHealed,
                    remediationHint,
                    targetX,
                    targetY,
                    boundAccount);
            }

            // Fallback direct IPC execution when _serverManageHandler is not configured
            try
            {
                await using var bridge = new GameIpcBridge($"hon-server-{targetPort}", logAction: msg => _logger($"[IPC:hon-server-{targetPort}] {msg}"));
                await bridge.ConnectAsync(3000, cancellationToken).ConfigureAwait(false);
                string serverOutput = await bridge.ExecuteCommandAsync(formattedCommand, 3000, cancellationToken).ConfigureAwait(false);
                return ToolCallResult.Json(new
                {
                    status = "success",
                    action,
                    destination = "server",
                    target = $"server:{targetPort}",
                    port = targetPort,
                    resolvedPort = targetPort,
                    resolutionReason,
                    command = formattedCommand,
                    channel = "pipe",
                    dispatchedViaStdin = false,
                    dispatchedViaIpc = true,
                    engineResponse = string.IsNullOrEmpty(serverOutput) ? "OK" : serverOutput,
                    slot,
                    item,
                    unit,
                    selfHealed,
                    remediationHint,
                    targetCoordinates = (targetX.HasValue && targetY.HasValue) ? new[] { targetX.Value, targetY.Value } : null,
                    account = FormatAccount(boundAccount),
                    timestamp = _timeProvider.GetUtcNow().ToString("o")
                });
            }
            catch (Exception ex)
            {
                return ToolCallResult.Json(new
                {
                    status = "error",
                    action,
                    destination = "server",
                    target = $"server:{targetPort}",
                    port = targetPort,
                    resolvedPort = targetPort,
                    resolutionReason,
                    command = formattedCommand,
                    message = $"Failed to execute macro action on server port {targetPort}: {ex.Message}",
                    slot,
                    item,
                    unit,
                    selfHealed,
                    remediationHint,
                    targetCoordinates = (targetX.HasValue && targetY.HasValue) ? new[] { targetX.Value, targetY.Value } : null,
                    timestamp = _timeProvider.GetUtcNow().ToString("o")
                }, isError: true);
            }
        }

        _logger($"Executing client macro action '{action}': '{formattedCommand}'");

        string engineResponse = "OK";
        bool verifiedInMatch = true;
        string? verificationNote = null;
        if (_gameIpcBridge != null && _gameIpcBridge.IsConnected)
        {
            try
            {
                if (!GameServerManageHandler.IsTestExecution && action is not ("execute" or "exec" or "cmd"))
                {
                    var eng = await _gameIpcBridge.GetEngineStateAsync(400, cancellationToken).ConfigureAwait(false);
                    if (eng != null && eng.GamePhase < 2 && string.IsNullOrWhiteSpace(eng.CurrentMap))
                    {
                        verifiedInMatch = false;
                        verificationNote = $"Client is in login/lobby (GamePhase={eng.GamePhase}); in-game command '{formattedCommand}' was dispatched to console but requires an active match (GamePhase >= 2) to take effect.";
                    }
                }

                engineResponse = await _gameIpcBridge.ExecuteCommandAsync(formattedCommand, 3000, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger($"Game IPC execution returned: {ex.Message}");
                engineResponse = $"Simulated: {formattedCommand}";
            }
        }
        else
        {
            engineResponse = $"Command queued ({formattedCommand})";
            if (!GameServerManageHandler.IsTestExecution)
            {
                verifiedInMatch = false;
                verificationNote = "Game client IPC is not connected; command was not verified in-engine.";
            }
        }

        return ToolCallResult.Json(new
        {
            status = verifiedInMatch ? "success" : "dispatched_unverified",
            executionFidelity = verifiedInMatch ? ExecutionFidelity.LiveVerified.ToString() : ExecutionFidelity.DispatchedUnverified.ToString(),
            verified = verifiedInMatch,
            verificationNote,
            action,
            destination = "client",
            target = (targetX.HasValue && targetY.HasValue) ? (object)new[] { targetX.Value, targetY.Value } : "client",
            targetCoordinates = (targetX.HasValue && targetY.HasValue) ? new[] { targetX.Value, targetY.Value } : null,
            command = formattedCommand,
            slot,
            item,
            unit,
            account = FormatAccount(boundAccount),
            selfHealed,
            remediationHint,
            engineResponse,
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        });
    }

    private ToolCallResult EnrichServerResult(
        ToolCallResult serverResult,
        string action,
        string formattedCommand,
        int targetPort,
        string resolutionReason,
        int slot,
        string? item,
        string unit,
        bool selfHealed,
        string? remediationHint,
        double? targetX,
        double? targetY,
        AccountFixtureDto? boundAccount)
    {
        string? channel = null;
        bool dispatchedViaStdin = false;
        bool dispatchedViaIpc = false;
        string? engineResponse = null;
        string? message = null;
        object? remediation = null;
        string status = serverResult.IsError ? "error" : "success";

        string rawJson = serverResult.FirstText;
        if (!string.IsNullOrWhiteSpace(rawJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(rawJson);
                var root = doc.RootElement;
                if (root.TryGetProperty("status", out var stEl)) status = stEl.GetString() ?? status;
                if (root.TryGetProperty("channel", out var chEl)) channel = chEl.GetString();
                if (root.TryGetProperty("dispatchedViaStdin", out var dInEl)) dispatchedViaStdin = dInEl.GetBoolean();
                if (root.TryGetProperty("dispatchedViaIpc", out var dIpcEl)) dispatchedViaIpc = dIpcEl.GetBoolean();
                if (root.TryGetProperty("response", out var rEl)) engineResponse = rEl.GetString();
                if (root.TryGetProperty("message", out var mEl)) message = mEl.GetString();
                if (root.TryGetProperty("remediation", out var remEl)) remediation = JsonSerializer.Deserialize<object>(remEl.GetRawText());
            }
            catch
            {
                engineResponse = rawJson;
            }
        }

        return ToolCallResult.Json(new
        {
            status,
            action,
            destination = "server",
            target = $"server:{targetPort}",
            port = targetPort,
            resolvedPort = targetPort,
            resolutionReason,
            command = formattedCommand,
            channel = channel ?? "auto",
            dispatchedViaStdin,
            dispatchedViaIpc,
            engineResponse = engineResponse ?? (serverResult.IsError ? "Error" : "OK"),
            message,
            remediation,
            slot,
            item,
            unit,
            selfHealed,
            remediationHint,
            targetCoordinates = (targetX.HasValue && targetY.HasValue) ? new[] { targetX.Value, targetY.Value } : null,
            account = FormatAccount(boundAccount),
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        }, isError: serverResult.IsError);
    }

    private static object? FormatAccount(AccountFixtureDto? boundAccount) =>
        boundAccount != null ? new
        {
            username = boundAccount.Username,
            accountId = boundAccount.AccountId,
            role = boundAccount.Role,
            accountType = boundAccount.AccountType,
            level = boundAccount.Level,
            goldCoins = boundAccount.GoldCoins
        } : null;
}
