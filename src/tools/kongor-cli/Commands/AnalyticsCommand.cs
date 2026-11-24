// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.ComponentModel;
using System.Text.Json;
using Kongor.Cli.Mcp.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Kongor.Cli.Commands;

public sealed class AnalyticsCommandSettings : CommandSettings
{
    [CommandArgument(0, "[action]")]
    [Description("Analytics action ('sql', 'summary', 'breakdown', 'heatmap', 'matchup', 'list' (default))")]
    [DefaultValue("list")]
    public string Action { get; init; } = "list";

    [CommandArgument(1, "[argument]")]
    [Description("SQL query string, Match ID, or target Hero ID")]
    public string? Argument { get; init; }

    [CommandOption("-p|--path <PATH>")]
    [Description("Explicit path to Apache Parquet match telemetry file")]
    public string? ParquetPath { get; init; }

    [CommandOption("-n|--limit <LIMIT>")]
    [Description("Limit number of rows or files returned")]
    [DefaultValue(50)]
    public int Limit { get; init; } = 50;

    [CommandOption("--json")]
    [Description("Output raw JSON payload rather than formatted Spectre table")]
    public bool JsonOutput { get; init; }
}

public sealed class AnalyticsCommand : AsyncCommand<AnalyticsCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, AnalyticsCommandSettings settings)
    {
        using DuckDbMatchAnalyticsService service = new();
        string action = settings.Action.ToLowerInvariant();

        switch (action)
        {
            case "list" or "files" or "catalog":
            {
                var files = service.ListParquetFiles();
                if (settings.JsonOutput)
                {
                    Console.WriteLine(JsonSerializer.Serialize(files, new JsonSerializerOptions { WriteIndented = true }));
                    return 0;
                }

                AnsiConsole.Write(new Rule("[bold cyan]🦆 Parquet Match Telemetry Lakehouse Catalog[/]").LeftJustified());
                if (files.Count == 0)
                {
                    AnsiConsole.MarkupLine("[yellow]No parquet telemetry files found in standard locations (userdataKONGOR/telemetry/lakehouse, logs/matches).[/]");
                    return 0;
                }

                Table table = new();
                table.AddColumn("[bold]File Name[/]");
                table.AddColumn("[bold]Match ID[/]");
                table.AddColumn("[bold]Size[/]");
                table.AddColumn("[bold]Last Modified[/]");
                table.AddColumn("[bold]Path[/]");

                foreach (var f in files.Take(settings.Limit))
                {
                    string matchStr = f.MatchId?.ToString() ?? "-";
                    table.AddRow(
                        Markup.Escape(f.FileName),
                        $"[cyan]{matchStr}[/]",
                        f.FormattedSize,
                        f.LastModified.ToString("yyyy-MM-dd HH:mm:ss"),
                        $"[grey]{Markup.Escape(f.FullPath)}[/]"
                    );
                }

                AnsiConsole.Write(table);
                AnsiConsole.MarkupLine($"[grey]Total files: {files.Count}[/]");
                return 0;
            }

            case "sql" or "query":
            {
                if (string.IsNullOrWhiteSpace(settings.Argument))
                {
                    AnsiConsole.MarkupLine("[red]Error: SQL query argument is required. Example: kongor analytics sql \"SELECT * FROM match_combat_events LIMIT 10\"[/]");
                    return 1;
                }

                var queryResult = await service.ExecuteQueryAsync(settings.Argument, settings.ParquetPath);
                if (!queryResult.Success)
                {
                    AnsiConsole.MarkupLine($"[red]Query Error:[/] {Markup.Escape(queryResult.Error ?? "Unknown SQL error")}");
                    return 1;
                }

                if (settings.JsonOutput)
                {
                    Console.WriteLine(JsonSerializer.Serialize(queryResult, new JsonSerializerOptions { WriteIndented = true }));
                    return 0;
                }

                AnsiConsole.Write(new Rule($"[bold cyan]🦆 DuckDB SQL Analytics[/] [grey]({queryResult.ExecutionDurationMs:F2} ms, {queryResult.RowCount} rows)[/]").LeftJustified());

                if (queryResult.Columns.Count == 0 || queryResult.Rows.Count == 0)
                {
                    AnsiConsole.MarkupLine("[yellow]Query executed successfully with 0 result rows.[/]");
                    return 0;
                }

                Table table = new();
                foreach (string col in queryResult.Columns)
                {
                    table.AddColumn($"[bold]{Markup.Escape(col)}[/]");
                }

                foreach (var row in queryResult.Rows.Take(settings.Limit))
                {
                    string[] cells = new string[queryResult.Columns.Count];
                    for (int i = 0; i < queryResult.Columns.Count; i++)
                    {
                        object? val = i < row.Count ? row[i] : null;
                        cells[i] = val is not null ? Markup.Escape(val.ToString() ?? "") : "[grey]NULL[/]";
                    }
                    table.AddRow(cells);
                }

                AnsiConsole.Write(table);
                if (queryResult.RowCount > settings.Limit)
                {
                    AnsiConsole.MarkupLine($"[grey](Displaying {settings.Limit} of {queryResult.RowCount} total rows)[/]");
                }
                return 0;
            }

            case "summary" or "player":
            {
                int accountId = int.TryParse(settings.Argument, out int parsedAcc) ? parsedAcc : 1;
                var summary = await service.GetPlayerSummaryAsync(accountId, parquetPath: settings.ParquetPath);

                if (settings.JsonOutput)
                {
                    Console.WriteLine(JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
                    return 0;
                }

                AnsiConsole.Write(new Rule($"[bold cyan]🏆 Player Combat Summary[/] [grey](Account ID: {accountId})[/]").LeftJustified());

                if (summary is null)
                {
                    AnsiConsole.MarkupLine($"[yellow]No combat events found for Account #{accountId}.[/]");
                    return 0;
                }

                Table table = new();
                table.AddColumn("[bold]Account[/]");
                table.AddColumn("[bold]Hero ID[/]");
                table.AddColumn("[bold green]Kills[/]");
                table.AddColumn("[bold red]Deaths[/]");
                table.AddColumn("[bold yellow]Assists[/]");
                table.AddColumn("[bold cyan]K/D Ratio[/]");
                table.AddColumn("[bold magenta]Damage Dealt[/]");
                table.AddColumn("[bold blue]Wards Placed[/]");
                table.AddColumn("[bold]Total Matches[/]");

                table.AddRow(
                    summary.AccountId.ToString(),
                    $"[yellow]Hero #{summary.HeroId}[/]",
                    $"[green]{summary.Kills}[/]",
                    $"[red]{summary.Deaths}[/]",
                    $"[yellow]{summary.Assists}[/]",
                    $"[cyan]{summary.KillDeathRatio:F2}[/]",
                    $"[magenta]{summary.TotalDamage:N0}[/]",
                    $"[blue]{summary.WardsPlaced}[/]",
                    summary.TotalMatches.ToString()
                );

                AnsiConsole.Write(table);
                return 0;
            }

            case "breakdown" or "scorecard":
            {
                long matchId = long.TryParse(settings.Argument, out long parsedMatch) ? parsedMatch : 1;
                var breakdown = await service.GetMatchBreakdownAsync(matchId, settings.ParquetPath);

                if (settings.JsonOutput)
                {
                    Console.WriteLine(JsonSerializer.Serialize(breakdown, new JsonSerializerOptions { WriteIndented = true }));
                    return 0;
                }

                AnsiConsole.Write(new Rule($"[bold cyan]📊 Match Scorecard Breakdown[/] [grey](Match ID: {matchId})[/]").LeftJustified());

                if (breakdown.Count == 0)
                {
                    AnsiConsole.MarkupLine($"[yellow]No player breakdown events found for Match #{matchId}.[/]");
                    return 0;
                }

                Table table = new();
                table.AddColumn("[bold]Slot[/]");
                table.AddColumn("[bold]Hero ID[/]");
                table.AddColumn("[bold green]K[/]");
                table.AddColumn("[bold red]D[/]");
                table.AddColumn("[bold yellow]A[/]");
                table.AddColumn("[bold cyan]K/D[/]");
                table.AddColumn("[bold magenta]Damage[/]");
                table.AddColumn("[bold blue]Wards[/]");
                table.AddColumn("[bold yellow]Towers[/]");

                foreach (var p in breakdown)
                {
                    string teamColor = p.PlayerIndex < 5 ? "blue" : "red";
                    table.AddRow(
                        $"[{teamColor}]#{p.PlayerIndex}[/]",
                        $"Hero #{p.HeroId}",
                        $"[green]{p.Kills}[/]",
                        $"[red]{p.Deaths}[/]",
                        $"[yellow]{p.Assists}[/]",
                        $"[cyan]{p.KillDeathRatio:F2}[/]",
                        $"[magenta]{p.TotalDamage:N0}[/]",
                        $"[blue]{p.WardsPlaced}[/]",
                        p.TowersKilled.ToString()
                    );
                }

                AnsiConsole.Write(table);
                return 0;
            }

            case "heatmap" or "minimap":
            {
                long matchId = long.TryParse(settings.Argument, out long parsedMatch) ? parsedMatch : 1;
                var points = await service.GetSpatialHeatmapAsync(matchId, eventType: null, settings.ParquetPath);

                if (settings.JsonOutput)
                {
                    Console.WriteLine(JsonSerializer.Serialize(points, new JsonSerializerOptions { WriteIndented = true }));
                    return 0;
                }

                AnsiConsole.Write(new Rule($"[bold cyan]🗺️ Spatial 2D Minimap Event Density[/] [grey](Match #{matchId}, {points.Count} points)[/]").LeftJustified());

                if (points.Count == 0)
                {
                    AnsiConsole.MarkupLine($"[yellow]No spatial coordinates recorded for Match #{matchId}.[/]");
                    return 0;
                }

                Table table = new();
                table.AddColumn("[bold]Event Type[/]");
                table.AddColumn("[bold]Position X[/]");
                table.AddColumn("[bold]Position Y[/]");
                table.AddColumn("[bold cyan]Frequency[/]");

                foreach (var pt in points.Take(settings.Limit))
                {
                    table.AddRow(
                        Markup.Escape(pt.EventTypeName),
                        pt.PositionX.ToString("F1"),
                        pt.PositionY.ToString("F1"),
                        $"[cyan]{pt.Frequency}[/]"
                    );
                }

                AnsiConsole.Write(table);
                return 0;
            }

            case "matchup" or "matrix":
            {
                short heroId = short.TryParse(settings.Argument, out short parsedHero) ? parsedHero : (short)1;
                var matchups = await service.GetHeroMatchupMatrixAsync(heroId, opponentHeroId: null, settings.ParquetPath);

                if (settings.JsonOutput)
                {
                    Console.WriteLine(JsonSerializer.Serialize(matchups, new JsonSerializerOptions { WriteIndented = true }));
                    return 0;
                }

                AnsiConsole.Write(new Rule($"[bold cyan]⚔️ Hero Head-to-Head Combat Matrix[/] [grey](Hero #{heroId})[/]").LeftJustified());

                if (matchups.Count == 0)
                {
                    AnsiConsole.MarkupLine($"[yellow]No matchup events recorded for Hero #{heroId}.[/]");
                    return 0;
                }

                Table table = new();
                table.AddColumn("[bold]Hero ID[/]");
                table.AddColumn("[bold]Opponent Hero ID[/]");
                table.AddColumn("[bold green]Kills Against[/]");
                table.AddColumn("[bold red]Deaths To[/]");
                table.AddColumn("[bold cyan]K/D Ratio[/]");
                table.AddColumn("[bold magenta]Damage Dealt[/]");
                table.AddColumn("[bold]Matches[/]");

                foreach (var m in matchups.Take(settings.Limit))
                {
                    table.AddRow(
                        $"Hero #{m.HeroId}",
                        $"[red]Hero #{m.OpponentHeroId}[/]",
                        $"[green]{m.KillsAgainst}[/]",
                        $"[red]{m.DeathsTo}[/]",
                        $"[cyan]{m.KillDeathRatio:F2}[/]",
                        $"[magenta]{m.DamageDealt:N0}[/]",
                        m.TotalMatches.ToString()
                    );
                }

                AnsiConsole.Write(table);
                return 0;
            }

            default:
                AnsiConsole.MarkupLine($"[red]Unknown action '{Markup.Escape(action)}'.[/]");
                AnsiConsole.MarkupLine("Supported actions: [cyan]sql[/], [cyan]summary[/], [cyan]breakdown[/], [cyan]heatmap[/], [cyan]matchup[/], [cyan]list[/]");
                return 1;
        }
    }
}
