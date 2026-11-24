// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using Kongor.Core.Services.Native;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Kongor.Cli.Commands;

public sealed class NativeCommandSettings : CommandSettings
{
    [CommandArgument(0, "[action]")]
    [Description("Action to perform: 'status', 'verify', 'upgrade', 'rollback', or 'cef' (default: 'status')")]
    public string? Action { get; init; }

    [CommandArgument(1, "[subaction]")]
    [Description("Sub-action for 'cef': 'status' (default), 'deactivate', or 'rollback'")]
    public string? SubAction { get; init; }

    [CommandOption("-t|--tier|--target <TIER>")]
    [Description("Target rollback tier: 'previous' (default) or 'old' (historical baseline)")]
    public string? TargetTier { get; init; }

    [CommandOption("-c|--candidate <PATH>")]
    [Description("Explicit path to candidate replacement DLL (optional)")]
    public string? CandidatePath { get; init; }

    [CommandOption("--skip-process-check")]
    [Description("Bypass running process check (useful for automated testing)")]
    public bool SkipProcessCheck { get; init; }
}

public sealed class NativeCommand : Command<NativeCommandSettings>
{
    public override int Execute(CommandContext context, NativeCommandSettings settings)
    {
        string action = (settings.Action ?? "status").Trim().ToLowerInvariant();
        string repoRoot = Program.FindRepoRoot();

        AnsiConsole.Write(new Rule("[bold cyan]Native C++ Binary Governance & Security Control[/]").LeftJustified());

        switch (action)
        {
            case "status":
                return ExecuteStatus(repoRoot);

            case "verify":
                return ExecuteVerify(repoRoot, settings.CandidatePath);

            case "upgrade":
                return ExecuteUpgrade(repoRoot, settings.CandidatePath, settings.SkipProcessCheck);

            case "rollback":
                return ExecuteRollback(repoRoot, settings.TargetTier ?? "previous", settings.SkipProcessCheck);

            case "cef":
                return ExecuteCef(repoRoot, settings);

            case "cef-status":
            case "cef_status":
                return ExecuteCefStatus(repoRoot);

            case "cef-deactivate":
            case "cef_deactivate":
            case "deactivate-cef":
                return ExecuteCefDeactivate(repoRoot, settings.SkipProcessCheck);

            case "cef-rollback":
            case "cef_rollback":
            case "rollback-cef":
                return ExecuteCefRollback(repoRoot, settings.TargetTier ?? "previous", settings.SkipProcessCheck);

            default:
                AnsiConsole.MarkupLine($"[bold red]Error:[/] Unknown action '[yellow]{Markup.Escape(action)}[/]'. Valid actions: status, verify, upgrade, rollback, cef [[status|deactivate|rollback]].");
                return 1;
        }
    }

    private static int ExecuteStatus(string repoRoot)
    {
        var targets = NativeBinaryManager.GetStatus(repoRoot);

        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold white]Native Game Engine Dependencies & Security Posture[/]")
            .AddColumn("[bold]Target Binary[/]")
            .AddColumn("[bold]Version[/]")
            .AddColumn("[bold]Size[/]")
            .AddColumn("[bold]Has .bak[/]")
            .AddColumn("[bold]Has .bak.old[/]")
            .AddColumn("[bold]Security Posture[/]");

        foreach (var t in targets)
        {
            string verColor = t.FileVersion.StartsWith("1.3") ? "green" : (t.FileVersion.StartsWith("1.2") ? "yellow" : "grey");
            string secColor = t.SecurityStatus.StartsWith("SECURE") ? "green" : (t.SecurityStatus.StartsWith("VULNERABLE") ? "red" : "grey");
            string bakColor = t.HasBak ? "green" : "grey";
            string oldColor = t.HasBakOld ? "cyan" : "grey";

            table.AddRow(
                $"[white]{Markup.Escape(t.RelativePath)}[/]",
                $"[{verColor}]{Markup.Escape(t.FileVersion)}[/]",
                t.Exists ? $"{t.SizeBytes:N0} B" : "[grey]Missing[/]",
                $"[{bakColor}]{t.HasBak}[/]",
                $"[{oldColor}]{t.HasBakOld}[/]",
                $"[{secColor}]{Markup.Escape(t.SecurityStatus)}[/]"
            );
        }

