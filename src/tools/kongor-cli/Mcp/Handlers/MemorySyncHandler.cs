// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Kongor.Cli.Mcp.Protocol;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// Conforms to @modelcontextprotocol/server-memory entity schema.
/// </summary>
public sealed class MemoryEntity
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("entityType")]
    public string EntityType { get; set; } = string.Empty;

    [JsonPropertyName("observations")]
    public List<string> Observations { get; set; } = [];
}

/// <summary>
/// Conforms to @modelcontextprotocol/server-memory relation schema.
/// </summary>
public sealed class MemoryRelation
{
    [JsonPropertyName("from")]
    public string From { get; set; } = string.Empty;

    [JsonPropertyName("to")]
    public string To { get; set; } = string.Empty;

    [JsonPropertyName("relationType")]
    public string RelationType { get; set; } = string.Empty;
}

/// <summary>
/// Structured container for the project knowledge graph conforming to @modelcontextprotocol/server-memory.
/// </summary>
public sealed class MemoryGraphPayload
{
    [JsonPropertyName("entities")]
    public List<MemoryEntity> Entities { get; set; } = [];

    [JsonPropertyName("relations")]
    public List<MemoryRelation> Relations { get; set; } = [];
}

/// <summary>
/// MCP Tool Handler for 'kongor_memory_sync' (alias: 'memory_sync').
/// Mines repository truth (AGENTS.md, docs/ISSUES.md, port mappings, engine invariants)
/// into a structured knowledge graph conforming to @modelcontextprotocol/server-memory.
/// </summary>
public sealed class MemorySyncHandler
{
    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _logger;
    private readonly string _workspaceRoot;

    public MemorySyncHandler(
        TimeProvider? timeProvider = null,
        Action<string>? logger = null,
        string? workspaceRoot = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? (msg => Console.Error.WriteLine($"[MemorySync] {msg}"));
        _workspaceRoot = workspaceRoot ?? Directory.GetCurrentDirectory();
    }

    public async Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        Stopwatch sw = Stopwatch.StartNew();

        string action = "extract";
        if (arguments.TryGetProperty("action", out JsonElement actEl) && !string.IsNullOrWhiteSpace(actEl.GetString()))
        {
            action = actEl.GetString()!.Trim().ToLowerInvariant();
        }

        string? query = null;
        if (arguments.TryGetProperty("query", out JsonElement qEl) && !string.IsNullOrWhiteSpace(qEl.GetString()))
        {
            query = qEl.GetString()!.Trim();
        }

        string? entityTypeFilter = null;
        if (arguments.TryGetProperty("entity_type", out JsonElement etEl) && !string.IsNullOrWhiteSpace(etEl.GetString()))
        {
            entityTypeFilter = etEl.GetString()!.Trim();
        }
        else if (arguments.TryGetProperty("entityType", out etEl) && !string.IsNullOrWhiteSpace(etEl.GetString()))
        {
            entityTypeFilter = etEl.GetString()!.Trim();
        }

        string? outputPath = null;
        if (arguments.TryGetProperty("output_path", out JsonElement outEl) && !string.IsNullOrWhiteSpace(outEl.GetString()))
        {
            outputPath = outEl.GetString()!.Trim();
        }
        else if (arguments.TryGetProperty("outputPath", out outEl) && !string.IsNullOrWhiteSpace(outEl.GetString()))
        {
            outputPath = outEl.GetString()!.Trim();
        }

        _logger?.Invoke($"Executing action '{action}' (query: '{query ?? "none"}', filter: '{entityTypeFilter ?? "none"}')");

        // 1. Build authoritative memory graph
        MemoryGraphPayload graph = BuildAuthoritativeGraph();

