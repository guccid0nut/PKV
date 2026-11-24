// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: M1EngineAndMathChallengerTests.cs
// System: Replay Studio Desktop GUI Tool
// Policy: Strict Explicit Typing (NO 'var')
// Challenger: Challenger 2 (Empirical Adversarial Math & Engine Stress Testing)
// ============================================================================

using System.Numerics;
using FluentAssertions;
using ReplayStudio.GUI.Director;
using ReplayStudio.GUI.Engine;
using ReplayStudio.GUI.Models;
using Xunit;

namespace ReplayStudio.Tests;

public sealed class ReplayEngineAndMathTests
{
    // ========================================================================
    // 1. Camera Matrices Validity & Invertibility Stress Tests
    // ========================================================================

    [Theory]
    [InlineData(CameraMode.PlayerPov)]
    [InlineData(CameraMode.FirstPerson)]
    [InlineData(CameraMode.RtsIsometric)]
    [InlineData(CameraMode.FreeSpectator)]
    [InlineData(CameraMode.CinematicSpline)]
    public void CameraMatrices_All5Modalities_ProduceNonNanNonInfInvertibleMatrices(CameraMode mode)
    {
        // Arrange
        CameraDirector director = new() { Mode = mode };

        // If spline mode, populate with initial sample nodes
        if (mode == CameraMode.CinematicSpline)
        {
            director.Spline.AddNode(new DirectorNode
            {
                TimestampMs = 0,
                Position = new Vector3(-10.0f, -10.0f, 15.0f),
                LookAtTarget = Vector3.Zero,
                Orientation = Quaternion.Identity,
                FovDegrees = 55.0f
            });
            director.Spline.AddNode(new DirectorNode
            {
                TimestampMs = 10000,
                Position = new Vector3(10.0f, 10.0f, 20.0f),
                LookAtTarget = new Vector3(2.0f, 2.0f, 0.0f),
                Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 4.0f),
                FovDegrees = 65.0f
            });
        }

        Vector3[] heroPositions =
        [
            Vector3.Zero,
            new Vector3(-16.0f, -16.0f, 0.0f),
            new Vector3(16.0f, 16.0f, 2.5f),
            new Vector3(-500.0f, 500.0f, -100.0f),
            new Vector3(1000.0f, -1000.0f, 200.0f)
        ];

        float[] headings = [0.0f, 45.0f, 90.0f, 180.0f, 270.0f, 359.9f, -180.0f, 720.0f];
        float[] aspectRatios = [16.0f / 9.0f, 4.0f / 3.0f, 21.0f / 9.0f, 1.0f, 9.0f / 16.0f];
        float[] fovs = [15.0f, 45.0f, 55.0f, 75.0f, 90.0f, 120.0f];

