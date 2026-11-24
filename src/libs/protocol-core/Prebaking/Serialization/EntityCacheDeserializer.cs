// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.IO.Compression;
using System.IO.Hashing;
using ASPIRE.Common.Memory;
using MemoryPack;

namespace ASPIRE.Common.Prebaking.Serialization;

public static class EntityCacheDeserializer
{
    /// <summary>
    ///     Deserializes an EntityCacheContainer from a binary buffer, verifying magic, version, and XxHash3 checksum.
    /// </summary>
    public static EntityCacheContainer Deserialize(ReadOnlySpan<byte> source)
    {
        if (source.Length < EntityCacheHeader.HeaderSize)
            throw new InvalidDataException("Source data is too small to contain a valid EntityCacheHeader.");

        // 1. Parse and validate header
        EntityCacheHeader header = EntityCacheHeader.ReadFrom(source);

        ReadOnlySpan<byte> payloadSpan = source.Slice(EntityCacheHeader.HeaderSize);

        // 2. Decompress Zstandard payload if compressed
        ReadOnlySpan<byte> decompressedSpan;
        byte[]? decompressedBytes = null;
        if (header.CompressionType == 1) // Zstd
        {
            if (header.UncompressedSize > int.MaxValue)
                throw new InvalidDataException(
                    $"Uncompressed size {header.UncompressedSize} exceeds maximum supported array size.");

            int uncompressedSize = (int)header.UncompressedSize;
            decompressedBytes = new byte[uncompressedSize];
            using MemoryStream inputMs = NexusMemoryStreamManager.Shared.GetStream(payloadSpan, "EntityCacheZstdIn");
            using ZstandardStream zstd = new(inputMs, CompressionMode.Decompress);

            int totalRead = 0;
            while (totalRead < uncompressedSize)
            {
                int read = zstd.Read(decompressedBytes, totalRead, uncompressedSize - totalRead);
                if (read == 0) break;
                totalRead += read;
            }

            if (totalRead != uncompressedSize)
                throw new InvalidDataException(
                    $"Decompressed size mismatch. Header: {header.UncompressedSize} bytes, Actual: {totalRead} bytes.");

            decompressedSpan = decompressedBytes;
        }
        else
        {
            decompressedSpan = payloadSpan;
        }

        // 3. Verify uncompressed length
        if ((ulong)decompressedSpan.Length != header.UncompressedSize)
            throw new InvalidDataException(
                $"Decompressed size mismatch. Header: {header.UncompressedSize} bytes, Actual: {decompressedSpan.Length} bytes.");

        // 4. Verify XxHash3-64 checksum
        ulong actualChecksum = XxHash3.HashToUInt64(decompressedSpan);
        if (actualChecksum != header.XxHash3Checksum)
            throw new InvalidDataException(
                $"Checksum mismatch! Corrupted entity cache. Expected 0x{header.XxHash3Checksum:X16}, computed 0x{actualChecksum:X16}.");

        // 5. MemoryPack zero-allocation deserialization
        EntityCacheContainer? container = MemoryPackSerializer.Deserialize<EntityCacheContainer>(decompressedSpan);
        if (container == null) throw new InvalidDataException("Deserialization failed: MemoryPack returned null.");

        container.TotalEntityCount = (int)header.EntityCount;
        container.ChecksumXxHash3 = actualChecksum;

        return container;
    }

    /// <summary>
    ///     Deserializes an EntityCacheContainer directly from a file asynchronously.
    /// </summary>
    public static async Task<EntityCacheContainer> DeserializeFromFileAsync(string filePath,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Entity cache file not found: {filePath}", filePath);

        byte[] fileBytes = await File.ReadAllBytesAsync(filePath, ct).ConfigureAwait(false);
        return Deserialize(fileBytes);
    }

    /// <summary>
    ///     Validates the integrity of a cache file without throwing unhandled exceptions.
    /// </summary>
    public static ValidationResult ValidateFile(string filePath)
    {
        if (!File.Exists(filePath)) return new ValidationResult(false, $"File does not exist: {filePath}", default);

        try
        {
            byte[] bytes = File.ReadAllBytes(filePath);
            if (bytes.Length < EntityCacheHeader.HeaderSize)
                return new ValidationResult(false,
                    $"File size ({bytes.Length} bytes) is smaller than header size ({EntityCacheHeader.HeaderSize} bytes).",
                    default);

            EntityCacheHeader header = EntityCacheHeader.ReadFrom(bytes);
            ReadOnlySpan<byte> payload = bytes.AsSpan(EntityCacheHeader.HeaderSize);

            ReadOnlySpan<byte> decompressed;
            byte[]? decompressedBuffer = null;
            if (header.CompressionType == 1)
            {
                if (header.UncompressedSize > int.MaxValue)
                    return new ValidationResult(false,
                        $"Uncompressed size {header.UncompressedSize} exceeds maximum supported array size.", header);

                int uncompressedSize = (int)header.UncompressedSize;
                decompressedBuffer = new byte[uncompressedSize];
                using MemoryStream inputMs = NexusMemoryStreamManager.Shared.GetStream(payload, "EntityCacheValidateIn");
                using ZstandardStream zstd = new(inputMs, CompressionMode.Decompress);

                int totalRead = 0;
                while (totalRead < uncompressedSize)
                {
                    int read = zstd.Read(decompressedBuffer, totalRead, uncompressedSize - totalRead);
                    if (read == 0) break;
                    totalRead += read;
                }

                if (totalRead != uncompressedSize)
                    return new ValidationResult(false,
                        $"Decompressed size mismatch (expected {header.UncompressedSize}, got {totalRead}).",
                        header);

                decompressed = decompressedBuffer;
            }
            else
            {
                decompressed = payload;
            }

            if ((ulong)decompressed.Length != header.UncompressedSize)
                return new ValidationResult(false,
                    $"Decompressed size mismatch (expected {header.UncompressedSize}, got {decompressed.Length}).",
                    header);

            ulong calculatedChecksum = XxHash3.HashToUInt64(decompressed);
            if (calculatedChecksum != header.XxHash3Checksum)
                return new ValidationResult(false,
                    $"XxHash3 checksum mismatch (expected 0x{header.XxHash3Checksum:X16}, computed 0x{calculatedChecksum:X16}).",
                    header);

            return new ValidationResult(true, "Cache file is valid and uncorrupted.", header);
        }
        catch (Exception ex)
        {
            return new ValidationResult(false, $"Validation exception: {ex.Message}", default);
        }
    }
}

public readonly record struct ValidationResult(bool IsValid, string Message, EntityCacheHeader Header);
