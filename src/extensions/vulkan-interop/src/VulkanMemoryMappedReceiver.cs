// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: VulkanMemoryMappedReceiver.cs
// System: NEXUS Replay Video Renderer Service
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Collections.Generic;
using System.IO.MemoryMappedFiles;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace ReplayVulkanRenderer;

/// <summary>
///     High-performance Win32 Shared Memory receiver for Vulkan swapchain frame capture.
///     Consumes uncompressed triple-buffered BGRA8 video frames directly from
///     MemoryMappedFile with Win32 EventWaitHandle synchronization.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class VulkanMemoryMappedReceiver : IAsyncDisposable
{
    private readonly uint _height;
    private readonly ILogger<VulkanMemoryMappedReceiver> _logger;
    private readonly BidirectionalPageMemoryTracker _pageTracker;
    private readonly int _slotCount;
    private readonly long _slotStride;
    private readonly long _totalMapSize;
    private readonly uint _width;
    private MemoryMappedViewAccessor? _accessor;
    private Task? _consumerTask;

    private CancellationTokenSource? _cts;
    private bool _disposed;
    private EventWaitHandle[] _doneEvents = [];

    private MemoryMappedFile? _mmf;
    private EventWaitHandle[] _readyEvents = [];
    private long _skippedUnmodifiedFramesCount;
    private long _totalBytesReceived;
    private long _totalFramesReceived;

    public VulkanMemoryMappedReceiver(
        ILogger<VulkanMemoryMappedReceiver> logger,
        int processId,
        uint width,
        uint height,
        int slotCount = 3,
        string? customMapName = null,
        bool skipUnmodifiedMenuFrames = false)
    {
        _logger = logger;
        ProcessId = processId;
        _width = width;
        _height = height;
        _slotCount = slotCount;
        SkipUnmodifiedMenuFrames = skipUnmodifiedMenuFrames;

        int headerSize = Marshal.SizeOf<FrameHeader>(); // 64 bytes
        long pixelDataSize = (long)width * height * 4;
        _slotStride = headerSize + pixelDataSize;
        _totalMapSize = _slotStride * slotCount;
        _pageTracker = new BidirectionalPageMemoryTracker((ulong)Math.Max(pixelDataSize, BidirectionalPageMemoryTracker.PageSizeBytes));
        _pageTracker.MarkRegionGpuModified(0UL, (ulong)Math.Max(pixelDataSize, BidirectionalPageMemoryTracker.PageSizeBytes));

        MapName = customMapName ?? $"Local\\HonReplayFrameBuffer_{processId}";

        _logger.LogInformation(
            "[SharedMemoryReceiver] Configured shared memory mapping: {MapName} ({Slots} slots, {Stride:N0} bytes/slot, Total: {Total:N0} bytes, SkipUnmodifiedMenuFrames={SkipUnmodified}).",
            MapName, slotCount, _slotStride, _totalMapSize, SkipUnmodifiedMenuFrames);
    }

    public int ProcessId { get; }

    public string MapName { get; }

    public BidirectionalPageMemoryTracker PageTracker => _pageTracker;

    public bool SkipUnmodifiedMenuFrames { get; set; }

    public long TotalFramesReceived => Interlocked.Read(ref _totalFramesReceived);
    public long TotalBytesReceived => Interlocked.Read(ref _totalBytesReceived);
    public long SkippedUnmodifiedFramesCount => Interlocked.Read(ref _skippedUnmodifiedFramesCount);

    /// <summary>
    /// (#206) Copies only GPU-modified 4KB page spans from <paramref name="sourceFrame"/> into <paramref name="destinationFrame"/>,
    /// skipping full-frame copies when an unmodified menu frame is presented or copying only dirty HUD/UI sub-regions.
    /// </summary>
    public bool TryCopyModifiedFrameRegions(ReadOnlySpan<byte> sourceFrame, Span<byte> destinationFrame, out int bytesCopied)
    {
        bytesCopied = 0;
        int frameLen = Math.Min(sourceFrame.Length, destinationFrame.Length);
        if (frameLen <= 0)
        {
            return false;
        }

        if (!_pageTracker.IsRegionGpuModified(0UL, (ulong)frameLen))
        {
            Interlocked.Increment(ref _skippedUnmodifiedFramesCount);
            return false;
        }

        List<DirtyPageSpan> dirtySpans = _pageTracker.CollectDirtyGpuSpansAndClear(clearAfterCollect: true);
        for (int i = 0; i < dirtySpans.Count; i++)
        {
            DirtyPageSpan span = dirtySpans[i];
            if (span.ByteOffset >= (ulong)frameLen)
            {
                continue;
            }

            int offset = (int)span.ByteOffset;
            int length = (int)Math.Min(span.ByteLength, (ulong)(frameLen - offset));
            if (length > 0)
            {
                sourceFrame.Slice(offset, length).CopyTo(destinationFrame.Slice(offset, length));
                bytesCopied += length;
            }
        }

        Interlocked.Add(ref _totalBytesReceived, bytesCopied);
        Interlocked.Increment(ref _totalFramesReceived);
        return bytesCopied > 0;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        _disposed = true;
        _logger.LogInformation("[SharedMemoryReceiver] Disposing Vulkan Shared Memory Receiver...");

        if (_cts != null)
        {
            _cts.Cancel();
            if (_consumerTask != null)
                try
                {
                    await _consumerTask;
                }
                catch
                {
                    // Suppress cancellation
                }

            _cts.Dispose();
            _cts = null;
        }

        for (int i = 0; i < _readyEvents.Length; i++) _readyEvents[i].Dispose();

        for (int i = 0; i < _doneEvents.Length; i++) _doneEvents[i].Dispose();

        _accessor?.Dispose();
        _accessor = null;
        _mmf?.Dispose();
        _mmf = null;
    }

/// <summary>
///     Delegate for direct unmanaged pointer blitting into native framebuffers (e.g. NativeBufferRing.BackBufferPtr) without heap byte-array allocation.
/// </summary>
public delegate ValueTask DirectFrameBlitHandler(FrameHeader header, IntPtr sourcePointer, int sizeBytes);

    /// <summary>
    ///     Initializes shared memory map, opens/creates sync events, and starts the asynchronous frame consumption loop
    ///     using managed ReadOnlyMemory byte arrays.
    /// </summary>
    public Task StartAsync(Func<FrameHeader, ReadOnlyMemory<byte>, ValueTask> onFrameReceived,
        CancellationToken cancellationToken)
    {
        return StartInternalAsync(onFrameReceived, null, cancellationToken);
    }

    /// <summary>
    ///     Initializes shared memory map, opens/creates sync events, and starts the zero-copy unmanaged pointer consumption loop
    ///     directly blitting into destination pointers without managed allocations.
    /// </summary>
    public Task StartAsync(DirectFrameBlitHandler onDirectBlit,
        CancellationToken cancellationToken)
    {
        return StartInternalAsync(null, onDirectBlit, cancellationToken);
    }

    private Task StartInternalAsync(
        Func<FrameHeader, ReadOnlyMemory<byte>, ValueTask>? onFrameReceived,
        DirectFrameBlitHandler? onDirectBlit,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("[SharedMemoryReceiver] Initializing Win32 Shared Memory mapping and sync events...");

        // Create or open MemoryMappedFile
        _mmf = MemoryMappedFile.CreateOrOpen(MapName, _totalMapSize, MemoryMappedFileAccess.ReadWrite);
        _accessor = _mmf.CreateViewAccessor(0, _totalMapSize, MemoryMappedFileAccess.ReadWrite);

        _readyEvents = new EventWaitHandle[_slotCount];
        _doneEvents = new EventWaitHandle[_slotCount];

        for (int i = 0; i < _slotCount; i++)
        {
            string readyEventName = $"Local\\HonFrameReady_{ProcessId}_{i}";
            string doneEventName = $"Local\\HonFrameDone_{ProcessId}_{i}";

            _readyEvents[i] = new EventWaitHandle(false, EventResetMode.AutoReset, readyEventName);
            _doneEvents[i] = new EventWaitHandle(true, EventResetMode.AutoReset, doneEventName);
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _consumerTask = Task.Run(() => ConsumerLoopAsync(onFrameReceived, onDirectBlit, _cts.Token), CancellationToken.None);

        return Task.CompletedTask;
    }

    private async Task ConsumerLoopAsync(
        Func<FrameHeader, ReadOnlyMemory<byte>, ValueTask>? onFrameReceived,
        DirectFrameBlitHandler? onDirectBlit,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "[SharedMemoryReceiver] Frame consumption loop started (Zero-Copy Blit: {ZeroCopy}). Awaiting Vulkan Layer signals...",
            onDirectBlit != null);

        int currentSlot = 0;
        int headerSize = Marshal.SizeOf<FrameHeader>();
        int pixelDataSize = (int)(_width * _height * 4);
        byte[]? localPixelBuffer = onDirectBlit == null ? new byte[pixelDataSize] : null;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                EventWaitHandle readyEvent = _readyEvents[currentSlot];
                EventWaitHandle doneEvent = _doneEvents[currentSlot];

                // Non-blocking asynchronous wait pattern for Win32 event
                bool signaled = await Task.Run(() => readyEvent.WaitOne(100), cancellationToken);
                if (!signaled) continue;

                long slotOffset = currentSlot * _slotStride;

                // Read FrameHeader
                FrameHeader header;
                _accessor!.Read(slotOffset, out header);

                if (!header.IsValid())
                {
                    _logger.LogWarning(
                        "[SharedMemoryReceiver] Received invalid frame header in slot {Slot}: {Header}. Skipping frame.",
                        currentSlot, header);
                    doneEvent.Set();
                    currentSlot = (currentSlot + 1) % _slotCount;
                    continue;
                }

                if (SkipUnmodifiedMenuFrames && !_pageTracker.IsRegionGpuModified(0UL, (ulong)pixelDataSize))
                {
                    Interlocked.Increment(ref _skippedUnmodifiedFramesCount);
                    doneEvent.Set();
                    currentSlot = (currentSlot + 1) % _slotCount;
                    continue;
                }

                if (onDirectBlit != null)
                {
                    IntPtr baseAddress = _accessor.SafeMemoryMappedViewHandle.DangerousGetHandle();
                    IntPtr pixelSrc = IntPtr.Add(baseAddress, checked((int)(slotOffset + headerSize + _accessor.PointerOffset)));
                    await onDirectBlit(header, pixelSrc, pixelDataSize);

                    // Acknowledge frame slot to release Vulkan staging buffer
                    doneEvent.Set();
                }
                else if (onFrameReceived != null && localPixelBuffer != null)
                {
                    // Read raw BGRA pixel data for dirty spans (#206)
                    List<DirtyPageSpan> dirtySpans = _pageTracker.CollectDirtyGpuSpansAndClear(clearAfterCollect: false);
                    if (dirtySpans.Count > 0 && dirtySpans.Count * BidirectionalPageMemoryTracker.PageSizeBytes < pixelDataSize)
                    {
                        for (int s = 0; s < dirtySpans.Count; s++)
                        {
                            DirtyPageSpan span = dirtySpans[s];
                            if (span.ByteOffset >= (ulong)pixelDataSize)
                            {
                                continue;
                            }

                            int spanOffset = (int)span.ByteOffset;
                            int spanLength = (int)Math.Min(span.ByteLength, (ulong)(pixelDataSize - spanOffset));
                            if (spanLength > 0)
                            {
                                _accessor.ReadArray(slotOffset + headerSize + spanOffset, localPixelBuffer, spanOffset, spanLength);
                            }
                        }
                    }
                    else
                    {
                        _accessor.ReadArray(slotOffset + headerSize, localPixelBuffer, 0, pixelDataSize);
                    }

                    // Acknowledge frame slot to release Vulkan staging buffer
                    doneEvent.Set();

                    await onFrameReceived(header, localPixelBuffer.AsMemory(0, pixelDataSize));
                }

                long frameCount = Interlocked.Increment(ref _totalFramesReceived);
                Interlocked.Add(ref _totalBytesReceived, pixelDataSize);

                if (frameCount % 300 == 0 || frameCount == 1)
                    _logger.LogInformation("[SharedMemoryReceiver] Frame #{Frame}: {Header} ({TotalMB:F2} MB total).",
                        frameCount, header, Interlocked.Read(ref _totalBytesReceived) / (1024.0 * 1024.0));

                if (header.IsEndOfStream())
                {
                    _logger.LogInformation(
                        "[SharedMemoryReceiver] End of Stream flag received in frame header. Exiting consumer loop.");
                    break;
                }

                currentSlot = (currentSlot + 1) % _slotCount;
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[SharedMemoryReceiver] Consumer loop canceled cleanly.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SharedMemoryReceiver] Exception in shared memory consumer loop: {Message}",
                ex.Message);
        }
    }
}

