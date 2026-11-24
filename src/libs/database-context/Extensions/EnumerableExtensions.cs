// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace MERRICK.DatabaseContext.Extensions;

public static class EnumerableExtensions
{
    /// <summary>
    ///     Returns TRUE if the collection is empty. Otherwise, returns FALSE.
    /// </summary>
    public static bool None<TSource>(this IEnumerable<TSource> collection)
    {
        return !collection.Any();
    }

    /// <summary>
    ///     Returns TRUE if no element in the collection satisfies the condition specified by a predicate. Otherwise, returns
    ///     FALSE.
    /// </summary>
    public static bool None<TSource>(this IEnumerable<TSource> collection, Func<TSource, bool> predicate)
    {
        return !collection.Any(predicate);
    }

    /// <summary>
    ///     Returns TRUE if the collection is empty. Otherwise, returns FALSE.
    /// </summary>
    public static async Task<bool> NoneAsync<TSource>(this IQueryable<TSource> collection,
        CancellationToken cancellationToken = default)
    {
        return !await collection.AnyAsync(cancellationToken);
    }

    /// <summary>
    ///     Returns TRUE if no element in the collection satisfies the condition specified by a predicate. Otherwise, returns
    ///     FALSE.
    /// </summary>
    public static async Task<bool> NoneAsync<TSource>(this IQueryable<TSource> collection,
        Expression<Func<TSource, bool>> predicate, CancellationToken cancellationToken = default)
    {
        return !await collection.AnyAsync(predicate, cancellationToken);
    }

    /// <summary>
    ///     Returns a new collection composed of the input collection's elements in a random order.
    /// </summary>
    public static IEnumerable<TSource> Shuffle<TSource>(this IEnumerable<TSource> collection)
    {
        return collection.OrderBy(element => Random.Shared.Next());
    }

    /// <summary>
    ///     Returns a random element from the collection.
    /// </summary>
    public static TSource RandomElement<TSource>(this IEnumerable<TSource> collection)
    {
        return collection.Shuffle().First();
    }
}
