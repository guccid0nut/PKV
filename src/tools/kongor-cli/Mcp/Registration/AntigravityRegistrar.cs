// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Text.Json;
using System.Text.Json.Nodes;
using Kongor.Cli.Mcp.Protocol;

namespace Kongor.Cli.Mcp.Registration;

/// <summary>
/// Result of an Antigravity registration operation.
/// </summary>
public sealed record RegistrationResult(
    bool Success,
    string TargetDirectory,
    IReadOnlyList<string> GeneratedFiles,
    string? ErrorMessage = null
);

/// <summary>
/// Generates and installs Antigravity MCP tool schema JSON definitions and agent guidelines.
/// </summary>
public static class AntigravityRegistrar
{
    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity", "mcp", "kongor");

    public static string IdeDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity-ide", "mcp", "kongor");

    /// <summary>
    /// Registers all MCP tool schemas and instructions into the Antigravity configuration directory.
    /// </summary>
    public static RegistrationResult Register(string? targetDirectory = null, McpToolRegistry? registry = null)
    {
        string dir = string.IsNullOrWhiteSpace(targetDirectory) ? DefaultDirectory : targetDirectory;
        registry ??= new McpToolRegistry(registerDefaults: true);

        List<string> writtenFiles = [];

        try
        {
            Directory.CreateDirectory(dir);

            IReadOnlyList<ToolDefinition> tools = registry.ListTools(toolsets: "all");
            HashSet<string> validFileNames = new(tools.Select(t => $"{t.Name}.json"), StringComparer.OrdinalIgnoreCase)
            {
                "instructions.md"
            };

            // Prune orphaned schema files no longer in canonical registry
            foreach (string existingFile in Directory.EnumerateFiles(dir, "*.json"))
            {
                string fn = Path.GetFileName(existingFile);
                if (!validFileNames.Contains(fn))
                {
                    try
                    {
                        File.Delete(existingFile);
                    }
                    catch (Exception delEx)
                    {
                        Console.Error.WriteLine($"[AntigravityRegistrar] Failed pruning orphaned schema '{existingFile}': {delEx.Message}");
                    }
                }
            }

            foreach (ToolDefinition tool in tools)
            {
                string filePath = Path.Combine(dir, $"{tool.Name}.json");
                string jsonContent = GenerateToolSchemaJson(tool);
                File.WriteAllText(filePath, jsonContent);
                writtenFiles.Add(filePath);
            }

            string instructionsPath = Path.Combine(dir, "instructions.md");
            string instructionsContent = GenerateInstructionsMarkdown();
            File.WriteAllText(instructionsPath, instructionsContent);
            writtenFiles.Add(instructionsPath);

            // Also mirror to Antigravity IDE directory if registering defaults and IDE directory exists
            if (string.IsNullOrWhiteSpace(targetDirectory))
            {
                try
                {
                    string ideDir = IdeDirectory;
                    Directory.CreateDirectory(ideDir);

                    foreach (string existingFile in Directory.EnumerateFiles(ideDir, "*.json"))
                    {
                        string fn = Path.GetFileName(existingFile);
                        if (!validFileNames.Contains(fn))
                        {
                            try { File.Delete(existingFile); }
                            catch (Exception delEx) { Console.Error.WriteLine($"[MCP Registration] Notice: Could not delete stale schema '{existingFile}': {delEx.Message}"); }
                        }
                    }

                    foreach (ToolDefinition tool in tools)
                    {
                        string filePath = Path.Combine(ideDir, $"{tool.Name}.json");
                        string jsonContent = GenerateToolSchemaJson(tool);
                        File.WriteAllText(filePath, jsonContent);
                    }
                    string ideInstructions = Path.Combine(ideDir, "instructions.md");
                    File.WriteAllText(ideInstructions, instructionsContent);
                }
                catch (Exception ideEx)
                {
                    Console.Error.WriteLine($"[MCP Registration] Notice: IDE schema mirror skipped: {ideEx.Message}");
                }
            }

            if (string.IsNullOrWhiteSpace(targetDirectory))
            {
                if (Kongor.Core.Configuration.BundledLayoutResolver.TryResolve(out var layout))
                {
                    WriteBundleMcpConfigFiles(layout);
                    UpsertAgentMcpServerConfigs(layout.KongorCliExe, ["mcp"], layout.Root);
                }
                else
                {
                    var (devCmd, devArgs, devEnv) = ResolveCanonicalDeveloperMcpCommand(Environment.ProcessPath);
                    UpsertAgentMcpServerConfigs(devCmd, devArgs, bundleRoot: null, userHomeOverride: null, extraEnv: devEnv);
                }
            }

            return new RegistrationResult(true, dir, writtenFiles);
        }
        catch (Exception ex)
        {
            return new RegistrationResult(false, dir, writtenFiles, ex.Message);
        }
    }

