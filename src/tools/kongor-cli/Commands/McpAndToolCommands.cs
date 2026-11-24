// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.ComponentModel;
using System.Text.Json;
using Kongor.Cli.Mcp.Logging;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Registration;
using Kongor.Cli.Mcp.Services;
using Spectre.Console;
using Spectre.Console.Cli;
using static Kongor.Cli.Program;

namespace Kongor.Cli.Commands;

public sealed class McpCommandSettings : CommandSettings
{
    [CommandOption("-r|--register|--register-antigravity")]
    [Description("Register tool schema JSON files and agent guidelines to Antigravity directory on startup")]
    public bool ShouldRegister { get; init; }

    [CommandOption("--antigravity-path|--schema-dir")]
    [Description("Custom target directory path for Antigravity schema registration")]
    public string? AntigravityPath { get; init; }

    [CommandOption("--register-only")]
    [Description("Register Antigravity tool schemas and exit immediately without starting stdio pump")]
    public bool RegisterOnly { get; init; }

    [CommandOption("-t|--transport")]
    [Description("MCP transport mode: 'stdio' (default — official ModelContextProtocol SDK stdio transport), 'http'/'sse' (Streamable HTTP/SSE daemon), or 'legacy-stdio' (fallback custom JSON-RPC pump)")]
    [DefaultValue("stdio")]
    public string Transport { get; init; } = "stdio";

    [CommandOption("--include-sidecars")]
    [Description("Federate all 11 Python MCP sidecars into JIT sidecar discovery without bloating startup tool tokens (< 12k tokens)")]
    public bool IncludeSidecars { get; init; }

    [CommandOption("-p|--port")]
    [Description("Listen port for Streamable HTTP/SSE transport when --transport http is used (default: 19520)")]
    [DefaultValue(19520)]
    public int Port { get; init; } = 19520;

    [CommandOption("--host")]
    [Description("Listen bind address for Streamable HTTP/SSE transport (default: 127.0.0.1)")]
    [DefaultValue("127.0.0.1")]
    public string Host { get; init; } = "127.0.0.1";

    [CommandOption("--read-only")]
    [Description("Run MCP server in read-only mode (blocks mutating tools and allows only inspection/telemetry tools)")]
    public bool ReadOnly { get; init; }

    [CommandOption("--toolsets")]
    [Description("Comma-separated toolset filter to advertise (default: 'core' when unset, cutting prompt tokens by ~82%; set to 'all' or '*' to expose all tools)")]
    public string? Toolsets { get; init; }

    [CommandOption("--ide")]
    [Description("Export/merge MCP server configuration for target IDE ('antigravity', 'cursor', 'vscode', 'zed', 'claude', 'codex', or 'all')")]
    public string? Ide { get; init; }

    [CommandOption("--workspace-config")]
    [Description("Optional workspace directory for workspace-scoped .cursor/mcp.json or .vscode/mcp.json export")]
    public string? WorkspaceConfigDir { get; init; }
}

