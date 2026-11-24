// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.ComponentModel;
using System.Text.Json;
using Kongor.Cli.Infrastructure;
using Kongor.Core.Configuration;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Kongor.Cli.Commands;

internal sealed class WhereCommandSettings : CommandSettings
{
    [CommandOption("--json")]
    [Description("Output machine-readable JSON for scripts and AI agents")]
    public bool Json { get; init; }
}

/// <summary>
/// <c>kongor where</c> / <c>kongor workspace current</c>: displays the resolved Kongor root,
/// layout kind, resolution tier, invocation CWD, and user config path.
/// </summary>
internal sealed class WhereCommand : Command<WhereCommandSettings>
{
    public override int Execute(CommandContext context, WhereCommandSettings settings)
    {
        bool resolved = CliRuntimeContext.TryResolveWorkspace(out KongorWorkspaceResult? ws);
        string configPath = KongorUserConfigStore.ResolveConfigPath();
        string? envWarning = KongorWorkspaceResolver.GetPermanentRootEnvWarning();

        if (settings.Json)
        {
            var payload = new
            {
                resolved,
                root = ws?.Root,
                kind = ws?.Kind.ToString(),
                source = ws?.Source.ToString(),
                alias = ws?.RegisteredAlias,
                invocationCwd = CliRuntimeContext.InvocationCwd,
                outOfTree = ws?.IsOutOfTree,
                configPath,
                permanentEnvWarning = envWarning
            };
            Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            return resolved ? 0 : 1;
        }

        if (!resolved || ws is null)
        {
            AnsiConsole.MarkupLine("[bold red]✗ No Project Kongor workspace or bundle could be resolved.[/]");
            AnsiConsole.MarkupLine($"[grey]Invocation CWD:[/] {Markup.Escape(CliRuntimeContext.InvocationCwd)}");
            AnsiConsole.MarkupLine($"[grey]Config File:[/]    {Markup.Escape(configPath)}");
            AnsiConsole.MarkupLine("\n[yellow]Fix:[/] Run [bold]kongor workspace use \"<path-to-ProjectKongor>\"[/]");
            return 1;
        }

        Table table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold cyan]Resolved Project Kongor Workspace[/]")
            .AddColumn("[bold white]Property[/]")
            .AddColumn("[bold white]Value[/]");

        table.AddRow("Workspace Root", $"[bold green]{Markup.Escape(ws.Root)}[/]");
        table.AddRow("Layout Kind", ws.IsSourceWorkspace ? "[cyan]SourceWorkspace (Developer Repo)[/]" : "[yellow]BundledInstall (Standalone Bundle)[/]");
        table.AddRow("Resolved Via", $"[white]{FormatResolutionSource(ws.Source)}[/]");
        table.AddRow("Registered Alias", string.IsNullOrWhiteSpace(ws.RegisteredAlias) ? "[grey](unregistered)[/]" : $"[green]{Markup.Escape(ws.RegisteredAlias)}[/]");
        table.AddRow("Invocation CWD", $"[grey]{Markup.Escape(ws.InvocationCwd)}[/]");
        table.AddRow("Out-of-Tree Mode", ws.IsOutOfTree ? "[yellow]Yes (running from outside workspace)[/]" : "[green]No (inside workspace)[/]");
        table.AddRow("User Config", $"[grey]{Markup.Escape(configPath)}[/]");

        AnsiConsole.Write(table);

        if (!string.IsNullOrWhiteSpace(envWarning))
        {
            AnsiConsole.MarkupLine($"\n[bold yellow]⚠ {Markup.Escape(envWarning)}[/]");
        }

        return 0;
    }

    private static string FormatResolutionSource(WorkspaceResolutionSource source) => source switch
    {
        WorkspaceResolutionSource.CliFlag => "CLI Flag (--root / --workspace)",
        WorkspaceResolutionSource.EnvKongorRoot => "Environment Variable (KONGOR_ROOT)",
        WorkspaceResolutionSource.EnvBundleRoot => "Environment Variable (KONGOR_BUNDLE_ROOT)",
        WorkspaceResolutionSource.EnvRepoRoot => "Environment Variable (KONGOR_REPO_ROOT)",
        WorkspaceResolutionSource.CwdAncestor => "Current Working Directory Ancestor",
        WorkspaceResolutionSource.ExeAncestor => "Executable Base Directory Ancestor",
        WorkspaceResolutionSource.UserConfigActive => "Saved User Config (~/.kongor/config.json active)",
        WorkspaceResolutionSource.UserConfigFallback => "Saved User Config (~/.kongor/config.json fallback)",
        _ => source.ToString()
    };
}

