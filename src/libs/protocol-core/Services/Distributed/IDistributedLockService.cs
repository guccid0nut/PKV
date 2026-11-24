// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace ASPIRE.Common.Services.Distributed;

/// <summary>
///     Provides cluster-wide distributed locking primitives across multi-region services.
/// </summary>
public interface IDistributedLockService
{
    /// <summary>
    ///     Attempts to acquire a distributed lock on the specified resource, waiting up to <paramref name="timeout"/>
    ///     with exponential backoff and jitter.
    /// </summary>
    /// <param name="resource">The resource identifier (e.g. "lock:account:123:store_checkout").</param>
    /// <param name="expiry">The lease duration before the lock expires automatically.</param>
    /// <param name="timeout">The maximum duration to wait for the lock to become available.</param>
    /// <param name="cancellationToken">A token to observe for cancellation.</param>
    /// <returns>A lock handle indicating acquisition status and providing safe auto-release via <see cref="IAsyncDisposable"/>.</returns>
    Task<IDistributedLock> AcquireLockAsync(
        string resource,
        TimeSpan expiry,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Attempts to acquire a distributed lock immediately without waiting or retrying.
    /// </summary>
    /// <param name="resource">The resource identifier (e.g. "lock:account:123:store_checkout").</param>
    /// <param name="expiry">The lease duration before the lock expires automatically.</param>
    /// <param name="cancellationToken">A token to observe for cancellation.</param>
    /// <returns>A lock handle indicating acquisition status and providing safe auto-release via <see cref="IAsyncDisposable"/>.</returns>
    Task<IDistributedLock> TryAcquireLockAsync(
        string resource,
        TimeSpan expiry,
        CancellationToken cancellationToken = default);
}
