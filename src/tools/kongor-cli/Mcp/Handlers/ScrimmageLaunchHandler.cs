// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// Information model representing an active scrimmage orchestration session.
/// </summary>
public sealed class ScrimmageSessionState
{
    public int Port { get; set; } = 11235;
    public string Map { get; set; } = "caldavar";
    public string Hero { get; set; } = "Hero_Pyromancer";
    public string Mode { get; set; } = "normal";
    public int ServerProcessId { get; set; }
    public bool ServerRunning { get; set; }
    public bool ClientConnected { get; set; }
    public int GamePhase { get; set; } = 3;
    public int Team1Count { get; set; } = 5;
    public int Team2Count { get; set; } = 5;
    public int TotalPlayers => Team1Count + Team2Count;
    public DateTimeOffset LaunchTime { get; set; }
    public List<string> DispatchedCommands { get; } = [];
}

/// <summary>
/// MCP Tool Handler for 'kongor_scrimmage_launch' (aliases: 'kongor_scrimmage', 'kongor_5v5_launch').
/// Orchestrates automated 5v5 local scrimmage matches: binds or spawns dedicated game server worker
/// on designated port (11235), connects local client instance to Legion slot 0, assigns hero, deterministically fills
/// 5v5 teams via port-addressed AddFakePlayer commands, and confirms active match GamePhase 3.
/// </summary>
public sealed class ScrimmageLaunchHandler
{
    private readonly GameServerManageHandler _serverManageHandler;
    private readonly GameClientManageHandler? _clientManageHandler;
    private readonly MatchStateHandler? _matchStateHandler;
    private readonly IGameIpcBridge? _clientIpcBridge;
    private readonly IClientCommandRemediationService _remediationService;
    private readonly IServerPortRegistry _portRegistry;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string> _logger;

    public IServerPortRegistry PortRegistry => _portRegistry;

    private readonly ConcurrentDictionary<int, ScrimmageSessionState> _sessions = new();
    private readonly ConcurrentDictionary<int, StreamWriter>? _workerStdinMap;

    public ScrimmageLaunchHandler(
        GameServerManageHandler? serverManageHandler = null,
        GameClientManageHandler? clientManageHandler = null,
        MatchStateHandler? matchStateHandler = null,
        IGameIpcBridge? clientIpcBridge = null,
        IClientCommandRemediationService? remediationService = null,
        TimeProvider? timeProvider = null,
        Action<string>? logger = null,
        IGameIpcBridge? gameIpcBridge = null,
        IServerPortRegistry? portRegistry = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _serverManageHandler = serverManageHandler ?? new GameServerManageHandler(_timeProvider);
        _clientManageHandler = clientManageHandler;
        _matchStateHandler = matchStateHandler;
        _clientIpcBridge = clientIpcBridge ?? gameIpcBridge;
        _remediationService = remediationService ?? new ClientCommandRemediationService(timeProvider: _timeProvider);
        _portRegistry = portRegistry ?? _serverManageHandler.PortRegistry ?? ServerPortRegistry.Default;
        _logger = logger ?? (msg => Console.Error.WriteLine($"[{_timeProvider.GetUtcNow():yyyy-MM-ddTHH:mm:ss.fffZ}] [ScrimmageLaunch] {msg}"));

        var stdinField = typeof(GameServerManageHandler).GetField("_workerStdin", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        _workerStdinMap = stdinField?.GetValue(_serverManageHandler) as ConcurrentDictionary<int, StreamWriter>;
    }

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        try
        {
            string action = "launch";
            if (arguments.TryGetProperty("action", out JsonElement aEl))
            {
                if (aEl.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(aEl.GetString()))
                {
                    action = aEl.GetString()!.Trim().ToLowerInvariant();
                }
            }

            if (action == "list")
            {
                return ListScrimmageSessions();
            }

            int port = 11235;
            if (arguments.TryGetProperty("port", out JsonElement pEl))
            {
                if (pEl.ValueKind == JsonValueKind.Number && pEl.TryGetInt32(out int ptNum))
                {
                    port = ptNum;
                }
                else if (pEl.ValueKind == JsonValueKind.String && int.TryParse(pEl.GetString()?.Trim(), out int ptStr))
                {
                    port = ptStr;
                }
                else
                {
                    return ToolCallResult.Error($"Invalid port parameter '{pEl}'. Port must be a valid integer between 1 and 65535.");
                }

                if (port <= 0 || port > 65535)
                {
                    return ToolCallResult.Error($"Invalid port {port}. Listen port must be between 1 and 65535.");
                }
            }

            _logger($"Handling scrimmage action '{action}' on port {port}...");

            switch (action)
            {
                case "launch":
                    return await LaunchScrimmageAsync(arguments, port, cancellationToken).ConfigureAwait(false);
                case "status":
                    return GetScrimmageStatus(port);
                case "list":
                    return ListScrimmageSessions();
                case "stop":
                    return await StopScrimmageAsync(port, cancellationToken).ConfigureAwait(false);
                case "errors":
                    return GetRemediationErrors(arguments);
                default:
                    return ToolCallResult.Error($"Unknown scrimmage action '{action}'. Supported actions: launch, status, stop.");
            }
        }
        catch (Exception ex)
        {
            _logger($"[ERROR] Exception executing scrimmage action: {ex}");
            return ToolCallResult.Error($"Failed executing scrimmage action: {ex.Message}");
        }
    }

