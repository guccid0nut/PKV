// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.ComponentModel;
using Spectre.Console.Cli;
using static Kongor.Cli.Program;

namespace Kongor.Cli.Commands;

public sealed class MasterServerCommand : Command<EmptyCommandSettings>
{
    public override int Execute(CommandContext context, EmptyCommandSettings settings)
    {
        return ForwardToProject("src/services/master-server/KONGOR.MasterServer.csproj", []);
    }
}

public sealed class ChatServerCommand : Command<EmptyCommandSettings>
{
    public override int Execute(CommandContext context, EmptyCommandSettings settings)
    {
        return ForwardToProject("src/services/chat-server/TRANSMUTANSTEIN.ChatServer.csproj", []);
    }
}

public sealed class WebApiCommand : Command<EmptyCommandSettings>
{
    public override int Execute(CommandContext context, EmptyCommandSettings settings)
    {
        return ForwardToProject("src/services/web-api/ZORGATH.WebPortal.API.csproj", []);
    }
}

public sealed class WebUiCommand : Command<EmptyCommandSettings>
{
    public override int Execute(CommandContext context, EmptyCommandSettings settings)
    {
        return ForwardToProject("src/services/web-ui/DAWNBRINGER.WebPortal.UI.csproj", []);
    }
}

public sealed class StopCommandSettings : CommandSettings
{
    [CommandArgument(0, "[target]")]
    [Description("Target process group to stop ('dotnet' (default), 'docker', 'comfy', 'blender', 'session', 'all')")]
    [DefaultValue("dotnet")]
    public string Target { get; init; } = "dotnet";

    [CommandOption("--global")]
    [Description("Stop Kongor processes across ALL registered workspaces/bundles on this machine (still spares unrelated dotnet processes)")]
    public bool Global { get; init; }

    [CommandOption("--include-mcp")]
    [Description("Also terminate active Kongor MCP stdio servers (spared by default)")]
    public bool IncludeMcp { get; init; }

    [CommandOption("-s|--session")]
    [Description("Stop only processes recorded in userdata/session_pids.json")]
    public bool Session { get; init; }
}

public sealed class StopCommand : AsyncCommand<StopCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, StopCommandSettings settings)
    {
        string target = settings.Target.ToLowerInvariant().Trim();
        if (settings.Session || target is "session")
        {
            return await StopDotNetProcessesAsync(
                globalScope: settings.Global,
                includeMcp: settings.IncludeMcp,
                sessionOnly: true);
        }

        if (target is "docker" or "stack" or "docker-stack")
        {
            return await StopDockerStackAsync();
        }

        if (target is "comfy" or "comfyui")
        {
            return StopComfyUI();
        }

        if (target is "blender")
        {
            return StopBlenderProcesses();
        }

        if (target is "all")
        {
            int c1 = StopComfyUI();
            int c2 = StopBlenderProcesses();
            int c3 = await StopDotNetProcessesAsync(globalScope: settings.Global, includeMcp: settings.IncludeMcp);
            int c4 = 0;
            string? repoRoot = Infrastructure.CliRuntimeContext.TryResolveWorkspace(out var ws) ? ws?.Root : null;
            var snapshot = await Kongor.Cli.Mcp.Services.DockerStackProbe.ProbeAsync(repoRoot: repoRoot, includeFleetHttpStatus: false);
            if (snapshot.IsStackActive)
            {
                c4 = await StopDockerStackAsync();
            }
            return c1 == 0 && c2 == 0 && c3 == 0 && c4 == 0 ? 0 : 1;
        }

        return await StopDotNetProcessesAsync(globalScope: settings.Global, includeMcp: settings.IncludeMcp);
    }
}
