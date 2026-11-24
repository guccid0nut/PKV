// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: AutoExecGeneratorTests.cs
// System: NEXUS Replay Video Renderer Test Suite
// Policy: Strict Explicit Typing (NO 'var'), xUnit Framework
// ============================================================================

using ReplayRenderer.Service.Models;
using ReplayRenderer.Service.ProcessLifecycle;
using Xunit;
using Xunit.Abstractions;

namespace ReplayRenderer.Tests;

public sealed class AutoExecGeneratorTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    [Fact]
    public void Generate_CameraLockingCvars_ContainsAllRequiredCvars()
    {
        _output.WriteLine("[Test] Validating camera locking cvars in generated autoexec.cfg...");

        RenderJobConfiguration config = new()
        {
            ReplayFilePath = "C:\\Replays\\match_01.honreplay",
            OutputFilePath = "C:\\Videos\\match_01.mp4",
            GameDirectory = "C:\\Games\\HoN",
            VulkanLayerPath = "C:\\Layers",
            PovClientIndex = 3,
            TargetFramerate = 60
        };

        string cfg = AutoExecGenerator.Generate(config, "match_01.honreplay");
        _output.WriteLine("[Test] Generated cfg:\n" + cfg);

        Assert.Contains("Set \"replay_client\" \"3\"", cfg);
        Assert.Contains("Set \"cg_lockCamera\" \"true\"", cfg);
        Assert.Contains("Set \"cg_fogofwar\" \"true\"", cfg);
        Assert.Contains("Set \"cam_mode\" \"0\"", cfg);
        Assert.Contains("Set \"cam_smooth\" \"1\"", cfg);
    }

    [Fact]
    public void Generate_FramerateAndTimingCvars_ContainsAllRequiredCvars()
    {
        _output.WriteLine("[Test] Validating framerate and timing cvars in generated autoexec.cfg...");

        RenderJobConfiguration config = new()
        {
            ReplayFilePath = "C:\\Replays\\match_01.honreplay",
            OutputFilePath = "C:\\Videos\\match_01.mp4",
            GameDirectory = "C:\\Games\\HoN",
            VulkanLayerPath = "C:\\Layers",
            RenderWidth = 2560,
            RenderHeight = 1440,
            TargetFramerate = 60,
            PovClientIndex = 1
        };

        string cfg = AutoExecGenerator.Generate(config, "match_01.honreplay");

        Assert.Contains("Set \"host_vidDriver\" \"vid_d3d9\"", cfg);
        Assert.Contains("Set \"vid_driver\" \"d3d9\"", cfg);
        Assert.Contains("Set \"vid_resolution\" \"2560,1440\"", cfg);
        Assert.Contains("Set \"vid_fullscreen\" \"false\"", cfg);
        Assert.Contains("Set \"vid_vsync\" \"false\"", cfg);
        Assert.Contains("Set \"vid_maxFPS\" \"60\"", cfg);
        Assert.Contains("Set \"cl_maxFPS\" \"60\"", cfg);
        Assert.Contains("Set \"host_maxFPS\" \"60\"", cfg);
        Assert.Contains("Set \"host_framerate\" \"60\"", cfg);
        Assert.Contains("Set \"sys_pauseOnUnfocus\" \"false\"", cfg);
        Assert.Contains("Set \"vid_pauseOnUnfocus\" \"false\"", cfg);
        Assert.Contains("Set \"cl_pauseOnUnfocus\" \"false\"", cfg);
        Assert.Contains("Set \"sys_unfocusedSleep\" \"0\"", cfg);
    }

    [Fact]
    public void Generate_UiSuppressionCvars_ContainsAllRequiredCvars()
    {
        _output.WriteLine("[Test] Validating UI and debug suppression cvars in generated autoexec.cfg...");

        RenderJobConfiguration config = new()
        {
            ReplayFilePath = "C:\\Replays\\match_01.honreplay",
            OutputFilePath = "C:\\Videos\\match_01.mp4",
            GameDirectory = "C:\\Games\\HoN",
            VulkanLayerPath = "C:\\Layers"
        };

        string cfg = AutoExecGenerator.Generate(config, "match_01.honreplay");

        Assert.Contains("Set \"cg_drawFPS\" \"false\"", cfg);
        Assert.Contains("Set \"con_notify\" \"false\"", cfg);
        Assert.Contains("Set \"ui_showQuickStart\" \"false\"", cfg);
        Assert.Contains("Set \"cg_enableLoginAnimation\" \"false\"", cfg);
    }

    [Fact]
    public void Generate_ReplayConfiguration_EmitsReplayTargetAndClient()
    {
        _output.WriteLine("[Test] Validating replay target and client in generated autoexec.cfg...");

        RenderJobConfiguration config = new()
        {
            ReplayFilePath = "C:\\Replays\\match_01.honreplay",
            OutputFilePath = "C:\\Videos\\match_01.mp4",
            GameDirectory = "C:\\Games\\HoN",
            VulkanLayerPath = "C:\\Layers",
            PovClientIndex = 3
        };

        string cfg = AutoExecGenerator.Generate(config, "match_01.honreplay");

        Assert.Contains("Set \"replay_target\" \"replays/match_01.honreplay\"", cfg);
        Assert.Contains("Set \"replay_client\" \"3\"", cfg);
        Assert.Contains("Set \"cg_lockCamera\" \"true\"", cfg);
    }

    [Theory]
    [InlineData(true, "false")]
    [InlineData(false, "true")]
    public void Generate_CinematicUiOption_TogglesCgDraw2dProperly(bool cinematicUi, string expectedUiValue)
    {
        RenderJobConfiguration config = new()
        {
            ReplayFilePath = "C:\\Replays\\match_01.honreplay",
            OutputFilePath = "C:\\Videos\\match_01.mp4",
            GameDirectory = "C:\\Games\\HoN",
            VulkanLayerPath = "C:\\Layers",
            EnableCleanCinematicUi = cinematicUi
        };

        string cfg = AutoExecGenerator.Generate(config, "match_01.honreplay");

        Assert.Contains($"Set \"cg_draw2d\" \"{expectedUiValue}\"", cfg);
        Assert.Contains($"Set \"cg_drawHealthBars\" \"{expectedUiValue}\"", cfg);
        Assert.Contains($"Set \"cg_drawUnitFrames\" \"{expectedUiValue}\"", cfg);
        Assert.Contains($"Set \"cg_drawMinimap\" \"{expectedUiValue}\"", cfg);
        Assert.Contains($"Set \"cg_drawChat\" \"{expectedUiValue}\"", cfg);
    }
}
