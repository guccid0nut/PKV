// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using Kongor.Cli.Mcp.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Kongor.Cli.Commands;

public sealed class VfsHydrateCommandSettings : CommandSettings
{
    [CommandOption("-f|--force")]
    [Description("Force hydration even if target archive already contains entries")]
    public bool Force { get; init; }
}

public sealed class VfsHydrateCommand : AsyncCommand<VfsHydrateCommandSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, VfsHydrateCommandSettings settings)
    {
        AnsiConsole.Write(new Rule("[bold cyan]ProjectKongor VFS Texture & Asset Hydration Pipeline[/]").LeftJustified());
        string repoRoot = Program.FindRepoRoot();

        var service = new VfsHydrationService(msg =>
        {
            AnsiConsole.MarkupLine($"[grey]>>> {Markup.Escape(msg)}[/]");
        });

        var result = service.Hydrate(repoRoot, settings.Force);

        if (!result.Success)
        {
            AnsiConsole.MarkupLine($"[bold red]✘ Hydration failed: {Markup.Escape(result.Error ?? "Unknown error")}[/]");
            return Task.FromResult(1);
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("[cyan]Metric[/]");
        table.AddColumn("[cyan]Value[/]");
        table.AddRow("Existing Entries Preserved", $"{result.ExistingEntriesPreserved:N0}");
        table.AddRow("Entries Modified (Bubbles MDFs)", $"{result.EntriesModified:N0}");
        table.AddRow("Textures Un-trapped & Added", $"{result.EntriesAdded:N0}");
        table.AddRow("Output Archive Size", $"{result.OutputSizeBytes / 1024 / 1024:N0} MB");
        table.AddRow("Duration", $"{result.ElapsedMilliseconds:N0} ms");
        AnsiConsole.Write(table);

        AnsiConsole.MarkupLine("[bold green]✔ VFS Hydration completed successfully! All UI, shared, and hero icon textures are active.[/]");
        return Task.FromResult(0);
    }
}
