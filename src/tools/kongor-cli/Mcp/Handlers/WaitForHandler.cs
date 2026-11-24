// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

// ============================================================================
// File: WaitForHandler.cs
// System: Kongor MCP Server - Polling State & UI Readiness Assertion Tool
// Purpose: Implements 'kongor_wait_for' (alias 'wait_for') to poll until UI,
//          engine, or Lua condition is satisfied with sub-second responsiveness.
// ============================================================================

using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// MCP tool handler for 'kongor_wait_for' (aliases: 'wait_for', 'kongor_await').
/// Actively polls engine state or UI DOM over high-speed Mailbox IPC until
/// an assertion is satisfied or a timeout window expires.
/// </summary>
public sealed class WaitForHandler
{
    private static readonly string[] CanonicalConditions =
    [
        "ipc_ready",
        "engine_ready",
        "client_ready",
        "game_phase",
        "map_loaded",
        "active_match",
        "frame_advance",
        "ui_visible",
        "ui_hidden",
        "ui_present",
        "ui_text",
        "lua"
    ];

    private static readonly HashSet<string> SupportedConditions = new(StringComparer.OrdinalIgnoreCase)
    {
        "client_ready", "engine_ready", "ipc_ready", "ipc", "pipe_ready",
        "game_phase", "phase",
        "map_loaded", "active_match",
        "frame_advance", "host_frame", "frame",
        "ui_visible", "widget_visible", "ui_hidden", "ui_invisible", "ui_present", "ui_text",
        "lua", "custom_lua", "eval"
    };

    public const int ColdStartWindowSeconds = 25;
    public const int ColdStartTimeoutFloorMs = 15000;

    private readonly ITaoClient? _taoClient;
    private readonly IGameIpcBridge? _gameIpcBridge;
    private readonly IClientProcessRegistry? _processRegistry;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _logger;
    private readonly Func<TimeSpan?>? _clientProcessAgeProvider;

    public WaitForHandler(
        ITaoClient? taoClient = null,
        IGameIpcBridge? gameIpcBridge = null,
        IClientProcessRegistry? processRegistry = null,
        TimeProvider? timeProvider = null,
        Action<string>? logger = null,
        Func<TimeSpan?>? clientProcessAgeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
        _taoClient = taoClient;
        _gameIpcBridge = gameIpcBridge;
        _processRegistry = processRegistry;
        _clientProcessAgeProvider = clientProcessAgeProvider;
    }

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Stopwatch sw = Stopwatch.StartNew();

        string condition = "ui_visible";
        if (arguments.TryGetProperty("condition", out JsonElement cEl) && !string.IsNullOrWhiteSpace(cEl.GetString()))
        {
            condition = cEl.GetString()!.Trim().ToLowerInvariant();
        }
        else if (arguments.TryGetProperty("type", out JsonElement tyEl) && !string.IsNullOrWhiteSpace(tyEl.GetString()))
        {
            condition = tyEl.GetString()!.Trim().ToLowerInvariant();
        }

        string target = "";
        if (arguments.TryGetProperty("target", out JsonElement tEl) && !string.IsNullOrWhiteSpace(tEl.GetString()))
        {
            target = tEl.GetString()!.Trim();
        }
        else if (arguments.TryGetProperty("widget", out JsonElement wEl) && !string.IsNullOrWhiteSpace(wEl.GetString()))
        {
            target = wEl.GetString()!.Trim();
        }
        else if (arguments.TryGetProperty("expression", out JsonElement expEl2) && !string.IsNullOrWhiteSpace(expEl2.GetString()))
        {
            target = expEl2.GetString()!.Trim();
        }

        string ifaceName = "main";
        if (arguments.TryGetProperty("interface", out JsonElement iEl) && !string.IsNullOrWhiteSpace(iEl.GetString()))
        {
            ifaceName = iEl.GetString()!.Trim();
        }

        string? expected = null;
        if (arguments.TryGetProperty("expected", out JsonElement expEl) && !string.IsNullOrWhiteSpace(expEl.GetString()))
        {
            expected = expEl.GetString()!.Trim();
        }
        else if (arguments.TryGetProperty("expectedText", out JsonElement etEl) && !string.IsNullOrWhiteSpace(etEl.GetString()))
        {
            expected = etEl.GetString()!.Trim();
        }

        int requestedTimeoutMs = ResolveTimeoutMs(arguments, condition);
        int timeoutMs = ResolveEffectiveTimeoutMs(arguments, condition, requestedTimeoutMs, out bool coldStartGraceApplied, out double? clientAgeSeconds);

        int intervalMs = 100;
        if ((arguments.TryGetProperty("intervalMs", out JsonElement intEl)
             || arguments.TryGetProperty("interval_ms", out intEl)
             || arguments.TryGetProperty("pollIntervalMs", out intEl)
             || arguments.TryGetProperty("poll_interval_ms", out intEl))
            && intEl.TryGetInt32(out int intVal) && intVal > 0)
        {
            intervalMs = Math.Clamp(intVal, 25, 5000);
        }

        long deadline = sw.ElapsedMilliseconds + timeoutMs;
        object? lastObservedState = null;
        bool satisfied = false;
        int pollCount = 0;
        ReadinessPollContext pollContext = new();

