// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using Kongor.Cli.Mcp.Handlers;

namespace Kongor.Cli.Mcp.Protocol;

public sealed partial class McpToolRegistry
{
    private CrashTriageHandler _crashTriageHandler = null!;

    private CrashPatchHandler _crashPatchHandler = null!;
    private SelfHealHandler _selfHealHandler = null!;
    private LogCorrelateHandler _logCorrelateHandler = null!;
    private GetClientLogsHandler _getClientLogsHandler = null!;

    public CrashTriageHandler CrashTriageHandler => _crashTriageHandler;

    public CrashPatchHandler CrashPatchHandler => _crashPatchHandler;
    public SelfHealHandler SelfHealHandler => _selfHealHandler;
    public LogCorrelateHandler LogCorrelateHandler => _logCorrelateHandler;
    public GetClientLogsHandler GetClientLogsHandler => _getClientLogsHandler;

    partial void InitializeForensicsHandlers(TimeProvider timeProvider)
    {
        _crashTriageHandler = new CrashTriageHandler(timeProvider);

        _crashPatchHandler = new CrashPatchHandler(timeProvider);
        _selfHealHandler = new SelfHealHandler(timeProvider);
        _logCorrelateHandler = new LogCorrelateHandler(timeProvider);
        _getClientLogsHandler = new GetClientLogsHandler(ipcBridge: _gameIpcBridge, timeProvider: timeProvider);
    }

    partial void RegisterForensicsTools()
    {
        RegisterTool(CreateGameTelemetryDefinition(), _gameTelemetryHandler.HandleAsync, McpToolCategory.ReadOnly);
        RegisterTool(CreateClusterDiagnosticsDefinition("kongor_diagnostics"), _clusterDiagnosticsHandler.HandleAsync, McpToolCategory.ReadOnly);
        RegisterTool(CreateEngineInspectDefinition("kongor_engine_inspect"), _engineInspectHandler.HandleAsync, McpToolCategory.ReadOnly);
        RegisterTool(CreateTelemetryStreamDefinition("kongor_telemetry_stream"), _telemetryStreamHandler.HandleAsync, McpToolCategory.ReadOnly);
        RegisterTool(CreateMemoryLeakDetectDefinition("kongor_memory_leak_detect"), _memoryLeakDetectHandler.HandleAsync, McpToolCategory.HeavyProfiling);
        RegisterTool(CreateCrashDumpAnalyzeDefinition("kongor_crash_dump_analyze"), _crashDumpAnalyzeHandler.HandleAsync, McpToolCategory.ReadOnly);
        RegisterTool(CreateNativeManageDefinition("kongor_native_manage"), _nativeManageHandler.HandleAsync, McpToolCategory.ServerManagement);

        RegisterTool(CreateTaoCapabilitiesDefinition("kongor_tao_capabilities"), _taoCapabilitiesHandler.HandleAsync, McpToolCategory.ReadOnly);
        RegisterAlias("kongor_tao_caps", "kongor_tao_capabilities");
        RegisterAlias("tao_capabilities", "kongor_tao_capabilities");

        RegisterTool(CreateErrorsSinceDefinition("kongor_errors_since"), _errorsSinceHandler.HandleAsync, McpToolCategory.ReadOnly);
        RegisterAlias("errors_since", "kongor_errors_since");
        RegisterAlias("kongor_errors", "kongor_errors_since");

        RegisterTool(CreateHealthCheckDefinition("kongor_health_check"), _healthCheckHandler.HandleAsync, McpToolCategory.ReadOnly);
        RegisterAlias("health_check", "kongor_health_check");
        RegisterAlias("kongor_health", "kongor_health_check");

        RegisterTool(CreatePerfWindowDefinition("kongor_perf_window"), _perfWindowHandler.HandleAsync, McpToolCategory.ReadOnly);
        RegisterAlias("perf_window", "kongor_perf_window");
        RegisterAlias("kongor_perf", "kongor_perf_window");

        RegisterTool(CreateCrashTriageDefinition("kongor_crash_triage"), _crashTriageHandler.HandleAsync, McpToolCategory.ReadOnly);
        RegisterAlias("crash_triage", "kongor_crash_triage");
        RegisterAlias("kongor_triage", "kongor_crash_triage");





        RegisterTool(CreateCrashPatchDefinition("kongor_crash_patch"), _crashPatchHandler.HandleAsync, McpToolCategory.ServerManagement);
        RegisterAlias("crash_patch", "kongor_crash_patch");

        RegisterTool(CreateSelfHealDefinition("kongor_self_heal"), _selfHealHandler.HandleAsync, McpToolCategory.ServerManagement);
        RegisterAlias("self_heal", "kongor_self_heal");

        RegisterTool(CreateLogCorrelateDefinition("kongor_log_correlate"), _logCorrelateHandler.HandleAsync, McpToolCategory.ReadOnly);
        RegisterAlias("log_correlate", "kongor_log_correlate");

        RegisterTool(CreateGetClientLogsDefinition("kongor_get_client_logs"), _getClientLogsHandler.HandleAsync, McpToolCategory.ReadOnly);
        RegisterAlias("get_client_logs", "kongor_get_client_logs");
    }

