// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

// ============================================================================
// File: ArchiveCommand.cs
// System: Kongor CLI - Native C++ Modernization & Engine Hardening
// Subsystem: Phase 7 Zstandard (Zstd) VFS Decompression & libdeflate Archive Modernization
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

public sealed class ArchiveCommandSettings : CommandSettings
{
    [CommandArgument(0, "[action]")]
    [Description("Primary action: 'status', 'benchmark', 'inspect', 'upgrade', or 'rollback' (default: 'status')")]
    public string? Action { get; init; }

    [CommandArgument(1, "[target]")]
    [Description("Target archive path (.s2z/.zip) for benchmark/inspect, or subaction for upgrade/rollback")]
    public string? Target { get; init; }

    [CommandOption("-f|--fleet <FLEET>")]
    [Description("Target fleet scope: 'client', 'server', or 'all' (default: 'all')")]
    public string? Fleet { get; init; }

    [CommandOption("-t|--tier <TIER>")]
    [Description("Target rollback tier: 'previous' (.bak, default) or 'old' (.bak.old, baseline)")]
    public string? Tier { get; init; }

    [CommandOption("-l|--limit <COUNT>")]
    [Description("Maximum archive entries to sample in benchmark (default: 100, 0 for all)")]
    public int Limit { get; init; } = 100;

    [CommandOption("--skip-process-check")]
    [Description("Bypass running process lock detection")]
    public bool SkipProcessCheck { get; init; }
}

