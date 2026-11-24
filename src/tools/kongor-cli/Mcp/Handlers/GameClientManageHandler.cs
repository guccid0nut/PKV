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

/// <summary>
/// MCP Tool Handler for 'kongor_game_client_manage' (alias: 'kongor_game_client_manager').
/// Controls launching, stopping, status querying, restarting, and tailing logs of the player game client binary (hon_x64.exe).
/// Explicitly resolves games/client/hon_x64.exe (never WILLOWMAKER).
/// Injects full VFS mod mounts, DXVK Vulkan settings, low-latency audio, and Named Pipe IPC.
/// </summary>
public sealed partial class GameClientManageHandler
{
    public static bool SuppressOffscreenWatchdog
    {
        get => ClientProcessSupervisor.SuppressOffscreenWatchdog;
        set => ClientProcessSupervisor.SuppressOffscreenWatchdog = value;
    }

    private readonly Lock _lock = new();
    private Process? _trackedClientProcess;
    private StreamWriter? _clientStdin;
    private string? _lastExecutable;
    private string? _lastWorkingDirectory;
    private IReadOnlyList<string> _lastRedactedArguments = []; // Diagnostic only; never replay into ProcessStartInfo
    private readonly IClientProcessSupervisor _processSupervisor;
    private readonly IClientProcessService _processService;
    private readonly IClientOutputRingBuffer _outputBuffer;
    private readonly IClientModMountBuilder _modMountBuilder;
    private readonly IGameIpcBridge _gameIpcBridge;
    private readonly IUiControlService _uiControlService;
    private readonly IClientCommandRemediationService _remediationService;
    private readonly IOffscreenSlotManager _slotManager;
    private readonly IClientProcessRegistry _processRegistry;
    private readonly IClientCredentialManager _credentialManager;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string> _logger;

    public GameClientManageHandler(
        IGameIpcBridge? ipcBridge = null,
        IUiControlService? uiControlService = null,
        IClientCommandRemediationService? remediationService = null,
        TimeProvider? timeProvider = null,
        Action<string>? logger = null,
        IClientProcessSupervisor? processSupervisor = null,
        IClientOutputRingBuffer? outputBuffer = null,
        IClientModMountBuilder? modMountBuilder = null,
        IOffscreenSlotManager? slotManager = null,
        IClientProcessRegistry? processRegistry = null,
        IClientCredentialManager? credentialManager = null,
        IClientProcessService? processService = null)
    {
        _gameIpcBridge = ipcBridge ?? new GameIpcBridge();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _uiControlService = uiControlService ?? new UiControlService(timeProvider: _timeProvider);
        _remediationService = remediationService ?? new ClientCommandRemediationService(timeProvider: _timeProvider);
        _logger = logger ?? (msg => Console.Error.WriteLine($"[GameClientManage] {msg}"));
        _processSupervisor = processSupervisor ?? new ClientProcessSupervisor();
        _outputBuffer = outputBuffer ?? new ClientOutputRingBuffer();
        _modMountBuilder = modMountBuilder ?? new ClientModMountBuilder();
        _slotManager = slotManager ?? OffscreenSlotManager.Default;
        _processRegistry = processRegistry ?? ClientProcessRegistry.Default;
        _credentialManager = credentialManager ?? ClientCredentialManager.Default;
        _processService = processService ?? new ClientProcessService(_processSupervisor, _logger);
    }

    public GameClientManageHandler(TimeProvider timeProvider)
        : this(null, null, null, timeProvider, null)
    {
    }

    public IGameIpcBridge IpcBridge => _gameIpcBridge;
    public IUiControlService UiControlService => _uiControlService;
    public IClientCommandRemediationService RemediationService => _remediationService;
    public IClientProcessSupervisor ProcessSupervisor => _processSupervisor;
    public IClientProcessService ProcessService => _processService;
    public IClientOutputRingBuffer OutputRingBuffer => _outputBuffer;
    public IClientModMountBuilder ModMountBuilder => _modMountBuilder;
    public IOffscreenSlotManager SlotManager => _slotManager;
    public IClientProcessRegistry ProcessRegistry => _processRegistry;
    public IClientCredentialManager CredentialManager => _credentialManager;

