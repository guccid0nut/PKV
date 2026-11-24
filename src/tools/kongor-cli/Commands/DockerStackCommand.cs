// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.ComponentModel;
using System.Diagnostics;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Kongor.Cli.Commands;

public class DockerStackBaseSettings : CommandSettings
{
    [CommandOption("-p|--perf|--performance <PRESET>")]
    [Description("Backend/Docker performance preset: Eco, Balanced (default on 'up'/'sync'), or High")]
    public string? Performance { get; init; }

    [CommandOption("-c|--client|--with-client")]
    [Description("Launch the native Windows game client on host after all containers & fleets are ready")]
    public bool WithClient { get; init; }

    [CommandOption("--launcher|--use-launcher")]
    [Description("Open WILLOWMAKER desktop launcher instead of spawning hon_x64.exe directly (implies --client)")]
    public bool UseLauncher { get; init; }

    [CommandOption("--local-postgres|--local-db")]
    [Description("Spin up a containerized PostgreSQL 17 instance inside the Docker Compose stack")]
    public bool LocalPostgres { get; init; }

    [CommandOption("-b|--rebuild|--build")]
    [Description("Force re-publish of linux-x64 binaries and rebuild Docker images")]
    public bool Rebuild { get; init; }

    [CommandOption("--stop-host-conflict|--stop-conflicts")]
    [Description("Automatically stop conflicting host ProjectKongor .NET/COMPEL processes occupying stack ports")]
    public bool StopHostConflict { get; init; }

    [CommandOption("--match-instances <COUNT>")]
    [Description("Override Dedicated Match Server worker count (1..8)")]
    public int? MatchInstances { get; init; }

    [CommandOption("--arcade-instances <COUNT>")]
    [Description("Override Queue Arcade worker count (1..8)")]
    public int? ArcadeInstances { get; init; }

    [CommandOption("-s|--service <NAME>")]
    [Description("Target a single service when viewing logs (e.g. master-server, chat-server, compel-dedicated)")]
    public string? Service { get; init; }

    [CommandOption("-n|--tail <LINES>")]
    [Description("Number of log lines to display for 'logs' (default: 120)")]
    [DefaultValue(120)]
    public int Tail { get; init; } = 120;

    [CommandOption("-f|--follow")]
    [Description("Stream container logs continuously")]
    public bool Follow { get; init; }

    [CommandOption("--testing-domain|--test-domain <DOMAIN>")]
    [Description("Additional testing domain(s) to whitelist for reverse-proxy CORS and SAN cert fallback (e.g. preview.example.com)")]
    public string? TestingDomain { get; init; }

    [CommandOption("--standalone-auth|--standalone")]
    [Description("Enable standalone username/password Web Portal mode instead of 'Start Cluster' Discord OAuth mode")]
    public bool StandaloneAuth { get; init; }

    [CommandOption("--skip-auto-rebuild|--no-build")]
    [Description("Reuse existing local Docker images for faster restarts and preset switches when all images are present")]
    public bool SkipAutoRebuild { get; init; }

    [CommandOption("--dry-run")]
    [Description("Preview 'cleanup' actions without deleting any containers, networks, or images")]
    public bool DryRun { get; init; }
}

public sealed class DockerStackCommandSettings : DockerStackBaseSettings
{
    [CommandArgument(0, "[action]")]
    [Description("Lifecycle action: 'up' (default), 'restart', 'rebuild', 'sync' (check->build->restart->verify), 'down', 'status', 'logs', 'check', 'init-secrets', or 'cleanup'")]
    [DefaultValue("up")]
    public string Action { get; init; } = "up";
}

public sealed class DockerStackCommand : Command<DockerStackCommandSettings>
{
    public override int Execute(CommandContext context, DockerStackCommandSettings settings)
    {
        return ExecuteWithAction(settings.Action, settings);
    }

