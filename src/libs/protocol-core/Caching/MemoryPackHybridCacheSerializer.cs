// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using MemoryPack;
using Microsoft.Extensions.Caching.Hybrid;

namespace ASPIRE.Common.Caching;

/// <summary>
///     High-performance zero-allocation binary serializer for <see cref="HybridCache" /> powered by
///     <see cref="MemoryPackSerializer" />.
/// </summary>
/// <typeparam name="T">The type being serialized and deserialized.</typeparam>
[UnconditionalSuppressMessage("Trimming", "IL2026:MembersAnnotatedWithRequiresUnreferencedCode",
    Justification = "Types serialized by MemoryPack are registered via source-generator or MemoryPackable attributes.")]
[UnconditionalSuppressMessage("Trimming", "IL2091:TargetGenericArgument",
    Justification = "Types serialized by MemoryPack are registered via source-generator or MemoryPackable attributes.")]
[UnconditionalSuppressMessage("AOT", "IL3050:MembersAnnotatedWithRequiresDynamicCode",
    Justification = "Types serialized by MemoryPack are registered via source-generator or MemoryPackable attributes.")]
public sealed class MemoryPackHybridCacheSerializer<T> : IHybridCacheSerializer<T>
{
    /// <inheritdoc />
    public T Deserialize(ReadOnlySequence<byte> source)
    {
        return MemoryPackSerializer.Deserialize<T>(source)!;
    }

    /// <inheritdoc />
    public void Serialize(T value, IBufferWriter<byte> target)
    {
        MemoryPackSerializer.Serialize(target, value);
    }
}