        AnsiConsole.Write(table);

        if (NativeBinaryManager.IsGameOrServerRunning(out string runningProcs))
        {
            AnsiConsole.MarkupLine($"\n[bold yellow]⚠️  Running Game Processes Detected:[/] [white]{Markup.Escape(runningProcs)}[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("\n[bold green]✓  Process Safety:[/] Zero game client or server locks active.");
        }

        return 0;
    }

    private static int ExecuteVerify(string repoRoot, string? candidatePath)
    {
        string candidate = ResolveCandidatePath(repoRoot, candidatePath);
        if (!File.Exists(candidate))
        {
            AnsiConsole.MarkupLine($"[bold red]Error:[/] Candidate DLL not found at: [yellow]{candidate}[/]");
            return 1;
        }

        var targets = NativeBinaryManager.FindZlibTargets(repoRoot);
        var baseline = targets.FirstOrDefault(File.Exists);

        if (baseline == null)
        {
            AnsiConsole.MarkupLine("[bold red]Error:[/] No existing baseline DLL found to compare exports against.");
            return 1;
        }

        AnsiConsole.MarkupLine($"[cyan]Baseline DLL:[/]  [white]{baseline}[/]");
        AnsiConsole.MarkupLine($"[cyan]Candidate DLL:[/] [white]{candidate}[/]");

        var res = NativeBinaryManager.VerifyExports(baseline, candidate);

        if (res.IsValid)
        {
            AnsiConsole.MarkupLine($"\n[bold green]✓ EXPORT PARITY VERIFIED (100% MATCH)[/]");
            AnsiConsole.MarkupLine($"  • Baseline Exports : [green]{res.BaselineSymbolCount}[/] symbols");
            AnsiConsole.MarkupLine($"  • Candidate Exports: [green]{res.CandidateSymbolCount}[/] symbols");
            AnsiConsole.MarkupLine($"  • Missing Symbols  : [green]0[/] (Zero broken imports)");
            if (res.AddedSymbols.Count > 0)
            {
                AnsiConsole.MarkupLine($"  • Added Symbols    : [cyan]{res.AddedSymbols.Count}[/] modern additions ({string.Join(", ", res.AddedSymbols.Take(5))})");
            }
            return 0;
        }
        else
        {
            AnsiConsole.MarkupLine($"\n[bold red]✗ EXPORT PARITY FAILED[/]");
            AnsiConsole.MarkupLine($"  • Missing Symbols Count: [bold red]{res.MissingSymbols.Count}[/]");
            foreach (var m in res.MissingSymbols)
            {
                AnsiConsole.MarkupLine($"    - [red]{m}[/]");
            }
            return 1;
        }
    }

    private static int ExecuteUpgrade(string repoRoot, string? candidatePath, bool skipProcessCheck)
    {
        string candidate = ResolveCandidatePath(repoRoot, candidatePath);
        if (!File.Exists(candidate))
        {
            AnsiConsole.MarkupLine($"[bold red]Error:[/] Candidate DLL not found at: [yellow]{candidate}[/]");
            return 1;
        }

        try
        {
            var res = NativeBinaryManager.UpgradeZlib(repoRoot, candidate, skipProcessCheck);
            if (res.Success)
            {
                AnsiConsole.MarkupLine($"\n[bold green]✓ {res.Message}[/]");
                foreach (var path in res.UpgradedPaths)
                {
                    AnsiConsole.MarkupLine($"  • Upgraded: [green]{Path.GetRelativePath(repoRoot, path)}[/]");
                }
                foreach (var old in res.RotatedBakOldPaths)
                {
                    AnsiConsole.MarkupLine($"  • Preserved Baseline: [cyan]{Path.GetRelativePath(repoRoot, old)}[/]");
                }
                return 0;
            }
            else
            {
                AnsiConsole.MarkupLine($"\n[bold red]✗ Upgrade Failed:[/] {res.Message}");
                return 1;
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"\n[bold red]✗ Upgrade Aborted:[/] {ex.Message}");
            return 1;
        }
    }

