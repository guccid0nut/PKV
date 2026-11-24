// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: NativeBufferRing.cs
// System: Replay Studio Desktop GUI Tool
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia.Platform;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ReplayStudio.GUI.Common;

/// <summary>
///     High-performance unmanaged double-buffered staging ring allocating pinned native memory
///     via <see cref="NativeMemory.Alloc(nuint)"/> for zero-GC frame blitting into Avalonia framebuffers.
/// </summary>
public sealed unsafe class NativeBufferRing : IDisposable
{
    private readonly ILogger<NativeBufferRing> _logger;
    private readonly Lock _resizeLock = new();

    private void* _buffer0;
    private void* _buffer1;
    private int _frontBufferIndex; // 0 or 1; BackBuffer is (1 - _frontBufferIndex)
    private int _width;
    private int _height;
    private int _stride;
    private int _bufferSizeBytes;
    private int _isDisposed;

    public int Width => _width;
    public int Height => _height;
    public int Stride => _stride;
    public int BufferSizeBytes => _bufferSizeBytes;
    public int FrontIndex => Volatile.Read(ref _frontBufferIndex);
    public int BackIndex => 1 - FrontIndex;
    public bool IsDisposed => Volatile.Read(ref _isDisposed) != 0;

    public IntPtr FrontBufferAddress
    {
        get
        {
            ThrowIfDisposed();
            return (IntPtr)(FrontIndex == 0 ? _buffer0 : _buffer1);
        }
    }

    public IntPtr BackBufferAddress
    {
        get
        {
            ThrowIfDisposed();
            return (IntPtr)(FrontIndex == 0 ? _buffer1 : _buffer0);
        }
    }

    public byte* FrontBufferPtr => (byte*)FrontBufferAddress;
    public byte* BackBufferPtr => (byte*)BackBufferAddress;

