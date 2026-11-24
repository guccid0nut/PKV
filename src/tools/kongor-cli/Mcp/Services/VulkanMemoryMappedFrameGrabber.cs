// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

// ============================================================================
// File: VulkanMemoryMappedFrameGrabber.cs
// System: ProjectKongor Developer CLI & TAO MCP Server
// Component: Vulkan Swapchain Direct Offscreen Frame Grabber
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.Graphics;

namespace Kongor.Cli.Mcp.Services;

/// <summary>
/// Result of an individual offscreen Vulkan swapchain frame grab.
/// </summary>
public sealed record VulkanFrameResult
{
    public bool Success { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public uint Format { get; init; }
    public ulong FrameIndex { get; init; }
    public ulong TimestampNs { get; init; }
    public byte[] PixelData { get; init; } = [];
    public int BufferSlotIndex { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Interface for acquiring raw Vulkan swapchain presentation frames directly from shared memory.
/// </summary>
public interface IVulkanFrameGrabber : IAsyncDisposable, IDisposable
{
    bool IsSharedMemoryAvailable(int processId);

    Task<VulkanFrameResult?> GrabSingleFrameAsync(
        int processId,
        int timeoutMs = 1500,
        CancellationToken cancellationToken = default);

    Task<VulkanFrameResult?> GrabSingleFrameFromMapAsync(
        string mapName,
        string readyEventPrefix,
        string doneEventPrefix,
        int slotCount = 3,
        int timeoutMs = 1500,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// High-performance Win32 Shared Memory receiver for Vulkan swapchain frame capture.
/// Reads uncompressed BGRA8/RGBA8 video frames directly from MemoryMappedFile with
/// Win32 EventWaitHandle synchronization (0ms dwell time, 0 desktop window exposure).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class VulkanMemoryMappedFrameGrabber : IVulkanFrameGrabber
{
    private readonly ILogger<VulkanMemoryMappedFrameGrabber> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<int, KeepAliveMutexManager> _keepAliveManagers = new();
    private bool _disposed;

    public VulkanMemoryMappedFrameGrabber(
        ILogger<VulkanMemoryMappedFrameGrabber>? logger = null,
        TimeProvider? timeProvider = null)
    {
        _logger = logger ?? NullLogger<VulkanMemoryMappedFrameGrabber>.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool IsDisposed => _disposed;

    /// <summary>
    /// #212 (Gem 3K): Ensures the host keep-alive mutex (<c>Local\KongorCaptureKeepAlive_&lt;PID&gt;</c>)
    /// is held for the specified game process ID so the game layer's 5s watchdog keeps GPU staging armed.
    /// </summary>
    public KeepAliveMutexManager? EnsureKeepAliveForProcess(int processId)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || processId <= 0 || _disposed)
        {
            return null;
        }

        return _keepAliveManagers.GetOrAdd(processId, static pid => new KeepAliveMutexManager(pid));
    }

    /// <summary>
    /// Checks whether the Vulkan shared memory file mapping exists for the specified process.
    /// </summary>
    public bool IsSharedMemoryAvailable(int processId)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || processId <= 0)
        {
            return false;
        }

        string mapName = $"Local\\HonReplayFrameBuffer_{processId}";
        try
        {
            using MemoryMappedFile mmf = MemoryMappedFile.OpenExisting(mapName, MemoryMappedFileRights.Read);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[VulkanFrameGrabber] Probing shared memory '{MapName}' encountered: {Message}", mapName, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Grabs a single frame from the standard process-specific shared memory ring buffer.
    /// </summary>
    public Task<VulkanFrameResult?> GrabSingleFrameAsync(
        int processId,
        int timeoutMs = 5000,
        CancellationToken cancellationToken = default)
    {
        if (processId <= 0)
        {
            return Task.FromResult<VulkanFrameResult?>(null);
        }

        _ = EnsureKeepAliveForProcess(processId);

        string mapName = $"Local\\HonReplayFrameBuffer_{processId}";
        string readyPrefix = $"Local\\HonFrameReady_{processId}";
        string donePrefix = $"Local\\HonFrameDone_{processId}";

        return GrabSingleFrameFromMapAsync(mapName, readyPrefix, donePrefix, slotCount: 3, timeoutMs, cancellationToken);
    }

    /// <summary>
    /// Grabs a single frame from the specified shared memory mapping and event prefixes.
    /// </summary>
    public async Task<VulkanFrameResult?> GrabSingleFrameFromMapAsync(
        string mapName,
        string readyEventPrefix,
        string doneEventPrefix,
        int slotCount = 3,
        int timeoutMs = 5000,
        CancellationToken cancellationToken = default)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            _logger.LogWarning("[VulkanFrameGrabber] Vulkan shared memory frame grabbing is only supported on Windows.");
            return null;
        }

        MemoryMappedFile? mmf = null;
        MemoryMappedViewAccessor? accessor = null;
        EventWaitHandle[] readyEvents = new EventWaitHandle[slotCount];
        EventWaitHandle[] doneEvents = new EventWaitHandle[slotCount];
        int openedReadyCount = 0;
        int openedDoneCount = 0;

        try
        {
            // 1. Open the existing MemoryMappedFile
            try
            {
                mmf = MemoryMappedFile.OpenExisting(mapName, MemoryMappedFileRights.ReadWrite);
                accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
            }
            catch (FileNotFoundException)
            {
                _logger.LogDebug("[VulkanFrameGrabber] Shared memory map '{MapName}' does not exist.", mapName);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[VulkanFrameGrabber] Failed to open shared memory map '{MapName}': {Message}", mapName, ex.Message);
                return null;
            }

            // 2. Open EventWaitHandles for all slots
            for (int i = 0; i < slotCount; i++)
            {
                string readyName = $"{readyEventPrefix}_{i}";
                string doneName = $"{doneEventPrefix}_{i}";

                try
                {
                    readyEvents[i] = EventWaitHandle.OpenExisting(readyName);
                    openedReadyCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[VulkanFrameGrabber] Could not open ready event '{EventName}': {Msg}", readyName, ex.Message);
                }

                try
                {
                    doneEvents[i] = EventWaitHandle.OpenExisting(doneName);
                    openedDoneCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[VulkanFrameGrabber] Could not open done event '{EventName}': {Msg}", doneName, ex.Message);
                }
            }

            if (openedReadyCount == 0)
            {
                _logger.LogDebug("[VulkanFrameGrabber] No synchronization ready events could be opened for prefix '{Prefix}'.", readyEventPrefix);
                return null;
            }

            // 3. Assemble active wait handles
            List<WaitHandle> activeHandlesList = new();
            List<int> slotMapping = new();
            for (int i = 0; i < slotCount; i++)
            {
                if (readyEvents[i] != null)
                {
                    activeHandlesList.Add(readyEvents[i]);
                    slotMapping.Add(i);
                }
            }

            WaitHandle[] activeHandles = activeHandlesList.ToArray();

            // 4. Asynchronously wait for any slot to be signaled by Vulkan layer
            int signaledListIndex = await Task.Run(() =>
            {
                return WaitHandle.WaitAny(activeHandles, Math.Clamp(timeoutMs, 10, 10000));
            }, cancellationToken).ConfigureAwait(false);

            int headerSize = Marshal.SizeOf<FrameHeader>(); // 64 bytes

            // Probe slot 0 header to determine slot stride if needed (#210 Gem 3A: 32-byte AVX alignment support)
            FrameHeader probeHeader;
            accessor.Read(0, out probeHeader);
            long slotStride;
            if (probeHeader.IsValid())
            {
                long rawStride = headerSize + (long)probeHeader.DataSizeBytes;
                long avxStride = Align32(rawStride);
                long legacyStride = (rawStride + 63L) & ~63L;
                slotStride = avxStride;
                if (slotCount > 1 && legacyStride != avxStride && legacyStride + headerSize <= accessor.Capacity)
                {
                    accessor.Read(legacyStride, out FrameHeader legacySlot1Hdr);
                    if (legacySlot1Hdr.IsValid())
                    {
                        slotStride = legacyStride;
                    }
                }
            }
            else
            {
                // Try estimating from MMF capacity or standard resolution presets
                long capacityBasedStride = Align32(accessor.Capacity / Math.Max(1, slotCount));
                long headlessStride = Align32(headerSize + (1280L * 720L * 4L));
                long defaultStride = Align32(headerSize + (1920L * 1080L * 4L));

                slotStride = defaultStride;
                long[] candidateStrides = [capacityBasedStride, headlessStride, defaultStride];
                bool resolved = false;

                foreach (long cand in candidateStrides)
                {
                    if (cand <= headerSize || cand >= accessor.Capacity) continue;
                    for (int s = 1; s < slotCount; s++)
                    {
                        long testOffset = s * cand;
                        if (testOffset + headerSize <= accessor.Capacity)
                        {
                            accessor.Read(testOffset, out FrameHeader testHdr);
                            if (testHdr.IsValid())
                            {
                                slotStride = Align32(headerSize + (long)testHdr.DataSizeBytes);
                                resolved = true;
                                break;
                            }
                        }
                    }
                    if (resolved) break;
                }
            }

            if (signaledListIndex == WaitHandle.WaitTimeout || signaledListIndex < 0 || signaledListIndex >= slotMapping.Count)
            {
                _logger.LogInformation("[VulkanFrameGrabber] Timed out waiting for Vulkan swapchain frame signal ({Timeout}ms). Checking for cached presented frames in ring buffer...", timeoutMs);

                int bestSlot = -1;
                ulong highestFrameIndex = 0;
                FrameHeader bestHeader = default;

                for (int s = 0; s < slotCount; s++)
                {
                    long offset = s * slotStride;
                    if (offset + headerSize <= accessor.Capacity)
                    {
                        accessor.Read(offset, out FrameHeader candidate);
                        if (candidate.IsValid())
                        {
                            if (bestSlot == -1 || candidate.FrameIndex >= highestFrameIndex)
                            {
                                bestSlot = s;
                                highestFrameIndex = candidate.FrameIndex;
                                bestHeader = candidate;
                            }
                        }
                    }
                }

                if (bestSlot != -1)
                {
                    _logger.LogInformation(
                        "[VulkanFrameGrabber] Successfully recovered latest valid cached frame #{Frame} from slot {Slot} ({W}x{H}, Format={Format}, {Size:N0} bytes) after presentation wait timed out.",
                        bestHeader.FrameIndex, bestSlot, bestHeader.Width, bestHeader.Height, bestHeader.Format, bestHeader.DataSizeBytes);

                    int cachedPixelSize = (int)bestHeader.DataSizeBytes;
                    byte[] cachedPixels = new byte[cachedPixelSize];
                    long bestOffset = bestSlot * slotStride;
                    accessor.ReadArray(bestOffset + headerSize, cachedPixels, 0, cachedPixelSize);

                    return new VulkanFrameResult
                    {
                        Success = true,
                        Width = (int)bestHeader.Width,
                        Height = (int)bestHeader.Height,
                        Format = bestHeader.Format,
                        FrameIndex = bestHeader.FrameIndex,
                        TimestampNs = bestHeader.TimestampNs,
                        PixelData = cachedPixels,
                        BufferSlotIndex = bestSlot
                    };
                }

                _logger.LogInformation("[VulkanFrameGrabber] No valid cached frames found in shared memory buffer '{MapName}'.", mapName);
                return null;
            }

            int slotIndex = slotMapping[signaledListIndex];
            _logger.LogDebug("[VulkanFrameGrabber] Slot {Slot} signaled by Vulkan layer.", slotIndex);

            // 5. Read FrameHeader from slot
            long targetOffset = slotIndex * slotStride;
            FrameHeader header;
            accessor.Read(targetOffset, out header);

            if (!header.IsValid())
            {
                _logger.LogWarning("[VulkanFrameGrabber] Slot {Slot} contained invalid frame header: {Header}", slotIndex, header);
                // Acknowledge done event to prevent layer deadlock
                doneEvents[slotIndex]?.Set();
                return null;
            }

            int pixelDataSize = (int)header.DataSizeBytes;
            byte[] pixelBytes = new byte[pixelDataSize];
            accessor.ReadArray(targetOffset + headerSize, pixelBytes, 0, pixelDataSize);

            // 6. Acknowledge frame slot immediately to release Vulkan staging buffer
            doneEvents[slotIndex]?.Set();
            _logger.LogDebug("[VulkanFrameGrabber] Acknowledged slot {Slot} done event.", slotIndex);

            _logger.LogInformation(
                "[VulkanFrameGrabber] Captured Vulkan offscreen frame #{Frame}: {W}x{H}, Format={Format}, Size={Size:N0} bytes (Slot {Slot}).",
                header.FrameIndex, header.Width, header.Height, header.Format, pixelDataSize, slotIndex);

            return new VulkanFrameResult
            {
                Success = true,
                Width = (int)header.Width,
                Height = (int)header.Height,
                Format = header.Format,
                FrameIndex = header.FrameIndex,
                TimestampNs = header.TimestampNs,
                PixelData = pixelBytes,
                BufferSlotIndex = slotIndex
            };
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[VulkanFrameGrabber] Frame grab canceled.");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[VulkanFrameGrabber] Exception during Vulkan shared memory frame grab: {Message}", ex.Message);
            return new VulkanFrameResult
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
        finally
        {
            for (int i = 0; i < slotCount; i++)
            {
                readyEvents[i]?.Dispose();
                doneEvents[i]?.Dispose();
            }

            accessor?.Dispose();
            mmf?.Dispose();
        }
    }

    // ========================================================================
    // #210 (Gem 3A): 32-Byte AVX-Aligned 3-Stage-to-2-Slot Shmem Ping-Pong
    // Inspired by obs-studio/plugins/win-capture/graphics-hook/graphics-hook.c:553-789
    // ========================================================================
    public const int NumStageBuffers = 3;
    public const int NumShmemTextures = 2;

    public static long Align32(long size) => (size + 31L) & ~31L;
    public static int Align32(int size) => (size + 31) & ~31;

    private long _swapchainResizePreReleaseCount;
    public long SwapchainResizePreReleaseCount => Interlocked.Read(ref _swapchainResizePreReleaseCount);

    /// <summary>
    /// #214 (Gem 3I): Pre-resize / pre-recreation destruction notifier that releases all captured
    /// backbuffer and shared-memory view references BEFORE swapchain resize/recreation executes.
    /// </summary>
    public long NotifySwapchainResizePreRelease(int processId, Action? releaseSharedMemoryViewsCallback = null)
    {
        long count = Interlocked.Increment(ref _swapchainResizePreReleaseCount);
        _logger.LogInformation(
            "[VulkanFrameGrabber] Swapchain resize pre-release invoked for PID={Pid} (PreReleaseCount={Count}). Releasing shared-memory views prior to resize.",
            processId,
            count);
        releaseSharedMemoryViewsCallback?.Invoke();
        return count;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (KeyValuePair<int, KeepAliveMutexManager> kv in _keepAliveManagers)
        {
            kv.Value.Dispose();
        }
        _keepAliveManagers.Clear();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// #210 (Gem 3A): OBS 3-stage GPU readback ring (NUM_STAGE_BUFFERS = 3) feeding a 2-slot
/// 32-byte AVX-aligned shared-memory ping-pong (shmem_textures[2] with last_tex atomic/seqlock index)
/// via a background copy thread so the game's render/present thread never stalls and tearing is impossible.
/// </summary>
public sealed class ObsAvx3StageTo2SlotPingPongBuffer : IDisposable
{
    public const int NumStageBuffers = 3;
    public const int NumShmemTextures = 2;

    public static long Align32(long size) => (size + 31L) & ~31L;
    public static int Align32(int size) => (size + 31) & ~31;

    private readonly byte[][] _stageBuffers = new byte[NumStageBuffers][];
    private readonly ulong[] _stageFrameIndices = new ulong[NumStageBuffers];
    private readonly byte[][] _shmemTextures = new byte[NumShmemTextures][];
    private readonly ulong[] _shmemFrameIndices = new ulong[NumShmemTextures];
    private readonly long[] _shmemSeqLocks = new long[NumShmemTextures];
    private readonly object[] _shmemSlotLocks = [new object(), new object()];
    private readonly object _stageLock = new();
    private readonly AutoResetEvent _copyEvent = new(false);
    private readonly ManualResetEvent _stopEvent = new(false);
    private readonly ManualResetEvent _workerExitedEvent = new(true);
    private readonly int _payloadSizeBytes;
    private readonly int _alignedStrideBytes;
    private int _curTex;
    private int _lastStageSubmitted = -1;
    private int _lastTex = -1;
    private bool _disposed;

    public ObsAvx3StageTo2SlotPingPongBuffer(int payloadSizeBytes, bool startBackgroundCopyThread = true)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(payloadSizeBytes);
        _payloadSizeBytes = payloadSizeBytes;
        _alignedStrideBytes = Align32(payloadSizeBytes);

        for (int i = 0; i < NumStageBuffers; i++)
        {
            _stageBuffers[i] = new byte[_alignedStrideBytes];
        }

        for (int i = 0; i < NumShmemTextures; i++)
        {
            _shmemTextures[i] = new byte[_alignedStrideBytes];
        }

        if (startBackgroundCopyThread)
        {
            _workerExitedEvent.Reset();
            Thread worker = new(CopyThreadMain)
            {
                IsBackground = true,
                Name = "KongorObsShmemCopyThread"
            };
            worker.Start();
        }
    }

    public int PayloadSizeBytes => _payloadSizeBytes;
    public int AlignedStrideBytes => _alignedStrideBytes;
    public int LastPublishedShmemSlot => Volatile.Read(ref _lastTex);

    public bool SubmitStageBuffer(int stageIndex, ReadOnlySpan<byte> pixels, ulong frameIndex, bool pumpSynchronously = false)
    {
        if (_disposed || stageIndex < 0 || stageIndex >= NumStageBuffers || pixels.Length < _payloadSizeBytes)
        {
            return false;
        }

        lock (_stageLock)
        {
            pixels.Slice(0, _payloadSizeBytes).CopyTo(_stageBuffers[stageIndex].AsSpan(0, _payloadSizeBytes));
            _stageFrameIndices[stageIndex] = frameIndex;
            Volatile.Write(ref _lastStageSubmitted, stageIndex);
        }

        if (pumpSynchronously)
        {
            PumpSingleCopyIteration();
            return true;
        }

        _copyEvent.Set();
        return true;
    }

    public void PumpSingleCopyIteration()
    {
        int stageIdx;
        ulong frameIndex;
        lock (_stageLock)
        {
            stageIdx = Volatile.Read(ref _lastStageSubmitted);
            if (stageIdx < 0 || stageIdx >= NumStageBuffers)
            {
                return;
            }
            frameIndex = _stageFrameIndices[stageIdx];
        }

        int nextSlot = _curTex;
        int prevSlot = Volatile.Read(ref _lastTex);
        int targetSlot = nextSlot;

        // Try non-contended slot first; fall back to alternate slot if locked by reader
        bool entered = Monitor.TryEnter(_shmemSlotLocks[targetSlot]);
        if (!entered)
        {
            int altSlot = (targetSlot == 0) ? 1 : 0;
            if (altSlot != prevSlot && Monitor.TryEnter(_shmemSlotLocks[altSlot]))
            {
                targetSlot = altSlot;
                entered = true;
            }
            else
            {
                Monitor.Enter(_shmemSlotLocks[targetSlot]);
                entered = true;
            }
        }

        try
        {
            Interlocked.Increment(ref _shmemSeqLocks[targetSlot]); // Odd = write in progress
            lock (_stageLock)
            {
                Buffer.BlockCopy(_stageBuffers[stageIdx], 0, _shmemTextures[targetSlot], 0, _payloadSizeBytes);
            }
            _shmemFrameIndices[targetSlot] = frameIndex;
            Interlocked.Increment(ref _shmemSeqLocks[targetSlot]); // Even = write complete
            Volatile.Write(ref _lastTex, targetSlot);
            _curTex = (targetSlot + 1) % NumShmemTextures;
        }
        finally
        {
            if (entered)
            {
                Monitor.Exit(_shmemSlotLocks[targetSlot]);
            }
        }
    }

    public bool TryReadLatestSlot(Span<byte> destination, out int slotIndex, out ulong frameIndex)
    {
        slotIndex = Volatile.Read(ref _lastTex);
        frameIndex = 0;
        if (slotIndex < 0 || slotIndex >= NumShmemTextures || destination.Length < _payloadSizeBytes)
        {
            return false;
        }

        lock (_shmemSlotLocks[slotIndex])
        {
            long seqBefore = Interlocked.Read(ref _shmemSeqLocks[slotIndex]);
            if ((seqBefore & 1L) != 0L)
            {
                return false;
            }

            _shmemTextures[slotIndex].AsSpan(0, _payloadSizeBytes).CopyTo(destination.Slice(0, _payloadSizeBytes));
            frameIndex = _shmemFrameIndices[slotIndex];

            long seqAfter = Interlocked.Read(ref _shmemSeqLocks[slotIndex]);
            return seqBefore == seqAfter;
        }
    }

    private void CopyThreadMain()
    {
        WaitHandle[] handles = [_stopEvent, _copyEvent];
        try
        {
            while (true)
            {
                int signaled = WaitHandle.WaitAny(handles);
                if (signaled == 0)
                {
                    break;
                }
                PumpSingleCopyIteration();
            }
        }
        finally
        {
            _workerExitedEvent.Set();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stopEvent.Set();
        _workerExitedEvent.WaitOne(2000);
        _copyEvent.Dispose();
        _stopEvent.Dispose();
        _workerExitedEvent.Dispose();
    }
}

/// <summary>
/// #212 (Gem 3K): OBS Keep-Alive Mutex Watchdog for Orphaned Shared-Memory Sessions.
/// Host creates/holds Local\KongorCaptureKeepAlive_&lt;PID&gt;; game layer probes OpenMutexW every 5s
/// and automatically disarms GPU staging when the host mutex disappears.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class KeepAliveMutexManager : IDisposable
{
    private readonly int _processId;
    private readonly string _mutexName;
    private Mutex? _keepAliveMutex;
    private bool _disposed;

    public KeepAliveMutexManager(int processId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        _processId = processId;
        _mutexName = BuildKeepAliveMutexName(processId);
        _keepAliveMutex = new Mutex(initiallyOwned: false, name: _mutexName, out bool _);
    }

    public int ProcessId => _processId;
    public string MutexName => _mutexName;
    public bool IsActive => !_disposed && _keepAliveMutex != null;

    public static string BuildKeepAliveMutexName(int processId) => $"Local\\KongorCaptureKeepAlive_{processId}";

    public static bool ProbeHostKeepAliveMutex(int processId)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || processId <= 0)
        {
            return false;
        }

        string name = BuildKeepAliveMutexName(processId);
        if (Mutex.TryOpenExisting(name, out Mutex? existingMutex))
        {
            existingMutex.Dispose();
            return true;
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _keepAliveMutex?.Dispose();
        _keepAliveMutex = null;
    }
}

/// <summary>
/// #213 (Gem 3M): OBS frame_ready(uint64_t interval_ns) Nanosecond Frame-Pacing Drop-Frame Throttle.
/// Inspired by obs-studio/plugins/win-capture/graphics-hook/graphics-hook.c:516-539.
/// </summary>
public sealed class ObsNanosecondFramePacer
{
    private ulong _intervalNs;
    private ulong _lastFrameTimeNs;
    private ulong _acceptedCount;
    private ulong _droppedCount;

    public ObsNanosecondFramePacer(ulong intervalNs = 0)
    {
        _intervalNs = intervalNs;
    }

    public ulong IntervalNs
    {
        get => _intervalNs;
        set
        {
            _intervalNs = value;
            _lastFrameTimeNs = 0;
        }
    }

    public ulong LastFrameTimeNs => _lastFrameTimeNs;
    public ulong AcceptedCount => _acceptedCount;
    public ulong DroppedCount => _droppedCount;

    public void Reset()
    {
        _lastFrameTimeNs = 0;
        _acceptedCount = 0;
        _droppedCount = 0;
    }

    public bool FrameReady()
    {
        ulong currentNs = (ulong)((Stopwatch.GetTimestamp() * 1_000_000_000.0) / Stopwatch.Frequency);
        return FrameReady(currentNs);
    }

    public bool FrameReady(ulong currentTimestampNs)
    {
        if (_intervalNs == 0)
        {
            _acceptedCount++;
            return true;
        }

        if (_lastFrameTimeNs == 0)
        {
            _lastFrameTimeNs = currentTimestampNs;
            _acceptedCount++;
            return true;
        }

        if (currentTimestampNs < _lastFrameTimeNs)
        {
            _droppedCount++;
            return false;
        }

        ulong elapsed = currentTimestampNs - _lastFrameTimeNs;
        if (elapsed < _intervalNs)
        {
            _droppedCount++;
            return false;
        }

        // OBS drift clamp: (elapsed >= interval_ns * 2) ? t : last_time + interval_ns
        _lastFrameTimeNs = (elapsed >= _intervalNs * 2UL)
            ? currentTimestampNs
            : (_lastFrameTimeNs + _intervalNs);
        _acceptedCount++;
        return true;
    }
}

/// <summary>
/// Wave 4 (#232): scrcpy-inspired 2-point linear clock estimator (`app/src/clock.c`, `sc_clock`)
/// with 32-tick progressive startup weight (`1 / range`) and 90%/10% exponential smoothing of `offset` and `slope`.
/// </summary>
public sealed class ScrcpyProgressiveClock
{
    public const int ProgressiveRangeMax = 32;

    private double _leftSystemNs;
    private double _leftStreamPtsNs;
    private double _rightSystemNs;
    private double _rightStreamPtsNs;
    private int _range;
    private double _slope = 1.0;
    private double _offsetNs;

    public int Range => _range;
    public double Slope => _slope;
    public double OffsetNs => _offsetNs;

    public void Reset()
    {
        _leftSystemNs = 0.0;
        _leftStreamPtsNs = 0.0;
        _rightSystemNs = 0.0;
        _rightStreamPtsNs = 0.0;
        _range = 0;
        _slope = 1.0;
        _offsetNs = 0.0;
    }

    public void Update(long systemTimeNs, long streamPtsNs)
    {
        if (_range == 0)
        {
            _leftSystemNs = systemTimeNs;
            _leftStreamPtsNs = streamPtsNs;
            _rightSystemNs = systemTimeNs;
            _rightStreamPtsNs = streamPtsNs;
            _range = 1;
            _slope = 1.0;
            _offsetNs = systemTimeNs - streamPtsNs;
            return;
        }

        if (_range < ProgressiveRangeMax)
        {
            _range++;
            double weight = 1.0 / _range;
            double keep = 1.0 - weight;

            _leftSystemNs = (keep * _leftSystemNs) + (weight * _rightSystemNs);
            _leftStreamPtsNs = (keep * _leftStreamPtsNs) + (weight * _rightStreamPtsNs);

            _rightSystemNs = (keep * _rightSystemNs) + (weight * systemTimeNs);
            _rightStreamPtsNs = (keep * _rightStreamPtsNs) + (weight * streamPtsNs);
        }
        else
        {
            // scrcpy 90% / 10% exponential smoothing
            _leftSystemNs = (0.9 * _leftSystemNs) + (0.1 * _rightSystemNs);
            _leftStreamPtsNs = (0.9 * _leftStreamPtsNs) + (0.1 * _rightStreamPtsNs);

            _rightSystemNs = (0.9 * _rightSystemNs) + (0.1 * systemTimeNs);
            _rightStreamPtsNs = (0.9 * _rightStreamPtsNs) + (0.1 * streamPtsNs);
        }

        double deltaPts = _rightStreamPtsNs - _leftStreamPtsNs;
        if (Math.Abs(deltaPts) > 1.0)
        {
            double rawSlope = (_rightSystemNs - _leftSystemNs) / deltaPts;
            _slope = Math.Clamp(rawSlope, 0.8, 1.2);
        }
        else
        {
            _slope = 1.0;
        }

        _offsetNs = _rightSystemNs - (_rightStreamPtsNs * _slope);
    }

    public long ToSystemTimeNs(long streamPtsNs)
    {
        if (_range == 0)
        {
            return streamPtsNs;
        }

        double estimated = ((streamPtsNs - _rightStreamPtsNs) * _slope) + _rightSystemNs;
        return (long)Math.Round(estimated);
    }
}

/// <summary>
/// Wave 4 (#232): scrcpy-inspired single-slot atomic overwrite frame buffer (`app/src/frame_buffer.c`, `sc_frame_buffer`).
/// Always retains only the freshest frame (`Push(frame, out bool previousSkipped)` / `Take()`) so slow consumers
/// skip stale frames immediately instead of building multi-second FIFO render lag.
/// </summary>
public sealed class ScrcpySingleSlotFrameBuffer<T>
{
    private readonly Lock _lock = new();
    private T? _pendingFrame;
    private bool _tmpFrameConsumed = true;

    public long PushedFramesCount { get; private set; }
    public long SkippedFramesCount { get; private set; }
    public long ConsumedFramesCount { get; private set; }

    public bool Push(T frame, out bool previousSkipped)
    {
        lock (_lock)
        {
            previousSkipped = !_tmpFrameConsumed;
            if (previousSkipped)
            {
                SkippedFramesCount++;
            }

            _pendingFrame = frame;
            _tmpFrameConsumed = false;
            PushedFramesCount++;
            return !previousSkipped;
        }
    }

    public bool TryTake(out T? frame)
    {
        lock (_lock)
        {
            if (_tmpFrameConsumed)
            {
                frame = default;
                return false;
            }

            frame = _pendingFrame;
            _pendingFrame = default;
            _tmpFrameConsumed = true;
            ConsumedFramesCount++;
            return true;
        }
    }

    public T Take()
    {
        if (!TryTake(out T? frame))
        {
            throw new InvalidOperationException("[ScrcpySingleSlotFrameBuffer] No unconsumed frame is available in the single-slot buffer.");
        }

        return frame!;
    }
}

/// <summary>
/// Wave 4 (#236): scrcpy-inspired interval-sliced rolling FPS counter (`app/src/fps_counter.c`, `sc_fps_counter`)
/// with `SC_FPS_COUNTER_INTERVAL = 1s`, `nr_slices_per_interval`, and O(1) circular slice expiration zeroing
/// without per-frame timestamp list allocations.
/// </summary>
public sealed class ScrcpyIntervalSlicedFpsCounter
{
    public const long DefaultIntervalNs = 1_000_000_000L;
    public const int DefaultSlicesPerInterval = 10;

    private struct SliceBucket
    {
        public int Rendered;
        public int Skipped;
    }

    private readonly SliceBucket[] _slices;
    private readonly long _intervalNs;
    private readonly long _sliceDurationNs;
    private int _currentSliceIndex;
    private long _nextSliceExpireNs;
    private int _windowRendered;
    private int _windowSkipped;

    public int SlicesPerInterval => _slices.Length;
    public long IntervalNs => _intervalNs;
    public int WindowRenderedFrames => _windowRendered;
    public int WindowSkippedFrames => _windowSkipped;

    public ScrcpyIntervalSlicedFpsCounter(
        long intervalNs = DefaultIntervalNs,
        int slicesPerInterval = DefaultSlicesPerInterval)
    {
        if (intervalNs <= 0L)
        {
            throw new ArgumentOutOfRangeException(nameof(intervalNs), intervalNs, "[ScrcpyIntervalSlicedFpsCounter] intervalNs must be positive.");
        }
        if (slicesPerInterval <= 0 || slicesPerInterval > intervalNs)
        {
            throw new ArgumentOutOfRangeException(nameof(slicesPerInterval), slicesPerInterval, "[ScrcpyIntervalSlicedFpsCounter] slicesPerInterval must be in [1, intervalNs].");
        }

        _intervalNs = intervalNs;
        _slices = new SliceBucket[slicesPerInterval];
        _sliceDurationNs = intervalNs / slicesPerInterval;
    }

    public void CheckIntervalExpired(long nowNs)
    {
        if (_nextSliceExpireNs == 0L)
        {
            _nextSliceExpireNs = nowNs + _sliceDurationNs;
            return;
        }

        if (nowNs < _nextSliceExpireNs)
        {
            return;
        }

        long elapsedSlices = ((nowNs - _nextSliceExpireNs) / _sliceDurationNs) + 1L;
        if (elapsedSlices >= _slices.Length)
        {
            Array.Clear(_slices, 0, _slices.Length);
            _windowRendered = 0;
            _windowSkipped = 0;
            _currentSliceIndex = 0;
            _nextSliceExpireNs = nowNs + _sliceDurationNs;
            return;
        }

        for (long i = 0; i < elapsedSlices; i++)
        {
            _currentSliceIndex = (_currentSliceIndex + 1) % _slices.Length;
            _windowRendered -= _slices[_currentSliceIndex].Rendered;
            _windowSkipped -= _slices[_currentSliceIndex].Skipped;
            _slices[_currentSliceIndex] = default;
            _nextSliceExpireNs += _sliceDurationNs;
        }
    }

    public void AddRenderedFrame(long nowNs)
    {
        CheckIntervalExpired(nowNs);
        _slices[_currentSliceIndex].Rendered++;
        _windowRendered++;
    }

    public void AddSkippedFrame(long nowNs)
    {
        CheckIntervalExpired(nowNs);
        _slices[_currentSliceIndex].Skipped++;
        _windowSkipped++;
    }

    public double GetFps(long nowNs)
    {
        CheckIntervalExpired(nowNs);
        return _windowRendered * (1_000_000_000.0 / _intervalNs);
    }
}