// ============================================================================
// (#204) Timeline-Semaphore Command Scheduler & Dirty-State Bitmask Tracker
// Inspired by shadPS4/src/video_core/renderer_vulkan/vk_scheduler.cpp:32-279
// ============================================================================

[Flags]
public enum VulkanDirtyStateFlags : uint
{
    None = 0,
    Viewport = 1u << 0,
    Scissor = 1u << 1,
    DepthBias = 1u << 2,
    Pipeline = 1u << 3,
    BlendConstants = 1u << 4,
    StencilCompareMask = 1u << 5,
    AllDynamicStates = Viewport | Scissor | DepthBias | Pipeline | BlendConstants | StencilCompareMask
}

public readonly record struct DeferredResourceRetirementEntry(
    ulong RetireAtTimelineTick,
    ulong ResourceHandle,
    string ResourceTag);

/// <summary>
/// (#204) Tracks a monotonically increasing VK_SEMAPHORE_TYPE_TIMELINE master clock,
/// a min-priority queue for fence-free deferred resource retirement, and a DirtyStateFlags
/// bitmask that suppresses redundant dynamic-state commands across command buffer splits.
/// </summary>
internal sealed class TimelineSemaphoreDirtyStateScheduler
{
    private readonly PriorityQueue<DeferredResourceRetirementEntry, ulong> _deferredRetirements = new();
    private ulong _submittedTick;
    private ulong _completedGpuTick;
    private VulkanDirtyStateFlags _dirtyFlags = VulkanDirtyStateFlags.AllDynamicStates;

