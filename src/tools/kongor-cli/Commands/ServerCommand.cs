// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

// ============================================================================
// File: ServerCommand.cs
// System: Kongor CLI - Native C++ Modernization & Engine Hardening
// Subsystem: Phase 6 Server Fleet Security & Audio Headless Null Stub (fmodex64.dll)
// ============================================================================

using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Kongor.Core.Services.Native;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Kongor.Cli.Commands;

public sealed class ServerCommandSettings : CommandSettings
{
    [CommandArgument(0, "[action]")]
    [Description("Primary action: 'status', 'security', 'audio-stub', 'audio', or 'vid' (default: 'status')")]
    public string? Action { get; init; }

    [CommandArgument(1, "[subaction]")]
    [Description("Subaction: 'upgrade', 'rollback', 'deploy', 'purge', or 'status' (default: 'status')")]
    public string? SubAction { get; init; }

    [CommandOption("-t|--tier <TIER>")]
    [Description("Target rollback tier: 'previous' (.bak, default) or 'old' (.bak.old, historical baseline)")]
    public string? Tier { get; init; }

    [CommandOption("-d|--dir|--server-dir <PATH>")]
    [Description("Path to server directory or repository root (defaults to auto-discovered games/server)")]
    public string? ServerDirectory { get; init; }

    [CommandOption("--skip-process-check")]
    [Description("Bypass running process lock detection")]
    public bool SkipProcessCheck { get; init; }

    [CommandOption("--skip-export-check")]
    [Description("Bypass export symbol parity check")]
    public bool SkipExportCheck { get; init; }

    [CommandOption("--redirect-iat")]
    [Description("Automatically redirect PE Import Address Table in server k2_x64.dll to modern proxy shims")]
    public bool RedirectIat { get; init; }
}