public sealed class WorkspaceListSettings : CommandSettings
{
    [CommandOption("--json")]
    [Description("Output machine-readable JSON")]
    public bool Json { get; init; }
}

public sealed class WorkspaceListCommand : Command<WorkspaceListSettings>
{
    public override int Execute(CommandContext context, WorkspaceListSettings settings)
    {
        KongorUserConfig config = KongorUserConfigStore.Load();
        string configPath = KongorUserConfigStore.ResolveConfigPath();

        if (settings.Json)
        {
            var payload = new
            {
                configPath,
                activeWorkspace = config.ActiveWorkspace,
                activeCliSlot = config.ActiveCliSlot,
                workspaces = config.Workspaces.Select(w => new
                {
                    w.Name,
                    w.Path,
                    kind = w.Kind.ToString(),
                    active = string.Equals(w.Name, config.ActiveWorkspace, StringComparison.OrdinalIgnoreCase),
                    valid = KongorWorkspaceResolver.TryIdentifyRoot(w.Path, out _, out _),
                    w.LastVerifiedUtc
                })
            };
            Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        if (config.Workspaces.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]No workspaces registered yet in '{Markup.Escape(configPath)}'.[/]");
            AnsiConsole.MarkupLine("Run [bold green]kongor workspace use \"<path-to-ProjectKongor>\"[/] to register one.");
            return 0;
        }

        Table table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold cyan]Registered Project Kongor Workspaces[/]")
            .AddColumn("[bold]Active[/]")
            .AddColumn("[bold]Alias[/]")
            .AddColumn("[bold]Kind[/]")
            .AddColumn("[bold]Status[/]")
            .AddColumn("[bold]Path[/]");

        foreach (KongorWorkspaceEntry entry in config.Workspaces)
        {
            bool isActive = string.Equals(entry.Name, config.ActiveWorkspace, StringComparison.OrdinalIgnoreCase);
            bool valid = KongorWorkspaceResolver.TryIdentifyRoot(entry.Path, out _, out _);
            table.AddRow(
                isActive ? "[bold green]★[/]" : "[grey] [/]",
                isActive ? $"[bold green]{Markup.Escape(entry.Name)}[/]" : $"[white]{Markup.Escape(entry.Name)}[/]",
                entry.Kind == KongorWorkspaceKind.SourceWorkspace ? "[cyan]Source[/]" : "[yellow]Bundle[/]",
                valid ? "[green]Valid[/]" : "[red]Missing[/]",
                $"[grey]{Markup.Escape(entry.Path)}[/]");
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[grey]Config: {Markup.Escape(configPath)}[/]");
        return 0;
    }
}

public sealed class WorkspacePathSettings : CommandSettings
{
    [CommandArgument(0, "<path-or-name>")]
    [Description("Directory path to a Project Kongor checkout/bundle, or an existing registered alias")]
    public string Target { get; init; } = string.Empty;

    [CommandOption("-n|--name <ALIAS>")]
    [Description("Optional short alias for this workspace")]
    public string? Name { get; init; }
}

public sealed class WorkspaceUseCommand : Command<WorkspacePathSettings>
{
    public override int Execute(CommandContext context, WorkspacePathSettings settings)
    {
        return RegisterOrActivate(settings.Target, settings.Name, setActive: true);
    }