    private float _boundViewportWidth;
    private float _boundViewportHeight;
    private uint _boundScissorWidth;
    private uint _boundScissorHeight;
    private float _boundDepthBiasConstant;
    private float _boundDepthBiasSlope;
    private int _suppressedStateBindCount;
    private int _emittedStateBindCount;

    public ulong CurrentSubmittedTick => _submittedTick;
    public ulong CompletedGpuTick => _completedGpuTick;
    public VulkanDirtyStateFlags DirtyFlags => _dirtyFlags;
    public int PendingRetirementCount => _deferredRetirements.Count;
    public int SuppressedStateBindCount => _suppressedStateBindCount;
    public int EmittedStateBindCount => _emittedStateBindCount;

    public void OnCommandBufferSplit()
    {
        // Across command buffer splits, mark all dynamic state flags dirty so the next draw re-binds once
        _dirtyFlags = VulkanDirtyStateFlags.AllDynamicStates;
    }

    public void SetDirty(VulkanDirtyStateFlags flags)
    {
        _dirtyFlags |= flags;
    }

    public bool TryBindViewport(float width, float height)
    {
        bool valuesChanged = _boundViewportWidth != width || _boundViewportHeight != height;
        bool isDirty = (_dirtyFlags & VulkanDirtyStateFlags.Viewport) != 0;
        if (!valuesChanged && !isDirty)
        {
            _suppressedStateBindCount++;
            return false;
        }

        _boundViewportWidth = width;
        _boundViewportHeight = height;
        _dirtyFlags &= ~VulkanDirtyStateFlags.Viewport;
        _emittedStateBindCount++;
        return true;
    }

