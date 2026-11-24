// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WILLOWMAKER.Core.Services;
using Kongor.Cli.Mcp.Handlers;

namespace Kongor.Cli.Mcp.Services;

/// <summary>
/// Options for launching the game client or launcher process.
/// </summary>
public sealed class GameLaunchOptions
{
    public string Mode { get; set; } = "normal"; // "normal", "practice", "bot_match", "editor"
    public string? ConnectAddress { get; set; }
    public string Resolution { get; set; } = "1920x1080";
    public bool Fullscreen { get; set; } = true;
    public string MasterServer { get; set; } = "127.0.0.1:55555";
    public string? CustomNickname { get; set; }
    public string? CustomClientPath { get; set; }
    public string? WorkingDirectory { get; set; }
    public Action<string>? LogAction { get; set; }
    public string WindowMode { get; set; } = "foreground"; // "headless", "foreground", "minimized"
    public bool StealFocus { get; set; } = false;
}

/// <summary>
/// Result of a game launch operation.
/// </summary>
public sealed class GameLaunchResult
{
    public bool Success { get; set; }
    public string Status { get; set; } = "success";
    public string Action { get; set; } = "launch";
    public bool Running { get; set; }
    public int ProcessId { get; set; }
    public string MainWindowHandle { get; set; } = "0x00000000";
    public string Executable { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public IReadOnlyList<string> Arguments { get; set; } = [];
    public string? ErrorMessage { get; set; }
    public DateTimeOffset Timestamp { get; set; }
}

/// <summary>
/// Result of querying game process status.
/// </summary>
public sealed class GameStatusResult
{
    public string Status { get; set; } = "success";
    public string Action { get; set; } = "status";
    public bool Running { get; set; }
    public int ProcessId { get; set; }
    public string MainWindowHandle { get; set; } = "0x00000000";
    public string? Executable { get; set; }
    public string? WorkingDirectory { get; set; }
    public long? WorkingSetBytes { get; set; }
    public double? WorkingSetMb => WorkingSetBytes.HasValue ? Math.Round(WorkingSetBytes.Value / (1024.0 * 1024.0), 2) : null;
    public bool Responding { get; set; }
    public DateTimeOffset Timestamp { get; set; }
}

/// <summary>
/// Result of stopping the game process.
/// </summary>
public sealed class GameStopResult
{
    public string Status { get; set; } = "success";
    public string Action { get; set; } = "stop";
    public bool Running { get; set; } = false;
    public int TerminatedProcessesCount { get; set; }
    public IReadOnlyList<int> TerminatedProcessIds { get; set; } = [];
    public string? Message { get; set; }
    public DateTimeOffset Timestamp { get; set; }
}

/// <summary>
/// Interface for Game Client Lifecycle and Process Management.
/// </summary>
public interface IGameLauncherService
{
    Task<GameLaunchResult> LaunchAsync(GameLaunchOptions options, CancellationToken cancellationToken = default);
    GameStatusResult GetStatus();
    GameStopResult Stop(int? processId = null, bool killTree = true);
}

/// <summary>
/// Genuine Game Launcher Service managing hon_x64.exe and WILLOWMAKER processes,
/// isolated userdata trees, autoexec.cfg configuration, and launch arguments.
/// </summary>
public sealed class GameLauncherService : IGameLauncherService
{
    private readonly Lock _lock = new();
    private Process? _trackedProcess;
    private string? _lastExecutable;
    private string? _lastWorkingDir;
    private IReadOnlyList<string> _lastRedactedArguments = []; // Diagnostic only; never replay into ProcessStartInfo
    private readonly Action<string>? _defaultLogger;
    private readonly TimeProvider _timeProvider;

