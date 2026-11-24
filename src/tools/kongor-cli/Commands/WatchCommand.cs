// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Kongor.Cli.Mcp.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Kongor.Cli.Commands;

public sealed class WatchCommandSettings : CommandSettings
{
    [CommandOption("-d|--debounce <MS>")]
    [Description("Sliding debounce buffer window in milliseconds to wait for disk I/O bursts (default: 400)")]
    public int DebounceMs { get; init; } = 400;

    [CommandOption("-p|--path <PATH>")]
    [Description("Additional directory path to supervise for asset changes")]
    public string? CustomPath { get; init; }

    [CommandOption("--dry-run")]
    [Description("Validate watcher configuration and test file lock check without running continuously")]
    public bool DryRun { get; init; }
}

/// <summary>
/// kongor watch: Real-time asset watcher using sliding debounce and atomic file swaps
/// to eliminate Win32 ERROR_SHARING_VIOLATION file lock errors, triggering hot reload.
/// </summary>
public sealed class WatchCommand : AsyncCommand<WatchCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, WatchCommandSettings settings)
    {
        AnsiConsole.Write(new Rule("[bold yellow]ProjectKongor Real-Time Asset Watcher & Hot-Reloader[/]").LeftJustified());

        var watcher = new AssetWatcherService(
            logger: msg => AnsiConsole.MarkupLine($"[grey]>>> {Markup.Escape(msg)}[/]"));

        List<string>? paths = null;
        if (!string.IsNullOrWhiteSpace(settings.CustomPath))
        {
            paths = [Path.GetFullPath(settings.CustomPath)];
        }

        watcher.OnAssetSettled += assetPath =>
        {
            AnsiConsole.MarkupLine($"[bold green]⚡ Asset Settled:[/] [white]{Markup.Escape(Path.GetFileName(assetPath))}[/] [grey]({Markup.Escape(assetPath)})[/]");
        };

        watcher.OnError += (path, ex) =>
        {
            AnsiConsole.MarkupLine($"[bold red]Watcher Error on '{Markup.Escape(path)}':[/] {Markup.Escape(ex.Message)}");
        };

        watcher.StartWatching(paths, settings.DebounceMs);

        AnsiConsole.MarkupLine($"[bold green]✔ Active watch directories:[/] [white]{string.Join(", ", watcher.WatchedPaths)}[/]");
        AnsiConsole.MarkupLine($"[cyan]Sliding debounce window:[/] [white]{watcher.DebounceMs} ms[/]");

        if (settings.DryRun)
        {
            AnsiConsole.MarkupLine("[bold blue][[DRY RUN]][/] Watcher initialized and verified successfully. Exiting.");
            watcher.StopWatching();
            return 0;
        }

        AnsiConsole.MarkupLine("[yellow]Supervising assets... Press [white]Ctrl+C[/] to exit.[/]\n");

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cts.Token);
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("\n[grey]Watch cancelled by user.[/]");
        }
        finally
        {
            watcher.StopWatching();
            AnsiConsole.MarkupLine($"[bold green]✔ Asset supervisor stopped cleanly.[/] (Debounced events: {watcher.TotalEventsDebounced}, Atomic swaps: {watcher.TotalSwapsExecuted})");
        }

        return 0;
    }
}