    public bool TryBindScissor(uint width, uint height)
    {
        bool valuesChanged = _boundScissorWidth != width || _boundScissorHeight != height;
        bool isDirty = (_dirtyFlags & VulkanDirtyStateFlags.Scissor) != 0;
        if (!valuesChanged && !isDirty)
        {
            _suppressedStateBindCount++;
            return false;
        }

        _boundScissorWidth = width;
        _boundScissorHeight = height;
        _dirtyFlags &= ~VulkanDirtyStateFlags.Scissor;
        _emittedStateBindCount++;
        return true;
    }

    public bool TryBindDepthBias(float constantFactor, float slopeFactor)
    {
        bool valuesChanged = _boundDepthBiasConstant != constantFactor || _boundDepthBiasSlope != slopeFactor;
        bool isDirty = (_dirtyFlags & VulkanDirtyStateFlags.DepthBias) != 0;
        if (!valuesChanged && !isDirty)
        {
            _suppressedStateBindCount++;
            return false;
        }

        _boundDepthBiasConstant = constantFactor;
        _boundDepthBiasSlope = slopeFactor;
        _dirtyFlags &= ~VulkanDirtyStateFlags.DepthBias;
        _emittedStateBindCount++;
        return true;
    }

    public ulong SubmitCommandChunk()
    {
        _submittedTick++;
        return _submittedTick;
    }

    public void EnqueueDeferredRetirement(ulong resourceHandle, string resourceTag, ulong retireAtTick = 0UL)
    {
        ulong effectiveTick = retireAtTick > 0UL ? retireAtTick : (_submittedTick > 0UL ? _submittedTick : 1UL);
        DeferredResourceRetirementEntry entry = new DeferredResourceRetirementEntry(
            effectiveTick,
            resourceHandle,
            resourceTag ?? string.Empty);
        _deferredRetirements.Enqueue(entry, effectiveTick);
    }

    public List<DeferredResourceRetirementEntry> PollCompletedGpuTimeline(ulong completedTick)
    {
        if (completedTick > _completedGpuTick)
        {
            _completedGpuTick = completedTick;
        }

        List<DeferredResourceRetirementEntry> retired = new List<DeferredResourceRetirementEntry>();
        while (_deferredRetirements.TryPeek(out DeferredResourceRetirementEntry top, out ulong priorityTick))
        {
            if (priorityTick > _completedGpuTick)
            {
                break;
            }
            retired.Add(_deferredRetirements.Dequeue());
        }

        return retired;
    }
}

// ============================================================================
// (#205) Three-Tier Staging Buffer Pool with Idle-Frame Garbage Collection
// Inspired by shadPS4/src/video_core/buffer_cache/buffer_cache.cpp:542-932 & stream_buffer.cpp
// ============================================================================

public enum StagingBufferTier : byte
{
    Tier1RingStreamBuffer = 1,
    Tier2FencedLargePool = 2
}

public readonly record struct StagingAllocationLease(
    StagingBufferTier Tier,
    ulong BufferId,
    ulong OffsetBytes,
    ulong SizeBytes,
    ulong ReadyTimelineTick);

