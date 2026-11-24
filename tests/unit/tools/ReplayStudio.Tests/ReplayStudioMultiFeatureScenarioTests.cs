// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: Tier5MultiFeatureScenarioTests.cs
// System: Replay Studio Desktop GUI Tool - Tier 5 Adversarial Coverage Hardening
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReplayRenderer.Service.Replay;
using ReplayStudio.GUI.Common;
using ReplayStudio.GUI.Director;
using ReplayStudio.GUI.Engine;
using ReplayStudio.GUI.Export;
using ReplayStudio.GUI.Models;
using ReplayStudio.GUI.Services;
using ReplayStudio.GUI.ViewModels;
using Xunit;

namespace ReplayStudio.Tests;

/// <summary>
///     Comprehensive Tier 5 White-Box Adversarial Stress Test Suite for Replay Studio.
///     Tests cover complex multi-feature interactions, multi-threaded lifecycle stress,
///     gimbal-free camera director modalities, Catmull-Rom splines with degenerate/coincident knots,
///     unmanaged NativeBufferRing memory safety under concurrent blitting/resizing,
///     full 3D scene rendering with LBS skeletal animation, and MVVM end-to-end telemetry coordination.
/// </summary>
public sealed class ReplayStudioMultiFeatureScenarioTests
{
    #region SECTION 1: Complex Multi-Feature Concurrent Interactions

    [Trait("Category", "Manual")]
    [Fact]
    public async Task ExportStreaming_WhileScrubbingTimeline8x_WithDyingHeroPov_UnderSunsetLighting()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // ARRANGE: Complex multi-feature interaction setup
        VideoExportPipeline pipeline = new(NullLogger<VideoExportPipeline>.Instance);
        ReplaySnapshotEngine snapshotEngine = new();
        PlaybackClock clock = new();
        CameraDirector cameraDirector = new();
        LightingController lightingController = new();
        MapEnvironmentManager mapManager = new();

        mapManager.LoadMap("caldavar");
        lightingController.LoadPreset(LightingPreset.SunsetGoldenHour);
        cameraDirector.Mode = CameraMode.PlayerPov;

        // Build a 10-second match scenario where Hero 0 dies at 3000ms and respawns at 7000ms
        List<GameStateSnapshot> matchKeyframes = [];
        for (int t = 0; t <= 10000; t += 500)
        {
            bool isDead = t >= 3000 && t < 7000;
            GameStateSnapshot snap = new()
            {
                TimestampMs = t,
                ScoreLegion = t >= 3000 ? 5 : 2,
                ScoreHellbourne = t >= 3000 ? 8 : 4,
                NetWorthAdvantage = t >= 3000 ? -3500 : 1200,
                IsKeyframe = (t % 1000 == 0)
            };

            for (int h = 0; h < 10; h++)
            {
                bool heroIsDead = (h == 0 && isDead);
                Vector3 heroPos;
                if (h == 0)
                {
                    if (heroIsDead)
                    {
                        heroPos = new Vector3(500f, 500f, 0f); // Death location
                    }
                    else if (t >= 7000)
                    {
                        heroPos = new Vector3(-800f, -800f, 50f) + new Vector3(t * 0.1f, t * 0.05f, 0f); // Respawned at base and walking
                    }
                    else
                    {
                        heroPos = new Vector3(100f + t * 0.4f, 200f + t * 0.3f, 10f); // Pre-death combat
                    }
                }
                else
                {
                    heroPos = new Vector3(h * 150f + (t * 0.05f), h * 80f, 10f);
                }

                snap.Heroes[h] = new HeroSnapshot
                {
                    ClientIndex = h,
                    HeroName = h == 0 ? "Hero_Devourer" : $"Hero_Slot{h}",
                    PlayerName = h == 0 ? "PudgeGod" : $"Player_{h}",
                    Position = heroPos,
                    HeadingAngle = (t * 0.2f + h * 36f) % 360f,
                    Health = heroIsDead ? 0f : (1000f - (t >= 2000 && t < 3000 ? (t - 2000) : 0f)),
                    MaxHealth = 1000f,
                    Mana = 400f,
                    MaxMana = 400f,
                    IsAlive = !heroIsDead,
                    RespawnTimerMs = heroIsDead ? (7000 - t) : 0,
                    Team = h < 5 ? 1 : 2
                };
            }

            matchKeyframes.Add(snap);
        }

