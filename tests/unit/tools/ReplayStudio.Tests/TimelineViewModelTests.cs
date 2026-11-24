// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: TimelineViewModelTests.cs
// System: Replay Studio Desktop GUI Tool
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using FluentAssertions;
using ReplayStudio.GUI.Engine;
using ReplayStudio.GUI.Models;
using ReplayStudio.GUI.ViewModels;
using Xunit;

namespace ReplayStudio.Tests;

public class TimelineViewModelTests
{
    [Fact]
    public void InitialState_WithValidArguments_ConstructsWithValidDefaults()
    {
        PlaybackClock clock = new();
        ReplaySnapshotEngine engine = new();
        TimelineViewModel vm = new(clock, engine);

        vm.CurrentTimeMs.Should().Be(0);
        vm.TotalDurationMs.Should().Be(0);
        vm.IsPlaying.Should().BeFalse();
        vm.PlaybackSpeed.Should().Be(1.0f);
        vm.AvailableSpeeds.Should().Contain([0.25f, 0.5f, 1.0f, 2.0f, 4.0f, 8.0f]);
        vm.PlayPauseIcon.Should().Be("▶");
    }

    [Fact]
    public void SetMatchDuration_WhenUpdated_ConfiguresClockAndMarkersCorrectly()
    {
        PlaybackClock clock = new();
        ReplaySnapshotEngine engine = new();
        TimelineViewModel vm = new(clock, engine);

        List<CombatEventMarker> events =
        [
            new CombatEventMarker { TimestampMs = 120000, EventType = CombatEventType.FirstBlood, Description = "First Blood" }
        ];

        vm.SetMatchDuration(600000, events); // 10 minutes

        vm.TotalDurationMs.Should().Be(600000);
        vm.TotalDurationFormatted.Should().Be("10:00");
        vm.InMarkerMs.Should().Be(0);
        vm.OutMarkerMs.Should().Be(600000);
        vm.CombatEvents.Should().HaveCount(1);
    }

    [Fact]
    public void TogglePlayPause_WhenInvoked_UpdatesClockAndIsPlayingState()
    {
        PlaybackClock clock = new() { TotalDurationMs = 100000 };
        ReplaySnapshotEngine engine = new();
        TimelineViewModel vm = new(clock, engine);

        vm.TogglePlayPause();
        vm.IsPlaying.Should().BeTrue();
        vm.PlayPauseIcon.Should().Be("⏸");

        vm.TogglePlayPause();
        vm.IsPlaying.Should().BeFalse();
        vm.PlayPauseIcon.Should().Be("▶");
    }

    [Fact]
    public void StepForwardAndBackward_WhenInvoked_AdvancesClockByDiscrete50ms()
    {
        PlaybackClock clock = new() { TotalDurationMs = 100000, CurrentTimeMs = 5000 };
        ReplaySnapshotEngine engine = new();
        TimelineViewModel vm = new(clock, engine);

        vm.StepForward();
        vm.CurrentTimeMs.Should().Be(5050);

        vm.StepBackward();
        vm.CurrentTimeMs.Should().Be(5000);
    }

    [Theory]
    [InlineData(0.25f)]
    [InlineData(0.5f)]
    [InlineData(1.0f)]
    [InlineData(2.0f)]
    [InlineData(4.0f)]
    [InlineData(8.0f)]
    public void SetSpeed_WhenUpdated_AdjustsPlaybackSpeed(float speed)
    {
        PlaybackClock clock = new();
        ReplaySnapshotEngine engine = new();
        TimelineViewModel vm = new(clock, engine);

        vm.SetSpeed(speed);

        vm.PlaybackSpeed.Should().Be(speed);
        clock.SpeedMultiplier.Should().Be(speed);
    }

    [Fact]
    public void SeekAndSeekFraction_WhenInvoked_SetsClockTimeAccurately()
    {
        PlaybackClock clock = new() { TotalDurationMs = 100000 };
        ReplaySnapshotEngine engine = new();
        TimelineViewModel vm = new(clock, engine);
        vm.SetMatchDuration(100000);

        vm.Seek(25000);
        vm.CurrentTimeMs.Should().Be(25000);

        vm.SeekFraction(0.75);
        vm.CurrentTimeMs.Should().Be(75000);
    }

    [Fact]
    public void SetInAndOutMarkers_WithValidArguments_SetsValidTrimRange()
    {
        PlaybackClock clock = new() { TotalDurationMs = 200000 };
        ReplaySnapshotEngine engine = new();
        TimelineViewModel vm = new(clock, engine);
        vm.SetMatchDuration(200000);

        vm.Seek(30000);
        vm.SetInMarker();
        vm.InMarkerMs.Should().Be(30000);

        vm.Seek(150000);
        vm.SetOutMarker();
        vm.OutMarkerMs.Should().Be(150000);

        vm.ResetMarkers();
        vm.InMarkerMs.Should().Be(0);
        vm.OutMarkerMs.Should().Be(200000);
    }

    [Fact]
    public void ToggleLoop_WhenInvoked_TogglesClockLoopState()
    {
        PlaybackClock clock = new();
        ReplaySnapshotEngine engine = new();
        TimelineViewModel vm = new(clock, engine);

        vm.ToggleLoop();
        vm.IsLooping.Should().BeTrue();
        clock.LoopPlayback.Should().BeTrue();

        vm.ToggleLoop();
        vm.IsLooping.Should().BeFalse();
        clock.LoopPlayback.Should().BeFalse();
    }

    [Theory]
    [InlineData(0, true, "00:00.000")]
    [InlineData(65432, true, "01:05.432")]
    [InlineData(3661000, false, "61:01")]
    public void FormatTimestamp_WhenInvoked_FormatsCorrectly(int ms, bool includeMillis, string expected)
    {
        string formatted = TimelineViewModel.FormatTimestamp(ms, includeMillis);
        formatted.Should().Be(expected);
    }
}
