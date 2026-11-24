// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// MCP Tool Handler for 'kongor_match_launch' (alias: 'kongor_practice_launch').
/// Programmatically launches the Project Kongor game client (hon_x64.exe) directly into a practice match
/// or bot match on Caldavar (or custom map) with pre-selected hero, bot count, developer cheats,
/// and optional headless mode without requiring manual UI menu navigation.
/// Supports IPC match monitoring, auto-hero selection, and rapid lifecycle termination guardrails.
/// </summary>
public sealed class MatchLaunchHandler
{
    internal sealed record ClientProcessCandidate(
        int ProcessId,
        IntPtr MainWindowHandle,
        string? CommandLine,
        Action KillTree,
        Func<int, bool> WaitForExit,
        Func<bool> IsAlive,
        long WorkingSetBytes = 0,
        bool Responding = true);

    private readonly Lock _lock = new();
    private Process? _trackedProcess;
    private string? _lastExecutable;
    private string? _lastWorkingDirectory;
    private IReadOnlyList<string> _lastRedactedArguments = []; // Diagnostic only; never replay into ProcessStartInfo
    private string _lastMap = "caldavar";
    private string _lastHero = "Hero_Pyromancer";
    private string _lastMode = "practice";

    private readonly IGameIpcBridge _ipcBridge;
    private readonly GameClientManageHandler? _clientManageHandler;
    private readonly IUiControlService _uiControlService;
    private readonly IClientProcessRegistry _clientRegistry;
    private readonly Func<IReadOnlyList<ClientProcessCandidate>>? _candidateEnumerator;
    private readonly Action<IntPtr> _windowOffscreenRepositioner;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string> _logger;

    public MatchLaunchHandler(
        IGameIpcBridge? ipcBridge = null,
        GameClientManageHandler? clientManageHandler = null,
        IUiControlService? uiControlService = null,
        TimeProvider? timeProvider = null,
        Action<string>? logger = null,
        IClientProcessRegistry? clientRegistry = null)
        : this(ipcBridge, clientManageHandler, uiControlService, timeProvider, logger, clientRegistry, null, null)
    {
    }

    internal MatchLaunchHandler(
        IGameIpcBridge? ipcBridge,
        GameClientManageHandler? clientManageHandler,
        IUiControlService? uiControlService,
        TimeProvider? timeProvider,
        Action<string>? logger,
        IClientProcessRegistry? clientRegistry,
        Func<IReadOnlyList<ClientProcessCandidate>>? candidateEnumerator,
        Action<IntPtr>? windowOffscreenRepositioner)
    {
        _ipcBridge = ipcBridge ?? new GameIpcBridge();
        _clientManageHandler = clientManageHandler;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _uiControlService = uiControlService ?? new UiControlService(timeProvider: _timeProvider);
        _clientRegistry = clientRegistry ?? ClientProcessRegistry.Default;
        _candidateEnumerator = candidateEnumerator;
        _windowOffscreenRepositioner = windowOffscreenRepositioner ?? (hwnd => _ = GameClientManageHandler.SetClientWindowHeadless(hwnd));
        _logger = logger ?? (msg => Console.Error.WriteLine($"[{_timeProvider.GetUtcNow():yyyy-MM-ddTHH:mm:ss.fffZ}] [MatchLaunchHandler] {msg}"));
    }

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        string action = "launch";
        if (arguments.TryGetProperty("action", out JsonElement actElem) && !string.IsNullOrWhiteSpace(actElem.GetString()))
        {
            action = actElem.GetString()!.Trim().ToLowerInvariant();
        }

        string map = arguments.TryGetProperty("map", out JsonElement mapElem) && !string.IsNullOrWhiteSpace(mapElem.GetString())
            ? mapElem.GetString()!.Trim()
            : "caldavar";

        string hero = arguments.TryGetProperty("hero", out JsonElement heroElem) && !string.IsNullOrWhiteSpace(heroElem.GetString())
            ? heroElem.GetString()!.Trim()
            : "Hero_Pyromancer";

        string mode = arguments.TryGetProperty("mode", out JsonElement modeElem) && !string.IsNullOrWhiteSpace(modeElem.GetString())
            ? modeElem.GetString()!.Trim().ToLowerInvariant()
            : "practice";

        int bots = arguments.TryGetProperty("bots", out JsonElement botsElem) && botsElem.TryGetInt32(out int bVal) && bVal >= 0
            ? bVal
            : 0;

        bool cheats = !arguments.TryGetProperty("cheats", out JsonElement chElem) || chElem.GetBoolean();
        bool headless = arguments.TryGetProperty("headless", out JsonElement hlElem) && hlElem.GetBoolean();

        string resolution = arguments.TryGetProperty("resolution", out JsonElement resElem) && !string.IsNullOrWhiteSpace(resElem.GetString())
            ? resElem.GetString()!.Trim()
            : "1920x1080";

        bool fullscreen = !arguments.TryGetProperty("fullscreen", out JsonElement fsElem) || fsElem.GetBoolean();

        int maxFps = arguments.TryGetProperty("maxFps", out JsonElement fpsElem) && fpsElem.TryGetInt32(out int fpsVal) && fpsVal > 0
            ? fpsVal
            : 1000;

        bool useLauncherSettings = (arguments.TryGetProperty("useLauncherSettings", out JsonElement ulsEl) ||
                                    arguments.TryGetProperty("use_launcher_settings", out ulsEl)) &&
                                   (ulsEl.ValueKind == JsonValueKind.True || (ulsEl.ValueKind == JsonValueKind.String && bool.TryParse(ulsEl.GetString(), out bool ulsB) && ulsB));

        string masterServer = arguments.TryGetProperty("masterServer", out JsonElement msElem) && !string.IsNullOrWhiteSpace(msElem.GetString())
            ? msElem.GetString()!.Trim()
            : GameLauncherService.ResolveDefaultMasterServer(useLauncherSettings: useLauncherSettings);

        string? customClientPath = arguments.TryGetProperty("customClientPath", out JsonElement ccpElem) && !string.IsNullOrWhiteSpace(ccpElem.GetString())
            ? ccpElem.GetString()!.Trim()
            : null;

        string? workingDirectory = arguments.TryGetProperty("workingDirectory", out JsonElement wdElem) && !string.IsNullOrWhiteSpace(wdElem.GetString())
            ? wdElem.GetString()!.Trim()
            : null;

