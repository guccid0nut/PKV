// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Text.Json;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class ClientNavigateHandlerTests
{
    [Fact]
    public void K2RouteRegistry_ResolvesAllCanonicalDestinations_AndAliases()
    {
        string[] canonicalDestinations =
        [
            "main_menu", "store", "learnatorium", "compendium",
            "watch", "live", "options", "profile", "hero_preview", "lobby"
        ];

        foreach (string dest in canonicalDestinations)
        {
            K2NavigationRoute? route = K2RouteRegistry.ResolveRoute(dest);
            route.Should().NotBeNull($"Canonical route '{dest}' must resolve in K2RouteRegistry.");
            route!.VerificationWidget.Should().NotBeNullOrWhiteSpace($"Route '{dest}' must have a verification widget.");
            route.TargetInterface.Should().NotBeNullOrWhiteSpace($"Route '{dest}' must specify a target interface.");
            
            K2NavigationContext ctx = new()
            {
                Destination = dest,
                Tab = "graphics",
                Hero = "Hero_Bubbles",
                Account = "guccidonut"
            };
            string lua = route.BuildLuaScript(ctx);
            lua.Should().NotBeNullOrWhiteSpace($"Route '{dest}' must generate a non-empty Lua transition script.");
        }

        // Test alias resolution
        K2RouteRegistry.ResolveRoute("home")?.Destination.Should().Be("main_menu");
        K2RouteRegistry.ResolveRoute("shop")?.Destination.Should().Be("store");
        K2RouteRegistry.ResolveRoute("replays")?.Destination.Should().Be("watch");
        K2RouteRegistry.ResolveRoute("settings")?.Destination.Should().Be("options");
        K2RouteRegistry.ResolveRoute("stats")?.Destination.Should().Be("profile");
        K2RouteRegistry.ResolveRoute("preview")?.Destination.Should().Be("hero_preview");
    }

    [Fact]
    public async Task ClientNavigateHandler_DryRun_ReturnsPlannedLuaAndCriteria()
    {
        ClientNavigateHandler handler = new();
        using JsonDocument doc = JsonDocument.Parse("""
        {
            "destination": "store",
            "authMode": "offline",
            "windowMode": "offscreen",
            "tab": "heroes",
            "hero": "Hero_Bubbles",
            "dryRun": true
        }
        """);

        ToolCallResult result = await handler.HandleAsync(doc.RootElement);
        result.IsError.Should().BeFalse();

        using JsonDocument resDoc = JsonDocument.Parse(result.FirstText);
        JsonElement root = resDoc.RootElement;

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("action").GetString().Should().Be("navigate");
        root.GetProperty("destination").GetString().Should().Be("store");
        root.GetProperty("authMode").GetString().Should().Be("offline");
        root.GetProperty("windowMode").GetString().Should().Be("offscreen");
        root.GetProperty("verificationWidget").GetString().Should().Be("store_container2");
        root.GetProperty("dryRun").GetBoolean().Should().BeTrue();
        root.GetProperty("plannedLua").GetString().Should().Contain("store_container2");
    }

    [Fact]
    public async Task ClientNavigateHandler_OfflineMode_ExecutesSuccessfullyInTestEnvironment()
    {
        ClientNavigateHandler handler = new();
        using JsonDocument doc = JsonDocument.Parse("""
        {
            "destination": "options",
            "authMode": "offline",
            "windowMode": "offscreen",
            "category": "graphics",
            "autoBoot": true,
            "verify": true,
            "captureProof": false
        }
        """);

        ToolCallResult result = await handler.HandleAsync(doc.RootElement);
        result.IsError.Should().BeFalse(result.FirstText);

        using JsonDocument resDoc = JsonDocument.Parse(result.FirstText);
        JsonElement root = resDoc.RootElement;

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("destination").GetString().Should().Be("options");
        root.GetProperty("authMode").GetString().Should().Be("offline");
        root.GetProperty("verificationWidget").GetString().Should().Be("game_options");
        root.GetProperty("verified").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task ClientNavigateHandler_AutoBootFalse_WhenClientOffline_ReturnsError()
    {
        ClientNavigateHandler handler = new();
        using JsonDocument doc = JsonDocument.Parse("""
        {
            "destination": "options",
            "autoBoot": false
        }
        """);

        ToolCallResult result = await handler.HandleAsync(doc.RootElement);
        result.IsError.Should().BeTrue();
        result.FirstText.Should().Contain("autoBoot");
    }

    [Fact]
    public async Task ClientNavigateHandler_ClusterMode_DryRun_GeneratesValidPlan()
    {
        ClientNavigateHandler handler = new();
        using JsonDocument doc = JsonDocument.Parse("""
        {
            "destination": "profile",
            "authMode": "cluster",
            "account": "testuser",
            "tab": "overview",
            "dryRun": true
        }
        """);

        ToolCallResult result = await handler.HandleAsync(doc.RootElement);
        result.IsError.Should().BeFalse();

        using JsonDocument resDoc = JsonDocument.Parse(result.FirstText);
        JsonElement root = resDoc.RootElement;

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("destination").GetString().Should().Be("profile");
        root.GetProperty("authMode").GetString().Should().Be("cluster");
        root.GetProperty("verificationWidget").GetString().Should().Be("playerstats");
        root.GetProperty("dryRun").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task ClientNavigateHandler_UnknownDestination_ReturnsDescriptiveError()
    {
        ClientNavigateHandler handler = new();
        using JsonDocument doc = JsonDocument.Parse("""
        {
            "destination": "nonexistent_secret_screen_xyz"
        }
        """);

        ToolCallResult result = await handler.HandleAsync(doc.RootElement);
        result.IsError.Should().BeTrue();
        result.FirstText.Should().Contain("Unknown navigation destination");
        result.FirstText.Should().Contain("store");
        result.FirstText.Should().Contain("learnatorium");
    }

    [Fact]
    public async Task McpToolRegistry_RoutesNavigationAliases_ToClientNavigate()
    {
        McpToolRegistry registry = new(registerDefaults: true);

        registry.HasTool("kongor_client_navigate").Should().BeTrue();
        registry.HasTool("kongor_navigate").Should().BeTrue();
        registry.HasTool("kongor_nav").Should().BeTrue();

        using JsonDocument doc = JsonDocument.Parse("""
        {
            "destination": "learnatorium",
            "dryRun": true
        }
        """);

        ToolCallResult result = await registry.ExecuteToolAsync("kongor_navigate", doc.RootElement);
        result.IsError.Should().BeFalse();

        using JsonDocument resDoc = JsonDocument.Parse(result.FirstText);
        resDoc.RootElement.GetProperty("destination").GetString().Should().Be("learnatorium");
        resDoc.RootElement.GetProperty("verificationWidget").GetString().Should().Be("learnatorium_base");
    }
}
