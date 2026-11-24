// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.IO.Hashing;
using ASPIRE.Common.Utilities;
using ASPIRE.Tests.ASPIRE.Common.EntityPrebake.Contracts;
using MemoryPack;

namespace ASPIRE.Tests.ASPIRE.Common.EntityPrebake.Harness;

public sealed class TestMemoryPackSerializer
{
    public byte[] SerializeToBytes(EntityCacheContainer container, bool compress = false, int compressionLevel = 3)
    {
        ArgumentNullException.ThrowIfNull(container);

        byte[] rawBytes = MemoryPackSerializer.Serialize(container);
        if (!compress) return rawBytes;

        return rawBytes.AsSpan().CompressZstd(compressionLevel);
    }

    public EntityCacheContainer DeserializeFromBytes(byte[] bytes, bool isCompressed = false)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        if (bytes.Length == 0) throw new ArgumentException("Cannot deserialize from empty byte array.", nameof(bytes));

        byte[] payload = bytes;
        if (isCompressed || ZstdCompressionExtensions.IsZstdCompressed(bytes))
            payload = bytes.AsSpan().DecompressZstd();

        EntityCacheContainer? container = MemoryPackSerializer.Deserialize<EntityCacheContainer>(payload);
        return container ?? throw new InvalidOperationException("Failed to deserialize EntityCacheContainer.");
    }

    public ulong ComputeXxHash64(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return XxHash3.HashToUInt64(data);
    }

    public bool VerifyHash(byte[] data, ulong expectedHash)
    {
        ArgumentNullException.ThrowIfNull(data);
        ulong actualHash = ComputeXxHash64(data);
        return actualHash == expectedHash;
    }
}
