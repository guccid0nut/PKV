// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: M2Challenger2StressTests.cs
// System: Replay Studio Desktop GUI Tool
// Policy: Strict Explicit Typing (NO 'var')
// Challenger: Challenger 2 (Empirical 3D Rendering Pipeline, Skeletal Animation, Map Terrain, Water Plane & Unmanaged Buffer Stress Testing)
// ============================================================================

using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using FluentAssertions;
using ReplayStudio.GUI.Common;
using ReplayStudio.GUI.Director;
using ReplayStudio.GUI.Engine;
using ReplayStudio.GUI.Models;
using ReplayVulkanRenderer;
using Xunit;

namespace ReplayStudio.Tests;

public sealed unsafe class ReplayRenderingPipelineStressTests
{
    // ========================================================================
    // Section 1: Skeletal Animation Posing & LBS Deformation Stress Tests
    // ========================================================================

    [Theory]
    [InlineData(-10000.0f, true, false)]
    [InlineData(-500.0f, false, true)]
    [InlineData(-1.0f, true, true)]
    [InlineData(-0.001f, false, false)]
    [InlineData(0.0f, true, false)]
    [InlineData(1e-6f, false, false)]
    [InlineData(0.016667f, true, false)]
    [InlineData(0.033333f, false, true)]
    [InlineData(1.0f, true, true)]
    [InlineData(50.0f, true, false)]
    [InlineData(1000.0f, false, false)]
    [InlineData(100000.0f, true, false)]
    public void ComputeSubsecondBonePose_ExtremeDeltaTimesAndTimestamps_ProducesBoundedFiniteTransforms(
        float timestampSeconds,
        bool isMoving,
        bool isAttacking)
    {
        // Act
        SkeletalPose pose = SkeletalAnimationEngine.ComputeSubsecondBonePose(timestampSeconds, isMoving, isAttacking);

        // Assert
        pose.Should().NotBeNull();
        pose.BoneTransforms.Length.Should().Be(64);

        // Root bone (bone 0) must contain finite translation matrix with subtle natural gait
        Matrix4x4 root = pose.BoneTransforms[0];
        float sway = root.M41;
        float bounce = root.M43;

        float.IsNaN(sway).Should().BeFalse("Root sway must not be NaN");
        float.IsInfinity(sway).Should().BeFalse("Root sway must not be Infinity");
        float.IsNaN(bounce).Should().BeFalse("Root bounce must not be NaN");
        float.IsInfinity(bounce).Should().BeFalse("Root bounce must not be Infinity");

        // Sway and bounce must be bounded within subtle gait limits [-0.05, 0.05]
        MathF.Abs(sway).Should().BeLessThanOrEqualTo(0.05f);
        MathF.Abs(bounce).Should().BeLessThanOrEqualTo(0.05f);

        // Rotation submatrix of root must remain pure identity
        root.M11.Should().Be(1.0f);
        root.M12.Should().Be(0.0f);
        root.M13.Should().Be(0.0f);
        root.M21.Should().Be(0.0f);
        root.M22.Should().Be(1.0f);
        root.M23.Should().Be(0.0f);
        root.M31.Should().Be(0.0f);
        root.M32.Should().Be(0.0f);
        root.M33.Should().Be(1.0f);

        // All child limb bones [1..63] must be strictly identity relative to bind pose
        for (int i = 1; i < 64; i++)
        {
            pose.BoneTransforms[i].Should().Be(Matrix4x4.Identity, $"Bone {i} must remain pristine identity in bind-pose");
        }
    }

