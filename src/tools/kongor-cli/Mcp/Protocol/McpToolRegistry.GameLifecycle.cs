// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using Kongor.Cli.Mcp.Handlers;

namespace Kongor.Cli.Mcp.Protocol;

public sealed partial class McpToolRegistry
{
    private CameraControlHandler _cameraControlHandler = null!;
    private FixtureInjectHandler _fixtureInjectHandler = null!;
    private SetStoreModeHandler _setStoreModeHandler = null!;

    public CameraControlHandler CameraControlHandler => _cameraControlHandler;
    public FixtureInjectHandler FixtureInjectHandler => _fixtureInjectHandler;
    public SetStoreModeHandler SetStoreModeHandler => _setStoreModeHandler;

    partial void InitializeGameLifecycleHandlers(TimeProvider timeProvider)
    {
        _cameraControlHandler = new CameraControlHandler(
            gameIpcBridge: _gameIpcBridge,
            clientManageHandler: _gameClientManageHandler,
            windowCapturer: _windowCapturer,
            timeProvider: timeProvider);

        _fixtureInjectHandler = new FixtureInjectHandler(
            _gameClientManageHandler,
            _gameServerManageHandler,
            _gameIpcBridge,
            timeProvider);

        _setStoreModeHandler = new SetStoreModeHandler(ipcBridge: _gameIpcBridge, timeProvider: timeProvider);
    }

    partial void RegisterGameLifecycleTools()
    {
        // Unified Game Lifecycle Management
        RegisterTool(CreateGameManageDefinition(), _gameManageHandler.HandleAsync, McpToolCategory.ServerManagement);

        // Player Game Client Lifecycle Management
        RegisterTool(CreateGameClientManageDefinition("kongor_game_client_manage"), _gameClientManageHandler.HandleAsync, McpToolCategory.ServerManagement);

        // Desktop Launcher Lifecycle Management
        RegisterTool(CreateWillowmakerManageDefinition("kongor_willowmaker_manage"), _willowmakerManageHandler.HandleAsync, McpToolCategory.ServerManagement);

        // IPC Client Command Dispatch
        RegisterTool(CreateGameCommandDefinition(), _gameCommandHandler.HandleAsync, McpToolCategory.GameCommand);

        // Direct Match & Practice Launch
        RegisterTool(CreateMatchLaunchDefinition("kongor_match_launch"), _matchLaunchHandler.HandleAsync, McpToolCategory.Simulation);

        // High-Value Autonomous Game Action
        RegisterTool(CreateGameActionDefinition(), _gameActionHandler.HandleAsync, McpToolCategory.GameCommand);

        // Vulkan 1.3 / DXVK Translation Governance
        RegisterTool(CreateVulkanManageDefinition("kongor_vulkan"), _vulkanManageHandler.HandleAsync, McpToolCategory.ServerManagement);

        // Deterministic One-Shot Practice Match Orchestrator
        RegisterTool(CreateClientPracticeDefinition("kongor_client_practice"), _clientPracticeHandler.HandleAsync, McpToolCategory.Simulation);
        RegisterAlias("kongor_practice", "kongor_client_practice");
        RegisterAlias("client_practice", "kongor_client_practice");
        RegisterAlias("practice_match", "kongor_client_practice");

        // Camera Trajectory, Positioning & Cinematic Control
        RegisterTool(CreateCameraControlDefinition("kongor_camera_control"), _cameraControlHandler.HandleAsync, McpToolCategory.GameCommand);

        // Atomic Game State Fixture Injector
        RegisterTool(CreateFixtureInjectDefinition("kongor_fixture_inject"), _fixtureInjectHandler.HandleAsync, McpToolCategory.GameCommand);
        RegisterAlias("fixture_inject", "kongor_fixture_inject");
        RegisterAlias("game_fixture", "kongor_fixture_inject");

        // Master Server Progression Mode Switcher
        RegisterTool(CreateSetStoreModeDefinition("kongor_set_store_mode"), _setStoreModeHandler.HandleAsync, McpToolCategory.ServerManagement);
        RegisterAlias("set_store_mode", "kongor_set_store_mode");
    }

