// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: VulkanMemoryMappedReceiver.cs
// System: NEXUS Replay Video Renderer Service
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace ReplayRenderer.Service.Ipc;

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
    private long _totalBytesReceived;
    private long _totalFramesReceived;

    public VulkanMemoryMappedReceiver(
        ILogger<VulkanMemoryMappedReceiver> logger,
        int processId,
        uint width,
        uint height,
        int slotCount = 3,
        string? customMapName = null)
    {
        _logger = logger;
        ProcessId = processId;
        _width = width;
        _height = height;
        _slotCount = slotCount;

        int headerSize = Marshal.SizeOf<FrameHeader>(); // 64 bytes
        long pixelDataSize = (long)width * height * 4;
        _slotStride = (headerSize + pixelDataSize + 63L) & ~63L;
        _totalMapSize = (_slotStride * slotCount + 0xFFFFL) & ~0xFFFFL;

        MapName = customMapName ?? $"Local\\HonReplayFrameBuffer_{processId}";

        _logger.LogInformation(
            "[SharedMemoryReceiver] Configured shared memory mapping: {MapName} ({Slots} slots, {Stride:N0} bytes/slot, Total: {Total:N0} bytes).",
            MapName, slotCount, _slotStride, _totalMapSize);
    }

    public int ProcessId { get; }

    public string MapName { get; }

    public long TotalFramesReceived => Interlocked.Read(ref _totalFramesReceived);
    public long TotalBytesReceived => Interlocked.Read(ref _totalBytesReceived);

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
                    // Read raw BGRA pixel data
                    _accessor.ReadArray(slotOffset + headerSize, localPixelBuffer, 0, pixelDataSize);

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
