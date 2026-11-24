// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Text.Json;
using FluentAssertions;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Protocol;
using Xunit;

namespace Kongor.Mcp.Tests;

/// <summary>
/// Unit tests for dedicated game server simulation tickrate calibration (30 Hz / 60 Hz)
/// in Kongor.Cli MCP handlers.
/// </summary>
public sealed class DedicatedServerTickrateTests
{
    private static JsonElement ParseJson(string json) => JsonElement.Parse(json);

    [Fact]
    public void BuildDedicatedServerArguments_Default_Emits20HzAndMaxFrames5()
    {
        IReadOnlyList<string> args = GameServerManageHandler.BuildDedicatedServerArguments(
            gameDir: "C:/Games/HoN",
            port: 11235,
            masterServer: "127.0.0.1:55555",
            serverName: "Kongor Tickrate Test",
            map: "caldavar");

        args.Should().Contain("+Set svr_gameFPS 20");
        args.Should().Contain("+Set svr_maxFramesPerHostFrame 5");
    }

    [Theory]
    [InlineData(20, "+Set svr_gameFPS 20")]
    [InlineData(30, "+Set svr_gameFPS 30")]
    [InlineData(60, "+Set svr_gameFPS 60")]
    public void BuildDedicatedServerArguments_CustomTickrate_EmitsExpectedCvars(int tickrate, string expectedCvar)
    {
        IReadOnlyList<string> args = GameServerManageHandler.BuildDedicatedServerArguments(
            gameDir: "C:/Games/HoN",
            port: 11235,
            masterServer: "127.0.0.1:55555",
            serverName: "Kongor Tickrate Test",
            map: "caldavar",
            tickrate: tickrate);

        args.Should().Contain(expectedCvar);
        args.Should().Contain("+Set svr_maxFramesPerHostFrame 5");
    }

    [Theory]
    [InlineData(5, "+Set svr_gameFPS 20")]   // Below min: clamps to 20
    [InlineData(120, "+Set svr_gameFPS 60")] // Above max: clamps to 60
    public void BuildDedicatedServerArguments_OutOfRangeTickrate_ClampsBetween20And60(int tickrate, string expectedCvar)
    {
        IReadOnlyList<string> args = GameServerManageHandler.BuildDedicatedServerArguments(
            gameDir: "C:/Games/HoN",
            port: 11235,
            masterServer: "127.0.0.1:55555",
            serverName: "Kongor Tickrate Test",
            map: "caldavar",
            tickrate: tickrate);

        args.Should().Contain(expectedCvar);
        args.Should().Contain("+Set svr_maxFramesPerHostFrame 5");
    }

    [Fact]
    public async Task GameServerManageHandler_Launch_WithTickrate30_ReturnsCalibratedArguments()
    {
        GameServerManageHandler handler = new();
        JsonElement args = ParseJson("{\"action\": \"launch\", \"port\": 11236, \"tickrate\": 30}");

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        string text = ((TextContentItem)result.Content[0]).Text;
        text.Should().Contain("\"tickrate\": 30");
        text.Should().Contain("+Set svr_gameFPS 30");
        text.Should().Contain("+Set svr_maxFramesPerHostFrame 5");
    }

    [Fact]
    public async Task GameServerManageHandler_Launch_WithTickrateString_ParsesAndCalibrates()
    {
        GameServerManageHandler handler = new();
        JsonElement args = ParseJson("{\"action\": \"launch\", \"port\": 11237, \"tickrate\": \"60\"}");

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        string text = ((TextContentItem)result.Content[0]).Text;
        text.Should().Contain("\"tickrate\": 60");
        text.Should().Contain("+Set svr_gameFPS 60");
        text.Should().Contain("+Set svr_maxFramesPerHostFrame 5");
    }

    [Fact]
    public async Task GameServerManageHandler_Launch_WithCamelCaseTickRate_ParsesCorrectly()
    {
        GameServerManageHandler handler = new();
        JsonElement args = ParseJson("{\"action\": \"launch\", \"port\": 11238, \"tickRate\": 30}");

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        string text = ((TextContentItem)result.Content[0]).Text;
        text.Should().Contain("\"tickrate\": 30");
        text.Should().Contain("+Set svr_gameFPS 30");
    }