/// <summary>
/// (#205) Three-tier staging buffer allocator:
/// - Tier 1: Persistent ring StreamBuffer with timeline-wait free-offset cursor for small readbacks/uploads.
/// - Tier 2: Fenced large staging buffer pool for full-frame captures.
/// - Tier 3: Periodic TickFrame() GC that evicts unused Tier 2 staging buffers after MaxIdleFrames epochs.
/// </summary>
public sealed class ThreeTierStagingBufferPool
{
    private sealed class PooledLargeStagingBuffer
    {
        public ulong BufferId { get; init; }
        public ulong CapacityBytes { get; init; }
        public ulong BusyUntilTick { get; set; }
        public ulong LastUsedFrameEpoch { get; set; }
        public bool InUse { get; set; }
    }

    public const ulong DefaultTier1RingCapacityBytes = 4UL * 1024UL * 1024UL; // 4 MiB ring
    public const ulong DefaultTier1MaxAllocationBytes = 256UL * 1024UL;       // <= 256 KiB uses Tier 1 ring
    public const uint DefaultMaxIdleFrames = 64U;

    private readonly ulong _ringCapacityBytes;
    private readonly ulong _tier1MaxAllocBytes;
    private readonly uint _maxIdleFrames;
    private readonly List<PooledLargeStagingBuffer> _tier2Pool = new();

    private ulong _ringWriteCursor;
    private ulong _currentFrameEpoch;
    private ulong _completedTimelineTick;
    private ulong _nextBufferId = 1000UL;
    private int _evictedBuffersTotal;
    private int _tier1AllocationsTotal;
    private int _tier2ReusedAllocationsTotal;
    private int _tier2NewAllocationsTotal;

    public ThreeTierStagingBufferPool(
        ulong ringCapacityBytes = DefaultTier1RingCapacityBytes,
        ulong tier1MaxAllocBytes = DefaultTier1MaxAllocationBytes,
        uint maxIdleFrames = DefaultMaxIdleFrames)
    {
        _ringCapacityBytes = ringCapacityBytes > 0UL ? ringCapacityBytes : DefaultTier1RingCapacityBytes;
        _tier1MaxAllocBytes = tier1MaxAllocBytes > 0UL ? tier1MaxAllocBytes : DefaultTier1MaxAllocationBytes;
        _maxIdleFrames = maxIdleFrames > 0U ? maxIdleFrames : DefaultMaxIdleFrames;
    }

    public ulong CurrentFrameEpoch => _currentFrameEpoch;
    public ulong RingWriteCursor => _ringWriteCursor;
    public int Tier2PoolCount => _tier2Pool.Count;
    public int EvictedBuffersTotal => _evictedBuffersTotal;
    public int Tier1AllocationsTotal => _tier1AllocationsTotal;
    public int Tier2ReusedAllocationsTotal => _tier2ReusedAllocationsTotal;
    public int Tier2NewAllocationsTotal => _tier2NewAllocationsTotal;

    public StagingAllocationLease Allocate(ulong sizeBytes, ulong submissionTimelineTick, ulong alignmentBytes = 256UL)
    {
        ulong effectiveSize = sizeBytes > 0UL ? sizeBytes : 1UL;
        ulong align = alignmentBytes > 0UL ? alignmentBytes : 1UL;

        // Tier 1: Small allocation routed to persistent ring StreamBuffer
        if (effectiveSize <= _tier1MaxAllocBytes && effectiveSize <= _ringCapacityBytes)
        {
            ulong alignedCursor = (_ringWriteCursor + align - 1UL) & ~(align - 1UL);
            if (alignedCursor + effectiveSize > _ringCapacityBytes)
            {
                // Wrap ring cursor to 0
                alignedCursor = 0UL;
            }

            _ringWriteCursor = alignedCursor + effectiveSize;
            _tier1AllocationsTotal++;
            return new StagingAllocationLease(
                StagingBufferTier.Tier1RingStreamBuffer,
                1UL,
                alignedCursor,
                effectiveSize,
                submissionTimelineTick);
        }

        // Tier 2: Fenced large staging buffer pool
        ulong roundedCapacity = (effectiveSize + 4095UL) & ~4095UL;
        for (int i = 0; i < _tier2Pool.Count; i++)
        {
            PooledLargeStagingBuffer candidate = _tier2Pool[i];
            if (!candidate.InUse &&
                candidate.BusyUntilTick <= _completedTimelineTick &&
                candidate.CapacityBytes >= roundedCapacity &&
                candidate.CapacityBytes <= roundedCapacity * 2UL)
            {
                candidate.InUse = true;
                candidate.BusyUntilTick = submissionTimelineTick;
                candidate.LastUsedFrameEpoch = _currentFrameEpoch;
                _tier2ReusedAllocationsTotal++;
                return new StagingAllocationLease(
                    StagingBufferTier.Tier2FencedLargePool,
                    candidate.BufferId,
                    0UL,
                    roundedCapacity,
                    submissionTimelineTick);
            }
        }

        ulong newId = ++_nextBufferId;
        PooledLargeStagingBuffer created = new PooledLargeStagingBuffer
        {
            BufferId = newId,
            CapacityBytes = roundedCapacity,
            BusyUntilTick = submissionTimelineTick,
            LastUsedFrameEpoch = _currentFrameEpoch,
            InUse = true
        };
        _tier2Pool.Add(created);
        _tier2NewAllocationsTotal++;

        return new StagingAllocationLease(
            StagingBufferTier.Tier2FencedLargePool,
            newId,
            0UL,
            roundedCapacity,
            submissionTimelineTick);
    }

