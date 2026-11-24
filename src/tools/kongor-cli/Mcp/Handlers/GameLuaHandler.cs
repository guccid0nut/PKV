// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// MCP Tool Handler for 'kongor_game_lua' (alias: 'kongor_lua').
/// Provides first-class programmatic Lua scripting control over the K2 game engine without requiring mouse clicks.
/// Supports arbitrary Lua statements, high-level automation macro actions (hide_login, practice_match,
/// apply_snapshot, collect_telemetry), firing engine triggers, and invoking widget events.
/// Dispatches via authentic dual-channel execution (client stdin stream or live in-game console actuation).
/// </summary>
public sealed partial class GameLuaHandler
{
    private readonly IGameIpcBridge? _gameIpcBridge;
    private readonly ILuaExecService _luaExecService;
    private readonly GameClientManageHandler? _clientManageHandler;
    private readonly GameServerManageHandler? _serverManageHandler;
    private readonly IUiControlService _uiControlService;
    private readonly IClientCommandRemediationService _remediationService;
    private readonly IServerPortRegistry _portRegistry;
    private readonly IWindowCapturer? _windowCapturer;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string> _logger;
    private readonly ITaoClient _taoClient;

    public GameLuaHandler(
        GameClientManageHandler? clientManageHandler = null,
        GameServerManageHandler? serverManageHandler = null,
        IUiControlService? uiControlService = null,
        IClientCommandRemediationService? remediationService = null,
        TimeProvider? timeProvider = null,
        Action<string>? logger = null,
        IGameIpcBridge? gameIpcBridge = null,
        IServerPortRegistry? portRegistry = null,
        IWindowCapturer? windowCapturer = null,
        ILuaExecService? luaExecService = null,
        ITaoClient? taoClient = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _clientManageHandler = clientManageHandler;
        _serverManageHandler = serverManageHandler;
        _portRegistry = portRegistry ?? serverManageHandler?.PortRegistry ?? ServerPortRegistry.Default;
        _uiControlService = uiControlService ?? clientManageHandler?.UiControlService ?? new UiControlService(timeProvider: _timeProvider);
        _remediationService = remediationService ?? clientManageHandler?.RemediationService ?? new ClientCommandRemediationService(timeProvider: _timeProvider);
        _logger = logger ?? (msg => Console.Error.WriteLine($"[GameLua] {msg}"));
        _gameIpcBridge = gameIpcBridge ?? clientManageHandler?.IpcBridge;
        _windowCapturer = windowCapturer;
        _luaExecService = luaExecService ?? new LuaExecService(_gameIpcBridge, _remediationService, _logger);

        NexusMailboxIpcBridge? mailboxBridge = _gameIpcBridge as NexusMailboxIpcBridge
            ?? (_gameIpcBridge as CompositeGameIpcBridge)?.Tertiary as NexusMailboxIpcBridge;
        _taoClient = taoClient ?? new TaoClient(
            mailboxBridge: mailboxBridge,
            fallbackBridge: _gameIpcBridge,
            logger: _logger);
    }

    public ILuaExecService LuaExecService => _luaExecService;
    public ITaoClient TaoClient => _taoClient;

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        // 1. Resolve requested action
        string action = "code";
        if (arguments.TryGetProperty("action", out JsonElement actEl) && !string.IsNullOrWhiteSpace(actEl.GetString()))
        {
            action = actEl.GetString()!.Trim().ToLowerInvariant();
        }

        // Diagnostic actions
        if (action is "errors" or "history")
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

        string target = "client";
        int? explicitServerPort = null;
        int? explicitClientPid = null;
        if (arguments.TryGetProperty("target", out JsonElement targetEl) && !string.IsNullOrWhiteSpace(targetEl.GetString()))
        {
            target = targetEl.GetString()!.Trim().ToLowerInvariant();
            if (target.StartsWith("server:", StringComparison.OrdinalIgnoreCase))
            {
                string portPart = target.Substring("server:".Length).Trim();
                if (int.TryParse(portPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedPort) && parsedPort > 0)
                {
                    explicitServerPort = parsedPort;
                }
                target = "server";
            }
            else if (target.StartsWith("client:", StringComparison.OrdinalIgnoreCase))
            {
                string pidPart = target.Substring("client:".Length).Trim();
                if (int.TryParse(pidPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedPid) && parsedPid > 0)
                {
                    explicitClientPid = parsedPid;
                }
                target = "client";
            }
            else if (target.StartsWith("pid:", StringComparison.OrdinalIgnoreCase))
            {
                string pidPart = target.Substring("pid:".Length).Trim();
                if (int.TryParse(pidPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedPid) && parsedPid > 0)
                {
                    explicitClientPid = parsedPid;
                }
                target = "client";
            }

        }

