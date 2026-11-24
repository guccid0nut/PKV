// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: Feature07_SilkNetVulkanViewportTests.cs
// System: ProjectKongor Solution-Wide E2E Testing Suite
// Tier 1: Feature Coverage (Feature 7: Cross-Platform Silk.NET Vulkan/OpenGL Viewport)
// Policy: Strict Explicit Typing, Opaque-Box Validation (>= 5 tests)
// ============================================================================

using System.Numerics;
using FluentAssertions;
using ReplayVulkanRenderer;

namespace ASPIRE.Tests.E2E.Tier1_FeatureCoverage;

public sealed class SilkNetVulkanViewportTests
{
    [Trait("Category", "Manual")]
    [Fact]
    public void SkeletalAnimationEngine_NormalizeQuaternion_ZeroMagnitudeReturnsIdentity()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange
        Quaternion zeroQuat = new(0f, 0f, 0f, 0f);

        // Act
        Quaternion result = SkeletalAnimationEngine.NormalizeQuaternion(zeroQuat);

        // Assert
        result.Should().Be(Quaternion.Identity);
    }

    [Fact]
    public void SkeletalAnimationEngine_Slerp_ExactEndpointsMatch()
    {
        // Arrange
        Quaternion q1 = Quaternion.Identity;
        Quaternion q2 = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);

        // Act
        Quaternion atStart = SkeletalAnimationEngine.Slerp(q1, q2, 0.0f);
        Quaternion atEnd = SkeletalAnimationEngine.Slerp(q1, q2, 1.0f);

        // Assert
        atStart.X.Should().BeApproximately(q1.X, 1e-4f);
        atStart.Y.Should().BeApproximately(q1.Y, 1e-4f);
        atStart.Z.Should().BeApproximately(q1.Z, 1e-4f);
        atStart.W.Should().BeApproximately(q1.W, 1e-4f);

        atEnd.X.Should().BeApproximately(q2.X, 1e-4f);
        atEnd.Y.Should().BeApproximately(q2.Y, 1e-4f);
        atEnd.Z.Should().BeApproximately(q2.Z, 1e-4f);
        atEnd.W.Should().BeApproximately(q2.W, 1e-4f);
    }

    [Fact]
    public void SkeletalAnimationEngine_Slerp_MidpointInterpolatesExpectedRotation()
    {
        // Arrange
        Quaternion q1 = Quaternion.Identity;
        Quaternion q2 = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);

        // Act
        Quaternion midpoint = SkeletalAnimationEngine.Slerp(q1, q2, 0.5f);
        Quaternion expected = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 4f);

        // Assert
        midpoint.X.Should().BeApproximately(expected.X, 1e-3f);
        midpoint.Y.Should().BeApproximately(expected.Y, 1e-3f);
        midpoint.Z.Should().BeApproximately(expected.Z, 1e-3f);
        midpoint.W.Should().BeApproximately(expected.W, 1e-3f);
    }

    [Fact]
    public void SkeletalAnimationEngine_Slerp_ClampsOutofBoundsTimeParameters()
    {
        // Arrange
        Quaternion q1 = Quaternion.Identity;
        Quaternion q2 = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 3f);

        // Act
        Quaternion underClamped = SkeletalAnimationEngine.Slerp(q1, q2, -0.5f);
        Quaternion overClamped = SkeletalAnimationEngine.Slerp(q1, q2, 1.5f);

        // Assert
        underClamped.W.Should().BeApproximately(q1.W, 1e-4f);
        overClamped.W.Should().BeApproximately(q2.W, 1e-4f);
    }

    [Fact]
    public void SkeletalPose_Initialization_PopulatesIdentityMatrices()
    {
        // Arrange & Act
        SkeletalPose pose = new(32);

        // Assert
        pose.BoneTransforms.Length.Should().Be(32);
        for (int i = 0; i < 32; i++) pose.BoneTransforms[i].Should().Be(Matrix4x4.Identity);
    }

    [Fact]
    public void SkeletalAnimationEngine_AntipodalQuaternions_SelectsShortestArc()
    {
        // Arrange
        Quaternion q1 = Quaternion.Identity;
        Quaternion q2 = -Quaternion.Identity; // Opposite representation of same rotation

        // Act
        Quaternion interp = SkeletalAnimationEngine.Slerp(q1, q2, 0.5f);

        // Assert
        interp.Length().Should().BeApproximately(1.0f, 1e-4f);
    }
}
