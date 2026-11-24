// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: VfsPrecacheQueue.cs
// System: Kongor Protocol Core - High-Performance Engine Modernization
// Subsystem: Strategic Initiative [SR-05] - Multi-Threaded VFS Asset Streaming
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kongor.Core.Services.Vfs;

/// <summary>
///     Priority tier for precached VFS asset loading.
/// </summary>
public enum VfsAssetPriority
{
    Low = 0,
    Normal = 1,
    High = 2,
    Critical = 3
}

/// <summary>
///     Immutable metrics snapshot of the VfsPrecacheQueue.
/// </summary>
public sealed record VfsPrecacheMetrics(
    long QueuedCount,
    long CompletedCount,
    long FailedCount,
    long CacheHitCount,
    long CacheMissCount,
    long TotalCachedBytes,
    int ActiveCachedEntries,
    double AverageLatencyMs
);

/// <summary>
///     Contract for multi-threaded asynchronous VFS asset streaming and precaching.
/// </summary>
public interface IVfsPrecacheQueue : IAsyncDisposable, IDisposable
{
    bool Enqueue(string vfsPath, VfsAssetPriority priority = VfsAssetPriority.Normal);
    ValueTask<ReadOnlyMemory<byte>> GetOrLoadAsync(string vfsPath, VfsAssetPriority priority = VfsAssetPriority.High, CancellationToken ct = default);
    bool TryGetCached(string vfsPath, out ReadOnlyMemory<byte> memory);
    void Invalidate(string vfsPath);
    void Clear();
    VfsPrecacheMetrics GetMetrics();
}

/// <summary>
///     Multi-threaded priority-paged streaming queue and lock-free in-memory decompressed asset cache.
///     Eliminates live-gameplay frame hitching when loading new skins, textures, and models.
/// </summary>
public sealed class VfsPrecacheQueue : IVfsPrecacheQueue
{
    private sealed record CacheEntry(
        ReadOnlyMemory<byte> Memory,
        long SizeBytes,
        long TimestampTicks
    );

    private sealed record PrecacheWorkItem(
        string NormalizedPath,
        VfsAssetPriority Priority,
        TaskCompletionSource<ReadOnlyMemory<byte>>? Tcs,
        long EnqueuedTimestampTicks
    );

    private readonly Func<string, CancellationToken, Task<byte[]?>> _assetLoader;
    private readonly ILogger _logger;
    private readonly long _maxCacheBytes;
    private readonly int _workerCount;

    // Priority channels: Critical (3), High (2), Normal (1), Low (0)
    private readonly Channel<PrecacheWorkItem>[] _priorityChannels;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task[] _workers;

    // Lock-free in-memory cache & in-flight deduplication
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<ReadOnlyMemory<byte>>> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    // Performance telemetry
    private long _queuedCount;
    private long _completedCount;
    private long _failedCount;
    private long _cacheHitCount;
    private long _cacheMissCount;
    private long _totalCachedBytes;
    private long _totalLatencyTicks;

    private int _isDisposed;

    public VfsPrecacheQueue(
        Func<string, CancellationToken, Task<byte[]?>> assetLoader,
        long maxCacheBytes = 256 * 1024 * 1024, // 256 MB default quota
        int? workerCount = null,
        ILogger? logger = null)
    {
        _assetLoader = assetLoader ?? throw new ArgumentNullException(nameof(assetLoader));
        _maxCacheBytes = maxCacheBytes > 0 ? maxCacheBytes : 256 * 1024 * 1024;
        _logger = logger ?? NullLogger<VfsPrecacheQueue>.Instance;
        _workerCount = Math.Clamp(workerCount ?? Math.Max(2, Environment.ProcessorCount / 2), 1, 16);

        _priorityChannels = new Channel<PrecacheWorkItem>[4];
        for (int i = 0; i < 4; i++)
        {
            _priorityChannels[i] = Channel.CreateUnbounded<PrecacheWorkItem>(new UnboundedChannelOptions
            {
                SingleWriter = false,
                SingleReader = false
            });
        }

        _workers = new Task[_workerCount];
        for (int i = 0; i < _workerCount; i++)
        {
            _workers[i] = Task.Run(WorkerLoopAsync);
        }

        _logger.LogInformation(
            "[VFS-Precache] Initialized VfsPrecacheQueue with {Workers} workers, MaxCache: {MaxMb:F1} MB",
            _workerCount, _maxCacheBytes / (1024.0 * 1024.0));
    }

    /// <summary>
    ///     Convenience constructor wrapping synchronous asset loader delegate.
    /// </summary>
    public VfsPrecacheQueue(
        Func<string, byte[]?> syncLoader,
        long maxCacheBytes = 256 * 1024 * 1024,
        int? workerCount = null,
        ILogger? logger = null)
        : this((path, _) => Task.FromResult(syncLoader(path)), maxCacheBytes, workerCount, logger)
    {
    }

