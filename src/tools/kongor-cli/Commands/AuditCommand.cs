// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.ComponentModel;
using System.Text.Json;
using System.Threading.Tasks;
using Kongor.Cli.Mcp.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Kongor.Cli.Commands;

public sealed class AuditCommandSettings : CommandSettings
{
    [CommandArgument(0, "[TARGET]")]
    [Description("Target audit domain: 'stats', 'match_history', 'all' (default: 'stats')")]
    public string Target { get; init; } = "stats";

    [CommandArgument(1, "[NICKNAME]")]
    [Description("Target player nickname to audit (default: 'MCPPlayer')")]
    public string Nickname { get; init; } = "MCPPlayer";

    [CommandOption("-t|--tab <TAB>")]
    [Description("UI tab to verify: 'overview', 'match_history', 'mastery', 'match_details', 'ghosting', 'all'")]
    public string? Tab { get; init; }

    [CommandOption("--synthetic")]
    [Description("Run in headless deterministic synthetic mode (ideal for CI)")]
    public bool Synthetic { get; init; } = true;

    [CommandOption("--dry-run")]
    [Description("Simulate audit without updating backlog or writing to docs/ISSUES.md")]
    public bool DryRun { get; init; }

    [CommandOption("--no-backlog")]
    [Description("Skip recording detected discrepancies into docs/ISSUES.md")]
    public bool NoBacklog { get; init; }

    [CommandOption("--json")]
    [Description("Output machine-readable JSON result")]
    public bool Json { get; init; }
}

