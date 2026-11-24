// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace ASPIRE.Common.Services.Distributed;

/// <summary>
///     Represents an active or attempted distributed cluster lock.
///     Implements <see cref="IAsyncDisposable"/> to ensure deterministic, token-safe lock release upon exiting a scope.
/// </summary>
public interface IDistributedLock : IAsyncDisposable
{
    /// <summary>
    ///     Gets the unique resource identifier or key being guarded by this lock.
    /// </summary>
    string Resource { get; }

    /// <summary>
    ///     Gets the unique cryptographic token verifying lock ownership.
    /// </summary>
    string LockToken { get; }

    /// <summary>
    ///     Gets a value indicating whether the lock was successfully acquired.
    /// </summary>
    bool IsAcquired { get; }

    /// <summary>
    ///     Gets the UTC timestamp when the lock was acquired.
    /// </summary>
    DateTimeOffset AcquiredAtUtc { get; }

    /// <summary>
    ///     Gets the UTC timestamp when the lock expires automatically.
    /// </summary>
    DateTimeOffset ExpiresAtUtc { get; }

    /// <summary>
    ///     Gets the estimated validity duration remaining on the lease.
    /// </summary>
    TimeSpan ValidityRemaining => IsAcquired ? TimeSpan.FromTicks(Math.Max(0, (ExpiresAtUtc - TimeProvider.System.GetUtcNow()).Ticks)) : TimeSpan.Zero;
}
