// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;

namespace Kongor.Cli.Mcp.Handlers;

public sealed partial class GameClientManageHandler
{
    /// <summary>
    /// Executes an in-engine command against the running client via authentic dual-channel routing:
    /// 1. In-game Win32 console actuation (Ctrl+F8 -> clipboard paste -> Enter -> Ctrl+F8) targeting MainWindowHandle.
    /// 2. Named Pipe IPC fallback if bridge is actively connected.
    /// Does NOT pipe to stdin for GUI client because hon_x64.exe in GUI mode only processes console commands via in-game console or IPC.
    /// <summary>
    /// Executes a command in the running client via IPC (primary) or in-game console actuation (secondary fallback).
    /// </summary>
    private static bool IsShutdownClientCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        string trimmed = command.Trim();
        return trimmed.Equals("Quit", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("Exit", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains("Cmd('Quit')", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains("Cmd(\"Quit\")", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains("KongorQuitClient", StringComparison.OrdinalIgnoreCase);
    }

    public Task<ToolCallResult> ExecuteClientCommandAsync(string command, CancellationToken cancellationToken)
        => ExecuteClientCommandAsync(command, 3000, autoLaunch: false, allowKeystrokeFallback: true, null, cancellationToken);

    public Task<ToolCallResult> ExecuteClientCommandAsync(string command, int timeoutMs, CancellationToken cancellationToken)
        => ExecuteClientCommandAsync(command, timeoutMs, autoLaunch: false, allowKeystrokeFallback: true, null, cancellationToken);

    public Task<ToolCallResult> ExecuteClientCommandAsync(string command, int timeoutMs, bool autoLaunch, CancellationToken cancellationToken)
        => ExecuteClientCommandAsync(command, timeoutMs, autoLaunch, allowKeystrokeFallback: true, null, cancellationToken);

    public Task<ToolCallResult> ExecuteClientCommandAsync(string command, int timeoutMs, bool autoLaunch, bool allowKeystrokeFallback, CancellationToken cancellationToken)
        => ExecuteClientCommandAsync(command, timeoutMs, autoLaunch, allowKeystrokeFallback, null, cancellationToken);

    public async Task<ToolCallResult> ExecuteClientCommandAsync(
        string command,
        int timeoutMs = 3000,
        bool autoLaunch = false,
        bool allowKeystrokeFallback = true,
        int? processId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return ToolCallResult.Error("Missing required parameter 'command'.");
        }

        string trimmed = command.Trim();
        string redactedCommand = ClientCredentialManager.RedactSingleText(trimmed);
        Stopwatch sw = Stopwatch.StartNew();

        string? callerAgentId = McpAgentContext.CurrentAgentId;

        // Auto-resolve processId from ambient agent context if not explicitly provided
        if (!processId.HasValue && !string.IsNullOrWhiteSpace(callerAgentId))
        {
            var agentClient = _processRegistry.GetClientByAgentId(callerAgentId);
            var allClients = (_processRegistry.GetAllClients() ?? _processRegistry.GetAll()) ?? Array.Empty<ClientInstance>();
            if (agentClient != null)
            {
                processId = agentClient.ProcessId;
            }
            else if (allClients.Count > 0)
            {
                return ToolCallResult.Error($"No game client found for agent '{callerAgentId}'. Active clients belong to other agents: [{string.Join(", ", allClients.Select(c => c.AgentId ?? "unnamed"))}]. Please launch your own client first via kongor_game_client_manage action:launch.");
            }
            else
            {
                var adopted = _processRegistry.AutoAdoptRunningClient(callerAgentId);
                if (adopted != null)
                {
                    processId = adopted.ProcessId;
                }
            }
        }

        // Ambient fallback: if processId is still unassigned, auto-adopt any running client
        if (!processId.HasValue && (!GameServerManageHandler.IsTestExecution || !ReferenceEquals(_processRegistry, ClientProcessRegistry.Default)))
        {
            var ambient = _processRegistry.AutoAdoptRunningClient(callerAgentId);
            if (ambient != null)
            {
                processId = ambient.ProcessId;
            }
        }

        // Validate agent access & anti-tamper permissions
        if (processId.HasValue)
        {
            // When targeting a specific process, NEVER allow falling back to keystrokes on the foreground window!
            allowKeystrokeFallback = false;
            if (!_processRegistry.ValidateAgentAccess(callerAgentId, processId.Value, out string? accessErr))
            {
                return ToolCallResult.Error(accessErr!);
            }
        }

        Process? clientProc = null;
        if (processId.HasValue)
        {
            clientProc = GetRunningClientProcess(processId.Value);
            if (clientProc != null && IsWindowOffscreen(clientProc.MainWindowHandle))
            {
                allowKeystrokeFallback = false;
            }
        }

        string targetDesc = processId.HasValue ? $"client:{processId.Value}" : "client";

        // Pre-Execution Self-Healing & Sandbox Guard (MCP-001, MCP-002, MCP-005, MCP-060)
        if (trimmed.StartsWith("Script ", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Script\t", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Script\n", StringComparison.OrdinalIgnoreCase))
        {
            string scriptBody = trimmed.Substring(6).TrimStart();
            var preAnalysis = ClientCommandRemediationService.AnalyzeCommandError(scriptBody, "", "lua", targetDesc);
            if (preAnalysis.ErrorCategory == "LuaSandboxRestriction")
            {
                var errPayload = new
                {
                    status = "error",
                    target = targetDesc,
                    channel = "none",
                    command = redactedCommand,
                    errorMessage = "K2 engine Lua sandbox violation: standard 'io', 'os', and 'debug' libraries are stripped in-engine. To inspect or return engine state, use 'Echo(...)' or 'return <value>' via 'kongor_game_lua'. For host filesystem reads/writes, use workspace tools or PowerShell.",
                    category = "LuaSandboxRestriction",
                    remediationHint = preAnalysis.RemediationHint,
                    timestamp = _timeProvider.GetUtcNow().ToString("o")
                };
                return ToolCallResult.Error(JsonRpcSerializer.SerializeIndented(errPayload));
            }

            if (preAnalysis.CanAutoRetry && !string.IsNullOrWhiteSpace(preAnalysis.ImprovedCommand) && !preAnalysis.ImprovedCommand.Equals(scriptBody, StringComparison.OrdinalIgnoreCase))
            {
                string redactedScriptBody = ClientCredentialManager.RedactSingleText(scriptBody);
                string redactedImprovedScript = ClientCredentialManager.RedactSingleText(preAnalysis.ImprovedCommand);
                _logger($"[ExecuteClientCommand] Auto-remediated Script command before dispatch: '{redactedScriptBody}' -> '{redactedImprovedScript}'");
                trimmed = "Script " + preAnalysis.ImprovedCommand;
                redactedCommand = ClientCredentialManager.RedactSingleText(trimmed);
            }
        }

        // Try dedicated per-instance Named Pipe if targeted
        ClientInstance? targetInstance = processId.HasValue ? _processRegistry.Get(processId.Value) : null;
        string? targetPipe = !string.IsNullOrEmpty(targetInstance?.NativePipeName)
            ? targetInstance.NativePipeName
            : targetInstance?.PipeName;
        if (targetInstance != null && !string.IsNullOrEmpty(targetPipe) && !targetPipe.Equals("willowmaker-ipc", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                await using var instanceBridge = new GameIpcBridge(targetPipe);
                await instanceBridge.ConnectAsync(1000, cancellationToken).ConfigureAwait(false);
                string output = await instanceBridge.ExecuteCommandAsync(trimmed, timeoutMs, cancellationToken).ConfigureAwait(false);
                sw.Stop();
                string redactedOutput = ClientCredentialManager.RedactSingleText(output);

                bool isEngineError = output.Contains("Unknown command", StringComparison.OrdinalIgnoreCase) ||
                                     output.Contains("attempt to index", StringComparison.OrdinalIgnoreCase) ||
                                     output.Contains("syntax error", StringComparison.OrdinalIgnoreCase) ||
                                     output.Contains("is read only", StringComparison.OrdinalIgnoreCase);

                if (isEngineError)
                {
                    var record = _remediationService.RecordAndRemediate(redactedCommand, redactedOutput, "ipc", $"client:{targetInstance.ProcessId}");
                    return ToolCallResult.Json(new
                    {
                        status = "error",
                        target = $"client:{targetInstance.ProcessId}",
                        channel = "ipc",
                        pipeName = targetPipe,
                        command = redactedCommand,
                        response = redactedOutput,
                        remediationCategory = record.Remediation.ErrorCategory,
                        remediationHint = record.Remediation.RemediationHint,
                        improvedCommand = record.Remediation.ImprovedCommand != null
                            ? ClientCredentialManager.RedactSingleText(record.Remediation.ImprovedCommand)
                            : null,
                        executionDurationMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                        timestamp = _timeProvider.GetUtcNow().ToString("o")
                    }, isError: true);
                }

                return ToolCallResult.Json(new
                {
                    status = "success",
                    target = $"client:{targetInstance.ProcessId}",
                    channel = "ipc",
                    pipeName = targetInstance.PipeName,
                    command = redactedCommand,
                    response = redactedOutput,
                    executionDurationMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                    timestamp = _timeProvider.GetUtcNow().ToString("o")
                });
            }
            catch (Exception ex)
            {
                _logger($"[WARN] Dedicated IPC for client PID {targetInstance.ProcessId} ({targetInstance.PipeName}) failed: {ex.Message}. Falling back to default channel.");
            }
        }

        bool isTestMock = _gameIpcBridge != null && _gameIpcBridge.GetType().Assembly.GetName().Name != typeof(GameIpcBridge).Assembly.GetName().Name;

        bool connectFailed = false;
        // Proactively attempt Named Pipe IPC connection if disconnected
        if (_gameIpcBridge != null && !_gameIpcBridge.IsConnected && (!GameServerManageHandler.IsTestExecution || isTestMock))
        {
            try
            {
                await _gameIpcBridge.ConnectAsync(1000, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                connectFailed = true;
                _logger($"[ExecuteClientCommand] Proactive IPC connection attempt failed: {ex.Message}");
            }
        }

        bool hasActiveIpc = _gameIpcBridge != null && (_gameIpcBridge.IsConnected || (isTestMock && !connectFailed));

        // If client is offline and autoLaunch is explicitly enabled (and not a Quit/Exit command), launch headless offscreen
        if (!hasActiveIpc && autoLaunch && !IsShutdownClientCommand(command) && !GameServerManageHandler.IsTestExecution)
        {
            clientProc ??= GetRunningClientProcess();
            if (clientProc == null)
            {
                _logger("[ExecuteClientCommand] Client is offline and autoLaunch is enabled. Launching headless offscreen game client...");
                var launchRes = await LaunchClientAsync(
                    fullscreen: false,
                    windowMode: "headless",
                    stealFocus: false,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                int waited = 0;
                while (waited < 15000)
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                    waited += 500;
                    if (_gameIpcBridge != null)
                    {
                        try
                        {
                            await _gameIpcBridge.ConnectAsync(500, cancellationToken).ConfigureAwait(false);
                            if (_gameIpcBridge.IsConnected)
                            {
                                hasActiveIpc = true;
                                _logger($"[ExecuteClientCommand] Auto-launched game client connected to IPC successfully after {waited}ms.");
                                break;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger($"[ExecuteClientCommand] Waiting for auto-launched client IPC ({waited}ms): {ex.Message}");
                        }
                    }
                }
            }
        }

        // 1. Channel 1: Try Named Pipe or Mailbox IPC if actively connected
        if (_gameIpcBridge != null && hasActiveIpc)
        {
            try
            {
                string output = await _gameIpcBridge.ExecuteCommandAsync(trimmed, timeoutMs, cancellationToken).ConfigureAwait(false);
                sw.Stop();
                string redactedOutput = ClientCredentialManager.RedactSingleText(output);

                bool isEngineError = output.Contains("Unknown command", StringComparison.OrdinalIgnoreCase) ||
                                     output.Contains("attempt to index", StringComparison.OrdinalIgnoreCase) ||
                                     output.Contains("syntax error", StringComparison.OrdinalIgnoreCase) ||
                                     output.Contains("is read only", StringComparison.OrdinalIgnoreCase);

                if (isEngineError)
                {
                    var record = _remediationService.RecordAndRemediate(redactedCommand, redactedOutput, "ipc", targetDesc);
                    return ToolCallResult.Json(new
                    {
                        status = "error",
                        target = targetDesc,
                        channel = "ipc",
                        command = redactedCommand,
                        response = redactedOutput,
                        remediationCategory = record.Remediation.ErrorCategory,
                        remediationHint = record.Remediation.RemediationHint,
                        improvedCommand = record.Remediation.ImprovedCommand != null
                            ? ClientCredentialManager.RedactSingleText(record.Remediation.ImprovedCommand)
                            : null,
                        executionDurationMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                        timestamp = _timeProvider.GetUtcNow().ToString("o")
                    }, isError: true);
                }

                return ToolCallResult.Json(new
                {
                    status = "success",
                    target = targetDesc,
                    channel = "ipc",
                    command = redactedCommand,
                    response = redactedOutput,
                    executionDurationMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                    timestamp = _timeProvider.GetUtcNow().ToString("o")
                });
            }
            catch (Exception ex)
            {
                string redactedEx = ClientCredentialManager.RedactSingleText(ex.Message);
                _logger($"[WARN] IPC execution error: {redactedEx}. Falling back to in-game console actuation.");
                _remediationService.RecordAndRemediate(redactedCommand, redactedEx, "ipc", targetDesc);
            }
        }

        // 2. Channel 2: Try native in-game Win32 console actuation targeting MainWindowHandle (if keystroke fallback permitted)
        if (!allowKeystrokeFallback)
        {
            sw.Stop();
            string errTxt = "IPC channel unavailable and keystroke fallback (Ctrl+F8) is suppressed to prevent desktop focus theft.";
            _logger($"[ExecuteClientCommand] {errTxt}");
            var errRecord = _remediationService.RecordAndRemediate(redactedCommand, errTxt, "none", targetDesc);
            return ToolCallResult.Json(new
            {
                status = "error",
                target = targetDesc,
                channel = "none",
                command = redactedCommand,
                errorMessage = errTxt,
                remediationCategory = "KeystrokeFallbackSuppressed",
                remediationHint = "Ensure game client is active with Nexus Mailbox IPC enabled in userdataKONGOR/extensions/nexus_ipc.",
                executionDurationMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            }, isError: true);
        }

        bool isUiMock = _uiControlService != null && _uiControlService.GetType() != typeof(UiControlService);
        if ((!GameServerManageHandler.IsTestExecution || isUiMock) && _uiControlService != null)
        {
            int? actPid = processId ?? clientProc?.Id ?? GetRunningClientProcess()?.Id;
            string consoleTarget = actPid.HasValue && actPid.Value > 0 ? $"pid:{actPid.Value}" : "hon_x64";

            _logger($"Dispatching '{redactedCommand}' via in-game console actuation targeting '{consoleTarget}'...");
            UiConsoleCommandResult result = await _uiControlService.ExecuteInGameConsoleCommandAsync(consoleTarget, trimmed, cancellationToken).ConfigureAwait(false);
            if (result.Success)
            {
                sw.Stop();
                return ToolCallResult.Json(new
                {
                    status = "success",
                    target = targetDesc,
                    channel = "in_game_console",
                    command = redactedCommand,
                    windowTitle = result.WindowTitle,
                    hwnd = result.Hwnd,
                    response = $"Executed '{redactedCommand}' via native in-game console.",
                    executionDurationMs = result.ExecutionDurationMs,
                    timestamp = _timeProvider.GetUtcNow().ToString("o")
                });
            }

            // In-game console failed. Analyze raw command for wire retry while recording redacted telemetry
            string consoleErr = ClientCredentialManager.RedactSingleText(result.ErrorMessage ?? "Console command failed");
            var rawRetryAnalysis = ClientCommandRemediationService.AnalyzeCommandError(trimmed, result.ErrorMessage ?? "Console command failed", "in_game_console", targetDesc);
            var record = _remediationService.RecordAndRemediate(redactedCommand, consoleErr, "in_game_console", targetDesc);

            // Check for automatic self-healing retry if an improved command is available
            if (rawRetryAnalysis.CanAutoRetry &&
                !string.IsNullOrWhiteSpace(rawRetryAnalysis.ImprovedCommand) &&
                !rawRetryAnalysis.ImprovedCommand.Equals(trimmed, StringComparison.OrdinalIgnoreCase))
            {
                string rawImprovedCommand = rawRetryAnalysis.ImprovedCommand;
                string redactedImprovedCommand = ClientCredentialManager.RedactSingleText(rawImprovedCommand);
                _logger($"[SelfHealing] Attempting auto-retry with improved command: '{redactedImprovedCommand}'...");
                var retryResult = await _uiControlService.ExecuteInGameConsoleCommandAsync(consoleTarget, rawImprovedCommand, cancellationToken).ConfigureAwait(false);
                if (retryResult.Success)
                {
                    sw.Stop();
                    _remediationService.MarkSelfHealed(record.Id);
                    _logger($"[SelfHealing] Successfully auto-healed command: '{redactedCommand}' -> '{redactedImprovedCommand}'");
                    return ToolCallResult.Json(new
                    {
                        status = "success",
                        target = targetDesc,
                        channel = "in_game_console",
                        selfHealed = true,
                        originalCommand = redactedCommand,
                        command = redactedImprovedCommand,
                        remediationCategory = record.Remediation.ErrorCategory,
                        remediationHint = record.Remediation.RemediationHint,
                        response = $"Executed '{redactedImprovedCommand}' via native in-game console (Self-Healed).",
                        executionDurationMs = retryResult.ExecutionDurationMs,
                        timestamp = _timeProvider.GetUtcNow().ToString("o")
                    });
                }
            }

            // 3. Channel 3: Tertiary Physical Mouse Click Last Resort Fallback
            string? widgetToClick = null;
            if (trimmed.StartsWith("Call ", StringComparison.OrdinalIgnoreCase))
            {
                string[] parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2) widgetToClick = parts[1];
            }
            else if (trimmed.Contains("store_container", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("store_button", StringComparison.OrdinalIgnoreCase))
            {
                widgetToClick = "store_button";
            }
            else if (trimmed.Contains("sysbar_quickStart", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("practice_button", StringComparison.OrdinalIgnoreCase))
            {
                widgetToClick = "sysbar_quickStart";
            }
            else if (trimmed.Contains("sysbar_options", StringComparison.OrdinalIgnoreCase))
            {
                widgetToClick = "sysbar_options";
            }

            if (widgetToClick != null)
            {
                _logger($"[ExecuteClientCommand] Programmatic console command failed. Falling back to physical mouse click on '{widgetToClick}' as last resort...");
                var clickRes = await _uiControlService.ClickAsync("hon_x64", elementName: widgetToClick, ct: cancellationToken).ConfigureAwait(false);
                if (clickRes.Success)
                {
                    sw.Stop();
                    return ToolCallResult.Json(new
                    {
                        status = "success",
                        target = targetDesc,
                        channel = "physical_click",
                        command = redactedCommand,
                        elementClicked = clickRes.ElementClicked,
                        response = $"Clicked widget '{clickRes.ElementClicked}' via physical mouse click last resort fallback.",
                        executionDurationMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                        timestamp = _timeProvider.GetUtcNow().ToString("o")
                    });
                }
            }

            sw.Stop();
            return ToolCallResult.Json(new
            {
                status = "error",
                target = targetDesc,
                channel = "in_game_console",
                command = redactedCommand,
                errorMessage = result.ErrorMessage != null ? ClientCredentialManager.RedactSingleText(result.ErrorMessage) : null,
                remediationCategory = record.Remediation.ErrorCategory,
                remediationHint = record.Remediation.RemediationHint,
                improvedCommand = record.Remediation.ImprovedCommand != null
                    ? ClientCredentialManager.RedactSingleText(record.Remediation.ImprovedCommand)
                    : null,
                executionDurationMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            }, isError: true);
        }

        sw.Stop();
        string offlineErr = "Game client is not currently running or has no active console window. Launch client first with 'kongor_game_client_manage action:launch'.";
        var offlineRecord = _remediationService.RecordAndRemediate(redactedCommand, offlineErr, "none", targetDesc);

        return ToolCallResult.Json(new
        {
            status = "error",
            target = targetDesc,
            channel = "none",
            command = redactedCommand,
            errorMessage = offlineErr,
            remediationCategory = offlineRecord.Remediation.ErrorCategory,
            remediationHint = offlineRecord.Remediation.RemediationHint,
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        }, isError: true);
    }

    /// <summary>
    /// Builds canonical launch arguments matching WILLOWMAKER and K2 specifications.
    /// Injects full VFS mod mounts, DXVK Vulkan settings, low-latency audio, and Named Pipe IPC.
    /// </summary>
    public static IReadOnlyList<string> BuildClientLaunchArguments(
        string gameDir,
        string mode,
        string? connectAddress,
        string masterServer,
        string resolution,
        bool fullscreen,
        int maxFps,
        string? customNickname = null,
        string windowMode = "foreground")
        => new ClientModMountBuilder().BuildClientLaunchArguments(
            gameDir, mode, connectAddress, masterServer, resolution, fullscreen, maxFps, customNickname, windowMode);

    public static string ResolveDefaultNickname(string? clientDirectory = null)
        => new ClientModMountBuilder().ResolveDefaultNickname(clientDirectory);

    public static void EnsureClientDefaultNameConfigured(string gameDirectory, string nickname)
        => new ClientModMountBuilder().EnsureClientDefaultNameConfigured(gameDirectory, nickname);

    public static IReadOnlyList<string> GetCanonicalMountHierarchy(string? clientDirectory = null)
        => new ClientModMountBuilder().GetCanonicalMountHierarchy(clientDirectory);

    /// <summary>
    /// Explicitly resolves games/client/hon_x64.exe (NEVER WILLOWMAKER).
    /// </summary>
    public static bool TryResolveClientExecutable(string? customPath, string? workingDir, [NotNullWhen(true)] out FileInfo? executable)
        => new ClientProcessSupervisor().TryResolveClientExecutable(customPath, workingDir, out executable);

    public static bool IsWillowmakerExecutable(string filename)
        => ClientProcessSupervisor.IsWillowmakerExecutable(filename);

    private static bool IsClientProcess(Process process)
        => new ClientProcessSupervisor().IsClientProcess(process);

    private static Process? FindRunningClientProcess()
        => new ClientProcessSupervisor().FindRunningClientProcess();

    private static string? TryGetProcessFileName(Process process)
    {
        try { return process.MainModule?.FileName; }
        catch { return null; }
    }

    public static string? TryGetProcessCommandLine(int processId)
        => new ClientProcessSupervisor().TryGetProcessCommandLine(processId);

    private static bool TryParseResolution(string res, out int width, out int height)
        => new ClientModMountBuilder().TryParseResolution(res, out width, out height);

    private static string FindRepoRoot(string startingDir)
        => new ClientModMountBuilder().FindRepoRoot(startingDir);

}
