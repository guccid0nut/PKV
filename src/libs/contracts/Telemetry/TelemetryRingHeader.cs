// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: TelemetryRingHeader.cs
// System: NEXUS Telemetry Contracts - Shared Memory Ring Buffer
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Runtime.InteropServices;

namespace NEXUS.Contracts.Telemetry;

/// <summary>
///     High-performance 64-byte blittable header struct for lock-free shared memory ring buffer (V1).
///     Contains atomic head/tail indices, capacity, versioning, and producer process ID.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 64)]
public struct TelemetryRingHeader
{
    public const uint ExpectedMagic = 0x4B4F4E47; // 'KONG'
    public const uint Version1 = 1;
    public const uint Version2 = 2;
    public const uint CurrentVersion = Version2;
    public const int HeaderSizeBytes = 64;
    public const int HeaderSizeBytesV2 = 192;

    public const ulong VoicePttTeamFlag = 0x01;
    public const ulong VoicePttLaneFlag = 0x02;
    public const ulong VoicePttPartyFlag = 0x04;
    public const ulong VoicePttMentorFlag = 0x08;

    [FieldOffset(0)]
    public uint Magic;

    [FieldOffset(4)]
    public uint Version;

    [FieldOffset(8)]
    public uint Capacity;

    [FieldOffset(12)]
    public uint RecordSizeBytes;

    [FieldOffset(16)]
    public ulong Head;

    [FieldOffset(24)]
    public ulong Tail;

    [FieldOffset(32)]
    public ulong ProducerProcessId;

    [FieldOffset(40)]
    public long StartTimestampQpc;

    [FieldOffset(48)]
    public long FrequencyQpc;

    [FieldOffset(56)]
    public ulong ReservedFlags;

    /// <summary>
    ///     Validates whether this header represents an authentic and supported ring buffer layout.
    /// </summary>
    public readonly bool IsValid(out string? failureReason)
    {
        if (Magic != ExpectedMagic)
        {
            failureReason = $"Invalid magic signature: 0x{Magic:X8} (Expected: 0x{ExpectedMagic:X8})";
            return false;
        }

        if (Version != Version1 && Version != Version2)
        {
            failureReason = $"Unsupported version: {Version} (Supported: {Version1}, {Version2})";
            return false;
        }

        if (Capacity == 0 || (Capacity & (Capacity - 1)) != 0)
        {
            failureReason = $"Capacity {Capacity} must be a non-zero power of 2.";
            return false;
        }

        if (RecordSizeBytes != FrameTelemetryRecord.RecordSizeBytes)
        {
            failureReason = $"Record size mismatch: {RecordSizeBytes} bytes (Expected: {FrameTelemetryRecord.RecordSizeBytes})";
            return false;
        }

        failureReason = null;
        return true;
    }
}

/// <summary>
///     High-performance 192-byte 3-cache-line blittable header struct for lock-free shared memory fast bus (V2).
///     Features a 64-bit atomic Seqlock and dedicated atomic latest snapshot with zero false sharing.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 192)]
public unsafe struct TelemetryRingHeaderV2
{
    public const uint ExpectedMagic = 0x4B4F4E47; // 'KONG'
    public const uint ExpectedVersion = 2;
    public const int HeaderSizeBytes = 192;

    // ------------------------------------------------------------------------
    // Cache Line 0 (Bytes 0..63): V1 Base Header (100% Backwards-Compatible)
    // ------------------------------------------------------------------------
    [FieldOffset(0)]
    public uint Magic;

    [FieldOffset(4)]
    public uint Version;

    [FieldOffset(8)]
    public uint Capacity;

    [FieldOffset(12)]
    public uint RecordSizeBytes;

    [FieldOffset(16)]
    public ulong Head;

    [FieldOffset(24)]
    public ulong Tail;

    [FieldOffset(32)]
    public ulong ProducerProcessId;

    [FieldOffset(40)]
    public long StartTimestampQpc;

    [FieldOffset(48)]
    public long FrequencyQpc;

    [FieldOffset(56)]
    public ulong ReservedFlags;

    // ------------------------------------------------------------------------
    // Cache Line 1 (Bytes 64..127): Seqlock Synchronization & Live Engine State
    // ------------------------------------------------------------------------
    [FieldOffset(64)]
    public ulong LatestSequence;

    [FieldOffset(72)]
    public long SnapshotTimestamp;

    [FieldOffset(80)]
    public uint ProducerThreadId;

    [FieldOffset(84)]
    public uint GamePhase;

    [FieldOffset(88)]
    public uint ActiveMatchId;

    [FieldOffset(92)]
    public uint ConnectedPlayers;

    [FieldOffset(96)]
    public uint LuaMemoryKb;

    [FieldOffset(100)]
    public fixed byte CurrentMap[16];

    [FieldOffset(116)]
    public fixed byte PlayerName[12];

    // ------------------------------------------------------------------------
    // Cache Line 2 (Bytes 128..191): Atomic Latest Snapshot (Exactly 64 bytes)
    // ------------------------------------------------------------------------
    [FieldOffset(128)]
    public FrameTelemetryRecord LatestRecord;

    /// <summary>
    ///     Reads the null-terminated CurrentMap field as a managed string.
    /// </summary>
    public readonly string GetCurrentMapString()
    {
        fixed (byte* ptr = CurrentMap)
        {
            int len = 0;
            while (len < 16 && ptr[len] != 0)
            {
                len++;
            }
            return len > 0 ? System.Text.Encoding.UTF8.GetString(ptr, len) : string.Empty;
        }
    }

    /// <summary>
    ///     Reads the null-terminated PlayerName field as a managed string.
    /// </summary>
    public readonly string GetPlayerNameString()
    {
        fixed (byte* ptr = PlayerName)
        {
            int len = 0;
            while (len < 12 && ptr[len] != 0)
            {
                len++;
            }
            return len > 0 ? System.Text.Encoding.UTF8.GetString(ptr, len) : string.Empty;
        }
    }

    /// <summary>
    ///     Validates whether this header represents an authentic V2 ring buffer layout.
    /// </summary>
    public readonly bool IsValid(out string? failureReason)
    {
        if (Magic != ExpectedMagic)
        {
            failureReason = $"Invalid magic signature: 0x{Magic:X8} (Expected: 0x{ExpectedMagic:X8})";
            return false;
        }

        if (Version != ExpectedVersion)
        {
            failureReason = $"Unsupported V2 version: {Version} (Expected: {ExpectedVersion})";
            return false;
        }

        if (Capacity == 0 || (Capacity & (Capacity - 1)) != 0)
        {
            failureReason = $"Capacity {Capacity} must be a non-zero power of 2.";
            return false;
        }

        if (RecordSizeBytes != FrameTelemetryRecord.RecordSizeBytes)
        {
            failureReason = $"Record size mismatch: {RecordSizeBytes} bytes (Expected: {FrameTelemetryRecord.RecordSizeBytes})";
            return false;
        }

        failureReason = null;
        return true;
    }
}
