// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Kongor.Cli.Mcp.Services;
using Kongor.Cli.Mcp.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Kongor.Cli.Commands;

public sealed class VfsAuditCommandSettings : CommandSettings
{
    [CommandArgument(0, "[TARGET]")]
    [Description("Optional specific target file, directory, or subpath to audit (e.g. 'ui/fe2/store' or 'heroes/kunas')")]
    public string? Target { get; init; }

    [CommandOption("-s|--scope <SCOPE>")]
    [Description("Asset scope to audit: 'all', 'ui', 'models', 'materials' (default: 'all')")]
    public string Scope { get; init; } = "all";

    [CommandOption("--strict")]
    [Description("Strict mode: fail with non-zero exit code on warnings as well as fatal errors")]
    public bool Strict { get; init; }

    [CommandOption("-l|--limit <LIMIT>")]
    [Description("Maximum number of issues to display in terminal (default: 100)")]
    public int Limit { get; init; } = 100;

    [CommandOption("--json")]
    [Description("Output machine-readable JSON report directly to stdout")]
    public bool Json { get; init; }

    [CommandOption("-o|--output <FILE>")]
    [Description("Save full machine-readable JSON or markdown report to specified output path")]
    public string? Output { get; init; }
}

/// <summary>
/// Pre-flight static VFS asset completeness auditor.
/// Traverses all UI packages, MDF manifests, and materials, cross-referencing referenced paths
/// against the full K2 VFS mount hierarchy (override, game, S2Z archives) before the client boots.
/// </summary>
public sealed class VfsAuditCommand : AsyncCommand<VfsAuditCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, VfsAuditCommandSettings settings)
    {
        if (!settings.Json)
        {
            AnsiConsole.Write(new Rule("[bold cyan]ProjectKongor Static VFS Asset Completeness Auditor[/]").LeftJustified());
            AnsiConsole.MarkupLine($"[grey]Scope:[/] [yellow]{Markup.Escape(settings.Scope)}[/] | [grey]Target:[/] [white]{Markup.Escape(settings.Target ?? "all")}[/] | [grey]Strict:[/] [{(settings.Strict ? "bold red" : "green")}]{settings.Strict}[/]");
        }

        var checker = new VfsCompletenessCheckerService(
            logger: msg =>
            {
                if (!settings.Json)
                {
                    AnsiConsole.MarkupLine($"[grey]>>> {Markup.Escape(msg)}[/]");
                }
            });

        var options = new VfsCompletenessOptions
        {
            Scope = settings.Scope,
            TargetPath = settings.Target,
            Strict = settings.Strict,
            Limit = settings.Limit
        };

        var report = await checker.AuditAsync(options);

        // 1. Machine-readable JSON output
        if (settings.Json)
        {
            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine(json);

            if (!string.IsNullOrWhiteSpace(settings.Output))
            {
                await File.WriteAllTextAsync(settings.Output, json);
            }

            return report.Success ? 0 : 1;
        }

        // 2. Terminal Spectre.Console Dashboard
        AnsiConsole.WriteLine();
        var statsTable = new Table().Border(TableBorder.Rounded).BorderColor(Color.Aqua);
        statsTable.AddColumn(new TableColumn("[bold]Metric[/]").Width(32));
        statsTable.AddColumn(new TableColumn("[bold]Value[/]"));

        statsTable.AddRow("Total VFS Entries Indexed", $"{report.TotalIndexedVfsEntries:N0} entries");
        statsTable.AddRow("VFS Index Build Time", $"{report.IndexDurationMs:N1} ms");
        statsTable.AddRow("Source Files Scanned", $"{report.TotalFilesScanned:N0} files");
        statsTable.AddRow("Asset References Verified", $"{report.TotalReferencesChecked:N0} references");
        statsTable.AddRow("Scan & Resolution Time", $"{report.ScanDurationMs:N1} ms");
        statsTable.AddRow("Total Audit Time", $"{report.TotalDurationMs:N1} ms");
        statsTable.AddRow("Fatal Missing Assets", report.MissingFatalCount == 0 ? "[green]0 (None)[/]" : $"[bold red]{report.MissingFatalCount}[/]");
        statsTable.AddRow("Asset Warnings", report.MissingWarningCount == 0 ? "[green]0 (None)[/]" : $"[bold yellow]{report.MissingWarningCount}[/]");
        statsTable.AddRow("Overall Status", report.Success ? "[bold green]✔ PASSED (Clean)[/]" : "[bold red]✘ FAILED (Discrepancies Found)[/]");

        AnsiConsole.Write(statsTable);

        // 3. Issues Inventory Table
        if (report.Issues.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[bold yellow]⚠ Missing Asset Inventory (showing top {report.Issues.Count} of {report.MissingFatalCount + report.MissingWarningCount}):[/]");

            var issueTable = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
            issueTable.AddColumn(new TableColumn("[bold]Sev[/]").Width(8));
            issueTable.AddColumn(new TableColumn("[bold]Category[/]").Width(24));
            issueTable.AddColumn(new TableColumn("[bold]Referenced Asset Path[/]").Width(38));
            issueTable.AddColumn(new TableColumn("[bold]Declaring Source File[/]"));

            foreach (var issue in report.Issues)
            {
                string sev = issue.Severity == "FATAL" ? "[bold red]FATAL[/]" : "[yellow]WARN[/]";
                string cat = Markup.Escape(issue.Category);
                string path = Markup.Escape(issue.VirtualPath);
                string source = $"{Markup.Escape(issue.DeclaringFile)}:{issue.LineNumber}";

                issueTable.AddRow(sev, cat, path, source);
            }

            AnsiConsole.Write(issueTable);
        }

        // 4. Save optional file report
        if (!string.IsNullOrWhiteSpace(settings.Output))
        {
            string ext = Path.GetExtension(settings.Output).ToLowerInvariant();
            if (ext == ".json")
            {
                string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(settings.Output, json);
                AnsiConsole.MarkupLine($"\n[grey]Saved JSON report to:[/] [cyan]{Markup.Escape(settings.Output)}[/]");
            }
            else
            {
                // Write Markdown report
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("# ProjectKongor Static VFS Completeness Report");
                sb.AppendLine($"*Timestamp*: {report.Timestamp}");
                sb.AppendLine($"*Status*: **{report.Status}**");
                sb.AppendLine($"- Total Files Scanned: {report.TotalFilesScanned}");
                sb.AppendLine($"- References Checked: {report.TotalReferencesChecked}");
                sb.AppendLine($"- Fatal Missing: {report.MissingFatalCount}");
                sb.AppendLine($"- Warnings: {report.MissingWarningCount}");
                sb.AppendLine($"- Audit Duration: {report.TotalDurationMs} ms\n");
                sb.AppendLine("| Severity | Category | Referenced Path | Declaring File | Line |");
                sb.AppendLine("| :--- | :--- | :--- | :--- | :--- |");
                foreach (var issue in report.Issues)
                {
                    sb.AppendLine($"| {issue.Severity} | {issue.Category} | `{issue.VirtualPath}` | `{issue.DeclaringFile}` | {issue.LineNumber} |");
                }
                await File.WriteAllTextAsync(settings.Output, sb.ToString());
                AnsiConsole.MarkupLine($"\n[grey]Saved Markdown report to:[/] [cyan]{Markup.Escape(settings.Output)}[/]");
            }
        }

        if (report.Success)
        {
            AnsiConsole.MarkupLine("\n[bold green]✔ Static VFS completeness audit passed![/] All referenced textures, models, and clips resolved cleanly across VFS layers.");
            return 0;
        }

        AnsiConsole.MarkupLine($"\n[bold red]✘ Static VFS completeness audit failed with {report.MissingFatalCount} fatal missing assets and {report.MissingWarningCount} warnings.[/]");
        return 1;
    }
}
