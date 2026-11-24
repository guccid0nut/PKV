// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;

namespace Kongor.Protocol.Diagnostics;

/// <summary>
/// Declarative field quantization flags modeled after Xash3D-FWGS delta.h / delta_Common.
/// Enables bit-level and scale-aware drift comparison between two entity/replay snapshots (#140).
/// </summary>
[Flags]
public enum DeltaFieldType
{
    None     = 0,
    Byte     = 1 << 0,
    Short    = 1 << 1,
    Integer  = 1 << 2,
    Float    = 1 << 3,
    TimeStep = 1 << 4,
    Angle    = 1 << 5,
    String   = 1 << 6,
    Signed   = 1 << 15
}

public sealed record DeltaFieldDesc(
    string Name,
    DeltaFieldType FieldType,
    int Bits,
    double Scale = 1.0);

public sealed record FieldDriftDiff(
    string FieldName,
    DeltaFieldType FieldType,
    int Bits,
    double Scale,
    double RawValueA,
    double RawValueB,
    long QuantizedA,
    long QuantizedB,
    long QuantizedDelta,
    bool IsDrifted);

public sealed record SnapshotDeltaReport(
    string SchemaName,
    int TotalFieldsChecked,
    int DriftedFieldCount,
    IReadOnlyList<FieldDriftDiff> Diffs);

public static class SnapshotDeltaSchema
{
    public static readonly IReadOnlyList<DeltaFieldDesc> DefaultEntityStateFields = new DeltaFieldDesc[]
    {
        new DeltaFieldDesc("origin_x",      DeltaFieldType.Float | DeltaFieldType.Signed,   16, 8.0),
        new DeltaFieldDesc("origin_y",      DeltaFieldType.Float | DeltaFieldType.Signed,   16, 8.0),
        new DeltaFieldDesc("origin_z",      DeltaFieldType.Float | DeltaFieldType.Signed,   14, 4.0),
        new DeltaFieldDesc("yaw",           DeltaFieldType.Angle,                           16, 65536.0 / 360.0),
        new DeltaFieldDesc("pitch",         DeltaFieldType.Angle | DeltaFieldType.Signed,   12, 4096.0 / 360.0),
        new DeltaFieldDesc("health",        DeltaFieldType.Integer,                         16, 1.0),
        new DeltaFieldDesc("mana",          DeltaFieldType.Integer,                         16, 1.0),
        new DeltaFieldDesc("move_speed",    DeltaFieldType.Float,                           12, 2.0),
        new DeltaFieldDesc("anim_sequence", DeltaFieldType.Byte,                             8, 1.0),
        new DeltaFieldDesc("state_flags",   DeltaFieldType.Integer,                         32, 1.0)
    };

    /// <summary>
    /// Quantizes a raw floating-point or integer field value using the declarative Xash3D-style
    /// scale multiplier and bit-width clamp/mask.
    /// </summary>
    public static long QuantizeValue(double rawValue, DeltaFieldType fieldType, int bits, double scale)
    {
        int clampedBits = Math.Clamp(bits, 1, 62);
        double effectiveScale = (scale > 0.0 && !double.IsNaN(scale) && !double.IsInfinity(scale)) ? scale : 1.0;

        double scaled = rawValue;
        bool isAngle = (fieldType & DeltaFieldType.Angle) != 0;
        bool isSigned = (fieldType & DeltaFieldType.Signed) != 0;

        if (isAngle && !isSigned)
        {
            double normalizedDeg = rawValue % 360.0;
            if (normalizedDeg < 0.0)
            {
                normalizedDeg += 360.0;
            }
            scaled = normalizedDeg * effectiveScale;
        }
        else if ((fieldType & (DeltaFieldType.Float | DeltaFieldType.TimeStep | DeltaFieldType.Angle)) != 0)
        {
            scaled = rawValue * effectiveScale;
        }

        long rounded = (long)Math.Round(scaled, MidpointRounding.AwayFromZero);

        if (isSigned)
        {
            long maxSigned = (1L << (clampedBits - 1)) - 1L;
            long minSigned = -(1L << (clampedBits - 1));
            return Math.Clamp(rounded, minSigned, maxSigned);
        }
        else
        {
            long maxUnsigned = (1L << clampedBits) - 1L;
            if (isAngle)
            {
                return rounded & maxUnsigned;
            }
            return Math.Clamp(rounded, 0L, maxUnsigned);
        }
    }

    /// <summary>
    /// Compares two snapshots against a declarative field schema and returns only fields whose
    /// quantized wire representations differ (or all fields when includeUnchanged is true).
    /// </summary>
    public static SnapshotDeltaReport CompareSnapshots(
        string schemaName,
        IReadOnlyList<DeltaFieldDesc> schemaFields,
        IReadOnlyDictionary<string, double> snapshotA,
        IReadOnlyDictionary<string, double> snapshotB,
        bool includeUnchanged = false)
    {
        ArgumentNullException.ThrowIfNull(schemaFields);
        ArgumentNullException.ThrowIfNull(snapshotA);
        ArgumentNullException.ThrowIfNull(snapshotB);

        List<FieldDriftDiff> diffs = new List<FieldDriftDiff>(schemaFields.Count);
        int driftedCount = 0;

        for (int i = 0; i < schemaFields.Count; i++)
        {
            DeltaFieldDesc desc = schemaFields[i];
            double valA = snapshotA.TryGetValue(desc.Name, out double foundA) ? foundA : 0.0;
            double valB = snapshotB.TryGetValue(desc.Name, out double foundB) ? foundB : 0.0;

            long quantA = QuantizeValue(valA, desc.FieldType, desc.Bits, desc.Scale);
            long quantB = QuantizeValue(valB, desc.FieldType, desc.Bits, desc.Scale);
            long delta = quantB - quantA;
            bool isDrifted = delta != 0;

            if (isDrifted)
            {
                driftedCount++;
            }

            if (isDrifted || includeUnchanged)
            {
                diffs.Add(new FieldDriftDiff(
                    desc.Name,
                    desc.FieldType,
                    desc.Bits,
                    desc.Scale,
                    valA,
                    valB,
                    quantA,
                    quantB,
                    delta,
                    isDrifted));
            }
        }

        return new SnapshotDeltaReport(
            string.IsNullOrWhiteSpace(schemaName) ? "k2_entity_state" : schemaName,
            schemaFields.Count,
            driftedCount,
            diffs);
    }
}