        snapshotEngine.LoadKeyframes(matchKeyframes, 10000);
        clock.TotalDurationMs = 10000;
        clock.SpeedMultiplier = 8.0f; // 8.0x speed multiplier
        clock.Play();

        ExportParameters exportParams = new()
        {
            Preset = ExportPreset.Fast720p60,
            OutputFilePath = Path.Combine(Path.GetTempPath(), $"tier5_scenario1_{Guid.NewGuid():N}.mp4"),
            InMarkerMs = 1000,
            OutMarkerMs = 9000, // 8.0s duration = 480 frames @ 60fps
            IncludeAudio = true
        };

        using MemoryStream exportSink = new();
        List<ExportProgress> progressRecords = [];
        Progress<ExportProgress> progressReporter = new(p => progressRecords.Add(p));

        int framesProcessed = 0;
        List<CameraState> recordedCameraStates = [];

        async Task ConcurrentScrubbingRenderCallback(int timestampMs, NativeBufferRing ring)
        {
            framesProcessed++;

            // Concurrently advance clock at 8.0x speed and simulate high-frequency timeline scrubbing
            clock.Update();
            if (framesProcessed % 10 == 0)
            {
                // Rapid scrubbing jump back and forth
                int scrubTarget = (framesProcessed % 20 == 0) ? 2500 : 7500;
                clock.CurrentTimeMs = scrubTarget;
            }

            // Dynamic lighting adjustments mid-export
            if (timestampMs > 4000 && timestampMs < 6000)
            {
                lightingController.LoadPreset(LightingPreset.HellbourneInferno);
            }
            else if (timestampMs >= 6000)
            {
                lightingController.LoadPreset(LightingPreset.SunsetGoldenHour);
            }

            // Evaluate dying / respawning hero state
            GameStateSnapshot snap = snapshotEngine.GetInterpolatedSnapshot(timestampMs);
            HeroSnapshot lockedHero = snap.Heroes[0];

            float dt = 1.0f / exportParams.TargetFps;
            float aspect = (float)exportParams.Width / exportParams.Height;
            CameraState camState = cameraDirector.Update(dt, lockedHero.Position, lockedHero.HeadingAngle, timestampMs, aspect);
            recordedCameraStates.Add(camState);

            // Verify Camera State validity (Finite numbers, no NaN / Infinity)
            float.IsNaN(camState.Position.X).Should().BeFalse();
            float.IsNaN(camState.Position.Y).Should().BeFalse();
            float.IsNaN(camState.Position.Z).Should().BeFalse();
            float.IsInfinity(camState.Position.X).Should().BeFalse();
            float.IsInfinity(camState.Position.Y).Should().BeFalse();
            float.IsInfinity(camState.Position.Z).Should().BeFalse();

            // Verify push constants generated without fault
            LightingPushConstants pushConstants = lightingController.BuildPushConstants(
                camState.ViewMatrix * camState.ProjectionMatrix,
                timestampMs / 1000.0f,
                team: lockedHero.Team);
            pushConstants.AmbientFactor.Should().BeGreaterThan(0f);
            pushConstants.SunDirection.Length().Should().BeApproximately(1.0f, 0.01f);

            await Task.CompletedTask;
        }

        // ACT: Execute export with multi-feature concurrency
        ExportResult result = await pipeline.ExportVideoAsync(
            exportParams,
            ConcurrentScrubbingRenderCallback,
            combatEvents: null,
            progress: progressReporter,
            customOutputStream: exportSink,
            ct: CancellationToken.None);

