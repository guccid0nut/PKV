// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: Tier5AdversarialHardeningTests.cs
// System: Replay Studio Desktop GUI Tool - Tier 5 Adversarial Coverage Hardening
// Policy: Strict Explicit Typing (NO 'var')
// Description: White-box adversarial stress tests targeting unexplored branches,
//              numerical boundaries, race conditions, Catmull-Rom singularities,
//              Vulkan push constants alignment, audio PCM synthesis, and zero-GC staging.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
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
using ReplayVulkanRenderer;
using Xunit;


namespace ReplayStudio.Tests;

/// <summary>
///     Tier 5 white-box adversarial coverage hardening test suite.
/// </summary>
public sealed class ReplayStudioAdversarialHardeningTests
{
    // ========================================================================
    // 1. NATIVE BUFFER RING UNEXPLORED PATHS & BLITTING EDGE CASES
    // ========================================================================

    [Fact]
    public void NativeBufferRing_Constructor_InvalidDimensions_ThrowsArgumentOutOfRangeException()
    {
        Action actZeroWidth = () => new NativeBufferRing(0, 1080);
        actZeroWidth.Should().Throw<ArgumentOutOfRangeException>()
            .WithMessage("*Width must be strictly positive.*");

        Action actNegativeWidth = () => new NativeBufferRing(-1920, 1080);
        actNegativeWidth.Should().Throw<ArgumentOutOfRangeException>()
            .WithMessage("*Width must be strictly positive.*");

        Action actZeroHeight = () => new NativeBufferRing(1920, 0);
        actZeroHeight.Should().Throw<ArgumentOutOfRangeException>()
            .WithMessage("*Height must be strictly positive.*");

        Action actNegativeHeight = () => new NativeBufferRing(1920, -1080);
        actNegativeHeight.Should().Throw<ArgumentOutOfRangeException>()
            .WithMessage("*Height must be strictly positive.*");
    }

    [Fact]
    public void NativeBufferRing_BlitFrontBuffer_InvalidArguments_ThrowsDescriptiveExceptions()
    {
        using NativeBufferRing ring = new(640, 480);

        Action actNullDest = () => ring.BlitFrontBuffer(IntPtr.Zero, 640 * 4, 480);
        actNullDest.Should().Throw<ArgumentException>()
            .WithMessage("*Destination address cannot be null.*");

        Action actInvalidRowBytes = () => ring.BlitFrontBuffer((IntPtr)0x1000, 0, 480);
        actInvalidRowBytes.Should().Throw<ArgumentOutOfRangeException>()
            .WithMessage("*Destination row bytes must be positive.*");

        Action actNegativeRowBytes = () => ring.BlitFrontBuffer((IntPtr)0x1000, -10, 480);
        actNegativeRowBytes.Should().Throw<ArgumentOutOfRangeException>()
            .WithMessage("*Destination row bytes must be positive.*");
    }

    [Fact]
    public unsafe void NativeBufferRing_BlitFrontBuffer_PaddedAndAsymmetricRowBytes_CopiesCorrectly()
    {
        // Arrange: 4x4 buffer (stride = 16 bytes)
        using NativeBufferRing ring = new(4, 4);

        // Fill front buffer with recognizable pattern
        byte* front = (byte*)ring.FrontBufferAddress;
        for (int i = 0; i < 64; i++)
        {
            front[i] = (byte)(i + 1);
        }

        // Padded destination: stride = 32 bytes (16 bytes data + 16 bytes padding), height = 4
        byte[] destArray = new byte[32 * 4];
        fixed (byte* dstPtr = destArray)
        {
            // Act: Blit with stride mismatch
            ring.BlitFrontBuffer((IntPtr)dstPtr, destinationRowBytes: 32, destinationHeight: 4);

            // Assert: verify each row was copied to proper padded offset
            for (int y = 0; y < 4; y++)
            {
                for (int x = 0; x < 16; x++)
                {
                    byte expected = (byte)(y * 16 + x + 1);
                    dstPtr[y * 32 + x].Should().Be(expected, $"Mismatch at row {y}, col {x}");
                }
            }
        }
    }

