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
    public ToolCallResult StopClient(int? requestedProcessId = null, int? requestedSlot = null)
    {
        List<int> killedIds = [];

        // If slot is specified but no processId, lookup process from slot
        if (requestedSlot.HasValue && !requestedProcessId.HasValue)
        {
            var match = _processRegistry.GetBySlot(requestedSlot.Value);
            if (match != null)
            {
                requestedProcessId = match.ProcessId;
            }
        }

        // Case A: Specific process targeting
        if (requestedProcessId.HasValue)
        {
            int targetPid = requestedProcessId.Value;
            _logger($"Stop requested for specific client PID {targetPid}...");

            try
            {
                var targetProc = Process.GetProcessById(targetPid);
                if (!targetProc.HasExited)
                {
                    // Protection guard: If it's the user's active game (unmanaged and onscreen), refuse to kill
                    if (!_processRegistry.IsManagedClient(targetPid) && _processSupervisor.IsUserClient(targetProc))
                    {
                        _logger($"[SECURITY GUARD] Refused to terminate PID {targetPid}: detected as user's active gaming session.");
                        return ToolCallResult.Error($"PID {targetPid} is the user's active game client. Protected from termination.");
                    }

                    _processSupervisor.StopOffscreenWindowWatchdog(targetPid);
                    if (GameServerManageHandler.IsTestExecution)
                    {
                        _logger($"[TEST MODE] Suppressing Process.Kill for targeted client PID {targetPid}.");
                        killedIds.Add(targetPid);
                    }
                    else
                    {
                        targetProc.Kill(true);
                        targetProc.WaitForExit(2000);
                        killedIds.Add(targetPid);
                        _logger($"Terminated targeted client process PID {targetPid}.");
                    }
                }
            }
            catch (ArgumentException)
            {
                _logger($"Process PID {targetPid} is already not running.");
            }
            catch (Exception ex)
            {
                _logger($"[WARN] Error terminating PID {targetPid}: {ex.Message}");
            }

            _slotManager.ReleaseByProcessId(targetPid);
            _processRegistry.Unregister(targetPid);

            lock (_lock)
            {
                if (_trackedClientProcess?.Id == targetPid)
                {
                    try
                    {
                        _clientStdin?.Dispose();
                    }
                    catch (Exception ex)
                    {
                        _logger($"[WARN] Error disposing client stdin for PID {targetPid}: {ex.GetType().Name}: {ex.Message}");
                    }
                    _clientStdin = null;
                    _trackedClientProcess = null;
                }
            }

            return ToolCallResult.Json(new
            {
                status = "success",
                action = "stop",
                targetPid,
                running = false,
                terminatedProcessesCount = killedIds.Count,
                terminatedProcessIds = killedIds,
                message = killedIds.Count > 0 ? $"Successfully stopped game client PID {targetPid}." : $"Client PID {targetPid} was not running.",
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            });
        }

        // Case B: Stop all managed test clients (Never touch the user's game)
        _logger("Stop requested for all managed game clients...");
        IReadOnlyList<ClientInstance> managedClients = _processRegistry.GetAll();
        foreach (ClientInstance inst in managedClients)
        {
            if (string.Equals(inst.AgentId, "ambient", StringComparison.OrdinalIgnoreCase))
            {
                _logger($"[SECURITY GUARD] Preserving ambient client PID {inst.ProcessId}.");
                continue;
            }

            try
            {
                Process p = Process.GetProcessById(inst.ProcessId);
                if (_processSupervisor.IsUserClient(p))
                {
                    _logger($"[SECURITY GUARD] Preserving user client PID {inst.ProcessId}.");
                    continue;
                }

                _processSupervisor.StopOffscreenWindowWatchdog(inst.ProcessId);
                if (GameServerManageHandler.IsTestExecution)
                {
                    _logger($"[TEST MODE] Suppressing Process.Kill for managed client PID {inst.ProcessId}.");
                    killedIds.Add(inst.ProcessId);
                }
                else if (!p.HasExited)
                {
                    p.Kill(true);
                    p.WaitForExit(1500);
                    killedIds.Add(inst.ProcessId);
                    _logger($"Terminated managed client PID {inst.ProcessId} (Slot {inst.SlotIndex}).");
                }
            }
            catch (ArgumentException ex)
            {
                _logger($"[StopClient] Managed client PID {inst.ProcessId} already exited: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger($"[WARN] Failed to terminate managed client PID {inst.ProcessId}: {ex.GetType().Name}: {ex.Message}");
            }
            _slotManager.ReleaseSlot(inst.SlotIndex);
            _processRegistry.Unregister(inst.ProcessId);
        }

        // Also terminate tracked process if not already killed and not user game
        lock (_lock)
        {
            if (_trackedClientProcess != null)
            {
                try
                {
                    if (!_trackedClientProcess.HasExited && !killedIds.Contains(_trackedClientProcess.Id))
                    {
                        if (GameServerManageHandler.IsTestExecution)
                        {
                            _logger($"[TEST MODE] Suppressing Process.Kill for tracked client PID {_trackedClientProcess.Id}.");
                            killedIds.Add(_trackedClientProcess.Id);
                        }
                        else if (!_processSupervisor.IsUserClient(_trackedClientProcess))
                        {
                            int pid = _trackedClientProcess.Id;
                            _processSupervisor.StopOffscreenWindowWatchdog(pid);
                            _trackedClientProcess.Kill(true);
                            _trackedClientProcess.WaitForExit(2000);
                            killedIds.Add(pid);
                            _logger($"Terminated tracked game client process PID {pid}.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger($"[WARN] Error terminating tracked client process: {ex.Message}");
                }
                finally
                {
                    try
                    {
                        _clientStdin?.Dispose();
                    }
                    catch (Exception ex)
                    {
                        _logger($"[WARN] Error disposing tracked client stdin: {ex.GetType().Name}: {ex.Message}");
                    }
                    _clientStdin = null;
                    _trackedClientProcess = null;
                }
            }
        }

        // Sweep orphaned headless test clients, strictly protecting user's active game
        if (!GameServerManageHandler.IsTestExecution)
        {
            string[] targetNames = ["hon_x64", "hon"];
            foreach (string name in targetNames)
            {
                try
                {
                    Process[] procs = Process.GetProcessesByName(name);
                    foreach (Process p in procs)
                    {
                        try
                        {
                            if (p.Id != Environment.ProcessId && !killedIds.Contains(p.Id))
                            {
                                if (IsClientProcess(p))
                                {
                                    if (_processSupervisor.IsUserClient(p))
                                    {
                                        _logger($"[SECURITY GUARD] Preserving user active game client PID {p.Id}.");
                                        continue;
                                    }

                                    if (IsWindowOffscreen(p.MainWindowHandle))
                                    {
                                        int pid = p.Id;
                                        p.Kill(true);
                                        p.WaitForExit(1500);
                                        killedIds.Add(pid);
                                        _logger($"Terminated orphaned headless game client process '{name}' PID {pid}.");
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger($"[WARN] Failed to inspect or terminate candidate process '{name}': {ex.GetType().Name}: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger($"[WARN] Failed enumerating processes for '{name}': {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        var response = new
        {
            status = "success",
            action = "stop",
            running = false,
            terminatedProcessesCount = killedIds.Count,
            terminatedProcessIds = killedIds,
            message = killedIds.Count > 0
                ? $"Successfully stopped {killedIds.Count} game client process(es) (PIDs: {string.Join(", ", killedIds)})."
                : "No active managed game client processes were running.",
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        };

        return ToolCallResult.Json(response);
    }

    public async Task<ToolCallResult> LoginClientAsync(
        string? username,
        string? password,
        int? processId = null,
        int? slotIndex = null,
        CancellationToken cancellationToken = default)
    {
        var creds = _credentialManager.ResolveCredentials(username, password, slotIndex ?? 0);
        var launchArgs = _credentialManager.BuildAuthenticationLaunchArguments(creds);
        var redactedLaunchArgs = RedactLaunchArguments(launchArgs, creds.PasswordMd5);

        _logger($"Performing client login for user '{creds.Username}' (PID: {processId?.ToString() ?? "auto"}, Slot: {slotIndex?.ToString() ?? "0"})...");

        string loginCmd = $"Set login_name \"{creds.Username}\"; Set login_password \"{creds.PasswordMd5}\"; Script if Nexus and Nexus.HideLogin then Nexus.HideLogin('{creds.Username}') else Trigger('LoginStatus', 'success', '', true); end";

        ToolCallResult? res = null;
        if (!GameServerManageHandler.IsTestExecution)
        {
            res = await ExecuteClientCommandAsync(loginCmd, 3000, autoLaunch: false, allowKeystrokeFallback: false, processId, cancellationToken).ConfigureAwait(false);
        }

        bool ok = res == null || !res.IsError;

        return ToolCallResult.Json(new
        {
            status = ok ? "success" : "error",
            action = "login",
            username = creds.Username,
            passwordMd5 = ClientCredentialManager.RedactedPlaceholder,
            launchArgs = redactedLaunchArgs,
            processId,
            message = ok ? $"Login credentials successfully dispatched for '{creds.Username}'." : "Login command failed or client offline.",
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        }, isError: !ok);
    }

    /// <summary>
    /// Gets authoritative liveness telemetry for the game client process.
    /// </summary>
    public ClientLivenessStatus GetLivenessStatus()
    {
        Process? proc = null;
        lock (_lock)
        {
            if (_trackedClientProcess != null && !_trackedClientProcess.HasExited)
            {
                proc = _trackedClientProcess;
            }
        }

        if (proc == null)
        {
            proc = FindRunningClientProcess();
        }

        bool isRunning = proc != null && !proc.HasExited;
        bool ipcConnected = EngineTelemetryDiagnostics.CheckIpcConnectivity();

        return new ClientLivenessStatus
        {
            Running = isRunning,
            ProcessId = isRunning && proc != null ? proc.Id : 0,
            EngineReady = isRunning && ipcConnected,
            IpcConnected = ipcConnected,
            MainWindowHandle = isRunning && proc != null ? $"0x{proc.MainWindowHandle.ToInt64():X8}" : null,
            WindowMode = isRunning && proc != null ? (IsWindowOffscreen(proc.MainWindowHandle) ? "headless" : "foreground") : null
        };
    }

    private async Task<ToolCallResult> GetClientStatusAsync(int? processId = null, CancellationToken cancellationToken = default)
    {
        Process? proc = null;
        if (processId.HasValue)
        {
            proc = GetRunningClientProcess(processId.Value);
        }
        else
        {
            lock (_lock)
            {
                if (_trackedClientProcess != null && !_trackedClientProcess.HasExited)
                {
                    proc = _trackedClientProcess;
                }
            }

            if (proc == null)
            {
                proc = FindRunningClientProcess();
            }
        }

        bool isRunning = proc != null && !proc.HasExited;
        bool ipcConnected = EngineTelemetryDiagnostics.CheckIpcConnectivity();
        var (phase, phaseName) = await EngineTelemetryDiagnostics.GetEnginePhaseAsync(proc, _gameIpcBridge, _lastWorkingDirectory, cancellationToken).ConfigureAwait(false);

        EngineStateDto? nativeEngineState = null;
        if (_gameIpcBridge != null && _gameIpcBridge.IsConnected)
        {
            try
            {
                nativeEngineState = await _gameIpcBridge.GetEngineStateAsync(500, cancellationToken).ConfigureAwait(false);
                if (nativeEngineState != null && nativeEngineState.GamePhase > 0)
                {
                    phase = nativeEngineState.GamePhase;
                    phaseName = nativeEngineState.GamePhase switch
                    {
                        1 => "Loading",
                        2 => "HeroSelect",
                        3 => "ActiveMatch",
                        4 => "PostGame",
                        _ => phaseName
                    };
                }
            }
            catch (Exception ex)
            {
                _logger($"[GameClientManage] GetEngineStateAsync probe skipped during status check: {ex.Message}");
            }
        }

        GraphicsStatusResult graphics = EngineTelemetryDiagnostics.GetGraphicsStatus(_lastWorkingDirectory);
        AudioStatusResult audio = EngineTelemetryDiagnostics.GetAudioStatus(_lastWorkingDirectory);
        VfsStatusResult vfs = VfsDiagnostics.GetVfsStatus(_lastWorkingDirectory);
        string nextAction = EngineTelemetryDiagnostics.GetClientNextAction(isRunning, ipcConnected, phase, vfs.ShadowedFilesCount);

        if (proc != null)
        {
            try
            {
                proc.Refresh();
                if (!proc.HasExited)
                {
                    string hwnd = $"0x{proc.MainWindowHandle.ToInt64():X8}";
                    long workingSet = proc.WorkingSet64;
                    double workingSetMb = Math.Round(workingSet / (1024.0 * 1024.0), 2);
                    string? exePath = TryGetProcessFileName(proc) ?? _lastExecutable;

                    bool isOffscreen = IsWindowOffscreen(proc.MainWindowHandle);
                    string currentWindowMode = isOffscreen ? "headless" : "foreground";
                    var managedInst = _processRegistry.Get(proc.Id);

                    var runningResponse = new
                    {
                        status = "success",
                        action = "status",
                        running = true,
                        processId = proc.Id,
                        mainWindowHandle = hwnd,
                        slotIndex = managedInst?.SlotIndex,
                        offscreenX = managedInst?.OffscreenX,
                        offscreenY = managedInst?.OffscreenY,
                        username = managedInst?.Username,
                        pipeName = managedInst?.PipeName,
                        agentOwner = managedInst?.AgentOwner,
                        totalManagedClients = (_processRegistry.GetAllClients() ?? _processRegistry.GetAll())?.Count ?? 0,
                        isOffscreen,
                        windowMode = currentWindowMode,
                        executable = exePath,
                        workingDirectory = _lastWorkingDirectory,
                        workingSetBytes = workingSet,
                        workingSetMb,
                        fps = nativeEngineState != null && nativeEngineState.CurrentFps > 0 ? nativeEngineState.CurrentFps : (double?)null,
                        responding = proc.Responding,
                        enginePhase = phase,
                        gamePhase = phase,
                        gamePhaseName = phaseName,
                        ipcConnected,
                        nativeEngineState = nativeEngineState != null ? new
                        {
                            fps = nativeEngineState.CurrentFps,
                            gamePhase = nativeEngineState.GamePhase,
                            canonicalGamePhase = nativeEngineState.CanonicalGamePhase,
                            isAuthenticated = nativeEngineState.IsAuthenticated,
                            isActiveMatch = nativeEngineState.IsActiveMatch || nativeEngineState.ActiveMatchId > 0 || nativeEngineState.GamePhase == 3,
                            isSpectator = nativeEngineState.IsSpectator,
                            workingSetMb = nativeEngineState.MemoryWorkingSetMb,
                            map = nativeEngineState.CurrentMap,
                            player = nativeEngineState.PlayerName,
                            activeMatchId = nativeEngineState.ActiveMatchId
                        } : null,
                        graphics = new
                        {
                            backend = graphics.Backend,
                            driver = graphics.Driver,
                            adapter = graphics.Adapter,
                            targetRefreshRate = graphics.TargetRefreshRate,
                            maxFps = graphics.MaxFps,
                            displayMode = graphics.DisplayMode
                        },
                        audio = new
                        {
                            driverName = audio.DriverName,
                            api = audio.Api,
                            sampleRate = audio.SampleRate,
                            channels = audio.Channels,
                            isReady = audio.IsReady,
                            fallbackToMute = audio.FallbackToMute
                        },
                        vfs = new
                        {
                            searchOrder = vfs.SearchOrder,
                            overrideMountedFirst = vfs.OverrideMountedFirst,
                            overrideFilesCount = vfs.OverrideFilesCount,
                            shadowedFilesCount = vfs.ShadowedFilesCount,
                            shadowedFiles = vfs.ShadowedFiles
                        },
                        nextAction,
                        selfHealingEnabled = _remediationService.IsEnabled,
                        timestamp = _timeProvider.GetUtcNow().ToString("o")
                    };

                    return ToolCallResult.Json(runningResponse);
                }
            }
            catch (Exception ex)
            {
                _logger($"[WARN] Exception inspecting running client process: {ex.Message}");
            }
        }

        _logger("Game client is not currently running.");
        var stoppedResponse = new
        {
            status = "success",
            action = "status",
            running = false,
            processId = 0,
            mainWindowHandle = "0x00000000",
            totalManagedClients = (_processRegistry.GetAllClients() ?? _processRegistry.GetAll())?.Count ?? 0,
            isOffscreen = false,
            windowMode = "offline",
            executable = _lastExecutable,
            workingDirectory = _lastWorkingDirectory,
            workingSetBytes = (long?)null,
            workingSetMb = (double?)null,
            responding = false,
            enginePhase = (int?)null,
            gamePhase = (int?)null,
            gamePhaseName = "Offline",
            ipcConnected = false,
            graphics = new
            {
                backend = graphics.Backend,
                driver = graphics.Driver,
                adapter = graphics.Adapter,
                targetRefreshRate = graphics.TargetRefreshRate,
                maxFps = graphics.MaxFps,
                displayMode = graphics.DisplayMode
            },
            audio = new
            {
                driverName = audio.DriverName,
                api = audio.Api,
                sampleRate = audio.SampleRate,
                channels = audio.Channels,
                isReady = audio.IsReady,
                fallbackToMute = audio.FallbackToMute
            },
            vfs = new
            {
                searchOrder = vfs.SearchOrder,
                overrideMountedFirst = vfs.OverrideMountedFirst,
                overrideFilesCount = vfs.OverrideFilesCount,
                shadowedFilesCount = vfs.ShadowedFilesCount,
                shadowedFiles = vfs.ShadowedFiles
            },
            nextAction,
            selfHealingEnabled = _remediationService.IsEnabled,
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        };

        return ToolCallResult.Json(stoppedResponse);
    }

    private async Task<ToolCallResult> HandlePipeStatusAsync(CancellationToken cancellationToken = default)
    {
        Stopwatch sw = Stopwatch.StartNew();

        // 1. In-process native game IPC pipe (kongor-native-client)
        bool nativePipeConnected = _gameIpcBridge.IsConnected;
        double? nativePingLatencyMs = null;
        EngineStateDto? engineState = null;
        if (!nativePipeConnected)
        {
            try
            {
                await _gameIpcBridge.ConnectAsync(500, cancellationToken).ConfigureAwait(false);
                nativePipeConnected = _gameIpcBridge.IsConnected;
            }
            catch (Exception ex)
            {
                _logger($"[GameClientManage] PipeStatus ConnectAsync skipped: {ex.Message}");
            }
        }

        if (nativePipeConnected)
        {
            try
            {
                Stopwatch pingSw = Stopwatch.StartNew();
                long pingMs = await _gameIpcBridge.PingAsync(500, cancellationToken).ConfigureAwait(false);
                pingSw.Stop();
                if (pingMs >= 0)
                {
                    nativePingLatencyMs = pingMs;
                }
            }
            catch (Exception ex)
            {
                _logger($"[GameClientManage] PipeStatus PingAsync skipped: {ex.Message}");
            }

            try
            {
                engineState = await _gameIpcBridge.GetEngineStateAsync(500, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger($"[GameClientManage] PipeStatus GetEngineStateAsync skipped: {ex.Message}");
            }
        }

        // 2. TAO Developer Console / Overlay pipe (kongor-overlay)
        bool overlayConnected = false;
        string? overlayTabs = null;
        double? overlayLatencyMs = null;
        try
        {
            Stopwatch oSw = Stopwatch.StartNew();
            using var overlayClient = new NamedPipeClientStream(".", "kongor-overlay", PipeDirection.InOut, PipeOptions.Asynchronous);
            await overlayClient.ConnectAsync(200, cancellationToken).ConfigureAwait(false);
            if (overlayClient.IsConnected)
            {
                overlayConnected = true;
                byte[] cmdBytes = Encoding.UTF8.GetBytes("GET_TABS\n");
                await overlayClient.WriteAsync(cmdBytes.AsMemory(), cancellationToken).ConfigureAwait(false);
                await overlayClient.FlushAsync(cancellationToken).ConfigureAwait(false);

                byte[] buffer = new byte[4096];
                int read = await overlayClient.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                oSw.Stop();
                overlayLatencyMs = Math.Round(oSw.Elapsed.TotalMilliseconds, 2);
                if (read > 0)
                {
                    overlayTabs = Encoding.UTF8.GetString(buffer, 0, read).Trim();
                }
            }
        }
        catch (Exception ex)
        {
            _logger($"[GameClientManage] Overlay pipe probe skipped: {ex.Message}");
        }

        // 3. Fallback / legacy Willowmaker IPC pipe (willowmaker-ipc)
        bool willowmakerConnected = (_gameIpcBridge as CompositeGameIpcBridge)?.Secondary.IsConnected == true
            || (_gameIpcBridge as McpServerIpcBridge)?.IsConnected == true;
        if (!willowmakerConnected && McpIpcServer.IsPipeHostedInCurrentProcess("willowmaker-ipc"))
        {
            willowmakerConnected = McpIpcServer.GetHostedPipeConnectedClientCount("willowmaker-ipc") > 0;
        }
        else if (!willowmakerConnected)
        {
            try
            {
                using var wmClient = new NamedPipeClientStream(".", "willowmaker-ipc", PipeDirection.InOut, PipeOptions.Asynchronous);
                await wmClient.ConnectAsync(100, cancellationToken).ConfigureAwait(false);
                willowmakerConnected = wmClient.IsConnected;
            }
            catch (Exception ex)
            {
                _logger($"[GameClientManage] Willowmaker pipe probe skipped: {ex.Message}");
            }
        }

        sw.Stop();

        return ToolCallResult.Json(new
        {
            status = "success",
            action = "pipe_status",
            totalPipes = 3,
            activePipes = (nativePipeConnected ? 1 : 0) + (overlayConnected ? 1 : 0) + (willowmakerConnected ? 1 : 0),
            pipes = new
            {
                nativeClient = new
                {
                    pipeName = @"\\.\pipe\kongor-native-client",
                    active = nativePipeConnected,
                    pingLatencyMs = nativePingLatencyMs,
                    protocol = "Binary OpCode IPC (0x0001: Execute, 0x0002: Ping, 0x0008: GetEngineState, 0x0009: DispatchUiEvent)",
                    engineState = engineState != null ? new
                    {
                        fps = engineState.CurrentFps,
                        workingSetMb = engineState.MemoryWorkingSetMb,
                        gamePhase = engineState.GamePhase,
                        map = engineState.CurrentMap,
                        player = engineState.PlayerName,
                        activeMatchId = engineState.ActiveMatchId,
                        isActiveMatch = engineState.ActiveMatchId > 0 || engineState.GamePhase == 3
                    } : null
                },
                overlayConsole = new
                {
                    pipeName = @"\\.\pipe\kongor-overlay",
                    active = overlayConnected,
                    latencyMs = overlayLatencyMs,
                    description = "TAO Developer Console & Visual Telemetry Multiplexer (RmlUi HUD)",
                    tabs = overlayTabs
                },
                willowmakerIpc = new
                {
                    pipeName = @"\\.\pipe\willowmaker-ipc",
                    active = willowmakerConnected,
                    description = "Legacy Willowmaker Bridge Named Pipe"
                }
            },
            durationMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        });
    }

    /// <summary>
    /// Waits up to <paramref name="timeoutMs"/> for the game client engine to be ready
    /// (window created and responding or IPC handshake established).
    /// </summary>
    public async Task<bool> EnsureEngineReadyAsync(int timeoutMs = 5000, CancellationToken cancellationToken = default)
    {
        if (GameServerManageHandler.IsTestExecution) return true;

        Stopwatch sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var liveness = await ExecutionTruth.ProbeClientLivenessAsync(this, _gameIpcBridge, cancellationToken).ConfigureAwait(false);
            if (liveness.EngineReady) return true;
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }
        return false;
    }

    /// <summary>
    /// Finds the currently active game client process.
    /// </summary>
    public Process? GetRunningClientProcess(int? processId = null)
    {
        if (processId.HasValue)
        {
            try
            {
                var p = Process.GetProcessById(processId.Value);
                if (!p.HasExited && IsClientProcess(p)) return p;
            }
            catch (Exception ex)
            {
                _logger($"[GameClientManage] Process lookup for PID {processId.Value} skipped: {ex.Message}");
            }
            return null;
        }

        lock (_lock)
        {
            if (_trackedClientProcess != null)
            {
                try
                {
                    _trackedClientProcess.Refresh();
                    if (!_trackedClientProcess.HasExited) return _trackedClientProcess;
                }
                catch (Exception ex)
                {
                    _logger($"[GameClientManage] Tracked process refresh skipped: {ex.Message}");
                }
            }
        }

        var managed = _processRegistry.GetAll();
        foreach (var inst in managed)
        {
            try
            {
                var p = Process.GetProcessById(inst.ProcessId);
                if (!p.HasExited) return p;
            }
            catch (Exception ex)
            {
                _logger($"[GameClientManage] Managed process lookup for PID {inst.ProcessId} skipped: {ex.Message}");
            }
        }

        // In test execution, suppress expensive Win32 process enumeration per AGENTS.md Rule 4
        if (GameServerManageHandler.IsTestExecution)
        {
            return null;
        }

        try
        {
            Process[] procs = Process.GetProcessesByName("hon_x64");
            foreach (Process p in procs)
            {
                if (p.Id == Environment.ProcessId || p.HasExited) continue;
                if (IsClientProcess(p)) return p;
            }
        }
        catch (Exception ex)
        {
            _logger($"[GameClientManage] Process enumeration skipped: {ex.Message}");
        }

        return null;
    }

}
