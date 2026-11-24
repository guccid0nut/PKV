// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Services;

namespace Kongor.Cli.Mcp.Protocol;

public sealed partial class McpToolRegistry
{
    private UiObserverHandler _uiObserverHandler = null!;
    private AuditStatsHandler _auditStatsHandler = null!;
    private ClientEvalLuaHandler _clientEvalLuaHandler = null!;
    private ClientCapturePreviewHandler _clientCapturePreviewHandler = null!;

    public UiObserverHandler UiObserverHandler => _uiObserverHandler;
    public AuditStatsHandler AuditStatsHandler => _auditStatsHandler;
    public ClientEvalLuaHandler ClientEvalLuaHandler => _clientEvalLuaHandler;
    public ClientCapturePreviewHandler ClientCapturePreviewHandler => _clientCapturePreviewHandler;

    partial void InitializeLuaUiHandlers(TimeProvider timeProvider)
    {
        _uiObserverHandler = new UiObserverHandler(
            uiInspectService: _k2UiInspectService,
            timeProvider: timeProvider);

        var auditorService = new ClientStatsAuditorService(
            ipcBridge: _gameIpcBridge,
            uiInspectService: _k2UiInspectService,
            timeProvider: timeProvider);
        _auditStatsHandler ??= new AuditStatsHandler(auditorService, timeProvider, mediator: _mediator);

        _clientEvalLuaHandler = new ClientEvalLuaHandler(timeProvider: timeProvider, ipcBridge: _gameIpcBridge);
        _clientCapturePreviewHandler = new ClientCapturePreviewHandler(windowCapturer: _windowCapturer, ipcBridge: _gameIpcBridge, timeProvider: timeProvider);
    }

    partial void RegisterLuaUiTools()
    {
        // Dedicated Lua Scripting & Engine Control
        RegisterTool(CreateGameLuaDefinition("kongor_game_lua"), _gameLuaHandler.HandleAsync, McpToolCategory.GameCommand);

        // Capture Window & Visual UI
        RegisterTool(CreateCaptureWindowDefinition(), _captureWindowHandler.HandleAsync, McpToolCategory.UiControl);
        RegisterTool(CreateTransientCaptureDefinition("kongor_transient_capture"), _captureWindowHandler.HandleTransientAsync, McpToolCategory.UiControl);
        RegisterAlias("kongor_capture_transient", "kongor_transient_capture");
        RegisterTool(CreateUiRenderViewDefinition(), _uiRenderViewHandler.HandleAsync, McpToolCategory.UiControl);
        RegisterTool(CreateUiControlDefinition("kongor_ui_control"), _uiControlHandler.HandleAsync, McpToolCategory.UiControl);

        // K2 UI Introspection
        RegisterTool(CreateK2UiInspectDefinition("kongor_k2_ui_inspect"), _k2UiInspectHandler.HandleAsync, McpToolCategory.ReadOnly);

        // Mid-Game Snapshot & Replay Tick Manager
        RegisterTool(CreateGameSnapshotDefinition("kongor_game_snapshot"), _gameSnapshotHandler.HandleAsync, McpToolCategory.ReadOnly);

        // Direct Lua UI Workflow Macros
        RegisterTool(CreateUiFlowDefinition("kongor_ui_flow"), _uiFlowHandler.HandleAsync, McpToolCategory.UiControl);

        // Perceptual UI Visual Diff
        RegisterTool(CreateUiVisualDiffDefinition("kongor_ui_visual_diff"), _uiVisualDiffHandler.HandleAsync, McpToolCategory.UiControl);

        // Canonical K2 Route Navigation & Closed-Loop Control
        RegisterTool(CreateClientNavigateDefinition("kongor_client_navigate"), _clientNavigateHandler.HandleAsync, McpToolCategory.UiControl);
        RegisterAlias("kongor_navigate", "kongor_client_navigate");
        RegisterAlias("kongor_nav", "kongor_client_navigate");

        // State & UI Readiness Poller
        RegisterTool(CreateWaitForDefinition("kongor_wait_for"), _waitForHandler.HandleAsync, McpToolCategory.UiControl);
        RegisterAlias("wait_for", "kongor_wait_for");
        RegisterAlias("kongor_await", "kongor_wait_for");

        // Declarative UI DOM Assertion
        RegisterTool(CreateAssertUiDefinition("kongor_assert_ui"), _assertUiHandler.HandleAsync, McpToolCategory.UiControl);
        RegisterAlias("assert_ui", "kongor_assert_ui");
        RegisterAlias("kongor_ui_assert", "kongor_assert_ui");

        // Reactive K2 XML/Lua UI Element Observer & State Awaiter
        RegisterTool(CreateUiObserverDefinition("kongor_ui_observer"), _uiObserverHandler.HandleAsync, McpToolCategory.UiControl);

        // Autonomous Client Stats & Match History Auditor
        RegisterTool(CreateAuditStatsDefinition("kongor_audit_stats"), _auditStatsHandler.HandleAsync, McpToolCategory.ReadOnly);
        RegisterAlias("audit_stats", "kongor_audit_stats");
        RegisterAlias("audit_ui", "kongor_audit_stats");

        // In-Game Client Lua Hot-Reload & Remote Execution
        RegisterTool(CreateClientEvalLuaDefinition("kongor_client_eval_lua"), _clientEvalLuaHandler.HandleAsync, McpToolCategory.GameCommand);
        RegisterAlias("client_eval_lua", "kongor_client_eval_lua");

        // Automated Preview Stage Viewport Capture
        RegisterTool(CreateClientCapturePreviewDefinition("kongor_client_capture_preview"), _clientCapturePreviewHandler.HandleAsync, McpToolCategory.UiControl);
        RegisterAlias("client_capture_preview", "kongor_client_capture_preview");
    }

