// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

// ============================================================================
// File: InputCommand.cs
// System: Kongor CLI - Native C++ Modernization & Engine Hardening
// Subsystem: Track 2 High-Polling-Rate Esports Mouse Modernization (Windows Raw Input 8000Hz)
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System;
using System.ComponentModel;
using System.IO;
using Kongor.Core.Services.Native;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Kongor.Cli.Commands;

public sealed class InputCommandSettings : CommandSettings
{
    [CommandArgument(0, "[action]")]
    [Description("Primary action: 'status', 'deploy', 'upgrade', 'rollback', or 'config' (default: 'status')")]
    public string? Action { get; init; }

    [CommandOption("-t|--tier <TIER>")]
    [Description("Rollback tier: 'previous' (.bak) or 'old' (.bak.old) (default: 'previous')")]
    public string Tier { get; init; } = "previous";

    [CommandOption("--skip-process-check")]
    [Description("Bypass running process lock detection")]
    public bool SkipProcessCheck { get; init; }

    [CommandOption("-s|--sensitivity <SENSITIVITY>")]
    [Description("Mouse raw sensitivity multiplier (default: 1.0)")]
    public float Sensitivity { get; init; } = 1.0f;

    [CommandOption("--disable")]
    [Description("Set in_rawInput to false in configuration")]
    public bool Disable { get; init; }
}

public sealed class InputCommand : Command<InputCommandSettings>
{
    public override int Execute(CommandContext context, InputCommandSettings settings)
    {
        string action = (settings.Action ?? "status").Trim().ToLowerInvariant();
        string repoRoot = Program.FindRepoRoot();

        AnsiConsole.Write(new Rule("[bold cyan]Track 2 High-Polling-Rate Esports Mouse Modernization (Windows Raw Input 8000Hz)[/]").LeftJustified());

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

            case "config":
            case "configure":
            case "set":
                return ExecuteConfig(repoRoot, !settings.Disable, settings.Sensitivity);

            default:
                AnsiConsole.MarkupLine($"[bold red]Unknown input action:[/] '{Markup.Escape(action)}'. Use 'status', 'deploy', 'rollback', or 'config'.");
                return 1;
        }
    }

    private static int ExecuteStatus(string repoRoot)
    {
        RawInputReport report = RawInputModernizationService.GetRawInputStatus(repoRoot);

        AnsiConsole.MarkupLine($"[bold]Target Fleet Root:[/] [cyan]{Markup.Escape(report.TargetDirectory)}[/]");
        AnsiConsole.MarkupLine(report.IsModernized
            ? "[bold green]Status: Modernized (8000Hz Raw Input Active)[/] - NativeGuard version.dll proxy active with IAT hooks."
            : "[bold yellow]Status: Legacy Polling[/] - Windows stock cursor polling active.");

        AnsiConsole.MarkupLine($"[bold]Hardware Polling:[/] [yellow]1000Hz - 8000Hz Esports Ready (ASUS ROG Harpe II Ace)[/]");
        AnsiConsole.MarkupLine($"[bold]Sensitivity Multiplier:[/] [cyan]{report.MouseSensitivity:F2}x[/]");
        AnsiConsole.MarkupLine($"[bold]Cvar in_rawInput:[/] {(report.CvarRawInputEnabled ? "[green]true[/]" : "[yellow]false[/]")}");

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

        foreach (RawInputTarget target in report.Targets)
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
        AnsiConsole.MarkupLine("[bold blue]Starting deployment of 8000Hz Windows Raw Input NativeGuard proxy...[/]");

        RawInputDeployResult result = RawInputModernizationService.DeployRawInput(
            repoRoot,
            null,
            skipProcessCheck,
            msg => AnsiConsole.MarkupLine($"  {Markup.Escape(msg)}")
        );

        if (!result.Success)
        {
            AnsiConsole.MarkupLine($"[bold red]Deployment Failed:[/] {Markup.Escape(result.Message)}");
            return 1;
        }

        AnsiConsole.MarkupLine($"[bold green]{Markup.Escape(result.Message)}[/]");
        if (result.RotatedBakPaths.Count > 0)
        {
            AnsiConsole.MarkupLine($"[grey]Created/Updated {result.RotatedBakPaths.Count} backup (.bak) files.[/]");
        }
        if (result.RotatedBakOldPaths.Count > 0)
        {
            AnsiConsole.MarkupLine($"[grey]Rotated {result.RotatedBakOldPaths.Count} secondary backup (.bak.old) files.[/]");
        }

        return 0;
    }

    private static int ExecuteRollback(string repoRoot, string tier, bool skipProcessCheck)
    {
        AnsiConsole.MarkupLine($"[bold yellow]Rolling back Raw Input proxy using tier '{Markup.Escape(tier)}'...[/]");

        RawInputRollbackResult result = RawInputModernizationService.RollbackRawInput(
            repoRoot,
            tier,
            skipProcessCheck,
            msg => AnsiConsole.MarkupLine($"  {Markup.Escape(msg)}")
        );

        if (!result.Success)
        {
            AnsiConsole.MarkupLine($"[bold red]Rollback Failed:[/] {Markup.Escape(result.Message)}");
            return 1;
        }

        AnsiConsole.MarkupLine($"[bold green]{Markup.Escape(result.Message)}[/]");
        return 0;
    }

    private static int ExecuteConfig(string repoRoot, bool enable, float sensitivity)
    {
        AnsiConsole.MarkupLine($"[bold blue]Configuring startup.cfg (in_rawInput: {enable}, m_rawSensitivity: {sensitivity:F2})...[/]");

        RawInputCvarResult result = RawInputModernizationService.ConfigureStartupCfg(
            repoRoot,
            enable,
            sensitivity,
            msg => AnsiConsole.MarkupLine($"  {Markup.Escape(msg)}")
        );

        if (!result.Success)
        {
            AnsiConsole.MarkupLine($"[bold red]Configuration Failed:[/] {Markup.Escape(result.Message)}");
            return 1;
        }

        AnsiConsole.MarkupLine($"[bold green]{Markup.Escape(result.Message)}[/]");
        return 0;
    }
}