public sealed class ArchiveCommand : AsyncCommand<ArchiveCommandSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, ArchiveCommandSettings settings)
    {
        string action = (settings.Action ?? "status").Trim().ToLowerInvariant();
        string repoRoot = Program.FindRepoRoot();
        TargetFleet fleet = ParseFleet(settings.Fleet);

        AnsiConsole.Write(new Rule("[bold cyan]Phase 7 Fast VFS & Dual-Engine Archive Acceleration[/]").LeftJustified());

        switch (action)
        {
            case "status":
                return Task.FromResult(ExecuteStatus(repoRoot, fleet));

            case "benchmark":
            case "bench":
                return Task.FromResult(ExecuteBenchmark(repoRoot, settings.Target, settings.Limit));

            case "inspect":
            case "info":
                return Task.FromResult(ExecuteInspect(repoRoot, settings.Target));

            case "upgrade":
            case "deploy":
            case "apply":
                return Task.FromResult(ExecuteUpgrade(repoRoot, fleet, settings.SkipProcessCheck));

            case "rollback":
            case "revert":
                return Task.FromResult(ExecuteRollback(repoRoot, fleet, settings.Tier ?? "previous", settings.SkipProcessCheck));

            default:
                // Check if user ran "kongor archive <path.s2z>"
                if (action.EndsWith(".s2z", StringComparison.OrdinalIgnoreCase) || action.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(ExecuteBenchmark(repoRoot, action, settings.Limit));
                }

                AnsiConsole.MarkupLine($"[bold red]Error:[/] Unknown archive action '[yellow]{action}[/]'. Valid actions: status, benchmark, inspect, upgrade, rollback.");
                return Task.FromResult(1);
        }
    }

    private static int ExecuteStatus(string repoRoot, TargetFleet fleet)
    {
        var report = FastVfsModernizationService.GetVfsStatus(repoRoot, fleet);

        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold white]Dual-Engine Fast VFS Runtime Status (zlibwapi_x64.dll)[/]")
            .AddColumn("[bold]Target Binary[/]")
            .AddColumn("[bold]Engine Status[/]")
            .AddColumn("[bold]Size[/]")
            .AddColumn("[bold]SHA-256[/]")
            .AddColumn("[bold]Backups[/]");

        foreach (var target in report.Targets)
        {
            string engineDisplay = target.IsDualEngineAccelerated
                ? "[bold green]Dual-Engine AVX2 + Zstd (Accelerated)[/]"
                : "[bold yellow]Standard zlib 1.3.1 (Baseline)[/]";

            string bakDisplay = target.HasBakOld
                ? "[bold green].bak + .bak.old[/]"
                : (target.HasBak ? "[green].bak[/]" : "[grey]-[/]");

            string sha = string.IsNullOrEmpty(target.Sha256Hash)
                ? "[grey]-[/]"
                : $"[grey]{target.Sha256Hash[..8]}...[/]";

            table.AddRow(
                $"[white]{Markup.Escape(target.RelativePath)}[/]",
                target.Exists ? engineDisplay : "[grey]Missing[/]",
                target.Exists ? $"{target.SizeBytes:N0} B" : "[grey]Missing[/]",
                sha,
                bakDisplay
            );
        }

        AnsiConsole.Write(table);

        if (report.GameProcessesRunning)
        {
            AnsiConsole.MarkupLine($"\n[bold yellow]⚠️  Running Game Processes Detected:[/] [white]{Markup.Escape(report.RunningProcesses)}[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("\n[bold green]✓  Process Safety:[/] Zero game client or server locks active.");
        }

        return 0;
    }

    private static int ExecuteBenchmark(string repoRoot, string? targetPath, int limit)
    {
        string archive = ResolveArchivePath(repoRoot, targetPath);
        if (!File.Exists(archive))
        {
            AnsiConsole.MarkupLine($"[bold red]Error:[/] Game archive file not found: [yellow]{Markup.Escape(archive)}[/]");
            return 1;
        }

        AnsiConsole.MarkupLine($"\n[bold white]Benchmarking Archive Decompression:[/] [cyan]{Markup.Escape(archive)}[/]");

        ArchiveBenchmarkResult result = AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .Start("Streaming and decompressing entries...", _ => FastVfsModernizationService.BenchmarkArchive(archive, limit));

        if (!result.Success)
        {
            AnsiConsole.MarkupLine($"[bold red]Benchmark Failed:[/] {Markup.Escape(result.ErrorMessage ?? "Unknown error")}");
            return 1;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold white]Archive Throughput Benchmark Metrics[/]")
            .AddColumn("[bold]Metric[/]")
            .AddColumn("[bold]Value[/]");

        table.AddRow("Archive Path", Markup.Escape(Path.GetFileName(result.ArchivePath)));
        table.AddRow("Entries Tested", $"{result.TotalFiles:N0}");
        table.AddRow("Compressed Payload", $"{result.TotalCompressedBytes / (1024.0 * 1024.0):N2} MB ({result.TotalCompressedBytes:N0} bytes)");
        table.AddRow("Uncompressed Payload", $"{result.TotalUncompressedBytes / (1024.0 * 1024.0):N2} MB ({result.TotalUncompressedBytes:N0} bytes)");
        table.AddRow("Elapsed Time", $"{result.ElapsedMilliseconds:N2} ms");
        table.AddRow("[bold green]Decompression Throughput[/]", $"[bold green]{result.ThroughputMbPerSecond:N2} MB/s[/]");
        table.AddRow("Compression Ratio", $"{result.CompressionRatio:P2}");

        AnsiConsole.Write(table);
        return 0;
    }

    private static int ExecuteInspect(string repoRoot, string? targetPath)
    {
        string archive = ResolveArchivePath(repoRoot, targetPath);
        if (!File.Exists(archive))
        {
            AnsiConsole.MarkupLine($"[bold red]Error:[/] Game archive file not found: [yellow]{Markup.Escape(archive)}[/]");
            return 1;
        }

        AnsiConsole.MarkupLine($"\n[bold white]Inspecting Archive Compression Format:[/] [cyan]{Markup.Escape(archive)}[/]");

        ArchiveInspectionReport report = AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .Start("Analyzing central directory...", _ => FastVfsModernizationService.InspectArchive(archive));

        if (!report.Success)
        {
            AnsiConsole.MarkupLine($"[bold red]Inspection Failed:[/] {Markup.Escape(report.ErrorMessage ?? "Unknown error")}");
            return 1;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold white]Archive Format Inspection[/]")
            .AddColumn("[bold]Property[/]")
            .AddColumn("[bold]Details[/]");

        table.AddRow("Filename", Markup.Escape(Path.GetFileName(report.ArchivePath)));
        table.AddRow("Archive File Size", $"{report.FileSizeBytes / (1024.0 * 1024.0):N2} MB ({report.FileSizeBytes:N0} bytes)");
        table.AddRow("Total File Entries", $"{report.TotalEntries:N0}");
        table.AddRow("Deflate-Compressed Files", $"{report.DeflateCount:N0}");
        table.AddRow("Zstd-Compressed Files", $"{report.ZstdCount:N0}");
        table.AddRow("Stored (Uncompressed) Files", $"{report.StoredCount:N0}");
        table.AddRow("Total Uncompressed Data", $"{report.TotalUncompressedSize / (1024.0 * 1024.0):N2} MB");

        AnsiConsole.Write(table);
        return 0;
    }

    private static int ExecuteUpgrade(string repoRoot, TargetFleet fleet, bool skipProcessCheck)
    {
        AnsiConsole.MarkupLine($"[bold green]Deploying Phase 7 dual-engine zlibwapi_x64.dll across ({fleet}):[/] [white]{Markup.Escape(repoRoot)}[/]\n");

        var result = FastVfsModernizationService.DeployFastVfsBinary(
            repoRoot,
            null,
            skipProcessCheck,
            fleet,
            msg => AnsiConsole.MarkupLine($"  [grey]>[/] {Markup.Escape(msg)}"));

        if (result.Success)
        {
            AnsiConsole.MarkupLine($"\n[bold green]✓ {Markup.Escape(result.Message)}[/]");
            return 0;
        }

        AnsiConsole.MarkupLine($"\n[bold red]✗ {Markup.Escape(result.Message)}[/]");
        return 1;
    }

    private static int ExecuteRollback(string repoRoot, TargetFleet fleet, string tier, bool skipProcessCheck)
    {
        AnsiConsole.MarkupLine($"[bold yellow]Rolling back zlibwapi_x64.dll to tier '{tier}' across ({fleet}):[/] [white]{Markup.Escape(repoRoot)}[/]\n");

        var result = FastVfsModernizationService.RollbackFastVfsBinary(
            repoRoot,
            tier,
            skipProcessCheck,
            fleet,
            msg => AnsiConsole.MarkupLine($"  [grey]>[/] {Markup.Escape(msg)}"));

        if (result.Success)
        {
            AnsiConsole.MarkupLine($"\n[bold green]✓ {Markup.Escape(result.Message)}[/]");
            return 0;
        }

        AnsiConsole.MarkupLine($"\n[bold red]✗ {Markup.Escape(result.Message)}[/]");
        return 1;
    }

    private static string ResolveArchivePath(string repoRoot, string? targetPath)
    {
        if (!string.IsNullOrWhiteSpace(targetPath))
        {
            string full = Path.GetFullPath(Path.Combine(repoRoot, targetPath));
            if (File.Exists(full)) return full;
        }

        // Standard candidate locations in repository
        var candidates = new[]
        {
            Path.Combine(repoRoot, "games", "client", "game", "maps", "caldavar.s2z"),
            Path.Combine(repoRoot, "games", "client", "game", "caldavar.s2z"),
            Path.Combine(repoRoot, "games", "client", "game", "resources0.s2z"),
            Path.Combine(repoRoot, "games", "server", "game", "maps", "caldavar.s2z"),
            Path.Combine(repoRoot, "games", "server", "game", "resources0.s2z")
        };

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate)) return candidate;
        }

        return targetPath ?? Path.Combine(repoRoot, "games", "client", "game", "caldavar.s2z");
    }

    private static TargetFleet ParseFleet(string? fleet)
    {
        if (string.IsNullOrWhiteSpace(fleet)) return TargetFleet.All;

        return fleet.Trim().ToLowerInvariant() switch
        {
            "client" => TargetFleet.Client,
            "server" or "dedicated" => TargetFleet.Server,
            _ => TargetFleet.All
        };
    }
}