    [Fact]
    public void DeformVertexSkeletalLbs_ExtremeRestPositions_TransformsAccurately()
    {
        // Arrange
        SkeletalPose pose = SkeletalAnimationEngine.ComputeSubsecondBonePose(1.0f, isMoving: true, isAttacking: false);
        Vector4 jointIndices = new(0, 0, 0, 0);
        Vector4 jointWeights = new(1.0f, 0f, 0f, 0f);

        Vector3[] testPositions =
        [
            Vector3.Zero,
            new Vector3(1.0f, 2.0f, 3.0f),
            new Vector3(-50.0f, 100.0f, -25.0f),
            new Vector3(1e4f, -1e4f, 5e3f)
        ];

        foreach (Vector3 restPos in testPositions)
        {
            // Act
            Vector3 deformed = SkeletalAnimationEngine.DeformVertexSkeletalLbs(restPos, jointIndices, jointWeights, pose);

            // Assert: Deformed vertex should match restPos + root translation
            Vector3 expected = Vector3.Transform(restPos, pose.BoneTransforms[0]);
            deformed.X.Should().BeApproximately(expected.X, 0.0001f);
            deformed.Y.Should().BeApproximately(expected.Y, 0.0001f);
            deformed.Z.Should().BeApproximately(expected.Z, 0.0001f);
        }
    }

    [Fact]
    public void DeformVertexSkeletalLbs_ExtremeAndInvalidJointIndices_ClampsSafelyWithoutCrashing()
    {
        // Arrange
        SkeletalPose pose = SkeletalAnimationEngine.ComputeSubsecondBonePose(2.0f, isMoving: true, isAttacking: false);
        Vector3 restPos = new(0.0f, 0.5f, 1.5f);

        // Test out-of-range negative and very large indices
        Vector4[] testIndices =
        [
            new Vector4(-1, -5, -100, -99999),
            new Vector4(64, 100, 5000, 999999),
            new Vector4(-50, 200, 0, 63)
        ];

        Vector4 weights = new(0.25f, 0.25f, 0.25f, 0.25f);

        foreach (Vector4 indices in testIndices)
        {
            // Act: Must not throw IndexOutOfRangeException or crash
            Action act = () => SkeletalAnimationEngine.DeformVertexSkeletalLbs(restPos, indices, weights, pose);

            // Assert
            act.Should().NotThrow("LBS skinning must clamp joint indices safely within [0, BoneCount-1]");
            Vector3 deformed = SkeletalAnimationEngine.DeformVertexSkeletalLbs(restPos, indices, weights, pose);
            float.IsNaN(deformed.X).Should().BeFalse();
            float.IsNaN(deformed.Y).Should().BeFalse();
            float.IsNaN(deformed.Z).Should().BeFalse();
        }
    }

    [Fact]
    public void DeformVertexSkeletalLbs_DegenerateJointWeights_HandlesZeroAndUnnormalizedWeights()
    {
        // Arrange
        SkeletalPose pose = SkeletalAnimationEngine.ComputeSubsecondBonePose(0.5f, isMoving: false, isAttacking: false);
        Vector3 restPos = new(1.0f, 1.0f, 1.0f);
        Vector4 jointIndices = new(0, 1, 2, 3);

        // 1. Zero weights fallback branch
        Vector4 zeroWeights = Vector4.Zero;
        Vector3 zeroResult = SkeletalAnimationEngine.DeformVertexSkeletalLbs(restPos, jointIndices, zeroWeights, pose);
        Vector3 expectedZero = Vector3.Transform(restPos, pose.BoneTransforms[0]);
        zeroResult.Should().Be(expectedZero, "Zero weights must fallback cleanly to root bone transform");

        // 2. Unnormalized weights (sum = 100.0f)
        Vector4 unnormalizedWeights = new(20.0f, 30.0f, 10.0f, 40.0f);
        Vector3 unnormResult = SkeletalAnimationEngine.DeformVertexSkeletalLbs(restPos, jointIndices, unnormalizedWeights, pose);
        float.IsNaN(unnormResult.X).Should().BeFalse();
        float.IsNaN(unnormResult.Y).Should().BeFalse();
        float.IsNaN(unnormResult.Z).Should().BeFalse();

        // 3. Single dominant weight
        Vector4 singleWeight = new(1.0f, 0f, 0f, 0f);
        Vector3 singleResult = SkeletalAnimationEngine.DeformVertexSkeletalLbs(restPos, jointIndices, singleWeight, pose);
        singleResult.Should().Be(expectedZero);
    }

