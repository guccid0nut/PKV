// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: CameraDirectorTests.cs
// System: Replay Studio Desktop GUI Tool
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Numerics;
using FluentAssertions;
using ReplayStudio.GUI.Director;
using ReplayStudio.GUI.Models;
using Xunit;

namespace ReplayStudio.Tests;

public sealed class CameraDirectorTests
{
    [Fact]
    public void ComputeViewMatrix_PlayerPOV_3rdPersonChase_FollowsHeadingAndOffset()
    {
        // Arrange
        CameraDirector director = new()
        {
            Mode = CameraMode.PlayerPov,
            ChaseDistance = 14.0f,
            ChaseHeight = 18.0f
        };

        Vector3 heroPos = new(10.0f, 20.0f, 0.0f);
        float headingDeg = 0.0f; // Facing +X direction

        // Act (dt = 0 triggers instant snap)
        CameraState state = director.Update(0.0f, heroPos, headingDeg, 0, 16.0f / 9.0f);

        // Assert: Ideal eye = heroPos - (14, 0, 0) + (0, 0, 18) = (-4, 20, 18)
        state.Position.X.Should().BeApproximately(-4.0f, 1e-3f);
        state.Position.Y.Should().BeApproximately(20.0f, 1e-3f);
        state.Position.Z.Should().BeApproximately(18.0f, 1e-3f);

        // Target should be heroPos + (0, 0, 1.5)
        state.Target.Should().Be(new Vector3(10.0f, 20.0f, 1.5f));
    }

    [Fact]
    public void PlayerPOV_ExponentialSmoothing_ConvergesDeterministically()
    {
        // Arrange
        CameraDirector director = new()
        {
            Mode = CameraMode.PlayerPov,
            ChaseDistance = 14.0f,
            ChaseHeight = 18.0f,
            ChaseSmoothingLambda = 6.0f
        };

        Vector3 initialHeroPos = Vector3.Zero;
        director.Update(0.0f, initialHeroPos, 0.0f, 0, 16.0f / 9.0f);

        Vector3 movedHeroPos = new(5.0f, 0.0f, 0.0f);
        float dt = 0.01666f; // ~60 FPS

        // Act: Step 60 frames (~1.0 second)
        for (int frame = 0; frame < 60; frame++)
        {
            director.Update(dt, movedHeroPos, 0.0f, frame * 16, 16.0f / 9.0f);
        }

        // Assert: Camera should have smoothly converged within 0.1m of ideal (-9, 0, 18)
        Vector3 expectedIdeal = new(-9.0f, 0.0f, 18.0f);
        director.CurrentEye.X.Should().BeApproximately(expectedIdeal.X, 0.1f);
        director.CurrentEye.Y.Should().BeApproximately(expectedIdeal.Y, 0.1f);
        director.CurrentEye.Z.Should().BeApproximately(expectedIdeal.Z, 0.1f);
    }

    [Fact]
    public void PlayerPOV_LargeDisplacement_TriggersInstantaneousReset()
    {
        // Arrange
        CameraDirector director = new() { Mode = CameraMode.PlayerPov };
        director.Update(0.0f, Vector3.Zero, 0.0f, 0, 16.0f / 9.0f);

        // Hero teleports 100 meters away
        Vector3 teleportPos = new(100.0f, 100.0f, 0.0f);

        // Act
        CameraState state = director.Update(0.016f, teleportPos, 0.0f, 16, 16.0f / 9.0f);

        // Assert: Should have snapped without taking minutes to smooth
        Vector3 ideal = teleportPos - new Vector3(14.0f, 0.0f, 0.0f) + new Vector3(0.0f, 0.0f, 18.0f);
        state.Position.X.Should().BeApproximately(ideal.X, 1e-2f);
        state.Position.Y.Should().BeApproximately(ideal.Y, 1e-2f);
    }

    [Fact]
    public void ComputeViewMatrix_FirstPersonPOV_AlignsWithEyeLevelAndLookDirection()
    {
        // Arrange
        CameraDirector director = new()
        {
            Mode = CameraMode.FirstPerson,
            FirstPersonPitchDeg = 0.0f
        };

        Vector3 heroPos = new(5.0f, 10.0f, 2.0f);
        float headingDeg = 90.0f; // Facing +Y direction

        // Act
        CameraState state = director.Update(0.016f, heroPos, headingDeg, 0, 16.0f / 9.0f);

        // Assert: Eye should be at (5, 10, 3.8)
        state.Position.Should().Be(new Vector3(5.0f, 10.0f, 3.8f));

        // Looking along +Y, target should be (5, 11, 3.8)
        state.Target.X.Should().BeApproximately(5.0f, 1e-4f);
        state.Target.Y.Should().BeApproximately(11.0f, 1e-4f);
        state.Target.Z.Should().BeApproximately(3.8f, 1e-4f);
    }

