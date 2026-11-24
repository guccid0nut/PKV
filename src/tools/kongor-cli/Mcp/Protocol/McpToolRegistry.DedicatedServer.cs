// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using Kongor.Cli.Mcp.Handlers;

namespace Kongor.Cli.Mcp.Protocol;

public sealed partial class McpToolRegistry
{
    private ServerPerfStreamHandler _serverPerfStreamHandler = null!;
    private ReplayDiffHandler _replayDiffHandler = null!;
    private DbPoolDiagnosticsHandler _dbPoolDiagnosticsHandler = null!;
#if !PLAYTEST_BUILD
    private AppEvalScriptHandler _appEvalScriptHandler = null!;
#endif
    private StateSnapshotHandler _stateSnapshotHandler = null!;

    private NetworkChaosHandler _networkChaosHandler = null!;
    private RealtimeStreamHandler _realtimeStreamHandler = null!;
    private NetPacketFuzzHandler _netPacketFuzzHandler = null!;
    private MemorySyncHandler _memorySyncHandler = null!;

    private MatchAnalyticsHandler _matchAnalyticsHandler = null!;
    private CiTriggerHandler _ciTriggerHandler = null!;
    private DbMigrateHandler _dbMigrateHandler = null!;
    private CanaryDeployHandler _canaryDeployHandler = null!;

    public ServerPerfStreamHandler ServerPerfStreamHandler => _serverPerfStreamHandler;
    public ReplayDiffHandler ReplayDiffHandler => _replayDiffHandler;
    public DbPoolDiagnosticsHandler DbPoolDiagnosticsHandler => _dbPoolDiagnosticsHandler;
#if !PLAYTEST_BUILD
    public AppEvalScriptHandler AppEvalScriptHandler => _appEvalScriptHandler;
#endif
    public StateSnapshotHandler StateSnapshotHandler => _stateSnapshotHandler;

    public NetworkChaosHandler NetworkChaosHandler => _networkChaosHandler;
    public RealtimeStreamHandler RealtimeStreamHandler => _realtimeStreamHandler;
    public NetPacketFuzzHandler NetPacketFuzzHandler => _netPacketFuzzHandler;
    public MemorySyncHandler MemorySyncHandler => _memorySyncHandler;

    public MatchAnalyticsHandler MatchAnalyticsHandler => _matchAnalyticsHandler;
    public CiTriggerHandler CiTriggerHandler => _ciTriggerHandler;
    public DbMigrateHandler DbMigrateHandler => _dbMigrateHandler;
    public CanaryDeployHandler CanaryDeployHandler => _canaryDeployHandler;

    partial void InitializeDedicatedServerHandlers(TimeProvider timeProvider)
    {
        _serverPerfStreamHandler = new ServerPerfStreamHandler(timeProvider);
        _replayDiffHandler = new ReplayDiffHandler(
            portRegistry: _gameServerManageHandler.PortRegistry,
            serverManageHandler: _gameServerManageHandler,
            gameIpcBridge: _gameIpcBridge,
            timeProvider: timeProvider);
        _dbPoolDiagnosticsHandler = new DbPoolDiagnosticsHandler(timeProvider);
#if !PLAYTEST_BUILD
        _appEvalScriptHandler = new AppEvalScriptHandler(timeProvider);
#endif
        _stateSnapshotHandler = new StateSnapshotHandler(timeProvider);

        _networkChaosHandler = new NetworkChaosHandler(
            _gameServerManageHandler,
            _gameServerManageHandler.PortRegistry,
            _gameIpcBridge,
            timeProvider);
        _realtimeStreamHandler = new RealtimeStreamHandler(timeProvider);
        _netPacketFuzzHandler = new NetPacketFuzzHandler(timeProvider);
        _memorySyncHandler = new MemorySyncHandler(timeProvider);

        _matchAnalyticsHandler = new MatchAnalyticsHandler(timeProvider);
        _ciTriggerHandler = new CiTriggerHandler(timeProvider);
        _dbMigrateHandler = new DbMigrateHandler(timeProvider);
        _canaryDeployHandler = new CanaryDeployHandler(timeProvider);
    }

    partial void RegisterDedicatedServerTools()
    {
        RegisterTool(CreateServerManageDefinition(), _serverManageHandler.HandleAsync, McpToolCategory.ServerManagement);
        RegisterTool(CreateCompelManageDefinition("kongor_compel_manage"), _compelManageHandler.HandleAsync, McpToolCategory.ServerManagement);
        RegisterTool(CreateGameServerManageDefinition("kongor_game_server_manage"), _gameServerManageHandler.HandleAsync, McpToolCategory.ServerManagement);
        RegisterTool(CreateGameServerCommandDefinition("kongor_game_server_command"), _gameCommandHandler.HandleServerCommandAsync, McpToolCategory.GameCommand);

        RegisterTool(CreateMatchStateDefinition(), _matchStateHandler.HandleAsync, McpToolCategory.ReadOnly);
        RegisterTool(CreateReplayInspectDefinition(), _replayInspectHandler.HandleAsync, McpToolCategory.ReadOnly);
        RegisterTool(CreateReplayPlaybackDefinition(), _replayPlaybackHandler.HandleAsync, McpToolCategory.ReadOnly);
        RegisterTool(CreateDbFixtureDefinition(), _dbFixtureHandler.HandleAsync, McpToolCategory.DatabaseOps);
        RegisterTool(CreateMatchmakingSimulateDefinition("kongor_matchmaking_simulate"), _matchmakingSimulateHandler.HandleAsync, McpToolCategory.Simulation);
        RegisterTool(CreateScrimmageLaunchDefinition("kongor_scrimmage_launch"), _scrimmageLaunchHandler.HandleAsync, McpToolCategory.Simulation);
        RegisterTool(CreateDbMigrationTestDefinition("kongor_db_migration_test"), _dbMigrationTestHandler.HandleAsync, McpToolCategory.DatabaseOps);
        RegisterTool(CreateBotChoreographyDefinition("kongor_bot_choreography"), _botChoreographyHandler.HandleAsync, McpToolCategory.Simulation);
        RegisterTool(CreateNetworkImpairSimulateDefinition("kongor_network_impair_simulate"), _networkImpairSimulateHandler.HandleAsync, McpToolCategory.NetworkDiagnostics);
        RegisterTool(CreateCacheCoherenceVerifyDefinition("kongor_cache_coherence_verify"), _cacheCoherenceVerifyHandler.HandleAsync, McpToolCategory.DatabaseOps);

        RegisterTool(CreateVectorSearchDefinition("kongor_vector_search"), _vectorSearchHandler.HandleAsync, McpToolCategory.ReadOnly);
        RegisterAlias("kongor_vector", "kongor_vector_search");

        RegisterTool(CreateServerPerfStreamDefinition("kongor_server_perf_stream"), _serverPerfStreamHandler.HandleAsync, McpToolCategory.ReadOnly);
        RegisterTool(CreateReplayDiffDefinition("kongor_replay_diff"), _replayDiffHandler.HandleAsync, McpToolCategory.ReadOnly);
        RegisterTool(CreateDbPoolDiagnosticsDefinition("kongor_db_pool_diagnostics"), _dbPoolDiagnosticsHandler.HandleAsync, McpToolCategory.DatabaseOps);
#if !PLAYTEST_BUILD
        RegisterTool(CreateAppEvalScriptDefinition("kongor_app_eval"), _appEvalScriptHandler.HandleAsync, McpToolCategory.GameCommand);
#endif
        RegisterTool(CreateStateSnapshotDefinition("kongor_state_snapshot"), _stateSnapshotHandler.HandleAsync, McpToolCategory.ReadOnly);

        RegisterTool(CreateNetworkChaosDefinition("kongor_network_chaos"), _networkChaosHandler.HandleAsync, McpToolCategory.NetworkDiagnostics);
        RegisterTool(CreateRealtimeStreamDefinition("kongor_realtime_stream"), _realtimeStreamHandler.HandleAsync, McpToolCategory.ReadOnly);

        RegisterTool(CreateNetPacketFuzzDefinition("kongor_net_packet_fuzz"), _netPacketFuzzHandler.HandleAsync, McpToolCategory.NetworkDiagnostics);
        RegisterAlias("packet_fuzz", "kongor_net_packet_fuzz");
        RegisterAlias("net_fuzz", "kongor_net_packet_fuzz");

        RegisterTool(CreateMemorySyncDefinition("kongor_memory_sync"), _memorySyncHandler.HandleAsync, McpToolCategory.DatabaseOps);
        RegisterAlias("memory_sync", "kongor_memory_sync");






        RegisterTool(CreateMatchAnalyticsDefinition("kongor_match_analytics"), _matchAnalyticsHandler.HandleAsync, McpToolCategory.ReadOnly);
        RegisterAlias("match_analytics", "kongor_match_analytics");
        RegisterAlias("kongor_lakehouse_query", "kongor_match_analytics");

        RegisterTool(CreateCiTriggerDefinition("kongor_ci_trigger"), _ciTriggerHandler.HandleAsync, McpToolCategory.ServerManagement);
        RegisterAlias("ci_trigger", "kongor_ci_trigger");

        RegisterTool(CreateDbMigrateDefinition("kongor_db_migrate"), _dbMigrateHandler.HandleAsync, McpToolCategory.DatabaseOps);
        RegisterAlias("db_migrate", "kongor_db_migrate");

        RegisterTool(CreateCanaryDeployDefinition("kongor_deploy_canary"), _canaryDeployHandler.HandleAsync, McpToolCategory.ServerManagement);
        RegisterAlias("deploy_canary", "kongor_deploy_canary");
    }

