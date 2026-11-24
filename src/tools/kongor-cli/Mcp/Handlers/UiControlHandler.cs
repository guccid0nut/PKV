// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Text.Json;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// MCP Tool Handler for 'kongor_ui_control' and 'kongor_ui_controller'.
/// Dispatches universal UI automation actions: inspect, click, drag, invoke, type, key.
/// Implements Phase 4 4-step pipeline: assert_ui guard -> actuate -> wait_for -> errors_since.
/// </summary>
public sealed class UiControlHandler
{
    private readonly IUiControlService _uiControlService;
    private readonly IGameIpcBridge? _gameIpcBridge;
    private readonly ITaoClient? _taoClient;
    private readonly WaitForHandler? _waitForHandler;
    private readonly ErrorsSinceHandler? _errorsSinceHandler;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<UiControlHandler> _logger;

    public UiControlHandler(
        IUiControlService? uiControlService = null,
        IGameIpcBridge? gameIpcBridge = null,
        ITaoClient? taoClient = null,
        WaitForHandler? waitForHandler = null,
        ErrorsSinceHandler? errorsSinceHandler = null,
        TimeProvider? timeProvider = null,
        ILogger<UiControlHandler>? logger = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<UiControlHandler>.Instance;
        _uiControlService = uiControlService ?? new UiControlService(timeProvider: _timeProvider);
        _gameIpcBridge = gameIpcBridge;
        _taoClient = taoClient;
        _waitForHandler = waitForHandler;
        _errorsSinceHandler = errorsSinceHandler;
    }

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        string action = "inspect";
        if (arguments.TryGetProperty("action", out JsonElement actionElem) && !string.IsNullOrWhiteSpace(actionElem.GetString()))
        {
            action = actionElem.GetString()!.Trim().ToLowerInvariant();
        }

        string target = "hon_x64";
        if (arguments.TryGetProperty("target", out JsonElement targetElem) && !string.IsNullOrWhiteSpace(targetElem.GetString()))
        {
            target = targetElem.GetString()!.Trim();
        }

        string? elementName = null;
        if ((arguments.TryGetProperty("element_name", out JsonElement enElem) ||
             arguments.TryGetProperty("elementName", out enElem)) &&
            !string.IsNullOrWhiteSpace(enElem.GetString()))
        {
            elementName = enElem.GetString()!.Trim();
        }

        int? x = null;
        if (arguments.TryGetProperty("x", out JsonElement xElem) && xElem.TryGetInt32(out int xVal))
        {
            x = xVal;
        }

        int? y = null;
        if (arguments.TryGetProperty("y", out JsonElement yElem) && yElem.TryGetInt32(out int yVal))
        {
            y = yVal;
        }

        bool? useInvokePattern = null;
        if (arguments.TryGetProperty("use_invoke_pattern", out JsonElement uipElem) ||
            arguments.TryGetProperty("useInvokePattern", out uipElem))
        {
            useInvokePattern = uipElem.GetBoolean();
        }

        string? text = null;
        if (arguments.TryGetProperty("text", out JsonElement textElem))
        {
            text = textElem.GetString();
        }

        string? key = null;
        if ((arguments.TryGetProperty("key", out JsonElement keyElem) ||
             arguments.TryGetProperty("key_name", out keyElem) ||
             arguments.TryGetProperty("keyName", out keyElem)) &&
            !string.IsNullOrWhiteSpace(keyElem.GetString()))
        {
            key = keyElem.GetString()!.Trim();
        }

        string? command = null;
        if (arguments.TryGetProperty("command", out JsonElement cmdElem) && !string.IsNullOrWhiteSpace(cmdElem.GetString()))
        {
            command = cmdElem.GetString()!.Trim();
        }

        LogDiagnostic($"[UiControlHandler] Executing action '{action}' on target '{target}' (element: '{elementName}', coords: ({x},{y}))");

