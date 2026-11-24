// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.ComponentModel;
using Kongor.Cli.Infrastructure;
using Kongor.Core.Configuration;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Kongor.Cli.Commands;

internal sealed class CompletionCommandSettings : CommandSettings
{
    [CommandArgument(0, "[shell]")]
    [Description("Target shell: powershell (default on Windows), bash, zsh, or fish")]
    public string? Shell { get; init; }

    [CommandOption("--write")]
    [Description("Write the static completion script to ~/.kongor/completions/ instead of printing to stdout")]
    public bool Write { get; init; }

    [CommandOption("--install-profile")]
    [Description("Write the completion script and idempotently wire it into your shell profile")]
    public bool InstallProfile { get; init; }

    [CommandOption("--no-profile")]
    [Description("Skip modifying any shell profile file (overrides --install-profile)")]
    public bool NoProfile { get; init; }
}

/// <summary>
/// <c>kongor completion powershell|bash|zsh|fish</c>: emits or installs an instant (&lt;2ms) static
/// tab-completion script with zero <c>kongor.exe</c> process spawn on &lt;Tab&gt;.
/// </summary>
internal sealed class CompletionCommand : Command<CompletionCommandSettings>
{
    public override int Execute(CommandContext context, CompletionCommandSettings settings)
    {
        string rawShell = string.IsNullOrWhiteSpace(settings.Shell)
            ? (OperatingSystem.IsWindows() ? "powershell" : "bash")
            : settings.Shell;

        if (!ShellCompletionGenerator.TryParseShell(rawShell, out CompletionShell shell))
        {
            AnsiConsole.MarkupLine(
                $"[bold red]✗ Unsupported shell '{Markup.Escape(rawShell)}'.[/] " +
                "Supported shells: [yellow]powershell[/], [yellow]bash[/], [yellow]zsh[/], [yellow]fish[/].");
            return 1;
        }

        if (settings.Write || settings.InstallProfile)
        {
            string scriptPath = ShellCompletionGenerator.WriteCompletionScript(shell, Program.AllPrimaryCommands);
            AnsiConsole.MarkupLine($"[bold green]✓ Wrote static {shell} completion script:[/] [white]{Markup.Escape(scriptPath)}[/]");

            if (settings.InstallProfile && !settings.NoProfile)
            {
                string? profilePath = ResolveDefaultProfilePath(shell);
                if (!string.IsNullOrWhiteSpace(profilePath))
                {
                    string snippet = ShellCompletionGenerator.BuildProfileSnippet(shell, scriptPath);
                    bool changed = ShellCompletionGenerator.EnsureProfileBlock(profilePath, snippet);
                    string state = changed ? "Updated" : "Already wired";
                    AnsiConsole.MarkupLine($"[bold green]✓ {state} shell profile:[/] [white]{Markup.Escape(profilePath)}[/]");
                }
            }
            else
            {
                string snippet = ShellCompletionGenerator.BuildProfileSnippet(shell, scriptPath);
                AnsiConsole.MarkupLine($"[grey]  Load in your current shell:[/] [yellow]{Markup.Escape(snippet)}[/]");
            }

            return 0;
        }

        // Pure stdout emit (e.g. 'kongor completion powershell | Out-String | Invoke-Expression')
        KongorUserConfig config = KongorUserConfigStore.Load();
        string script = ShellCompletionGenerator.GenerateScript(
            shell,
            Program.AllPrimaryCommands,
            config.Workspaces.Select(w => w.Name));
        Console.Write(script);
        return 0;
    }

    private static string? ResolveDefaultProfilePath(CompletionShell shell)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return shell switch
        {
            CompletionShell.PowerShell => OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "WindowsPowerShell", "Microsoft.PowerShell_profile.ps1")
                : Path.Combine(home, ".config", "powershell", "Microsoft.PowerShell_profile.ps1"),
            CompletionShell.Bash => Path.Combine(home, ".bashrc"),
            CompletionShell.Zsh => Path.Combine(home, ".zshrc"),
            CompletionShell.Fish => Path.Combine(home, ".config", "fish", "conf.d", "kongor_completions.fish"),
            _ => null
        };
    }
}