    [Fact]
    public void ComputeViewMatrix_RtsIsometric_Enforces45DegreePitchAndTacticalOffset()
    {
        // Arrange
        CameraDirector director = new()
        {
            Mode = CameraMode.RtsIsometric,
            RtsDistance = 28.0f
        };

        Vector3 heroPos = new(0.0f, 0.0f, 0.0f);

        // Act
        CameraState state = director.Update(0.016f, heroPos, 0.0f, 0, 16.0f / 9.0f);

        // Assert: Offset along -Y and +Z by 28 * sqrt(2)/2 = ~19.79899
        float expectedOffset = 28.0f * 0.70710678f;
        state.Position.X.Should().BeApproximately(0.0f, 1e-3f);
        state.Position.Y.Should().BeApproximately(-expectedOffset, 1e-3f);
        state.Position.Z.Should().BeApproximately(expectedOffset, 1e-3f);
        state.Target.Should().Be(heroPos);
    }

    [Fact]
    public void FreeSpectator_SphericalCoordinates_ClampsPitchToPreventGimbalLock()
    {
        // Arrange
        CameraDirector director = new() { Mode = CameraMode.FreeSpectator };

        // Act & Assert
        director.SetFreeSpectatorOrientation(45.0f, 95.0f);
        director.FreePitchDeg.Should().Be(89.0f);

        director.SetFreeSpectatorOrientation(45.0f, -95.0f);
        director.FreePitchDeg.Should().Be(-89.0f);

        director.SetFreeSpectatorOrientation(180.0f, 30.0f);
        director.FreePitchDeg.Should().Be(30.0f);
        director.FreeYawDeg.Should().Be(180.0f);
    }

    [Fact]
    public void FreeSpectator_SpatialTranslation_MovesAlongLocalAxes()
    {
        // Arrange
        CameraDirector director = new()
        {
            Mode = CameraMode.FreeSpectator,
            MoveSpeed = 10.0f
        };
        director.ResetPosition(Vector3.Zero, Vector3.UnitY);
        director.SetFreeSpectatorOrientation(90.0f, 0.0f); // Facing +Y

        // Act: Move forward (+Y) by 1.0 second
        director.TranslateFreeSpectator(new Vector3(0, 1, 0), 1.0f, sprint: false);

        // Assert: Eye should have advanced +10 units along Y
        director.CurrentEye.Y.Should().BeApproximately(10.0f, 1e-3f);
    }

    [Fact]
    public void ComputeProjectionMatrix_Perspective_ProducesValidVulkanDepthBounds()
    {
        // Arrange & Act
        Matrix4x4 proj = CameraDirector.ComputeProjectionMatrix(55.0f, 16.0f / 9.0f, 0.1f, 1000.0f);

        // Assert: Projection matrix diagonal entries must be non-zero and finite
        proj.M11.Should().BeGreaterThan(0.0f);
        proj.M22.Should().BeGreaterThan(0.0f);
        proj.M33.Should().NotBe(0.0f);
        proj.M34.Should().Be(-1.0f);
    }

    [Fact]
    public void ComputeViewMatrix_LookAt_ProducesInvertibleMatrix()
    {
        // Arrange
        Vector3 eye = new(10.0f, -20.0f, 30.0f);
        Vector3 target = new(0.0f, 0.0f, 0.0f);
        Vector3 up = Vector3.UnitZ;

        // Act
        Matrix4x4 view = CameraDirector.ComputeViewMatrix(eye, target, up);
        bool invertible = Matrix4x4.Invert(view, out Matrix4x4 invView);

        // Assert
        invertible.Should().BeTrue();
        invView.Translation.X.Should().BeApproximately(eye.X, 1e-3f);
        invView.Translation.Y.Should().BeApproximately(eye.Y, 1e-3f);
        invView.Translation.Z.Should().BeApproximately(eye.Z, 1e-3f);
    }

    [Fact]
    public void ModeTransition_SwitchingModes_UpdatesCameraStateWithoutException()
    {
        // Arrange
        CameraDirector director = new();
        Vector3 heroPos = new(10.0f, 20.0f, 1.0f);

        // Act & Assert for all 5 modes
        CameraMode[] modes =
        [
            CameraMode.PlayerPov,
            CameraMode.FirstPerson,
            CameraMode.RtsIsometric,
            CameraMode.FreeSpectator,
            CameraMode.CinematicSpline
        ];

        foreach (CameraMode mode in modes)
        {
            director.Mode = mode;
            CameraState state = director.Update(0.016f, heroPos, 45.0f, 1000, 16.0f / 9.0f);
            state.ViewMatrix.M44.Should().Be(1.0f);
            float.IsNaN(state.Position.X).Should().BeFalse();
            float.IsNaN(state.Position.Y).Should().BeFalse();
            float.IsNaN(state.Position.Z).Should().BeFalse();
        }
    }
}