    [Theory]
    [InlineData("+Set svr_gameFPS 30", 30)]
    [InlineData("+Set svr_gameFPS 60", 60)]
    [InlineData("-execute \"Set svr_gameFPS 20;\"", 20)]
    [InlineData("+Set svr_fps 45", 45)]
    [InlineData("+Set svr_gameFPS 5", 20)]   // Below min: clamps to 20
    [InlineData("+Set svr_gameFPS 120", 60)] // Above max: clamps to 60
    public void ExtractTickrateFromCommandLine_ParsesAndClampsTickrate(string cmdLine, int expectedTickrate)
    {
        int? parsed = GameServerManageHandler.ExtractTickrateFromCommandLine(cmdLine);
        parsed.Should().Be(expectedTickrate);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("-dedicated -port 11235 -name \"Test Server\"")]
    public void ExtractTickrateFromCommandLine_WhenMissing_ReturnsNull(string? cmdLine)
    {
        int? parsed = GameServerManageHandler.ExtractTickrateFromCommandLine(cmdLine);
        parsed.Should().BeNull();
    }

    [Fact]
    public void BuildMatchLaunchArguments_Default_Emits20HzAndMaxFrames5()
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

        string joined = string.Join(' ', args);
        joined.Should().Contain("+Set svr_gameFPS 20");
        joined.Should().Contain("+Set svr_maxFramesPerHostFrame 5");
    }

    [Fact]
    public void BuildMatchLaunchArguments_Headless_EmitsCursorAndFullscreenSafetyFlags()
    {
        IReadOnlyList<string> args = MatchLaunchHandler.BuildMatchLaunchArguments(
            map: "caldavar",
            hero: "Hero_Pyromancer",
            mode: "practice",
            bots: 0,
            cheats: true,
            headless: true,
            resolution: "1280x720",
            fullscreen: false,
            maxFps: 1000,
            masterServer: "127.0.0.1:55555");

        string joined = string.Join(' ', args);
        joined.Should().Contain("+Set d3d_exclusive false");
        joined.Should().Contain("+Set options_fullscreen false");
        joined.Should().Contain("+Set host_startupCfg \"~/startup_headless.cfg\"");
        joined.Should().NotContain("+Set cg_constrainCursor false");
    }

    [Theory]
    [InlineData(20, "+Set svr_gameFPS 20")]
    [InlineData(30, "+Set svr_gameFPS 30")]
    [InlineData(60, "+Set svr_gameFPS 60")]
    [InlineData(10, "+Set svr_gameFPS 20")]  // Clamped to 20
    [InlineData(120, "+Set svr_gameFPS 60")] // Clamped to 60
    public void BuildMatchLaunchArguments_CustomTickrate_EmitsExpectedCvars(int tickrate, string expectedCvar)
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
            masterServer: "127.0.0.1:55555",
            tickrate: tickrate);

        string joined = string.Join(' ', args);
        joined.Should().Contain(expectedCvar);
        joined.Should().Contain("+Set svr_maxFramesPerHostFrame 5");
    }

    [Fact]
    public async Task MatchLaunchHandler_Launch_WithTickrate30_ReturnsCalibratedArguments()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;

        MatchLaunchHandler handler = new();
        JsonElement args = ParseJson("{\"action\": \"launch\", \"map\": \"caldavar\", \"hero\": \"Hero_Pyromancer\", \"tickrate\": 30, \"rapidTerminateAfterLaunch\": true}");

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        string text = ((TextContentItem)result.Content[0]).Text;
        text.Should().Contain("\"tickrate\": 30");
        text.Should().Contain("+Set svr_gameFPS 30");
        text.Should().Contain("+Set svr_maxFramesPerHostFrame 5");
    }

    [Fact]
    public async Task MatchLaunchHandler_Launch_WithTickrateString_ParsesAndCalibrates()
    {
        if (!NexusTestEnvironment.HasGameAssets) return;

        MatchLaunchHandler handler = new();
        JsonElement args = ParseJson("{\"action\": \"launch\", \"map\": \"caldavar\", \"hero\": \"Hero_Pyromancer\", \"tickrate\": \"60\", \"rapidTerminateAfterLaunch\": true}");

        ToolCallResult result = await handler.HandleAsync(args);

        result.IsError.Should().BeFalse();
        string text = ((TextContentItem)result.Content[0]).Text;
        text.Should().Contain("\"tickrate\": 60");
        text.Should().Contain("+Set svr_gameFPS 60");
        text.Should().Contain("+Set svr_maxFramesPerHostFrame 5");
    }
}
