// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: VfsPrecacheQueueTests.cs
// System: Kongor Core Unit Tests - [SR-05] Multi-Threaded VFS Asset Streaming
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Kongor.Core.Services.Vfs;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Kongor.Core.Tests.Vfs;

public sealed class VfsPrecacheQueueTests
{
    [Fact]
    public async Task VfsPrecacheQueue_GetOrLoadAsync_DeduplicatesConcurrentRequests()
    {
        int invocationCount = 0;
        TaskCompletionSource releaseLoaderTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<byte[]?> SlowLoader(string path, CancellationToken ct)
        {
            Interlocked.Increment(ref invocationCount);
            await releaseLoaderTcs.Task.WaitAsync(ct);
            return Encoding.UTF8.GetBytes($"Payload for {path}");
        }

        await using VfsPrecacheQueue queue = new(SlowLoader, maxCacheBytes: 1024 * 1024, workerCount: 2);

        // Fire 5 concurrent requests for the exact same path
        Task<ReadOnlyMemory<byte>>[] tasks = new Task<ReadOnlyMemory<byte>>[5];
        for (int i = 0; i < 5; i++)
        {
            tasks[i] = queue.GetOrLoadAsync("heroes/pyromancer/hero.entity", VfsAssetPriority.Critical).AsTask();
        }

        // Release slow loader
        releaseLoaderTcs.SetResult();

        ReadOnlyMemory<byte>[] results = await Task.WhenAll(tasks);

        // All 5 requests must yield identical non-empty results
        foreach (ReadOnlyMemory<byte> res in results)
        {
            res.Length.Should().BeGreaterThan(0);
            Encoding.UTF8.GetString(res.Span).Should().Be("Payload for heroes/pyromancer/hero.entity");
        }

        // Loader must have only been invoked ONCE due to in-flight deduplication
        invocationCount.Should().Be(1);

        VfsPrecacheMetrics metrics = queue.GetMetrics();
        metrics.CompletedCount.Should().Be(1);
    }

    [Fact]
    public async Task VfsPrecacheQueue_GetOrLoadAsync_ReturnsCachedOnSecondCall_WithoutReinvokingLoader()
    {
        int invocationCount = 0;
        byte[] sampleData = [10, 20, 30, 40, 50];

        Task<byte[]?> Loader(string path, CancellationToken ct)
        {
            Interlocked.Increment(ref invocationCount);
            return Task.FromResult<byte[]?>(sampleData);
        }

        await using VfsPrecacheQueue queue = new(Loader, maxCacheBytes: 1024 * 1024);

        // 1. Initial load (Cache Miss)
        ReadOnlyMemory<byte> first = await queue.GetOrLoadAsync("textures/icon.tga", VfsAssetPriority.High);
        first.ToArray().Should().Equal(sampleData);
        invocationCount.Should().Be(1);

        // 2. Second load (Cache Hit - ~0 ms)
        ReadOnlyMemory<byte> second = await queue.GetOrLoadAsync("textures/icon.tga", VfsAssetPriority.High);
        second.ToArray().Should().Equal(sampleData);
        invocationCount.Should().Be(1);

        VfsPrecacheMetrics metrics = queue.GetMetrics();
        metrics.CacheHitCount.Should().Be(1);
        metrics.CacheMissCount.Should().Be(1);
        metrics.ActiveCachedEntries.Should().Be(1);
        metrics.TotalCachedBytes.Should().Be(sampleData.Length);
    }