    public GameLauncherService(Action<string>? defaultLogger = null, TimeProvider? timeProvider = null)
    {
        _defaultLogger = defaultLogger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Launches the game client or editor process with genuine argument composition and directory setup.
    /// </summary>
    public async Task<GameLaunchResult> LaunchAsync(GameLaunchOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        Action<string> log = options.LogAction ?? _defaultLogger ?? (msg => Console.Error.WriteLine($"[GameLauncher] {msg}"));

        log($"Initiating game launch (Mode: {options.Mode}, Resolution: {options.Resolution}, MasterServer: {options.MasterServer})...");

        // 1. Resolve Game Executable
        if (!TryResolveGameExecutable(options.CustomClientPath, options.WorkingDirectory, out FileInfo? executable))
        {
            string err = $"Unable to resolve game executable '{options.CustomClientPath ?? "hon_x64.exe / WILLOWMAKER.exe"}'. Make sure the game is installed in games/client or specify customClientPath.";
            log($"[ERROR] {err}");
            return new GameLaunchResult
            {
                Success = false,
                Status = "error",
                Action = "launch",
                Running = false,
                ErrorMessage = err,
                Timestamp = _timeProvider.GetUtcNow()
            };
        }

        string gameDir = options.WorkingDirectory ?? executable.DirectoryName ?? Environment.CurrentDirectory;
        log($"Resolved executable: '{executable.FullName}', working directory: '{gameDir}'");

        // 2. Ensure User Directory Initialized & Clean Shader Cache
        EnsureUserDirectoryInitialized(gameDir, log);

        // 2.5. Async pre-flight Vulkan runtime configuration check
        try
        {
            if (!VulkanDriverUpgradeService.CheckDotLocalRedirection(gameDir) ||
                !File.Exists(Path.Combine(gameDir, "dxvk.conf")))
            {
                log("[VULKAN] DotLocal redirection or dxvk.conf missing. Auto-upgrading Vulkan runtime configuration...");
                await VulkanDriverUpgradeService.UpgradeToVulkanAsync(gameDir, RayTracingPreset.Competitive, log).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            log($"[VULKAN ERROR] Pre-flight upgrade failed: {ex.Message}");
        }

        // 3. Write Custom autoexec.cfg configuration
        WriteAutoexecConfig(gameDir, log);

        // 4. Compose Launch Arguments
        IReadOnlyList<string> arguments = BuildLaunchArguments(options, gameDir);
        IReadOnlyList<string> redactedArguments = GameClientManageHandler.RedactLaunchArguments(arguments);
        string joinedArgs = string.Join(" ", arguments);
        log($"Constructed launch arguments: {string.Join(" ", redactedArguments)}");

        bool isHeadless = string.Equals(options.WindowMode, "headless", StringComparison.OrdinalIgnoreCase);

        // 5. Start Process
        ProcessStartInfo startInfo = new()
        {
            FileName = executable.FullName,
            WorkingDirectory = gameDir,
            Arguments = joinedArgs,
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            RedirectStandardInput = false,
            CreateNoWindow = isHeadless,
            WindowStyle = isHeadless ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal
        };

        // Unix library paths if applicable
        if (OperatingSystem.IsLinux())
        {
            string libsDir = Path.Combine(gameDir, "libs-x86_64");
            if (Directory.Exists(libsDir))
            {
                string existingLd = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH") ?? string.Empty;
                startInfo.EnvironmentVariables["LD_LIBRARY_PATH"] = string.IsNullOrEmpty(existingLd) ? libsDir : $"{libsDir}:{existingLd}";
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            string frameworksDir = Path.Combine(gameDir, "Contents", "Frameworks", "universal64");
            if (Directory.Exists(frameworksDir))
            {
                string existingDyld = Environment.GetEnvironmentVariable("DYLD_LIBRARY_PATH") ?? string.Empty;
                startInfo.EnvironmentVariables["DYLD_LIBRARY_PATH"] = string.IsNullOrEmpty(existingDyld) ? frameworksDir : $"{frameworksDir}:{existingDyld}";
            }
        }
        else if (OperatingSystem.IsWindows())
        {
            string? layerDir = ResolveVulkanLayerDirectory(gameDir);
            if (!string.IsNullOrEmpty(layerDir))
            {
                string existingLayerPath = Environment.GetEnvironmentVariable("VK_LAYER_PATH") ?? string.Empty;
                string layerPathStr = string.IsNullOrEmpty(existingLayerPath)
                    ? $"{layerDir};{gameDir}"
                    : $"{layerDir};{gameDir};{existingLayerPath}";
                startInfo.EnvironmentVariables["VK_LAYER_PATH"] = layerPathStr;
                startInfo.EnvironmentVariables["VK_ADD_LAYER_PATH"] = layerPathStr;
                startInfo.EnvironmentVariables["VK_INSTANCE_LAYERS"] = "VK_LAYER_HON_replay_capture";
                startInfo.EnvironmentVariables["VK_LOADER_LAYERS_ENABLE"] = "*replay_capture*,VK_LAYER_HON_replay_capture";
                string? capEnv = Environment.GetEnvironmentVariable("HON_CAPTURE_ENABLE");
                startInfo.EnvironmentVariables["HON_CAPTURE_ENABLE"] = !string.IsNullOrEmpty(capEnv) ? capEnv : "1";
                startInfo.EnvironmentVariables["HON_CAPTURE_SLOTS"] = "3";
                startInfo.EnvironmentVariables["HON_REPLAY_FPS"] = "60";
                ConfigureVulkanOverlayEnvironment(
                    startInfo.EnvironmentVariables,
                    isHeadless,
                    Environment.GetEnvironmentVariable("HON_MODERN_HUD"),
                    Environment.GetEnvironmentVariable("HON_DEVTOOLS"));

                int resW = 1280;
                int resH = 720;
                if (!string.IsNullOrWhiteSpace(options.Resolution) && options.Resolution.Contains('x', StringComparison.OrdinalIgnoreCase))
                {
                    string[] parts = options.Resolution.Split(['x', 'X'], StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h))
                    {
                        resW = w;
                        resH = h;
                    }
                }
                startInfo.EnvironmentVariables["HON_REPLAY_WIDTH"] = resW.ToString();
                startInfo.EnvironmentVariables["HON_REPLAY_HEIGHT"] = resH.ToString();
                log($"[GameLauncher] Configured Vulkan Swapchain Layer: VK_LAYER_PATH='{layerDir}', Res={resW}x{resH}, HON_MODERN_HUD='{startInfo.EnvironmentVariables["HON_MODERN_HUD"]}', HON_DEVTOOLS='{startInfo.EnvironmentVariables["HON_DEVTOOLS"]}', KONGOR_HEADLESS='{startInfo.EnvironmentVariables["KONGOR_HEADLESS"]}'");
            }
        }

        Process process = new() { StartInfo = startInfo, EnableRaisingEvents = false };

        bool started;
        try
        {
            started = process.Start();
        }
        catch (Exception ex)
        {
            string err = $"Failed to start process '{executable.FullName}': {ex.Message}";
            log($"[ERROR] {err}");
            return new GameLaunchResult
            {
                Success = false,
                Status = "error",
                Action = "launch",
                Running = false,
                Executable = executable.FullName,
                WorkingDirectory = gameDir,
                Arguments = redactedArguments,
                ErrorMessage = err,
                Timestamp = _timeProvider.GetUtcNow()
            };
        }

        if (!started)
        {
            string err = $"Process.Start returned false for '{executable.FullName}'.";
            log($"[ERROR] {err}");
            return new GameLaunchResult
            {
                Success = false,
                Status = "error",
                Action = "launch",
                Running = false,
                Executable = executable.FullName,
                WorkingDirectory = gameDir,
                Arguments = redactedArguments,
                ErrorMessage = err,
                Timestamp = _timeProvider.GetUtcNow()
            };
        }

        lock (_lock)
        {
            _trackedProcess = process;
            _lastExecutable = executable.FullName;
            _lastWorkingDir = gameDir;
            _lastRedactedArguments = redactedArguments;
        }

        // 6. Wait for MainWindowHandle or process initialization (up to 3 seconds for responsive MCP tool return)
        int waitMs = 0;
        const int maxWait = 3000;
        IntPtr detectedHwnd = IntPtr.Zero;
        while (!process.HasExited && detectedHwnd == IntPtr.Zero && waitMs < maxWait)
        {
            try { process.Refresh(); } catch { }
            detectedHwnd = process.MainWindowHandle != IntPtr.Zero
                ? process.MainWindowHandle
                : WindowCapturer.FindWindowForProcessId(process.Id);
            if (detectedHwnd != IntPtr.Zero) break;
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            waitMs += 100;
        }

        int pid = 0;
        string hwndStr = "0x00000000";
        bool isRunning = false;

        try
        {
            if (!process.HasExited)
            {
                pid = process.Id;
                if (detectedHwnd == IntPtr.Zero)
                {
                    detectedHwnd = process.MainWindowHandle != IntPtr.Zero
                        ? process.MainWindowHandle
                        : WindowCapturer.FindWindowForProcessId(pid);
                }
                hwndStr = $"0x{detectedHwnd.ToInt64():X8}";
                isRunning = true;
                log($"Process launched successfully. PID: {pid}, MainWindowHandle: {hwndStr}");
            }
            else
            {
                log($"Process exited early with exit code: {process.ExitCode}");
            }
        }
        catch (Exception ex)
        {
            log($"Warning querying process state: {ex.Message}");
        }

        return new GameLaunchResult
        {
            Success = isRunning,
            Status = isRunning ? "success" : "exited",
            Action = "launch",
            Running = isRunning,
            ProcessId = pid,
            MainWindowHandle = hwndStr,
            Executable = executable.FullName,
            WorkingDirectory = gameDir,
            Arguments = redactedArguments,
            Timestamp = _timeProvider.GetUtcNow()
        };
    }

    /// <summary>
    /// Gets current running status of tracked game process or any active hon_x64/WILLOWMAKER process on the system.
    /// </summary>
    public GameStatusResult GetStatus()
    {
        Process? proc = null;
        lock (_lock)
        {
            if (_trackedProcess != null && !_trackedProcess.HasExited)
            {
                proc = _trackedProcess;
            }
        }

        if (proc == null)
        {
            // Discover any running hon_x64 or WILLOWMAKER process
            proc = FindRunningGameProcess();
        }

        if (proc != null)
        {
            try
            {
                proc.Refresh();
                if (!proc.HasExited)
                {
                    return new GameStatusResult
                    {
                        Status = "success",
                        Action = "status",
                        Running = true,
                        ProcessId = proc.Id,
                        MainWindowHandle = $"0x{proc.MainWindowHandle.ToInt64():X8}",
                        Executable = TryGetProcessFileName(proc) ?? _lastExecutable,
                        WorkingDirectory = _lastWorkingDir,
                        WorkingSetBytes = proc.WorkingSet64,
                        Responding = proc.Responding,
                        Timestamp = _timeProvider.GetUtcNow()
                    };
                }
            }
            catch
            {
                // Fallthrough to stopped
            }
        }

        return new GameStatusResult
        {
            Status = "success",
            Action = "status",
            Running = false,
            ProcessId = 0,
            MainWindowHandle = "0x00000000",
            Executable = _lastExecutable,
            WorkingDirectory = _lastWorkingDir,
            WorkingSetBytes = null,
            Responding = false,
            Timestamp = _timeProvider.GetUtcNow()
        };
    }

    /// <summary>
    /// Stops the tracked game process and any active hon_x64 / WILLOWMAKER processes on the machine.
    /// </summary>
    public GameStopResult Stop(int? processId = null, bool killTree = true)
    {
        List<int> killedIds = [];
        Action<string> log = _defaultLogger ?? (msg => Console.Error.WriteLine($"[GameLauncher] {msg}"));

        if (Handlers.GameServerManageHandler.IsTestExecution)
        {
            log("[TEST MODE] Suppressing Process.Kill in GameLauncherService.Stop to preserve running client.");
            return new GameStopResult
            {
                Status = "success",
                Action = "stop",
                Running = false,
                TerminatedProcessesCount = 0,
                TerminatedProcessIds = killedIds,
                Message = "Test mode: suppressed process termination."
            };
        }

        if (processId.HasValue && processId.Value > 0)
        {
            try
            {
                Process p = Process.GetProcessById(processId.Value);
                p.Kill(killTree);
                p.WaitForExit(2000);
                killedIds.Add(processId.Value);
                log($"Terminated explicit process PID {processId.Value}.");
            }
            catch (Exception ex)
            {
                log($"Could not terminate PID {processId.Value}: {ex.Message}");
            }
        }
        else
        {
            // Kill tracked process
            lock (_lock)
            {
                if (_trackedProcess != null)
                {
                    try
                    {
                        if (!_trackedProcess.HasExited)
                        {
                            int pid = _trackedProcess.Id;
                            _trackedProcess.Kill(killTree);
                            _trackedProcess.WaitForExit(2000);
                            killedIds.Add(pid);
                            log($"Terminated tracked game process PID {pid}.");
                        }
                    }
                    catch (Exception ex)
                    {
                        log($"Error terminating tracked process: {ex.Message}");
                    }
                    finally
                    {
                        _trackedProcess = null;
                    }
                }
            }

            // Also search and terminate any other orphaned hon_x64 or WILLOWMAKER processes
            string[] targetProcessNames = ["hon_x64", "WILLOWMAKER", "hon"];
            foreach (string pName in targetProcessNames)
            {
                try
                {
                    Process[] procs = Process.GetProcessesByName(pName);
                    foreach (Process p in procs)
                    {
                        try
                        {
                            if (p.Id != Environment.ProcessId && !killedIds.Contains(p.Id))
                            {
                                int pid = p.Id;
                                p.Kill(killTree);
                                p.WaitForExit(1000);
                                killedIds.Add(pid);
                                log($"Terminated process '{pName}' PID {pid}.");
                            }
                        }
                        catch (Exception killEx)
                        {
                            log($"[WARN] Error terminating process '{pName}': {killEx.Message}");
                        }
                    }
                }
                catch (Exception enumEx)
                {
                    log($"[WARN] Error enumerating '{pName}' processes: {enumEx.Message}");
                }
            }
        }

        return new GameStopResult
        {
            Status = "success",
            Action = "stop",
            Running = false,
            TerminatedProcessesCount = killedIds.Count,
            TerminatedProcessIds = killedIds,
            Message = killedIds.Count > 0
                ? $"Successfully stopped {killedIds.Count} game process(es) (PIDs: {string.Join(", ", killedIds)})."
                : "No active game client processes were running.",
            Timestamp = _timeProvider.GetUtcNow()
        };
    }

    /// <summary>
    /// Builds canonical command-line arguments matching WILLOWMAKER MainViewModel.Launch.cs.
    /// </summary>
    public static IReadOnlyList<string> BuildLaunchArguments(GameLaunchOptions options, string gameDir)
    {
        ArgumentNullException.ThrowIfNull(options);

        bool isP2p = string.Equals(options.Mode, "p2p_host", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(options.Mode, "p2p_connect", StringComparison.OrdinalIgnoreCase);

        string effectiveMasterServer = isP2p
            ? ((string.Equals(options.MasterServer, "127.0.0.1:55555", StringComparison.OrdinalIgnoreCase) || string.Equals(options.MasterServer, "masterserver.newbs.io", StringComparison.OrdinalIgnoreCase)) ? "127.0.0.1:19999" : NormalizeMasterServerAddress(options.MasterServer))
            : NormalizeMasterServerAddress(options.MasterServer);

        List<string> args = [];

        // 1. Service Routing
        args.Add($"-masterserver {effectiveMasterServer}");
        args.Add("-chatserver 127.0.0.1:11031");
        args.Add($"-webserver {effectiveMasterServer}");
        args.Add($"-messageserver {effectiveMasterServer}");
        args.Add("+Set chat_serverAddrOverride 127.0.0.1");
        args.Add("+Set chat_serverPortOverride 11031");

        // 2. Canonical Mod / VFS Chain
        if (string.Equals(options.Mode, "editor", StringComparison.OrdinalIgnoreCase))
        {
            args.Add(ClientModMountBuilder.GetCanonicalModString() + ";editor");
        }
        else
        {
            args.Add(ClientModMountBuilder.GetCanonicalModString());
        }

        // 3. Real-time logging with flushing enabled for telemetry & debugging
        args.Add("-writeconsole");
        args.Add("-logfile console.log");
        args.Add("+Set log_flush true");
        args.Add("+Set con_flushLog true");
        args.Add("+Set sys_flushLogs true");
        args.Add("+Set con_log 1");
        args.Add("+Set host_writeConsole true");

        // 4. Named Pipe IPC Hook
        args.Add("-pipe willowmaker-ipc");

        // 5. Performance & FPS Uncapping
        args.Add("+Set host_maxFPS 1000");
        args.Add("+Set cl_maxFPS 1000");
        args.Add("+Set vid_maxFPS 1000");
        args.Add("+Set sys_sleepTime 0");
        args.Add("+Set gl_swapInterval 0");
        args.Add("+Set d3d_tripleBuffering false");
        args.Add("+Set d3d_pure true");
        args.Add("+Set net_enableWatermark false");

        // 6. UI & Developer Mode
        args.Add("+Set releaseStage_dev true");
        args.Add("+Set releaseStage_test true");
        args.Add("+Set releaseStage_stable false");
        args.Add("+Set ui_dev true");
        args.Add("+Set ui_dev_button true");
        args.Add("+Set ui_showQuickStart true");
        args.Add("+Set ui_canCreateGame true");
        args.Add("+Set cg_24hourClock true");
        args.Add("+Set host_dynamicResReload true");

        bool isHeadless = string.Equals(options.WindowMode, "headless", StringComparison.OrdinalIgnoreCase);

        // 7. Display & Resolution Settings
        if (isHeadless)
        {
            // Do not pass vid_headless true as it suppresses GPU presentation and results in black captures
            args.Add("+Set vid_resolution 1280x720");
            args.Add("+Set vid_width 1280");
            args.Add("+Set vid_height 720");
            args.Add("+Set vid_fullscreen false");
            args.Add("+Set vid_borderless true");
            args.Add("+Set vid_pos -32000 -32000");
            args.Add("+Set sound_disable true");
            args.Add("+Set sys_autoSaveConfig false");
        }
        else
        {
            string res = string.IsNullOrWhiteSpace(options.Resolution) ? "1920x1080" : options.Resolution.Trim().Replace(',', 'x');
            args.Add($"+Set vid_resolution {res}");

            if (TryParseResolution(res, out int width, out int height))
            {
                args.Add($"+Set vid_width {width}");
                args.Add($"+Set vid_height {height}");
            }

            args.Add($"+Set vid_fullscreen {(options.Fullscreen ? "true" : "false")}");
            args.Add($"+Set vid_borderless {(options.Fullscreen ? "false" : "true")}");
        }

        if (isP2p || (!string.IsNullOrWhiteSpace(options.CustomNickname) &&
                      !string.Equals(options.CustomNickname, Environment.UserName, StringComparison.OrdinalIgnoreCase) &&
                      !string.Equals(options.CustomNickname, "UnnamedNewbie", StringComparison.OrdinalIgnoreCase)))
        {
            string resolvedNick = !string.IsNullOrWhiteSpace(options.CustomNickname) &&
                                 !string.Equals(options.CustomNickname, Environment.UserName, StringComparison.OrdinalIgnoreCase) &&
                                 !string.Equals(options.CustomNickname, "UnnamedNewbie", StringComparison.OrdinalIgnoreCase)
                ? options.CustomNickname.Trim()
                : GameClientManageHandler.ResolveDefaultNickname(options.WorkingDirectory);

            args.Add($"+Set p2p_nickname \"{resolvedNick}\"");
            args.Add($"+Set login_name \"{resolvedNick}\"");
            GameClientManageHandler.EnsureClientDefaultNameConfigured(options.WorkingDirectory ?? Environment.CurrentDirectory, resolvedNick);
        }

        // 8. Specific Game Modes (K2 Engine CMD(StartGame) requires >= 3 tokens: <type> <name> <options>)
        if (string.Equals(options.Mode, "practice", StringComparison.OrdinalIgnoreCase))
        {
            args.Add("+Set sv_heroSelectTime 0");
            args.Add("+Set sv_heroSelectInitialTime 0");
            args.Add("+Set sv_preMatchTime 0");
            args.Add("+Set sv_matchStartAnnounceTime 0");
            args.Add("+Set sv_gameStartCountdown 0");
            args.Add("+Set sv_gameCountdownLength 0");
            args.Add("+Set sv_precacheEntities false");
            args.Add("+Set svr_login false");
            args.Add("+Set svr_broadcastLocal false");
            args.Add("+Set ui_quickStart true");
            args.Add("+StartGame practice KongorMatch \"map:caldavar allheroes:true\"");
        }
        else if (string.Equals(options.Mode, "bot_match", StringComparison.OrdinalIgnoreCase))
        {
            args.Add("+Set game_botMatch true");
            args.Add("+Set svr_botMatch true");
            args.Add("+Set sv_heroSelectTime 0");
            args.Add("+Set sv_heroSelectInitialTime 0");
            args.Add("+Set sv_preMatchTime 0");
            args.Add("+Set sv_matchStartAnnounceTime 0");
            args.Add("+Set sv_gameStartCountdown 0");
            args.Add("+Set sv_gameCountdownLength 0");
            args.Add("+Set svr_login false");
            args.Add("+Set svr_broadcastLocal false");
            args.Add("+Set ui_quickStart true");
            args.Add("+StartGame practice KongorBotMatch \"map:caldavar allheroes:true bots:true\"");
        }
        else if (string.Equals(options.Mode, "p2p_host", StringComparison.OrdinalIgnoreCase))
        {
            args.Add("+Set p2p_mode true");
            args.Add("+Set p2p_auto_host true");
            args.Add("+Set p2p_standalone true");
            args.Add("+Set login_autoLogin false");
            args.Add("+Set login_rememberPassword false");
        }
        else if (string.Equals(options.Mode, "p2p_connect", StringComparison.OrdinalIgnoreCase))
        {
            args.Add("+Set p2p_mode true");
            args.Add("+Set p2p_auto_connect true");
            args.Add("+Set p2p_standalone true");
            args.Add("+Set login_autoLogin false");
            args.Add("+Set login_rememberPassword false");
            if (!string.IsNullOrWhiteSpace(options.ConnectAddress))
            {
                args.Add($"+Set p2p_connect_addr \"{options.ConnectAddress.Trim()}\"");
            }
        }

        // 9. Direct Connect Address
        if (!string.IsNullOrWhiteSpace(options.ConnectAddress))
        {
            args.Add($"+Connect {options.ConnectAddress.Trim()}");
        }

        return args.AsReadOnly();
    }

    /// <summary>
    /// Normalizes master server address by resolving localhost to 127.0.0.1 and appending port 83 for masterserver.newbs.io.
    /// </summary>
    public static string NormalizeMasterServerAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return "127.0.0.1:55555";

        string clean = address.Trim();
        if (clean.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            clean = clean[7..];
        else if (clean.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            clean = clean[8..];

        clean = clean.TrimEnd('/');

        // The game client does not understand "localhost", replace with 127.0.0.1
        clean = clean.Replace("localhost", IPAddress.Loopback.ToString(), StringComparison.OrdinalIgnoreCase);

        // Ensure 127.0.0.1 connects via port 55555 if no port specified.
        // Note: masterserver.newbs.io is fronted by Cloudflare Tunnel (:80/:443) and must NOT be rewritten to legacy :83 (MCP-251).
        if (clean.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase))
            clean = "127.0.0.1:55555";

        return clean;
    }

    public static bool IsLoopbackMasterServer(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return true;
        }

        string clean = NormalizeMasterServerAddress(address);
        return clean.StartsWith("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
               clean.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) ||
               clean.StartsWith("[::1]", StringComparison.OrdinalIgnoreCase) ||
               clean.StartsWith("::1", StringComparison.OrdinalIgnoreCase) ||
               clean.StartsWith("0.0.0.0", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves the default master server address. Defaults to local cluster loopback (127.0.0.1:55555)
    /// unless useLauncherSettings is explicitly requested (MCP-288).
    /// </summary>
    public static string ResolveDefaultMasterServer(string? gameDir = null, bool useLauncherSettings = false)
    {
        if (!useLauncherSettings)
        {
            return "127.0.0.1:55555";
        }

        try
        {
            string baseDir = gameDir ?? Environment.CurrentDirectory;
            string repoRoot = FindRepoRoot(baseDir);
            string clientDir = Path.Combine(repoRoot, "games", "client");

            string settingsPath = Path.Combine(clientDir, "launcher_settings.json");
            if (!File.Exists(settingsPath))
            {
                settingsPath = Path.Combine(baseDir, "launcher_settings.json");
            }
            if (!File.Exists(settingsPath))
            {
                settingsPath = Path.Combine(repoRoot, "launcher_settings.json");
            }

            if (File.Exists(settingsPath))
            {
                string json = File.ReadAllText(settingsPath);
                using var doc = JsonDocument.Parse(json);
                string? candidate = TryReadCaseInsensitiveString(doc.RootElement, "CustomMasterServerAddress")
                                    ?? TryReadCaseInsensitiveString(doc.RootElement, "MasterServerAddress");

                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    return NormalizeMasterServerAddress(candidate);
                }
            }
        }
        catch { }

        return "127.0.0.1:55555";
    }

    private static string? TryReadCaseInsensitiveString(JsonElement root, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (JsonProperty prop in root.EnumerateObject())
        {
            if (string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase) &&
                prop.Value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(prop.Value.GetString()))
            {
                return prop.Value.GetString()!.Trim();
            }
        }

        return null;
    }

    /// <summary>
    /// Ensures isolated userdata tree exists and cleans old shader cache.
    /// </summary>
    public static void EnsureUserDirectoryInitialized(string? baseDirectory = null, Action<string>? log = null)
    {
        string rootDir = baseDirectory ?? Environment.CurrentDirectory;
        string userdataRoot = Path.Combine(rootDir, "userdata");
        string userdataKongor = Path.Combine(rootDir, "userdataKONGOR");
        string kongorDir = Path.Combine(rootDir, "KONGOR");

        string[] requiredSubdirs =
        [
            userdataRoot,
            Path.Combine(userdataRoot, "game"),
            Path.Combine(userdataRoot, "game", "logs"),
            Path.Combine(userdataRoot, "game", "replays"),
            Path.Combine(userdataRoot, "game", "shaders"),
            Path.Combine(userdataRoot, "editor"),
            Path.Combine(userdataRoot, "Heroes of Newerth x64"),
            Path.Combine(userdataRoot, "Heroes of Newerth x64", "game"),
            Path.Combine(userdataRoot, "Heroes of Newerth x64", "game", "logs"),
            Path.Combine(userdataRoot, "Heroes of Newerth x64", "game", "replays"),
            Path.Combine(userdataRoot, "Heroes of Newerth x64", "replays"),
            userdataKongor,
            Path.Combine(userdataKongor, "extensions"),
            Path.Combine(userdataKongor, "configuration"),
            Path.Combine(userdataKongor, "updates"),
            kongorDir,
            Path.Combine(kongorDir, "configuration")
        ];

        foreach (string dir in requiredSubdirs)
        {
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }

        // Bi-directional replay synchronization between isolated userdata folders and the engine's canonical path
        try
        {
            string canonicalReplaysDir = Path.Combine(userdataRoot, "Heroes of Newerth x64", "game", "replays");
            if (!Directory.Exists(canonicalReplaysDir))
            {
                Directory.CreateDirectory(canonicalReplaysDir);
            }

            string[] sourceReplayDirs =
            [
                Path.Combine(userdataRoot, "replays"),
                Path.Combine(userdataRoot, "game", "replays"),
                Path.Combine(rootDir, "replays"),
                Path.Combine(rootDir, "game", "replays")
            ];

            foreach (string sDir in sourceReplayDirs)
            {
                if (Directory.Exists(sDir))
                {
                    string[] replayFiles = Directory.GetFiles(sDir, "*.honreplay");
                    foreach (string replayFile in replayFiles)
                    {
                        string fileName = Path.GetFileName(replayFile);
                        string targetPath = Path.Combine(canonicalReplaysDir, fileName);
                        if (!File.Exists(targetPath) || new FileInfo(targetPath).Length != new FileInfo(replayFile).Length)
                        {
                            File.Copy(replayFile, targetPath, true);
                            log?.Invoke($"[PORTABLE_STORAGE] Synchronized replay '{fileName}' from '{sDir}' to canonical engine path: '{targetPath}'");
                        }
                    }
                }
            }

            // Mirror canonical replays back to local userdata folders so external tools find them
            string fallbackUserdataReplays = Path.Combine(userdataRoot, "game", "replays");
            string rootUserdataReplays = Path.Combine(userdataRoot, "replays");
            string[] canonicalFiles = Directory.GetFiles(canonicalReplaysDir, "*.honreplay");
            foreach (string cFile in canonicalFiles)
            {
                string fileName = Path.GetFileName(cFile);
                string dest1 = Path.Combine(fallbackUserdataReplays, fileName);
                if (!File.Exists(dest1) || new FileInfo(dest1).Length != new FileInfo(cFile).Length)
                {
                    File.Copy(cFile, dest1, true);
                    log?.Invoke($"[PORTABLE_STORAGE] Mirrored replay '{fileName}' to '{dest1}'");
                }
                string dest2 = Path.Combine(rootUserdataReplays, fileName);
                if (!File.Exists(dest2) || new FileInfo(dest2).Length != new FileInfo(cFile).Length)
                {
                    File.Copy(cFile, dest2, true);
                    log?.Invoke($"[PORTABLE_STORAGE] Mirrored replay '{fileName}' to '{dest2}'");
                }
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"[PORTABLE_STORAGE] Warning synchronizing replays: {ex.Message}");
        }

        string markerPath = Path.Combine(userdataRoot, "game", "shaders", ".cache_version_v3_dxvk_clean");
        try
        {
            if (!File.Exists(markerPath))
            {
                File.WriteAllText(markerPath, "v3_clean_shader_cache");
            }
        }
        catch { }

        // Self-healing shader and texture cache validation for modern KONGOR x64 engine
        try
        {
            string[] cacheDirs =
            [
                Path.Combine(userdataRoot, "game", "shaders"),
                Path.Combine(userdataKongor, "extensions", "shadercache"),
                Path.Combine(userdataKongor, "extensions", "texturecache")
            ];

            foreach (string cacheDir in cacheDirs)
            {
                if (Directory.Exists(cacheDir))
                {
                    // Ensure all XML descriptors in cache have no UTF-8 BOM which breaks K2 XML parser
                    foreach (string xmlFile in Directory.GetFiles(cacheDir, "*.xml"))
                    {
                        try
                        {
                            byte[] bytes = File.ReadAllBytes(xmlFile);
                            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                            {
                                File.WriteAllBytes(xmlFile, bytes[3..]);
                                log?.Invoke($"[PORTABLE_STORAGE] Stripped UTF-8 BOM from cache descriptor '{xmlFile}'.");
                            }
                        }
                        catch { }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"[PORTABLE_STORAGE] Note: Cache validation non-critical error: {ex.Message}");
        }
    }

    /// <summary>
    /// Writes canonical autoexec.cfg configuration into userdataKONGOR/configuration and KONGOR/configuration.
    /// </summary>
    public static void WriteAutoexecConfig(
        string gameDir,
        Action<string>? log = null,
        string refreshRate = "240",
        bool fullscreen = true,
        bool borderless = false,
        bool isHeadless = false,
        string? username = null,
        string? passwordMd5 = null,
        string? resolution = null)
    {
        string fullscreenVal = fullscreen ? "true" : "false";
        string borderlessVal = borderless ? "true" : "false";
        string effectiveUser = !string.IsNullOrWhiteSpace(username) ? username : ClientCredentialManager.PrimaryDefaultUsername;
        string effectivePassMd5 = !string.IsNullOrWhiteSpace(passwordMd5) ? passwordMd5 : ClientCredentialManager.DefaultPasswordMd5;
        string effectiveResolution = !string.IsNullOrWhiteSpace(resolution) ? resolution.Trim().Replace('x', ',') : "1920,1080";

        string cfgContent =
            $$"""
            // autoexec.cfg auto-generated by ProjectKongor MCP Service
            // load order: 1) startup.cfg, 2) login.cfg, 3) init.cfg, 4) components, 5) autoexec.cfg

            // Next-Gen High-Performance Engine & Latency Tuning
            SetSave "host_affinity" "-1"
            SetSave "vid_maxFPS" "1000"
            SetSave "cl_maxFPS" "1000"
            SetSave "host_maxFPS" "1000"
            SetSave "vid_refreshRate" "{{refreshRate}}"
            SetSave "vid_resolution" "{{effectiveResolution}}"
            SetSave "vid_bpp" "32"
            SetSave "vid_fullscreen" "{{fullscreenVal}}"
            SetSave "vid_borderless" "{{borderlessVal}}"
            SetSave "sys_sleepTime" "0"
            SetSave "mem_maxHeapSize" "4096"
            SetSave "gl_swapInterval" "0"
            SetSave "d3d_tripleBuffering" "false"
            SetSave "d3d_pure" "true"
            SetSave "vid_driver" "d3d9"
            SetSave "vid_textureCompression" "true"
            SetSave "vid_checkOpenGLVersion" "false"

            // Dynamic Soft Shadows & Foliage
            SetSave "vid_shadows" "true"
            SetSave "vid_terrainShadows" "true"
            SetSave "vid_shadowmapSize" "2048"
            SetSave "vid_shadowmapFilterWidth" "2"
            SetSave "vid_shadowmapType" "1"
            SetSave "vid_shadowDrawDistance" "2500.0000"
            SetSave "vid_shadowFalloffDistance" "800.0000"
            SetSave "vid_shadowLeak" "0.1000"
            SetSave "vid_foliageDensity" "1.0000"
            SetSave "vid_foliageFalloffDistance" "1000.0000"
            SetSave "vid_cullGroundSprites" "false"

            // Engine Shader Baseline Resets
            SetSave "vid_shaderFalloffQuality" "0"
            SetSave "vid_shaderFogQuality" "0"
            SetSave "vid_shaderLightingQuality" "0"
            SetSave "vid_shaderRimLighting" "true"
            SetSave "vid_shaderWaterQuality" "0"
            SetSave "vid_shaderAmbientOcclusion" "true"
            SetSave "vid_reflectionMapSize" "2048"
            SetSave "vid_treeSmoothNormals" "true"
            SetSave "vid_specularLookup" "false"
            SetSave "vid_meshQuality" "0"
            SetSave "vid_modelLOD" "0"
            SetSave "vid_textureFiltering" "3"
            SetSave "vid_antialiasing" "4"
            SetSave "cg_enableLoginAnimation" "false"
            SetSave "ui_disable3DBackground" "false"
            SetSave "ui_background" "0"

            // Low-Latency Audio Tuning (Ultra-Fast 5.3ms Mixer & 64-Voice Spatial Polyphony)
            SetSave "sound_numChannels" "64"
            SetSave "sound_bufferLength" "256"
            SetSave "sound_mixRate" "48000"
            SetSave "sound_recording_mixRate" "48000"

            // Ultra-Smooth 500 FPS Skeletal Animation Interpolation & Scene Prediction
            SetSave "skel_interpolate" "true"
            SetSave "skel_blendAnims" "true"
            SetSave "skel_skipKeyFrames" "false"
            SetSave "scene_interpolate" "true"

            // Competitive 128-Tick Network Prediction & Interpolation
            SetSave "net_maxPacketRate" "128"
            SetSave "cl_packetEpsilon" "0.001"
            SetSave "net_smoothInterpolation" "1"

            // Real-time Logging with flushing enabled for telemetry & rapid development
            SetSave "con_verbose" "false"
            SetSave "con_writeLog" "true"
            SetSave "con_appendLog" "true"
            SetSave "con_flushLog" "true"
            SetSave "sys_flushLogs" "true"
            SetSave "con_showNet" "false"
            SetSave "con_showDev" "false"
            SetSave "http_printDebugInfo" "false"
            SetSave "php_printDebugInfo" "false"
            SetSave "sys_dumpOnFatal" "true"

            // Real-Time Debugging & Notifications
            SetSave "con_notify" "false"
            SetSave "con_notifyLines" "0"
            SetSave "con_notifyTime" "0"

            // Accelerated Hero Mastery & Entity Database Loading
            SetSave "cg_heroLoadingChunkDurationMS" "1000"
            SetSave "svr_port" "11580"
            SetSave "svr_voicePortStart" "12080"
            SetSave "svr_voicePortEnd" "12080"
            // svr_proxyPort and man_startServerPort require [1024, 65535]; leaving engine defaults avoids range warnings
            SetSave "res_unloadHeroResources" "false"
            SetSave "res_unloadMapResources" "false"
            SetSave "cg_forceDefinitionReloadAll" "false"
            SetSave "fs_trackCustomFiles" "false"

            // console (CTRL+F8)
            SetSave "con_height" "0.85"
            SetSave "con_alpha" "0.85"
            SetSave "con_showNet" "false"

            // interface & developer mode
            SetSave "releaseStage_dev" "true"
            SetSave "releaseStage_test" "true"
            SetSave "releaseStage_stable" "false"
            SetSave "ui_dev" "true"
            SetSave "ui_dev_button" "true"
            SetSave "ui_showQuickStart" "true"
            SetSave "ui_canCreateGame" "true"
            SetSave "cg_24hourClock" "true"

            // Account Authentication & Auto-Login
            SetSave "chat_serverAddrOverride" "127.0.0.1"
            SetSave "chat_serverPortOverride" "11031"
            SetSave "login_rememberName" "true"
            SetSave "login_rememberPassword" "true"
            SetSave "login_autoLogin" "true"
            Script "if GetCvarString('login_name') == '' then Set('login_name', '{{effectiveUser}}') end"
            Script "if GetCvarString('login_password') == '' then Set('login_password', '{{effectivePassMd5}}') end"

            // TAO In-Engine Telemetry & JSON-RPC Runtime
            Script dofile('/ui/scripts/tao.lua')

            // Native Heartbeat Settings
            SetSave "cg_heartbeat_enabled" "true"
            SetSave "cg_heartbeat_freq" "80.0000"
            SetSave "cg_heartbeat_show_border" "true"
            SetSave "cg_heartbeat_show_circle" "true"
            SetSave "cg_heartbeat_sound" "true"
            SetSave "cg_heartbeat_threshold" "0.2000"
            """;

        string trackedCfgPath = Path.Combine(gameDir, "KONGOR", "configuration", "autoexec.cfg");
        string[] targets =
        [
            Path.Combine(gameDir, "userdataKONGOR", "configuration", "autoexec.cfg"),
            trackedCfgPath,
            Path.Combine(gameDir, "userdata", "Heroes of Newerth x64", "KONGOR", "extensions", "autoexec.cfg")
        ];

        foreach (string path in targets)
        {
            try
            {
                if (string.Equals(path, trackedCfgPath, StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(path) &&
                    File.ReadAllText(path, Encoding.UTF8).Contains("Zero-Knowledge", StringComparison.OrdinalIgnoreCase))
                {
                    log?.Invoke($"[CONFIG] Preserved git-tracked zero-knowledge '{path}'.");
                    continue;
                }

                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllText(path, cfgContent, Encoding.UTF8);
                log?.Invoke($"[CONFIG] Wrote autoexec.cfg to '{path}'.");
            }
            catch (Exception ex)
            {
                log?.Invoke($"[CONFIG ERROR] Failed to write autoexec.cfg to '{path}': {ex.Message}");
            }
        }

        // Pre-flight check: verify DotLocal redirection synchronously
        try
        {
            if (!VulkanDriverUpgradeService.CheckDotLocalRedirection(gameDir))
            {
                log?.Invoke("[VULKAN] DotLocal redirection missing. Ensuring DotLocal redirection markers...");
                VulkanDriverUpgradeService.EnsureDotLocalRedirection(gameDir, log);
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"[VULKAN ERROR] Pre-flight DotLocal check failed: {ex.Message}");
        }

        // Defensive sanitization: ensure startup.cfg across all layers does not force exclusive fullscreen or 500Hz
        SanitizeStartupConfigs(gameDir, log, fullscreen, isHeadless);
    }

    /// <summary>
    /// Traverses all configuration directories and sanitizes startup.cfg files to prevent fatal D3D_SetMode desktop crashes.
    /// </summary>
    public static void SanitizeStartupConfigs(string gameDir, Action<string>? log = null, bool fullscreen = false, bool isHeadless = false)
    {
        if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir)) return;

        try
        {
            var configFiles = Directory.EnumerateFiles(gameDir, "*startup.cfg*", SearchOption.AllDirectories);
            foreach (string file in configFiles)
            {
                try
                {
                    byte[] bytes = File.ReadAllBytes(file);
                    if (bytes.Length == 0) continue;

                    bool isUnicode = (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) ||
                                     (bytes.Length >= 2 && bytes[1] == 0x00 && bytes[0] != 0x00);
                    Encoding enc = isUnicode ? Encoding.Unicode : Encoding.UTF8;
                    string text = enc.GetString(bytes);

                    bool modified = false;

                    if (Regex.IsMatch(text, @"(?i)SetSave\s+""?vid_refreshRate""?\s+""?500""?"))
                    {
                        text = Regex.Replace(text, @"(?i)SetSave\s+""?vid_refreshRate""?\s+""?500""?(?:\s+""?\d+""?)?", "SetSave \"vid_refreshRate\" \"60\" \"0\"");
                        modified = true;
                    }

                    if (modified)
                    {
                        File.WriteAllText(file, text, enc);
                        log?.Invoke($"[CONFIG] Sanitized startup config '{file}' (vid_refreshRate=60).");
                    }
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"[CONFIG] Warning enumerating startup configs: {ex.Message}");
        }
    }

    /// <summary>
    /// Resolves executable file using search hierarchy.
    /// </summary>
    public static bool TryResolveGameExecutable(string? customPath, string? workingDir, [NotNullWhen(true)] out FileInfo? executable)
    {
        executable = null;

        // 1. Explicit Custom Path
        if (!string.IsNullOrWhiteSpace(customPath))
        {
            string expanded = Environment.ExpandEnvironmentVariables(customPath);
            FileInfo customFi = new(expanded);
            if (customFi.Exists)
            {
                executable = customFi;
                return true;
            }
            if (File.Exists(Path.GetFullPath(expanded)))
            {
                executable = new FileInfo(Path.GetFullPath(expanded));
                return true;
            }
        }

        string baseDir = workingDir ?? Environment.CurrentDirectory;
        string repoRoot = FindRepoRoot(baseDir);

        string[] candidateNames =
        [
            "WILLOWMAKER.exe",
            "WILLOWMAKER",
            "hon_x64.exe",
            "hon_x64",
            "hon.exe",
            "hon"
        ];

        string[] candidateDirectories =
        [
            baseDir,
            Path.Combine(repoRoot, "games", "client"),
            Path.Combine(repoRoot, "src", "apps", "willowmaker", "bin", "Debug", "net11.0"),
            Path.Combine(repoRoot, "src", "apps", "willowmaker", "bin", "Release", "net11.0"),
            Path.Combine(repoRoot, "src", "apps", "willowmaker", "bin", "Debug", "net10.0"),
            Path.Combine(repoRoot, "src", "apps", "willowmaker", "bin", "Release", "net10.0"),
            AppContext.BaseDirectory
        ];

        foreach (string dir in candidateDirectories)
        {
            if (!Directory.Exists(dir)) continue;

            foreach (string name in candidateNames)
            {
                string fullPath = Path.Combine(dir, name);
                if (File.Exists(fullPath))
                {
                    executable = new FileInfo(fullPath);
                    return true;
                }
            }
        }

        // Platform subdirectories check (windows-x64, etc.)
        string platformSubdir = OperatingSystem.IsWindows() ? "windows-x64"
            : OperatingSystem.IsLinux() ? "linux-x64"
            : OperatingSystem.IsMacOS() ? "macos-x64"
            : string.Empty;

        if (!string.IsNullOrEmpty(platformSubdir))
        {
            foreach (string dir in candidateDirectories)
            {
                string subPath = Path.Combine(dir, platformSubdir);
                if (!Directory.Exists(subPath)) continue;

                foreach (string name in candidateNames)
                {
                    string fullPath = Path.Combine(subPath, name);
                    if (File.Exists(fullPath))
                    {
                        executable = new FileInfo(fullPath);
                        return true;
                    }
                }
            }
        }

        // Fallback search in games/client
        string clientDir = Path.Combine(repoRoot, "games", "client");
        if (Directory.Exists(clientDir))
        {
            try
            {
                FileInfo[] files = new DirectoryInfo(clientDir).GetFiles("*.exe", SearchOption.AllDirectories);
                if (files.Length > 0)
                {
                    executable = files[0];
                    return true;
                }
            }
            catch { }
        }

        return false;
    }

    private static bool TryParseResolution(string res, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (string.IsNullOrWhiteSpace(res)) return false;

        string[] parts = res.ToLowerInvariant().Split('x');
        if (parts.Length == 2 && int.TryParse(parts[0].Trim(), out width) && int.TryParse(parts[1].Trim(), out height))
        {
            return width > 0 && height > 0;
        }

        return false;
    }

    private static Process? FindRunningGameProcess()
    {
        string[] targetNames = ["hon_x64", "WILLOWMAKER", "hon"];
        foreach (string name in targetNames)
        {
            try
            {
                Process[] procs = Process.GetProcessesByName(name);
                Process? match = procs.FirstOrDefault(p => p.Id != Environment.ProcessId && !p.HasExited);
                if (match != null) return match;
            }
            catch { }
        }

        return null;
    }

    private static string? TryGetProcessFileName(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    private static string FindRepoRoot(string startingDir)
    {
        DirectoryInfo? current = new(startingDir);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Directory.Build.props")) ||
                File.Exists(Path.Combine(current.FullName, "PROJECT.md")) ||
                File.Exists(Path.Combine(current.FullName, "ProjectKongor.slnx")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }

        return startingDir;
    }

    internal static string? ResolveVulkanLayerDirectory(string gameDir)
    {
        string repoRoot = FindRepoRoot(gameDir);
        string[] candidates =
        [
            gameDir,
            Path.Combine(gameDir, "windows-x64"),
            Path.Combine(repoRoot, "games", "client"),
            Path.Combine(repoRoot, "games", "client", "windows-x64"),
            Path.Combine(repoRoot, "native", "vulkan-layer", "build", "x64", "Release"),
            Path.Combine(repoRoot, "native", "vulkan-layer", "build", "Release")
        ];

        foreach (string candidate in candidates)
        {
            if (Directory.Exists(candidate))
            {
                string manifest = Path.Combine(candidate, "VkLayer_hon_replay_capture.json");
                string dll = Path.Combine(candidate, "VkLayer_hon_replay_capture.dll");
                if (File.Exists(manifest) && File.Exists(dll))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Configures HON_MODERN_HUD, HON_DEVTOOLS, and KONGOR_HEADLESS consistently across all Windows
    /// game launchers so foreground sessions always initialize RmlUi (Ctrl+Shift+D) while headless
    /// test sessions stay lean unless explicitly overridden via ambient environment variables.
    /// </summary>
    public static void ConfigureVulkanOverlayEnvironment(
        IDictionary<string, string> env,
        bool isHeadless,
        string? ambientModernHud = null,
        string? ambientDevTools = null)
    {
        ArgumentNullException.ThrowIfNull(env);

        string resolvedHud = !string.IsNullOrEmpty(ambientModernHud)
            ? ambientModernHud
            : (isHeadless ? "0" : "1");
        string resolvedDevTools = !string.IsNullOrEmpty(ambientDevTools)
            ? ambientDevTools
            : (isHeadless ? "0" : "1");

        env["KONGOR_HEADLESS"] = isHeadless ? "1" : "0";
        env["HON_MODERN_HUD"] = resolvedHud;
        env["HON_DEVTOOLS"] = resolvedDevTools;
    }

    /// <summary>
    /// Overload for <see cref="System.Collections.Specialized.StringDictionary"/> (<see cref="ProcessStartInfo.EnvironmentVariables"/>).
    /// </summary>
    public static void ConfigureVulkanOverlayEnvironment(
        System.Collections.Specialized.StringDictionary env,
        bool isHeadless,
        string? ambientModernHud = null,
        string? ambientDevTools = null)
    {
        ArgumentNullException.ThrowIfNull(env);

        string resolvedHud = !string.IsNullOrEmpty(ambientModernHud)
            ? ambientModernHud
            : (isHeadless ? "0" : "1");
        string resolvedDevTools = !string.IsNullOrEmpty(ambientDevTools)
            ? ambientDevTools
            : (isHeadless ? "0" : "1");

        env["KONGOR_HEADLESS"] = isHeadless ? "1" : "0";
        env["HON_MODERN_HUD"] = resolvedHud;
        env["HON_DEVTOOLS"] = resolvedDevTools;
    }
}
