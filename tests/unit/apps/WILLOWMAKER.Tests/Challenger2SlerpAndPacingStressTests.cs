// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Numerics;
using FluentAssertions;
using ReplayVulkanRenderer;
using WILLOWMAKER.Tests.Infrastructure;
using Xunit;

namespace WILLOWMAKER.Tests;

public sealed class Challenger2SlerpAndPacingStressTests
{
    private static Quaternion CreateRandomUnitQuaternion(Random rng)
    {
        // Uniform distribution on S^3 using Marsaglia algorithm
        float u1 = (float)rng.NextDouble();
        float u2 = (float)rng.NextDouble();
        float u3 = (float)rng.NextDouble();

        float sqrt1MinusU1 = MathF.Sqrt(1f - u1);
        float sqrtU1 = MathF.Sqrt(u1);

        float theta1 = 2f * MathF.PI * u2;
        float theta2 = 2f * MathF.PI * u3;

        return new Quaternion(
            sqrt1MinusU1 * MathF.Sin(theta1),
            sqrt1MinusU1 * MathF.Cos(theta1),
            sqrtU1 * MathF.Sin(theta2),
            sqrtU1 * MathF.Cos(theta2)
        );
    }

    [Trait("Category", "Manual")]
    [Fact]
    public void StressTest_ShortestArcAntipodalSlerp_10000RandomPairs_NeverTraversesLongArc()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        Random rng = new(1337);
        const int iterations = 10000;

