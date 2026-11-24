// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: CameraState.cs
// System: Replay Studio Desktop GUI Tool
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Numerics;

namespace ReplayStudio.GUI.Models;

/// <summary>
///     Immutable state structure representing computed camera spatial parameters and transformation matrices.
/// </summary>
public readonly struct CameraState
{
    public Vector3 Position { get; }
    public Vector3 Target { get; }
    public Vector3 Up { get; }
    public float FovDegrees { get; }
    public Matrix4x4 ViewMatrix { get; }
    public Matrix4x4 ProjectionMatrix { get; }
    public Matrix4x4 ViewProjectionMatrix => ViewMatrix * ProjectionMatrix;

    public CameraState(
        Vector3 position,
        Vector3 target,
        Vector3 up,
        float fovDegrees,
        float aspectRatio,
        float nearZ = 0.1f,
        float farZ = 1000.0f)
    {
        Position = position;
        Target = target;
        Up = up;
        FovDegrees = fovDegrees;

        ViewMatrix = Matrix4x4.CreateLookAt(position, target, up);
        float fovRad = fovDegrees * (MathF.PI / 180.0f);
        ProjectionMatrix = Matrix4x4.CreatePerspectiveFieldOfView(fovRad, aspectRatio, nearZ, farZ);
    }
}
