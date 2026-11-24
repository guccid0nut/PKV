// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics.CodeAnalysis;
using MemoryPack;
using Microsoft.Extensions.Caching.Hybrid;

namespace ASPIRE.Common.Caching;

/// <summary>
///     Factory that dynamically provisions <see cref="MemoryPackHybridCacheSerializer{T}" /> for types decorated with
///     <see cref="MemoryPackableAttribute" />.
/// </summary>
[UnconditionalSuppressMessage("Trimming", "IL2026:MembersAnnotatedWithRequiresUnreferencedCode",
    Justification = "Types serialized by MemoryPack are registered via source-generator or MemoryPackable attributes.")]
[UnconditionalSuppressMessage("AOT", "IL3050:MembersAnnotatedWithRequiresDynamicCode",
    Justification = "Types serialized by MemoryPack are registered via source-generator or MemoryPackable attributes.")]
public sealed class MemoryPackHybridCacheSerializerFactory : IHybridCacheSerializerFactory
{
    /// <inheritdoc />
    public bool TryCreateSerializer<T>([NotNullWhen(true)] out IHybridCacheSerializer<T>? serializer)
    {
        if (MemoryPackFormatterProvider.IsRegistered<T>())
        {
            serializer = new MemoryPackHybridCacheSerializer<T>();
            return true;
        }

        serializer = null;
        return false;
    }
}