        for (int i = 0; i < iterations; i++)
        {
            Quaternion qA = CreateRandomUnitQuaternion(rng);
            Quaternion qB = CreateRandomUnitQuaternion(rng);

            // Compute direct angular distance in 3D
            float initialDistance = SkeletalAnimationEngine.AngularDistanceDeg(qA, qB);

            // Midpoint Slerp
            Quaternion qMid = SkeletalAnimationEngine.Slerp(qA, qB, 0.5f);
            float distMidA = SkeletalAnimationEngine.AngularDistanceDeg(qA, qMid);
            float distMidB = SkeletalAnimationEngine.AngularDistanceDeg(qMid, qB);

            // Midpoint must be equidistant and half of total shortest distance
            distMidA.Should()
                .BeApproximately(initialDistance * 0.5f, 0.05f, $"Midpoint to A deviated at iteration {i}");
            distMidB.Should()
                .BeApproximately(initialDistance * 0.5f, 0.05f, $"Midpoint to B deviated at iteration {i}");

            // Maximum geodesic distance on SO(3) is 180 degrees
            initialDistance.Should().BeLessThanOrEqualTo(180.001f);
            distMidA.Should().BeLessThanOrEqualTo(90.001f);

            // Test antipodal reflection explicitly
            Quaternion qNegB = new(-qB.X, -qB.Y, -qB.Z, -qB.W);
            Quaternion qMidNeg = SkeletalAnimationEngine.Slerp(qA, qNegB, 0.5f);
            float distNeg = SkeletalAnimationEngine.AngularDistanceDeg(qMid, qMidNeg);
            distNeg.Should().BeLessThan(0.001f,
                $"Antipodal reflection produced different midpoint rotation at iteration {i}");
        }
    }

    [Trait("Category", "Manual")]
    [Fact]
    public void StressTest_NLerpSingularityFallback_MicroSteppingAroundThreshold()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        // Micro-step dot products right across the 0.9995 threshold
        float[] testDots =
        [
            0.999000f, 0.999400f, 0.999490f, 0.999499f,
            0.999500f, 0.999501f, 0.999510f, 0.999600f,
            0.999900f, 0.999990f, 0.999999f, 1.000000f
        ];

        Quaternion q1 = Quaternion.Identity;

        foreach (float dot in testDots)
        {
            float theta = MathF.Acos(Math.Clamp(dot, -1f, 1f));
            Quaternion q2 = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, theta);

            for (float t = 0f; t <= 1f; t += 0.1f)
            {
                Quaternion interp = SkeletalAnimationEngine.Slerp(q1, q2, t);

                // Must be valid unit quaternion
                float.IsNaN(interp.X).Should().BeFalse($"NaN at dot={dot}, t={t}");
                float.IsNaN(interp.Y).Should().BeFalse($"NaN at dot={dot}, t={t}");
                float.IsNaN(interp.Z).Should().BeFalse($"NaN at dot={dot}, t={t}");
                float.IsNaN(interp.W).Should().BeFalse($"NaN at dot={dot}, t={t}");
                float.IsInfinity(interp.X).Should().BeFalse();

                float lengthSq = interp.LengthSquared();
                lengthSq.Should().BeApproximately(1.0f, 1e-4f, $"Length non-unit at dot={dot}, t={t}");

                // Expected angular distance from q1 is t * theta in degrees
                float expectedDistDeg = t * theta * (180f / MathF.PI);
                float actualDistDeg = SkeletalAnimationEngine.AngularDistanceDeg(q1, interp);
                actualDistDeg.Should()
                    .BeApproximately(expectedDistDeg, 0.05f, $"Angular distance error at dot={dot}, t={t}");
            }
        }
    }

    [Trait("Category", "Manual")]
    [Fact]
    public void StressTest_QuaternionNormalization_DegenerateAndExtremeInputs()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        // 1. Zero vector
        Quaternion zero = new(0f, 0f, 0f, 0f);
        SkeletalAnimationEngine.NormalizeQuaternion(zero).Should().Be(Quaternion.Identity);

        // 2. Subnormal near-zero vector (< 1e-6 magnitude)
        Quaternion tiny = new(1e-7f, 1e-7f, 1e-7f, 1e-7f);
        SkeletalAnimationEngine.NormalizeQuaternion(tiny).Should().Be(Quaternion.Identity);

        // 3. Huge magnitude quaternion
        Quaternion huge = new(1e10f, 0f, 0f, 0f);
        Quaternion normHuge = SkeletalAnimationEngine.NormalizeQuaternion(huge);
        normHuge.LengthSquared().Should().BeApproximately(1.0f, 1e-5f);
        normHuge.X.Should().BeApproximately(1.0f, 1e-5f);

        // 4. Slerp with extreme non-unit inputs
        Quaternion unnormA = new(500f, 0f, 0f, 0f);
        Quaternion unnormB = new(0f, 0.001f, 0f, 0f);
        Quaternion blended = SkeletalAnimationEngine.Slerp(unnormA, unnormB, 0.5f);
        blended.LengthSquared().Should().BeApproximately(1.0f, 1e-5f);

        // 5. Clamping out-of-range t
        Quaternion atNeg = SkeletalAnimationEngine.Slerp(Quaternion.Identity,
            Quaternion.CreateFromAxisAngle(Vector3.UnitX, 1.0f), -10.0f);
        Quaternion atZero = SkeletalAnimationEngine.Slerp(Quaternion.Identity,
            Quaternion.CreateFromAxisAngle(Vector3.UnitX, 1.0f), 0.0f);
        atNeg.Should().Be(atZero);

        Quaternion atExcess = SkeletalAnimationEngine.Slerp(Quaternion.Identity,
            Quaternion.CreateFromAxisAngle(Vector3.UnitX, 1.0f), 10.0f);
        Quaternion atOne = SkeletalAnimationEngine.Slerp(Quaternion.Identity,
            Quaternion.CreateFromAxisAngle(Vector3.UnitX, 1.0f), 1.0f);
        atExcess.Should().Be(atOne);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public void StressTest_FractionalTscPacing_100000FramesAt144Fps_ContinuousAnalysis()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        const long baseTicks = 1234567;
        const long stopwatchFrequency = 10000000;
        const int targetFps = 144;
        const long totalFrames = 100000;

        double maxTimeDriftSeconds = 0.0;
        double maxFrameDrift = 0.0;

        for (long frame = 1; frame <= totalFrames; frame++)
        {
            long tscTicks =
                SkeletalAnimationEngine.CalculateInvariantTscTimestamp(baseTicks, frame, targetFps, stopwatchFrequency);
            double idealSeconds = (double)frame / targetFps;
            double actualSeconds = (double)(tscTicks - baseTicks) / stopwatchFrequency;
            double timeDrift = Math.Abs(actualSeconds - idealSeconds);
            double frameDrift = timeDrift * targetFps;
            if (timeDrift > maxTimeDriftSeconds) maxTimeDriftSeconds = timeDrift;
            if (frameDrift > maxFrameDrift) maxFrameDrift = frameDrift;
        }

        maxTimeDriftSeconds.Should().BeLessThan(0.0001);
        maxFrameDrift.Should().BeLessThan(0.01);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public void StressTest_FractionalTscPacing_100000FramesAt1000Fps_ContinuousAnalysis()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        const long baseTicks = 10000000;
        const long stopwatchFrequency = 10000000; // 10 MHz standard Windows QPC
        const int targetFps = 1000;
        const long totalFrames = 100000;

        double maxTimeDriftSeconds = 0.0;
        double maxFrameDrift = 0.0;

        for (long frame = 1; frame <= totalFrames; frame++)
        {
            long tscTicks =
                SkeletalAnimationEngine.CalculateInvariantTscTimestamp(baseTicks, frame, targetFps, stopwatchFrequency);

            double idealSeconds = (double)frame / targetFps;
            double actualSeconds = (double)(tscTicks - baseTicks) / stopwatchFrequency;

            double timeDrift = Math.Abs(actualSeconds - idealSeconds);
            double frameDrift = timeDrift * targetFps;

            if (timeDrift > maxTimeDriftSeconds) maxTimeDriftSeconds = timeDrift;
            if (frameDrift > maxFrameDrift) maxFrameDrift = frameDrift;
        }

        // At 1000 FPS and 10 MHz, ticksPerFrame = 10,000 integer exactly -> 0 drift!
        maxTimeDriftSeconds.Should().BeLessThan(1e-9, "1000 FPS on 10 MHz timer must have zero fractional drift");
        maxFrameDrift.Should().BeLessThan(1e-6, "Frame drift must be zero");
    }

    [Trait("Category", "Manual")]
    [Fact]
    public void StressTest_FractionalTscPacing_100000FramesAt240Fps_ContinuousAnalysis()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        const long baseTicks = 9876543;
        const long stopwatchFrequency = 10000000;
        const int targetFps = 240;
        const long totalFrames = 100000;

        double maxTimeDriftSeconds = 0.0;
        double maxFrameDrift = 0.0;

        for (long frame = 1; frame <= totalFrames; frame++)
        {
            long tscTicks =
                SkeletalAnimationEngine.CalculateInvariantTscTimestamp(baseTicks, frame, targetFps, stopwatchFrequency);
            double idealSeconds = (double)frame / targetFps;
            double actualSeconds = (double)(tscTicks - baseTicks) / stopwatchFrequency;
            double timeDrift = Math.Abs(actualSeconds - idealSeconds);
            double frameDrift = timeDrift * targetFps;
            if (timeDrift > maxTimeDriftSeconds) maxTimeDriftSeconds = timeDrift;
            if (frameDrift > maxFrameDrift) maxFrameDrift = frameDrift;
        }

        maxTimeDriftSeconds.Should().BeLessThan(0.0001);
        maxFrameDrift.Should().BeLessThan(0.01);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public void StressTest_FractionalTscPacing_100000FramesAt60Fps_ContinuousAnalysis()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        const long baseTicks = 5000000;
        const long stopwatchFrequency = 10000000; // 10 MHz
        const int targetFps = 60;
        const long totalFrames = 100000;

        double maxTimeDriftSeconds = 0.0;
        double maxFrameDrift = 0.0;

        for (long frame = 1; frame <= totalFrames; frame++)
        {
            long tscTicks =
                SkeletalAnimationEngine.CalculateInvariantTscTimestamp(baseTicks, frame, targetFps, stopwatchFrequency);

            double idealSeconds = (double)frame / targetFps;
            double actualSeconds = (double)(tscTicks - baseTicks) / stopwatchFrequency;

            double timeDrift = Math.Abs(actualSeconds - idealSeconds);
            double frameDrift = timeDrift * targetFps;

            if (timeDrift > maxTimeDriftSeconds) maxTimeDriftSeconds = timeDrift;
            if (frameDrift > maxFrameDrift) maxFrameDrift = frameDrift;
        }

        // Fractional rounding error for 10MHz QPC at 60 FPS is bounded by 0.5 ticks = 50 ns
        maxTimeDriftSeconds.Should()
            .BeLessThanOrEqualTo(5.001e-8, "Maximum time drift must never exceed 50 nanoseconds (0.5 ticks)");
        maxFrameDrift.Should().BeLessThan(0.0001, "Maximum frame drift across 100,000 frames must be < 0.0001 frames");
    }

    [Trait("Category", "Manual")]
    [Fact]
    public void StressTest_VariableTargetFpsAndZeroBaseTicks_ContinuousFrameAnalysis()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        long[] frequencies = [10_000_000, 3_000_000_000, 2_400_000_000, 14_318_180, 1_000_000];
        int[] fpsList = [60, 120, 144, 240, 500, 1000];

        foreach (long freq in frequencies)
        foreach (int fps in fpsList)
        {
            // Test frame 100,000 and 1,000,000,000 (continuous multi-week server uptime)
            long[] framesToTest = [1, 1000, 60000, 100000, 1000000, 1000000000];

            foreach (long n in framesToTest)
            {
                long tscTicks = SkeletalAnimationEngine.CalculateInvariantTscTimestamp(0, n, fps, freq);

                double idealSeconds = (double)n / fps;
                double actualSeconds = (double)tscTicks / freq;

                double driftSeconds = Math.Abs(actualSeconds - idealSeconds);
                double driftFrames = driftSeconds * fps;

                // Drift should never exceed 1 tick's duration
                double maxExpectedDriftSec = 1.0 / freq + 1e-9;
                driftSeconds.Should().BeLessThanOrEqualTo(maxExpectedDriftSec,
                    $"Drift exceeded 1 tick at freq={freq}, fps={fps}, frame={n}");
                driftFrames.Should().BeLessThan(1.0, $"Drift exceeded 1 frame at freq={freq}, fps={fps}, frame={n}");
            }
        }
    }

    [Fact]
    public void StressTest_PacingInvalidInputs_ReturnsBaseTicksSafely()
    {
        SkeletalAnimationEngine.CalculateInvariantTscTimestamp(100, 50, 0, 10000000).Should().Be(100);
        SkeletalAnimationEngine.CalculateInvariantTscTimestamp(100, 50, -60, 10000000).Should().Be(100);
        SkeletalAnimationEngine.CalculateInvariantTscTimestamp(100, 50, 60, 0).Should().Be(100);
        SkeletalAnimationEngine.CalculateInvariantTscTimestamp(100, 50, 60, -10000000).Should().Be(100);
    }

    [Fact]
    public void StressTest_EulerAngles_GimbalLockAndFullSphereCoverage()
    {
        // Test various Euler combinations including gimbal lock regions (+/- 90 pitch)
        float[] pitches = [-89.9f, -45f, 0f, 45f, 89.9f];
        float[] rolls = [-170f, -90f, 0f, 90f, 170f];
        float[] yaws = [-170f, -90f, 0f, 90f, 170f];

        foreach (float p in pitches)
        foreach (float r in rolls)
        foreach (float y in yaws)
        {
            Quaternion q = SkeletalAnimationEngine.EulerToQuaternion(p, r, y, "xyz");
            q.LengthSquared().Should().BeApproximately(1.0f, 1e-5f);

            (float ep, float er, float ey) = SkeletalAnimationEngine.QuaternionToEuler(q);
            Quaternion qReconstructed = SkeletalAnimationEngine.EulerToQuaternion(ep, er, ey, "xyz");

            float dist = SkeletalAnimationEngine.AngularDistanceDeg(q, qReconstructed);
            dist.Should().BeLessThan(0.01f, $"Euler roundtrip failed for ({p}, {r}, {y})");
        }
    }

    [Fact]
    public void StressTest_LinearBlendSkinning_ConvexCombinationInvariance()
    {
        SkeletalPose pose = new(64);
        Vector3 offset = new(12.5f, -7.25f, 99.0f);

        for (int i = 0; i < 64; i++) pose.BoneTransforms[i] = Matrix4x4.CreateTranslation(offset);

        Vector3 restPos = new(10f, 20f, 30f);
        Vector4 weights = new(0.25f, 0.25f, 0.25f, 0.25f);
        Vector4 indices = new(0, 1, 2, 3);

        Vector3 deformed = SkeletalAnimationEngine.DeformVertexSkeletalLbs(restPos, indices, weights, pose);
        Vector3 expected = restPos + offset;

        deformed.X.Should().BeApproximately(expected.X, 1e-4f);
        deformed.Y.Should().BeApproximately(expected.Y, 1e-4f);
        deformed.Z.Should().BeApproximately(expected.Z, 1e-4f);
    }
}