        if (!explicitClientPid.HasValue)
        {
            if ((arguments.TryGetProperty("process_id", out JsonElement pidEl) ||
                 arguments.TryGetProperty("processId", out pidEl) ||
                 arguments.TryGetProperty("pid", out pidEl)) &&
                pidEl.TryGetInt32(out int pVal) && pVal > 0)
            {
                explicitClientPid = pVal;
            }
        }

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

        string channel = "auto";
        if (arguments.TryGetProperty("channel", out JsonElement chEl) && !string.IsNullOrWhiteSpace(chEl.GetString()))
        {
            channel = chEl.GetString()!.Trim().ToLowerInvariant();
        }

        int timeoutMs = (arguments.TryGetProperty("timeoutMs", out JsonElement toEl) && toEl.TryGetInt32(out int toVal) && toVal > 0)
            ? toVal
            : 3000;

        string? verifyWidgetName = null;
        if ((arguments.TryGetProperty("verifyWidget", out JsonElement vwElem) ||
             arguments.TryGetProperty("verify_widget", out vwElem) ||
             arguments.TryGetProperty("assertWidget", out vwElem) ||
             arguments.TryGetProperty("assert_widget", out vwElem)) &&
            !string.IsNullOrWhiteSpace(vwElem.GetString()))
        {
            verifyWidgetName = vwElem.GetString()!.Trim();
        }

        string? verifyStateExpr = null;
        if ((arguments.TryGetProperty("verifyState", out JsonElement vsElem) ||
             arguments.TryGetProperty("verify_state", out vsElem) ||
             arguments.TryGetProperty("assertState", out vsElem) ||
             arguments.TryGetProperty("assert_state", out vsElem)) &&
            !string.IsNullOrWhiteSpace(vsElem.GetString()))
        {
            verifyStateExpr = vsElem.GetString()!.Trim();
        }

        bool captureProof = false;
        if (arguments.TryGetProperty("captureProof", out JsonElement cpElem) ||
            arguments.TryGetProperty("capture_proof", out cpElem) ||
            arguments.TryGetProperty("visualProof", out cpElem) ||
            arguments.TryGetProperty("visual_proof", out cpElem))
        {
            captureProof = cpElem.ValueKind == JsonValueKind.True ||
                           (cpElem.ValueKind == JsonValueKind.String && bool.TryParse(cpElem.GetString(), out bool cpBool) && cpBool);
        }

        bool autoLaunch = GetAutoLaunch(arguments);
        Stopwatch sw = Stopwatch.StartNew();

        string formattedCommand;
        bool preSelfHealed = false;
        string? originalInput = null;
        string? selfHealHint = null;
        string? selfHealCategory = null;