public sealed class McpCommand : AsyncCommand<McpCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, McpCommandSettings settings)
    {
        string transport = (settings.Transport ?? "stdio").Trim().ToLowerInvariant();
        bool isHttpTransport = transport is "http" or "sse" or "streamable-http";
        bool registerOnly = settings.RegisterOnly;

        Stream? rawStdout = null;
        StreamWriter? rpcOut = null;
        TextWriter originalOut = Console.Out;

        if (!registerOnly && !isHttpTransport)
        {
            // Defense-in-depth stdout firewall: redirect Console.Out to Console.Error immediately so zero
            // startup/library Console.WriteLine calls can ever corrupt JSON-RPC framing over stdio.
            rawStdout = Console.OpenStandardOutput();
            rpcOut = new StreamWriter(rawStdout, new System.Text.UTF8Encoding(false)) { AutoFlush = true };
            Console.SetOut(Console.Error);
        }

        try
        {
            bool shouldRegister = settings.ShouldRegister;
            string? schemaPath = settings.AntigravityPath;
            McpToolRegistry registry = new(registerDefaults: true)
            {
                IncludeSidecars = settings.IncludeSidecars ||
                                  string.Equals(Environment.GetEnvironmentVariable("KONGOR_MCP_INCLUDE_SIDECARS"), "1", StringComparison.OrdinalIgnoreCase)
            };
            string? effectiveToolsets = settings.Toolsets ?? Environment.GetEnvironmentVariable("KONGOR_MCP_TOOLSETS");
            if (string.IsNullOrWhiteSpace(effectiveToolsets))
            {
                registry.SetDefaultToolsets("core");
            }
            else
            {
                registry.SetDefaultToolsets(effectiveToolsets);
            }

            if (shouldRegister || registerOnly || !string.IsNullOrWhiteSpace(schemaPath))
            {
                RegistrationResult regResult = AntigravityRegistrar.Register(schemaPath, registry);
                if (regResult.Success)
                {
                    Console.Error.WriteLine($"[MCP Server] Successfully registered {regResult.GeneratedFiles.Count} tool schemas to '{regResult.TargetDirectory}'.");
                }
                else
                {
                    Console.Error.WriteLine($"[MCP Server] Warning: Failed to register Antigravity schemas: {regResult.ErrorMessage}");
                }

                if (registerOnly)
                {
                    if (!string.IsNullOrWhiteSpace(settings.Ide))
                    {
                        var (cmd, args, env) = AntigravityRegistrar.ResolveCanonicalDeveloperMcpCommand(Environment.ProcessPath);
                        var exports = IdeConfigExporter.Export(settings.Ide, cmd, args, env, workspaceDir: settings.WorkspaceConfigDir);
                        foreach (var exp in exports)
                        {
                            Console.Error.WriteLine(exp.Success
                                ? $"[MCP Server] Merged '{exp.Ide}' config -> {exp.TargetFilePath}"
                                : $"[MCP Server] Error exporting '{exp.Ide}': {exp.ErrorMessage}");
                        }
                        if (exports.Any(e => !e.Success))
                        {
                            return 1;
                        }
                    }
                    return regResult.Success ? 0 : 1;
                }
            }

            // Always auto-ensure Antigravity registration in default path if directory doesn't exist or assembly is newer
            try
            {
                string markerSchema = System.IO.Path.Combine(AntigravityRegistrar.DefaultDirectory, "kongor_health_check.json");
                string asmPath = typeof(McpCommand).Assembly.Location;
                bool isStale = !File.Exists(markerSchema) ||
                    (!string.IsNullOrEmpty(asmPath) && File.Exists(asmPath) &&
                     File.GetLastWriteTimeUtc(asmPath) > File.GetLastWriteTimeUtc(markerSchema));

                if (!Directory.Exists(AntigravityRegistrar.DefaultDirectory) || isStale)
                {
                    AntigravityRegistrar.Register(registry: registry);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[MCP Server] Debug: Auto-registration check skipped: {ex.Message}");
            }

            await using McpConversationLogger conversationLogger = new();
            using McpServer server = new(
                registry,
                Console.Error,
                conversationLogger: conversationLogger,
                readOnlyMode: settings.ReadOnly);
            server.EnsureOutcomeLogInitialized();
            using CancellationTokenSource cts = new();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            if (isHttpTransport)
            {
                await OfficialMcpHttpHost.RunHttpDaemonAsync(server, settings.Host, settings.Port, cts.Token).ConfigureAwait(false);
                return 0;
            }

            if (transport is "legacy-stdio")
            {
                await server.RunAsync(Console.In, rpcOut ?? originalOut, cts.Token).ConfigureAwait(false);
                return 0;
            }

            // Default stdio transport: canonical official ModelContextProtocol SDK transport (sdk-stdio)
            await using Stream rawStdin = Console.OpenStandardInput();
            await OfficialMcpHttpHost.RunSdkStdioAsync(server, rawStdin, rawStdout, cts.Token).ConfigureAwait(false);
            return 0;
        }
        finally
        {
            Console.SetOut(originalOut);
            if (rpcOut is not null) await rpcOut.DisposeAsync().ConfigureAwait(false);
            if (rawStdout is not null) await rawStdout.DisposeAsync().ConfigureAwait(false);
        }
    }
}