    private static int ExecuteRollback(string repoRoot, string targetTier, bool skipProcessCheck)
    {
        try
        {
            var res = NativeBinaryManager.RollbackZlib(repoRoot, targetTier, skipProcessCheck);
            if (res.Success)
            {
                AnsiConsole.MarkupLine($"\n[bold green]✓ {res.Message}[/]");
                foreach (var path in res.RestoredPaths)
                {
                    AnsiConsole.MarkupLine($"  • Restored: [yellow]{Path.GetRelativePath(repoRoot, path)}[/]");
                }
                return 0;
            }
            else
            {
                AnsiConsole.MarkupLine($"\n[bold red]✗ Rollback Failed:[/] {res.Message}");
                return 1;
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"\n[bold red]✗ Rollback Aborted:[/] {ex.Message}");
            return 1;
        }
    }

    private static int ExecuteCef(string repoRoot, NativeCommandSettings settings)
    {
        string subAction = (settings.SubAction ?? "status").Trim().ToLowerInvariant();
        switch (subAction)
        {
            case "status":
                return ExecuteCefStatus(repoRoot);
            case "deactivate":
            case "disable":
            case "retire":
                return ExecuteCefDeactivate(repoRoot, settings.SkipProcessCheck);
            case "rollback":
            case "restore":
            case "enable":
                return ExecuteCefRollback(repoRoot, settings.TargetTier ?? "previous", settings.SkipProcessCheck);
            default:
                AnsiConsole.MarkupLine($"[bold red]Error:[/] Unknown CEF sub-action '[yellow]{subAction}[/]'. Valid sub-actions: status, deactivate, rollback.");
                return 1;
        }
    }

    private static int ExecuteCefStatus(string repoRoot)
    {
        var rep = CefModernizationService.GetStatus(repoRoot);

        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold white]CEF (Chromium Embedded Framework) Retirement & Memory Telemetry[/]")
            .AddColumn("[bold]Target Asset[/]")
            .AddColumn("[bold]Status[/]")
            .AddColumn("[bold]Size[/]")
            .AddColumn("[bold]Has .bak[/]")
            .AddColumn("[bold]Has .bak.old[/]")
            .AddColumn("[bold]SHA-256[/]");

        foreach (var t in rep.Targets)
        {
            string statusColor = t.Status == "Active" ? "red" : (t.Status == "Deactivated" ? "green" : "grey");
            string bakColor = t.HasBak ? "green" : "grey";
            string oldColor = t.HasBakOld ? "cyan" : "grey";
            string sizeStr = t.SizeBytes > 0 ? $"{t.SizeBytes / 1024.0 / 1024.0:F2} MB" : "-";
            string shortSha = t.Sha256Hash.Length > 12 ? t.Sha256Hash[..12] + "..." : t.Sha256Hash;

            table.AddRow(
                $"[white]{Markup.Escape(t.RelativePath)}[/]",
                $"[{statusColor}]{t.Status}[/]",
                $"[yellow]{sizeStr}[/]",
                $"[{bakColor}]{(t.HasBak ? "YES" : "NO")}[/]",
                $"[{oldColor}]{(t.HasBakOld ? "YES" : "NO")}[/]",
                $"[dim]{shortSha}[/]"
            );
        }

        AnsiConsole.Write(table);

        var summary = new Grid()
            .AddColumn()
            .AddRow($"[bold]CEF State:[/] {(rep.IsActive ? "[bold red]ACTIVE (Causes [[ISSUE-241]] crashes)[/]" : (rep.IsDeactivated ? "[bold green]SAFELY DEACTIVATED (Zero crashes)[/]" : "[grey]NOT FOUND[/]"))}")
            .AddRow($"[bold]Active Disk Footprint:[/] [yellow]{rep.TotalActiveSizeBytes / 1024.0 / 1024.0:F2} MB[/] ({rep.ActiveFileCount} files)")
            .AddRow($"[bold]Reclaimed Disk In Backups:[/] [cyan]{rep.TotalReclaimedSizeBytes / 1024.0 / 1024.0:F2} MB[/] ({rep.DeactivatedFileCount} files)")
            .AddRow($"[bold]Estimated RAM Savings:[/] [bold green]{(rep.IsDeactivated ? "> 300 MB" : "0 MB (Active in RAM)")}[/]")
            .AddRow($"[bold]Target Directory:[/] [dim]{Markup.Escape(rep.CefDirectory)}[/]");

