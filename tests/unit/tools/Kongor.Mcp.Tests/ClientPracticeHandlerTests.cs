// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Text.Json;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class ClientPracticeHandlerTests
{
    [Fact]
    public async Task ClientPracticeHandler_DryRun_ReturnsPlannedCommandsAndCriteria()
    {
        ClientPracticeHandler handler = new();
        using JsonDocument doc = JsonDocument.Parse("""
        {
            "hero": "Hero_Bubbles",
            "map": "midwars",
            "bots": 3,
            "tickrate": 45,
            "cheats": true,
            "authMode": "offline",
            "windowMode": "offscreen",
            "dryRun": true
        }
        """);

        ToolCallResult result = await handler.HandleAsync(doc.RootElement);
        result.IsError.Should().BeFalse(result.FirstText);

        using JsonDocument resDoc = JsonDocument.Parse(result.FirstText);
        JsonElement root = resDoc.RootElement;

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("action").GetString().Should().Be("practice");
        root.GetProperty("hero").GetString().Should().Be("Hero_Bubbles");
        root.GetProperty("map").GetString().Should().Be("midwars");
        root.GetProperty("bots").GetInt32().Should().Be(3);
        root.GetProperty("tickrate").GetInt32().Should().Be(45);
        root.GetProperty("cheats").GetBoolean().Should().BeTrue();
        root.GetProperty("authMode").GetString().Should().Be("offline");
        root.GetProperty("windowMode").GetString().Should().Be("offscreen");
        root.GetProperty("dryRun").GetBoolean().Should().BeTrue();

        string practiceCmd = root.GetProperty("plannedPracticeCommand").GetString()!;
        practiceCmd.Should().Contain("StartGame practice PracticeGame map:midwars allheroes:true");
        practiceCmd.Should().Contain("Set svr_gameFPS 45");
        practiceCmd.Should().Contain("Set sv_cheats true");

        string heroSelectCmd = root.GetProperty("plannedHeroSelectCommand").GetString()!;
        heroSelectCmd.Should().Contain("SpawnHero Hero_Bubbles");
        heroSelectCmd.Should().Contain("SelectHero Hero_Bubbles");

        JsonElement botCmds = root.GetProperty("plannedBotCommands");
        botCmds.GetArrayLength().Should().Be(3);
        foreach (var el in botCmds.EnumerateArray())
        {
            el.GetString().Should().Be("AddFakePlayer 2");
        }
    }

    [Fact]
    public async Task ClientPracticeHandler_OfflineMode_ExecutesSuccessfullyInTestEnvironment()
    {
        ClientPracticeHandler handler = new();
        using JsonDocument doc = JsonDocument.Parse("""
        {
            "hero": "Hero_Pyromancer",
            "map": "caldavar",
            "authMode": "offline",
            "windowMode": "offscreen",
            "autoBoot": true,
            "cleanse": true,
            "captureProof": false
        }
        """);

        ToolCallResult result = await handler.HandleAsync(doc.RootElement);
        result.IsError.Should().BeFalse(result.FirstText);

        using JsonDocument resDoc = JsonDocument.Parse(result.FirstText);
        JsonElement root = resDoc.RootElement;

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("action").GetString().Should().Be("practice");
        root.GetProperty("hero").GetString().Should().Be("Hero_Pyromancer");
        root.GetProperty("map").GetString().Should().Be("caldavar");
        root.GetProperty("authMode").GetString().Should().Be("offline");
        root.GetProperty("gamePhase").GetInt32().Should().Be(3);
        root.GetProperty("phaseReached").GetBoolean().Should().BeTrue();
        root.GetProperty("entityVerified").GetBoolean().Should().BeTrue();
        root.GetProperty("heroHealth").GetSingle().Should().BeGreaterThan(0f);
        root.GetProperty("heroMaxHealth").GetSingle().Should().BeGreaterThan(0f);
    }

    [Fact]
    public async Task ClientPracticeHandler_ClusterMode_DryRun_GeneratesValidPlan()
    {
        ClientPracticeHandler handler = new();
        using JsonDocument doc = JsonDocument.Parse("""
        {
            "hero": "Hero_Valkyrie",
            "authMode": "cluster",
            "account": "testchampion",
            "password": "secretpassword",
            "dryRun": true
        }
        """);

        ToolCallResult result = await handler.HandleAsync(doc.RootElement);
        result.IsError.Should().BeFalse();

        using JsonDocument resDoc = JsonDocument.Parse(result.FirstText);
        JsonElement root = resDoc.RootElement;

        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("hero").GetString().Should().Be("Hero_Valkyrie");
        root.GetProperty("authMode").GetString().Should().Be("cluster");
        root.GetProperty("dryRun").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task ClientPracticeHandler_AutoBootFalse_WhenClientOffline_ReturnsError()
    {
        ClientPracticeHandler handler = new();
        using JsonDocument doc = JsonDocument.Parse("""
        {
            "hero": "Hero_Pyromancer",
            "autoBoot": false
        }
        """);

        ToolCallResult result = await handler.HandleAsync(doc.RootElement);
        result.IsError.Should().BeTrue();
        result.FirstText.Should().Contain("autoBoot");
    }

    [Fact]
    public async Task ClientPracticeHandler_TickrateClamping_EnforcesSafeRange()
    {
        ClientPracticeHandler handler = new();

        // Test clamping below minimum (10 -> 20)
        using (JsonDocument docLow = JsonDocument.Parse("""{"tickrate": 10, "dryRun": true}"""))
        {
            ToolCallResult resLow = await handler.HandleAsync(docLow.RootElement);
            resLow.IsError.Should().BeFalse();
            using JsonDocument resDoc = JsonDocument.Parse(resLow.FirstText);
            resDoc.RootElement.GetProperty("tickrate").GetInt32().Should().Be(20);
        }

        // Test clamping above maximum (100 -> 60)
        using (JsonDocument docHigh = JsonDocument.Parse("""{"tickrate": 100, "dryRun": true}"""))
        {
            ToolCallResult resHigh = await handler.HandleAsync(docHigh.RootElement);
            resHigh.IsError.Should().BeFalse();
            using JsonDocument resDoc = JsonDocument.Parse(resHigh.FirstText);
            resDoc.RootElement.GetProperty("tickrate").GetInt32().Should().Be(60);
        }
    }

    [Fact]
    public async Task McpToolRegistry_RoutesPracticeAliases_ToClientPractice()
    {
        McpToolRegistry registry = new(registerDefaults: true);

        registry.HasTool("kongor_client_practice").Should().BeTrue();
        registry.HasTool("kongor_practice").Should().BeTrue();
        registry.HasTool("client_practice").Should().BeTrue();
        registry.HasTool("practice_match").Should().BeTrue();

        using JsonDocument doc = JsonDocument.Parse("""
        {
            "hero": "Hero_FlintBeastwood",
            "dryRun": true
        }
        """);

        ToolCallResult result = await registry.ExecuteToolAsync("kongor_practice", doc.RootElement);
        result.IsError.Should().BeFalse();

        using JsonDocument resDoc = JsonDocument.Parse(result.FirstText);
        resDoc.RootElement.GetProperty("hero").GetString().Should().Be("Hero_FlintBeastwood");
        resDoc.RootElement.GetProperty("action").GetString().Should().Be("practice");
        resDoc.RootElement.GetProperty("dryRun").GetBoolean().Should().BeTrue();
    }

    [Theory]
    [InlineData("WARM_PROBE|5|caldavar|false|false|620", "caldavar", true, "none")]
    [InlineData("WARM_PROBE|3|caldavar|false|false|620", "caldavar", false, "not_in_active_match_phase_3")]
    [InlineData("WARM_PROBE|5|midwars|false|false|620", "caldavar", false, "map_mismatch_midwars_vs_caldavar")]
    [InlineData("WARM_PROBE|5|caldavar|true|false|620", "caldavar", false, "replay_or_spectator")]
    [InlineData("WARM_PROBE|5|caldavar|false|true|620", "caldavar", false, "replay_or_spectator")]
    [InlineData("WARM_PROBE|5|caldavar|false|false|0", "caldavar", false, "hero_dead_or_unspawned")]
    [InlineData("", "caldavar", false, "empty_probe_response")]
    public void EvaluateWarmProbeResponse_EnforcesMandatoryInvalidationList(
        string probePayload,
        string requestedMap,
        bool expectedCanReuse,
        string expectedReason)
    {
        var (canReuse, reason) = ClientPracticeHandler.EvaluateWarmProbeResponse(probePayload, requestedMap);
        canReuse.Should().Be(expectedCanReuse);
        reason.Should().Be(expectedReason);
    }

    [Fact]
    public async Task TryEvaluateWarmPracticeReuseAsync_RejectsCleanse_DisabledReuse_AndBotRequests()
    {
        ClientPracticeHandler handler = new();

        (await handler.TryEvaluateWarmPracticeReuseAsync("caldavar", 0, cleanseExplicitlyTrue: true, reuseIfActive: true, null, CancellationToken.None))
            .Should().Be((false, "caller_requested_cleanse"));

        (await handler.TryEvaluateWarmPracticeReuseAsync("caldavar", 0, cleanseExplicitlyTrue: false, reuseIfActive: false, null, CancellationToken.None))
            .Should().Be((false, "reuse_disabled"));

        (await handler.TryEvaluateWarmPracticeReuseAsync("caldavar", 3, cleanseExplicitlyTrue: false, reuseIfActive: true, null, CancellationToken.None))
            .Should().Be((false, "bots_requested"));
    }
}