    public static ToolDefinition CreateGameManageDefinition() => new()
    {
        Name = "kongor_game_manage",
        Description = "LEGACY router kept for backwards compatibility. Directs requests to the player game client (hon_x64.exe) or the desktop launcher (WILLOWMAKER.exe) based on the target or executable hint. New callers should use kongor_game_client_manage or kongor_willowmaker_manage directly.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = "kongor_game_manageArguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["action"] = new()
                {
                    Type = "string",
                    Title = "Action",
                    Description = "Game lifecycle action.",
                    Enum = ["launch", "stop", "status"]
                },
                ["target"] = new()
                {
                    Type = "string",
                    Title = "Target",
                    Description = "Subsystem target: 'client' (hon_x64.exe) or 'willowmaker'/'launcher' (desktop launcher).",
                    Enum = ["client", "willowmaker", "launcher"],
                    Default = "client"
                },
                ["mode"] = new()
                {
                    Type = "string",
                    Title = "Mode",
                    Description = "Startup mode.",
                    Enum = ["normal", "practice", "bot_match", "editor"],
                    Default = "normal"
                },
                ["connectAddress"] = new()
                {
                    Type = "string",
                    Title = "Connect Address",
                    Description = "Dedicated server address to connect to directly (e.g. '127.0.0.1:11235')."
                },
                ["resolution"] = new()
                {
                    Type = "string",
                    Title = "Resolution",
                    Description = "Display resolution (e.g. '1920x1080').",
                    Default = "1920x1080"
                },
                ["fullscreen"] = new()
                {
                    Type = "boolean",
                    Title = "Fullscreen",
                    Description = "Fullscreen mode.",
                    Default = false
                },
                ["masterServer"] = new()
                {
                    Type = "string",
                    Title = "Master Server",
                    Description = "Master server address.",
                    Default = "127.0.0.1:55555"
                },
                ["customClientPath"] = new()
                {
                    Type = "string",
                    Title = "Custom Client Path",
                    Description = "Custom path to hon_x64.exe or WILLOWMAKER.exe."
                }
            },
            Required = ["action"]
        }
    };

    public static ToolDefinition CreateGameClientManageDefinition(string name = "kongor_game_client_manage") => new()
    {
        Name = name,
        Description = "Controls the player game client binary (hon_x64.exe). Launches, cleanly terminates, queries status, or restarts the game client with complete VFS mod mounts (override;base;game;../shared;KONGOR/configuration;KONGOR/updates;KONGOR/extensions), DXVK Vulkan settings, low-latency audio, and Named Pipe IPC (-pipe willowmaker-ipc).",
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
                    Description = "Lifecycle action to perform: 'status', 'launch', 'stop', 'restart', 'pipe_status', 'pipes', 'logs', 'command', 'set_window_mode', 'errors', 'history'.",
                    Enum = ["launch", "stop", "status", "restart", "logs", "command", "set_window_mode", "pipe_status", "pipes", "errors", "history"]
                },
                ["command"] = new()
                {
                    Type = "string",
                    Title = "Command",
                    Description = "In-game console command or cvar to pipe to the player game client (e.g. 'connect 127.0.0.1:11235', 'SelectHero Hero_Pyromancer', 'reloadui')."
                },
                ["mode"] = new()
                {
                    Type = "string",
                    Title = "Mode",
                    Description = "Game client startup mode.",
                    Enum = ["normal", "practice", "bot_match", "connect", "p2p_host", "p2p_connect"],
                    Default = "normal"
                },
                ["connectAddress"] = new()
                {
                    Type = "string",
                    Title = "Connect Address",
                    Description = "Dedicated game server host:port to connect to directly (e.g. '127.0.0.1:11235')."
                },
                ["masterServer"] = new()
                {
                    Type = "string",
                    Title = "Master Server",
                    Description = "Master server address for login and matchmaking.",
                    Default = "127.0.0.1:55555"
                },
                ["resolution"] = new()
                {
                    Type = "string",
                    Title = "Resolution",
                    Description = "Display resolution (e.g. '1920x1080', '2560x1440', '1280x720').",
                    Default = "1920x1080"
                },
                ["fullscreen"] = new()
                {
                    Type = "boolean",
                    Title = "Fullscreen",
                    Description = "Whether to launch in fullscreen mode.",
                    Default = false
                },
                ["maxFps"] = new()
                {
                    Type = "integer",
                    Title = "Max FPS",
                    Description = "Maximum engine render framerate cap.",
                    Default = 1000
                },
                ["customClientPath"] = new()
                {
                    Type = "string",
                    Title = "Custom Client Path",
                    Description = "Explicit path to hon_x64.exe executable."
                },
                ["lines"] = new()
                {
                    Type = "integer",
                    Title = "Lines",
                    Description = "Number of recent log lines to retrieve (default: 50).",
                    Default = 50
                },
                ["logType"] = new()
                {
                    Type = "string",
                    Title = "Log Type",
                    Description = "Target client log stream: 'console', 'dxvk', or 'all'.",
                    Enum = ["console", "dxvk", "all"],
                    Default = "console"
                },
                ["level"] = new()
                {
                    Type = "string",
                    Title = "Level",
                    Description = "Log level severity filter: 'all', 'error', 'warn', 'info'.",
                    Enum = ["all", "error", "warn", "info"],
                    Default = "all"
                },
                ["window_mode"] = new()
                {
                    Type = "string",
                    Title = "Window Mode",
                    Description = "Window presentation mode: 'headless' (positioned offscreen at 25000,25000 for zero monitor disruption), 'foreground' (restored to visible desktop), or 'minimized'. Default: 'headless'.",
                    Enum = ["headless", "foreground", "minimized"],
                    Default = "headless"
                },
                ["steal_focus"] = new()
                {
                    Type = "boolean",
                    Title = "Steal Focus",
                    Description = "Whether to forcefully bring window to foreground and steal keyboard focus (default: false).",
                    Default = false
                }
            },
            Required = ["action"]
        }
    };

    public static ToolDefinition CreateWillowmakerManageDefinition(string name = "kongor_willowmaker_manage") => new()
    {
        Name = name,
        Description = "Controls the desktop launcher application (WILLOWMAKER.exe). Programmatically launches, cleanly terminates, and queries process and window state of the WILLOWMAKER launcher.",
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
                    Description = "Lifecycle action to perform.",
                    Enum = ["launch", "stop", "status"]
                },
                ["customPath"] = new()
                {
                    Type = "string",
                    Title = "Custom Path",
                    Description = "Explicit custom path to WILLOWMAKER.exe."
                },
                ["workingDirectory"] = new()
                {
                    Type = "string",
                    Title = "Working Directory",
                    Description = "Custom working directory for the launcher process."
                }
            },
            Required = ["action"]
        }
    };

    public static ToolDefinition CreateGameCommandDefinition() => new()
    {
        Name = "kongor_game_command",
        Description = "Send remote console commands, deterministic time adjustments (set_timescale, step_frames, pause, resume), or cvar updates to the running HoN client, dedicated server, or both simultaneously via Named Pipe IPC (\\\\.\\pipe\\willowmaker-ipc) and receive execution output.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = "kongor_game_commandArguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["action"] = new()
                {
                    Type = "string",
                    Title = "Action",
                    Description = "Optional deterministic action: 'set_timescale' (adjust host_timeScale 0.01 to 100.0), 'step_frames' (advance simulation by N frames), 'pause' (pause game), 'resume' (unpause game), 'errors' (query recent errors). Defaults to executing raw 'command'.",
                    Enum = ["set_timescale", "step_frames", "pause", "resume", "errors"]
                },
                ["target"] = new()
                {
                    Type = "string",
                    Title = "Target",
                    Description = "Target subsystem: 'client' (player client hon_x64.exe), 'server' (dedicated server hon_x64.exe -dedicated, or 'server:11235'), or 'both' (multiplexed across client and dedicated server worker).",
                    Enum = ["client", "server", "both"],
                    Default = "client"
                },
                ["channel"] = new()
                {
                    Type = "string",
                    Title = "Channel",
                    Description = "Execution channel for dedicated server commands: 'auto' (prefer stdin with pipe fallback), 'stdin' (direct stdin stream write), 'ipc' / 'pipe' (dedicated Named Pipe \\\\.\\pipe\\hon-server-{port}), or 'both'.",
                    Enum = ["auto", "stdin", "ipc", "pipe", "both"],
                    Default = "auto"
                },
                ["command"] = new()
                {
                    Type = "string",
                    Title = "Command",
                    Description = "The K2 console command or cvar to execute (e.g. 'Set vid_maxFPS 1000', 'AddFakePlayer 2', 'StartGame practice'). Required if action is omitted."
                },
                ["scale"] = new()
                {
                    Type = "number",
                    Title = "Timescale",
                    Description = "Simulation timescale multiplier (0.01 to 100.0) when action is 'set_timescale'. Defaults to 1.0.",
                    Default = 1.0
                },
                ["frames"] = new()
                {
                    Type = "integer",
                    Title = "Frames",
                    Description = "Number of frames/ticks to advance (1 to 1000) when action is 'step_frames'. Defaults to 1.",
                    Default = 1
                },
                ["port"] = new()
                {
                    Type = "integer",
                    Title = "Port",
                    Description = "Target UDP listen port of dedicated server worker (e.g. 11235). Required when target is 'server'. If omitted, defaults to client IPC (\\\\.\\pipe\\willowmaker-ipc)."
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

    public static ToolDefinition CreateGameClientCommandDefinition(string name = "kongor_game_client_command") => new()
    {
        Name = name,
        Description = "Send remote console commands or cvar adjustments specifically to the running player game client (hon_x64.exe) via Named Pipe IPC (\\\\.\\pipe\\willowmaker-ipc) and receive execution output.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = $"{name}Arguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["command"] = new()
                {
                    Type = "string",
                    Title = "Command",
                    Description = "The K2 console command or cvar to execute on the player game client (e.g. 'connect 127.0.0.1:11235', 'SelectHero Hero_Pyromancer', 'reloadui', 'Set vid_maxFPS 1000')."
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

    public static ToolDefinition CreateMatchLaunchDefinition(string name = "kongor_match_launch") => new()
    {
        Name = name,
        Description = "Programmatically launches the Project Kongor game client (hon_x64.exe) directly into a practice match or bot match on Caldavar (or custom map) with pre-selected hero, bot count, developer cheats, and optional headless mode without requiring manual UI menu navigation. Supports IPC match monitoring and rapid lifecycle termination.",
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
                    Description = "Lifecycle action to perform.",
                    Enum = ["launch", "stop", "status", "restart"],
                    Default = "launch"
                },
                ["map"] = new()
                {
                    Type = "string",
                    Title = "Map",
                    Description = "Map name to load (e.g. 'caldavar', 'midwars', 'watchtower', 'dev_heroes').",
                    Enum = ["caldavar", "midwars", "watchtower", "dev_heroes", "grimms_crossing"],
                    Default = "caldavar"
                },
                ["hero"] = new()
                {
                    Type = "string",
                    Title = "Hero",
                    Description = "Default hero entity name to pre-select upon loading (e.g. 'Hero_Pyromancer', 'Hero_WitchSlayer', 'Hero_FlintBeastwood', 'Hero_Chronos', 'Hero_Empath').",
                    Default = "Hero_Pyromancer"
                },
                ["mode"] = new()
                {
                    Type = "string",
                    Title = "Mode",
                    Description = "Startup game match mode.",
                    Enum = ["practice", "bot_match", "normal", "tournament", "dev"],
                    Default = "practice"
                },
                ["bots"] = new()
                {
                    Type = "integer",
                    Title = "Bots",
                    Description = "Number of bots to populate in match. When > 0, sets svr_botMatch true and svr_numBots.",
                    Default = 0
                },
                ["cheats"] = new()
                {
                    Type = "boolean",
                    Title = "Cheats",
                    Description = "Enable in-game developer cheats (+Set dev true +Set cheats true).",
                    Default = true
                },
                ["headless"] = new()
                {
                    Type = "boolean",
                    Title = "Headless",
                    Description = "Run in headless mode (+Set vid_headless true +Set sound_disable true) for automated testing without rendering a desktop window.",
                    Default = false
                },
                ["resolution"] = new()
                {
                    Type = "string",
                    Title = "Resolution",
                    Description = "Display resolution (e.g. '1920x1080', '1280x720').",
                    Default = "1920x1080"
                },
                ["fullscreen"] = new()
                {
                    Type = "boolean",
                    Title = "Fullscreen",
                    Description = "Whether to run in fullscreen mode.",
                    Default = false
                },
                ["maxFps"] = new()
                {
                    Type = "integer",
                    Title = "Max FPS",
                    Description = "Maximum engine render framerate cap.",
                    Default = 1000
                },
                ["masterServer"] = new()
                {
                    Type = "string",
                    Title = "Master Server",
                    Description = "Master server address for login and matchmaking.",
                    Default = "127.0.0.1:55555"
                },
                ["autoSelectHero"] = new()
                {
                    Type = "boolean",
                    Title = "Auto Select Hero",
                    Description = "Whether to dispatch hero selection command via IPC once match finishes loading.",
                    Default = true
                },
                ["rapidTerminateAfterLaunch"] = new()
                {
                    Type = "boolean",
                    Title = "Rapid Terminate After Launch",
                    Description = "Guardrail option: launch, verify process and arguments, and terminate immediately within 2 seconds.",
                    Default = false
                },
                ["customClientPath"] = new()
                {
                    Type = "string",
                    Title = "Custom Client Path",
                    Description = "Explicit path to hon_x64.exe executable."
                },
                ["workingDirectory"] = new()
                {
                    Type = "string",
                    Title = "Working Directory",
                    Description = "Custom working directory for the game client process."
                },
                ["tickrate"] = new()
                {
                    Type = "integer",
                    Title = "Tickrate",
                    Description = "Simulation tickrate in Hz (canonical default: 20 for 50ms K2 simulation step; 30/60 supported). Clamped between 20 and 60.",
                    Default = 20
                },
                ["adopt_existing"] = new()
                {
                    Type = "boolean",
                    Title = "Adopt Existing Client",
                    Description = "Explicit opt-in permitting reuse of an un-owned personal hon_x64 process when launching a practice match (default: false).",
                    Default = false
                },
                ["reposition_existing"] = new()
                {
                    Type = "boolean",
                    Title = "Reposition Existing Window",
                    Description = "When adopting an existing client in headless mode, whether to reposition its window offscreen (default: false to preserve user window placement).",
                    Default = false
                },
                ["all"] = new()
                {
                    Type = "boolean",
                    Title = "All Processes",
                    Description = "When action='stop', explicitly sweep all non-dedicated hon_x64 processes rather than only MCP-tracked match processes (default: false).",
                    Default = false
                }
            },
            Required = ["action"]
        }
    };

    public static ToolDefinition CreateGameActionDefinition(string name = "kongor_game_action") => new()
    {
        Name = name,
        Description = "Dispatch deterministic engine actions, unit commands, cheats, and game state mutations (spawn_unit, kill_unit, give_gold, level_up, teleport, cast_ability, order, execute, buy_item, sell_item, move_to, attack_target, use_item) to either the local player client or dedicated game servers.",
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
                    Description = "Deterministic game action to execute.",
                    Enum = ["spawn_unit", "kill_unit", "give_gold", "level_up", "teleport", "cast_ability", "order", "execute", "buy_item", "sell_item", "move_to", "attack_target", "use_item"]
                },
                ["destination"] = new()
                {
                    Type = "string",
                    Title = "Destination",
                    Description = "Execution destination node: 'client' (local player game client, default) or 'server' / 'server:{port}' (dedicated game server worker).",
                    Enum = ["client", "server"]
                },
                ["target"] = new()
                {
                    Type = "string",
                    Title = "Target",
                    Description = "Target entity ID, coordinate target, or execution destination ('client', 'server', 'server:{port}').",
                    Enum = ["client", "server", "both", "auto"]
                },
                ["entity"] = new()
                {
                    Type = "string",
                    Title = "Entity Target",
                    Description = "Target in-game unit or entity name/ID for actions such as kill_unit (e.g. 'Hero_Pyromancer', 'Creep_LegionMelee', 'selected')."
                },
                ["channel"] = new()
                {
                    Type = "string",
                    Title = "Execution Channel",
                    Description = "Execution channel when targeting dedicated servers: 'auto' (default: stdin with pipe fallback), 'stdin', or 'pipe'.",
                    Enum = ["auto", "stdin", "pipe"]
                },
                ["unit"] = new()
                {
                    Type = "string",
                    Title = "Unit Entity Name",
                    Description = "Unit entity definition or hero name (e.g. 'Hero_Pyromancer', 'Creep_LegionMelee')."
                },
                ["item"] = new()
                {
                    Type = "string",
                    Title = "Item Name",
                    Description = "Item entity name (e.g. 'Item_PostHaste', 'Item_LoggersHatchet') for buy_item."
                },
                ["slot"] = new()
                {
                    Type = "integer",
                    Title = "Ability Slot",
                    Description = "Ability slot index (0 to 3) for cast_ability actions."
                },
                ["amount"] = new()
                {
                    Type = "integer",
                    Title = "Amount",
                    Description = "Numeric amount (e.g., gold quantity or levels to grant)."
                },
                ["x"] = new()
                {
                    Type = "number",
                    Title = "X Coordinate",
                    Description = "World X coordinate."
                },
                ["y"] = new()
                {
                    Type = "number",
                    Title = "Y Coordinate",
                    Description = "World Y coordinate."
                },
                ["z"] = new()
                {
                    Type = "number",
                    Title = "Z Coordinate",
                    Description = "World Z coordinate (height)."
                },
                ["team"] = new()
                {
                    Type = "integer",
                    Title = "Team",
                    Description = "Team identifier (1 = Legion, 2 = Hellbourne)."
                },
                ["port"] = new()
                {
                    Type = "integer",
                    Title = "Server Port",
                    Description = "Optional dedicated server port to dispatch action to."
                }
            },
            Required = ["action"]
        }
    };

    public static ToolDefinition CreateVulkanManageDefinition(string name = "kongor_vulkan") => new()
    {
        Name = name,
        Description = "Inspect, verify, and upgrade player game client and dedicated game servers to Vulkan 1.3 / DXVK translation, 1000 FPS unclamp, DotLocal DLL redirection, and purge DXVK state caches.",
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
                    Description = "Action to perform: 'status', 'upgrade', or 'purge_cache'.",
                    Enum = ["status", "upgrade", "purge_cache"],
                    Default = "status"
                },
                ["target"] = new()
                {
                    Type = "string",
                    Title = "Target Architecture",
                    Description = "Target architecture to manage: 'client' (player game client) or 'server' (dedicated server).",
                    Enum = ["client", "server"],
                    Default = "client"
                },
                ["preset"] = new()
                {
                    Type = "string",
                    Title = "Ray Tracing / DXVK Preset",
                    Description = "Target graphics/rendering preset: 'competitive' (default), 'balanced', or 'ultra'.",
                    Enum = ["competitive", "balanced", "ultra"],
                    Default = "competitive"
                },
                ["directory"] = new()
                {
                    Type = "string",
                    Title = "Directory",
                    Description = "Explicit path to game client or dedicated server directory (optional)."
                }
            },
            Required = ["action"]
        }
    };

    public static ToolDefinition CreateClientPracticeDefinition(string name = "kongor_client_practice") => new()
    {
        Name = name,
        Description = "Deterministic one-shot practice match orchestrator for the K2 Game Client (hon_x64.exe). Transitions the client from any state directly into an active practice match (GamePhase 3) with a specified hero, map, and bots. Defensively cleanses lingering matches via Disconnect, supports dual authentication ('offline' cvar bypass vs 'cluster' live MasterServer auto-boot), flexible window placement ('offscreen' headless vs 'foreground'), deterministic hero spawning/selection, live hero entity assertions, and transient ~350ms BitBlt visual proof.",
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
                    Description = "Practice operation: 'launch' (default, starts practice match and spawns hero), 'start_match' (transitions lobby/hero pick to active match), 'level_up' (levels up active hero), 'give_items' (grants items to active hero), or 'status'.",
                    Enum = ["launch", "start_match", "level_up", "give_items", "status"],
                    Default = "launch"
                },
                ["hero"] = new()
                {
                    Type = "string",
                    Title = "Hero",
                    Description = "Hero entity identifier to spawn and select (e.g. 'Hero_Pyromancer', 'Hero_Bubbles', 'Hero_Valkyrie'). Aliases: 'heroId', 'heroName'. Default: 'Hero_Pyromancer'.",
                    Default = "Hero_Pyromancer"
                },
                ["map"] = new()
                {
                    Type = "string",
                    Title = "Map",
                    Description = "Map name to load: 'caldavar', 'midwars', 'tutorial', 'practice'. Aliases: 'mapName'. Default: 'caldavar'.",
                    Enum = ["caldavar", "midwars", "tutorial", "practice", "grimms_crossing"],
                    Default = "caldavar"
                },
                ["level"] = new()
                {
                    Type = "integer",
                    Title = "Hero Level",
                    Description = "Target hero level (1 to 25) for 'launch' or 'level_up' actions. Aliases: 'heroLevel'. Default: 1.",
                    Default = 1
                },
                ["items"] = new()
                {
                    Type = "array",
                    Title = "Inventory Items",
                    Description = "Optional list of item entity names to grant to the hero (e.g. ['Item_PortalKey', 'Item_PostHaste']) for 'launch' or 'give_items'.",
                    Items = new ToolPropertySchema
                    {
                        Type = "string",
                        Title = "Item Entity Name",
                        Description = "K2 item entity identifier (e.g. 'Item_PortalKey')."
                    }
                },
                ["bots"] = new()
                {
                    Type = "integer",
                    Title = "Bot Count",
                    Description = "Number of opponent fake player bots to spawn on Hellbourne (0 to 9, default: 0). Aliases: 'botCount', 'fakePlayers'.",
                    Default = 0
                },
                ["cheats"] = new()
                {
                    Type = "boolean",
                    Title = "Cheats Enabled",
                    Description = "Enable in-game sandbox / cheat commands (default: true).",
                    Default = true
                },
                ["tickrate"] = new()
                {
                    Type = "integer",
                    Title = "Tickrate / Host FPS",
                    Description = "Target simulation tickrate in Hz (20 to 60, canonical default: 20 for 50ms K2 simulation step). Aliases: 'tickRate', 'fps'.",
                    Default = 20
                },
                ["authMode"] = new()
                {
                    Type = "string",
                    Title = "Authentication Mode",
                    Description = "Authentication mode: 'offline' (fast standalone bypass setting local cvars without backend dependencies) or 'cluster' (probes and starts MasterServer/Aspire cluster if needed and authenticates genuinely). Default: 'offline'.",
                    Enum = ["offline", "cluster"],
                    Default = "offline"
                },
                ["ensureCluster"] = new()
                {
                    Type = "boolean",
                    Title = "Ensure Cluster",
                    Description = "When authMode is 'cluster', automatically launches the Aspire cluster / MasterServer via LifecycleService if not responding and waits for health probe (default: true).",
                    Default = true
                },
                ["windowMode"] = new()
                {
                    Type = "string",
                    Title = "Window Mode",
                    Description = "Game client window placement: 'offscreen' (runs headless at -32000,-32000 with 0 desktop focus disruption; visual proof uses transient BitBlt capture) or 'foreground' (normal visible desktop window for interactive watching). Default: 'offscreen'.",
                    Enum = ["offscreen", "foreground"],
                    Default = "offscreen"
                },
                ["account"] = new()
                {
                    Type = "string",
                    Title = "Account",
                    Description = "Account username for authentication (default: 'KongorPlayer'). Aliases: 'username'.",
                    Default = "KongorPlayer"
                },
                ["password"] = new()
                {
                    Type = "string",
                    Title = "Password",
                    Description = "Password for cluster authentication (default: 'password'). Aliases: 'pass'.",
                    Default = "password"
                },
                ["autoBoot"] = new()
                {
                    Type = "boolean",
                    Title = "Auto Boot",
                    Description = "If true, automatically boots hon_x64.exe in the specified windowMode if not currently running (default: true).",
                    Default = true
                },
                ["cleanse"] = new()
                {
                    Type = "boolean",
                    Title = "Defensive Cleanse",
                    Description = "If true, forces 'Disconnect' to cleanly exit any existing match before starting fresh. When omitted, defaults to warm in-place hero morphing if already in active match on the same map.",
                    Default = false
                },
                ["reuseIfActive"] = new()
                {
                    Type = "boolean",
                    Title = "Warm Practice Reuse",
                    Description = "If true (default when cleanse is not explicitly true), reuses an active in-world practice match on the same map via in-place SpawnHero/Morph (< 3-5s) and falls back to cold StartGame on map mismatch, replay/spectator mode, dead hero, or bots > 0.",
                    Default = true
                },
                ["waitForPhase"] = new()
                {
                    Type = "integer",
                    Title = "Wait For GamePhase",
                    Description = "Target GamePhase to await before proceeding to hero selection (default: 3 = Active Match).",
                    Default = 3
                },
                ["captureProof"] = new()
                {
                    Type = "boolean",
                    Title = "Capture Visual Proof",
                    Description = "If true, captures a transient BitBlt screenshot frame and attaches it to the response (default: true).",
                    Default = true
                },
                ["timeoutMs"] = new()
                {
                    Type = "integer",
                    Title = "Timeout (Milliseconds)",
                    Description = "Timeout in milliseconds for phase synchronization and entity assertion (default: 30000, min: 1000, max: 120000).",
                    Default = 30000
                },
                ["clusterTimeoutMs"] = new()
                {
                    Type = "integer",
                    Title = "Cluster Timeout (Milliseconds)",
                    Description = "Timeout in milliseconds to wait for MasterServer health probe when ensureCluster is enabled (default: 15000).",
                    Default = 15000
                },
                ["dryRun"] = new()
                {
                    Type = "boolean",
                    Title = "Dry Run",
                    Description = "If true, generates the command pipeline, cvars, and verification criteria without executing (default: false).",
                    Default = false
                }
            },
            Required = []
        }
    };

    public static ToolDefinition CreateCameraControlDefinition(string name = "kongor_camera_control") => new()
    {
        Name = name,
        Description = "Programmatic camera positioning, free-cam tracking, zoom, pitch, and FOV interpolation for automated cinematic recording and replay testing via K2 console cvars.",
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
                    Description = "Camera control operation: set_position, get_position, set_fov, set_pitch, follow_entity, interpolate_path, capture_view.",
                    Enum = ["set_position", "get_position", "set_fov", "set_pitch", "follow_entity", "interpolate_path", "capture_view"],
                    Default = "get_position"
                },
                ["x"] = new() { Type = "number", Title = "X Coordinate", Description = "World X coordinate." },
                ["y"] = new() { Type = "number", Title = "Y Coordinate", Description = "World Y coordinate." },
                ["z"] = new() { Type = "number", Title = "Z Coordinate", Description = "World Z coordinate." },
                ["fov"] = new() { Type = "number", Title = "Field of View", Description = "Camera FOV degrees (30.0 - 130.0)." },
                ["pitch"] = new() { Type = "number", Title = "Camera Pitch", Description = "Camera pitch angle degrees (-89.0 - 89.0)." },
                ["entity_id"] = new() { Type = "integer", Title = "Entity ID", Description = "Entity or hero slot index to follow." },
                ["duration_ms"] = new() { Type = "integer", Title = "Duration Milliseconds", Description = "Interpolation time in ms." },
                ["waypoints"] = new()
                {
                    Type = "array",
                    Title = "Waypoints",
                    Description = "Array of 3D waypoint coordinate objects for spline interpolation.",
                    Items = new ToolPropertySchema
                    {
                        Type = "object",
                        Title = "Waypoint",
                        Description = "3D camera waypoint { x, y, z }.",
                        Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
                        {
                            ["x"] = new() { Type = "number", Title = "X", Description = "World X coordinate." },
                            ["y"] = new() { Type = "number", Title = "Y", Description = "World Y coordinate." },
                            ["z"] = new() { Type = "number", Title = "Z", Description = "World Z coordinate." }
                        }
                    }
                }
            },
            Required = ["action"]
        }
    };

    public static ToolDefinition CreateFixtureInjectDefinition(string name = "kongor_fixture_inject") => new()
    {
        Name = name,
        Description = "Atomic Game State Fixture Injector: teleports hero, assigns level, gold, items, skills, spawns creeps, and sets HP/mana in a single atomic transaction for instant scenario staging.",
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
                    Description = "Fixture action: 'inject' (apply custom parameters), 'preset' (apply named scenario), or 'reset' (restore fresh hero state).",
                    Default = "inject",
                    Enum = ["inject", "preset", "reset"]
                },
                ["hero"] = new()
                {
                    Type = "string",
                    Title = "Hero Entity",
                    Description = "Hero entity identifier to morph into (e.g. 'Hero_Pyromancer', 'Hero_Devourer', 'Hero_Chronos')."
                },
                ["level"] = new()
                {
                    Type = "integer",
                    Title = "Hero Level",
                    Description = "Target hero level (1 to 25).",
                    Default = 1
                },
                ["gold"] = new()
                {
                    Type = "integer",
                    Title = "Gold",
                    Description = "Gold amount to assign to player.",
                    Default = 0
                },
                ["teleport"] = new()
                {
                    Type = "object",
                    Title = "Teleport Coordinates",
                    Description = "Map coordinates { x, y, z } to teleport hero to instantly.",
                    Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
                    {
                        ["x"] = new() { Type = "number", Title = "X", Description = "World X coordinate." },
                        ["y"] = new() { Type = "number", Title = "Y", Description = "World Y coordinate." },
                        ["z"] = new() { Type = "number", Title = "Z", Description = "World Z coordinate." }
                    }
                },
                ["items"] = new()
                {
                    Type = "array",
                    Title = "Inventory Items",
                    Description = "List of item entity names to give to hero (e.g. ['Item_PortalKey', 'Item_PostHaste']).",
                    Items = new ToolPropertySchema
                    {
                        Type = "string",
                        Title = "Item Entity Name",
                        Description = "K2 item entity identifier."
                    }
                },
                ["health_percent"] = new()
                {
                    Type = "number",
                    Title = "Health Percentage",
                    Description = "Target hero HP percentage (0.0 to 1.0).",
                    Default = 1.0
                },
                ["mana_percent"] = new()
                {
                    Type = "number",
                    Title = "Mana Percentage",
                    Description = "Target hero mana percentage (0.0 to 1.0).",
                    Default = 1.0
                },
                ["preset"] = new()
                {
                    Type = "string",
                    Title = "Scenario Preset",
                    Description = "Named scenario preset: 'mid_clash', 'roshan_contest', 'fountain_dive', or 'rune_fight'.",
                    Enum = ["mid_clash", "roshan_contest", "fountain_dive", "rune_fight"]
                },
                ["spawn_creeps"] = new()
                {
                    Type = "array",
                    Title = "Spawn Creep Waves",
                    Description = "List of creep specifications to spawn: [{ type: 'Creep_LegionMelee', count: 3 }].",
                    Items = new ToolPropertySchema
                    {
                        Type = "object",
                        Title = "Creep Wave Spec",
                        Description = "Creep entity type and count to spawn.",
                        Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
                        {
                            ["type"] = new() { Type = "string", Title = "Creep Entity Type", Description = "Creep entity name (e.g. 'Creep_LegionMelee')." },
                            ["count"] = new() { Type = "integer", Title = "Count", Description = "Number of creeps to spawn." }
                        }
                    }
                },
                ["target"] = new()
                {
                    Type = "string",
                    Title = "Target Plane",
                    Description = "Target execution plane: 'both' (client + server), 'client', or 'server'.",
                    Default = "both",
                    Enum = ["both", "client", "server"]
                },
                ["port"] = new()
                {
                    Type = "integer",
                    Title = "Server Port",
                    Description = "Target dedicated server port (e.g. 11235) when target includes server."
                }
            },
            Required = ["action"]
        }
    };

    public static ToolDefinition CreateSetStoreModeDefinition(string name = "kongor_set_store_mode") => new()
    {
        Name = name,
        Description = "Master Server Progression Mode Switcher: dynamically toggles store configuration between Sandbox Mode (UnlockAllItems=true, all 1,400+ cosmetics unlocked free for testing) and Progression Mode (UnlockAllItems=false, standard economy). Persists to appsettings.json and notifies live hon_x64.exe to reload its store cache.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = $"{name}Arguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["mode"] = new()
                {
                    Type = "string",
                    Title = "Store Mode",
                    Description = "Store mode: 'sandbox' (all 1,400+ items unlocked), 'progression' (earned progression with coin balance checks), 'toggle' (flips mode), or 'status' (inspects mode). Default is 'sandbox'.",
                    Default = "sandbox",
                    Enum = ["sandbox", "progression", "toggle", "status"]
                },
                ["refresh_client"] = new()
                {
                    Type = "boolean",
                    Title = "Refresh Client Cache",
                    Description = "If true and client IPC is connected, invalidates and refreshes the client-side HoN_Store and HeroDefinitionsCache in memory. Default is true.",
                    Default = true
                },
                ["persist"] = new()
                {
                    Type = "boolean",
                    Title = "Persist to Configuration",
                    Description = "If true, persists the setting into master-server appsettings.json. Default is true.",
                    Default = true
                }
            },
            Required = ["mode"]
        }
    };
}
