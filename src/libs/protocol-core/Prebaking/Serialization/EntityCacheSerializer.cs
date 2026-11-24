// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.IO.Compression;
using System.IO.Hashing;
using MemoryPack;

namespace ASPIRE.Common.Prebaking.Serialization;

public static class EntityCacheSerializer
{
    /// <summary>
    ///     Serializes an EntityCacheContainer into a binary payload with a 32-byte KNEC header, MemoryPack serialization, and
    ///     optional Zstd compression.
    /// </summary>
    public static byte[] Serialize(EntityCacheContainer container, int compressionLevel = 3, bool compress = true)
    {
        ArgumentNullException.ThrowIfNull(container);

        // 1. Count total entities and assign to container before serialization
        uint totalEntities = (uint)(
            container.Heroes.Count +
            container.AltAvatars.Count +
            container.Abilities.Count +
            container.States.Count +
            container.Projectiles.Count +
            container.Items.Count +
            container.CosmeticUpgrades.Count);
        container.TotalEntityCount = (int)totalEntities;

        // 2. Serialize container using MemoryPack
        byte[] uncompressedBytes = MemoryPackSerializer.Serialize(container);
        ulong uncompressedSize = (ulong)uncompressedBytes.Length;

        // 3. Compute XxHash3-64 of uncompressed payload
        ulong checksum = XxHash3.HashToUInt64(uncompressedBytes);
        container.ChecksumXxHash3 = checksum;

        // 4. Compress payload if requested
        byte[] payloadBytes;
        EntityCacheHeader header;

        if (compress)
        {
            CompressionLevel compLevel = compressionLevel switch
            {
                <= 1 => CompressionLevel.Fastest,
                >= 9 => CompressionLevel.SmallestSize,
                _ => CompressionLevel.Optimal
            };

            using MemoryStream ms = new();
            using (ZstandardStream zstd = new(ms, compLevel, leaveOpen: true))
            {
                zstd.Write(uncompressedBytes);
            }

            payloadBytes = ms.ToArray();
            header = EntityCacheHeader.CreateZstd(totalEntities, uncompressedSize, checksum, (uint)compressionLevel);
        }
        else
        {
            payloadBytes = uncompressedBytes;
            header = EntityCacheHeader.CreateRaw(totalEntities, uncompressedSize, checksum);
        }

        // 5. Construct final buffer with header
        byte[] finalBuffer = new byte[EntityCacheHeader.HeaderSize + payloadBytes.Length];
        header.WriteTo(finalBuffer.AsSpan(0, EntityCacheHeader.HeaderSize));
        payloadBytes.CopyTo(finalBuffer.AsSpan(EntityCacheHeader.HeaderSize));

        return finalBuffer;
    }

    /// <summary>
    ///     Writes the serialized entity cache directly to the specified file path asynchronously.
    /// </summary>
    public static async Task SerializeToFileAsync(
        EntityCacheContainer container,
        string targetFilePath,
        int compressionLevel = 3,
        bool compress = true,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFilePath);

        string? dir = Path.GetDirectoryName(targetFilePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

        byte[] serializedData = Serialize(container, compressionLevel, compress);
        await File.WriteAllBytesAsync(targetFilePath, serializedData, ct).ConfigureAwait(false);
    }
}
