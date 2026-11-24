// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Text.Json;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Moq;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class K2UiInspectAndMatchLaunchTests
{
    [Fact]
    public void McpToolRegistry_WhenInvoked_RegistersNewToolsAndAliases()
    {
        McpToolRegistry registry = new(registerDefaults: true);
        IReadOnlyList<ToolDefinition> tools = registry.ListTools();

        string[] expectedTools =
        [
            "kongor_ui_inspect",
            "kongor_ui_tree",
            "kongor_k2_ui_inspect",
            "kongor_match_launch",
            "kongor_practice_launch"
        ];

        foreach (string toolName in expectedTools)
        {
            registry.HasTool(toolName).Should().BeTrue($"Registry must contain '{toolName}'");
            ToolDefinition? def = registry.GetTool(toolName);
            def.Should().NotBeNull();
            def!.Description.Should().NotBeNullOrWhiteSpace();
            def.InputSchema.Should().NotBeNull();
            def.InputSchema.Type.Should().Be("object");
        }
    }

    [Fact]
    public void RepoRoot_OnQuery_IsFoundCorrectly()
    {
        string repoRoot = K2UiInspectService.FindRepoRoot();
        if (!NexusTestEnvironment.HasGameAssets)
        {
            File.Exists(Path.Combine(repoRoot, "ProjectKongor.slnx")).Should().BeTrue($"repoRoot was '{repoRoot}' and expected ProjectKongor.slnx");
            return;
        }

        bool found = File.Exists(Path.Combine(repoRoot, "games", "client", "override", "ui", "fe2", "main_v2.interface")) ||
                     File.Exists(Path.Combine(repoRoot, "games", "client", "override", "ui", "fe2", "newui", "background_v2.package")) ||
                     File.Exists(Path.Combine(repoRoot, "games", "client", "override", "ui", "fe2", "background.package")) ||
                     File.Exists(Path.Combine(repoRoot, "games", "client", "ClientPackageFiles", "ui", "fe2", "main_v2.interface")) ||
                     File.Exists(Path.Combine(repoRoot, "games", "client", "ClientPackageFiles", "ui", "fe2", "newui", "background_v2.package")) ||
                     File.Exists(Path.Combine(repoRoot, "games", "client", "ClientPackageFiles", "ui", "fe2", "background.package"));
        found.Should().BeTrue($"repoRoot was '{repoRoot}' and expected UI assets in games/client/override/ui/fe2/ or games/client/ClientPackageFiles/ui/fe2/");
    }

    [Fact]
    [Trait("Category", "Manual")]
    public void K2UiInspectService_InspectStaticUi_ParsesPackageWithTemplateExpansion()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        K2UiInspectService service = new();

        // Target background.package or main_v2.interface in client files
        K2UiInspectResult result = service.InspectStaticUi("background.package", depth: 8);

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.Mode.Should().Be("static");
        result.SourcePath.Should().NotBeNullOrWhiteSpace();
        result.TotalWidgets.Should().BeGreaterThan(0);
        result.Root.Should().NotBeNull();
        result.Widgets.Should().NotBeEmpty();

        // Check if fe2_background or background_main widget was parsed
        bool hasBackgroundWidget = result.Widgets.Any(w =>
            w.Name.Contains("background", StringComparison.OrdinalIgnoreCase) ||
            w.Type.Equals("panel", StringComparison.OrdinalIgnoreCase));
        hasBackgroundWidget.Should().BeTrue();
    }

    [Trait("Category", "Manual")]
    [Fact]
    public void K2UiInspectService_WhenInvoked_FlagsMissingTexturesAndWhiteFallbacks()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        K2UiInspectService service = new();

        // Parse main_v2.interface or background.package
        K2UiInspectResult result = service.InspectStaticUi("main_v2.interface", depth: 5);

        result.Success.Should().BeTrue();
        result.Root.Should().NotBeNull();

        // Any $white should be flagged as WhiteFallback
        foreach (var widget in result.Widgets.Where(w => string.Equals(w.Texture, "$white", StringComparison.OrdinalIgnoreCase)))
        {
            widget.IsWhiteFallback.Should().BeTrue();
            result.Warnings.Should().Contain(w => w.WarningType == "WhiteFallback" && w.WidgetName == widget.Name);
        }
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task K2UiInspectHandler_HandleAsync_ExecutesStaticInspection()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        K2UiInspectService service = new();
        K2UiInspectHandler handler = new(service);

        JsonElement args = JsonElement.Parse("{\"mode\": \"static\", \"depth\": 4}");
        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        result.Content.Should().ContainSingle();
        string jsonText = ((TextContentItem)result.Content[0]).Text;
        jsonText.Should().Contain("\"status\": \"success\"");
        jsonText.Should().Contain("\"mode\": \"static\"");
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task K2UiInspectHandler_HandleAsync_SpecificWidgetFilter()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        K2UiInspectService service = new();
        K2UiInspectHandler handler = new(service);

        JsonElement args = JsonElement.Parse("{\"mode\": \"static\", \"widget_name\": \"background_main\", \"depth\": 6}");
        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        string jsonText = ((TextContentItem)result.Content[0]).Text;
        jsonText.Should().Contain("\"status\": \"success\"");
    }

    [Fact]
    public void MatchLaunchHandler_BuildMatchLaunchArguments_ConstructsValidPracticeArgs()
    {
        IReadOnlyList<string> args = MatchLaunchHandler.BuildMatchLaunchArguments(
            map: "caldavar",
            hero: "Hero_Pyromancer",
            mode: "practice",
            bots: 0,
            cheats: true,
            headless: false,
            resolution: "1920x1080",
            fullscreen: false,
            maxFps: 1000,
            masterServer: "127.0.0.1:55555");

        string joined = string.Join(" ", args);

        joined.Should().Contain("+Set map caldavar");
        joined.Should().Contain("+Set default_hero Hero_Pyromancer");
        joined.Should().Contain("+StartGame practice PracticeGame map:caldavar allheroes:true");
        joined.Should().Contain("+Set dev true");
        joined.Should().Contain("+Set cheats true");
        joined.Should().Contain("-pipe willowmaker-ipc");
        joined.Should().Contain(ClientModMountBuilder.GetCanonicalModString());
    }

    [Fact]
    public void MatchLaunchHandler_BuildMatchLaunchArguments_BotsAndHeadlessConfigured()
    {
        IReadOnlyList<string> args = MatchLaunchHandler.BuildMatchLaunchArguments(
            map: "midwars",
            hero: "Hero_WitchSlayer",
            mode: "bot_match",
            bots: 9,
            cheats: true,
            headless: true,
            resolution: "1280x720",
            fullscreen: false,
            maxFps: 500,
            masterServer: "127.0.0.1:55555");

        string joined = string.Join(" ", args);

        joined.Should().Contain("+Set map midwars");
        joined.Should().Contain("+Set default_hero Hero_WitchSlayer");
        joined.Should().Contain("+StartGame bot_match PracticeGame map:midwars allheroes:true");
        joined.Should().Contain("+Set game_botMatch true");
        joined.Should().Contain("+Set svr_botMatch true");
        joined.Should().Contain("+Set svr_numBots 9");
        joined.Should().Contain("+Set vid_pos -32000 -32000");
        joined.Should().Contain("+Set sound_disable true");
    }

    [Fact]
    public async Task MatchLaunchHandler_HandleAsync_StatusWhenNotRunning()
    {
        MatchLaunchHandler handler = new();

        JsonElement args = JsonElement.Parse("{\"action\": \"status\"}");
        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        string jsonText = ((TextContentItem)result.Content[0]).Text;
        jsonText.Should().Contain("\"status\": \"success\"");
        jsonText.Should().Contain("\"running\": false");
    }

    [Fact]
    public async Task MatchLaunchHandler_HandleAsync_StopWhenNotRunning()
    {
        MatchLaunchHandler handler = new();

        JsonElement args = JsonElement.Parse("{\"action\": \"stop\"}");
        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        string jsonText = ((TextContentItem)result.Content[0]).Text;
        jsonText.Should().Contain("\"status\": \"success\"");
        jsonText.Should().Contain("\"running\": false");
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task MatchLaunchHandler_HandleAsync_RapidTerminateGuardrail()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        // When hon_x64.exe is present in games/client, rapidTerminateAfterLaunch verifies launch and terminates within 2 seconds.
        // If not found in dev environment, it returns an error with clean JSON.
        MatchLaunchHandler handler = new();

        JsonElement args = JsonElement.Parse("{\"action\": \"launch\", \"map\": \"caldavar\", \"rapidTerminateAfterLaunch\": true, \"headless\": true}");
        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        string jsonText = ((TextContentItem)result.Content[0]).Text;
        // Either successfully launched and rapid_terminated, or reported missing hon_x64.exe binary
        (jsonText.Contains("rapid_terminated") || jsonText.Contains("Unable to locate hon_x64.exe")).Should().BeTrue();
    }
}
