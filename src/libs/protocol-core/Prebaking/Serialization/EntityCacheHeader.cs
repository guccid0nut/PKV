// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Buffers.Binary;

namespace ASPIRE.Common.Prebaking.Serialization;

public readonly record struct EntityCacheHeader(
    uint Magic,
    ushort FormatVersion,
    ushort CompressionType,
    uint CompressionLevel,
    uint EntityCount,
    ulong UncompressedSize,
    ulong XxHash3Checksum)
{
    public const uint ExpectedMagic = 0x43454E4B; // "KNEC" in Little Endian (0x4B, 0x4E, 0x45, 0x43)
    public const ushort CurrentVersion = 1;
    public const int HeaderSize = 32;

    public static EntityCacheHeader CreateZstd(uint entityCount, ulong uncompressedSize, ulong xxHash3Checksum,
        uint compressionLevel = 3)
    {
        return new EntityCacheHeader(
            ExpectedMagic,
            CurrentVersion,
            1,
            compressionLevel,
            entityCount,
            uncompressedSize,
            xxHash3Checksum);
    }

    public static EntityCacheHeader CreateRaw(uint entityCount, ulong uncompressedSize, ulong xxHash3Checksum)
    {
        return new EntityCacheHeader(
            ExpectedMagic,
            CurrentVersion,
            0,
            0,
            entityCount,
            uncompressedSize,
            xxHash3Checksum);
    }

    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < HeaderSize)
            throw new ArgumentException("Destination span is too small for EntityCacheHeader.", nameof(destination));

        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(0, 4), Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(4, 2), FormatVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(6, 2), CompressionType);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(8, 4), CompressionLevel);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(12, 4), EntityCount);
        BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(16, 8), UncompressedSize);
        BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(24, 8), XxHash3Checksum);
    }

    public static EntityCacheHeader ReadFrom(ReadOnlySpan<byte> source)
    {
        if (source.Length < HeaderSize)
            throw new InvalidDataException(
                $"Invalid header size. Expected at least {HeaderSize} bytes, got {source.Length}.");

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(0, 4));
        if (magic != ExpectedMagic)
            throw new InvalidDataException(
                $"Invalid magic bytes. Expected 0x{ExpectedMagic:X8} ('KNEC'), got 0x{magic:X8}.");

        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(4, 2));
        if (version != CurrentVersion)
            throw new InvalidDataException($"Unsupported format version: {version}. Expected: {CurrentVersion}.");

        ushort compressionType = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(6, 2));
        uint compressionLevel = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(8, 4));
        uint entityCount = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(12, 4));
        ulong uncompressedSize = BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(16, 8));
        ulong checksum = BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(24, 8));

        return new EntityCacheHeader(magic, version, compressionType, compressionLevel, entityCount, uncompressedSize,
            checksum);
    }
}