        if (!SupportedConditions.Contains(condition))
        {
            sw.Stop();
            double invalidElapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
            string[] suggestions = SuggestClosestConditions(condition);
            string suggestionHint = suggestions.Length > 0
                ? $" Did you mean '{suggestions[0]}'?"
                : string.Empty;
            string invalidMsg = $"Unknown wait condition '{condition}'.{suggestionHint} Valid conditions: {string.Join(", ", CanonicalConditions)}.";
            _logger?.Invoke($"[WaitForHandler] {invalidMsg}");
            return ToolCallResult.Json(new
            {
                status = "error",
                errorCode = "invalid_condition",
                satisfied = false,
                error = invalidMsg,
                condition,
                target,
                interfaceName = ifaceName,
                pollCount = 0,
                elapsedMs = invalidElapsedMs,
                timeoutMs,
                suggestions,
                validConditions = CanonicalConditions
            }, isError: true);
        }

        if (coldStartGraceApplied)
        {
            _logger?.Invoke(
                $"[WaitForHandler] Cold-start grace floor applied (MCP-194/MCP-198): hon_x64 age={clientAgeSeconds:F1}s (< {ColdStartWindowSeconds}s) -> extending timeoutMs from {requestedTimeoutMs}ms to {timeoutMs}ms for condition '{condition}'.");
        }
        _logger?.Invoke($"[WaitForHandler] Polling condition '{condition}' for target '{target}' (timeout: {timeoutMs}ms, interval: {intervalMs}ms)...");

        while (sw.ElapsedMilliseconds < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pollCount++;

            int remainingBudgetMs = (int)Math.Max(50, deadline - sw.ElapsedMilliseconds);
            using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            pollCts.CancelAfter(remainingBudgetMs);

            (bool Satisfied, object? ObservedState) checkResult;
            try
            {
                checkResult = await EvaluateConditionAsync(
                    condition, target, ifaceName, expected, pollContext, remainingBudgetMs, pollCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger?.Invoke($"[WaitForHandler] Poll #{pollCount} for '{condition}' hit remaining deadline budget ({remainingBudgetMs}ms).");
                break;
            }

            lastObservedState = checkResult.ObservedState;

            if (checkResult.Satisfied)
            {
                satisfied = true;
                break;
            }

            long remaining = deadline - sw.ElapsedMilliseconds;
            if (remaining <= 0) break;

            int delay = (int)Math.Min(intervalMs, remaining);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        sw.Stop();
        double elapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2);

        if (satisfied)
        {
            _logger?.Invoke($"[WaitForHandler] Condition '{condition}' satisfied in {elapsedMs}ms after {pollCount} poll(s).");
            return ToolCallResult.Json(new
            {
                status = "success",
                satisfied = true,
                condition,
                target,
                interfaceName = ifaceName,
                pollCount,
                elapsedMs,
                timeoutMs,
                requestedTimeoutMs,
                coldStartGraceApplied,
                observedState = lastObservedState
            });
        }
        else
        {
            var (classification, engineSnap, dynamicRemediation) =
                await ClassifyTimeoutAndRemediateAsync(condition, target, ifaceName, lastObservedState, cancellationToken).ConfigureAwait(false);

            string errorMsg = $"Wait timed out after {elapsedMs}ms ({pollCount} polls, classification='{classification}') waiting for condition '{condition}' on '{target}'. {dynamicRemediation}";
            _logger?.Invoke($"[WaitForHandler] {errorMsg}");
            return ToolCallResult.Json(new
            {
                status = "timeout",
                satisfied = false,
                timeoutClassification = classification,
                error = errorMsg,
                condition,
                target,
                interfaceName = ifaceName,
                pollCount,
                elapsedMs,
                timeoutMs,
                requestedTimeoutMs,
                coldStartGraceApplied,
                observedState = lastObservedState,
                engineSnapshot = engineSnap,
                actionableRemediation = dynamicRemediation
            }, isError: true);
        }
    }

    private async Task<(string Classification, object? EngineSnapshot, string Remediation)> ClassifyTimeoutAndRemediateAsync(
        string condition,
        string target,
        string ifaceName,
        object? lastObservedState,
        CancellationToken ct)
    {
        EngineStateDto? snap = null;
        IGameIpcBridge? bridge = _gameIpcBridge is CompositeGameIpcBridge comp ? comp.Primary : _gameIpcBridge;
        if (bridge is { IsConnected: true } && !ct.IsCancellationRequested)
        {
            try
            {
                using CancellationTokenSource snapCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                snapCts.CancelAfter(120);
                snap = await bridge.GetEngineStateAsync(120, snapCts.Token).WaitAsync(snapCts.Token).ConfigureAwait(false);
            }
            catch
            {
                snap = null;
            }
        }

        string observedJson = lastObservedState is null ? string.Empty : JsonSerializer.Serialize(lastObservedState);

        if (bridge is { IsConnected: false } && _taoClient == null)
        {
            return (
                "client_not_running_or_ipc_offline",
                null,
                "K2 client process or IPC pipe is offline. Launch via kongor_game_client_manage (action: 'launch') or kongor_client_practice before calling kongor_wait_for.");
        }

        if (snap?.MainThreadBusy == true || observedJson.Contains("\"main_thread_busy\"", StringComparison.OrdinalIgnoreCase))
        {
            return (
                "engine_loading_stall",
                snap,
                $"K2 main thread is currently busy loading resources/maps (hostFrame={snap?.HostFrame}, map='{snap?.CurrentMap}'). Retry kongor_wait_for with a larger timeoutMs (e.g. 15000-30000).");
        }

        if (observedJson.Contains("\"isEvalError\":true", StringComparison.OrdinalIgnoreCase))
        {
            return (
                "lua_predicate_error",
                snap,
                "The Lua wait expression threw an error or returned a non-value ACK. Test and fix the expression with kongor_client_eval_lua first.");
        }

        if (condition is "ui_visible" or "widget_visible" or "ui_present" or "ui_text")
        {
            if (snap != null && !snap.IsActiveMatch && ifaceName.StartsWith("game", StringComparison.OrdinalIgnoreCase))
            {
                return (
                    "wrong_game_phase",
                    snap,
                    $"Target interface '{ifaceName}' is an in-game HUD interface, but client is not in an active match (isActiveMatch=false, map='{snap.CurrentMap}'). Start a match via kongor_client_practice first.");
            }

            if (observedJson.Contains("\"found\":false", StringComparison.OrdinalIgnoreCase))
            {
                return (
                    "widget_not_found_in_interface",
                    snap,
                    $"Widget '{target}' was not found in interface '{ifaceName}' while engine frames are advancing. Inspect exact widget names via kongor_k2_ui_inspect (interface: '{ifaceName}').");
            }

            if (observedJson.Contains("\"found\":true", StringComparison.OrdinalIgnoreCase) &&
                observedJson.Contains("\"visible\":false", StringComparison.OrdinalIgnoreCase))
            {
                return (
                    "widget_present_but_hidden",
                    snap,
                    $"Widget '{target}' exists in interface '{ifaceName}' but IsVisible()==false. Trigger the parent container/tab first via kongor_ui_control or kongor_client_navigate.");
            }
        }

        return (
            "predicate_unsatisfied",
            snap,
            "Verify that the requested interface/widget or state transition was triggered using kongor_k2_ui_inspect or kongor_game_client_manage (action: 'status').");
    }