        // 2. Macro Action Command Formatting
        switch (action)
        {
            case "hide_login":
            {
                string accountName = Services.ClientCredentialManager.PrimaryDefaultUsername;
                if (arguments.TryGetProperty("accountName", out JsonElement accEl) && !string.IsNullOrWhiteSpace(accEl.GetString()))
                {
                    accountName = accEl.GetString()!.Trim();
                }
                else if (arguments.TryGetProperty("account", out accEl) && !string.IsNullOrWhiteSpace(accEl.GetString()))
                {
                    accountName = accEl.GetString()!.Trim();
                }
                else if (arguments.TryGetProperty("player", out accEl) && !string.IsNullOrWhiteSpace(accEl.GetString()))
                {
                    accountName = accEl.GetString()!.Trim();
                }

                string safeAccount = EscapeLuaString(accountName);
                formattedCommand = $"Script if Nexus and Nexus.HideLogin then Nexus.HideLogin('{safeAccount}') else Set('_loggedin', true); Trigger('LoginStatus', 'success', '', true); end";
                break;
            }

            case "open_store":
            {
                string? storeTab = GetStringParam(arguments, default, "tab")
                    ?? GetStringParam(arguments, default, "storeTab")
                    ?? GetStringParam(arguments, default, "page")
                    ?? GetStringParam(arguments, default, "category");

                string? subpage = GetStringParam(arguments, default, "subpage")
                    ?? GetStringParam(arguments, default, "subTab")
                    ?? GetStringParam(arguments, default, "submenu")
                    ?? GetStringParam(arguments, default, "subCategory");

                string? item = GetStringParam(arguments, default, "item")
                    ?? GetStringParam(arguments, default, "itemId")
                    ?? GetStringParam(arguments, default, "productId")
                    ?? GetStringParam(arguments, default, "product")
                    ?? GetStringParam(arguments, default, "hero")
                    ?? GetStringParam(arguments, default, "avatar");

                if (item == null && arguments.TryGetProperty("productId", out JsonElement prodEl) && prodEl.ValueKind == JsonValueKind.Number)
                {
                    item = prodEl.GetInt32().ToString();
                }

                formattedCommand = "Script " + UiFlowHandler.BuildOpenStoreLua(storeTab, subpage, item);
                break;
            }

            case "practice_match":
            {
                JsonElement optsEl = default;
                bool hasOpts = arguments.TryGetProperty("options", out optsEl) && optsEl.ValueKind == JsonValueKind.Object;

                string map = GetStringParam(arguments, hasOpts ? optsEl : default, "map") ?? "caldavar";
                string hero = GetStringParam(arguments, hasOpts ? optsEl : default, "hero") ?? "Hero_Pyromancer";
                string mode = GetStringParam(arguments, hasOpts ? optsEl : default, "mode") ?? "practice";
                string gameName = GetStringParam(arguments, hasOpts ? optsEl : default, "gameName") ?? "PracticeGame";
                bool cheats = GetBoolParam(arguments, hasOpts ? optsEl : default, "cheats", defaultValue: true);
                int team = GetIntParam(arguments, hasOpts ? optsEl : default, "team", defaultValue: 1);
                int slot = GetIntParam(arguments, hasOpts ? optsEl : default, "slot", defaultValue: 0);

                if (!hero.StartsWith("Hero_", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(hero))
                {
                    hero = $"Hero_{hero}";
                }

                string safeMap = EscapeLuaString(map);
                string safeHero = EscapeLuaString(hero);
                string safeMode = EscapeLuaString(mode);
                string safeGameName = EscapeLuaString(gameName);
                string cheatsLower = cheats ? "true" : "false";

                formattedCommand = $"Script if Nexus and Nexus.PracticeMatch then Nexus.PracticeMatch({{ map = '{safeMap}', hero = '{safeHero}', mode = '{safeMode}', gameName = '{safeGameName}', cheats = {cheatsLower}, team = {team}, slot = {slot} }}) else Cmd('Set dev true; Set cheats true; StartGame {safeMode} {safeGameName} map:{safeMap} allheroes:true'); end";
                break;
            }

            case "apply_snapshot":
            {
                JsonElement snapshotEl = default;
                bool hasSnapshot = arguments.TryGetProperty("snapshot", out snapshotEl) && snapshotEl.ValueKind == JsonValueKind.Object;

                if (!hasSnapshot)
                {
                    if (arguments.TryGetProperty("gold", out _) ||
                        arguments.TryGetProperty("level", out _) ||
                        arguments.TryGetProperty("items", out _) ||
                        arguments.TryGetProperty("pos", out _) ||
                        arguments.TryGetProperty("inventory", out _))
                    {
                        snapshotEl = arguments;
                        hasSnapshot = true;
                    }
                }

                if (!hasSnapshot)
                {
                    return ToolCallResult.Error("Missing required parameter: 'snapshot' object containing hero state mutations (e.g. gold, level, items, pos).");
                }

                string snapshotLuaTable = FormatJsonElementAsLuaTable(snapshotEl);
                formattedCommand = $"Script if Nexus and Nexus.ApplySnapshot then Nexus.ApplySnapshot({snapshotLuaTable}) end";
                break;
            }

            case "collect_telemetry":
            {
                formattedCommand = "Script if Nexus and Nexus.CollectTelemetry then Nexus.CollectTelemetry() end";
                break;
            }

            case "gc_pace":
            case "pace_gc":
            case "gc_step":
            {
                int stepSize = GetIntParam(arguments, default, "step", defaultValue: 50);
                if (stepSize <= 0) stepSize = 50;
                formattedCommand = $"Script local m1 = collectgarbage('count'); collectgarbage('step', {stepSize}); local m2 = collectgarbage('count'); Echo('LUA_GC_PACE|' .. tostring(m1) .. '|' .. tostring(m2) .. '|' .. tostring({stepSize}));";
                break;
            }

            case "gc_stats":
            case "gc_count":
            case "gc_memory":
            {
                formattedCommand = "Script local m = collectgarbage('count'); Echo('LUA_GC_STATS|' .. tostring(m));";
                break;
            }

            case "gc_collect":
            case "collect_garbage":
            {
                formattedCommand = "Script local m1 = collectgarbage('count'); collectgarbage('collect'); local m2 = collectgarbage('count'); Echo('LUA_GC_COLLECT|' .. tostring(m1) .. '|' .. tostring(m2));";
                break;
            }

            case "code":
            case "execute":
            case "run":
            case "eval":
            case "script":
            {
                // Fallback to legacy code / trigger / invokeWidget resolution
                string? code = null;
                if ((arguments.TryGetProperty("code", out JsonElement codeEl) ||
                     arguments.TryGetProperty("lua", out codeEl) ||
                     arguments.TryGetProperty("script", out codeEl)) &&
                    !string.IsNullOrWhiteSpace(codeEl.GetString()))
                {
                    code = codeEl.GetString()!.Trim();
                }

                string? trigger = null;
                if ((arguments.TryGetProperty("trigger", out JsonElement trigEl) ||
                     arguments.TryGetProperty("trigger_name", out trigEl)) &&
                    !string.IsNullOrWhiteSpace(trigEl.GetString()))
                {
                    trigger = trigEl.GetString()!.Trim();
                }

                string? widget = null;
                if ((arguments.TryGetProperty("invoke_widget", out JsonElement wEl) ||
                     arguments.TryGetProperty("invokeWidget", out wEl) ||
                     arguments.TryGetProperty("widget", out wEl)) &&
                    !string.IsNullOrWhiteSpace(wEl.GetString()))
                {
                    widget = wEl.GetString()!.Trim();
                }

                string widgetEvent = "onclick";
                if ((arguments.TryGetProperty("widget_event", out JsonElement evEl) ||
                     arguments.TryGetProperty("widgetEvent", out evEl) ||
                     arguments.TryGetProperty("event", out evEl)) &&
                    !string.IsNullOrWhiteSpace(evEl.GetString()))
                {
                    widgetEvent = evEl.GetString()!.Trim();
                }

                bool allowSelfHeal = _remediationService.IsEnabled;
                if (arguments.TryGetProperty("self_heal", out JsonElement shElem) || arguments.TryGetProperty("selfHeal", out shElem))
                {
                    allowSelfHeal = shElem.GetBoolean();
                }

                if (!string.IsNullOrWhiteSpace(code))
                {
                    if (allowSelfHeal)
                    {
                        var preAnalysis = ClientCommandRemediationService.AnalyzeCommandError(code, "", "lua");
                        if (preAnalysis.ErrorCategory == "LuaSandboxRestriction")
                        {
                            _logger($"[GameLua] Intercepted sandboxed I/O call before engine execution: '{code}'");
                            _remediationService.RecordAndRemediate(code, "attempt to index global 'io' (a nil value)", channel, target);
                            var errPayload = new
                            {
                                status = "error",
                                action,
                                target,
                                errorMessage = "K2 engine Lua sandbox violation: standard 'io' and 'os' libraries are stripped in-engine ('io' is nil). To inspect or return engine state, use 'Echo(...)' or 'return <value>' which is captured directly by TAO Mailbox IPC. For host filesystem reads/writes, use workspace tools or PowerShell.",
                                category = "LuaSandboxRestriction",
                                remediationHint = preAnalysis.RemediationHint,
                                timestamp = _timeProvider.GetUtcNow().ToString("o")
                            };
                            return ToolCallResult.Error(JsonRpcSerializer.SerializeIndented(errPayload));
                        }

                        if (preAnalysis.CanAutoRetry && !string.IsNullOrWhiteSpace(preAnalysis.ImprovedCommand) && !preAnalysis.ImprovedCommand.Equals(code, StringComparison.OrdinalIgnoreCase))
                        {
                            _logger($"[GameLua] Auto-remediated Lua expression: '{code}' -> '{preAnalysis.ImprovedCommand}'");
                            preSelfHealed = true;
                            originalInput = code;
                            selfHealHint = preAnalysis.RemediationHint;
                            selfHealCategory = preAnalysis.ErrorCategory;
                            code = preAnalysis.ImprovedCommand;
                        }
                    }

                    if (action == "eval" && _taoClient != null && _taoClient.IsConnected)
                    {
                        var taoEvalResp = await _taoClient.DispatchAsync("eval", new { code }, 3000, cancellationToken).ConfigureAwait(false);
                        if (taoEvalResp.Success && taoEvalResp.Data.ValueKind == JsonValueKind.Object)
                        {
                            bool ok = taoEvalResp.Data.TryGetProperty("status", out JsonElement stEl) && stEl.GetString() == "success";
                            object? resVal = taoEvalResp.Data.TryGetProperty("result", out JsonElement rEl) ? rEl : null;
                            string? errStr = taoEvalResp.Data.TryGetProperty("error", out JsonElement eEl) ? eEl.GetString() : null;
                            sw.Stop();
                            return ToolCallResult.Json(new
                            {
                                status = ok ? "success" : "error",
                                action = "eval",
                                target,
                                channel = "tao_mailbox",
                                result = resVal,
                                error = errStr,
                                executionDurationMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                                timestamp = _timeProvider.GetUtcNow().ToString("o")
                            }, isError: !ok);
                        }
                    }

                    formattedCommand = code.StartsWith("Script ", StringComparison.OrdinalIgnoreCase) ||
                                       code.StartsWith("Cmd ", StringComparison.OrdinalIgnoreCase) ||
                                       code.StartsWith("Trigger ", StringComparison.OrdinalIgnoreCase)
                        ? code
                        : $"Script {code}";
                }
                else if (!string.IsNullOrWhiteSpace(trigger))
                {
                    formattedCommand = $"Trigger {trigger}";
                }
                else if (!string.IsNullOrWhiteSpace(widget))
                {
                    if (allowSelfHeal && widget.Equals("practice_button", StringComparison.OrdinalIgnoreCase))
                    {
                        widget = "sysbar_quickStart";
                        preSelfHealed = true;
                        originalInput = "practice_button";
                        selfHealCategory = "WidgetNotFound";
                        selfHealHint = "Replaced non-existent widget 'practice_button' with native system bar widget 'sysbar_quickStart'.";
                        _logger($"[GameLua] Auto-remediated widget name: 'practice_button' -> 'sysbar_quickStart'");
                    }
                    else if (allowSelfHeal && widget.Equals("store_container", StringComparison.OrdinalIgnoreCase))
                    {
                        widget = "store_container2";
                        preSelfHealed = true;
                        originalInput = "store_container";
                        selfHealCategory = "StoreContainerMismatch";
                        selfHealHint = "In modern K2 client builds with 'cg_store2_' enabled, replaced legacy container 'store_container' with 'store_container2'.";
                        _logger($"[GameLua] Auto-remediated widget name: 'store_container' -> 'store_container2'");
                    }

                    formattedCommand = $"Call {widget} {widgetEvent}";
                }
                else
                {
                    return ToolCallResult.Error("Missing required parameter: provide either 'code' (raw Lua code), 'trigger' (trigger name), or 'invokeWidget' (widget name).");
                }
                break;
            }

            case "tao_dispatch":
            {
                string subAction = GetStringParam(arguments, default, "subaction")
                                   ?? GetStringParam(arguments, default, "tao_action")
                                   ?? GetStringParam(arguments, default, "method")
                                   ?? "inspect_ui";
                object? payload = null;
                if (arguments.TryGetProperty("payload", out JsonElement payloadEl) ||
                    arguments.TryGetProperty("params", out payloadEl) ||
                    arguments.TryGetProperty("data", out payloadEl))
                {
                    payload = payloadEl;
                }

                if (target is "client")
                {
                    return await DispatchTaoToClientAsync(subAction, payload, action, timeoutMs, cancellationToken).ConfigureAwait(false);
                }

                string payloadJson = payload != null ? JsonSerializer.Serialize(payload) : "{}";
                string safeSubAction = EscapeLuaString(subAction);
                string safePayloadJson = EscapeLuaString(payloadJson);
                formattedCommand = $"Script if TAO and TAO.Dispatch then TAO.Dispatch('{safeSubAction}', '{safePayloadJson}') else Echo('[TAO_ERROR] TAO companion layer is not loaded'); end";
                break;
            }

            case "inspect_ui":
            {
                if (target is "server")
                {
                    return ToolCallResult.Error("UI inspection is not supported on dedicated servers (headless with 0 UI interfaces). Target must be 'client'.");
                }
                string iface = GetStringParam(arguments, default, "interface")
                               ?? GetStringParam(arguments, default, "interface_name")
                               ?? "main";
                string? widget = GetStringParam(arguments, default, "widget")
                                 ?? GetStringParam(arguments, default, "root");
                int maxDepth = GetIntParam(arguments, default, "maxDepth", 8);
                if (arguments.TryGetProperty("max_depth", out JsonElement mdEl) && mdEl.TryGetInt32(out int md))
                {
                    maxDepth = md;
                }
                bool includeHidden = true;
                if (arguments.TryGetProperty("visible_only", out JsonElement voEl))
                {
                    includeHidden = !voEl.GetBoolean();
                }
                else if (arguments.TryGetProperty("visibleOnly", out JsonElement vo2El))
                {
                    includeHidden = !vo2El.GetBoolean();
                }
                else if (arguments.TryGetProperty("includeHidden", out JsonElement ihEl))
                {
                    includeHidden = ihEl.GetBoolean();
                }
                else if (arguments.TryGetProperty("include_hidden", out JsonElement ih2El))
                {
                    includeHidden = ih2El.GetBoolean();
                }

                var payloadObj = new Dictionary<string, object?>
                {
                    ["interface"] = iface,
                    ["maxDepth"] = maxDepth,
                    ["includeHidden"] = includeHidden
                };
                if (!string.IsNullOrWhiteSpace(widget))
                {
                    payloadObj["widget"] = widget;
                }

                return await DispatchTaoToClientAsync("inspect_ui", payloadObj, action, timeoutMs, cancellationToken).ConfigureAwait(false);
            }

            case "navigate":
            {
                if (target is "server")
                {
                    return ToolCallResult.Error("Navigation is not supported on dedicated servers (headless with 0 UI interfaces). Target must be 'client'.");
                }
                string route = GetStringParam(arguments, default, "route")
                               ?? GetStringParam(arguments, default, "target")
                               ?? GetStringParam(arguments, default, "destination")
                               ?? GetStringParam(arguments, default, "to")
                               ?? "main";
                string? hero = GetStringParam(arguments, default, "hero");
                string? avatar = GetStringParam(arguments, default, "avatar");

                var navObj = new Dictionary<string, object?>
                {
                    ["route"] = route
                };
                if (!string.IsNullOrWhiteSpace(hero)) navObj["hero"] = hero;
                if (!string.IsNullOrWhiteSpace(avatar)) navObj["avatar"] = avatar;

                return await DispatchTaoToClientAsync("navigate", navObj, action, timeoutMs, cancellationToken).ConfigureAwait(false);
            }

            case "protect_queues":
            {
                formattedCommand = "Script if TAO and TAO.ProtectFrameQueues then TAO.ProtectFrameQueues() else Echo('[TAO_ERROR] TAO companion layer is not loaded'); end";
                break;
            }

            case "apply_fixture" or "fixture":
            {
                JsonElement fixEl = default;
                bool hasFix = arguments.TryGetProperty("fixture", out fixEl) && fixEl.ValueKind == JsonValueKind.Object;
                if (!hasFix)
                {
                    fixEl = arguments;
                }

                string? hero = null;
                if (fixEl.TryGetProperty("hero", out JsonElement hEl) && !string.IsNullOrWhiteSpace(hEl.GetString()))
                {
                    hero = hEl.GetString()!.Trim();
                }

                int? level = null;
                if (fixEl.TryGetProperty("level", out JsonElement lvlEl) && lvlEl.TryGetInt32(out int lvlVal))
                {
                    level = lvlVal;
                }

                int? gold = null;
                if (fixEl.TryGetProperty("gold", out JsonElement gEl) && gEl.TryGetInt32(out int gVal))
                {
                    gold = gVal;
                }

                var itemsList = new List<string>();
                if (fixEl.TryGetProperty("inventory", out JsonElement invEl) || fixEl.TryGetProperty("items", out invEl))
                {
                    if (invEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in invEl.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                            {
                                itemsList.Add(item.GetString()!.Trim());
                            }
                        }
                    }
                }

                double? posX = null, posY = null, posZ = null;
                if (fixEl.TryGetProperty("pos", out JsonElement posEl) || fixEl.TryGetProperty("position", out posEl))
                {
                    if (posEl.ValueKind == JsonValueKind.Object)
                    {
                        if (posEl.TryGetProperty("x", out JsonElement xEl) && xEl.TryGetDouble(out double x)) posX = x;
                        if (posEl.TryGetProperty("y", out JsonElement yEl) && yEl.TryGetDouble(out double y)) posY = y;
                        if (posEl.TryGetProperty("z", out JsonElement zEl) && zEl.TryGetDouble(out double z)) posZ = z;
                    }
                }

                bool resetCooldowns = true;
                if (fixEl.TryGetProperty("resetCooldowns", out JsonElement rcEl) || fixEl.TryGetProperty("reset_cooldowns", out rcEl))
                {
                    resetCooldowns = rcEl.GetBoolean();
                }

                bool refreshVitality = true;
                if (fixEl.TryGetProperty("refreshVitality", out JsonElement rvEl) || fixEl.TryGetProperty("refresh_vitality", out rvEl))
                {
                    refreshVitality = rvEl.GetBoolean();
                }

                var fixturePayload = new Dictionary<string, object?>
                {
                    ["resetCooldowns"] = resetCooldowns,
                    ["refreshVitality"] = refreshVitality
                };
                if (!string.IsNullOrWhiteSpace(hero)) fixturePayload["hero"] = hero;
                if (level.HasValue) fixturePayload["level"] = level.Value;
                if (gold.HasValue) fixturePayload["gold"] = gold.Value;
                if (itemsList.Count > 0) fixturePayload["inventory"] = itemsList;
                if (posX.HasValue && posY.HasValue)
                {
                    fixturePayload["pos"] = new { x = posX.Value, y = posY.Value, z = posZ ?? 0 };
                }

                if (target is "client")
                {
                    return await DispatchTaoToClientAsync("apply_fixture", fixturePayload, action, timeoutMs, cancellationToken).ConfigureAwait(false);
                }

                string payloadJson = JsonSerializer.Serialize(fixturePayload);
                string safePayload = EscapeLuaString(payloadJson);
                formattedCommand = $"Script if TAO and TAO.Dispatch then TAO.Dispatch('apply_fixture', '{safePayload}') elseif Nexus and Nexus.ApplySnapshot then Nexus.ApplySnapshot({{ gold = {gold ?? 0}, level = {level ?? 1} }}) end";
                break;
            }

            case "set_timescale" or "timescale":
            {
                double scale = 1.0;
                if ((arguments.TryGetProperty("scale", out JsonElement scEl) ||
                     arguments.TryGetProperty("timeScale", out scEl) ||
                     arguments.TryGetProperty("timescale", out scEl) ||
                     arguments.TryGetProperty("factor", out scEl) ||
                     arguments.TryGetProperty("value", out scEl) ||
                     (arguments.TryGetProperty("options", out JsonElement optScEl) && optScEl.ValueKind == JsonValueKind.Object &&
                      (optScEl.TryGetProperty("scale", out scEl) || optScEl.TryGetProperty("timescale", out scEl)))) &&
                    scEl.TryGetDouble(out double scVal))
                {
                    scale = scVal;
                }
                scale = Math.Clamp(scale, 0.01, 100.0);

                string payloadJson = FormattableString.Invariant($"{{\"scale\":{scale:F4}}}");
                string safePayload = EscapeLuaString(payloadJson);
                formattedCommand = FormattableString.Invariant($"Script if TAO and TAO.Dispatch then TAO.Dispatch('set_timescale', '{safePayload}') else Cmd('Set host_timeScale {scale:F4}'); end");
                break;
            }

            case "step_frames" or "step":
            {
                int frames = 1;
                if ((arguments.TryGetProperty("frames", out JsonElement frEl) ||
                     arguments.TryGetProperty("ticks", out frEl) ||
                     arguments.TryGetProperty("count", out frEl) ||
                     arguments.TryGetProperty("step", out frEl) ||
                     (arguments.TryGetProperty("options", out JsonElement optFrEl) && optFrEl.ValueKind == JsonValueKind.Object &&
                      (optFrEl.TryGetProperty("frames", out frEl) || optFrEl.TryGetProperty("ticks", out frEl) || optFrEl.TryGetProperty("count", out frEl)))) &&
                    frEl.TryGetInt32(out int frVal))
                {
                    frames = frVal;
                }
                frames = Math.Clamp(frames, 1, 1000);

                string payloadJson = $"{{\"frames\":{frames}}}";
                string safePayload = EscapeLuaString(payloadJson);
                formattedCommand = $"Script if TAO and TAO.Dispatch then TAO.Dispatch('step_frames', '{safePayload}') else Cmd('ReplayIncFrame {frames}'); end";
                break;
            }

            case "pause":
            {
                formattedCommand = "Script if TAO and TAO.Dispatch then TAO.Dispatch('pause', '{\"paused\":true}') else Cmd('Pause'); end";
                break;
            }

            case "resume" or "unpause":
            {
                formattedCommand = "Script if TAO and TAO.Dispatch then TAO.Dispatch('pause', '{\"paused\":false}') else Cmd('Unpause'); end";
                break;
            }

            default:
                return ToolCallResult.Error($"Unknown action '{action}'. Supported actions: 'hide_login', 'open_store', 'practice_match', 'apply_snapshot', 'apply_fixture', 'set_timescale', 'step_frames', 'pause', 'resume', 'collect_telemetry', 'inspect_ui', 'navigate', 'protect_queues', 'tao_dispatch', 'code', 'execute', 'errors', 'history'.");
        }

