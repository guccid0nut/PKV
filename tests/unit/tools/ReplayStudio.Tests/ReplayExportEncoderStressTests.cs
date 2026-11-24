// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: M3Challenger2StressTests.cs
// System: Replay Studio Desktop GUI Tool - Milestone M3 Challenger 2 Stress Suite
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using FluentAssertions;
using ReplayRenderer.Service.Replay;
using ReplayStudio.GUI.Director;
using ReplayStudio.GUI.Engine;
using ReplayStudio.GUI.Models;
using ReplayStudio.GUI.Services;
using ReplayStudio.GUI.ViewModels;
using WILLOWMAKER.Core.ViewModels;
using Xunit;
using StudioMainViewModel = ReplayStudio.GUI.ViewModels.MainViewModel;

namespace ReplayStudio.Tests;

public class ReplayExportEncoderStressTests
{
    private static StudioMainViewModel CreateTestViewModel(
        out ReplayStudioService service,
        out PlaybackClock clock,
        out CameraDirector cameraDirector,
        out LightingController lightingController)
    {
        ReplaySnapshotEngine snapshotEngine = new();
        clock = new PlaybackClock();
        MapEnvironmentManager mapManager = new();
        cameraDirector = new CameraDirector();
        lightingController = new LightingController();
        service = new ReplayStudioService(snapshotEngine, clock, mapManager);

        StudioMainViewModel vm = new(service, clock, cameraDirector, lightingController);
        return vm;
    }

    // ========================================================================
    // SECTION 1: WILLOWMAKER Integration & Path Edge Cases
    // ========================================================================