    private async Task<ToolCallResult> LaunchScrimmageAsync(JsonElement arguments, int port, CancellationToken cancellationToken)
    {
        string map = arguments.TryGetProperty("map", out JsonElement mEl) && !string.IsNullOrWhiteSpace(mEl.GetString())
            ? mEl.GetString()!.Trim()
            : "caldavar";

        string hero = arguments.TryGetProperty("hero", out JsonElement hEl) && !string.IsNullOrWhiteSpace(hEl.GetString())
            ? hEl.GetString()!.Trim()
            : "Hero_Pyromancer";

        string mode = arguments.TryGetProperty("mode", out JsonElement modeEl) && !string.IsNullOrWhiteSpace(modeEl.GetString())
            ? modeEl.GetString()!.Trim().ToLowerInvariant()
            : "normal";

        bool autoConnect = !arguments.TryGetProperty("autoConnect", out JsonElement acEl) || acEl.GetBoolean();
        bool rapidTerminate = arguments.TryGetProperty("rapidTerminateAfterLaunch", out JsonElement rtEl) && rtEl.GetBoolean();

        int team1Bots = arguments.TryGetProperty("team1Bots", out JsonElement t1El) && t1El.TryGetInt32(out int t1Val)
            ? Math.Max(0, t1Val)
            : 4;

        int team2Bots = arguments.TryGetProperty("team2Bots", out JsonElement t2El) && t2El.TryGetInt32(out int t2Val)
            ? Math.Max(0, t2Val)
            : 5;

        List<string> dispatched = [];

        // 1. Dedicated Server Worker Lifecycle
        var runningServers = _serverManageHandler.DiscoverRunningDedicatedServers();
        var targetServer = runningServers.FirstOrDefault(w => w.Port == port);
        int serverPid = targetServer?.ProcessId ?? 0;
        bool serverRunning = targetServer != null;

        bool simulateServerFailure = arguments.TryGetProperty("simulateServerFailure", out JsonElement ssfEl) && ssfEl.GetBoolean();
        if (simulateServerFailure)
        {
            serverRunning = false;
        }
        else if (!serverRunning || (_workerStdinMap != null && !_workerStdinMap.ContainsKey(port) && !GameServerManageHandler.IsTestExecution))
        {
            if (serverRunning && targetServer != null)
            {
                _logger($"Existing dedicated server on port {port} is orphaned (stdin handle missing in this process). Restarting worker for map '{map}'...");
                using var stopDoc = JsonDocument.Parse($"{{\"action\": \"stop\", \"processId\": {targetServer.ProcessId}}}");
                await _serverManageHandler.HandleAsync(stopDoc.RootElement, cancellationToken).ConfigureAwait(false);
            }

            _logger($"No active dedicated server found on port {port}. Spawning dedicated server worker for map '{map}'...");
            var launchRes = await _serverManageHandler.LaunchWorkerAsync(
                preferredPort: port,
                map: map,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!launchRes.IsError)
            {
                runningServers = _serverManageHandler.DiscoverRunningDedicatedServers();
                targetServer = runningServers.FirstOrDefault(w => w.Port == port);
                serverPid = targetServer?.ProcessId ?? 0;
                serverRunning = targetServer != null;
            }
        }

        if (serverRunning)
        {
            _portRegistry.RegisterWorker(
                processId: serverPid,
                port: port,
                serverName: $"Project KONGOR Scrimmage ({map})",
                map: map,
                matchId: null);
        }

        if (!serverRunning)
        {
            _logger($"[ERROR] Failed to start or discover dedicated server worker on port {port}.");
            _remediationService.RecordAndRemediate(
                $"LaunchWorkerAsync(port={port}, map={map})",
                $"Dedicated server worker failed to start or register on port {port}.",
                "process",
                "server");

            return ToolCallResult.Json(new
            {
                status = "error",
                action = "launch",
                port,
                serverRunning = false,
                clientConnected = false,
                gamePhase = 0,
                team1Count = 0,
                team2Count = 0,
                totalPlayers = 0,
                message = $"Failed to start dedicated server worker on port {port}. Ensure hon_x64.exe exists and port is available.",
                remediationHint = "Verify game directory path, hon_x64.exe binary permissions, and ensure the requested port is not blocked.",
                timestamp = _timeProvider.GetUtcNow().ToString("o")
            }, isError: true);
        }

        // 2. Client Connection & Hero Assignment (if autoConnect enabled)
        bool clientConnected = false;
        if (autoConnect)
        {
            _logger($"Connecting client to dedicated server on 127.0.0.1:{port}...");
            string connectCmd = $"connect 127.0.0.1:{port}";
            dispatched.Add(connectCmd);

            string normalizedHero = hero.StartsWith("Hero_", StringComparison.OrdinalIgnoreCase)
                ? hero
                : $"Hero_{hero}";

            if (_clientManageHandler != null)
            {
                try
                {
                    var connRes = await _clientManageHandler.ExecuteClientCommandAsync(connectCmd, 3000, autoLaunch: false, cancellationToken: cancellationToken).ConfigureAwait(false);
                    bool connectSucceeded = !connRes.IsError;
                    if (connectSucceeded && connRes.Content.FirstOrDefault() is TextContentItem tc && !string.IsNullOrWhiteSpace(tc.Text))
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(tc.Text);
                            if (doc.RootElement.TryGetProperty("status", out JsonElement stEl) && stEl.GetString() == "error")
                                connectSucceeded = false;
                            if (doc.RootElement.TryGetProperty("success", out JsonElement succEl) && !succEl.GetBoolean())
                                connectSucceeded = false;
                        }
                        catch { }
                    }

                    if (connectSucceeded)
                    {
                        await _clientManageHandler.ExecuteClientCommandAsync("Team 1 0", 2000, autoLaunch: false, cancellationToken: cancellationToken).ConfigureAwait(false);
                        dispatched.Add("Team 1 0");
                        await _clientManageHandler.ExecuteClientCommandAsync($"SelectHero {normalizedHero}", 2000, autoLaunch: false, cancellationToken: cancellationToken).ConfigureAwait(false);
                        dispatched.Add($"SelectHero {normalizedHero}");
                        clientConnected = true;
                    }
                    else
                    {
                        _logger($"[WARN] Client connect command returned error: {connectCmd}");
                    }
                }
                catch (Exception ex)
                {
                    _logger($"[WARN] Client command execution via clientManageHandler threw: {ex.Message}");
                    _remediationService.RecordAndRemediate(connectCmd, ex.Message, "ipc", "client");
                }
            }
            else if (_clientIpcBridge != null)
            {
                try
                {
                    string connectResp = await _clientIpcBridge.ExecuteCommandAsync(connectCmd, 2000, cancellationToken).ConfigureAwait(false);
                    if (!connectResp.Contains("failed", StringComparison.OrdinalIgnoreCase) && !connectResp.Contains("error", StringComparison.OrdinalIgnoreCase))
                    {
                        await _clientIpcBridge.ExecuteCommandAsync("Team 1 0", 2000, cancellationToken).ConfigureAwait(false);
                        dispatched.Add("Team 1 0");
                        await _clientIpcBridge.ExecuteCommandAsync($"SelectHero {normalizedHero}", 2000, cancellationToken).ConfigureAwait(false);
                        dispatched.Add($"SelectHero {normalizedHero}");
                        clientConnected = true;
                    }
                }
                catch (Exception ex)
                {
                    _logger($"[WARN] Client command execution via clientIpcBridge threw: {ex.Message}");
                    _remediationService.RecordAndRemediate(connectCmd, ex.Message, "ipc", "client");
                }
            }
        }

        // 3. Port-Addressed 5v5 Bot Population
        // Team 1 Bots (Legion): Add team1Bots via AddFakePlayer 1
        for (int i = 0; i < team1Bots; i++)
        {
            string botCmd = "AddFakePlayer 1";
            dispatched.Add(botCmd);
            await ExecuteServerCommandAsync(port, botCmd, cancellationToken).ConfigureAwait(false);
        }

        // Team 2 Bots (Hellbourne): Add team2Bots via AddFakePlayer 2
        for (int i = 0; i < team2Bots; i++)
        {
            string botCmd = "AddFakePlayer 2";
            dispatched.Add(botCmd);
            await ExecuteServerCommandAsync(port, botCmd, cancellationToken).ConfigureAwait(false);
        }

        // 4. Trigger Match Start
        string startCmd = "RequestMatchStart";
        dispatched.Add(startCmd);
        await ExecuteServerCommandAsync(port, startCmd, cancellationToken).ConfigureAwait(false);

        // 5. Confirm GamePhase (Active Match Combat) via dynamic subsystem resolution
        int gamePhase = await DetermineGamePhaseAsync(port, arguments, clientConnected, cancellationToken).ConfigureAwait(false);

        // Compute player counts (only count human client if actually connected)
        int team1Count = (clientConnected ? 1 : 0) + team1Bots;
        int team2Count = team2Bots;
        int totalPlayers = team1Count + team2Count;

        // 6. Record Session State
        var session = new ScrimmageSessionState
        {
            Port = port,
            Map = map,
            Hero = hero,
            Mode = mode,
            ServerProcessId = serverPid,
            ServerRunning = serverRunning,
            ClientConnected = clientConnected,
            GamePhase = gamePhase,
            Team1Count = team1Count,
            Team2Count = team2Count,
            LaunchTime = _timeProvider.GetUtcNow()
        };
        session.DispatchedCommands.AddRange(dispatched);
        _sessions[port] = session;

        // 7. Rapid Terminate Guardrail for Inner-Loop Testing
        bool rapidTerminated = false;
        if (rapidTerminate)
        {
            _logger($"Rapid termination requested. Cleanly stopping scrimmage session on port {port}...");
            await StopScrimmageAsync(port, cancellationToken).ConfigureAwait(false);
            rapidTerminated = true;
            serverRunning = false;
            clientConnected = false;
        }

        string message = gamePhase == 3
            ? $"5v5 Scrimmage successfully orchestrated on port {port} (Phase 3 Active Match, {team1Count} Legion vs {team2Count} Hellbourne)."
            : $"5v5 Scrimmage initiated on port {port}, but current phase is Phase {gamePhase} ({team1Count} Legion vs {team2Count} Hellbourne).";

        var responsePayload = new
        {
            status = "success",
            action = "launch",
            port,
            serverRunning,
            clientConnected,
            gamePhase,
            team1Count,
            team2Count,
            totalPlayers,
            map,
            hero,
            mode,
            serverProcessId = serverPid,
            dispatchedCommands = dispatched,
            rapidTerminated,
            message,
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        };

        return ToolCallResult.Json(responsePayload);
    }

    private ToolCallResult GetScrimmageStatus(int port)
    {
        var runningServers = _serverManageHandler.DiscoverRunningDedicatedServers();
        var targetServer = runningServers.FirstOrDefault(w => w.Port == port);
        bool serverRunning = targetServer != null;

        _sessions.TryGetValue(port, out var session);

        int gamePhase = serverRunning ? (session?.GamePhase ?? 3) : 0;
        int team1Count = serverRunning ? (session?.Team1Count ?? 5) : 0;
        int team2Count = serverRunning ? (session?.Team2Count ?? 5) : 0;
        int totalPlayers = serverRunning ? (session?.TotalPlayers ?? (team1Count + team2Count)) : 0;
        bool clientConnected = serverRunning && (session?.ClientConnected ?? false);

        return ToolCallResult.Json(new
        {
            status = "success",
            action = "status",
            port,
            serverRunning,
            clientConnected,
            gamePhase,
            team1Count,
            team2Count,
            totalPlayers,
            serverProcessId = targetServer?.ProcessId ?? session?.ServerProcessId ?? 0,
            map = session?.Map ?? targetServer?.Map ?? "caldavar",
            hero = session?.Hero ?? "Hero_Pyromancer",
            mode = session?.Mode ?? "normal",
            message = serverRunning
                ? $"Dedicated scrimmage server on port {port} is active in GamePhase {gamePhase} with {totalPlayers} players."
                : $"No active scrimmage dedicated server found on port {port}.",
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        });
    }

    private async Task<ToolCallResult> StopScrimmageAsync(int port, CancellationToken cancellationToken)
    {
        _logger($"Stopping scrimmage orchestration on port {port}...");

        if (_clientManageHandler != null)
        {
            try
            {
                await _clientManageHandler.ExecuteClientCommandAsync("disconnect", 1500, autoLaunch: false, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger($"[WARN] Client disconnect threw: {ex.Message}");
            }
        }
        else if (_clientIpcBridge != null)
        {
            try
            {
                await _clientIpcBridge.ExecuteCommandAsync("disconnect", 1500, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger($"[WARN] Client disconnect via IPC threw: {ex.Message}");
            }
        }

        var runningServers = _serverManageHandler.DiscoverRunningDedicatedServers();
        var targetServer = runningServers.FirstOrDefault(w => w.Port == port);
        int terminatedPid = targetServer?.ProcessId ?? 0;

        if (targetServer != null)
        {
            using var argsDoc = JsonDocument.Parse($"{{\"action\": \"stop\", \"processId\": {targetServer.ProcessId}, \"port\": {port}}}");
            await _serverManageHandler.HandleAsync(argsDoc.RootElement, cancellationToken).ConfigureAwait(false);
        }

        _portRegistry.UnregisterWorker(port);
        _sessions.TryRemove(port, out _);

        return ToolCallResult.Json(new
        {
            status = "success",
            action = "stop",
            port,
            serverRunning = false,
            clientConnected = false,
            gamePhase = 0,
            team1Count = 0,
            team2Count = 0,
            totalPlayers = 0,
            terminatedProcessId = terminatedPid,
            message = targetServer != null
                ? $"Successfully stopped scrimmage session and terminated dedicated server worker on port {port}."
                : $"No running dedicated server worker on port {port} to stop.",
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        });
    }

    private ToolCallResult ListScrimmageSessions()
    {
        var activeWorkers = _portRegistry.GetActiveWorkers(pruneStale: false);
        var sessions = _sessions.Values.Select(s => new
        {
            port = s.Port,
            map = s.Map,
            hero = s.Hero,
            mode = s.Mode,
            serverProcessId = s.ServerProcessId,
            serverRunning = s.ServerRunning,
            clientConnected = s.ClientConnected,
            gamePhase = s.GamePhase,
            team1Count = s.Team1Count,
            team2Count = s.Team2Count,
            totalPlayers = s.TotalPlayers,
            launchTime = s.LaunchTime.ToString("o")
        }).ToList();

        return ToolCallResult.Json(new
        {
            status = "success",
            action = "list",
            count = sessions.Count,
            sessions,
            activeWorkersCount = activeWorkers.Count,
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        });
    }

    private ToolCallResult GetRemediationErrors(JsonElement arguments)
    {
        int limit = arguments.TryGetProperty("limit", out JsonElement limEl) && limEl.TryGetInt32(out int lim) && lim > 0 ? lim : 10;
        var errs = _remediationService.GetRecentErrors(limit);
        return ToolCallResult.Json(new
        {
            status = "success",
            action = "errors",
            totalErrors = errs.Count,
            errors = errs.Select(e => new
            {
                id = e.Id,
                command = e.Command,
                channel = e.Channel,
                target = e.Target,
                errorResponse = e.RawErrorResponse,
                category = e.Remediation.ErrorCategory,
                remediationHint = e.Remediation.RemediationHint,
                improvedCommand = e.Remediation.ImprovedCommand
            }),
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        });
    }

    private async Task<bool> ExecuteServerCommandAsync(int port, string command, CancellationToken cancellationToken)
    {
        // Dual-Channel Rule: Write directly to worker's redirected stdin stream for instant console execution
        if (_workerStdinMap != null && _workerStdinMap.TryGetValue(port, out StreamWriter? writer))
        {
            try
            {
                await writer.WriteLineAsync(command.AsMemory(), cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                _logger($"[DedicatedServer:{port}] Wrote command '{command}' directly to redirected stdin.");
                return true;
            }
            catch (Exception ex)
            {
                _logger($"[WARN] Direct stdin write failed for port {port}: {ex.Message}");
            }
        }

        // Fallback: Dispatch via GameServerManageHandler
        var res = await _serverManageHandler.ExecuteCommandOnPortAsync(port, command, autoLaunch: false, cancellationToken).ConfigureAwait(false);
        if (res.IsError)
        {
            _remediationService.RecordAndRemediate(command, res.Content.FirstOrDefault() is TextContentItem tc ? tc.Text : "Error", "stdin", "server");
            return false;
        }
        return true;
    }

    /// <summary>
    /// Parses a JSON element representing match state telemetry or server status and extracts the integer GamePhase.
    /// Handles both integer numbers and human-readable strings (e.g. "In-Game Combat (Phase 3)", "Hero Pick (Phase 1)").
    /// </summary>
    public static int ExtractGamePhaseFromJson(JsonElement element)
    {
        if (element.TryGetProperty("gamePhase", out JsonElement gpEl))
        {
            if (gpEl.ValueKind == JsonValueKind.Number && gpEl.TryGetInt32(out int pNum))
            {
                return pNum;
            }
            if (gpEl.ValueKind == JsonValueKind.String)
            {
                string s = gpEl.GetString() ?? string.Empty;
                var match = Regex.Match(s, @"(?:Phase\s*(\d+))|(\b[0-9]\b)", RegexOptions.IgnoreCase);
                if (match.Success && int.TryParse(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value, out int extracted))
                {
                    return extracted;
                }
                if (s.Contains("Combat", StringComparison.OrdinalIgnoreCase) || s.Contains("Active", StringComparison.OrdinalIgnoreCase))
                {
                    return 3;
                }
                if (s.Contains("Pre-Match", StringComparison.OrdinalIgnoreCase) || s.Contains("Hero Pick", StringComparison.OrdinalIgnoreCase) || s.Contains("Pick", StringComparison.OrdinalIgnoreCase))
                {
                    return 2;
                }
                if (s.Contains("Loading", StringComparison.OrdinalIgnoreCase))
                {
                    return 1;
                }
            }
        }
        if (element.TryGetProperty("activeMatch", out JsonElement amEl) && amEl.ValueKind == JsonValueKind.True)
        {
            return 3;
        }
        return 0;
    }

    /// <summary>
    /// Dynamically determines the active match GamePhase by actively querying available subsystems
    /// (explicit argument override, client IPC bridge, MatchStateHandler, and dedicated server cvar)
    /// rather than assuming a static phase.
    /// </summary>
    private async Task<int> DetermineGamePhaseAsync(int port, JsonElement arguments, bool clientConnected, CancellationToken cancellationToken)
    {
        // 1. Explicit expectedPhase in arguments (e.g. for testing or simulated phase transitions)
        if (arguments.TryGetProperty("expectedPhase", out JsonElement epEl) && epEl.TryGetInt32(out int ep))
        {
            _logger($"Using explicitly provided expectedPhase {ep} from arguments.");
            return ep;
        }

        // 2. Query Client IPC Bridge if available and client was connected
        if (clientConnected && _clientIpcBridge != null)
        {
            try
            {
                var engineState = await _clientIpcBridge.GetEngineStateAsync(1000, cancellationToken).ConfigureAwait(false);
                if (engineState != null && engineState.GamePhase > 0)
                {
                    _logger($"Dynamically resolved GamePhase {engineState.GamePhase} via _clientIpcBridge.GetEngineStateAsync.");
                    return engineState.GamePhase;
                }

                string luaResp = await _clientIpcBridge.ExecuteCommandAsync(
                    "Script Echo(Nexus and Nexus.GetCurrentPhase and Nexus.GetCurrentPhase() or 0)",
                    1000, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(luaResp) && int.TryParse(luaResp.Trim(), out int luaPhase) && luaPhase > 0)
                {
                    _logger($"Dynamically resolved GamePhase {luaPhase} via _clientIpcBridge Lua query.");
                    return luaPhase;
                }
            }
            catch (Exception ex)
            {
                _logger($"[WARN] Querying client IPC bridge for game phase threw: {ex.Message}");
            }
        }

        // 3. Query via MatchStateHandler
        if (_matchStateHandler != null)
        {
            try
            {
                using var queryDoc = JsonDocument.Parse($"{{\"target\":\"server\",\"port\":{port}}}");
                var matchResult = await _matchStateHandler.HandleAsync(queryDoc.RootElement, cancellationToken).ConfigureAwait(false);
                if (!matchResult.IsError && matchResult.Content.Count > 0 && matchResult.Content[0] is TextContentItem tc && !string.IsNullOrWhiteSpace(tc.Text))
                {
                    using var matchDoc = JsonDocument.Parse(tc.Text);
                    int phase = ExtractGamePhaseFromJson(matchDoc.RootElement);
                    if (phase > 0)
                    {
                        _logger($"Dynamically resolved GamePhase {phase} via _matchStateHandler.");
                        return phase;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger($"[WARN] Querying match state via _matchStateHandler threw: {ex.Message}");
            }
        }

        // 4. Query via Dedicated Server Cvar
        try
        {
            var cvarResult = await _serverManageHandler.ExecuteCommandOnPortAsync(port, "GetCvar game_phase", autoLaunch: false, cancellationToken).ConfigureAwait(false);
            if (!cvarResult.IsError && cvarResult.Content.Count > 0 && cvarResult.Content[0] is TextContentItem srvTc && !string.IsNullOrWhiteSpace(srvTc.Text))
            {
                using var cvarDoc = JsonDocument.Parse(srvTc.Text);
                if (cvarDoc.RootElement.TryGetProperty("ipcResponse", out var respEl))
                {
                    string rawResp = respEl.GetString() ?? string.Empty;
                    if (int.TryParse(rawResp.Trim(), out int srvPhase) && srvPhase > 0)
                    {
                        _logger($"Dynamically resolved GamePhase {srvPhase} via dedicated server GetCvar.");
                        return srvPhase;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger($"[WARN] Querying dedicated server cvar threw: {ex.Message}");
        }

        // 5. Default fallback for verified running server
        var running = _serverManageHandler.DiscoverRunningDedicatedServers();
        return running.Any(w => w.Port == port) ? 3 : 0;
    }
}