    public static ToolDefinition CreateServerManageDefinition() => new()
    {
        Name = "kongor_server_manage",
        Description = "Start, stop, restart, or inspect the health and port status of the Aspire microservice cluster (Master Server, Chat Server, Web API, Web UI, MERRICK DB, Redis). Dedicated to Aspire cluster orchestration. NOT for game servers: use kongor_compel_manage for the COMPEL fleet supervisor or kongor_game_server_manage for individual hon_x64.exe -dedicated workers.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = "kongor_server_manageArguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["action"] = new()
                {
                    Type = "string",
                    Title = "Action",
                    Description = "Lifecycle action to perform ('arcade_idle_mode' switches Queue Arcade idle behavior between 'hibernate' and 'selfplay').",
                    Enum = ["start", "stop", "restart", "status", "logs", "arcade_idle_mode"]
                },
                ["mode"] = new()
                {
                    Type = "string",
                    Title = "Queue Arcade Idle Mode",
                    Description = "Mode for action='arcade_idle_mode': 'hibernate', 'selfplay', or 'status'.",
                    Enum = ["hibernate", "selfplay", "status"]
                },
                ["target"] = new()
                {
                    Type = "string",
                    Title = "Target",
                    Description = "Specific microservice or cluster target.",
                    Enum = ["cluster", "docker-stack", "master-server", "chat-server", "web-portal-api", "web-portal-ui", "redis", "database", "pyroscope", "aspire-host", "compel", "control-panel", "all"],
                    Default = "cluster"
                },
                ["withGameServers"] = new()
                {
                    Type = "boolean",
                    Title = "With Game Servers",
                    Description = "Include COMPEL dedicated game servers.",
                    Default = false
                },
                ["lines"] = new()
                {
                    Type = "integer",
                    Title = "Lines",
                    Description = "Number of recent log lines to retrieve (default: 50).",
                    Default = 50
                },
                ["level"] = new()
                {
                    Type = "string",
                    Title = "Level",
                    Description = "Log level severity filter: 'all', 'error', 'warn', 'info'.",
                    Enum = ["all", "error", "warn", "info"],
                    Default = "all"
                }
            },
            Required = ["action"]
        }
    };

    public static ToolDefinition CreateCompelManageDefinition(string name = "kongor_compel_manage") => new()
    {
        Name = name,
        Description = "Controls the COMPEL dedicated server supervisor & REST API on port 11234. Queries health and telemetry (/ping, /status, /api/servers), starts and stops worker instance pools (/instances/start, /instances/stop), and manages the COMPEL server fleet host. Preferred way to run game servers (matches the production path). Use kongor_game_server_manage only for raw single-worker debugging without COMPEL; use kongor_server_manage for the Aspire services.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = $"{name}Arguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["action"] = new()
                {
                    Type = "string",
                    Title = "Action",
                    Description = "Lifecycle action to perform: status, query, servers, start, stop, or instances.",
                    Enum = ["start", "stop", "status", "query", "servers", "instances"]
                },
                ["host"] = new()
                {
                    Type = "string",
                    Title = "Host",
                    Description = "COMPEL control plane HTTP host and port.",
                    Default = "http://127.0.0.1:11234"
                },
                ["authToken"] = new()
                {
                    Type = "string",
                    Title = "Auth Token",
                    Description = "Bearer authentication token for protected control plane management endpoints."
                },
                ["instancesAction"] = new()
                {
                    Type = "string",
                    Title = "Instances Action",
                    Description = "Action for 'instances': query, start, stop, or restart.",
                    Enum = ["query", "start", "stop", "restart"],
                    Default = "query"
                }
            },
            Required = ["action"]
        }
    };

    public static ToolDefinition CreateGameServerManageDefinition(string name = "kongor_game_server_manage") => new()
    {
        Name = name,
        Description = "Controls dedicated game server worker instances (hon_x64.exe running in -dedicated mode). Spawns new server workers on specific ports, discovers running dedicated workers, inspects telemetry, stops workers by PID, or terminates all server workers. Low-level: bypasses the COMPEL supervisor (no auto-restart, no disconnect grace). Prefer kongor_compel_manage unless you are debugging a single worker.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = $"{name}Arguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["action"] = new()
                {
                    Type = "string",
                    Title = "Action",
                    Description = "Server worker action to perform.",
                    Enum = ["launch", "stop", "status", "stop_all", "logs", "command"]
                },
                ["command"] = new()
                {
                    Type = "string",
                    Title = "Command",
                    Description = "In-game console command or cvar to pipe to the dedicated server (e.g. 'AddFakePlayer 2', 'kick 1', 'restart')."
                },
                ["lines"] = new()
                {
                    Type = "integer",
                    Title = "Lines",
                    Description = "Number of recent log lines to retrieve (default: 50).",
                    Default = 50
                },
                ["level"] = new()
                {
                    Type = "string",
                    Title = "Level",
                    Description = "Log level severity filter: 'all', 'error', 'warn', 'info'.",
                    Enum = ["all", "error", "warn", "info"],
                    Default = "all"
                },
                ["processId"] = new()
                {
                    Type = "integer",
                    Title = "Process ID",
                    Description = "Process ID of the specific dedicated server worker to stop."
                },
                ["port"] = new()
                {
                    Type = "integer",
                    Title = "Port",
                    Description = "UDP game server listen port to allocate.",
                    Default = 11235
                },
                ["masterServer"] = new()
                {
                    Type = "string",
                    Title = "Master Server",
                    Description = "Master server address to register this dedicated server with.",
                    Default = "127.0.0.1:55555"
                },
                ["serverName"] = new()
                {
                    Type = "string",
                    Title = "Server Name",
                    Description = "Display name advertised to master server and matchmaking.",
                    Default = "Project KONGOR Dedicated Server"
                },
                ["map"] = new()
                {
                    Type = "string",
                    Title = "Map",
                    Description = "Initial map to load on the dedicated server.",
                    Default = "caldavar"
                },
                ["customServerPath"] = new()
                {
                    Type = "string",
                    Title = "Custom Server Path",
                    Description = "Custom path to hon_x64.exe dedicated server binary."
                },
                ["tickrate"] = new()
                {
                    Type = "integer",
                    Title = "Tickrate",
                    Description = "Simulation tickrate in Hz (canonical default: 20 for 50ms K2 simulation step; 30/60 supported). Clamped between 20 and 60.",
                    Default = 20
                }
            },
            Required = ["action"]
        }
    };

    public static ToolDefinition CreateGameServerCommandDefinition(string name = "kongor_game_server_command") => new()
    {
        Name = name,
        Description = "Send console commands or cvar adjustments specifically to a dedicated game server worker (hon_x64.exe -dedicated) on a target port (e.g. 11235) via console stdin and port-specific Named Pipe.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = $"{name}Arguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["port"] = new()
                {
                    Type = "integer",
                    Title = "Port",
                    Description = "The target UDP listen port of the dedicated server worker (e.g. 11235).",
                    Default = 11235
                },
                ["command"] = new()
                {
                    Type = "string",
                    Title = "Command",
                    Description = "The console command to execute on the dedicated server (e.g. 'AddFakePlayer 2', 'playerlist', 'kick 1', 'ServerStatus', 'RestartMatch')."
                },
                ["timeoutMs"] = new()
                {
                    Type = "integer",
                    Title = "Timeout Ms",
                    Description = "Timeout in milliseconds for command response.",
                    Default = 3000
                }
            },
            Required = ["command"]
        }
    };

    public static ToolDefinition CreateMatchStateDefinition(string name = "kongor_match_state") => new()
    {
        Name = name,
        Description = "Read full live in-game match state without screen scraping, including match phase, game time, gold, scores, connected players, hero names, and units via IPC or dedicated server inspection. Supports live client <-> dedicated server state reconciliation and desync delta detection ('client', 'server', 'both', 'reconciled').",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = $"{name}Arguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["port"] = new()
                {
                    Type = "integer",
                    Title = "Dedicated Server Port",
                    Description = "Target port for dedicated server queries (default: 11235)."
                },
                ["includePlayers"] = new()
                {
                    Type = "boolean",
                    Title = "Include Players",
                    Description = "Whether to include detailed player inventories, stats, and hero info.",
                    Default = true
                },
                ["includeUnits"] = new()
                {
                    Type = "boolean",
                    Title = "Include Units",
                    Description = "Whether to include non-hero spawned units (creeps, neutrals, bosses).",
                    Default = false
                },
                ["target"] = new()
                {
                    Type = "string",
                    Title = "Target",
                    Description = "Target to query: 'client', 'server', 'both', or 'reconciled' (compares client view against server ground-truth authoritative simulation state and returns desync deltas).",
                    Enum = ["client", "server", "both", "reconciled"],
                    Default = "both"
                },
                ["toleranceEpsilon"] = new()
                {
                    Type = "number",
                    Title = "Tolerance Epsilon",
                    Description = "Allowed 3D spatial coordinate drift in units before flagging a position desync (default: 50.0).",
                    Default = 50.0
                },
                ["maxClockDrift"] = new()
                {
                    Type = "number",
                    Title = "Max Clock Drift",
                    Description = "Allowed simulation clock drift in seconds before flagging a clock desync (default: 1.0).",
                    Default = 1.0
                }
            },
            Required = []
        }
    };

    public static ToolDefinition CreateReplayInspectDefinition(string name = "kongor_replay_inspect") => new()
    {
        Name = name,
        Description = "Parse .honreplay archives, extract match metadata (matchId, map, duration, winner, version, server), discover replays, parse player scores and items, or extract full chronological combat log timeline without starting a game client.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = $"{name}Arguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["action"] = new()
                {
                    Type = "string",
                    Title = "Action",
                    Description = "Replay inspection action: 'inspect' / 'summary' (extract metadata & scoreboard), 'list' / 'find' (discover replays on disk), or 'combat_log' / 'timeline' (extract chronological combat events).",
                    Enum = ["inspect", "summary", "list", "find", "combat_log", "timeline"],
                    Default = "inspect"
                },
                ["path"] = new()
                {
                    Type = "string",
                    Title = "Replay Path",
                    Description = "Absolute path or filename in replays directory of the .honreplay file to inspect."
                },
                ["matchId"] = new()
                {
                    Type = "string",
                    Title = "Match ID",
                    Description = "Target match ID to inspect from active dedicated server worker or replay archives."
                },
                ["port"] = new()
                {
                    Type = "integer",
                    Title = "Server Listen Port",
                    Description = "Optional dedicated game server port (e.g. 11235) to inspect active match replay metadata from."
                },
                ["summaryOnly"] = new()
                {
                    Type = "boolean",
                    Title = "Summary Only",
                    Description = "Whether to omit detailed player inventory breakdown and return high-level match stats only.",
                    Default = false
                },
                ["hero"] = new()
                {
                    Type = "string",
                    Title = "Hero Filter",
                    Description = "Filter combat log events involving a specific hero (e.g., 'Hero_Pyromancer')."
                },
                ["player"] = new()
                {
                    Type = "string",
                    Title = "Player Filter",
                    Description = "Filter combat log events involving a specific player name."
                },
                ["eventType"] = new()
                {
                    Type = "string",
                    Title = "Event Type Filter",
                    Description = "Filter combat log events by type ('FirstBlood', 'HeroKill', 'TowerKill', 'KongorSlay', 'DoubleKill', 'TripleKill', 'BaseRaze').",
                    Enum = ["FirstBlood", "HeroKill", "TowerKill", "KongorSlay", "DoubleKill", "TripleKill", "BaseRaze"]
                }
            },
            Required = ["action"]
        }
    };

    public static ToolDefinition CreateReplayPlaybackDefinition(string name = "kongor_replay_playback") => new()
    {
        Name = name,
        Description = "Control replay playback or live match spectating in a running game client or dedicated server worker: spectate live match, record demo, start, pause, resume, stop, seek to time, or adjust playback speed multiplier.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = $"{name}Arguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["action"] = new()
                {
                    Type = "string",
                    Title = "Action",
                    Description = "Playback control action to execute: 'start', 'pause', 'resume', 'stop', 'seek', 'speed', 'spectate' (connect client as spectator to dedicated server), 'record_demo' (pipe RecordDemo to worker/client), 'stop_demo', 'camera', 'fog'.",
                    Enum = ["start", "pause", "resume", "stop", "seek", "speed", "spectate", "record_demo", "stop_demo", "camera", "fog"]
                },
                ["target"] = new()
                {
                    Type = "string",
                    Title = "Target Subsystem",
                    Description = "Target subsystem for the command: 'client', 'server', or 'both'.",
                    Enum = ["client", "server", "both"],
                    Default = "client"
                },
                ["port"] = new()
                {
                    Type = "integer",
                    Title = "Server Listen Port",
                    Description = "Target dedicated server listen port (e.g. 11235) when recording demos or connecting client as spectator."
                },
                ["demoName"] = new()
                {
                    Type = "string",
                    Title = "Demo Name",
                    Description = "Name of the demo file when recording a demo via 'record_demo'."
                },
                ["replayFile"] = new()
                {
                    Type = "string",
                    Title = "Replay File",
                    Description = "Path to the .honreplay file when starting playback."
                },
                ["time"] = new()
                {
                    Type = "number",
                    Title = "Time in Seconds",
                    Description = "Seek target timestamp in seconds (for 'seek' action)."
                },
                ["speed"] = new()
                {
                    Type = "number",
                    Title = "Playback Speed Multiplier",
                    Description = "Playback speed factor (e.g. 0.5, 1.0, 2.0, 4.0, 8.0)."
                },
                ["hero"] = new()
                {
                    Type = "string",
                    Title = "Camera Target Hero",
                    Description = "Hero identifier to bind camera to (for 'camera' action, e.g. 'Hero_Pyromancer')."
                },
                ["enabled"] = new()
                {
                    Type = "boolean",
                    Title = "Enabled Flag",
                    Description = "Toggle flag for fog of war (for 'fog' action)."
                }
            },
            Required = ["action"]
        }
    };

    public static ToolDefinition CreateDbFixtureDefinition(string name = "kongor_db_fixture") => new()
    {
        Name = name,
        Description = "Seed or reset deterministic test fixtures (users, accounts, match histories, hero stats) in the local development database.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = $"{name}Arguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["action"] = new()
                {
                    Type = "string",
                    Title = "Action",
                    Description = "Database fixture operation to execute.",
                    Enum = ["seed", "reset", "query", "status"]
                },
                ["fixture"] = new()
                {
                    Type = "string",
                    Title = "Fixture Name",
                    Description = "Target fixture or table: 'accounts', 'match_history', 'heroes', 'all'.",
                    Enum = ["accounts", "match_history", "heroes", "all"],
                    Default = "all"
                },
                ["count"] = new()
                {
                    Type = "integer",
                    Title = "Record Count",
                    Description = "Number of sample records to seed (where applicable).",
                    Default = 10
                }
            },
            Required = ["action"]
        }
    };

    public static ToolDefinition CreateMatchmakingSimulateDefinition(string name = "kongor_matchmaking_simulate") => new()
    {
        Name = name,
        Description = "Simulates up to 10 headless virtual TCP clients connecting to ChatServer, executing binary handshake (NET_CHAT_CL_CONNECT 0x0C00) and matchmaking queue requests (NET_CHAT_CL_TMM_GROUP_CREATE 0x0C0A), tracking latency benchmarks, reporting match assignment details, and cleanly closing all sockets without leaks.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = $"{name}Arguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["clientCount"] = new()
                {
                    Type = "integer",
                    Title = "Client Count",
                    Description = "Number of virtual clients to connect (1-10). Default is 10.",
                    Default = 10
                },
                ["serverPort"] = new()
                {
                    Type = "integer",
                    Title = "Server Port",
                    Description = "Explicit ChatServer port. If omitted, uses tiered discovery (explicit -> Aspire -> fallback 11031)."
                },
                ["serverHost"] = new()
                {
                    Type = "string",
                    Title = "Server Host",
                    Description = "ChatServer host address. Default is 127.0.0.1.",
                    Default = "127.0.0.1"
                },
                ["queueType"] = new()
                {
                    Type = "string",
                    Title = "Queue Type",
                    Description = "Matchmaking queue type ('pvp', 'solo', 'coop'). Default is 'pvp'.",
                    Enum = ["pvp", "solo", "coop"],
                    Default = "pvp"
                },
                ["gameType"] = new()
                {
                    Type = "string",
                    Title = "Game Type",
                    Description = "Game type ('normal', 'casual', 'midwars'). Default is 'normal'.",
                    Enum = ["normal", "casual", "midwars"],
                    Default = "normal"
                },
                ["mapName"] = new()
                {
                    Type = "string",
                    Title = "Map Name",
                    Description = "Target map name (default: caldavar).",
                    Default = "caldavar"
                },
                ["gameMode"] = new()
                {
                    Type = "string",
                    Title = "Game Mode",
                    Description = "Game mode (e.g. 'ap', 'ar', 'sd'). Default is 'ap'.",
                    Enum = ["ap", "ar", "sd", "bd", "bp", "cm"],
                    Default = "ap"
                },
                ["region"] = new()
                {
                    Type = "string",
                    Title = "Region",
                    Description = "Server region code (e.g. 'USE', 'USW', 'EU'). Default is 'USE'.",
                    Enum = ["USE", "USW", "EU", "SEA", "AU", "BR", "RU"],
                    Default = "USE"
                },
                ["timeoutSeconds"] = new()
                {
                    Type = "integer",
                    Title = "Timeout Seconds",
                    Description = "Benchmark timeout in seconds (default: 15).",
                    Default = 15
                },
                ["simulateMatchServer"] = new()
                {
                    Type = "boolean",
                    Title = "Simulate Match Server",
                    Description = "Simulate mock match server on port 11032 for full match assignment confirmation.",
                    Default = false
                }
            },
            Required = []
        }
    };

    public static ToolDefinition CreateScrimmageLaunchDefinition(string name = "kongor_scrimmage_launch") => new()
    {
        Name = name,
        Description = "Automated 5v5 local scrimmage orchestrator. Launches or binds to a dedicated server worker on a designated port (default: 11235 or dynamically allocated from port registry), connects local client instance to Legion slot 0, and populates the remaining slots via port-addressed AddFakePlayer commands (4 bots on Team 1, 5 bots on Team 2). Confirms active match transition to GamePhase 3.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = $"{name}Arguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["action"] = new()
                {
                    Type = "string",
                    Title = "Action",
                    Description = "Scrimmage action to perform: 'launch' (start server, connect client, fill bots, begin match), 'status' (query active match state & player counts), 'list' (list all active scrimmage sessions), 'errors' (view remediation history), or 'stop' (disconnect client and terminate server).",
                    Enum = ["launch", "status", "list", "errors", "stop"],
                    Default = "launch"
                },
                ["port"] = new()
                {
                    Type = "integer",
                    Title = "Port",
                    Description = "Target UDP listen port for the dedicated server worker (default: 11235 or resolved from active port registry).",
                    Default = 11235
                },
                ["map"] = new()
                {
                    Type = "string",
                    Title = "Map",
                    Description = "Map to host on the dedicated server (default: 'caldavar').",
                    Default = "caldavar"
                },
                ["hero"] = new()
                {
                    Type = "string",
                    Title = "Hero",
                    Description = "Hero entity name for local client player (default: 'Hero_Pyromancer').",
                    Default = "Hero_Pyromancer"
                },
                ["mode"] = new()
                {
                    Type = "string",
                    Title = "Mode",
                    Description = "Game mode configuration (default: 'normal').",
                    Enum = ["normal", "casual", "practice", "bot_match"],
                    Default = "normal"
                },
                ["team1Bots"] = new()
                {
                    Type = "integer",
                    Title = "Team 1 Bots",
                    Description = "Number of bots to add to Team 1 (Legion) alongside human player (default: 4).",
                    Default = 4
                },
                ["team2Bots"] = new()
                {
                    Type = "integer",
                    Title = "Team 2 Bots",
                    Description = "Number of bots to add to Team 2 (Hellbourne) (default: 5).",
                    Default = 5
                },
                ["autoConnect"] = new()
                {
                    Type = "boolean",
                    Title = "Auto Connect Client",
                    Description = "Whether to automatically connect the local game client to the dedicated server (default: true).",
                    Default = true
                },
                ["rapidTerminateAfterLaunch"] = new()
                {
                    Type = "boolean",
                    Title = "Rapid Terminate After Launch",
                    Description = "Fast test execution guard: immediately terminates worker after verifying launch sequence to stay under inner-loop threshold (< 2.0s).",
                    Default = false
                }
            },
            Required = ["action"]
        }
    };

    public static ToolDefinition CreateDbMigrationTestDefinition(string name = "kongor_db_migration_test") => new()
    {
        Name = name,
        Description = "Automated schema drift verification, index presence testing, and transactional rollback validation across Supabase (PostgreSQL) and SQL Server.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = $"{name}Arguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["action"] = new()
                {
                    Type = "string",
                    Title = "Action",
                    Description = "Action to perform: 'verify_drift' (reconcile schema against EF Core models), 'test_rollback' (transactional probe & rollback), or 'status'.",
                    Enum = ["verify_drift", "test_rollback", "status"],
                    Default = "verify_drift"
                },
                ["provider"] = new()
                {
                    Type = "string",
                    Title = "Database Provider",
                    Description = "Target database provider: 'postgresql', 'sqlserver', or 'all' (default: 'all').",
                    Enum = ["postgresql", "sqlserver", "all"],
                    Default = "all"
                }
            },
            Required = ["action"]
        }
    };

    public static ToolDefinition CreateBotChoreographyDefinition(string name = "kongor_bot_choreography") => new()
    {
        Name = name,
        Description = "Deterministic waypoint patrol, ability combo sequencing, and bot match test choreography strictly targeting specific dedicated server ports via dual-channel routing or the running game client.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = $"{name}Arguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["action"] = new()
                {
                    Type = "string",
                    Title = "Action",
                    Description = "Choreography action: 'execute' (run custom action sequence), 'preset' (run named combat scenario), 'stop' (halt bots), 'list' (list active sessions), or 'status' (view session state).",
                    Enum = ["execute", "preset", "stop", "list", "status"],
                    Default = "preset"
                },
                ["target"] = new()
                {
                    Type = "string",
                    Title = "Target Execution Channel",
                    Description = "Execution target: 'server' / 'server:{port}' (dedicated server stdin/IPC), 'client' (running game client Lua/IPC), or 'both' (synchronized concurrent server bot orchestration and client choreography).",
                    Default = "server:11235"
                },
                ["port"] = new()
                {
                    Type = "integer",
                    Title = "Server Port",
                    Description = "Target dedicated server listen port (mandatory routing rule: e.g. 11235).",
                    Default = 11235
                },
                ["preset"] = new()
                {
                    Type = "string",
                    Title = "Choreography Preset",
                    Description = "Preset scenario name: 'pyro_mid_combo', 'kongor_pit_clash', 'dual_lane_patrol', 'push_and_defend', 'gank_rotation', or 'teamfight_5v5'.",
                    Enum = ["pyro_mid_combo", "kongor_pit_clash", "dual_lane_patrol", "push_and_defend", "gank_rotation", "teamfight_5v5"],
                    Default = "pyro_mid_combo"
                },
                ["actions"] = new()
                {
                    Type = "array",
                    Title = "Custom Actions",
                    Description = "Array of custom bot action definitions (bot, type, x, y, z, slot, target_bot, delay_ms).",
                    Items = new ToolPropertySchema
                    {
                        Type = "object",
                        Title = "Bot Action",
                        Description = "Single bot choreography step.",
                        Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
                        {
                            ["bot"] = new() { Type = "integer", Title = "Bot Index", Description = "Bot player slot index." },
                            ["type"] = new() { Type = "string", Title = "Order Type", Description = "Order type (e.g. 'move', 'attack', 'cast', 'hold')." },
                            ["x"] = new() { Type = "number", Title = "X", Description = "Target world X coordinate." },
                            ["y"] = new() { Type = "number", Title = "Y", Description = "Target world Y coordinate." },
                            ["z"] = new() { Type = "number", Title = "Z", Description = "Target world Z coordinate." },
                            ["slot"] = new() { Type = "integer", Title = "Ability/Item Slot", Description = "Ability or inventory slot index." },
                            ["target_bot"] = new() { Type = "integer", Title = "Target Bot", Description = "Target bot index for unit-targeted actions." },
                            ["delay_ms"] = new() { Type = "integer", Title = "Delay (ms)", Description = "Delay in milliseconds before executing this step." }
                        }
                    }
                }
            },
            Required = ["action"]
        }
    };

    public static ToolDefinition CreateNetworkImpairSimulateDefinition(string name = "kongor_network_impair_simulate") => new()
    {
        Name = name,
        Description = "In-engine and proxy simulation of UDP latency, packet loss, and jitter for network resilience and lag testing, adhering to port targeting rules.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = $"{name}Arguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["action"] = new()
                {
                    Type = "string",
                    Title = "Action",
                    Description = "Action: 'set' (configure latency/loss/jitter), 'profile' (apply named network profile), or 'reset' (restore zero impairment).",
                    Enum = ["set", "profile", "reset"],
                    Default = "set"
                },
                ["target"] = new()
                {
                    Type = "string",
                    Title = "Target Destination",
                    Description = "Target execution node: 'server' (dedicated server port), 'client' (player client), or 'both' (concurrent client and server).",
                    Enum = ["server", "client", "both"],
                    Default = "server"
                },
                ["port"] = new()
                {
                    Type = "integer",
                    Title = "Target Port",
                    Description = "Target dedicated server listen port (default: 11235).",
                    Default = 11235
                },
                ["latencyMs"] = new()
                {
                    Type = "integer",
                    Title = "Latency (Milliseconds)",
                    Description = "Simulated one-way UDP packet latency in milliseconds (0–1000ms, default: 120ms).",
                    Default = 120
                },
                ["packetLossPercent"] = new()
                {
                    Type = "number",
                    Title = "Packet Loss Percentage",
                    Description = "Simulated UDP packet loss percentage (0.0–100.0%, default: 0.0%).",
                    Default = 0.0
                },
                ["jitterMs"] = new()
                {
                    Type = "integer",
                    Title = "Jitter (Milliseconds)",
                    Description = "Simulated UDP latency variation/jitter in milliseconds (0–500ms, default: 0ms).",
                    Default = 0
                },
                ["profile"] = new()
                {
                    Type = "string",
                    Title = "Impairment Profile",
                    Description = "Named profile: 'oceanic_lag', 'burst_packet_loss', or 'mobile_jitter'.",
                    Enum = ["oceanic_lag", "burst_packet_loss", "mobile_jitter"]
                }
            },
            Required = ["action"]
        }
    };

    public static ToolDefinition CreateCacheCoherenceVerifyDefinition(string name = "kongor_cache_coherence_verify") => new()
    {
        Name = name,
        Description = "Automated reconciliation and self-healing between EF Core relational entities and Redis distributed key states across multi-region and local clusters.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = $"{name}Arguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["action"] = new()
                {
                    Type = "string",
                    Title = "Action",
                    Description = "Action: 'reconcile' (scan and verify key parity) or 'heal' (evict orphans and refresh stale keys).",
                    Enum = ["reconcile", "heal"],
                    Default = "reconcile"
                },
                ["scope"] = new()
                {
                    Type = "string",
                    Title = "Verification Scope",
                    Description = "Key namespace scope: 'accounts', 'sessions', 'inventory', 'telemetry', or 'all' (default: 'all').",
                    Enum = ["accounts", "sessions", "inventory", "telemetry", "all"],
                    Default = "all"
                }
            },
            Required = ["action"]
        }
    };

    public static ToolDefinition CreateVectorSearchDefinition(string name = "kongor_vector_search") => new()
    {
        Name = name,
        Description = "Sub-millisecond vector similarity search, element ingestion, and index management directly over Microsoft Garnet Vector Sets (VADD, VSIM, VREM, VINFO, VDIM).",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = $"{name}Arguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["action"] = new()
                {
                    Type = "string",
                    Title = "Action",
                    Description = "Action: 'search' (find nearest neighbors via VSIM), 'add' (insert vector via VADD), 'remove' (delete element via VREM), 'info' (query index metadata via VINFO), 'dimension' (query vector dimensions via VDIM), or 'list' (list indices).",
                    Enum = ["search", "add", "remove", "info", "dimension", "list"],
                    Default = "search"
                },
                ["key"] = new()
                {
                    Type = "string",
                    Title = "Index Key",
                    Description = "Vector set index key in Garnet (e.g. 'tactics:caldavar', 'replays:combat').",
                    Default = "vectors:default"
                },
                ["element"] = new()
                {
                    Type = "string",
                    Title = "Element Identifier",
                    Description = "Unique identifier of the element to add, remove, or use as reference query in search."
                },
                ["vector"] = new()
                {
                    Type = "array",
                    Title = "Vector Embedding",
                    Description = "32-bit floating point coordinate array (e.g. [0.12, 0.45, -0.89]).",
                    Items = new ToolPropertySchema
                    {
                        Type = "number",
                        Title = "Dimension Value",
                        Description = "32-bit floating-point vector component."
                    }
                },
                ["attributes"] = new()
                {
                    Type = "string",
                    Title = "Metadata Attributes",
                    Description = "Optional string or JSON payload stored alongside the vector element."
                },
                ["count"] = new()
                {
                    Type = "integer",
                    Title = "Result Count",
                    Description = "Maximum number of nearest neighbors to return (default: 10, max: 100).",
                    Default = 10
                },
                ["metric"] = new()
                {
                    Type = "string",
                    Title = "Distance Metric",
                    Description = "Distance metric: 'cosine' (default), 'l2' (euclidean), or 'ip' (inner product).",
                    Enum = ["cosine", "l2", "ip"],
                    Default = "cosine"
                },
                ["quantization"] = new()
                {
                    Type = "string",
                    Title = "Quantization",
                    Description = "Vector quantization: 'none' (default, 32-bit float), 'sq8' (8-bit scalar), or 'pq'.",
                    Enum = ["none", "sq8", "pq"],
                    Default = "none"
                },
                ["with_scores"] = new()
                {
                    Type = "boolean",
                    Title = "Include Distance Scores",
                    Description = "Whether to include distance scores in search results (default: true).",
                    Default = true
                },
                ["with_attribs"] = new()
                {
                    Type = "boolean",
                    Title = "Include Attributes",
                    Description = "Whether to return stored metadata attributes in search results (default: false).",
                    Default = false
                },
                ["epsilon"] = new()
                {
                    Type = "number",
                    Title = "Epsilon Distance Threshold",
                    Description = "Maximum distance threshold for results."
                },
                ["filter"] = new()
                {
                    Type = "string",
                    Title = "Filter Expression",
                    Description = "Attribute filter expression to prune search space."
                },
                ["pattern"] = new()
                {
                    Type = "string",
                    Title = "Search Pattern",
                    Description = "Key glob pattern for 'list' action (e.g. 'tactics:*').",
                    Default = "*"
                }
            },
            Required = ["action"]
        }
    };

    public static ToolDefinition CreateServerPerfStreamDefinition(string name = "kongor_server_perf_stream") => new()
    {
        Name = name,
        Description = "Sub-second dedicated server telemetry streaming reporting sleep duration overshoot, tick execution time (ms), and frame drift to verify 1.0ms timer resolution. Target port is strictly required per AGENTS.md.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = $"{name}Arguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["action"] = new()
                {
                    Type = "string",
                    Title = "Action",
                    Description = "Stream action: snapshot, start_stream, stop_stream.",
                    Enum = ["snapshot", "start_stream", "stop_stream"],
                    Default = "snapshot"
                },
                ["port"] = new()
                {
                    Type = "integer",
                    Title = "Dedicated Server Listen Port",
                    Description = "Dedicated server listen port (e.g. 11235). Required by AGENTS.md routing rules.",
                    Default = 11235
                },
                ["interval_ms"] = new()
                {
                    Type = "integer",
                    Title = "Sampling Interval Milliseconds",
                    Description = "Telemetry sampling interval in ms (10 - 5000).",
                    Default = 100
                }
            },
            Required = []
        }
    };

    public static ToolDefinition CreateReplayDiffDefinition(string name = "kongor_replay_diff") => new()
    {
        Name = name,
        Description = "Comparative frame-by-frame entity desync detection comparing server authoritative state against client replay streams.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = $"{name}Arguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["action"] = new()
                {
                    Type = "string",
                    Title = "Action",
                    Description = "Replay diff action: diff_metadata, diff_keyframes, compare_ticks, find_first_desync, live_compare.",
                    Enum = ["diff_metadata", "diff_keyframes", "compare_ticks", "find_first_desync", "live_compare"],
                    Default = "compare_ticks"
                },
                ["port"] = new() { Type = "integer", Title = "Server Port", Description = "Target dedicated server listen port. If omitted, dynamically resolved via ServerPortRegistry." },
                ["server_replay"] = new() { Type = "string", Title = "Server Replay Path", Description = "Path to authoritative server replay." },
                ["client_replay"] = new() { Type = "string", Title = "Client Replay Path", Description = "Path to client replay stream." },
                ["tick"] = new() { Type = "integer", Title = "Target Tick", Description = "Specific game tick or frame index to compare.", Default = 0 },
                ["tolerance"] = new() { Type = "number", Title = "Coordinate Tolerance", Description = "Coordinate delta threshold for desync.", Default = 0.01 },
                ["start_tick"] = new() { Type = "integer", Title = "Start Tick", Description = "Starting tick for binary search desync pinpointing.", Default = 0 },
                ["end_tick"] = new() { Type = "integer", Title = "End Tick", Description = "Ending tick for binary search desync pinpointing." }
            },
            Required = ["action"]
        }
    };

    public static ToolDefinition CreateDbPoolDiagnosticsDefinition(string name = "kongor_db_pool_diagnostics") => new()
    {
        Name = name,
        Description = "Real-time Npgsql and Microsoft.Data.SqlClient connection pool monitoring (active connections, idle count, wait duration, thread starvation).",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = $"{name}Arguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["action"] = new()
                {
                    Type = "string",
                    Title = "Action",
                    Description = "Diagnostic action: status, pool_metrics, starvation_check.",
                    Enum = ["status", "pool_metrics", "starvation_check"],
                    Default = "status"
                },
                ["provider"] = new()
                {
                    Type = "string",
                    Title = "Database Provider Filter",
                    Description = "Filter provider: all, npgsql, sqlclient.",
                    Enum = ["all", "npgsql", "sqlclient"],
                    Default = "all"
                }
            },
            Required = ["action"]
        }
    };

