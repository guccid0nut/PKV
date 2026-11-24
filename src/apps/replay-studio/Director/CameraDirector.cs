// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: CameraDirector.cs
// System: Replay Studio Desktop GUI Tool
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Numerics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReplayStudio.GUI.Models;

namespace ReplayStudio.GUI.Director;

/// <summary>
///     High-performance 5-modality multi-perspective camera director supporting Player POV 3rd-person chase,
///     1st-person POV, RTS 45-degree isometric, gimbal-free 6-DOF spectator, and Catmull-Rom splines.
/// </summary>
public sealed class CameraDirector
{
    private readonly ILogger<CameraDirector> _logger;

    public CameraMode Mode { get; set; } = CameraMode.RtsIsometric;
    public float FovDegrees { get; set; } = 55.0f;
    public float NearPlane { get; set; } = 0.1f;
    public float FarPlane { get; set; } = 1000.0f;

    // Tracking state
    public Vector3 CurrentEye { get; private set; } = new(0, -14, 18);
    public Vector3 CurrentTarget { get; private set; } = Vector3.Zero;
    public Vector3 CurrentUp { get; private set; } = Vector3.UnitZ;

    // Player POV Chase parameters
    public float ChaseDistance { get; set; } = 14.0f;
    public float ChaseHeight { get; set; } = 18.0f;
    public float ChaseSmoothingLambda { get; set; } = 6.0f;
    public float TargetSmoothingLambda { get; set; } = 10.0f;

    // 1st-Person parameters
    public float FirstPersonPitchDeg { get; set; } = 0.0f;

    // RTS Isometric parameters
    public float RtsDistance { get; set; } = 28.0f;

    // Free Spectator parameters
    public float FreeYawDeg { get; set; } = 90.0f;
    public float FreePitchDeg { get; set; } = -30.0f;
    public float MoveSpeed { get; set; } = 20.0f;

    // Spline Controller
    public CatmullRomSpline Spline { get; }

    public CameraDirector(ILogger<CameraDirector>? logger = null)
    {
        _logger = logger ?? NullLogger<CameraDirector>.Instance;
        Spline = new CatmullRomSpline();
        _logger.LogInformation("[CameraDirector] Initialized with Mode={Mode}, FOV={Fov}deg", Mode, FovDegrees);
    }

    public void ResetPosition(Vector3 eye, Vector3 target)
    {
        CurrentEye = eye;
        CurrentTarget = target;
        CurrentUp = Vector3.UnitZ;
        _logger.LogInformation("[CameraDirector] Camera reset to Eye={Eye}, Target={Target}", eye, target);
    }

    public void SetFreeSpectatorOrientation(float yawDeg, float pitchDeg)
    {
        FreeYawDeg = yawDeg;
        FreePitchDeg = Math.Clamp(pitchDeg, -89.0f, 89.0f);
    }

    public void TranslateFreeSpectator(Vector3 movementInput, float dt, bool sprint = false)
    {
        float speed = MoveSpeed * (sprint ? 2.5f : 1.0f);
        float pitchRad = FreePitchDeg * (MathF.PI / 180.0f);
        float yawRad = FreeYawDeg * (MathF.PI / 180.0f);

        Vector3 forward = Vector3.Normalize(new Vector3(
            MathF.Cos(pitchRad) * MathF.Cos(yawRad),
            MathF.Cos(pitchRad) * MathF.Sin(yawRad),
            MathF.Sin(pitchRad)));

        Vector3 right = Vector3.Normalize(new Vector3(
            -MathF.Sin(yawRad),
            MathF.Cos(yawRad),
            0.0f));

        Vector3 up = Vector3.UnitZ;

        Vector3 displacement = (right * movementInput.X + forward * movementInput.Y + up * movementInput.Z) * speed * dt;
        CurrentEye += displacement;
        CurrentTarget = CurrentEye + forward;
    }

