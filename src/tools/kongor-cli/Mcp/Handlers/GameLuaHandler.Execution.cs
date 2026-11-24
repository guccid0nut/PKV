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

public sealed partial class GameLuaHandler
{
    private async Task<ToolCallResult> ExecuteServerLuaAsync(
        int port,
        string formattedCommand,
        string action,
        bool autoLaunch,
        string channel,
        string? resolutionReason,
        CancellationToken cancellationToken)
    {
        if (_serverManageHandler != null)
        {
            bool autoLaunchServer = (port == 11235) && autoLaunch;
            var serverRes = await _serverManageHandler.ExecuteCommandOnPortAsync(
                port,
                formattedCommand,
                autoLaunchServer,
                channel,
                resolutionReason,
                cancellationToken).ConfigureAwait(false);

            if (serverRes.IsError && serverRes.Content.FirstOrDefault() is TextContentItem tc && !string.IsNullOrWhiteSpace(tc.Text))
            {
                _remediationService.RecordAndRemediate(formattedCommand, tc.Text, channel, $"server:{port}");
            }
            return serverRes;
        }

        // Standalone Named Pipe fallback via GameIpcBridge
        try
        {
            await using var pipeBridge = new GameIpcBridge($"hon-server-{port}");
            await pipeBridge.ConnectAsync(1000, cancellationToken).ConfigureAwait(false);
            string output = await pipeBridge.ExecuteCommandAsync(formattedCommand, 3000, cancellationToken).ConfigureAwait(false);

            bool isEngineError = output.Contains("attempt to index", StringComparison.OrdinalIgnoreCase) ||
                                 output.Contains("syntax error", StringComparison.OrdinalIgnoreCase) ||
                                 output.Contains("is read only", StringComparison.OrdinalIgnoreCase);

            if (isEngineError)
            {
                var rec = _remediationService.RecordAndRemediate(formattedCommand, output, channel, $"server:{port}");
                return ToolCallResult.Json(new
                {
                    status = "engine_error",
                    target = $"server:{port}",
                    port,
                    resolvedPort = port,
                    resolutionReason = resolutionReason ?? "explicit_port",
                    action,
                    command = formattedCommand,
                    response = output,
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
                action,
                command = formattedCommand,
                response = output,
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            });
        }
        catch (Exception ex)
        {
            var rec = _remediationService.RecordAndRemediate(formattedCommand, ex.Message, channel, $"server:{port}");
            return ToolCallResult.Json(new
            {
                status = "error",
                target = $"server:{port}",
                port,
                resolvedPort = port,
                resolutionReason = resolutionReason ?? "explicit_port",
                command = formattedCommand,
                message = $"Failed to execute Lua command on server port {port}: {ex.Message}",
                remediationHint = rec.Remediation.RemediationHint,
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            }, isError: true);
        }
    }

    private async Task<ToolCallResult> ExecuteClientLuaAsync(
        string formattedCommand,
        string action,
        bool preSelfHealed,
        string? originalInput,
        string? selfHealHint,
        string? selfHealCategory,
        int timeoutMs,
        bool autoLaunch,
        bool allowKeystroke,
        Stopwatch sw,
        int? processId,
        string? verifyWidgetName,
        string? verifyStateExpr,
        bool captureProof,
        CancellationToken cancellationToken)
    {
        ToolCallResult baseResult;

        if (_clientManageHandler != null)
        {
            var res = await _clientManageHandler.ExecuteClientCommandAsync(formattedCommand, timeoutMs, autoLaunch, allowKeystroke, processId, cancellationToken).ConfigureAwait(false);
            baseResult = EnrichToolResult(res, action, formattedCommand, preSelfHealed, originalInput, selfHealHint, selfHealCategory);
            return await AppendVerificationAsync(baseResult, verifyWidgetName, verifyStateExpr, captureProof, cancellationToken).ConfigureAwait(false);
        }

        // Direct Named Pipe IPC Bridge (when clientManageHandler not provided)
        if (_gameIpcBridge != null)
        {
            bool isTestMock = _gameIpcBridge.GetType().Assembly.GetName().Name != typeof(GameIpcBridge).Assembly.GetName().Name;
            if (!_gameIpcBridge.IsConnected && (!GameServerManageHandler.IsTestExecution || isTestMock))
            {
                try
                {
                    await _gameIpcBridge.ConnectAsync(1000, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger($"[GameLua] Proactive IPC connection attempt failed: {ex.Message}");
                }
            }

            if (_gameIpcBridge.IsConnected)
            {
                try
                {
                    string output = await _gameIpcBridge.ExecuteCommandAsync(formattedCommand, timeoutMs, cancellationToken).ConfigureAwait(false);
                    sw.Stop();
                    baseResult = EnrichToolResult(ToolCallResult.Json(new
                    {
                        status = "success",
                        target = "client",
                        channel = "ipc",
                        command = formattedCommand,
                        response = output,
                        executionDurationMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                        timestamp = _timeProvider.GetUtcNow().ToString("o")
                    }), action, formattedCommand, preSelfHealed, originalInput, selfHealHint, selfHealCategory);
                    return await AppendVerificationAsync(baseResult, verifyWidgetName, verifyStateExpr, captureProof, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger($"[GameLua] IPC execution failed: {ex.Message}. Falling back to in-game console.");
                }
            }
        }

        // Direct in-game console actuation fallback (if keystroke fallback permitted)
        if (!allowKeystroke)
        {
            sw.Stop();
            string suppressErrTxt = "IPC channel unavailable and keystroke fallback (Ctrl+F8) is suppressed to prevent desktop focus theft.";
            baseResult = ToolCallResult.Json(new
            {
                status = "error",
                action = action != "code" ? action : null,
                target = "client",
                channel = "none",
                command = formattedCommand,
                errorMessage = suppressErrTxt,
                remediationCategory = "KeystrokeFallbackSuppressed",
                remediationHint = "Ensure game client is active with Nexus Mailbox IPC enabled in userdataKONGOR/extensions/nexus_ipc.",
                executionDurationMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            }, isError: true);
            return await AppendVerificationAsync(baseResult, verifyWidgetName, verifyStateExpr, captureProof, cancellationToken).ConfigureAwait(false);
        }

        int? actPid = processId ?? _clientManageHandler?.GetRunningClientProcess()?.Id;
        string consoleTarget = actPid.HasValue && actPid.Value > 0 ? $"pid:{actPid.Value}" : "hon_x64";

        UiConsoleCommandResult consoleRes = await _uiControlService.ExecuteInGameConsoleCommandAsync(consoleTarget, formattedCommand, cancellationToken).ConfigureAwait(false);
        if (consoleRes.Success)
        {
            sw.Stop();
            baseResult = ToolCallResult.Json(new
            {
                status = "success",
                action = action != "code" ? action : null,
                target = "client",
                channel = "in_game_console",
                selfHealed = preSelfHealed,
                remediationCategory = preSelfHealed ? selfHealCategory : null,
                originalScript = originalInput,
                command = formattedCommand,
                remediationHint = selfHealHint,
                windowTitle = consoleRes.WindowTitle,
                hwnd = consoleRes.Hwnd,
                response = $"Executed '{formattedCommand}' via native in-game console.",
                executionDurationMs = consoleRes.ExecutionDurationMs,
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            });
            return await AppendVerificationAsync(baseResult, verifyWidgetName, verifyStateExpr, captureProof, cancellationToken).ConfigureAwait(false);
        }

        // Physical mouse click last resort fallback
        string? clickTarget = null;
        if (formattedCommand.StartsWith("Call ", StringComparison.OrdinalIgnoreCase))
        {
            string[] parts = formattedCommand.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2) clickTarget = parts[1];
        }
        else if (action == "open_store")
        {
            clickTarget = "store_button";
        }
        else if (action == "practice_match")
        {
            clickTarget = "sysbar_quickStart";
        }

        if (clickTarget != null)
        {
            string targetWidget = clickTarget;
            _logger($"[GameLua] In-game console failed. Attempting physical mouse click on '{targetWidget}' as last resort...");
            var clickRes = await _uiControlService.ClickAsync(consoleTarget, elementName: targetWidget, ct: cancellationToken).ConfigureAwait(false);
            if (clickRes.Success)
            {
                sw.Stop();
                baseResult = ToolCallResult.Json(new
                {
                    status = "success",
                    action = action != "code" ? action : null,
                    target = "client",
                    channel = "physical_click",
                    selfHealed = preSelfHealed,
                    remediationCategory = preSelfHealed ? selfHealCategory : null,
                    originalScript = originalInput,
                    command = formattedCommand,
                    elementClicked = clickRes.ElementClicked,
                    remediationHint = selfHealHint,
                    response = $"Clicked widget '{clickRes.ElementClicked}' via physical mouse click last resort fallback.",
                    executionDurationMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                    timestamp = _timeProvider.GetUtcNow().ToString("o")
                });
                return await AppendVerificationAsync(baseResult, verifyWidgetName, verifyStateExpr, captureProof, cancellationToken).ConfigureAwait(false);
            }
        }

        sw.Stop();
        string errTxt = consoleRes.ErrorMessage ?? "Game client is not currently running.";
        var errRec = _remediationService.RecordAndRemediate(formattedCommand, errTxt, "none", "client");

        baseResult = ToolCallResult.Json(new
        {
            status = "error",
            action = action != "code" ? action : null,
            target = "client",
            channel = "none",
            command = formattedCommand,
            errorMessage = errTxt,
            remediationCategory = errRec.Remediation.ErrorCategory,
            remediationHint = errRec.Remediation.RemediationHint,
            improvedCommand = errRec.Remediation.ImprovedCommand,
            executionDurationMs = consoleRes.ExecutionDurationMs,
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        }, isError: true);
        return await AppendVerificationAsync(baseResult, verifyWidgetName, verifyStateExpr, captureProof, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ToolCallResult> AppendVerificationAsync(
        ToolCallResult baseResult,
        string? verifyWidgetName,
        string? verifyStateExpr,
        bool captureProof,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(verifyWidgetName) &&
            string.IsNullOrWhiteSpace(verifyStateExpr) &&
            !captureProof)
        {
            return baseResult;
        }

        if (baseResult.Content.Count == 0 || baseResult.Content[0] is not TextContentItem textItem)
        {
            return baseResult;
        }

        try
        {
            Dictionary<string, object?> dict = JsonSerializer.Deserialize<Dictionary<string, object?>>(textItem.Text) ?? [];

            if (!string.IsNullOrWhiteSpace(verifyWidgetName))
            {
                var (verified, verifyDurationMs) = await VerifyWidgetVisibilityWithRetryAsync(verifyWidgetName, cancellationToken).ConfigureAwait(false);
                dict["verified"] = verified;
                dict["verifiedWidget"] = verifyWidgetName;
                dict["verificationDurationMs"] = verifyDurationMs;
            }

            if (!string.IsNullOrWhiteSpace(verifyStateExpr))
            {
                var (verifiedState, stateResult, stateVerifyDurationMs) = await VerifyStateExpressionAsync(verifyStateExpr, cancellationToken).ConfigureAwait(false);
                dict["verifiedState"] = verifiedState;
                dict["stateExpression"] = verifyStateExpr;
                dict["stateResult"] = stateResult;
                dict["stateVerificationDurationMs"] = stateVerifyDurationMs;
            }

            if (captureProof)
            {
                var visualProof = await CaptureVisualProofAsync(cancellationToken).ConfigureAwait(false);
                if (visualProof != null)
                {
                    dict["visualProof"] = visualProof;
                }
            }

            return ToolCallResult.Json(dict, isError: baseResult.IsError);
        }
        catch
        {
            return baseResult;
        }
    }

    private async Task<(bool verified, double latencyMs)> VerifyWidgetVisibilityWithRetryAsync(
        string widgetName,
        CancellationToken cancellationToken)
    {
        Stopwatch verifySw = Stopwatch.StartNew();
        int[] delaysMs = [0, 50, 150, 300];

        string safeWidget = EscapeLuaString(widgetName);
        string verifyLua = $"Script Echo('WIDGET_VERIFIED|' .. tostring(((UIManager.GetInterface('main') and UIManager.GetInterface('main'):GetWidget('{safeWidget}') and UIManager.GetInterface('main'):GetWidget('{safeWidget}'):IsVisible()) or (UIManager.GetInterface('game') and UIManager.GetInterface('game'):GetWidget('{safeWidget}') and UIManager.GetInterface('game'):GetWidget('{safeWidget}'):IsVisible())) or false))";

        foreach (int delay in delaysMs)
        {
            if (delay > 0)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            var bridge = _gameIpcBridge ?? _clientManageHandler?.IpcBridge;
            if (bridge != null && bridge.IsConnected)
            {
                try
                {
                    string resp = await bridge.ExecuteCommandAsync(verifyLua, 1000, cancellationToken).ConfigureAwait(false);
                    if (resp.Contains("WIDGET_VERIFIED|true", StringComparison.OrdinalIgnoreCase))
                    {
                        verifySw.Stop();
                        return (true, Math.Round(verifySw.Elapsed.TotalMilliseconds, 2));
                    }
                }
                catch { }
            }
        }

        verifySw.Stop();
        return (false, Math.Round(verifySw.Elapsed.TotalMilliseconds, 2));
    }

    private async Task<(bool verified, string rawResult, double latencyMs)> VerifyStateExpressionAsync(
        string stateExpr,
        CancellationToken cancellationToken)
    {
        Stopwatch verifySw = Stopwatch.StartNew();
        int[] delaysMs = [0, 50, 150, 300];

        string safeExpr = stateExpr.Replace("\"", "\\\"");
        string verifyLua = $"Script Echo('STATE_VERIFIED|' .. tostring({safeExpr}))";

        foreach (int delay in delaysMs)
        {
            if (delay > 0)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            var bridge = _gameIpcBridge ?? _clientManageHandler?.IpcBridge;
            if (bridge != null && bridge.IsConnected)
            {
                try
                {
                    string resp = await bridge.ExecuteCommandAsync(verifyLua, 1000, cancellationToken).ConfigureAwait(false);
                    if (resp.Contains("STATE_VERIFIED|true", StringComparison.OrdinalIgnoreCase))
                    {
                        verifySw.Stop();
                        return (true, "true", Math.Round(verifySw.Elapsed.TotalMilliseconds, 2));
                    }
                    if (resp.Contains("STATE_VERIFIED|", StringComparison.OrdinalIgnoreCase))
                    {
                        int idx = resp.IndexOf("STATE_VERIFIED|", StringComparison.OrdinalIgnoreCase);
                        string extracted = resp.Substring(idx + "STATE_VERIFIED|".Length).Trim();
                        int newlineIdx = extracted.IndexOfAny(['\r', '\n']);
                        if (newlineIdx >= 0) extracted = extracted.Substring(0, newlineIdx).Trim();
                        verifySw.Stop();
                        return (false, extracted, Math.Round(verifySw.Elapsed.TotalMilliseconds, 2));
                    }
                }
                catch { }
            }
        }

        verifySw.Stop();
        return (false, "nil", Math.Round(verifySw.Elapsed.TotalMilliseconds, 2));
    }

    private async Task<object?> CaptureVisualProofAsync(CancellationToken cancellationToken)
    {
        if (_windowCapturer == null) return null;
        try
        {
            var capRes = await _windowCapturer.CaptureAsync("hon_x64", "auto", saveArtifact: true, customOutputPath: null, stealFocus: false, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (capRes.Success)
            {
                return new
                {
                    success = true,
                    artifactPath = capRes.ArtifactPath,
                    captureMethodUsed = capRes.CaptureMethodUsed,
                    zeroWindowExposure = capRes.CaptureMethodUsed.Contains("vulkan", StringComparison.OrdinalIgnoreCase),
                    width = capRes.Width,
                    height = capRes.Height
                };
            }
        }
        catch (Exception ex)
        {
            _logger($"[GameLua] Visual proof capture encountered error: {ex.Message}");
        }
        return null;
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

    private ToolCallResult EnrichToolResult(
        ToolCallResult baseResult,
        string action,
        string formattedCommand,
        bool preSelfHealed,
        string? originalInput,
        string? selfHealHint,
        string? selfHealCategory = null)
    {
        if (baseResult.Content.Count == 0 || baseResult.Content[0] is not TextContentItem textItem)
        {
            return baseResult;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(textItem.Text);
            var root = doc.RootElement;

            string? status = root.TryGetProperty("status", out var sEl) ? sEl.GetString() : null;
            string? channel = root.TryGetProperty("channel", out var cEl) ? cEl.GetString() : null;
            string? responseStr = root.TryGetProperty("response", out var rEl) ? rEl.GetString() : null;
            double executionDurationMs = root.TryGetProperty("executionDurationMs", out var dEl) && dEl.TryGetDouble(out double d) ? d : 0.0;
            string? elementClicked = root.TryGetProperty("elementClicked", out var ecEl) ? ecEl.GetString() : null;

            // Parse Telemetry and Event emissions
            bool hasTelemetry = TryExtractTelemetry(responseStr, out JsonElement telemetry);
            bool hasEvent = TryExtractEvent(responseStr, out string? eventName, out JsonElement eventPayload);

            // Phase guard assertion check (e.g. ApplySnapshot called outside active match phase 3)
            bool hasStructuredError = TryDetectStructuredError(
                responseStr, eventName, eventPayload,
                out string? errorCode, out string? errorMessage, out int? currentPhase);

            if (hasStructuredError)
            {
                var errDict = new Dictionary<string, object?>
                {
                    ["status"] = "error",
                    ["action"] = action != "code" ? action : null,
                    ["target"] = "client",
                    ["channel"] = channel ?? "ipc",
                    ["command"] = formattedCommand,
                    ["errorCode"] = errorCode,
                    ["errorMessage"] = errorMessage ?? "Operation rejected by Nexus lifecycle guard.",
                    ["response"] = responseStr,
                    ["executionDurationMs"] = executionDurationMs,
                    ["timestamp"] = _timeProvider.GetUtcNow().ToString("o")
                };

                if (currentPhase.HasValue)
                {
                    errDict["currentPhase"] = currentPhase.Value;
                }
                if (hasEvent)
                {
                    errDict["event"] = new { name = eventName, payload = eventPayload };
                }

                return ToolCallResult.Json(errDict, isError: true);
            }

            // 2. TAO JSON-RPC Response extraction
            if (TryExtractTaoResponse(responseStr, out JsonElement taoData, out string? taoStatus, out string? taoError))
            {
                bool isTaoError = string.Equals(taoStatus, "error", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrEmpty(taoError);
                var taoDict = new Dictionary<string, object?>
                {
                    ["status"] = isTaoError ? "error" : (status ?? "success"),
                    ["action"] = action != "code" ? action : null,
                    ["target"] = "client",
                    ["channel"] = channel ?? "ipc",
                    ["command"] = formattedCommand,
                    ["taoStatus"] = taoStatus ?? (isTaoError ? "error" : "success"),
                    ["executionDurationMs"] = executionDurationMs,
                    ["timestamp"] = _timeProvider.GetUtcNow().ToString("o")
                };

                if (taoData.ValueKind != JsonValueKind.Undefined)
                {
                    taoDict["data"] = taoData;
                }
                if (!string.IsNullOrEmpty(taoError))
                {
                    taoDict["errorMessage"] = taoError;
                }
                if (!string.IsNullOrWhiteSpace(responseStr))
                {
                    taoDict["response"] = responseStr;
                }

                return ToolCallResult.Json(taoDict, isError: isTaoError);
            }

            if (!string.IsNullOrWhiteSpace(responseStr) && responseStr.Contains("[TAO_ERROR]", StringComparison.OrdinalIgnoreCase))
            {
                return ToolCallResult.Json(new
                {
                    status = "error",
                    action = action != "code" ? action : null,
                    target = "client",
                    channel = channel ?? "ipc",
                    command = formattedCommand,
                    errorMessage = responseStr.Trim(),
                    executionDurationMs,
                    timestamp = _timeProvider.GetUtcNow().ToString("o")
                }, isError: true);
            }

            // If action is collect_telemetry specifically
            if (action == "collect_telemetry")
            {
                if (hasTelemetry)
                {
                    return ToolCallResult.Json(new
                    {
                        status = "success",
                        action = "collect_telemetry",
                        target = "client",
                        channel = channel ?? "ipc",
                        telemetry = telemetry,
                        command = formattedCommand,
                        executionDurationMs = executionDurationMs,
                        timestamp = _timeProvider.GetUtcNow().ToString("o")
                    });
                }

                if (baseResult.IsError) return baseResult;

                return ToolCallResult.Json(new
                {
                    status = "success",
                    action = "collect_telemetry",
                    target = "client",
                    channel = channel ?? "ipc",
                    command = formattedCommand,
                    response = responseStr,
                    warning = "Telemetry command dispatched but no [NEXUS_TELEMETRY] emission was found in response.",
                    executionDurationMs = executionDurationMs,
                    timestamp = _timeProvider.GetUtcNow().ToString("o")
                });
            }

            // If action is GC pacing / memory management
            if (action is "gc_pace" or "pace_gc" or "gc_step")
            {
                double mBefore = 0.0, mAfter = 0.0;
                int stepApplied = 50;
                if (!string.IsNullOrEmpty(formattedCommand))
                {
                    int stepIdx = formattedCommand.IndexOf("collectgarbage('step', ", StringComparison.OrdinalIgnoreCase);
                    if (stepIdx >= 0)
                    {
                        int startNum = stepIdx + "collectgarbage('step', ".Length;
                        int endNum = formattedCommand.IndexOf(')', startNum);
                        if (endNum > startNum && int.TryParse(formattedCommand.Substring(startNum, endNum - startNum).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int cmdStep))
                        {
                            stepApplied = cmdStep;
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(responseStr) && responseStr.Contains("LUA_GC_PACE|", StringComparison.OrdinalIgnoreCase))
                {
                    int idx = responseStr.IndexOf("LUA_GC_PACE|", StringComparison.OrdinalIgnoreCase);
                    string afterTag = responseStr.Substring(idx + "LUA_GC_PACE|".Length);
                    int endOfLine = afterTag.IndexOfAny(['\r', '\n']);
                    string payload = (endOfLine >= 0 ? afterTag.Substring(0, endOfLine) : afterTag).Trim();
                    string[] parts = payload.Split('|');
                    if (parts.Length >= 2)
                    {
                        double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out mBefore);
                        double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out mAfter);
                    }
                    if (parts.Length >= 3 && int.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedStep))
                    {
                        stepApplied = parsedStep;
                    }
                }

                double reclaimed = Math.Max(0.0, mBefore - mAfter);
                string watermark = mAfter > 131072 ? "critical" : (mAfter > 65536 ? "elevated" : "nominal");

                return ToolCallResult.Json(new
                {
                    status = "success",
                    action = "gc_pace",
                    target = "client",
                    channel = channel ?? "ipc",
                    memoryBeforeKb = mBefore,
                    memoryAfterKb = mAfter,
                    memoryAfterMb = Math.Round(mAfter / 1024.0, 2),
                    reclaimedKb = Math.Round(reclaimed, 2),
                    stepSize = stepApplied,
                    watermark = watermark,
                    executionDurationMs = executionDurationMs,
                    timestamp = _timeProvider.GetUtcNow().ToString("o")
                });
            }

            if (action is "gc_stats" or "gc_count" or "gc_memory")
            {
                double currentKb = 0.0;
                if (!string.IsNullOrWhiteSpace(responseStr) && responseStr.Contains("LUA_GC_STATS|", StringComparison.OrdinalIgnoreCase))
                {
                    int idx = responseStr.IndexOf("LUA_GC_STATS|", StringComparison.OrdinalIgnoreCase);
                    string afterTag = responseStr.Substring(idx + "LUA_GC_STATS|".Length);
                    int endOfLine = afterTag.IndexOfAny(['\r', '\n']);
                    string payload = (endOfLine >= 0 ? afterTag.Substring(0, endOfLine) : afterTag).Trim();
                    string firstToken = payload.Split(['|', ' ', '\t'])[0].Trim();
                    double.TryParse(firstToken, NumberStyles.Float, CultureInfo.InvariantCulture, out currentKb);
                }

                string watermark = currentKb > 131072 ? "critical" : (currentKb > 65536 ? "elevated" : "nominal");

                return ToolCallResult.Json(new
                {
                    status = "success",
                    action = "gc_stats",
                    target = "client",
                    channel = channel ?? "ipc",
                    memoryKb = currentKb,
                    memoryMb = Math.Round(currentKb / 1024.0, 2),
                    watermark = watermark,
                    executionDurationMs = executionDurationMs,
                    timestamp = _timeProvider.GetUtcNow().ToString("o")
                });
            }

            if (action is "gc_collect" or "collect_garbage")
            {
                double mBefore = 0.0, mAfter = 0.0;
                if (!string.IsNullOrWhiteSpace(responseStr) && responseStr.Contains("LUA_GC_COLLECT|", StringComparison.OrdinalIgnoreCase))
                {
                    int idx = responseStr.IndexOf("LUA_GC_COLLECT|", StringComparison.OrdinalIgnoreCase);
                    string afterTag = responseStr.Substring(idx + "LUA_GC_COLLECT|".Length);
                    int endOfLine = afterTag.IndexOfAny(['\r', '\n']);
                    string payload = (endOfLine >= 0 ? afterTag.Substring(0, endOfLine) : afterTag).Trim();
                    string[] parts = payload.Split('|');
                    if (parts.Length >= 2)
                    {
                        double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out mBefore);
                        double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out mAfter);
                    }
                }

                double reclaimed = Math.Max(0.0, mBefore - mAfter);

                return ToolCallResult.Json(new
                {
                    status = "success",
                    action = "gc_collect",
                    target = "client",
                    channel = channel ?? "ipc",
                    memoryBeforeKb = mBefore,
                    memoryAfterKb = mAfter,
                    memoryAfterMb = Math.Round(mAfter / 1024.0, 2),
                    reclaimedKb = Math.Round(reclaimed, 2),
                    executionDurationMs = executionDurationMs,
                    timestamp = _timeProvider.GetUtcNow().ToString("o")
                });
            }

            // For other actions, if telemetry or events were detected, enrich result
            if (hasTelemetry || hasEvent || preSelfHealed || action != "code" || !string.IsNullOrWhiteSpace(elementClicked))
            {
                var dict = new Dictionary<string, object?>
                {
                    ["status"] = status ?? (baseResult.IsError ? "error" : "success"),
                    ["action"] = action != "code" ? action : null,
                    ["target"] = "client",
                    ["channel"] = channel ?? "ipc",
                    ["command"] = formattedCommand,
                    ["response"] = responseStr,
                    ["executionDurationMs"] = executionDurationMs,
                    ["timestamp"] = _timeProvider.GetUtcNow().ToString("o")
                };

                if (!string.IsNullOrWhiteSpace(elementClicked)) dict["elementClicked"] = elementClicked;
                if (hasTelemetry) dict["telemetry"] = telemetry;
                if (hasEvent) dict["event"] = new { name = eventName, payload = eventPayload };
                if (preSelfHealed)
                {
                    dict["selfHealed"] = true;
                    dict["originalScript"] = originalInput;
                    dict["remediationHint"] = selfHealHint;
                    if (!string.IsNullOrWhiteSpace(selfHealCategory))
                    {
                        dict["remediationCategory"] = selfHealCategory;
                    }
                }

                return ToolCallResult.Json(dict, isError: baseResult.IsError);
            }
        }
        catch
        {
            // If response parsing fails, return baseResult safely
        }

        return baseResult;
    }

    private async Task<ToolCallResult> DispatchTaoToClientAsync(
        string taoAction,
        object? payload,
        string toolActionName,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        Stopwatch sw = Stopwatch.StartNew();
        TaoResponse<JsonElement> taoRes = await _taoClient.DispatchAsync(taoAction, payload, timeoutMs, cancellationToken).ConfigureAwait(false);
        sw.Stop();

        string channel = (_taoClient as TaoClient)?.IsMailboxConnected == true ? "mailbox" : "ipc";
        string statusStr = taoRes.Success ? "success" : "error";
        double durationMs = Math.Round(taoRes.ElapsedMs > 0 ? taoRes.ElapsedMs : sw.Elapsed.TotalMilliseconds, 2);

        var resDict = new Dictionary<string, object?>
        {
            ["status"] = statusStr,
            ["action"] = toolActionName != "code" ? toolActionName : null,
            ["target"] = "client",
            ["channel"] = channel,
            ["command"] = $"TAO.Dispatch('{taoAction}')",
            ["taoStatus"] = statusStr,
            ["executionDurationMs"] = durationMs,
            ["timestamp"] = _timeProvider.GetUtcNow().ToString("o")
        };

        if (taoRes.Data.ValueKind != JsonValueKind.Undefined)
        {
            resDict["data"] = taoRes.Data;
            if (taoRes.Data.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty prop in taoRes.Data.EnumerateObject())
                {
                    if (!resDict.ContainsKey(prop.Name))
                    {
                        resDict[prop.Name] = prop.Value;
                    }
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(taoRes.Error))
        {
            resDict["errorMessage"] = taoRes.Error;
            resDict["error"] = taoRes.Error;
        }

        return ToolCallResult.Json(resDict, isError: !taoRes.Success);
    }
}
