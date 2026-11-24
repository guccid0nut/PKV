// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.ComponentModel;
using System.Diagnostics;
using Spectre.Console;
using Spectre.Console.Cli;
using static Kongor.Cli.Program;

namespace Kongor.Cli.Commands;

public sealed class ModelCommand : Command<RawArgsSettings>
{
    public override int Execute(CommandContext context, RawArgsSettings settings)
    {
        string[] raw = CommandArgsHelper.GetEffectiveArgs(context, settings);
        bool isMixamo = false;

        List<string> argsList = [];
        foreach (string arg in raw)
        {
            if (arg.Equals("mixamo", StringComparison.OrdinalIgnoreCase))
            {
                isMixamo = true;
            }
            else
            {
                argsList.Add(arg);
            }
        }

        if (isMixamo && !argsList.Contains("--mixamo"))
        {
            argsList.Add("--mixamo");
        }

        if (isMixamo)
        {
            AnsiConsole.MarkupLine("[bold cyan]🕺 Running HoN to Mixamo Character Exporter (Auto-Rig Ready)...[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("[bold cyan]🎨 Running HoN Model & Animation Exporter...[/]");
        }

        string repo = FindRepoRoot();
        string[] normalizedArgs = Infrastructure.CliPathResolver.NormalizeForwardedPathArgs([.. argsList], repo);
        return RunPythonScript("tools/hon-model-tools/hon_model_converter.py", normalizedArgs);
    }
}

public sealed class MixamoCommand : Command<RawArgsSettings>
{
    public override int Execute(CommandContext context, RawArgsSettings settings)
    {
        string[] raw = CommandArgsHelper.GetEffectiveArgs(context, settings);
        AnsiConsole.MarkupLine("[bold cyan]🕺 Running HoN to Mixamo Character Exporter (Auto-Rig Ready)...[/]");
        List<string> argsList = [.. raw];
        if (!argsList.Contains("--mixamo"))
        {
            argsList.Add("--mixamo");
        }
        string repo = FindRepoRoot();
        string[] normalizedArgs = Infrastructure.CliPathResolver.NormalizeForwardedPathArgs([.. argsList], repo);
        return RunPythonScript("tools/hon-model-tools/hon_model_converter.py", normalizedArgs);
    }
}

public sealed class BlenderCommand : Command<RawArgsSettings>
{
    public override int Execute(CommandContext context, RawArgsSettings settings)
    {
        string[] raw = CommandArgsHelper.GetEffectiveArgs(context, settings);
        if (raw.Length > 0)
        {
            string first = raw[0].ToLowerInvariant();
            if (first is "stop")
            {
                return StopBlenderProcesses();
            }

            if (first is "install" or "setup")
            {
                AnsiConsole.MarkupLine("[bold cyan]📦 Running Cross-Platform Blender 4.2+ Installer...[/]");
                return RunPythonScript("src/tools/blender/install_blender.py", [.. raw.Skip(1)]);
            }

            if (first is "uninstall" or "remove")
            {
                AnsiConsole.MarkupLine("[bold yellow]🗑️  Running Blender Uninstaller...[/]");
                return RunPythonScript("src/tools/blender/install_blender.py", ["--uninstall"]);
            }
        }

        string repo = FindRepoRoot();
        string? blenderBin = FindBlenderExecutable(repo);

        if (blenderBin == null)
        {
            AnsiConsole.MarkupLine("[bold yellow]Blender not found locally. Running installer...[/]");
            int installCode = RunPythonScript("src/tools/blender/install_blender.py", ["install"]);
            if (installCode != 0)
            {
                AnsiConsole.MarkupLine("[bold red]❌ Blender installation failed.[/]");
                return installCode;
            }
            blenderBin = FindBlenderExecutable(repo);
        }

        if (blenderBin != null && File.Exists(blenderBin))
        {
            AnsiConsole.MarkupLine($"[bold cyan]🎨 Launching Blender 3D:[/] [green]{Markup.Escape(blenderBin)}[/]");
            string[] normalizedBlenderArgs = Infrastructure.CliPathResolver.NormalizeForwardedPathArgs(raw, repo);
            string argsStr = FormatProcessArgs(normalizedBlenderArgs);
            if (normalizedBlenderArgs.Length > 0)
            {
                return RunProcess(blenderBin, argsStr, repo);
            }
            else
            {
                Process.Start(new ProcessStartInfo(blenderBin) { UseShellExecute = true });
                return 0;
            }
        }
        else
        {
            AnsiConsole.MarkupLine("[bold red]❌ Could not locate or launch Blender binary.[/]");
            return 1;
        }
    }

