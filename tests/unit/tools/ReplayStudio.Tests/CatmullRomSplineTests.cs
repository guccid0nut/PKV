// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: CatmullRomSplineTests.cs
// System: Replay Studio Desktop GUI Tool
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Numerics;
using FluentAssertions;
using ReplayStudio.GUI.Director;
using ReplayStudio.GUI.Models;
using Xunit;

namespace ReplayStudio.Tests;

public sealed class CatmullRomSplineTests
{
    [Fact]
    public void Interpolate_AtEndpoints_MatchesControlPointsExactly()
    {
        // Arrange
        Vector3 p0 = new(0, 0, 0);
        Vector3 p1 = new(10, 20, 30);
        Vector3 p2 = new(40, 50, 60);
        Vector3 p3 = new(70, 80, 90);

        // Act
        Vector3 atU0 = CatmullRomSpline.EvaluateCubic(p0, p1, p2, p3, 0.0f);
        Vector3 atU1 = CatmullRomSpline.EvaluateCubic(p0, p1, p2, p3, 1.0f);

        // Assert: At u=0 it must equal p1, at u=1 it must equal p2
        atU0.Should().Be(p1);
        atU1.Should().Be(p2);
    }

    [Fact]
    public void Interpolate_AtMidpoint_MatchesAlgebraicFormula()
    {
        // Arrange
        Vector3 p0 = new(0, 0, 0);
        Vector3 p1 = new(10, 0, 0);
        Vector3 p2 = new(20, 0, 0);
        Vector3 p3 = new(30, 0, 0);

        // Act: Collinear points with equal spacing should have midpoint exactly at (15, 0, 0)
        Vector3 midpoint = CatmullRomSpline.EvaluateCubic(p0, p1, p2, p3, 0.5f);

        // Assert
        midpoint.X.Should().BeApproximately(15.0f, 1e-4f);
        midpoint.Y.Should().BeApproximately(0.0f, 1e-4f);
        midpoint.Z.Should().BeApproximately(0.0f, 1e-4f);
    }

    [Fact]
    public void TangentContinuity_AcrossSegmentBoundaries_FirstDerivativesAreEqual()
    {
        // Arrange: 5 control points forming 2 contiguous segments [P1..P2] and [P2..P3]
        Vector3 p0 = new(0, 5, 0);
        Vector3 p1 = new(10, 15, 2);
        Vector3 p2 = new(20, 10, 4);
        Vector3 p3 = new(30, 25, 8);
        Vector3 p4 = new(40, 20, 10);

        // Act:
        // Segment 1 [p1..p2] uses (p0, p1, p2, p3) evaluated at u = 1.0
        Vector3 tangentAtEndOfSegment1 = CatmullRomSpline.EvaluateTangent(p0, p1, p2, p3, 1.0f);

        // Segment 2 [p2..p3] uses (p1, p2, p3, p4) evaluated at u = 0.0
        Vector3 tangentAtStartOfSegment2 = CatmullRomSpline.EvaluateTangent(p1, p2, p3, p4, 0.0f);

        // Assert: Tangents at the shared knot P2 must be identical (C1 continuity)
        // Both equal 0.5 * (p3 - p1)
        Vector3 expectedTangentAtP2 = 0.5f * (p3 - p1);

        tangentAtEndOfSegment1.X.Should().BeApproximately(expectedTangentAtP2.X, 1e-4f);
        tangentAtEndOfSegment1.Y.Should().BeApproximately(expectedTangentAtP2.Y, 1e-4f);
        tangentAtEndOfSegment1.Z.Should().BeApproximately(expectedTangentAtP2.Z, 1e-4f);

        tangentAtStartOfSegment2.X.Should().BeApproximately(expectedTangentAtP2.X, 1e-4f);
        tangentAtStartOfSegment2.Y.Should().BeApproximately(expectedTangentAtP2.Y, 1e-4f);
        tangentAtStartOfSegment2.Z.Should().BeApproximately(expectedTangentAtP2.Z, 1e-4f);
    }

