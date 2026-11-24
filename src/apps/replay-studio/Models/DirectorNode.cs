// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: DirectorNode.cs
// System: Replay Studio Desktop GUI Tool
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Numerics;

namespace ReplayStudio.GUI.Models;

/// <summary>
///     Keyframe node placed along the replay timeline for cinematic camera paths.
/// </summary>
public sealed class DirectorNode
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int TimestampMs { get; set; }
    public Vector3 Position { get; set; }
    public Vector3 LookAtTarget { get; set; }
    public Quaternion Orientation { get; set; } = Quaternion.Identity;
    public bool UseLookAtTarget { get; set; } = true;
    public float FovDegrees { get; set; } = 55.0f;
    public float Tension { get; set; } = 0.5f; // Catmull-Rom parameter
    public int? FocusHeroSlot { get; set; }
    public string Description { get; set; } = string.Empty;

    public DirectorNode Clone()
    {
        return new DirectorNode
        {
            Id = Id,
            TimestampMs = TimestampMs,
            Position = Position,
            LookAtTarget = LookAtTarget,
            Orientation = Orientation,
            UseLookAtTarget = UseLookAtTarget,
            FovDegrees = FovDegrees,
            Tension = Tension,
            FocusHeroSlot = FocusHeroSlot,
            Description = Description
        };
    }
}