        try
        {
            return action switch
            {
                "inspect" or "status" or "state" or "dump_tree" or "get_ui_tree" or "tree" => await HandleInspectAsync(target, arguments, action, cancellationToken).ConfigureAwait(false),
                "dispatch_event" or "dispatch" or "trigger_widget" => await HandleDispatchEventAsync(arguments, cancellationToken).ConfigureAwait(false),
                "click" or "click_widget" or "click_at" or "mouse_click" or "press_button" or "button_click" or "click_element" => await HandleClickAsync(target, elementName, x, y, useInvokePattern ?? false, arguments, cancellationToken).ConfigureAwait(false),
                "drag" or "mouse_drag" or "swipe" or "drag_drop" => await HandleDragAsync(target, arguments, cancellationToken).ConfigureAwait(false),
                "invoke" => await HandleClickAsync(target, elementName, x, y, useInvokePattern ?? true, arguments, cancellationToken).ConfigureAwait(false),
                "type" or "write" or "input" or "send_text" or "enter_text" => await HandleTypeAsync(target, text, arguments, cancellationToken).ConfigureAwait(false),
                "key" or "key_tap" or "tap" or "press" or "keypress" or "send_key" or "sendkey" => await HandleKeyAsync(target, key, cancellationToken).ConfigureAwait(false),
                "console" or "exec" or "command" => await HandleConsoleAsync(target, command ?? text, cancellationToken).ConfigureAwait(false),
                "dismiss_console" or "close_console" or "put_away_console" or "clear_focus" => await HandleDismissConsoleAsync(target, cancellationToken).ConfigureAwait(false),
                _ => HandleUnknownAction(action, target)
            };
        }
        catch (Exception ex)
        {
            LogDiagnostic($"[UiControlHandler] Exception processing action '{action}': {ex}");
            var errPayload = new
            {
                status = "error",
                action,
                target,
                errorMessage = $"Exception executing kongor_ui_control: {ex.Message}",
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            };
            return ToolCallResult.Error(JsonRpcSerializer.SerializeIndented(errPayload));
        }
    }

    private async Task<ToolCallResult> HandleDispatchEventAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        string widget = string.Empty;
        if ((arguments.TryGetProperty("widget", out JsonElement wEl) ||
             arguments.TryGetProperty("element_name", out wEl) ||
             arguments.TryGetProperty("elementName", out wEl)) && !string.IsNullOrWhiteSpace(wEl.GetString()))
        {
            widget = wEl.GetString()!.Trim();
        }

        if (string.IsNullOrWhiteSpace(widget))
        {
            return ToolCallResult.Error("Missing required parameter: 'widget' (or 'element_name') for action 'dispatch_event'.");
        }

        string eventType = "click";
        if ((arguments.TryGetProperty("event", out JsonElement evEl) ||
             arguments.TryGetProperty("event_type", out evEl) ||
             arguments.TryGetProperty("eventType", out evEl)) && !string.IsNullOrWhiteSpace(evEl.GetString()))
        {
            eventType = evEl.GetString()!.Trim();
        }

        string iface = "main";
        if ((arguments.TryGetProperty("interface", out JsonElement ifEl) ||
             arguments.TryGetProperty("interface_name", out ifEl)) && !string.IsNullOrWhiteSpace(ifEl.GetString()))
        {
            iface = ifEl.GetString()!.Trim();
        }

        int? eventIndex = null;
        if ((arguments.TryGetProperty("index", out JsonElement idxEl) ||
             arguments.TryGetProperty("event_index", out idxEl) ||
             arguments.TryGetProperty("eventIndex", out idxEl)) && idxEl.TryGetInt32(out int iv))
        {
            eventIndex = iv;
        }

        bool remediated = false;
        string? remediationHint = null;
        if (widget.Equals("practice_button", StringComparison.OrdinalIgnoreCase))
        {
            widget = "sysbar_quickStart";
            remediated = true;
            remediationHint = "Replaced non-existent widget 'practice_button' with native system bar widget 'sysbar_quickStart'.";
        }

        LogDiagnostic($"[UiControlHandler] Dispatching DOM event '{eventType}' to widget '{widget}' on interface '{iface}' (Index: {eventIndex})");

        if (_gameIpcBridge != null)
        {
            try
            {
                string dispatchResult = await _gameIpcBridge.DispatchUiEventAsync(iface, widget, eventType, eventIndex, 3000, cancellationToken).ConfigureAwait(false);
                return ToolCallResult.Json(new
                {
                    status = "success",
                    action = "dispatch_event",
                    channel = "engine_internal_ipc",
                    interfaceName = iface,
                    widget,
                    eventType,
                    eventIndex,
                    selfHealed = remediated,
                    remediationHint,
                    response = dispatchResult,
                    timestamp = _timeProvider.GetUtcNow().ToString("o")
                });
            }
            catch (Exception ex)
            {
                LogDiagnostic($"[UiControlHandler] IPC event dispatch failed: {ex.Message}");
            }
        }

        return ToolCallResult.Error($"Failed to dispatch UI event to widget '{widget}'. IPC connection to game client is not available.");
    }

    private async Task<ToolCallResult> HandleInspectAsync(string target, JsonElement arguments, string action, CancellationToken cancellationToken)
    {
        string iface = "main";
        bool hasExplicitInterface = false;
        if ((arguments.TryGetProperty("interface", out JsonElement ifEl) ||
             arguments.TryGetProperty("interface_name", out ifEl) ||
             arguments.TryGetProperty("interfaceName", out ifEl)) &&
            !string.IsNullOrWhiteSpace(ifEl.GetString()))
        {
            iface = ifEl.GetString()!.Trim();
            hasExplicitInterface = true;
        }

        if (_taoClient != null && _taoClient.IsConnected &&
            (action is "dump_tree" or "get_ui_tree" or "tree" || hasExplicitInterface))
        {
            var taoTree = await _taoClient.DispatchAsync("get_ui_tree", new { @interface = iface }, 2000, cancellationToken).ConfigureAwait(false);
            if (taoTree is { Success: true })
            {
                return ToolCallResult.Json(new
                {
                    status = "success",
                    action,
                    target,
                    interfaceName = iface,
                    tree = taoTree.Data,
                    timestamp = _timeProvider.GetUtcNow().ToString("o")
                });
            }
        }

        UiInspectResult result = await _uiControlService.InspectUiAsync(target, cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            if (_taoClient != null && _taoClient.IsConnected)
            {
                var fallbackTree = await _taoClient.DispatchAsync("get_ui_tree", new { @interface = iface }, 2000, cancellationToken).ConfigureAwait(false);
                if (fallbackTree is { Success: true })
                {
                    return ToolCallResult.Json(new
                    {
                        status = "success",
                        action,
                        target,
                        interfaceName = iface,
                        tree = fallbackTree.Data,
                        timestamp = _timeProvider.GetUtcNow().ToString("o")
                    });
                }
            }

            LogDiagnostic($"[UiControlHandler] Inspect failed for target '{target}': {result.ErrorMessage}");
            var errPayload = new
            {
                status = "error",
                action = "inspect",
                target = result.Target,
                windowTitle = result.WindowTitle,
                hwnd = result.Hwnd,
                pid = result.Pid,
                errorMessage = result.ErrorMessage ?? "UI inspection failed.",
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            };
            return ToolCallResult.Error(JsonRpcSerializer.SerializeIndented(errPayload));
        }

        var responsePayload = new
        {
            status = "success",
            action = "inspect",
            target = result.Target,
            windowTitle = result.WindowTitle,
            hwnd = result.Hwnd,
            pid = result.Pid,
            controlCount = result.ControlCount,
            controls = result.Controls,
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        };

        LogDiagnostic($"[UiControlHandler] Inspect succeeded for '{target}': {result.ControlCount} controls returned.");
        return ToolCallResult.Json(responsePayload);
    }

    private async Task<ToolCallResult> HandleClickAsync(
        string target,
        string? elementName,
        int? x,
        int? y,
        bool useInvokePattern,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        string? widget = elementName;
        if (string.IsNullOrWhiteSpace(widget) &&
            (arguments.TryGetProperty("widget", out JsonElement wEl) ||
             arguments.TryGetProperty("target_widget", out wEl) ||
             arguments.TryGetProperty("element_name", out wEl) ||
             arguments.TryGetProperty("elementName", out wEl)) &&
            !string.IsNullOrWhiteSpace(wEl.GetString()))
        {
            widget = wEl.GetString()!.Trim();
        }

        string iface = "main";
        if ((arguments.TryGetProperty("interface", out JsonElement ifEl) ||
             arguments.TryGetProperty("interface_name", out ifEl) ||
             arguments.TryGetProperty("interfaceName", out ifEl)) &&
            !string.IsNullOrWhiteSpace(ifEl.GetString()))
        {
            iface = ifEl.GetString()!.Trim();
        }

        string fidelity = "dom";
        if ((arguments.TryGetProperty("fidelity", out JsonElement fidEl) ||
             arguments.TryGetProperty("click_fidelity", out fidEl) ||
             arguments.TryGetProperty("clickFidelity", out fidEl)) &&
            fidEl.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(fidEl.GetString()))
        {
            fidelity = fidEl.GetString()!.Trim().ToLowerInvariant();
        }
        bool humanFidelityRequested = string.Equals(fidelity, "human", StringComparison.OrdinalIgnoreCase);

        bool highlight = (arguments.TryGetProperty("highlight", out JsonElement hlEl) && hlEl.GetBoolean()) ||
                         (arguments.TryGetProperty("flash_overlay", out JsonElement foEl) && foEl.GetBoolean()) ||
                         (arguments.TryGetProperty("flashOverlay", out JsonElement foEl2) && foEl2.GetBoolean());

        bool isGameClient = target.Equals("client", StringComparison.OrdinalIgnoreCase) ||
                           target.Equals("game", StringComparison.OrdinalIgnoreCase) ||
                           target.Equals("hon_x64", StringComparison.OrdinalIgnoreCase) ||
                           (target.Equals("foreground", StringComparison.OrdinalIgnoreCase) && ((_gameIpcBridge != null && _gameIpcBridge.IsConnected) || (_taoClient != null && _taoClient.IsConnected)));

        // Phase 4 Actuation Pipeline: When targeting a K2 widget on the game client (including 'invoke', MCP-289)
        if (!string.IsNullOrWhiteSpace(widget) && _taoClient != null && _taoClient.IsConnected && isGameClient && x == null && y == null)
        {
            DateTimeOffset preActuationTime = _timeProvider.GetUtcNow();
            string? preCheckpoint = _errorsSinceHandler?.CaptureCheckpointToken();
            Stopwatch totalSw = Stopwatch.StartNew();

            LogDiagnostic($"[UiControlHandler] Executing Phase 4 Actuation pipeline on widget '{widget}' (interface: '{iface}', fidelity: '{fidelity}')");

            var taoPayload = new
            {
                widget,
                target = widget,
                @interface = iface,
                highlight,
                flashOverlay = highlight
            };

            var taoResp = await _taoClient.DispatchAsync("click_widget", taoPayload, 2000, cancellationToken).ConfigureAwait(false);

            // Step 1: Evaluate Parent Visibility & Enabled Guard
            if (taoResp != null && taoResp.Data.ValueKind == JsonValueKind.Object)
            {
                if (taoResp.Data.TryGetProperty("guard", out JsonElement guardEl))
                {
                    string guardVal = guardEl.GetString() ?? "";
                    if (guardVal == "parent_visibility_rejected")
                    {
                        totalSw.Stop();
                        string errMsg = taoResp.Data.TryGetProperty("error", out JsonElement errEl) ? errEl.GetString() ?? "Parent visibility rejected" : "Cannot actuate hidden widget";
                        LogDiagnostic($"[UiControlHandler] Parent visibility guard REJECTED click on '{widget}': {errMsg}");
                        return ToolCallResult.Json(new
                        {
                            status = "fail",
                            passed = false,
                            guard = "parent_visibility_rejected",
                            found = true,
                            visible = false,
                            enabled = taoResp.Data.TryGetProperty("enabled", out JsonElement enEl) && enEl.GetBoolean(),
                            widget,
                            interfaceName = iface,
                            errorMessage = errMsg,
                            elapsedMs = Math.Round(totalSw.Elapsed.TotalMilliseconds, 2),
                            timestamp = _timeProvider.GetUtcNow().ToString("o")
                        });
                    }
                    else if (guardVal == "widget_disabled_rejected")
                    {
                        totalSw.Stop();
                        string errMsg = taoResp.Data.TryGetProperty("error", out JsonElement errEl) ? errEl.GetString() ?? "Widget disabled rejected" : "Cannot actuate disabled widget";
                        LogDiagnostic($"[UiControlHandler] Widget disabled guard REJECTED click on '{widget}': {errMsg}");
                        return ToolCallResult.Json(new
                        {
                            status = "fail",
                            passed = false,
                            guard = "widget_disabled_rejected",
                            found = true,
                            visible = true,
                            enabled = false,
                            widget,
                            interfaceName = iface,
                            errorMessage = errMsg,
                            elapsedMs = Math.Round(totalSw.Elapsed.TotalMilliseconds, 2),
                            timestamp = _timeProvider.GetUtcNow().ToString("o")
                        });
                    }
                }

                if (taoResp.Data.TryGetProperty("found", out JsonElement fEl) && !fEl.GetBoolean())
                {
                    totalSw.Stop();
                    string errMsg = taoResp.Data.TryGetProperty("error", out JsonElement errEl) ? errEl.GetString() ?? $"Widget '{widget}' not found" : $"Widget '{widget}' not found in interface '{iface}'";
                    return ToolCallResult.Json(new
                    {
                        status = "fail",
                        passed = false,
                        found = false,
                        widget,
                        interfaceName = iface,
                        errorMessage = errMsg,
                        elapsedMs = Math.Round(totalSw.Elapsed.TotalMilliseconds, 2),
                        timestamp = _timeProvider.GetUtcNow().ToString("o")
                    });
                }
            }

            bool taoPassed = taoResp != null && taoResp.Success && !string.Equals(taoResp.Status, "error", StringComparison.OrdinalIgnoreCase);
            string method = "none";
            if (taoResp?.Data.ValueKind == JsonValueKind.Object && taoResp.Data.TryGetProperty("method", out JsonElement mEl))
            {
                method = mEl.GetString() ?? "none";
            }

            // Step 2 Fallback: If method == "none" (no Lua callback), click at widget absolute center
            int? clickScreenX = null;
            int? clickScreenY = null;
            if (taoResp?.Data.ValueKind == JsonValueKind.Object && taoResp.Data.TryGetProperty("bounds", out JsonElement bEl))
            {
                int bx = bEl.TryGetProperty("x", out JsonElement xEl) && xEl.TryGetInt32(out int xv) ? xv : 0;
                int by = bEl.TryGetProperty("y", out JsonElement yEl) && yEl.TryGetInt32(out int yv) ? yv : 0;
                int bw = bEl.TryGetProperty("width", out JsonElement wEl2) && wEl2.TryGetInt32(out int wv) ? wv : 0;
                int bh = bEl.TryGetProperty("height", out JsonElement hEl2) && hEl2.TryGetInt32(out int hv) ? hv : 0;
                if (bw > 0 && bh > 0)
                {
                    clickScreenX = bx + bw / 2;
                    clickScreenY = by + bh / 2;
                }
            }

            bool hardwareClickRequested = arguments.TryGetProperty("hardware_click", out JsonElement hwEl) && hwEl.GetBoolean();
            if (humanFidelityRequested && _gameIpcBridge != null && _gameIpcBridge.IsConnected)
            {
                int cx = clickScreenX ?? 640;
                int cy = clickScreenY ?? 360;
                string bgResp = await _gameIpcBridge.DispatchBackgroundHumanClickAsync(iface, widget, cx, cy, false, 2000, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(bgResp) && !bgResp.Contains("\"success\":false", StringComparison.OrdinalIgnoreCase))
                {
                    taoPassed = true;
                    method = "background_human_click";
                }
            }
            else if (hardwareClickRequested)
            {
                if (clickScreenX.HasValue && clickScreenY.HasValue)
                {
                    LogDiagnostic($"[WARN] Hardware mouse click explicitly requested for widget '{widget}' - taking control of desktop cursor at ({clickScreenX},{clickScreenY})");
                    var clickRes = await _uiControlService.ClickAsync(target, null, clickScreenX, clickScreenY, false, cancellationToken).ConfigureAwait(false);
                    if (clickRes.Success)
                    {
                        taoPassed = true;
                        method = "win32_hardware_click";
                    }
                }
            }
            else if ((!taoPassed || method == "none") && _gameIpcBridge != null && _gameIpcBridge.IsConnected)
            {
                try
                {
                    string safeWidget = UiFlowHandler.EscapeLuaString(widget);
                    string safeIface = UiFlowHandler.EscapeLuaString(iface);
                    string fallbackClickLua =
                        $"local iface = (UIManager and UIManager.GetInterface and UIManager.GetInterface('{safeIface}')) or (UIManager and UIManager.GetActiveInterface and UIManager.GetActiveInterface()) " +
                        $"local names = {{'{safeWidget}', '{safeWidget}_btn', '{safeWidget}_catcher', '{safeWidget}_button'}} " +
                        $"for _, n in ipairs(names) do " +
                        $"  local w = (iface and iface.GetWidget and iface:GetWidget(n)) or (GetWidget and GetWidget(n, '{safeIface}', true)) " +
                        $"  if w then " +
                        $"    local acted = false " +
                        $"    if w.GetCallback then local okCb, cb = pcall(w.GetCallback, w, 'onclick') if okCb and type(cb) == 'function' then pcall(cb, w) acted = true end end " +
                        $"    if type(w.onclicklua) == 'string' and #w.onclicklua > 0 then if iface and iface.UICmd then pcall(iface.UICmd, iface, w.onclicklua) acted = true else local fn = loadstring(w.onclicklua) if fn then pcall(fn) acted = true end end end " +
                        $"    if not acted and w.DoEvent then pcall(function() w:DoEvent() end) end " +
                        $"    return 'CLICKED:' .. n " +
                        $"  end " +
                        $"end " +
                        $"return 'NOT_FOUND'";

                    string? luaOut = await _gameIpcBridge.ExecuteLuaAsync(fallbackClickLua, 2000, cancellationToken).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(luaOut))
                    {
                        luaOut = await _gameIpcBridge.ExecuteCommandAsync("Script " + fallbackClickLua, 2000, cancellationToken).ConfigureAwait(false);
                    }

                    if (!string.IsNullOrWhiteSpace(luaOut) &&
                        !luaOut.Contains("NOT_FOUND", StringComparison.OrdinalIgnoreCase) &&
                        !luaOut.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
                    {
                        taoPassed = true;
                        method = "ipc_dom_doevent";
                        LogDiagnostic($"[UiControlHandler] Direct IPC DOM fallback clicked '{widget}' ({luaOut.Trim()}).");
                    }
                    else
                    {
                        string dispatchOut = await _gameIpcBridge.DispatchUiEventAsync(iface, widget, "click", null, 2000, cancellationToken).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(dispatchOut) &&
                            !dispatchOut.Contains("error", StringComparison.OrdinalIgnoreCase) &&
                            !dispatchOut.Contains("not found", StringComparison.OrdinalIgnoreCase))
                        {
                            taoPassed = true;
                            method = "ipc_dispatch_ui_event";
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogDiagnostic($"[UiControlHandler] Direct IPC DOM click fallback failed for '{widget}': {ex.Message}");
                }
            }
            else if (method == "none" && !taoPassed)
            {
                LogDiagnostic($"[UiControlHandler] Widget '{widget}' has no in-engine Lua click callback; hardware click suppressed because hardware_click was not explicitly enabled.");
            }

            // Step 3: Post-State Verification (Wait For)
            object? waitReport = null;
            bool waitPassed = true;
            bool hasWaitFor = TryExtractWaitFor(arguments, widget, iface, out JsonElement waitArgs);
            if (hasWaitFor && _waitForHandler != null)
            {
                var waitCallRes = await _waitForHandler.HandleAsync(waitArgs, cancellationToken).ConfigureAwait(false);
                if (waitCallRes.Content.Count > 0 && waitCallRes.Content[0] is TextContentItem waitTxt)
                {
                    using var doc = JsonDocument.Parse(waitTxt.Text);
                    waitPassed = doc.RootElement.TryGetProperty("satisfied", out JsonElement sEl) && sEl.GetBoolean();
                    waitReport = JsonSerializer.Deserialize<object>(waitTxt.Text);
                }
                else
                {
                    waitPassed = !waitCallRes.IsError;
                }
            }

            // Step 4: Errors Since (Log Baseline Audit)
            ErrorsScanReport? errorReport = null;
            if (_errorsSinceHandler != null)
            {
                errorReport = !string.IsNullOrWhiteSpace(preCheckpoint)
                    ? _errorsSinceHandler.ScanLogsSinceCheckpoint(preCheckpoint, severityFilter: "error")
                    : _errorsSinceHandler.ScanLogs(preActuationTime, severityFilter: "error");
            }

            totalSw.Stop();
            double elapsedMs = Math.Round(totalSw.Elapsed.TotalMilliseconds, 2);
            bool isClean = errorReport?.IsClean ?? true;
            bool overallSuccess = taoPassed && waitPassed && isClean;

            bool fullDetail = arguments.TryGetProperty("detail", out JsonElement detEl) &&
                              detEl.ValueKind == JsonValueKind.String &&
                              string.Equals(detEl.GetString(), "full", StringComparison.OrdinalIgnoreCase);

            LogDiagnostic($"[UiControlHandler] Phase 4 click on '{widget}' result: Success={overallSuccess}, Method={method}, Fidelity={fidelity}, WaitPassed={waitPassed}, Clean={isClean}, Elapsed={elapsedMs}ms");

            return ToolCallResult.Json(new
            {
                status = overallSuccess ? "success" : "fail",
                passed = overallSuccess,
                detail = fullDetail ? "full" : "concise",
                guard = "passed",
                action = "click",
                fidelity,
                widget,
                interfaceName = iface,
                method,
                elapsedMs,
                clean = isClean,
                errorCount = errorReport?.Errors.Count ?? 0,
                errors = fullDetail ? (errorReport?.Errors ?? []) : (errorReport?.Errors.Take(2).ToList() ?? []),
                postState = waitReport,
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            });
        }

        // Direct DOM or background human dispatch fallback when TAO is not connected but Game IPC Bridge is available
        if (_gameIpcBridge != null && _gameIpcBridge.IsConnected && isGameClient &&
            !string.IsNullOrWhiteSpace(widget) && x == null && y == null)
        {
            if (humanFidelityRequested)
            {
                LogDiagnostic($"[UiControlHandler] Routing background human click on widget '{widget}' via Game IPC Bridge.");
                string bgResp = await _gameIpcBridge.DispatchBackgroundHumanClickAsync(iface, widget, 640, 360, false, 3000, cancellationToken).ConfigureAwait(false);
                return ToolCallResult.Json(new
                {
                    status = "success",
                    action = "click",
                    fidelity = "human",
                    method = "background_human_click",
                    channel = "engine_internal_ipc",
                    interfaceName = iface,
                    widget,
                    response = bgResp,
                    timestamp = _timeProvider.GetUtcNow().ToString("o")
                });
            }
            LogDiagnostic($"[UiControlHandler] Routing click on widget '{widget}' directly through engine DOM event dispatching.");
            return await HandleDispatchEventAsync(arguments, cancellationToken).ConfigureAwait(false);
        }

        UiClickResult result = await _uiControlService.ClickAsync(target, elementName, x, y, useInvokePattern, cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            LogDiagnostic($"[UiControlHandler] Click failed for target '{target}': {result.ErrorMessage}");
            var errPayload = new
            {
                status = "error",
                action = useInvokePattern ? "invoke" : "click",
                target = result.Target,
                windowTitle = result.WindowTitle,
                hwnd = result.Hwnd,
                elementClicked = result.ElementClicked,
                errorMessage = result.ErrorMessage ?? "Click operation failed.",
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            };
            return ToolCallResult.Error(JsonRpcSerializer.SerializeIndented(errPayload));
        }

        var responsePayload = new
        {
            status = "success",
            action = useInvokePattern ? "invoke" : "click",
            target = result.Target,
            windowTitle = result.WindowTitle,
            hwnd = result.Hwnd,
            elementClicked = result.ElementClicked,
            actionTaken = result.ActionTaken,
            screenX = result.ScreenX,
            screenY = result.ScreenY,
            windowRelativeX = result.WindowRelativeX,
            windowRelativeY = result.WindowRelativeY,
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        };

        LogDiagnostic($"[UiControlHandler] Click succeeded on '{target}' (Action: {result.ActionTaken}, Coords: {result.ScreenX},{result.ScreenY})");
        return ToolCallResult.Json(responsePayload);
    }

    private async Task<ToolCallResult> HandleDragAsync(
        string target,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        DateTimeOffset preTime = _timeProvider.GetUtcNow();
        Stopwatch sw = Stopwatch.StartNew();

        int startX = 0;
        int startY = 0;
        int endX = 0;
        int endY = 0;
        int durationMs = 300;

        if (arguments.TryGetProperty("x", out JsonElement xEl) && xEl.TryGetInt32(out int xVal)) startX = xVal;
        if (arguments.TryGetProperty("y", out JsonElement yEl) && yEl.TryGetInt32(out int yVal)) startY = yVal;
        if (arguments.TryGetProperty("x2", out JsonElement x2El) && x2El.TryGetInt32(out int x2Val)) endX = x2Val;
        else if (arguments.TryGetProperty("to_x", out JsonElement toxEl) && toxEl.TryGetInt32(out int toxVal)) endX = toxVal;

        if (arguments.TryGetProperty("y2", out JsonElement y2El) && y2El.TryGetInt32(out int y2Val)) endY = y2Val;
        else if (arguments.TryGetProperty("to_y", out JsonElement toyEl) && toyEl.TryGetInt32(out int toyVal)) endY = toyVal;

        if (arguments.TryGetProperty("duration_ms", out JsonElement durEl) && durEl.TryGetInt32(out int durVal)) durationMs = durVal;
        else if (arguments.TryGetProperty("durationMs", out JsonElement durEl2) && durEl2.TryGetInt32(out int durVal2)) durationMs = durVal2;

        string windowTarget = (!string.IsNullOrWhiteSpace(target) &&
                               (string.Equals(target, "hon_x64", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(target, "hon", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(target, "client", StringComparison.OrdinalIgnoreCase)))
            ? target
            : "hon_x64";

        string? fromWidget = null;
        if ((arguments.TryGetProperty("widget", out JsonElement wEl) ||
             arguments.TryGetProperty("from_widget", out wEl) ||
             arguments.TryGetProperty("fromWidget", out wEl) ||
             arguments.TryGetProperty("element_name", out wEl)) && !string.IsNullOrWhiteSpace(wEl.GetString()))
        {
            fromWidget = wEl.GetString()!.Trim();
        }
        else if (!string.IsNullOrWhiteSpace(target) &&
                 !string.Equals(target, "hon_x64", StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(target, "hon", StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(target, "client", StringComparison.OrdinalIgnoreCase))
        {
            fromWidget = target.Trim();
        }

        string? toWidget = null;
        if ((arguments.TryGetProperty("to_widget", out JsonElement twEl) ||
             arguments.TryGetProperty("toWidget", out twEl)) && !string.IsNullOrWhiteSpace(twEl.GetString()))
        {
            toWidget = twEl.GetString()!.Trim();
        }

        string iface = "main";
        if (arguments.TryGetProperty("interface", out JsonElement ifEl) && !string.IsNullOrWhiteSpace(ifEl.GetString()))
        {
            iface = ifEl.GetString()!.Trim();
        }

        if (!string.IsNullOrWhiteSpace(fromWidget) && _taoClient != null && _taoClient.IsConnected)
        {
            var inspectResp = await _taoClient.DispatchAsync("assert_ui", new { widget = fromWidget, @interface = iface }, 2000, cancellationToken).ConfigureAwait(false);
            if (inspectResp != null && inspectResp.Data.ValueKind == JsonValueKind.Object)
            {
                if (inspectResp.Data.TryGetProperty("visible", out JsonElement vEl) && !vEl.GetBoolean())
                {
                    return ToolCallResult.Json(new
                    {
                        status = "fail",
                        passed = false,
                        guard = "parent_visibility_rejected",
                        visible = false,
                        widget = fromWidget,
                        errorMessage = $"Cannot drag from hidden widget '{fromWidget}'"
                    });
                }

                JsonElement bEl = inspectResp.Data.TryGetProperty("bounds", out JsonElement b) ? b : inspectResp.Data;
                int ix = bEl.TryGetProperty("x", out JsonElement xEl2) && xEl2.TryGetInt32(out int xv) ? xv : 0;
                int iy = bEl.TryGetProperty("y", out JsonElement yEl2) && yEl2.TryGetInt32(out int yv) ? yv : 0;
                int iw = bEl.TryGetProperty("width", out JsonElement wEl2) && wEl2.TryGetInt32(out int wv) ? wv : 0;
                int ih = bEl.TryGetProperty("height", out JsonElement hEl2) && hEl2.TryGetInt32(out int hv) ? hv : 0;

                if (iw > 0 && ih > 0)
                {
                    startX = ix + iw / 2;
                    startY = iy + ih / 2;
                }
            }

            if (arguments.TryGetProperty("dx", out JsonElement dxEl) && dxEl.TryGetInt32(out int dxVal)) endX = startX + dxVal;
            else if (arguments.TryGetProperty("offset_x", out JsonElement oxEl) && oxEl.TryGetInt32(out int oxVal)) endX = startX + oxVal;

            if (arguments.TryGetProperty("dy", out JsonElement dyEl) && dyEl.TryGetInt32(out int dyVal)) endY = startY + dyVal;
            else if (arguments.TryGetProperty("offset_y", out JsonElement oyEl) && oyEl.TryGetInt32(out int oyVal)) endY = startY + oyVal;

            if (endY == 0 && endX != 0) endY = startY;
            if (endX == 0 && endY != 0) endX = startX;

            int? sliderVal = null;
            if (arguments.TryGetProperty("value", out JsonElement valEl) && valEl.TryGetInt32(out int sv)) sliderVal = sv;
            int dxAmount = (endX - startX);
            if (dxAmount != 0 || sliderVal.HasValue)
            {
                var dragResp = await _taoClient.DispatchAsync("drag_slider", new
                {
                    widget = fromWidget,
                    @interface = iface,
                    dx = dxAmount,
                    value = sliderVal
                }, 2000, cancellationToken).ConfigureAwait(false);

                if (dragResp != null && dragResp.Data.ValueKind == JsonValueKind.Object)
                {
                    if (dragResp.Data.TryGetProperty("passed", out JsonElement passEl) && !passEl.GetBoolean())
                    {
                        string err = dragResp.Data.TryGetProperty("error", out JsonElement errEl) ? errEl.GetString() ?? "Drag failed" : "Drag failed";
                        string guard = dragResp.Data.TryGetProperty("guard", out JsonElement gEl) ? gEl.GetString() ?? "" : "";
                        return ToolCallResult.Json(new
                        {
                            status = "fail",
                            passed = false,
                            guard,
                            widget = fromWidget,
                            interfaceName = iface,
                            errorMessage = err
                        });
                    }

                    bool needHardware = arguments.TryGetProperty("hardware_drag", out JsonElement hwDragEl) && hwDragEl.GetBoolean() ||
                                        arguments.TryGetProperty("hardware_click", out JsonElement hwEl2) && hwEl2.GetBoolean();
                    if (!needHardware && passEl.GetBoolean())
                    {
                        // Tier 1 successfully handled the drag! Return immediately with 0 physical mouse movement!
                        sw.Stop();
                        return ToolCallResult.Json(new
                        {
                            status = "success",
                            passed = true,
                            action = "drag",
                            method = "tier1_tao_ipc",
                            widget = fromWidget,
                            interfaceName = iface,
                            initialValue = dragResp.Data.TryGetProperty("initialValue", out JsonElement ivEl) ? ivEl.GetDouble() : 0,
                            value = dragResp.Data.TryGetProperty("value", out JsonElement vEl) ? vEl.GetDouble() : 0,
                            elapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                            timestamp = _timeProvider.GetUtcNow().ToString("o")
                        });
                    }
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(toWidget) && _taoClient != null && _taoClient.IsConnected)
        {
            var inspectResp2 = await _taoClient.DispatchAsync("assert_ui", new { widget = toWidget, @interface = iface }, 2000, cancellationToken).ConfigureAwait(false);
            if (inspectResp2 != null && inspectResp2.Data.ValueKind == JsonValueKind.Object)
            {
                JsonElement bEl2 = inspectResp2.Data.TryGetProperty("bounds", out JsonElement b2) ? b2 : inspectResp2.Data;
                int ix2 = bEl2.TryGetProperty("x", out JsonElement xEl3) && xEl3.TryGetInt32(out int xv2) ? xv2 : 0;
                int iy2 = bEl2.TryGetProperty("y", out JsonElement yEl3) && yEl3.TryGetInt32(out int yv2) ? yv2 : 0;
                int iw2 = bEl2.TryGetProperty("width", out JsonElement wEl3) && wEl3.TryGetInt32(out int wv2) ? wv2 : 0;
                int ih2 = bEl2.TryGetProperty("height", out JsonElement hEl3) && hEl3.TryGetInt32(out int hv2) ? hv2 : 0;

                if (iw2 > 0 && ih2 > 0)
                {
                    endX = ix2 + iw2 / 2;
                    endY = iy2 + ih2 / 2;
                }
            }
        }

        LogDiagnostic($"[WARN] Hardware mouse drag explicitly requested - taking control of desktop cursor from ({startX},{startY}) to ({endX},{endY})");
        var dragResult = await _uiControlService.DragAsync(windowTarget, startX, startY, endX, endY, durationMs, cancellationToken).ConfigureAwait(false);

        ErrorsScanReport? errorReport = null;
        if (_errorsSinceHandler != null)
        {
            errorReport = _errorsSinceHandler.ScanLogs(preTime, severityFilter: "error");
        }

        sw.Stop();
        bool isClean = errorReport?.IsClean ?? true;
        bool overallPassed = dragResult.Success && isClean;

        return ToolCallResult.Json(new
        {
            status = overallPassed ? "success" : "fail",
            passed = overallPassed,
            action = "drag",
            startX,
            startY,
            endX,
            endY,
            fromWidget,
            toWidget,
            durationMs,
            elapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
            clean = isClean,
            errorCount = errorReport?.Errors.Count ?? 0,
            errors = errorReport?.Errors ?? []
        });
    }

    private async Task<ToolCallResult> HandleTypeAsync(
        string target,
        string? text,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        if (text is null)
        {
            var errPayload = new
            {
                status = "error",
                action = "type",
                target,
                errorMessage = "Missing required parameter 'text' for action 'type'.",
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            };
            return ToolCallResult.Error(JsonRpcSerializer.SerializeIndented(errPayload));
        }

        DateTimeOffset preTime = _timeProvider.GetUtcNow();
        Stopwatch sw = Stopwatch.StartNew();

        string windowTarget = (!string.IsNullOrWhiteSpace(target) &&
                               (string.Equals(target, "hon_x64", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(target, "hon", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(target, "client", StringComparison.OrdinalIgnoreCase)))
            ? target
            : "hon_x64";

        string? widget = null;
        if ((arguments.TryGetProperty("widget", out JsonElement wEl) ||
             arguments.TryGetProperty("element_name", out wEl) ||
             arguments.TryGetProperty("elementName", out wEl)) && !string.IsNullOrWhiteSpace(wEl.GetString()))
        {
            widget = wEl.GetString()!.Trim();
        }
        else if (!string.IsNullOrWhiteSpace(target) &&
                 !string.Equals(target, "hon_x64", StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(target, "hon", StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(target, "client", StringComparison.OrdinalIgnoreCase))
        {
            widget = target.Trim();
        }

        string iface = "main";
        if (arguments.TryGetProperty("interface", out JsonElement ifEl) && !string.IsNullOrWhiteSpace(ifEl.GetString()))
        {
            iface = ifEl.GetString()!.Trim();
        }

        if (!string.IsNullOrWhiteSpace(widget) && _taoClient != null && _taoClient.IsConnected)
        {
            var inspectResp = await _taoClient.DispatchAsync("assert_ui", new { widget, @interface = iface }, 2000, cancellationToken).ConfigureAwait(false);
            if (inspectResp != null && inspectResp.Data.ValueKind == JsonValueKind.Object)
            {
                bool isVisible = true;
                if (inspectResp.Data.TryGetProperty("visible", out JsonElement vEl))
                {
                    isVisible = vEl.GetBoolean();
                }
                else if (inspectResp.Data.TryGetProperty("root", out JsonElement rootEl) && rootEl.TryGetProperty("visible", out JsonElement rvEl))
                {
                    isVisible = rvEl.GetBoolean();
                }

                if (!isVisible)
                {
                    return ToolCallResult.Json(new
                    {
                        status = "fail",
                        passed = false,
                        guard = "parent_visibility_rejected",
                        visible = false,
                        widget,
                        interfaceName = iface,
                        errorMessage = $"Cannot type into hidden widget '{widget}'"
                    });
                }
            }

            var typeResp = await _taoClient.DispatchAsync("type_text", new
            {
                widget,
                @interface = iface,
                text
            }, 2000, cancellationToken).ConfigureAwait(false);

            if (typeResp != null && typeResp.Data.ValueKind == JsonValueKind.Object)
            {
                if (typeResp.Data.TryGetProperty("passed", out JsonElement passEl) && !passEl.GetBoolean())
                {
                    string err = typeResp.Data.TryGetProperty("error", out JsonElement errEl) ? errEl.GetString() ?? "Type failed" : "Type failed";
                    string guard = typeResp.Data.TryGetProperty("guard", out JsonElement gEl) ? gEl.GetString() ?? "" : "";
                    return ToolCallResult.Json(new
                    {
                        status = "fail",
                        passed = false,
                        guard,
                        widget,
                        interfaceName = iface,
                        errorMessage = err
                    });
                }

                bool needHardware = arguments.TryGetProperty("hardware_type", out JsonElement hwTypeEl) && hwTypeEl.GetBoolean() ||
                                    arguments.TryGetProperty("hardware_click", out JsonElement hwEl3) && hwEl3.GetBoolean();
                if (!needHardware && passEl.GetBoolean())
                {
                    // Tier 1 successfully typed text in-engine via SetText / SetInputLine!
                    sw.Stop();
                    return ToolCallResult.Json(new
                    {
                        status = "success",
                        passed = true,
                        action = "type",
                        method = "tier1_tao_ipc",
                        widget,
                        interfaceName = iface,
                        text = typeResp.Data.TryGetProperty("text", out JsonElement tEl) ? tEl.GetString() : text,
                        elapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                        timestamp = _timeProvider.GetUtcNow().ToString("o")
                    });
                }
            }
        }

        LogDiagnostic("[WARN] Hardware typing explicitly requested - sending keystrokes to foreground window");
        UiTypeResult result = await _uiControlService.TypeTextAsync(windowTarget, text, cancellationToken).ConfigureAwait(false);

        ErrorsScanReport? errorReport = null;
        if (_errorsSinceHandler != null)
        {
            errorReport = _errorsSinceHandler.ScanLogs(preTime, severityFilter: "error");
        }

        sw.Stop();
        bool isClean = errorReport?.IsClean ?? true;
        bool overallPassed = result.Success && isClean;

        return ToolCallResult.Json(new
        {
            status = overallPassed ? "success" : "fail",
            passed = overallPassed,
            action = "type",
            widget,
            text,
            charactersTyped = result.CharactersTyped,
            elapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
            clean = isClean,
            errorCount = errorReport?.Errors.Count ?? 0,
            errors = errorReport?.Errors ?? []
        });
    }

    private static bool TryExtractWaitFor(JsonElement arguments, string defaultWidget, string defaultIface, out JsonElement waitArgs)
    {
        if (arguments.TryGetProperty("waitFor", out waitArgs) || arguments.TryGetProperty("wait_for", out waitArgs))
        {
            return true;
        }

        if (arguments.TryGetProperty("verify_widget", out JsonElement vwEl) || arguments.TryGetProperty("verifyWidget", out vwEl))
        {
            string verifyWidget = vwEl.GetString()?.Trim() ?? defaultWidget;
            bool expectedVisible = true;
            if (arguments.TryGetProperty("verify_visible", out JsonElement vvEl) || arguments.TryGetProperty("verifyVisible", out vvEl))
            {
                expectedVisible = vvEl.GetBoolean();
            }

            int timeoutMs = 5000;
            if (arguments.TryGetProperty("verify_timeout_ms", out JsonElement vtEl) || arguments.TryGetProperty("verifyTimeoutMs", out vtEl))
            {
                if (vtEl.TryGetInt32(out int t)) timeoutMs = t;
            }

            var dict = new Dictionary<string, object>
            {
                ["condition"] = expectedVisible ? "ui_visible" : "ui_hidden",
                ["widget"] = verifyWidget,
                ["interface"] = defaultIface,
                ["timeoutMs"] = timeoutMs
            };
            string json = JsonSerializer.Serialize(dict);
            using var doc = JsonDocument.Parse(json);
            waitArgs = doc.RootElement.Clone();
            return true;
        }

        waitArgs = default;
        return false;
    }

    private async Task<ToolCallResult> HandleKeyAsync(string target, string? key, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            var errPayload = new
            {
                status = "error",
                action = "key",
                target,
                errorMessage = "Missing required parameter 'key' for action 'key'.",
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            };
            return ToolCallResult.Error(JsonRpcSerializer.SerializeIndented(errPayload));
        }

        UiKeyResult result = await _uiControlService.SendKeyAsync(target, key, cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            // If Win32 window SendKey failed on game client, but IPC mailbox is connected, route key action via direct IPC fallback!
            bool isGameClient = target.Equals("client", StringComparison.OrdinalIgnoreCase) ||
                               target.Equals("game", StringComparison.OrdinalIgnoreCase) ||
                               target.Equals("hon_x64", StringComparison.OrdinalIgnoreCase) ||
                               (target.Equals("foreground", StringComparison.OrdinalIgnoreCase) &&
                                ((_gameIpcBridge != null && _gameIpcBridge.IsConnected) || (_taoClient != null && _taoClient.IsConnected)));

            if (isGameClient && _taoClient != null && _taoClient.IsConnected)
            {
                LogDiagnostic($"[UiControlHandler] Falling back to TAO IPC key_press for key '{key}' on '{target}'");
                var keyResp = await _taoClient.DispatchAsync("key_press", new { key }, 2000, cancellationToken).ConfigureAwait(false);
                if (keyResp != null && keyResp.Success)
                {
                    return ToolCallResult.Json(new
                    {
                        status = "success",
                        action = "key",
                        target,
                        windowTitle = result.WindowTitle,
                        hwnd = result.Hwnd,
                        key,
                        channel = "tao_ipc_fallback",
                        actionTaken = $"tao_key_{key.ToLowerInvariant()}",
                        timestamp = _timeProvider.GetUtcNow().ToString("o")
                    });
                }
            }

            if (isGameClient && _gameIpcBridge != null && _gameIpcBridge.IsConnected)
            {
                if (key.Equals("Escape", StringComparison.OrdinalIgnoreCase) || key.Equals("Esc", StringComparison.OrdinalIgnoreCase))
                {
                    LogDiagnostic($"[UiControlHandler] Falling back to direct IPC in-game console dismissal for Escape key on '{target}'");
                    var dismissResult = await _uiControlService.DismissInGameConsoleAsync(target, cancellationToken).ConfigureAwait(false);
                    if (dismissResult.Success)
                    {
                        return ToolCallResult.Json(new
                        {
                            status = "success",
                            action = "key",
                            target,
                            windowTitle = dismissResult.WindowTitle,
                            hwnd = dismissResult.Hwnd,
                            key,
                            channel = "ipc_fallback",
                            actionTaken = "console_dismissed_focus_cleared",
                            timestamp = _timeProvider.GetUtcNow().ToString("o")
                        });
                    }
                }

                try
                {
                    LogDiagnostic($"[UiControlHandler] Falling back to direct GameIpcBridge key dispatch for key '{key}' on '{target}'");
                    string ipcKeyOutput = await _gameIpcBridge.ExecuteCommandAsync($"echo [MCP:KEY] {key}", 2000, cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(ipcKeyOutput))
                    {
                        return ToolCallResult.Json(new
                        {
                            status = "success",
                            action = "key",
                            target,
                            windowTitle = result.WindowTitle,
                            hwnd = result.Hwnd,
                            key,
                            channel = "ipc_fallback",
                            actionTaken = $"ipc_key_{key.ToLowerInvariant()}",
                            timestamp = _timeProvider.GetUtcNow().ToString("o")
                        });
                    }
                }
                catch (Exception ex)
                {
                    LogDiagnostic($"[UiControlHandler] Direct GameIpcBridge key fallback failed for '{key}': {ex.Message}");
                }
            }

            LogDiagnostic($"[UiControlHandler] SendKey failed for target '{target}', key '{key}': {result.ErrorMessage}");
            var errPayload = new
            {
                status = "error",
                action = "key",
                target = result.Target,
                windowTitle = result.WindowTitle,
                hwnd = result.Hwnd,
                key,
                errorMessage = result.ErrorMessage ?? "SendKey operation failed.",
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            };
            return ToolCallResult.Error(JsonRpcSerializer.SerializeIndented(errPayload));
        }

        var responsePayload = new
        {
            status = "success",
            action = "key",
            target = result.Target,
            windowTitle = result.WindowTitle,
            hwnd = result.Hwnd,
            key = result.KeyName,
            virtualKeyCode = result.VirtualKeyCode,
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        };

        LogDiagnostic($"[UiControlHandler] SendKey succeeded on '{target}' (Key: {result.KeyName}, VK: 0x{result.VirtualKeyCode:X2})");
        return ToolCallResult.Json(responsePayload);
    }

    private async Task<ToolCallResult> HandleConsoleAsync(string target, string? command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            var errPayload = new
            {
                status = "error",
                action = "console",
                target,
                errorMessage = "Parameter 'command' is required for action 'console'.",
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            };
            return ToolCallResult.Error(JsonRpcSerializer.SerializeIndented(errPayload));
        }

        bool isGameClient = target.Equals("client", StringComparison.OrdinalIgnoreCase) ||
                           target.Equals("game", StringComparison.OrdinalIgnoreCase) ||
                           target.Equals("hon_x64", StringComparison.OrdinalIgnoreCase) ||
                           (target.Equals("foreground", StringComparison.OrdinalIgnoreCase) && _gameIpcBridge != null && _gameIpcBridge.IsConnected);

        if (_gameIpcBridge != null && _gameIpcBridge.IsConnected && isGameClient)
        {
            try
            {
                LogDiagnostic($"[UiControlHandler] Routing console command '{command}' directly via background IPC.");
                var swIpc = System.Diagnostics.Stopwatch.StartNew();
                string ipcResp = await _gameIpcBridge.ExecuteCommandAsync(command.Trim(), 3000, cancellationToken).ConfigureAwait(false);
                swIpc.Stop();

                return ToolCallResult.Json(new
                {
                    status = "success",
                    action = "console",
                    target,
                    channel = "ipc",
                    command,
                    response = ipcResp,
                    executionDurationMs = Math.Round(swIpc.Elapsed.TotalMilliseconds, 2),
                    timestamp = _timeProvider.GetUtcNow().ToString("o")
                });
            }
            catch (Exception ex)
            {
                LogDiagnostic($"[UiControlHandler] Background IPC console execution failed ({ex.Message}), falling back to Win32 console actuation.");
            }
        }

        UiConsoleCommandResult result = await _uiControlService.ExecuteInGameConsoleCommandAsync(target, command, cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            LogDiagnostic($"[UiControlHandler] In-game console command failed for '{target}': {result.ErrorMessage}");
            var errPayload = new
            {
                status = "error",
                action = "console",
                target = result.Target,
                windowTitle = result.WindowTitle,
                hwnd = result.Hwnd,
                command = result.Command,
                errorMessage = result.ErrorMessage ?? "Console actuation failed.",
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            };
            return ToolCallResult.Error(JsonRpcSerializer.SerializeIndented(errPayload));
        }

        var responsePayload = new
        {
            status = "success",
            action = "console",
            target = result.Target,
            windowTitle = result.WindowTitle,
            hwnd = result.Hwnd,
            command = result.Command,
            executionDurationMs = result.ExecutionDurationMs,
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        };

        LogDiagnostic($"[UiControlHandler] In-game console command succeeded on '{target}' in {result.ExecutionDurationMs} ms");
        return ToolCallResult.Json(responsePayload);
    }

    private async Task<ToolCallResult> HandleDismissConsoleAsync(string target, CancellationToken cancellationToken)
    {
        LogDiagnostic($"[UiControlHandler] Dismissing in-game console / clearing input focus on target '{target}'");
        UiConsoleCommandResult result = await _uiControlService.DismissInGameConsoleAsync(target, cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            LogDiagnostic($"[UiControlHandler] Dismiss console failed for '{target}': {result.ErrorMessage}");
            var errPayload = new
            {
                status = "error",
                action = "dismiss_console",
                target = result.Target,
                windowTitle = result.WindowTitle,
                hwnd = result.Hwnd,
                errorMessage = result.ErrorMessage ?? "Console dismissal failed.",
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            };
            return ToolCallResult.Error(JsonRpcSerializer.SerializeIndented(errPayload));
        }

        var responsePayload = new
        {
            status = "success",
            action = "dismiss_console",
            target = result.Target,
            windowTitle = result.WindowTitle,
            hwnd = result.Hwnd,
            actionTaken = "console_dismissed_focus_cleared",
            executionDurationMs = result.ExecutionDurationMs,
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        };

        LogDiagnostic($"[UiControlHandler] In-game console dismissed on '{target}' in {result.ExecutionDurationMs} ms");
        return ToolCallResult.Json(responsePayload);
    }

    private ToolCallResult HandleUnknownAction(string action, string target)
    {
        var errPayload = new
        {
            status = "error",
            action,
            target,
            errorMessage = $"Unknown action '{action}'. Valid actions are: 'inspect', 'click', 'invoke', 'type', 'key', 'console', 'dismiss_console'. (Supported aliases: 'key_tap', 'press', 'mouse_click', 'write', 'status')",
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        };
        return ToolCallResult.Error(JsonRpcSerializer.SerializeIndented(errPayload));
    }

    private void LogDiagnostic(string message)
    {
        _logger.LogInformation("{Message}", message);
        try
        {
            Console.Error.WriteLine($"[{_timeProvider.GetUtcNow():yyyy-MM-ddTHH:mm:ss.fffZ}] {message}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[UiControlHandler] Console.Error write failed: {ex.Message}");
        }
    }
}