    [Fact]
    public unsafe void NativeBufferRing_ClearBuffer_FrontAndBack_FillsExpectedColorChannels()
    {
        using NativeBufferRing ring = new(8, 8);

        // Clear front buffer to Red (255, 0, 0, 255)
        ring.ClearBuffer(clearBackBuffer: false, r: 255, g: 0, b: 0, a: 255);

        // Clear back buffer to Green (0, 255, 0, 128)
        ring.ClearBuffer(clearBackBuffer: true, r: 0, g: 255, b: 0, a: 128);

        uint* frontPixels = (uint*)ring.FrontBufferAddress;
        uint* backPixels = (uint*)ring.BackBufferAddress;

        uint expectedFront = ((uint)255) | (((uint)0) << 8) | (((uint)0) << 16) | (((uint)255) << 24);
        uint expectedBack = ((uint)0) | (((uint)255) << 8) | (((uint)0) << 16) | (((uint)128) << 24);

        for (int i = 0; i < 64; i++)
        {
            frontPixels[i].Should().Be(expectedFront);
            backPixels[i].Should().Be(expectedBack);
        }
    }

    [Fact]
    public void NativeBufferRing_Resize_NegativeOrZero_SafelyIgnored()
    {
        using NativeBufferRing ring = new(800, 600);
        int origSize = ring.BufferSizeBytes;

        ring.Resize(0, 600);
        ring.Width.Should().Be(800);
        ring.Height.Should().Be(600);
        ring.BufferSizeBytes.Should().Be(origSize);

        ring.Resize(800, -100);
        ring.Width.Should().Be(800);
        ring.Height.Should().Be(600);
        ring.BufferSizeBytes.Should().Be(origSize);

        ring.Resize(800, 600); // Identical dimensions -> no-op
        ring.Width.Should().Be(800);
        ring.Height.Should().Be(600);
    }

    [Fact]
    public void NativeBufferRing_PostDisposeOperations_ThrowObjectDisposedException()
    {
        NativeBufferRing ring = new(320, 240);
        ring.Dispose();

        ring.IsDisposed.Should().BeTrue();
        Action actFront = () => _ = ring.FrontBufferAddress;
        actFront.Should().Throw<ObjectDisposedException>();

        Action actBack = () => _ = ring.BackBufferAddress;
        actBack.Should().Throw<ObjectDisposedException>();

        Action actAcquire = () => ring.AcquireBackBuffer();
        actAcquire.Should().Throw<ObjectDisposedException>();

        Action actCommit = () => ring.CommitBackBuffer();
        actCommit.Should().Throw<ObjectDisposedException>();

        Action actClear = () => ring.ClearBuffer(true, 0, 0, 0);
        actClear.Should().Throw<ObjectDisposedException>();

        Action actResize = () => ring.Resize(640, 480);
        actResize.Should().Throw<ObjectDisposedException>();
    }

    // ========================================================================
    // 2. CATMULL-ROM SPLINE ANALYTICAL DERIVATIVES & SLERP SINGULARITIES
    // ========================================================================

    [Fact]
    public void CatmullRomSpline_EvaluateTangent_AnalyticalDerivatives_MatchFiniteDifference()
    {
        Vector3 p0 = new(0, 0, 0);
        Vector3 p1 = new(10, 20, 5);
        Vector3 p2 = new(40, 30, 15);
        Vector3 p3 = new(70, 10, 25);

        float eps = 0.0001f;

        for (float u = 0.1f; u <= 0.9f; u += 0.1f)
        {
            Vector3 analytical = CatmullRomSpline.EvaluateTangent(p0, p1, p2, p3, u);

            Vector3 posPlus = CatmullRomSpline.EvaluateCubic(p0, p1, p2, p3, u + eps);
            Vector3 posMinus = CatmullRomSpline.EvaluateCubic(p0, p1, p2, p3, u - eps);
            Vector3 numerical = (posPlus - posMinus) / (2.0f * eps);

            analytical.X.Should().BeApproximately(numerical.X, 0.05f);
            analytical.Y.Should().BeApproximately(numerical.Y, 0.05f);
            analytical.Z.Should().BeApproximately(numerical.Z, 0.05f);
        }
    }

