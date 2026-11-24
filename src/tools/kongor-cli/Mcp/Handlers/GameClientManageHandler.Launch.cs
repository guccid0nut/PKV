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
using ASPIRE.Common.Vulkan;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;

namespace Kongor.Cli.Mcp.Handlers;

public sealed partial class GameClientManageHandler
{
    public Task<ToolCallResult> LaunchClientAsync(
        string mode = "normal",
        string? connectAddress = null,
        string masterServer = "127.0.0.1:55555",
        string resolution = "1920x1080",
        bool fullscreen = true,
        int maxFps = 1000,
        string? customClientPath = null,
        string? customWorkingDirectory = null,
        string? customNickname = null,
        string windowMode = "foreground",
        bool stealFocus = false,
        string? password = null,
        bool autoLogin = true,
        int? requestedSlot = null,
        string? agentOwner = null,
        string launchProfile = "mcp_visual_offscreen",
        bool? enableReplayCapture = null,
        bool enforceFramePacing = false,
        CancellationToken cancellationToken = default)
    {
        string flightKey = (string.Equals(windowMode, "headless", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(windowMode, "offscreen", StringComparison.OrdinalIgnoreCase) ||
                            requestedSlot.HasValue ||
                            !string.IsNullOrEmpty(agentOwner))
            ? $"client_{requestedSlot?.ToString() ?? agentOwner ?? Guid.NewGuid().ToString("N")[..8]}"
            : "client";

        return SingleFlightSpawner.ExecuteSingleFlightAsync(flightKey, () =>
            LaunchClientCoreAsync(mode, connectAddress, masterServer, resolution, fullscreen, maxFps, customClientPath, customWorkingDirectory, customNickname, windowMode, stealFocus, password, autoLogin, requestedSlot, agentOwner, launchProfile, enableReplayCapture, enforceFramePacing, cancellationToken),
            cancellationToken);
    }

    private async Task<ToolCallResult> LaunchClientCoreAsync(
        string mode,
        string? connectAddress,
        string masterServer,
        string resolution,
        bool fullscreen,
        int maxFps,
        string? customClientPath,
        string? customWorkingDirectory,
        string? customNickname,
        string windowMode,
        bool stealFocus,
        string? password,
        bool autoLogin,
        int? requestedSlot,
        string? agentOwner,
        string launchProfile,
        bool? enableReplayCapture,
        bool enforceFramePacing,
        CancellationToken cancellationToken)
    {
        _logger($"Starting game client resolution (WindowMode: '{windowMode}', StealFocus: {stealFocus}, User: '{customNickname ?? "auto"}', Slot: {requestedSlot?.ToString() ?? "auto"})...");

        if (!_processSupervisor.TryResolveClientExecutable(customClientPath, customWorkingDirectory, out FileInfo? executable))
        {
            string err = $"Unable to resolve game client executable 'hon_x64.exe'. Checked 'games/client/hon_x64.exe' and custom path '{customClientPath ?? "none"}'. Make sure the HoN game client is installed in games/client/.";
            _logger($"[ERROR] {err}");
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
        _logger($"Resolved game client binary: '{executable.FullName}', working directory: '{gameDir}'.");

        // Ensure user directory tree exists and clean legacy shader cache (skip during unit tests)
        if (!GameServerManageHandler.IsTestExecution)
        {
            GameLauncherService.EnsureUserDirectoryInitialized(gameDir, _logger);
        }

        // Build launch arguments (preserve explicit masterserver.newbs.io Cloudflare Tunnel host; see MCP-251 & MCP-288)
        string effectiveMasterServer = !string.IsNullOrWhiteSpace(masterServer)
            ? GameLauncherService.NormalizeMasterServerAddress(masterServer.Trim())
            : GameLauncherService.ResolveDefaultMasterServer(gameDir, useLauncherSettings: false);
        bool isLoopback = GameLauncherService.IsLoopbackMasterServer(effectiveMasterServer);
        _logger($"[GameClientManage] Resolved effective master server: '{effectiveMasterServer}' (isLoopback: {isLoopback}, requested: '{masterServer}').");

        if (!isLoopback && autoLogin && !GameServerManageHandler.IsTestExecution &&
            !string.Equals(Environment.GetEnvironmentVariable("KONGOR_MCP_ALLOW_REMOTE_LOGIN"), "1", StringComparison.OrdinalIgnoreCase))
        {
            string remoteErr =
                $"Blocked automatic credential login to non-loopback master server '{effectiveMasterServer}' (MCP-288 safety guard). " +
                "Pass masterServer='127.0.0.1:55555' for local testing, pass autoLogin=false, or set KONGOR_MCP_ALLOW_REMOTE_LOGIN=1 in the MCP host environment.";
            _logger($"[ERROR] {remoteErr}");
            return ToolCallResult.Json(new
            {
                status = "error",
                ok = false,
                action = "launch",
                errorCode = "remote_login_blocked",
                running = false,
                effectiveMasterServer,
                isLoopback = false,
                errorMessage = remoteErr,
                message = remoteErr,
                remediation_call = new
                {
                    tool = "kongor_game_client_manage",
                    arguments = new
                    {
                        action = "launch",
                        window_mode = "offscreen",
                        masterServer = "127.0.0.1:55555"
                    }
                },
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            }, isError: true);
        }

        bool isHeadless = string.Equals(windowMode, "headless", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(windowMode, "offscreen", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(mode, "headless", StringComparison.OrdinalIgnoreCase);
        bool effectiveFullscreen = isHeadless ? false : fullscreen;
        string effectiveResolution = isHeadless && string.Equals(resolution, "1920x1080", StringComparison.OrdinalIgnoreCase)
            ? "1280x720"
            : resolution;

        OffscreenSlot? allocatedSlot = null;
        if (isHeadless)
        {
            allocatedSlot = _slotManager.AcquireSlot(preferredSlot: requestedSlot);
            _logger($"[GameClientManage] Allocated offscreen slot {allocatedSlot.Index} at ({allocatedSlot.OffscreenX}, {allocatedSlot.OffscreenY}).");
        }

        string effectiveUsername = !string.IsNullOrWhiteSpace(customNickname)
            ? customNickname
            : (allocatedSlot != null ? allocatedSlot.DefaultNickname : _modMountBuilder.ResolveDefaultNickname(gameDir));

        string passMd5 = !string.IsNullOrWhiteSpace(password)
            ? _credentialManager.HashPassword(password)
            : _credentialManager.DefaultPasswordMd5;

        int offscreenX = allocatedSlot?.OffscreenX ?? -10000;
        int offscreenY = allocatedSlot?.OffscreenY ?? -10000;
        int slotIdx = allocatedSlot?.Index ?? requestedSlot ?? 0;
        string pipeName = slotIdx > 0 ? $"hon-client-{slotIdx}" : "willowmaker-ipc";
        string nativePipeName = slotIdx > 0 ? $"kongor-native-client-{slotIdx}" : "kongor-native-client";

        bool isPerfCleanProfile = string.Equals(launchProfile, "perf_clean", StringComparison.OrdinalIgnoreCase);
        bool isKnownLaunchProfile = isPerfCleanProfile ||
                                    string.Equals(launchProfile, "mcp_visual_offscreen", StringComparison.OrdinalIgnoreCase);
        if (!isKnownLaunchProfile)
        {
            _logger($"[WARN] Unknown launchProfile '{launchProfile}'; defaulting to 'mcp_visual_offscreen'. Valid profiles: 'mcp_visual_offscreen', 'perf_clean'.");
        }
        bool injectReplayCaptureLayer = enableReplayCapture ?? !isPerfCleanProfile;

        // Only write autoexec.cfg for primary (slot 0) non-perf_clean foreground/user launches to avoid mutating user configuration during headless, perf_clean, multi-client, or unit testing
        if (slotIdx == 0 && !isHeadless && !isPerfCleanProfile && !GameServerManageHandler.IsTestExecution)
        {
            GameLauncherService.WriteAutoexecConfig(
                gameDir,
                _logger,
                refreshRate: "240",
                fullscreen: effectiveFullscreen,
                borderless: false,
                isHeadless: false,
                username: effectiveUsername,
                passwordMd5: passMd5,
                resolution: resolution);
        }

        IReadOnlyList<string> arguments = _modMountBuilder.BuildClientLaunchArguments(
            gameDir,
            mode,
            connectAddress,
            effectiveMasterServer,
            effectiveResolution,
            effectiveFullscreen,
            maxFps,
            effectiveUsername,
            windowMode,
            offscreenX,
            offscreenY,
            slotIdx,
            pipeName,
            passMd5,
            autoLogin);
        string joinedArguments = string.Join(" ", arguments);
        IReadOnlyList<string> redactedArguments = RedactLaunchArguments(arguments, passMd5);
        _logger($"Constructed launch arguments: {string.Join(" ", redactedArguments)}");

        if (GameServerManageHandler.IsTestExecution)
        {
            _logger("[TEST ENVIRONMENT DETECTED] Simulating game client launch without spawning real desktop GUI window.");
            int simulatedPid = Environment.ProcessId;
            if (allocatedSlot != null)
            {
                _slotManager.AssignProcess(slotIdx, simulatedPid);
            }
            _processRegistry.Register(new ClientInstance
            {
                ProcessId = simulatedPid,
                SlotIndex = slotIdx,
                OffscreenX = offscreenX,
                OffscreenY = offscreenY,
                PipeName = pipeName,
                NativePipeName = nativePipeName,
                Username = effectiveUsername,
                AgentOwner = agentOwner,
                WindowMode = windowMode,
                LaunchedAt = _timeProvider.GetUtcNow()
            });

            return ToolCallResult.Json(new
            {
                status = "success",
                ok = true,
                action = "launch",
                running = true,
                processId = simulatedPid,
                mainWindowHandle = "0x00010001",
                slotIndex = slotIdx,
                offscreenX,
                offscreenY,
                username = effectiveUsername,
                pipeName,
                nativePipeName,
                agentOwner,
                executable = executable.FullName,
                workingDirectory = gameDir,
                arguments = redactedArguments,
                mode,
                resolution = effectiveResolution,
                fullscreen = effectiveFullscreen,
                windowMode,
                isOffscreen = isHeadless,
                masterServer = effectiveMasterServer,
                effectiveMasterServer,
                isLoopback,
                connectAddress,
                maxFps,
                launchProfile,
                enableReplayCapture = injectReplayCaptureLayer,
                enforceFramePacing,
                message = $"Simulated game client launched (PID: {simulatedPid}, Slot: {slotIdx}, User: {effectiveUsername}).",
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            });
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = executable.FullName,
            WorkingDirectory = gameDir,
            Arguments = joinedArguments,
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            RedirectStandardInput = false,
            CreateNoWindow = false,
            WindowStyle = ProcessWindowStyle.Normal
        };

        if (isHeadless)
        {
            // When enforceFramePacing is true (e.g. perf_clean), keep KONGOR_HEADLESS=0 so vid_vulkan_spy's
            // PaceFrameToTargetMaxFps() executes while version.dll + STARTF_USEPOSITION preserve zeroWindowExposure.
            startInfo.EnvironmentVariables["KONGOR_HEADLESS"] = enforceFramePacing ? "0" : "1";
            startInfo.EnvironmentVariables["DXVK_HUD"] = "0";
        }

        startInfo.EnvironmentVariables["KONGOR_NATIVE_PIPE"] = nativePipeName;
        if (slotIdx > 0)
        {
            startInfo.EnvironmentVariables["KONGOR_USERDATA_DIR"] = $"userdata_slot_{slotIdx}";
        }

        string userdataKongorDir = Path.Combine(gameDir, "userdataKONGOR");
        string dxvkConfPath = Path.Combine(userdataKongorDir, "dxvk.conf");
        string dxvkCacheDir = Path.Combine(userdataKongorDir, "dxvk_cache");
        if (File.Exists(dxvkConfPath))
        {
            startInfo.EnvironmentVariables["DXVK_CONFIG_FILE"] = dxvkConfPath;
        }
        startInfo.EnvironmentVariables["DXVK_SHADER_CACHE"] = "1";
        startInfo.EnvironmentVariables["DXVK_SHADER_CACHE_PATH"] = dxvkCacheDir;
        startInfo.EnvironmentVariables["DXVK_STATE_CACHE_PATH"] = dxvkCacheDir;
        string alsoftConfPath = Path.Combine(userdataKongorDir, "alsoft.ini");
        if (File.Exists(alsoftConfPath))
        {
            startInfo.EnvironmentVariables["ALSOFT_CONF"] = alsoftConfPath;
        }

        if (OperatingSystem.IsWindows())
        {
            VulkanImplicitLayerShieldPlan shieldPlan = VulkanImplicitLayerShield.DiscoverAndBuildPlan(
                allowThirdPartyVulkanOverlays: false,
                log: _logger);
            VulkanImplicitLayerShield.ApplyToEnvironment(
                startInfo.EnvironmentVariables,
                shieldPlan,
                setEmptyInsteadOfRemoveForEnableVars: true);
            string loaderDisableMask = VulkanImplicitLayerShield.BuildLoaderLayersDisableMask(
                injectKongorReplayCaptureLayer: injectReplayCaptureLayer,
                allowThirdPartyVulkanOverlays: false)
                ?? "~implicit~";
            startInfo.EnvironmentVariables["VK_LOADER_LAYERS_DISABLE"] = loaderDisableMask;
            _logger($"[GameClientManage][VULKAN_SHIELD] {VulkanImplicitLayerShield.FormatDiagnosticSummary(shieldPlan)}");

            if (injectReplayCaptureLayer)
            {
                // Preserve Tier 0 zero-window-exposure offscreen screenshots (WindowCapturer.cs)
                // while still blocking third-party HKLM implicit Vulkan layers (OBS, Steam, RenderDoc, TikTok).
                // Set explicitly to "0" (not Remove) because BuildUnicodeEnvironmentBlock merges parent process env.
                startInfo.EnvironmentVariables["HON_CAPTURE_DISABLE"] = "0";

                string? layerDir = GameLauncherService.ResolveVulkanLayerDirectory(gameDir);
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
                    GameLauncherService.ConfigureVulkanOverlayEnvironment(
                        startInfo.EnvironmentVariables,
                        isHeadless,
                        Environment.GetEnvironmentVariable("HON_MODERN_HUD"),
                        Environment.GetEnvironmentVariable("HON_DEVTOOLS"));

                    int resW = 1280;
                    int resH = 720;
                    if (!string.IsNullOrWhiteSpace(resolution) && resolution.Contains('x', StringComparison.OrdinalIgnoreCase))
                    {
                        string[] parts = resolution.Split(['x', 'X'], StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h))
                        {
                            resW = w;
                            resH = h;
                        }
                    }
                    startInfo.EnvironmentVariables["HON_REPLAY_WIDTH"] = resW.ToString();
                    startInfo.EnvironmentVariables["HON_REPLAY_HEIGHT"] = resH.ToString();
                    _logger($"[GameClientManage] Profile='{launchProfile}' (Tier 0 capture enabled, 3rd-party HKLM implicit layers blocked): VK_LAYER_PATH='{layerDir}', Res={resW}x{resH}, DXVK_CONFIG_FILE='{dxvkConfPath}', KONGOR_HEADLESS='{startInfo.EnvironmentVariables["KONGOR_HEADLESS"]}'");
                }
            }
            else
            {
                // Clean DirectFlip / WILLOWMAKER parity profile: disable replay capture layer & all HKLM implicit layers
                startInfo.EnvironmentVariables["VK_LAYER_PATH"] = string.Empty;
                startInfo.EnvironmentVariables["VK_ADD_LAYER_PATH"] = string.Empty;
                startInfo.EnvironmentVariables["VK_INSTANCE_LAYERS"] = string.Empty;
                startInfo.EnvironmentVariables["VK_LOADER_LAYERS_ENABLE"] = string.Empty;
                startInfo.EnvironmentVariables["HON_CAPTURE_ENABLE"] = "0";
                startInfo.EnvironmentVariables["HON_CAPTURE_DISABLE"] = "1";
                startInfo.EnvironmentVariables["HON_MODERN_HUD"] = "0";
                startInfo.EnvironmentVariables["HON_FSR_ENABLE"] = "0";
                _logger($"[GameClientManage] Profile='{launchProfile}' (perf_clean: VK_LAYER_HON_replay_capture & all HKLM implicit layers disabled, EnforcePacing={enforceFramePacing}, DXVK_CONFIG_FILE='{dxvkConfPath}', KONGOR_HEADLESS='{startInfo.EnvironmentVariables["KONGOR_HEADLESS"]}')");
            }
        }

        // Environment variables for Linux/macOS dynamic linkers
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

        Process process = new() { StartInfo = startInfo, EnableRaisingEvents = true };
        if (startInfo.RedirectStandardOutput)
        {
            process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    _logger($"[hon_x64 STDOUT] {e.Data}");
                    _outputBuffer.AppendLine(e.Data);
                }
            };
        }
        if (startInfo.RedirectStandardError)
        {
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    _logger($"[hon_x64 STDERR] {e.Data}");
                    _outputBuffer.AppendLine($"[STDERR] {e.Data}");
                }
            };
        }

        // Pre-warm Named Pipe IPC listener so hon_x64.exe connects on first attempt during engine boot
        var mcpBridge = ResolveMcpServerIpcBridge();
        if (mcpBridge != null)
        {
            try
            {
                _logger($"Pre-warming Named Pipe IPC server (\\\\.\\pipe\\{mcpBridge.PipeName})...");
                await mcpBridge.EnsureListeningAsync(2000, cancellationToken).ConfigureAwait(false);
                _logger($"Named Pipe IPC server is actively listening on \\\\.\\pipe\\{mcpBridge.PipeName}.");
            }
            catch (Exception ex)
            {
                _logger($"[WARN] Pre-warm IPC server encountered exception: {ex.Message}");
            }
        }

        Process? startedProc = null;
        try
        {
            Dictionary<string, string> envDict = [];
            foreach (System.Collections.DictionaryEntry de in startInfo.EnvironmentVariables)
            {
                if (de.Key != null && de.Value != null)
                {
                    envDict[de.Key.ToString()!] = de.Value.ToString()!;
                }
            }

            startedProc = InteractiveProcessLauncher.StartInteractiveProcess(
                executable.FullName,
                joinedArguments,
                gameDir,
                headless: isHeadless,
                stealFocus: stealFocus,
                environmentVariables: envDict);
        }
        catch (Exception ex)
        {
            string err = $"Failed to start process '{executable.FullName}': {ex.Message}";
            _logger($"[ERROR] {err}");
            return ToolCallResult.Json(new
            {
                status = "error",
                action = "launch",
                running = false,
                executable = executable.FullName,
                workingDirectory = gameDir,
                arguments = redactedArguments,
                errorMessage = err,
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            });
        }

        if (startedProc == null)
        {
            string err = $"Process.Start returned null for '{executable.FullName}'.";
            _logger($"[ERROR] {err}");
            return ToolCallResult.Json(new
            {
                status = "error",
                action = "launch",
                running = false,
                executable = executable.FullName,
                workingDirectory = gameDir,
                arguments = redactedArguments,
                errorMessage = err,
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            });
        }

        process = startedProc;
        if (isHeadless)
        {
            JobObjectManager.AssignProcess(process);
        }

        if (startInfo.RedirectStandardOutput) process.BeginOutputReadLine();
        if (startInfo.RedirectStandardError) process.BeginErrorReadLine();

        lock (_lock)
        {
            _trackedClientProcess = process;
            if (startInfo.RedirectStandardInput)
            {
                _clientStdin = process.StandardInput;
                _clientStdin.AutoFlush = true;
            }
            _lastExecutable = executable.FullName;
            _lastWorkingDirectory = gameDir;
            _lastRedactedArguments = redactedArguments;
        }

        var serverBridge = ResolveMcpServerIpcBridge();
        if (serverBridge != null && process != null)
        {
            serverBridge.AttachProcess(process);
        }

        // Wait for MainWindowHandle (skip extended delay for headless/offscreen windows)
        int waitMs = 0;
        int maxWait = isHeadless ? 500 : 3000;
        IntPtr detectedHwnd = IntPtr.Zero;
        while (process != null && !process.HasExited && detectedHwnd == IntPtr.Zero && waitMs < maxWait)
        {
            try { process.Refresh(); } catch (Exception ex) { _logger($"[LaunchGame] process.Refresh warning: {ex.Message}"); }
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
            if (process != null && !process.HasExited)
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
                _logger($"Game client launched successfully! PID: {pid}, MainWindowHandle: {hwndStr}, Slot: {slotIdx}, User: {effectiveUsername}");

                if (allocatedSlot != null)
                {
                    _slotManager.AssignProcess(slotIdx, pid);
                }

                _processRegistry.Register(new ClientInstance
                {
                    ProcessId = pid,
                    MainWindowHandle = detectedHwnd,
                    SlotIndex = slotIdx,
                    OffscreenX = offscreenX,
                    OffscreenY = offscreenY,
                    PipeName = pipeName,
                    NativePipeName = nativePipeName,
                    Username = effectiveUsername,
                    AgentOwner = agentOwner,
                    WindowMode = windowMode,
                    LaunchedAt = _timeProvider.GetUtcNow()
                });

                if (isHeadless)
                {
                    int wdW = 1280;
                    int wdH = 720;
                    if (_modMountBuilder.TryParseResolution(effectiveResolution, out int parsedWdW, out int parsedWdH))
                    {
                        wdW = parsedWdW;
                        wdH = parsedWdH;
                    }
                    _processSupervisor.StartOffscreenWindowWatchdog(process, offscreenX, offscreenY, wdW, wdH);
                    _logger($"Active continuous offscreen window watchdog started for PID {pid} at slot {slotIdx} ({offscreenX}, {offscreenY}, {wdW}x{wdH}).");
                }
                else if (process.MainWindowHandle != IntPtr.Zero)
                {
                    int targetX = (slotIdx == 0) ? 20 : 660;
                    int targetY = 40;
                    int targetW = 1280;
                    int targetH = 720;
                    if (_modMountBuilder.TryParseResolution(resolution, out int rw, out int rh))
                    {
                        targetW = rw;
                        targetH = rh;
                    }
                    SetClientWindowForeground(process.MainWindowHandle, targetX, targetY, targetW, targetH, activate: true);
                }

                // Proactively attempt Named Pipe IPC connection in background so UI thread commands route via IPC
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _gameIpcBridge.ConnectAsync(5000, CancellationToken.None).ConfigureAwait(false);
                        if (isHeadless)
                        {
                            // Enforce non-exclusive windowed state after K2 step-5 autoexec.cfg finishes (without touching cg_constrainCursor)
                            const string headlessSafetyCmd = "Set vid_fullscreen false; Set d3d_exclusive false; Set options_fullscreen false; Set options_exclusive false; Set vid_borderless true";
                            await _gameIpcBridge.ExecuteCommandAsync(headlessSafetyCmd, 2000, CancellationToken.None).ConfigureAwait(false);
                            _logger($"[LaunchGame] Enforced post-autoexec headless windowed safety CVars for PID {pid}.");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger($"[LaunchGame] Background IPC pre-connect deferred for PID {pid}: {ex.Message}");
                    }
                }, CancellationToken.None);
            }
            else
            {
                _logger($"Game client exited prematurely with exit code: {process?.ExitCode}");
            }
        }
        catch (Exception ex)
        {
            _logger($"[WARN] Could not retrieve process state: {ex.Message}");
        }

        var response = new
        {
            status = isRunning ? "success" : "exited",
            action = "launch",
            running = isRunning,
            processId = pid,
            mainWindowHandle = hwndStr,
            slotIndex = slotIdx,
            offscreenX,
            offscreenY,
            username = effectiveUsername,
            pipeName,
            nativePipeName,
            agentOwner,
            executable = executable.FullName,
            workingDirectory = gameDir,
            arguments = redactedArguments,
            mode,
            resolution = effectiveResolution,
            fullscreen = effectiveFullscreen,
            windowMode,
            isOffscreen = isHeadless || (process != null && detectedHwnd != IntPtr.Zero && IsWindowOffscreen(detectedHwnd)),
            masterServer = effectiveMasterServer,
            effectiveMasterServer,
            isLoopback,
            connectAddress,
            maxFps,
            launchProfile,
            enableReplayCapture = injectReplayCaptureLayer,
            enforceFramePacing,
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        };

        return ToolCallResult.Json(response);
    }

    internal static IReadOnlyList<string> RedactLaunchArguments(
        IReadOnlyList<string> arguments,
        string? explicitSecret = null)
    {
        if (arguments.Count == 0)
        {
            return arguments;
        }

        string[]? secrets = !string.IsNullOrWhiteSpace(explicitSecret) ? [explicitSecret] : null;
        string[] redacted = new string[arguments.Count];
        for (int i = 0; i < arguments.Count; i++)
        {
            redacted[i] = ClientCredentialManager.RedactSingleText(arguments[i], secrets);
        }

        return redacted;
    }
}