    internal static int RegisterOrActivate(string target, string? alias, bool setActive)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            AnsiConsole.MarkupLine("[bold red]✗ Workspace path or alias is required.[/]");
            return 1;
        }

        KongorUserConfig current = KongorUserConfigStore.Load();
        KongorWorkspaceEntry? byAlias = current.Workspaces.FirstOrDefault(w =>
            string.Equals(w.Name, target.Trim(), StringComparison.OrdinalIgnoreCase));

        string candidatePath = byAlias?.Path ?? target.Trim();
        if (!Path.IsPathRooted(candidatePath))
        {
            candidatePath = Path.Combine(CliRuntimeContext.InvocationCwd, candidatePath);
        }

        if (!KongorWorkspaceResolver.TryIdentifyRoot(candidatePath, out KongorWorkspaceKind kind, out _))
        {
            AnsiConsole.MarkupLine(
                $"[bold red]✗ '{Markup.Escape(candidatePath)}' is not a valid Project Kongor checkout or bundle.[/]\n" +
                "[grey]Expected either 'ProjectKongor.slnx' (developer repo) or 'playtest_policy.json' + 'games/client/' (standalone bundle).[/]");
            return 1;
        }

        KongorUserConfig updated = KongorUserConfigStore.RegisterOrUpdateWorkspace(
            candidatePath,
            kind,
            alias: alias ?? byAlias?.Name,
            setActive: setActive);

        CliRuntimeContext.InvalidateWorkspaceCache();
        ShellCompletionGenerator.TryRegenerateAll(Program.AllPrimaryCommands);
        string resolvedAlias = updated.ActiveWorkspace ?? alias ?? Path.GetFileName(candidatePath);
        string verb = setActive ? "Active workspace set to" : "Registered workspace";
        AnsiConsole.MarkupLine(
            $"[bold green]✓ {verb}:[/] [cyan]{Markup.Escape(resolvedAlias)}[/] -> [white]{Markup.Escape(Path.GetFullPath(candidatePath))}[/] ([grey]{kind}[/])");
        return 0;
    }
}

public sealed class WorkspaceAddCommand : Command<WorkspacePathSettings>
{
    public override int Execute(CommandContext context, WorkspacePathSettings settings)
    {
        return WorkspaceUseCommand.RegisterOrActivate(settings.Target, settings.Name, setActive: false);
    }
}

public sealed class WorkspaceRemoveSettings : CommandSettings
{
    [CommandArgument(0, "<name-or-path>")]
    [Description("Alias or directory path of the workspace to unregister")]
    public string Target { get; init; } = string.Empty;
}

public sealed class WorkspaceRemoveCommand : Command<WorkspaceRemoveSettings>
{
    public override int Execute(CommandContext context, WorkspaceRemoveSettings settings)
    {
        var (_, removed) = KongorUserConfigStore.RemoveWorkspace(settings.Target);
        CliRuntimeContext.InvalidateWorkspaceCache();
        if (removed)
        {
            ShellCompletionGenerator.TryRegenerateAll(Program.AllPrimaryCommands);
        }

        if (!removed)
        {
            AnsiConsole.MarkupLine($"[yellow]No registered workspace matched '{Markup.Escape(settings.Target)}'.[/]");
            return 1;
        }

        AnsiConsole.MarkupLine($"[bold green]✓ Removed workspace '{Markup.Escape(settings.Target)}' from ~/.kongor/config.json.[/]");
        return 0;
    }
}

public sealed class WorkspacePruneCommand : Command<EmptyCommandSettings>
{
    public override int Execute(CommandContext context, EmptyCommandSettings settings)
    {
        var (_, pruned) = KongorUserConfigStore.PruneInvalidWorkspaces();
        CliRuntimeContext.InvalidateWorkspaceCache();
        if (pruned.Count > 0)
        {
            ShellCompletionGenerator.TryRegenerateAll(Program.AllPrimaryCommands);
        }

        if (pruned.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]✓ All registered workspaces are valid; nothing pruned.[/]");
            return 0;
        }

        foreach (KongorWorkspaceEntry entry in pruned)
        {
            AnsiConsole.MarkupLine($"[yellow]  Pruned missing workspace '{Markup.Escape(entry.Name)}' ({Markup.Escape(entry.Path)})[/]");
        }
        AnsiConsole.MarkupLine($"[bold green]✓ Pruned {pruned.Count} stale workspace entry(ies).[/]");
        return 0;
    }
}