#if !PLAYTEST_BUILD
    public static ToolDefinition CreateAppEvalScriptDefinition(string name = "kongor_app_eval") => new()
    {
        Name = name,
        Description = "Dynamically compiles and evaluates arbitrary C# expressions or scripts in memory using Roslyn (Microsoft.CodeAnalysis.CSharp.Scripting) with injected DbContext, Redis, TimeProvider, and Print helpers.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = $"{name}Arguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["script"] = new()
                {
                    Type = "string",
                    Title = "C# Script / Expression",
                    Description = "The C# code or expression to evaluate (e.g. '1 + 1' or 'Db?.Accounts.Count()' or 'Variables[\"count\"] = 42')."
                },
                ["sessionId"] = new()
                {
                    Type = "string",
                    Title = "Session ID",
                    Description = "Optional session identifier to persist variables and execution state across successive calls.",
                    Default = "default"
                },
                ["resetSession"] = new()
                {
                    Type = "boolean",
                    Title = "Reset Session",
                    Description = "Whether to reset the session state and clear accumulated variables before execution.",
                    Default = false
                },
                ["timeoutMs"] = new()
                {
                    Type = "integer",
                    Title = "Timeout (ms)",
                    Description = "Execution timeout in milliseconds (default: 5000, max: 30000).",
                    Default = 5000
                }
            },
            Required = ["script"]
        }
    };