        // Act & Assert across all parameter permutations
        foreach (Vector3 heroPos in heroPositions)
        {
            foreach (float heading in headings)
            {
                foreach (float aspect in aspectRatios)
                {
                    foreach (float fov in fovs)
                    {
                        director.FovDegrees = fov;
                        CameraState state = director.Update(0.0166f, heroPos, heading, 5000, aspect);

                        // 1. Position, Target, Up finiteness
                        float.IsNaN(state.Position.X).Should().BeFalse();
                        float.IsNaN(state.Position.Y).Should().BeFalse();
                        float.IsNaN(state.Position.Z).Should().BeFalse();
                        float.IsInfinity(state.Position.X).Should().BeFalse();
                        float.IsInfinity(state.Position.Y).Should().BeFalse();
                        float.IsInfinity(state.Position.Z).Should().BeFalse();

                        float.IsNaN(state.Target.X).Should().BeFalse();
                        float.IsNaN(state.Target.Y).Should().BeFalse();
                        float.IsNaN(state.Target.Z).Should().BeFalse();
                        float.IsInfinity(state.Target.X).Should().BeFalse();
                        float.IsInfinity(state.Target.Y).Should().BeFalse();
                        float.IsInfinity(state.Target.Z).Should().BeFalse();

                        float.IsNaN(state.Up.X).Should().BeFalse();
                        float.IsNaN(state.Up.Y).Should().BeFalse();
                        float.IsNaN(state.Up.Z).Should().BeFalse();
                        float.IsInfinity(state.Up.X).Should().BeFalse();
                        float.IsInfinity(state.Up.Y).Should().BeFalse();
                        float.IsInfinity(state.Up.Z).Should().BeFalse();
                        state.Up.Length().Should().BeGreaterThan(0.001f);

                        // 2. ViewMatrix properties
                        Matrix4x4 vm = state.ViewMatrix;
                        float.IsNaN(vm.M11).Should().BeFalse();
                        float.IsNaN(vm.M22).Should().BeFalse();
                        float.IsNaN(vm.M33).Should().BeFalse();
                        float.IsNaN(vm.M44).Should().BeFalse();
                        float.IsInfinity(vm.M11).Should().BeFalse();
                        float.IsInfinity(vm.M22).Should().BeFalse();
                        float.IsInfinity(vm.M33).Should().BeFalse();
                        float.IsInfinity(vm.M44).Should().BeFalse();

                        bool viewInvertible = Matrix4x4.Invert(vm, out Matrix4x4 _);
                        viewInvertible.Should().BeTrue();
                        float viewDet = vm.GetDeterminant();
                        MathF.Abs(viewDet).Should().BeGreaterThan(1e-4f);

                        // 3. ProjectionMatrix properties
                        Matrix4x4 pm = state.ProjectionMatrix;
                        float.IsNaN(pm.M11).Should().BeFalse();
                        float.IsNaN(pm.M22).Should().BeFalse();
                        float.IsNaN(pm.M33).Should().BeFalse();
                        float.IsNaN(pm.M44).Should().BeFalse();
                        float.IsInfinity(pm.M11).Should().BeFalse();
                        float.IsInfinity(pm.M22).Should().BeFalse();
                        float.IsInfinity(pm.M33).Should().BeFalse();

                        bool projInvertible = Matrix4x4.Invert(pm, out Matrix4x4 _);
                        projInvertible.Should().BeTrue();
                        float projDet = pm.GetDeterminant();
                        MathF.Abs(projDet).Should().BeGreaterThan(1e-12f);

                        // 4. ViewProjection combined matrix properties
                        Matrix4x4 vpm = state.ViewProjectionMatrix;
                        bool vpInvertible = Matrix4x4.Invert(vpm, out Matrix4x4 _);
                        vpInvertible.Should().BeTrue();
                        float vpDet = vpm.GetDeterminant();
                        MathF.Abs(vpDet).Should().BeGreaterThan(1e-16f);
                    }
                }
            }
        }
    }

    // ========================================================================
    // 2. Free Spectator Gimbal Lock Resistance Stress Tests
    // ========================================================================

    [Theory]
    [InlineData(-89.0f, 0.0f)]
    [InlineData(89.0f, 0.0f)]
    [InlineData(-89.0f, 45.0f)]
    [InlineData(89.0f, 45.0f)]
    [InlineData(-89.0f, 90.0f)]
    [InlineData(89.0f, 90.0f)]
    [InlineData(-89.0f, 180.0f)]
    [InlineData(89.0f, 180.0f)]
    [InlineData(-89.0f, 270.0f)]
    [InlineData(89.0f, 270.0f)]
    [InlineData(-90.0f, 45.0f)] // Beyond clamp: -90 should be clamped to -89
    [InlineData(90.0f, 45.0f)]  // Beyond clamp: +90 should be clamped to +89
    [InlineData(-120.0f, 135.0f)]
    [InlineData(120.0f, 135.0f)]
    [InlineData(-720.0f, 0.0f)]
    [InlineData(720.0f, 0.0f)]
    public void FreeSpectator_GimbalLockResistance_MaintainsOrthonormalBasisAndInvertibility(float testPitch, float testYaw)
    {
        // Arrange
        CameraDirector director = new() { Mode = CameraMode.FreeSpectator };
        director.SetFreeSpectatorOrientation(testYaw, testPitch);

        // Act
        CameraState state = director.Update(0.016f, Vector3.Zero, 0.0f, 0, 16.0f / 9.0f);

        // Assert
        // Pitch must be clamped within [-89, 89]
        director.FreePitchDeg.Should().BeInRange(-89.0f, 89.0f);

        Vector3 forward = Vector3.Normalize(state.Target - state.Position);
        Vector3 up = state.Up;

        float yawRad = director.FreeYawDeg * (MathF.PI / 180.0f);
        Vector3 right = Vector3.Normalize(new Vector3(-MathF.Sin(yawRad), MathF.Cos(yawRad), 0.0f));

        // Orthonormal basis checks:
        // 1. Lengths are unit length
        forward.Length().Should().BeApproximately(1.0f, 1e-4f);
        right.Length().Should().BeApproximately(1.0f, 1e-4f);
        up.Length().Should().BeApproximately(1.0f, 1e-4f);

        // 2. Dot products between basis vectors are zero (perpendicular)
        Vector3.Dot(forward, right).Should().BeApproximately(0.0f, 1e-4f);
        Vector3.Dot(forward, up).Should().BeApproximately(0.0f, 1e-4f);
        Vector3.Dot(right, up).Should().BeApproximately(0.0f, 1e-4f);

        // 3. View matrix is invertible with non-zero determinant
        bool invertible = Matrix4x4.Invert(state.ViewMatrix, out Matrix4x4 _);
        invertible.Should().BeTrue();
        MathF.Abs(state.ViewMatrix.GetDeterminant()).Should().BeGreaterThan(0.5f);
    }

    [Fact]
    public void FreeSpectator_DirectPropertyMutation_BeyondClamp_ClampsGracefullyInUpdate()
    {
        // Arrange: Directly set unvalidated pitch to 95 degrees (overhead flip)
        CameraDirector director = new()
        {
            Mode = CameraMode.FreeSpectator,
            FreePitchDeg = 95.0f,
            FreeYawDeg = 60.0f
        };

        // Act
        CameraState state = director.Update(0.016f, Vector3.Zero, 0.0f, 0, 16.0f / 9.0f);

        // Assert: Update should clamp to 89 degrees and produce valid non-NaN matrices
        float.IsNaN(state.Up.X).Should().BeFalse();
        float.IsNaN(state.Up.Y).Should().BeFalse();
        float.IsNaN(state.Up.Z).Should().BeFalse();
        state.Up.Length().Should().BeApproximately(1.0f, 1e-4f);

        bool invertible = Matrix4x4.Invert(state.ViewMatrix, out Matrix4x4 _);
        invertible.Should().BeTrue();
    }

    [Fact]
    public void FreeSpectator_MultiAxisSpatialTranslation_3DMovementsRemainFiniteAndAccurate()
    {
        // Arrange
        CameraDirector director = new()
        {
            Mode = CameraMode.FreeSpectator,
            MoveSpeed = 10.0f
        };
        director.ResetPosition(Vector3.Zero, Vector3.UnitY);
        director.SetFreeSpectatorOrientation(yawDeg: 0.0f, pitchDeg: 0.0f); // Facing +X

        // Act: Move diagonally (X=1, Y=1, Z=1) with sprint = true (2.5x multiplier)
        Vector3 input = new(1.0f, 1.0f, 1.0f);
        float dt = 0.5f; // 0.5s at 25 speed = 12.5 magnitude along each component
        director.TranslateFreeSpectator(input, dt, sprint: true);

        // Assert
        float.IsNaN(director.CurrentEye.X).Should().BeFalse();
        float.IsNaN(director.CurrentEye.Y).Should().BeFalse();
        float.IsNaN(director.CurrentEye.Z).Should().BeFalse();

        // Forward (+Y in input space) moved along +X by 12.5
        // Right (+X in input space) moved along +Y by 12.5
        // Up (+Z in input space) moved along +Z by 12.5
        director.CurrentEye.X.Should().BeApproximately(12.5f, 1e-3f);
        director.CurrentEye.Y.Should().BeApproximately(12.5f, 1e-3f);
        director.CurrentEye.Z.Should().BeApproximately(12.5f, 1e-3f);
    }

    // ========================================================================
    // 3. Catmull-Rom Spline Stress Tests (1, 2, 3, 100 Nodes, Duplicates, Zero Tension)
    // ========================================================================

    [Fact]
    public void CatmullRomSpline_ZeroNodes_ReturnsDefaultSafeState()
    {
        // Arrange
        CatmullRomSpline spline = new();

        // Act
        (Vector3 pos, Vector3 target, Quaternion rot, float fov) = spline.Evaluate(5000);

        // Assert
        pos.Should().Be(new Vector3(0, -14, 18));
        target.Should().Be(Vector3.Zero);
        rot.Should().Be(Quaternion.Identity);
        fov.Should().Be(55.0f);
    }

    [Fact]
    public void CatmullRomSpline_OneNode_EvaluatesToExactNodeAttributesAtAnyTimestamp()
    {
        // Arrange
        CatmullRomSpline spline = new();
        DirectorNode node = new()
        {
            TimestampMs = 5000,
            Position = new Vector3(12.0f, -34.0f, 56.0f),
            LookAtTarget = new Vector3(1.0f, 2.0f, 3.0f),
            Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1.23f),
            FovDegrees = 72.5f,
            Tension = 0.5f
        };
        spline.AddNode(node);

        int[] timestampsToTest = [-10000, 0, 2500, 5000, 7500, 100000, int.MaxValue / 2];

        // Act & Assert
        foreach (int t in timestampsToTest)
        {
            (Vector3 pos, Vector3 target, Quaternion rot, float fov) = spline.Evaluate(t);
            pos.Should().Be(node.Position);
            target.Should().Be(node.LookAtTarget);
            rot.Should().Be(node.Orientation);
            fov.Should().Be(node.FovDegrees);
        }
    }

    [Fact]
    public void CatmullRomSpline_TwoNodes_PerformsExactLinearInterpolationBetweenEndpoints()
    {
        // Arrange
        CatmullRomSpline spline = new();
        spline.AddNode(new DirectorNode
        {
            TimestampMs = 1000,
            Position = new Vector3(0.0f, 0.0f, 0.0f),
            LookAtTarget = new Vector3(0.0f, 0.0f, 0.0f),
            FovDegrees = 40.0f
        });
        spline.AddNode(new DirectorNode
        {
            TimestampMs = 5000,
            Position = new Vector3(40.0f, 80.0f, 120.0f),
            LookAtTarget = new Vector3(4.0f, 8.0f, 12.0f),
            FovDegrees = 80.0f
        });

        // Act & Assert: Before t0
        (Vector3 posBefore, _, _, float fovBefore) = spline.Evaluate(500);
        posBefore.Should().Be(new Vector3(0, 0, 0));
        fovBefore.Should().Be(40.0f);

        // After t1
        (Vector3 posAfter, _, _, float fovAfter) = spline.Evaluate(7000);
        posAfter.Should().Be(new Vector3(40.0f, 80.0f, 120.0f));
        fovAfter.Should().Be(80.0f);

        // Exact Quarter (t = 2000, alpha = 0.25)
        (Vector3 posQ, Vector3 targetQ, _, float fovQ) = spline.Evaluate(2000);
        posQ.X.Should().BeApproximately(10.0f, 1e-4f);
        posQ.Y.Should().BeApproximately(20.0f, 1e-4f);
        posQ.Z.Should().BeApproximately(30.0f, 1e-4f);
        targetQ.X.Should().BeApproximately(1.0f, 1e-4f);
        fovQ.Should().BeApproximately(50.0f, 1e-4f);

        // Exact Midpoint (t = 3000, alpha = 0.5)
        (Vector3 posMid, Vector3 targetMid, _, float fovMid) = spline.Evaluate(3000);
        posMid.X.Should().BeApproximately(20.0f, 1e-4f);
        posMid.Y.Should().BeApproximately(40.0f, 1e-4f);
        posMid.Z.Should().BeApproximately(60.0f, 1e-4f);
        targetMid.X.Should().BeApproximately(2.0f, 1e-4f);
        fovMid.Should().BeApproximately(60.0f, 1e-4f);
    }

    [Fact]
    public void CatmullRomSpline_ThreeNodes_EvaluatesSmoothCubicSegmentsWithContinuousKnot()
    {
        // Arrange
        CatmullRomSpline spline = new();
        spline.AddNode(new DirectorNode { TimestampMs = 0, Position = new Vector3(0.0f, 0.0f, 0.0f) });
        spline.AddNode(new DirectorNode { TimestampMs = 1000, Position = new Vector3(10.0f, 20.0f, 5.0f) });
        spline.AddNode(new DirectorNode { TimestampMs = 2000, Position = new Vector3(30.0f, 10.0f, 15.0f) });

        // Act: Evaluate across 100 steps from 0 to 2000ms
        Vector3 prevPos = spline.Evaluate(0).Position;
        for (int t = 20; t <= 2000; t += 20)
        {
            (Vector3 pos, Vector3 target, Quaternion rot, float fov) = spline.Evaluate(t);

            // Assert: No NaN, no Inf
            float.IsNaN(pos.X).Should().BeFalse();
            float.IsNaN(pos.Y).Should().BeFalse();
            float.IsNaN(pos.Z).Should().BeFalse();

            // Delta step continuity: no massive teleports (> 5.0 units per 20ms)
            float stepDist = Vector3.Distance(pos, prevPos);
            stepDist.Should().BeLessThan(5.0f);
            prevPos = pos;
        }

        // At exactly knot t = 1000ms, should match node 1
        (Vector3 knotPos, _, _, _) = spline.Evaluate(1000);
        knotPos.X.Should().BeApproximately(10.0f, 1e-4f);
        knotPos.Y.Should().BeApproximately(20.0f, 1e-4f);
        knotPos.Z.Should().BeApproximately(5.0f, 1e-4f);
    }

    [Fact]
    public void CatmullRomSpline_OneHundredNodes_DenseTimelineEvaluationIsStableAndSmooth()
    {
        // Arrange: Generate 100 nodes along a winding 3D trajectory
        CatmullRomSpline spline = new();
        const int nodeCount = 100;
        for (int i = 0; i < nodeCount; i++)
        {
            float theta = i * 0.2f;
            float radius = 10.0f + i * 0.5f;
            DirectorNode node = new()
            {
                TimestampMs = i * 1000,
                Position = new Vector3(radius * MathF.Cos(theta), radius * MathF.Sin(theta), i * 0.3f),
                LookAtTarget = new Vector3(0.0f, 0.0f, i * 0.1f),
                Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, theta),
                FovDegrees = 50.0f + 20.0f * MathF.Sin(theta),
                Tension = 0.5f
            };
            spline.AddNode(node);
        }

        spline.Count.Should().Be(100);

        // Act: Sample 2,000 discrete timestamps across 0..99000ms
        Vector3 lastPos = spline.Evaluate(0).Position;
        for (int sample = 0; sample <= 2000; sample++)
        {
            int timestampMs = (int)(sample * (99000.0f / 2000.0f));
            (Vector3 pos, Vector3 target, Quaternion rot, float fov) = spline.Evaluate(timestampMs);

            // Assert
            float.IsNaN(pos.X).Should().BeFalse();
            float.IsNaN(pos.Y).Should().BeFalse();
            float.IsNaN(pos.Z).Should().BeFalse();

            float.IsNaN(target.X).Should().BeFalse();
            float.IsNaN(target.Y).Should().BeFalse();
            float.IsNaN(target.Z).Should().BeFalse();

            // Quaternion must remain normalized
            rot.Length().Should().BeApproximately(1.0f, 1e-3f);
            fov.Should().BeInRange(30.0f, 70.01f);

            lastPos = pos;
        }
    }

    [Fact]
    public void CatmullRomSpline_DuplicateTimestamps_EvaluatesSafelyWithoutDivideByZero()
    {
        // Arrange: Nodes with identical timestamps (degenerate timeline interval)
        CatmullRomSpline spline = new();
        spline.AddNode(new DirectorNode { TimestampMs = 1000, Position = new Vector3(0, 0, 0) });
        spline.AddNode(new DirectorNode { TimestampMs = 1000, Position = new Vector3(10, 10, 10) }); // Duplicate t=1000
        spline.AddNode(new DirectorNode { TimestampMs = 2000, Position = new Vector3(20, 20, 20) });
        spline.AddNode(new DirectorNode { TimestampMs = 2000, Position = new Vector3(30, 30, 30) }); // Duplicate t=2000

        // Act & Assert: Evaluating at duplicate timestamp boundary must not throw or return NaN
        int[] timestamps = [500, 1000, 1500, 2000, 2500];
        foreach (int t in timestamps)
        {
            (Vector3 pos, Vector3 target, Quaternion rot, float fov) = spline.Evaluate(t);

            float.IsNaN(pos.X).Should().BeFalse();
            float.IsNaN(pos.Y).Should().BeFalse();
            float.IsNaN(pos.Z).Should().BeFalse();
            float.IsInfinity(pos.X).Should().BeFalse();
            float.IsInfinity(pos.Y).Should().BeFalse();
            float.IsInfinity(pos.Z).Should().BeFalse();
        }
    }

    [Fact]
    public void CatmullRomSpline_ZeroAndExtremeTension_RemainsStable()
    {
        // Arrange
        CatmullRomSpline spline = new();
        spline.AddNode(new DirectorNode { TimestampMs = 0, Position = new Vector3(0, 0, 0), Tension = 0.0f });
        spline.AddNode(new DirectorNode { TimestampMs = 1000, Position = new Vector3(10, 0, 0), Tension = 0.0f });
        spline.AddNode(new DirectorNode { TimestampMs = 2000, Position = new Vector3(20, 0, 0), Tension = 0.0f });
        spline.AddNode(new DirectorNode { TimestampMs = 3000, Position = new Vector3(30, 0, 0), Tension = 0.0f });

        // Act
        (Vector3 pos, Vector3 target, Quaternion rot, float fov) = spline.Evaluate(1500);

        // Assert
        pos.X.Should().BeApproximately(15.0f, 1e-3f);
        float.IsNaN(pos.X).Should().BeFalse();
    }

    // ========================================================================
    // 4. Map Coordinate Conversions Roundtrips (HoN <-> World <-> Normalized)
    // ========================================================================

    [Fact]
    public void MapCoordinateConversion_HonToWorldToHon_ExactRoundtripAcrossFullRange()
    {
        // Arrange: Sample distinct coordinates across interior and exterior domains
        float[] testHonCoords =
        [
            0.0f, 1.0f, 1024.0f, 4096.0f, 8192.0f, 12288.0f, 15360.0f, 16384.0f,
            -5000.0f, 25000.0f, 1234.5678f, 9876.543f, 16383.999f
        ];

        foreach (float x in testHonCoords)
        {
            foreach (float y in testHonCoords)
            {
                Vector2 originalHon = new(x, y);

                // Act: HoN -> World3D -> HoN
                Vector3 world = MapEnvironmentManager.HonToWorld3D(originalHon, height: 3.5f);
                Vector2 roundtripHon = MapEnvironmentManager.World3DToHon(world);

                // Assert: Precision loss must be < 1e-3 HoN units
                roundtripHon.X.Should().BeApproximately(originalHon.X, 1e-3f);
                roundtripHon.Y.Should().BeApproximately(originalHon.Y, 1e-3f);
                world.Z.Should().Be(3.5f);
            }
        }
    }

    [Fact]
    public void MapCoordinateConversion_WorldToHonToWorld_ExactRoundtripAcrossFullRange()
    {
        // Arrange: Sample coordinates in world space [-32.0, +32.0]
        float[] testWorldCoords =
        [
            -16.0f, -11.7f, -8.0f, 0.0f, 8.0f, 11.7f, 16.0f,
            -32.0f, 32.0f, 1.23456f, -7.89101f
        ];

        foreach (float wx in testWorldCoords)
        {
            foreach (float wy in testWorldCoords)
            {
                Vector3 originalWorld = new(wx, wy, 2.0f);

                // Act: World -> HoN -> World
                Vector2 hon = MapEnvironmentManager.World3DToHon(originalWorld);
                Vector3 roundtripWorld = MapEnvironmentManager.HonToWorld3D(hon, height: 2.0f);

                // Assert
                roundtripWorld.X.Should().BeApproximately(originalWorld.X, 1e-4f);
                roundtripWorld.Y.Should().BeApproximately(originalWorld.Y, 1e-4f);
                roundtripWorld.Z.Should().Be(2.0f);
            }
        }
    }

    [Fact]
    public void MapCoordinateConversion_NormalizedToHonToNormalized_ExactRoundtrip()
    {
        // Arrange: Normalized coords [0.0 .. 1.0] and extended range [-0.5 .. 1.5]
        float[] testNormCoords = [0.0f, 0.0625f, 0.25f, 0.5f, 0.75f, 0.9375f, 1.0f, -0.2f, 1.5f, 0.123456f];

        foreach (float nx in testNormCoords)
        {
            foreach (float ny in testNormCoords)
            {
                Vector2 originalNorm = new(nx, ny);

                // Act: Normalized -> HoN -> Normalized
                Vector2 hon = MapEnvironmentManager.NormalizedToHon(originalNorm);
                Vector2 roundtripNorm = MapEnvironmentManager.HonToNormalized(hon);

                // Assert
                roundtripNorm.X.Should().BeApproximately(originalNorm.X, 1e-5f);
                roundtripNorm.Y.Should().BeApproximately(originalNorm.Y, 1e-5f);
            }
        }
    }

    [Fact]
    public void MapCoordinateConversion_ThreeWayFullRoundtrip_PreservesCoordinateIntegrity()
    {
        // Arrange: Start in World 3D, go to HoN, then to Normalized, then back to HoN, then back to World 3D
        Vector3 startWorld = new(-5.75f, 8.25f, 1.4f);

        // Act
        Vector2 step1Hon = MapEnvironmentManager.World3DToHon(startWorld);
        Vector2 step2Norm = MapEnvironmentManager.HonToNormalized(step1Hon);
        Vector2 step3Hon = MapEnvironmentManager.NormalizedToHon(step2Norm);
        Vector3 step4World = MapEnvironmentManager.HonToWorld3D(step3Hon, height: startWorld.Z);

        // Assert
        step4World.X.Should().BeApproximately(startWorld.X, 1e-4f);
        step4World.Y.Should().BeApproximately(startWorld.Y, 1e-4f);
        step4World.Z.Should().Be(startWorld.Z);
    }
}
