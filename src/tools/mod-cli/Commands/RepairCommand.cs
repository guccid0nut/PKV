// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.ComponentModel;
using Kongor.Modding.Repair;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Kongor.ModCli.Commands;

public sealed class RepairSettings : CommandSettings
{
    [CommandArgument(0, "<PATH>")]
    [Description("Path to broken .honmod file or directory containing mods")]
    public string Path { get; init; } = string.Empty;

    [CommandOption("--out <PATH>")]
    [Description("Destination path for repaired .honmod file or output directory")]
    public string? OutPath { get; init; }

    [CommandOption("--dry-run")]
    [Description("Simulate repairs without writing to disk")]
    [DefaultValue(false)]
    public bool DryRun { get; init; }
}

public sealed class RepairCliCommand : AsyncCommand<RepairSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, RepairSettings settings)
    {
        await RepairCommand.ExecuteAsync(settings.Path, settings.OutPath, settings.DryRun);
        return 0;
    }
}

/// <summary>
///     CLI command handler for automated compatibility repair of broken or legacy HoN mods.
/// </summary>
public static class RepairCommand
{
    public static async Task ExecuteAsync(string targetPath, string? outPath, bool dryRun = false)
    {
        AnsiConsole.MarkupLine("[bold cyan]ProjectKongor Automated Compatibility & Repair Engine[/]");

        ModRepairEngine repairEngine = new();

        // 1. Single file repair
        if (File.Exists(targetPath))
        {
            string destination = outPath ?? Path.Combine(
                Path.GetDirectoryName(targetPath) ?? string.Empty,
                Path.GetFileNameWithoutExtension(targetPath) + ".repaired.honmod");

            if (dryRun)
                destination = Path.Combine(Path.GetTempPath(),
                    "KongorRepairDryRun_" + Guid.NewGuid().ToString("N") + ".honmod");

            AnsiConsole.MarkupLine($"[grey]Source:[/]      [bold]{Markup.Escape(targetPath)}[/]");
            AnsiConsole.MarkupLine($"[grey]Destination:[/] [bold]{Markup.Escape(destination)}[/]");
            if (dryRun) AnsiConsole.MarkupLine("[yellow]Mode: DRY RUN (temporary output)[/]");

            RepairResult result = await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync($"Repairing mod '{Path.GetFileName(targetPath)}'...",
                    async ctx => { return await repairEngine.RepairModAsync(targetPath, destination); });

            if (dryRun && File.Exists(destination))
                try
                {
                    File.Delete(destination);
                }
                catch
                {
                    /* ignore */
                }

            if (result.Success)
            {
                AnsiConsole.MarkupLine(
                    $"[bold green]✓ Successfully repaired mod:[/] [bold]{Markup.Escape(result.ModName)}[/]");
                AnsiConsole.MarkupLine(
                    $"[grey]Fixes applied:[/] [cyan]{result.FixesAppliedCount}[/] across [cyan]{result.TransformedFilesCount}[/] files.");

                if (result.TransformationLog.Count > 0)
                {
                    Table logTable = new();
                    logTable.Border(TableBorder.Rounded);
                    logTable.Title("[bold]Applied Transformations & Fixes[/]");
                    logTable.AddColumn("Action");

                    foreach (string entry in result.TransformationLog)
                        logTable.AddRow($"[green]• {Markup.Escape(entry)}[/]");

                    AnsiConsole.Write(logTable);
                }
            }
            else
            {
                AnsiConsole.MarkupLine($"[bold red]✗ Failed to repair mod '{Path.GetFileName(targetPath)}'[/]");
                if (!string.IsNullOrEmpty(result.ErrorMessage))
                    AnsiConsole.MarkupLine($"[red]Error: {Markup.Escape(result.ErrorMessage)}[/]");
            }

            return;
        }

        // 2. Batch directory repair
        if (Directory.Exists(targetPath))
        {
            string destDir = outPath ?? Path.Combine(targetPath, "repaired");
            if (dryRun)
                destDir = Path.Combine(Path.GetTempPath(), "KongorRepairBatchDryRun_" + Guid.NewGuid().ToString("N"));

            AnsiConsole.MarkupLine($"[grey]Scanning directory:[/] [bold]{Markup.Escape(targetPath)}[/]");
            AnsiConsole.MarkupLine($"[grey]Output directory:[/]   [bold]{Markup.Escape(destDir)}[/]");

            BatchRepairResult batchResult = await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Running compatibility repair across directory...",
                    async ctx => { return await repairEngine.RepairAllAsync(targetPath, destDir); });

            if (dryRun && Directory.Exists(destDir))
                try
                {
                    Directory.Delete(destDir, true);
                }
                catch
                {
                    /* ignore */
                }

            Table summaryTable = new();
            summaryTable.Border(TableBorder.Rounded);
            summaryTable.Title(
                $"[bold cyan]Batch Repair Results ({batchResult.TotalRepaired}/{batchResult.TotalDiscovered} Succeeded - {batchResult.SuccessRate:F1}%)[/]");
            summaryTable.AddColumn("Mod Name");
            summaryTable.AddColumn("Status");
            summaryTable.AddColumn("Files Changed");
            summaryTable.AddColumn("Fixes Applied");
            summaryTable.AddColumn("Notes / Error");

            foreach (RepairResult res in batchResult.Results)
            {
                string status = res.Success ? "[bold green]REPAIRED[/]" : "[bold red]FAILED[/]";
                string notes = res.Success
                    ? res.TransformationLog.Count > 0 ? Markup.Escape(res.TransformationLog[0]) : "OK"
                    : Markup.Escape(res.ErrorMessage ?? "Unknown error");

                summaryTable.AddRow(
                    Markup.Escape(res.ModName),
                    status,
                    res.TransformedFilesCount.ToString(),
                    res.FixesAppliedCount.ToString(),
                    notes);
            }

            AnsiConsole.Write(summaryTable);

            Panel panel = new(new Rows(
                new Markup($"[bold]Discovered Mods:[/]  {batchResult.TotalDiscovered}"),
                new Markup($"[bold green]Repaired Successfully:[/] {batchResult.TotalRepaired}"),
                new Markup($"[bold red]Failed Mods:[/]           {batchResult.TotalFailed}"),
                new Markup($"[bold gold1]Repair Success Rate:[/]   {batchResult.SuccessRate:F1}% (Target: >= 70.0%)")
            ))
            {
                Header = new PanelHeader("[bold green]Compatibility Repair Summary[/]"),
                Border = BoxBorder.Double
            };

            AnsiConsole.Write(panel);
            return;
        }

        AnsiConsole.MarkupLine(
            $"[red]Error: Target path '{Markup.Escape(targetPath)}' does not exist as a file or directory.[/]");
    }
}
