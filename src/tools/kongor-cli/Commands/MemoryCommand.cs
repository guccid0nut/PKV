// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

// ============================================================================
// File: MemoryCommand.cs
// System: Kongor CLI - Native C++ Modernization & Engine Hardening
// Subsystem: Track 1 Client Memory Allocator Modernization (Microsoft mimalloc)
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using Kongor.Core.Services.Native;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Kongor.Cli.Commands;

public sealed class MemoryCommandSettings : CommandSettings
{
    [CommandArgument(0, "[action]")]
    [Description("Primary action: 'status', 'deploy', 'upgrade', or 'rollback' (default: 'status')")]
    public string? Action { get; init; }

    [CommandOption("-t|--tier <TIER>")]
    [Description("Rollback tier: 'previous' (.bak) or 'old' (.bak.old) (default: 'previous')")]
    public string Tier { get; init; } = "previous";

    [CommandOption("--skip-process-check")]
    [Description("Bypass running process lock detection")]
    public bool SkipProcessCheck { get; init; }
}

public sealed class MemoryCommand : Command<MemoryCommandSettings>
{
    public override int Execute(CommandContext context, MemoryCommandSettings settings)
    {
        string action = (settings.Action ?? "status").Trim().ToLowerInvariant();
        string repoRoot = Program.FindRepoRoot();

        AnsiConsole.Write(new Rule("[bold cyan]Track 1 Client Memory Allocator Modernization (Microsoft mimalloc)[/]").LeftJustified());

        switch (action)
        {
            case "status":
                return ExecuteStatus(repoRoot);

            case "deploy":
            case "upgrade":
            case "modernize":
                return ExecuteDeploy(repoRoot, settings.SkipProcessCheck);

            case "rollback":
            case "revert":
                return ExecuteRollback(repoRoot, settings.Tier, settings.SkipProcessCheck);

            default:
                AnsiConsole.MarkupLine($"[bold red]Unknown memory action:[/] '{Markup.Escape(action)}'. Use 'status', 'deploy', or 'rollback'.");
                return 1;
        }
    }

    private static int ExecuteStatus(string repoRoot)
    {
        ClientMemoryReport report = MemoryModernizationService.GetMemoryStatus(repoRoot);

        AnsiConsole.MarkupLine($"[bold]Target Fleet Root:[/] [cyan]{Markup.Escape(report.TargetDirectory)}[/]");
        AnsiConsole.MarkupLine(report.IsModernized
            ? "[bold green]Status: Modernized[/] - Microsoft mimalloc dynamic UCRT allocator active across all targets."
            : "[bold yellow]Status: Stock UCRT[/] - Windows default CRT heap allocator active.");

        AnsiConsole.MarkupLine($"[bold]Active Backend:[/] [yellow]{Markup.Escape(report.MemoryBackend)}[/]");

        if (report.ClientProcessesRunning)
        {
            AnsiConsole.MarkupLine($"[bold yellow]Active Client Processes:[/] {Markup.Escape(report.RunningProcesses)}");
        }
        else
        {
            AnsiConsole.MarkupLine("[bold green]Client Processes:[/] Idle (Safe for deployment/rollback)");
        }

        Table table = new();
        table.AddColumn("[bold]Target Binary[/]");
        table.AddColumn("[bold]Status[/]");
        table.AddColumn("[bold]Size[/]");
        table.AddColumn("[bold]SHA-256[/]");
        table.AddColumn("[bold]Backups[/]");

        foreach (ClientMemoryTarget target in report.Targets)
        {
            string statusStr = target.Exists ? "[green]Present[/]" : "[red]Missing[/]";
            string sizeStr = target.Exists ? $"{target.SizeBytes:N0} B" : "-";
            string hashStr = target.Exists && target.Sha256Hash.Length >= 8 ? target.Sha256Hash[..8] + "..." : "-";
            string backupStr = (target.HasBackup, target.HasBackupOld) switch
            {
                (true, true) => "[green].bak[/], [yellow].bak.old[/]",
                (true, false) => "[green].bak[/]",
                (false, true) => "[yellow].bak.old[/]",
                _ => "[grey]None[/]"
            };

            table.AddRow(
                Markup.Escape(target.RelativePath),
                statusStr,
                sizeStr,
                hashStr,
                backupStr
            );
        }

        AnsiConsole.Write(table);
        return 0;
    }

    private static int ExecuteDeploy(string repoRoot, bool skipProcessCheck)
    {
        AnsiConsole.MarkupLine("[bold blue]Deploying Microsoft mimalloc across client targets...[/]");

        MemoryDeployResult result = MemoryModernizationService.DeployMimalloc(
            repoRoot,
            null,
            skipProcessCheck,
            msg => AnsiConsole.MarkupLine($"  [grey]{Markup.Escape(msg)}[/]"));

        if (!result.Success)
        {
            AnsiConsole.MarkupLine($"[bold red]Deployment Failed:[/] {Markup.Escape(result.Message)}");
            return 1;
        }

        AnsiConsole.MarkupLine($"[bold green]Success:[/] {Markup.Escape(result.Message)}");
        AnsiConsole.MarkupLine($"[green]Deployed Binaries:[/] {result.DeployedPaths.Count}");
        if (result.RotatedBakPaths.Count > 0)
        {
            AnsiConsole.MarkupLine($"[yellow]Backups Preserved (.bak):[/] {result.RotatedBakPaths.Count}");
        }
        if (result.RotatedBakOldPaths.Count > 0)
        {
            AnsiConsole.MarkupLine($"[yellow]Secondary Backups (.bak.old):[/] {result.RotatedBakOldPaths.Count}");
        }

        return 0;
    }

    private static int ExecuteRollback(string repoRoot, string tier, bool skipProcessCheck)
    {
        AnsiConsole.MarkupLine($"[bold yellow]Rolling back Microsoft mimalloc across client targets (Tier: {tier})...[/]");

        MemoryRollbackResult result = MemoryModernizationService.RollbackMimalloc(
            repoRoot,
            tier,
            skipProcessCheck,
            msg => AnsiConsole.MarkupLine($"  [grey]{Markup.Escape(msg)}[/]"));

        if (!result.Success)
        {
            AnsiConsole.MarkupLine($"[bold red]Rollback Failed:[/] {Markup.Escape(result.Message)}");
            return 1;
        }

        AnsiConsole.MarkupLine($"[bold green]Success:[/] {Markup.Escape(result.Message)}");
        return 0;
    }
}