        AnsiConsole.Write(new Panel(summary) { Header = new PanelHeader("[bold cyan]CEF Subsystem Summary[/]") });

        if (rep.ClientProcessRunning)
        {
            AnsiConsole.MarkupLine($"\n[bold yellow]⚠ Warning:[/] Game client process detected: [white]{Markup.Escape(rep.RunningProcesses)}[/]. Close client before deactivating.");
        }

        string icon = rep.IsDeactivated ? "[bold green]✓[/]" : "[bold cyan]ℹ[/]";
        AnsiConsole.MarkupLine($"\n{icon} {Markup.Escape(rep.Message)}");
        return 0;
    }

    private static int ExecuteCefDeactivate(string repoRoot, bool skipProcessCheck)
    {
        try
        {
            AnsiConsole.MarkupLine("[bold cyan]Executing atomic CEF deactivation with two-tier rolling backups (.bak / .bak.old)...[/]");
            var res = CefModernizationService.Deactivate(repoRoot, skipProcessCheck);

            if (res.Success)
            {
                AnsiConsole.MarkupLine($"\n[bold green]✓ {Markup.Escape(res.Message)}[/]");
                AnsiConsole.MarkupLine($"  • Deactivated Files: [yellow]{res.DeactivatedCount}[/]");
                AnsiConsole.MarkupLine($"  • Reclaimed Disk Space: [cyan]{res.ReclaimedDiskBytes / 1024.0 / 1024.0:F2} MB[/]");
                AnsiConsole.MarkupLine($"  • Estimated RAM Savings: [green]{res.EstimatedRamSavingsBytes / 1024.0 / 1024.0:F0} MB[/]");

                foreach (var p in res.DeactivatedPaths)
                {
                    AnsiConsole.MarkupLine($"    - Neutralized: [dim]{Path.GetRelativePath(repoRoot, p)}[/] -> [green].bak[/]");
                }

                if (res.RotatedBakOldPaths.Count > 0)
                {
                    AnsiConsole.MarkupLine($"  • Rotated to Tier 2 (.bak.old): [cyan]{res.RotatedBakOldPaths.Count} files[/]");
                }

                return 0;
            }
            else
            {
                AnsiConsole.MarkupLine($"\n[bold red]✗ Deactivation Failed:[/] {Markup.Escape(res.Message)}");
                return 1;
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"\n[bold red]✗ Deactivation Aborted:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
    }

    private static int ExecuteCefRollback(string repoRoot, string targetTier, bool skipProcessCheck)
    {
        try
        {
            AnsiConsole.MarkupLine($"[bold yellow]Rolling back CEF from backup tier '{targetTier}'...[/]");
            var res = CefModernizationService.Rollback(repoRoot, targetTier, skipProcessCheck);

            if (res.Success)
            {
                AnsiConsole.MarkupLine($"\n[bold green]✓ {Markup.Escape(res.Message)}[/]");
                foreach (var path in res.RestoredPaths)
                {
                    AnsiConsole.MarkupLine($"  • Restored: [yellow]{Path.GetRelativePath(repoRoot, path)}[/]");
                }
                return 0;
            }
            else
            {
                AnsiConsole.MarkupLine($"\n[bold red]✗ Rollback Failed:[/] {Markup.Escape(res.Message)}");
                return 1;
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"\n[bold red]✗ Rollback Aborted:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
    }

    private static string ResolveCandidatePath(string repoRoot, string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
        {
            return Path.GetFullPath(explicitPath);
        }

        var defaultPaths = new[]
        {
            Path.Combine(repoRoot, "native", "build", "kongor_zlib_build", "Release", "zlibwapi_x64.dll"),
            Path.Combine(repoRoot, "artifacts", "native-binaries", "windows-x64", "zlibwapi_x64.dll"),
            Path.Combine(repoRoot, "artifacts", "native-build", "release", "zlibwapi_x64.dll")
        };

        foreach (var p in defaultPaths)
        {
            if (File.Exists(p)) return p;
        }

        return defaultPaths[0];
    }
}