    [Fact]
    public void Evaluate_TwoNodes_FallsBackToLinearInterpolation()
    {
        // Arrange
        CatmullRomSpline spline = new();
        spline.AddNode(new DirectorNode { TimestampMs = 0, Position = new Vector3(0, 0, 0), FovDegrees = 50f });
        spline.AddNode(new DirectorNode { TimestampMs = 1000, Position = new Vector3(100, 200, 300), FovDegrees = 70f });

        // Act
        (Vector3 pos, Vector3 target, Quaternion rot, float fov) = spline.Evaluate(500);

        // Assert
        pos.X.Should().BeApproximately(50f, 1e-3f);
        pos.Y.Should().BeApproximately(100f, 1e-3f);
        pos.Z.Should().BeApproximately(150f, 1e-3f);
        fov.Should().BeApproximately(60f, 1e-3f);
    }

    [Fact]
    public void Evaluate_SingleNode_ReturnsConstantNodePosition()
    {
        // Arrange
        CatmullRomSpline spline = new();
        spline.AddNode(new DirectorNode { TimestampMs = 5000, Position = new Vector3(10, 20, 30), FovDegrees = 65f });

        // Act
        (Vector3 pos, Vector3 target, Quaternion rot, float fov) = spline.Evaluate(12000);

        // Assert
        pos.Should().Be(new Vector3(10, 20, 30));
        fov.Should().Be(65f);
    }

    [Fact]
    public void Evaluate_EmptySpline_ReturnsSafeDefaultState()
    {
        // Arrange
        CatmullRomSpline spline = new();

        // Act
        (Vector3 pos, Vector3 target, Quaternion rot, float fov) = spline.Evaluate(1000);

        // Assert
        pos.Should().Be(new Vector3(0, -14, 18));
        target.Should().Be(Vector3.Zero);
        fov.Should().Be(55.0f);
    }

    [Fact]
    public void TimelineEvaluation_WhenInvoked_MapsGlobalTimestampToCorrectSegmentAndAlpha()
    {
        // Arrange
        CatmullRomSpline spline = new();
        spline.AddNode(new DirectorNode { TimestampMs = 0, Position = new Vector3(0, 0, 0) });
        spline.AddNode(new DirectorNode { TimestampMs = 10000, Position = new Vector3(10, 0, 0) });
        spline.AddNode(new DirectorNode { TimestampMs = 20000, Position = new Vector3(20, 0, 0) });
        spline.AddNode(new DirectorNode { TimestampMs = 30000, Position = new Vector3(30, 0, 0) });

        // Act: Evaluate at t = 15000 (midpoint of segment 1..2)
        (Vector3 pos, Vector3 target, Quaternion rot, float fov) = spline.Evaluate(15000);

        // Assert: Should be approximately (15, 0, 0)
        pos.X.Should().BeApproximately(15.0f, 1e-3f);
    }

    [Fact]
    public void InterpolateCameraAttributes_WhenInvoked_SmoothlyBlendsFovAndOrientation()
    {
        // Arrange
        Quaternion q1 = Quaternion.Identity;
        Quaternion q2 = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2.0f); // 90 deg rotation

        Quaternion blended = CatmullRomSpline.SlerpShortestArc(q1, q2, 0.5f);

        // Assert: Halfway rotation should be 45 deg around Z
        Quaternion expectedHalf = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 4.0f);
        blended.X.Should().BeApproximately(expectedHalf.X, 1e-3f);
        blended.Y.Should().BeApproximately(expectedHalf.Y, 1e-3f);
        blended.Z.Should().BeApproximately(expectedHalf.Z, 1e-3f);
        blended.W.Should().BeApproximately(expectedHalf.W, 1e-3f);
    }

    [Fact]
    public void NodeManagement_AddRemoveClear_MaintainsOrderAndCount()
    {
        // Arrange
        CatmullRomSpline spline = new();
        DirectorNode n1 = new() { TimestampMs = 1000 };
        DirectorNode n2 = new() { TimestampMs = 500 };
        DirectorNode n3 = new() { TimestampMs = 2000 };

        // Act
        spline.AddNode(n1);
        spline.AddNode(n2);
        spline.AddNode(n3);

        // Assert: Sorted order
        spline.Count.Should().Be(3);
        spline.Nodes[0].TimestampMs.Should().Be(500);
        spline.Nodes[1].TimestampMs.Should().Be(1000);
        spline.Nodes[2].TimestampMs.Should().Be(2000);

        // Remove node
        spline.RemoveNode(n1.Id);
        spline.Count.Should().Be(2);

        // Clear
        spline.Clear();
        spline.Count.Should().Be(0);
    }
}
