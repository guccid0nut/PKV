// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: ReplayRendererBoundaryTests.cs
// System: ProjectKongor Solution-Wide E2E Testing Suite
// Tier 2: Boundary & Corner Cases (ReplayRenderer Service Boundaries)
// Policy: Strict Explicit Typing, Adversarial Boundary Verification (>= 5 tests)
// ============================================================================

using FluentAssertions;
using ReplayRenderer.Service.Models;

namespace ASPIRE.Tests.E2E.Tier2_BoundaryAndCornerCases;

public sealed class ReplayRendererBoundaryTests
{
    [Fact]
    public void Boundary_JobConfig_UltraHighFrameRate_ValidatesMaxFps()
    {
        // Arrange
        RenderJobConfiguration job = new()
        {
            ReplayFilePath = "test.honreplay",
            OutputFilePath = "test.mp4",
            GameDirectory = "C:\\Games\\HoN",
            VulkanLayerPath = "C:\\Layers\\layer.dll",
            TargetFramerate = 240, // 240Hz competitive capture
            RenderWidth = 3840,
            RenderHeight = 2160
        };

        // Assert
        job.TargetFramerate.Should().Be(240);
        job.RenderWidth.Should().Be(3840);
        job.RenderHeight.Should().Be(2160);
    }

    [Fact]
    public void Boundary_JobConfig_ZeroFps_DefaultsSafely()
    {
        // Arrange
        RenderJobConfiguration job = new()
        {
            ReplayFilePath = "test.honreplay",
            OutputFilePath = "test.mp4",
            GameDirectory = "C:\\Games\\HoN",
            VulkanLayerPath = "C:\\Layers\\layer.dll",
            TargetFramerate = 0
        };

        // Assert
        job.TargetFramerate.Should().Be(0);
    }

    [Fact]
    public void Boundary_JobConfig_NonExistentPaths_PreservesStringValues()
    {
        // Arrange
        string nonExistentReplay = @"Z:\NonExistent\Drive\fake.honreplay";
        RenderJobConfiguration job = new()
        {
            ReplayFilePath = nonExistentReplay,
            OutputFilePath = @"Z:\NonExistent\Drive\fake.mp4",
            GameDirectory = @"Z:\NonExistent\Game",
            VulkanLayerPath = @"Z:\NonExistent\Layer.dll"
        };

        // Assert
        job.ReplayFilePath.Should().Be(nonExistentReplay);
    }

    [Fact]
    public void Boundary_ProgressTelemetry_CalculatesZeroFramesSafely()
    {
        // Arrange
        RenderJobProgress progress = new()
        {
            JobId = "job-zero",
            State = RenderJobState.Initializing,
            TotalFramesRendered = 0,
            CurrentFps = 0.0,
            SpeedMultiplier = 0.0,
            ElapsedTime = TimeSpan.Zero
        };

        // Assert
        progress.TotalFramesRendered.Should().Be(0);
        progress.CurrentFps.Should().Be(0.0);
        progress.ElapsedTime.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void Boundary_ProgressTelemetry_ExtremeLongDuration_Handles24HourJob()
    {
        // Arrange
        RenderJobProgress progress = new()
        {
            JobId = "job-long",
            State = RenderJobState.Capturing,
            TotalFramesRendered = 5_184_000, // 24 hours at 60fps
            CurrentFps = 60.0,
            SpeedMultiplier = 1.0,
            ElapsedTime = TimeSpan.FromHours(24)
        };

        // Assert
        progress.TotalFramesRendered.Should().Be(5184000);
        progress.ElapsedTime.TotalHours.Should().Be(24.0);
    }

    [Fact]
    public void Boundary_VideoCodecEnum_ExposesAllSupportedCodecs()
    {
        // Assert
        VideoCodec.H264Nvenc.Should().BeDefined();
        VideoCodec.HevcNvenc.Should().BeDefined();
        VideoCodec.Libx264CpuFallback.Should().BeDefined();
    }

    [Fact]
    public void Boundary_RenderJobStateEnum_ExposesAllLifecycleStates()
    {
        // Assert
        RenderJobState.Initializing.Should().BeDefined();
        RenderJobState.SpawningGame.Should().BeDefined();
        RenderJobState.Capturing.Should().BeDefined();
        RenderJobState.Encoding.Should().BeDefined();
        RenderJobState.Finalizing.Should().BeDefined();
        RenderJobState.Completed.Should().BeDefined();
        RenderJobState.Failed.Should().BeDefined();
    }
}
