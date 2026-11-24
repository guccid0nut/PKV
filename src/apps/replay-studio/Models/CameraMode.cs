// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: CameraMode.cs
// System: Replay Studio Desktop GUI Tool
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

namespace ReplayStudio.GUI.Models;

/// <summary>
///     Specifies the 5 multi-perspective camera director modalities available in Replay Studio.
/// </summary>
public enum CameraMode
{
    /// <summary>
    ///     3rd-person chase camera locked to active hero coordinates and heading with exponential smoothing.
    ///     Formula: E = P - d * H + h * Z with exponential filter (1 - e^(-lambda * dt)).
    /// </summary>
    PlayerPov = 0,

    /// <summary>
    ///     1st-person perspective aligned directly with hero eye position and directional gaze heading.
    /// </summary>
    FirstPerson = 1,

    /// <summary>
    ///     Classic 45-degree tactical isometric overhead view centered on hero or cursor location.
    /// </summary>
    RtsIsometric = 2,

    /// <summary>
    ///     Free 6-DOF spectator camera with mouse orbit/pan (pitch/yaw spherical coords) and WASD spatial translation.
    /// </summary>
    FreeSpectator = 3,

    /// <summary>
    ///     Cinematic Catmull-Rom cubic spline path smoothly interpolating user director keyframe nodes.
    /// </summary>
    CinematicSpline = 4
}
