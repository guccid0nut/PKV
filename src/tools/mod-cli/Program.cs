// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.ComponentModel;
using System.Text;
using Kongor.ModCli.Commands;
using Kongor.Modding.Core.Models;
using Kongor.Modding.Core.Packaging;
using Kongor.Modding.Core.Parsing;
using Kongor.Modding.Harvester;
using Kongor.Modding.Harvester.Models;
using Kongor.Modding.Resolution;
using Kongor.Modding.Vfs;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Kongor.ModCli;

public class ModBaseSettings : CommandSettings
{
    [CommandOption("--mods-dir <DIR>")]
    [Description("Directory containing .honmod packages")]
    public string? ModsDir { get; init; }

    [CommandOption("--client-dir <DIR>")]
    [Description("HoN Client game directory")]
    public string? ClientDir { get; init; }

    public string GetEffectiveModsDir() => !string.IsNullOrWhiteSpace(ModsDir) ? ModsDir : Program.ResolveDefaultModsDir();
    public string GetEffectiveClientDir() => !string.IsNullOrWhiteSpace(ClientDir) ? ClientDir : Program.ResolveDefaultClientDir();
}

public class ModTargetSettings : ModBaseSettings
{
    [CommandArgument(0, "<mod>")]
    [Description("Name, filename, or pattern of the mod")]
    public string Mod { get; init; } = string.Empty;
}

public class ApplySettings : ModBaseSettings
{
    [CommandOption("--mode <MODE>")]
    [Description("Injection mode: 'vfs' (loose files to override/) or 'archive' (resources999.s2z)")]
    [DefaultValue("vfs")]
    public string Mode { get; init; } = "vfs";
}

public class HarvestSettings : CommandSettings
{
    [CommandOption("--output-dir <DIR>")]
    [Description("Destination directory for harvested community mods and manifests")]
    [DefaultValue("harvested_mods")]
    public string OutputDir { get; init; } = "harvested_mods";

    [CommandOption("--category <CAT>")]
    [Description("Filter by community mod category (e.g. 'Working mods', 'Broken/Outdated mods', 'All')")]
    public string? Category { get; init; }

    [CommandOption("--dry-run")]
    [Description("Scrape catalog and resolve URLs without downloading binary files")]
    [DefaultValue(false)]
    public bool DryRun { get; init; }

    [CommandOption("--catalog-file <PATH>")]
    [Description("Path to local Trello JSON catalog file for offline/custom harvesting")]
    public string? CatalogFile { get; init; }

    [CommandOption("--force")]
    [Description("Overwrite existing cached mod packages")]
    [DefaultValue(false)]
    public bool Force { get; init; }
}

public class PackageSettings : CommandSettings
{
    [CommandArgument(0, "<dir>")]
    [Description("Source unpacked mod directory containing mod.xml")]
    public string Dir { get; init; } = string.Empty;

    [CommandOption("--output <PATH>")]
    [Description("Destination .honmod file path")]
    public string? Output { get; init; }
}

public sealed class ListCommand : AsyncCommand<ModBaseSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, ModBaseSettings settings)
    {
        await Program.HandleListAsync(settings.GetEffectiveModsDir());
        return 0;
    }
}

public sealed class EnableCommand : AsyncCommand<ModTargetSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, ModTargetSettings settings)
    {
        await Program.HandleEnableAsync(settings.Mod, settings.GetEffectiveModsDir());
        return 0;
    }
}

public sealed class DisableCommand : AsyncCommand<ModTargetSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, ModTargetSettings settings)
    {
        await Program.HandleDisableAsync(settings.Mod, settings.GetEffectiveModsDir());
        return 0;
    }
}

public sealed class InspectCommand : AsyncCommand<ModTargetSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, ModTargetSettings settings)
    {
        await Program.HandleInspectAsync(settings.Mod, settings.GetEffectiveModsDir());
        return 0;
    }
}

public sealed class ApplyCommand : AsyncCommand<ApplySettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, ApplySettings settings)
    {
        await Program.HandleApplyAsync(settings.Mode, settings.GetEffectiveModsDir(), settings.GetEffectiveClientDir());
        return 0;
    }
}

public sealed class RollbackCommand : AsyncCommand<ModBaseSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, ModBaseSettings settings)
    {
        await Program.HandleRollbackAsync(settings.GetEffectiveClientDir());
        return 0;
    }
}

