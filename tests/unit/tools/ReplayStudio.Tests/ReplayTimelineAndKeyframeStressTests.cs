// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: M3Challenger1StressTests.cs
// System: Replay Studio Desktop GUI Tool
// Policy: Strict Explicit Typing (NO 'var')
// Challenger 1: Adversarial Stress Testing of ViewModels, Timeline & CLI
// ============================================================================

using System.Diagnostics;
using System.Numerics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReplayRenderer.Service.Replay;
using ReplayStudio.GUI.Director;
using ReplayStudio.GUI.Engine;
using ReplayStudio.GUI.Models;
using ReplayStudio.GUI.Services;
using ReplayStudio.GUI.ViewModels;
using Xunit;

namespace ReplayStudio.Tests;

/// <summary>
///     Adversarial stress-testing suite for Milestone M3 (Avalonia GUI & WILLOWMAKER Integration).
///     Covers:
///     1. Rapid timeline scrubbing (10,000 rapid seek commands, boundary stepping, speed multiplier stress).
///     2. Roster POV locking (rapid cycling across 10 heroes, unassigned/negative/out-of-bounds slots, dead hero camera tracking).
///     3. CLI argument parser edge cases (missing arguments, malformed numbers, non-existent replay files, NaN/Infinity inputs).
/// </summary>
public sealed class ReplayTimelineAndKeyframeStressTests
{
    private static MainViewModel CreateTestViewModel(out ReplayStudioService service, out PlaybackClock clock, out CameraDirector director)
    {
        ReplaySnapshotEngine snapshotEngine = new(ringBufferSize: 256);
        clock = new PlaybackClock();
        MapEnvironmentManager mapManager = new();
        director = new CameraDirector();
        LightingController lightingController = new();
        service = new ReplayStudioService(snapshotEngine, clock, mapManager);

        MainViewModel mainVm = new(service, clock, director, lightingController);
        mainVm.LoadDemoMatch();
        return mainVm;
    }

    // ============================================================================
    // 1. Rapid Timeline Scrubbing (10,000 Seek Commands & Stress)
    // ============================================================================

    [Trait("Category", "Manual")]
    [Fact]
    public void RapidScrubbing_10000SeeksAcrossDiversePatterns_ExecutesSafelyWithinTimeLimit()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        MainViewModel vm = CreateTestViewModel(out ReplayStudioService _, out PlaybackClock clock, out CameraDirector _);
        int totalDurationMs = vm.TimelineVM.TotalDurationMs;
        totalDurationMs.Should().BeGreaterThan(0, "Demo match must have positive duration");

        Stopwatch sw = Stopwatch.StartNew();
        const int totalIterations = 10000;

        for (int i = 0; i < totalIterations; i++)
        {
            int pattern = i % 4;
            int targetMs;

            switch (pattern)
            {
                case 0: // Monotonic forward step
                    targetMs = (i * 250) % (totalDurationMs + 1);
                    break;
                case 1: // Monotonic backward step
                    targetMs = (totalDurationMs - ((i * 250) % (totalDurationMs + 1)));
                    break;
                case 2: // Pseudo-random jump
                    targetMs = (int)(((long)i * 1234567L + 891011L) % (totalDurationMs + 1));
                    break;
                default: // Extreme boundaries and fractions
                    targetMs = (i % 2 == 0) ? -10000 : totalDurationMs + 50000;
                    break;
            }

            vm.TimelineVM.Seek(targetMs);

            // Assert clamping invariants
            vm.TimelineVM.CurrentTimeMs.Should().BeInRange(0, totalDurationMs);
            vm.TimelineVM.CurrentTimeFormatted.Should().NotBeNullOrWhiteSpace();

            // Verify live roster update
            vm.RosterVM.Players.Should().HaveCount(10);
            for (int p = 0; p < 10; p++)
            {
                RosterPlayerItem player = vm.RosterVM.Players[p];
                player.CurrentHp.Should().BeInRange(0f, 10000f);
                float.IsNaN(player.CurrentHp).Should().BeFalse();
                float.IsInfinity(player.CurrentHp).Should().BeFalse();
            }

            // Verify viewport coordinates readout is finite and valid
            vm.ViewportVM.CameraPositionReadout.Should().NotContain("NaN");
            vm.ViewportVM.CameraPositionReadout.Should().NotContain("Infinity");
        }

