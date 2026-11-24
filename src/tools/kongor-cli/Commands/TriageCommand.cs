// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Kongor.Cli.Mcp.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Kongor.Cli.Commands;

public sealed class TriageCommandSettings : CommandSettings
{
    [CommandOption("-d|--dir|--dump <PATH>")]
    [Description("Path to crash dump or diagnostic file or directory (default: auto-discover)")]
    public string? DumpPath { get; init; }

    [CommandOption("--dry-run")]
    [Description("Display triage diagnostics without modifying backlog or writing resuscitation manifests")]
    public bool DryRun { get; init; }

    [CommandOption("--no-backlog")]
    [Description("Skip recording newly triaged crashes into docs/ISSUES.md")]
    public bool NoBacklog { get; init; }

    [CommandOption("-a|--args <ARGS>")]
    [Description("Command line arguments to preserve in resuscitation manifest")]
    public string? CommandLineArgs { get; init; }
}

/// <summary>
/// kongor triage: Autonomous crash dump analysis, Ghidra symbol triangulation,
/// and process resuscitation orchestration.
/// </summary>
public sealed class TriageCommand : AsyncCommand<TriageCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, TriageCommandSettings settings)
    {
        AnsiConsole.Write(new Rule("[bold red]ProjectKongor Autonomous Crash Triage & Resuscitation[/]").LeftJustified());

        CrashTriageOrchestrator orchestrator = new CrashTriageOrchestrator(
            logger: msg => AnsiConsole.MarkupLine($"[grey]>>> {Markup.Escape(msg)}[/]"));

        string? artifact = settings.DumpPath;
        if (string.IsNullOrWhiteSpace(artifact) || (!File.Exists(artifact) && !Directory.Exists(artifact)))
        {
            artifact = orchestrator.ResolveLatestCrashArtifact();
        }

        if (string.IsNullOrWhiteSpace(artifact) || !File.Exists(artifact))
        {
            AnsiConsole.MarkupLine("[bold green]✔ Zero crash artifacts found.[/] No recent minidump or K2 diagnostic crash logs detected.");
            return 0;
        }

        AnsiConsole.MarkupLine($"[bold yellow]🔍 Ingesting crash artifact:[/] [white]{Markup.Escape(artifact)}[/]");

        CrashTriageReport report = await orchestrator.TriageAndOrchestrateAsync(new CrashTriageRequest
        {
            DumpPath = artifact,
            CommandLineArgs = settings.CommandLineArgs,
            PreserveMatchContext = true
        });

        // Render Spectre.Console Diagnostic Summary Card
        Table table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
        table.AddColumn(new TableColumn("[bold]Property[/]").Width(25));
        table.AddColumn(new TableColumn("[bold]Diagnostic Data[/]"));

        table.AddRow("Artifact File", Markup.Escape(Path.GetFileName(report.ArtifactPath)));
        table.AddRow("Faulting Target", $"[bold red]{Markup.Escape(report.FaultingModule)}[/]+[bold yellow]{Markup.Escape(report.FaultingRva)}[/]");
        table.AddRow("Exception", $"[bold red]{Markup.Escape(report.ExceptionCode)}[/] ([yellow]{Markup.Escape(report.ExceptionName)}[/])");
        table.AddRow("Fault Classification", Markup.Escape(report.FaultType));
        table.AddRow("Ghidra Triangulation", $"[cyan]{Markup.Escape(report.GhidraTriangulation.Command)}[/]");
        table.AddRow("WinDbg Analysis", $"[cyan]{Markup.Escape(report.WinDbgCommand)}[/]");
        table.AddRow("Root Cause", $"[white]{Markup.Escape(report.RootCauseHypothesis)}[/]");
        table.AddRow("Recommendation", $"[green]{Markup.Escape(report.Recommendation)}[/]");
        string relaunchDesc = report.ResuscitationManifest != null
            ? $"{Markup.Escape(report.ResuscitationManifest.ExecutablePath)} {Markup.Escape(report.ResuscitationManifest.CommandLineArgs)}"
            : "None (no command-line arguments provided)";
        table.AddRow("Relaunch Manifest", $"[bold green]{relaunchDesc}[/]");

        AnsiConsole.Write(table);

        if (!settings.DryRun)
        {
            if (report.ResuscitationManifest != null)
            {
                // Write resuscitation manifest
                string triageDir = Path.Combine(Program.FindRepoRoot(), "output", "triage");
                Directory.CreateDirectory(triageDir);
                string manifestPath = Path.Combine(triageDir, "resuscitation_manifest.json");
                await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(report.ResuscitationManifest, new JsonSerializerOptions { WriteIndented = true }));
                AnsiConsole.MarkupLine($"[grey]Saved resuscitation manifest to {Markup.Escape(manifestPath)}[/]");
            }

            // Record into living backlog
            if (!settings.NoBacklog)
            {
                IssueTrackerService issueTracker = new IssueTrackerService();
                string remediation = report.ResuscitationManifest != null
                    ? $"Ghidra command: {report.GhidraTriangulation.Command}. Resuscitation: {report.ResuscitationManifest.ExecutablePath} {report.ResuscitationManifest.CommandLineArgs}"
                    : $"Ghidra command: {report.GhidraTriangulation.Command}.";
                string issueId = await issueTracker.RecordIssueAsync(new IssueEntry
                {
                    Title = $"Crash Triage: {report.FaultingModule}+{report.FaultingRva} ({report.ExceptionName})",
                    Subsystem = "Native Runtime & Crash Triage",
                    DiscoveryMethod = "kongor triage CLI orchestration",
                    Symptom = $"{report.ExceptionCode} fault in {report.FaultingModule} at offset {report.FaultingRva}",
                    RootCause = report.RootCauseHypothesis,
                    Status = IssueStatus.Investigating,
                    Remediation = remediation
                });
                AnsiConsole.MarkupLine($"[bold green]✔ Recorded crash into docs/ISSUES.md:[/] [yellow]{issueId}[/]");
            }
        }
        else
        {
            AnsiConsole.MarkupLine("[bold blue][[DRY RUN]][/] Triage evaluated cleanly. Zero files modified.");
        }

        return 0;
    }
}
