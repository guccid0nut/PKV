// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Runtime.InteropServices;

namespace NEXUS.Contracts.Graphics;

/// <summary>
///     64-byte binary frame header matching the C++ Vulkan capture layer (HONCAP_FRAME_HEADER).
///     Prepended to every frame buffer in Win32 shared memory and high-throughput named pipes.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 64)]
public struct FrameHeader
{
    public const uint ExpectedMagicHonc = 0x484F4E43; // 'HONC' in Little Endian
    public const uint ExpectedMagicHonf = 0x484F4E46; // 'HONF' in Little Endian
    public const uint ExpectedVersion = 1;
    public const uint FormatB8G8R8A8Unorm = 44; // VK_FORMAT_B8G8R8A8_UNORM
    public const uint FormatR8G8B8A8Unorm = 37; // VK_FORMAT_R8G8B8A8_UNORM

    public const uint FlagKeyframe = 0x00000001;
    public const uint FlagZeroCopyDma = 0x00000001;
    public const uint FlagEndOfStream = 0x00000002;

    /// <summary>
    ///     Magic identifier (0x484F4E43 = 'HONC' or 0x484F4E46 = 'HONF').
    /// </summary>
    public uint Magic;

    /// <summary>
    ///     Binary header protocol version (always 1).
    /// </summary>
    public uint Version;

    /// <summary>
    ///     Frame width in pixels (e.g. 1920 or 3840).
    /// </summary>
    public uint Width;

    /// <summary>
    ///     Frame height in pixels (e.g. 1080 or 2160).
    /// </summary>
    public uint Height;

    /// <summary>
    ///     Pixel format (44 = VK_FORMAT_B8G8R8A8_UNORM, 37 = VK_FORMAT_R8G8B8A8_UNORM).
    /// </summary>
    public uint Format;

    /// <summary>
    ///     Row stride in bytes (typically Width * 4).
    /// </summary>
    public uint RowPitch;

    /// <summary>
    ///     Monotonically increasing 64-bit frame sequence index (0, 1, 2...).
    /// </summary>
    public ulong FrameIndex;

    /// <summary>
    ///     High-resolution GPU/CPU presentation timestamp in nanoseconds.
    /// </summary>
    public ulong TimestampNs;

    /// <summary>
    ///     Compatibility alias for TimestampNs.
    /// </summary>
    public ulong TimestampNanoseconds
    {
        readonly get => TimestampNs;
        set => TimestampNs = value;
    }

    /// <summary>
    ///     Raw uncompressed pixel payload size in bytes (Width * Height * 4).
    /// </summary>
    public uint DataSizeBytes;

    /// <summary>
    ///     Ring buffer slot index (0, 1, 2) in triple-buffered shared memory.
    /// </summary>
    public uint BufferSlotIndex;

    /// <summary>
    ///     Stream control flags (bit 0 = Keyframe, bit 1 = End of stream).
    /// </summary>
    public uint Flags;

    /// <summary>
    ///     Reserved alignment padding (12 bytes) to guarantee 64-byte alignment (52 + 12 = 64).
    /// </summary>
    public uint Reserved0;

    public ulong Reserved1;

    /// <summary>
    ///     Compatibility alias for RowPitch.
    /// </summary>
    public uint Stride
    {
        readonly get => RowPitch;
        set => RowPitch = value;
    }

    /// <summary>
    ///     Validates whether the frame header contains valid magic and non-zero dimensions.
    /// </summary>
    public readonly bool IsValid()
    {
        bool isMagicValid = Magic is ExpectedMagicHonc or ExpectedMagicHonf;
        bool isVersionValid = Version == ExpectedVersion;
        bool areDimensionsValid = Width > 0 && Height > 0 && RowPitch >= Width * 4;
        bool isSizeValid = DataSizeBytes >= Width * Height * 4;

        return isMagicValid && isVersionValid && areDimensionsValid && isSizeValid;
    }

    /// <summary>
    ///     Checks if this frame signals the end of the replay stream.
    /// </summary>
    public readonly bool IsEndOfStream()
    {
        return (Flags & FlagEndOfStream) != 0;
    }

    /// <summary>
    ///     Returns a structured string representation for debugging and telemetry logs.
    /// </summary>
    public readonly override string ToString()
    {
        return
            $"FrameHeader [Index={FrameIndex}, Dim={Width}x{Height}, Pitch={RowPitch}, Size={DataSizeBytes} bytes, Slot={BufferSlotIndex}, Flags=0x{Flags:X8}, TsNs={TimestampNs}]";
    }
}
