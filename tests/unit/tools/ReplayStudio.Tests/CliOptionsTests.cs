// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: CliOptionsTests.cs
// System: Replay Studio Desktop GUI Tool
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using FluentAssertions;
using ReplayStudio.GUI.Models;
using Xunit;

namespace ReplayStudio.Tests;

public class CliOptionsTests
{
    [Fact]
    public void Parse_NullOrEmptyArgs_ReturnsDefaultOptions()
    {
        ReplayStudioCliOptions nullOptions = ReplayStudioCliOptions.Parse(null);
        nullOptions.Should().NotBeNull();
        nullOptions.ReplayPath.Should().BeNull();
        nullOptions.InitialTimestampSec.Should().Be(0);
        nullOptions.InitialCameraMode.Should().Be(CameraMode.PlayerPov);
        nullOptions.AutoPlay.Should().BeFalse();
        nullOptions.PlaybackSpeed.Should().Be(1.0f);

        ReplayStudioCliOptions emptyOptions = ReplayStudioCliOptions.Parse([]);
        emptyOptions.Should().NotBeNull();
        emptyOptions.ReplayPath.Should().BeNull();
    }

    [Fact]
    public void Parse_ReplayPathAndTimestamp_SetsPropertiesCorrectly()
    {
        string[] args = ["--replay", "C:\\Replays\\grand_finals.honreplay", "--timestamp", "120"];
        ReplayStudioCliOptions options = ReplayStudioCliOptions.Parse(args);

        options.ReplayPath.Should().Be("C:\\Replays\\grand_finals.honreplay");
        options.InitialTimestampSec.Should().Be(120);
    }

    [Theory]
    [InlineData("free", CameraMode.FreeSpectator)]
    [InlineData("FreeSpectator", CameraMode.FreeSpectator)]
    [InlineData("rts", CameraMode.RtsIsometric)]
    [InlineData("RtsIsometric", CameraMode.RtsIsometric)]
    [InlineData("firstperson", CameraMode.FirstPerson)]
    [InlineData("1st", CameraMode.FirstPerson)]
    [InlineData("spline", CameraMode.CinematicSpline)]
    [InlineData("CinematicSpline", CameraMode.CinematicSpline)]
    [InlineData("chase", CameraMode.PlayerPov)]
    [InlineData("pov", CameraMode.PlayerPov)]
    [InlineData("PlayerPov", CameraMode.PlayerPov)]
    public void Parse_CameraModes_ParsesAllAliases(string modeArg, CameraMode expectedMode)
    {
        string[] args = ["--camera", modeArg];
        ReplayStudioCliOptions options = ReplayStudioCliOptions.Parse(args);

        options.InitialCameraMode.Should().Be(expectedMode);
    }

    [Fact]
    public void Parse_HeroAndAutoplayAndSpeed_ParsesCorrectly()
    {
        string[] args = ["--hero", "4", "--autoplay", "--speed", "2.0"];
        ReplayStudioCliOptions options = ReplayStudioCliOptions.Parse(args);

        options.TargetHeroSlot.Should().Be(4);
        options.AutoPlay.Should().BeTrue();
        options.PlaybackSpeed.Should().Be(2.0f);
    }

    [Fact]
    public void Parse_SpeedOutOfRange_ClampsToValidBounds()
    {
        string[] argsLow = ["--speed", "0.05"];
        ReplayStudioCliOptions optLow = ReplayStudioCliOptions.Parse(argsLow);
        optLow.PlaybackSpeed.Should().Be(0.25f);

        string[] argsHigh = ["--speed", "16.0"];
        ReplayStudioCliOptions optHigh = ReplayStudioCliOptions.Parse(argsHigh);
        optHigh.PlaybackSpeed.Should().Be(8.0f);
    }

    [Fact]
    public void Parse_NegativeTimestamp_ClampsToZero()
    {
        string[] args = ["--timestamp", "-50"];
        ReplayStudioCliOptions options = ReplayStudioCliOptions.Parse(args);

        options.InitialTimestampSec.Should().Be(0);
    }
}
