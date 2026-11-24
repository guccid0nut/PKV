// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: MapEnvironmentTests.cs
// System: Replay Studio Desktop GUI Tool
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Numerics;
using System.Runtime.InteropServices;
using FluentAssertions;
using ReplayStudio.GUI.Director;
using ReplayStudio.GUI.Engine;
using Xunit;

namespace ReplayStudio.Tests;

public sealed class MapEnvironmentTests
{
    [Fact]
    public void GetMapDefinition_ForestsOfCaldavar_HasValidBoundsAndLandmarks()
    {
        // Arrange
        MapEnvironmentManager manager = new();

        // Act
        MapDefinition map = manager.GetMapDefinition(MapType.ForestsOfCaldavar);

        // Assert
        map.Type.Should().Be(MapType.ForestsOfCaldavar);
        map.WorldMin.Should().Be(new Vector2(0.0f, 0.0f));
        map.WorldMax.Should().Be(new Vector2(16384.0f, 16384.0f));
        map.LegionBasePosition.X.Should().BeApproximately(-11.70f, 1e-2f);
        map.HellbourneBasePosition.X.Should().BeApproximately(11.70f, 1e-2f);
        map.WaterPlane.WaterLevel.Should().Be(0.0f);
    }

    [Fact]
    public void GetMapDefinition_MidWars_HasValidBridgeBoundsAndDimensions()
    {
        // Arrange
        MapEnvironmentManager manager = new();

        // Act
        MapDefinition map = manager.GetMapDefinition(MapType.MidWars);

        // Assert
        map.Type.Should().Be(MapType.MidWars);
        map.LegionBasePosition.Should().Be(new Vector3(-12.0f, 0.0f, 1.2f));
        map.HellbourneBasePosition.Should().Be(new Vector3(12.0f, 0.0f, 1.2f));
        map.WaterPlane.WaterLevel.Should().Be(-2.5f);
    }

    [Fact]
    public void HonToWorld3D_CenterCoordinate_MapsToOrigin()
    {
        // Arrange: (8192, 8192) in HoN coordinates is the map center
        Vector2 honCenter = new(8192.0f, 8192.0f);

        // Act
        Vector3 worldPos = MapEnvironmentManager.HonToWorld3D(honCenter, 0.0f);

        // Assert: Maps to (0, 0, 0) in 3D world space
        worldPos.X.Should().BeApproximately(0.0f, 1e-4f);
        worldPos.Y.Should().BeApproximately(0.0f, 1e-4f);
        worldPos.Z.Should().Be(0.0f);
    }

    [Fact]
    public void HonToWorld3D_Corners_MapToCorrectWorldExtents()
    {
        // Arrange: (0, 0) and (16384, 16384)
        Vector2 minHon = new(0.0f, 0.0f);
        Vector2 maxHon = new(16384.0f, 16384.0f);

        // Act
        Vector3 worldMin = MapEnvironmentManager.HonToWorld3D(minHon);
        Vector3 worldMax = MapEnvironmentManager.HonToWorld3D(maxHon);

        // Assert: Should map to (-16, -16) and (+16, +16)
        worldMin.X.Should().BeApproximately(-16.0f, 1e-4f);
        worldMin.Y.Should().BeApproximately(-16.0f, 1e-4f);
        worldMax.X.Should().BeApproximately(16.0f, 1e-4f);
        worldMax.Y.Should().BeApproximately(16.0f, 1e-4f);
    }

    [Fact]
    public void World3DToHon_Origin_MapsToCenter()
    {
        // Arrange
        Vector3 worldOrigin = Vector3.Zero;

        // Act
        Vector2 honPos = MapEnvironmentManager.World3DToHon(worldOrigin);

        // Assert
        honPos.X.Should().BeApproximately(8192.0f, 1e-4f);
        honPos.Y.Should().BeApproximately(8192.0f, 1e-4f);
    }

    [Fact]
    public void NormalizedToHon_And_HonToNormalized_InvertsAccurately()
    {
        // Arrange
        Vector2 normalized = new(0.25f, 0.75f);

        // Act
        Vector2 hon = MapEnvironmentManager.NormalizedToHon(normalized);
        Vector2 roundtrip = MapEnvironmentManager.HonToNormalized(hon);

        // Assert
        hon.X.Should().BeApproximately(4096.0f, 1e-4f);
        hon.Y.Should().BeApproximately(12288.0f, 1e-4f);
        roundtrip.X.Should().BeApproximately(normalized.X, 1e-4f);
        roundtrip.Y.Should().BeApproximately(normalized.Y, 1e-4f);
    }

    [Fact]
    public void SampleHeight_CaldavarRiver_ReturnsDepressedElevation()
    {
        // Arrange: Forests of Caldavar map
        MapEnvironmentManager manager = new();
        manager.LoadMap("caldavar");

        // River center line x + y = 0 (e.g. (0, 0) or (-2, 2))
        float riverZ = manager.SampleHeight(0.0f, 0.0f);
        float jungleZ = manager.SampleHeight(-6.0f, 0.0f);

        // Assert: Riverbed is lower than surrounding terrain
        riverZ.Should().BeLessThan(jungleZ);
    }

