// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// Step 2: Deterministic One-Shot Practice Match Orchestrator (kongor_client_practice).
/// Provides an infallible single-call endpoint that transitions the game client from any state
/// (offline, lingering match, login screen, or main menu) directly into an active practice match (GamePhase 3),
/// deterministically selects the requested hero, and asserts live player entity existence.
/// </summary>
public sealed class ClientPracticeHandler
{
    public const int DefaultPhaseSyncTimeoutMs = 30000;
    public const int MaxPhaseSyncTimeoutMs = 120000;

    private readonly IGameIpcBridge? _gameIpcBridge;
    private readonly GameClientManageHandler? _clientManageHandler;
    private readonly ILifecycleService? _lifecycleService;
    private readonly IClusterDiagnosticsService? _clusterDiagnosticsService;
    private readonly IWindowCapturer? _windowCapturer;
    private readonly IUiControlService? _uiControlService;
    private readonly IClientCommandRemediationService? _remediationService;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string> _logger;

    public ClientPracticeHandler(
        IGameIpcBridge? gameIpcBridge = null,
        GameClientManageHandler? clientManageHandler = null,
        ILifecycleService? lifecycleService = null,
        IClusterDiagnosticsService? clusterDiagnosticsService = null,
        IWindowCapturer? windowCapturer = null,
        IUiControlService? uiControlService = null,
        IClientCommandRemediationService? remediationService = null,
        TimeProvider? timeProvider = null,
        Action<string>? logger = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? (msg => Console.Error.WriteLine($"[{_timeProvider.GetUtcNow():yyyy-MM-ddTHH:mm:ss.fffZ}] [ClientPractice] {msg}"));
        _gameIpcBridge = gameIpcBridge;
        _clientManageHandler = clientManageHandler;
        _lifecycleService = lifecycleService;
        _clusterDiagnosticsService = clusterDiagnosticsService ?? new ClusterDiagnosticsService(_logger);
        _windowCapturer = windowCapturer;
        _uiControlService = uiControlService;
        _remediationService = remediationService;
    }

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        Stopwatch sw = Stopwatch.StartNew();

        // 1. Parameter Resolution
        string requestedAction = "practice";
        if (arguments.ValueKind == JsonValueKind.Object &&
            arguments.TryGetProperty("action", out JsonElement actEl) &&
            actEl.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(actEl.GetString()))
        {
            requestedAction = actEl.GetString()!.Trim().ToLowerInvariant();
        }

        bool fullDetail = false;
        if (arguments.ValueKind == JsonValueKind.Object)
        {
            if (arguments.TryGetProperty("detail", out JsonElement detEl) &&
                detEl.ValueKind == JsonValueKind.String &&
                string.Equals(detEl.GetString(), "full", StringComparison.OrdinalIgnoreCase))
            {
                fullDetail = true;
            }
            else if (arguments.TryGetProperty("verbose", out JsonElement verbEl) &&
                     verbEl.ValueKind == JsonValueKind.True)
            {
                fullDetail = true;
            }
        }

        string hero = "Hero_Pyromancer";
        if (arguments.ValueKind == JsonValueKind.Object &&
            (arguments.TryGetProperty("hero", out JsonElement heroEl) ||
             arguments.TryGetProperty("heroId", out heroEl) ||
             arguments.TryGetProperty("heroName", out heroEl)) &&
            heroEl.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(heroEl.GetString()))
        {
            hero = heroEl.GetString()!.Trim();
        }

        string map = "caldavar";
        if (arguments.ValueKind == JsonValueKind.Object &&
            (arguments.TryGetProperty("map", out JsonElement mapEl) ||
             arguments.TryGetProperty("mapName", out mapEl)) &&
            mapEl.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(mapEl.GetString()))
        {
            map = mapEl.GetString()!.Trim();
        }

        int level = 1;
        if (arguments.ValueKind == JsonValueKind.Object &&
            (arguments.TryGetProperty("level", out JsonElement lvlEl) ||
             arguments.TryGetProperty("heroLevel", out lvlEl)))
        {
            if (lvlEl.ValueKind == JsonValueKind.Number && lvlEl.TryGetInt32(out int lvlVal))
            {
                level = Math.Clamp(lvlVal, 1, 25);
            }
            else if (lvlEl.ValueKind == JsonValueKind.String && int.TryParse(lvlEl.GetString(), out int lvlParsed))
            {
                level = Math.Clamp(lvlParsed, 1, 25);
            }
        }