        sw.Stop();
        // 10,000 seeks should finish in well under 5 seconds (sub-millisecond per seek)
        sw.ElapsedMilliseconds.Should().BeLessThan(5000, "10,000 timeline seeks must execute under 5,000ms");
    }

    [Fact]
    public void StepTick_StartAndEndMatchBoundaries_MaintainsClampedValuesWithoutExceptions()
    {
        MainViewModel vm = CreateTestViewModel(out ReplayStudioService _, out PlaybackClock clock, out CameraDirector _);
        int totalDurationMs = vm.TimelineVM.TotalDurationMs;

        // 1. Boundary at t = 0
        vm.TimelineVM.Seek(0);
        vm.TimelineVM.CurrentTimeMs.Should().Be(0);

        for (int i = 0; i < 50; i++)
        {
            vm.TimelineVM.StepBackward();
            vm.TimelineVM.CurrentTimeMs.Should().Be(0, "Stepping backward at 0ms must clamp to 0");
        }

        // 2. Boundary at t = TotalDurationMs
        vm.TimelineVM.Seek(totalDurationMs);
        vm.TimelineVM.CurrentTimeMs.Should().Be(totalDurationMs);

        for (int i = 0; i < 50; i++)
        {
            vm.TimelineVM.StepForward();
            vm.TimelineVM.CurrentTimeMs.Should().Be(totalDurationMs, "Stepping forward at match end must clamp to TotalDurationMs");
        }

        // 3. Step while playing transitions to paused
        clock.Play();
        clock.IsPlaying.Should().BeTrue();
        vm.TimelineVM.StepForward();
        clock.IsPaused.Should().BeTrue("Stepping while playing must transition clock to paused");
    }

    [Fact]
    public void SpeedMultiplier_RapidTransitionsAndHighFrequencyClockUpdates_NoCumulativeDrift()
    {
        MainViewModel vm = CreateTestViewModel(out ReplayStudioService _, out PlaybackClock clock, out CameraDirector _);
        float[] validSpeeds = [0.25f, 0.5f, 1.0f, 2.0f, 4.0f, 8.0f];

        // Rapid speed switching
        for (int i = 0; i < 1000; i++)
        {
            float speed = validSpeeds[i % validSpeeds.Length];
            vm.TimelineVM.SetSpeed(speed);
            vm.TimelineVM.PlaybackSpeed.Should().Be(speed);
            clock.SpeedMultiplier.Should().Be(speed);
        }

        // Out-of-bounds speed clamping
        vm.TimelineVM.SetSpeed(-10f);
        clock.SpeedMultiplier.Should().Be(0.25f, "Negative speeds must clamp to 0.25x minimum");

        vm.TimelineVM.SetSpeed(100f);
        clock.SpeedMultiplier.Should().Be(8.0f, "Excessive speeds must clamp to 8.0x maximum");

        // High frequency clock updates at 8.0x speed
        vm.TimelineVM.Seek(10000);
        vm.TimelineVM.SetSpeed(8.0f);
        clock.Play();

        for (int i = 0; i < 500; i++)
        {
            clock.Update();
        }

        clock.CurrentTimeMs.Should().BeGreaterThanOrEqualTo(10000);
        clock.CurrentTimeMs.Should().BeLessThanOrEqualTo(vm.TimelineVM.TotalDurationMs);
    }

    // ============================================================================
    // 2. Roster POV Locking & Camera Modality Stress
    // ============================================================================

    [Fact]
    public void RosterPovLocking_RapidCycling10000Times_MaintainsStrictSingleLock()
    {
        MainViewModel vm = CreateTestViewModel(out ReplayStudioService _, out PlaybackClock _, out CameraDirector director);

        for (int iter = 0; iter < 1000; iter++)
        {
            for (int slot = 0; slot < 10; slot++)
            {
                RosterPlayerItem targetPlayer = vm.RosterVM.Players[slot];
                vm.RosterVM.LockPov(targetPlayer);

                // Assert invariant: exactly 1 player is locked
                vm.RosterVM.LockedHeroSlot.Should().Be(targetPlayer.ClientNum);
                vm.RosterVM.LockedPovPlayer.Should().Be(targetPlayer);
                targetPlayer.IsPovLocked.Should().BeTrue();

                for (int other = 0; other < 10; other++)
                {
                    if (other != slot)
                    {
                        vm.RosterVM.Players[other].IsPovLocked.Should().BeFalse();
                    }
                }

                vm.ViewportVM.LockedHeroSlot.Should().Be(targetPlayer.ClientNum);
                vm.ViewportVM.IsPovLocked.Should().BeTrue();
                vm.ViewportVM.LockedHeroName.Should().Contain(targetPlayer.PlayerName);
            }
        }
    }

    [Fact]
    public void RosterPovLocking_UnassignedAndOutOfBoundsSlots_HandledGracefullyWithoutCrashing()
    {
        MainViewModel vm = CreateTestViewModel(out ReplayStudioService _, out PlaybackClock _, out CameraDirector director);

        // 1. Lock to null
        vm.RosterVM.LockPov(null);
        vm.RosterVM.LockedHeroSlot.Should().Be(-1);
        vm.RosterVM.LockedPovPlayer.Should().BeNull();
        vm.ViewportVM.IsPovLocked.Should().BeFalse();
        vm.ViewportVM.LockedHeroName.Should().Be("None");

        // 2. Lock to unassigned player item (out of bounds client index)
        RosterPlayerItem invalidPlayer = new()
        {
            ClientNum = 999,
            Team = 1,
            PlayerName = "GhostPlayer",
            HeroName = "Hero_Null"
        };

        vm.RosterVM.LockPov(invalidPlayer);
        vm.RosterVM.LockedHeroSlot.Should().Be(999);
        vm.ViewportVM.LockedHeroSlot.Should().Be(999);
        // MainViewModel guards OnPovLockChanged: slot >= 0 && slot < RosterVM.Players.Count
        // so LockedHeroName should be "None"
        vm.ViewportVM.LockedHeroName.Should().Be("None");

        // Seek while invalid slot is locked
        vm.TimelineVM.Seek(50000);
        vm.ViewportVM.CameraPositionReadout.Should().NotContain("NaN");

        // 3. Negative slot
        vm.ViewportVM.FocusHero(-5);
        vm.ViewportVM.IsPovLocked.Should().BeFalse();
        vm.TimelineVM.Seek(60000);
        vm.ViewportVM.CameraPositionReadout.Should().NotContain("NaN");
    }

    [Fact]
    public void RosterPovLocking_DeadHeroCameraFocus_GracefulHandlingAndMatrixStability()
    {
        MainViewModel vm = CreateTestViewModel(out ReplayStudioService service, out PlaybackClock clock, out CameraDirector director);

        // Create keyframes where hero 2 (WitchSlayer) dies and stays dead
        List<GameStateSnapshot> snapshots = [];
        for (int sec = 0; sec <= 120; sec += 10)
        {
            GameStateSnapshot snap = new()
            {
                TimestampMs = sec * 1000,
                ScoreLegion = 5,
                ScoreHellbourne = 8,
                IsKeyframe = true
            };

            for (int h = 0; h < 10; h++)
            {
                bool isDead = (h == 2 && sec >= 30 && sec <= 90);
                snap.Heroes[h] = new HeroSnapshot
                {
                    ClientIndex = h,
                    Team = h < 5 ? 1 : 2,
                    PlayerName = $"Player_{h}",
                    HeroName = $"Hero_{h}",
                    Health = isDead ? 0f : 1200f,
                    MaxHealth = 1200f,
                    Mana = isDead ? 0f : 600f,
                    MaxMana = 600f,
                    IsAlive = !isDead,
                    Position = isDead ? new Vector3(8500f, 6200f, 150f) : new Vector3(h * 1000f, h * 500f, 100f),
                    HeadingAngle = 45f + h * 30f,
                    RespawnTimerMs = isDead ? (90 - sec) * 1000 : 0
                };
            }
            snapshots.Add(snap);
        }

        service.SnapshotEngine.LoadKeyframes(snapshots, 120000);
        vm.TimelineVM.SetMatchDuration(120000);

        // Lock POV to hero 2
        RosterPlayerItem p2 = vm.RosterVM.Players[2];
        vm.RosterVM.LockPov(p2);

        // Scrub across alive -> dying -> dead -> respawned
        for (int ms = 0; ms <= 120000; ms += 2000)
        {
            vm.TimelineVM.Seek(ms);

            // Assert Hero 2 state
            if (ms >= 30000 && ms <= 90000)
            {
                p2.IsAlive.Should().BeFalse();
                p2.CurrentHp.Should().Be(0f);
            }
            else if (ms >= 100000)
            {
                p2.IsAlive.Should().BeTrue();
            }

            // Assert Camera Matrix validity (no NaNs or Infinities)
            CameraState cam = director.Update(0.016f, director.CurrentEye, 45f, ms, 16f / 9f);
            float.IsNaN(cam.Position.X).Should().BeFalse();
            float.IsNaN(cam.Position.Y).Should().BeFalse();
            float.IsNaN(cam.Position.Z).Should().BeFalse();
            float.IsNaN(cam.Target.X).Should().BeFalse();
            float.IsNaN(cam.Target.Y).Should().BeFalse();
            float.IsNaN(cam.Target.Z).Should().BeFalse();

            Matrix4x4 viewMat = cam.ViewMatrix;
            Matrix4x4 projMat = cam.ProjectionMatrix;

            float.IsNaN(viewMat.M11).Should().BeFalse();
            float.IsNaN(projMat.M11).Should().BeFalse();
            viewMat.GetDeterminant().Should().NotBe(0f);
            projMat.GetDeterminant().Should().NotBe(0f);
        }
    }

    [Theory]
    [InlineData(CameraMode.PlayerPov)]
    [InlineData(CameraMode.FirstPerson)]
    [InlineData(CameraMode.RtsIsometric)]
    [InlineData(CameraMode.FreeSpectator)]
    [InlineData(CameraMode.CinematicSpline)]
    public void CameraDirector_AllModalitiesUnderRapidPovSwitching_PreservesMatrixInvariants(CameraMode mode)
    {
        MainViewModel vm = CreateTestViewModel(out ReplayStudioService _, out PlaybackClock _, out CameraDirector director);
        vm.ViewportVM.SetCameraMode(mode);

        for (int i = 0; i < 50; i++)
        {
            int slot = i % 10;
            vm.RosterVM.LockPov(vm.RosterVM.Players[slot]);
            vm.TimelineVM.Seek(i * 5000);

            CameraState state = director.Update(0.016f, new Vector3(i * 10f, i * 5f, 50f), i * 15f, i * 5000, 16f / 9f);

            float.IsNaN(state.Position.X).Should().BeFalse();
            float.IsNaN(state.Target.X).Should().BeFalse();
            float.IsNaN(state.Up.Z).Should().BeFalse();
            state.FovDegrees.Should().BeInRange(10f, 150f);
            state.ProjectionMatrix.GetDeterminant().Should().NotBe(0f);
        }
    }

    // ============================================================================
    // 3. CLI Argument Parser Edge Cases
    // ============================================================================

    [Fact]
    public void CliOptions_MissingArgumentValues_DoesNotThrowIndexOutOfRange()
    {
        // Trailing flags without corresponding value parameter
        string[][] testCases =
        [
            ["--replay"],
            ["--timestamp"],
            ["--camera"],
            ["--hero"],
            ["--speed"],
            ["--replay", "valid.honreplay", "--timestamp"],
            ["--camera", "rts", "--hero"],
            ["--autoplay", "--speed"]
        ];

        foreach (string[] args in testCases)
        {
            ReplayStudioCliOptions options = ReplayStudioCliOptions.Parse(args);
            options.Should().NotBeNull();
            options.PlaybackSpeed.Should().BeInRange(0.25f, 8.0f);
        }
    }

    [Fact]
    public void CliOptions_InvalidAndMalformedNumericValues_HandlesSafely()
    {
        string[] args =
        [
            "--timestamp", "not_a_number",
            "--hero", "abc_hero",
            "--speed", "unparseable_speed"
        ];

        ReplayStudioCliOptions options = ReplayStudioCliOptions.Parse(args);
        options.InitialTimestampSec.Should().Be(0);
        options.TargetHeroSlot.Should().BeNull();
        options.PlaybackSpeed.Should().Be(1.0f);
    }

    [Fact]
    public void CliOptions_OverflowAndNegativeNumbers_ClampsAndHandlesGracefully()
    {
        // Timestamp overflow and negative
        string[] args1 = ["--timestamp", "999999999999999999999999999999999999999999"];
        ReplayStudioCliOptions opt1 = ReplayStudioCliOptions.Parse(args1);
        opt1.InitialTimestampSec.Should().Be(0, "int.TryParse fails on overflow and leaves 0");

        string[] args2 = ["--timestamp", "-12345"];
        ReplayStudioCliOptions opt2 = ReplayStudioCliOptions.Parse(args2);
        opt2.InitialTimestampSec.Should().Be(0, "Negative timestamps must clamp to 0");

        // Speed overflow and underflow
        string[] args3 = ["--speed", "999999.0"];
        ReplayStudioCliOptions opt3 = ReplayStudioCliOptions.Parse(args3);
        opt3.PlaybackSpeed.Should().Be(8.0f, "Speed > 8.0 must clamp to 8.0f");

        string[] args4 = ["--speed", "-50.0"];
        ReplayStudioCliOptions opt4 = ReplayStudioCliOptions.Parse(args4);
        opt4.PlaybackSpeed.Should().Be(0.25f, "Speed < 0.25 must clamp to 0.25f");
    }

    [Fact]
    public async Task MainViewModel_NonExistentAndMalformedReplayPaths_HandledGracefully()
    {
        MainViewModel vm = CreateTestViewModel(out ReplayStudioService _, out PlaybackClock _, out CameraDirector _);

        // 1. Non-existent file path
        await vm.LoadReplayFileAsync("C:\\NonExistentPath\\NonExistentMatch.honreplay");
        vm.IsLoading.Should().BeFalse();
        vm.StatusMessage.Should().ContainAny("Failed", "Error");

        // 2. Empty / whitespace path
        await vm.LoadReplayFileAsync("");
        vm.IsLoading.Should().BeFalse();

        await vm.LoadReplayFileAsync("   ");
        vm.IsLoading.Should().BeFalse();
    }
}