/// <summary>
/// kongor audit: Autonomous game client telemetry, stats & match history discrepancy auditor,
/// and self-healing issue logger (Phase 5 Subsystem A).
/// </summary>
public sealed class ClientStatsAuditCommand : AsyncCommand<AuditCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, AuditCommandSettings settings)
    {
        if (settings.Target is "assets" or "vfs" or "textures" or "completeness")
        {
            var vfsCmd = new VfsAuditCommand();
            return await vfsCmd.ExecuteAsync(context, new VfsAuditCommandSettings
            {
                Target = settings.Tab,
                Scope = "all",
                Json = settings.Json
            });
        }

        string targetTab = settings.Tab ?? settings.Target switch
        {
            "match-history" or "match_history" or "matches" => "match_history",
            "stats" or "overview" => "overview",
            "mastery" => "mastery",
            "match_details" or "match-details" or "details" => "match_details",
            "ghosting" => "ghosting",
            _ => "all"
        };

        if (!settings.Json)
        {
            AnsiConsole.Write(new Rule("[bold cyan]ProjectKongor Autonomous Client Telemetry & Stats Auditor[/]").LeftJustified());
            AnsiConsole.MarkupLine($"[grey]Auditing target:[/] [white]{Markup.Escape(settings.Nickname)}[/] | [grey]Tab:[/] [yellow]{targetTab}[/] | [grey]Mode:[/] [bold green]{(settings.Synthetic ? "Synthetic (CI)" : "Live IPC")}[/]");
        }

        var auditor = new ClientStatsAuditorService(
            logger: msg =>
            {
                if (!settings.Json) AnsiConsole.MarkupLine($"[grey]>>> {Markup.Escape(msg)}[/]");
            });

        var report = await auditor.RunFullAuditAsync(new StatsAuditRequest
        {
            Nickname = settings.Nickname,
            Tab = targetTab,
            Synthetic = settings.Synthetic,
            RecordBacklog = !settings.DryRun && !settings.NoBacklog
        });

        if (settings.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            return report.Success ? 0 : 1;
        }

        // 1. Overview Table
        var overviewTable = new Table().Border(TableBorder.Rounded).BorderColor(Color.Aqua);
        overviewTable.AddColumn(new TableColumn("[bold]Statistic[/]").Width(25));
        overviewTable.AddColumn(new TableColumn("[bold]Ground Truth (DB)[/]"));
        overviewTable.AddColumn(new TableColumn("[bold]Rendered UI[/]"));
        overviewTable.AddColumn(new TableColumn("[bold]Parity Status[/]"));

        if (report.GroundTruth != null && report.RenderedClientState != null)
        {
            var gt = report.GroundTruth;
            var ui = report.RenderedClientState;

            overviewTable.AddRow("Total Wins", gt.Wins.ToString(), ui.Wins.ToString(), gt.Wins == ui.Wins ? "[green]✔ OK[/]" : "[bold red]✘ MISMATCH[/]");
            overviewTable.AddRow("Total Losses", gt.Losses.ToString(), ui.Losses.ToString(), gt.Losses == ui.Losses ? "[green]✔ OK[/]" : "[bold red]✘ MISMATCH[/]");
            overviewTable.AddRow("Win Rate", $"{Math.Floor((double)gt.Wins / Math.Max(1, gt.Wins + gt.Losses) * 100):0}%", ui.WinningPercentage, ui.WinningPercentage.Contains("nan", StringComparison.OrdinalIgnoreCase) ? "[bold red]✘ NaN ANOMALY[/]" : "[green]✔ OK[/]");
            overviewTable.AddRow("Total Disconnects", gt.Disconnects.ToString(), ui.Disconnects.ToString(), gt.Disconnects == ui.Disconnects ? "[green]✔ OK[/]" : "[bold red]✘ MISMATCH[/]");
            overviewTable.AddRow("MMR / PSR", gt.Mmr.ToString(), ui.Mmr.ToString(), gt.Mmr == ui.Mmr ? "[green]✔ OK[/]" : "[bold red]✘ MISMATCH[/]");
        }

        AnsiConsole.Write(overviewTable);

        // 2. Match History Table
        if (report.RenderedClientState?.MatchRows.Count > 0)
        {
            var matchTable = new Table().Border(TableBorder.Rounded).BorderColor(Color.Blue);
            matchTable.AddColumn(new TableColumn("[bold]Match ID[/]"));
            matchTable.AddColumn(new TableColumn("[bold]Hero Name[/]"));
            matchTable.AddColumn(new TableColumn("[bold]K/D/A[/]"));
            matchTable.AddColumn(new TableColumn("[bold]Duration[/]"));
            matchTable.AddColumn(new TableColumn("[bold]Outcome[/]"));
            matchTable.AddColumn(new TableColumn("[bold]Texture / VFS[/]"));

            foreach (var row in report.RenderedClientState.MatchRows)
            {
                bool isVictory = row.ResultTexture.Contains("victory", StringComparison.OrdinalIgnoreCase);
                string outcome = isVictory ? "[green]Victory[/]" : "[red]Defeat[/]";
                bool textureOk = !string.IsNullOrEmpty(row.HeroIconTexture) && !row.HeroIconTexture.Contains("unknown", StringComparison.OrdinalIgnoreCase);
                string textureStatus = textureOk ? "[green]✔ OK[/]" : "[bold red]✘ MISSING[/]";

                matchTable.AddRow(
                    Markup.Escape(row.MatchId),
                    Markup.Escape(row.HeroName),
                    Markup.Escape(row.KdaText),
                    Markup.Escape(row.DurationText),
                    outcome,
                    textureStatus
                );
            }

            AnsiConsole.Write(matchTable);
        }

        // 3. Discrepancy & Backlog Summary
        if (report.Success)
        {
            AnsiConsole.MarkupLine("[bold green]✔ Zero discrepancies detected![/] 100% data and visual parity between client UI and ground truth.");
            return 0;
        }

        AnsiConsole.MarkupLine($"\n[bold red]⚠ Detected {report.Discrepancies.Count} discrepancies during telemetry sweep:[/]");
        foreach (var disc in report.Discrepancies)
        {
            AnsiConsole.MarkupLine($"  [bold yellow]• [[{Markup.Escape(disc.RuleCode.ToString())}]][/] {Markup.Escape(disc.Description)} (Expected: [green]{Markup.Escape(disc.Expected)}[/], Actual: [red]{Markup.Escape(disc.Actual)}[/])");
            if (!string.IsNullOrEmpty(disc.RemediationHint))
            {
                AnsiConsole.MarkupLine($"    [grey]Remediation Hint: {Markup.Escape(disc.RemediationHint)}[/]");
            }
        }

        if (report.LoggedIssueIds.Count > 0)
        {
            AnsiConsole.MarkupLine($"\n[bold yellow]📝 Logged {report.LoggedIssueIds.Count} new issues into docs/ISSUES.md:[/] [cyan]{string.Join(", ", report.LoggedIssueIds)}[/]");
        }

        return 1;
    }
}