        _logger($"Dispatching Lua command to target '{target}': '{formattedCommand}'");

        string? mapFilter = arguments.TryGetProperty("map", out JsonElement mEl) ? mEl.GetString() : null;
        long? matchIdFilter = arguments.TryGetProperty("matchId", out JsonElement miEl) && miEl.TryGetInt64(out long mi) ? mi : null;
        int? rawPort = explicitServerPort ?? ((arguments.TryGetProperty("port", out JsonElement pEl) && pEl.TryGetInt32(out int pt) && pt > 0) ? pt : null);
        int targetPort = _portRegistry.ResolveServerPort(rawPort, mapFilter, matchIdFilter, out string resolutionReason);

        bool isUiOnlyAction = action is "hide_login" or "open_store" or "inspect_ui" or "navigate" or "protect_queues";
        bool allowKeystroke = (!arguments.TryGetProperty("allow_keystroke_fallback", out JsonElement akfEl) && !arguments.TryGetProperty("allowKeystrokeFallback", out akfEl)) || akfEl.GetBoolean();

        // 1. Multiplexed Target: Client and Server
        if (target is "both" or "all")
        {
            var clientTask = ExecuteClientLuaAsync(formattedCommand, action, preSelfHealed, originalInput, selfHealHint, selfHealCategory, timeoutMs, autoLaunch, allowKeystroke, sw, explicitClientPid, verifyWidgetName, verifyStateExpr, captureProof, cancellationToken);

            Task<ToolCallResult> serverTask;
            if (isUiOnlyAction)
            {
                serverTask = Task.FromResult(ToolCallResult.Json(new
                {
                    status = "skipped",
                    target = $"server:{targetPort}",
                    port = targetPort,
                    message = $"Action '{action}' is a client GUI operation and was skipped on dedicated server worker.",
                    timestamp = _timeProvider.GetUtcNow().ToString("o")
                }));
            }
            else
            {
                serverTask = ExecuteServerLuaAsync(targetPort, formattedCommand, action, autoLaunch, channel, resolutionReason, cancellationToken);
            }

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

            sw.Stop();
            return ToolCallResult.Json(new
            {
                status = overallStatus,
                action,
                target = "both",
                command = formattedCommand,
                multiplexed = true,
                port = targetPort,
                resolvedPort = targetPort,
                resolutionReason,
                executionDurationMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                client = clientPayload,
                server = serverPayload,
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            }, isError: !clientOk && !serverOk);
        }

