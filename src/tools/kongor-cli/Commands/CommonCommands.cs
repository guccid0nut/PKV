// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using Spectre.Console;
using Spectre.Console.Cli;
using static Kongor.Cli.Program;

namespace Kongor.Cli.Commands;

public class RawArgsSettings : CommandSettings
{
    [CommandArgument(0, "[args]")]
    public string[]? Args { get; init; }
}

public static class CommandArgsHelper
{
    public static string[] GetEffectiveArgs(CommandContext? context, RawArgsSettings settings)
    {
        if (context?.Remaining == null || context.Remaining.Raw.Count == 0)
        {
            return settings.Args ?? [];
        }

        if (settings.Args == null || settings.Args.Length == 0)
        {
            return [.. context.Remaining.Raw];
        }

        List<string> combined = [.. settings.Args];
        foreach (string r in context.Remaining.Raw)
        {
            if (!combined.Contains(r))
            {
                combined.Add(r);
            }
        }
        return [.. combined];
    }
}

public sealed class PlayCommand : Command<RawArgsSettings>
{
    public override int Execute(CommandContext context, RawArgsSettings settings)
    {
        string[] raw = CommandArgsHelper.GetEffectiveArgs(context, settings);
        return LaunchGameClient(raw);
    }
}

public sealed class HostCommand : Command<RawArgsSettings>
{
    public override int Execute(CommandContext context, RawArgsSettings settings)
    {
        string[] raw = CommandArgsHelper.GetEffectiveArgs(context, settings);
        return LaunchGameServer(raw);
    }
}

public sealed class GuiCommand : Command<EmptyCommandSettings>
{
    public override int Execute(CommandContext context, EmptyCommandSettings settings)
    {
        var ws = Infrastructure.CliRuntimeContext.RequireAnyWorkspace();
        if (ws.IsBundledInstall && ws.BundledLayout is { } layout)
        {
            AnsiConsole.MarkupLine($"[bold green][[BundledLayout]] Launching bundled NEXUS Control Panel: {Markup.Escape(layout.ControlPanelExe)}[/]");
            return RunProcess(layout.ControlPanelExe, "", Path.GetDirectoryName(layout.ControlPanelExe) ?? layout.Root);
        }

        AnsiConsole.MarkupLine("[bold green]🖥️ Launching NEXUS Control Panel Desktop GUI...[/]");
        return ForwardToProject("src/apps/control-panel/NEXUS.ControlPanel.GUI.csproj", []);
    }
}

public sealed class AspireCommand : Command<EmptyCommandSettings>
{
    public override int Execute(CommandContext context, EmptyCommandSettings settings)
    {
        var ws = Infrastructure.CliRuntimeContext.RequireAnyWorkspace();
        if (ws.IsBundledInstall && ws.BundledLayout is { } layout)
        {
            AnsiConsole.MarkupLine($"[bold green][[BundledLayout]] Launching NEXUS Control Panel with auto-start standalone cluster: {Markup.Escape(layout.ControlPanelExe)}[/]");
            Environment.SetEnvironmentVariable("KONGOR_AUTO_START_CLUSTER", "1");
            return RunProcess(layout.ControlPanelExe, "", Path.GetDirectoryName(layout.ControlPanelExe) ?? layout.Root);
        }

        AnsiConsole.MarkupLine("[bold green]🚀 Launching .NET Aspire Microservice Cluster...[/]");
        AnsiConsole.MarkupLine("[grey]Dashboard URL:[/] [link]https://localhost:5550[/]");
        return ForwardToProject("src/services/aspire-host/ASPIRE.ApplicationHost.csproj", []);
    }
}

public sealed class CaptureCommandSettings : CommandSettings
{
    [CommandOption("-t|--target <TARGET>")]
    public string Target { get; init; } = "hon_x64";

    [CommandOption("-o|--output <OUTPUT>")]
    public string? Output { get; init; }

    [CommandOption("-m|--method <METHOD>")]
    public string Method { get; init; } = "auto";
}

public sealed class CaptureCommand : AsyncCommand<CaptureCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, CaptureCommandSettings settings)
    {
        Mcp.Services.IVulkanFrameGrabber? grabber = OperatingSystem.IsWindows() ? new Mcp.Services.VulkanMemoryMappedFrameGrabber() : null;
        Mcp.Services.WindowCapturer capturer = new(vulkanGrabber: grabber);
        string target = string.IsNullOrWhiteSpace(settings.Target) ? "hon_x64" : settings.Target;
        string wsRoot = Infrastructure.CliRuntimeContext.TryResolveWorkspace(out var ws) && ws is not null
            ? ws.Root
            : Infrastructure.CliRuntimeContext.InvocationCwd;
        string outputPath = Infrastructure.CliPathResolver.ResolveOutputPath(
            settings.Output,
            wsRoot,
            "capture.png",
            isExplicitExportCommand: true);

        Mcp.Services.WindowCaptureResult result = await capturer.CaptureAsync(
            target: target,
            captureMethod: settings.Method,
            saveArtifact: true,
            customOutputPath: outputPath);

        if (result.Success)
        {
            AnsiConsole.MarkupLine($"[bold green]✓ Window captured successfully to:[/] [white]{Markup.Escape(result.ArtifactPath ?? outputPath)}[/]");
            return 0;
        }

        AnsiConsole.MarkupLine($"[bold red]✗ Failed to capture window:[/] [white]{Markup.Escape(result.ErrorMessage ?? "Unknown error")}[/]");
        return 1;
    }
}