    public void ReleaseLease(in StagingAllocationLease lease)
    {
        if (lease.Tier != StagingBufferTier.Tier2FencedLargePool)
        {
            return;
        }

        for (int i = 0; i < _tier2Pool.Count; i++)
        {
            if (_tier2Pool[i].BufferId == lease.BufferId)
            {
                _tier2Pool[i].InUse = false;
                _tier2Pool[i].LastUsedFrameEpoch = _currentFrameEpoch;
                break;
            }
        }
    }

    /// <summary>
    /// (#205) Tier 3 periodic frame GC: updates completed GPU timeline tick, marks finished Tier 2
    /// buffers idle, and evicts any buffer unused for &gt;= MaxIdleFrames epochs.
    /// </summary>
    public int TickFrame(ulong completedGpuTimelineTick)
    {
        _currentFrameEpoch++;
        if (completedGpuTimelineTick > _completedTimelineTick)
        {
            _completedTimelineTick = completedGpuTimelineTick;
        }

        int evictedThisTick = 0;
        for (int i = _tier2Pool.Count - 1; i >= 0; i--)
        {
            PooledLargeStagingBuffer buf = _tier2Pool[i];
            if (buf.InUse && buf.BusyUntilTick <= _completedTimelineTick)
            {
                buf.InUse = false;
            }

            if (!buf.InUse &&
                buf.BusyUntilTick <= _completedTimelineTick &&
                _currentFrameEpoch >= buf.LastUsedFrameEpoch &&
                (_currentFrameEpoch - buf.LastUsedFrameEpoch) >= _maxIdleFrames)
            {
                _tier2Pool.RemoveAt(i);
                evictedThisTick++;
                _evictedBuffersTotal++;
            }
        }

        return evictedThisTick;
    }
}

// ============================================================================
// (#206) Two-Level 4KB Page Bitmask Memory Tracker (cpu_modified / gpu_modified)
// Inspired by shadPS4/src/video_core/buffer_cache/memory_tracker_base.h & region_manager.h
// ============================================================================

public readonly record struct DirtyPageSpan(
    int StartPageIndex,
    int PageCount,
    ulong ByteOffset,
    ulong ByteLength);

/// <summary>
/// (#206) Two-level 4KB page tracker with 64-page (256KB) L1 <see cref="ulong"/> bitmasks
/// tracking bidirectional <c>cpu_modified</c> and <c>gpu_modified</c> dirty pages via
/// <see cref="BitOperations.TrailingZeroCount(ulong)"/>.
/// </summary>
public sealed class BidirectionalPageMemoryTracker
{
    public const int PageSizeBytes = 4096;               // 4 KiB per page
    public const int PagesPerL1Word = 64;                // 64 pages per uint64_t word
    public const int L1RegionBytes = PageSizeBytes * PagesPerL1Word; // 256 KiB per L1 word

    private readonly ulong _totalSizeBytes;
    private readonly int _totalPages;
    private readonly ulong[] _cpuModifiedWords;
    private readonly ulong[] _gpuModifiedWords;

    public BidirectionalPageMemoryTracker(ulong totalSizeBytes)
    {
        _totalSizeBytes = totalSizeBytes > 0UL ? totalSizeBytes : (ulong)L1RegionBytes;
        _totalPages = (int)((_totalSizeBytes + (ulong)PageSizeBytes - 1UL) / (ulong)PageSizeBytes);
        int wordCount = (_totalPages + PagesPerL1Word - 1) / PagesPerL1Word;
        _cpuModifiedWords = new ulong[wordCount];
        _gpuModifiedWords = new ulong[wordCount];
    }

    public ulong TotalSizeBytes => _totalSizeBytes;
    public int TotalPages => _totalPages;
    public int L1WordCount => _cpuModifiedWords.Length;

    public void MarkRegionCpuModified(ulong byteOffset, ulong byteLength)
    {
        UpdateBits(_cpuModifiedWords, byteOffset, byteLength, setBits: true);
    }

    public void MarkRegionGpuModified(ulong byteOffset, ulong byteLength)
    {
        UpdateBits(_gpuModifiedWords, byteOffset, byteLength, setBits: true);
    }

