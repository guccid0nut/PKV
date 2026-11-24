// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.CQRS;

/// <summary>
///     Marker interface for MediatR requests that require a cluster-wide distributed lock before execution.
/// </summary>
public interface ILockableRequest
{
    /// <summary>
    ///     The unique resource identifier for the lock (e.g. "lock:account:123:store_checkout").
    /// </summary>
    string LockKey { get; }

    /// <summary>
    ///     The maximum duration to hold the lock before automatic expiry.
    /// </summary>
    TimeSpan LockExpiry => TimeSpan.FromSeconds(10);

    /// <summary>
    ///     The maximum duration to wait to acquire the lock.
    /// </summary>
    TimeSpan LockTimeout => TimeSpan.FromSeconds(5);
}