    internal static int ExecuteWithAction(string action, DockerStackBaseSettings settings)
    {
        string repoRoot = Program.FindRepoRoot();
        string scriptPath = Path.Combine(repoRoot, "scripts", "docker", "launch_docker_stack.ps1");
        if (!File.Exists(scriptPath))
        {
            AnsiConsole.MarkupLine(
                $"[bold red]ERROR:[/] Docker stack orchestrator script not found at [yellow]{Markup.Escape(scriptPath)}[/]. " +
                "Run from a valid ProjectKongor workspace checkout.");
            return 1;
        }

        IReadOnlyList<string> psArgs = BuildPowerShellArguments(scriptPath, action, settings);
        string shellExe = OperatingSystem.IsWindows() ? "powershell" : "pwsh";

        ProcessStartInfo psi = new()
        {
            FileName = shellExe,
            WorkingDirectory = repoRoot,
            UseShellExecute = false
        };
        foreach (string arg in psArgs)
        {
            psi.ArgumentList.Add(arg);
        }

        using Process? process = Process.Start(psi);
        if (process is null)
        {
            AnsiConsole.MarkupLine(
                $"[bold red]ERROR:[/] Failed to start '{Markup.Escape(shellExe)}' for [cyan]scripts/docker/launch_docker_stack.ps1[/].");
            return 1;
        }

        process.WaitForExit();
        return process.ExitCode;
    }

    internal static IReadOnlyList<string> BuildPowerShellArguments(string scriptPath, DockerStackCommandSettings settings)
    {
        return BuildPowerShellArguments(scriptPath, settings.Action, settings);
    }

    internal static IReadOnlyList<string> BuildPowerShellArguments(string scriptPath, string action, DockerStackBaseSettings settings)
    {
        string normalizedAction = string.IsNullOrWhiteSpace(action)
            ? "up"
            : action.Trim().ToLowerInvariant();
        bool impliesRebuildAndStop = normalizedAction is "rebuild" or "build" or "sync" or "retest" or "redeploy" or "fresh" or "reset";

        List<string> args =
        [
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            scriptPath,
            string.IsNullOrWhiteSpace(action) ? "up" : action.Trim()
        ];

        bool defaultToBalancedPreset = normalizedAction is not ("restart" or "reload" or "bounce");
        string? effectivePerformance = !string.IsNullOrWhiteSpace(settings.Performance)
            ? settings.Performance.Trim()
            : defaultToBalancedPreset
                ? "Balanced"
                : null;
        if (!string.IsNullOrWhiteSpace(effectivePerformance))
        {
            args.Add("-Performance");
            args.Add(effectivePerformance);
        }

        if (settings.WithClient || settings.UseLauncher)
        {
            args.Add("-WithClient");
        }

        if (settings.UseLauncher)
        {
            args.Add("-UseLauncher");
        }

        if (settings.LocalPostgres)
        {
            args.Add("-LocalPostgres");
        }

        if (settings.Rebuild || impliesRebuildAndStop)
        {
            args.Add("-Rebuild");
        }

        if (settings.StopHostConflict || impliesRebuildAndStop)
        {
            args.Add("-StopHostConflict");
        }

        if (settings.MatchInstances is > 0)
        {
            args.Add("-MatchInstances");
            args.Add(settings.MatchInstances.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (settings.ArcadeInstances is > 0)
        {
            args.Add("-ArcadeInstances");
            args.Add(settings.ArcadeInstances.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrWhiteSpace(settings.Service))
        {
            args.Add("-Service");
            args.Add(settings.Service.Trim());
        }

        if (settings.Tail != 120 && settings.Tail > 0)
        {
            args.Add("-Tail");
            args.Add(settings.Tail.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (settings.Follow)
        {
            args.Add("-Follow");
        }

        if (!string.IsNullOrWhiteSpace(settings.TestingDomain))
        {
            args.Add("-TestingDomain");
            args.Add(settings.TestingDomain.Trim());
        }

        if (settings.StandaloneAuth)
        {
            args.Add("-StandaloneAuth");
        }

        if (settings.SkipAutoRebuild)
        {
            args.Add("-SkipAutoRebuild");
        }

        if (settings.DryRun)
        {
            args.Add("-DryRun");
        }

        return args;
    }
}

public sealed class DockerSyncCommand : Command<DockerStackBaseSettings>
{
    public override int Execute(CommandContext context, DockerStackBaseSettings settings)
    {
        return DockerStackCommand.ExecuteWithAction("sync", settings);
    }
}

public sealed class DockerRebuildCommand : Command<DockerStackBaseSettings>
{
    public override int Execute(CommandContext context, DockerStackBaseSettings settings)
    {
        return DockerStackCommand.ExecuteWithAction("rebuild", settings);
    }
}