    public static string? FindBlenderExecutable(string repo)
    {
        // 1. Explicit environment variable
        string? envBlender = Environment.GetEnvironmentVariable("BLENDER_PATH");
        if (!string.IsNullOrWhiteSpace(envBlender) && File.Exists(envBlender))
        {
            return envBlender;
        }

        // 2. Known local candidate directories in repo
        string[] localCandidates =
        [
            Path.Combine(repo, "src/tools/blender/blender-5.2.1-windows-x64/blender.exe"),
            Path.Combine(repo, "src/tools/blender/blender-4.2.1-windows-x64/blender.exe"),
            Path.Combine(repo, "tools/blender/blender-4.2.1-windows-x64/blender.exe")
        ];
        foreach (string c in localCandidates)
        {
            if (File.Exists(c)) return c;
        }

        // 3. Search under src/tools/blender
        string searchDir = Path.Combine(repo, "src/tools/blender");
        if (Directory.Exists(searchDir))
        {
            string? found = Directory.GetFiles(searchDir, "blender.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (found != null) return found;
        }

        // 4. System PATH lookup
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
        {
            char sep = OperatingSystem.IsWindows() ? ';' : ':';
            string binName = OperatingSystem.IsWindows() ? "blender.exe" : "blender";
            foreach (string p in pathEnv.Split(sep, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string full = Path.Combine(p, binName);
                if (File.Exists(full)) return full;
            }
        }

        // 5. Standard Windows installation directories
        if (OperatingSystem.IsWindows())
        {
            string[] systemDirs =
            [
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Path.Combine(repo, ".."),
                "D:\\"
            ];

            foreach (string baseDir in systemDirs)
            {
                if (!Directory.Exists(baseDir)) continue;

                // Check Blender Foundation folder
                string bf = Path.Combine(baseDir, "Blender Foundation");
                if (Directory.Exists(bf))
                {
                    try
                    {
                        string? bfFound = Directory.GetFiles(bf, "blender.exe", SearchOption.AllDirectories).FirstOrDefault();
                        if (bfFound != null) return bfFound;
                    }
                    catch { }
                }

                // Check direct portable blender folders (e.g. D:\blender* or user profile)
                try
                {
                    foreach (string d in Directory.GetDirectories(baseDir, "*blender*", SearchOption.TopDirectoryOnly))
                    {
                        string directBin = Path.Combine(d, "blender.exe");
                        if (File.Exists(directBin)) return directBin;
                    }
                }
                catch { }
            }
        }

        return null;
    }
}

public sealed class InstallBlenderCommand : Command<RawArgsSettings>
{
    public override int Execute(CommandContext context, RawArgsSettings settings)
    {
        string[] raw = CommandArgsHelper.GetEffectiveArgs(context, settings);
        AnsiConsole.MarkupLine("[bold cyan]📦 Running Cross-Platform Blender 4.2+ Installer...[/]");
        return RunPythonScript("src/tools/blender/install_blender.py", raw);
    }
}

public sealed class UninstallBlenderCommand : Command<EmptyCommandSettings>
{
    public override int Execute(CommandContext context, EmptyCommandSettings settings)
    {
        AnsiConsole.MarkupLine("[bold yellow]🗑️  Running Blender Uninstaller...[/]");
        return RunPythonScript("src/tools/blender/install_blender.py", ["--uninstall"]);
    }
}

public sealed class ComfyCommand : Command<RawArgsSettings>
{
    public override int Execute(CommandContext context, RawArgsSettings settings)
    {
        string[] raw = CommandArgsHelper.GetEffectiveArgs(context, settings);
        if (raw.Length > 0)
        {
            string first = raw[0].ToLowerInvariant();
            if (first is "stop")
            {
                return StopComfyUI();
            }

            if (first is "install" or "setup")
            {
                AnsiConsole.MarkupLine("[bold magenta]📦 Running Automated ComfyUI & MCP Environment Installer...[/]");
                return RunPythonScript("src/tools/comfyui/install_comfyui.py", [.. raw.Skip(1)]);
            }

            if (first is "uninstall" or "remove")
            {
                AnsiConsole.MarkupLine("[bold yellow]🗑️  Running ComfyUI & MCP Uninstaller...[/]");
                return RunPythonScript("src/tools/comfyui/install_comfyui.py", ["--uninstall"]);
            }
        }

        AnsiConsole.MarkupLine("[bold magenta]🎨 Launching ComfyUI Generative AI Server (http://127.0.0.1:8188)...[/]");
        return RunPythonScript("src/tools/comfyui/launch_comfyui.py", raw);
    }
}

public sealed class InstallComfyCommand : Command<RawArgsSettings>
{
    public override int Execute(CommandContext context, RawArgsSettings settings)
    {
        string[] raw = CommandArgsHelper.GetEffectiveArgs(context, settings);
        AnsiConsole.MarkupLine("[bold magenta]📦 Running Automated ComfyUI & MCP Environment Installer...[/]");
        return RunPythonScript("src/tools/comfyui/install_comfyui.py", raw);
    }
}

public sealed class UninstallComfyCommand : Command<EmptyCommandSettings>
{
    public override int Execute(CommandContext context, EmptyCommandSettings settings)
    {
        AnsiConsole.MarkupLine("[bold yellow]🗑️  Running ComfyUI & MCP Uninstaller...[/]");
        return RunPythonScript("src/tools/comfyui/install_comfyui.py", ["--uninstall"]);
    }
}

public sealed class ComfyMcpCommand : Command<RawArgsSettings>
{
    public override int Execute(CommandContext context, RawArgsSettings settings)
    {
        string[] raw = CommandArgsHelper.GetEffectiveArgs(context, settings);
        AnsiConsole.MarkupLine("[bold magenta]🔌 Starting ComfyUI MCP Stdio Server...[/]");
        return RunPythonScript("src/tools/comfyui/mcp_server.py", raw);
    }
}

public sealed class DocsCommand : Command<RawArgsSettings>
{
    public override int Execute(CommandContext context, RawArgsSettings settings)
    {
        string[] raw = CommandArgsHelper.GetEffectiveArgs(context, settings);
        AnsiConsole.MarkupLine("[bold cyan]📖 Launching ProjectKongor Developer Documentation Hub (DocFX)...[/]");
        return RunPythonScript("tools/docgen/serve_docs.py", raw);
    }
}

internal sealed class RefreshCommandSettings : CommandSettings
{
    [CommandOption("--ide")]
    [Description("Export/merge TAO MCP server configuration for target IDE ('antigravity', 'cursor', 'vscode', 'zed', 'claude', 'codex', or 'all')")]
    public string? Ide { get; init; }

    [CommandOption("--config-only")]
    [Description("Export/merge IDE MCP configuration without rebuilding global CLI binary")]
    public bool ConfigOnly { get; init; }

    [CommandOption("--workspace-config")]
    [Description("Optional workspace directory for workspace-scoped .cursor/mcp.json or .vscode/mcp.json export")]
    public string? WorkspaceConfigDir { get; init; }

    [CommandOption("--prefer-global-cli")]
    [Description("Prefer global ~/.kongor/bin/kongor executable in exported IDE configuration")]
    public bool PreferGlobalCli { get; init; }

    [CommandOption("--no-build")]
    [Description("Skip recompiling Kongor.Cli and Kongor.Shim and stage existing Release binaries directly")]
    public bool NoBuild { get; init; }
}

internal sealed class RefreshCommand : Command<RefreshCommandSettings>
{
    public override int Execute(CommandContext context, RefreshCommandSettings settings)
    {
        int rc = 0;
        if (!settings.ConfigOnly)
        {
            rc = RefreshCli(noBuild: settings.NoBuild);
            if (rc != 0)
            {
                return rc;
            }
        }

        if (!string.IsNullOrWhiteSpace(settings.Ide))
        {
            string? bundleRoot = null;
            string cmd;
            IReadOnlyList<string> args;
            IReadOnlyDictionary<string, string>? env = null;

            if (Kongor.Core.Configuration.BundledLayoutResolver.TryResolve(out var layout))
            {
                cmd = layout.KongorCliExe;
                args = ["mcp"];
                bundleRoot = layout.Root;
            }
            else
            {
                (cmd, args, env) = Kongor.Cli.Mcp.Registration.AntigravityRegistrar.ResolveCanonicalDeveloperMcpCommand(
                    Environment.ProcessPath,
                    preferGlobalCli: settings.PreferGlobalCli);
            }

            if (settings.Ide.Contains("antigravity", StringComparison.OrdinalIgnoreCase) ||
                settings.Ide.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                Kongor.Cli.Mcp.Registration.AntigravityRegistrar.Register();
            }

            var exports = Kongor.Cli.Mcp.Registration.IdeConfigExporter.Export(
                settings.Ide,
                cmd,
                args,
                env,
                bundleRoot,
                workspaceDir: settings.WorkspaceConfigDir);

            foreach (var item in exports)
            {
                if (item.Success)
                {
                    AnsiConsole.MarkupLine($"[green]✓ Merged MCP config for '{Markup.Escape(item.Ide)}':[/] [white]{Markup.Escape(item.TargetFilePath)}[/]");
                }
                else
                {
                    AnsiConsole.MarkupLine($"[red]✗ Failed to export MCP config for '{Markup.Escape(item.Ide)}': {Markup.Escape(item.ErrorMessage ?? "Unknown error")}[/]");
                    rc = 1;
                }
            }
        }

        return rc;
    }
}

public sealed class AskCommand : Command<RawArgsSettings>
{
    public override int Execute(CommandContext context, RawArgsSettings settings)
    {
        string[] raw = CommandArgsHelper.GetEffectiveArgs(context, settings);
        if (raw.Length == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Please provide a question (e.g. 'kongor ask how do i play?' or 'kongor ? <question>')[/]");
            return 1;
        }

        string question = string.Join(" ", raw);
        AnsiConsole.MarkupLine($"[bold purple]🤖 Asking Kongor AI:[/] [white]{Markup.Escape(question)}[/]\n");
        return RunPythonScript("tools/docgen/query_docs.py", raw);
    }
}

public sealed class VersionCommandSettings : CommandSettings
{
    [CommandArgument(0, "[version]")]
    [Description("Optional target platform SemVer 2.0 to set (e.g. 5.0.0)")]
    public string? Version { get; init; }

    [CommandOption("--build-date")]
    [Description("Target CalVer build release date (default: today yyyy.MM.dd)")]
    public string? BuildDate { get; init; }

    [CommandOption("--engine-protocol")]
    [Description("Target engine protocol version (default: 5.0.0)")]
    [DefaultValue("5.0.0")]
    public string? EngineProtocol { get; init; } = "5.0.0";

    [CommandOption("--min-engine-protocol")]
    [Description("Target min compatible engine protocol version for mods (default: 4.10.1)")]
    [DefaultValue("4.10.1")]
    public string? MinEngineProtocol { get; init; } = "4.10.1";
}

public sealed class VersionCommand : Command<VersionCommandSettings>
{
    public override int Execute(CommandContext context, VersionCommandSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Version))
        {
            PrintVersionInfo();
            return 0;
        }

        AnsiConsole.MarkupLine($"[bold cyan]🔄 Running Ecosystem Unified Version Synchronization Pipeline for v{settings.Version}...[/]");
        string dateVal = string.IsNullOrWhiteSpace(settings.BuildDate)
            ? TimeProvider.System.GetUtcNow().ToString("yyyy.MM.dd")
            : settings.BuildDate;
        string protoVal = string.IsNullOrWhiteSpace(settings.EngineProtocol) ? "5.0.0" : settings.EngineProtocol;
        string minProtoVal = string.IsNullOrWhiteSpace(settings.MinEngineProtocol) ? "4.10.1" : settings.MinEngineProtocol;
        List<string> psArgs =
        [
            "-Version", settings.Version, "-BuildDate", dateVal, "-EngineProtocolVersion", protoVal,
            "-MinCompatibleEngineProtocol", minProtoVal
        ];
        return RunPowerShellScript("scripts/sync-version.ps1", [.. psArgs]);
    }
}

public sealed class SyncVersionCommandSettings : CommandSettings
{
    [CommandArgument(0, "[version]")]
    [Description("Target platform SemVer 2.0 (e.g. 5.0.0)")]
    [DefaultValue("5.0.0")]
    public string Version { get; init; } = "5.0.0";