    /// <summary>
    ///     Updates the camera spatial state and returns computed View/Projection matrices.
    /// </summary>
    public CameraState Update(float dt, Vector3 heroPos, float heroHeadingDeg, int timestampMs, float aspectRatio)
    {
        float heroHeadingRad = heroHeadingDeg * (MathF.PI / 180.0f);
        float effectiveFov = FovDegrees;

        switch (Mode)
        {
            case CameraMode.PlayerPov:
            {
                // 1. Player POV (3rd Person Chase)
                Vector3 headingVec = new(MathF.Cos(heroHeadingRad), MathF.Sin(heroHeadingRad), 0.0f);
                Vector3 idealEye = heroPos - headingVec * ChaseDistance + Vector3.UnitZ * ChaseHeight;
                Vector3 idealTarget = heroPos + new Vector3(0, 0, 1.5f);

                // Teleport / Respawn jump check
                float distToIdeal = Vector3.Distance(CurrentEye, idealEye);
                if (distToIdeal > 50.0f || dt <= 0.0f)
                {
                    CurrentEye = idealEye;
                    CurrentTarget = idealTarget;
                }
                else
                {
                    float alphaEye = 1.0f - MathF.Exp(-ChaseSmoothingLambda * dt);
                    float alphaTarget = 1.0f - MathF.Exp(-TargetSmoothingLambda * dt);

                    CurrentEye = Vector3.Lerp(CurrentEye, idealEye, Math.Clamp(alphaEye, 0.0f, 1.0f));
                    CurrentTarget = Vector3.Lerp(CurrentTarget, idealTarget, Math.Clamp(alphaTarget, 0.0f, 1.0f));
                }

                CurrentUp = Vector3.UnitZ;
                break;
            }

            case CameraMode.FirstPerson:
            {
                // 2. 1st-Person POV
                CurrentEye = heroPos + new Vector3(0, 0, 1.8f);
                float pitchRad = Math.Clamp(FirstPersonPitchDeg, -85.0f, 85.0f) * (MathF.PI / 180.0f);

                Vector3 lookDir = new(
                    MathF.Cos(pitchRad) * MathF.Cos(heroHeadingRad),
                    MathF.Cos(pitchRad) * MathF.Sin(heroHeadingRad),
                    MathF.Sin(pitchRad));

                CurrentTarget = CurrentEye + lookDir;
                CurrentUp = Vector3.UnitZ;
                break;
            }

            case CameraMode.RtsIsometric:
            {
                // 3. RTS Isometric (Fixed 45-degree tactical overhead)
                float cos45 = 0.70710678f;
                float sin45 = 0.70710678f;

                CurrentEye = new Vector3(
                    heroPos.X,
                    heroPos.Y - RtsDistance * cos45,
                    heroPos.Z + RtsDistance * sin45);

                CurrentTarget = heroPos;
                CurrentUp = Vector3.UnitZ;
                break;
            }

            case CameraMode.FreeSpectator:
            {
                // 4. Free Spectator 6-DOF
                float pitchRad = Math.Clamp(FreePitchDeg, -89.0f, 89.0f) * (MathF.PI / 180.0f);
                float yawRad = FreeYawDeg * (MathF.PI / 180.0f);

                Vector3 forward = Vector3.Normalize(new Vector3(
                    MathF.Cos(pitchRad) * MathF.Cos(yawRad),
                    MathF.Cos(pitchRad) * MathF.Sin(yawRad),
                    MathF.Sin(pitchRad)));

                Vector3 right = Vector3.Normalize(new Vector3(
                    -MathF.Sin(yawRad),
                    MathF.Cos(yawRad),
                    0.0f));

                CurrentTarget = CurrentEye + forward;
                CurrentUp = Vector3.Normalize(Vector3.Cross(right, forward));
                break;
            }

            case CameraMode.CinematicSpline:
            {
                // 5. Catmull-Rom Spline
                (Vector3 splinePos, Vector3 splineTarget, Quaternion splineRot, float splineFov) = Spline.Evaluate(timestampMs);
                CurrentEye = splinePos;
                CurrentTarget = splineTarget;
                effectiveFov = splineFov;

                Matrix4x4 rotMat = Matrix4x4.CreateFromQuaternion(splineRot);
                CurrentUp = Vector3.Transform(Vector3.UnitZ, rotMat);
                if (CurrentUp.LengthSquared() < 1e-6f) CurrentUp = Vector3.UnitZ;
                break;
            }
        }

        return new CameraState(CurrentEye, CurrentTarget, CurrentUp, effectiveFov, aspectRatio, NearPlane, FarPlane);
    }

    public static Matrix4x4 ComputeViewMatrix(Vector3 eye, Vector3 target, Vector3 up)
    {
        return Matrix4x4.CreateLookAt(eye, target, up);
    }

    public static Matrix4x4 ComputeProjectionMatrix(float fovDegrees, float aspectRatio, float nearZ = 0.1f, float farZ = 1000.0f)
    {
        float fovRad = fovDegrees * (MathF.PI / 180.0f);
        return Matrix4x4.CreatePerspectiveFieldOfView(fovRad, aspectRatio, nearZ, farZ);
    }
}
