// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using Kongor.Cli.Commands;
using Kongor.Cli.Infrastructure;
using Kongor.Core.Configuration;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Kongor.Cli;

public static partial class Program
{
    internal static IReadOnlyList<string> AllPrimaryCommands => KnownPrimaryCommands;

    private static readonly string[] KnownPrimaryCommands =
    [
        "play", "launcher", "willowmaker",
        "client", "host", "compel", "server",
        "gui", "control-panel", "nexus", "admin",
        "aspire", "cluster", "start",
        "docker", "stack", "docker-stack", "sync", "docker-sync", "docker-rebuild",
        "build", "compile", "rebuild", "test", "tests", "package", "publish",
        "master-server", "chat-server", "web-api", "web-ui",
        "stop", "kill", "shutdown",
        "mcp", "mcp-outcomes", "capture", "k2-lint", "lua-check", "ui-playground", "ui",
        "engine-inspect", "native", "patch", "vulkan", "security", "guard",
        "server-fleet", "archive", "vfs", "rl", "audio", "video", "memory", "input",
        "replay", "highlight", "triage", "heal", "forge", "scrimmage", "watch",
        "audit", "vfs-audit", "vfs-hydrate", "telemetry", "analytics", "wal", "deploy",
        "model", "mixamo", "blender", "comfy", "comfyui", "docs",
        "refresh", "install", "ask", "version", "sync-version",
        "tools", "mod", "prebake", "audit-endpoints", "lua-verify", "k2-test",
        "doctor", "status", "clean", "secret", "logs", "account", "invite",
        "where", "workspace", "ws", "completion",
        "gate", "oracle", "practice", "inject", "decompile", "sanitize", "sync-lua",
        "sbom", "scan", "soak", "health", "migrate", "tailwind", "install-hooks", "audit-all", "test-e2e", "docgen",
        "coverage", "test-coverage"
    ];