        bool autoSelectHero = !arguments.TryGetProperty("autoSelectHero", out JsonElement ashElem) || ashElem.GetBoolean();
        bool rapidTerminate = arguments.TryGetProperty("rapidTerminateAfterLaunch", out JsonElement rtElem) && rtElem.GetBoolean();
        bool adoptExisting = (arguments.TryGetProperty("adoptExisting", out JsonElement aeElem) ||
                              arguments.TryGetProperty("adopt_existing", out aeElem)) &&
                             (aeElem.ValueKind == JsonValueKind.True || (aeElem.ValueKind == JsonValueKind.String && bool.TryParse(aeElem.GetString(), out bool aeB) && aeB));
        bool repositionExisting = (arguments.TryGetProperty("repositionExisting", out JsonElement reElem) ||
                                   arguments.TryGetProperty("reposition_existing", out reElem)) &&
                                  (reElem.ValueKind == JsonValueKind.True || (reElem.ValueKind == JsonValueKind.String && bool.TryParse(reElem.GetString(), out bool reB) && reB));

        int tickrate = ClientModMountBuilder.CanonicalSimulationTickRate;
        if (arguments.TryGetProperty("tickrate", out JsonElement tickElem) || arguments.TryGetProperty("tickRate", out tickElem))
        {
            if (tickElem.ValueKind == JsonValueKind.Number && tickElem.TryGetInt32(out int tr))
            {
                tickrate = Math.Clamp(tr, 20, 60);
            }
            else if (tickElem.ValueKind == JsonValueKind.String && int.TryParse(tickElem.GetString(), out int trParsed))
            {
                tickrate = Math.Clamp(trParsed, 20, 60);
            }
        }

        if (tickrate != ClientModMountBuilder.CanonicalSimulationTickRate)
        {
            _logger($"[WARN] Non-canonical simulation tickrate ({tickrate} Hz != 20 Hz) requested: IEntityState Phase-1 expiration and 20 Hz <onframe> scripts may malfunction.");
        }

        _logger($"Handling action '{action}': Map='{map}', Hero='{hero}', Mode='{mode}', Bots={bots}, Cheats={cheats}, Headless={headless}, Tickrate={tickrate} Hz, RapidTerminate={rapidTerminate}");