    public static ToolDefinition CreateGameTelemetryDefinition() => new()
    {
        Name = "kongor_game_telemetry",
        Description = "Query real-time game engine telemetry (instantaneous FPS, frametimes, memory working set, entities, draw calls, ping, match state) across dual planes: zero-copy Shared Memory (MMF ring buffer) for sub-microsecond data plane queries, and Named Pipe IPC for control plane fallback.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = "kongor_game_telemetryArguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["channel"] = new()
                {
                    Type = "string",
                    Title = "Channel",
                    Description = "Data transport channel: 'auto' (try SHM MMF first, fallback to IPC), 'shm' (require shared memory ring buffer), or 'pipe'/'ipc' (Named Pipe IPC).",
                    Enum = ["auto", "shm", "mmf", "pipe", "ipc"],
                    Default = "auto"
                },
                ["action"] = new()
                {
                    Type = "string",
                    Title = "Action",
                    Description = "Telemetry read operation: 'latest' (read single latest instantaneous frame), 'poll_ring' (drain available frame batch from circular ring buffer), or 'stream'.",
                    Enum = ["latest", "poll_ring", "stream"],
                    Default = "latest"
                },
                ["mapName"] = new()
                {
                    Type = "string",
                    Title = "Map Name",
                    Description = "Optional explicit Win32 Shared Memory file mapping name (e.g. 'Local\\KongorClientTelemetry_12345'). If omitted, auto-discovers running client PID or global mapping."
                },
                ["limit"] = new()
                {
                    Type = "integer",
                    Title = "Limit",
                    Description = "Maximum number of frame telemetry records to drain when action is 'poll_ring' (1 to 1024).",
                    Default = 64
                },
                ["pid"] = new()
                {
                    Type = "integer",
                    Title = "Process ID",
                    Description = "Explicit process ID of target client to resolve its private memory-mapped telemetry ring buffer."
                },
                ["timeoutMs"] = new()
                {
                    Type = "integer",
                    Title = "Timeout Ms",
                    Description = "Timeout in milliseconds for Named Pipe IPC fallback query.",
                    Default = 2000
                }
            },
            Required = ["action"]
        }
    };

    public static ToolDefinition CreateTaoCapabilitiesDefinition(string name = "kongor_tao_capabilities") => new()
    {
        Name = name,
        Description = "Inspects and reports in-engine TAO (Together as One) capabilities, registered action handlers, SHA-256 integrity hash of tao.lua, staleness status (comparing live engine hash vs disk hash), and mailbox round-trip latency.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = $"{name}Arguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["timeoutMs"] = new()
                {
                    Type = "integer",
                    Title = "Timeout (ms)",
                    Description = "Optional timeout in milliseconds for the capabilities probe (default: 2000, min: 250, max: 10000).",
                    Default = 2000
                }
            },
            Required = []
        }
    };

    public static ToolDefinition CreateErrorsSinceDefinition(string name = "kongor_errors_since") => new()
    {
        Name = name,
        Description = "Inspects engine console logs, client Lua debug logs, sentinel crash journals, and minidumps since a specified checkpoint token or time window.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = $"{name}Arguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["checkpoint"] = new()
                {
                    Type = "string",
                    Title = "Checkpoint Token",
                    Description = "Optional checkpoint token (e.g. 'cp_1728748800000') or ISO-8601 UTC timestamp. If omitted, defaults to sinceSeconds.",
                    Examples = ["cp_1728748800000", "2026-10-12T15:00:00Z"]
                },
                ["sinceSeconds"] = new()
                {
                    Type = "integer",
                    Title = "Since Seconds",
                    Description = "Lookback window in seconds when no checkpoint token is provided (default: 60, min: 1, max: 86400).",
                    Default = 60
                },
                ["severity"] = new()
                {
                    Type = "string",
                    Title = "Severity Filter",
                    Description = "Filter level: 'error' (default), 'warn', or 'all'.",
                    Enum = ["error", "warn", "all"],
                    Default = "error"
                },
                ["limit"] = new()
                {
                    Type = "integer",
                    Title = "Result Limit",
                    Description = "Maximum number of error lines to return (default: 100, max: 1000).",
                    Default = 100
                }
            },
            Required = []
        }
    };

    public static ToolDefinition CreateHealthCheckDefinition(string name = "kongor_health_check") => new()
    {
        Name = name,
        Description = "Performs comprehensive multi-subsystem health check: client process status, mailbox IPC ping latency, TAO layer build freshness, and error status.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = $"{name}Arguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["checkpoint"] = new()
                {
                    Type = "string",
                    Title = "Checkpoint Token",
                    Description = "Optional checkpoint token for baseline error assessment. If omitted, checks the last 60 seconds.",
                    Examples = ["cp_1728748800000"]
                },
                ["probeMailbox"] = new()
                {
                    Type = "boolean",
                    Title = "Probe Mailbox",
                    Description = "Whether to ping the mailbox IPC to measure round-trip latency (default: true).",
                    Default = true
                },
                ["probeCapabilities"] = new()
                {
                    Type = "boolean",
                    Title = "Probe Capabilities",
                    Description = "Whether to check TAO layer build freshness against disk tao.lua (default: true).",
                    Default = true
                }
            },
            Required = []
        }
    };

    public static ToolDefinition CreatePerfWindowDefinition(string name = "kongor_perf_window") => new()
    {
        Name = name,
        Description = "Measures a specific interaction window with frame-accurate native QPC telemetry and Lua UI transition attribution. Detects hitches (>30ms, >50ms), attributes hitches to UI transitions by frame number, and verifies frame budget (<= 1.0ms for UI ticks).",
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
                    Description = "Action to execute: 'start' (begin recording window), 'stop' (end recording and compute metrics), 'status' (query active recording state), 'measure' (run start/wait/stop), or 'simulate_hitch' (force deliberate >30ms busy-loop).",
                    Enum = ["start", "stop", "status", "measure", "simulate_hitch"],
                    Default = "status"
                },
                ["durationMs"] = new()
                {
                    Type = "integer",
                    Title = "Duration (ms)",
                    Description = "Duration in milliseconds when action is 'measure' (default: 1000, min: 50, max: 30000).",
                    Default = 1000
                },
                ["hitchThresholdMs"] = new()
                {
                    Type = "number",
                    Title = "Hitch Threshold (ms)",
                    Description = "Frame time threshold in milliseconds to classify as a minor hitch (default: 30.0).",
                    Default = 30.0
                },
                ["majorHitchThresholdMs"] = new()
                {
                    Type = "number",
                    Title = "Major Hitch Threshold (ms)",
                    Description = "Frame time threshold in milliseconds to classify as a major hitch (default: 50.0).",
                    Default = 50.0
                },
                ["budgetThresholdMs"] = new()
                {
                    Type = "number",
                    Title = "Frame Budget Threshold (ms)",
                    Description = "Maximum allowed duration in milliseconds for UI tick callbacks / transitions (default: 1.0).",
                    Default = 1.0
                },
                ["pid"] = new()
                {
                    Type = "integer",
                    Title = "Process ID",
                    Description = "Optional target process ID of running game client (auto-detected if omitted)."
                }
            },
            Required = ["action"]
        }
    };

    public static ToolDefinition CreateClusterDiagnosticsDefinition(string name = "kongor_cluster_diagnostics") => new()
    {
        Name = name,
        Description = "Comprehensive health and wire probe across the ProjectKongor cluster. Validates Redis distributed cache (port 28298, ping latency, auth), Supabase / PostgreSQL MERRICK DB connection string and socket, service HTTP/TCP endpoints (MasterServer: 55555, ChatServer: 11031/5554, Web API: 5200, Web UI: 5266, COMPEL: 11234), and aggregates recent fatal/error entries across service logs.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = $"{name}Arguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["redisHost"] = new()
                {
                    Type = "string",
                    Title = "Redis Host",
                    Description = "Hostname or IP for the Redis distributed cache.",
                    Default = "127.0.0.1"
                },
                ["redisPort"] = new()
                {
                    Type = "integer",
                    Title = "Redis Port",
                    Description = "Port number for the Redis distributed cache.",
                    Default = 28298
                },
                ["dbConnectionString"] = new()
                {
                    Type = "string",
                    Title = "DB Connection String",
                    Description = "Optional explicit PostgreSQL/Supabase connection string. Defaults to solution configuration / User Secrets."
                },
                ["maxLogErrors"] = new()
                {
                    Type = "integer",
                    Title = "Max Log Errors",
                    Description = "Maximum number of recent fatal or error log entries to aggregate across log files.",
                    Default = 5
                }
            },
            Required = []
        }
    };

    public static ToolDefinition CreateEngineInspectDefinition(string name = "kongor_engine_inspect") => new()
    {
        Name = name,
        Description = "Lightweight PE binary symbol, export table, Lua CFunction, and CVar introspector. Inspects binary exports, registered Lua routines, and default CVars in k2_x64.dll, cgame_x64.dll, game_shared_x64.dll, etc. in strictly read-only mode.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = $"{name}Arguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["target"] = new()
                {
                    Type = "string",
                    Title = "Target",
                    Description = "Target binary name or relative path (e.g. 'cgame_x64.dll', 'k2_x64.dll', 'game_shared_x64.dll', 'hon_x64.exe').",
                    Enum = ["cgame_x64.dll", "k2_x64.dll", "game_x64.dll", "game_shared_x64.dll", "hon_x64.exe", "vid_d3d9_x64.dll", "vid_vk_x64.dll"],
                    Default = "cgame_x64.dll"
                },
                ["query"] = new()
                {
                    Type = "string",
                    Title = "Query",
                    Description = "Symbol name, function name, CVar name, or substring filter to search for (e.g. 'StartWatchingMatch', 'cl_spectator', 'vid_')."
                },
                ["category"] = new()
                {
                    Type = "string",
                    Title = "Category",
                    Description = "Symbol category filter: 'all', 'export', 'lua_binding', 'cvar', 'cmd'.",
                    Enum = ["all", "export", "lua_binding", "cvar", "cmd"],
                    Default = "all"
                },
                ["max_results"] = new()
                {
                    Type = "integer",
                    Title = "Max Results",
                    Description = "Maximum number of matched symbols to return (default: 50).",
                    Default = 50
                }
            },
            Required = []
        }
    };

    public static ToolDefinition CreateTelemetryStreamDefinition(string name = "kongor_telemetry_stream") => new()
    {
        Name = name,
        Description = "Stream 20Hz / 60Hz binary & structured match telemetry of all entities (heroes, creeps, towers, buildings, projectiles) with sub-millisecond precision.",
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
                    Description = "Action to perform: 'start' (begin streaming), 'snapshot' (instant telemetry frame), 'read', or 'stop' (halt streaming).",
                    Enum = ["start", "snapshot", "read", "stop"],
                    Default = "snapshot"
                },
                ["port"] = new()
                {
                    Type = "integer",
                    Title = "Server Port",
                    Description = "Dedicated server listen port (default: 11235).",
                    Default = 11235
                },
                ["pid"] = new()
                {
                    Type = "integer",
                    Title = "Process ID",
                    Description = "Optional client process ID (hon_x64.exe) to attach to client telemetry ring buffer (Local\\KongorClientTelemetry_{pid})."
                },
                ["mapName"] = new()
                {
                    Type = "string",
                    Title = "Shared Memory Map Name",
                    Description = "Explicit shared memory map name to consume from (e.g. Local\\KongorClientTelemetry_1234 or Local\\KongorServerTelemetry_11235)."
                },
                ["rateHz"] = new()
                {
                    Type = "integer",
                    Title = "Stream Rate (Hz)",
                    Description = "Telemetry stream frequency in Hertz: 20 or 60 (default: 20).",
                    Default = 20
                },
                ["sessionId"] = new()
                {
                    Type = "string",
                    Title = "Session ID",
                    Description = "Optional session identifier for tracking continuous streaming."
                }
            },
            Required = ["action"]
        }
    };

    public static ToolDefinition CreateMemoryLeakDetectDefinition(string name = "kongor_memory_leak_detect") => new()
    {
        Name = name,
        Description = "Continuous sampling of native working set, private bytes, GDI handles, User handles, and OS handles in hon_x64.exe and dedicated servers with regression slope analysis.",
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
                    Description = "Action: 'sample' (instant memory & handle sample) or 'profile' (multi-sample regression slope calculation).",
                    Enum = ["sample", "profile"],
                    Default = "sample"
                },
                ["target"] = new()
                {
                    Type = "string",
                    Title = "Target Process",
                    Description = "Target process: 'client' (hon_x64.exe), 'server' (dedicated server), 'compel', or specific PID.",
                    Enum = ["client", "server", "compel"],
                    Default = "client"
                },
                ["durationSeconds"] = new()
                {
                    Type = "integer",
                    Title = "Profile Duration (Seconds)",
                    Description = "Sampling duration in seconds for 'profile' action (1–30s, default: 3s).",
                    Default = 3
                },
                ["intervalMs"] = new()
                {
                    Type = "integer",
                    Title = "Sample Interval (ms)",
                    Description = "Interval between samples in milliseconds (default: 500ms).",
                    Default = 500
                }
            },
            Required = ["action"]
        }
    };

    public static ToolDefinition CreateCrashDumpAnalyzeDefinition(string name = "kongor_crash_dump_analyze") => new()
    {
        Name = name,
        Description = "Win32 .dmp exception code extractor, faulting module resolver, and native stack unwinder for game client and dedicated server crashes.",
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
                    Description = "Action: 'analyze' (extract exception code & stack from .dmp) or 'list_recent' (discover crash dumps).",
                    Enum = ["analyze", "list_recent"],
                    Default = "analyze"
                },
                ["dumpPath"] = new()
                {
                    Type = "string",
                    Title = "Dump File Path",
                    Description = "Absolute or relative path to .dmp file. If omitted, the most recent crash dump is resolved automatically."
                }
            },
            Required = ["action"]
        }
    };

    public static ToolDefinition CreateNativeManageDefinition(string name = "kongor_native_manage") => new()
    {
        Name = name,
        Description = "Governs native C++ game engine binaries (e.g. zlibwapi_x64.dll). Inspects security posture and CVE status, validates in-memory PE export table parity, upgrades to modern drop-in builds (zlib 1.3.1 with AVX2), and manages two-tier rollback (.bak and .bak.old).",
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
                    Description = "Action to perform: 'status', 'verify', 'upgrade', or 'rollback'.",
                    Enum = ["status", "verify", "upgrade", "rollback"],
                    Default = "status"
                },
                ["targetTier"] = new()
                {
                    Type = "string",
                    Title = "Target Tier",
                    Description = "Rollback target tier: 'previous' (immediate previous, default) or 'old' (historical baseline).",
                    Enum = ["previous", "old"],
                    Default = "previous"
                },
                ["candidatePath"] = new()
                {
                    Type = "string",
                    Title = "Candidate Path",
                    Description = "Explicit path to candidate replacement DLL (optional)."
                },
                ["skipProcessCheck"] = new()
                {
                    Type = "boolean",
                    Title = "Skip Process Check",
                    Description = "Bypass running game process check (useful for automated tests).",
                    Default = false
                }
            },
            Required = ["action"]
        }
    };

    public static ToolDefinition CreateCrashTriageDefinition(string name = "kongor_crash_triage") => new()
    {
        Name = name,
        Description = "Automated Crash & Fault Triangulation: minidump (.dmp) and diagnostic log analysis, ASLR multi-DLL mapping across k2/cgame/game_shared, faulting register extraction, and Ghidra pseudo-C decompilation commands in < 500ms.",
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
                    Description = "Triage action: 'triage' (analyze latest or specified dump), 'diagnose', or 'list' (list recent crash files).",
                    Default = "triage",
                    Enum = ["triage", "diagnose", "list"]
                },
                ["dump_path"] = new()
                {
                    Type = "string",
                    Title = "Dump File Path",
                    Description = "Path to minidump (.dmp) or diagnostic log (diag_*.txt, console.log). Auto-resolves latest if omitted."
                },
                ["module"] = new()
                {
                    Type = "string",
                    Title = "Target Module Override",
                    Description = "Faulting module name override (e.g. 'cgame_x64.dll', 'k2_x64.dll', 'vid_d3d9_x64.dll').",
                    Enum = ["cgame_x64.dll", "k2_x64.dll", "game_x64.dll", "game_shared_x64.dll", "hon_x64.exe", "vid_d3d9_x64.dll", "vid_vk_x64.dll"]
                },
                ["address"] = new()
                {
                    Type = "string",
                    Title = "Faulting Address / RVA",
                    Description = "Faulting relative virtual address (RVA) or offset (e.g. '0x1e5de0', '0x1402')."
                }
            },
            Required = ["action"]
        }
    };


    public static ToolDefinition CreateCrashPatchDefinition(string name = "kongor_crash_patch") => new()
    {
        Name = name,
        Description = "Machine Code Crash Patcher & ABI-Safe Detour Verifier: synthesizes x64 detours (null-checks, zero-divisor guards, early returns) conforming to Windows x64 calling conventions, performs non-destructive dry-run byte verification, and idempotently applies patches with .bak backup safety.",
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
                    Description = "Crash patch action: 'synthesize' (generates ABI-safe detour recipe), 'dry_run' (verifies binary bytes without writing), 'apply' (creates .bak and applies patch), or 'revert' (restores from .bak).",
                    Default = "synthesize",
                    Enum = ["synthesize", "dry_run", "apply", "revert"]
                },
                ["target_module"] = new()
                {
                    Type = "string",
                    Title = "Target Module",
                    Description = "Target native binary name (e.g. 'k2_x64.dll', 'cgame_x64.dll', 'vid_d3d9_x64.dll', 'hon_x64.exe').",
                    Enum = ["k2_x64.dll", "cgame_x64.dll", "game_x64.dll", "game_shared_x64.dll", "hon_x64.exe", "vid_d3d9_x64.dll", "vid_vk_x64.dll"],
                    Default = "k2_x64.dll"
                },
                ["faulting_rva"] = new()
                {
                    Type = "string",
                    Title = "Faulting RVA / Address",
                    Description = "Hexadecimal RVA offset where the crash occurred (e.g. '0x0005B0D0', '0x8B72A8').",
                    Default = "0x0"
                },
                ["exception_code"] = new()
                {
                    Type = "string",
                    Title = "Exception Code",
                    Description = "Exception code (e.g. '0xC0000005' for Access Violation, '0xC0000094' for Divide by Zero, '0xC00000FD' for Stack Overflow).",
                    Enum = ["0xC0000005", "0xC0000094", "0xC00000FD", "0xE06D7363"],
                    Default = "0xC0000005"
                },
                ["original_bytes_hex"] = new()
                {
                    Type = "string",
                    Title = "Original Bytes Hex",
                    Description = "Expected original instruction bytes in hex (e.g. '48 8B 41 18') for pre-flight safety verification."
                },
                ["detour_bytes_hex"] = new()
                {
                    Type = "string",
                    Title = "Detour Bytes Hex",
                    Description = "Replacement detour instruction bytes in hex (e.g. '48 85 C9 74 05 ...')."
                },
                ["binary_path"] = new()
                {
                    Type = "string",
                    Title = "Custom Binary Path",
                    Description = "Optional explicit absolute file path to the target binary."
                }
            },
            Required = ["action"]
        }
    };

    public static ToolDefinition CreateSelfHealDefinition(string name = "kongor_self_heal") => new()
    {
        Name = name,
        Description = "Autonomous Self-Healing Pipeline & Developer Issues Backplane: watches engine logs (client console.log, dedicated servers, Aspire), ingests and deduplicates runtime errors with SHA-256 fingerprints, and executes the Dual-State Reflex loop (Record First -> Immediate Reflex Healing -> Auto-Resolve with Receipt) to maintain the prioritized Self-Healing Journey backlog.",
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
                    Description = "Self-healing operation: 'scan_and_ingest' (scans logs and executes reflex healing), 'auto_heal_reflex' (executes reflex recipe on specific fingerprint), 'list_pending' (returns prioritized backlog for self-healing journey), 'journey_summary' (full journey report), or 'sync_tracker' (syncs to docs/ISSUES.md and memory).",
                    Default = "journey_summary",
                    Enum = ["scan_and_ingest", "auto_heal_reflex", "list_pending", "journey_summary", "sync_tracker"]
                },
                ["log_path"] = new()
                {
                    Type = "string",
                    Title = "Log File Path",
                    Description = "Optional path to log file to scan (e.g. 'console.log', 'logs/server_11235.log', 'WILLOWMAKER.log')."
                },
                ["raw_log"] = new()
                {
                    Type = "string",
                    Title = "Raw Log Text",
                    Description = "Optional raw log snippet or error stack trace to ingest and self-heal."
                },
                ["fingerprint"] = new()
                {
                    Type = "string",
                    Title = "Issue Fingerprint",
                    Description = "Target SHA-256 fingerprint when action='auto_heal_reflex'."
                },
                ["limit"] = new()
                {
                    Type = "integer",
                    Title = "Limit",
                    Description = "Maximum number of pending issues to list when action='list_pending'. Default is 20."
                }
            },
            Required = ["action"]
        }
    };

    public static ToolDefinition CreateLogCorrelateDefinition(string name = "kongor_log_correlate") => new()
    {
        Name = name,
        Description = "Cross-Service Telemetry & Distributed Log Correlator: unifies match events, combat metrics, distributed traces, and error logs across MasterServer, ChatServer, WebApi, Compel Dedicated Servers, and DuckDB lakehouse.",
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
                    Description = "Correlation operation: 'match' (correlates DuckDB combat stats and service events for a match ID), 'errors' (aggregates recent errors across services), 'trace' (queries distributed spans and logs for a trace ID), or 'latency' (identifies slow operations >50ms).",
                    Default = "errors",
                    Enum = ["match", "errors", "trace", "latency"]
                },
                ["match_id"] = new()
                {
                    Type = "integer",
                    Title = "Match ID",
                    Description = "Target match ID when action='match' (e.g. 1001)."
                },
                ["trace_id"] = new()
                {
                    Type = "string",
                    Title = "Trace ID",
                    Description = "W3C Trace ID string when action='trace'."
                },
                ["minutes"] = new()
                {
                    Type = "integer",
                    Title = "Time Window (Minutes)",
                    Description = "Time window in minutes when action='errors' (default: 15).",
                    Default = 15
                },
                ["limit"] = new()
                {
                    Type = "integer",
                    Title = "Row Limit",
                    Description = "Maximum entries to return for latency queries (default: 20).",
                    Default = 20
                }
            },
            Required = ["action"]
        }
    };

    public static ToolDefinition CreateGetClientLogsDefinition(string name = "kongor_get_client_logs") => new()
    {
        Name = name,
        Description = "Real-Time Telemetry & Ring Buffer Streamer: queries the 300-entry in-memory ring buffer in KongorLogger.lua over Named Pipe IPC (\\\\.\\pipe\\willowmaker-ipc) or tails disk logs (client_lua_debug.log / game logs). Delivers structured diagnostic logs with channel filtering (AVATAR, STORE, STATE, CRASH_GUARD, UI, HERO, NET) and error triage.",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Title = $"{name}Arguments",
            Properties = new Dictionary<string, ToolPropertySchema>(StringComparer.Ordinal)
            {
                ["channel"] = new()
                {
                    Type = "string",
                    Title = "Channel Filter",
                    Description = "Diagnostic channel filter ('AVATAR', 'STORE', 'STATE', 'CRASH_GUARD', 'UI', 'HERO', 'NET', 'MAP', 'ALL'). Default is 'ALL'.",
                    Default = "ALL",
                    Enum = ["ALL", "AVATAR", "STORE", "STATE", "CRASH_GUARD", "UI", "HERO", "NET", "MAP", "GENERAL"]
                },
                ["errors_only"] = new()
                {
                    Type = "boolean",
                    Title = "Errors Only",
                    Description = "If true, returns only entries with severity WARN, ERROR, or FATAL. Default is false.",
                    Default = false
                },
                ["limit"] = new()
                {
                    Type = "integer",
                    Title = "Max Entries",
                    Description = "Maximum number of recent log entries to retrieve. Default is 50.",
                    Default = 50
                },
                ["source"] = new()
                {
                    Type = "string",
                    Title = "Log Source",
                    Description = "Log source: 'auto' (prefers in-memory IPC ring buffer, falls back to disk), 'ring_buffer' (in-memory only), or 'disk'. Default is 'auto'.",
                    Default = "auto",
                    Enum = ["auto", "ring_buffer", "disk"]
                }
            },
            Required = []
        }
    };
}
