// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Kongor.Cli.Commands;
using Kongor.Cli.Mcp.Protocol;
using Spectre.Console;
using Spectre.Console.Cli;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class V020CliAndMcpStandaloneTests
{
    [Fact]
    public void McpToolRegistry_BundledSafeAllowlist_ExposesStandaloneLifecycleToolsAndBlocksMutatingDbEval()
    {
        // protects: Standalone bundle MCP capability set (allowing server/client/compel/willowmaker management while blocking db_migrate & app_eval).
        // fails_when: Essential standalone tools drop out of BundledSafeTools or unsafe dev tools enter BundledSafeTools.
        // seam: Real McpToolRegistry.BundledSafeTools & BundledUnsafeTools sets.
        string[] requiredSafe =
        [
            "kongor_health_check",
            "kongor_diagnostics",
            "kongor_vfs_resolve",
            "kongor_server_manage",
            "kongor_compel_manage",
            "kongor_willowmaker_manage",
            "kongor_game_client_manage",
            "kongor_game_server_manage",
            "kongor_capture_window",
            "kongor_client_eval_lua",
            "kongor_ui_control"
        ];

        foreach (string tool in requiredSafe)
        {
            McpToolRegistry.BundledSafeTools.Should().Contain(tool);
            McpToolRegistry.BundledUnsafeTools.Should().NotContain(tool);
        }

        McpToolRegistry.BundledUnsafeTools.Should().Contain("kongor_db_migrate");
        McpToolRegistry.BundledUnsafeTools.Should().Contain("kongor_app_eval");
        McpToolRegistry.BundledSafeTools.Should().NotContain("kongor_db_migrate");
        McpToolRegistry.BundledSafeTools.Should().NotContain("kongor_app_eval");
    }

    [Fact]
    public void AccountCreateSettings_SupportsBothNameAndUsernameCliOptions()
    {
        // protects: CLI ergonomics for standalone community hosts running 'kongor account create --username <IGN>' or '--name <IGN>'.
        // fails_when: CommandOption attribute drops '-u' or '--username', causing Spectre.Console.Cli option parse failures.
        // seam: Real AccountCreateSettings reflection + Spectre.Console Markup.Escape safety on bracketed clan tags.
        PropertyInfo? nameProp = typeof(AccountCreateSettings).GetProperty(nameof(AccountCreateSettings.Name));
        nameProp.Should().NotBeNull();

        CommandOptionAttribute? optionAttr = nameProp!.GetCustomAttribute<CommandOptionAttribute>();
        optionAttr.Should().NotBeNull();
        optionAttr!.LongNames.Should().Contain(["name", "username"]);
        optionAttr.ShortNames.Should().Contain(["n", "u"]);

        // Verify bracketed clan tags and telemetry prefixes never throw markup exceptions when escaped
        string rawBracketed = "[BundledLayout] [PK]CommunityHost [NEXUS_TELEMETRY]";
        Action renderAction = () =>
        {
            StringWriter sw = new();
            IAnsiConsole testConsole = AnsiConsole.Create(new AnsiConsoleSettings
            {
                Out = new AnsiConsoleOutput(sw)
            });
            testConsole.MarkupLine($"[green]{Markup.Escape(rawBracketed)}[/]");
        };
        renderAction.Should().NotThrow();
    }
}
