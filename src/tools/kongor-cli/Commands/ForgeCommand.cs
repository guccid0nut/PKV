// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using static Kongor.Cli.Program;

namespace Kongor.Cli.Commands;

/// <summary>
/// kongor forge: Dispatches multi-modal asset creation requests to domain-specific
/// Python generation pipelines (hero, skin, taunt, announcer, icon, symbol) with
/// transparent argument forwarding, transactional staging, and pre-commit gateway evaluation.
/// </summary>
public sealed class ForgeCommand : Command<RawArgsSettings>
{
    public override int Execute(CommandContext context, RawArgsSettings settings)
    {
        string[] raw = CommandArgsHelper.GetEffectiveArgs(context, settings);

        if (raw.Length == 0 || raw[0].Equals("--help", StringComparison.OrdinalIgnoreCase) || raw[0].Equals("-h", StringComparison.OrdinalIgnoreCase))
        {
            PrintUsage();
            return 0;
        }

        string pipeline = raw[0].ToLowerInvariant();
        string[] pipelineArgs = raw.Skip(1).ToArray();

        AnsiConsole.Write(new Rule($"[bold cyan]ProjectKongor Multi-Modal Content Forge ({pipeline.ToUpperInvariant()})[/]").LeftJustified());

        return pipeline switch
        {
            "hero" => RunPythonScript("scripts/forge/hero_forge.py", pipelineArgs),
            "skin" or "avatar" => RunPythonScript("scripts/forge/skin_forge.py", pipelineArgs),
            "taunt" or "smack" => RunPythonScript("scripts/forge/taunt_forge.py", pipelineArgs),
            "announcer" => RunPythonScript("scripts/forge/announcer_forge.py", pipelineArgs),
            "icon" => RunPythonScript("scripts/forge/icon_forge.py", pipelineArgs),
            "symbol" or "emblem" => RunPythonScript("scripts/forge/icon_forge.py", [.. pipelineArgs, "--type", "symbol"]),
            "texture" or "textures" or "modernize-textures" => ExecuteModernizeTextures(pipelineArgs),
            _ => HandleUnknownPipeline(pipeline)
        };
    }

    private static int ExecuteModernizeTextures(string[] args)
    {
        string heroName = "bubbles";
        string avatarName = "alt";

        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i].Equals("--hero", StringComparison.OrdinalIgnoreCase) || args[i].Equals("-h", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
            {
                heroName = args[++i];
            }
            else if ((args[i].Equals("--alt", StringComparison.OrdinalIgnoreCase) || args[i].Equals("-a", StringComparison.OrdinalIgnoreCase) || args[i].Equals("--avatar", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
            {
                avatarName = args[++i];
            }
        }

        string repoRoot = Program.FindRepoRoot();
        Kongor.Modding.Services.TextureModernizationService service = new();
        Kongor.Modding.Services.HeroTextureModernizationReport report = service.ModernizeHeroAvatar(repoRoot, heroName, avatarName);

        if (!report.Success)
        {
            AnsiConsole.MarkupLine($"[bold red]Error:[/] Texture modernization failed: {Markup.Escape(report.ErrorMessage ?? "Unknown error")}");
            return 1;
        }

        Table table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Green);
        table.Title($"[bold white]Canary Texture Modernization — {heroName} ({avatarName})[/]");
        table.AddColumn(new TableColumn("[bold]Texture File[/]"));
        table.AddColumn(new TableColumn("[bold]Format[/]"));
        table.AddColumn(new TableColumn("[bold]Size[/]"));
        table.AddColumn(new TableColumn("[bold]SHA-256 (64-bit truncated)[/]"));

        foreach (Kongor.Modding.Services.TextureFileReport tex in report.ProcessedTextures)
        {
            table.AddRow(
                $"[bold white]{tex.FileName}[/]",
                $"[cyan]{tex.Format}[/]",
                $"{tex.SizeBytes / 1024.0:F1} KB",
                $"[grey]{tex.Sha256[..12]}...[/]");
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[bold green]✓ Successfully modernized {report.ProcessedTextures.Count} textures into:[/] [yellow]{Markup.Escape(report.OutputDirectory)}[/]");
        return 0;
    }

    private static int HandleUnknownPipeline(string pipeline)
    {
        AnsiConsole.MarkupLine($"[bold red]Error:[/] Unknown content forge pipeline '[white]{Markup.Escape(pipeline)}[/]'.");
        PrintUsage();
        return 1;
    }

    private static void PrintUsage()
    {
        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Aqua);
        table.AddColumn(new TableColumn("[bold]Pipeline[/]").Width(15));
        table.AddColumn(new TableColumn("[bold]Target Output & Gateway Enforcement[/]"));
        table.AddColumn(new TableColumn("[bold]Example Invocation[/]"));

        table.AddRow("hero", "Complete 5-slot Hero (MDF, Clips, 20-line Voice, Textures)", "kongor forge hero --name Valkyrie --offline");
        table.AddRow("skin", "Alt Avatar (MDF inspection, RxGB normal maps, Store 2.0 Cat 2)", "kongor forge skin --hero Pyromancer --skin-id inferno_drake --dry-run");
        table.AddRow("taunt", "Custom Taunt ($Z=0$ Prop mesh, .effect XML, 48kHz Mono, Cat 27/77)", "kongor forge taunt --taunt-id anvil_drop --dry-run");
        table.AddRow("announcer", "Announcer Pack (28-cue matrix, 48kHz Stereo EBU R128, Cat 5)", "kongor forge announcer --announcer-id valkyrie --dry-run");
        table.AddRow("icon", "Account Icon (POT DXT5 DDS + Web PNG, 4 Mips, Cat 3)", "kongor forge icon --icon-id celestial_shield --dry-run");
        table.AddRow("symbol", "Account Symbol (POT DXT5 DDS + Web PNG, 4 Mips, Cat 4)", "kongor forge symbol --icon-id arcane_glyph --dry-run");

        AnsiConsole.Write(table);
    }
}