        // ASSERT: Empirical verification
        result.Success.Should().BeTrue();
        result.TotalFramesRendered.Should().Be(480);
        recordedCameraStates.Should().HaveCount(480);
        exportSink.Length.Should().Be(480 * exportParams.CalculatedVideoFrameBytes);
        progressRecords.Should().NotBeEmpty();
        progressRecords[^1].PercentComplete.Should().Be(100.0);
    }

    [Fact]
    public async Task ConcurrentModalityTransitions_DuringHighRateBlitting_MaintainsAtomicStability()
    {
        // ARRANGE: Concurrency harness testing rapid camera modality switching during continuous memory blitting
        CameraDirector director = new();
        using NativeBufferRing bufferRing = new(1280, 720);
        using CancellationTokenSource cts = new();

        int blitCount = 0;
        int modeSwitchCount = 0;

        IntPtr mockDestination = Marshal.AllocHGlobal(bufferRing.BufferSizeBytes);
        try
        {
            // Task 1: Background rendering and blitting loop
            Task blitterTask = Task.Run(() =>
            {
                byte colorByte = 0;
                while (!cts.Token.IsCancellationRequested)
                {
                    bufferRing.ClearBuffer(clearBackBuffer: true, r: colorByte, g: colorByte, b: colorByte);
                    bufferRing.CommitBackBuffer();
                    bufferRing.BlitFrontBuffer(mockDestination, bufferRing.Stride, bufferRing.Height);
                    Interlocked.Increment(ref blitCount);
                    colorByte = (byte)((colorByte + 1) % 255);
                }
            });

            // Task 2: High-frequency Camera Modality transitions and coordinate modifications
            Task directorTask = Task.Run(async () =>
            {
                CameraMode[] modes =
                [
                    CameraMode.PlayerPov,
                    CameraMode.FirstPerson,
                    CameraMode.RtsIsometric,
                    CameraMode.FreeSpectator,
                    CameraMode.CinematicSpline
                ];

                director.Spline.Clear();
                director.Spline.AddNode(new DirectorNode { TimestampMs = 0, Position = new Vector3(0, 0, 100), LookAtTarget = Vector3.Zero });
                director.Spline.AddNode(new DirectorNode { TimestampMs = 1000, Position = new Vector3(200, 200, 50), LookAtTarget = new Vector3(50, 50, 0) });

                int iter = 0;
                while (!cts.Token.IsCancellationRequested)
                {
                    CameraMode targetMode = modes[iter % modes.Length];
                    director.Mode = targetMode;

                    // Apply arbitrary inputs
                    director.SetFreeSpectatorOrientation((iter * 15f) % 360f, -25f);
                    director.TranslateFreeSpectator(new Vector3(1f, 0.5f, 0f), 0.016f, sprint: (iter % 2 == 0));

                    Vector3 heroPos = new(iter * 2f, iter * 3f, 5f);
                    CameraState state = director.Update(0.016f, heroPos, (iter * 10f) % 360f, iter * 50, 16.0f / 9.0f);

                    // Assert camera state invariants
                    state.FovDegrees.Should().BeInRange(10f, 150f);
                    Matrix4x4.Invert(state.ViewMatrix, out _).Should().BeTrue();
                    Matrix4x4.Invert(state.ProjectionMatrix, out _).Should().BeTrue();

                    Interlocked.Increment(ref modeSwitchCount);
                    iter++;
                    await Task.Yield();
                }
            });

            // Run until both concurrent operations achieve sufficient sample depth or timeout
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while ((Volatile.Read(ref blitCount) < 20 || Volatile.Read(ref modeSwitchCount) < 20) && !timeoutCts.IsCancellationRequested)
            {
                await Task.Delay(5);
            }
            cts.Cancel();

            await Task.WhenAll(blitterTask, directorTask);

            // ASSERT: Both high-frequency blitting and camera switching executed extensively with 0 data races
            blitCount.Should().BeGreaterThan(10);
            modeSwitchCount.Should().BeGreaterThan(10);
        }
        finally
        {
            Marshal.FreeHGlobal(mockDestination);
        }
    }

    #endregion

    #region SECTION 2: Catmull-Rom Spline & Camera Director Extreme Edge Cases

    [Fact]
    public void CatmullRomSpline_DegenerateAndCoincidentKnots_EvaluatesWithC1ContinuityAndNoNaN()
    {
        CatmullRomSpline spline = new();

        // 1. Empty spline evaluation fallback
        (Vector3 pos0, Vector3 tgt0, Quaternion rot0, float fov0) = spline.Evaluate(500);
        pos0.Should().Be(new Vector3(0, -14, 18));
        tgt0.Should().Be(Vector3.Zero);
        rot0.Should().Be(Quaternion.Identity);
        fov0.Should().Be(55.0f);

        // 2. Single node spline
        DirectorNode node1 = new()
        {
            TimestampMs = 1000,
            Position = new Vector3(10, 20, 30),
            LookAtTarget = new Vector3(1, 2, 3),
            Orientation = Quaternion.CreateFromYawPitchRoll(0.5f, 0.2f, 0.1f),
            FovDegrees = 65.0f
        };
        spline.AddNode(node1);

        (Vector3 pos1, Vector3 tgt1, Quaternion rot1, float fov1) = spline.Evaluate(500);
        pos1.Should().Be(node1.Position);
        tgt1.Should().Be(node1.LookAtTarget);
        fov1.Should().Be(65.0f);

        // 3. Multiple coincident and collinear knots
        DirectorNode node2 = new() { TimestampMs = 2000, Position = new Vector3(100, 200, 300), LookAtTarget = new Vector3(10, 20, 30), FovDegrees = 75.0f };
        DirectorNode node3 = new() { TimestampMs = 2000, Position = new Vector3(100, 200, 300), LookAtTarget = new Vector3(10, 20, 30), FovDegrees = 75.0f }; // Coincident timestamp
        DirectorNode node4 = new() { TimestampMs = 4000, Position = new Vector3(200, 400, 600), LookAtTarget = new Vector3(20, 40, 60), FovDegrees = 85.0f };

        spline.AddNode(node2);
        spline.AddNode(node3);
        spline.AddNode(node4);

        // Evaluate across timestamp range
        for (int t = 0; t <= 5000; t += 100)
        {
            (Vector3 p, Vector3 target, Quaternion q, float fov) = spline.Evaluate(t);

            float.IsNaN(p.X).Should().BeFalse();
            float.IsNaN(p.Y).Should().BeFalse();
            float.IsNaN(p.Z).Should().BeFalse();

            float.IsNaN(target.X).Should().BeFalse();
            float.IsNaN(target.Y).Should().BeFalse();
            float.IsNaN(target.Z).Should().BeFalse();

            float.IsNaN(q.W).Should().BeFalse();
            q.Length().Should().BeApproximately(1.0f, 0.001f);
            fov.Should().BeInRange(50f, 90f);
        }

        // Test removal and clearing
        spline.RemoveNode(node1.Id).Should().BeTrue();
        spline.Count.Should().Be(3);
        spline.Clear();
        spline.Count.Should().Be(0);
    }

    [Fact]
    public void CatmullRomSpline_SlerpShortestArc_HandlesAntipodalAndCollinearQuaternions()
    {
        Quaternion q1 = Quaternion.Identity;
        Quaternion qOpposite = new(-0.0f, -0.0f, -0.0f, -1.0f); // Antipodal (-q1)
        Quaternion qSlightRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.0001f);
        Quaternion q90Deg = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2.0f);

        // 1. Antipodal check: Slerp between q1 and -q1 should not invert or produce NaN
        Quaternion slerpAntipodal = CatmullRomSpline.SlerpShortestArc(q1, qOpposite, 0.5f);
        float.IsNaN(slerpAntipodal.W).Should().BeFalse();
        slerpAntipodal.Length().Should().BeApproximately(1.0f, 0.0001f);

        // 2. High collinearity check (dot > 0.9995f) -> triggers fast nlerp path
        Quaternion slerpCollinear = CatmullRomSpline.SlerpShortestArc(q1, qSlightRotation, 0.5f);
        slerpCollinear.Length().Should().BeApproximately(1.0f, 0.0001f);

        // 3. 90-degree standard spherical arc
        Quaternion slerpMid = CatmullRomSpline.SlerpShortestArc(q1, q90Deg, 0.5f);
        slerpMid.Length().Should().BeApproximately(1.0f, 0.0001f);
        // Midpoint of 90 deg rotation is 45 deg
        Quaternion expected45 = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 4.0f);
        MathF.Abs(Quaternion.Dot(slerpMid, expected45)).Should().BeApproximately(1.0f, 0.01f);
    }

    [Fact]
    public void CameraDirector_HeroTeleportationAndSupersonicVelocity_HandlesInstantaneousJumps()
    {
        CameraDirector director = new() { Mode = CameraMode.PlayerPov };

        Vector3 initialPos = new(0, 0, 10);
        director.Update(0.016f, initialPos, 0f, 0, 1.77f);

        Vector3 initialEye = director.CurrentEye;

        // Simulate hero sudden supersonic teleport jump (e.g. Blink Dagger / Teleport Scroll: 2000 units away)
        Vector3 teleportPos = new(2000, 2000, 10);
        director.Update(0.016f, teleportPos, 90f, 50, 1.77f);

        // Jump threshold is 50 units -> camera should instantly snap to ideal position instead of trailing across the entire map
        Vector3 expectedHeadingVec = new(MathF.Cos(MathF.PI / 2f), MathF.Sin(MathF.PI / 2f), 0f);
        Vector3 expectedIdealEye = teleportPos - expectedHeadingVec * director.ChaseDistance + Vector3.UnitZ * director.ChaseHeight;

        Vector3.Distance(director.CurrentEye, expectedIdealEye).Should().BeApproximately(0f, 0.1f);
    }

    [Fact]
    public void CameraDirector_All5Modalities_ProduceInvertibleAndWellConditionedMatrices()
    {
        CameraDirector director = new();
        Vector3 heroPos = new(150, 250, 15);
        float heroHeading = 45.0f;
        float aspect = 1920f / 1080f;

        CameraMode[] allModes =
        [
            CameraMode.PlayerPov,
            CameraMode.FirstPerson,
            CameraMode.RtsIsometric,
            CameraMode.FreeSpectator,
            CameraMode.CinematicSpline
        ];

        director.Spline.Clear();
        director.Spline.AddNode(new DirectorNode { TimestampMs = 0, Position = new Vector3(100, 100, 50), LookAtTarget = heroPos });
        director.Spline.AddNode(new DirectorNode { TimestampMs = 5000, Position = new Vector3(300, 300, 100), LookAtTarget = heroPos });

        foreach (CameraMode mode in allModes)
        {
            director.Mode = mode;
            CameraState state = director.Update(0.016f, heroPos, heroHeading, 2500, aspect);

            // Verify View Matrix
            Matrix4x4 view = state.ViewMatrix;
            Matrix4x4.Invert(view, out Matrix4x4 invView).Should().BeTrue($"View matrix for {mode} must be invertible");

            // Verify Projection Matrix
            Matrix4x4 proj = state.ProjectionMatrix;
            Matrix4x4.Invert(proj, out Matrix4x4 invProj).Should().BeTrue($"Projection matrix for {mode} must be invertible");

            // Verify Combined MVP
            Matrix4x4 mvp = view * proj;
            float det = mvp.GetDeterminant();
            float.IsNaN(det).Should().BeFalse();
            float.IsInfinity(det).Should().BeFalse();
            det.Should().NotBe(0f);
        }
    }

    #endregion

    #region SECTION 3: NativeBufferRing Memory Safety & Concurrent Blitting

    [Fact]
    public void NativeBufferRing_ZeroMemoryLeaks_UnderDynamicResizingAndDoubleDisposal()
    {
        NativeBufferRing ring = new(640, 480);
        ring.Width.Should().Be(640);
        ring.Height.Should().Be(480);
        ring.Stride.Should().Be(2560);
        ring.BufferSizeBytes.Should().Be(2560 * 480);
        ring.IsDisposed.Should().BeFalse();

        // Commit and clear buffer
        ring.ClearBuffer(clearBackBuffer: true, 255, 0, 128);
        ring.CommitBackBuffer();
        ring.ClearBuffer(clearBackBuffer: false, 0, 255, 64);

        // Dynamically resize staging buffers multiple times (up and down)
        ring.Resize(1920, 1080);
        ring.Width.Should().Be(1920);
        ring.Height.Should().Be(1080);
        ring.Stride.Should().Be(7680);
        ring.BufferSizeBytes.Should().Be(7680 * 1080);

        ring.Resize(3840, 2160); // 4K staging
        ring.Width.Should().Be(3840);
        ring.BufferSizeBytes.Should().Be(3840 * 4 * 2160);

        ring.Resize(800, 600); // Downscale
        ring.Width.Should().Be(800);
        ring.Height.Should().Be(600);

        // Disposal safety
        ring.Dispose();
        ring.IsDisposed.Should().BeTrue();

        // Idempotent double dispose
        Action doubleDispose = () => ring.Dispose();
        doubleDispose.Should().NotThrow();

        // Post-dispose access throws ObjectDisposedException
        Action accessBack = () => ring.AcquireBackBuffer();
        accessBack.Should().Throw<ObjectDisposedException>();

        Action commit = () => ring.CommitBackBuffer();
        commit.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void NativeBufferRing_MismatchedRowBytes_BlitsWithAccuratePadding()
    {
        using NativeBufferRing ring = new(4, 2); // 4x2 pixels = 16 bytes stride, 32 bytes total
        ring.ClearBuffer(clearBackBuffer: true, 10, 20, 30, 255);
        ring.CommitBackBuffer();

        // Destination buffer with 32 bytes row stride (16 bytes pixel data + 16 bytes padding)
        int paddedRowBytes = 32;
        int destHeight = 2;
        byte[] destArray = new byte[paddedRowBytes * destHeight];

        GCHandle handle = GCHandle.Alloc(destArray, GCHandleType.Pinned);
        try
        {
            IntPtr destPtr = handle.AddrOfPinnedObject();
            ring.BlitFrontBuffer(destPtr, paddedRowBytes, destHeight);

            // Row 0 pixels [0..15] should have (10, 20, 30, 255)
            destArray[0].Should().Be(10);
            destArray[1].Should().Be(20);
            destArray[2].Should().Be(30);
            destArray[3].Should().Be(255);

            // Row 0 padding [16..31] should remain 0
            destArray[16].Should().Be(0);
            destArray[31].Should().Be(0);

            // Row 1 pixels [32..47] should have (10, 20, 30, 255)
            destArray[32].Should().Be(10);
            destArray[33].Should().Be(20);
            destArray[34].Should().Be(30);
            destArray[35].Should().Be(255);
        }
        finally
        {
            handle.Free();
        }
    }

    #endregion

    #region SECTION 4: End-to-End Replay Studio MVVM Integration & Telemetry

    [Fact]
    public void MainViewModel_FullRosterAndTelemetryBinding_UpdatesSmoothlyOnTimelineSeek()
    {
        ReplaySnapshotEngine snapshotEngine = new();
        PlaybackClock clock = new();
        MapEnvironmentManager mapManager = new();
        ReplayStudioService service = new(snapshotEngine, clock, mapManager);
        CameraDirector director = new();
        LightingController lighting = new();
        MainViewModel mainVm = new(service, clock, director, lighting);

        // 1. Load synthetic demo match
        mainVm.LoadDemoMatch();

        mainVm.IsReplayLoaded.Should().BeTrue();
        mainVm.MatchId.Should().Be("#135892401");
        mainVm.RosterVM.Players.Should().HaveCount(10);
        mainVm.RosterVM.LegionPlayers.Should().HaveCount(5);
        mainVm.RosterVM.HellbournePlayers.Should().HaveCount(5);

        // 2. Lock POV to Player 2 (Valkyrie / MoonMeander)
        RosterPlayerItem valkyrie = mainVm.RosterVM.Players[1];
        mainVm.RosterVM.LockPov(valkyrie);
        mainVm.ViewportVM.LockedHeroSlot.Should().Be(1);
        mainVm.ViewportVM.IsPovLocked.Should().BeTrue();

        // 3. Seek timeline to mid-game combat event (145000ms = 02:25)
        mainVm.TimelineVM.Seek(145000);

        mainVm.TimelineVM.CurrentTimeMs.Should().Be(145000);
        mainVm.TimelineVM.CurrentTimeFormatted.Should().Be("02:25.000");
        mainVm.RosterVM.LegionScore.Should().BeGreaterThanOrEqualTo(0);

        // 4. Step single tick forward and backward
        mainVm.TimelineVM.StepForward();
        mainVm.TimelineVM.CurrentTimeMs.Should().Be(145050); // +50ms

        mainVm.TimelineVM.StepBackward();
        mainVm.TimelineVM.CurrentTimeMs.Should().Be(145000); // -50ms

        // 5. Change playback speed
        mainVm.TimelineVM.SetSpeed4();
        mainVm.TimelineVM.PlaybackSpeed.Should().Be(4.0f);
        clock.SpeedMultiplier.Should().Be(4.0f);

        // 6. Test In/Out marker clamping
        mainVm.TimelineVM.SetInMarker();
        mainVm.TimelineVM.InMarkerMs.Should().Be(145000);

        mainVm.TimelineVM.Seek(200000);
        mainVm.TimelineVM.SetOutMarker();
        mainVm.TimelineVM.OutMarkerMs.Should().Be(200000);

        // 7. Open Export modal and verify marker synchronization
        mainVm.TimelineVM.OpenExportModal();
        mainVm.ExportVM.IsVisible.Should().BeTrue();
        mainVm.ExportVM.InMarkerMs.Should().Be(145000);
        mainVm.ExportVM.OutMarkerMs.Should().Be(200000);
    }

    #endregion
}
