// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using MemoryPack;
using ZiggyCreatures.Caching.Fusion.Serialization;

namespace ASPIRE.Common.Caching;

/// <summary>
///     Zero-allocation Native AOT compatible <see cref="IFusionCacheSerializer" /> backed by
///     first-party <see cref="MemoryPackSerializer" /> with automatic <see cref="JsonSerializer" />
///     fallback for non-<see cref="MemoryPackableAttribute" /> JIT portal DTOs.
/// </summary>
[UnconditionalSuppressMessage("Trimming", "IL2026:MembersAnnotatedWithRequiresUnreferencedCode",
    Justification = "AOT services register types via MemoryPackable source generation; JIT web portal DTOs fall back to System.Text.Json.")]
[UnconditionalSuppressMessage("Trimming", "IL2091:TargetGenericArgument",
    Justification = "AOT services register types via MemoryPackable source generation; JIT web portal DTOs fall back to System.Text.Json.")]
[UnconditionalSuppressMessage("AOT", "IL3050:MembersAnnotatedWithRequiresDynamicCode",
    Justification = "AOT services register types via MemoryPackable source generation; JIT web portal DTOs fall back to System.Text.Json.")]
internal sealed class MemoryPackFusionCacheSerializer : IFusionCacheSerializer
{
    public byte[] Serialize<T>(T? obj)
    {
        return MemoryPackFormatterProvider.IsRegistered<T>()
            ? MemoryPackSerializer.Serialize(obj)
            : JsonSerializer.SerializeToUtf8Bytes(obj);
    }

    public T? Deserialize<T>(byte[] data)
    {
        return MemoryPackFormatterProvider.IsRegistered<T>()
            ? MemoryPackSerializer.Deserialize<T>(data)
            : JsonSerializer.Deserialize<T>(data);
    }

    public ValueTask<byte[]> SerializeAsync<T>(T? obj, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return new ValueTask<byte[]>(Serialize(obj));
    }

    public ValueTask<T?> DeserializeAsync<T>(byte[] data, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return new ValueTask<T?>(Deserialize<T>(data));
    }
}