    private static readonly HashSet<string> RawForwardingCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "play", "launcher", "willowmaker",
        "host", "compel", "server",
        "model", "models", "model_exporter", "model-exporter", "model-tools", "model-export", "mixamo", "mixamo-export",
        "blender", "launch-blender", "open-blender",
        "install_blender", "install-blender", "setup-blender",
        "comfyui", "comfy", "launch-comfyui",
        "install_comfyui", "install-comfyui", "setup-comfyui",
        "comfyui_mcp", "comfy-mcp", "comfymcp",
        "replay", "replays", "render-replay", "render", "replay-mp4",
        "highlight", "highlights", "teamfight",
        "docs", "doc", "docfx",
        "secret", "secrets", "config",
        "forge", "content-forge", "create-content",
        "ui-playground", "ui", "playground",
        "gate", "gate-check", "gatecheck",
        "oracle", "netcode-oracle", "verify-netcode",
        "practice", "screen-practice", "launch-practice",
        "inject", "inject-s2z", "inject-override",
        "decompile", "decompile-bytecode",
        "sanitize", "sanitize-workspace",
        "sync-lua", "sync-override",
        "scan", "vuln", "vulnerabilities", "security-scan",
        "soak", "endurance",
        "migrate", "migration", "migrations", "db-migrate",
        "tailwind", "tw", "css",
        "audit-all", "test-all", "audit-suite",
        "test-e2e", "e2e", "test-suite",
        "docgen", "charts", "generate-charts"
    };

    public static async Task<int> Main(string[] args)
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

        Stopwatch sw = Stopwatch.StartNew();
        string[] effectiveArgs = CliRuntimeContext.Initialize(args);
        int exitCode = 0;
        string? telemetryError = null;
        bool isMcpStdio = effectiveArgs.Length > 0 &&
            (effectiveArgs[0].Equals("mcp", StringComparison.OrdinalIgnoreCase) ||
             effectiveArgs[0].Equals("mcp-server", StringComparison.OrdinalIgnoreCase) ||
             effectiveArgs[0].Equals("mcp_server", StringComparison.OrdinalIgnoreCase));

        try
        {
            // If single argument is 'version', '-v', or '--version', display current version information directly
            if (effectiveArgs.Length == 1 && (effectiveArgs[0].Equals("version", StringComparison.OrdinalIgnoreCase) ||
                                              effectiveArgs[0].Equals("--version", StringComparison.OrdinalIgnoreCase) ||
                                              effectiveArgs[0].Equals("-v", StringComparison.OrdinalIgnoreCase)))
            {
                PrintVersionInfo();
                return 0;
            }

            // Explicit AI query routing (ONLY leading 'ask'/'ai'/'q'/'query'/'?'/'-?' or '?<word>' — never hijacks URLs containing '?')
            if (CliRuntimeContext.TryExtractAskQuestion(effectiveArgs, out string[] questionArgs))
            {
                string questionText = string.Join(" ", questionArgs);
                AnsiConsole.MarkupLine(
                    $"[bold purple]🤖 Asking Kongor AI:[/] [white]{Markup.Escape(questionText)}[/]\n");
                exitCode = RunPythonScript("tools/docgen/query_docs.py", questionArgs);
                return exitCode;
            }

            if (effectiveArgs.Length == 0)
            {
                PrintStatusDashboard();
                return 0;
            }

            CommandApp app = new();
            app.Configure(config =>
            {
                ConfigureCommands(config);
                config.PropagateExceptions();
            });

            string[] processedArgs = PreprocessArgs(effectiveArgs);
            exitCode = await app.RunAsync(processedArgs);
            return exitCode;
        }
        catch (KongorWorkspaceNotFoundException wsEx)
        {
            telemetryError = wsEx.Message;
            exitCode = 1;
            if (!isMcpStdio)
            {
                AnsiConsole.MarkupLine($"[bold red]✗ Workspace Error:[/] {Markup.Escape(wsEx.Message)}");
            }
            else
            {
                Console.Error.WriteLine($"[kongor] Workspace Error: {wsEx.Message}");
            }
            return exitCode;
        }
        catch (CommandParseException parseEx)
        {
            telemetryError = parseEx.Message;
            exitCode = 1;
            AnsiConsole.MarkupLine($"[bold red]Error:[/] {Markup.Escape(parseEx.Message)}");

            if (effectiveArgs.Length > 0 && !effectiveArgs[0].StartsWith('-'))
            {
                string? suggestion = CliRuntimeContext.SuggestClosestCommand(effectiveArgs[0], KnownPrimaryCommands);
                if (!string.IsNullOrWhiteSpace(suggestion))
                {
                    AnsiConsole.MarkupLine($"[yellow]Did you mean:[/] [bold cyan]kongor {Markup.Escape(suggestion)}[/]?");
                }
            }

            AnsiConsole.MarkupLine("[grey]Run [yellow]kongor --help[/] to list all available commands.[/]");
            return exitCode;
        }
        catch (CommandRuntimeException rtEx)
        {
            telemetryError = rtEx.Message;
            exitCode = 1;
            AnsiConsole.MarkupLine($"[bold red]Error:[/] {Markup.Escape(rtEx.Message)}");
            return exitCode;
        }
        catch (Exception ex)
        {
            telemetryError = ex.Message;
            exitCode = 1;
            if (isMcpStdio)
            {
                Console.Error.WriteLine($"[kongor] Unhandled error: {ex}");
            }
            else
            {
                AnsiConsole.MarkupLine($"[bold red]✗ Unhandled CLI error:[/] {Markup.Escape(ex.Message)}");
            }
            return exitCode;
        }
        finally
        {
            sw.Stop();
            CliTelemetryLogger.RecordInvocation(
                rawArgs: args,
                invocationCwd: CliRuntimeContext.InvocationCwd,
                workspace: CliRuntimeContext.LastResolvedWorkspace,
                durationMs: sw.ElapsedMilliseconds,
                exitCode: exitCode,
                errorMessage: telemetryError);
        }
    }

    internal static void ConfigureCommands(IConfigurator config)
    {
        config.SetApplicationName("kongor");
        config.SetInterceptor(new KongorLayoutGateInterceptor());

            // =========================================================================
            // 1. PRIMARY ECOSYSTEM APPLICATIONS & ALIASES
            // =========================================================================
            config.AddCommand<PlayCommand>("play")
                .WithAlias("launcher")
                .WithAlias("willowmaker")
                .WithDescription("Launch WILLOWMAKER from games/client (DXVK Vulkan 1.3, 1000 FPS, CDN Auto-Sync)");

            config.AddCommand<ClientCommand>("client")
                .WithAlias("game-client")
                .WithDescription("Launch, inspect, supervise, stop, and configure the player game client (hon_x64.exe)");

            config.AddCommand<HostCommand>("host")
                .WithAlias("compel")
                .WithAlias("server")
                .WithDescription("Run COMPEL from games/server (Win32 Sandbox, 1.0ms Timer, Anti-DDoS Proxy)");

            config.AddCommand<GuiCommand>("gui")
                .WithAlias("control-panel")
                .WithAlias("nexus")
                .WithAlias("admin")
                .WithDescription("Launch NEXUS Control Panel Desktop Admin GUI (Avalonia 11 / 3D Hero Viewport)");

            config.AddCommand<AspireCommand>("aspire")
                .WithAlias("cluster")
                .WithAlias("start")
                .WithDescription("Launch full 9-microservice .NET Aspire local cluster (https://localhost:5550)");

            config.AddCommand<DockerStackCommand>("docker")
                .WithAlias("stack")
                .WithAlias("docker-stack")
                .WithDescription("Launch full cluster, dedicated match servers & queue arcade in Docker (+ optional host client)");

            config.AddCommand<DockerSyncCommand>("sync")
                .WithAlias("docker-sync")
                .WithAlias("sync-docker")
                .WithAlias("retest-docker")
                .WithDescription("Full Docker stack sync: pre-flight -> publish & rebuild -> restart -> verify (alias for 'docker sync')");

            config.AddCommand<DockerRebuildCommand>("docker-rebuild")
                .WithAlias("rebuild-docker")
                .WithDescription("Re-publish linux-x64 binaries, rebuild Docker images, stop host conflicts & launch stack (alias for 'docker rebuild')");

            // =========================================================================
            // 2. BUILD & TEST COMMANDS
            // =========================================================================
            config.AddCommand<BuildCommand>("build")
                .WithAlias("compile")
                .WithAlias("rebuild")
                .WithDescription("Build the ProjectKongor ecosystem or specific projects (Debug, Release, or Both)");

            config.AddCommand<TestCommand>("test")
                .WithAlias("tests")
                .WithAlias("tunit")
                .WithAlias("xunit")
                .WithDescription("Run TUnit/xUnit test suites across ProjectKongor (default: --no-build)");

            config.AddCommand<PackageCommand>("package")
                .WithAlias("publish")
                .WithAlias("export")
                .WithDescription("Package cross-platform distribution bundles (willowmaker, compel, services, all)");

            // =========================================================================
            // 3. MICROSERVICES & STANDALONE DAEMONS
            // =========================================================================
            config.AddCommand<MasterServerCommand>("master-server")
                .WithAlias("master")
                .WithDescription("Run KONGOR Master Server standalone (SRP Authentication / EF Core)");

            config.AddCommand<ChatServerCommand>("chat-server")
                .WithAlias("chat")
                .WithDescription("Run TRANSMUTANSTEIN TCP Chat Server standalone (Ports 11031-11033)");

            config.AddCommand<WebApiCommand>("web-api")
                .WithAlias("api")
                .WithDescription("Run ZORGATH Web Portal REST API standalone (Ports 5200-5201)");

            config.AddCommand<WebUiCommand>("web-ui")
                .WithAlias("web")
                .WithDescription("Run DAWNBRINGER Web Portal MVC UI standalone (Ports 5266-5557)");

            config.AddCommand<StopCommand>("stop")
                .WithAlias("kill")
                .WithAlias("shutdown")
                .WithDescription("Stop running ProjectKongor .NET processes, background services, ComfyUI, or Blender");

            // =========================================================================
            // 4. MCP PROTOCOL & DIAGNOSTICS
            // =========================================================================
            config.AddCommand<McpCommand>("mcp")
                .WithAlias("mcp-server")
                .WithAlias("mcp_server")
                .WithDescription("Launch ProjectKongor native .NET Model Context Protocol (MCP) JSON-RPC 2.0 server over stdio transport");

            config.AddCommand<McpOutcomesCommand>("mcp-outcomes")
                .WithDescription("Ingest C# & Python MCP outcome NDJSON logs into Parquet and analyze reliability hotspots via DuckDB");

            config.AddCommand<CaptureCommand>("capture")
                .WithAlias("screenshot")
                .WithAlias("capture-window")
                .WithDescription("Capture live screenshot of game client, server, or desktop window to PNG");

            config.AddCommand<K2LintCommand>("k2-lint")
                .WithAlias("k2_lint")
                .WithAlias("lint-k2")
                .WithDescription("Fast offline static linter for K2 XML packages (.package, .interface) and Lua scripts (.lua)");

            config.AddCommand<LuaCheckCommand>("lua-check")
                .WithAlias("check-lua")
                .WithAlias("lua_check")
                .WithDescription("Phase 12 Whole-Codebase Lua Static Typecheck, EmmyLua Validation & Catalog Consistency Gate");

            config.AddCommand<UiPlaygroundCommand>("ui-playground")
                .WithAlias("ui")
                .WithAlias("playground")
                .WithDescription("Launch Browser-Based K2 UI Visual Editor & Hot-Reload Playground");

            config.AddCommand<EngineInspectCommand>("engine-inspect")
                .WithAlias("engine_inspect")
                .WithAlias("inspect-engine")
                .WithDescription("Lightweight PE binary symbol, export table, Lua CFunction, and CVar introspector");

            config.AddCommand<NativeCommand>("native")
                .WithAlias("native-manage")
                .WithAlias("native-security")
                .WithDescription("Inspect, verify, upgrade, and rollback native game C++ libraries (e.g. zlib 1.3.1)");

            config.AddCommand<PatchCommand>("patch")
                .WithAlias("patch-engine")
                .WithAlias("engine-patch")
                .WithDescription("Machine-code crash patching, 1000 FPS unclamp, and PE Import Address Table (IAT) redirection");

            config.AddCommand<VulkanCommand>("vulkan")
                .WithAlias("dxvk")
                .WithDescription("Inspect, verify, and upgrade game client and dedicated servers to Vulkan 1.3 / DXVK");

            config.AddCommand<SecurityCommand>("security")
                .WithAlias("tls")
                .WithAlias("crypto")
                .WithAlias("ssl")
                .WithDescription("Modern TLS 1.3 network security, libcurl 8.8+ upgrade, OpenSSL ABI proxy shim, and CVE remediation");

            config.AddCommand<GuardCommand>("guard")
                .WithAlias("detours")
                .WithAlias("minhook")
                .WithAlias("injection")
                .WithDescription("Native runtime supervision, Detours/MinHook guard layer, and 64-bit ±2 GB RIP trampoline inspection");

            config.AddCommand<ServerCommand>("server-fleet")
                .WithAlias("server-manage")
                .WithAlias("dedicated")
                .WithAlias("dedicated-server")
                .WithDescription("Dedicated server fleet security governance and headless audio null stub management");

            config.AddCommand<ArchiveCommand>("archive")
                .WithAlias("vfs")
                .WithAlias("s2z")
                .WithAlias("compression")
                .WithDescription("Phase 7 Fast VFS, Zstandard (Zstd) transparent stream decompression, and AVX2 Deflate acceleration");

            config.AddCommand<RlCommand>("rl")
                .WithAlias("bot-rl")
                .WithAlias("self-play")
                .WithDescription("Manage Queue Arcade RL Self-Play mode, AWAC training, Champion/Candidate arena, and Lua weight export");

            config.AddCommand<AudioCommand>("audio")
                .WithAlias("wasapi")
                .WithAlias("spatial-audio")
                .WithAlias("hrtf")
                .WithDescription("Phase 8 Client Low-Latency WASAPI & 3D Spatial HRTF Audio Modernization");

            config.AddCommand<VideoCommand>("video")
                .WithAlias("ffmpeg")
                .WithAlias("video-pipeline")
                .WithDescription("Phase 9 Hardware-Accelerated Video Decoding & Dedicated Server Bloat Purge");

            config.AddCommand<MemoryCommand>("memory")
                .WithAlias("mimalloc")
                .WithAlias("allocator")
                .WithAlias("heap")
                .WithDescription("Track 1 Client Memory Allocator Modernization (Microsoft mimalloc Dynamic Override)");

            config.AddCommand<InputCommand>("input")
                .WithAlias("raw-input")
                .WithAlias("mouse")
                .WithDescription("Track 2 High-Polling-Rate Esports Mouse Modernization (Windows Raw Input 8000Hz)");

            config.AddCommand<ReplayCommand>("replay")
                .WithAlias("replays")
                .WithAlias("render-replay")
                .WithAlias("render")
                .WithAlias("replay-mp4")
                .WithDescription("Cinematic Replay-to-MP4 Renderer & Teamfight Highlight Clipper");

            config.AddCommand<HighlightCommand>("highlight")
                .WithAlias("highlights")
                .WithAlias("teamfight")
                .WithDescription("Automated game-winning teamfight highlight clip generator");

            // =========================================================================
            // 4.5. UNIFIED MCP ORCHESTRATION & CONTENT FORGE (PHASE 4)
            // =========================================================================
            config.AddCommand<TriageCommand>("triage")
                .WithAlias("crash-triage")
                .WithAlias("resuscitate")
                .WithDescription("Autonomous crash dump analysis, Ghidra symbol triangulation, and process resuscitation");

            config.AddCommand<HealCommand>("heal")
                .WithAlias("self-heal")
                .WithAlias("reconcile")
                .WithDescription("Automated Lua/XML AST repairs, dedicated server netcode audit, and Redis lobby reconciliation");

            config.AddCommand<ForgeCommand>("forge")
                .WithAlias("content-forge")
                .WithAlias("create-content")
                .WithDescription("Multi-modal content forge dispatcher (hero, skin, taunt, announcer, icon, symbol)");

            config.AddCommand<ScrimmageCommand>("scrimmage")
                .WithAlias("bot-match")
                .WithAlias("headless-match")
                .WithDescription("Headless dedicated server scrimmage orchestration, deterministic bot injection, and UDP netcode audit");

            config.AddCommand<WatchCommand>("watch")
                .WithAlias("asset-watch")
                .WithAlias("hot-watch")
                .WithDescription("Real-time asset supervision, sliding debounce, atomic file swap (MoveFileEx), and hot reload");

            // =========================================================================
            // 4.6. PHASE 5: AUTONOMOUS CLIENT TELEMETRY & AUDITING
            // =========================================================================
            config.AddCommand<ClientStatsAuditCommand>("audit")
                .WithAlias("audit-client")
                .WithAlias("verify-ui")
                .WithAlias("audit-stats")
                .WithDescription("Autonomous game client telemetry, stats & match history discrepancy auditor, and self-healing issue logger");

            config.AddCommand<VfsAuditCommand>("vfs-audit")
                .WithAlias("audit-assets")
                .WithAlias("audit-vfs")
                .WithAlias("check-assets")
                .WithAlias("vfs-check")
                .WithDescription("Pre-flight static VFS asset completeness auditor: detects silent white dummy textures, pink checkerboards, and Utah Teapots");

            config.AddCommand<VfsHydrateCommand>("vfs-hydrate")
                .WithAlias("hydrate-textures")
                .WithAlias("hydrate-vfs")
                .WithAlias("un-trap-textures")
                .WithDescription("Un-traps UI, shared effects, item icons, and hero ability textures from textures.s2z into resources99.s2z");

            config.AddCommand<TelemetryCommand>("telemetry")
                .WithAlias("metrics")
                .WithAlias("telemetry-stream")
                .WithDescription("Real-time dual-plane in-game telemetry stream, QPC frame latencies, and shared memory stats");

            config.AddCommand<AnalyticsCommand>("analytics")
                .WithAlias("match-analytics")
                .WithAlias("lakehouse")
                .WithDescription("Direct in-process DuckDB telemetry lakehouse SQL analytics, scorecards & spatial heatmaps");

            config.AddCommand<WalCommand>("wal")
                .WithAlias("replay-wal")
                .WithAlias("recover-wal")
                .WithDescription("Inspect Write-Ahead Log (WAL) journals and recover competitive match replays");

            config.AddCommand<DeployCommand>("deploy")
                .WithAlias("k3s")
                .WithAlias("k8s")
                .WithDescription("Inspect Kubernetes (K3s), Docker status and orchestrate Agones dedicated server fleet");

            // =========================================================================
            // 5. ECOSYSTEM, MODEL EXPORT, BLENDER, COMFYUI & DOCS
            // =========================================================================
            config.AddCommand<ModelCommand>("model")
                .WithAlias("models")
                .WithAlias("model_exporter")
                .WithAlias("model-exporter")
                .WithAlias("model-tools")
                .WithAlias("model-export")
                .WithDescription("HoN 3D Model, Skeleton & Animation Exporter (glTF/GLB for Blender 4.2+)");

            config.AddCommand<MixamoCommand>("mixamo")
                .WithAlias("mixamo-export")
                .WithAlias("rig")
                .WithDescription("Export character model as Adobe Mixamo auto-rig ready unrigged OBJ/ZIP package");

            config.AddCommand<BlenderCommand>("blender")
                .WithAlias("launch-blender")
                .WithAlias("open-blender")
                .WithDescription("Launch Blender 3D (v5.2+ / v4.2 LTS), or manage lifecycle ('blender install', 'blender uninstall', 'blender stop')");

            config.AddCommand<InstallBlenderCommand>("install_blender")
                .WithAlias("install-blender")
                .WithAlias("setup-blender")
                .WithDescription("Download and configure portable Blender 4.2+ LTS and io_scene_hon addon");

            config.AddCommand<UninstallBlenderCommand>("uninstall_blender")
                .WithAlias("uninstall-blender")
                .WithDescription("Uninstall portable Blender and remove downloaded archives");

            config.AddCommand<ComfyCommand>("comfyui")
                .WithAlias("comfy")
                .WithAlias("launch-comfyui")
                .WithDescription("Launch ComfyUI generative AI server on port 8188, or manage lifecycle ('comfy install', 'comfy uninstall', 'comfy stop')");

            config.AddCommand<InstallComfyCommand>("install_comfyui")
                .WithAlias("install-comfyui")
                .WithAlias("setup-comfyui")
                .WithDescription("Download, configure ComfyUI, PyTorch acceleration, and ComfyUI-Manager");

            config.AddCommand<UninstallComfyCommand>("uninstall_comfyui")
                .WithAlias("uninstall-comfyui")
                .WithDescription("Uninstall ComfyUI and remove dedicated virtual environment");

            config.AddCommand<ComfyMcpCommand>("comfyui_mcp")
                .WithAlias("comfy-mcp")
                .WithAlias("comfymcp")
                .WithDescription("Run ComfyUI Model Context Protocol (MCP) server for AI agent integration");

            config.AddCommand<DocsCommand>("docs")
                .WithAlias("doc")
                .WithAlias("docfx")
                .WithDescription("Launch DocFX developer documentation hub (http://localhost:55590)");

            config.AddCommand<RefreshCommand>("refresh")
                .WithAlias("install")
                .WithAlias("install-cli")
                .WithAlias("setup")
                .WithAlias("reinstall")
                .WithAlias("self-update")
                .WithAlias("update-cli")
                .WithDescription("Install or update the 'kongor' CLI onto your user PATH (standalone bundle or source SDK)");

            config.AddCommand<AskCommand>("ask")
                .WithAlias("ai")
                .WithAlias("?")
                .WithAlias("q")
                .WithAlias("query")
                .WithDescription("Ask natural language questions to KONGOR AI (RAG codebase & architecture search)");

            config.AddCommand<VersionCommand>("version")
                .WithAlias("ver")
                .WithDescription("Display current ecosystem version information or synchronize to a new version");

            config.AddCommand<SyncVersionCommand>("sync-version")
                .WithAlias("sync-versions")
                .WithAlias("set-version")
                .WithDescription("Synchronize platform version (SemVer 5.0.0), CalVer build date & engine protocol across all projects & mod manifests");

            // =========================================================================
            // 6. TOOLS BRANCH SUB-ROUTER
            // =========================================================================
            config.AddBranch("tools", tools =>
            {
                tools.SetDescription("Unified developer and ecosystem tooling sub-router");
                tools.AddCommand<ToolsDocgenCommand>("docgen")
                    .WithDescription("Generate architecture and decision funnel SVG diagrams");
                tools.AddCommand<ToolsBlenderCommand>("blender")
                    .WithDescription("Launch Blender 3D (v5.2+ / v4.2 LTS) or open .blend scene");
                tools.AddCommand<ToolsE2ECommand>("test-e2e")
                    .WithDescription("Execute 4-tier E2E verification test harness");
            });

            // =========================================================================
            // 7. MOD BRANCH (MOD-CLI)
            // =========================================================================
            Action<IConfigurator<CommandSettings>> configureMod = mod =>
            {
                mod.SetDescription("HoN mod package manager (.honmod/.s2z), DAG conflict resolver, AST normalizer");
                mod.AddCommand<ModListCommand>("list")
                    .WithDescription("List all discovered mods with enabled/disabled status and metadata");
                mod.AddCommand<ModEnableCommand>("enable")
                    .WithDescription("Enable one or more mods");
                mod.AddCommand<ModDisableCommand>("disable")
                    .WithDescription("Disable one or more mods (or 'all' to disable all active mods)");
                mod.AddCommand<ModInspectCommand>("inspect")
                    .WithDescription("Inspect detailed manifest, dependencies, and target files of a mod");
                mod.AddCommand<ModApplyCommand>("apply")
                    .WithDescription("Apply all enabled mods with DAG ordering to game client (vfs or archive)");
                mod.AddCommand<ModRollbackCommand>("rollback")
                    .WithDescription("Revert all applied modifications cleanly using rollback journal");
                mod.AddCommand<ModVerifyCommand>("verify")
                    .WithDescription("Run dependency checking, cycle detection, and collision matrix tests");
                mod.AddCommand<ModHarvestCommand>("harvest")
                    .WithDescription("Harvest community mods and metadata from Trello catalog");
                mod.AddCommand<ModRepairCommand>("repair")
                    .WithDescription("Repair corrupted XML/Lua AST entities in mods");
                mod.AddCommand<ModPackCommand>("pack")
                    .WithAlias("package")
                    .WithAlias("bundle")
                    .WithDescription("Package loose overrides or mod directory into a clean single-layer archive (.s2z or .honmod)");
            };
            config.AddBranch("mod", configureMod);
            config.AddBranch("mod-cli", configureMod);

            // =========================================================================
            // 8. PREBAKE BRANCH (PREBAKE-TOOL)
            // =========================================================================
            Action<IConfigurator<CommandSettings>> configurePrebake = prebake =>
            {
                prebake.SetDescription("Entity XML harvesting & MemoryPack+Zstd binary cache generator");
                prebake.AddCommand<PrebakeAllCommand>("all")
                    .WithDescription("Execute full pre-baking pipeline (parse, serialize, lua cache, s2z bundle, stage)");
                prebake.AddCommand<PrebakeEntitiesCommand>("entities")
                    .WithDescription("Parse game entity XMLs and serialize to entity_cache.bin (MemoryPack+Zstd)");
                prebake.AddCommand<PrebakeLuaCommand>("lua")
                    .WithDescription("Generate hero_definitions_cache.lua for instant client UI resolution");
                prebake.AddCommand<PrebakeBundleCommand>("bundle")
                    .WithDescription("Package pre-baked assets into resources0_prebaked.s2z archive");
            };
            config.AddBranch("prebake", configurePrebake);
            config.AddBranch("prebake-tool", configurePrebake);

            // =========================================================================
            // 9. DEVELOPER UTILITIES, VERIFIERS & DIAGNOSTICS
            // =========================================================================
            config.AddCommand<EndpointAuditCommand>("audit-endpoints")
                .WithAlias("endpoint-auditor")
                .WithDescription("Extract Lua/XML SubmitForm routes & reconcile API endpoints");

            config.AddCommand<LuaCommand>("lua-verify")
                .WithAlias("lua-verifier")
                .WithAlias("lua")
                .WithDescription("Headless NLua sandbox mocking K2 UI widgets, Cvars & PHP/Base64 serialization");

            config.AddCommand<K2Command>("k2-test")
                .WithAlias("k2-field-test")
                .WithAlias("k2")
                .WithDescription("Headless K2 engine & cvar simulator (consolidated into lua-verifier)");

            config.AddCommand<DoctorCommand>("doctor")
                .WithAlias("check")
                .WithDescription("Perform system environment, port availability, and health diagnostics");

            config.AddCommand<StatusCommand>("status")
                .WithAlias("info")
                .WithDescription("Show ProjectKongor ecosystem topology, running services, ports & health");

            config.AddCommand<CleanCommand>("clean")
                .WithAlias("purge")
                .WithAlias("deep-clean")
                .WithDescription("Deep-clean all bin/, obj/, TestResults/, logs, crash dumps, and temporary build caches");

            config.AddCommand<SecretsCommand>("secret")
                .WithAlias("secrets")
                .WithAlias("config")
                .WithDescription("Manage the centralized User Secrets vault (00005072-6f6a-6563-7420-4b4f4e474f52)");

            config.AddCommand<LogsCommand>("logs")
                .WithAlias("log")
                .WithAlias("tail-logs")
                .WithDescription("Inspect and live-stream log files from background microservices or dedicated servers");

            // =========================================================================
            // 10. STANDALONE CLUSTER MANAGEMENT (ACCOUNTS & INVITES)
            // =========================================================================
            config.AddBranch("account", account =>
            {
                account.SetDescription("Manage standalone player accounts");
                account.AddCommand<AccountCreateCommand>("create")
                    .WithDescription("Create a standalone player account via ZORGATH direct registration");
            });

            config.AddBranch("invite", invite =>
            {
                invite.SetDescription("Manage standalone player invite codes");
                invite.AddCommand<InviteCreateCommand>("create")
                    .WithDescription("Generate standalone admin bootstrap invite codes and append to invite_codes.txt");
            });

            // =========================================================================
            // 11. LOCATION-INDEPENDENT WORKSPACE & INSTALLATION MANAGEMENT
            // =========================================================================
            config.AddCommand<WhereCommand>("where")
                .WithDescription("Display the resolved ProjectKongor workspace root, layout kind, and resolution tier");

            Action<IConfigurator<CommandSettings>> configureWorkspace = ws =>
            {
                ws.SetDescription("Manage registered ProjectKongor workspaces & standalone bundles (~/.kongor/config.json)");
                ws.AddCommand<WhereCommand>("current")
                    .WithDescription("Show the currently resolved workspace root and source");
                ws.AddCommand<WorkspaceListCommand>("list")
                    .WithAlias("ls")
                    .WithDescription("List all registered ProjectKongor workspaces and standalone bundles");
                ws.AddCommand<WorkspaceUseCommand>("use")
                    .WithAlias("set")
                    .WithDescription("Register and activate a ProjectKongor workspace path or alias");
                ws.AddCommand<WorkspaceAddCommand>("add")
                    .WithDescription("Register a ProjectKongor workspace path without switching the active workspace");
                ws.AddCommand<WorkspaceRemoveCommand>("remove")
                    .WithAlias("rm")
                    .WithDescription("Unregister a workspace alias or path from ~/.kongor/config.json");
                ws.AddCommand<WorkspacePruneCommand>("prune")
                    .WithDescription("Remove deleted or invalid workspace entries from ~/.kongor/config.json");
            };
            config.AddBranch("workspace", configureWorkspace);
            config.AddBranch("ws", configureWorkspace);

            config.AddCommand<CompletionCommand>("completion")
                .WithAlias("completions")
                .WithDescription("Generate or install instant (<2ms) static shell tab-completion scripts (powershell, bash, zsh, fish)");

            // =========================================================================
            // 12. DEVELOPER INNER-LOOP, QA, SECURITY & MIGRATION PIPELINE (PHASE 4 PARITY)
            // =========================================================================
            config.AddCommand<CoverageCommand>("coverage")
                .WithAlias("test-coverage")
                .WithData(KongorLayoutSupport.SourceWorkspaceOnly)
                .WithDescription("Run test suite with XPlat Code Coverage & ReportGenerator HTML/Markdown summary");

            config.AddCommand<GateCommand>("gate")
                .WithAlias("gate-check")
                .WithAlias("gatecheck")
                .WithData(KongorLayoutSupport.SourceWorkspaceOnly)
                .WithDescription("Execute sub-5-second inner-loop quality gate (BannedSymbols, Lua, XML, NativeGuard)");

            config.AddCommand<OracleCommand>("oracle")
                .WithAlias("netcode-oracle")
                .WithAlias("verify-netcode")
                .WithData(KongorLayoutSupport.SourceWorkspaceOnly)
                .WithDescription("Execute Netcode Oracle verification (x64 MXCSR float determinism & sub-tick sorting)");

            config.AddCommand<PracticeCommand>("practice")
                .WithAlias("screen-practice")
                .WithAlias("launch-practice")
                .WithData(KongorLayoutSupport.SourceWorkspaceOnly)
                .WithDescription("Launch Caldavar practice mode with live DevTools & modern HUD");

            config.AddCommand<InjectCommand>("inject")
                .WithAlias("inject-s2z")
                .WithAlias("inject-override")
                .WithData(KongorLayoutSupport.SourceWorkspaceOnly)
                .WithDescription("Atomically inject override/ assets into target .s2z archive with pre-flight guards");

            config.AddCommand<DecompileCommand>("decompile")
                .WithAlias("decompile-bytecode")
                .WithData(KongorLayoutSupport.SourceWorkspaceOnly)
                .WithDescription("Decompile legacy Lua 5.1 bytecode blobs into clean plain-text Lua 5.4");

            config.AddCommand<SanitizeCommand>("sanitize")
                .WithAlias("sanitize-workspace")
                .WithData(KongorLayoutSupport.SourceWorkspaceOnly)
                .WithDescription("Quarantine loose scratch scripts, textures, test logs and leaked build artifacts");

            config.AddCommand<SyncLuaCommand>("sync-lua")
                .WithAlias("sync-override")
                .WithData(KongorLayoutSupport.SourceWorkspaceOnly)
                .WithDescription("Synchronize override Lua definitions to shared and client trees");

            config.AddCommand<SbomCommand>("sbom")
                .WithAlias("cyclonedx")
                .WithData(KongorLayoutSupport.SourceWorkspaceOnly)
                .WithDescription("Generate CycloneDX Software Bill of Materials (artifacts/sbom.json)");

            config.AddCommand<ScanCommand>("scan")
                .WithAlias("vuln")
                .WithAlias("vulnerabilities")
                .WithAlias("security-scan")
                .WithData(KongorLayoutSupport.SourceWorkspaceOnly)
                .WithDescription("Scan solution for vulnerable NuGet packages & emit SARIF 2.1.0");

            config.AddCommand<SoakCommand>("soak")
                .WithAlias("endurance")
                .WithData(KongorLayoutSupport.SourceWorkspaceOnly)
                .WithDescription("Run endurance & memory leak soak test");

            config.AddCommand<HealthCommand>("health")
                .WithAlias("healthcheck")
                .WithAlias("health-check")
                .WithDescription("Query live health check diagnostics (/health) across microservices");

            config.AddCommand<MigrateCommand>("migrate")
                .WithAlias("migration")
                .WithAlias("migrations")
                .WithAlias("db-migrate")
                .WithData(KongorLayoutSupport.SourceWorkspaceOnly)
                .WithDescription("Manage EF Core database migrations (list, preview, apply, rollback, add)");

            config.AddCommand<TailwindCommand>("tailwind")
                .WithAlias("tw")
                .WithAlias("css")
                .WithData(KongorLayoutSupport.SourceWorkspaceOnly)
                .WithDescription("Tailwind CSS v4 pipeline (build, watch, dev, setup)");

            config.AddCommand<InstallHooksCommand>("install-hooks")
                .WithAlias("hooks")
                .WithAlias("setup-hooks")
                .WithData(KongorLayoutSupport.SourceWorkspaceOnly)
                .WithDescription("Install Project Kongor Git pre-push quality gate hooks");

            config.AddCommand<AuditAllCommand>("audit-all")
                .WithAlias("test-all")
                .WithAlias("audit-suite")
                .WithData(KongorLayoutSupport.SourceWorkspaceOnly)
                .WithDescription("Run automated smoke audit across all developer CLI tools");

            config.AddCommand<TestE2ECommand>("test-e2e")
                .WithAlias("e2e")
                .WithAlias("test-suite")
                .WithData(KongorLayoutSupport.SourceWorkspaceOnly)
                .WithDescription("Execute 4-tier E2E verification test suite (tests/e2e/run_all.ps1)");

            config.AddCommand<DocgenCommand>("docgen")
                .WithAlias("charts")
                .WithAlias("generate-charts")
                .WithData(KongorLayoutSupport.SourceWorkspaceOnly)
                .WithDescription("Generate architecture and decision funnel SVG diagrams");
    }

    private static string[] PreprocessArgs(string[] args)
    {
        if (args.Length <= 1)
        {
            return args;
        }

        string cmd = args[0];
        if (RawForwardingCommands.Contains(cmd) && !args.Contains("--"))
        {
            string firstArg = args[1];
            if (!firstArg.Equals("--help", StringComparison.OrdinalIgnoreCase) &&
                !firstArg.Equals("-h", StringComparison.OrdinalIgnoreCase) &&
                !firstArg.Equals("-?", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Skip(1).Any(a => a.StartsWith('-')))
                {
                    return [cmd, "--", .. args.Skip(1)];
                }
            }
        }

        return args;
    }
}
