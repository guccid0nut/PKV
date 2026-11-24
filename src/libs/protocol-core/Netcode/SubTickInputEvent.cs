// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: SubTickInputEvent.cs
// System: Kongor Core Netcode - Microsecond Sub-Tick Input Event Model
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Numerics;

namespace ASPIRE.Common.Netcode;

/// <summary>
///     Identifies the category of player input action captured in a sub-tick event.
/// </summary>
public enum SubTickCommandType : byte
{
    None = 0,
    Move = 1,
    Attack = 2,
    CastAbility = 3,
    CancelAction = 4,
    HoldPosition = 5,
    CustomAction = 6
}

/// <summary>
///     High-precision, microsecond-timestamped player input command.
///     Eliminates fixed-tick quantization error by tagging commands with the exact fractional
///     moment within the tick interval when the player clicked or pressed a key.
/// </summary>
public readonly record struct SubTickInputEvent
{
    public const int BinarySize = 36;
    public const ushort GroundTargetEntityIndex = 0xFFFF;

    public uint Sequence { get; init; }
    public uint BaseTick { get; init; }
    public float SubTickOffset { get; init; }
    public long HardwareTimestampUs { get; init; }
    public SubTickCommandType CommandType { get; init; }
    public byte Flags { get; init; }
    public ushort TargetEntityIndex { get; init; }
    public float TargetX { get; init; }
    public float TargetY { get; init; }
    public float TargetZ { get; init; }
    public float CameraYaw { get; init; }

    public Vector3 TargetPosition => new(TargetX, TargetY, TargetZ);

    public SubTickInputEvent(
        uint sequence,
        uint baseTick,
        float subTickOffset,
        long hardwareTimestampUs,
        SubTickCommandType commandType,
        byte flags,
        ushort targetEntityIndex,
        float targetX,
        float targetY,
        float targetZ,
        float cameraYaw)
    {
        Sequence = sequence;
        BaseTick = baseTick;
        SubTickOffset = Math.Clamp(subTickOffset, 0.0f, 0.999999f);
        HardwareTimestampUs = hardwareTimestampUs;
        CommandType = commandType;
        Flags = flags;
        TargetEntityIndex = targetEntityIndex;
        TargetX = targetX;
        TargetY = targetY;
        TargetZ = targetZ;
        CameraYaw = cameraYaw;
    }

    /// <summary>
    ///     Creates a ground-targeted movement command event.
    /// </summary>
    public static SubTickInputEvent CreateMove(
        uint sequence,
        uint baseTick,
        float subTickOffset,
        long timestampUs,
        Vector3 destination,
        float cameraYaw)
    {
        return new SubTickInputEvent(
            sequence,
            baseTick,
            subTickOffset,
            timestampUs,
            SubTickCommandType.Move,
            flags: 0,
            GroundTargetEntityIndex,
            destination.X,
            destination.Y,
            destination.Z,
            cameraYaw);
    }

    /// <summary>
    ///     Creates an entity-targeted attack or spell command event.
    /// </summary>
    public static SubTickInputEvent CreateTargeted(
        uint sequence,
        uint baseTick,
        float subTickOffset,
        long timestampUs,
        SubTickCommandType commandType,
        ushort targetEntityIndex,
        Vector3 targetPosition,
        float cameraYaw)
    {
        return new SubTickInputEvent(
            sequence,
            baseTick,
            subTickOffset,
            timestampUs,
            commandType,
            flags: 0,
            targetEntityIndex,
            targetPosition.X,
            targetPosition.Y,
            targetPosition.Z,
            cameraYaw);
    }
}
