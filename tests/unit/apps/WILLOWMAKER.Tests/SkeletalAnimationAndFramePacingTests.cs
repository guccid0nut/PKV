// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Numerics;
using FluentAssertions;
using ReplayVulkanRenderer;
using Xunit;

namespace WILLOWMAKER.Tests;

public sealed class SkeletalAnimationAndFramePacingTests
{
    [Fact]
    public void NormalizeQuaternion_ZeroLength_ReturnsIdentity()
    {
        Quaternion zero = new(0f, 0f, 0f, 0f);
        Quaternion normalized = SkeletalAnimationEngine.NormalizeQuaternion(zero);

        normalized.Should().Be(Quaternion.Identity);
    }

    [Fact]
    public void Slerp_AntipodalQuaternions_TakesShortestArcWithZeroDeviation()
    {
        // Arrange: create a rotation and its exact antipodal negation (-q represents same 3D orientation)
        Quaternion q1 = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 4f);
        Quaternion q2 = new(-q1.X, -q1.Y, -q1.Z, -q1.W);

        // Act: interpolate at t=0.5
        Quaternion result = SkeletalAnimationEngine.Slerp(q1, q2, 0.5f);

        // Assert: Angular distance must be zero (no 360-degree flip)
        float angularDist = SkeletalAnimationEngine.AngularDistanceDeg(q1, result);
        angularDist.Should().BeLessThan(0.001f);
    }

    [Fact]
    public void Slerp_NearParallelQuaternions_TriggersNLerpWithoutSingularityOrNaN()
    {
        // Arrange: small angle delta (dot > 0.9995)
        Quaternion q1 = Quaternion.Identity;
        Quaternion q2 = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.001f);

        // Act
        Quaternion result = SkeletalAnimationEngine.Slerp(q1, q2, 0.5f);

        // Assert
        float.IsNaN(result.X).Should().BeFalse();
        float.IsNaN(result.Y).Should().BeFalse();
        float.IsNaN(result.Z).Should().BeFalse();
        float.IsNaN(result.W).Should().BeFalse();

        float angularDist = SkeletalAnimationEngine.AngularDistanceDeg(q1, result);
        angularDist.Should().BeLessThan(0.1f);
    }

    [Fact]
    public void Slerp_BoundaryValues_ReturnsExactEndPoints()
    {
        Quaternion q1 = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.3f);
        Quaternion q2 = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.7f);

        Quaternion at0 = SkeletalAnimationEngine.Slerp(q1, q2, 0.0f);
        Quaternion at1 = SkeletalAnimationEngine.Slerp(q1, q2, 1.0f);

        SkeletalAnimationEngine.AngularDistanceDeg(q1, at0).Should().BeLessThan(0.0001f);
        SkeletalAnimationEngine.AngularDistanceDeg(q2, at1).Should().BeLessThan(0.0001f);
    }

    [Fact]
    public void SkeletalAnimation_SmoothstepCurves_SatisfyBoundaryAndMonotonicityConstraints()
    {
        // Test Hermite Smoothstep
        SkeletalAnimationEngine.HermiteSmoothstep(0.0f).Should().Be(0.0f);
        SkeletalAnimationEngine.HermiteSmoothstep(1.0f).Should().Be(1.0f);
        SkeletalAnimationEngine.HermiteSmoothstep(0.5f).Should().Be(0.5f);
        SkeletalAnimationEngine.HermiteSmoothstep(-0.5f).Should().Be(0.0f);
        SkeletalAnimationEngine.HermiteSmoothstep(1.5f).Should().Be(1.0f);

        // Test Cosine Smoothstep
        SkeletalAnimationEngine.CosineSmoothstep(0.0f).Should().Be(0.0f);
        SkeletalAnimationEngine.CosineSmoothstep(1.0f).Should().Be(1.0f);
        SkeletalAnimationEngine.CosineSmoothstep(0.5f).Should().Be(0.5f);

        // Monotonicity check
        float prev = 0.0f;
        for (float u = 0.05f; u <= 1.0f; u += 0.05f)
        {
            float val = SkeletalAnimationEngine.HermiteSmoothstep(u);
            val.Should().BeGreaterThanOrEqualTo(prev);
            prev = val;
        }
    }

    [Fact]
    public void SkeletalAnimation_EulerAndQuaternionConversions_AreConsistent()
    {
        float pitch = 15.0f;
        float roll = 30.0f;
        float yaw = 45.0f;

        Quaternion q = SkeletalAnimationEngine.EulerToQuaternion(pitch, roll, yaw, "xyz");
        (float p, float r, float y) = SkeletalAnimationEngine.QuaternionToEuler(q);

        MathF.Abs(p - pitch).Should().BeLessThan(0.01f);
        MathF.Abs(r - roll).Should().BeLessThan(0.01f);
        MathF.Abs(y - yaw).Should().BeLessThan(0.01f);
    }

    [Fact]
    public void SkeletalAnimation_InterpolatePose_BlendsTransformsSmoothly()
    {
        SkeletalPose poseA = new(64);
        SkeletalPose poseB = new(64);

        poseA.BoneTransforms[0] = Matrix4x4.CreateTranslation(0f, 0f, 0f);
        poseB.BoneTransforms[0] = Matrix4x4.CreateTranslation(10f, 20f, 30f);

        SkeletalPose blendedHalf = SkeletalAnimationEngine.InterpolatePose(poseA, poseB, 0.5f);

        blendedHalf.BoneTransforms[0].Translation.X.Should().BeApproximately(5.0f, 0.001f);
        blendedHalf.BoneTransforms[0].Translation.Y.Should().BeApproximately(10.0f, 0.001f);
        blendedHalf.BoneTransforms[0].Translation.Z.Should().BeApproximately(15.0f, 0.001f);
    }

    [Fact]
    public void FramePacing_CalculateInvariantTscTimestamp_MaintainsZeroFrameDriftOver100000FramesAt1000Fps()
    {
        long baseTicks = 10000000;
        long stopwatchFrequency = 10000000; // 10 MHz standard TSC
        int targetFps = 1000;
        long totalFrames = 100000; // 100.0 seconds of gameplay

        for (long frame = 0; frame <= totalFrames; frame += 1000)
        {
            long calculatedTicks = SkeletalAnimationEngine.CalculateInvariantTscTimestamp(
                baseTicks, frame, targetFps, stopwatchFrequency);

            double expectedSeconds = (double)frame / targetFps;
            double actualSeconds = (double)(calculatedTicks - baseTicks) / stopwatchFrequency;

            double timeDriftSeconds = Math.Abs(actualSeconds - expectedSeconds);
            double frameDrift = timeDriftSeconds * targetFps;

            frameDrift.Should().BeLessThan(1.0, "Cumulative frame drift must be < 1 frame.");
            timeDriftSeconds.Should().BeLessThan(0.0001, "Cumulative time drift must be < 0.1ms.");
        }
    }

    [Fact]
    public void FramePacing_CalculateInvariantTscTimestamp_MaintainsZeroFrameDriftOverOneHourAt60Fps()
    {
        long baseTicks = 5000000;
        long stopwatchFrequency = 10000000; // 10 MHz
        int targetFps = 60;
        long totalFrames = 216000; // 3600 seconds = 1 Hour

        long finalTicks = SkeletalAnimationEngine.CalculateInvariantTscTimestamp(
            baseTicks, totalFrames, targetFps, stopwatchFrequency);

        double expectedSeconds = 3600.0;
        double actualSeconds = (double)(finalTicks - baseTicks) / stopwatchFrequency;
        double frameDrift = Math.Abs(actualSeconds - expectedSeconds) * targetFps;

        frameDrift.Should().BeLessThan(0.001, "Frame drift over 1 hour at 60 FPS must be negligible (< 0.001 frames).");
    }

    [Fact]
    public void SkeletalAnimation_DeformVertexSkeletalLbs_ComputesWeightedPositionAccurately()
    {
        SkeletalPose pose = new(64);
        pose.BoneTransforms[1] = Matrix4x4.CreateTranslation(10f, 0f, 0f);
        pose.BoneTransforms[2] = Matrix4x4.CreateTranslation(0f, 20f, 0f);

        Vector3 restPos = new(1f, 1f, 1f);
        Vector4 jointIndices = new(1f, 2f, 0f, 0f);
        Vector4 jointWeights = new(0.5f, 0.5f, 0f, 0f);

        Vector3 animated = SkeletalAnimationEngine.DeformVertexSkeletalLbs(restPos, jointIndices, jointWeights, pose);

        // Expected: 0.5 * (rest + (10,0,0)) + 0.5 * (rest + (0,20,0)) = rest + (5, 10, 0) = (6, 11, 1)
        animated.X.Should().BeApproximately(6.0f, 0.001f);
        animated.Y.Should().BeApproximately(11.0f, 0.001f);
        animated.Z.Should().BeApproximately(1.0f, 0.001f);
    }
}