        try
        {
            switch (action)
            {
                case "launch":
                {
                    return await LaunchMatchAsync(
                        map, hero, mode, bots, cheats, headless,
                        resolution, fullscreen, maxFps, masterServer,
                        customClientPath, workingDirectory, autoSelectHero, rapidTerminate,
                        tickrate, adoptExisting, repositionExisting,
                        cancellationToken).ConfigureAwait(false);
                }

                case "stop":
                {
                    bool killAll = arguments.ValueKind == JsonValueKind.Object &&
                                   ((arguments.TryGetProperty("all", out var allEl) && allEl.ValueKind == JsonValueKind.True) ||
                                    (arguments.TryGetProperty("killAll", out var kaEl) && kaEl.ValueKind == JsonValueKind.True));
                    return StopMatch(killAll);
                }

                case "restart":
                {
                    _logger("Restart requested: stopping existing match instance first...");
                    StopMatch(killAll: false);
                    await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                    return await LaunchMatchAsync(
                        map, hero, mode, bots, cheats, headless,
                        resolution, fullscreen, maxFps, masterServer,
                        customClientPath, workingDirectory, autoSelectHero, rapidTerminate,
                        tickrate, adoptExisting, repositionExisting,
                        cancellationToken).ConfigureAwait(false);
                }

                case "status":
                default:
                {
                    return GetMatchStatus();
                }
            }
        }
        catch (Exception ex)
        {
            _logger($"[ERROR] Exception executing match launch action '{action}': {ex}");
            await (McpProgressContext.Current?.ReportProgressAsync(1.0, 1.0, $"Failed executing match launch action '{action}': {ex.Message}", cancellationToken) ?? Task.CompletedTask);
            return ToolCallResult.Error($"Failed executing match launch action '{action}': {ex.Message}");
        }
    }

    private async Task<ToolCallResult> LaunchMatchAsync(
        string map,
        string hero,
        string mode,
        int bots,
        bool cheats,
        bool headless,
        string resolution,
        bool fullscreen,
        int maxFps,
        string masterServer,
        string? customClientPath,
        string? customWorkingDirectory,
        bool autoSelectHero,
        bool rapidTerminate,
        int tickrate,
        bool adoptExisting,
        bool repositionExisting,
        CancellationToken cancellationToken)
    {
        int clampedTickrate = Math.Clamp(tickrate, 20, 60);
        await (McpProgressContext.Current?.ReportProgressAsync(0.2, 1.0, $"Preparing match launch on '{map}' ({mode}, {clampedTickrate} Hz)...", cancellationToken) ?? Task.CompletedTask);
        _logger($"Resolving game client binary for match launch (Tickrate: {clampedTickrate} Hz)...");

        int reusedPid = 0;
        IntPtr reusedHwnd = IntPtr.Zero;
        bool reusedIsMcpManaged = false;

        Process? directManagedProcess = null;
        lock (_lock)
        {
            if (_trackedProcess != null && !_trackedProcess.HasExited)
            {
                directManagedProcess = _trackedProcess;
            }
        }

        if (directManagedProcess == null && !GameServerManageHandler.IsTestExecution)
        {
            directManagedProcess = _clientManageHandler?.GetRunningClientProcess();
        }

        if (directManagedProcess != null && !directManagedProcess.HasExited)
        {
            reusedPid = directManagedProcess.Id;
            reusedHwnd = directManagedProcess.MainWindowHandle;
            reusedIsMcpManaged = true;
        }
        else if (_candidateEnumerator != null || !GameServerManageHandler.IsTestExecution)
        {
            IReadOnlyList<ClientProcessCandidate> candidates = EnumerateClientCandidates();
            ClientProcessCandidate? managedCandidate = candidates.FirstOrDefault(c => _clientRegistry.IsManagedClient(c.ProcessId));
            if (managedCandidate != null)
            {
                reusedPid = managedCandidate.ProcessId;
                reusedHwnd = managedCandidate.MainWindowHandle;
                reusedIsMcpManaged = true;
            }
            else if (candidates.Count > 0)
            {
                ClientProcessCandidate foreign = candidates[0];
                if (!adoptExisting)
                {
                    string foreignMsg =
                        $"Foreign (un-owned) game client process detected (PID {foreign.ProcessId}) that was not launched by TAO MCP. " +
                        "Refusing to hijack or reposition your personal game session (MCP-297). " +
                        "Pass 'adoptExisting: true' to dispatch into the running client without moving its window, or close the personal client first.";
                    _logger($"[WARN] {foreignMsg}");
                    return ToolCallResult.Json(new
                    {
                        status = "error",
                        ok = false,
                        action = "launch",
                        running = false,
                        errorCode = "foreign_client_running",
                        foreignProcessId = foreign.ProcessId,
                        error = foreignMsg,
                        message = foreignMsg,
                        remediation_call = new
                        {
                            tool = "kongor_match_launch",
                            arguments = new
                            {
                                action = "launch",
                                map,
                                hero,
                                mode,
                                adoptExisting = true
                            }
                        },
                        timestamp = _timeProvider.GetUtcNow().ToString("o")
                    }, isError: true);
                }

                reusedPid = foreign.ProcessId;
                reusedHwnd = foreign.MainWindowHandle;
                reusedIsMcpManaged = false;
            }
        }

        if (reusedPid > 0 && !rapidTerminate)
        {
            bool shouldRepositionOffscreen = headless && reusedHwnd != IntPtr.Zero &&
                                             (repositionExisting ||
                                              (reusedIsMcpManaged && _clientRegistry.GetClient(reusedPid)?.WindowMode is "headless" or "offscreen"));
            if (shouldRepositionOffscreen)
            {
                _windowOffscreenRepositioner(reusedHwnd);
                _logger($"Existing client window 0x{reusedHwnd.ToInt64():X8} repositioned offscreen (headless mode).");
            }

            await (McpProgressContext.Current?.ReportProgressAsync(0.6, 1.0, $"Dispatching match launch commands to running client PID {reusedPid}...", cancellationToken) ?? Task.CompletedTask);
            _logger($"Game client is already running (PID {reusedPid}). Dispatching match launch commands directly into running instance...");

            string cleanMap = string.IsNullOrWhiteSpace(map) ? "caldavar" : map.Trim();
            string cleanHero = string.IsNullOrWhiteSpace(hero) ? "Hero_Pyromancer" : hero.Trim();
            string cleanMode = string.IsNullOrWhiteSpace(mode) ? "practice" : mode.Trim();
            bool isBotMatch = cleanMode.Equals("botmatch", StringComparison.OrdinalIgnoreCase);
            string precacheFlag = isBotMatch ? "" : "Set sv_precacheEntities false; ";

            string startCmd = $"Set sv_heroSelectTime 0; Set sv_heroSelectInitialTime 0; Set sv_preMatchTime 0; Set sv_matchStartAnnounceTime 0; Set sv_gameStartCountdown 0; Set sv_gameCountdownLength 0; Set svr_login false; Set svr_broadcastLocal false; Set svr_gameFPS {clampedTickrate}; Set svr_maxFramesPerHostFrame 5; {precacheFlag}StartGame {cleanMode} PracticeGame map:{cleanMap} allheroes:true";
            _logger($"Dispatching '{startCmd}' to running client...");

            string dispatchResult = "";
            bool eventAwaited = false;

            // Priority Channel: In-Engine Nexus RPC (Zero UI Click / Delay)
            if (_ipcBridge != null && _ipcBridge.IsConnected)
            {
                try
                {
                    await _ipcBridge.ExecuteRpcAsync<object>("Nexus.Match.StartPractice", new
                    {
                        map = cleanMap,
                        hero = cleanHero,
                        mode = cleanMode,
                        cheats = true,
                        tickrate = clampedTickrate
                    }, 5000, cancellationToken).ConfigureAwait(false);

                    dispatchResult = "dispatched_via_nexus_rpc";

                    // Await in-engine MATCH_READY event
                    try
                    {
                        await _ipcBridge.WaitForEventAsync("MATCH_READY", 3000, cancellationToken).ConfigureAwait(false);
                        eventAwaited = true;
                    }
                    catch
                    {
                        // Proceed even if event wait timed out; match was initiated
                    }
                }
                catch (Exception ex)
                {
                    _logger($"[WARN] Nexus.Match.StartPractice RPC failed ({ex.Message}), falling back to console pipeline...");
                    dispatchResult = "";
                }
            }

            if (string.IsNullOrEmpty(dispatchResult))
            {
                if (_clientManageHandler != null)
                {
                    await _clientManageHandler.ExecuteClientCommandAsync(startCmd, 5000, cancellationToken).ConfigureAwait(false);
                    dispatchResult = "dispatched_via_client_manage";
                }
                else if (!headless)
                {
                    var r1 = await _uiControlService.ExecuteInGameConsoleCommandAsync("hon_x64", startCmd, cancellationToken).ConfigureAwait(false);
                    dispatchResult = r1.Success ? "dispatched_via_in_game_console" : $"failed: {r1.ErrorMessage}";
                }
                else
                {
                    dispatchResult = "suppressed_keystroke_fallback_in_headless_mode";
                }
            }

            await (McpProgressContext.Current?.ReportProgressAsync(1.0, 1.0, $"Match launch completed for '{cleanHero}' on '{cleanMap}' ({clampedTickrate} Hz).", cancellationToken) ?? Task.CompletedTask);

            return ToolCallResult.Json(new
            {
                status = "success",
                action = "launch",
                running = true,
                mode = cleanMode,
                map = cleanMap,
                hero = cleanHero,
                tickrate = clampedTickrate,
                processId = reusedPid,
                channel = dispatchResult,
                eventAwaited,
                message = $"Successfully dispatched practice match launch on '{cleanMap}' ({clampedTickrate} Hz) to running client (PID {reusedPid}).",
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            });
        }

        await (McpProgressContext.Current?.ReportProgressAsync(0.5, 1.0, "Resolving game client binary for match launch...", cancellationToken) ?? Task.CompletedTask);

        if (!GameClientManageHandler.TryResolveClientExecutable(customClientPath, customWorkingDirectory, out FileInfo? executable))
        {
            string err = $"Unable to locate hon_x64.exe for match launch. Checked 'games/client/hon_x64.exe' and custom path '{customClientPath ?? "none"}'.";
            _logger($"[ERROR] {err}");
            await (McpProgressContext.Current?.ReportProgressAsync(1.0, 1.0, $"Match launch aborted: client executable not found.", cancellationToken) ?? Task.CompletedTask);
            return ToolCallResult.Json(new
            {
                status = "error",
                action = "launch",
                running = false,
                errorMessage = err,
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            });
        }

        string gameDir = customWorkingDirectory ?? executable.DirectoryName ?? Environment.CurrentDirectory;
        _logger($"Resolved game client: '{executable.FullName}', working directory: '{gameDir}'.");

        // Initialize user directory and configuration (skip during unit tests to avoid mutating repository autoexec.cfg)
        if (!GameServerManageHandler.IsTestExecution)
        {
            GameLauncherService.EnsureUserDirectoryInitialized(gameDir, _logger);
            GameLauncherService.WriteAutoexecConfig(
                gameDir,
                _logger,
                refreshRate: headless ? "60" : "240",
                fullscreen: headless ? false : fullscreen,
                borderless: headless ? true : !fullscreen,
                isHeadless: headless);
        }

        // Construct match launch arguments
        IReadOnlyList<string> arguments = BuildMatchLaunchArguments(
            map, hero, mode, bots, cheats, headless,
            resolution, fullscreen, maxFps, masterServer, clampedTickrate);
        IReadOnlyList<string> redactedArguments = GameClientManageHandler.RedactLaunchArguments(arguments);

        string joinedArgs = string.Join(" ", arguments);
        _logger($"Constructed match launch arguments: {string.Join(" ", redactedArguments)}");

        if (GameServerManageHandler.IsTestExecution)
        {
            _logger($"[TEST ENVIRONMENT DETECTED] Simulating match launch ({clampedTickrate} Hz) without spawning real desktop GUI window.");
            int simulatedPid = Environment.ProcessId;
            return ToolCallResult.Json(new
            {
                status = rapidTerminate ? "rapid_terminated" : "success",
                action = "launch",
                running = !rapidTerminate,
                rapidTerminated = rapidTerminate,
                processId = simulatedPid,
                mainWindowHandle = "0x00010001",
                map,
                hero,
                mode,
                bots,
                cheats,
                headless,
                tickrate = clampedTickrate,
                heroSelection = "simulated",
                executable = executable.FullName,
                workingDirectory = gameDir,
                arguments = redactedArguments,
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            });
        }

        if (headless && Directory.Exists(gameDir))
        {
            Services.ClientModMountBuilder.EnsureHeadlessStartupCfgSeeded(gameDir, _logger);
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = executable.FullName,
            WorkingDirectory = gameDir,
            Arguments = joinedArgs,
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            CreateNoWindow = headless
        };

        Process process = new() { StartInfo = startInfo, EnableRaisingEvents = false };

        await (McpProgressContext.Current?.ReportProgressAsync(0.7, 1.0, $"Launching game client process for '{hero}' on '{map}'...", cancellationToken) ?? Task.CompletedTask);

        Dictionary<string, string> envDict = [];
        if (OperatingSystem.IsWindows())
        {
            string? layerDir = GameLauncherService.ResolveVulkanLayerDirectory(gameDir);
            if (!string.IsNullOrEmpty(layerDir))
            {
                string existingLayerPath = Environment.GetEnvironmentVariable("VK_LAYER_PATH") ?? string.Empty;
                string layerPathStr = string.IsNullOrEmpty(existingLayerPath)
                    ? $"{layerDir};{gameDir}"
                    : $"{layerDir};{gameDir};{existingLayerPath}";
                envDict["VK_LAYER_PATH"] = layerPathStr;
                envDict["VK_ADD_LAYER_PATH"] = layerPathStr;
                envDict["VK_INSTANCE_LAYERS"] = "VK_LAYER_HON_replay_capture";
                envDict["VK_LOADER_LAYERS_ENABLE"] = "*replay_capture*,VK_LAYER_HON_replay_capture";
                string? capEnv = Environment.GetEnvironmentVariable("HON_CAPTURE_ENABLE");
                envDict["HON_CAPTURE_ENABLE"] = !string.IsNullOrEmpty(capEnv) ? capEnv : "1";
                envDict["HON_CAPTURE_SLOTS"] = "3";
                envDict["HON_REPLAY_FPS"] = "60";
                envDict["HON_REPLAY_WIDTH"] = "1280";
                envDict["HON_REPLAY_HEIGHT"] = "720";
                GameLauncherService.ConfigureVulkanOverlayEnvironment(
                    envDict,
                    headless,
                    Environment.GetEnvironmentVariable("HON_MODERN_HUD"),
                    Environment.GetEnvironmentVariable("HON_DEVTOOLS"));
                _logger($"[MatchLaunch] Configured Vulkan Swapchain Layer: VK_LAYER_PATH='{layerDir}', HON_MODERN_HUD='{envDict["HON_MODERN_HUD"]}', HON_DEVTOOLS='{envDict["HON_DEVTOOLS"]}', KONGOR_HEADLESS='{envDict["KONGOR_HEADLESS"]}'");
            }
        }

        Process? startedProc = null;
        try
        {
            startedProc = InteractiveProcessLauncher.StartInteractiveProcess(
                executable.FullName,
                joinedArgs,
                gameDir,
                headless: headless,
                stealFocus: false,
                environmentVariables: envDict);
        }
        catch (Exception ex)
        {
            string err = $"Failed to start match process '{executable.FullName}': {ex.Message}";
            _logger($"[ERROR] {err}");
            await (McpProgressContext.Current?.ReportProgressAsync(1.0, 1.0, $"Failed to start match process: {ex.Message}", cancellationToken) ?? Task.CompletedTask);
            return ToolCallResult.Json(new
            {
                status = "error",
                action = "launch",
                running = false,
                errorMessage = err,
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            });
        }

        if (startedProc == null)
        {
            string err = $"Process.Start returned null for '{executable.FullName}'.";
            _logger($"[ERROR] {err}");
            await (McpProgressContext.Current?.ReportProgressAsync(1.0, 1.0, err, cancellationToken) ?? Task.CompletedTask);
            return ToolCallResult.Json(new
            {
                status = "error",
                action = "launch",
                running = false,
                errorMessage = err,
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            });
        }

        process = startedProc;

        lock (_lock)
        {
            _trackedProcess = process;
            _lastExecutable = executable.FullName;
            _lastWorkingDirectory = gameDir;
            _lastRedactedArguments = redactedArguments;
            _lastMap = map;
            _lastHero = hero;
            _lastMode = mode;
        }

        // Runtime supervision: Unified in version.dll; only inject Kongor.NativeGuard.dll as fallback if version.dll proxy is absent
        if (OperatingSystem.IsWindows())
        {
            try
            {
                bool versionProxyPresent = File.Exists(Path.Combine(gameDir, "version.dll")) ||
                                           File.Exists(Path.Combine(gameDir, "windows-x64", "version.dll"));
                if (versionProxyPresent)
                {
                    _logger($"[NativeProcessInjection] Unified version.dll supervisor active for hon_x64.exe PID {process.Id}; skipping redundant Kongor.NativeGuard.dll injection.");
                }
                else
                {
                    string guardPath = Path.Combine(gameDir, "Kongor.NativeGuard.dll");
                    if (!File.Exists(guardPath))
                    {
                        guardPath = Path.Combine(AppContext.BaseDirectory, "Kongor.NativeGuard.dll");
                    }
                    if (!File.Exists(guardPath))
                    {
                        guardPath = Path.Combine(AppContext.BaseDirectory, "windows-x64", "Kongor.NativeGuard.dll");
                    }
                    if (!File.Exists(guardPath))
                    {
                        guardPath = Path.Combine(gameDir, "windows-x64", "Kongor.NativeGuard.dll");
                    }

                    if (File.Exists(guardPath))
                    {
                        Kongor.Core.Services.Native.InjectionResult injectResult =
                            Kongor.Core.Services.Native.NativeProcessInjectionService.InjectDll(process.Handle, guardPath);
                        if (injectResult.Success)
                        {
                            _logger($"[NativeProcessInjection] Injected fallback Kongor.NativeGuard.dll into hon_x64.exe PID {process.Id} (Base: 0x{injectResult.ModuleBase:X8}) for accelerated VFS loading.");
                        }
                        else
                        {
                            _logger($"[NativeProcessInjection] Injection warning for PID {process.Id}: {injectResult.ErrorMessage}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger($"[NativeProcessInjection] Injection exception for PID {process.Id}: {ex.Message}");
            }
        }

        // Wait up to 3 seconds for MainWindowHandle or initialization
        int waitMs = 0;
        const int maxWait = 3000;
        IntPtr detectedHwnd = IntPtr.Zero;
        while (!process.HasExited && detectedHwnd == IntPtr.Zero && waitMs < maxWait)
        {
            try { process.Refresh(); } catch (Exception refEx) { _logger($"[DEBUG] Process refresh during HWND poll: {refEx.Message}"); }
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
                _logger($"Match launched successfully! PID: {pid}, MainWindowHandle: {hwndStr}");
                _clientRegistry.RegisterClient(new ClientInstance
                {
                    ProcessId = pid,
                    MainWindowHandle = detectedHwnd,
                    SlotIndex = 0,
                    OffscreenX = headless ? -32000 : 0,
                    OffscreenY = headless ? -32000 : 0,
                    WindowMode = headless ? "headless" : "windowed",
                    Username = "MatchLaunch",
                    LaunchedAt = _timeProvider.GetUtcNow(),
                    ProcessStartTimeTicks = ClientProcessRegistry.SafeGetStartTimeTicks(process),
                    Process = process
                });

                if (headless)
                {
                    Environment.SetEnvironmentVariable("KONGOR_HEADLESS", "1");
                    GameClientManageHandler.HideAllProcessWindows(process.Id);
                    if (detectedHwnd != IntPtr.Zero)
                    {
                        GameClientManageHandler.SetClientWindowHeadless(detectedHwnd);
                        _logger($"Positioned match client window {hwndStr} offscreen (headless mode).");
                    }
                    _ = Task.Run(async () =>
                    {
                        for (int i = 0; i < 75; i++)
                        {
                            await Task.Delay(200).ConfigureAwait(false);
                            if (process == null || process.HasExited) break;
                            try
                            {
                                process.Refresh();
                                GameClientManageHandler.HideAllProcessWindows(process.Id);
                                IntPtr bgHwnd = process.MainWindowHandle != IntPtr.Zero
                                    ? process.MainWindowHandle
                                    : WindowCapturer.FindWindowForProcessId(process.Id);
                                if (bgHwnd != IntPtr.Zero)
                                {
                                    GameClientManageHandler.SetClientWindowHeadless(bgHwnd);
                                }
                            }
                            catch (Exception bgEx)
                            {
                                _logger($"[DEBUG] Headless window monitor iteration ignored error: {bgEx.Message}");
                            }
                        }
                    }, CancellationToken.None);
                }
            }
            else
            {
                _logger($"Match process exited prematurely with exit code: {process.ExitCode}");
            }
        }
        catch (Exception ex)
        {
            _logger($"[WARN] Could not retrieve process state: {ex.Message}");
        }

        // IPC match monitoring and hero selection if client is running
        string heroSelectStatus = "skipped";
        if (isRunning && autoSelectHero && !rapidTerminate)
        {
            _logger($"Monitoring match loading over IPC to select hero '{hero}'...");
            try
            {
                if (!_ipcBridge.IsConnected)
                {
                    await _ipcBridge.ConnectAsync(2000, cancellationToken).ConfigureAwait(false);
                }

                if (_ipcBridge.IsConnected)
                {
                    // Dispatch hero selection command (SpawnHero for picker phase, Morph for in-game)
                    string cmd = $"SpawnHero {hero}; SelectHero {hero}; Morph 0 {hero}";
                    _logger($"Dispatching hero selection command via IPC: '{cmd}'");
                    string resp = await _ipcBridge.ExecuteCommandAsync(cmd, 2500, cancellationToken).ConfigureAwait(false);
                    heroSelectStatus = $"dispatched: {resp}";
                }
            }
            catch (Exception ex)
            {
                _logger($"[WARN] Hero selection via IPC encountered warning: {ex.Message}");
                heroSelectStatus = $"pending: {ex.Message}";
            }
        }

        // Process lifecycle guardrail: rapid termination support
        bool rapidTerminated = false;
        if (rapidTerminate && isRunning)
        {
            _logger("Lifecycle Guardrail: Rapid termination requested. Verifying process startup and terminating cleanly within 2 seconds...");
            await Task.Delay(1200, cancellationToken).ConfigureAwait(false);
            StopMatch();
            rapidTerminated = true;
            isRunning = false;
            _logger("Lifecycle Guardrail: Rapid termination completed successfully.");
        }

        var response = new
        {
            status = rapidTerminated ? "rapid_terminated" : (isRunning ? "success" : "exited"),
            action = "launch",
            running = isRunning,
            rapidTerminated,
            processId = pid,
            mainWindowHandle = hwndStr,
            map,
            hero,
            mode,
            bots,
            cheats,
            headless,
            tickrate = clampedTickrate,
            heroSelection = heroSelectStatus,
            executable = executable.FullName,
            workingDirectory = gameDir,
            arguments = redactedArguments,
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        };

        await (McpProgressContext.Current?.ReportProgressAsync(1.0, 1.0, $"Match launch completed for '{hero}' on '{map}' ({clampedTickrate} Hz).", cancellationToken) ?? Task.CompletedTask);

        return ToolCallResult.Json(response);
    }

    private IReadOnlyList<ClientProcessCandidate> EnumerateClientCandidates()
    {
        if (_candidateEnumerator != null)
        {
            return _candidateEnumerator();
        }

        List<ClientProcessCandidate> candidates = [];
        HashSet<int> seenPids = [];

        foreach (string name in new[] { "hon_x64", "hon" })
        {
            try
            {
                Process[] procs = Process.GetProcessesByName(name);
                foreach (Process p in procs)
                {
                    try
                    {
                        if (p.Id == Environment.ProcessId || p.HasExited || !seenPids.Add(p.Id))
                        {
                            continue;
                        }

                        string? cmd = GameClientManageHandler.TryGetProcessCommandLine(p.Id);
                        if (cmd != null && cmd.Contains("-dedicated", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        int capturedPid = p.Id;
                        IntPtr capturedHwnd = p.MainWindowHandle;
                        long ws = 0;
                        bool resp = true;
                        try
                        {
                            ws = p.WorkingSet64;
                            resp = p.Responding;
                        }
                        catch (Exception statEx)
                        {
                            _logger($"[DEBUG] Unable to read WorkingSet/Responding for PID {capturedPid}: {statEx.Message}");
                        }

                        candidates.Add(new ClientProcessCandidate(
                            ProcessId: capturedPid,
                            MainWindowHandle: capturedHwnd,
                            CommandLine: cmd,
                            KillTree: () => p.Kill(true),
                            WaitForExit: timeoutMs => p.WaitForExit(timeoutMs),
                            IsAlive: () => IsOsProcessAlive(capturedPid),
                            WorkingSetBytes: ws,
                            Responding: resp));
                    }
                    catch (Exception procEx)
                    {
                        _logger($"[DEBUG] Skipping candidate process during '{name}' scan: {procEx.Message}");
                    }
                }
            }
            catch (Exception enumEx)
            {
                _logger($"[WARN] Error enumerating '{name}' processes: {enumEx.Message}");
            }
        }

        return candidates;
    }

    private static bool IsOsProcessAlive(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using Process recheck = Process.GetProcessById(pid);
            return !recheck.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private ToolCallResult StopMatch(bool killAll = false)
    {
        List<int> killedIds = [];
        List<int> survivingIds = [];
        List<object> killErrors = [];

        if (GameServerManageHandler.IsTestExecution && _candidateEnumerator == null)
        {
            _logger("[TEST MODE] Suppressing StopMatch OS process kills to preserve live client.");
            var testResponse = new
            {
                status = "success",
                ok = true,
                action = "stop",
                running = false,
                terminatedProcessesCount = 0,
                terminatedProcessIds = killedIds,
                survivingProcessIds = survivingIds,
                message = "Test mode: suppressed OS process termination to preserve live client.",
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            };
            return ToolCallResult.Json(testResponse);
        }

        Process? trackedToKill = null;
        lock (_lock)
        {
            trackedToKill = _trackedProcess;
            _trackedProcess = null;
        }

        if (trackedToKill != null)
        {
            int pid = 0;
            try
            {
                if (!trackedToKill.HasExited)
                {
                    pid = trackedToKill.Id;
                    trackedToKill.Kill(true);
                    trackedToKill.WaitForExit(5000);
                    if (IsOsProcessAlive(pid))
                    {
                        survivingIds.Add(pid);
                        killErrors.Add(new { processId = pid, state = "survived", error = $"Process PID {pid} remained alive after 5000ms exit wait." });
                        _logger($"[ERROR] Tracked match process PID {pid} survived Kill(true) after 5000ms.");
                    }
                    else
                    {
                        killedIds.Add(pid);
                        _clientRegistry.UnregisterClient(pid, out _);
                        _logger($"Terminated tracked match process PID {pid}.");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger($"[ERROR] Error terminating tracked match process PID {pid}: {ex.Message}");
                if (pid > 0 && IsOsProcessAlive(pid) && !survivingIds.Contains(pid))
                {
                    survivingIds.Add(pid);
                }
                killErrors.Add(new { processId = pid, state = "kill_error", error = ex.Message });
            }
        }

        // Sweep managed candidates by default, and sweep all non-dedicated hon_x64 processes only when killAll == true
        IReadOnlyList<ClientProcessCandidate> candidates = (_candidateEnumerator != null || killAll)
            ? EnumerateClientCandidates()
            : [];

        foreach (ClientProcessCandidate candidate in candidates)
        {
            int pid = candidate.ProcessId;
            if (killedIds.Contains(pid) || survivingIds.Contains(pid))
            {
                continue;
            }

            if (!killAll && !_clientRegistry.IsManagedClient(pid))
            {
                continue;
            }

            try
            {
                candidate.KillTree();
                bool exitedInWindow = candidate.WaitForExit(5000);
                bool stillAlive = candidate.IsAlive();
                if (!exitedInWindow && stillAlive)
                {
                    survivingIds.Add(pid);
                    killErrors.Add(new { processId = pid, state = "survived", error = $"Process PID {pid} remained alive after 5000ms exit wait." });
                    _logger($"[ERROR] Client process PID {pid} survived Kill(true) after 5000ms.");
                }
                else
                {
                    killedIds.Add(pid);
                    _clientRegistry.UnregisterClient(pid, out _);
                    _logger($"Terminated client process PID {pid}.");
                }
            }
            catch (Exception killEx)
            {
                _logger($"[ERROR] Failed terminating client process PID {pid}: {killEx.Message}");
                bool stillAlive = true;
                try
                {
                    stillAlive = candidate.IsAlive();
                }
                catch (Exception probeEx)
                {
                    _logger($"[WARN] Post-kill survival probe failed for PID {pid}: {probeEx.Message}");
                }

                if (stillAlive)
                {
                    survivingIds.Add(pid);
                }
                killErrors.Add(new { processId = pid, state = stillAlive ? "survived" : "terminated_with_warning", error = killEx.Message });
            }
        }

        if (survivingIds.Count > 0 || killErrors.Count > 0)
        {
            string failMsg =
                $"Failed to terminate {survivingIds.Count} match process(es) (surviving PIDs: [{string.Join(", ", survivingIds)}], errors: {killErrors.Count}).";
            return ToolCallResult.Json(new
            {
                status = "error",
                ok = false,
                action = "stop",
                errorCode = "process_termination_failed",
                running = survivingIds.Count > 0,
                terminatedProcessesCount = killedIds.Count,
                terminatedProcessIds = killedIds,
                survivingProcessIds = survivingIds,
                killErrors,
                error = failMsg,
                message = failMsg,
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            }, isError: true);
        }

        var response = new
        {
            status = "success",
            ok = true,
            action = "stop",
            running = false,
            terminatedProcessesCount = killedIds.Count,
            terminatedProcessIds = killedIds,
            survivingProcessIds = survivingIds,
            message = killedIds.Count > 0
                ? $"Successfully stopped and verified exit of {killedIds.Count} match process(es) (PIDs: {string.Join(", ", killedIds)})."
                : "No active match process was running.",
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        };

        return ToolCallResult.Json(response);
    }

    private ToolCallResult GetMatchStatus()
    {
        Process? proc = null;
        lock (_lock)
        {
            if (_trackedProcess != null && !_trackedProcess.HasExited)
            {
                proc = _trackedProcess;
            }
        }

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

                    _logger($"Match process running: PID {proc.Id}, MainWindowHandle: {hwnd}, WorkingSet: {workingSetMb} MB");

                    return ToolCallResult.Json(new
                    {
                        status = "success",
                        ok = true,
                        action = "status",
                        running = true,
                        managedByMcp = true,
                        processId = proc.Id,
                        mainWindowHandle = hwnd,
                        map = _lastMap,
                        hero = _lastHero,
                        mode = _lastMode,
                        executable = _lastExecutable,
                        workingDirectory = _lastWorkingDirectory,
                        workingSetBytes = workingSet,
                        workingSetMb,
                        responding = proc.Responding,
                        timestamp = _timeProvider.GetUtcNow().ToString("o")
                    });
                }
            }
            catch (Exception ex)
            {
                _logger($"[WARN] Error inspecting running match process: {ex.Message}");
            }
        }

        if (_candidateEnumerator != null || !GameServerManageHandler.IsTestExecution)
        {
            IReadOnlyList<ClientProcessCandidate> candidates = EnumerateClientCandidates();
            ClientProcessCandidate? managedCandidate = candidates.FirstOrDefault(c => _clientRegistry.IsManagedClient(c.ProcessId));
            if (managedCandidate != null)
            {
                string hwnd = $"0x{managedCandidate.MainWindowHandle.ToInt64():X8}";
                double workingSetMb = Math.Round(managedCandidate.WorkingSetBytes / (1024.0 * 1024.0), 2);
                return ToolCallResult.Json(new
                {
                    status = "success",
                    ok = true,
                    action = "status",
                    running = true,
                    managedByMcp = true,
                    processId = managedCandidate.ProcessId,
                    mainWindowHandle = hwnd,
                    map = _lastMap,
                    hero = _lastHero,
                    mode = _lastMode,
                    executable = _lastExecutable,
                    workingDirectory = _lastWorkingDirectory,
                    workingSetBytes = managedCandidate.WorkingSetBytes,
                    workingSetMb,
                    responding = managedCandidate.Responding,
                    timestamp = _timeProvider.GetUtcNow().ToString("o")
                });
            }

            if (candidates.Count > 0)
            {
                ClientProcessCandidate foreign = candidates[0];
                return ToolCallResult.Json(new
                {
                    status = "success",
                    ok = true,
                    action = "status",
                    running = false,
                    managedByMcp = false,
                    foreignClientRunning = true,
                    foreignProcessId = foreign.ProcessId,
                    processId = 0,
                    mainWindowHandle = "0x00000000",
                    map = _lastMap,
                    hero = _lastHero,
                    mode = _lastMode,
                    message = $"No MCP-managed match is running (un-owned personal client PID {foreign.ProcessId} is active).",
                    timestamp = _timeProvider.GetUtcNow().ToString("o")
                });
            }
        }

        _logger("No match process currently running.");
        return ToolCallResult.Json(new
        {
            status = "success",
            ok = true,
            action = "status",
            running = false,
            managedByMcp = false,
            foreignClientRunning = false,
            processId = 0,
            mainWindowHandle = "0x00000000",
            map = _lastMap,
            hero = _lastHero,
            mode = _lastMode,
            executable = _lastExecutable,
            workingDirectory = _lastWorkingDirectory,
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        });
    }

    /// <summary>
    /// Builds extended command-line arguments for direct practice/bot match loading.
    /// </summary>
    public static IReadOnlyList<string> BuildMatchLaunchArguments(
        string map,
        string hero,
        string mode,
        int bots,
        bool cheats,
        bool headless,
        string resolution,
        bool fullscreen,
        int maxFps,
        string masterServer,
        int tickrate = ClientModMountBuilder.CanonicalSimulationTickRate)
    {
        string normalizedMasterServer = GameLauncherService.NormalizeMasterServerAddress(masterServer);
        int clampedTickrate = Math.Clamp(tickrate, 20, 60);
        List<string> args = [];

        // 1. Service Endpoints Routing
        args.Add($"-masterserver {normalizedMasterServer}");
        args.Add("-chatserver 127.0.0.1:11031");
        args.Add($"-webserver {normalizedMasterServer}");
        args.Add($"-messageserver {normalizedMasterServer}");
        args.Add("+Set chat_serverAddrOverride 127.0.0.1");
        args.Add("+Set chat_serverPortOverride 11031");

        // 2. Full VFS Mod Mounts
        args.Add(ClientModMountBuilder.GetCanonicalModString());

        // 3. Real-Time Console Logging
        args.Add("-writeconsole");
        args.Add("-logfile console.log");
        args.Add("+Set log_flush false");
        args.Add("+Set con_flushLog false");
        args.Add("+Set sys_flushLogs false");
        args.Add("+Set con_log 1");
        args.Add("+Set host_writeConsole true");

        // 4. Named Pipe IPC Hook
        args.Add("-pipe willowmaker-ipc");

        // 5. Performance & FPS Uncapping
        args.Add($"+Set host_maxFPS {maxFps}");
        args.Add($"+Set cl_maxFPS {maxFps}");
        args.Add($"+Set vid_maxFPS {maxFps}");
        args.Add("+Set sys_sleepTime 0");
        args.Add("+Set gl_swapInterval 0");
        args.Add("+Set d3d_tripleBuffering false");
        args.Add("+Set d3d_pure true");
        args.Add("+Set vid_driver d3d9");
        args.Add("+Set vid_textureCompression true");

        // 6. Audio Tuning & Headless Handling
        if (headless)
        {
            args.Add("+Set sound_disable true");
        }
        else
        {
            args.Add("+Set sound_numChannels 64");
            args.Add("+Set sound_bufferLength 256");
            args.Add("+Set sound_mixRate 48000");
            args.Add("+Set sound_recording_mixRate 48000");
        }

        // 7. Developer & Cheat Flags
        args.Add("+Set releaseStage_dev true");
        args.Add("+Set releaseStage_test true");
        args.Add("+Set releaseStage_stable false");
        args.Add("+Set ui_dev true");
        args.Add("+Set ui_dev_button true");
        args.Add("+Set ui_showQuickStart true");
        args.Add("+Set ui_canCreateGame true");

        if (cheats)
        {
            args.Add("+Set dev true");
            args.Add("+Set cheats true");
        }

        // 8. Display & Resolution Settings
        if (headless)
        {
            // vid_pos -32000 -32000 and NativeGuard/SetClientWindowHeadless position the window offscreen for invisible GPU rendering.
            // Isolate config writes into ~/startup_headless.cfg and never mutate cg_constrainCursor so foreground startup.cfg stays clean.
            args.Add("+Set host_startupCfg \"~/startup_headless.cfg\"");
            args.Add("+Set vid_resolution \"1280,720\"");
            args.Add("+Set vid_width 1280");
            args.Add("+Set vid_height 720");
            args.Add("+Set vid_fullscreen false");
            args.Add("+Set vid_borderless true");
            args.Add("+Set d3d_exclusive false");
            args.Add("+Set options_fullscreen false");
            args.Add("+Set vid_pos -32000 -32000");
            args.Add("-headless");
            args.Add("+Set sys_autoSaveConfig false");
        }
        else
        {
            string res = string.IsNullOrWhiteSpace(resolution) ? "1920x1080" : resolution.Trim();
            args.Add($"+Set vid_resolution {res}");

            string[] parts = res.ToLowerInvariant().Split('x');
            if (parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h) && w > 0 && h > 0)
            {
                args.Add($"+Set vid_width {w}");
                args.Add($"+Set vid_height {h}");
            }

            args.Add($"+Set vid_fullscreen {(fullscreen ? "true" : "false")}");
            args.Add($"+Set vid_borderless {(fullscreen ? "false" : "true")}");
        }

        // 9. Map and Hero Configuration
        string mapName = string.IsNullOrWhiteSpace(map) ? "caldavar" : map.Trim();
        string heroName = string.IsNullOrWhiteSpace(hero) ? "Hero_Pyromancer" : hero.Trim();

        args.Add($"+Set map {mapName}");
        args.Add($"+Set default_hero {heroName}");

        // 10. Bots Configuration
        if (bots > 0)
        {
            args.Add("+Set game_botMatch true");
            args.Add("+Set svr_botMatch true");
            args.Add($"+Set svr_numBots {bots}");
        }

        // 11. Match Startup & Ephemeral Fast Launch Cvars (deconflicted from COMPEL 11235..11534)
        string matchMode = string.IsNullOrWhiteSpace(mode) ? "practice" : mode.Trim();
        int clientSvrPort = bots > 0
            ? ClientPracticeHandler.ResolvePracticeServerPort(11590, 12150)
            : ClientPracticeHandler.ResolvePracticeServerPort(11580, 12100);
        int clientVoicePort = clientSvrPort + 500;
        args.Add($"+Set svr_port {clientSvrPort}");
        args.Add($"+Set svr_voicePortStart {clientVoicePort}");
        args.Add($"+Set svr_voicePortEnd {clientVoicePort}");
        args.Add("+Set sv_heroSelectTime 0");
        args.Add("+Set sv_heroSelectInitialTime 0");
        args.Add("+Set sv_preMatchTime 0");
        args.Add("+Set sv_matchStartAnnounceTime 0");
        args.Add("+Set sv_gameStartCountdown 0");
        args.Add("+Set sv_gameCountdownLength 0");
        args.Add("+Set svr_login false");
        args.Add("+Set svr_broadcastLocal false");

        // Variable Simulation Tickrate (20-60 Hz) & Anti-Stall Frame Headroom
        args.Add($"+Set svr_gameFPS {clampedTickrate}");
        args.Add("+Set svr_maxFramesPerHostFrame 5");

        if (bots <= 0)
        {
            // Set sv_precacheEntities false only for 1-player practice to avoid combat micro-stutter with bots
            args.Add("+Set sv_precacheEntities false");
        }
        args.Add("+Set ui_quickStart true");
        args.Add($"+StartGame {matchMode} PracticeGame map:{mapName} allheroes:true");

        return args.AsReadOnly();
    }
}