public sealed class ServerCommand : AsyncCommand<ServerCommandSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, ServerCommandSettings settings)
    {
        string primary = (settings.Action ?? "status").Trim().ToLowerInvariant();
        string subaction = (settings.SubAction ?? "").Trim().ToLowerInvariant();
        string repoRoot = Program.FindRepoRoot();
        string targetDir = ResolveServerDirectory(repoRoot, settings.ServerDirectory);

        AnsiConsole.Write(new Rule("[bold cyan]Dedicated Server Fleet Security & Headless Audio/Video Governance[/]").LeftJustified());

        switch (primary)
        {
            case "status":
                return Task.FromResult(ExecuteStatus(targetDir));

            case "security":
                return Task.FromResult(ExecuteSecurity(targetDir, subaction, settings));

            case "audio-stub":
            case "audio":
            case "stub":
                return Task.FromResult(ExecuteAudioStub(targetDir, subaction, settings));

            case "vid":
            case "video":
            case "vid-purge":
            case "graphics":
                return Task.FromResult(ExecuteVideoBloat(targetDir, subaction, settings));

            default:
                AnsiConsole.MarkupLine($"[bold red]Error:[/] Unknown server action '[yellow]{primary}[/]'. Valid actions: status, security, audio-stub, vid.");
                return Task.FromResult(1);
        }
    }

    private static int ExecuteStatus(string targetDir)
    {
        AnsiConsole.MarkupLine($"\n[bold white]Inspecting Dedicated Server Environment:[/] [cyan]{Markup.Escape(targetDir)}[/]");

        // 1. Audio Stub Inspection
        AudioStubReport audioReport = HeadlessAudioStubService.GetAudioStubStatus(targetDir);
        var audioTable = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold white]Dedicated Server Audio Subsystem Status (fmodex64.dll)[/]")
            .AddColumn("[bold]Target Binary[/]")
            .AddColumn("[bold]Status[/]")
            .AddColumn("[bold]Size[/]")
            .AddColumn("[bold]SHA-256[/]")
            .AddColumn("[bold]Backups[/]");

        foreach (AudioStubTarget item in audioReport.Targets)
        {
            string status = item.IsHeadlessStub
                ? "[bold green]Headless Null Stub (Optimized)[/]"
                : "[bold yellow]Full FMOD Ex Runtime (High RAM/Threads)[/]";

            string bakStatus = item.HasBackupOld
                ? "[bold green].bak + .bak.old[/]"
                : (item.HasBackup ? "[green].bak[/]" : "[grey]-[/]");

            string sha = string.IsNullOrEmpty(item.Sha256Hash)
                ? "[grey]-[/]"
                : $"[grey]{item.Sha256Hash[..8]}...[/]";

            audioTable.AddRow(
                $"[white]{Markup.Escape(item.RelativePath)}[/]",
                item.Exists ? status : "[grey]Missing[/]",
                item.Exists ? $"{item.SizeBytes:N0} B" : "[grey]Missing[/]",
                sha,
                bakStatus
            );
        }

        AnsiConsole.Write(audioTable);

        // 2. Security Binary Inspection
        SecurityUpgradeReport secReport = NativeSecurityUpgradeService.GetSecurityStatus(targetDir, TargetFleet.Server);
        var secTable = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold white]Dedicated Server Security & TLS 1.3 Stack[/]")
            .AddColumn("[bold]Binary[/]")
            .AddColumn("[bold]Version[/]")
            .AddColumn("[bold]Size[/]")
            .AddColumn("[bold]TLS 1.3[/]")
            .AddColumn("[bold]CVE Remediation Status[/]")
            .AddColumn("[bold]Backups[/]");

        foreach (SecurityBinaryTarget item in secReport.Targets)
        {
            string tlsStatus = item.IsTls13Supported
                ? "[bold green]Supported[/]"
                : "[bold red]No (Legacy)[/]";

            string cveDisplay = item.CveRemediationStatus.StartsWith("Remediated", StringComparison.OrdinalIgnoreCase)
                ? $"[green]{Markup.Escape(item.CveRemediationStatus)}[/]"
                : $"[bold red]{Markup.Escape(item.CveRemediationStatus)}[/]";

            string bakStatus = item.HasBakOld
                ? "[bold green].bak + .bak.old[/]"
                : (item.HasBak ? "[green].bak[/]" : "[grey]-[/]");

            secTable.AddRow(
                $"[white]{Markup.Escape(item.RelativePath)}[/]",
                item.Exists ? $"[cyan]{Markup.Escape(item.FileVersion)}[/]" : "[grey]Missing[/]",
                item.Exists ? $"{item.SizeBytes:N0} B" : "[grey]Missing[/]",
                tlsStatus,
                cveDisplay,
                bakStatus
            );
        }

        AnsiConsole.Write(secTable);

        // 3. Headless Video Bloat Inspection (Phase 10)
        ServerVidReport vidReport = HeadlessVidService.GetStatusReport(targetDir);
        var vidTable = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold white]Dedicated Server Video Bloat Elimination (d3d9, vid_d3d9, vid_gl2, dxvk)[/]")
            .AddColumn("[bold]Target File[/]")
            .AddColumn("[bold]Status[/]")
            .AddColumn("[bold]Size[/]")
            .AddColumn("[bold]SHA-256[/]")
            .AddColumn("[bold]Backups[/]");

        foreach (ServerVidTarget item in vidReport.Targets)
        {
            string status = item.IsPurged
                ? "[bold green]Purged (Zero GPU Bloat)[/]"
                : (item.Exists ? "[bold yellow]Active Bloat (Unneeded Direct3D/GL)[/]" : "[grey]Absent[/]");

            string bakStatus = item.HasBackupOld
                ? "[bold green].bak + .bak.old[/]"
                : (item.HasBackup ? "[green].bak[/]" : "[grey]-[/]");

            string sha = string.IsNullOrEmpty(item.Sha256Hash)
                ? "[grey]-[/]"
                : $"[grey]{item.Sha256Hash[..Math.Min(8, item.Sha256Hash.Length)]}...[/]";

            vidTable.AddRow(
                $"[white]{Markup.Escape(item.RelativePath)}[/]",
                status,
                item.Exists ? $"{item.SizeBytes:N0} B" : "[grey]-[/]",
                sha,
                bakStatus
            );
        }

        AnsiConsole.Write(vidTable);

        if (audioReport.ServerProcessesRunning || secReport.GameProcessesRunning || vidReport.ServerProcessesRunning)
        {
            string procs = string.Join(", ", new[] { audioReport.RunningProcesses, secReport.RunningProcesses, vidReport.RunningProcesses }.Where(s => !string.IsNullOrEmpty(s)).Distinct());
            AnsiConsole.MarkupLine($"\n[bold yellow]⚠️  Running Game Processes Detected:[/] [white]{Markup.Escape(procs)}[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("\n[bold green]✓  Process Safety:[/] Zero game server locks active.");
        }

        return 0;
    }

    private static int ExecuteSecurity(string targetDir, string subaction, ServerCommandSettings settings)
    {
        string action = string.IsNullOrEmpty(subaction) ? "status" : subaction;

        switch (action)
        {
            case "status":
                return ExecuteStatus(targetDir);

            case "upgrade":
            case "apply":
                AnsiConsole.MarkupLine($"[bold green]Upgrading dedicated server security stack in:[/] [white]{Markup.Escape(targetDir)}[/]\n");
                var upResult = NativeSecurityUpgradeService.UpgradeSecurityBinaries(
                    targetDir,
                    null,
                    settings.SkipProcessCheck,
                    settings.SkipExportCheck,
                    settings.RedirectIat,
                    TargetFleet.Server,
                    msg => AnsiConsole.MarkupLine($"  [grey]>[/] {Markup.Escape(msg)}"));

                if (upResult.Success)
                {
                    AnsiConsole.MarkupLine($"\n[bold green]✓ {Markup.Escape(upResult.Message)}[/]");
                    return 0;
                }
                AnsiConsole.MarkupLine($"\n[bold red]✗ {Markup.Escape(upResult.Message)}[/]");
                return 1;

            case "rollback":
            case "revert":
                string tier = settings.Tier ?? "previous";
                AnsiConsole.MarkupLine($"[bold yellow]Rolling back dedicated server security stack ({tier}) in:[/] [white]{Markup.Escape(targetDir)}[/]\n");
                var rbResult = NativeSecurityUpgradeService.RollbackSecurityBinaries(
                    targetDir,
                    tier,
                    settings.SkipProcessCheck,
                    TargetFleet.Server,
                    msg => AnsiConsole.MarkupLine($"  [grey]>[/] {Markup.Escape(msg)}"));

                if (rbResult.Success)
                {
                    AnsiConsole.MarkupLine($"\n[bold green]✓ {Markup.Escape(rbResult.Message)}[/]");
                    return 0;
                }
                AnsiConsole.MarkupLine($"\n[bold red]✗ {Markup.Escape(rbResult.Message)}[/]");
                return 1;

            default:
                AnsiConsole.MarkupLine($"[bold red]Error:[/] Unknown security subaction '[yellow]{action}[/]'. Valid: upgrade, rollback, status.");
                return 1;
        }
    }

    private static int ExecuteAudioStub(string targetDir, string subaction, ServerCommandSettings settings)
    {
        string action = string.IsNullOrEmpty(subaction) ? "status" : subaction;

        switch (action)
        {
            case "status":
                return ExecuteStatus(targetDir);

            case "deploy":
            case "install":
            case "apply":
                AnsiConsole.MarkupLine($"[bold green]Deploying high-performance fmodex64 null stub to server targets in:[/] [white]{Markup.Escape(targetDir)}[/]\n");
                var depResult = HeadlessAudioStubService.DeployHeadlessStub(
                    targetDir,
                    null,
                    settings.SkipProcessCheck,
                    msg => AnsiConsole.MarkupLine($"  [grey]>[/] {Markup.Escape(msg)}"));

                if (depResult.Success)
                {
                    AnsiConsole.MarkupLine($"\n[bold green]✓ {Markup.Escape(depResult.Message)}[/]");
                    AnsiConsole.MarkupLine("  [cyan]Eliminated audio mixer threads and cut dedicated server memory footprint (~15-25MB per instance).[/]");
                    return 0;
                }
                AnsiConsole.MarkupLine($"\n[bold red]✗ {Markup.Escape(depResult.Message)}[/]");
                return 1;

            case "rollback":
            case "revert":
                string tier = settings.Tier ?? "previous";
                AnsiConsole.MarkupLine($"[bold yellow]Rolling back fmodex64.dll ({tier}) in:[/] [white]{Markup.Escape(targetDir)}[/]\n");
                var rbResult = HeadlessAudioStubService.RollbackHeadlessStub(
                    targetDir,
                    tier,
                    settings.SkipProcessCheck,
                    msg => AnsiConsole.MarkupLine($"  [grey]>[/] {Markup.Escape(msg)}"));

                if (rbResult.Success)
                {
                    AnsiConsole.MarkupLine($"\n[bold green]✓ {Markup.Escape(rbResult.Message)}[/]");
                    return 0;
                }
                AnsiConsole.MarkupLine($"\n[bold red]✗ {Markup.Escape(rbResult.Message)}[/]");
                return 1;

            default:
                AnsiConsole.MarkupLine($"[bold red]Error:[/] Unknown audio-stub subaction '[yellow]{action}[/]'. Valid: deploy, rollback, status.");
                return 1;
        }
    }

    private static int ExecuteVideoBloat(string targetDir, string subaction, ServerCommandSettings settings)
    {
        string action = string.IsNullOrEmpty(subaction) ? "status" : subaction;

        switch (action)
        {
            case "status":
                return ExecuteStatus(targetDir);

            case "purge":
            case "clean":
            case "apply":
                AnsiConsole.MarkupLine($"[bold green]Purging dedicated server video bloat across targets in:[/] [white]{Markup.Escape(targetDir)}[/]\n");
                var purgeResult = HeadlessVidService.PurgeServerVideoBloat(
                    targetDir,
                    settings.SkipProcessCheck,
                    msg => AnsiConsole.MarkupLine($"  [grey]>[/] {Markup.Escape(msg)}"));

                if (purgeResult.Success)
                {
                    AnsiConsole.MarkupLine($"\n[bold green]✓ {Markup.Escape(purgeResult.Message)}[/]");
                    AnsiConsole.MarkupLine($"  [cyan]Eliminated Direct3D/GL driver files and freed {purgeResult.BytesFreed:N0} bytes from server footprint.[/]");
                    return 0;
                }
                AnsiConsole.MarkupLine($"\n[bold red]✗ {Markup.Escape(purgeResult.Message)}[/]");
                return 1;

            case "rollback":
            case "revert":
            case "restore":
                string tier = settings.Tier ?? "previous";
                AnsiConsole.MarkupLine($"[bold yellow]Rolling back dedicated server video files ({tier}) in:[/] [white]{Markup.Escape(targetDir)}[/]\n");
                var rbResult = HeadlessVidService.RollbackServerVideoBloat(
                    targetDir,
                    tier,
                    settings.SkipProcessCheck,
                    msg => AnsiConsole.MarkupLine($"  [grey]>[/] {Markup.Escape(msg)}"));

                if (rbResult.Success)
                {
                    AnsiConsole.MarkupLine($"\n[bold green]✓ {Markup.Escape(rbResult.Message)}[/]");
                    AnsiConsole.MarkupLine($"  [cyan]Restored {rbResult.BytesRestored:N0} bytes across {rbResult.RestoredPaths.Count} files.[/]");
                    return 0;
                }
                AnsiConsole.MarkupLine($"\n[bold red]✗ {Markup.Escape(rbResult.Message)}[/]");
                return 1;

            default:
                AnsiConsole.MarkupLine($"[bold red]Error:[/] Unknown video subaction '[yellow]{action}[/]'. Valid: purge, rollback, status.");
                return 1;
        }
    }

    private static string ResolveServerDirectory(string repoRoot, string? explicitDir)
    {
        if (!string.IsNullOrWhiteSpace(explicitDir))
        {
            return Path.GetFullPath(explicitDir);
        }

        string cand1 = Path.Combine(repoRoot, "games", "server");
        if (Directory.Exists(cand1)) return cand1;

        string cand2 = Path.Combine(repoRoot, "games", "server", "windows-x64");
        if (Directory.Exists(cand2)) return cand2;

        return repoRoot;
    }
}