    /// <summary>
    /// Resolves the installed global .NET tool executable (<c>~/.dotnet/tools/kongor[.exe]</c>) when present.
    /// </summary>
    public static string? ResolveGlobalCliPath(string? userHomeOverride = null)
    {
        string userHome = !string.IsNullOrWhiteSpace(userHomeOverride)
            ? userHomeOverride
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userHome))
        {
            return null;
        }

        string exeName = OperatingSystem.IsWindows() ? "kongor.exe" : "kongor";
        string unifiedShim = Path.Combine(userHome, ".kongor", "bin", exeName);
        if (File.Exists(unifiedShim))
        {
            return unifiedShim;
        }

        string candidate = Path.Combine(userHome, ".dotnet", "tools", exeName);
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>
    /// Resolves the canonical MCP server command for developer checkouts and installed global CLIs.
    /// Never persists ephemeral TEMP shadow-copy paths (e.g. kongor_mcp_runtime\pid_XXXX\kongor.exe)
    /// into IDE mcp_config.json; prefers `python -u &lt;repoRoot&gt;/src/tools/tao_mcp.py` inside a checkout
    /// (or global `~/.dotnet/tools/kongor[.exe]` when <paramref name="preferGlobalCli"/> is true or outside a checkout).
    /// </summary>
    public static (string Command, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string>? Env) ResolveCanonicalDeveloperMcpCommand(
        string? processPath = null,
        string? repoRootOverride = null,
        string? userHomeOverride = null,
        bool preferGlobalCli = false)
    {
        string? resolvedDotNetRoot = Kongor.Cli.Mcp.Services.LifecycleService.ResolveDotNetRoot(homeOverride: userHomeOverride);
        string? globalCli = ResolveGlobalCliPath(userHomeOverride);

        if (preferGlobalCli && !string.IsNullOrWhiteSpace(globalCli))
        {
            Dictionary<string, string>? globalEnv = !string.IsNullOrWhiteSpace(resolvedDotNetRoot)
                ? new Dictionary<string, string>(StringComparer.Ordinal) { ["DOTNET_ROOT"] = resolvedDotNetRoot }
                : null;
            return (globalCli, ["mcp"], globalEnv);
        }

        string? repoRoot = repoRootOverride != null
            ? FindRepositoryRoot(repoRootOverride)
            : FindRepositoryRoot(Directory.GetCurrentDirectory());
        if (repoRootOverride == null && repoRoot == null && !string.IsNullOrWhiteSpace(processPath) && !IsEphemeralShadowPath(processPath))
        {
            repoRoot = FindRepositoryRoot(Path.GetDirectoryName(processPath));
        }

        if (!string.IsNullOrWhiteSpace(repoRoot))
        {
            string taoMcpPath = Path.Combine(repoRoot, "src", "tools", "tao_mcp.py");
            if (File.Exists(taoMcpPath))
            {
                Dictionary<string, string> env = new(StringComparer.Ordinal)
                {
                    ["PYTHONUNBUFFERED"] = "1"
                };
                if (!string.IsNullOrWhiteSpace(resolvedDotNetRoot))
                {
                    env["DOTNET_ROOT"] = resolvedDotNetRoot;
                }
                return ("python", ["-u", taoMcpPath], env);
            }

            string apphostName = OperatingSystem.IsWindows() ? "kongor.exe" : "kongor";
            string releaseExe = Path.Combine(repoRoot, "src", "tools", "kongor-cli", "bin", "Release", "net11.0", apphostName);
            if (File.Exists(releaseExe))
            {
                Dictionary<string, string>? env = !string.IsNullOrWhiteSpace(resolvedDotNetRoot)
                    ? new Dictionary<string, string>(StringComparer.Ordinal) { ["DOTNET_ROOT"] = resolvedDotNetRoot }
                    : null;
                return (releaseExe, ["mcp"], env);
            }
        }

        if (!string.IsNullOrWhiteSpace(globalCli))
        {
            Dictionary<string, string>? globalEnv = !string.IsNullOrWhiteSpace(resolvedDotNetRoot)
                ? new Dictionary<string, string>(StringComparer.Ordinal) { ["DOTNET_ROOT"] = resolvedDotNetRoot }
                : null;
            return (globalCli, ["mcp"], globalEnv);
        }

        if (!string.IsNullOrWhiteSpace(processPath) && !IsEphemeralShadowPath(processPath))
        {
            return (processPath, ["mcp"], null);
        }

        return ("kongor", ["mcp"], null);
    }

    private static bool IsEphemeralShadowPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;
        string fileName = Path.GetFileNameWithoutExtension(path);
        return path.Contains("kongor_mcp_runtime", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("testhost", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("vstest", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("xunit", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindRepositoryRoot(string? startDirectory)
    {
        if (string.IsNullOrWhiteSpace(startDirectory))
        {
            return null;
        }

        try
        {
            DirectoryInfo? current = new(Path.GetFullPath(startDirectory));
            while (current != null)
            {
                if (File.Exists(Path.Combine(current.FullName, "ProjectKongor.slnx")) ||
                    File.Exists(Path.Combine(current.FullName, "Directory.Build.props")))
                {
                    return current.FullName;
                }
                current = current.Parent;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MCP Registration] Debug: FindRepositoryRoot probe notice for '{startDirectory}': {ex.Message}");
        }

        return null;
    }

    public static void WriteBundleMcpConfigFiles(Kongor.Core.Configuration.BundledLayout layout)
    {
        try
        {
            string mcpJsonContent = BuildWorkspaceMcpJson(layout.KongorCliExe, layout.Root);
            string rootMcpJson = Path.Combine(layout.Root, ".mcp.json");
            File.WriteAllText(rootMcpJson, mcpJsonContent, new System.Text.UTF8Encoding(false));

            string cursorDir = Path.Combine(layout.Root, ".cursor");
            if (Directory.Exists(cursorDir))
            {
                File.WriteAllText(Path.Combine(cursorDir, "mcp.json"), mcpJsonContent, new System.Text.UTF8Encoding(false));
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MCP Registration] Notice: Could not write workspace .mcp.json: {ex.Message}");
        }
    }

    public static string BuildWorkspaceMcpJson(string commandPath, string? bundleRoot = null)
    {
        JsonObject serverNode = new()
        {
            ["command"] = commandPath,
            ["args"] = new JsonArray("mcp")
        };

        if (!string.IsNullOrWhiteSpace(bundleRoot))
        {
            serverNode["env"] = new JsonObject
            {
                ["KONGOR_BUNDLE_ROOT"] = bundleRoot
            };
        }

        JsonObject root = new()
        {
            ["mcpServers"] = new JsonObject
            {
                ["kongor"] = serverNode
            }
        };

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    public static void UpsertAgentMcpServerConfigs(
        string commandPath,
        IReadOnlyList<string> args,
        string? bundleRoot = null,
        string? userHomeOverride = null,
        IReadOnlyDictionary<string, string>? extraEnv = null)
    {
        try
        {
            string userHome = !string.IsNullOrWhiteSpace(userHomeOverride)
                ? userHomeOverride
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(userHome)) return;

            string antigravityConfigPath = Path.Combine(userHome, ".gemini", "antigravity", "mcp_config.json");
            UpsertMcpServerEntryInFile(antigravityConfigPath, commandPath, args, bundleRoot, createParentIfMissing: true, extraEnv: extraEnv);

            string claudeDir = OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude")
                : OperatingSystem.IsMacOS()
                    ? Path.Combine(userHome, "Library", "Application Support", "Claude")
                    : Path.Combine(userHome, ".config", "Claude");

            if (!string.IsNullOrWhiteSpace(userHomeOverride))
            {
                claudeDir = Path.Combine(userHome, ".config", "Claude");
            }

            if (Directory.Exists(claudeDir))
            {
                string claudeConfigPath = Path.Combine(claudeDir, "claude_desktop_config.json");
                UpsertMcpServerEntryInFile(claudeConfigPath, commandPath, args, bundleRoot, createParentIfMissing: false, extraEnv: extraEnv);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MCP Registration] Notice: Agent mcp_config upsert skipped: {ex.Message}");
        }
    }

    private static void UpsertMcpServerEntryInFile(
        string configFilePath,
        string commandPath,
        IReadOnlyList<string> args,
        string? bundleRoot,
        bool createParentIfMissing,
        IReadOnlyDictionary<string, string>? extraEnv = null)
    {
        string? parentDir = Path.GetDirectoryName(configFilePath);
        if (string.IsNullOrWhiteSpace(parentDir)) return;
        if (!Directory.Exists(parentDir))
        {
            if (!createParentIfMissing) return;
            Directory.CreateDirectory(parentDir);
        }

        JsonObject rootObj = [];
        if (File.Exists(configFilePath))
        {
            try
            {
                string existing = File.ReadAllText(configFilePath);
                if (!string.IsNullOrWhiteSpace(existing) && JsonNode.Parse(existing) is JsonObject parsedObj)
                {
                    rootObj = parsedObj;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[MCP Registration] Warning: Preserving malformed '{configFilePath}' ({ex.Message}).");
                return;
            }
        }

        if (rootObj["mcpServers"] is not JsonObject serversObj)
        {
            serversObj = [];
            rootObj["mcpServers"] = serversObj;
        }

        JsonArray argsArray = [];
        foreach (string arg in args) argsArray.Add(arg);

        JsonObject kongorNode = new()
        {
            ["command"] = commandPath,
            ["args"] = argsArray
        };

        JsonObject envObj = [];
        if (!string.IsNullOrWhiteSpace(bundleRoot))
        {
            envObj["KONGOR_BUNDLE_ROOT"] = bundleRoot;
        }
        if (extraEnv != null)
        {
            foreach (KeyValuePair<string, string> kvp in extraEnv)
            {
                envObj[kvp.Key] = kvp.Value;
            }
        }
        if (envObj.Count > 0)
        {
            kongorNode["env"] = envObj;
        }

        serversObj["kongor"] = kongorNode;
        File.WriteAllText(
            configFilePath,
            rootObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n",
            new System.Text.UTF8Encoding(false));
    }

    /// <summary>
    /// Generates schema JSON matching Antigravity tool schema format.
    /// </summary>
    public static string GenerateToolSchemaJson(ToolDefinition tool)
    {
        JsonObject parametersObject = new()
        {
            ["type"] = "object",
            ["title"] = tool.InputSchema.Title ?? $"{tool.Name}Arguments"
        };

        JsonObject propertiesObject = new();
        foreach (KeyValuePair<string, ToolPropertySchema> prop in tool.InputSchema.Properties)
        {
            propertiesObject[prop.Key] = BuildPropertySchemaNode(prop.Key, prop.Value);
        }

        parametersObject["properties"] = propertiesObject;

        JsonArray requiredArray = new();
        foreach (string req in tool.InputSchema.Required)
        {
            requiredArray.Add(req);
        }
        parametersObject["required"] = requiredArray;

        JsonObject rootObject = new()
        {
            ["name"] = tool.Name,
            ["description"] = StripMcpPitfallProse(tool.Description),
            ["parameters"] = parametersObject
        };

        return JsonSerializer.Serialize(rootObject, JsonRpcSerializer.IndentedOptions);
    }

    private static JsonObject BuildPropertySchemaNode(string? key, ToolPropertySchema schema)
    {
        JsonObject propNode = new()
        {
            ["type"] = schema.Type
        };

        string? resolvedTitle = schema.Title ?? (!string.IsNullOrEmpty(key) ? CapitalizeTitle(key) : null);
        if (!string.IsNullOrWhiteSpace(resolvedTitle))
        {
            propNode["title"] = resolvedTitle;
        }

        propNode["description"] = StripMcpPitfallProse(schema.Description);

        if (schema.Enum is { Count: > 0 })
        {
            JsonArray enumArray = new();
            foreach (string val in schema.Enum)
            {
                enumArray.Add(val);
            }
            propNode["enum"] = enumArray;
        }

        if (schema.Default != null)
        {
            propNode["default"] = JsonValue.Create(schema.Default);
        }

        if (schema.Examples is { Count: > 0 })
        {
            JsonArray examplesArray = new();
            foreach (string ex in schema.Examples)
            {
                examplesArray.Add(ex);
            }
            propNode["examples"] = examplesArray;
        }

        if (schema.Items != null)
        {
            propNode["items"] = BuildPropertySchemaNode(null, schema.Items);
        }

        if (schema.Properties is { Count: > 0 })
        {
            JsonObject nestedProps = new();
            foreach (KeyValuePair<string, ToolPropertySchema> nested in schema.Properties)
            {
                nestedProps[nested.Key] = BuildPropertySchemaNode(nested.Key, nested.Value);
            }
            propNode["properties"] = nestedProps;
        }

        if (schema.Required is { Count: > 0 })
        {
            JsonArray nestedRequired = new();
            foreach (string req in schema.Required)
            {
                nestedRequired.Add(req);
            }
            propNode["required"] = nestedRequired;
        }

        return propNode;
    }

    public static string StripMcpPitfallProse(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return string.Empty;
        }

        string stripped = System.Text.RegularExpressions.Regex.Replace(
            description,
            @"\s*\[MCP-\d+:(?:[^\]]|\](?!\s*(?:\[MCP-\d+:|$)))*\]|\s*\[MCP-\d+[^\]]*\]",
            string.Empty,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return stripped.Trim();
    }

    /// <summary>
    /// Generates instructions.md guide for AI coding agents.
    /// </summary>
    public static string GenerateInstructionsMarkdown()
    {
        return @"# ProjectKongor MCP Server — Agent Usage Guidelines

The ProjectKongor MCP Server provides AI coding agents with native programmatic control and visual inspection capabilities over the ProjectKongor game engine, microservice cluster, and UI applications.

## Universal Cross-IDE Catalog Discovery & Execution (`kongor_call_tool`)

By default, the server advertises the focused 18-tool `core` allowlist so all IDEs (including Cursor, Zed, JetBrains, Windsurf, Codex, Claude Code, VS Code, and Antigravity) stay fast and well under tool-count ceilings. To discover, inspect, or execute any of the ~100 specialized non-core tools without requiring `notifications/tools/list_changed`:
- **Search catalog**: `kongor_call_tool` with `{ ""action"": ""search"", ""query"": ""texture"" }` (or `{ ""action"": ""list"" }`)
- **Inspect exact schema before calling**: `kongor_call_tool` with `{ ""action"": ""describe"", ""tool_name"": ""kongor_k2_lint"" }`
- **Execute any tool on the first try**: `kongor_call_tool` with `{ ""action"": ""call"", ""tool_name"": ""kongor_k2_lint"", ""arguments_json"": ""{\""path\"":\""games/client/override/ui\""}"" }`

## Available Tools

1. **`kongor_server_manage`**:
   - **Purpose**: Manage the lifecycle (start, stop, restart, status, logs) of the Aspire microservice cluster (Master Server, Chat Server, Web API, Web UI, Redis, MERRICK DB). Dedicated to Aspire cluster orchestration.
   - **Key Actions**:
     - Query health: `{ ""action"": ""status"", ""target"": ""cluster"" }`
     - Start microservices: `{ ""action"": ""start"", ""target"": ""cluster"" }`
     - Stop services cleanly: `{ ""action"": ""stop"", ""target"": ""cluster"" }`

2. **`kongor_game_client_manage` / `kongor_game_client_manager`**:
   - **Purpose**: Controls the player game client binary (`hon_x64.exe`). Launches, stops, queries status, and restarts the game client with complete VFS mod mounts (`override;base;game;../shared;KONGOR/configuration;KONGOR/updates;KONGOR/extensions`), DXVK Vulkan settings, low-latency audio, and Named Pipe IPC.
   - **Key Actions**:
     - Launch practice mode: `{ ""action"": ""launch"", ""mode"": ""practice"", ""resolution"": ""1920x1080"" }`
     - Launch bot match: `{ ""action"": ""launch"", ""mode"": ""bot_match"" }`
     - Connect to server: `{ ""action"": ""launch"", ""connectAddress"": ""127.0.0.1:11235"" }`
     - Query status: `{ ""action"": ""status"" }`
     - Stop client: `{ ""action"": ""stop"" }`
     - Restart client: `{ ""action"": ""restart"" }`

3. **`kongor_willowmaker_manage` / `kongor_willowmaker_manager`**:
   - **Purpose**: Controls the desktop launcher application (`WILLOWMAKER.exe`). Programmatically launches, terminates, and queries process/window state.
   - **Key Actions**:
     - Launch desktop launcher: `{ ""action"": ""launch"" }`
     - Query status: `{ ""action"": ""status"" }`
     - Cleanly close launcher: `{ ""action"": ""stop"" }`

4. **`kongor_compel_manage` / `kongor_compel_manager`**:
   - **Purpose**: Controls the COMPEL dedicated server supervisor & REST API on port 11234 (`/ping`, `/status`, `/instances/start`, `/instances/stop`).
   - **Key Actions**:
     - Inspect node health & ports: `{ ""action"": ""status"" }`
     - Start supervisor & instances: `{ ""action"": ""start"" }`
     - Stop instances & supervisor: `{ ""action"": ""stop"" }`
     - Manage worker pools: `{ ""action"": ""instances"", ""instancesAction"": ""start"" }`

5. **`kongor_game_server_manage` / `kongor_game_server_manager`**:
   - **Purpose**: Controls dedicated game server worker instances (`hon_x64.exe` in `-dedicated` mode). Spawns server workers, discovers running servers, queries worker telemetry, and manages worker lifecycle.
   - **Key Actions**:
     - Launch dedicated server worker: `{ ""action"": ""launch"", ""port"": 11235, ""serverName"": ""Project KONGOR Dedicated Server"" }`
     - List running server workers: `{ ""action"": ""status"" }`
     - Stop worker by PID: `{ ""action"": ""stop"", ""processId"": 12345 }`
     - Stop all server workers: `{ ""action"": ""stop_all"" }`

6. **`kongor_game_manage`**:
   - **Purpose**: Backwards-compatible game lifecycle router. Automatically redirects requests to `kongor_game_client_manage` or `kongor_willowmaker_manage` based on target.
   - **Key Actions**:
     - `{ ""action"": ""launch"", ""mode"": ""practice"" }`
     - `{ ""action"": ""status"" }`
     - `{ ""action"": ""stop"" }`

7. **`kongor_game_command`**:
   - **Purpose**: Unified remote console command router and multiplexer. Routes commands to running player client (`\\.\pipe\willowmaker-ipc`), specific dedicated server listen ports via stdin and port-named pipes (`\\.\pipe\hon-server-{port}`), or multiplexes across both client and server simultaneously.
   - **Examples**:
     - Client cvar: `{ ""command"": ""Set vid_maxFPS 1000"" }`
     - Client practice: `{ ""command"": ""StartGame practice GameName map:caldavar allheroes:true"" }`
     - Server command: `{ ""command"": ""AddFakePlayer 2"", ""port"": 11235 }`
     - Multiplexed simulation control: `{ ""action"": ""set_timescale"", ""scale"": 2.0, ""target"": ""both"" }`
     - Multiplexed frame step: `{ ""action"": ""step_frames"", ""frames"": 10, ""target"": ""both"" }`

8. **`kongor_game_client_command`**:
   - **Purpose**: Dedicated command executor strictly targeting the running player game client (`hon_x64.exe`).
   - **Usage**: `{ ""command"": ""Connect 127.0.0.1:11235"" }`

9. **`kongor_game_server_command`**:
   - **Purpose**: Dedicated command executor strictly targeting dedicated game server instances (`hon_x64.exe -dedicated`) by port.
   - **Usage**: `{ ""port"": 11235, ""command"": ""AddFakePlayer 2"", ""channel"": ""stdin"" }`

10. **`kongor_game_lua` / `kongor_lua`**:
   - **Purpose**: First-class programmatic Lua scripting control over the K2 engine without requiring mouse clicks. Supports arbitrary Lua expressions, firing engine triggers, and invoking widget events. Supports dual-targeting ('client', 'server') and multiplexed dual execution ('target': 'both').
   - **Usage**:
     - Evaluate Lua expression: `{ ""code"": ""Cmd('StartGame practice GameName map:caldavar allheroes:true')"" }`
     - Fire named K2 trigger: `{ ""trigger"": ""main_login"" }`
     - Invoke widget action: `{ ""invokeWidget"": ""sysbar_quickStart"", ""event"": ""onclick"" }`
     - Target dedicated server: `{ ""code"": ""println('Server status: OK')"", ""target"": ""server"", ""port"": 11235 }`
     - Multiplexed Lua execution: `{ ""code"": ""Echo('[TAO_SYNC] Checkpoint active')"", ""target"": ""both"" }`

11. **`kongor_game_telemetry`**:
   - **Purpose**: Fetch real-time engine telemetry (current FPS, working set memory, active match ID, current map name, player count, uptime).
   - **Usage**: `{ ""timeoutMs"": 2000 }`

12. **`kongor_capture_window`**:
   - **Purpose**: High-performance visual screenshot capture and real-time anomaly detection. Defaults to **Tier 0 Direct GPU DMA Zero-Copy Vulkan Offscreen Capture** (`VK_EXT_external_memory_host`, 0 ms dwell, 0% window exposure) when capturing `hon_x64` parked offscreen at `(-32000, -32000)`. Automatically cascades through Tier 1 (`PrintWindow`), Tier 2 (`Window DC`), and Tier 3 (`transient`) if the Vulkan layer is inactive or capturing desktop apps (`WILLOWMAKER`, `NEXUS Control Panel`).
   - **Usage**: `{ ""target"": ""hon_x64"", ""capture_method"": ""auto"", ""save_artifact"": true, ""detect_anomalies"": true }`

13. **`kongor_transient_capture`**:
   - **Purpose**: Capture sub-second onscreen screenshots of offscreen/headless game client windows (`hon_x64` at `-32000, -32000`) with zero workflow disruption. Temporarily exposes window at `(0, 0)` for dwell time (default 350ms) to allow GPU surface present, captures Desktop DC BitBlt, and immediately returns window to offscreen/hidden.
   - **Usage**: `{ ""target"": ""hon_x64"", ""dwell_ms"": 350, ""width"": 1280, ""height"": 720, ""save_artifact"": true }`

14. **`kongor_ui_render_view`**:
   - **Purpose**: Offscreen headless Avalonia XAML view rendering with SkiaSharp for visual layout and theme verification without requiring desktop interaction.
   - **Supported Views**: `ModManagerView`, `ReplayStudioView`, `SecretsConfigView`, `DiagnosticsView`, `DashboardView`, `GameServersView`, `UsersView`, `LiveLogsView`.
   - **Usage**: `{ ""view_name"": ""ModManagerView"", ""theme"": ""dark"", ""width"": 1280, ""height"": 720 }`

14. **`kongor_ui_control` / `kongor_ui_controller`**:
   - **Purpose**: Universal interactive UI control and automation for Project Kongor windows (`hon_x64`, `WILLOWMAKER`, `NEXUS Control Panel`, foreground).
   - **Supported Actions**:
     - `""inspect""`: Traverses UI Automation tree and Win32 controls; returns interactive elements (name, automationId, controlType, bounding rectangle, isEnabled).
     - `""click""`: Clicks by element name/AutomationId or window-relative (x, y) coordinates with natural 100-150ms mouse down/up delay. If target is the game client and widget name is supplied, automatically routes to engine-internal DOM event dispatching.
     - `""dispatch_event""`: Directly dispatches engine-internal K2 DOM events (`Call <widget> <event>`, `UIManager.GetWidget():DoEvent[N]()`) over Named Pipe IPC, bypassing Win32 mouse simulation.
     - `""invoke""`: Invokes UI Automation `InvokePattern` on target element directly without mouse movement.
     - `""type""`: Dispatches text characters into focused window.
     - `""key""`: Dispatches key presses (`Enter`, `Tab`, `Escape`, `F8`, `Space`, etc.) to target window.
   - **Usage**:
     - Inspect UI: `{ ""action"": ""inspect"", ""target"": ""hon_x64"" }`
     - Dispatch DOM event: `{ ""action"": ""dispatch_event"", ""widget"": ""sysbar_quickStart"", ""event"": ""click"" }`
     - Invoke element: `{ ""action"": ""invoke"", ""target"": ""willowmaker"", ""element_name"": ""LaunchButton"" }`
     - Click coords: `{ ""action"": ""click"", ""target"": ""hon_x64"", ""x"": 960, ""y"": 540 }`
     - Type text: `{ ""action"": ""type"", ""target"": ""hon_x64"", ""text"": ""StartGame practice"" }`
     - Send key: `{ ""action"": ""key"", ""target"": ""hon_x64"", ""key"": ""F8"" }`

15. **`kongor_ui_inspect`** (aliases: `kongor_ui_tree`, `kongor_k2_ui_inspect`):
   - **Purpose**: Deep dual-mode K2 engine UI introspection. Queries live widget hierarchy (panels, images, buttons, labels, modelpanels) via Named Pipe IPC (`\\.\pipe\willowmaker-ipc`) or parses offline `.interface` and `.package` XML documents with complete `<template>` and `<instance>` expansion.
   - **Key Features**: Inspects widget visibility and bounds, validates bound textures against VFS mount paths, and flags warnings for missing textures or solid white (`$white`) fallbacks.
   - **Usage**:
     - Auto mode: `{ ""mode"": ""auto"" }`
     - Specific package: `{ ""path"": ""/ui/fe2/main_v2.interface"", ""mode"": ""static"" }`
     - Search widgets: `{ ""search"": ""quickStart"" }`

16. **`kongor_match_launch`** (alias: `kongor_practice_launch`):
   - **Purpose**: Programmatically launch directly into a practice match or bot match on Caldavar (or custom map) with pre-selected hero, bot count, and developer cheats without needing interactive UI menu navigation.
   - **Key Actions**:
     - Launch practice match: `{ ""action"": ""launch"", ""map"": ""caldavar"", ""hero"": ""Hero_Pyromancer"", ""mode"": ""practice"", ""cheats"": true }`
     - Launch bot match: `{ ""action"": ""launch"", ""map"": ""caldavar"", ""bots"": 5, ""mode"": ""bot_match"" }`
     - Headless match: `{ ""action"": ""launch"", ""headless"": true }`
     - Stop match: `{ ""action"": ""stop"" }`

17. **`kongor_asset_inspect`** (alias: `kongor_asset_inspector`):
   - **Purpose**: Pure managed C# binary sanity checker for texture and visual assets (`.tga` and `.dds`).
   - **Usage**: `{ ""path"": ""ui/fe2/mainmenu/logo_fb.tga"" }`

18. **`kongor_engine_cache_purge`** (alias: `kongor_cache_purge`):
   - **Purpose**: Targeted atomic cache flushing and hot-reloading (`shadercache`, `texturecache`, `luadata`, DXVK state).
   - **Usage**: `{ ""targets"": [""all""], ""hotReload"": true }`

19. **`kongor_cluster_diagnostics`** (alias: `kongor_diagnostics`):
   - **Purpose**: Deep wire and health probe across Redis (`28298`), MERRICK PostgreSQL DB (`5432`/`6543`), MasterServer (`55555`), ChatServer (`11031`), Web API (`5200`), Web UI (`5266`), and COMPEL (`11234`).
   - **Usage**: `{}`

20. **`kongor_match_state`**:
   - **Purpose**: Live match introspection and state reconciliation without screen scraping. Queries game phase, game time, scores, gold, connected players, hero vitality, and active units via IPC and dedicated server inspection. Supports client <-> server authoritative cross-verification (`target: 'reconciled'` / `'both'`), detecting spatial position drift vs tolerance epsilon, gold prediction divergence, and clock drift.
   - **Usage**:
     - Client match state: `{ ""target"": ""client"" }`
     - Server match state: `{ ""target"": ""server"", ""port"": 11235 }`
     - Reconciled state & desync analysis: `{ ""target"": ""reconciled"", ""toleranceEpsilon"": 50.0 }`

21. **`kongor_game_snapshot`** (aliases: `kongor_snapshot`, `kongor_checkpoint`):
   - **Purpose**: Mid-game state checkpointing, persistence, restoration, world state querying, and state reconciliation with Phase 3 lifecycle safety guards. Saves/restores hero level, gold, items, position, cooldowns, and performs frame-accurate replay tick seeking (20 ticks/sec). Supports dual-target restoration (`target: 'both'`) and live world state cross-verification (`action: 'world_state'`, `target: 'reconciled'`, `action: 'reconcile'`).
   - **Usage**:
     - Checkpoint state: `{ ""action"": ""save"", ""id"": ""snap_lane_phase"" }`
     - Reconcile world state: `{ ""action"": ""world_state"", ""target"": ""reconciled"" }`
     - Reconcile snapshot against live: `{ ""action"": ""reconcile"", ""id"": ""snap_lane_phase"" }`
     - Apply fixture to both client & server: `{ ""action"": ""apply_fixture"", ""target"": ""both"", ""fixture"": { ""hero"": ""Hero_Pyromancer"", ""gold"": 5000, ""level"": 16 } }`
     - Replay seek: `{ ""action"": ""seek"", ""timeFormatted"": ""02:15"" }`

22. **`kongor_game_action`**:
   - **Purpose**: Deterministic engine action, unit command, cheat, and macro dispatcher (`spawn_unit`, `kill_unit`, `give_gold`, `level_up`, `teleport`, `cast_ability`, `order`, `execute`, `buy_item`, `sell_item`, `move_to`, `attack_target`, `use_item`) with dual-targeting across player clients and dedicated game servers.
   - **Usage**:
     - Client spawn unit: `{ ""action"": ""spawn_unit"", ""unit"": ""Hero_Pyromancer"", ""team"": 1, ""x"": 1000, ""y"": 1000 }`
     - Server macro action: `{ ""action"": ""spawn_unit"", ""target"": ""server:11235"", ""unit"": ""Hero_Devourer"", ""team"": 2 }`
     - Dedicated server kill unit: `{ ""action"": ""kill_unit"", ""target"": ""server"", ""entity"": ""Hero_Pyromancer"" }`
     - Give gold: `{ ""action"": ""give_gold"", ""amount"": 5000 }`
     - Cast ability: `{ ""action"": ""cast_ability"", ""slot"": 0, ""x"": 1200, ""y"": 1200 }`

23. **`kongor_replay_inspect`**:
   - **Purpose**: Fast headless parser for `.honreplay` zip archives. Extracts match ID, map, game duration, winning team, version, server info, and per-player scores and item builds without starting the game. Supports discovering replay files on disk and dynamic server port resolution from active workers.
   - **Usage**:
     - Inspect file: `{ ""path"": ""replays/match_12345.honreplay"", ""summaryOnly"": false }`
     - Discover replays: `{ ""action"": ""list"" }`
     - Inspect active server match: `{ ""port"": 11235 }`

24. **`kongor_replay_playback`**:
   - **Purpose**: Programmatic replay player & live match spectating controller: spectate live matches on dedicated server workers (`action: 'spectate'`), record server demos via redirected stdin (`action: 'record_demo'`), stop demos (`action: 'stop_demo'`), bind spectator camera to heroes, start playback, pause, resume, stop, seek to time, or set playback speed multiplier. Supports multi-target dispatch (`client`, `server`, `both`).
   - **Usage**:
     - Spectate live server: `{ ""action"": ""spectate"", ""port"": 11235 }`
     - Record server demo: `{ ""action"": ""record_demo"", ""demoName"": ""match_11235_demo"", ""target"": ""server"", ""port"": 11235 }`
     - Seek: `{ ""action"": ""seek"", ""time"": 300.0 }`
     - Speed: `{ ""action"": ""speed"", ""speed"": 2.0 }`
     - Camera hero tracking: `{ ""action"": ""camera"", ""hero"": ""Hero_Pyromancer"" }`

24. **`kongor_db_fixture`**:
   - **Purpose**: Manage deterministic test fixtures in local development DB: seed sample accounts, match histories, hero statistics, or reset tables.
   - **Usage**: `{ ""action"": ""seed"", ""fixture"": ""accounts"", ""count"": 10 }`

25. **`kongor_vfs_resolve`**:
   - **Purpose**: Virtual File System path resolver across game mounts (`base`, `game`, `shared`, mods, localized archives). Reports resolved physical paths, layer priority, and file collisions.
   - **Usage**: `{ ""virtualPath"": ""/ui/main.interface"", ""checkMounts"": true }`

26. **`kongor_mod_build`**:
   - **Purpose**: Package and validate `.honmod` (ZIP with mod manifest) and `.s2z` modification archives from directory sources with automatic checksum validation. Supports instant live client hot-reloading (`hotReload: true`, triggers `ReloadTextures` & `ReloadInterfaces` via IPC) and dedicated server shared layer synchronization (`syncShared: true`).
   - **Usage**: `{ ""sourceDirectory"": ""mods/CustomUi"", ""outputFile"": ""game/resources99.s2z"", ""modType"": ""s2z"", ""hotReload"": true, ""syncShared"": true }`

27. **`kongor_audio_preview`**:
   - **Purpose**: Pure managed audio asset decoder, inspector & live audio trigger. Reads `.wav`, `.ogg`, and `.mp3` files from disk or VFS, inspects sample rate, channel count, duration, exports uncompressed WAV, and dispatches live audio playback (`action: 'play'`, `PlaySound`) across player client and dedicated server workers (`target: 'client'`, `'server'`, `'both'`) with dynamic server port resolution.
   - **Usage**:
     - Audio info: `{ ""soundPath"": ""shared/sounds/ui/button_click.wav"", ""action"": ""info"" }`
     - Live playback: `{ ""soundPath"": ""/shared/sounds/ui/button_click.wav"", ""action"": ""play"", ""target"": ""both"", ""port"": 11235 }`

28. **`kongor_app_eval`** (alias: `app_eval_script`):
   - **Purpose**: Dynamic C# script execution & live memory poking via Roslyn compiler services. Enables runtime query of active memory, DI containers, and caches without recompilation.
   - **Usage**: `{ ""script"": ""Print(TimeProvider.System.GetUtcNow().ToString());"" }`

29. **`kongor_state_snapshot`** (aliases: `state_snapshot_create`, `state_snapshot_restore`):
   - **Purpose**: Time-travel state snapshotting and restoration across relational databases, Redis keyspaces, and active session tables for deterministic testing and instant rollback.
   - **Usage**: `{ ""action"": ""create"", ""name"": ""pre-match-state"" }` or `{ ""action"": ""restore"", ""name"": ""pre-match-state"" }`

31. **`kongor_network_chaos`** (alias: `network_chaos_inject`):
   - **Purpose**: Live network impairment & chaos engineering injection. Injects latency spikes (100–500 ms), packet drops (5–25%), and connection resets into sockets, Redis connections, or game server pipelines with auto-healing timers.
   - **Usage**: `{ ""action"": ""inject"", ""type"": ""latency"", ""latencyMs"": 250, ""durationSeconds"": 30 }`

32. **`kongor_realtime_stream`** (alias: `realtime_telemetry_stream`):
   - **Purpose**: Real-time log tailing, OpenTelemetry trace activity queries, and live session metric streaming via Server-Sent Events / streaming ndjson.
   - **Usage**: `{ ""streamType"": ""logs"", ""filter"": ""ERROR"", ""maxEvents"": 100 }`

33. **`kongor_texture_preview`** (alias: `texture_preview`):
   - **Purpose**: Fast texture preview, VFS hierarchy resolution (`override` > `game` > `base` > `shared` > `.s2z`), & channel isolation suite. Decodes DDS and TGA binaries, isolates individual color channels (Diffuse, Alpha, RxGB Normal with Z reconstruction \(Z = \sqrt{1 - X^2 - Y^2}\), Specular), exports 2x2 diagnostic mosaic artifacts, and dispatches live `ReloadTextures` commands to the player client via IPC (`reload_textures: true`).
   - **Usage**: `{ ""path"": ""heroes/pyromancer/color.tga"", ""channel"": ""mosaic"", ""reload_textures"": true, ""save_artifact"": true }`

34. **`kongor_texture_in_game_inspect`** (alias: `texture_in_game_inspect`):
   - **Purpose**: In-engine live visual inspection with dynamic camera presets (`front`, `side`, `back`, `top`, `portrait`) and zoom levels (`close`, `medium`, `wide`). Binds camera target to hero/model, triggers `ReloadTextures` console command via Named Pipe IPC, and captures high-resolution rendered viewport frame buffer without manual mouse input.
   - **Usage**: `{ ""hero"": ""Hero_Pyromancer"", ""texture"": ""heroes/pyromancer/color.tga"", ""camera_preset"": ""front"", ""camera_zoom"": ""close"", ""reload_textures"": true }`

35. **`kongor_texture_diff`** (alias: `texture_diff`):
   - **Purpose**: Perceptual visual diff between two texture assets or render captures. Computes Mean Squared Error (MSE), Peak Signal-to-Noise Ratio (PSNR), and Structural Similarity Index (SSIM), detects placeholder white-out or significant drift anomalies based on configurable drift tolerance, and generates a perceptual delta heatmap artifact.
   - **Usage**: `{ ""baseline"": ""textures/base.png"", ""candidate"": ""textures/modded.png"", ""tolerance"": 0.05, ""generate_heatmap"": true }`

36. **`kongor_material_inspect`** (alias: `material_inspect`):
   - **Purpose**: K2 `.material` XML definition parsing, VFS layer sampler resolution (`override` > `game` > `base` > `shared` > `.s2z`), and dedicated server shared layer compatibility auditing (`serverCompatible`). Verifies diffuse, normalmap, and specular samplers exist, checks power-of-two (POT) dimensions, flags missing textures in `games/shared` to prevent dedicated server NULL buffer crashes, and validates DXT compression.
   - **Usage**: `{ ""path"": ""heroes/pyromancer/hero.material"" }`

37. **`kongor_scrimmage_launch`** (aliases: `kongor_scrimmage`, `kongor_5v5_launch`):
   - **Purpose**: Automated 5v5 local scrimmage orchestration. Binds to or spawns dedicated game server worker on designated port (default: 11235 or dynamically allocated from port registry), connects local client instance to Legion slot 0, and populates balanced 5v5 rosters via port-addressed AddFakePlayer commands. Authoritatively registers worker in `ServerPortRegistry` and verifies GamePhase 3.
   - **Usage**:
     - Launch 5v5 scrimmage: `{ ""action"": ""launch"", ""map"": ""caldavar"", ""hero"": ""Hero_Pyromancer"", ""port"": 11235 }`
     - Query scrimmage status: `{ ""action"": ""status"", ""port"": 11235 }`
     - List active scrimmage sessions: `{ ""action"": ""list"" }`
     - Stop scrimmage: `{ ""action"": ""stop"", ""port"": 11235 }`

38. **`kongor_bot_choreography`** (alias: `kongor_bot`):
   - **Purpose**: Deterministic bot choreography, combat combo execution, and synchronized client/server scenario orchestration (`target: 'server'`, `'client'`, or `'both'`). Dynamically resolves server port from `ServerPortRegistry` when omitted, and pipes live combat orders and fake player actions to dedicated server workers while coordinating companion client scripts.
   - **Usage**:
     - Run combat preset on server: `{ ""action"": ""preset"", ""preset"": ""kongor_pit_clash"", ""port"": 11235 }`
     - Run synchronized dual client/server scenario: `{ ""action"": ""preset"", ""preset"": ""teamfight_5v5"", ""target"": ""both"" }`
     - Run custom action sequence: `{ ""action"": ""execute"", ""target"": ""server"", ""orders"": [{ ""type"": ""move"", ""bot"": 1, ""pos"": { ""x"": 7500, ""y"": 7000 } }] }`
     - Halt choreography: `{ ""action"": ""stop"", ""port"": 11235 }`

39. **`kongor_network_impair_simulate`** (alias: `kongor_net_impair`):
   - **Purpose**: In-engine and proxy simulation of UDP latency, packet loss, and jitter for network resilience, lag compensation, and delta netcode testing (`target: 'server'`, `'client'`, or `'both'`). Dynamically resolves server port from `ServerPortRegistry` when omitted, and pipes live impairment cvars (`net_lag`, `net_drop`, `net_jitter`) to dedicated servers and player client IPC.
   - **Usage**:
     - Set latency and packet loss: `{ ""action"": ""set"", ""latencyMs"": 150, ""packetLossPercent"": 5.0, ""jitterMs"": 20, ""target"": ""both"" }`
     - Apply named profile: `{ ""action"": ""profile"", ""profile"": ""oceanic_lag"", ""target"": ""server"" }`
     - Reset impairment: `{ ""action"": ""reset"", ""target"": ""both"" }`

40. **`kongor_network_chaos`** (aliases: `network_chaos_inject`, `kongor_net_chaos`):
   - **Purpose**: Chaos engineering and transient network fault injection (`target: 'port'`, `'client'`, `'both'`, or `'session'`). Injects latency spikes (100ms–500ms), burst packet drops (5%–25%), and connection resets with automated auto-healing countdown timers (`durationSeconds: 30`) to prevent permanent test impairment.
   - **Usage**:
     - Inject transient latency: `{ ""action"": ""inject"", ""type"": ""latency"", ""latencyMs"": 250, ""durationSeconds"": 30, ""target"": ""both"" }`
     - Inject spike: `{ ""action"": ""spike"", ""latencyMs"": 400, ""durationSeconds"": 15 }`
     - Inject burst drop: `{ ""action"": ""drop"", ""dropRatePercent"": 15.0, ""durationSeconds"": 10 }`
     - Query active chaos rules: `{ ""action"": ""list"" }`
     - Heal/Clear rules: `{ ""action"": ""heal"", ""target"": ""both"" }`

41. **`kongor_replay_diff`** (alias: `kongor_replay_compare`):
   - **Purpose**: Replay diff and live client/server desync audit tool. Compares packet timelines across multiple `.honreplay` files or audits live tick synchronicity between player client IPC and dedicated game server workers by port (`action: 'live_compare'`).
   - **Usage**:
     - Compare two replays: `{ ""action"": ""diff"", ""replayA"": ""replays/match_A.honreplay"", ""replayB"": ""replays/match_B.honreplay"" }`
     - Live desync audit: `{ ""action"": ""live_compare"", ""port"": 11235 }`

42. **`kongor_game_step`** (aliases: `game_step`, `step_frame`):
   - **Purpose**: Deterministic frame stepping and synchronized `Host.TimeScale` simulation acceleration across client IPC and dedicated server worker streams. Eliminates long bot combat test setup by running simulations at accelerated speed (e.g. 10.0x) with zero prediction desyncs.
   - **Usage**:
     - Step 10 frames: `{ ""action"": ""step"", ""frames"": 10, ""target"": ""both"" }`
     - Accelerate simulation 5x for 3 seconds: `{ ""action"": ""timescale"", ""speed"": 5.0, ""duration_ms"": 3000, ""target"": ""both"" }`
     - Reset to normal 1.0x: `{ ""action"": ""reset"", ""target"": ""both"" }`

43. **`kongor_assert_visual`** (aliases: `assert_visual`, `verify_visual_golden`):
   - **Purpose**: Perceptual golden visual assertion. Crops target UI widgets or viewport regions from GPU captures and asserts visual fidelity against baseline fixtures using Structural Similarity Index (SSIM) and MSE.
   - **Usage**:
     - Assert against golden fixture: `{ ""fixture"": ""hud_health_bar.png"", ""threshold"": 0.95 }`
     - Crop bounding box: `{ ""fixture"": ""minimap.png"", ""crop"": { ""x"": 0, ""y"": 800, ""width"": 280, ""height"": 280 } }`
     - Establish new baseline if missing: `{ ""fixture"": ""new_dialog.png"", ""generate_fixture_if_missing"": true }`

44. **`kongor_fixture_inject`** (aliases: `fixture_inject`, `game_fixture`):
   - **Purpose**: Atomic Game State Fixture Injector. Teleports hero, assigns level, gold, items, skills, spawns creeps, and sets HP/mana in a single atomic transaction for instant scenario staging.
   - **Usage**:
     - Inject scenario: `{ ""action"": ""inject"", ""hero"": ""Hero_Pyromancer"", ""level"": 16, ""gold"": 5000, ""teleport"": { ""x"": 0, ""y"": 0, ""z"": 100 }, ""items"": [""Item_PortalKey"", ""Item_PostHaste""] }`
     - Preset scenario: `{ ""action"": ""preset"", ""preset"": ""roshan_contest"" }`
     - Reset state: `{ ""action"": ""reset"" }`

45. **`kongor_crash_triage`** (aliases: `crash_triage`, `kongor_triage`):
   - **Purpose**: Automated Crash & Fault Triangulation. Parses minidumps (.dmp) and diagnostic text logs, maps ASLR offsets across k2/cgame/game_shared/vid_d3d9, extracts faulting registers (RCX, RDX, R8, R9, RSP), and generates ready-to-run Ghidra decompile commands in < 500ms.
   - **Usage**:
     - Triage latest dump: `{ ""action"": ""triage"" }`
     - Triage specific dump: `{ ""action"": ""triage"", ""dump_path"": ""logs/crash_dump.dmp"" }`
     - List recent crashes: `{ ""action"": ""list"" }`

46. **`kongor_net_packet_fuzz`** (aliases: `packet_fuzz`, `net_fuzz`):
   - **Purpose**: Network Packet Dissector & Mutation Fuzzer. Dissects raw K2 UDP/TCP binary frames (reliable/unreliable headers, sequences, acks, opcodes) and applies bit-flips, truncations, and boundary mutations to measure network resilience and detect desyncs.
   - **Usage**:
     - Dissect packet: `{ ""action"": ""dissect"", ""packet_hex"": ""03011a041904..."" }`
     - Fuzz mutation: `{ ""action"": ""fuzz"", ""mutation_strategy"": ""bit_flip"", ""mutation_rate"": 0.05, ""packet_count"": 5 }`
     - Verify desync status: `{ ""action"": ""verify_desync"", ""target_port"": 11235 }`

48. **`kongor_memory_sync`** (alias: `memory_sync`):
   - **Purpose**: Project Knowledge Graph & Persistent Memory Bridge. Mines repository truth (`AGENTS.md`, `docs/ISSUES.md`, port mappings, engine invariants) into structured entities and relations conforming to `@modelcontextprotocol/server-memory`.
   - **Usage**:
     - Extract knowledge graph: `{ ""action"": ""extract"" }`
     - Export to artifacts: `{ ""action"": ""export"", ""output_path"": ""artifacts/memory/kongor_memory_graph.json"" }`
     - Search entities/relations: `{ ""action"": ""query"", ""query"": ""UIManager"" }`
     - Query memory status: `{ ""action"": ""status"" }`

49. **`kongor_vfs_locate`** (alias: `vfs_locate`):
   - **Purpose**: Fast K2 Virtual File System Disk Locator & Everything Indexer Bridge. Maps virtual asset paths to physical files across mount layers (`override -> extensions -> updates -> configuration -> game -> shared -> base`) with sub-ms Voidtools Everything NTFS MFT discovery and cache invalidation.
   - **Usage**:
     - Locate asset on disk: `{ ""virtual_path"": ""/heroes/pyromancer/ability_01/icon.tga"" }`
     - Search with cache refresh: `{ ""virtual_path"": ""pyromancer.entity"", ""refresh_cache"": true }`

50. **`kongor_crash_patch`** (alias: `crash_patch`):
   - **Purpose**: Machine Code Crash Patcher & ABI-Safe Detour Verifier. Synthesizes x64 machine code detours (null-checks, zero-divisor guards, early returns) strictly conforming to Windows x64 calling conventions (16-byte stack alignment, non-volatile register preservation), performs pre-flight byte verification (`dry_run`), and idempotently applies patches with `.bak` safety.
   - **Usage**:
     - Synthesize detour recipe: `{ ""action"": ""synthesize"", ""target_module"": ""k2_x64.dll"", ""faulting_rva"": ""0x0005B0D0"", ""exception_code"": ""0xC0000005"" }`
     - Dry-run verify bytes: `{ ""action"": ""dry_run"", ""target_module"": ""k2_x64.dll"", ""faulting_rva"": ""0x0005B0D0"", ""original_bytes_hex"": ""48 8B 41 18"", ""detour_bytes_hex"": ""48 85 C9 74 05 ..."" }`
     - Apply patch with .bak: `{ ""action"": ""apply"", ""target_module"": ""k2_x64.dll"", ""faulting_rva"": ""0x0005B0D0"" }`
     - Revert patch: `{ ""action"": ""revert"", ""target_module"": ""k2_x64.dll"" }`

51. **`kongor_asset_bake_pipeline`** (alias: `asset_bake_pipeline`):
   - **Purpose**: Generative 2D/3D Asset Baking Pipeline. Bridges generative AI outputs (ComfyUI, Meshy) to the K2 engine with suffix-aware texture compression (BC7 sRGB color, BC5 linear tangent normal with OpenGL Y-inversion, BC4 scalar specular), POT dimension validation, missing texture pre-flight guard, K2 `.material` XML synthesis, and hot-reloading.
   - **Usage**:
     - Inspect texture & recommend flags: `{ ""action"": ""inspect"", ""texture_path"": ""hero_norm.png"", ""width"": 1024, ""height"": 1024, ""invert_normal_y"": true }`
     - Bake K2 .material XML: `{ ""action"": ""bake_material"", ""material_name"": ""pyromancer_alt1"", ""shader_type"": ""hero"", ""samplers"": { ""diffuse"": ""pyromancer_color.dds"", ""normalmap"": ""pyromancer_norm.dds"" } }`
     - Hot-reload live materials: `{ ""action"": ""hot_reload"", ""target"": ""/heroes/pyromancer"" }`

52. **`kongor_audio_studio`** (alias: `audio_studio`):
   - **Purpose**: Audio Voice & SFX Studio Pipeline. Broadcast audio loudness normalization (EBU R128 -16 LUFS, -1.5 dBFS peak), channel layout routing (mono WAV for 3D spatialization vs stereo OGG for music), multi-sample randomized K2 `.sound` XML definition synthesis, ElevenLabs prompt handoff, and in-engine 3D preview.
   - **Usage**:
     - Normalize audio with FFmpeg: `{ ""action"": ""normalize"", ""input_file"": ""voice_raw.wav"", ""audio_type"": ""voice"", ""lufs"": -16.0, ""true_peak"": -1.5 }`
     - Synthesize voice line with ElevenLabs: `{ ""action"": ""synthesize_voice"", ""prompt"": ""By fire be purged!"", ""voice_id"": ""pNInz6obpgDQGcFmaJgB"" }`
     - Bake multi-sample K2 .sound XML: `{ ""action"": ""bake_sound"", ""sound_name"": ""hero_attack"", ""sample_files"": [""attack_01.wav"", ""attack_02.wav""], ""volume"": 1.0, ""falloff"": 1200 }`
     - In-engine 3D audition: `{ ""action"": ""preview"", ""sound_name"": ""hero_attack"", ""sound_path"": ""sounds/attack_01.wav"", ""x"": 0, ""y"": 0, ""z"": 100 }`

54. **`kongor_self_heal`** (alias: `self_heal`):
   - **Purpose**: Autonomous Self-Healing Pipeline & Developer Issues Backplane. Watches runtime engine logs across game client and dedicated servers, ingests and deduplicates errors with dynamic token normalization and SHA-256 fingerprints, executes the Dual-State Reflex loop (Record First -> Immediate Reflex Healing -> Auto-Resolve with Receipt), and maintains the prioritized Self-Healing Journey backlog.
   - **Usage**:
     - Ingest log file & execute reflex: `{ ""action"": ""scan_and_ingest"", ""log_path"": ""console.log"" }`
     - Ingest raw error trace: `{ ""action"": ""scan_and_ingest"", ""raw_log"": ""[ERROR] Access violation at k2_x64.dll+0x3A20"" }`
     - Trigger specific reflex recipe: `{ ""action"": ""auto_heal_reflex"", ""fingerprint"": ""a1b2c3d4..."" }`
     - View prioritized pending journey backlog: `{ ""action"": ""list_pending"", ""limit"": 20 }`
     - Full journey metrics & summary: `{ ""action"": ""journey_summary"" }`
     - Sync resolved receipts to docs/ISSUES.md & memory: `{ ""action"": ""sync_tracker"" }`

## K2 UI Scripting, Headless Clicking & Common Pitfalls

1. **Global UI Manager is `UIManager`, NEVER `Windows`**:
   - Incorrect: `Windows.main:GetWidget('button')` -> Throws `attempt to index global 'Windows' (a nil value)`.
   - Correct: `UIManager.GetInterface('main'):GetWidget('widget_name')` (or `GetWidget('widget_name')` inside interface context).

2. **`DoEvent()` Signature Rules**:
   - `widget:DoEvent()` takes NO parameters. It triggers the XML `onevent` script.
   - `widget:DoEventN(index)` takes an INTEGER index (e.g. `0`, `1`, `2`) for `onevent0`, `onevent1`, etc.
   - Never pass event names like `'onclick'` to `DoEvent`. It throws a type mismatch error.

3. **Direct Practice Match Launch Without UI Navigation**:
   - Best Approach: Use `kongor_match_launch` (`{ ""mode"": ""practice"", ""map"": ""caldavar"", ""hero"": ""Hero_Pyromancer"" }`).
   - Console Command: `StartGame practice GameName map:caldavar allheroes:true`.
   - Widget Trigger: The system bar quickstart button is `sysbar_quickStart`, not `practice_button`.

## MCP Conversation History & Traffic Logging

All inbound requests and outbound responses are stored in `logs/MCP Conversation History/`:
- **Markdown Session Timeline (`session_YYYYMMDD_HHMMSS.md`)**: Human-readable record of requests, execution channels (`stdin`, `in_game_console`, `ipc`, `invoke_pattern`), and engine outputs.
- **Traffic NDJSON (`mcp_traffic_YYYY-MM-DD.ndjson`)**: Machine-parseable JSON-RPC stream with millisecond timings.
- **Automated Size Management**: Base64 screenshots scrubbed, 10 MB file rolling, 50 MB FIFO quota, 14-day rolling retention.

## Client Command Error Collection & Self-Healing Remediation

When executing commands via `kongor_game_client_command`, `kongor_game_command`, or `kongor_game_lua`:
- **Persistent Error Log**: All client command execution failures and K2 engine errors are recorded in `logs/MCP Conversation History/client_command_errors.ndjson` and kept in a 50-record memory buffer.
- **Query Failures**: Call `kongor_game_client_manage action: ""errors""` or `kongor_game_command action: ""errors""` to inspect recent command errors, root cause categories, and suggested remediations.
- **Automated Self-Healing**: Common syntax and reference pitfalls (e.g. `Windows.main` -> `UIManager.GetInterface('main')`, `practice_button` -> `sysbar_quickStart`, `DoEvent('onclick')` -> `DoEvent()`, missing `Script` prefix) are automatically analyzed, improved, and auto-retried with `selfHealed: true`.

## Best Practices & Operating Rules

- **Dedicated Server Command Routing**: Always provide target `port` (e.g. `11235`) when piping commands to dedicated servers (`kongor_game_server_command` or `kongor_game_command port: 11235`). Never use generic unnamed pipes.
- **Strict Stdio Separation**: All MCP communication occurs over stdin/stdout as JSON-RPC 2.0. Never write non-JSON data to stdout. All diagnostic logs are written to stderr.
- **Port Verification**: Use `kongor_server_manage` with action `""status""` to ensure Master Server (`55555`) and Chat Server (`11031`) are listening before initiating game client connections.
- **Pre-Flight UI Verification**: Use `kongor_k2_ui_inspect` to verify widget existence and visibility before dispatching UI triggers.
- **Vulkan Offscreen Capture & Zero Desktop Disruption**: Always run and test the game client (`hon_x64.exe`) in headless / offscreen mode (`window_mode: ""offscreen""`, `+Set vid_pos -32000 -32000`). Visual assertions, regression tests, and screenshots default to Tier 0 Direct GPU DMA Zero-Copy Vulkan Offscreen Capture (`VK_EXT_external_memory_host`) with 0 ms dwell time and 0% window exposure, guaranteeing zero desktop popups, focus stealing, or presentation stalls.
- **Mandatory MCP Issue Logging (`docs/MCP_ISSUES.md`)**: Whenever ANY MCP command or tool call fails, times out, or produces unexpected results, immediately log it to `docs/MCP_ISSUES.md` with exact parameters, problem analysis, logs, and a concrete next-time prevention rule so future calls succeed on the first attempt.
";
    }

    private static string CapitalizeTitle(string key)
    {
        if (string.IsNullOrEmpty(key)) return key;
        string[] parts = key.Split(['_', '-'], StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", parts.Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }
}