    public NativeBufferRing(int width, int height, ILogger<NativeBufferRing>? logger = null)
    {
        _logger = logger ?? NullLogger<NativeBufferRing>.Instance;

        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "Width must be strictly positive.");
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), height, "Height must be strictly positive.");
        }
        _width = width;
        _height = height;
        _stride = width * 4;
        _bufferSizeBytes = _stride * height;
        _frontBufferIndex = 0;

        nuint allocSize = (nuint)_bufferSizeBytes;
        _buffer0 = NativeMemory.Alloc(allocSize);
        _buffer1 = NativeMemory.Alloc(allocSize);
        NativeMemory.Clear(_buffer0, allocSize);
        NativeMemory.Clear(_buffer1, allocSize);

        double sizeMb = (double)_bufferSizeBytes * 2.0 / (1024.0 * 1024.0);
        _logger.LogInformation(
            "[NativeBufferRing] Allocated double staging buffers ({Width}x{Height}, Stride={Stride} bytes, {Bytes} bytes each, Total={TotalMB:F2} MB)",
            _width,
            _height,
            _stride,
            _bufferSizeBytes,
            sizeMb);
    }

    /// <summary>
    ///     Acquires the unmanaged pointer address of the current back buffer for rendering.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IntPtr AcquireBackBuffer()
    {
        ThrowIfDisposed();
        return BackBufferAddress;
    }

    /// <summary>
    ///     Atomically swaps the front and back buffer indices to present the newly rendered back buffer.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CommitBackBuffer()
    {
        ThrowIfDisposed();
        int currentFront = Volatile.Read(ref _frontBufferIndex);
        int nextFront = 1 - currentFront;
        while (Interlocked.CompareExchange(ref _frontBufferIndex, nextFront, currentFront) != currentFront)
        {
            currentFront = Volatile.Read(ref _frontBufferIndex);
            nextFront = 1 - currentFront;
        }
    }

    /// <summary>
    ///     Blits the unmanaged front buffer into the Avalonia locked framebuffer with zero managed allocations.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void BlitFrontBuffer(ILockedFramebuffer targetFramebuffer)
    {
        ArgumentNullException.ThrowIfNull(targetFramebuffer);
        BlitFrontBuffer(targetFramebuffer.Address, targetFramebuffer.RowBytes, targetFramebuffer.Size.Height);
    }

    /// <summary>
    ///     Vectorized unmanaged memory blit from front buffer directly to destination address.
    ///     Performs a single contiguous block copy when strides match, or row-by-row copy if destination has row padding.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void BlitFrontBuffer(IntPtr destinationAddress, int destinationRowBytes, int destinationHeight)
    {
        ThrowIfDisposed();
        if (destinationAddress == IntPtr.Zero)
        {
            throw new ArgumentException("Destination address cannot be null.", nameof(destinationAddress));
        }

        if (destinationRowBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(destinationRowBytes), destinationRowBytes, "Destination row bytes must be positive.");
        }

        void* srcPtr = (void*)FrontBufferAddress;
        void* dstPtr = (void*)destinationAddress;

        int srcStride = _stride;
        int copyHeight = Math.Min(_height, destinationHeight);

        if (destinationRowBytes == srcStride)
        {
            // Fast vectorized block copy: strides match perfectly
            ulong totalBytesToCopy = (ulong)(copyHeight * srcStride);
            Buffer.MemoryCopy(srcPtr, dstPtr, totalBytesToCopy, totalBytesToCopy);
        }
        else
        {
            // Row-by-row copy for padded strides (e.g. non-standard display scaling or hardware alignment)
            byte* srcBytePtr = (byte*)srcPtr;
            byte* dstBytePtr = (byte*)dstPtr;
            ulong rowCopyBytes = (ulong)Math.Min(srcStride, destinationRowBytes);
            ulong dstRowStride = (ulong)destinationRowBytes;

            for (int y = 0; y < copyHeight; y++)
            {
                Buffer.MemoryCopy(srcBytePtr + (y * srcStride), dstBytePtr + (y * destinationRowBytes), dstRowStride, rowCopyBytes);
            }
        }
    }

    /// <summary>
    ///     Fills the specified buffer (front or back) with a solid RGBA32 color.
    /// </summary>
    public void ClearBuffer(bool clearBackBuffer, byte r, byte g, byte b, byte a = 255)
    {
        ThrowIfDisposed();
        byte* targetPtr = clearBackBuffer ? BackBufferPtr : FrontBufferPtr;
        uint pixel = (uint)(r | (g << 8) | (b << 16) | (a << 24));
        uint* pixelPtr = (uint*)targetPtr;
        int totalPixels = _width * _height;

        for (int i = 0; i < totalPixels; i++)
        {
            pixelPtr[i] = pixel;
        }
    }

    /// <summary>
    ///     Reallocates staging buffers if viewport dimensions have changed.
    /// </summary>
    public void Resize(int newWidth, int newHeight)
    {
        ThrowIfDisposed();
        if (newWidth <= 0 || newHeight <= 0)
        {
            return;
        }

        if (newWidth == _width && newHeight == _height)
        {
            return;
        }

        lock (_resizeLock)
        {
            if (newWidth == _width && newHeight == _height)
            {
                return;
            }

            int newStride = newWidth * 4;
            int newSizeBytes = newStride * newHeight;
            nuint allocSize = (nuint)newSizeBytes;

            void* newBuf0 = NativeMemory.Alloc(allocSize);
            void* newBuf1 = NativeMemory.Alloc(allocSize);
            NativeMemory.Clear(newBuf0, allocSize);
            NativeMemory.Clear(newBuf1, allocSize);

            void* oldBuf0 = _buffer0;
            void* oldBuf1 = _buffer1;

            _buffer0 = newBuf0;
            _buffer1 = newBuf1;
            _width = newWidth;
            _height = newHeight;
            _stride = newStride;
            _bufferSizeBytes = newSizeBytes;
            Volatile.Write(ref _frontBufferIndex, 0);

            if (oldBuf0 != null)
            {
                NativeMemory.Free(oldBuf0);
            }

            if (oldBuf1 != null)
            {
                NativeMemory.Free(oldBuf1);
            }

            double sizeMb = (double)_bufferSizeBytes * 2.0 / (1024.0 * 1024.0);
            _logger.LogInformation(
                "[NativeBufferRing] Resized staging buffers to {Width}x{Height} (Stride={Stride} bytes, Total={TotalMB:F2} MB)",
                _width,
                _height,
                _stride,
                sizeMb);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, typeof(NativeBufferRing));
    }

    private void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
        {
            return;
        }

        lock (_resizeLock)
        {
            if (_buffer0 != null)
            {
                NativeMemory.Free(_buffer0);
                _buffer0 = null;
            }

            if (_buffer1 != null)
            {
                NativeMemory.Free(_buffer1);
                _buffer1 = null;
            }
        }

        if (disposing)
        {
            _logger?.LogInformation("[NativeBufferRing] Disposed native staging buffers successfully");
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    ~NativeBufferRing()
    {
        Dispose(false);
    }
}