    [Theory]
    [InlineData("C:\\Replays\\Match With Spaces 2026.honreplay")]
    [InlineData("C:\\Program Files (x86)\\Project Kongor\\Replays\\finals #1 [EU].honreplay")]
    [InlineData("D:\\G@m3s & R3pl@ys\\test_replay (v4.10.1).honreplay")]
    public void Willowmaker_OpenInReplayStudio_HandlesPathsWithSpacesAndSpecialChars(string replayPath)
    {
        ReplayConverterViewModel vm = new()
        {
            ReplayFilePath = replayPath
        };

        vm.OpenInReplayStudioCommand.Execute(null);

        vm.LogText.Should().Contain("[LAUNCH]");
        vm.LogText.Should().Contain(Path.GetFileName(replayPath));
        vm.StatusText.Should().NotBeNullOrWhiteSpace();

        // Simulate CLI arguments parsing on receiving side
        string[] args = ["--replay", replayPath, "--timestamp", "0", "--camera", "PlayerPov"];
        ReplayStudioCliOptions options = ReplayStudioCliOptions.Parse(args);

        options.ReplayPath.Should().Be(replayPath);
        options.InitialTimestampSec.Should().Be(0);
        options.InitialCameraMode.Should().Be(CameraMode.PlayerPov);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void Willowmaker_OpenInReplayStudio_HandlesNullOrWhitespacePaths(string emptyPath)
    {
        ReplayConverterViewModel vm = new()
        {
            ReplayFilePath = emptyPath
        };

        vm.OpenInReplayStudioCommand.Execute(null);

        vm.LogText.Should().Contain("[LAUNCH]");
        vm.StatusText.Should().NotBeNullOrWhiteSpace();

        // CLI parsing with no replay argument
        string[] args = ["--camera", "PlayerPov"];
        ReplayStudioCliOptions options = ReplayStudioCliOptions.Parse(args);

        options.ReplayPath.Should().BeNull();
        options.InitialCameraMode.Should().Be(CameraMode.PlayerPov);
    }

    [Theory]
    [InlineData("replays/match_relative.honreplay")]
    [InlineData("../shared_replays/game_02.honreplay")]
    [InlineData(".\\replays\\nested\\match_03.honreplay")]
    public void Willowmaker_OpenInReplayStudio_ResolvesRelativePaths(string relativePath)
    {
        ReplayConverterViewModel vm = new()
        {
            ReplayFilePath = relativePath
        };

        vm.OpenInReplayStudioCommand.Execute(null);

        vm.LogText.Should().Contain("[LAUNCH]");
        string expectedFileName = Path.GetFileName(relativePath);
        vm.LogText.Should().Contain(expectedFileName);

        // Verify resolved absolute path parsing
        string fullPath = Path.GetFullPath(relativePath);
        string[] args = ["--replay", fullPath, "--timestamp", "15", "--camera", "RtsIsometric"];
        ReplayStudioCliOptions options = ReplayStudioCliOptions.Parse(args);

        options.ReplayPath.Should().Be(fullPath);
        options.InitialTimestampSec.Should().Be(15);
        options.InitialCameraMode.Should().Be(CameraMode.RtsIsometric);
    }

    [Theory]
    [InlineData("\"C:\\Replays\\enclosed_quotes.honreplay\"")]
    [InlineData("'C:\\Replays\\single_quotes.honreplay'")]
    [InlineData("  \"C:\\Replays\\trimmed_quotes.honreplay\"  ")]
    public void Willowmaker_OpenInReplayStudio_StripsExtraneousQuotes(string quotedPath)
    {
        ReplayConverterViewModel vm = new()
        {
            ReplayFilePath = quotedPath
        };

        vm.OpenInReplayStudioCommand.Execute(null);

        vm.LogText.Should().Contain("[LAUNCH]");
        string cleanFileName = Path.GetFileName(quotedPath.Trim().Trim('\"', '\''));
        vm.LogText.Should().Contain(cleanFileName);
    }

    // ========================================================================
    // SECTION 2: 5-Modality Camera Transitions During Active Playback & Seeking
    // ========================================================================

    [Fact]
    public void CameraDirector_SwitchingAll5ModalitiesDuringActivePlayback_MaintainsMatrixStability()
    {
        StudioMainViewModel vm = CreateTestViewModel(out ReplayStudioService service, out PlaybackClock clock, out CameraDirector director, out LightingController lighting);
        vm.LoadDemoMatch();

        CameraMode[] modes = [
            CameraMode.PlayerPov,
            CameraMode.FirstPerson,
            CameraMode.RtsIsometric,
            CameraMode.FreeSpectator,
            CameraMode.CinematicSpline
        ];

        // Configure spline points for CinematicSpline mode
        director.Spline.AddNode(new DirectorNode { TimestampMs = 0, Position = new Vector3(0, -20, 20), LookAtTarget = Vector3.Zero, FovDegrees = 55f });
        director.Spline.AddNode(new DirectorNode { TimestampMs = 30000, Position = new Vector3(50, -50, 30), LookAtTarget = new Vector3(50, 50, 0), FovDegrees = 60f });
        director.Spline.AddNode(new DirectorNode { TimestampMs = 60000, Position = new Vector3(100, 0, 40), LookAtTarget = new Vector3(100, 100, 0), FovDegrees = 45f });

        clock.Play();
        clock.SpeedMultiplier = 2.0f;

        List<CameraMode> eventLog = [];
        vm.ViewportVM.CameraModeChanged += (m) => eventLog.Add(m);

        int timeMs = 0;
        int modeIndex = 0;

        // Step through 120 seconds of playback, switching camera mode every 5 seconds
        while (timeMs <= 120000)
        {
            clock.StepTick(2); // +100ms
            timeMs = clock.CurrentTimeMs;

            if (timeMs % 5000 == 0)
            {
                CameraMode nextMode = modes[modeIndex % modes.Length];
                vm.ViewportVM.SetCameraMode(nextMode);
                vm.ViewportVM.CurrentCameraMode.Should().Be(nextMode);
                director.Mode.Should().Be(nextMode);
                modeIndex++;
            }

            // Seek nearest hero
            Vector3 heroPos = new(timeMs * 0.05f, timeMs * 0.02f, 100f);
            float heroHeading = (timeMs * 0.1f) % 360f;

            CameraState cam = director.Update(0.016f, heroPos, heroHeading, timeMs, 16f / 9f);

            // Assert mathematical validity
            float.IsNaN(cam.Position.X).Should().BeFalse();
            float.IsNaN(cam.Position.Y).Should().BeFalse();
            float.IsNaN(cam.Position.Z).Should().BeFalse();
            float.IsNaN(cam.Target.X).Should().BeFalse();
            float.IsNaN(cam.Target.Y).Should().BeFalse();
            float.IsNaN(cam.Target.Z).Should().BeFalse();
            float.IsNaN(cam.Up.X).Should().BeFalse();
            float.IsNaN(cam.Up.Y).Should().BeFalse();
            float.IsNaN(cam.Up.Z).Should().BeFalse();

            float.IsInfinity(cam.Position.X).Should().BeFalse();
            float.IsInfinity(cam.Position.Y).Should().BeFalse();
            float.IsInfinity(cam.Position.Z).Should().BeFalse();

            cam.FovDegrees.Should().BeInRange(10f, 150f);

            // Compute View and Projection matrices
            Matrix4x4 view = cam.ViewMatrix;
            Matrix4x4 proj = cam.ProjectionMatrix;

            float detView = view.GetDeterminant();
            float.IsNaN(detView).Should().BeFalse();
            MathF.Abs(detView).Should().BeGreaterThan(1e-5f);

            float detProj = proj.GetDeterminant();
            float.IsNaN(detProj).Should().BeFalse();
        }

        eventLog.Should().HaveCountGreaterOrEqualTo(20);
    }

    [Fact]
    public void CameraDirector_RapidSeekingAcrossMatchDuration_ExponentialSmoothingRemainsStable()
    {
        StudioMainViewModel vm = CreateTestViewModel(out ReplayStudioService service, out PlaybackClock clock, out CameraDirector director, out LightingController lighting);
        vm.LoadDemoMatch();

        director.Mode = CameraMode.PlayerPov;
        director.ChaseSmoothingLambda = 6.0f;
        director.TargetSmoothingLambda = 10.0f;

        // Perform extreme pseudo-random jumps across 42 minutes
        int[] seekTargets = [0, 600000, 120000, 2400000, 30000, 1800000, 5000, 2500000];

        foreach (int targetMs in seekTargets)
        {
            vm.TimelineVM.Seek(targetMs);
            clock.CurrentTimeMs.Should().Be(targetMs);

            // Update camera with realistic hero pos
            Vector3 heroPos = new(targetMs * 0.01f, targetMs * 0.005f, 150f);
            CameraState cam = director.Update(0.016f, heroPos, 90f, targetMs, 16f / 9f);

            cam.Position.Length().Should().BeGreaterThan(0f);
            float.IsNaN(cam.Position.X).Should().BeFalse();
            float.IsNaN(cam.Target.X).Should().BeFalse();

            // Verify ViewportViewModel reactive readout
            vm.ViewportVM.CameraPositionReadout.Should().NotBeNullOrEmpty();
            vm.ViewportVM.CameraHeadingReadout.Should().Contain("Heading:");
        }
    }

    [Fact]
    public void CameraDirector_FreeSpectatorMode_GimbalLockResistanceAcrossFullPitchAndYawRange()
    {
        CameraDirector director = new();
        director.Mode = CameraMode.FreeSpectator;

        // Test extreme pitch boundaries: -89 deg to +89 deg
        float[] pitchValues = [-89.0f, -60.0f, -30.0f, 0.0f, 30.0f, 60.0f, 89.0f];
        // Full 360 yaw rotation
        for (float yaw = 0f; yaw <= 360f; yaw += 45f)
        {
            foreach (float pitch in pitchValues)
            {
                director.SetFreeSpectatorOrientation(yaw, pitch);
                director.FreeYawDeg.Should().Be(yaw);
                director.FreePitchDeg.Should().Be(pitch);

                CameraState cam = director.Update(0.016f, Vector3.Zero, 0f, 0, 16f / 9f);

                // Ensure forward direction is non-zero and normalized
                Vector3 forward = cam.Target - cam.Position;
                forward.Length().Should().BeApproximately(1.0f, 0.001f);

                // Up vector must be valid and non-zero
                cam.Up.Length().Should().BeApproximately(1.0f, 0.001f);

                // View matrix determinant must be non-zero (orthonormal basis)
                Matrix4x4 view = CameraDirector.ComputeViewMatrix(cam.Position, cam.Target, cam.Up);
                MathF.Abs(view.GetDeterminant()).Should().BeGreaterThan(0.5f);
            }
        }
    }

    // ========================================================================
    // SECTION 3: Lighting Preset Transitions & Custom Sun Angles
    // ========================================================================

    [Fact]
    public void LightingController_AllPresetTransitions_UpdatesPushConstantsAccurately()
    {
        LightingController controller = new();
        CameraDirector director = new();
        ViewportViewModel vm = new(director, controller);

        List<LightingPreset> triggeredEvents = [];
        vm.LightingPresetChanged += (p) => triggeredEvents.Add(p);

        // 1. MiddaySun
        vm.SetLightingPreset(LightingPreset.MiddaySun);
        vm.SelectedLightingPreset.Should().Be(LightingPreset.MiddaySun);
        controller.AmbientFactor.Should().Be(0.40f);
        controller.SpecularFactor.Should().Be(0.85f);
        controller.BloomThreshold.Should().Be(1.20f);
        controller.TeamAuraIntensity.Should().Be(0.50f);

        // 2. SunsetGoldenHour
        vm.SetLightingPreset(LightingPreset.SunsetGoldenHour);
        vm.SelectedLightingPreset.Should().Be(LightingPreset.SunsetGoldenHour);
        controller.AmbientFactor.Should().Be(0.28f);
        controller.SpecularFactor.Should().Be(1.10f);
        controller.BloomThreshold.Should().Be(0.95f);
        controller.TeamAuraIntensity.Should().Be(0.70f);

        // 3. NightMoonlight
        vm.SetLightingPreset(LightingPreset.NightMoonlight);
        vm.SelectedLightingPreset.Should().Be(LightingPreset.NightMoonlight);
        controller.AmbientFactor.Should().Be(0.15f);
        controller.SpecularFactor.Should().Be(0.40f);
        controller.BloomThreshold.Should().Be(1.40f);
        controller.TeamAuraIntensity.Should().Be(0.85f);

        // 4. HellbourneInferno
        vm.SetLightingPreset(LightingPreset.HellbourneInferno);
        vm.SelectedLightingPreset.Should().Be(LightingPreset.HellbourneInferno);
        controller.AmbientFactor.Should().Be(0.30f);
        controller.SpecularFactor.Should().Be(1.30f);
        controller.BloomThreshold.Should().Be(0.80f);
        controller.TeamAuraIntensity.Should().Be(1.00f);

        triggeredEvents.Should().Equal([
            LightingPreset.MiddaySun,
            LightingPreset.SunsetGoldenHour,
            LightingPreset.NightMoonlight,
            LightingPreset.HellbourneInferno
        ]);
    }

    [Fact]
    public void LightingController_CustomSunAngles_EnforcesVectorNormalizationAndBounds()
    {
        LightingController controller = new();

        // 1000 randomized spherical coordinate samples
        Random rng = new(42);
        for (int i = 0; i < 1000; i++)
        {
            float azimuth = (float)(rng.NextDouble() * 720.0 - 360.0); // -360 to +360
            float elevation = (float)(rng.NextDouble() * 180.0 - 45.0); // -45 to +135

            controller.SetSunAngles(azimuth, elevation);

            // Sun direction vector must be strictly unit length
            float length = controller.SunDirection.Length();
            length.Should().BeApproximately(1.0f, 1e-5f, because: $"SunDirection must be strictly unit normalized at Az={azimuth}, El={elevation}");

            // Z component corresponds to sin(clamped elevation between 5 and 85 deg)
            float minSin = MathF.Sin(5.0f * (MathF.PI / 180.0f));
            float maxSin = MathF.Sin(85.0f * (MathF.PI / 180.0f));
            controller.SunDirection.Z.Should().BeInRange(minSin - 1e-4f, maxSin + 1e-4f);
        }
    }

    [Fact]
    public void LightingController_PushConstantsStruct_Maintains128ByteUnmanagedLayoutAndTeamTints()
    {
        LightingController controller = new();
        controller.LoadPreset(LightingPreset.SunsetGoldenHour);

        // Struct size must be exactly 128 bytes for Vulkan uniform push constants
        int structSize = Marshal.SizeOf<LightingPushConstants>();
        structSize.Should().Be(128, because: "CVulkanDriver push constant uniform block is strictly 128 bytes");

        Matrix4x4 mvp = Matrix4x4.Identity;

        // Team 1 (Legion)
        LightingPushConstants legionConstants = controller.BuildPushConstants(mvp, 12.5f, 1, new Vector4(50f, 64f, 0.02f, 32f));
        legionConstants.TeamTint.Should().Be(controller.LegionTint);
        legionConstants.AmbientFactor.Should().Be(controller.AmbientFactor);
        legionConstants.SpecularFactor.Should().Be(controller.SpecularFactor);
        legionConstants.TimeSeconds.Should().Be(12.5f);
        legionConstants.EnvironmentalParams.X.Should().Be(50f);

        // Team 2 (Hellbourne)
        LightingPushConstants hellbourneConstants = controller.BuildPushConstants(mvp, 25.0f, 2, new Vector4(50f, 64f, 0.02f, 32f));
        hellbourneConstants.TeamTint.Should().Be(controller.HellbourneTint);

        // Neutral / Spectator
        LightingPushConstants neutralConstants = controller.BuildPushConstants(mvp, 0f, 0);
        neutralConstants.TeamTint.Should().Be(new Vector4(1f, 1f, 1f, 1f));
    }
}
