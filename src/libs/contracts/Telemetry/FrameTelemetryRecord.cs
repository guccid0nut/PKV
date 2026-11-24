// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: FrameTelemetryRecord.cs
// System: NEXUS Telemetry Contracts - Shared Memory Ring Buffer
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Runtime.InteropServices;

namespace NEXUS.Contracts.Telemetry;

/// <summary>
///     High-performance 64-byte blittable frame telemetry record matching an exact CPU cache line.
///     Designed for zero-copy memory-mapped writes and lock-free ring buffer transmission.
///     <para>
///     <b>Producer Field Semantics (Zero-ABI-Change Contract):</b>
///     </para>
///     <list type="bullet">
///         <item>
///             <description>
///                 <b><c>vid_vulkan_x64.dll</c> (<c>Local\KongorTelemetryBuffer</c>, per-rendered-frame 200+ Hz):</b>
///                 <see cref="FrameIndex"/> = monotonic rendered frame counter;
///                 <see cref="TimestampQpc"/> = QPC tick at present/publish (consecutive deltas yield exact end-to-end frame time);
///                 <see cref="CpuFrameTimeMs"/> = engine work between frames (<c>engineMs</c>);
///                 <see cref="GpuFrameTimeMs"/> = render pass duration (<c>renderMs</c>);
///                 <see cref="InputLatencyMs"/> = present wait duration (<c>presentWaitMs</c>);
///                 <see cref="DrawCallCount"/> = draw calls in frame;
///                 <see cref="ReservedFlags"/> = sentinel hitch classification bitmask (<see cref="FlagHitch"/>..<see cref="FlagPreemption"/>).
///             </description>
///         </item>
///         <item>
///             <description>
///                 <b><c>version.dll</c> (<c>Local\KongorClientTelemetry_&lt;pid&gt;</c>, ~20 Hz monitor thread):</b>
///                 State/PTT heartbeat ring sampled via <c>Sleep(50)</c> in <c>shm_telemetry.cpp</c>.
///                 NOT render-frame-accurate for High-Hz frame pacing; <see cref="ReservedFlags"/> carries
///                 <c>g_voicePttFlags</c> (bit 0 collides with <see cref="FlagHitch"/> and must not be scored as a hitch).
///             </description>
///         </item>
///     </list>
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 64)]
public struct FrameTelemetryRecord : IEquatable<FrameTelemetryRecord>
{
    public const int RecordSizeBytes = 64;

    public const uint FlagHitch = 1U << 0;
    public const uint FlagCpuStall = 1U << 1;
    public const uint FlagGpuStall = 1U << 2;
    public const uint FlagDwmWait = 1U << 3;
    public const uint FlagPreemption = 1U << 4;

    [FieldOffset(0)]
    public ulong FrameIndex;

    [FieldOffset(8)]
    public long TimestampQpc;

    [FieldOffset(16)]
    public float CpuFrameTimeMs;

    [FieldOffset(20)]
    public float GpuFrameTimeMs;

    [FieldOffset(24)]
    public float InputLatencyMs;

    [FieldOffset(28)]
    public uint DrawCallCount;

    [FieldOffset(32)]
    public uint ActiveEntities;

    [FieldOffset(36)]
    public uint ParticlesAlive;

    [FieldOffset(40)]
    public uint PingMicroseconds;

    [FieldOffset(44)]
    public float PacketLossPercent;

    [FieldOffset(48)]
    public uint MemoryWorkingSetMb;

    [FieldOffset(52)]
    public uint AudioVoicesActive;

    [FieldOffset(56)]
    public uint ServerTick;

    [FieldOffset(60)]
    public uint ReservedFlags;

    public readonly bool Equals(FrameTelemetryRecord other)
    {
        return FrameIndex == other.FrameIndex &&
               TimestampQpc == other.TimestampQpc &&
               CpuFrameTimeMs.Equals(other.CpuFrameTimeMs) &&
               GpuFrameTimeMs.Equals(other.GpuFrameTimeMs) &&
               InputLatencyMs.Equals(other.InputLatencyMs) &&
               DrawCallCount == other.DrawCallCount &&
               ActiveEntities == other.ActiveEntities &&
               ParticlesAlive == other.ParticlesAlive &&
               PingMicroseconds == other.PingMicroseconds &&
               PacketLossPercent.Equals(other.PacketLossPercent) &&
               MemoryWorkingSetMb == other.MemoryWorkingSetMb &&
               AudioVoicesActive == other.AudioVoicesActive &&
               ServerTick == other.ServerTick &&
               ReservedFlags == other.ReservedFlags;
    }

    public override readonly bool Equals(object? obj)
    {
        return obj is FrameTelemetryRecord other && Equals(other);
    }

    public override readonly int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(FrameIndex);
        hash.Add(TimestampQpc);
        hash.Add(CpuFrameTimeMs);
        hash.Add(GpuFrameTimeMs);
        hash.Add(DrawCallCount);
        hash.Add(PingMicroseconds);
        return hash.ToHashCode();
    }

    public static bool operator ==(FrameTelemetryRecord left, FrameTelemetryRecord right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(FrameTelemetryRecord left, FrameTelemetryRecord right)
    {
        return !(left == right);
    }
}