    [Fact]
    public async Task VfsPrecacheQueue_PriorityOrdering_ProcessesCriticalBeforeLow()
    {
        ConcurrentQueue<string> executionOrder = new();
        TaskCompletionSource workerBlockedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource unblockWorkerTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<byte[]?> OrderingLoader(string path, CancellationToken ct)
        {
            if (path == "blocker")
            {
                workerBlockedTcs.TrySetResult();
                await unblockWorkerTcs.Task.WaitAsync(ct);
                return Encoding.UTF8.GetBytes(path);
            }

            executionOrder.Enqueue(path);
            return Encoding.UTF8.GetBytes(path);
        }

        // Single worker to ensure deterministic FIFO execution by priority
        await using VfsPrecacheQueue queue = new(OrderingLoader, maxCacheBytes: 1024 * 1024, workerCount: 1);

        // Enqueue blocker to guarantee the worker is actively held inside OrderingLoader before items are queued
        queue.Enqueue("blocker", VfsAssetPriority.Low);
        await workerBlockedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Enqueue items across different priorities while worker is guaranteed blocked
        queue.Enqueue("low/asset_01.tga", VfsAssetPriority.Low);
        queue.Enqueue("low/asset_02.tga", VfsAssetPriority.Low);
        queue.Enqueue("critical/hero.entity", VfsAssetPriority.Critical);
        queue.Enqueue("high/model.mdf", VfsAssetPriority.High);

        // Unblock worker
        unblockWorkerTcs.SetResult();

        // Await until all 4 items are processed
        for (int i = 0; i < 50; i++)
        {
            if (executionOrder.Count >= 4) break;
            await Task.Delay(20);
        }

        executionOrder.Count.Should().Be(4);
        string[] order = executionOrder.ToArray();

        // Critical must be processed first, followed by High, then Low
        order[0].Should().Be("critical/hero.entity");
        order[1].Should().Be("high/model.mdf");
        order.Should().Contain("low/asset_01.tga");
        order.Should().Contain("low/asset_02.tga");
    }

    [Fact]
    public async Task VfsPrecacheQueue_EnsureCacheQuota_EvictsOldestEntries_WhenLimitExceeded()
    {
        // 100 KB max quota
        long maxQuota = 100 * 1024;
        Task<byte[]?> LargeLoader(string path, CancellationToken ct)
        {
            // Each asset is 40 KB
            return Task.FromResult<byte[]?>(new byte[40 * 1024]);
        }

        await using VfsPrecacheQueue queue = new(LargeLoader, maxCacheBytes: maxQuota, workerCount: 2);

        // Load 4 items = 160 KB total (exceeds 100 KB limit)
        await queue.GetOrLoadAsync("item1.dds");
        await Task.Delay(10);
        await queue.GetOrLoadAsync("item2.dds");
        await Task.Delay(10);
        await queue.GetOrLoadAsync("item3.dds");
        await Task.Delay(10);
        await queue.GetOrLoadAsync("item4.dds");

        VfsPrecacheMetrics metrics = queue.GetMetrics();
        // Quota must be honored (total cached bytes <= maxQuota)
        metrics.TotalCachedBytes.Should().BeLessThanOrEqualTo(maxQuota);
        metrics.ActiveCachedEntries.Should().BeLessThan(4);
    }

    [Fact]
    public async Task VfsPrecacheQueue_TryGetCached_And_Invalidate_OperatesCorrectly()
    {
        byte[] payload = [1, 2, 3, 4, 5, 6, 7, 8];
        await using VfsPrecacheQueue queue = new((path, _) => Task.FromResult<byte[]?>(payload));

        // Not cached initially
        queue.TryGetCached("heroes/glacius/icon.tga", out ReadOnlyMemory<byte> memoryBefore).Should().BeFalse();
        memoryBefore.IsEmpty.Should().BeTrue();

        // Load and verify cached
        await queue.GetOrLoadAsync("heroes/glacius/icon.tga");
        queue.TryGetCached("heroes/glacius/icon.tga", out ReadOnlyMemory<byte> memoryAfter).Should().BeTrue();
        memoryAfter.ToArray().Should().Equal(payload);

        // Invalidate
        queue.Invalidate("heroes/glacius/icon.tga");
        queue.TryGetCached("heroes/glacius/icon.tga", out _).Should().BeFalse();
        queue.GetMetrics().TotalCachedBytes.Should().Be(0);
    }
}