    private sealed class ReadinessPollContext
    {
        public int? FirstHostFrame { get; set; }
        public bool HostFrameAdvancing { get; set; }
        public int? LastObservedHostFrame { get; set; }
    }

    private async Task<(bool Satisfied, object? ObservedState)> EvaluateConditionAsync(
        string condition, string target, string ifaceName, string? expected, ReadinessPollContext pollContext, int remainingBudgetMs, CancellationToken ct)
    {
        switch (condition)
        {
            case "client_ready":
            case "engine_ready":
            case "ipc_ready":
            case "ipc":
            case "pipe_ready":
            {
                bool running = false;
                if (_processRegistry != null)
                {
                    var p = _processRegistry.GetPrimaryClient();
                    running = p != null && p.IsAlive;
                }
                if (!running && !GameServerManageHandler.IsTestExecution)
                {
                    var procs = Process.GetProcessesByName("hon_x64");
                    running = procs.Any(p => !p.HasExited);
                }

                if (_gameIpcBridge != null)
                {
                    IGameIpcBridge targetBridge = _gameIpcBridge is CompositeGameIpcBridge composite
                        ? composite.Primary
                        : _gameIpcBridge;

                    if (!targetBridge.IsConnected)
                    {
                        try
                        {
                            await targetBridge.ConnectAsync(Math.Min(350, remainingBudgetMs), ct).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                        {
                            _logger?.Invoke($"[WaitForHandler] IPC connect attempt during '{condition}' poll: {ex.Message}");
                        }
                    }

                    bool ipcConnected = targetBridge.IsConnected;
                    if (ipcConnected)
                    {
                        running = true;
                    }

                    if (!ipcConnected)
                    {
                        return (false, new
                        {
                            stage = "connecting",
                            running,
                            ipcConnected = false,
                            pipeName = targetBridge.PipeName
                        });
                    }

                    // Stage 1: ipc_ready — Named Pipe connected and responsive to Ping (0x0001 -> 0x0002)
                    if (condition is "ipc_ready" or "ipc" or "pipe_ready")
                    {
                        try
                        {
                            long pingUs = await targetBridge.PingAsync(Math.Min(350, remainingBudgetMs), ct).ConfigureAwait(false);
                            return (pingUs >= 0, new
                            {
                                stage = "ipc_ready",
                                running = true,
                                ipcConnected = true,
                                pingUs,
                                pipeName = targetBridge.PipeName
                            });
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                        {
                            return (false, new
                            {
                                stage = "connecting",
                                running,
                                ipcConnected = targetBridge.IsConnected,
                                error = ex.Message
                            });
                        }
                    }

                    // Stage 2: engine_ready — CHost::Execute main loop is ticking (hostFrame advancing across polls and !mainThreadBusy)
                    EngineStateDto? state = null;
                    try
                    {
                        state = await targetBridge.GetEngineStateAsync(Math.Min(500, remainingBudgetMs), ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                    {
                        return (false, new
                        {
                            stage = "ipc_ready",
                            running = true,
                            ipcConnected = targetBridge.IsConnected,
                            error = ex.Message
                        });
                    }

                    if (state == null)
                    {
                        return (false, new
                        {
                            stage = "ipc_ready",
                            running = true,
                            ipcConnected = true,
                            hostFrame = 0,
                            hostFrameAdvancing = false
                        });
                    }

                    int? rawHostFrame = state.HostFrame;
                    int hostFrame = rawHostFrame ?? 0;
                    bool mainThreadBusy = state.MainThreadBusy;
                    bool hostFrameAdvancing = false;
                    bool legacyLuaFallbackUsed = false;

                    if (rawHostFrame.HasValue)
                    {
                        if (hostFrame > 0)
                        {
                            if (!pollContext.FirstHostFrame.HasValue)
                            {
                                pollContext.FirstHostFrame = hostFrame;
                            }
                            else if (hostFrame > pollContext.FirstHostFrame.Value)
                            {
                                hostFrameAdvancing = true;
                            }
                        }
                        // Note: when rawHostFrame.HasValue && hostFrame <= 0, the new version.dll is still in early
                        // cold-boot before g_ppHost resolves. Do NOT dispatch a Lua probe (which would block ~2.7s).
                    }
                    else if (!mainThreadBusy)
                    {
                        // Older version.dll without hostFrame in 0x0005 JSON: prove main-thread liveness via a
                        // deadline-clamped 0x000E Lua round-trip.
                        legacyLuaFallbackUsed = true;
                        try
                        {
                            string livenessOut = await targetBridge.ExecuteLuaAsync(
                                "return '1'", Math.Min(600, remainingBudgetMs), ct).ConfigureAwait(false);
                            hostFrameAdvancing = string.Equals(livenessOut?.Trim(), "1", StringComparison.Ordinal);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                        {
                            _logger?.Invoke($"[WaitForHandler] Legacy hostFrame-absent Lua liveness probe failed: {ex.Message}");
                            hostFrameAdvancing = false;
                        }
                    }

                    bool engineTickReady = hostFrameAdvancing && !mainThreadBusy;
                    if (condition == "engine_ready")
                    {
                        return (engineTickReady, new
                        {
                            stage = engineTickReady ? "engine_ready" : "ipc_ready",
                            running = true,
                            ipcConnected = true,
                            firstHostFrame = pollContext.FirstHostFrame,
                            hostFrame = rawHostFrame,
                            msSinceHostFrameAdvance = state.MsSinceHostFrameAdvance,
                            hostFrameAdvancing,
                            legacyLuaFallbackUsed,
                            mainThreadBusy,
                            activeOpElapsedMs = state.ActiveOpElapsedMs,
                            activeMatchId = state.ActiveMatchId,
                            serverMatchId = state.ServerMatchId,
                            gamePhase = state.GamePhase
                        });
                    }

                    // Stage 3: client_ready — engine_ready PLUS UIManager.GetInterface(ifaceName) ~= nil over 0x000E EvalLuaSync
                    if (!engineTickReady)
                    {
                        return (false, new
                        {
                            stage = "ipc_ready",
                            running = true,
                            ipcConnected = true,
                            firstHostFrame = pollContext.FirstHostFrame,
                            hostFrame = rawHostFrame,
                            msSinceHostFrameAdvance = state.MsSinceHostFrameAdvance,
                            hostFrameAdvancing,
                            legacyLuaFallbackUsed,
                            mainThreadBusy,
                            activeOpElapsedMs = state.ActiveOpElapsedMs,
                            interfaceName = ifaceName,
                            uiInterfaceLoaded = false
                        });
                    }

                    bool uiInterfaceLoaded = false;
                    string? probeOutput = null;
                    try
                    {
                        string luaProbe = $"return tostring(UIManager ~= nil and UIManager.GetInterface ~= nil and UIManager.GetInterface('{ifaceName}') ~= nil)";
                        probeOutput = await targetBridge.ExecuteLuaAsync(luaProbe, Math.Min(600, remainingBudgetMs), ct).ConfigureAwait(false);
                        uiInterfaceLoaded = string.Equals(probeOutput?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                    {
                        probeOutput = ex.Message;
                    }

                    return (uiInterfaceLoaded, new
                    {
                        stage = uiInterfaceLoaded ? "client_ready" : "engine_ready",
                        running = true,
                        ipcConnected = true,
                        firstHostFrame = pollContext.FirstHostFrame,
                        hostFrame = rawHostFrame,
                        msSinceHostFrameAdvance = state.MsSinceHostFrameAdvance,
                        hostFrameAdvancing = true,
                        legacyLuaFallbackUsed,
                        mainThreadBusy = false,
                        interfaceName = ifaceName,
                        uiInterfaceLoaded,
                        probeOutput
                    });
                }

                if (!running) return (false, new { running = false, mailbox = false, ipcConnected = false });

                bool mailboxConnected = false;
                if (_taoClient != null)
                {
                    var ping = await _taoClient.DispatchAsync("ping", new { }, 500, ct).ConfigureAwait(false);
                    mailboxConnected = ping != null && ping.Success;
                }

                return (running && mailboxConnected, new { running, mailboxConnected, ipcConnected = false });
            }

            case "game_phase":
            case "phase":
            {
                IGameIpcBridge? targetBridge = _gameIpcBridge is CompositeGameIpcBridge composite
                    ? composite.Primary
                    : _gameIpcBridge;

                if (targetBridge != null)
                {
                    if (!targetBridge.IsConnected)
                    {
                        try
                        {
                            await targetBridge.ConnectAsync(Math.Min(350, remainingBudgetMs), ct).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                        {
                            _logger?.Invoke($"[WaitForHandler] IPC connect attempt during '{condition}' poll: {ex.Message}");
                        }
                    }

                    if (targetBridge.IsConnected)
                    {
                        EngineStateDto? state = null;
                        try
                        {
                            state = await targetBridge.GetEngineStateAsync(Math.Min(300, remainingBudgetMs), ct).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                        {
                            _logger?.Invoke($"[WaitForHandler] GetEngineStateAsync during '{condition}' poll: {ex.Message}");
                        }

                        if (state != null)
                        {
                            if (int.TryParse(target, out int tp) || int.TryParse(expected, out tp))
                            {
                                bool matches = state.GamePhase == tp || state.CanonicalGamePhase == tp;
                                return (matches, new
                                {
                                    stage = "game_phase",
                                    gamePhase = state.GamePhase,
                                    canonicalGamePhase = state.CanonicalGamePhase,
                                    gamePhaseName = state.GamePhaseName,
                                    targetPhase = tp
                                });
                            }

                            string? query = !string.IsNullOrWhiteSpace(target) ? target : expected;
                            if (!string.IsNullOrWhiteSpace(query))
                            {
                                bool matches = state.GamePhaseName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                               state.GamePhase.ToString().Equals(query, StringComparison.OrdinalIgnoreCase);
                                return (matches, new
                                {
                                    stage = "game_phase",
                                    gamePhase = state.GamePhase,
                                    canonicalGamePhase = state.CanonicalGamePhase,
                                    gamePhaseName = state.GamePhaseName,
                                    query
                                });
                            }

                            bool nonZero = state.GamePhase > 0;
                            return (nonZero, new
                            {
                                stage = "game_phase",
                                gamePhase = state.GamePhase,
                                canonicalGamePhase = state.CanonicalGamePhase,
                                gamePhaseName = state.GamePhaseName
                            });
                        }
                    }
                }

                if (_taoClient != null)
                {
                    var ping = await _taoClient.DispatchAsync("ping", new { }, 500, ct).ConfigureAwait(false);
                    if (ping != null && ping.Success && ping.Data.TryGetProperty("phase", out var phaseEl))
                    {
                        int pVal = phaseEl.GetInt32();
                        if (int.TryParse(target, out int tp) || int.TryParse(expected, out tp))
                        {
                            return (pVal == tp, new { gamePhase = pVal, targetPhase = tp });
                        }
                        return (pVal > 0, new { gamePhase = pVal });
                    }
                }

                return (false, new { error = "Game phase information unavailable" });
            }

            case "map_loaded":
            case "active_match":
            {
                IGameIpcBridge? targetBridge = _gameIpcBridge is CompositeGameIpcBridge composite
                    ? composite.Primary
                    : _gameIpcBridge;

                if (targetBridge != null)
                {
                    if (!targetBridge.IsConnected)
                    {
                        try
                        {
                            await targetBridge.ConnectAsync(Math.Min(350, remainingBudgetMs), ct).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                        {
                            _logger?.Invoke($"[WaitForHandler] IPC connect attempt during '{condition}' poll: {ex.Message}");
                        }
                    }

                    if (targetBridge.IsConnected)
                    {
                        EngineStateDto? state = null;
                        try
                        {
                            state = await targetBridge.GetEngineStateAsync(Math.Min(300, remainingBudgetMs), ct).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                        {
                            _logger?.Invoke($"[WaitForHandler] GetEngineStateAsync during '{condition}' poll: {ex.Message}");
                        }

                        if (state != null)
                        {
                            if (condition == "active_match")
                            {
                                bool matches = state.IsActiveMatch || state.ActiveMatchId > 0;
                                return (matches, new
                                {
                                    stage = "active_match",
                                    isActiveMatch = state.IsActiveMatch,
                                    activeMatchId = state.ActiveMatchId,
                                    currentMap = state.CurrentMap
                                });
                            }
                            else
                            {
                                bool hasMap = !string.IsNullOrWhiteSpace(state.CurrentMap) &&
                                              !state.CurrentMap.Equals("none", StringComparison.OrdinalIgnoreCase);
                                string? query = !string.IsNullOrWhiteSpace(target) ? target : expected;
                                bool matches = !string.IsNullOrWhiteSpace(query)
                                    ? state.CurrentMap.Contains(query, StringComparison.OrdinalIgnoreCase)
                                    : (hasMap || state.IsActiveMatch);

                                return (matches, new
                                {
                                    stage = "map_loaded",
                                    mapLoaded = matches,
                                    currentMap = state.CurrentMap,
                                    isActiveMatch = state.IsActiveMatch,
                                    target = query
                                });
                            }
                        }
                    }
                }

                return (false, new { error = "Map / match state unavailable" });
            }

            case "frame_advance":
            case "host_frame":
            case "frame":
            {
                IGameIpcBridge? targetBridge = _gameIpcBridge is CompositeGameIpcBridge composite
                    ? composite.Primary
                    : _gameIpcBridge;

                if (targetBridge != null)
                {
                    if (!targetBridge.IsConnected)
                    {
                        try
                        {
                            await targetBridge.ConnectAsync(Math.Min(350, remainingBudgetMs), ct).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                        {
                            _logger?.Invoke($"[WaitForHandler] IPC connect attempt during '{condition}' poll: {ex.Message}");
                        }
                    }

                    if (targetBridge.IsConnected)
                    {
                        EngineStateDto? state = null;
                        try
                        {
                            state = await targetBridge.GetEngineStateAsync(Math.Min(300, remainingBudgetMs), ct).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                        {
                            _logger?.Invoke($"[WaitForHandler] GetEngineStateAsync during '{condition}' poll: {ex.Message}");
                        }

                        if (state != null && state.HostFrame.HasValue)
                        {
                            int hostFrame = state.HostFrame.Value;
                            if (!pollContext.FirstHostFrame.HasValue)
                            {
                                pollContext.FirstHostFrame = hostFrame;
                                return (false, new
                                {
                                    stage = "frame_advance",
                                    hostFrame,
                                    firstHostFrame = hostFrame,
                                    framesAdvanced = 0
                                });
                            }

                            int requiredDelta = 1;
                            if (int.TryParse(target, out int td) && td > 0) requiredDelta = td;
                            else if (int.TryParse(expected, out int ed) && ed > 0) requiredDelta = ed;

                            int advanced = hostFrame - pollContext.FirstHostFrame.Value;
                            bool matches = advanced >= requiredDelta;
                            return (matches, new
                            {
                                stage = "frame_advance",
                                hostFrame,
                                firstHostFrame = pollContext.FirstHostFrame.Value,
                                framesAdvanced = advanced,
                                requiredDelta
                            });
                        }
                    }
                }

                return (false, new { error = "Host frame telemetry unavailable" });
            }

            case "ui_visible":
            case "widget_visible":
            case "ui_hidden":
            case "ui_invisible":
            case "ui_present":
            case "ui_text":
            {
                // Fast-path / frame stall gate: skip redundant polling if main thread is busy or host frame hasn't advanced
                if (_gameIpcBridge != null)
                {
                    IGameIpcBridge? targetBridge = _gameIpcBridge is CompositeGameIpcBridge composite
                        ? composite.Primary
                        : _gameIpcBridge;

                    if (targetBridge != null && targetBridge.IsConnected)
                    {
                        EngineStateDto? state = null;
                        try
                        {
                            state = await targetBridge.GetEngineStateAsync(Math.Min(100, remainingBudgetMs), ct).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                        {
                        }

                        if (state != null)
                        {
                            if (state.MainThreadBusy)
                            {
                                _logger?.Invoke($"[WaitForHandler] Main thread busy ({state.ActiveOpElapsedMs}ms), skipping '{condition}' poll this tick.");
                                return (false, new { stage = "main_thread_busy", activeOpElapsedMs = state.ActiveOpElapsedMs, hostFrame = state.HostFrame });
                            }

                            if (state.HostFrame.HasValue)
                            {
                                if (pollContext.LastObservedHostFrame.HasValue && state.HostFrame.Value == pollContext.LastObservedHostFrame.Value)
                                {
                                    _logger?.Invoke($"[WaitForHandler] HostFrame {state.HostFrame.Value} unchanged, skipping redundant '{condition}' poll this tick.");
                                    return (false, new { stage = "frame_not_advanced", hostFrame = state.HostFrame.Value });
                                }
                                pollContext.LastObservedHostFrame = state.HostFrame.Value;
                            }
                        }
                    }
                }
                if (_taoClient == null)
                {
                    return (false, new { error = "TaoClient not configured." });
                }

                var inspectPayload = new
                {
                    widget = target,
                    @interface = ifaceName,
                    maxDepth = 2
                };

                var resp = await _taoClient.DispatchAsync("inspect_ui", inspectPayload, 1000, ct).ConfigureAwait(false);
                if (resp == null || !resp.Success || string.Equals(resp.Status, "error", StringComparison.OrdinalIgnoreCase))
                {
                    return (false, new { found = false, error = resp?.Error ?? "Dispatch failed" });
                }

                if (!resp.Data.TryGetProperty("found", out var foundEl) || !foundEl.GetBoolean())
                {
                    if (condition == "ui_hidden" || condition == "ui_invisible")
                    {
                        return (true, new { found = false, visible = false });
                    }
                    return (false, new { found = false });
                }

                if (condition == "ui_present")
                {
                    return (true, new { found = true, data = resp.Data });
                }

                // Check root or widget visible property
                bool isVisible = false;
                string actualText = "";
                if (resp.Data.TryGetProperty("root", out var rootNode))
                {
                    if (rootNode.TryGetProperty("visible", out var vEl)) isVisible = vEl.GetBoolean();
                    if (rootNode.TryGetProperty("text", out var tEl)) actualText = tEl.GetString() ?? "";
                }

                if (condition is "ui_visible" or "widget_visible")
                {
                    return (isVisible, new { found = true, visible = isVisible, actualText });
                }

                if (condition == "ui_hidden" || condition == "ui_invisible")
                {
                    return (!isVisible, new { found = true, visible = isVisible, actualText });
                }

                if (condition == "ui_text")
                {
                    bool textMatches = !string.IsNullOrEmpty(expected) &&
                        (string.Equals(actualText, expected, StringComparison.OrdinalIgnoreCase) ||
                         actualText.Contains(expected, StringComparison.OrdinalIgnoreCase));

                    return (isVisible && textMatches, new { found = true, visible = isVisible, actualText, expected });
                }

                return (false, null);
            }

            case "lua":
            case "custom_lua":
            case "eval":
            {
                if (_taoClient == null && _gameIpcBridge == null)
                {
                    return (false, new { error = "No dispatch bridge available for Lua evaluation." });
                }

                try
                {
                    string output = "";
                    IGameIpcBridge? targetBridge = _gameIpcBridge is CompositeGameIpcBridge composite
                        ? composite.Primary
                        : _gameIpcBridge;

                    if (targetBridge != null && !targetBridge.IsConnected)
                    {
                        try
                        {
                            await targetBridge.ConnectAsync(Math.Min(350, remainingBudgetMs), ct).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                        {
                            _logger?.Invoke($"[WaitForHandler] IPC connect attempt during '{condition}' poll: {ex.Message}");
                        }
                    }

                    // Fast-path / frame stall gate: skip redundant polling if main thread is busy or host frame hasn't advanced
                    if (targetBridge != null && targetBridge.IsConnected)
                    {
                        EngineStateDto? state = null;
                        try
                        {
                            state = await targetBridge.GetEngineStateAsync(Math.Min(100, remainingBudgetMs), ct).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                        {
                        }

                        if (state != null)
                        {
                            if (state.MainThreadBusy)
                            {
                                _logger?.Invoke($"[WaitForHandler] Main thread busy ({state.ActiveOpElapsedMs}ms), skipping Lua poll this tick.");
                                return (false, new { stage = "main_thread_busy", activeOpElapsedMs = state.ActiveOpElapsedMs, hostFrame = state.HostFrame });
                            }

                            if (state.HostFrame.HasValue)
                            {
                                if (pollContext.LastObservedHostFrame.HasValue && state.HostFrame.Value == pollContext.LastObservedHostFrame.Value)
                                {
                                    _logger?.Invoke($"[WaitForHandler] HostFrame {state.HostFrame.Value} unchanged, skipping redundant Lua poll this tick.");
                                    return (false, new { stage = "frame_not_advanced", hostFrame = state.HostFrame.Value });
                                }
                                pollContext.LastObservedHostFrame = state.HostFrame.Value;
                            }
                        }
                    }

                    int probeBudgetMs = Math.Min(1000, remainingBudgetMs);
                    using CancellationTokenSource probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    probeCts.CancelAfter(probeBudgetMs);

                    string normalizedLuaExpr = NormalizeLuaWaitExpression(target);
                    if (targetBridge != null && targetBridge.IsConnected)
                    {
                        output = await targetBridge.ExecuteLuaAsync($"return tostring(({normalizedLuaExpr}))", probeBudgetMs, probeCts.Token).ConfigureAwait(false);
                    }
                    else if (_taoClient != null)
                    {
                        TaoResponse<JsonElement>? resp = await _taoClient.DispatchAsync("eval", new { expression = normalizedLuaExpr }, probeBudgetMs, probeCts.Token).ConfigureAwait(false);
                        if (resp == null || !resp.Success)
                        {
                            resp = await _taoClient.DispatchAsync("inspect_ui", new { lua = normalizedLuaExpr }, probeBudgetMs, probeCts.Token).ConfigureAwait(false);
                        }
                        if (resp != null)
                        {
                            if (!resp.Success || string.Equals(resp.Status, "error", StringComparison.OrdinalIgnoreCase))
                            {
                                return (false, new { output = "", isEvalError = true, error = resp.Error ?? "Lua evaluation failed via TaoClient." });
                            }

                            if (resp.Data.ValueKind == JsonValueKind.Object && resp.Data.TryGetProperty("result", out JsonElement resProp))
                            {
                                output = resProp.ToString();
                            }
                            else
                            {
                                output = resp.Data.ToString();
                            }
                        }
                    }

                    string trimmed = (output ?? string.Empty).Trim();
                    if (trimmed.StartsWith("[LUA_OK]", StringComparison.OrdinalIgnoreCase))
                    {
                        trimmed = trimmed[8..].Trim();
                    }

                    bool isEvalErrorOrQueuedAck =
                        string.IsNullOrEmpty(trimmed) ||
                        trimmed.StartsWith("[ERROR]", StringComparison.OrdinalIgnoreCase) ||
                        trimmed.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) ||
                        trimmed.StartsWith("ERR_", StringComparison.OrdinalIgnoreCase) ||
                        trimmed.StartsWith("Lua Error", StringComparison.OrdinalIgnoreCase) ||
                        trimmed.StartsWith("[LUA_ERR]", StringComparison.OrdinalIgnoreCase) ||
                        trimmed.StartsWith("[SECURITY ERROR]", StringComparison.OrdinalIgnoreCase) ||
                        trimmed.StartsWith("[OK] Command queued", StringComparison.OrdinalIgnoreCase) ||
                        (trimmed.StartsWith('{') && trimmed.Contains("\"success\":false", StringComparison.OrdinalIgnoreCase));

                    if (isEvalErrorOrQueuedAck)
                    {
                        return (false, new
                        {
                            output = trimmed,
                            isEvalError = true,
                            error = $"Lua evaluation error or non-value ACK: {trimmed}"
                        });
                    }

                    if (!string.IsNullOrEmpty(expected))
                    {
                        bool matchesExpected = string.Equals(trimmed, expected, StringComparison.OrdinalIgnoreCase);
                        return (matchesExpected, new { output = trimmed, expected });
                    }

                    bool truthy = !trimmed.Equals("nil", StringComparison.OrdinalIgnoreCase) &&
                                  !trimmed.Equals("false", StringComparison.OrdinalIgnoreCase) &&
                                  !trimmed.Equals("0", StringComparison.OrdinalIgnoreCase);

                    return (truthy, new { output = trimmed });
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    return (false, new { error = ex.Message });
                }
            }

            default:
                return (false, new { error = $"Unknown wait condition '{condition}'" });
        }
    }

    private TimeSpan? ResolveClientProcessAge()
    {
        if (_clientProcessAgeProvider != null)
        {
            return _clientProcessAgeProvider();
        }

        DateTimeOffset nowUtc = _timeProvider.GetUtcNow();
        ClientInstance? primary = _processRegistry?.GetPrimaryClient();
        if (primary != null && primary.IsAlive && primary.LaunchedAt != default)
        {
            TimeSpan age = nowUtc - primary.LaunchedAt;
            if (age >= TimeSpan.Zero)
            {
                return age;
            }
        }

        if (GameServerManageHandler.IsTestExecution)
        {
            return null;
        }

        try
        {
            Process[] procs = Process.GetProcessesByName("hon_x64");
            TimeSpan? youngestAge = null;
            try
            {
                foreach (Process p in procs)
                {
                    try
                    {
                        if (p.HasExited) continue;
                        DateTimeOffset startedUtc = new(p.StartTime.ToUniversalTime(), TimeSpan.Zero);
                        TimeSpan age = nowUtc - startedUtc;
                        if (age >= TimeSpan.Zero && (!youngestAge.HasValue || age < youngestAge.Value))
                        {
                            youngestAge = age;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.Invoke($"[WaitForHandler] Cold-start age probe skipped for PID {p.Id}: {ex.Message}");
                    }
                }
            }
            finally
            {
                foreach (Process p in procs)
                {
                    p.Dispose();
                }
            }

            return youngestAge;
        }
        catch (Exception ex)
        {
            _logger?.Invoke($"[WaitForHandler] Process enumeration for cold-start probe skipped: {ex.Message}");
            return null;
        }
    }

    internal int ResolveEffectiveTimeoutMs(
        JsonElement arguments,
        string condition,
        int requestedTimeoutMs,
        out bool coldStartGraceApplied,
        out double? clientAgeSeconds)
    {
        coldStartGraceApplied = false;
        clientAgeSeconds = null;

        if (!SupportedConditions.Contains(condition) || requestedTimeoutMs >= ColdStartTimeoutFloorMs)
        {
            return requestedTimeoutMs;
        }

        bool strictTimeout =
            arguments.ValueKind == JsonValueKind.Object &&
            ((arguments.TryGetProperty("strictTimeout", out JsonElement stEl) && stEl.ValueKind == JsonValueKind.True) ||
             (arguments.TryGetProperty("strict_timeout", out JsonElement stEl2) && stEl2.ValueKind == JsonValueKind.True));
        if (strictTimeout)
        {
            return requestedTimeoutMs;
        }

        TimeSpan? age = ResolveClientProcessAge();
        if (age.HasValue && age.Value >= TimeSpan.Zero && age.Value < TimeSpan.FromSeconds(ColdStartWindowSeconds))
        {
            coldStartGraceApplied = true;
            clientAgeSeconds = Math.Round(age.Value.TotalSeconds, 2);
            return ColdStartTimeoutFloorMs;
        }

        return requestedTimeoutMs;
    }

    public static int ResolveTimeoutMs(JsonElement arguments, string condition)
    {
        int defaultTimeoutMs = condition switch
        {
            "client_ready" or "engine_ready" => 60000,
            "ipc_ready" or "ipc" or "pipe_ready" => 15000,
            _ => 5000
        };
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return defaultTimeoutMs;
        }

        string[] msAliases = ["timeoutMs", "timeout_ms", "max_wait_ms", "maxWaitMs"];
        foreach (string key in msAliases)
        {
            if (arguments.TryGetProperty(key, out JsonElement el) && TryReadInt(el, out int val) && val > 0)
            {
                return Math.Clamp(val, 100, 120000);
            }
        }

        string[] secAliases = ["timeout_sec", "timeoutSec", "timeout_seconds", "timeoutSeconds"];
        foreach (string key in secAliases)
        {
            if (arguments.TryGetProperty(key, out JsonElement el) && TryReadInt(el, out int secVal) && secVal > 0)
            {
                return Math.Clamp(secVal * 1000, 100, 120000);
            }
        }

        if (arguments.TryGetProperty("timeout", out JsonElement timeoutEl) && TryReadInt(timeoutEl, out int tVal) && tVal > 0)
        {
            int resolved = tVal <= 120 ? tVal * 1000 : tVal;
            return Math.Clamp(resolved, 100, 120000);
        }

        return defaultTimeoutMs;
    }

    public static string NormalizeLuaWaitExpression(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return "nil";
        }

        string expr = target.Trim();
        while (expr.EndsWith(';'))
        {
            expr = expr[..^1].TrimEnd();
        }

        if (string.Equals(expr, "return", StringComparison.Ordinal))
        {
            return "nil";
        }

        if (expr.StartsWith("return", StringComparison.Ordinal) &&
            expr.Length > 6 &&
            !(char.IsLetterOrDigit(expr[6]) || expr[6] == '_'))
        {
            expr = expr[6..].TrimStart();
            while (expr.EndsWith(';'))
            {
                expr = expr[..^1].TrimEnd();
            }
        }

        return string.IsNullOrWhiteSpace(expr) ? "nil" : expr;
    }

    private static bool TryReadInt(JsonElement el, out int value)
    {
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out value))
        {
            return true;
        }
        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out value))
        {
            return true;
        }
        value = 0;
        return false;
    }

    public static string[] SuggestClosestConditions(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return ["ipc_ready", "engine_ready", "client_ready"];
        }

        string norm = input.Trim().ToLowerInvariant().Replace("-", "_");
        return CanonicalConditions
            .Select(c =>
            {
                string prefix = c.Split('_')[0];
                int bonus = norm.Contains(prefix, StringComparison.OrdinalIgnoreCase) ? -4 : 0;
                return (Condition: c, Score: ComputeLevenshteinDistance(norm, c) + bonus);
            })
            .OrderBy(x => x.Score)
            .ThenBy(x => x.Condition, StringComparer.Ordinal)
            .Take(3)
            .Select(x => x.Condition)
            .ToArray();
    }

    private static int ComputeLevenshteinDistance(string a, string b)
    {
        int n = a.Length;
        int m = b.Length;
        int[,] d = new int[n + 1, m + 1];
        for (int i = 0; i <= n; i++) d[i, 0] = i;
        for (int j = 0; j <= m; j++) d[0, j] = j;
        for (int i = 1; i <= n; i++)
        {
            for (int j = 1; j <= m; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
            }
        }
        return d[n, m];
    }
}