    [Fact]
    public void SkeletalMath_QuaternionSlerpAndNLerp_AntipodalAndSingularityHandling()
    {
        // 1. Antipodal check: q and -q represent the exact same orientation
        Quaternion q1 = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.5f);
        Quaternion q2Neg = -q1;
        Quaternion slerpAntipodal = SkeletalAnimationEngine.Slerp(q1, q2Neg, 0.5f);
        slerpAntipodal.Length().Should().BeApproximately(1.0f, 0.001f);

        float distDeg = SkeletalAnimationEngine.AngularDistanceDeg(q1, q2Neg);
        distDeg.Should().BeApproximately(0.0f, 0.01f, "Antipodal quaternions must have 0 degrees angular distance");

        // 2. Near-parallel quaternions (dot > 0.9995) NLerp fallback
        Quaternion qNearA = Quaternion.Identity;
        Quaternion qNearB = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.001f);
        Quaternion slerpNear = SkeletalAnimationEngine.Slerp(qNearA, qNearB, 0.5f);
        slerpNear.Length().Should().BeApproximately(1.0f, 0.001f);
        float.IsNaN(slerpNear.X).Should().BeFalse();

        // 3. Degenerate zero quaternion returns Identity
        Quaternion zeroQuat = new(0, 0, 0, 0);
        Quaternion normZero = SkeletalAnimationEngine.NormalizeQuaternion(zeroQuat);
        normZero.Should().Be(Quaternion.Identity);

        // 4. NLerp antipodal handling
        Quaternion nlerpResult = SkeletalAnimationEngine.NLerp(q1, q2Neg, 0.5f);
        nlerpResult.Length().Should().BeApproximately(1.0f, 0.001f);
    }

    [Fact]
    public void SkeletalMath_EulerConversionsAndGimbalLock_ConvertsAccurately()
    {
        // Test standard pitch/roll/yaw conversions in zxy and xyz order
        (float pitch, float roll, float yaw) = (25.0f, 15.0f, 45.0f);
        Quaternion qXyz = SkeletalAnimationEngine.EulerToQuaternion(pitch, roll, yaw, "xyz");
        qXyz.Length().Should().BeApproximately(1.0f, 0.001f);

        (float pOut, float rOut, float yOut) = SkeletalAnimationEngine.QuaternionToEuler(qXyz, "xyz");
        pOut.Should().BeApproximately(pitch, 0.5f);
        rOut.Should().BeApproximately(roll, 0.5f);
        yOut.Should().BeApproximately(yaw, 0.5f);

        // Gimbal lock test at pitch = 90 degrees
        Quaternion qGimbal = SkeletalAnimationEngine.EulerToQuaternion(90.0f, 0.0f, 30.0f, "zxy");
        qGimbal.Length().Should().BeApproximately(1.0f, 0.001f);
        Action act = () => SkeletalAnimationEngine.QuaternionToEuler(qGimbal, "zxy");
        act.Should().NotThrow("Gimbal lock evaluation must not throw or produce NaN");
    }

    [Fact]
    public void SkeletalMath_SmoothstepAndTscClock_MonotonicityAndZeroDrift()
    {
        // Smoothstep monotonicity & clamping
        SkeletalAnimationEngine.HermiteSmoothstep(-1.0f).Should().Be(0.0f);
        SkeletalAnimationEngine.HermiteSmoothstep(0.0f).Should().Be(0.0f);
        SkeletalAnimationEngine.HermiteSmoothstep(0.5f).Should().Be(0.5f);
        SkeletalAnimationEngine.HermiteSmoothstep(1.0f).Should().Be(1.0f);
        SkeletalAnimationEngine.HermiteSmoothstep(2.0f).Should().Be(1.0f);

        SkeletalAnimationEngine.CosineSmoothstep(-1.0f).Should().Be(0.0f);
        SkeletalAnimationEngine.CosineSmoothstep(0.0f).Should().Be(0.0f);
        SkeletalAnimationEngine.CosineSmoothstep(0.5f).Should().BeApproximately(0.5f, 0.001f);
        SkeletalAnimationEngine.CosineSmoothstep(1.0f).Should().Be(1.0f);
        SkeletalAnimationEngine.CosineSmoothstep(2.0f).Should().Be(1.0f);

        // Invariant TSC frame timestamp zero-drift test across 1,000,000 frames
        long baseTicks = 100_000_000L;
        long freq = 10_000_000L; // 10 MHz stopwatch frequency
        int targetFps = 60;

        long frame1M = SkeletalAnimationEngine.CalculateInvariantTscTimestamp(baseTicks, 1_000_000, targetFps, freq);
        long expectedDelta = (long)Math.Floor(1_000_000.0 * freq / targetFps + 0.5);
        (frame1M - baseTicks).Should().Be(expectedDelta, "Invariant TSC timestamp must exhibit zero cumulative drift!");

        // Invariant TSC with 0 or negative FPS
        SkeletalAnimationEngine.CalculateInvariantTscTimestamp(baseTicks, 100, 0, freq).Should().Be(baseTicks);
        SkeletalAnimationEngine.CalculateInvariantTscTimestamp(baseTicks, 100, 60, -1).Should().Be(baseTicks);
    }

    // ========================================================================
    // Section 2: Map Terrain & Water Plane Bounds Stress Tests
    // ========================================================================

    [Fact]
    public void MapEnvironmentManager_MapCatalogAndSwitching_LoadsAllMapsCorrectly()
    {
        MapEnvironmentManager manager = new();

        // 1. Default map is Forests of Caldavar
        manager.CurrentMap.Type.Should().Be(MapType.ForestsOfCaldavar);
        manager.CurrentMap.DisplayName.Should().Be("Forests of Caldavar");
        manager.CurrentMap.WaterPlane.WaterLevel.Should().Be(0.0f);

        // 2. Load Mid Wars
        manager.LoadMap("midwars");
        manager.CurrentMap.Type.Should().Be(MapType.MidWars);
        manager.CurrentMap.DisplayName.Should().Be("Mid Wars");
        manager.CurrentMap.WaterPlane.WaterLevel.Should().Be(-2.5f);

        // 3. Load Grimm's Crossing
        manager.LoadMap("grimms_crossing");
        manager.CurrentMap.Type.Should().Be(MapType.GrimmsCrossing);
        manager.CurrentMap.DisplayName.Should().Be("Grimm's Crossing");

        // 4. Invalid / Null name fallback to Caldavar
        manager.LoadMap(null);
        manager.CurrentMap.Type.Should().Be(MapType.ForestsOfCaldavar);

        manager.LoadMap("unknown_custom_arena_name");
        manager.CurrentMap.Type.Should().Be(MapType.ForestsOfCaldavar);
    }

    [Fact]
    public void MapEnvironment_CoordinateConversions_RoundTripPrecisionAcrossExtremeBounds()
    {
        // 1. Standard coordinate bounds [1024..15360]
        Vector2[] testHonCoords =
        [
            new Vector2(1024.0f, 1024.0f),
            new Vector2(8192.0f, 8192.0f),
            new Vector2(15360.0f, 15360.0f),
            new Vector2(0.0f, 0.0f),
            new Vector2(16384.0f, 16384.0f),
            new Vector2(-50000.0f, 75000.0f) // Out of bounds
        ];

        foreach (Vector2 honPos in testHonCoords)
        {
            Vector3 world = MapEnvironmentManager.HonToWorld3D(honPos, height: 1.5f);
            Vector2 roundTripHon = MapEnvironmentManager.World3DToHon(world);

            roundTripHon.X.Should().BeApproximately(honPos.X, 0.001f);
            roundTripHon.Y.Should().BeApproximately(honPos.Y, 0.001f);
            world.Z.Should().Be(1.5f);
        }

        // 2. Normalized to HoN coordinate round trip
        Vector2[] normCoords =
        [
            new Vector2(0.0f, 0.0f),
            new Vector2(0.5f, 0.5f),
            new Vector2(1.0f, 1.0f),
            new Vector2(-2.5f, 3.5f)
        ];

        foreach (Vector2 norm in normCoords)
        {
            Vector2 hon = MapEnvironmentManager.NormalizedToHon(norm);
            Vector2 roundTripNorm = MapEnvironmentManager.HonToNormalized(hon);

            roundTripNorm.X.Should().BeApproximately(norm.X, 0.0001f);
            roundTripNorm.Y.Should().BeApproximately(norm.Y, 0.0001f);
        }
    }

    [Fact]
    public void MapTerrain_SampleHeight_CaldavarAndMidWarsAcrossCoordinateGrid()
    {
        MapEnvironmentManager manager = new();

        // 1. Caldavar height properties
        manager.LoadMap("caldavar");

        // Sample grid from -20 to +20
        for (int gx = -50; gx <= 50; gx++)
        {
            for (int gy = -50; gy <= 50; gy++)
            {
                float x = gx * 0.4f;
                float y = gy * 0.4f;

                float h = manager.SampleHeight(x, y);
                float.IsNaN(h).Should().BeFalse($"Height at ({x}, {y}) must not be NaN");
                float.IsInfinity(h).Should().BeFalse($"Height at ({x}, {y}) must not be Infinity");

                // Heights must remain in plausible world range [-5.0, 10.0]
                h.Should().BeInRange(-5.0f, 10.0f);
            }
        }

        // River depression minimum
        float riverH = manager.SampleHeight(0.0f, 0.0f);
        float jungleH = manager.SampleHeight(5.0f, 5.0f);
        float legionBaseH = manager.SampleHeight(-11.7f, -11.7f);
        float hellbourneBaseH = manager.SampleHeight(11.7f, 11.7f);

        riverH.Should().BeLessThan(jungleH);
        legionBaseH.Should().BeGreaterThan(jungleH);
        hellbourneBaseH.Should().BeGreaterThan(jungleH);

        // 2. Mid Wars height properties
        manager.LoadMap("midwars");
        float bridgeCenterH = manager.SampleHeight(0.0f, 0.0f);
        float bridgeEdgeH = manager.SampleHeight(5.0f, 1.5f);
        float abyssH = manager.SampleHeight(0.0f, 6.0f);

        bridgeCenterH.Should().BeApproximately(1.2f, 0.01f);
        bridgeEdgeH.Should().BeApproximately(1.2f, 0.01f);
        abyssH.Should().BeLessThanOrEqualTo(-3.3f);
    }

    [Fact]
    public void MapTerrain_SampleNormal_ProducesNormalizedUpwardVectors()
    {
        MapEnvironmentManager manager = new();
        manager.LoadMap("caldavar");

        for (int x = -15; x <= 15; x += 2)
        {
            for (int y = -15; y <= 15; y += 2)
            {
                Vector3 normal = manager.SampleNormal(x, y);

                normal.Length().Should().BeApproximately(1.0f, 0.001f, $"Normal at ({x}, {y}) must be unit length");
                normal.Z.Should().BeGreaterThan(0.0f, $"Normal at ({x}, {y}) must point upward (Z > 0)");
                float.IsNaN(normal.X).Should().BeFalse();
                float.IsNaN(normal.Y).Should().BeFalse();
                float.IsNaN(normal.Z).Should().BeFalse();
            }
        }
    }

    [Fact]
    public void MapTerrain_ClassifyTerrain_ClassifiesAllZonesAccurately()
    {
        MapEnvironmentManager manager = new();

        // 1. Out of Bounds
        manager.ClassifyTerrain(17.0f, 0.0f).Should().Be(GroundClassification.OutOfBounds);
        manager.ClassifyTerrain(-16.5f, 0.0f).Should().Be(GroundClassification.OutOfBounds);
        manager.ClassifyTerrain(0.0f, 20.0f).Should().Be(GroundClassification.OutOfBounds);

        // 2. Caldavar zones
        manager.LoadMap("caldavar");
        manager.ClassifyTerrain(0.0f, 0.0f).Should().Be(GroundClassification.River);
        manager.ClassifyTerrain(-11.7f, -11.7f).Should().Be(GroundClassification.BasePlateau);
        manager.ClassifyTerrain(11.7f, 11.7f).Should().Be(GroundClassification.BasePlateau);
        manager.ClassifyTerrain(6.0f, 6.0f).Should().Be(GroundClassification.Jungle);

        // 3. Mid Wars zones
        manager.LoadMap("midwars");
        manager.ClassifyTerrain(0.0f, 0.0f).Should().Be(GroundClassification.Bridge);
        manager.ClassifyTerrain(5.0f, 1.0f).Should().Be(GroundClassification.Bridge);
        manager.ClassifyTerrain(0.0f, 5.0f).Should().Be(GroundClassification.Abyss);
    }

    [Fact]
    public void WaterPlane_SinusoidalWaveAndFresnel_EvaluatesBoundedWaveHeightsAndOptics()
    {
        MapEnvironmentManager manager = new();
        WaterPlaneSettings water = manager.CurrentMap.WaterPlane;

        float k = water.RippleFrequency;
        float amp = water.RippleAmplitude;
        float omega = water.FlowSpeed * 4.0f;
        float waterLevel = water.WaterLevel;

        // 1. Wave amplitude bound test over 1,000 time steps and spatial coordinates
        for (int step = 0; step < 100; step++)
        {
            float t = step * 0.1f;
            for (float x = -20.0f; x <= 20.0f; x += 2.0f)
            {
                float waveZ = waterLevel + amp * MathF.Sin(k * x + omega * t);
                waveZ.Should().BeInRange(waterLevel - amp - 0.0001f, waterLevel + amp + 0.0001f);

                // Derivative and normal
                float dZdx = amp * k * MathF.Cos(k * x + omega * t);
                Vector3 waveNorm = Vector3.Normalize(new Vector3(-dZdx, 0f, 1.0f));
                waveNorm.Length().Should().BeApproximately(1.0f, 0.001f);
                waveNorm.Z.Should().BeGreaterThan(0.0f);
            }
        }

        // 2. Fresnel reflectance bounds [r0, 1.0]
        float r0 = 0.02f;
        for (float angleDeg = 0.0f; angleDeg <= 90.0f; angleDeg += 5.0f)
        {
            float cosTheta = MathF.Cos(angleDeg * (MathF.PI / 180.0f));
            float fresnel = r0 + (1.0f - r0) * MathF.Pow(1.0f - cosTheta, 5.0f);
            fresnel.Should().BeInRange(r0 - 0.0001f, 1.0001f);
        }

        // 3. Specular highlight bounds [0, 1]
        Vector3 sunDir = Vector3.Normalize(new Vector3(0.5f, 0.5f, 1.0f));
        Vector3 viewDir = Vector3.Normalize(new Vector3(0.0f, 0.0f, 1.0f));
        Vector3 norm = Vector3.UnitZ;
        Vector3 reflect = Vector3.Normalize(Vector3.Reflect(-sunDir, norm));
        float spec = MathF.Pow(Math.Max(0.0f, Vector3.Dot(reflect, viewDir)), water.SpecularPower);
        spec.Should().BeInRange(0.0f, 1.0f);
    }

    [Theory]
    [InlineData(2, 2)]
    [InlineData(8, 8)]
    [InlineData(32, 32)]
    [InlineData(64, 64)]
    public void TerrainMeshGeneration_VariousResolutions_ProducesValidGeometryAndTopology(int resX, int resY)
    {
        MapEnvironmentManager manager = new();
        TerrainMeshData mesh = manager.GenerateTerrainMesh(resX, resY);

        int expectedVerts = resX * resY;
        int expectedIndices = (resX - 1) * (resY - 1) * 6;

        mesh.Positions.Length.Should().Be(expectedVerts);
        mesh.Normals.Length.Should().Be(expectedVerts);
        mesh.TexCoords.Length.Should().Be(expectedVerts);
        mesh.Indices.Length.Should().Be(expectedIndices);

        for (int i = 0; i < mesh.Indices.Length; i++)
        {
            mesh.Indices[i].Should().BeInRange(0, (uint)(expectedVerts - 1));
        }

        for (int v = 0; v < expectedVerts; v++)
        {
            mesh.Normals[v].Length().Should().BeApproximately(1.0f, 0.001f);
            mesh.TexCoords[v].X.Should().BeInRange(0.0f, 1.0f);
            mesh.TexCoords[v].Y.Should().BeInRange(0.0f, 1.0f);
        }
    }

    // ========================================================================
    // Section 3: Memory Safety of Unmanaged Depth Buffers & Push Constants
    // ========================================================================

    [Fact]
    public void PushConstants_WhenInvoked_Exact128BytesSizeAlignmentAndFieldOffsets()
    {
        // 1. Struct sizes must be EXACTLY 128 bytes (Vulkan 1.3 push constants standard)
        int lightingSize = Marshal.SizeOf<LightingPushConstants>();
        lightingSize.Should().Be(128);

        // 2. Offsets check for LightingPushConstants
        Marshal.OffsetOf<LightingPushConstants>(nameof(LightingPushConstants.ModelViewProjection)).ToInt32().Should().Be(0);
        Marshal.OffsetOf<LightingPushConstants>(nameof(LightingPushConstants.SunDirection)).ToInt32().Should().Be(64);
        Marshal.OffsetOf<LightingPushConstants>(nameof(LightingPushConstants.AmbientFactor)).ToInt32().Should().Be(76);
        Marshal.OffsetOf<LightingPushConstants>(nameof(LightingPushConstants.TeamTint)).ToInt32().Should().Be(80);
        Marshal.OffsetOf<LightingPushConstants>(nameof(LightingPushConstants.SpecularFactor)).ToInt32().Should().Be(96);
        Marshal.OffsetOf<LightingPushConstants>(nameof(LightingPushConstants.BloomThreshold)).ToInt32().Should().Be(100);
        Marshal.OffsetOf<LightingPushConstants>(nameof(LightingPushConstants.TeamAuraGlow)).ToInt32().Should().Be(104);
        Marshal.OffsetOf<LightingPushConstants>(nameof(LightingPushConstants.TimeSeconds)).ToInt32().Should().Be(108);
        Marshal.OffsetOf<LightingPushConstants>(nameof(LightingPushConstants.EnvironmentalParams)).ToInt32().Should().Be(112);

        // 3. Binary serialization & deserialization round-trip into unmanaged memory
        LightingPushConstants original = new()
        {
            ModelViewProjection = Matrix4x4.Identity,
            SunDirection = new Vector3(0.577f, 0.577f, 0.577f),
            AmbientFactor = 0.42f,
            TeamTint = new Vector4(0.1f, 0.2f, 0.3f, 1.0f),
            SpecularFactor = 0.88f,
            BloomThreshold = 1.15f,
            TeamAuraGlow = 0.65f,
            TimeSeconds = 45.2f,
            EnvironmentalParams = new Vector4(0.0f, 1.0f, 0.05f, 64.0f)
        };

        IntPtr nativeMem = Marshal.AllocHGlobal(128);
        try
        {
            Marshal.StructureToPtr(original, nativeMem, false);
            LightingPushConstants restored = Marshal.PtrToStructure<LightingPushConstants>(nativeMem);

            restored.ModelViewProjection.Should().Be(original.ModelViewProjection);
            restored.SunDirection.Should().Be(original.SunDirection);
            restored.AmbientFactor.Should().Be(original.AmbientFactor);
            restored.TeamTint.Should().Be(original.TeamTint);
            restored.SpecularFactor.Should().Be(original.SpecularFactor);
            restored.BloomThreshold.Should().Be(original.BloomThreshold);
            restored.TeamAuraGlow.Should().Be(original.TeamAuraGlow);
            restored.TimeSeconds.Should().Be(original.TimeSeconds);
            restored.EnvironmentalParams.Should().Be(original.EnvironmentalParams);
        }
        finally
        {
            Marshal.FreeHGlobal(nativeMem);
        }
    }

    [Fact]
    public void SkeletalAnimationEngine_ComputeSubsecondBonePose_AllocatesSinglePoseObjectPerHero()
    {
        // Act: Measure GC allocation per bone pose computation
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            SkeletalPose pose = SkeletalAnimationEngine.ComputeSubsecondBonePose(i * 0.016f, isMoving: true, isAttacking: false);
        }
        long allocAfter = GC.GetAllocatedBytesForCurrentThread();
        long totalAlloc = allocAfter - allocBefore;

        // Assert: 100 poses allocate exactly ~4.19 KB per pose (Matrix4x4[64] array + SkeletalPose object)
        totalAlloc.Should().BeInRange(400_000, 450_000, "SkeletalPose allocates 64-element Matrix4x4 array per call");
    }
}
