// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics.CodeAnalysis;

namespace ASPIRE.Common.Caching;

[SuppressMessage("ReSharper", "UnusedMember.Global")]
[SuppressMessage("ReSharper", "UnusedMemberInSuper.Global")]
public interface INexusHybridCache
{
    Task<T?> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, Task<T?>> factory,
        TimeSpan? l1Expiration = null,
        TimeSpan? l2Expiration = null,
        CancellationToken cancellationToken = default);

    Task<T?> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, Task<T?>> factory,
        IReadOnlyCollection<string>? tags,
        TimeSpan? l1Expiration = null,
        TimeSpan? l2Expiration = null,
        CancellationToken cancellationToken = default);

    Task RemoveAsync(string key, CancellationToken cancellationToken = default);

    Task RemoveByTagAsync(string tag, CancellationToken cancellationToken = default);

    Task RemoveByTagAsync(IEnumerable<string> tags, CancellationToken cancellationToken = default);
}

/// <summary>
///     Internal opt-in stale-on-error contract implemented by <see cref="NexusHybridCache" /> so existing
///     <see cref="INexusHybridCache" /> Moq test doubles remain 100% compatible without extra setups.
/// </summary>
internal interface IStaleTolerantHybridCache
{
    Task<T?> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, Task<T?>> factory,
        IReadOnlyCollection<string>? tags,
        TimeSpan? l1Expiration,
        TimeSpan? l2Expiration,
        bool allowStaleOnError,
        CancellationToken cancellationToken = default);
}

public static class NexusHybridCacheExtensions
{
    public static Task<T?> GetOrCreateAsync<T>(
        this INexusHybridCache cache,
        string key,
        Func<CancellationToken, Task<T?>> factory,
        TimeSpan? l1Expiration,
        TimeSpan? l2Expiration,
        bool allowStaleOnError,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cache);

        if (cache is IStaleTolerantHybridCache staleTolerant)
        {
            return staleTolerant.GetOrCreateAsync(
                key,
                factory,
                tags: null,
                l1Expiration: l1Expiration,
                l2Expiration: l2Expiration,
                allowStaleOnError: allowStaleOnError,
                cancellationToken: cancellationToken);
        }

        return cache.GetOrCreateAsync(
            key,
            factory,
            l1Expiration,
            l2Expiration,
            cancellationToken);
    }

    public static Task<T?> GetOrCreateAsync<T>(
        this INexusHybridCache cache,
        string key,
        Func<CancellationToken, Task<T?>> factory,
        IReadOnlyCollection<string>? tags,
        TimeSpan? l1Expiration,
        TimeSpan? l2Expiration,
        bool allowStaleOnError,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cache);

        if (cache is IStaleTolerantHybridCache staleTolerant)
        {
            return staleTolerant.GetOrCreateAsync(
                key,
                factory,
                tags,
                l1Expiration,
                l2Expiration,
                allowStaleOnError,
                cancellationToken);
        }

        return cache.GetOrCreateAsync(
            key,
            factory,
            tags,
            l1Expiration,
            l2Expiration,
            cancellationToken);
    }
}
