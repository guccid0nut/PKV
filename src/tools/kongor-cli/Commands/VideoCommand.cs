// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

// ============================================================================
// File: VideoCommand.cs
// System: ProjectKongor CLI Tooling (kongor video)
// Subsystem: Phase 9 Hardware-Accelerated Video Decoding (FFmpeg 7.x / DXVA2)
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

public sealed class VideoCommandSettings : CommandSettings
{
    [CommandArgument(0, "[action]")]
    [Description("Primary action: 'status', 'clean-server', 'restore-server', 'upgrade', 'rollback' (default: 'status')")]
    public string? Action { get; init; }

    [CommandOption("--skip-process-check|--force")]
    [Description("Bypass running process lock detection")]
    public bool SkipProcessCheck { get; init; }

    [CommandOption("--root <PATH>")]
    [Description("Project root directory override")]
    public string? RepoRoot { get; init; }
}

public sealed class VideoCommand : AsyncCommand<VideoCommandSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, VideoCommandSettings settings)
    {
        string subCommand = (settings.Action ?? "status").Trim().ToLowerInvariant();
        string repoRoot = !string.IsNullOrWhiteSpace(settings.RepoRoot)
            ? Path.GetFullPath(settings.RepoRoot)
            : FindRepoRoot();
        bool skipProcessCheck = settings.SkipProcessCheck;

        AnsiConsole.Write(new Rule("[bold cyan]Phase 9 Hardware-Accelerated Video Decoding & Server Optimization[/]").LeftJustified());

        int result = subCommand switch
        {
            "status" => ExecuteStatus(repoRoot),
            "clean-server" or "purge-server" or "clean" => ExecuteCleanServer(repoRoot, skipProcessCheck),
            "restore-server" => ExecuteRestoreServer(repoRoot, skipProcessCheck),
            "upgrade" or "deploy" or "modernize" => ExecuteUpgrade(repoRoot, skipProcessCheck),
            "rollback" or "revert" => ExecuteRollback(repoRoot, skipProcessCheck),
            _ => ShowHelp(subCommand)
        };

        return Task.FromResult(result);
    }

    private static string FindRepoRoot() => Program.FindRepoRoot();

    private static int ShowHelp(string invalidAction)
    {
        AnsiConsole.MarkupLine($"[bold red]Unknown video action:[/] '{invalidAction}'.");
        AnsiConsole.MarkupLine("Supported actions:");
        AnsiConsole.MarkupLine("  [cyan]kongor video status[/]          - Show video library status across client and server fleet");
        AnsiConsole.MarkupLine("  [cyan]kongor video clean-server[/]    - Purge 25.5 MB unused video libraries from dedicated servers");
        AnsiConsole.MarkupLine("  [cyan]kongor video restore-server[/]  - Restore purged video libraries to dedicated servers");
        AnsiConsole.MarkupLine("  [cyan]kongor video upgrade[/]         - Deploy hardware-accelerated video shims to client");
        AnsiConsole.MarkupLine("  [cyan]kongor video rollback[/]        - Roll back client video libraries to vanilla FFmpeg");
        return 1;
    }

    private static int ExecuteStatus(string repoRoot)
    {
        var report = VideoModernizationService.GetVideoStatus(repoRoot);

        // 1. Client Table
        var clientTable = new Table().Border(TableBorder.Rounded).Title("[bold green]Client Video Subsystem (games/client/)[/]");
        clientTable.AddColumn(new TableColumn("Library").LeftAligned());
        clientTable.AddColumn(new TableColumn("Status").LeftAligned());
        clientTable.AddColumn(new TableColumn("Size").RightAligned());
        clientTable.AddColumn(new TableColumn("SHA-256").Centered());
        clientTable.AddColumn(new TableColumn("Core / Backups").LeftAligned());

        foreach (var t in report.ClientTargets)
        {
            var backups = new System.Collections.Generic.List<string>();
            if (t.HasCoreEngine) backups.Add("[green]avcodec_core.dll[/]");
            if (t.HasBackup) backups.Add(".bak");
            if (t.HasBackupOld) backups.Add(".bak.old");
            string backupStr = backups.Count > 0 ? string.Join(", ", backups) : "[dim]None[/]";
            string hashShort = t.Sha256Hash.Length > 10 ? t.Sha256Hash[..8] + "..." : t.Sha256Hash;
            clientTable.AddRow(
                $"[white]{t.RelativePath}[/]",
                t.IsShim ? $"[bold green]{t.Status}[/]" : $"[yellow]{t.Status}[/]",
                $"{t.SizeBytes:N0} B",
                $"[dim]{hashShort}[/]",
                backupStr
            );
        }
        AnsiConsole.Write(clientTable);

        // 2. Server Table
        var serverTable = new Table().Border(TableBorder.Rounded).Title("[bold yellow]Dedicated Server Fleet (games/server/)[/]");
        serverTable.AddColumn(new TableColumn("Library").LeftAligned());
        serverTable.AddColumn(new TableColumn("Status").LeftAligned());
        serverTable.AddColumn(new TableColumn("Size").RightAligned());
        serverTable.AddColumn(new TableColumn("Optimized / Saved").Centered());

        foreach (var t in report.ServerTargets)
        {
            serverTable.AddRow(
                $"[white]{t.RelativePath}[/]",
                t.Exists ? $"[bold yellow]{t.Status}[/]" : $"[bold green]{t.Status}[/]",
                $"{t.SizeBytes:N0} B",
                t.Exists ? "[yellow]Ready to purge[/]" : "[bold green]0 MB (Purged)[/]"
            );
        }
        AnsiConsole.Write(serverTable);

        // 3. Metrics
        var metricsTable = new Table().Border(TableBorder.Rounded).Title("[bold cyan]Video Architecture & Safety Metrics[/]");
        metricsTable.AddColumn("Property");
        metricsTable.AddColumn("Value");

        metricsTable.AddRow("Decode Acceleration", $"[bold green]{report.HardwareAccelerationBackend}[/]");
        metricsTable.AddRow("Dedicated Server Waste", report.IsServerPurged ? "[bold green]0 MB (Purged & Clean)[/]" : $"[bold yellow]{report.ServerTargets.Sum(s => s.SizeBytes):N0} B (Ready to purge)[/]");
        metricsTable.AddRow("Server Storage Freed", $"[bold green]{report.ServerSpaceSavedBytes:N0} bytes[/]");
        metricsTable.AddRow("Login Screen Asset Safety", report.LoginScreenPreserved ? "[bold green]100% Preserved (login_1080.avi.bak untouched)[/]" : "[bold red]Warning: login video modified[/]");

        AnsiConsole.Write(metricsTable);
        return 0;
    }

    private static int ExecuteCleanServer(string repoRoot, bool skipProcessCheck)
    {
        AnsiConsole.MarkupLine($"Purging dead video libraries from dedicated servers in [bold white]{repoRoot}[/]...");
        var result = VideoModernizationService.PurgeServerVideoBloat(repoRoot, skipProcessCheck);

        if (!result.Success)
        {
            AnsiConsole.MarkupLine($"[bold red]Server Purge Failed:[/] {result.Message}");
            return 1;
        }

        foreach (string p in result.PurgedPaths)
        {
            AnsiConsole.MarkupLine($"  [dim]>[/] [bold green][[PURGED]][/] Backed up and removed dead DLL -> '[green]{p}[/]'.");
        }

        AnsiConsole.MarkupLine($"\n[bold green]✓ {result.Message}[/]");
        return 0;
    }

    private static int ExecuteRestoreServer(string repoRoot, bool skipProcessCheck)
    {
        AnsiConsole.MarkupLine($"Restoring video libraries to dedicated servers in [bold white]{repoRoot}[/]...");
        var result = VideoModernizationService.RestoreServerVideo(repoRoot, skipProcessCheck);

        if (!result.Success)
        {
            AnsiConsole.MarkupLine($"[bold red]Server Restore Failed:[/] {result.Message}");
            return 1;
        }

        foreach (string p in result.PurgedPaths)
        {
            AnsiConsole.MarkupLine($"  [dim]>[/] [bold green][[RESTORED]][/] Restored video DLL -> '[green]{p}[/]'.");
        }

        AnsiConsole.MarkupLine($"\n[bold green]✓ {result.Message}[/]");
        return 0;
    }

    private static int ExecuteUpgrade(string repoRoot, bool skipProcessCheck)
    {
        string cmakeDir = Path.Combine(repoRoot, "native", "build", "kongor_video_build", "Release");
        string stagedDir = Directory.Exists(cmakeDir)
            ? cmakeDir
            : Path.Combine(repoRoot, "artifacts", "native-binaries", "windows-x64", "video");
        AnsiConsole.MarkupLine($"Deploying hardware-accelerated video shims to client: [bold white]{repoRoot}[/]");

        var result = VideoModernizationService.DeployClientVideoUpgrade(repoRoot, stagedDir, skipProcessCheck);
        if (!result.Success)
        {
            AnsiConsole.MarkupLine($"[bold red]Deployment Failed:[/] {result.Message}");
            return 1;
        }

        foreach (string core in result.PreservedCorePaths)
        {
            AnsiConsole.MarkupLine($"  [dim]>[/] [bold green][[PRESERVE_CORE]][/] Preserved genuine core engine -> '[cyan]{core}[/]'.");
        }
        foreach (string rotated in result.RotatedBakPaths)
        {
            AnsiConsole.MarkupLine($"  [dim]>[/] [bold yellow][[BACKUP_ROTATED]][/] Rotated previous backup -> '[yellow]{rotated}[/]'.");
        }
        foreach (string dep in result.DeployedPaths)
        {
            AnsiConsole.MarkupLine($"  [dim]>[/] [bold green][[DEPLOYED_SHIM]][/] Deployed hardware-accelerated shim -> '[green]{dep}[/]'.");
        }

        AnsiConsole.MarkupLine($"\n[bold green]✓ {result.Message}[/]");
        return 0;
    }

    private static int ExecuteRollback(string repoRoot, bool skipProcessCheck)
    {
        AnsiConsole.MarkupLine($"Rolling back client video libraries to vanilla FFmpeg: [bold white]{repoRoot}[/]");
        var result = VideoModernizationService.RollbackClientVideo(repoRoot, skipProcessCheck);

        if (!result.Success)
        {
            AnsiConsole.MarkupLine($"[bold red]Rollback Failed:[/] {result.Message}");
            return 1;
        }

        foreach (string restored in result.RestoredPaths)
        {
            AnsiConsole.MarkupLine($"  [dim]>[/] [bold green][[RESTORED]][/] Restored vanilla video library -> '[green]{restored}[/]'.");
        }

        AnsiConsole.MarkupLine($"\n[bold green]✓ {result.Message}[/]");
        return 0;
    }
}
