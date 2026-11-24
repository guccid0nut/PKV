// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.ComponentModel;
using Kongor.Cli.Mcp.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Kongor.Cli.Commands;

public sealed class LuaCheckSettings : CommandSettings
{
    [CommandArgument(0, "[path]")]
    [Description("Path to Lua script file, directory, or XML package to check")]
    [DefaultValue("games/client/override/ui")]
    public string Path { get; init; } = "games/client/override/ui";

    [CommandOption("--strict")]
    [Description("Fail execution on warnings as well as errors")]
    public bool Strict { get; init; }

    [CommandOption("--fix-bom")]
    [Description("Automatically strip UTF-8 Byte Order Marks (BOM)")]
    public bool FixBom { get; init; }

    [CommandOption("--no-recurse")]
    [Description("Scan only top directory without recursing into subdirectories")]
    public bool NoRecurse { get; init; }

    [CommandOption("-v|--verbose")]
    [Description("Print all scanned files and detailed diagnostics")]
    public bool Verbose { get; init; }
}

public sealed class LuaCheckCommand : AsyncCommand<LuaCheckSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, LuaCheckSettings settings)
    {
        AnsiConsole.MarkupLine($"[bold cyan]🔍 ProjectKongor Lua & XML Static Typecheck & Linting[/]");
        AnsiConsole.MarkupLine($"Target Path: [yellow]{Markup.Escape(settings.Path)}[/]");

        K2LintService linter = new();
        K2LintReport report = await linter.LintAsync(
            settings.Path,
            recursive: !settings.NoRecurse,
            fixBom: settings.FixBom);

        if (report.FixedBomFiles.Count > 0)
        {
            AnsiConsole.MarkupLine($"[green]✔ Stripped UTF-8 BOM from {report.FixedBomFiles.Count} files.[/]");
        }

        // Output summary table
        Table table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("[bold]Metric[/]");
        table.AddColumn("[bold]Value[/]");
        table.AddRow("Files Scanned", report.FilesScanned.ToString());
        table.AddRow("Clean Files", $"[green]{report.CleanFilesCount}[/]");
        table.AddRow("Errors", report.ErrorCount == 0 ? "[green]0[/]" : $"[red]{report.ErrorCount}[/]");
        table.AddRow("Warnings", report.WarningCount == 0 ? "[green]0[/]" : $"[yellow]{report.WarningCount}[/]");
        AnsiConsole.Write(table);

        if (report.Issues.Count > 0)
        {
            AnsiConsole.MarkupLine("\n[bold underline]Diagnostics Details:[/]");
            foreach (K2LintIssue issue in report.Issues)
            {
                string color = issue.Severity == "error" ? "red" : "yellow";
                string prefix = issue.Severity == "error" ? "✖ ERROR" : "▲ WARN";

                AnsiConsole.MarkupLine($"[{color} bold]{prefix}[/] [[{issue.Code}]] [white]{Markup.Escape(issue.File)}:{issue.Line}:{issue.Column}[/]");
                AnsiConsole.MarkupLine($"  [white]{Markup.Escape(issue.Message)}[/]");

                if (!string.IsNullOrWhiteSpace(issue.Snippet))
                {
                    AnsiConsole.MarkupLine($"  [grey]Snippet: {Markup.Escape(issue.Snippet)}[/]");
                }

                if (!string.IsNullOrWhiteSpace(issue.RemediationHint))
                {
                    AnsiConsole.MarkupLine($"  [green]Hint: {Markup.Escape(issue.RemediationHint)}[/]");
                }
                Console.WriteLine();
            }
        }

        bool failed = report.ErrorCount > 0 || (settings.Strict && report.WarningCount > 0);
        if (failed)
        {
            AnsiConsole.MarkupLine($"[bold red]✖ Lua verification failed with {report.ErrorCount} errors and {report.WarningCount} warnings.[/]");
            return 1;
        }

        AnsiConsole.MarkupLine($"[bold green]✔ Lua verification passed with 0 errors across {report.FilesScanned} files![/]");
        return 0;
    }
}
