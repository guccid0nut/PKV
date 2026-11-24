// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: SubTickSimulationInterpolator.cs
// System: Kongor Core Netcode - Continuous Sub-Tick Simulation Interpolation
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Numerics;

namespace ASPIRE.Common.Netcode;

/// <summary>
///     High-precision mathematical interpolator calculating exact continuous hero positions,
///     rotations, and skillshot origins at any fractional sub-tick moment.
/// </summary>
internal static class SubTickSimulationInterpolator
{
    private const float TwoPi = (float)(Math.PI * 2.0);

    /// <summary>
    ///     Interpolates linear world position between tick boundaries using sub-tick fraction t in [0.0, 1.0].
    /// </summary>
    public static Vector3 InterpolatePosition(Vector3 prevPosition, Vector3 currentPosition, float subTickFraction)
    {
        float t = Math.Clamp(subTickFraction, 0.0f, 1.0f);
        return Vector3.Lerp(prevPosition, currentPosition, t);
    }

    /// <summary>
    ///     Interpolates facing/camera angle (in radians) along the shortest arc,
    ///     preventing 360-degree snap artifacts when crossing the -Pi / +Pi boundary.
    /// </summary>
    public static float InterpolateAngle(float prevAngleRad, float currentAngleRad, float subTickFraction)
    {
        float t = Math.Clamp(subTickFraction, 0.0f, 1.0f);
        float diff = (currentAngleRad - prevAngleRad) % TwoPi;

        if (diff < -(float)Math.PI)
        {
            diff += TwoPi;
        }
        else if (diff > (float)Math.PI)
        {
            diff -= TwoPi;
        }

        return prevAngleRad + (diff * t);
    }

    /// <summary>
    ///     Calculates the exact projectile launch origin from the caster's sub-tick interpolated position,
    ///     accounting for hero collision hull radius and aim direction.
    /// </summary>
    public static Vector3 CalculateSkillshotOrigin(Vector3 casterPosAtSubTick, Vector3 aimDirection, float heroRadius = 32.0f)
    {
        if (aimDirection == Vector3.Zero)
        {
            return casterPosAtSubTick;
        }

        Vector3 normalized = Vector3.Normalize(aimDirection);
        return casterPosAtSubTick + (normalized * heroRadius);
    }

    /// <summary>
    ///     Calculates the latency quantization error (in milliseconds) eliminated by sub-tick processing
    ///     compared to legacy fixed-tick queuing.
    /// </summary>
    public static double CalculateQuantizationErrorSavedMs(double tickIntervalMs, float subTickOffset)
    {
        double clampedOffset = Math.Clamp(subTickOffset, 0.0, 1.0);
        return tickIntervalMs * (1.0 - clampedOffset);
    }
}