#endif

    public static ToolDefinition CreateStateSnapshotDefinition(string name = "kongor_state_snapshot") => new()
    {
        Name = name,
        Description = "Time-travel state snapshotting and rollback: capture or restore relational database tables (Accounts, Inventory, MatchStats) and Redis keyspaces to portable state bundles for instant rollback and regression testing.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = $"{name}Arguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["action"] = new()
                {
                    Type = "string",
                    Title = "Action",
                    Description = "Action to perform: 'create' (save state bundle), 'restore' (rollback to state bundle), 'list' (list available snapshots), 'diff' (compare live state to snapshot), or 'delete'.",
                    Enum = ["create", "restore", "list", "diff", "delete"],
                    Default = "create"
                },
                ["name"] = new()
                {
                    Type = "string",
                    Title = "Snapshot Name",
                    Description = "Name of the state snapshot bundle (default: 'default').",
                    Default = "default"
                },
                ["metadata"] = new()
                {
                    Type = "object",
                    Title = "Metadata",
                    Description = "Optional key-value metadata tags to attach to the snapshot bundle.",
                    Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
                    {
                        ["label"] = new() { Type = "string", Title = "Label", Description = "Human-readable snapshot label." },
                        ["owner"] = new() { Type = "string", Title = "Owner", Description = "Agent or developer identifier." },
                        ["notes"] = new() { Type = "string", Title = "Notes", Description = "Optional diagnostic notes." }
                    }
                }
            },
            Required = ["action"]
        }
    };


    public static ToolDefinition CreateNetworkChaosDefinition(string name = "kongor_network_chaos") => new()
    {
        Name = name,
        Description = "Injects live network impairment and chaos engineering: simulate latency spikes (100ms - 500ms), packet loss (5% - 25%), and socket connection resets with automatic auto-healing timers.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = $"{name}Arguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["action"] = new()
                {
                    Type = "string",
                    Title = "Action",
                    Description = "Action: 'inject' (set latency/drop), 'spike' (transient 350ms spike), 'drop' (packet drop burst), 'reset' (TCP reset), 'profile' (curated preset), 'heal' (clear rules), 'list'.",
                    Enum = ["inject", "spike", "drop", "reset", "profile", "heal", "list"],
                    Default = "inject"
                },
                ["target"] = new()
                {
                    Type = "string",
                    Title = "Target Type",
                    Description = "Target type or node: 'port'/'server' (dedicated server port), 'client' (player client), 'both' (concurrent server and client), or 'session' (socket session).",
                    Enum = ["port", "server", "client", "both", "session"],
                    Default = "port"
                },
                ["targetId"] = new()
                {
                    Type = "string",
                    Title = "Target ID",
                    Description = "Target listen port or session identifier (default: '11235').",
                    Default = "11235"
                },
                ["port"] = new()
                {
                    Type = "integer",
                    Title = "Listen Port",
                    Description = "Target dedicated server listen port (resolves dynamically from active workers if omitted)."
                },
                ["latencyMs"] = new()
                {
                    Type = "integer",
                    Title = "Latency (ms)",
                    Description = "Base network latency in milliseconds.",
                    Default = 120
                },
                ["dropRatePercent"] = new()
                {
                    Type = "number",
                    Title = "Packet Drop %",
                    Description = "Packet loss percentage (0.0 to 100.0).",
                    Default = 0.0
                },
                ["jitterMs"] = new()
                {
                    Type = "integer",
                    Title = "Jitter (ms)",
                    Description = "Packet delay jitter in milliseconds.",
                    Default = 15
                },
                ["durationSeconds"] = new()
                {
                    Type = "integer",
                    Title = "Duration / Auto-Heal (Seconds)",
                    Description = "Auto-healing duration after which impairment is automatically cleared (default: 30s).",
                    Default = 30
                },
                ["profile"] = new()
                {
                    Type = "string",
                    Title = "Chaos Profile",
                    Description = "Curated profile: 'flaky_wifi', 'packet_storm', 'oceanic_pipe', 'peer_reconnect_burst'.",
                    Enum = ["flaky_wifi", "packet_storm", "oceanic_pipe", "peer_reconnect_burst"]
                }
            },
            Required = ["action"]
        }
    };

    public static ToolDefinition CreateRealtimeStreamDefinition(string name = "kongor_realtime_stream") => new()
    {
        Name = name,
        Description = "Real-time streaming and telemetry inspector: tail rolling logs from microservices and dedicated servers, query in-memory OpenTelemetry trace activities, and inspect active socket sessions.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = $"{name}Arguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["action"] = new()
                {
                    Type = "string",
                    Title = "Action",
                    Description = "Action: 'tail' (stream rolling log entries), 'traces' (inspect OpenTelemetry activities), 'sessions' (active socket sessions), 'subscribe'.",
                    Enum = ["tail", "traces", "sessions", "subscribe"],
                    Default = "tail"
                },
                ["source"] = new()
                {
                    Type = "string",
                    Title = "Log Source",
                    Description = "Log source filter: 'master-server', 'chat-server', 'compel', 'dedicated', or 'all'.",
                    Enum = ["all", "master-server", "chat-server", "compel", "dedicated"],
                    Default = "all"
                },
                ["maxLines"] = new()
                {
                    Type = "integer",
                    Title = "Max Lines",
                    Description = "Maximum number of recent log or trace lines to return (default: 50, max: 500).",
                    Default = 50
                },
                ["filter"] = new()
                {
                    Type = "string",
                    Title = "Filter Expression",
                    Description = "Case-insensitive text filter to match log or trace messages."
                }
            },
            Required = ["action"]
        }
    };

    public static ToolDefinition CreateNetPacketFuzzDefinition(string name = "kongor_net_packet_fuzz") => new()
    {
        Name = name,
        Description = "Network Packet Dissector & Mutation Fuzzer: dissects K2 UDP/TCP binary frames, applies bit-flips, truncations, and boundary mutations, and measures network desyncs.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = $"{name}Arguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["action"] = new()
                {
                    Type = "string",
                    Title = "Action",
                    Description = "Fuzzing action: 'fuzz' (generate and mutate frames), 'dissect' (parse packet structure), 'inject' (send to socket/port), or 'verify_desync'.",
                    Default = "fuzz",
                    Enum = ["fuzz", "dissect", "inject", "verify_desync"]
                },
                ["mutation_strategy"] = new()
                {
                    Type = "string",
                    Title = "Mutation Strategy",
                    Description = "Mutation strategy to apply: 'bit_flip', 'truncate', 'byte_scramble', 'opcode_tamper', 'boundary_overflow', or 'zero_payload'.",
                    Default = "bit_flip",
                    Enum = ["bit_flip", "truncate", "byte_scramble", "opcode_tamper", "boundary_overflow", "zero_payload"]
                },
                ["mutation_rate"] = new()
                {
                    Type = "number",
                    Title = "Mutation Rate",
                    Description = "Probability of mutating individual bytes (0.001 to 1.0).",
                    Default = 0.05
                },
                ["packet_count"] = new()
                {
                    Type = "integer",
                    Title = "Packet Count",
                    Description = "Number of fuzzed packets to generate.",
                    Default = 5
                },
                ["target_port"] = new()
                {
                    Type = "integer",
                    Title = "Target Port",
                    Description = "Dedicated server UDP port to target (e.g. 11235).",
                    Default = 11235
                },
                ["packet_hex"] = new()
                {
                    Type = "string",
                    Title = "Packet Hex Data",
                    Description = "Optional raw packet hex string to mutate or dissect. Synthesizes standard K2 packet if omitted."
                }
            },
            Required = ["action"]
        }
    };

    public static ToolDefinition CreateMemorySyncDefinition(string name = "kongor_memory_sync") => new()
    {
        Name = name,
        Description = "Project Knowledge Graph & Persistent Memory Bridge: mines repository truth (AGENTS.md, docs/ISSUES.md, port mappings, engine invariants) into structured entities and relations conforming to @modelcontextprotocol/server-memory.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = $"{name}Arguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["action"] = new()
                {
                    Type = "string",
                    Title = "Action",
                    Description = "Memory operation: 'extract' (returns full knowledge graph), 'export' (writes artifacts/memory/kongor_memory_graph.json with live memory tool handoffs), 'query' (searches entities & relations), or 'status' (summary metrics).",
                    Default = "extract",
                    Enum = ["extract", "export", "query", "status"]
                },
                ["query"] = new()
                {
                    Type = "string",
                    Title = "Search Query",
                    Description = "Optional search keyword to filter entities, observations, and relations when action='query'."
                },
                ["entity_type"] = new()
                {
                    Type = "string",
                    Title = "Entity Type Filter",
                    Description = "Optional entity type filter (e.g. 'Subsystem', 'Engine_Invariant', 'Infrastructure', 'Architecture', 'K2_Lua_Global').",
                    Enum = ["Subsystem", "Engine_Invariant", "Infrastructure", "Architecture", "K2_Lua_Global"]
                },
                ["output_path"] = new()
                {
                    Type = "string",
                    Title = "Export Path",
                    Description = "Target JSON export path when action='export'. Defaults to 'artifacts/memory/kongor_memory_graph.json'."
                }
            },
            Required = ["action"]
        }
    };


    public static ToolDefinition CreateMatchAnalyticsDefinition(string name = "kongor_match_analytics") => new()
    {
        Name = name,
        Description = "Direct DuckDB Telemetry Lakehouse Query Engine: executes in-process analytical SQL queries over compressed Apache Parquet match combat logs with sub-millisecond latency. Generates 2D minimap kill/death heatmaps, 10-player match breakdown scorecards, hero matchup cross-tabulations, and career combat summaries.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = $"{name}Arguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["action"] = new()
                {
                    Type = "string",
                    Title = "Action",
                    Description = "Lakehouse analytical query action: 'query_sql' (arbitrary analytical SQL query), 'player_summary' (career combat metrics for account), 'match_breakdown' (10-player combat scorecard for match), 'heatmap' (spatial minimap kill/death coordinates), 'hero_matchup' (head-to-head hero matchup winrates), or 'list_files' (discovers match parquet archives).",
                    Default = "list_files",
                    Enum = ["query_sql", "player_summary", "match_breakdown", "heatmap", "hero_matchup", "list_files"]
                },
                ["sql"] = new()
                {
                    Type = "string",
                    Title = "SQL Query",
                    Description = "Analytical read-only SQL query to execute against DuckDB (e.g. 'SELECT event_type, COUNT(*) FROM match_combat_events GROUP BY event_type'). Required when action='query_sql'."
                },
                ["parquet_path"] = new()
                {
                    Type = "string",
                    Title = "Parquet File Path",
                    Description = "Optional explicit path to target .parquet file (e.g. 'userdataKONGOR/telemetry/lakehouse/match_1001.parquet'). If omitted, auto-discovers available match files."
                },
                ["match_id"] = new()
                {
                    Type = "integer",
                    Title = "Match ID",
                    Description = "Target match ID when action is 'match_breakdown', 'heatmap', or 'player_summary' (e.g. 1001)."
                },
                ["account_id"] = new()
                {
                    Type = "integer",
                    Title = "Account ID",
                    Description = "Target player account ID when action is 'player_summary' (e.g. 1)."
                },
                ["hero_id"] = new()
                {
                    Type = "integer",
                    Title = "Hero ID",
                    Description = "Target hero ID when action is 'hero_matchup' (e.g. 1 for Hero_Pyromancer, 2 for Hero_Magmus)."
                },
                ["opponent_hero_id"] = new()
                {
                    Type = "integer",
                    Title = "Opponent Hero ID",
                    Description = "Optional opponent hero ID to filter head-to-head statistics when action is 'hero_matchup'."
                },
                ["event_type"] = new()
                {
                    Type = "string",
                    Title = "Event Type",
                    Description = "Optional event category filter when action is 'heatmap' (e.g. 'HeroKill', 'HeroDeath', 'WardPlaced').",
                    Enum = ["HeroKill", "HeroDeath", "HeroAssist", "WardPlaced", "BuildingDestroyed", "GoldEarned"]
                },
                ["bucket_size"] = new()
                {
                    Type = "integer",
                    Title = "Heatmap Bucket Size",
                    Description = "Spatial grid cell dimension in map coordinate units for heatmap aggregation (default: 100)."
                },
                ["limit"] = new()
                {
                    Type = "integer",
                    Title = "Row Limit",
                    Description = "Maximum number of rows to return for SQL queries (default: 100, max: 5000)."
                },
                ["directory_path"] = new()
                {
                    Type = "string",
                    Title = "Directory Path",
                    Description = "Optional root directory to scan for parquet files when action='list_files'."
                }
            },
            Required = ["action"]
        }
    };

    public static ToolDefinition CreateCiTriggerDefinition(string name = "kongor_ci_trigger") => new()
    {
        Name = name,
        Description = "GitHub Actions CI/CD Pipeline Dispatcher & Monitor: triggers, polls, views logs, lists runs, and cancels CI/CD workflows directly from the IDE without browser context-switching.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = $"{name}Arguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["action"] = new()
                {
                    Type = "string",
                    Title = "Action",
                    Description = "CI operation: 'trigger' (dispatches a workflow), 'status' (checks current run status), 'logs' (fetches failed step and job logs), 'list' (lists recent runs), or 'cancel' (terminates active run).",
                    Default = "status",
                    Enum = ["trigger", "status", "logs", "list", "cancel"]
                },
                ["workflow"] = new()
                {
                    Type = "string",
                    Title = "Workflow File Name",
                    Description = "Workflow file name to target (e.g. 'build-and-test.yml', 'docker-publish.yml', 'deploy-fleet.yml', 'quality-gates.yml', 'e2e-smoke.yml').",
                    Enum = ["build-and-test.yml", "docker-publish.yml", "deploy-fleet.yml", "quality-gates.yml", "e2e-smoke.yml"],
                    Default = "build-and-test.yml"
                },
                ["branch"] = new()
                {
                    Type = "string",
                    Title = "Git Branch",
                    Description = "Branch to run the workflow on (default: 'main').",
                    Default = "main"
                },
                ["run_id"] = new()
                {
                    Type = "integer",
                    Title = "Run ID",
                    Description = "Specific workflow run ID for 'status', 'logs', or 'cancel' actions."
                },
                ["tail"] = new()
                {
                    Type = "integer",
                    Title = "Tail Lines",
                    Description = "Number of log lines to return from the end for failed jobs (default: 100).",
                    Default = 100
                }
            },
            Required = ["action"]
        }
    };

    public static ToolDefinition CreateDbMigrateDefinition(string name = "kongor_db_migrate") => new()
    {
        Name = name,
        Description = "Entity Framework Core Migration & Safe Rollback Engine: wraps 'dotnet ef' CLI with safety rails. Features automated idempotent SQL preview, dependency pre-flight verification, PascalCase validation, and migration rollback.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = $"{name}Arguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["action"] = new()
                {
                    Type = "string",
                    Title = "Action",
                    Description = "Database migration operation: 'list' (shows applied and pending migrations), 'preview' (generates idempotent SQL script without applying), 'apply' (previews and applies pending migrations), 'rollback' (rolls back to target migration), or 'add' (scaffolds new migration).",
                    Default = "list",
                    Enum = ["list", "status", "preview", "apply", "rollback", "add"]
                },
                ["target"] = new()
                {
                    Type = "string",
                    Title = "Target Migration Name",
                    Description = "Target migration name for rollback (e.g. '20260922190000_ComprehensiveNormalizationAndItemTimeline' or '0' for clean slate)."
                },
                ["name"] = new()
                {
                    Type = "string",
                    Title = "New Migration Name",
                    Description = "PascalCase migration name to scaffold when action='add' (e.g. 'AddPlayerLoadoutPresets')."
                }
            },
            Required = ["action"]
        }
    };

    public static ToolDefinition CreateCanaryDeployDefinition(string name = "kongor_deploy_canary") => new()
    {
        Name = name,
        Description = "Agones GameServer Canary Deployment & Promotion: deploys a standalone canary GameServer matching fleet specs, verifies Agones SDK health, promotes via live JSON patch without dirtying git files, or rolls back safely.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = $"{name}Arguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["action"] = new()
                {
                    Type = "string",
                    Title = "Action",
                    Description = "Canary deployment operation: 'deploy' (launches isolated canary GameServer with new image), 'check' (verifies health and readiness), 'promote' (updates fleet to canary image via live JSON patch), 'rollback' (deletes canary), or 'status' (shows fleet + canary status).",
                    Default = "status",
                    Enum = ["deploy", "check", "promote", "rollback", "status"]
                },
                ["image"] = new()
                {
                    Type = "string",
                    Title = "Docker Image",
                    Description = "Full Docker image tag to deploy as canary (e.g. 'projectkongor/compel-dedicated:sha-1a2b3c4'). Required when action='deploy'."
                }
            },
            Required = ["action"]
        }
    };
}