    public void ClearRegionCpuModified(ulong byteOffset, ulong byteLength)
    {
        UpdateBits(_cpuModifiedWords, byteOffset, byteLength, setBits: false);
    }

    public void ClearRegionGpuModified(ulong byteOffset, ulong byteLength)
    {
        UpdateBits(_gpuModifiedWords, byteOffset, byteLength, setBits: false);
    }

    public bool IsRegionCpuModified(ulong byteOffset, ulong byteLength)
    {
        return QueryBits(_cpuModifiedWords, byteOffset, byteLength);
    }

    public bool IsRegionGpuModified(ulong byteOffset, ulong byteLength)
    {
        return QueryBits(_gpuModifiedWords, byteOffset, byteLength);
    }

    public List<DirtyPageSpan> CollectDirtyCpuSpansAndClear(bool clearAfterCollect = false)
    {
        return CollectDirtySpans(_cpuModifiedWords, clearAfterCollect);
    }

    public List<DirtyPageSpan> CollectDirtyGpuSpansAndClear(bool clearAfterCollect = false)
    {
        return CollectDirtySpans(_gpuModifiedWords, clearAfterCollect);
    }

    private void UpdateBits(ulong[] words, ulong byteOffset, ulong byteLength, bool setBits)
    {
        if (byteLength == 0UL || byteOffset >= _totalSizeBytes)
        {
            return;
        }

        ulong clampedEnd = byteOffset + byteLength;
        if (clampedEnd > _totalSizeBytes || clampedEnd < byteOffset)
        {
            clampedEnd = _totalSizeBytes;
        }

        int firstPage = (int)(byteOffset / (ulong)PageSizeBytes);
        int lastPage = (int)((clampedEnd - 1UL) / (ulong)PageSizeBytes);

        for (int page = firstPage; page <= lastPage && page < _totalPages; page++)
        {
            int wordIdx = page / PagesPerL1Word;
            int bitIdx = page % PagesPerL1Word;
            ulong mask = 1UL << bitIdx;
            if (setBits)
            {
                words[wordIdx] |= mask;
            }
            else
            {
                words[wordIdx] &= ~mask;
            }
        }
    }

    private bool QueryBits(ulong[] words, ulong byteOffset, ulong byteLength)
    {
        if (byteLength == 0UL || byteOffset >= _totalSizeBytes)
        {
            return false;
        }

        ulong clampedEnd = byteOffset + byteLength;
        if (clampedEnd > _totalSizeBytes || clampedEnd < byteOffset)
        {
            clampedEnd = _totalSizeBytes;
        }

        int firstPage = (int)(byteOffset / (ulong)PageSizeBytes);
        int lastPage = (int)((clampedEnd - 1UL) / (ulong)PageSizeBytes);

        for (int page = firstPage; page <= lastPage && page < _totalPages; page++)
        {
            int wordIdx = page / PagesPerL1Word;
            int bitIdx = page % PagesPerL1Word;
            if ((words[wordIdx] & (1UL << bitIdx)) != 0UL)
            {
                return true;
            }
        }

        return false;
    }

    private List<DirtyPageSpan> CollectDirtySpans(ulong[] words, bool clearAfterCollect)
    {
        List<DirtyPageSpan> spans = new List<DirtyPageSpan>();
        int currentStartPage = -1;
        int currentCount = 0;

        for (int wordIdx = 0; wordIdx < words.Length; wordIdx++)
        {
            ulong word = words[wordIdx];
            if (clearAfterCollect)
            {
                words[wordIdx] = 0UL;
            }

            while (word != 0UL)
            {
                int bit = BitOperations.TrailingZeroCount(word);
                int pageIdx = (wordIdx * PagesPerL1Word) + bit;
                if (pageIdx < _totalPages)
                {
                    if (currentStartPage < 0)
                    {
                        currentStartPage = pageIdx;
                        currentCount = 1;
                    }
                    else if (pageIdx == currentStartPage + currentCount)
                    {
                        currentCount++;
                    }
                    else
                    {
                        spans.Add(new DirtyPageSpan(
                            currentStartPage,
                            currentCount,
                            (ulong)currentStartPage * (ulong)PageSizeBytes,
                            (ulong)currentCount * (ulong)PageSizeBytes));
                        currentStartPage = pageIdx;
                        currentCount = 1;
                    }
                }

                word &= word - 1UL;
            }
        }

        if (currentStartPage >= 0 && currentCount > 0)
        {
            spans.Add(new DirtyPageSpan(
                currentStartPage,
                currentCount,
                (ulong)currentStartPage * (ulong)PageSizeBytes,
                (ulong)currentCount * (ulong)PageSizeBytes));
        }

        return spans;
    }
}

// ============================================================================
// (#207) GPU Fault-Buffer Compute Ring & Timeline-Deferred Buffer Streaming
// Inspired by shadPS4/src/video_core/buffer_cache/buffer_cache.cpp:250-540
// ============================================================================

