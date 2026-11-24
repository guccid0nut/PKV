// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using NLua;

namespace ASPIRE.Common.Tooling.Lua;

/// <summary>
/// Defines the contract for acquiring, using, and managing pooled, thread-safe, isolated Lua runtime states.
/// </summary>
public interface ILuaStatePool : IDisposable
{
    /// <summary>
    /// Acquires an isolated Lua state wrapped in an IDisposable lease.
    /// Disposing the lease automatically sanitizes the state and returns it to the pool.
    /// </summary>
    PooledLuaLease GetLease();

    /// <summary>
    /// Synchronously executes an action within an isolated Lua state lease.
    /// </summary>
    TResult Execute<TResult>(Func<NLua.Lua, TResult> action);

    /// <summary>
    /// Asynchronously executes an action within an isolated Lua state lease.
    /// </summary>
    ValueTask<TResult> ExecuteAsync<TResult>(Func<NLua.Lua, ValueTask<TResult>> action);

    /// <summary>
    /// Gets the current number of active leases currently checked out.
    /// </summary>
    int ActiveLeaseCount { get; }

    /// <summary>
    /// Gets the maximum number of states retained by the pool.
    /// </summary>
    int MaximumRetained { get; }
}