        // 2. Target: Dedicated Server
        if (target == "server" || explicitServerPort.HasValue)
        {
            if (_serverManageHandler == null)
            {
                return ToolCallResult.Json(new
                {
                    status = "error",
                    target = $"server:{targetPort}",
                    port = targetPort,
                    message = "GameServerManageHandler is not configured.",
                    timestamp = _timeProvider.GetUtcNow().ToString("o")
                }, isError: true);
            }

            if (isUiOnlyAction)
            {
                return ToolCallResult.Json(new
                {
                    status = "skipped",
                    target = $"server:{targetPort}",
                    port = targetPort,
                    message = $"Action '{action}' is a client GUI operation and is not applicable to dedicated server.",
                    timestamp = _timeProvider.GetUtcNow().ToString("o")
                });
            }

            return await ExecuteServerLuaAsync(targetPort, formattedCommand, action, autoLaunch, channel, resolutionReason, cancellationToken).ConfigureAwait(false);
        }

        // 3. Target: Player Game Client (default)
        return await ExecuteClientLuaAsync(formattedCommand, action, preSelfHealed, originalInput, selfHealHint, selfHealCategory, timeoutMs, autoLaunch, allowKeystroke, sw, explicitClientPid, verifyWidgetName, verifyStateExpr, captureProof, cancellationToken).ConfigureAwait(false);
    }
}
