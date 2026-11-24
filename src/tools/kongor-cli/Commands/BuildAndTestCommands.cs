// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.ComponentModel;
using Spectre.Console.Cli;
using static Kongor.Cli.Program;

namespace Kongor.Cli.Commands;

public sealed class BuildCommandSettings : CommandSettings
{
    [CommandArgument(0, "[target]")]
    [Description("Target project or category to build (e.g. 'all', 'apps', 'services', 'libs', 'tools', 'willowmaker', 'compel', 'aspire')")]
    [DefaultValue("all")]
    public string Target { get; init; } = "all";

    [CommandOption("-c|--configuration")]
    [Description("Build configuration ('Debug', 'Release', 'All' (default: builds both Debug and Release))")]
    [DefaultValue("All")]
    public string Configuration { get; init; } = "All";

    [CommandOption("--clean")]
    [Description("Run dotnet clean before building")]
    public bool Clean { get; init; }

    [CommandOption("--restore")]
    [Description("Force project package restoration")]
    public bool Restore { get; init; }

    [CommandOption("-v|--verbosity")]
    [Description("MSBuild verbosity ('quiet', 'minimal', 'normal', 'detailed')")]
    [DefaultValue("minimal")]
    public string Verbosity { get; init; } = "minimal";
}

public sealed class BuildCommand : Command<BuildCommandSettings>
{
    public override int Execute(CommandContext context, BuildCommandSettings settings)
    {
        return ExecuteBuild(settings.Target, settings.Configuration, settings.Clean, settings.Restore, settings.Verbosity);
    }
}

public sealed class TestCommandSettings : CommandSettings
{
    [CommandArgument(0, "[target]")]
    [Description("Test category ('all' (default), 'unit', 'integration', 'performance', or specific project name)")]
    [DefaultValue("all")]
    public string Target { get; init; } = "all";

    [CommandOption("--no-build")]
    [Description("Do not build projects before testing (default: true)")]
    public bool? NoBuild { get; init; }

    [CommandOption("--build")]
    [Description("Build projects before testing")]
    public bool Build { get; init; }

    [CommandOption("--coverage")]
    [Description("Collect code coverage metrics")]
    public bool Coverage { get; init; }

    [CommandOption("-f|--filter")]
    [Description("Filter expression for tests")]
    public string? Filter { get; init; }

    [CommandOption("--all")]
    [Description("Run all tests including gated manual tests without filtering")]
    public bool All { get; init; }

    [CommandOption("-v|--verbosity")]
    [Description("Dotnet test verbosity (quiet, minimal, normal, detailed)")]
    public string? Verbosity { get; init; }

    [CommandOption("--headless")]
    [Description("Suppress auto-opening the generated HTML code coverage report in a browser")]
    public bool Headless { get; init; }
}

public sealed class TestCommand : Command<TestCommandSettings>
{
    public override int Execute(CommandContext context, TestCommandSettings settings)
    {
        bool effectiveNoBuild = !settings.Build && (settings.NoBuild ?? true);
        return RunTests(
            settings.Target,
            effectiveNoBuild,
            settings.Coverage,
            settings.Filter,
            settings.Verbosity,
            settings.All,
            settings.Headless);
    }
}

public sealed class CoverageCommand : Command<TestCommandSettings>
{
    public override int Execute(CommandContext context, TestCommandSettings settings)
    {
        bool effectiveNoBuild = !settings.Build && (settings.NoBuild ?? true);
        return RunTests(
            settings.Target,
            effectiveNoBuild,
            coverage: true,
            settings.Filter,
            settings.Verbosity,
            settings.All,
            settings.Headless);
    }
}
