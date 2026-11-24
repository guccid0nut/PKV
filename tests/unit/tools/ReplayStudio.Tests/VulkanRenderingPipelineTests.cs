// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: VulkanRenderingPipelineTests.cs
// System: Replay Studio Desktop GUI Tool
// Policy: Strict Explicit Typing (NO 'var')
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

public sealed unsafe class VulkanRenderingPipelineTests
{
    [Fact]
    public void PushConstants_SizeAndAlignment_Exactly128Bytes()
    {
        // Assert: Struct sizes must match Vulkan 1.3 push constant limits exactly
        int lightingPushConstantsSize = Marshal.SizeOf<LightingPushConstants>();
        lightingPushConstantsSize.Should().Be(128, "LightingPushConstants must be exactly 128 bytes");

        // Verify Field Offsets
        Marshal.OffsetOf<LightingPushConstants>(nameof(LightingPushConstants.ModelViewProjection)).ToInt32().Should().Be(0);
        Marshal.OffsetOf<LightingPushConstants>(nameof(LightingPushConstants.SunDirection)).ToInt32().Should().Be(64);
        Marshal.OffsetOf<LightingPushConstants>(nameof(LightingPushConstants.AmbientFactor)).ToInt32().Should().Be(76);
        Marshal.OffsetOf<LightingPushConstants>(nameof(LightingPushConstants.TeamTint)).ToInt32().Should().Be(80);
        Marshal.OffsetOf<LightingPushConstants>(nameof(LightingPushConstants.SpecularFactor)).ToInt32().Should().Be(96);
        Marshal.OffsetOf<LightingPushConstants>(nameof(LightingPushConstants.BloomThreshold)).ToInt32().Should().Be(100);
        Marshal.OffsetOf<LightingPushConstants>(nameof(LightingPushConstants.TeamAuraGlow)).ToInt32().Should().Be(104);
        Marshal.OffsetOf<LightingPushConstants>(nameof(LightingPushConstants.TimeSeconds)).ToInt32().Should().Be(108);
        Marshal.OffsetOf<LightingPushConstants>(nameof(LightingPushConstants.EnvironmentalParams)).ToInt32().Should().Be(112);
    }

    [Fact]
    public void BuildPushConstants_LightingController_ProducesValidUniformValues()
    {
        // Arrange
        LightingController controller = new();
        controller.SetSunAngles(azimuthDeg: 45.0f, elevationDeg: 60.0f);
        Matrix4x4 testMvp = Matrix4x4.CreateLookAt(new Vector3(0, 0, 10), Vector3.Zero, Vector3.UnitY);
        Vector4 envParams = new(0.0f, 1.0f, 0.05f, 64.0f);

        // Act
        LightingPushConstants pc = controller.BuildPushConstants(testMvp, timeSeconds: 12.5f, team: 1, envParams: envParams);

        // Assert
        pc.ModelViewProjection.Should().Be(testMvp);
        pc.TimeSeconds.Should().Be(12.5f);
        pc.AmbientFactor.Should().Be(controller.AmbientFactor);
        pc.SpecularFactor.Should().Be(controller.SpecularFactor);
        pc.BloomThreshold.Should().Be(controller.BloomThreshold);
        pc.TeamAuraGlow.Should().Be(controller.TeamAuraIntensity);
        pc.TeamTint.Should().Be(controller.LegionTint);
        pc.EnvironmentalParams.Should().Be(envParams);

        // Sun Direction must be unit length
        pc.SunDirection.Length().Should().BeApproximately(1.0f, 0.001f);
    }

    [Fact]
    public void SkeletalAnimation_ComputeBoneTransforms_Generates64BonesAndValidLbsDeformation()
    {
        // Arrange
        float timestamp = 2.5f;

        // Act
        SkeletalPose pose = SkeletalAnimationEngine.ComputeSubsecondBonePose(timestamp, isMoving: true, isAttacking: false);

        // Assert
        pose.Should().NotBeNull();
        pose.BoneTransforms.Length.Should().Be(64);

        // Verify root bone sway/bounce
        Matrix4x4 rootTransform = pose.BoneTransforms[0];
        rootTransform.M41.Should().NotBe(float.NaN); // Sway X
        rootTransform.M43.Should().NotBe(float.NaN); // Bounce Z

        // Verify LBS skinning deformation
        Vector3 restPos = new(0.0f, 0.0f, 1.0f);
        Vector4 jointIndices = new(0, 1, 0, 0);
        Vector4 jointWeights = new(0.5f, 0.5f, 0f, 0f);

        Vector3 animatedPos = SkeletalAnimationEngine.DeformVertexSkeletalLbs(restPos, jointIndices, jointWeights, pose);
        animatedPos.Should().NotBe(Vector3.Zero);
        animatedPos.Z.Should().BeGreaterThan(0.5f);
    }

    [Fact]
    public void TerrainMeshGeneration_ForestsOfCaldavar_ValidVerticesAndHeights()
    {
        // Arrange
        MapEnvironmentManager manager = new();
        manager.LoadMap("caldavar");

        // Act
        TerrainMeshData mesh = manager.GenerateTerrainMesh(gridResX: 16, gridResY: 16);

        // Assert
        mesh.Positions.Length.Should().Be(256);
        mesh.Normals.Length.Should().Be(256);
        mesh.TexCoords.Length.Should().Be(256);
        mesh.Indices.Length.Should().Be(15 * 15 * 6);

        // River diagonal (0, 0) should be depressed relative to base plateau (-11.7, -11.7)
        float riverHeight = manager.SampleHeight(0.0f, 0.0f);
        float baseHeight = manager.SampleHeight(-11.7f, -11.7f);

        riverHeight.Should().BeLessThan(baseHeight);
    }

    [Fact]
    public void TerrainMeshGeneration_MidWars_ValidBridgeAndAbyssHeights()
    {
        // Arrange
        MapEnvironmentManager manager = new();
        manager.LoadMap("midwars");

        // Act
        float bridgeHeight = manager.SampleHeight(0.0f, 0.0f); // Central lane deck
        float abyssHeight = manager.SampleHeight(0.0f, 6.0f);  // Off-bridge chasm

        // Assert
        bridgeHeight.Should().BeApproximately(1.2f, 0.01f);
        abyssHeight.Should().BeLessThan(-2.0f);

        GroundClassification bridgeClass = manager.ClassifyTerrain(0.0f, 0.0f);
        GroundClassification abyssClass = manager.ClassifyTerrain(0.0f, 6.0f);

        bridgeClass.Should().Be(GroundClassification.Bridge);
        abyssClass.Should().Be(GroundClassification.Abyss);
    }

    [Fact]
    public void WaterPlanePhysics_SinusoidalWaveAndFresnel_ComputesExpectedWaveDisplacement()
    {
        // Arrange
        MapEnvironmentManager manager = new();
        WaterPlaneSettings water = manager.CurrentMap.WaterPlane;
        float x = 5.0f;
        float k = water.RippleFrequency;
        float amp = water.RippleAmplitude;
        float omega = water.FlowSpeed * 4.0f;

        // Act: Evaluate wave at t0 and t1
        float t0 = 0.0f;
        float t1 = 1.0f;
        float z0 = water.WaterLevel + amp * MathF.Sin(k * x + omega * t0);
        float z1 = water.WaterLevel + amp * MathF.Sin(k * x + omega * t1);

        // Assert
        z0.Should().BeInRange(water.WaterLevel - amp - 0.001f, water.WaterLevel + amp + 0.001f);
        z1.Should().BeInRange(water.WaterLevel - amp - 0.001f, water.WaterLevel + amp + 0.001f);
    }
}
