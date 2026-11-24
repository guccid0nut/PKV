// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.ComponentModel;
using Spectre.Console.Cli;
using static Kongor.Cli.Program;

namespace Kongor.Cli.Commands;

public sealed class ModNameSettings : CommandSettings
{
    [CommandArgument(0, "<mod>")]
    [Description("Name or pattern of the mod")]
    public string ModName { get; init; } = string.Empty;
}

public sealed class ModListCommand : Command<EmptyCommandSettings>
{
    public override int Execute(CommandContext context, EmptyCommandSettings settings)
    {
        return ForwardToProject("src/tools/mod-cli/ModCli.csproj", ["list"]);
    }
}

public sealed class ModEnableCommand : Command<ModNameSettings>
{
    public override int Execute(CommandContext context, ModNameSettings settings)
    {
        return ForwardToProject("src/tools/mod-cli/ModCli.csproj", ["enable", settings.ModName]);
    }
}

public sealed class ModDisableCommand : Command<ModNameSettings>
{
    public override int Execute(CommandContext context, ModNameSettings settings)
    {
        return ForwardToProject("src/tools/mod-cli/ModCli.csproj", ["disable", settings.ModName]);
    }
}

public sealed class ModInspectCommand : Command<ModNameSettings>
{
    public override int Execute(CommandContext context, ModNameSettings settings)
    {
        return ForwardToProject("src/tools/mod-cli/ModCli.csproj", ["inspect", settings.ModName]);
    }
}

public sealed class ModApplyCommand : Command<EmptyCommandSettings>
{
    public override int Execute(CommandContext context, EmptyCommandSettings settings)
    {
        return ForwardToProject("src/tools/mod-cli/ModCli.csproj", ["apply"]);
    }
}

public sealed class ModRollbackCommand : Command<EmptyCommandSettings>
{
    public override int Execute(CommandContext context, EmptyCommandSettings settings)
    {
        return ForwardToProject("src/tools/mod-cli/ModCli.csproj", ["rollback"]);
    }
}

public sealed class ModVerifyCommand : Command<EmptyCommandSettings>
{
    public override int Execute(CommandContext context, EmptyCommandSettings settings)
    {
        return ForwardToProject("src/tools/mod-cli/ModCli.csproj", ["verify"]);
    }
}

public sealed class ModHarvestCommand : Command<EmptyCommandSettings>
{
    public override int Execute(CommandContext context, EmptyCommandSettings settings)
    {
        return ForwardToProject("src/tools/mod-cli/ModCli.csproj", ["harvest"]);
    }
}

public sealed class ModRepairCommand : Command<EmptyCommandSettings>
{
    public override int Execute(CommandContext context, EmptyCommandSettings settings)
    {
        return ForwardToProject("src/tools/mod-cli/ModCli.csproj", ["repair"]);
    }
}

public sealed class ModPackSettings : CommandSettings
{
    [CommandArgument(0, "[dir]")]
    [Description("Source directory containing loose files or mod files")]
    public string? Dir { get; init; }

    [CommandOption("-o|--output <PATH>")]
    [Description("Destination archive path (.s2z or .honmod)")]
    public string? Output { get; init; }
}

public sealed class ModPackCommand : Command<ModPackSettings>
{
    public override int Execute(CommandContext context, ModPackSettings settings)
    {
        string wsRoot = FindRepoRoot();
        List<string> args = ["package"];
        if (!string.IsNullOrWhiteSpace(settings.Dir))
        {
            args.Add(Infrastructure.CliPathResolver.ResolveInputPath(settings.Dir, wsRoot));
        }
        if (!string.IsNullOrWhiteSpace(settings.Output))
        {
            args.Add("--output");
            args.Add(Infrastructure.CliPathResolver.ResolveOutputPath(
                settings.Output,
                wsRoot,
                Path.GetFileName(settings.Output),
                isExplicitExportCommand: true));
        }
        return ForwardToProject("src/tools/mod-cli/ModCli.csproj", [.. args]);
    }
}