public sealed class K2LintCommandSettings : CommandSettings
{
    [CommandArgument(0, "[path]")]
    [Description("Path to file, directory, or pattern to lint")]
    [DefaultValue("games/client/override/ui")]
    public string Path { get; init; } = "games/client/override/ui";

    [CommandOption("--fix-bom")]
    [Description("Automatically fix and strip UTF-8 Byte Order Marks")]
    public bool FixBom { get; init; }

    [CommandOption("--no-recurse")]
    [Description("Disable recursive scanning of child directories")]
    public bool NoRecurse { get; init; }
}

public sealed class K2LintCommand : AsyncCommand<K2LintCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, K2LintCommandSettings settings)
    {
        string wsRoot = FindRepoRoot();
        string resolvedPath = Infrastructure.CliPathResolver.ResolveInputPath(settings.Path, wsRoot);
        K2LintService linter = new();
        var report = await linter.LintAsync(resolvedPath, recursive: !settings.NoRecurse, fixBom: settings.FixBom);
        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return report.ErrorCount == 0 ? 0 : 1;
    }
}

public sealed class EngineInspectCommandSettings : CommandSettings
{
    [CommandArgument(0, "[target]")]
    [Description("Target binary name or relative path")]
    [DefaultValue("cgame_x64.dll")]
    public string Target { get; init; } = "cgame_x64.dll";

    [CommandArgument(1, "[query]")]
    [Description("Symbol name, function name, or CVar filter query")]
    public string? Query { get; init; }

    [CommandOption("-c|--category")]
    [Description("Category filter ('all', 'export', 'lua_binding', 'cvar', 'cmd')")]
    [DefaultValue("all")]
    public string Category { get; init; } = "all";

    [CommandOption("-n|--limit")]
    [Description("Maximum number of matched symbols to return")]
    [DefaultValue(50)]
    public int Limit { get; init; } = 50;
}

public sealed class EngineInspectCommand : AsyncCommand<EngineInspectCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, EngineInspectCommandSettings settings)
    {
        EngineInspectService inspector = new();
        var report = await inspector.InspectBinaryAsync(settings.Target, settings.Query, settings.Category, settings.Limit);
        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return report.Status == "success" ? 0 : 1;
    }
}

public sealed class ReplayCommand : Command<RawArgsSettings>
{
    public override int Execute(CommandContext context, RawArgsSettings settings)
    {
        string wsRoot = FindRepoRoot();
        string[] raw = CommandArgsHelper.GetEffectiveArgs(context, settings);
        if (raw.Length > 0 && raw[0].Equals("highlight", StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine("[bold cyan]🎬 Running Automated Game-Winning Teamfight Highlight Clipper...[/]");
            string[] normHighlight = Infrastructure.CliPathResolver.NormalizeForwardedPathArgs([.. raw.Skip(1)], wsRoot);
            return RunPythonScript("tools/cinematic/highlight_cli.py", normHighlight);
        }

        string[] forwardArgs = raw.Length > 0 && raw[0].Equals("render", StringComparison.OrdinalIgnoreCase)
            ? [.. raw.Skip(1)]
            : raw;
        string[] normRender = Infrastructure.CliPathResolver.NormalizeForwardedPathArgs(forwardArgs, wsRoot);
        AnsiConsole.MarkupLine("[bold cyan]🎬 Executing Replay-to-MP4 Renderer Service...[/]");
        return ForwardToProject("src/extensions/replay-renderer/ReplayRenderer.Service.csproj", normRender);
    }
}

public sealed class HighlightCommand : Command<RawArgsSettings>
{
    public override int Execute(CommandContext context, RawArgsSettings settings)
    {
        string wsRoot = FindRepoRoot();
        string[] raw = Infrastructure.CliPathResolver.NormalizeForwardedPathArgs(
            CommandArgsHelper.GetEffectiveArgs(context, settings),
            wsRoot);
        AnsiConsole.MarkupLine("[bold cyan]🎬 Running Automated Game-Winning Teamfight Highlight Clipper...[/]");
        return RunPythonScript("tools/cinematic/highlight_cli.py", raw);
    }
}