    private static bool GetAutoLaunch(JsonElement arguments) => TryGetBool(arguments, "autoLaunch");

    private static bool TryGetBool(JsonElement arguments, string propertyName)
    {
        if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(propertyName, out JsonElement el))
        {
            if (el.ValueKind == JsonValueKind.False) return false;
            if (el.ValueKind == JsonValueKind.True) return true;
            if (el.ValueKind == JsonValueKind.String && bool.TryParse(el.GetString(), out bool b)) return b;
        }
        return false;
    }

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        string action = "status";
        if (arguments.TryGetProperty("action", out JsonElement actionElem) && !string.IsNullOrWhiteSpace(actionElem.GetString()))
        {
            action = actionElem.GetString()!.Trim().ToLowerInvariant();
        }

        string mode = arguments.TryGetProperty("mode", out JsonElement modeElem) && !string.IsNullOrWhiteSpace(modeElem.GetString())
            ? modeElem.GetString()!.Trim().ToLowerInvariant()
            : "normal";

        string? connectAddress = arguments.TryGetProperty("connectAddress", out JsonElement connElem) && !string.IsNullOrWhiteSpace(connElem.GetString())
            ? connElem.GetString()!.Trim()
            : null;

        string? workingDirectory = arguments.TryGetProperty("workingDirectory", out JsonElement wdElem) && !string.IsNullOrWhiteSpace(wdElem.GetString())
            ? wdElem.GetString()!.Trim()
            : null;

        bool useLauncherSettings = TryGetBool(arguments, "useLauncherSettings") || TryGetBool(arguments, "use_launcher_settings");

        string masterServer = arguments.TryGetProperty("masterServer", out JsonElement msElem) && !string.IsNullOrWhiteSpace(msElem.GetString())
            ? msElem.GetString()!.Trim()
            : GameLauncherService.ResolveDefaultMasterServer(workingDirectory, useLauncherSettings);

        string resolution = arguments.TryGetProperty("resolution", out JsonElement resElem) && !string.IsNullOrWhiteSpace(resElem.GetString())
            ? resElem.GetString()!.Trim()
            : "1920x1080";

        bool fullscreen = !arguments.TryGetProperty("fullscreen", out JsonElement fsElem) || fsElem.GetBoolean();

        int maxFps = arguments.TryGetProperty("maxFps", out JsonElement fpsElem) && fpsElem.TryGetInt32(out int fpsVal) && fpsVal > 0
            ? fpsVal
            : 1000;

        string? customClientPath = arguments.TryGetProperty("customClientPath", out JsonElement customElem) && !string.IsNullOrWhiteSpace(customElem.GetString())
            ? customElem.GetString()!.Trim()
            : null;

        string? nickname = arguments.TryGetProperty("nickname", out JsonElement nickElem) && !string.IsNullOrWhiteSpace(nickElem.GetString())
            ? nickElem.GetString()!.Trim()
            : (arguments.TryGetProperty("customNickname", out JsonElement cNickElem) && !string.IsNullOrWhiteSpace(cNickElem.GetString())
                ? cNickElem.GetString()!.Trim()
                : (arguments.TryGetProperty("playerNickname", out JsonElement pNickElem) && !string.IsNullOrWhiteSpace(pNickElem.GetString())
                    ? pNickElem.GetString()!.Trim()
                    : null));

        string windowMode = "foreground";
        if (arguments.TryGetProperty("window_mode", out JsonElement wmElem) && !string.IsNullOrWhiteSpace(wmElem.GetString()))
        {
            windowMode = wmElem.GetString()!.Trim().ToLowerInvariant();
        }
        else if (arguments.TryGetProperty("windowMode", out JsonElement wmElem2) && !string.IsNullOrWhiteSpace(wmElem2.GetString()))
        {
            windowMode = wmElem2.GetString()!.Trim().ToLowerInvariant();
        }
        else if (arguments.TryGetProperty("headless", out JsonElement hlElem))
        {
            if (hlElem.ValueKind == JsonValueKind.True || (hlElem.ValueKind == JsonValueKind.String && bool.TryParse(hlElem.GetString(), out bool b) && b))
            {
                windowMode = "headless";
            }
        }

        bool stealFocus = false;
        if (arguments.TryGetProperty("steal_focus", out JsonElement sfEl))
        {
            stealFocus = sfEl.ValueKind == JsonValueKind.True || (sfEl.ValueKind == JsonValueKind.String && bool.TryParse(sfEl.GetString(), out bool b) && b);
        }
        else if (arguments.TryGetProperty("stealFocus", out JsonElement sfEl2))
        {
            stealFocus = sfEl2.ValueKind == JsonValueKind.True || (sfEl2.ValueKind == JsonValueKind.String && bool.TryParse(sfEl2.GetString(), out bool b) && b);
        }

        int? requestedProcessId = null;
        if ((arguments.TryGetProperty("processId", out JsonElement pidEl) ||
             arguments.TryGetProperty("process_id", out pidEl) ||
             arguments.TryGetProperty("pid", out pidEl)) && pidEl.TryGetInt32(out int parsedPid) && parsedPid > 0)
        {
            requestedProcessId = parsedPid;
        }

        int? requestedSlot = null;
        if ((arguments.TryGetProperty("slot", out JsonElement slotEl) ||
             arguments.TryGetProperty("slotIndex", out slotEl) ||
             arguments.TryGetProperty("slot_index", out slotEl)) && slotEl.TryGetInt32(out int parsedSlot) && parsedSlot >= 0)
        {
            requestedSlot = parsedSlot;
        }

        string? username = null;
        if ((arguments.TryGetProperty("username", out JsonElement userEl) ||
             arguments.TryGetProperty("account", out userEl) ||
             arguments.TryGetProperty("accountName", out userEl)) && !string.IsNullOrWhiteSpace(userEl.GetString()))
        {
            username = userEl.GetString()!.Trim();
        }
        else
        {
            username = nickname;
        }

        string? password = null;
        if ((arguments.TryGetProperty("password", out JsonElement pwEl) ||
             arguments.TryGetProperty("pass", out pwEl)) && !string.IsNullOrWhiteSpace(pwEl.GetString()))
        {
            password = pwEl.GetString()!.Trim();
        }

        bool autoLogin = true;
        if (arguments.TryGetProperty("autoLogin", out JsonElement alEl) ||
            arguments.TryGetProperty("auto_login", out alEl))
        {
            autoLogin = alEl.ValueKind == JsonValueKind.True || (alEl.ValueKind == JsonValueKind.String && bool.TryParse(alEl.GetString(), out bool b) && b);
        }

        string? agentOwner = null;
        if ((arguments.TryGetProperty("agentOwner", out JsonElement aoEl) ||
             arguments.TryGetProperty("agent_owner", out aoEl) ||
             arguments.TryGetProperty("agentId", out aoEl) ||
             arguments.TryGetProperty("agent_id", out aoEl)) && !string.IsNullOrWhiteSpace(aoEl.GetString()))
        {
            agentOwner = aoEl.GetString()!.Trim();
        }
        agentOwner ??= McpAgentContext.CurrentAgentId;

        string launchProfile = "mcp_visual_offscreen";
        if ((arguments.TryGetProperty("launchProfile", out JsonElement lpEl) ||
             arguments.TryGetProperty("launch_profile", out lpEl)) && !string.IsNullOrWhiteSpace(lpEl.GetString()))
        {
            launchProfile = lpEl.GetString()!.Trim().ToLowerInvariant();
        }

        bool? enableReplayCapture = null;
        if (arguments.TryGetProperty("enableReplayCapture", out JsonElement ercEl) ||
            arguments.TryGetProperty("enable_replay_capture", out ercEl))
        {
            if (ercEl.ValueKind == JsonValueKind.True) enableReplayCapture = true;
            else if (ercEl.ValueKind == JsonValueKind.False) enableReplayCapture = false;
            else if (ercEl.ValueKind == JsonValueKind.String && bool.TryParse(ercEl.GetString(), out bool parsedErc)) enableReplayCapture = parsedErc;
        }

        bool enforceFramePacing = string.Equals(launchProfile, "perf_clean", StringComparison.OrdinalIgnoreCase);
        if (arguments.TryGetProperty("enforceFramePacing", out JsonElement efpEl) ||
            arguments.TryGetProperty("enforce_frame_pacing", out efpEl))
        {
            if (efpEl.ValueKind == JsonValueKind.True) enforceFramePacing = true;
            else if (efpEl.ValueKind == JsonValueKind.False) enforceFramePacing = false;
            else if (efpEl.ValueKind == JsonValueKind.String && bool.TryParse(efpEl.GetString(), out bool parsedEfp)) enforceFramePacing = parsedEfp;
        }

        _logger($"Received action '{action}' (Mode: '{mode}', Profile: '{launchProfile}', ReplayCapture: {enableReplayCapture?.ToString() ?? "profile_default"}, EnforcePacing: {enforceFramePacing}, User: '{username ?? "auto"}', Slot: {requestedSlot?.ToString() ?? "auto"}, MasterServer: '{masterServer}', Resolution: '{resolution}', Fullscreen: {fullscreen}, WindowMode: '{windowMode}', StealFocus: {stealFocus}, MaxFPS: {maxFps}, Connect: '{connectAddress ?? "none"}', Agent: '{agentOwner ?? "none"}').");

        try
        {
            switch (action)
            {
                case "launch":
                case "start":
                {
                    bool effectiveFullscreen = (string.Equals(windowMode, "headless", StringComparison.OrdinalIgnoreCase) ||
                                                string.Equals(windowMode, "offscreen", StringComparison.OrdinalIgnoreCase)) ? false : fullscreen;
                    return await LaunchClientAsync(mode, connectAddress, masterServer, resolution, effectiveFullscreen, maxFps, customClientPath, workingDirectory, username, windowMode, stealFocus, password, autoLogin, requestedSlot, agentOwner, launchProfile, enableReplayCapture, enforceFramePacing, cancellationToken).ConfigureAwait(false);
                }

                case "stop":
                {
                    bool stopAll = arguments.TryGetProperty("all", out JsonElement allEl) && allEl.ValueKind == JsonValueKind.True;
                    if (requestedProcessId.HasValue)
                    {
                        if (!_processRegistry.ValidateAgentAccess(agentOwner, requestedProcessId.Value, out string? accessErr))
                        {
                            return ToolCallResult.Error(accessErr!);
                        }
                        return StopClient(requestedProcessId, requestedSlot);
                    }

                    if (!stopAll)
                    {
                        if (!string.IsNullOrWhiteSpace(agentOwner))
                        {
                            var agentClient = _processRegistry.GetClientByAgentId(agentOwner);
                            if (agentClient != null)
                            {
                                return StopClient(agentClient.ProcessId, agentClient.SlotIndex);
                            }
                        }

                        var ambientClient = _processRegistry.GetClientByAgentId("ambient")
                            ?? _processRegistry.GetPrimaryClient()
                            ?? _processRegistry.AutoAdoptRunningClient(string.IsNullOrWhiteSpace(agentOwner) ? "ambient" : agentOwner);
                        if (ambientClient != null)
                        {
                            return StopClient(ambientClient.ProcessId, ambientClient.SlotIndex);
                        }
                    }

                    return StopClient(requestedProcessId, requestedSlot);
                }

                case "restart":
                {
                    _logger("Restart requested: stopping existing game client first...");
                    int? targetRestartPid = requestedProcessId;
                    int? targetRestartSlot = requestedSlot;
                    if (!targetRestartPid.HasValue && !targetRestartSlot.HasValue)
                    {
                        var existing = (!string.IsNullOrWhiteSpace(agentOwner) ? _processRegistry.GetClientByAgentId(agentOwner) : null)
                            ?? _processRegistry.GetClientByAgentId("ambient")
                            ?? _processRegistry.GetPrimaryClient()
                            ?? _processRegistry.AutoAdoptRunningClient(string.IsNullOrWhiteSpace(agentOwner) ? "ambient" : agentOwner);
                        if (existing != null)
                        {
                            targetRestartPid = existing.ProcessId;
                            targetRestartSlot = existing.SlotIndex;
                        }
                    }
                    if (targetRestartPid.HasValue)
                    {
                        if (!_processRegistry.ValidateAgentAccess(agentOwner, targetRestartPid.Value, out string? accessErr))
                        {
                            return ToolCallResult.Error(accessErr!);
                        }
                    }
                    StopClient(targetRestartPid, targetRestartSlot);
                    await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                    bool effectiveFullscreen = (string.Equals(windowMode, "headless", StringComparison.OrdinalIgnoreCase) ||
                                                string.Equals(windowMode, "offscreen", StringComparison.OrdinalIgnoreCase)) ? false : fullscreen;
                    return await LaunchClientAsync(mode, connectAddress, masterServer, resolution, effectiveFullscreen, maxFps, customClientPath, workingDirectory, username, windowMode, stealFocus, password, autoLogin, targetRestartSlot, agentOwner, launchProfile, enableReplayCapture, enforceFramePacing, cancellationToken).ConfigureAwait(false);
                }

                case "list":
                case "clients":
                {
                    var instances = (_processRegistry.GetAllClients() ?? _processRegistry.GetAll()) ?? Array.Empty<ClientInstance>();
                    return ToolCallResult.Json(new
                    {
                        status = "success",
                        action = "list",
                        totalActive = instances.Count,
                        totalManagedClients = instances.Count,
                        clients = instances.Select(i => new
                        {
                            processId = i.ProcessId,
                            slotIndex = i.SlotIndex,
                            offscreenX = i.OffscreenX,
                            offscreenY = i.OffscreenY,
                            pipeName = i.PipeName,
                            username = i.Username,
                            agentOwner = i.AgentOwner,
                            windowMode = i.WindowMode,
                            launchedAt = i.LaunchedAt.ToString("o")
                        }),
                        timestamp = _timeProvider.GetUtcNow().ToString("o")
                    });
                }

                case "login":
                {
                    return await LoginClientAsync(username, password, requestedProcessId, requestedSlot, cancellationToken).ConfigureAwait(false);
                }

                case "set_window_mode":
                case "window_mode":
                {
                    string targetMode = windowMode;
                    if (string.Equals(targetMode, "foreground", StringComparison.OrdinalIgnoreCase))
                    {
                        if (arguments.TryGetProperty("mode", out JsonElement mEl) && !string.IsNullOrWhiteSpace(mEl.GetString()))
                        {
                            targetMode = mEl.GetString()!.Trim().ToLowerInvariant();
                        }
                    }

                    Process? proc = GetRunningClientProcess();
                    if (proc == null || proc.HasExited)
                    {
                        return ToolCallResult.Error("No active game client process found to set window mode.");
                    }

                    IntPtr hwnd = proc.MainWindowHandle;
                    if (hwnd == IntPtr.Zero)
                    {
                        hwnd = WindowCapturer.FindWindowForProcessId(proc.Id);
                    }
                    if (hwnd == IntPtr.Zero)
                    {
                        return ToolCallResult.Error($"Client process PID {proc.Id} has no active MainWindowHandle.");
                    }

                    bool success = false;
                    string appliedMode = targetMode;
                    if (targetMode is "headless" or "offscreen" or "background")
                    {
                        appliedMode = "headless";
                        success = SetClientWindowHeadless(hwnd);
                        _logger($"Moved client window 0x{hwnd.ToInt64():X8} offscreen (headless mode). Success: {success}");
                    }
                    else if (targetMode is "foreground" or "normal" or "visible" or "onscreen")
                    {
                        appliedMode = "foreground";
                        success = SetClientWindowForeground(hwnd, 0, 0, 1920, 1080, activate: stealFocus);
                        _logger($"Moved client window 0x{hwnd.ToInt64():X8} to foreground. Success: {success}");
                    }
                    else if (targetMode is "minimized" or "minimize")
                    {
                        appliedMode = "minimized";
                        success = SetClientWindowMinimized(hwnd);
                        _logger($"Minimized client window 0x{hwnd.ToInt64():X8}. Success: {success}");
                    }
                    else
                    {
                        return ToolCallResult.Error($"Unknown window mode '{targetMode}'. Valid modes: 'headless', 'foreground', 'minimized'.");
                    }

                    return ToolCallResult.Json(new
                    {
                        status = success ? "success" : "warning",
                        action = "set_window_mode",
                        windowMode = appliedMode,
                        processId = proc.Id,
                        mainWindowHandle = $"0x{hwnd.ToInt64():X8}",
                        isOffscreen = IsWindowOffscreen(hwnd),
                        timestamp = _timeProvider.GetUtcNow().ToString("o")
                    });
                }

                case "logs":
                {
                    int lines = (arguments.TryGetProperty("lines", out JsonElement lElem) && lElem.TryGetInt32(out int lVal) && lVal > 0)
                        ? lVal
                        : (arguments.TryGetProperty("maxLines", out JsonElement maxElem) && maxElem.TryGetInt32(out int ml) && ml > 0)
                            ? ml
                            : 50;

                    string logType = arguments.TryGetProperty("logType", out JsonElement ltElem) && !string.IsNullOrWhiteSpace(ltElem.GetString())
                        ? ltElem.GetString()!.Trim().ToLowerInvariant()
                        : "console";

                    string level = arguments.TryGetProperty("level", out JsonElement lvlElem) && !string.IsNullOrWhiteSpace(lvlElem.GetString())
                        ? lvlElem.GetString()!.Trim().ToLowerInvariant()
                        : "all";

                    return await GetClientLogsAsync(lines, logType, level, workingDirectory, cancellationToken).ConfigureAwait(false);
                }

                case "command":
                {
                    if (!arguments.TryGetProperty("command", out JsonElement cmdElem) || string.IsNullOrWhiteSpace(cmdElem.GetString()))
                    {
                        return ToolCallResult.Error("Missing required parameter 'command' for action 'command'.");
                    }
                    string cmd = cmdElem.GetString()!.Trim();
                    int timeout = (arguments.TryGetProperty("timeoutMs", out JsonElement toElem) && toElem.TryGetInt32(out int parsedTo) && parsedTo > 0)
                        ? parsedTo
                        : 3000;
                    bool autoLaunch = GetAutoLaunch(arguments);
                    bool allowKeystroke = (arguments.TryGetProperty("allow_keystroke_fallback", out JsonElement akfEl) || arguments.TryGetProperty("allowKeystrokeFallback", out akfEl)) && akfEl.GetBoolean();
                    return await ExecuteClientCommandAsync(cmd, timeout, autoLaunch, allowKeystroke, requestedProcessId, cancellationToken).ConfigureAwait(false);
                }

                case "lua":
                {
                    string lua = string.Empty;
                    if (arguments.TryGetProperty("code", out JsonElement codeElem) && !string.IsNullOrWhiteSpace(codeElem.GetString()))
                    {
                        lua = codeElem.GetString()!.Trim();
                    }
                    else if (arguments.TryGetProperty("command", out JsonElement cmdElem) && !string.IsNullOrWhiteSpace(cmdElem.GetString()))
                    {
                        lua = cmdElem.GetString()!.Trim();
                    }
                    else if (arguments.TryGetProperty("trigger", out JsonElement trigElem) && !string.IsNullOrWhiteSpace(trigElem.GetString()))
                    {
                        lua = $"Trigger {trigElem.GetString()!.Trim()}";
                    }

                    if (string.IsNullOrWhiteSpace(lua))
                    {
                        return ToolCallResult.Error("Missing required parameter 'code' or 'trigger' for action 'lua'.");
                    }

                    if (!lua.StartsWith("Script ", StringComparison.OrdinalIgnoreCase) && !lua.StartsWith("Trigger ", StringComparison.OrdinalIgnoreCase))
                    {
                        lua = $"Script {lua}";
                    }

                    int timeout = (arguments.TryGetProperty("timeoutMs", out JsonElement toElem) && toElem.TryGetInt32(out int parsedTo) && parsedTo > 0)
                        ? parsedTo
                        : 3000;
                    bool autoLaunch = GetAutoLaunch(arguments);
                    bool allowKeystroke = (arguments.TryGetProperty("allow_keystroke_fallback", out JsonElement akfEl) || arguments.TryGetProperty("allowKeystrokeFallback", out akfEl)) && akfEl.GetBoolean();
                    return await ExecuteClientCommandAsync(lua, timeout, autoLaunch, allowKeystroke, requestedProcessId, cancellationToken).ConfigureAwait(false);
                }

                case "errors":
                case "history":
                {
                    int limit = (arguments.TryGetProperty("limit", out JsonElement limEl) && limEl.TryGetInt32(out int lVal) && lVal > 0)
                        ? lVal
                        : 10;
                    var errors = _remediationService.GetRecentErrors(limit);
                    return ToolCallResult.Json(new
                    {
                        status = "success",
                        action = "errors",
                        totalErrors = errors.Count,
                        errors = errors.Select(e => new
                        {
                            id = e.Id,
                            timestamp = e.Timestamp.ToString("o"),
                            command = e.Command,
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

                case "ai_diagnose":
                {
                    var errors = _remediationService.GetRecentErrors(10);
                    string? exitCode = arguments.TryGetProperty("exitCode", out JsonElement ecEl) ? ecEl.GetString() : null;
                    string? contextText = arguments.TryGetProperty("context", out JsonElement ctxEl) ? ctxEl.GetString() : null;

                    string promptContext = contextText ?? (errors.Count > 0
                        ? $"Recent Command Errors ({errors.Count}):\n" + string.Join("\n", errors.Select(e => $"[{e.Remediation.ErrorCategory}] Cmd: '{e.Command}' -> Error: {e.RawErrorResponse}"))
                        : "No recent command errors in memory buffer.");

                    string code = exitCode ?? (errors.Count > 0 ? errors[0].Remediation.ErrorCategory : "0xC0000005");
                    string? aiAnalysis = null;
                    bool samplingSupported = McpSamplingContext.Current?.IsSamplingSupported ?? false;
                    if (samplingSupported)
                    {
                        aiAnalysis = await McpSamplingContext.Current!.RequestCrashAnalysisAsync(code, promptContext, cancellationToken).ConfigureAwait(false);
                    }

                    return ToolCallResult.Json(new
                    {
                        status = "success",
                        action = "ai_diagnose",
                        exitCode = code,
                        samplingSupported,
                        aiAnalysis = aiAnalysis ?? "Sampling not available or no response from client LLM.",
                        recentErrorsCount = errors.Count,
                        timestamp = _timeProvider.GetUtcNow().ToString("o")
                    });
                }

                case "set_self_healing":
                case "toggle_self_healing":
                {
                    bool enabled = true;
                    if (arguments.TryGetProperty("enabled", out JsonElement enEl))
                    {
                        enabled = enEl.GetBoolean();
                    }
                    else
                    {
                        enabled = !_remediationService.IsEnabled;
                    }
                    _remediationService.IsEnabled = enabled;
                    return ToolCallResult.Json(new
                    {
                        status = "success",
                        action = "set_self_healing",
                        enabled = _remediationService.IsEnabled,
                        message = _remediationService.IsEnabled ? "Self-healing remediation is ENABLED." : "Self-healing remediation is DISABLED.",
                        timestamp = _timeProvider.GetUtcNow().ToString("o")
                    });
                }

                case "pipe_status":
                case "pipes":
                {
                    return await HandlePipeStatusAsync(cancellationToken).ConfigureAwait(false);
                }

                case "status":
                {
                    return await GetClientStatusAsync(requestedProcessId, cancellationToken).ConfigureAwait(false);
                }

                default:
                {
                    return ToolCallResult.Json(new
                    {
                        status = "error",
                        action,
                        running = false,
                        errorMessage = $"Unknown action '{action}' for kongor_game_client_manage. Valid actions: status, launch, start, stop, restart, list, login, set_window_mode, logs, command, lua, errors, ai_diagnose, set_self_healing, pipes.",
                        timestamp = _timeProvider.GetUtcNow().ToString("o")
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger($"[ERROR] Exception executing game client manage action '{action}': {ex}");
            return ToolCallResult.Error($"Failed executing game client manage action '{action}': {ex.Message}");
        }
    }
}