public sealed class VerifyCommand : AsyncCommand<ModBaseSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, ModBaseSettings settings)
    {
        await Program.HandleVerifyAsync(settings.GetEffectiveModsDir());
        return 0;
    }
}

public sealed class HarvestCommand : AsyncCommand<HarvestSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, HarvestSettings settings)
    {
        await Program.HandleHarvestAsync(settings.OutputDir, settings.Category, settings.DryRun, settings.CatalogFile, settings.Force);
        return 0;
    }
}

public sealed class PackageCommand : AsyncCommand<PackageSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, PackageSettings settings)
    {
        await Program.HandlePackageAsync(settings.Dir, settings.Output);
        return 0;
    }
}

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && (args[0].Equals("--version", StringComparison.OrdinalIgnoreCase) ||
                                 args[0].Equals("-v", StringComparison.OrdinalIgnoreCase) ||
                                 args[0].Equals("version", StringComparison.OrdinalIgnoreCase)))
        {
            AnsiConsole.MarkupLine($"[bold cyan]mod-cli[/] version [green]{NEXUS.Contracts.Domain.NexusVersionInfo.PlatformVersion}[/]");
            return 0;
        }

        CommandApp app = new();
        app.Configure(config =>
        {
            config.SetApplicationName("mod-cli");
            config.SetApplicationVersion(NEXUS.Contracts.Domain.NexusVersionInfo.PlatformVersion);

            config.AddCommand<ListCommand>("list")
                .WithDescription("List all discovered mods with enabled/disabled status and metadata");

            config.AddCommand<EnableCommand>("enable")
                .WithDescription("Enable one or more mods");

            config.AddCommand<DisableCommand>("disable")
                .WithDescription("Disable one or more mods");

            config.AddCommand<InspectCommand>("inspect")
                .WithDescription("Inspect detailed manifest, dependencies, and target files of a mod");

            config.AddCommand<ApplyCommand>("apply")
                .WithDescription("Apply all enabled mods with DAG ordering to game client");

            config.AddCommand<RollbackCommand>("rollback")
                .WithDescription("Revert all applied modifications cleanly using the rollback journal");

            config.AddCommand<RepairCliCommand>("repair")
                .WithDescription("Run automated compatibility repair on broken or outdated HoN mods");

            config.AddCommand<VerifyCommand>("verify")
                .WithDescription("Run dependency checking, cycle detection, and collision matrix tests on installed mods");

            config.AddCommand<HarvestCommand>("harvest")
                .WithDescription("Harvest and scrape community mods from HoN Mods Repository Trello board");

            config.AddCommand<PackageCommand>("package")
                .WithAlias("pack")
                .WithAlias("bundle")
                .WithDescription("Package loose overrides or mod directory into a clean single-layer archive (.s2z or .honmod)");
        });

        return await app.RunAsync(args);
    }

    public static async Task HandleListAsync(string modsDir)
    {
        ModManager manager = new();
        IReadOnlyList<ModPackage> mods = await manager.DiscoverModsAsync(modsDir);
        ModStateConfig state = await ModStateConfig.LoadAsync(modsDir);

        if (mods.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]No mods found in directory:[/] [bold]{modsDir}[/]");
            return;
        }

        foreach (ModPackage mod in mods) mod.IsEnabled = state.EnabledMods.Contains(mod.Manifest.Name);

        Table table = new();
        table.Border(TableBorder.Rounded);
        table.Title($"[bold cyan]ProjectKongor Discovered Mods ({mods.Count})[/]");
        table.AddColumn("[bold]Status[/]");
        table.AddColumn("[bold]Name[/]");
        table.AddColumn("[bold]Version[/]");
        table.AddColumn("[bold]Author[/]");
        table.AddColumn("[bold]Category[/]");
        table.AddColumn("[bold]Description[/]");

        foreach (ModPackage mod in mods.OrderBy(m => m.Manifest.Name))
        {
            string status = mod.IsEnabled ? "[bold green]ENABLED[/]" : "[grey]Disabled[/]";
            string name = Markup.Escape(mod.Manifest.Name);
            string version = Markup.Escape(mod.Manifest.Version);
            string author = Markup.Escape(mod.Manifest.Author ?? "Unknown");
            string category = Markup.Escape(mod.Manifest.Category ?? "General");
            string desc = Markup.Escape(Truncate(mod.Manifest.Description ?? string.Empty, 50));

            table.AddRow(status, name, version, author, category, desc);
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[grey]Total: {mods.Count} mods ({mods.Count(m => m.IsEnabled)} enabled)[/]");
    }

    public static async Task HandleEnableAsync(string modQuery, string modsDir)
    {
        ModManager manager = new();
        IReadOnlyList<ModPackage> mods = await manager.DiscoverModsAsync(modsDir);
        ModStateConfig state = await ModStateConfig.LoadAsync(modsDir);

        List<ModPackage> matched = FindMatchingMods(mods, modQuery);
        if (matched.Count == 0)
        {
            AnsiConsole.MarkupLine($"[red]No mod matching query '{modQuery}' found in {modsDir}[/]");
            return;
        }

        foreach (ModPackage mod in matched)
        {
            // Auto-disable conflicting / incompatible mods to prevent simultaneous collisions
            foreach (ModIncompatibility incomp in mod.Manifest.Incompatibilities)
                if (state.EnabledMods.Contains(incomp.Name))
                {
                    state.EnabledMods.Remove(incomp.Name);
                    AnsiConsole.MarkupLine(
                        $"[yellow]ℹ Auto-disabled conflicting mod:[/] [bold]{Markup.Escape(incomp.Name)}[/]");
                }

            // Also check reverse incompatibilities
            foreach (ModPackage existing in mods.Where(m => state.EnabledMods.Contains(m.Manifest.Name)).ToList())
                if (existing.Manifest.Incompatibilities.Any(inc =>
                        inc.Name.Equals(mod.Manifest.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    state.EnabledMods.Remove(existing.Manifest.Name);
                    AnsiConsole.MarkupLine(
                        $"[yellow]ℹ Auto-disabled conflicting mod:[/] [bold]{Markup.Escape(existing.Manifest.Name)}[/]");
                }

            state.EnabledMods.Add(mod.Manifest.Name);
            AnsiConsole.MarkupLine(
                $"[green]✓ Enabled mod:[/] [bold]{Markup.Escape(mod.Manifest.Name)}[/] (v{mod.Manifest.Version})");
        }

        await ModStateConfig.SaveAsync(modsDir, state);
    }

    public static async Task HandleDisableAsync(string modQuery, string modsDir)
    {
        ModStateConfig state = await ModStateConfig.LoadAsync(modsDir);

        if (modQuery.Equals("all", StringComparison.OrdinalIgnoreCase) ||
            modQuery.Equals("*", StringComparison.OrdinalIgnoreCase))
        {
            int count = state.EnabledMods.Count;
            state.EnabledMods.Clear();
            await ModStateConfig.SaveAsync(modsDir, state);
            AnsiConsole.MarkupLine($"[yellow]✓ Disabled all active mods ({count} mods disabled).[/]");
            return;
        }

        ModManager manager = new();
        IReadOnlyList<ModPackage> mods = await manager.DiscoverModsAsync(modsDir);

        List<ModPackage> matched = FindMatchingMods(mods, modQuery);
        if (matched.Count == 0)
        {
            AnsiConsole.MarkupLine($"[red]No mod matching query '{modQuery}' found in {modsDir}[/]");
            return;
        }

        foreach (ModPackage mod in matched)
        {
            state.EnabledMods.Remove(mod.Manifest.Name);
            AnsiConsole.MarkupLine($"[yellow]✓ Disabled mod:[/] [bold]{Markup.Escape(mod.Manifest.Name)}[/]");
        }

        await ModStateConfig.SaveAsync(modsDir, state);
    }

    public static async Task HandleInspectAsync(string modQuery, string modsDir)
    {
        ModPackage? mod = null;
        if (File.Exists(modQuery))
        {
            mod = await HonModArchiveReader.ReadPackageAsync(modQuery);
        }
        else
        {
            ModManager manager = new();
            IReadOnlyList<ModPackage> mods = await manager.DiscoverModsAsync(modsDir);
            mod = FindMatchingMods(mods, modQuery).FirstOrDefault();
        }

        if (mod == null)
        {
            AnsiConsole.MarkupLine($"[red]Could not find mod matching '{modQuery}'[/]");
            return;
        }

        ModManifest m = mod.Manifest;
        Panel panel = new(new Rows(
            new Markup($"[bold cyan]Name:[/] {Markup.Escape(m.Name)}"),
            new Markup($"[bold cyan]Version:[/] {Markup.Escape(m.Version)}"),
            new Markup($"[bold cyan]Author:[/] {Markup.Escape(m.Author ?? "Unknown")}"),
            new Markup($"[bold cyan]App Version:[/] {Markup.Escape(m.AppVersion ?? "*")}"),
            new Markup($"[bold cyan]MM Version:[/] {Markup.Escape(m.MmVersion ?? "1.3")}"),
            new Markup($"[bold cyan]WebLink:[/] {Markup.Escape(m.WebLink ?? "None")}"),
            new Markup($"[bold cyan]File Path:[/] {Markup.Escape(mod.FilePath)}"),
            new Markup($"[bold cyan]Size:[/] {mod.FileSizeBytes:N0} bytes"),
            new Markup($"[bold cyan]SHA-256:[/] {mod.Sha256Hash}"),
            new Rule("[bold]Description[/]"),
            new Markup(Markup.Escape(m.Description ?? "No description provided.")),
            new Rule("[bold]Requirements & Ordering[/]"),
            new Markup(
                $"[bold]Requirements:[/] {(m.Requirements.Count == 0 ? "None" : string.Join(", ", m.Requirements.Select(r => $"{r.Name} {r.VersionExpression}".Trim())))}"),
            new Markup(
                $"[bold]Incompatibilities:[/] {(m.Incompatibilities.Count == 0 ? "None" : string.Join(", ", m.Incompatibilities.Select(r => $"{r.Name} {r.VersionExpression}".Trim())))}"),
            new Markup(
                $"[bold]Apply Before:[/] {(m.ApplyBefore.Count == 0 ? "None" : string.Join(", ", m.ApplyBefore.Select(r => r.Name)))}"),
            new Markup(
                $"[bold]Apply After:[/] {(m.ApplyAfter.Count == 0 ? "None" : string.Join(", ", m.ApplyAfter.Select(r => r.Name)))}"),
            new Rule("[bold]File Operations[/]"),
            new Markup(
                $"[bold]File Copies ({m.FileCopies.Count}):[/] {string.Join(", ", m.FileCopies.Take(10).Select(c => c.TargetName))}{(m.FileCopies.Count > 10 ? "..." : "")}"),
            new Markup(
                $"[bold]File Edits ({m.FileEdits.Count}):[/] {string.Join(", ", m.FileEdits.Take(10).Select(e => $"{e.TargetName} ({e.Operations.Count} ops)"))}{(m.FileEdits.Count > 10 ? "..." : "")}"),
            new Markup(
                $"[bold]Stringtable Edits ({m.StringtableEdits.Count}):[/] {string.Join(", ", m.StringtableEdits.Select(s => s.TargetName))}")
        ))
        {
            Header = new PanelHeader($"[bold gold1]Mod Inspection: {Markup.Escape(m.Name)}[/]"),
            Border = BoxBorder.Double
        };

        AnsiConsole.Write(panel);
    }

    public static async Task HandleApplyAsync(string modeStr, string modsDir, string clientDir)
    {
        InjectionMode mode = modeStr.Equals("archive", StringComparison.OrdinalIgnoreCase)
            ? InjectionMode.Archive
            : InjectionMode.Vfs;

        ModManager manager = new();
        IReadOnlyList<ModPackage> mods = await manager.DiscoverModsAsync(modsDir);
        ModStateConfig state = await ModStateConfig.LoadAsync(modsDir);

        foreach (ModPackage mod in mods) mod.IsEnabled = state.EnabledMods.Contains(mod.Manifest.Name);

        List<ModPackage> enabledMods = [.. mods.Where(m => m.IsEnabled)];
        if (enabledMods.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No mods are currently enabled. Use 'mod-cli enable <name>' first.[/]");
            return;
        }

        AnsiConsole.MarkupLine(
            $"[cyan]Applying {enabledMods.Count} enabled mods to client:[/] [bold]{clientDir}[/] ([green]Mode: {mode}[/])");

        ApplyResult result = await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Applying mod patches...", async ctx =>
            {
                return await manager.ApplyModsAsync(new ApplyOptions
                {
                    GameClientDirectory = clientDir,
                    ModsToApply = enabledMods,
                    Mode = mode
                });
            });

        if (!result.Success)
        {
            AnsiConsole.MarkupLine("[bold red]Apply Failed![/]");
            foreach (string err in result.Errors) AnsiConsole.MarkupLine($"[red]  ✗ {Markup.Escape(err)}[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[bold green]✓ Successfully applied {result.AppliedMods.Count} mods![/]");
        AnsiConsole.MarkupLine($"[grey]Modified files ({result.ModifiedFiles.Count}):[/]");
        foreach (string f in result.ModifiedFiles.Take(15)) AnsiConsole.MarkupLine($"  • [cyan]{Markup.Escape(f)}[/]");
        if (result.ModifiedFiles.Count > 15)
            AnsiConsole.MarkupLine($"  [grey]... and {result.ModifiedFiles.Count - 15} more files[/]");

        if (result.Warnings.Count > 0)
        {
            AnsiConsole.MarkupLine($"[yellow]Warnings ({result.Warnings.Count}):[/]");
            foreach (string w in result.Warnings) AnsiConsole.MarkupLine($"  [yellow]⚠ {Markup.Escape(w)}[/]");
        }
    }

    public static async Task HandleRollbackAsync(string clientDir)
    {
        ModManager manager = new();
        AnsiConsole.MarkupLine($"[cyan]Executing rollback on client directory:[/] [bold]{clientDir}[/]");

        RollbackResult res = await manager.RollbackAsync(clientDir);
        if (res.Success)
            AnsiConsole.MarkupLine($"[bold green]✓ {res.Message}[/]");
        else
            AnsiConsole.MarkupLine($"[bold red]✗ {res.Message}[/]");
    }

    public static async Task HandleVerifyAsync(string modsDir)
    {
        ModManager manager = new();
        IReadOnlyList<ModPackage> mods = await manager.DiscoverModsAsync(modsDir);
        ModStateConfig state = await ModStateConfig.LoadAsync(modsDir);

        foreach (ModPackage mod in mods) mod.IsEnabled = state.EnabledMods.Contains(mod.Manifest.Name);

        AnsiConsole.MarkupLine($"[cyan]Verifying {mods.Count} mods in:[/] [bold]{modsDir}[/]");

        VerificationResult vres = manager.VerifyMods(mods);
        if (vres.Success)
        {
            AnsiConsole.MarkupLine(
                "[bold green]✓ All dependency checks, cycle detection, and conflict tests PASSED![/]");
        }
        else
        {
            AnsiConsole.MarkupLine("[bold red]✗ Verification failed with issues:[/]");
            foreach (string err in vres.Errors) AnsiConsole.MarkupLine($"  [red]✗ {Markup.Escape(err)}[/]");
        }

        if (vres.Conflicts != null && vres.Conflicts.Collisions.Count > 0)
        {
            Table confTable = new();
            confTable.Title("[bold gold1]File Collision Matrix[/]");
            confTable.AddColumn("Target File");
            confTable.AddColumn("Severity");
            confTable.AddColumn("Description");

            foreach (FileCollision c in vres.Conflicts.Collisions)
            {
                string color = c.Severity switch
                {
                    CollisionSeverity.Conflict => "red",
                    CollisionSeverity.Warning => "yellow",
                    _ => "grey"
                };
                confTable.AddRow(Markup.Escape(c.TargetFile), $"[{color}]{c.Severity}[/]",
                    Markup.Escape(c.Description));
            }

            AnsiConsole.Write(confTable);
        }
    }

    public static async Task HandleHarvestAsync(string outputDir, string? category, bool dryRun, string? catalogFile,
        bool force)
    {
        AnsiConsole.MarkupLine("[bold cyan]ProjectKongor Community Mod Harvester[/]");
        AnsiConsole.MarkupLine($"[grey]Output Directory:[/] [bold]{outputDir}[/]");
        if (!string.IsNullOrWhiteSpace(category))
            AnsiConsole.MarkupLine($"[grey]Category Filter:[/]  [bold]{category}[/]");
        if (dryRun) AnsiConsole.MarkupLine("[bold yellow]Mode: DRY RUN (no files will be downloaded)[/]");

        string? resolvedCatalogPath = catalogFile;
        if (string.IsNullOrWhiteSpace(resolvedCatalogPath))
        {
            resolvedCatalogPath = Kongor.Modding.Harvester.Scraper.TrelloBoardScraper.ResolveDefaultCatalogPath();
        }

        HarvestOptions options = new()
        {
            OutputDirectory = outputDir,
            CategoryFilter = category,
            DryRun = dryRun,
            OverwriteExisting = force,
            CustomCatalogPath = resolvedCatalogPath,
            GenerateManifests = true
        };

        TrelloModHarvester harvester = new();

        HarvestResult result = await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Scraping Trello community catalog and resolving download targets...", async ctx =>
            {
                return await harvester.HarvestAsync(options, msg =>
                {
                    // AnsiConsole.MarkupLine($"[grey]{Markup.Escape(msg)}[/]");
                });
            });

        Table table = new();
        table.Border(TableBorder.Rounded);
        table.Title(
            $"[bold cyan]Community Mods ({result.TotalCataloged} Cataloged, {result.DownloadedMods.Count} Available)[/]");
        table.AddColumn("[bold]Category[/]");
        table.AddColumn("[bold]Mod Name[/]");
        table.AddColumn("[bold]Author[/]");
        table.AddColumn("[bold]Host[/]");
        table.AddColumn("[bold]Status / Checksum[/]");

        foreach (CommunityModEntry mod in result.Catalog.OrderBy(m => m.Category).ThenBy(m => m.Name))
        {
            string hostStr = mod.ResolvedHost switch
            {
                DownloadHostType.Dropbox => "[blue]Dropbox[/]",
                DownloadHostType.GitHubRelease => "[green]GitHub[/]",
                DownloadHostType.MediaFire => "[gold1]MediaFire[/]",
                DownloadHostType.GoogleDrive => "[yellow]GDrive[/]",
                DownloadHostType.MailRu => "[magenta]Mail.ru[/]",
                DownloadHostType.TrelloAttachment => "[cyan]Trello[/]",
                DownloadHostType.DirectHttp => "[white]Direct[/]",
                _ => "[grey]None[/]"
            };

            DownloadedModInfo? dl = result.DownloadedMods.FirstOrDefault(d => d.ModId == mod.Id);
            string statusStr;
            if (dl != null)
            {
                string cacheTag = dl.WasCached ? " (cached)" : "";
                string shaShort = dl.Sha256Hash.Length >= 8 ? dl.Sha256Hash[..8] : dl.Sha256Hash;
                statusStr = $"[bold green]✓ Downloaded{cacheTag}[/] [grey]({shaShort}...)[/]";
            }
            else if (mod.IsDownloadable)
            {
                statusStr = dryRun ? "[yellow]Ready (Dry Run)[/]" : "[red]Failed / Pending[/]";
            }
            else
            {
                statusStr = "[grey]No binary available[/]";
            }

            table.AddRow(
                Markup.Escape(mod.Category),
                Markup.Escape(mod.Name),
                Markup.Escape(mod.Author),
                hostStr,
                statusStr
            );
        }

        AnsiConsole.Write(table);

        Panel summaryPanel = new(new Rows(
            new Markup($"[bold]Total Discovered:[/] {result.TotalDiscovered}"),
            new Markup($"[bold]Total Cataloged:[/]  {result.TotalCataloged}"),
            new Markup($"[bold green]Downloaded:[/]        {result.TotalDownloaded}"),
            new Markup($"[bold cyan]Cached:[/]            {result.TotalCached}"),
            new Markup($"[bold red]Failed:[/]            {result.TotalFailed}"),
            new Markup($"[bold grey]Skipped (No DL):[/]   {result.TotalSkipped}"),
            new Markup($"[bold]Time Elapsed:[/]      {result.ElapsedTime.TotalSeconds:F2}s"),
            new Markup($"[bold]Manifest JSON:[/]     {Markup.Escape(result.ManifestPathJson ?? "None")}"),
            new Markup($"[bold]Manifest MD:[/]       {Markup.Escape(result.ManifestPathMd ?? "None")}")
        ))
        {
            Header = new PanelHeader("[bold green]Harvest Summary[/]"),
            Border = BoxBorder.Double
        };

        AnsiConsole.Write(summaryPanel);
    }

    public static async Task HandlePackageAsync(string sourceDir, string? outputPath)
    {
        if (!Directory.Exists(sourceDir))
        {
            AnsiConsole.MarkupLine($"[red]Source directory not found:[/] [bold]{sourceDir}[/]");
            return;
        }

        string fullSource = Path.GetFullPath(sourceDir);
        string modXmlPath = Path.Combine(fullSource, "mod.xml");
        bool hasModXml = File.Exists(modXmlPath);

        string targetArchive = outputPath ?? Path.Combine(
            Path.GetDirectoryName(fullSource) ?? ".",
            $"{Path.GetFileName(fullSource)}{(hasModXml ? ".honmod" : ".s2z")}"
        );

        ModPackFormat format = targetArchive.EndsWith(".honmod", StringComparison.OrdinalIgnoreCase)
            ? ModPackFormat.HonMod
            : ModPackFormat.S2z;

        AnsiConsole.MarkupLine($"[bold cyan]📦 Packaging directory:[/] [white]{Markup.Escape(fullSource)}[/] ➔ [yellow]{Markup.Escape(targetArchive)}[/] ({format})");

        var result = await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Compressing loose files into clean archive...", async _ =>
            {
                return await ModPacker.PackDirectoryAsync(fullSource, targetArchive, format);
            });

        if (result.Success)
        {
            AnsiConsole.MarkupLine($"[bold green]✓ Successfully packaged {result.TotalFiles} files:[/] [cyan]{Markup.Escape(result.OutputPath)}[/]");
            AnsiConsole.MarkupLine($"  • Uncompressed: [white]{result.UncompressedBytes / 1024.0:F1} KB[/] | Compressed: [white]{result.CompressedBytes / 1024.0:F1} KB[/]");
            if (!string.IsNullOrEmpty(result.Sha256))
            {
                AnsiConsole.MarkupLine($"  • SHA-256: [grey]{result.Sha256}[/]");
            }
        }
        else
        {
            AnsiConsole.MarkupLine($"[bold red]✗ Packaging failed:[/] {Markup.Escape(result.ErrorMessage ?? "Unknown error")}");
        }
    }

    private static List<ModPackage> FindMatchingMods(IEnumerable<ModPackage> mods, string query)
    {
        string q = query.Trim();
        List<ModPackage> list =
        [
            .. mods.Where(m =>
                m.Manifest.Name.Equals(q, StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(m.FilePath).Equals(q, StringComparison.OrdinalIgnoreCase) ||
                m.Manifest.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileNameWithoutExtension(m.FilePath).Contains(q, StringComparison.OrdinalIgnoreCase)
            )
        ];

        return list;
    }

    public static string ResolveDefaultModsDir()
    {
        if (Kongor.Core.Configuration.KongorWorkspaceResolver.TryResolve(out var ws) && ws is not null)
        {
            string wsClientMods = Path.Combine(ws.Root, "games", "client", "mods");
            if (Directory.Exists(wsClientMods)) return wsClientMods;
            string wsMods = Path.Combine(ws.Root, "mods");
            if (Directory.Exists(wsMods)) return wsMods;
        }

        string cur = Directory.GetCurrentDirectory();
        string clientMods = Path.Combine(cur, "games", "client", "mods");
        if (Directory.Exists(clientMods)) return clientMods;

        string localMods = Path.Combine(cur, "mods");
        if (Directory.Exists(localMods)) return localMods;

        DirectoryInfo? dir = new(cur);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "games", "client", "mods");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        return cur;
    }

    public static string ResolveDefaultClientDir()
    {
        if (Kongor.Core.Configuration.KongorWorkspaceResolver.TryResolve(out var ws) && ws is not null)
        {
            string wsClient = Path.Combine(ws.Root, "games", "client");
            if (Directory.Exists(wsClient)) return wsClient;
        }

        string cur = Directory.GetCurrentDirectory();
        string clientDir = Path.Combine(cur, "games", "client");
        if (Directory.Exists(clientDir)) return clientDir;

        DirectoryInfo? dir = new(cur);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "games", "client");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        return cur;
    }

    private static string Truncate(string str, int max)
    {
        if (string.IsNullOrEmpty(str)) return string.Empty;
        string s = str.Replace('\r', ' ').Replace('\n', ' ');
        return s.Length <= max ? s : s[..max] + "...";
    }
}