    [CommandOption("--build-date")]
    [Description("Target CalVer build release date (default: today yyyy.MM.dd)")]
    public string? BuildDate { get; init; }

    [CommandOption("--engine-protocol")]
    [Description("Target engine protocol version (default: 5.0.0)")]
    [DefaultValue("5.0.0")]
    public string? EngineProtocol { get; init; } = "5.0.0";

    [CommandOption("--min-engine-protocol")]
    [Description("Target min compatible engine protocol version for mods (default: 4.10.1)")]
    [DefaultValue("4.10.1")]
    public string? MinEngineProtocol { get; init; } = "4.10.1";
}

public sealed class SyncVersionCommand : Command<SyncVersionCommandSettings>
{
    public override int Execute(CommandContext context, SyncVersionCommandSettings settings)
    {
        AnsiConsole.MarkupLine($"[bold cyan]🔄 Running Ecosystem Unified Version Synchronization Pipeline for v{settings.Version}...[/]");
        string dateVal = string.IsNullOrWhiteSpace(settings.BuildDate)
            ? TimeProvider.System.GetUtcNow().ToString("yyyy.MM.dd")
            : settings.BuildDate;
        string protoVal = string.IsNullOrWhiteSpace(settings.EngineProtocol) ? "5.0.0" : settings.EngineProtocol;
        string minProtoVal = string.IsNullOrWhiteSpace(settings.MinEngineProtocol) ? "4.10.1" : settings.MinEngineProtocol;
        List<string> psArgs =
        [
            "-Version", settings.Version, "-BuildDate", dateVal, "-EngineProtocolVersion", protoVal,
            "-MinCompatibleEngineProtocol", minProtoVal
        ];
        return RunPowerShellScript("scripts/sync-version.ps1", [.. psArgs]);
    }
}

public sealed class ToolsDocgenCommand : Command<RawArgsSettings>
{
    public override int Execute(CommandContext context, RawArgsSettings settings)
    {
        string[] raw = CommandArgsHelper.GetEffectiveArgs(context, settings);
        return RunPythonScript("tools/docgen/generate_charts.py", raw);
    }
}

public sealed class ToolsBlenderCommand : Command<RawArgsSettings>
{
    public override int Execute(CommandContext context, RawArgsSettings settings)
    {
        BlenderCommand cmd = new();
        return cmd.Execute(context, settings);
    }
}

public sealed class ToolsE2ECommand : Command<RawArgsSettings>
{
    public override int Execute(CommandContext context, RawArgsSettings settings)
    {
        string[] raw = CommandArgsHelper.GetEffectiveArgs(context, settings);
        AnsiConsole.MarkupLine("[bold cyan]🚀 Executing 4-Tier E2E Verification Suite (tests/e2e/run_all.ps1)...[/]");
        return RunPowerShellScript("tests/e2e/run_all.ps1", raw);
    }
}

public sealed class UiPlaygroundCommand : Command<RawArgsSettings>
{
    public override int Execute(CommandContext context, RawArgsSettings settings)
    {
        string[] raw = CommandArgsHelper.GetEffectiveArgs(context, settings);
        AnsiConsole.MarkupLine("[bold cyan]🦍 Launching Together as One (TAO) - K2 UI Playground & Visual Editor...[/]");
        AnsiConsole.MarkupLine("[bold yellow]🌐 Opening http://127.0.0.1:5277 in default browser...[/]");
        try
        {
            Process.Start(new ProcessStartInfo("http://127.0.0.1:5277") { UseShellExecute = true });
        }
        catch { }
        return RunPythonScript("tools/ui-playground/server.py", raw);
    }
}