    public static ToolDefinition CreateGameLuaDefinition(string name = "kongor_game_lua") => new()
    {
        Name = name,
        Description = "Execute Lua scripts, simulation control actions (apply_fixture, set_timescale, step_frames, pause, resume), UI automation (inspect_ui, navigate, protect_queues, open_store), high-level macros (practice_match, hide_login), or direct TAO dispatches in the running K2 game engine client, dedicated server, or both simultaneously via direct Named Pipe IPC, stdin stream, or in-game console actuation.",
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
                    Description = "High-level macro action or simulation operation: 'code' (default arbitrary Lua evaluation), 'apply_fixture' (in-engine active hero/gold/level/item injection), 'set_timescale' (simulation timescale 0.01 to 100.0), 'step_frames' (advance simulation by N frames), 'pause', 'resume', 'inspect_ui' (defensive DOM crawl), 'navigate' (safe routing), 'protect_queues', 'tao_dispatch', 'hide_login', 'open_store', 'practice_match', 'apply_snapshot', 'collect_telemetry', 'errors', or 'history'.",
                    Enum = ["code", "apply_fixture", "set_timescale", "step_frames", "pause", "resume", "inspect_ui", "navigate", "protect_queues", "tao_dispatch", "hide_login", "open_store", "practice_match", "apply_snapshot", "collect_telemetry", "errors", "history"],
                    Default = "code"
                },
                ["accountName"] = new()
                {
                    Type = "string",
                    Title = "Account Name",
                    Description = "Account name for 'hide_login' action (e.g. 'KongorPlayer', 'NexusPlayer'). Defaults to 'KongorPlayer'.",
                    Default = "KongorPlayer"
                },
                ["options"] = new()
                {
                    Type = "object",
                    Title = "Options",
                    Description = "Configuration options for macro actions such as 'practice_match' (e.g. { map: 'caldavar', hero: 'Hero_Pyromancer', mode: 'practice', cheats: true, team: 1, slot: 0 }).",
                    Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
                    {
                        ["map"] = new() { Type = "string", Title = "Map", Description = "Map identifier (e.g. 'caldavar')." },
                        ["hero"] = new() { Type = "string", Title = "Hero", Description = "Hero entity identifier (e.g. 'Hero_Pyromancer')." },
                        ["mode"] = new() { Type = "string", Title = "Mode", Description = "Match mode (e.g. 'practice')." },
                        ["cheats"] = new() { Type = "boolean", Title = "Cheats", Description = "Enable cheat commands." },
                        ["team"] = new() { Type = "integer", Title = "Team", Description = "Team index (1 = Legion, 2 = Hellbourne)." },
                        ["slot"] = new() { Type = "integer", Title = "Slot", Description = "Player slot index." }
                    }
                },
                ["map"] = new()
                {
                    Type = "string",
                    Title = "Map",
                    Description = "Map name for 'practice_match' action (e.g. 'caldavar', 'midwars'). Defaults to 'caldavar'.",
                    Enum = ["caldavar", "midwars", "grimms_crossing", "watchtower", "prophets"],
                    Default = "caldavar"
                },
                ["hero"] = new()
                {
                    Type = "string",
                    Title = "Hero",
                    Description = "Hero entity name for 'practice_match' action (e.g. 'Hero_Pyromancer', 'Hero_WitchSlayer'). Defaults to 'Hero_Pyromancer'.",
                    Default = "Hero_Pyromancer"
                },
                ["snapshot"] = new()
                {
                    Type = "object",
                    Title = "Snapshot",
                    Description = "Mid-game state snapshot for 'apply_snapshot' action containing level, gold, items, and/or coordinates pos (e.g. { level: 6, gold: 1500, items: ['Item_BootsOfSpeed'], pos: { x: 100.5, y: 200.0, z: 50.0 } }).",
                    Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
                    {
                        ["level"] = new() { Type = "integer", Title = "Level", Description = "Hero level (1-25)." },
                        ["gold"] = new() { Type = "integer", Title = "Gold", Description = "Gold amount." },
                        ["items"] = new()
                        {
                            Type = "array",
                            Title = "Items",
                            Description = "Item entity names.",
                            Items = new ToolPropertySchema { Type = "string", Title = "Item", Description = "Item entity name." }
                        },
                        ["pos"] = new()
                        {
                            Type = "object",
                            Title = "Position",
                            Description = "3D coordinates { x, y, z }.",
                            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
                            {
                                ["x"] = new() { Type = "number", Title = "X", Description = "World X coordinate." },
                                ["y"] = new() { Type = "number", Title = "Y", Description = "World Y coordinate." },
                                ["z"] = new() { Type = "number", Title = "Z", Description = "World Z coordinate." }
                            }
                        }
                    }
                },
                ["code"] = new()
                {
                    Type = "string",
                    Title = "Code",
                    Description = "Arbitrary Lua script expression to evaluate (e.g. \"println('hello')\", \"Windows.main:GetWidget('play_button'):DoEvent('onclick')\"). Automatically wrapped in 'Script ' if omitted."
                },
                ["trigger"] = new()
                {
                    Type = "string",
                    Title = "Trigger",
                    Description = "Name of the K2 Lua/UI trigger to fire (e.g. 'main_login', 'JoinMatchmaking')."
                },
                ["invokeWidget"] = new()
                {
                    Type = "string",
                    Title = "Invoke Widget",
                    Description = "Name of the K2 GUI widget to invoke an action on (e.g. 'play_button', 'chat_input')."
                },
                ["event"] = new()
                {
                    Type = "string",
                    Title = "Event",
                    Description = "Event name when invokeWidget is specified (e.g. 'onclick', 'onshow', 'onhide', 'onenter').",
                    Enum = ["onclick", "onshow", "onhide", "onenter", "onevent", "onevent0", "onevent1"],
                    Default = "onclick"
                },
                ["target"] = new()
                {
                    Type = "string",
                    Title = "Target",
                    Description = "Target subsystem: 'client' (player game client), 'server' (dedicated server worker, or 'server:11235'), or 'both' (multiplexed across client and dedicated server).",
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
                ["port"] = new()
                {
                    Type = "integer",
                    Title = "Port",
                    Description = "Target UDP port when target is 'server' (e.g. 11235)."
                },
                ["tab"] = new()
                {
                    Type = "string",
                    Title = "Store Tab / Page",
                    Description = "Target main store tab for 'open_store': 'specials', 'featured', 'altavatars', 'accountvanity', 'gamevanity', 'others', 'bundles'. Aliases: 'storeTab', 'page', 'category'.",
                    Enum = ["specials", "featured", "altavatars", "accountvanity", "gamevanity", "others", "bundles"]
                },
                ["subpage"] = new()
                {
                    Type = "string",
                    Title = "Store Submenu / Subpage",
                    Description = "Target store submenu/subpage for 'open_store' (e.g. 'taunts', 'announcers', 'couriers', 'wards', 'tpeffects', 'upgrades', 'accounticons', 'namecolors', 'symbols'). Aliases: 'subTab', 'submenu', 'subCategory'.",
                    Enum = ["taunts", "announcers", "couriers", "wards", "tpeffects", "upgrades", "accounticons", "namecolors", "symbols"]
                },
                ["item"] = new()
                {
                    Type = "string",
                    Title = "Store Item / Avatar / Hero",
                    Description = "Direct target store item, avatar, or hero identifier for 'open_store' (e.g. 'Hero_Bubbles.Alt16', '1307', 'Hero_Bubbles'). Aliases: 'itemId', 'productId', 'product', 'avatar'."
                },
                ["timeoutMs"] = new()
                {
                    Type = "integer",
                    Title = "Timeout Ms",
                    Description = "Timeout in milliseconds for script execution.",
                    Default = 3000
                },
                ["limit"] = new()
                {
                    Type = "integer",
                    Title = "Limit",
                    Description = "Maximum number of error records to return when action is 'errors' or 'history'.",
                    Default = 10
                },
                ["autoLaunch"] = new()
                {
                    Type = "boolean",
                    Title = "Auto Launch",
                    Description = "Whether to automatically launch client or server if not running.",
                    Default = true
                },
                ["verifyWidget"] = new()
                {
                    Type = "string",
                    Title = "Verify Widget Visibility",
                    Description = "Optional widget name to verify visibility in engine memory after execution with micro-retry loop (e.g. 'sysbar_quickStart', 'store_button')."
                },
                ["verifyState"] = new()
                {
                    Type = "string",
                    Title = "Verify State Expression",
                    Description = "Optional boolean Lua expression to verify evaluates to true after execution (e.g. 'Nexus ~= nil', 'GetHostClient():GetHero() ~= nil')."
                },
                ["captureProof"] = new()
                {
                    Type = "boolean",
                    Title = "Capture Visual Proof",
                    Description = "If true, captures offscreen visual proof artifact of the resulting frame with zero window exposure.",
                    Default = false
                }
            },
            Required = []
        }
    };

    public static ToolDefinition CreateCaptureWindowDefinition() => new()
    {
        Name = "kongor_capture_window",
        Description = "Captures a live visual screenshot (Base64 PNG and saved disk artifact) from a running application window (e.g., hon_x64 game client, WILLOWMAKER launcher, NEXUS Control Panel) using hardware Win32 P/Invoke capture with DWM extended frame bounds and DPI awareness. Defaults to Tier 0 Direct GPU DMA Zero-Copy Vulkan Offscreen Capture (VK_EXT_external_memory_host, 0ms dwell, 0% window exposure) for hon_x64 parked offscreen at (-32000, -32000).",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = "kongor_capture_windowArguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["target"] = new()
                {
                    Type = "string",
                    Title = "Target",
                    Description = "Target window identifier. Can be 'hon_x64', 'client', 'game_client', 'willowmaker', 'nexus.controlpanel.gui', an explicit HWND (hex '0x000A1234'), process ID ('pid:1234'), or 'foreground'. Defaults to 'hon_x64'.",
                    Enum = ["hon_x64", "client", "game_client", "willowmaker", "nexus.controlpanel.gui", "foreground"],
                    Default = "hon_x64"
                },
                ["capture_method"] = new()
                {
                    Type = "string",
                    Title = "Capture Method",
                    Description = "Capture modality. 'auto' defaults to Tier 0 Direct GPU DMA Zero-Copy Vulkan offscreen shared memory (0ms dwell, 0% exposure) when capturing hon_x64, cascading gracefully to PrintWindow, Window DC, and Desktop DC/transient. 'vulkan_offscreen' forces Tier 0 direct GPU DMA capture. 'transient' performs momentary exposure fallback.",
                    Enum = ["auto", "vulkan_offscreen", "print_window", "window_dc", "desktop_dc", "transient", "in_engine", "backbuffer"],
                    Default = "auto"
                },
                ["save_artifact"] = new()
                {
                    Type = "boolean",
                    Title = "Save Artifact",
                    Description = "Whether to write the captured image as a PNG artifact file on disk under artifacts/captures/.",
                    Default = true
                },
                ["output_path"] = new()
                {
                    Type = "string",
                    Title = "Output Path",
                    Description = "Alias for custom_output_path: explicit file path where the PNG artifact should be saved."
                },
                ["save_path"] = new()
                {
                    Type = "string",
                    Title = "Save Path",
                    Description = "Alias for custom_output_path: explicit file path where the PNG artifact should be saved."
                },
                ["file_path"] = new()
                {
                    Type = "string",
                    Title = "File Path",
                    Description = "Alias for custom_output_path: explicit file path where the PNG artifact should be saved."
                },
                ["custom_output_path"] = new()
                {
                    Type = "string",
                    Title = "Custom Output Path",
                    Description = "Optional explicit file path where the PNG artifact should be saved."
                },
                ["detect_anomalies"] = new()
                {
                    Type = "boolean",
                    Title = "Detect Anomalies",
                    Description = "Whether to execute automated real-time visual anomaly detection (missing textures, Utah Teapot model fallbacks, blank white quad placeholders, black screen stalls, inverted viewports) on the captured frame.",
                    Default = true
                },
                ["console_logs"] = new()
                {
                    Type = "string",
                    Title = "Console Logs",
                    Description = "Optional recent in-game console log snippet used to corroborate visual anomaly diagnosis (e.g. null.mdf teapot fallbacks)."
                },
                ["steal_focus"] = new()
                {
                    Type = "boolean",
                    Title = "Steal Focus",
                    Description = "Whether to restore and bring the target window to the foreground before capturing (default: false for background/headless capture without desktop disruption).",
                    Default = false
                },
                ["allow_window_exposure"] = new()
                {
                    Type = "boolean",
                    Title = "Allow Window Exposure",
                    Description = "Explicit opt-in synonym for steal_focus permitting momentary onscreen window exposure when zero-exposure capture is unavailable (default: false).",
                    Default = false
                }
            },
            Required = []
        }
    };

    public static ToolDefinition CreateTransientCaptureDefinition(string toolName = "kongor_transient_capture") => new()
    {
        Name = toolName,
        Description = "Captures an instantaneous screenshot of an offscreen/headless window (e.g. hon_x64 running at -32000,-32000). When the Vulkan swapchain capture layer is active, frames are acquired directly from shared memory with 0ms dwell time and ZERO desktop window exposure. If the Vulkan layer is inactive, automatically falls back to momentary onscreen presentation and restoration.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = $"{toolName}Arguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["target"] = new()
                {
                    Type = "string",
                    Title = "Target",
                    Description = "Target window identifier. Can be a process name ('hon_x64', 'willowmaker'), a window title query ('*Heroes of Newerth*'), an explicit HWND (hex '0x000A1234' or integer), or 'foreground'.",
                    Enum = ["hon_x64", "willowmaker", "foreground"],
                    Default = "hon_x64"
                },
                ["dwell_ms"] = new()
                {
                    Type = "integer",
                    Title = "Dwell Ms",
                    Description = "Dwell time in milliseconds to keep the window onscreen while the GPU frame presents before capturing (default: 350ms).",
                    Default = 350
                },
                ["width"] = new()
                {
                    Type = "integer",
                    Title = "Width",
                    Description = "Width in pixels for onscreen capture (default: 1280).",
                    Default = 1280
                },
                ["height"] = new()
                {
                    Type = "integer",
                    Title = "Height",
                    Description = "Height in pixels for onscreen capture (default: 720).",
                    Default = 720
                },
                ["offscreen_x"] = new()
                {
                    Type = "integer",
                    Title = "Offscreen X",
                    Description = "Offscreen X coordinate to restore the window to immediately after capture (default: -32000).",
                    Default = -32000
                },
                ["offscreen_y"] = new()
                {
                    Type = "integer",
                    Title = "Offscreen Y",
                    Description = "Offscreen Y coordinate to restore the window to immediately after capture (default: -32000).",
                    Default = -32000
                },
                ["save_artifact"] = new()
                {
                    Type = "boolean",
                    Title = "Save Artifact",
                    Description = "Whether to write the captured image as a PNG artifact file on disk under artifacts/captures/.",
                    Default = true
                },
                ["output_path"] = new()
                {
                    Type = "string",
                    Title = "Output Path",
                    Description = "Alias for custom_output_path: explicit file path where the PNG artifact should be saved."
                },
                ["save_path"] = new()
                {
                    Type = "string",
                    Title = "Save Path",
                    Description = "Alias for custom_output_path: explicit file path where the PNG artifact should be saved."
                },
                ["file_path"] = new()
                {
                    Type = "string",
                    Title = "File Path",
                    Description = "Alias for custom_output_path: explicit file path where the PNG artifact should be saved."
                },
                ["custom_output_path"] = new()
                {
                    Type = "string",
                    Title = "Custom Output Path",
                    Description = "Optional explicit file path where the PNG artifact should be saved."
                },
                ["detect_anomalies"] = new()
                {
                    Type = "boolean",
                    Title = "Detect Anomalies",
                    Description = "Whether to execute automated real-time visual anomaly detection (missing textures, teapot fallbacks, blank white quads, black screen stalls) on the captured frame.",
                    Default = true
                },
                ["console_logs"] = new()
                {
                    Type = "string",
                    Title = "Console Logs",
                    Description = "Optional recent in-game console log snippet used to corroborate visual anomaly diagnosis."
                },
                ["steal_focus"] = new()
                {
                    Type = "boolean",
                    Title = "Steal Focus",
                    Description = "Whether to permit momentary onscreen window exposure when zero-exposure Vulkan capture is unavailable (default: false).",
                    Default = false
                },
                ["allow_window_exposure"] = new()
                {
                    Type = "boolean",
                    Title = "Allow Window Exposure",
                    Description = "Explicit opt-in permitting momentary onscreen presentation onto the desktop when zero-exposure capture is unavailable (default: false).",
                    Default = false
                }
            },
            Required = []
        }
    };

    public static ToolDefinition CreateUiRenderViewDefinition() => new()
    {
        Name = "kongor_ui_render_view",
        Description = "Renders an arbitrary Avalonia XAML view/UserControl offscreen to a pixel-perfect PNG snapshot and Base64 string using Avalonia Headless and SkiaSharp. Allows AI coding agents to visually inspect UI styling, layout, theme responsiveness, and control rendering without requiring interactive desktop manipulation.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = "kongor_ui_render_viewArguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["view_name"] = new()
                {
                    Type = "string",
                    Title = "View Name",
                    Description = "Name of the Avalonia view or UserControl to render (e.g. 'ModManagerView', 'ReplayStudioView', 'SecretsConfigView', 'DiagnosticsView', 'DashboardView', 'GameServersView', 'UsersView', 'LiveLogsView', 'ReplayConverterView', 'MainView').",
                    Enum = ["ModManagerView", "ReplayStudioView", "SecretsConfigView", "DiagnosticsView", "DashboardView", "GameServersView", "UsersView", "LiveLogsView", "ReplayConverterView", "MainView"],
                    Examples = ["ModManagerView", "ReplayStudioView", "SecretsConfigView", "DiagnosticsView"]
                },
                ["width"] = new()
                {
                    Type = "integer",
                    Title = "Width",
                    Description = "Viewport render width in logical pixels.",
                    Default = 1280
                },
                ["height"] = new()
                {
                    Type = "integer",
                    Title = "Height",
                    Description = "Viewport render height in logical pixels.",
                    Default = 720
                },
                ["theme"] = new()
                {
                    Type = "string",
                    Title = "Theme",
                    Description = "Avalonia ThemeVariant to apply during rendering.",
                    Enum = ["dark", "light", "default"],
                    Default = "dark"
                },
                ["mock_data"] = new()
                {
                    Type = "boolean",
                    Title = "Mock Data",
                    Description = "Whether to inject comprehensive mock/sample ViewModel data to render populated tables, badges, cards, and status indicators.",
                    Default = true
                },
                ["save_artifact"] = new()
                {
                    Type = "boolean",
                    Title = "Save Artifact",
                    Description = "Whether to save the rendered PNG image to disk under artifacts/ui_renders/.",
                    Default = true
                },
                ["output_path"] = new()
                {
                    Type = "string",
                    Title = "Output Path",
                    Description = "Alias for custom_output_path: explicit file path where the PNG artifact should be saved."
                },
                ["save_path"] = new()
                {
                    Type = "string",
                    Title = "Save Path",
                    Description = "Alias for custom_output_path: explicit file path where the PNG artifact should be saved."
                },
                ["file_path"] = new()
                {
                    Type = "string",
                    Title = "File Path",
                    Description = "Alias for custom_output_path: explicit file path where the PNG artifact should be saved."
                },
                ["custom_output_path"] = new()
                {
                    Type = "string",
                    Title = "Custom Output Path",
                    Description = "Optional explicit file path where the PNG artifact should be saved."
                }
            },
            Required = ["view_name"]
        }
    };

    public static ToolDefinition CreateUiControlDefinition(string name = "kongor_ui_control") => new()
    {
        Name = name,
        Description = "Universal UI automation and interactive control for Project Kongor windows (hon_x64, WILLOWMAKER, NEXUS Control Panel, active foreground). Inspects UI element trees (buttons, textboxes, comboboxes, checkboxes), performs element or coordinate clicks with 100-150ms actuation delays, invokes UIA patterns, types text characters, and dispatches virtual key presses (Enter, Tab, Escape, F8, Space).",
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
                    Description = "UI automation action to execute.",
                    Enum = ["inspect", "click", "click_widget", "trigger_widget", "invoke", "drag", "type", "key", "dispatch_event", "dispatch", "console", "dismiss_console"]
                },
                ["target"] = new()
                {
                    Type = "string",
                    Title = "Target",
                    Description = "Target window identifier. Process name ('hon_x64', 'willowmaker', 'nexus.controlpanel.gui'), window title query ('*Heroes of Newerth*'), HWND hex ('0x001B0E24'), or 'foreground'.",
                    Enum = ["hon_x64", "willowmaker", "nexus.controlpanel.gui", "foreground"],
                    Default = "hon_x64"
                },
                ["widget"] = new()
                {
                    Type = "string",
                    Title = "Widget",
                    Description = "K2 UI widget name to target for engine-internal DOM event dispatching or parent-visibility guarded click/type/drag (e.g., 'sysbar_quickStart', 'btn_play', 'sysbar_options_button')."
                },
                ["event"] = new()
                {
                    Type = "string",
                    Title = "Event",
                    Description = "Event name to trigger on the K2 widget (e.g., 'click', 'onevent', 'onevent0', 'onevent1'). Default: 'click'.",
                    Enum = ["click", "onevent", "onevent0", "onevent1", "onclick", "onshow", "onhide"],
                    Default = "click"
                },
                ["interface"] = new()
                {
                    Type = "string",
                    Title = "Interface",
                    Description = "Optional K2 interface name containing the widget (e.g., 'main', 'game', 'scoreboard'). Defaults to active interface or 'main'."
                },
                ["element_name"] = new()
                {
                    Type = "string",
                    Title = "Element Name",
                    Description = "Name or AutomationId of the UI control element to target for click or invoke."
                },
                ["x"] = new()
                {
                    Type = "integer",
                    Title = "X Coordinate",
                    Description = "Window-relative X coordinate to click or start drag."
                },
                ["y"] = new()
                {
                    Type = "integer",
                    Title = "Y Coordinate",
                    Description = "Window-relative Y coordinate to click or start drag."
                },
                ["x2"] = new()
                {
                    Type = "integer",
                    Title = "X2 Coordinate",
                    Description = "Window-relative destination X coordinate for drag."
                },
                ["y2"] = new()
                {
                    Type = "integer",
                    Title = "Y2 Coordinate",
                    Description = "Window-relative destination Y coordinate for drag."
                },
                ["to_widget"] = new()
                {
                    Type = "string",
                    Title = "Destination Widget",
                    Description = "Optional destination K2 widget name for drag operation."
                },
                ["duration_ms"] = new()
                {
                    Type = "integer",
                    Title = "Duration (ms)",
                    Description = "Duration of the drag motion in milliseconds (default: 300).",
                    Default = 300
                },
                ["highlight"] = new()
                {
                    Type = "boolean",
                    Title = "Highlight Widget",
                    Description = "If true, flashes an on-screen visual highlight overlay over the widget before actuating.",
                    Default = false
                },
                ["use_invoke_pattern"] = new()
                {
                    Type = "boolean",
                    Title = "Use Invoke Pattern",
                    Description = "If true, attempts direct UIA InvokePattern instead of hardware mouse event simulation.",
                    Default = false
                },
                ["text"] = new()
                {
                    Type = "string",
                    Title = "Text",
                    Description = "Text characters to type into the focused target window."
                },
                ["key"] = new()
                {
                    Type = "string",
                    Title = "Key",
                    Description = "Virtual key name to send (e.g., 'Enter', 'Tab', 'Escape', 'F8', 'Space', 'Backspace', 'Up', 'Down', 'Left', 'Right').",
                    Enum = ["Enter", "Tab", "Escape", "F8", "Space", "Backspace", "Up", "Down", "Left", "Right"]
                },
                ["verify_widget"] = new()
                {
                    Type = "string",
                    Title = "Verify Widget",
                    Description = "Optional widget name to verify post-actuation state against using wait_for (e.g. 'game_options')."
                },
                ["verify_visible"] = new()
                {
                    Type = "boolean",
                    Title = "Verify Visible",
                    Description = "Expected visibility state for verify_widget (default: true).",
                    Default = true
                },
                ["wait_for"] = new()
                {
                    Type = "object",
                    Title = "Wait For Post-Condition",
                    Description = "Optional declarative post-condition payload to verify after actuation via kongor_wait_for.",
                    Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
                    {
                        ["condition"] = new() { Type = "string", Title = "Condition", Description = "Condition type (e.g. 'widget_visible', 'game_phase', 'lua_expr')." },
                        ["widget"] = new() { Type = "string", Title = "Widget", Description = "Target widget name." },
                        ["interface"] = new() { Type = "string", Title = "Interface", Description = "Target UI interface name." },
                        ["expectedPhase"] = new() { Type = "integer", Title = "Expected Phase", Description = "Target GamePhase integer." },
                        ["timeoutMs"] = new() { Type = "integer", Title = "Timeout (ms)", Description = "Maximum wait time in milliseconds." }
                    }
                }
            },
            Required = ["action"]
        }
    };

    public static ToolDefinition CreateK2UiInspectDefinition(string name = "kongor_ui_inspect") => new()
    {
        Name = name,
        Description = "Inspects the K2 engine UI widget hierarchy (panels, buttons, images, labels, modelpanels) in live running game or offline static interface/package XML files. Expands templates and instances, inspects bounds/visibility, verifies active texture paths against VFS mounts, and flags warnings for missing textures or solid white ($white) fallbacks.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = $"{name}Arguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["path"] = new()
                {
                    Type = "string",
                    Title = "Path",
                    Description = "Optional file path or VFS path to .interface or .package file to inspect (e.g. '/ui/fe2/main_v2.interface', 'games/client/override/ui/fe2/main_v2.interface'). Defaults to main menu interface in static mode."
                },
                ["widget_name"] = new()
                {
                    Type = "string",
                    Title = "Widget Name",
                    Description = "Optional specific widget name or substring to inspect (e.g. 'main_logo_header', 'login_hero_viewport', 'background_main')."
                },
                ["depth"] = new()
                {
                    Type = "integer",
                    Title = "Depth",
                    Description = "Maximum traversal depth in the widget hierarchy (default: 10).",
                    Default = 10
                },
                ["mode"] = new()
                {
                    Type = "string",
                    Title = "Mode",
                    Description = "Introspection mode: 'auto' (live query if client is running, otherwise static XML parser), 'live' (Named Pipe IPC only), 'static' (offline XML parsing), or 'layout' (5-kind 2D layout defect analysis).",
                    Enum = ["auto", "live", "static", "layout"],
                    Default = "auto"
                },
                ["contactSheetPath"] = new()
                {
                    Type = "string",
                    Title = "Contact Sheet Output Path",
                    Description = "Optional file path to save a 1920x1080 2x3 storyboard contact-sheet PNG with numbered red (#e05555) bounding boxes around detected layout defects."
                }
            },
            Required = []
        }
    };

    public static ToolDefinition CreateGameSnapshotDefinition(string name = "kongor_game_snapshot") => new()
    {
        Name = name,
        Description = "Mid-game state checkpointing, persistence, restoration, and replay tick manager. Enables developers and AI agents to save, restore, list, and delete game state snapshots (hero level, gold, items, position, cooldowns, game time) and perform frame-accurate replay tick seeking (20 ticks/sec). Enforces Phase 3 (Active Match) lifecycle guardrails to prevent native C++ engine assertions.",
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
                    Description = "Action to perform: 'save' (capture current live state or store inline snapshot), 'restore' (apply stored snapshot mutations to live hero in Phase 3), 'apply_fixture' (in-engine active hero/gold/level/inventory injection), 'world_state' (query structured in-engine world state), 'seek' (frame-accurate replay playback seeking), 'list' (list all stored snapshots), 'delete' (remove snapshot from memory), or 'reconcile' (reconcile client state against server authoritative state).",
                    Enum = ["save", "restore", "apply_fixture", "world_state", "seek", "list", "delete", "reconcile"]
                },
                ["target"] = new()
                {
                    Type = "string",
                    Title = "Target Node",
                    Description = "Target execution node: 'client' (default), 'server', 'both' (apply mutations or reconcile across both), or 'reconciled'.",
                    Enum = ["client", "server", "both", "reconciled"],
                    Default = "client"
                },
                ["port"] = new()
                {
                    Type = "integer",
                    Title = "Dedicated Server Port",
                    Description = "Dedicated server UDP listen port for server operations (default: 11235)."
                },
                ["toleranceEpsilon"] = new()
                {
                    Type = "number",
                    Title = "Tolerance Epsilon",
                    Description = "Allowed 3D spatial coordinate drift in units before flagging a position desync (default: 50.0).",
                    Default = 50.0
                },
                ["id"] = new()
                {
                    Type = "string",
                    Title = "Snapshot ID",
                    Description = "Unique identifier for the snapshot. If omitted during 'save', auto-generates a unique timestamped ID."
                },
                ["snapshot"] = new()
                {
                    Type = "object",
                    Title = "Snapshot Payload",
                    Description = "Optional inline snapshot object containing hero state mutations (gold, level, items, pos, cooldowns).",
                    Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
                    {
                        ["gold"] = new() { Type = "integer", Title = "Gold", Description = "Gold amount." },
                        ["level"] = new() { Type = "integer", Title = "Level", Description = "Hero level (1-25)." },
                        ["items"] = new()
                        {
                            Type = "array",
                            Title = "Items",
                            Description = "Item entity names.",
                            Items = new ToolPropertySchema { Type = "string", Title = "Item", Description = "Item entity name." }
                        },
                        ["pos"] = new()
                        {
                            Type = "object",
                            Title = "Position",
                            Description = "3D coordinates { x, y, z }.",
                            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
                            {
                                ["x"] = new() { Type = "number", Title = "X", Description = "World X coordinate." },
                                ["y"] = new() { Type = "number", Title = "Y", Description = "World Y coordinate." },
                                ["z"] = new() { Type = "number", Title = "Z", Description = "World Z coordinate." }
                            }
                        },
                        ["resetCooldowns"] = new() { Type = "boolean", Title = "Reset Cooldowns", Description = "Reset ability and item cooldowns." }
                    }
                },
                ["fixture"] = new()
                {
                    Type = "object",
                    Title = "Fixture Payload",
                    Description = "Active fixture payload for 'apply_fixture' action (e.g. { hero: 'Hero_Pyromancer', level: 16, gold: 5000, items: ['Item_PortalKey', 'Item_GrimoireOfPower'], pos: { x: 0, y: 0 }, resetCooldowns: true, refreshVitality: true }).",
                    Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
                    {
                        ["hero"] = new() { Type = "string", Title = "Hero", Description = "Hero entity name." },
                        ["level"] = new() { Type = "integer", Title = "Level", Description = "Hero level (1-25)." },
                        ["gold"] = new() { Type = "integer", Title = "Gold", Description = "Gold amount." },
                        ["items"] = new()
                        {
                            Type = "array",
                            Title = "Items",
                            Description = "Item entity names.",
                            Items = new ToolPropertySchema { Type = "string", Title = "Item", Description = "Item entity name." }
                        },
                        ["pos"] = new()
                        {
                            Type = "object",
                            Title = "Position",
                            Description = "Coordinates { x, y, z }.",
                            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
                            {
                                ["x"] = new() { Type = "number", Title = "X", Description = "World X coordinate." },
                                ["y"] = new() { Type = "number", Title = "Y", Description = "World Y coordinate." },
                                ["z"] = new() { Type = "number", Title = "Z", Description = "World Z coordinate." }
                            }
                        },
                        ["resetCooldowns"] = new() { Type = "boolean", Title = "Reset Cooldowns", Description = "Reset ability cooldowns." },
                        ["refreshVitality"] = new() { Type = "boolean", Title = "Refresh Vitality", Description = "Restore full HP and mana." }
                    }
                },
                ["tick"] = new()
                {
                    Type = "integer",
                    Title = "Simulation Tick",
                    Description = "Explicit engine simulation tick frame for 'seek' (20 ticks/sec, 1 tick = 50ms)."
                },
                ["timeSeconds"] = new()
                {
                    Type = "number",
                    Title = "Time in Seconds",
                    Description = "Target playback timestamp in seconds for 'seek' (e.g. 15.5)."
                },
                ["time"] = new()
                {
                    Type = "number",
                    Title = "Time Alias",
                    Description = "Alias for timeSeconds."
                },
                ["timeMs"] = new()
                {
                    Type = "integer",
                    Title = "Time in Milliseconds",
                    Description = "Target playback timestamp in milliseconds for 'seek' (e.g. 2500)."
                },
                ["timeFormatted"] = new()
                {
                    Type = "string",
                    Title = "Formatted Timestamp",
                    Description = "Target playback timestamp as 'MM:SS' or 'MM:SS.fff' for 'seek' (e.g. '01:30')."
                },
                ["timestamp"] = new()
                {
                    Type = "string",
                    Title = "Timestamp Alias",
                    Description = "Alias for timeFormatted."
                }
            },
            Required = ["action"]
        }
    };

    public static ToolDefinition CreateUiFlowDefinition(string name = "kongor_ui_flow") => new()
    {
        Name = name,
        Description = "High-level direct Lua UI automation workflow macros for ProjectKongor. Bypasses fragile mouse clicks and coordinate navigation by executing atomic, sandboxed in-engine UI transitions: 'bypass_login' (programmatic authentication & login dialog dismissal), 'open_lobby' (custom/matchmaking/practice lobby activation), 'open_options' (direct options tab activation for graphics, sound, controls), 'open_store' (store 2.0 navigation via cg_store2_), 'open_watch' (Watch/HoN Live tab navigation), 'open_learnatorium' (Learnatorium/Compendium panel navigation), and 'dismiss_popups' (modal alert dismissal). Conforms strictly to K2 UI rules: uses UIManager (never Windows), 0-arg DoEvent(), and no string arguments.",
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
                    Description = "UI flow macro action to perform: 'bypass_login', 'open_lobby', 'open_options', 'open_store', 'open_watch'/'open_live', 'open_learnatorium'/'open_compendium', 'open_stats'/'open_profile', 'dismiss_popups', 'trigger_widget'/'click_widget' (in-process OpCode 0x0009 native UI event), 'errors', or 'history'.",
                    Enum = ["bypass_login", "open_lobby", "open_options", "open_store", "open_watch", "open_live", "open_learnatorium", "open_compendium", "open_stats", "open_profile", "dismiss_popups", "trigger_widget", "click_widget", "errors", "history"]
                },
                ["targetWidget"] = new()
                {
                    Type = "string",
                    Title = "Target Widget",
                    Description = "Target widget name for 'trigger_widget' / 'click_widget' (e.g. 'sysbar_quickStart', 'store_button', 'btnPlayNow')."
                },
                ["eventType"] = new()
                {
                    Type = "string",
                    Title = "Event Type",
                    Description = "Event type to fire on target widget: 'click' (default), 'mouseldown', 'mouselup', 'show', 'hide', etc.",
                    Enum = ["click", "mouseldown", "mouselup", "show", "hide", "onevent", "onevent0", "onevent1"],
                    Default = "click"
                },
                ["eventIndex"] = new()
                {
                    Type = "integer",
                    Title = "Event Index",
                    Description = "Event index for DoEventN (e.g. 0 for onevent0, 1 for onevent1). If null/omitted, triggers primary DoEvent()."
                },
                ["verifyWidget"] = new()
                {
                    Type = "string",
                    Title = "Verify Widget Visibility",
                    Description = "Optional widget name to verify visibility in engine memory after execution with micro-retry loop (e.g. 'sysbar_quickStart', 'store_button')."
                },
                ["captureProof"] = new()
                {
                    Type = "boolean",
                    Title = "Capture Visual Proof",
                    Description = "If true, captures offscreen visual proof artifact of the resulting frame with zero window exposure.",
                    Default = false
                },
                ["accountName"] = new()
                {
                    Type = "string",
                    Title = "Account Name",
                    Description = "Account nickname to programmatically authenticate with for 'bypass_login' (default: 'KongorPlayer'). Aliases: 'account', 'player'.",
                    Default = "KongorPlayer"
                },
                ["nickname"] = new()
                {
                    Type = "string",
                    Title = "Account Nickname",
                    Description = "Target player nickname for 'open_stats' / 'open_profile' (default: current logged in account). Aliases: 'account', 'player'."
                },
                ["lobbyType"] = new()
                {
                    Type = "string",
                    Title = "Lobby Type",
                    Description = "Target lobby type for 'open_lobby': 'custom' (default), 'public', 'practice', 'matchmaking', or 'lobby'.",
                    Enum = ["custom", "public", "practice", "matchmaking", "lobby"],
                    Default = "custom"
                },
                ["optionsTab"] = new()
                {
                    Type = "string",
                    Title = "Options Tab",
                    Description = "Settings category tab to activate for 'open_options': 'graphics' (video), 'sound' (audio), 'controls' (hotkeys), 'gameplay', 'chat' (interface), or 'mods'.",
                    Enum = ["graphics", "video", "sound", "audio", "controls", "hotkeys", "gameplay", "chat", "mods"],
                    Default = "graphics"
                },
                ["targetInterface"] = new()
                {
                    Type = "string",
                    Title = "Target Interface",
                    Description = "Target interface to scan for 'dismiss_popups' or 'trigger_widget' (default: 'main').",
                    Enum = ["all", "main", "game", "scoreboard", "loading"],
                    Default = "all"
                },
                ["tab"] = new()
                {
                    Type = "string",
                    Title = "Store Tab / Page",
                    Description = "Target main store tab for 'open_store': 'specials', 'featured', 'altavatars' ('heroes', 'avatars'), 'accountvanity' ('account'), 'gamevanity' ('vanity'), 'others' ('services'), or 'bundles'. Aliases: 'storeTab', 'page', 'category'.",
                    Enum = ["specials", "featured", "altavatars", "heroes", "avatars", "accountvanity", "account", "gamevanity", "vanity", "others", "services", "bundles"]
                },
                ["subpage"] = new()
                {
                    Type = "string",
                    Title = "Store Submenu / Subpage",
                    Description = "Target store submenu/subpage for 'open_store'. Under Game Vanity: 'taunts', 'announcers', 'couriers', 'wards', 'tpeffects', 'upgrades'. Under Account Vanity: 'accounticons', 'namecolors', 'symbols'. Aliases: 'subTab', 'submenu', 'subCategory'.",
                    Enum = ["taunts", "announcers", "couriers", "wards", "tpeffects", "upgrades", "accounticons", "namecolors", "symbols"]
                },
                ["item"] = new()
                {
                    Type = "string",
                    Title = "Store Item / Avatar / Hero",
                    Description = "Direct target store item, avatar, or hero identifier to jump directly to detail/pedestal page (e.g. 'Hero_Bubbles.Alt16', '1307', 'Hero_Bubbles'). Aliases: 'itemId', 'productId', 'product', 'hero', 'avatar'."
                },
                ["timeoutMs"] = new()
                {
                    Type = "integer",
                    Title = "Timeout (Milliseconds)",
                    Description = "Maximum duration to wait for IPC or client command completion in milliseconds (default: 3000).",
                    Default = 3000
                },
                ["dryRun"] = new()
                {
                    Type = "boolean",
                    Title = "Dry Run",
                    Description = "If true, validates parameters and generates the exact K2-compliant Lua script without dispatching to the running game client.",
                    Default = false
                }
            },
            Required = ["action"]
        }
    };

    public static ToolDefinition CreateUiVisualDiffDefinition(string name = "kongor_ui_visual_diff") => new()
    {
        Name = name,
        Description = "SSIM (Structural Similarity Index), O(W*H) 2D integral-image 7x7 local_max perceptual comparator, square-rooted thermal heatmap, and SwiftShader golden corpus verification for K2 XML interfaces and 3D viewers.",
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
                    Description = "Action: 'diff' (compute SSIM + 7x7 local_max and output thermal heatmap artifact), 'assert' (fail if below SSIM or above local_max threshold), or 'capture_golden' (ensure r128 SwiftShader golden corpus).",
                    Enum = ["diff", "assert", "capture_golden"],
                    Default = "diff"
                },
                ["metric"] = new()
                {
                    Type = "string",
                    Title = "Comparison Metric",
                    Description = "Comparison metric: 'local_max' (7x7 integral-image neighborhood max + SSIM), 'ssim' (global luminance SSIM), or 'pixel'.",
                    Enum = ["local_max", "ssim", "pixel"],
                    Default = "local_max"
                },
                ["baselinePath"] = new()
                {
                    Type = "string",
                    Title = "Baseline Screenshot Path",
                    Description = "Path to baseline PNG screenshot image."
                },
                ["candidatePath"] = new()
                {
                    Type = "string",
                    Title = "Candidate Screenshot Path",
                    Description = "Path to candidate PNG screenshot image. If omitted, a synthetic UI frame is used."
                },
                ["threshold"] = new()
                {
                    Type = "number",
                    Title = "SSIM Threshold",
                    Description = "Minimum acceptable SSIM similarity score between 0.0 and 1.0 (default: 0.95).",
                    Default = 0.95
                },
                ["localMaxThreshold"] = new()
                {
                    Type = "number",
                    Title = "7x7 Local Max Threshold",
                    Description = "Maximum acceptable 7x7 neighborhood average peak difference in [0.0, 1.0] (default: 0.08).",
                    Default = 0.08
                },
                ["outputPath"] = new()
                {
                    Type = "string",
                    Title = "Diff Output Path",
                    Description = "Optional custom file path to save the highlighted difference PNG artifact."
                }
            },
            Required = ["action"]
        }
    };

    public static ToolDefinition CreateClientNavigateDefinition(string name = "kongor_client_navigate") => new()
    {
        Name = name,
        Description = "Declarative, closed-loop UI navigation over the K2 Game Client (hon_x64.exe). Navigates to any canonical screen ('main_menu', 'store', 'learnatorium', 'compendium', 'watch', 'live', 'options', 'profile', 'hero_preview', 'lobby'). Supports dual authentication ('authMode': 'cluster' for live MasterServer with auto-boot cluster health probing vs 'offline' for fast local bypass), flexible window placement ('windowMode': 'offscreen' for headless testing without desktop focus theft vs 'foreground'), pre-flight modal clearance, closed-loop widget assertions in engine memory, and transient ~350ms BitBlt visual proof.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = $"{name}Arguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["destination"] = new()
                {
                    Type = "string",
                    Title = "Destination",
                    Description = "Target screen or route: 'main_menu', 'store', 'learnatorium', 'compendium', 'watch', 'live', 'options', 'profile', 'hero_preview', or 'lobby'. Aliases: 'target', 'route', 'screen'.",
                    Enum = ["main_menu", "store", "learnatorium", "compendium", "watch", "live", "options", "profile", "hero_preview", "lobby", "create_game", "match_stats"],
                    Default = "main_menu"
                },
                ["authMode"] = new()
                {
                    Type = "string",
                    Title = "Authentication Mode",
                    Description = "Authentication mode: 'cluster' (probes and starts MasterServer/Aspire cluster if needed, boots connected to MasterServer, and authenticates genuinely to load real catalogs and inventories) or 'offline' (fast standalone bypass setting local cvars and unlocking UI without backend dependencies). Default: 'offline'.",
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
                    Description = "Optional local cluster dev account username for authentication (e.g. 'GUEST-01'). Aliases: 'username', 'player', 'accountName'."
                },
                ["password"] = new()
                {
                    Type = "string",
                    Title = "Password",
                    Description = "Optional local cluster password for authentication (resolved from local dev fixture when omitted). Aliases: 'pass'."
                },
                ["category"] = new()
                {
                    Type = "string",
                    Title = "Category / Tab",
                    Description = "Sub-category or tab (e.g. for options: 'graphics', 'sound', 'controls', 'gameplay', 'mods'; for store: 'altavatars', 'taunts', 'announcers'; for profile: 'overview', 'ranked', 'casual'). Aliases: 'tab'.",
                    Enum = ["graphics", "sound", "controls", "gameplay", "chat", "interface", "voice", "mods", "specials", "featured", "altavatars", "accountvanity", "gamevanity", "others", "bundles", "taunts", "announcers", "couriers", "wards", "overview", "ranked", "casual", "match_history", "heroes", "items"]
                },
                ["tab"] = new()
                {
                    Type = "string",
                    Title = "Tab",
                    Description = "Specific sub-tab within the target screen (e.g. 'graphics', 'heroes', 'replays', 'overview')."
                },
                ["subpage"] = new()
                {
                    Type = "string",
                    Title = "Subpage",
                    Description = "Subpage within store or compendium."
                },
                ["hero"] = new()
                {
                    Type = "string",
                    Title = "Hero",
                    Description = "Target hero identifier (e.g. 'Hero_Bubbles', 'Hero_Pyromancer') for compendium, store, or hero preview."
                },
                ["altSkin"] = new()
                {
                    Type = "string",
                    Title = "Alternate Skin / Avatar",
                    Description = "Alternate skin or avatar ID for store/hero preview (e.g. '16', 'Alt16'). Aliases: 'avatar'."
                },
                ["autoBoot"] = new()
                {
                    Type = "boolean",
                    Title = "Auto Boot",
                    Description = "If true, automatically boots hon_x64.exe in the specified windowMode if not currently running (default: true).",
                    Default = true
                },
                ["dismissPopups"] = new()
                {
                    Type = "boolean",
                    Title = "Dismiss Popups",
                    Description = "If true, sweeps and dismisses blocking modal popups (news, unverified warning, etc.) before navigating (default: true).",
                    Default = true
                },
                ["verify"] = new()
                {
                    Type = "boolean",
                    Title = "Closed-Loop Verification",
                    Description = "If true, verifies that the target container widget is visible in engine memory before returning (default: true).",
                    Default = true
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
                    Description = "Timeout in milliseconds for closed-loop verification (default: 3000).",
                    Default = 3000
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
                    Description = "If true, resolves the route, generates planned Lua and verification criteria without executing (default: false).",
                    Default = false
                }
            },
            Required = ["destination"]
        }
    };

    public static ToolDefinition CreateWaitForDefinition(string name = "kongor_wait_for") => new()
    {
        Name = name,
        Description = "Actively polls engine state or UI DOM over high-speed Mailbox IPC until a condition is satisfied or a timeout window expires.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = $"{name}Arguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["condition"] = new()
                {
                    Type = "string",
                    Title = "Condition",
                    Description = "Readiness condition to poll for: 'ipc_ready', 'engine_ready', 'client_ready', 'ui_visible', 'ui_hidden', 'ui_text', 'ui_present', or 'lua'.",
                    Enum = ["ipc_ready", "engine_ready", "client_ready", "ui_visible", "ui_hidden", "ui_text", "ui_present", "lua"],
                    Default = "ui_visible"
                },
                ["target"] = new()
                {
                    Type = "string",
                    Title = "Target",
                    Description = "Target widget name (for UI conditions) or Lua expression to evaluate.",
                    Examples = ["play_button", "main_background", "GetTime() > 5000"]
                },
                ["interface"] = new()
                {
                    Type = "string",
                    Title = "Interface Name",
                    Description = "Target UI interface name (default: 'main').",
                    Default = "main"
                },
                ["expected"] = new()
                {
                    Type = "string",
                    Title = "Expected Value",
                    Description = "Expected text or substring when condition is 'ui_text'."
                },
                ["timeoutMs"] = new()
                {
                    Type = "integer",
                    Title = "Timeout (ms)",
                    Description = "Maximum time to wait in milliseconds (default: 5000, min: 100, max: 60000).",
                    Default = 5000
                },
                ["intervalMs"] = new()
                {
                    Type = "integer",
                    Title = "Interval (ms)",
                    Description = "Polling interval in milliseconds (default: 100, min: 25, max: 5000).",
                    Default = 100
                }
            },
            Required = ["condition"]
        }
    };

    public static ToolDefinition CreateAssertUiDefinition(string name = "kongor_assert_ui") => new()
    {
        Name = name,
        Description = "Performs declarative zero-flake assertions on in-engine UI widget state (visibility, text, enabled, texture, dimensions) over Mailbox IPC.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = $"{name}Arguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["widget"] = new()
                {
                    Type = "string",
                    Title = "Widget Name",
                    Description = "Target widget name to inspect and assert.",
                    Examples = ["play_button", "hero_icon", "menu_bar"]
                },
                ["interface"] = new()
                {
                    Type = "string",
                    Title = "Interface Name",
                    Description = "UI interface containing the widget (default: 'main').",
                    Default = "main"
                },
                ["visible"] = new()
                {
                    Type = "boolean",
                    Title = "Expected Visible",
                    Description = "Asserts that widget visibility matches this boolean."
                },
                ["enabled"] = new()
                {
                    Type = "boolean",
                    Title = "Expected Enabled",
                    Description = "Asserts that widget enabled state matches this boolean."
                },
                ["text"] = new()
                {
                    Type = "string",
                    Title = "Expected Exact Text",
                    Description = "Asserts that widget text exactly matches this string."
                },
                ["textContains"] = new()
                {
                    Type = "string",
                    Title = "Expected Text Substring",
                    Description = "Asserts that widget text contains this substring."
                },
                ["texture"] = new()
                {
                    Type = "string",
                    Title = "Expected Texture Substring",
                    Description = "Asserts that widget texture contains this path or file name."
                },
                ["minWidth"] = new()
                {
                    Type = "number",
                    Title = "Minimum Width",
                    Description = "Asserts that widget width >= this value."
                },
                ["minHeight"] = new()
                {
                    Type = "number",
                    Title = "Minimum Height",
                    Description = "Asserts that widget height >= this value."
                }
            },
            Required = ["widget"]
        }
    };

    public static ToolDefinition CreateUiObserverDefinition(string name = "kongor_ui_observer") => new()
    {
        Name = name,
        Description = "Reactive widget observer that waits asynchronously for K2 XML/Lua UI elements to become visible, hidden, or change attributes with configurable timeouts. Enforces UIManager and parameterless :DoEvent().",
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
                    Description = "Observation action: wait_visible, wait_hidden, wait_text_change, wait_state.",
                    Enum = ["wait_visible", "wait_hidden", "wait_text_change", "wait_state"],
                    Default = "wait_visible"
                },
                ["widget"] = new()
                {
                    Type = "string",
                    Title = "Widget Path",
                    Description = "K2 widget path (e.g. 'main:sysbar_quickStart'). Conforms to UIManager.",
                    Default = "main:sysbar_quickStart"
                },
                ["timeout_ms"] = new()
                {
                    Type = "integer",
                    Title = "Timeout Milliseconds",
                    Description = "Maximum wait timeout in ms.",
                    Default = 5000
                },
                ["poll_interval_ms"] = new()
                {
                    Type = "integer",
                    Title = "Polling Interval Milliseconds",
                    Description = "Polling check interval in ms.",
                    Default = 50
                },
                ["expected_text"] = new() { Type = "string", Title = "Expected Text", Description = "Expected text content." },
                ["expected_state"] = new() { Type = "string", Title = "Expected State", Description = "Expected state string." }
            },
            Required = ["action", "widget"]
        }
    };

    public static ToolDefinition CreateAuditStatsDefinition(string name = "kongor_audit_stats") => new()
    {
        Name = name,
        Description = "Autonomous Client Stats & Match History Auditor: sweeps in-client player stats and match history, compares rendered UI against DB ground truth, detects mathematical/visual anomalies, and logs self-healing issues.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = $"{name}Arguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["nickname"] = new()
                {
                    Type = "string",
                    Title = "Player Nickname",
                    Description = "Target player nickname to audit (default: 'MCPPlayer').",
                    Default = "MCPPlayer"
                },
                ["tab"] = new()
                {
                    Type = "string",
                    Title = "Tab to Audit",
                    Description = "Target UI tab to audit: 'overview', 'match_history', or 'all'.",
                    Default = "all",
                    Enum = ["all", "overview", "match_history"]
                },
                ["synthetic"] = new()
                {
                    Type = "boolean",
                    Title = "Synthetic Mode",
                    Description = "Run in headless deterministic synthetic mode without requiring a live game window.",
                    Default = true
                },
                ["recordIssue"] = new()
                {
                    Type = "boolean",
                    Title = "Record Backlog Issue",
                    Description = "Automatically record detected discrepancies into docs/ISSUES.md.",
                    Default = true
                }
            },
            Required = []
        }
    };

    public static ToolDefinition CreateClientEvalLuaDefinition(string name = "kongor_client_eval_lua") => new()
    {
        Name = name,
        Description = "In-Game Client Lua Hot-Reload & Remote Execution: dispatches sub-50ms Lua statements and hot-reloads modular hero definitions, catalogs, and UI scripts into the live hon_x64.exe client memory over Named Pipe IPC (\\\\.\\pipe\\willowmaker-ipc). Eliminates having to close and restart the game client when editing scripts.",
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
                    Description = "Operation: 'eval' (executes arbitrary Lua expression with pcall safety and returns output), 'reload_hero' (hot-reloads specific hero in HeroDefinitionsCache), 'reload_all_heroes' (reloads all 141 heroes into memory), 'reload_store' (refreshes HoN_Store cache), or 'reload_ui' (ReloadInterfaces + nexus_bridge).",
                    Default = "eval",
                    Enum = ["eval", "reload_hero", "reload_all_heroes", "reload_store", "reload_ui"]
                },
                ["code"] = new()
                {
                    Type = "string",
                    Title = "Lua Code",
                    Description = "Lua snippet to evaluate when action='eval' (e.g. 'return HeroDefinitionsCache.Heroes[\"Hero_Pyromancer\"].displayName')."
                },
                ["hero"] = new()
                {
                    Type = "string",
                    Title = "Hero Key",
                    Description = "Hero entity key to reload when action='reload_hero' (e.g. 'Hero_Pyromancer', 'Hero_Fayde', 'Hero_Artillery'). Default is 'Hero_Pyromancer'.",
                    Default = "Hero_Pyromancer"
                },
                ["timeout_ms"] = new()
                {
                    Type = "integer",
                    Title = "Timeout (ms)",
                    Description = "IPC response timeout in milliseconds. Default is 3000.",
                    Default = 3000
                }
            },
            Required = ["action"]
        }
    };

    public static ToolDefinition CreateClientCapturePreviewDefinition(string name = "kongor_client_capture_preview") => new()
    {
        Name = name,
        Description = "Automated Preview Stage Viewport Capture: directs the live hon_x64.exe client to stage a hero/alt in the 3D preview viewport, waits for model/texture rendering, and captures a PNG screenshot saved directly to the artifacts directory for instant visual inspection.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = $"{name}Arguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["hero"] = new()
                {
                    Type = "string",
                    Title = "Hero Key",
                    Description = "Hero entity key to stage in preview viewport (e.g. 'Hero_Pyromancer', 'Hero_Fayde', 'Hero_Artillery')."
                },
                ["alt"] = new()
                {
                    Type = "string",
                    Title = "Alt / Avatar Key",
                    Description = "Avatar or alt skin folder name (e.g. 'default', 'alt', 'alt2'). Default is 'default'.",
                    Default = "default"
                },
                ["target"] = new()
                {
                    Type = "string",
                    Title = "Capture Target",
                    Description = "Target window or process name. Default is 'hon_x64'.",
                    Enum = ["hon_x64", "foreground"],
                    Default = "hon_x64"
                },
                ["delay_ms"] = new()
                {
                    Type = "integer",
                    Title = "Render Delay (ms)",
                    Description = "Milliseconds to wait for the 3D model frame to render before snapping screenshot. Default is 350.",
                    Default = 350
                },
                ["save_path"] = new()
                {
                    Type = "string",
                    Title = "Save Path",
                    Description = "Optional custom file path for the captured PNG artifact."
                }
            },
            Required = ["hero"]
        }
    };
}