        // 2. Process requested action
        switch (action)
        {
            case "status":
            {
                sw.Stop();
                var statusData = new
                {
                    action = "status",
                    entityCount = graph.Entities.Count,
                    relationCount = graph.Relations.Count,
                    entityTypes = graph.Entities.Select(e => e.EntityType).Distinct().OrderBy(t => t).ToList(),
                    workspaceRoot = _workspaceRoot,
                    timestamp = _timeProvider.GetUtcNow().ToString("o")
                };

                return ExecutionTruthEnvelope.CreateJsonResult(
                    status: "success",
                    executionMode: ExecutionMode.OfflineDirect,
                    targetStatus: null,
                    metrics: new ExecutionMetrics
                    {
                        RoundtripLatencyMs = sw.Elapsed.TotalMilliseconds,
                        Timestamp = _timeProvider.GetUtcNow().ToString("o")
                    },
                    data: statusData);
            }

            case "query":
            {
                var filteredEntities = graph.Entities.AsEnumerable();
                if (!string.IsNullOrEmpty(entityTypeFilter))
                {
                    filteredEntities = filteredEntities.Where(e =>
                        string.Equals(e.EntityType, entityTypeFilter, StringComparison.OrdinalIgnoreCase));
                }

                if (!string.IsNullOrEmpty(query))
                {
                    filteredEntities = filteredEntities.Where(e =>
                        e.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        e.Observations.Any(o => o.Contains(query, StringComparison.OrdinalIgnoreCase)));
                }

                var matchedEntities = filteredEntities.ToList();
                var matchedNames = new HashSet<string>(matchedEntities.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);

                var matchedRelations = graph.Relations.Where(r =>
                    matchedNames.Contains(r.From) || matchedNames.Contains(r.To)).ToList();

                sw.Stop();
                var queryData = new
                {
                    action = "query",
                    query = query ?? string.Empty,
                    entityTypeFilter = entityTypeFilter ?? string.Empty,
                    matchedEntityCount = matchedEntities.Count,
                    matchedRelationCount = matchedRelations.Count,
                    entities = matchedEntities,
                    relations = matchedRelations
                };

                return ExecutionTruthEnvelope.CreateJsonResult(
                    status: "success",
                    executionMode: ExecutionMode.OfflineDirect,
                    targetStatus: null,
                    metrics: new ExecutionMetrics
                    {
                        RoundtripLatencyMs = sw.Elapsed.TotalMilliseconds,
                        Timestamp = _timeProvider.GetUtcNow().ToString("o")
                    },
                    data: queryData);
            }

            case "export":
            {
                string targetFile = string.IsNullOrEmpty(outputPath)
                    ? Path.Combine(_workspaceRoot, "artifacts", "memory", "kongor_memory_graph.json")
                    : Path.IsPathRooted(outputPath) ? outputPath : Path.Combine(_workspaceRoot, outputPath);

                string? dir = Path.GetDirectoryName(targetFile);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
                string serialized = JsonSerializer.Serialize(graph, jsonOptions);
                await File.WriteAllTextAsync(targetFile, serialized, cancellationToken);
                _logger?.Invoke($"Exported knowledge graph to '{targetFile}' ({serialized.Length} bytes)");

                sw.Stop();

                var handoffs = new List<ToolHandoff>
                {
                    ToolHandoff.Create(
                        server: "memory",
                        tool: "create_entities",
                        reason: "Seed extracted ProjectKongor invariant entities into active memory",
                        arguments: new { entities = graph.Entities },
                        path: targetFile),
                    ToolHandoff.Create(
                        server: "memory",
                        tool: "create_relations",
                        reason: "Seed extracted ProjectKongor subsystem relations into active memory",
                        arguments: new { relations = graph.Relations },
                        path: targetFile)
                };

                var exportData = new
                {
                    action = "export",
                    exportedPath = targetFile,
                    fileSizeBytes = new FileInfo(targetFile).Length,
                    entityCount = graph.Entities.Count,
                    relationCount = graph.Relations.Count,
                    handoffCount = handoffs.Count
                };

                return ExecutionTruthEnvelope.CreateJsonResult(
                    status: "success",
                    executionMode: ExecutionMode.OfflineDirect,
                    targetStatus: null,
                    metrics: new ExecutionMetrics
                    {
                        RoundtripLatencyMs = sw.Elapsed.TotalMilliseconds,
                        Timestamp = _timeProvider.GetUtcNow().ToString("o")
                    },
                    data: exportData,
                    isError: false,
                    handoffs: handoffs);
            }

            case "extract":
            default:
            {
                sw.Stop();
                var handoffs = new List<ToolHandoff>
                {
                    ToolHandoff.Create(
                        server: "memory",
                        tool: "create_entities",
                        reason: "Seed authoritative ProjectKongor entities into persistent memory",
                        arguments: new { entities = graph.Entities })
                };

                var extractData = new
                {
                    action = "extract",
                    entityCount = graph.Entities.Count,
                    relationCount = graph.Relations.Count,
                    entities = graph.Entities,
                    relations = graph.Relations
                };

                return ExecutionTruthEnvelope.CreateJsonResult(
                    status: "success",
                    executionMode: ExecutionMode.OfflineDirect,
                    targetStatus: null,
                    metrics: new ExecutionMetrics
                    {
                        RoundtripLatencyMs = sw.Elapsed.TotalMilliseconds,
                        Timestamp = _timeProvider.GetUtcNow().ToString("o")
                    },
                    data: extractData,
                    isError: false,
                    handoffs: handoffs);
            }
        }
    }

    public MemoryGraphPayload BuildAuthoritativeGraph()
    {
        var entities = new List<MemoryEntity>();
        var relations = new List<MemoryRelation>();

        // 1. Subsystems
        entities.Add(new MemoryEntity
        {
            Name = "TAO",
            EntityType = "Subsystem",
            Observations =
            [
                "Together as One (TAO) is the ProjectKongor MCP server unifying client, dedicated servers, Aspire, and AI pair tools.",
                "Server identifier in mcp_config.json is 'kongor', exposing 75 canonical tools and aliases under jsonrpc 2.0."
            ]
        });

        entities.Add(new MemoryEntity
        {
            Name = "K2_Engine",
            EntityType = "Subsystem",
            Observations =
            [
                "Custom 64-bit multi-threaded game engine written in C++ (hon_x64.exe, k2_x64.dll, cgame_x64.dll).",
                "Uses virtual file system (VFS) with mount priorities (override -> extensions -> updates -> configuration -> game -> shared -> base)."
            ]
        });

        entities.Add(new MemoryEntity
        {
            Name = "Dedicated_Server",
            EntityType = "Subsystem",
            Observations =
            [
                "Headless worker process spawned as 'hon_x64.exe -dedicated'.",
                "Requires listen port parameter (e.g. port: 11235) for routing console commands and cvar adjustments.",
                "Dual channel execution: console stdin redirected stream and named pipe \\\\.\\pipe\\hon-server-{port}."
            ]
        });

        entities.Add(new MemoryEntity
        {
            Name = "Willowmaker",
            EntityType = "Subsystem",
            Observations =
            [
                "Native detour and instrumentation layer injecting into hon_x64.exe for low-level memory inspection and IPC.",
                "Exposes named pipe \\\\.\\pipe\\willowmaker-ipc."
            ]
        });

        entities.Add(new MemoryEntity
        {
            Name = "Aspire_Cluster",
            EntityType = "Subsystem",
            Observations =
            [
                ".NET 11 Aspire cloud application model orchestrating Auth, Matchmaking, Replay Studio, and Web services.",
                "Services include Web API on port 5200, Web UI on port 5266, and Redis cache on port 28298."
            ]
        });

        // 2. Engine Invariants (Zero-Hallucination Guardrails)
        entities.Add(new MemoryEntity
        {
            Name = "UIManager",
            EntityType = "K2_Lua_Global",
            Observations =
            [
                "Global UI manager in K2 engine. Never use 'Windows' (Windows is undefined in K2 Lua).",
                "Correct invocation: UIManager.GetInterface('main'):GetWidget('widget_name')."
            ]
        });

        entities.Add(new MemoryEntity
        {
            Name = "CWidget_DoEvent",
            EntityType = "Engine_Invariant",
            Observations =
            [
                "widget:DoEvent() takes NO parameters. It triggers the XML 'onevent' attribute.",
                "widget:DoEventN(index) takes an integer index (0, 1, 2) to trigger onevent0, onevent1, etc.",
                "Never pass strings like 'onclick' to DoEvent; in K2, onclick is an input action or callback (SetCallback('onclick', fn))."
            ]
        });

        entities.Add(new MemoryEntity
        {
            Name = "Headless_Window_Protocol",
            EntityType = "Engine_Invariant",
            Observations =
            [
                "The game client (hon_x64.exe) runs in headless / offscreen mode (+Set vid_pos -32000 -32000) during automated testing.",
                "Never leave the game client window visible or stealing focus on the user's desktop outside of brief transient capture moments."
            ]
        });

        entities.Add(new MemoryEntity
        {
            Name = "Dedicated_Server_Port_Routing",
            EntityType = "Engine_Invariant",
            Observations =
            [
                "Commands targeting dedicated servers MUST include the target listen port (e.g. port: 11235).",
                "Never broadcast dedicated server commands to a generic unnamed pipe."
            ]
        });

        entities.Add(new MemoryEntity
        {
            Name = "Inner_Loop_Test_Gating",
            EntityType = "Engine_Invariant",
            Observations =
            [
                "Any test executing longer than 2.0 seconds, stress tests (>= 1,000 iterations), or hardware tests must be gated with NexusTestEnvironment.IsManualTestEnabled.",
                "Tests must never spawn external interactive desktop GUI windows (e.g. Process.Start('ReplayStudio.GUI.exe'))."
            ]
        });

        // 3. Canonical Port Architecture
        entities.Add(new MemoryEntity
        {
            Name = "Port_Map",
            EntityType = "Infrastructure",
            Observations =
            [
                "Master Server: UDP/TCP 55555",
                "Chat Server: TCP 11031",
                "Dedicated Game Server: UDP 11235",
                "Redis Cache & Pub/Sub: TCP 28298",
                "Aspire Web API: TCP 5200",
                "Aspire Web UI Dashboard: TCP 5266"
            ]
        });

        // 4. Native Modules
        entities.Add(new MemoryEntity
        {
            Name = "Native_Modules",
            EntityType = "Architecture",
            Observations =
            [
                "hon_x64.exe: Main game executable (K2 engine core bootstrapper).",
                "k2_x64.dll: Core engine runtime, memory management, and file system.",
                "cgame_x64.dll: Client-side game logic, hero entities, visual effects, and UI bindings.",
                "game_shared_x64.dll: Shared entity definitions, networking serializers, and game state models.",
                "vid_d3d9_x64.dll / vid_vulkan_x64.dll: Video rendering drivers."
            ]
        });

        // 5. Relations
        relations.Add(new MemoryRelation { From = "TAO", To = "K2_Engine", RelationType = "orchestrates" });
        relations.Add(new MemoryRelation { From = "TAO", To = "Dedicated_Server", RelationType = "controls" });
        relations.Add(new MemoryRelation { From = "TAO", To = "Aspire_Cluster", RelationType = "integrates" });
        relations.Add(new MemoryRelation { From = "UIManager", To = "K2_Engine", RelationType = "ui_root_in" });
        relations.Add(new MemoryRelation { From = "CWidget_DoEvent", To = "UIManager", RelationType = "method_contract_for" });
        relations.Add(new MemoryRelation { From = "Dedicated_Server", To = "Dedicated_Server_Port_Routing", RelationType = "enforces" });
        relations.Add(new MemoryRelation { From = "K2_Engine", To = "Headless_Window_Protocol", RelationType = "runs_under" });
        relations.Add(new MemoryRelation { From = "TAO", To = "Inner_Loop_Test_Gating", RelationType = "complies_with" });
        relations.Add(new MemoryRelation { From = "Aspire_Cluster", To = "Port_Map", RelationType = "listens_on" });
        relations.Add(new MemoryRelation { From = "K2_Engine", To = "Native_Modules", RelationType = "composed_of" });

        // Augment with ISSUES.md dynamic summary if present
        try
        {
            string issuesPath = Path.Combine(_workspaceRoot, "docs", "ISSUES.md");
            if (File.Exists(issuesPath))
            {
                string text = File.ReadAllText(issuesPath);
                int openCount = Regex.Count(text, @"\[OPEN\]", RegexOptions.IgnoreCase);
                int resolvedCount = Regex.Count(text, @"\[RESOLVED\]|\[PATCHED\]", RegexOptions.IgnoreCase);

                entities.Add(new MemoryEntity
                {
                    Name = "Living_Issue_Tracker",
                    EntityType = "Repository_State",
                    Observations =
                    [
                        $"Tracks repository bugs and historical fixes in docs/ISSUES.md.",
                        $"Current state: {resolvedCount} resolved/patched issues, {openCount} open issues."
                    ]
                });
                relations.Add(new MemoryRelation { From = "Living_Issue_Tracker", To = "TAO", RelationType = "monitored_by" });
            }
        }
        catch (Exception ex)
        {
            _logger?.Invoke($"Warning: Failed to inspect docs/ISSUES.md: {ex.Message}");
        }

        return new MemoryGraphPayload
        {
            Entities = entities,
            Relations = relations
        };
    }
}
