// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using Microsoft.Extensions.ObjectPool;
using NLua;

namespace ASPIRE.Common.Tooling.Lua;

/// <summary>
/// Thread-safe, high-performance object pool for isolated Lua runtime states.
/// Provides sub-microsecond checkout latency, automatic state sanitization, and native garbage collection.
/// </summary>
public sealed class LuaStatePool : ILuaStatePool
{
    private readonly ObjectPool<NLua.Lua> _pool;
    private readonly LuaPoolOptions _options;
    private readonly ConcurrentBag<NLua.Lua> _trackedInstances = [];
    private int _activeLeaseCount;
    private bool _disposed;

    public LuaStatePool(LuaPoolOptions? options = null)
    {
        _options = options ?? new LuaPoolOptions();
        LuaStatePooledObjectPolicy policy = new(_options);
        DefaultPooledObjectPolicyWrapper wrapper = new(policy, this);
        _pool = new DefaultObjectPool<NLua.Lua>(wrapper, _options.MaximumRetained);

        // Prewarm instances up to requested PrewarmCount
        int prewarm = Math.Clamp(_options.PrewarmCount, 0, _options.MaximumRetained);
        List<NLua.Lua> prewarmed = new(prewarm);
        for (int i = 0; i < prewarm; i++)
        {
            prewarmed.Add(_pool.Get());
        }
        foreach (NLua.Lua item in prewarmed)
        {
            _pool.Return(item);
        }
    }

    public int ActiveLeaseCount => Volatile.Read(ref _activeLeaseCount);
    public int MaximumRetained => _options.MaximumRetained;

    public PooledLuaLease GetLease()
    {
        ThrowIfDisposed();
        Interlocked.Increment(ref _activeLeaseCount);
        NLua.Lua state = _pool.Get();
        return new PooledLuaLease(this, state);
    }

    public TResult Execute<TResult>(Func<NLua.Lua, TResult> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        using PooledLuaLease lease = GetLease();
        return action(lease.State);
    }

    public async ValueTask<TResult> ExecuteAsync<TResult>(Func<NLua.Lua, ValueTask<TResult>> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        using PooledLuaLease lease = GetLease();
        return await action(lease.State).ConfigureAwait(false);
    }

    internal void Return(NLua.Lua state)
    {
        if (state == null) return;
        try
        {
            if (!_disposed)
            {
                _pool.Return(state);
            }
            else
            {
                state.Dispose();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _activeLeaseCount);
        }
    }

    private void TrackInstance(NLua.Lua instance)
    {
        _trackedInstances.Add(instance);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        while (_trackedInstances.TryTake(out NLua.Lua? state))
        {
            try
            {
                state.Dispose();
            }
            catch
            {
                // Suppress disposal errors on teardown
            }
        }
    }

    private sealed class DefaultPooledObjectPolicyWrapper(
        IPooledObjectPolicy<NLua.Lua> innerPolicy,
        LuaStatePool parentPool) : IPooledObjectPolicy<NLua.Lua>
    {
        public NLua.Lua Create()
        {
            NLua.Lua instance = innerPolicy.Create();
            parentPool.TrackInstance(instance);
            return instance;
        }

        public bool Return(NLua.Lua obj) => innerPolicy.Return(obj);
    }
}

/// <summary>
/// RAII disposable wrapper representing a thread-safe lease of an isolated Lua state.
/// </summary>
public readonly struct PooledLuaLease : IDisposable
{
    private readonly LuaStatePool _pool;
    private readonly NLua.Lua _state;

    internal PooledLuaLease(LuaStatePool pool, NLua.Lua state)
    {
        _pool = pool;
        _state = state;
    }

    public NLua.Lua State => _state;
    public NLua.Lua Lua => _state;

    public void Dispose()
    {
        _pool?.Return(_state);
    }
}