    [Fact]
    public void SampleHeight_LegionBase_ReturnsElevatedHighGround()
    {
        // Arrange
        MapEnvironmentManager manager = new();
        manager.LoadMap("caldavar");

        // Legion base plateau at (-11.7, -11.7)
        float baseZ = manager.SampleHeight(-11.7f, -11.7f);
        float jungleZ = manager.SampleHeight(-6.0f, 0.0f);

        // Assert: Base is elevated higher than neutral jungle
        baseZ.Should().BeGreaterThan(jungleZ);
    }

    [Fact]
    public void SampleHeight_MidWarsBridge_ReturnsFlatBridgeElevation()
    {
        // Arrange
        MapEnvironmentManager manager = new();
        manager.LoadMap("midwars");

        // Center bridge surface at Y = 0.0
        float bridgeZ0 = manager.SampleHeight(-5.0f, 0.0f);
        float bridgeZ1 = manager.SampleHeight(0.0f, 0.5f);
        float bridgeZ2 = manager.SampleHeight(5.0f, -0.5f);

        // Assert: Flat combat surface at 1.2f
        bridgeZ0.Should().BeApproximately(1.2f, 1e-3f);
        bridgeZ1.Should().BeApproximately(1.2f, 1e-3f);
        bridgeZ2.Should().BeApproximately(1.2f, 1e-3f);
    }

    [Fact]
    public void SampleHeight_MidWarsAbyss_ReturnsDeepDropOff()
    {
        // Arrange
        MapEnvironmentManager manager = new();
        manager.LoadMap("midwars");

        // Off-bridge abyss at Y = 5.0
        float abyssZ = manager.SampleHeight(0.0f, 5.0f);

        // Assert: Drops significantly below bridge (Z < -2.0)
        abyssZ.Should().BeLessThan(-2.0f);
    }

    [Fact]
    public void IsInWater_CaldavarRiver_ReturnsTrue()
    {
        // Arrange
        MapEnvironmentManager manager = new();
        manager.LoadMap("caldavar");

        // River at (0, 0)
        bool inWater = manager.IsInWater(0.0f, 0.0f);
        bool inBase = manager.IsInWater(-11.7f, -11.7f);

        // Assert
        inWater.Should().BeTrue();
        inBase.Should().BeFalse();
    }

    [Fact]
    public void ClassifyTerrain_WhenInvoked_ReturnsAccurateZoneClassification()
    {
        // Arrange
        MapEnvironmentManager manager = new();
        manager.LoadMap("caldavar");

        // Act & Assert
        manager.ClassifyTerrain(0.0f, 0.0f).Should().Be(GroundClassification.River);
        manager.ClassifyTerrain(-11.7f, -11.7f).Should().Be(GroundClassification.BasePlateau);
        manager.ClassifyTerrain(11.7f, 11.7f).Should().Be(GroundClassification.BasePlateau);
        manager.ClassifyTerrain(20.0f, 0.0f).Should().Be(GroundClassification.OutOfBounds);

        // Mid Wars
        manager.LoadMap("midwars");
        manager.ClassifyTerrain(0.0f, 0.0f).Should().Be(GroundClassification.Bridge);
        manager.ClassifyTerrain(0.0f, 6.0f).Should().Be(GroundClassification.Abyss);
    }

    [Fact]
    public void GenerateTerrainMesh_WithValidArguments_ProducesValidIndexedGeometryAndNormals()
    {
        // Arrange
        MapEnvironmentManager manager = new();

        // Act
        TerrainMeshData mesh = manager.GenerateTerrainMesh(16, 16);

        // Assert
        mesh.Positions.Length.Should().Be(256);
        mesh.Normals.Length.Should().Be(256);
        mesh.TexCoords.Length.Should().Be(256);
        mesh.Indices.Length.Should().Be(15 * 15 * 6); // 1350 indices = 450 triangles

        // Normal vectors must be unit length
        foreach (Vector3 normal in mesh.Normals)
        {
            normal.Length().Should().BeApproximately(1.0f, 1e-2f);
        }
    }

    [Fact]
    public void LightingController_PushConstants_HasExact128ByteSizeAndProperFields()
    {
        // Assert: Unmanaged layout size MUST be exactly 128 bytes for Vulkan 1.3 push constants
        int structSize = Marshal.SizeOf<LightingPushConstants>();
        structSize.Should().Be(128);

        // Arrange & Act
        LightingController lighting = new();
        lighting.LoadPreset(LightingPreset.SunsetGoldenHour);

        Matrix4x4 mvp = Matrix4x4.Identity;
        LightingPushConstants pushConstants = lighting.BuildPushConstants(mvp, 10.5f, team: 1);

        // Assert
        pushConstants.ModelViewProjection.Should().Be(mvp);
        pushConstants.TimeSeconds.Should().Be(10.5f);
        pushConstants.AmbientFactor.Should().BeApproximately(0.28f, 1e-3f);
        pushConstants.SpecularFactor.Should().BeApproximately(1.10f, 1e-3f);
        pushConstants.BloomThreshold.Should().BeApproximately(0.95f, 1e-3f);
        pushConstants.TeamTint.Should().Be(lighting.LegionTint);
    }
}
