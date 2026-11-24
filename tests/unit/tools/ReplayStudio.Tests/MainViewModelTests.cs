// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: MainViewModelTests.cs
// System: Replay Studio Desktop GUI Tool
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Numerics;
using FluentAssertions;
using ReplayRenderer.Service.Replay;
using ReplayStudio.GUI.Director;
using ReplayStudio.GUI.Engine;
using ReplayStudio.GUI.Models;
using ReplayStudio.GUI.Services;
using ReplayStudio.GUI.ViewModels;
using Xunit;

namespace ReplayStudio.Tests;

public class MainViewModelTests
{
    [Fact]
    public void InitialState_OnConstruction_ConstructsWithSubViewModelsAndDefaults()
    {
        ReplaySnapshotEngine snapshotEngine = new();
        PlaybackClock clock = new();
        MapEnvironmentManager mapManager = new();
        CameraDirector cameraDirector = new();
        LightingController lightingController = new();
        ReplayStudioService service = new(snapshotEngine, clock, mapManager);

        MainViewModel vm = new(service, clock, cameraDirector, lightingController);

        vm.RosterVM.Should().NotBeNull();
        vm.TimelineVM.Should().NotBeNull();
        vm.ViewportVM.Should().NotBeNull();
        vm.ExportVM.Should().NotBeNull();

        vm.IsReplayLoaded.Should().BeFalse();
        vm.FpsReadout.Should().Be("60.0 FPS");
        vm.RenderTimeReadout.Should().Be("2.1 ms");
    }

    [Fact]
    public void LoadDemoMatch_WhenInvoked_PopulatesMetadataRosterAndTimelineCorrectly()
    {
        ReplaySnapshotEngine snapshotEngine = new();
        PlaybackClock clock = new();
        MapEnvironmentManager mapManager = new();
        CameraDirector cameraDirector = new();
        LightingController lightingController = new();
        ReplayStudioService service = new(snapshotEngine, clock, mapManager);

        MainViewModel vm = new(service, clock, cameraDirector, lightingController);

        vm.LoadDemoMatch();

        vm.IsReplayLoaded.Should().BeTrue();
        vm.MatchId.Should().Be("#135892401");
        vm.WinnerTeamName.Should().Be("Legion");
        vm.IsLegionWinner.Should().BeTrue();
        vm.IsHellbourneWinner.Should().BeFalse();
        vm.RosterVM.Players.Should().HaveCount(10);
        vm.RosterVM.LegionPlayers.Should().HaveCount(5);
        vm.RosterVM.HellbournePlayers.Should().HaveCount(5);
        vm.TimelineVM.TotalDurationMs.Should().BeGreaterThan(0);
        vm.TimelineVM.CombatEvents.Should().NotBeEmpty();
    }

    [Fact]
    public void TimelineSeeking_WhenInvoked_UpdatesRosterAndCameraTracking()
    {
        ReplaySnapshotEngine snapshotEngine = new();
        PlaybackClock clock = new();
        MapEnvironmentManager mapManager = new();
        CameraDirector cameraDirector = new();
        LightingController lightingController = new();
        ReplayStudioService service = new(snapshotEngine, clock, mapManager);

        MainViewModel vm = new(service, clock, cameraDirector, lightingController);
        vm.LoadDemoMatch();

        // Seek to 1 minute into the match
        vm.TimelineVM.Seek(60000);

        vm.TimelineVM.CurrentTimeMs.Should().Be(60000);
        vm.RosterVM.Players[0].CurrentHp.Should().BeGreaterThan(0);
        vm.ViewportVM.CameraPositionReadout.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void PovLockChanged_WhenInvoked_UpdatesViewportTarget()
    {
        ReplaySnapshotEngine snapshotEngine = new();
        PlaybackClock clock = new();
        MapEnvironmentManager mapManager = new();
        CameraDirector cameraDirector = new();
        LightingController lightingController = new();
        ReplayStudioService service = new(snapshotEngine, clock, mapManager);

        MainViewModel vm = new(service, clock, cameraDirector, lightingController);
        vm.LoadDemoMatch();

        RosterPlayerItem p3 = vm.RosterVM.Players[3];
        vm.RosterVM.LockPov(p3);

        vm.ViewportVM.LockedHeroSlot.Should().Be(3);
        vm.ViewportVM.IsPovLocked.Should().BeTrue();
        vm.ViewportVM.LockedHeroName.Should().Contain(p3.PlayerName);
    }

    [Fact]
    public void ResetCamera_WhenInvoked_ResetsCameraPosition()
    {
        ReplaySnapshotEngine snapshotEngine = new();
        PlaybackClock clock = new();
        MapEnvironmentManager mapManager = new();
        CameraDirector cameraDirector = new();
        LightingController lightingController = new();
        ReplayStudioService service = new(snapshotEngine, clock, mapManager);

        MainViewModel vm = new(service, clock, cameraDirector, lightingController);
        cameraDirector.ResetPosition(new Vector3(100, 100, 100), Vector3.Zero);

        vm.ResetCamera();

        cameraDirector.CurrentEye.Should().Be(new Vector3(0, -14, 18));
    }

    [Fact]
    public void ToggleExportModal_WhenInvoked_TogglesVisibility()
    {
        ReplaySnapshotEngine snapshotEngine = new();
        PlaybackClock clock = new();
        MapEnvironmentManager mapManager = new();
        CameraDirector cameraDirector = new();
        LightingController lightingController = new();
        ReplayStudioService service = new(snapshotEngine, clock, mapManager);

        MainViewModel vm = new(service, clock, cameraDirector, lightingController);

        vm.ExportVM.IsVisible.Should().BeFalse();
        vm.ToggleExportModal();
        vm.ExportVM.IsVisible.Should().BeTrue();
        vm.ToggleExportModal();
        vm.ExportVM.IsVisible.Should().BeFalse();
    }

    [Fact]
    public void LightingPreset_WhenInvoked_UpdatesPushConstantsAndSubproperties()
    {
        ReplaySnapshotEngine snapshotEngine = new();
        PlaybackClock clock = new();
        MapEnvironmentManager mapManager = new();
        CameraDirector cameraDirector = new();
        LightingController lightingController = new();
        ReplayStudioService service = new(snapshotEngine, clock, mapManager);

        MainViewModel vm = new(service, clock, cameraDirector, lightingController);

        vm.ViewportVM.SetLightingPreset(LightingPreset.SunsetGoldenHour);

        vm.ViewportVM.SelectedLightingPreset.Should().Be(LightingPreset.SunsetGoldenHour);
        lightingController.AmbientFactor.Should().Be(0.28f);
        lightingController.SpecularFactor.Should().Be(1.10f);
    }
}