public sealed class PrebakeAllCommand : Command<EmptyCommandSettings>
{
    public override int Execute(CommandContext context, EmptyCommandSettings settings)
    {
        return ForwardToProject("src/tools/prebake-tool/prebake-tool.csproj", ["all"]);
    }
}

public sealed class PrebakeEntitiesCommand : Command<EmptyCommandSettings>
{
    public override int Execute(CommandContext context, EmptyCommandSettings settings)
    {
        return ForwardToProject("src/tools/prebake-tool/prebake-tool.csproj", ["entities"]);
    }
}

public sealed class PrebakeLuaCommand : Command<EmptyCommandSettings>
{
    public override int Execute(CommandContext context, EmptyCommandSettings settings)
    {
        return ForwardToProject("src/tools/prebake-tool/prebake-tool.csproj", ["lua"]);
    }
}

public sealed class PrebakeBundleCommand : Command<EmptyCommandSettings>
{
    public override int Execute(CommandContext context, EmptyCommandSettings settings)
    {
        return ForwardToProject("src/tools/prebake-tool/prebake-tool.csproj", ["bundle"]);
    }
}

public sealed class EndpointAuditCommand : Command<EmptyCommandSettings>
{
    public override int Execute(CommandContext context, EmptyCommandSettings settings)
    {
        return ForwardToProject("src/tools/endpoint-auditor/EndpointAuditor.csproj", []);
    }
}

public sealed class LuaCommand : Command<EmptyCommandSettings>
{
    public override int Execute(CommandContext context, EmptyCommandSettings settings)
    {
        return ForwardToProject("src/tools/lua-verifier/LuaVerifier.csproj", []);
    }
}

public sealed class K2Command : Command<EmptyCommandSettings>
{
    public override int Execute(CommandContext context, EmptyCommandSettings settings)
    {
        return ForwardToProject("src/tools/lua-verifier/LuaVerifier.csproj", []);
    }
}

public sealed class StatusCommand : AsyncCommand<EmptyCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, EmptyCommandSettings settings)
    {
        await ShowEcosystemStatusAsync();
        return 0;
    }
}

public sealed class CleanCommandSettings : CommandSettings
{
    [CommandOption("-l|--logs")]
    [Description("Purge all .log files across services and games")]
    public bool CleanLogs { get; init; }

    [CommandOption("-a|--all")]
    [Description("Also remove logs, virtual environments, temporary models, and docfx metadata")]
    public bool CleanAll { get; init; }

    [CommandOption("-y|--yes")]
    [Description("Confirm out-of-tree workspace clean non-interactively")]
    public bool Yes { get; init; }
}

public sealed class CleanCommand : Command<CleanCommandSettings>
{
    public override int Execute(CommandContext context, CleanCommandSettings settings)
    {
        return RunClean(settings.CleanAll, settings.CleanLogs, settings.Yes);
    }
}

public sealed class SecretsCommand : Command<RawArgsSettings>
{
    public override int Execute(CommandContext context, RawArgsSettings settings)
    {
        string[] raw = CommandArgsHelper.GetEffectiveArgs(context, settings);
        return ManageSecrets(raw);
    }
}

public sealed class LogsCommandSettings : CommandSettings
{
    [CommandArgument(0, "[service]")]
    [Description("Service name ('master', 'chat', 'compel', 'willowmaker', 'api', 'ui', or 'all')")]
    [DefaultValue("all")]
    public string Service { get; init; } = "all";

    [CommandOption("-t|-f|--tail")]
    [Description("Live-stream/follow log file updates in real time")]
    public bool Tail { get; init; }

    [CommandOption("-n|--lines")]
    [Description("Number of initial lines to display (default: 50)")]
    [DefaultValue(50)]
    public int Lines { get; init; } = 50;
}

public sealed class LogsCommand : Command<LogsCommandSettings>
{
    public override int Execute(CommandContext context, LogsCommandSettings settings)
    {
        return ShowLogs(settings.Service, settings.Tail, settings.Lines);
    }
}