public readonly record struct RegisteredGpuBufferRegion(
    ulong BufferId,
    string DebugLabel,
    ulong BaseVirtualAddress,
    ulong SizeBytes);

public readonly record struct GpuPageFaultIsolationRecord(
    int PageIndex,
    ulong FaultVirtualAddress,
    ulong MatchedBufferId,
    string MatchedBufferLabel,
    ulong OffsetInBufferBytes);

public readonly record struct DownloadRingSlotLease(
    int SlotIndex,
    ulong TimelineTick,
    ulong ByteSize);

/// <summary>
/// (#207) 8-slot <c>download_buffer</c> timeline-deferred streaming ring and GPU page-fault
/// 64-bit word scanner (<see cref="BitOperations.TrailingZeroCount(ulong)"/>) that isolates
/// which vertex/index/uniform buffer or 4KB texture page triggered a <c>VK_ERROR_DEVICE_LOST</c>.
/// </summary>
public sealed class GpuFaultBufferComputeRing
{
    public const int DownloadRingSlotCount = 8;
    public const ulong FaultPageSizeBytes = 4096UL;

    private readonly ulong[] _slotBusyUntilTick = new ulong[DownloadRingSlotCount];
    private readonly ulong[] _slotSizes = new ulong[DownloadRingSlotCount];
    private readonly List<RegisteredGpuBufferRegion> _registeredBuffers = new();
    private readonly ulong _baseAddress;
    private int _nextSlotCursor;

    public GpuFaultBufferComputeRing(ulong baseVirtualAddress = 0x1000_0000UL)
    {
        _baseAddress = baseVirtualAddress;
    }

    public ulong BaseVirtualAddress => _baseAddress;
    public int RegisteredBufferCount => _registeredBuffers.Count;

    public void RegisterBufferRegion(ulong bufferId, string debugLabel, ulong baseVirtualAddress, ulong sizeBytes)
    {
        if (sizeBytes == 0UL)
        {
            return;
        }

        _registeredBuffers.Add(new RegisteredGpuBufferRegion(
            bufferId,
            debugLabel ?? "UnnamedBuffer",
            baseVirtualAddress,
            sizeBytes));
    }

    public bool TryAcquireDownloadRingSlot(
        ulong completedGpuTimelineTick,
        ulong submissionTimelineTick,
        ulong byteSize,
        out DownloadRingSlotLease lease)
    {
        for (int attempt = 0; attempt < DownloadRingSlotCount; attempt++)
        {
            int slotIdx = (_nextSlotCursor + attempt) % DownloadRingSlotCount;
            if (_slotBusyUntilTick[slotIdx] <= completedGpuTimelineTick)
            {
                _slotBusyUntilTick[slotIdx] = submissionTimelineTick;
                _slotSizes[slotIdx] = byteSize;
                _nextSlotCursor = (slotIdx + 1) % DownloadRingSlotCount;
                lease = new DownloadRingSlotLease(slotIdx, submissionTimelineTick, byteSize);
                return true;
            }
        }

        lease = default;
        return false;
    }

    /// <summary>
    /// (#207) Scans 64-bit GPU fault bitmask words using <see cref="BitOperations.TrailingZeroCount(ulong)"/>
    /// and correlates each faulting 4KB page to registered GPU buffers.
    /// </summary>
    public List<GpuPageFaultIsolationRecord> ScanFaultBitmaskWords(ReadOnlySpan<ulong> faultBitmaskWords)
    {
        List<GpuPageFaultIsolationRecord> faults = new List<GpuPageFaultIsolationRecord>();

        for (int wordIdx = 0; wordIdx < faultBitmaskWords.Length; wordIdx++)
        {
            ulong word = faultBitmaskWords[wordIdx];
            while (word != 0UL)
            {
                int bit = BitOperations.TrailingZeroCount(word);
                int pageIndex = (wordIdx * 64) + bit;
                ulong faultVa = _baseAddress + ((ulong)pageIndex * FaultPageSizeBytes);

                ulong matchedId = 0UL;
                string matchedLabel = "UnmappedGpuPage";
                ulong offsetInBuffer = 0UL;

                for (int i = 0; i < _registeredBuffers.Count; i++)
                {
                    RegisteredGpuBufferRegion reg = _registeredBuffers[i];
                    if (faultVa >= reg.BaseVirtualAddress && faultVa < reg.BaseVirtualAddress + reg.SizeBytes)
                    {
                        matchedId = reg.BufferId;
                        matchedLabel = reg.DebugLabel;
                        offsetInBuffer = faultVa - reg.BaseVirtualAddress;
                        break;
                    }
                }

                faults.Add(new GpuPageFaultIsolationRecord(
                    pageIndex,
                    faultVa,
                    matchedId,
                    matchedLabel,
                    offsetInBuffer));

                word &= word - 1UL;
            }
        }

        return faults;
    }
}