    /// <summary>
    ///     Normalizes VFS paths to lowercase forward-slash representation.
    /// </summary>
    public static string NormalizePath(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath)) return string.Empty;
        string trimmed = rawPath.Trim().Replace('\\', '/').TrimStart('/');
        return trimmed.ToLowerInvariant();
    }

    /// <inheritdoc />
    public bool Enqueue(string vfsPath, VfsAssetPriority priority = VfsAssetPriority.Normal)
    {
        string norm = NormalizePath(vfsPath);
        if (string.IsNullOrEmpty(norm) || _isDisposed != 0) return false;

        // If already cached, no need to re-enqueue
        if (_cache.ContainsKey(norm)) return true;

        // If already in-flight, return true
        if (_inFlight.ContainsKey(norm)) return true;

        Interlocked.Increment(ref _queuedCount);
        int pIdx = Math.Clamp((int)priority, 0, 3);
        PrecacheWorkItem item = new(norm, priority, null, Stopwatch.GetTimestamp());
        return _priorityChannels[pIdx].Writer.TryWrite(item);
    }

    /// <inheritdoc />
    public ValueTask<ReadOnlyMemory<byte>> GetOrLoadAsync(
        string vfsPath,
        VfsAssetPriority priority = VfsAssetPriority.High,
        CancellationToken ct = default)
    {
        string norm = NormalizePath(vfsPath);
        if (string.IsNullOrEmpty(norm))
            return ValueTask.FromResult(ReadOnlyMemory<byte>.Empty);

        // Tier 0: Direct Lock-Free Memory Cache Hit (0 ms)
        if (_cache.TryGetValue(norm, out CacheEntry? existing))
        {
            Interlocked.Increment(ref _cacheHitCount);
            // Touch timestamp for LRU tracking
            _cache[norm] = existing with { TimestampTicks = Stopwatch.GetTimestamp() };
            return ValueTask.FromResult(existing.Memory);
        }

        Interlocked.Increment(ref _cacheMissCount);

        // Tier 1: In-Flight Task Deduplication
        Task<ReadOnlyMemory<byte>> task = _inFlight.GetOrAdd(norm, pathKey =>
        {
            TaskCompletionSource<ReadOnlyMemory<byte>> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Increment(ref _queuedCount);
            int pIdx = Math.Clamp((int)priority, 0, 3);
            PrecacheWorkItem item = new(pathKey, priority, tcs, Stopwatch.GetTimestamp());
            if (!_priorityChannels[pIdx].Writer.TryWrite(item))
            {
                // Fallback: execute loader synchronously inside task if channel is closed
                return Task.Run(async () => await LoadAndCacheDirectAsync(pathKey, ct).ConfigureAwait(false), ct);
            }
            return tcs.Task;
        });

        return new ValueTask<ReadOnlyMemory<byte>>(task);
    }

    /// <inheritdoc />
    public bool TryGetCached(string vfsPath, out ReadOnlyMemory<byte> memory)
    {
        string norm = NormalizePath(vfsPath);
        if (!string.IsNullOrEmpty(norm) && _cache.TryGetValue(norm, out CacheEntry? entry))
        {
            Interlocked.Increment(ref _cacheHitCount);
            memory = entry.Memory;
            return true;
        }

        memory = ReadOnlyMemory<byte>.Empty;
        return false;
    }

    /// <inheritdoc />
    public void Invalidate(string vfsPath)
    {
        string norm = NormalizePath(vfsPath);
        if (!string.IsNullOrEmpty(norm) && _cache.TryRemove(norm, out CacheEntry? removed))
        {
            Interlocked.Add(ref _totalCachedBytes, -removed.SizeBytes);
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        _cache.Clear();
        _inFlight.Clear();
        Interlocked.Exchange(ref _totalCachedBytes, 0);
    }

    /// <inheritdoc />
    public VfsPrecacheMetrics GetMetrics()
    {
        long completed = Interlocked.Read(ref _completedCount);
        long latencyTicks = Interlocked.Read(ref _totalLatencyTicks);
        double avgLatencyMs = (completed > 0)
            ? (latencyTicks * 1000.0 / Stopwatch.Frequency) / completed
            : 0.0;

        return new VfsPrecacheMetrics(
            QueuedCount: Interlocked.Read(ref _queuedCount),
            CompletedCount: completed,
            FailedCount: Interlocked.Read(ref _failedCount),
            CacheHitCount: Interlocked.Read(ref _cacheHitCount),
            CacheMissCount: Interlocked.Read(ref _cacheMissCount),
            TotalCachedBytes: Interlocked.Read(ref _totalCachedBytes),
            ActiveCachedEntries: _cache.Count,
            AverageLatencyMs: avgLatencyMs
        );
    }

    private async Task WorkerLoopAsync()
    {
        CancellationToken ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                PrecacheWorkItem? workItem = null;

                // Priority Cascade: Critical (3) -> High (2) -> Normal (1) -> Low (0)
                for (int p = 3; p >= 0; p--)
                {
                    if (_priorityChannels[p].Reader.TryRead(out PrecacheWorkItem? item))
                    {
                        workItem = item;
                        break;
                    }
                }

                if (workItem == null)
                {
                    // Await next item on any channel
                    Task<bool>[] waitTasks = new Task<bool>[4];
                    for (int p = 0; p < 4; p++)
                    {
                        waitTasks[p] = _priorityChannels[p].Reader.WaitToReadAsync(ct).AsTask();
                    }

                    Task<bool> firstReady = await Task.WhenAny(waitTasks).ConfigureAwait(false);
                    if (await firstReady.ConfigureAwait(false))
                    {
                        continue;
                    }

                    // All channels closed
                    break;
                }

                await ProcessWorkItemAsync(workItem, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[VFS-Precache] WorkerLoop unhandled exception");
            }
        }
    }

    private async Task ProcessWorkItemAsync(PrecacheWorkItem item, CancellationToken ct)
    {
        string norm = item.NormalizedPath;
        try
        {
            // If already cached by a sibling worker
            if (_cache.TryGetValue(norm, out CacheEntry? existing))
            {
                item.Tcs?.TrySetResult(existing.Memory);
                _inFlight.TryRemove(norm, out _);
                return;
            }

            long startTicks = Stopwatch.GetTimestamp();
            byte[]? decompressed = await _assetLoader(norm, ct).ConfigureAwait(false);
            long elapsedTicks = Stopwatch.GetTimestamp() - startTicks;
            Interlocked.Add(ref _totalLatencyTicks, elapsedTicks);

            if (decompressed != null && decompressed.Length > 0)
            {
                ReadOnlyMemory<byte> memory = decompressed;
                CacheEntry entry = new(memory, decompressed.Length, Stopwatch.GetTimestamp());

                // Enforce memory quota before inserting
                EnsureCacheQuota(decompressed.Length);

                _cache[norm] = entry;
                Interlocked.Add(ref _totalCachedBytes, decompressed.Length);
                Interlocked.Increment(ref _completedCount);

                item.Tcs?.TrySetResult(memory);
            }
            else
            {
                Interlocked.Increment(ref _failedCount);
                item.Tcs?.TrySetResult(ReadOnlyMemory<byte>.Empty);
            }
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _failedCount);
            _logger.LogWarning(ex, "[VFS-Precache] Failed to stream asset {Path}", norm);
            item.Tcs?.TrySetException(ex);
        }
        finally
        {
            _inFlight.TryRemove(norm, out _);
        }
    }

    private async Task<ReadOnlyMemory<byte>> LoadAndCacheDirectAsync(string norm, CancellationToken ct)
    {
        try
        {
            long startTicks = Stopwatch.GetTimestamp();
            byte[]? data = await _assetLoader(norm, ct).ConfigureAwait(false);
            long elapsed = Stopwatch.GetTimestamp() - startTicks;
            Interlocked.Add(ref _totalLatencyTicks, elapsed);

            if (data != null && data.Length > 0)
            {
                ReadOnlyMemory<byte> memory = data;
                EnsureCacheQuota(data.Length);
                _cache[norm] = new CacheEntry(memory, data.Length, Stopwatch.GetTimestamp());
                Interlocked.Add(ref _totalCachedBytes, data.Length);
                Interlocked.Increment(ref _completedCount);
                return memory;
            }

            Interlocked.Increment(ref _failedCount);
            return ReadOnlyMemory<byte>.Empty;
        }
        finally
        {
            _inFlight.TryRemove(norm, out _);
        }
    }

    private void EnsureCacheQuota(long incomingBytes)
    {
        long current = Interlocked.Read(ref _totalCachedBytes);
        if (current + incomingBytes <= _maxCacheBytes) return;

        // Evict LRU entries until under 80% watermark
        long targetBytes = (long)(_maxCacheBytes * 0.80);
        var entriesToEvict = _cache
            .OrderBy(kvp => kvp.Value.TimestampTicks)
            .Take(Math.Max(4, _cache.Count / 4))
            .ToList();

        foreach (var kvp in entriesToEvict)
        {
            if (_cache.TryRemove(kvp.Key, out CacheEntry? removed))
            {
                long remaining = Interlocked.Add(ref _totalCachedBytes, -removed.SizeBytes);
                if (remaining + incomingBytes <= targetBytes) break;
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0) return;

        _cts.Cancel();
        for (int p = 0; p < 4; p++)
        {
            _priorityChannels[p].Writer.TryComplete();
        }

        try
        {
            Task.WaitAll(_workers, TimeSpan.FromMilliseconds(500));
        }
        catch { }

        _cts.Dispose();
        Clear();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0) return;

        _cts.Cancel();
        for (int p = 0; p < 4; p++)
        {
            _priorityChannels[p].Writer.TryComplete();
        }

        try
        {
            await Task.WhenAll(_workers).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch { }

        _cts.Dispose();
        Clear();
    }
}