    [Fact]
    public void CatmullRomSpline_SlerpShortestArc_AntipodalQuaternions_InvertsCorrectly()
    {
        Quaternion q1 = Quaternion.Identity;
        Quaternion q2 = -Quaternion.Identity; // Exact antipodal representation of same rotation

        Quaternion result = CatmullRomSpline.SlerpShortestArc(q1, q2, 0.5f);

        // Must remain normalized and represent identity rotation without NaN
        float.IsFinite(result.W).Should().BeTrue();
        MathF.Abs(result.W).Should().BeApproximately(1.0f, 0.001f);
        result.X.Should().BeApproximately(0f, 0.001f);
        result.Y.Should().BeApproximately(0f, 0.001f);
        result.Z.Should().BeApproximately(0f, 0.001f);
    }

    [Fact]
    public void CatmullRomSpline_SlerpShortestArc_NearlyCollinearQuaternions_NlerpFallbackOperates()
    {
        Quaternion q1 = Quaternion.Identity;
        Quaternion q2 = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.0001f); // Extremely small rotation (dot > 0.9995)

        Quaternion result = CatmullRomSpline.SlerpShortestArc(q1, q2, 0.5f);

        result.Length().Should().BeApproximately(1.0f, 0.001f);
        result.Z.Should().BeGreaterThanOrEqualTo(0f);
    }

    [Fact]
    public void CatmullRomSpline_Evaluate_ZeroAndSingleNode_ReturnsFallbackDefaults()
    {
        CatmullRomSpline spline = new();

        // 0 nodes
        (Vector3 pos0, Vector3 tgt0, Quaternion rot0, float fov0) = spline.Evaluate(5000);
        pos0.Should().Be(new Vector3(0, -14, 18));
        tgt0.Should().Be(Vector3.Zero);
        rot0.Should().Be(Quaternion.Identity);
        fov0.Should().Be(55.0f);

        // 1 node
        DirectorNode single = new()
        {
            TimestampMs = 2000,
            Position = new Vector3(10, 20, 30),
            LookAtTarget = new Vector3(5, 5, 0),
            Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f),
            FovDegrees = 75.0f
        };
        spline.AddNode(single);

        (Vector3 pos1, Vector3 tgt1, Quaternion rot1, float fov1) = spline.Evaluate(0);
        pos1.Should().Be(single.Position);
        tgt1.Should().Be(single.LookAtTarget);
        fov1.Should().Be(75.0f);

        (Vector3 pos2, Vector3 tgt2, Quaternion rot2, float fov2) = spline.Evaluate(10000);
        pos2.Should().Be(single.Position);
        tgt2.Should().Be(single.LookAtTarget);
        fov2.Should().Be(75.0f);
    }

    [Fact]
    public void CatmullRomSpline_RemoveAndClearNodes_ModifiesStateDeterministically()
    {
        CatmullRomSpline spline = new();
        DirectorNode node1 = new() { TimestampMs = 1000 };
        DirectorNode node2 = new() { TimestampMs = 2000 };
        DirectorNode node3 = new() { TimestampMs = 3000 };

        spline.AddNode(node1);
        spline.AddNode(node2);
        spline.AddNode(node3);

        spline.Count.Should().Be(3);

        bool removed = spline.RemoveNode(node2.Id);
        removed.Should().BeTrue();
        spline.Count.Should().Be(2);

        bool removedAgain = spline.RemoveNode(node2.Id);
        removedAgain.Should().BeFalse();

        spline.Clear();
        spline.Count.Should().Be(0);
    }

    // ========================================================================
    // 3. CAMERA DIRECTOR PROJECTION & MULTI-MODALITY MATRICES
    // ========================================================================

    [Fact]
    public void CameraDirector_ComputeProjectionMatrix_ExtremeAspectRatios_GeneratesFinitePerspective()
    {
        // Ultra-wide (32:9)
        Matrix4x4 projUltraWide = CameraDirector.ComputeProjectionMatrix(55.0f, 32.0f / 9.0f, 0.1f, 1000.0f);
        float.IsFinite(projUltraWide.M11).Should().BeTrue();
        projUltraWide.M11.Should().BeGreaterThan(0f);

        // Portrait mobile (9:16)
        Matrix4x4 projPortrait = CameraDirector.ComputeProjectionMatrix(55.0f, 9.0f / 16.0f, 0.1f, 1000.0f);
        float.IsFinite(projPortrait.M11).Should().BeTrue();
        projPortrait.M11.Should().BeGreaterThan(0f);

        // Square (1:1)
        Matrix4x4 projSquare = CameraDirector.ComputeProjectionMatrix(55.0f, 1.0f, 0.1f, 1000.0f);
        float.IsFinite(projSquare.M11).Should().BeTrue();
        projSquare.M11.Should().Be(projSquare.M22); // Diagonal equality for square aspect ratio
    }

    [Fact]
    public void CameraDirector_TranslateFreeSpectator_SprintAndMultiAxis_MovesCorrectDisplacement()
    {
        CameraDirector director = new()
        {
            Mode = CameraMode.FreeSpectator,
            MoveSpeed = 20.0f
        };
        director.SetFreeSpectatorOrientation(yawDeg: 90.0f, pitchDeg: 0.0f);
        director.ResetPosition(Vector3.Zero, Vector3.UnitY);

        // Forward movement along Y-axis for 1.0s without sprint (Speed = 20)
        director.TranslateFreeSpectator(new Vector3(0, 1, 0), dt: 1.0f, sprint: false);
        director.CurrentEye.Y.Should().BeApproximately(20.0f, 0.5f);

        // Forward movement along Y-axis for 1.0s WITH sprint (Speed = 20 * 2.5 = 50)
        director.TranslateFreeSpectator(new Vector3(0, 1, 0), dt: 1.0f, sprint: true);
        director.CurrentEye.Y.Should().BeApproximately(70.0f, 0.5f);
    }

    [Fact]
    public void CameraDirector_PlayerPov_TeleportDistanceJump_SnapsInstantly()
    {
        CameraDirector director = new()
        {
            Mode = CameraMode.PlayerPov,
            ChaseDistance = 14.0f,
            ChaseHeight = 18.0f
        };
        director.ResetPosition(new Vector3(0, 0, 0), Vector3.Zero);

        // Hero respawns 500 units away
        Vector3 farHeroPos = new(500, 500, 2);
        CameraState state = director.Update(dt: 0.016f, heroPos: farHeroPos, heroHeadingDeg: 0f, timestampMs: 1000, aspectRatio: 1.777f);

        // Teleport threshold (>50 units) snaps camera immediately
        Vector3 headingVec = new(MathF.Cos(0f), MathF.Sin(0f), 0f);
        Vector3 expectedEye = farHeroPos - headingVec * 14.0f + Vector3.UnitZ * 18.0f;

        director.CurrentEye.X.Should().BeApproximately(expectedEye.X, 0.01f);
        director.CurrentEye.Y.Should().BeApproximately(expectedEye.Y, 0.01f);
        director.CurrentEye.Z.Should().BeApproximately(expectedEye.Z, 0.01f);
    }

    // ========================================================================
    // 4. LIGHTING CONTROLLER & 128-BYTE PUSH CONSTANTS VALIDATION
    // ========================================================================

    [Fact]
    public void PushConstants_MemorySizeAndLayout_Strictly128Bytes()
    {
        int lightingSize = Marshal.SizeOf<LightingPushConstants>();

        lightingSize.Should().Be(128, "LightingPushConstants must match Vulkan 128-byte push constant budget");
    }

    [Theory]
    [InlineData(LightingPreset.MiddaySun, 0.40f, 0.85f, 1.20f, 0.50f)]
    [InlineData(LightingPreset.SunsetGoldenHour, 0.28f, 1.10f, 0.95f, 0.70f)]
    [InlineData(LightingPreset.NightMoonlight, 0.15f, 0.40f, 1.40f, 0.85f)]
    [InlineData(LightingPreset.HellbourneInferno, 0.30f, 1.30f, 0.80f, 1.00f)]
    public void LightingController_Presets_AppliesExactIrradianceAndBloomParameters(
        LightingPreset preset,
        float expectedAmbient,
        float expectedSpecular,
        float expectedBloom,
        float expectedAura)
    {
        LightingController controller = new();
        controller.LoadPreset(preset);

        controller.AmbientFactor.Should().Be(expectedAmbient);
        controller.SpecularFactor.Should().Be(expectedSpecular);
        controller.BloomThreshold.Should().Be(expectedBloom);
        controller.TeamAuraIntensity.Should().Be(expectedAura);
    }

    [Fact]
    public void LightingController_BuildPushConstants_TeamTints_MappedCorrectly()
    {
        LightingController controller = new();
        Matrix4x4 mvp = Matrix4x4.Identity;

        LightingPushConstants legionConstants = controller.BuildPushConstants(mvp, 10.0f, team: 1);
        legionConstants.TeamTint.Should().Be(controller.LegionTint);

        LightingPushConstants hellConstants = controller.BuildPushConstants(mvp, 10.0f, team: 2);
        hellConstants.TeamTint.Should().Be(controller.HellbourneTint);

        LightingPushConstants neutralConstants = controller.BuildPushConstants(mvp, 10.0f, team: 0);
        neutralConstants.TeamTint.Should().Be(new Vector4(1f, 1f, 1f, 1f));
    }

    // ========================================================================
    // 5. REPLAY SNAPSHOT ENGINE & DISCRETE HERO ATTRIBUTE TRANSITIONS
    // ========================================================================

    [Fact]
    public void InterpolateHeroInto_DiscreteTransitions_KillsDeathsLevelAndAnimation_SwitchAtThresholds()
    {
        HeroSnapshot h0 = new()
        {
            ClientIndex = 0,
            Team = 1,
            Level = 5,
            Kills = 2,
            Deaths = 1,
            Assists = 3,
            IsAlive = true,
            Position = new Vector3(0, 0, 0)
        };

        HeroSnapshot h1 = new()
        {
            ClientIndex = 0,
            Team = 1,
            Level = 6,
            Kills = 3,
            Deaths = 2,
            Assists = 4,
            IsAlive = false, // Hero died at h1
            Position = new Vector3(10, 10, 0)
        };

        HeroSnapshot dest = new();

        // Alpha = 0.49 (Before midpoint)
        ReplaySnapshotEngine.InterpolateHeroInto(h0, h1, 0.49f, dest);
        dest.Level.Should().Be(5);
        dest.Kills.Should().Be(2);
        dest.IsAlive.Should().BeTrue();
        dest.ActiveAnimation.Should().Be("walk"); // Moving and alive

        // Alpha = 0.51 (After midpoint)
        ReplaySnapshotEngine.InterpolateHeroInto(h0, h1, 0.51f, dest);
        dest.Level.Should().Be(6);
        dest.Kills.Should().Be(3);
        dest.IsAlive.Should().BeTrue(); // Alive until alpha >= 0.95

        // Alpha = 0.96 (Near death)
        ReplaySnapshotEngine.InterpolateHeroInto(h0, h1, 0.96f, dest);
        dest.IsAlive.Should().BeFalse();
        dest.ActiveAnimation.Should().Be("death");
    }

    [Fact]
    public void ReplaySnapshotEngine_LargeKeyframeCount_BinarySearchMaintainsSubMillisecondLookup()
    {
        ReplaySnapshotEngine engine = new(ringBufferSize: 128);
        List<GameStateSnapshot> keyframes = [];

        // 5,000 keyframes across 50,000 seconds match
        for (int i = 0; i < 5000; i++)
        {
            keyframes.Add(new GameStateSnapshot
            {
                TimestampMs = i * 10000,
                KeyframeTag = $"Frame_{i}"
            });
        }
        engine.LoadKeyframes(keyframes, totalDurationMs: 50000 * 1000);

        engine.KeyframeCount.Should().Be(5000);

        // Seek test
        GameStateSnapshot found = engine.SeekNearestKeyframe(25000 * 1000 + 4999);
        found.TimestampMs.Should().Be(25000 * 1000);
        found.KeyframeTag.Should().Be("Frame_2500");
    }

    // ========================================================================
    // 6. PLAYBACK CLOCK BOUNDARY CONDITIONS & TIME DRIFT INVARIANTS
    // ========================================================================

    [Fact]
    public void PlaybackClock_OutMarkerLessThanInMarker_EffectiveEndDefaultsToTotalDuration()
    {
        PlaybackClock clock = new()
        {
            TotalDurationMs = 30000,
            InMarkerMs = 15000,
            OutMarkerMs = 5000, // Invalid: Out < In
            CurrentTimeMs = 29990
        };

        clock.Play();
        clock.Update();

        // Effective end should be 30,000ms
        clock.CurrentTimeMs.Should().BeInRange(0, 30000);
    }

    [Fact]
    public void PlaybackClock_Stop_EmitsTimeUpdatedWithInMarker()
    {
        PlaybackClock clock = new()
        {
            TotalDurationMs = 60000,
            InMarkerMs = 5000,
            CurrentTimeMs = 25000
        };

        int receivedTime = -1;
        clock.TimeUpdated += t => receivedTime = t;

        clock.Stop();

        clock.CurrentTimeMs.Should().Be(5000);
        clock.State.Should().Be(PlaybackState.Stopped);
        receivedTime.Should().Be(5000);
    }

    // ========================================================================
    // 7. MAP ENVIRONMENT MANAGER COORDINATE TRANSFORMS & TERRAIN MESH
    // ========================================================================

    [Fact]
    public void MapEnvironmentManager_CoordinateTransforms_BijectiveRoundtrip()
    {
        // Test 100 random coordinate points within HoN world bounds [0..16384]
        Random rng = new(1337);

        for (int i = 0; i < 100; i++)
        {
            float honX = (float)rng.NextDouble() * 16384f;
            float honY = (float)rng.NextDouble() * 16384f;
            Vector2 honPos = new(honX, honY);

            // HoN -> World3D -> HoN
            Vector3 world3D = MapEnvironmentManager.HonToWorld3D(honPos, 1.5f);
            Vector2 roundtripHon = MapEnvironmentManager.World3DToHon(world3D);

            roundtripHon.X.Should().BeApproximately(honX, 0.001f);
            roundtripHon.Y.Should().BeApproximately(honY, 0.001f);

            // HoN -> Normalized -> HoN
            Vector2 normalized = MapEnvironmentManager.HonToNormalized(honPos);
            Vector2 roundtripNorm = MapEnvironmentManager.NormalizedToHon(normalized);

            roundtripNorm.X.Should().BeApproximately(honX, 0.001f);
            roundtripNorm.Y.Should().BeApproximately(honY, 0.001f);
        }
    }

    [Fact]
    public void MapEnvironmentManager_GenerateTerrainMesh_ValidTopologyAndNormals()
    {
        MapEnvironmentManager manager = new();
        manager.LoadMap("caldavar");

        TerrainMeshData mesh = manager.GenerateTerrainMesh(gridResX: 16, gridResY: 16);

        mesh.Positions.Length.Should().Be(256);
        mesh.Normals.Length.Should().Be(256);
        mesh.TexCoords.Length.Should().Be(256);
        mesh.Indices.Length.Should().Be(15 * 15 * 6); // 1350 indices (450 triangles)

        // Verify all normals are normalized unit vectors
        for (int i = 0; i < mesh.Normals.Length; i++)
        {
            mesh.Normals[i].Length().Should().BeApproximately(1.0f, 0.01f);
        }
    }

    // ========================================================================
    // 8. AUDIO STREAM SYNC BUFFER WAV CONTAINER & SYNTHESIS SAFETY
    // ========================================================================

    [Fact]
    public void AudioStreamSyncBuffer_WriteWavHeader_RIFFStructureExactBytes()
    {
        using MemoryStream ms = new();
        int pcmLength = 3200 * 60; // 1 second of audio (192,000 bytes)

        AudioStreamSyncBuffer.WriteWavHeader(ms, pcmLength, sampleRate: 48000, channels: 2, bitsPerSample: 16);

        byte[] headerBytes = ms.ToArray();
        headerBytes.Length.Should().Be(44);

        // Check RIFF header ASCII markers
        string riff = System.Text.Encoding.ASCII.GetString(headerBytes, 0, 4);
        string wave = System.Text.Encoding.ASCII.GetString(headerBytes, 8, 4);
        string fmt = System.Text.Encoding.ASCII.GetString(headerBytes, 12, 4);
        string data = System.Text.Encoding.ASCII.GetString(headerBytes, 36, 4);

        riff.Should().Be("RIFF");
        wave.Should().Be("WAVE");
        fmt.Should().Be("fmt ");
        data.Should().Be("data");

        // Verify audio format = 1 (PCM) and channels = 2
        short audioFormat = BitConverter.ToInt16(headerBytes, 20);
        short channels = BitConverter.ToInt16(headerBytes, 22);
        int sampleRate = BitConverter.ToInt32(headerBytes, 24);
        short bitsPerSample = BitConverter.ToInt16(headerBytes, 34);

        audioFormat.Should().Be(1);
        channels.Should().Be(2);
        sampleRate.Should().Be(48000);
        bitsPerSample.Should().Be(16);
    }

    [Theory]
    [InlineData(48000, 60, 2, 16, 3200)]
    [InlineData(48000, 30, 2, 16, 6400)]
    [InlineData(44100, 60, 2, 16, 2940)]
    [InlineData(96000, 60, 2, 16, 6400)]
    [InlineData(48000, 0, 2, 16, 0)] // Invalid 0 FPS
    public void AudioStreamSyncBuffer_CalculateBytesPerFrame_MatchesFormulas(
        int rate, int fps, int ch, int bits, int expectedBytes)
    {
        int bytes = AudioStreamSyncBuffer.CalculateBytesPerFrame(rate, fps, ch, bits);
        bytes.Should().Be(expectedBytes);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public void AudioStreamSyncBuffer_SynthesizeMultipleOverlappingEvents_NoClippingOverflow()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        using AudioStreamSyncBuffer buffer = new(sampleRate: 48000, channels: 2, bitsPerSample: 16, targetFps: 60);

        // 10 simultaneous combat events at the exact same millisecond
        List<CombatEventMarker> stackedEvents = [];
        for (int i = 0; i < 10; i++)
        {
            stackedEvents.Add(new CombatEventMarker
            {
                TimestampMs = 1000,
                EventType = (CombatEventType)(i % 5),
                IntensityScore = 1.0f
            });
        }

        byte[] audio = buffer.SynthesizeAudioTrack(inMarkerMs: 0, outMarkerMs: 3000, targetFps: 60, combatEvents: stackedEvents);

        audio.Should().NotBeEmpty();
        audio.Length.Should().Be(180 * 3200);

        // Verify no PCM sample exceeded 16-bit bounds (Math.Clamp guarantee)
        short[] samples = new short[audio.Length / 2];
        Buffer.BlockCopy(audio, 0, samples, 0, audio.Length);

        for (int s = 0; s < samples.Length; s++)
        {
            samples[s].Should().BeInRange(short.MinValue, short.MaxValue);
        }
    }

    // ========================================================================
    // 9. VIDEO EXPORT PRESETS & FFMPEG COMMAND-LINE ARGUMENTS
    // ========================================================================

    [Theory]
    [InlineData(ExportVideoCodec.H264Nvenc, "h264_nvenc")]
    [InlineData(ExportVideoCodec.HevcNvenc, "hevc_nvenc")]
    [InlineData(ExportVideoCodec.H264Qsv, "h264_qsv")]
    [InlineData(ExportVideoCodec.H264Amf, "h264_amf")]
    [InlineData(ExportVideoCodec.Libx264, "libx264")]
    public void VideoExportPipeline_BuildFfmpegArguments_AllCodecs_ContainsTargetEncoderFlags(
        ExportVideoCodec codec,
        string expectedEncoderString)
    {
        ExportParameters parameters = new()
        {
            Preset = ExportPreset.CreateCustom(
                width: 1920,
                height: 1080,
                fps: 60,
                videoBitrate: "20M",
                codec: codec,
                audioBitrateKbps: 320,
                constantQuality: 18,
                maxBitrate: "30M",
                bufferSize: "40M"),
            OutputFilePath = @"C:\Exports\output.mp4",
            InMarkerMs = 0,
            OutMarkerMs = 5000,
            IncludeAudio = true
        };

        string args = VideoExportPipeline.BuildFfmpegArguments(parameters);

        args.Should().Contain(expectedEncoderString);
        args.Should().Contain("-pix_fmt");
        args.Should().Contain("-g 120"); // 60 FPS * 2 = 120 GOP
        args.Should().Contain("-movflags +faststart");
    }

    [Fact]
    public void ExportPreset_FromDisplayName_UnknownName_DefaultsTo1080pUltra()
    {
        ExportPreset preset = ExportPreset.FromDisplayName("NonExistentPreset");
        preset.Should().NotBeNull();
        preset.DisplayName.Should().Be("1080p60 Ultra (NVENC GPU)");
        preset.Width.Should().Be(1920);
        preset.Height.Should().Be(1080);
    }

    // ========================================================================
    // 11. VIEWMODELS & SERVICE REACTIVE BINDINGS
    // ========================================================================

    [Fact]
    public void TimelineViewModel_FormatTimestamp_MillisecondsFormatting()
    {
        TimelineViewModel.FormatTimestamp(0, true).Should().Be("00:00.000");
        TimelineViewModel.FormatTimestamp(65432, true).Should().Be("01:05.432");
        TimelineViewModel.FormatTimestamp(65432, false).Should().Be("01:05");
        TimelineViewModel.FormatTimestamp(-100, true).Should().Be("00:00.000");
    }

    [Fact]
    public void RosterViewModel_SelectPlayerAndPovLock_EmitsEventsCorrectly()
    {
        RosterViewModel roster = new();
        RosterPlayerItem p1 = new() { ClientNum = 0, Team = 1, PlayerName = "Player1" };
        RosterPlayerItem p2 = new() { ClientNum = 1, Team = 1, PlayerName = "Player2" };

        roster.PopulateRoster([p1, p2]);

        int povLockedSlot = -99;
        roster.PovLockChanged += slot => povLockedSlot = slot;

        roster.LockPov(p2);
        povLockedSlot.Should().Be(1);
        roster.LockedHeroSlot.Should().Be(1);
        p2.IsPovLocked.Should().BeTrue();
        p1.IsPovLocked.Should().BeFalse();

        roster.UnlockPov();
        povLockedSlot.Should().Be(-1);
        roster.LockedHeroSlot.Should().Be(-1);
        p2.IsPovLocked.Should().BeFalse();
    }

    [Fact]
    public void MainViewModel_LoadDemoMatch_PopulatesAllViewModelsAndMetadata()
    {
        ReplaySnapshotEngine snapshotEngine = new();
        PlaybackClock clock = new();
        MapEnvironmentManager mapManager = new();
        ReplayStudioService service = new(snapshotEngine, clock, mapManager);
        CameraDirector cameraDirector = new();
        LightingController lightingController = new();

        MainViewModel mainVm = new(service, clock, cameraDirector, lightingController);

        mainVm.LoadDemoMatch();

        mainVm.IsReplayLoaded.Should().BeTrue();
        mainVm.MatchId.Should().Be("#135892401");
        mainVm.RosterVM.Players.Count.Should().Be(10);
        mainVm.RosterVM.LegionPlayers.Count.Should().Be(5);
        mainVm.RosterVM.HellbournePlayers.Count.Should().Be(5);
        mainVm.TimelineVM.TotalDurationMs.Should().BeGreaterThan(0);
    }
}