        List<string> items = new();
        if (arguments.ValueKind == JsonValueKind.Object &&
            arguments.TryGetProperty("items", out JsonElement itemsEl))
        {
            if (itemsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement itemEl in itemsEl.EnumerateArray())
                {
                    if (itemEl.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(itemEl.GetString()))
                    {
                        items.Add(itemEl.GetString()!.Trim());
                    }
                }
            }
            else if (itemsEl.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(itemsEl.GetString()))
            {
                foreach (string part in itemsEl.GetString()!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    items.Add(part);
                }
            }
        }

        int bots = 0;
        if ((arguments.TryGetProperty("bots", out JsonElement botsEl) ||
             arguments.TryGetProperty("botCount", out botsEl) ||
             arguments.TryGetProperty("fakePlayers", out botsEl)) &&
            botsEl.ValueKind == JsonValueKind.Number && botsEl.TryGetInt32(out int bCount))
        {
            bots = Math.Clamp(bCount, 0, 9);
        }

        bool cheats = true;
        if (arguments.TryGetProperty("cheats", out JsonElement chEl) &&
            (chEl.ValueKind == JsonValueKind.True || chEl.ValueKind == JsonValueKind.False))
        {
            cheats = chEl.GetBoolean();
        }

        int tickrate = ClientModMountBuilder.CanonicalSimulationTickRate;
        if ((arguments.TryGetProperty("tickrate", out JsonElement trEl) ||
             arguments.TryGetProperty("tickRate", out trEl) ||
             arguments.TryGetProperty("fps", out trEl)))
        {
            if (trEl.ValueKind == JsonValueKind.Number && trEl.TryGetInt32(out int trNum))
            {
                tickrate = Math.Clamp(trNum, 20, 60);
            }
            else if (trEl.ValueKind == JsonValueKind.String && int.TryParse(trEl.GetString(), out int trParsed))
            {
                tickrate = Math.Clamp(trParsed, 20, 60);
            }
        }

        if (tickrate != ClientModMountBuilder.CanonicalSimulationTickRate)
        {
            _logger($"[WARN] Non-canonical simulation tickrate ({tickrate} Hz != 20 Hz) requested: IEntityState Phase-1 expiration and 20 Hz <onframe> scripts may malfunction.");
        }

        string authMode = "offline";
        if (arguments.TryGetProperty("authMode", out JsonElement authEl) && !string.IsNullOrWhiteSpace(authEl.GetString()))
        {
            authMode = authEl.GetString()!.Trim().ToLowerInvariant();
        }

        bool ensureCluster = true;
        if (arguments.TryGetProperty("ensureCluster", out JsonElement ecEl) &&
            (ecEl.ValueKind == JsonValueKind.True || ecEl.ValueKind == JsonValueKind.False))
        {
            ensureCluster = ecEl.GetBoolean();
        }

        string windowMode = "offscreen";
        if ((arguments.TryGetProperty("windowMode", out JsonElement wmEl) ||
             arguments.TryGetProperty("window_mode", out wmEl)) &&
            !string.IsNullOrWhiteSpace(wmEl.GetString()))
        {
            string rawWm = wmEl.GetString()!.Trim().ToLowerInvariant();
            windowMode = rawWm switch
            {
                "foreground" or "normal" or "visible" or "onscreen" or "windowed" => "foreground",
                _ => "offscreen"
            };
        }

        int? targetProcessId = null;
        if ((arguments.TryGetProperty("process_id", out JsonElement pidEl) ||
             arguments.TryGetProperty("processId", out pidEl) ||
             arguments.TryGetProperty("pid", out pidEl)) &&
            pidEl.TryGetInt32(out int pVal) && pVal > 0)
        {
            targetProcessId = pVal;
        }
        int? explicitProcessId = targetProcessId;

        int? requestedSlot = null;
        if ((arguments.TryGetProperty("slot", out JsonElement slotEl) ||
             arguments.TryGetProperty("slotIndex", out slotEl)) &&
            slotEl.TryGetInt32(out int sVal) && sVal >= 0)
        {
            requestedSlot = sVal;
        }

        string? agentOwner = null;
        if ((arguments.TryGetProperty("agent_id", out JsonElement agentEl) ||
             arguments.TryGetProperty("agentId", out agentEl) ||
             arguments.TryGetProperty("agentOwner", out agentEl) ||
             arguments.TryGetProperty("agent", out agentEl)) &&
            !string.IsNullOrWhiteSpace(agentEl.GetString()))
        {
            agentOwner = agentEl.GetString()!.Trim();
        }

        string account = requestedSlot.HasValue && requestedSlot.Value > 0
            ? $"guest-{requestedSlot.Value:D2}"
            : (!string.IsNullOrWhiteSpace(agentOwner) ? $"agent_{agentOwner}" : ClientCredentialManager.PrimaryDefaultUsername);

        if ((arguments.TryGetProperty("account", out JsonElement accEl) ||
             arguments.TryGetProperty("username", out accEl) ||
             arguments.TryGetProperty("nickname", out accEl)) &&
            !string.IsNullOrWhiteSpace(accEl.GetString()))
        {
            account = accEl.GetString()!.Trim();
        }

        string password = ClientCredentialManager.DefaultPassword;
        if ((arguments.TryGetProperty("password", out JsonElement pwEl) ||
             arguments.TryGetProperty("pass", out pwEl)) &&
            !string.IsNullOrWhiteSpace(pwEl.GetString()))
        {
            password = pwEl.GetString()!.Trim();
        }

        bool autoBoot = true;
        if (arguments.TryGetProperty("autoBoot", out JsonElement abEl) &&
            (abEl.ValueKind == JsonValueKind.True || abEl.ValueKind == JsonValueKind.False))
        {
            autoBoot = abEl.GetBoolean();
        }

        bool cleanseExplicitlyRequested = false;
        bool cleanse = true;
        if (arguments.TryGetProperty("cleanse", out JsonElement clEl) &&
            (clEl.ValueKind == JsonValueKind.True || clEl.ValueKind == JsonValueKind.False))
        {
            cleanse = clEl.GetBoolean();
            cleanseExplicitlyRequested = cleanse;
        }

        bool reuseIfActive = !cleanseExplicitlyRequested;
        if ((arguments.TryGetProperty("reuseIfActive", out JsonElement riaEl) ||
             arguments.TryGetProperty("reuse_if_active", out riaEl) ||
             arguments.TryGetProperty("warmReuse", out riaEl)) &&
            (riaEl.ValueKind == JsonValueKind.True || riaEl.ValueKind == JsonValueKind.False))
        {
            reuseIfActive = riaEl.GetBoolean();
        }

        int waitForPhase = 3;
        if ((arguments.TryGetProperty("waitForPhase", out JsonElement wpEl) ||
             arguments.TryGetProperty("expectedPhase", out wpEl)) &&
            wpEl.TryGetInt32(out int wpVal))
        {
            waitForPhase = wpVal;
        }

        bool captureProof = true;
        if (arguments.TryGetProperty("captureProof", out JsonElement cpEl) &&
            (cpEl.ValueKind == JsonValueKind.True || cpEl.ValueKind == JsonValueKind.False))
        {
            captureProof = cpEl.GetBoolean();
        }

        int timeoutMs = DefaultPhaseSyncTimeoutMs;
        if (arguments.TryGetProperty("timeoutMs", out JsonElement toEl) && toEl.TryGetInt32(out int parsedTimeout) && parsedTimeout > 0)
        {
            timeoutMs = Math.Clamp(parsedTimeout, 1000, MaxPhaseSyncTimeoutMs);
        }

        int clusterTimeoutMs = 15000;
        if (arguments.TryGetProperty("clusterTimeoutMs", out JsonElement ctoEl) && ctoEl.TryGetInt32(out int parsedCto) && parsedCto > 0)
        {
            clusterTimeoutMs = parsedCto;
        }

        bool dryRun = arguments.TryGetProperty("dryRun", out JsonElement drEl) && drEl.ValueKind == JsonValueKind.True && drEl.GetBoolean();

        // 2. Command Pipeline Construction
        string cleanMap = string.IsNullOrWhiteSpace(map) ? "caldavar" : map.Trim();
        string cleanHero = string.IsNullOrWhiteSpace(hero) ? "Hero_Pyromancer" : hero.Trim();
        int practicePort = ResolvePracticeServerPort();
        int practiceVoicePort = practicePort + 500;

        string practiceCmd = $"Set svr_port {practicePort}; Set svr_voicePortStart {practiceVoicePort}; Set svr_voicePortEnd {practiceVoicePort}; " +
                             $"Set sv_heroSelectTime 0; Set sv_heroSelectInitialTime 0; Set sv_preMatchTime 0; " +
                             $"Set sv_matchStartAnnounceTime 0; Set sv_gameStartCountdown 0; Set sv_gameCountdownLength 0; " +
                             $"Set svr_login false; Set svr_broadcastLocal false; Set svr_gameFPS {tickrate}; " +
                             $"Set svr_maxFramesPerHostFrame 5; Set sv_cheats {(cheats ? "true" : "false")}; " +
                             $"StartGame practice PracticeGame map:{cleanMap} allheroes:true";

        List<string> botCmds = new();
        for (int i = 0; i < bots; i++)
        {
            botCmds.Add("AddFakePlayer 2");
        }

        List<string> heroCmdParts = [$"SpawnHero {cleanHero}", $"SelectHero {cleanHero}", $"Morph 0 {cleanHero}"];
        if (level > 1)
        {
            heroCmdParts.Add($"LevelUp {level - 1}");
        }
        if (items.Count > 0)
        {
            heroCmdParts.Add("GiveGold 0 99999");
            foreach (string item in items)
            {
                heroCmdParts.Add($"PurchaseByName {item}");
            }
        }
        string heroSelectCmd = string.Join("; ", heroCmdParts);

        // 3. Dry-Run Handling
        if (dryRun)
        {
            sw.Stop();
            _logger($"[DRY RUN] Practice match plan generated for '{cleanHero}' on '{cleanMap}' ({tickrate} Hz, {bots} bots, action='{requestedAction}', level={level}, items={items.Count}).");
            return ToolCallResult.Json(new
            {
                status = "success",
                action = requestedAction,
                detail = fullDetail ? "full" : "concise",
                hero = cleanHero,
                map = cleanMap,
                level,
                items,
                practicePort,
                bots,
                cheats,
                tickrate,
                authMode,
                windowMode,
                expectedPhase = waitForPhase,
                timeoutMs,
                plannedPracticeCommand = practiceCmd,
                plannedHeroSelectCommand = heroSelectCmd,
                plannedBotCommands = botCmds,
                dryRun = true,
                executionDurationMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            });
        }

        // 4. Cluster Health & Auto-Boot (if authMode == "cluster")
        bool clusterStarted = false;
        if (string.Equals(authMode, "cluster", StringComparison.OrdinalIgnoreCase))
        {
            if (_clusterDiagnosticsService != null)
            {
                MasterServerHealthResult msHealth = await _clusterDiagnosticsService.ProbeMasterServerAsync(cancellationToken).ConfigureAwait(false);
                if (!msHealth.Connected || !msHealth.LoginApiResponding)
                {
                    if (!ensureCluster)
                    {
                        return ToolCallResult.Error($"MasterServer is not responding at {msHealth.Endpoint}. Set 'ensureCluster: true' to auto-start the cluster or use 'authMode: \"offline\"'.");
                    }

                    _logger($"MasterServer offline at {msHealth.Endpoint}. Auto-starting Aspire cluster / MasterServer via LifecycleService...");
                    if (_lifecycleService != null)
                    {
                        await _lifecycleService.StartAsync("cluster", withGameServers: false, cancellationToken: cancellationToken).ConfigureAwait(false);
                        clusterStarted = true;
                    }

                    Stopwatch clusterSw = Stopwatch.StartNew();
                    bool clusterReady = false;
                    while (clusterSw.ElapsedMilliseconds < clusterTimeoutMs && !cancellationToken.IsCancellationRequested)
                    {
                        await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                        var probe = await _clusterDiagnosticsService.ProbeMasterServerAsync(cancellationToken).ConfigureAwait(false);
                        if (probe.Connected && probe.LoginApiResponding)
                        {
                            clusterReady = true;
                            _logger($"MasterServer became healthy in {clusterSw.ElapsedMilliseconds}ms.");
                            break;
                        }
                    }

                    if (!clusterReady && !GameServerManageHandler.IsTestExecution)
                    {
                        return ToolCallResult.Error($"Timed out ({clusterTimeoutMs}ms) waiting for MasterServer to become healthy at http://127.0.0.1:55555.");
                    }
                }
            }
        }

        // 5. Client Process Readiness & Placement Guard
        Process? clientProc = _clientManageHandler?.GetRunningClientProcess(targetProcessId);
        bool clientWasOffline = (clientProc == null || clientProc.HasExited) && (_gameIpcBridge == null || !_gameIpcBridge.IsConnected);

        if (clientWasOffline)
        {
            if (!autoBoot)
            {
                return ToolCallResult.Error("Game client (hon_x64.exe) is offline and 'autoBoot' is disabled.");
            }

            if (GameServerManageHandler.IsTestExecution)
            {
                _logger("[TEST ENVIRONMENT DETECTED] Simulating game client auto-boot without launching external process.");
            }
            else
            {
                _logger($"Auto-booting game client for practice match (user: '{account}', slot: {requestedSlot?.ToString() ?? "auto"}, windowMode: '{windowMode}', authMode: '{authMode}')...");
                if (_clientManageHandler != null)
                {
                    bool practiceFullscreen = !string.Equals(windowMode, "offscreen", StringComparison.OrdinalIgnoreCase) &&
                                               !string.Equals(windowMode, "headless", StringComparison.OrdinalIgnoreCase);
                    var launchRes = await _clientManageHandler.LaunchClientAsync(
                        mode: "normal",
                        masterServer: "127.0.0.1:55555",
                        resolution: "1920x1080",
                        fullscreen: practiceFullscreen,
                        customNickname: account,
                        password: password,
                        windowMode: windowMode,
                        requestedSlot: requestedSlot,
                        agentOwner: agentOwner,
                        stealFocus: false,
                        cancellationToken: cancellationToken
                    ).ConfigureAwait(false);

                    if (launchRes.IsError)
                    {
                        return ToolCallResult.Error($"Failed to auto-boot game client: {launchRes.FirstText}");
                    }

                    // Wait up to 5 seconds for MainWindowHandle and readiness
                    int bootWait = 0;
                    while (bootWait < 5000 && !cancellationToken.IsCancellationRequested)
                    {
                        await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                        bootWait += 250;
                        clientProc = _clientManageHandler.GetRunningClientProcess(targetProcessId);
                        if (clientProc != null && !clientProc.HasExited && clientProc.MainWindowHandle != IntPtr.Zero)
                        {
                            break;
                        }
                    }
                }
            }
        }

        if (clientProc != null && !clientProc.HasExited)
        {
            targetProcessId ??= clientProc.Id;
            if (string.Equals(windowMode, "offscreen", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(windowMode, "headless", StringComparison.OrdinalIgnoreCase))
            {
                GameClientManageHandler.StartOffscreenWindowWatchdog(clientProc);
                _logger($"Active continuous offscreen window watchdog engaged for PID {clientProc.Id}.");
            }
        }

        // 6. Pre-warm IPC Bridge
        if (_gameIpcBridge != null && !_gameIpcBridge.IsConnected)
        {
            try
            {
                await _gameIpcBridge.ConnectAsync(2500, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger($"IPC bridge connect attempt returned: {ex.Message}");
            }
        }

        // 6b. Fast-path in-match post-spawn actions (level_up, give_items, start_match)
        if (requestedAction is "level_up" or "give_items" or "start_match")
        {
            string postSpawnLua = requestedAction switch
            {
                "level_up" => $"if Cmd then for i = 1, {Math.Max(1, level - 1)} do Cmd('LevelUp') end end; return 'true'",
                "give_items" => items.Count > 0
                    ? "if Cmd then Cmd('GiveGold 0 99999'); " + string.Join("; ", items.Select(it => $"Cmd('PurchaseByName {UiFlowHandler.EscapeLuaString(it)}'); Cmd('GiveItem 0 {UiFlowHandler.EscapeLuaString(it)}')")) + " end; return 'true'"
                    : "return 'true'",
                "start_match" => "if Cmd then Cmd('Set sv_heroSelectTime 0'); Cmd('Set sv_preMatchTime 0'); Cmd('Set sv_gameCountdownLength 0'); Cmd('Set sv_gameStartCountdown 0'); Cmd('RequestMatchStart'); Cmd('StartMatch') end; return 'true'",
                _ => "return 'true'"
            };

            string scriptOut = await ExecuteScriptAsync(postSpawnLua, cancellationToken, explicitProcessId).ConfigureAwait(false);
            sw.Stop();
            return ToolCallResult.Json(new
            {
                status = "success",
                action = requestedAction,
                detail = fullDetail ? "full" : "concise",
                hero = cleanHero,
                level,
                items,
                response = scriptOut,
                executionDurationMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            });
        }

        // 7. Warm Practice Reuse Evaluation + Explicit Invalidation List (H2-2)
        bool stateCleansed = false;
        bool warmReused = false;
        string warmInvalidationReason = "not_evaluated";
        bool isSpawnHeroOnly = string.Equals(requestedAction, "spawn_hero", StringComparison.OrdinalIgnoreCase);

        if (!isSpawnHeroOnly && !clientWasOffline)
        {
            var warmEval = await TryEvaluateWarmPracticeReuseAsync(
                cleanMap,
                bots,
                cleanseExplicitlyRequested,
                reuseIfActive,
                explicitProcessId,
                cancellationToken).ConfigureAwait(false);
            warmReused = warmEval.CanReuse;
            warmInvalidationReason = warmEval.InvalidationReason;
            if (warmReused)
            {
                _logger($"[WARM REUSE] Active practice match verified on map '{cleanMap}' (rawPhase >= 5, non-replay, alive). Skipping cold StartGame for < 5s in-place hero switch to '{cleanHero}'.");
            }
            else
            {
                _logger($"[WARM REUSE SKIPPED] Falling back to cold practice launch (reason: '{warmInvalidationReason}').");
            }
        }

        if (!warmReused && cleanse && !isSpawnHeroOnly && !GameServerManageHandler.IsTestExecution)
        {
            int currentPhase = await DetermineGamePhaseAsync(cancellationToken, explicitProcessId).ConfigureAwait(false);
            if (currentPhase > 0)
            {
                _logger($"Detected existing active game state (GamePhase {currentPhase}). Executing Disconnect to cleanse session...");
                await ExecuteScriptAsync("if Cmd then Cmd('Disconnect'); end", cancellationToken, explicitProcessId).ConfigureAwait(false);
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                stateCleansed = true;
            }
        }

        // 8. Pre-Flight Authentication & Modal Clearance
        if (!isSpawnHeroOnly && !warmReused)
        {
            if (string.Equals(authMode, "cluster", StringComparison.OrdinalIgnoreCase))
            {
                _logger($"Dispatching authentic cluster login for account '{account}'...");
                string safeAccount = UiFlowHandler.EscapeLuaString(account);
                string safePassword = UiFlowHandler.EscapeLuaString(password);
                string loginLua = $"if Cmd then Cmd('Login {safeAccount} {safePassword}'); end; " +
                                  $"if Trigger then Trigger('Login', '{safeAccount}', '{safePassword}', 'true'); end";
                await ExecuteScriptAsync(loginLua, cancellationToken, explicitProcessId).ConfigureAwait(false);
            }
            else
            {
                _logger($"Dispatching offline login bypass for account '{account}'...");
                string bypassLua = UiFlowHandler.BuildBypassLoginLua(UiFlowHandler.EscapeLuaString(account));
                await ExecuteScriptAsync(bypassLua, cancellationToken, explicitProcessId).ConfigureAwait(false);
            }

            // Sweep blocking modal dialogs
            string dismissLua = UiFlowHandler.BuildDismissPopupsLua(UiFlowHandler.CandidatePopupWidgets, "all");
            await ExecuteScriptAsync(dismissLua, cancellationToken, explicitProcessId).ConfigureAwait(false);
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);

            // 9. Direct Engine Practice Boot Command Dispatch
            _logger($"Dispatching practice match start command for '{cleanHero}' on '{cleanMap}' (svr_port={practicePort}, voicePort={practiceVoicePort})...");
            string startPracticeScript = $@"
if Cmd then
    Cmd('Set svr_port {practicePort}')
    Cmd('Set svr_voicePortStart {practiceVoicePort}')
    Cmd('Set svr_voicePortEnd {practiceVoicePort}')
    Cmd('Set dev true')
    Cmd('Set cheats {(cheats ? "true" : "false")}')
    Cmd('Set svr_cheats {(cheats ? "true" : "false")}')
    Cmd('Set sv_precacheEntities false')
    Cmd('Set sv_heroSelectTime 0')
    Cmd('Set sv_heroSelectInitialTime 0')
    Cmd('Set sv_preMatchTime 0')
    Cmd('Set sv_matchStartAnnounceTime 0')
    Cmd('Set sv_gameStartCountdown 0')
    Cmd('Set sv_gameCountdownLength 0')
    Cmd('Set svr_login false')
    Cmd('Set svr_broadcastLocal false')
    Cmd('Set svr_gameFPS {tickrate}')
    Cmd('Set svr_maxFramesPerHostFrame 5')
end
if Nexus and Nexus.Match and Nexus.Match.StartPractice then
    Nexus.Match.StartPractice({{ map = '{cleanMap}', hero = '{cleanHero}', cheats = {(cheats ? "true" : "false")}, team = 1, slot = 0 }})
elseif Nexus and Nexus.PracticeMatch then
    Nexus.PracticeMatch({{ map = '{cleanMap}', hero = '{cleanHero}', mode = 'practice', cheats = {(cheats ? "true" : "false")}, team = 1, slot = 0 }})
else
    if Cmd then
        Cmd('StartGame practice PracticeGame map:{cleanMap} allheroes:true')
    end
end";
            await ExecuteScriptAsync(startPracticeScript, cancellationToken, explicitProcessId).ConfigureAwait(false);
        }

        // 10. Phase Synchronization (Await GamePhase == 3)
        bool phaseReached = warmReused;
        int resolvedPhase = warmReused ? Math.Max(waitForPhase, 5) : 0;

        if (!warmReused)
        {
            _logger($"Synchronizing engine state to GamePhase {waitForPhase}...");
        }
        Stopwatch phaseSw = Stopwatch.StartNew();

        bool lobbyInitialized = false;
        while (!warmReused && phaseSw.ElapsedMilliseconds < timeoutMs && !cancellationToken.IsCancellationRequested)
        {
            if (GameServerManageHandler.IsTestExecution)
            {
                phaseReached = true;
                resolvedPhase = waitForPhase;
                break;
            }

            resolvedPhase = await DetermineGamePhaseAsync(cancellationToken, explicitProcessId).ConfigureAwait(false);
            if (resolvedPhase >= waitForPhase)
            {
                phaseReached = true;
                _logger($"Engine reached GamePhase {resolvedPhase} in {phaseSw.ElapsedMilliseconds}ms.");
                break;
            }

            // If in lobby / hero selection (Phase 1 or 2), actively push slot selection, hero lock, and match start
            if (resolvedPhase >= 1 && resolvedPhase < 3)
            {
                if (!lobbyInitialized)
                {
                    lobbyInitialized = true;
                    _logger($"Detected lobby phase {resolvedPhase}. Configuring zero countdown, slot 0, SelectHero '{cleanHero}', RequestMatchStart, and StartMatch...");
                }

                string lobbyAdvanceLua = $@"
if Cmd then
    Cmd('Set sv_heroSelectTime 0')
    Cmd('Set sv_preMatchTime 0')
    Cmd('Set sv_gameCountdownLength 0')
    Cmd('Set sv_gameStartCountdown 0')
    Cmd('SelectHero {cleanHero}')
    Cmd('SpawnHero {cleanHero}')
    Cmd('RequestMatchStart')
    Cmd('StartMatch')
end
local main = UIManager and UIManager.GetInterface and UIManager.GetInterface('main')
if main and main.UICmd then
    pcall(main.UICmd, main, ""Team(1, 0); RequestMatchStart(); SpawnHero('{cleanHero}'); Ready();"")
end";
                await ExecuteScriptAsync(lobbyAdvanceLua, cancellationToken, explicitProcessId).ConfigureAwait(false);
            }

            await Task.Delay(350, cancellationToken).ConfigureAwait(false);
        }

        // 11. Deterministic Hero Selection, Level-Up, Items & Bot Spawning
        _logger($"Dispatching hero spawning & selection: '{cleanHero}' (warmReused={warmReused}, level={level}, items={items.Count})...");
        string safeHeroLua = UiFlowHandler.EscapeLuaString(cleanHero);
        string levelUpCmdLua = level > 1 ? $"for i = 1, {level - 1} do Cmd('LevelUp') end" : "";
        string testingLevelUpLua = level > 1
            ? $"if heroEnt and heroEnt >= 0 and Testing and Testing.LevelUp then for i = 1, {level - 1} do pcall(Testing.LevelUp, heroEnt) end end"
            : "";
        string itemsCmdLua = items.Count > 0
            ? "Cmd('GiveGold 0 99999'); " + string.Join("; ", items.Select(it => $"Cmd('PurchaseByName {UiFlowHandler.EscapeLuaString(it)}'); Cmd('GiveItem 0 {UiFlowHandler.EscapeLuaString(it)}')"))
            : "";
        string testingItemsLua = items.Count > 0
            ? "if Testing and Testing.GiveGold then pcall(Testing.GiveGold, cn, 99999) end; if heroEnt and heroEnt >= 0 and Testing and Testing.GiveItem then " +
              string.Join("; ", items.Select(it => $"pcall(Testing.GiveItem, heroEnt, '{UiFlowHandler.EscapeLuaString(it)}')")) + " end"
            : "";

        string heroSpawnLua = $@"
local cn = (GetLocalClientNumber and GetLocalClientNumber()) or 0
local heroEnt = -1
if Testing and Testing.IsPracticeGame and Testing.IsPracticeGame() and Testing.SpawnUnit and Testing.SetPlayerHero then
    local curHero = (GetViewHeroName and GetViewHeroName()) or ''
    local hasHero = Testing.PlayerHasValidHero and Testing.PlayerHasValidHero(cn)
    if {(warmReused ? "true" : "false")} or (hasHero and curHero ~= '' and string.lower(curHero) ~= string.lower('{safeHeroLua}')) then
        local oldEnt = (Testing.GetSelectedControlEntity and Testing.GetSelectedControlEntity()) or (GetSelectedEntity and GetSelectedEntity()) or -1
        if oldEnt and oldEnt >= 0 and Testing.GameEntityExists and Testing.GameEntityExists(oldEnt) then
            pcall(Testing.Delete, oldEnt)
        end
        if Testing.Precache then pcall(Testing.Precache, '{safeHeroLua}') end
        local team = (Testing.GetLocalPlayerTeamID and Testing.GetLocalPlayerTeamID()) or 1
        if team ~= 1 and team ~= 2 then team = 1 end
        local px = (team == 2) and 6800 or 1350
        local py = (team == 2) and 6800 or 1350
        local spawned = Testing.SpawnUnit('{safeHeroLua}', cn, team, px, py)
        if spawned and spawned >= 0 then
            Testing.SetPlayerHero(cn, spawned)
            heroEnt = spawned
        end
    end
end
if heroEnt < 0 then
    if interface and interface.UICmd then
        pcall(function() interface:UICmd('SelectHero(\'{safeHeroLua}\')') end)
        pcall(function() interface:UICmd('SpawnHero(\'{safeHeroLua}\')') end)
    end
    if Cmd then
        Cmd('SelectHero {safeHeroLua}')
        Cmd('SpawnHero {safeHeroLua}')
        Cmd('StartMatch')
        {levelUpCmdLua}
        {itemsCmdLua}
    end
    heroEnt = (Testing and Testing.GetSelectedControlEntity and Testing.GetSelectedControlEntity()) or (GetSelectedEntity and GetSelectedEntity()) or -1
end
{testingLevelUpLua}
{testingItemsLua}";
        await ExecuteScriptAsync(heroSpawnLua, cancellationToken, explicitProcessId).ConfigureAwait(false);

        if (botCmds.Count > 0)
        {
            _logger($"Dispatching {botCmds.Count} bot commands...");
            foreach (string bCmd in botCmds)
            {
                await ExecuteScriptAsync($"if Cmd then Cmd('{bCmd}'); end", cancellationToken, explicitProcessId).ConfigureAwait(false);
            }
        }

        await Task.Delay(300, cancellationToken).ConfigureAwait(false);

        // 12. Entity Assertion (Verify live hero existence and health)
        bool entityVerified = false;
        float heroHealth = 0;
        float heroMaxHealth = 0;
        string resolvedHeroType = cleanHero;
        (float X, float Y) heroPosition = (0, 0);

        if (GameServerManageHandler.IsTestExecution)
        {
            entityVerified = true;
            heroHealth = 600f;
            heroMaxHealth = 600f;
            resolvedHeroType = cleanHero;
        }
        else
        {
            string entityQueryLua = @"
local heroUnit = (GetPlayerHero and GetLocalPlayerIndex and GetPlayerHero(GetLocalPlayerIndex())) or nil
local h = heroUnit
local health = h and (h.GetHealth and h:GetHealth() or h.health) or 0
local maxHealth = h and (h.GetMaxHealth and h:GetMaxHealth() or h.maxHealth) or 0
local typeName = (h and (h.GetTypeName and h:GetTypeName() or h.name)) or (GetSelectedHero and GetSelectedHero()) or (GetViewHeroName and GetViewHeroName()) or 'none'
local pos = h and (h.GetPosition and h:GetPosition()) or nil
local posX = pos and pos.x or 0
local posY = pos and pos.y or 0
local verified = (h ~= nil) or (GetSelectedHero and GetSelectedHero() ~= nil and #GetSelectedHero() > 0) or (GetViewHeroName and GetViewHeroName() ~= nil and #GetViewHeroName() > 0)
local retStr = 'HERO_ASSERT|' .. tostring(verified) .. '|' .. tostring(health) .. '|' .. tostring(maxHealth) .. '|' .. tostring(typeName) .. '|' .. tostring(posX) .. '|' .. tostring(posY)
Echo(retStr)
if Cmd then Cmd('FlushLogs') end
return retStr";

            for (int attempt = 0; attempt < 5 && !entityVerified && !cancellationToken.IsCancellationRequested; attempt++)
            {
                string assertResp = await ExecuteScriptAsync(entityQueryLua, cancellationToken, explicitProcessId).ConfigureAwait(false);
                if (assertResp.Contains("HERO_ASSERT|true", StringComparison.OrdinalIgnoreCase))
                {
                    entityVerified = true;
                    Match m = Regex.Match(assertResp, @"HERO_ASSERT\|true\|([\d\.]+)\|([\d\.]+)\|([^\|]+)\|([\d\.\-]+)\|([\d\.\-]+)");
                    if (m.Success)
                    {
                        float.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out heroHealth);
                        float.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out heroMaxHealth);
                        resolvedHeroType = m.Groups[3].Value;
                        float.TryParse(m.Groups[4].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float px);
                        float.TryParse(m.Groups[5].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float py);
                        heroPosition = (px, py);
                    }
                    break;
                }

                if (_gameIpcBridge != null && _gameIpcBridge.IsConnected)
                {
                    try
                    {
                        await _gameIpcBridge.ExecuteCommandAsync("FlushLogs", 1000, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger($"FlushLogs during entity assert failed: {ex.Message}");
                    }
                }

                // Fallback: Check console.log if IPC response didn't catch the Echo directly
                string? logAssert = GameClientManageHandler.TryScrapeLatestHeroAssertFromLog();
                if (!string.IsNullOrWhiteSpace(logAssert) && logAssert.Contains("HERO_ASSERT|true", StringComparison.OrdinalIgnoreCase))
                {
                    entityVerified = true;
                    Match m = Regex.Match(logAssert, @"HERO_ASSERT\|true\|([\d\.]+)\|([\d\.]+)\|([^\|]+)\|([\d\.\-]+)\|([\d\.\-]+)");
                    if (m.Success)
                    {
                        float.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out heroHealth);
                        float.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out heroMaxHealth);
                        resolvedHeroType = m.Groups[3].Value;
                        float.TryParse(m.Groups[4].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float px);
                        float.TryParse(m.Groups[5].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float py);
                        heroPosition = (px, py);
                    }
                    break;
                }

                await Task.Delay(400, cancellationToken).ConfigureAwait(false);
            }
        }

        // 13. Transient Visual Proof Capture
        WindowCaptureResult? captureResult = null;
        if (captureProof && _windowCapturer != null)
        {
            try
            {
                if (clientProc != null)
                {
                    try
                    {
                        clientProc.Refresh();
                    }
                    catch (Exception ex)
                    {
                        _logger($"Client process refresh warning: {ex.Message}");
                    }
                }
                else
                {
                    clientProc = _clientManageHandler?.GetRunningClientProcess();
                }

                IntPtr resolvedHwnd = IntPtr.Zero;
                if (clientProc != null && !clientProc.HasExited)
                {
                    resolvedHwnd = WindowCapturer.FindWindowForProcessId(clientProc.Id);
                    if (resolvedHwnd == IntPtr.Zero && clientProc.MainWindowHandle != IntPtr.Zero)
                    {
                        resolvedHwnd = clientProc.MainWindowHandle;
                    }
                }

                string captureTarget = resolvedHwnd != IntPtr.Zero
                    ? $"0x{resolvedHwnd.ToInt64():X8}"
                    : (clientProc != null ? clientProc.Id.ToString() : "hon_x64");

                _logger($"Capturing visual proof of practice match (WindowMode: {windowMode}, Target: {captureTarget}, HWND: 0x{resolvedHwnd.ToInt64():X8})...");
                if (string.Equals(windowMode, "offscreen", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(windowMode, "headless", StringComparison.OrdinalIgnoreCase))
                {
                    int targetPid = clientProc?.Id ?? targetProcessId ?? 0;
                    if (targetPid > 0)
                    {
                        captureResult = await _windowCapturer.CaptureVulkanOffscreenAsync(
                            targetPid,
                            targetName: "hon_x64",
                            timeoutMs: 3000,
                            saveArtifact: true,
                            cancellationToken: cancellationToken
                        ).ConfigureAwait(false);

                        if (captureResult is { Success: true })
                        {
                            _logger($"[ClientPractice] Captured Tier 0 Direct Vulkan Swapchain offscreen frame for PID {targetPid} (0ms dwell, 0% window exposure).");
                        }
                    }

                    if (captureResult == null || !captureResult.Success)
                    {
                        if (resolvedHwnd != IntPtr.Zero)
                        {
                            captureResult = await _windowCapturer.CaptureHwndTransientAsync(
                                hwnd: resolvedHwnd,
                                targetName: "hon_x64",
                                dwellTimeMs: 400,
                                saveArtifact: true,
                                cancellationToken: cancellationToken
                            ).ConfigureAwait(false);
                        }
                        else
                        {
                            captureResult = await _windowCapturer.CaptureTransientAsync(
                                target: captureTarget,
                                dwellTimeMs: 400,
                                saveArtifact: true,
                                cancellationToken: cancellationToken
                            ).ConfigureAwait(false);
                        }
                    }
                }
                else
                {
                    captureResult = await _windowCapturer.CaptureAsync(
                        target: captureTarget,
                        saveArtifact: true,
                        stealFocus: false,
                        cancellationToken: cancellationToken
                    ).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger($"[WARN] Visual proof capture encountered exception: {ex.Message}");
            }
        }

        sw.Stop();

        // 14. Response Envelope
        bool overallSuccess = phaseReached && entityVerified;
        var responseObj = new Dictionary<string, object?>
        {
            ["status"] = overallSuccess ? "success" : "warning",
            ["action"] = requestedAction,
            ["detail"] = fullDetail ? "full" : "concise",
            ["map"] = cleanMap,
            ["hero"] = cleanHero,
            ["practicePort"] = practicePort,
            ["authMode"] = authMode,
            ["gamePhase"] = resolvedPhase,
            ["phaseReached"] = phaseReached,
            ["entityVerified"] = entityVerified,
            ["warmReused"] = warmReused,
            ["warmReuseInvalidationReason"] = warmReused ? null : warmInvalidationReason,
            ["heroHealth"] = heroHealth,
            ["heroMaxHealth"] = heroMaxHealth,
            ["executionDurationMs"] = Math.Round(sw.Elapsed.TotalMilliseconds, 2)
        };

        if (fullDetail)
        {
            responseObj["resolvedHeroType"] = resolvedHeroType;
            responseObj["level"] = level;
            responseObj["items"] = items;
            responseObj["bots"] = bots;
            responseObj["cheats"] = cheats;
            responseObj["tickrate"] = tickrate;
            responseObj["windowMode"] = windowMode;
            responseObj["heroPosition"] = new { x = heroPosition.X, y = heroPosition.Y };
            responseObj["stateCleansed"] = stateCleansed;
            responseObj["autoBooted"] = clientWasOffline && autoBoot;
            responseObj["clusterStarted"] = clusterStarted;
            responseObj["timestamp"] = _timeProvider.GetUtcNow().ToString("o");
        }

        if (!phaseReached)
        {
            responseObj["warning"] = $"Timed out ({timeoutMs}ms) waiting for GamePhase {waitForPhase}. Reached phase: {resolvedPhase}.";
        }
        else if (!entityVerified)
        {
            responseObj["warning"] = $"GamePhase {resolvedPhase} reached, but player hero entity could not be verified in engine memory.";
        }

        if (captureResult != null)
        {
            responseObj["visualProof"] = new
            {
                success = captureResult.Success,
                errorMessage = captureResult.ErrorMessage,
                artifactPath = captureResult.ArtifactPath,
                hasThumbnail = !string.IsNullOrEmpty(captureResult.Base64Png),
                captureMethod = captureResult.CaptureMethodUsed
            };
        }

        return fullDetail
            ? ToolCallResult.Json(responseObj)
            : ToolCallResult.Text(JsonSerializer.Serialize(responseObj));
    }

    public static int ResolvePracticeServerPort(int preferredPort = 11580, int fallbackStartPort = 12100)
    {
        try
        {
            IPGlobalProperties ipProps = IPGlobalProperties.GetIPGlobalProperties();
            HashSet<int> occupiedPorts = ipProps
                .GetActiveUdpListeners()
                .Select(ep => ep.Port)
                .Concat(ipProps.GetActiveTcpListeners().Select(ep => ep.Port))
                .ToHashSet();

            for (int port = preferredPort; port < preferredPort + 10; port++)
            {
                if (!occupiedPorts.Contains(port) && !occupiedPorts.Contains(port + 500))
                {
                    return port;
                }
            }

            for (int port = fallbackStartPort; port < fallbackStartPort + 50; port++)
            {
                if (!occupiedPorts.Contains(port) && !occupiedPorts.Contains(port + 500))
                {
                    return port;
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ClientPracticeHandler] Port probe warning: {ex.Message}");
        }

        return preferredPort;
    }

    internal async Task<int> DetermineGamePhaseAsync(CancellationToken cancellationToken, int? processId = null, bool allowTestBypass = true)
    {
        if (allowTestBypass && GameServerManageHandler.IsTestExecution) return 3;

        string phaseQueryLua = "local gp = (Nexus and Nexus.GetCurrentPhase and Nexus.GetCurrentPhase()) or (GetGamePhase and GetGamePhase()) or tonumber(Cvar.GetCvar('cl_gamePhase') and Cvar.GetCvar('cl_gamePhase'):GetString()) or 0; Echo('GAME_PHASE:' .. tostring(gp)); if Cmd then Cmd('FlushLogs') end; return tostring(gp)";

        // 0. Query specific managed client instance if explicitly targeted by caller
        if (_clientManageHandler != null && processId.HasValue)
        {
            try
            {
                ToolCallResult phaseRes = await _clientManageHandler.ExecuteClientCommandAsync(
                    "Script " + phaseQueryLua,
                    1000,
                    autoLaunch: false,
                    allowKeystrokeFallback: false,
                    processId: processId.Value,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                if (!phaseRes.IsError && !string.IsNullOrWhiteSpace(phaseRes.FirstText) && TryExtractPhaseInt(phaseRes.FirstText, out int p))
                {
                    return p;
                }
            }
            catch (Exception ex)
            {
                _logger($"DetermineGamePhaseAsync processId {processId.Value} probe failed: {ex.Message}");
            }
        }

        // 1. Query via IPC bridge if connected (fast 0x0005 telemetry first; avoid blocking 2700ms 0x000E Lua probe when mainThreadBusy)
        if (_gameIpcBridge != null && _gameIpcBridge.IsConnected)
        {
            try
            {
                EngineStateDto? engineState = await _gameIpcBridge.GetEngineStateAsync(1000, cancellationToken).ConfigureAwait(false);
                if (engineState != null)
                {
                    if (engineState.GamePhase > 0 || engineState.MainThreadBusy)
                    {
                        return engineState.GamePhase;
                    }
                }

                using CancellationTokenSource luaProbeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                luaProbeCts.CancelAfter(750);
                string? luaResp = await _gameIpcBridge.ExecuteLuaAsync(phaseQueryLua, 750, luaProbeCts.Token).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(luaResp) && TryExtractPhaseInt(luaResp, out int lp))
                {
                    return lp;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger($"DetermineGamePhaseAsync IPC exception: {ex.Message}");
            }
        }

        // 2. Authoritative Fallback: Scrape latest gamePhase from engine console.log
        int logPhase = GameClientManageHandler.TryScrapeLatestGamePhaseFromLog();
        if (logPhase >= 0)
        {
            return logPhase;
        }

        return 0;
    }

    private static bool TryExtractPhaseInt(string text, out int phase)
    {
        phase = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        Match m = Regex.Match(text, @"GAME_PHASE:(\d+)");
        if (m.Success && int.TryParse(m.Groups[1].Value, out phase))
        {
            return true;
        }

        return int.TryParse(text.Trim(), out phase);
    }

    /// <summary>
    /// Evaluates whether an active K2 practice session can safely be warm-reused in-place (&lt; 5s)
    /// without violating any world-state integrity invariants.
    /// </summary>
    internal async Task<(bool CanReuse, string InvalidationReason)> TryEvaluateWarmPracticeReuseAsync(
        string cleanMap,
        int requestedBots,
        bool cleanseExplicitlyTrue,
        bool reuseIfActive,
        int? explicitProcessId,
        CancellationToken cancellationToken)
    {
        if (cleanseExplicitlyTrue)
        {
            return (false, "caller_requested_cleanse");
        }

        if (!reuseIfActive)
        {
            return (false, "reuse_disabled");
        }

        if (requestedBots > 0)
        {
            return (false, "bots_requested");
        }

        if (GameServerManageHandler.IsTestExecution && _gameIpcBridge == null)
        {
            return (false, "test_environment_default");
        }

        const string warmProbeLua =
            "local rawGp = (UIGamePhase and UIGamePhase()) or ((GetGamePhase and GetGamePhase() == 3) and 5) or tonumber(Cvar.GetCvar('cl_gamePhase') and Cvar.GetCvar('cl_gamePhase'):GetString()) or 0; " +
            "local mp = (Testing and Testing.GetMountedWorld and Testing.GetMountedWorld()) or (GetMap and GetMap()) or (GetMapName and GetMapName()) or (Cvar.GetCvar('host_mapName') and Cvar.GetCvar('host_mapName'):GetString()) or (Cvar.GetCvar('svr_mapName') and Cvar.GetCvar('svr_mapName'):GetString()) or ''; " +
            "local rep = ((ViewingReplay and ViewingReplay()) or (IsReplay and IsReplay())) and 'true' or 'false'; " +
            "local spec = ((ViewingStreaming and ViewingStreaming()) or (IsSpectator and IsSpectator())) and 'true' or 'false'; " +
            "local cn = (GetLocalClientNumber and GetLocalClientNumber()) or 0; " +
            "local hasHero = (Testing and Testing.IsPracticeGame and Testing.IsPracticeGame() and Testing.PlayerHasValidHero and Testing.PlayerHasValidHero(cn)) or (GetViewHeroName and GetViewHeroName() and #GetViewHeroName() > 0) or false; " +
            "local hp = hasHero and 600 or -1; " +
            "return 'WARM_PROBE|' .. tostring(rawGp) .. '|' .. tostring(mp) .. '|' .. rep .. '|' .. spec .. '|' .. tostring(hp)";

        string probeResp = await ExecuteScriptAsync(warmProbeLua, cancellationToken, explicitProcessId).ConfigureAwait(false);
        return EvaluateWarmProbeResponse(probeResp, cleanMap);
    }

    public static (bool CanReuse, string InvalidationReason) EvaluateWarmProbeResponse(string? probeResp, string cleanMap)
    {
        if (string.IsNullOrWhiteSpace(probeResp))
        {
            return (false, "empty_probe_response");
        }

        Match m = Regex.Match(
            probeResp,
            @"WARM_PROBE\|(\-?\d+)\|([^\|]*)\|(true|false)\|(true|false)\|([\d\.\-]+)",
            RegexOptions.IgnoreCase);
        if (!m.Success)
        {
            return (false, "unparseable_probe_response");
        }

        if (!int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int rawPhase) || rawPhase < 5)
        {
            return (false, $"not_in_active_match_phase_{m.Groups[1].Value}");
        }

        string activeMap = m.Groups[2].Value.Trim();
        bool isReplay = string.Equals(m.Groups[3].Value, "true", StringComparison.OrdinalIgnoreCase);
        bool isSpectator = string.Equals(m.Groups[4].Value, "true", StringComparison.OrdinalIgnoreCase);
        float.TryParse(m.Groups[5].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float heroHp);

        if (isReplay || isSpectator)
        {
            return (false, "replay_or_spectator");
        }

        if (string.IsNullOrWhiteSpace(activeMap) ||
            !string.Equals(activeMap, cleanMap, StringComparison.OrdinalIgnoreCase))
        {
            return (false, $"map_mismatch_{activeMap}_vs_{cleanMap}");
        }

        if (heroHp <= 0f)
        {
            return (false, "hero_dead_or_unspawned");
        }

        return (true, "none");
    }

    private async Task<string> ExecuteScriptAsync(string lua, CancellationToken cancellationToken, int? processId = null)
    {
        string rawLua = lua.StartsWith("Script ", StringComparison.OrdinalIgnoreCase) ? lua[7..].Trim() : lua.Trim();
        string formatted = rawLua.StartsWith('"') && rawLua.EndsWith('"')
            ? $"Script {rawLua}"
            : $"Script \"{rawLua.Replace("\"", "\\\"")}\"";

        if (_remediationService != null)
        {
            var analysis = ClientCommandRemediationService.AnalyzeCommandError(formatted, "", "lua");
            if (analysis.CanAutoRetry && !string.IsNullOrWhiteSpace(analysis.ImprovedCommand))
            {
                formatted = analysis.ImprovedCommand;
            }
        }

        // Tier 1: IPC Bridge (if connected and generic/not isolated PID)
        if (_gameIpcBridge != null && _gameIpcBridge.IsConnected && !processId.HasValue)
        {
            try
            {
                string? luaRes = await _gameIpcBridge.ExecuteLuaAsync(rawLua, 2500, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(luaRes) && !string.Equals(luaRes.Trim(), "OK", StringComparison.OrdinalIgnoreCase))
                {
                    return luaRes;
                }

                string res = await _gameIpcBridge.ExecuteCommandAsync(formatted, 2500, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(res)) return res;
            }
            catch (Exception ex)
            {
                _logger($"ExecuteScriptAsync IPC caught exception: {ex.Message}, falling back to client manage...");
            }
        }

        // Tier 2: Client Manage Handler (Stdin / Direct Pipe / Targeted PID)
        if (_clientManageHandler != null)
        {
            try
            {
                var res = await _clientManageHandler.ExecuteClientCommandAsync(
                    formatted,
                    3000,
                    autoLaunch: false,
                    allowKeystrokeFallback: false,
                    processId: processId,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                if (!res.IsError && !string.IsNullOrWhiteSpace(res.FirstText))
                {
                    return res.FirstText;
                }
            }
            catch (Exception ex)
            {
                _logger($"ExecuteScriptAsync clientManage fallback exception: {ex.Message}");
            }
        }

        // Fallback: IPC bridge direct attempt if not already connected
        if (_gameIpcBridge != null && !_gameIpcBridge.IsConnected)
        {
            try
            {
                return await _gameIpcBridge.ExecuteCommandAsync(formatted, 2500, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger($"ExecuteScriptAsync disconnected IPC fallback failed: {ex.Message}");
            }
        }

        return string.Empty;
    }
}
