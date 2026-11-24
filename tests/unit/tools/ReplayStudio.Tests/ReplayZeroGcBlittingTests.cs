// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: M2ZeroGcChallengerTests.cs
// System: Replay Studio Desktop GUI Tool
// Policy: Strict Explicit Typing (NO 'var')
// Challenger: Challenger 1 (Empirical Adversarial Zero-GC Viewport & NativeBufferRing Stress Testing)
// ============================================================================

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Platform;
using FluentAssertions;
using ReplayStudio.GUI.Common;
using Xunit;

namespace ReplayStudio.Tests;

/// <summary>
///     Adversarial empirical stress-testing suite for <see cref="NativeBufferRing"/> and
///     zero-GC viewport blitting under high-concurrency, extreme resolutions, and unaligned strides.
/// </summary>
public sealed class ReplayZeroGcBlittingTests
{
    // ========================================================================
    // 1. 10,000 Iterations Zero-GC Heap Allocation Stress Tests
    // ========================================================================

    [Trait("Category", "Manual")]
    [Fact]
    public unsafe void BlitFrontBuffer_10000Iterations_ProducesAbsoluteZeroManagedAllocations()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange: 1080p Full HD resolution (1920x1080)
        int width = 1920;
        int height = 1080;
        int stride = width * 4;
        int totalBytes = stride * height;

        using NativeBufferRing ring = new(width, height);
        ring.ClearBuffer(clearBackBuffer: false, r: 0x55, g: 0xAA, b: 0x33, a: 0xFF);

        void* destMem = NativeMemory.Alloc((nuint)totalBytes);
        IntPtr destAddress = (IntPtr)destMem;

        try
        {
            // Warmup JIT compiler and tiering
            for (int w = 0; w < 100; w++)
            {
                ring.BlitFrontBuffer(destAddress, stride, height);
            }

            // Act: Capture allocation counter before 10,000 consecutive blit iterations
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

            for (int i = 0; i < 10000; i++)
            {
                ring.BlitFrontBuffer(destAddress, stride, height);
            }

            long allocatedAfter = GC.GetAllocatedBytesForCurrentThread();
            long totalAllocated = allocatedAfter - allocatedBefore;

            // Assert: Absolutely ZERO managed heap allocations across 10,000 frames
            totalAllocated.Should().Be(0, "10,000 viewport blits must produce EXACTLY 0 bytes of managed heap allocations!");
        }
        finally
        {
            NativeMemory.Free(destMem);
        }
    }

    [Trait("Category", "Manual")]
    [Fact]
    public unsafe void BlitFrontBuffer_10000Iterations_PaddedStride_ProducesAbsoluteZeroManagedAllocations()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange: 1080p with non-matching destination stride (e.g. 8192 bytes row pitch)
        int width = 1920;
        int height = 1080;
        int srcStride = width * 4; // 7680 bytes
        int dstStride = 8192; // 512 bytes padding per row
        int dstTotalBytes = dstStride * height;

        using NativeBufferRing ring = new(width, height);
        ring.ClearBuffer(clearBackBuffer: false, r: 0x12, g: 0x34, b: 0x56, a: 0x78);

        void* destMem = NativeMemory.Alloc((nuint)dstTotalBytes);
        IntPtr destAddress = (IntPtr)destMem;

        try
        {
            // Warmup JIT
            for (int w = 0; w < 100; w++)
            {
                ring.BlitFrontBuffer(destAddress, dstStride, height);
            }

            // Act: Measure 10,000 padded row-by-row blits
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

            for (int i = 0; i < 10000; i++)
            {
                ring.BlitFrontBuffer(destAddress, dstStride, height);
            }

            long allocatedAfter = GC.GetAllocatedBytesForCurrentThread();
            long totalAllocated = allocatedAfter - allocatedBefore;

            // Assert
            totalAllocated.Should().Be(0, "10,000 padded row-by-row blits must produce EXACTLY 0 bytes of managed heap allocations!");
        }
        finally
        {
            NativeMemory.Free(destMem);
        }
    }

    [Trait("Category", "Manual")]
    [Fact]
    public unsafe void BlitFrontBuffer_4K_1000Iterations_ProducesAbsoluteZeroManagedAllocations()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange: 4K UHD resolution (3840x2160) -> 33.17 MB per frame
        int width = 3840;
        int height = 2160;
        int stride = width * 4;
        int totalBytes = stride * height;

        using NativeBufferRing ring = new(width, height);
        ring.ClearBuffer(clearBackBuffer: false, r: 0xFF, g: 0x88, b: 0x00, a: 0xFF);

        void* destMem = NativeMemory.Alloc((nuint)totalBytes);
        IntPtr destAddress = (IntPtr)destMem;

        try
        {
            // Warmup
            for (int w = 0; w < 10; w++)
            {
                ring.BlitFrontBuffer(destAddress, stride, height);
            }

            // Act
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

            for (int i = 0; i < 1000; i++)
            {
                ring.BlitFrontBuffer(destAddress, stride, height);
            }

            long allocatedAfter = GC.GetAllocatedBytesForCurrentThread();
            long totalAllocated = allocatedAfter - allocatedBefore;

            // Assert
            totalAllocated.Should().Be(0, "1,000 4K UHD blits (33.17 GB transferred) must produce EXACTLY 0 bytes of managed heap allocations!");
        }
        finally
        {
            NativeMemory.Free(destMem);
        }
    }

    [Trait("Category", "Manual")]
    [Fact]
    public void ClearAndSwap_10000Iterations_ProducesAbsoluteZeroManagedAllocations()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange
        int width = 1280;
        int height = 720;
        using NativeBufferRing ring = new(width, height);

        // Warmup
        for (int w = 0; w < 100; w++)
        {
            ring.ClearBuffer(clearBackBuffer: true, 0x11, 0x22, 0x33, 0xFF);
            ring.CommitBackBuffer();
        }

        // Act
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < 10000; i++)
        {
            byte val = (byte)(i & 0xFF);
            ring.ClearBuffer(clearBackBuffer: true, val, val, val, 255);
            ring.CommitBackBuffer();
        }

        long allocatedAfter = GC.GetAllocatedBytesForCurrentThread();
        long totalAllocated = allocatedAfter - allocatedBefore;

        // Assert
        totalAllocated.Should().Be(0, "10,000 render clears and atomic CAS swaps must produce EXACTLY 0 bytes of managed heap allocations!");
    }

    // ========================================================================
    // 2. Extreme Resolutions & Boundary Dimensions
    // ========================================================================

    [Fact]
    public unsafe void ExtremeResolutions_1x1Minimal_AllocatesAndBlitsCorrectly()
    {
        // Arrange: Minimal valid dimensions 1x1
        int width = 1;
        int height = 1;
        int stride = 4;
        int totalBytes = 4;

        using NativeBufferRing ring = new(width, height);
        ring.Width.Should().Be(1);
        ring.Height.Should().Be(1);
        ring.Stride.Should().Be(4);
        ring.BufferSizeBytes.Should().Be(4);

        // Write test pixel into front buffer
        ring.ClearBuffer(clearBackBuffer: false, r: 0xFE, g: 0xDC, b: 0xBA, a: 0x98);

        void* destMem = NativeMemory.Alloc((nuint)totalBytes);
        NativeMemory.Clear(destMem, (nuint)totalBytes);
        IntPtr destAddress = (IntPtr)destMem;

        try
        {
            // Act
            ring.BlitFrontBuffer(destAddress, stride, height);

            // Assert
            byte* b = (byte*)destMem;
            b[0].Should().Be(0xFE);
            b[1].Should().Be(0xDC);
            b[2].Should().Be(0xBA);
            b[3].Should().Be(0x98);
        }
        finally
        {
            NativeMemory.Free(destMem);
        }
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    [InlineData(3, 7)]
    [InlineData(13, 17)]
    [InlineData(1366, 768)] // Non-standard laptop resolution
    [InlineData(1921, 1079)] // Prime/odd non-standard resolution
    public unsafe void ExtremeResolutions_NonStandardDimensions_AllocatesAndBlitsCorrectly(int width, int height)
    {
        // Arrange
        int stride = width * 4;
        int totalBytes = stride * height;

        using NativeBufferRing ring = new(width, height);
        ring.Width.Should().Be(width);
        ring.Height.Should().Be(height);
        ring.Stride.Should().Be(stride);
        ring.BufferSizeBytes.Should().Be(totalBytes);

        // Fill with pattern: pixel (x, y) = (x % 256, y % 256, (x+y) % 256, 255)
        uint* backPtr = (uint*)ring.BackBufferPtr;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte r = (byte)(x & 0xFF);
                byte g = (byte)(y & 0xFF);
                byte b = (byte)((x + y) & 0xFF);
                byte a = 255;
                backPtr[y * width + x] = (uint)(r | (g << 8) | (b << 16) | (a << 24));
            }
        }

        ring.CommitBackBuffer();

        void* destMem = NativeMemory.Alloc((nuint)totalBytes);
        NativeMemory.Clear(destMem, (nuint)totalBytes);
        IntPtr destAddress = (IntPtr)destMem;

        try
        {
            // Act
            ring.BlitFrontBuffer(destAddress, stride, height);

            // Assert: Spot check corners and center
            uint* destUint = (uint*)destMem;

            // Top-left (0, 0)
            byte* tl = (byte*)&destUint[0];
            tl[0].Should().Be(0);
            tl[1].Should().Be(0);
            tl[2].Should().Be(0);
            tl[3].Should().Be(255);

            // Bottom-right (width - 1, height - 1)
            int brIndex = (height - 1) * width + (width - 1);
            byte* br = (byte*)&destUint[brIndex];
            br[0].Should().Be((byte)((width - 1) & 0xFF));
            br[1].Should().Be((byte)((height - 1) & 0xFF));
            br[2].Should().Be((byte)((width - 1 + height - 1) & 0xFF));
            br[3].Should().Be(255);
        }
        finally
        {
            NativeMemory.Free(destMem);
        }
    }

    [Fact]
    public unsafe void ExtremeResolutions_4K_UHD_3840x2160_AllocatesAndBlitsCorrectly()
    {
        // Arrange: 3840x2160 (4K UHD) -> 33,177,600 bytes
        int width = 3840;
        int height = 2160;
        int stride = width * 4;
        int totalBytes = stride * height;

        using NativeBufferRing ring = new(width, height);
        ring.BufferSizeBytes.Should().Be(33_177_600);

        ring.ClearBuffer(clearBackBuffer: false, r: 0x4B, g: 0x88, b: 0xCC, a: 0xFF);

        void* destMem = NativeMemory.Alloc((nuint)totalBytes);
        NativeMemory.Clear(destMem, (nuint)totalBytes);
        IntPtr destAddress = (IntPtr)destMem;

        try
        {
            // Act
            ring.BlitFrontBuffer(destAddress, stride, height);

            // Assert: Sample across every 100,000th pixel to verify full memory integrity
            uint* destUint = (uint*)destMem;
            int totalPixels = width * height;
            uint expectedPixel = 0xFF000000u | (0xCCu << 16) | (0x88u << 8) | 0x4Bu;

            for (int p = 0; p < totalPixels; p += 100_000)
            {
                destUint[p].Should().Be(expectedPixel, $"Pixel at index {p} in 4K buffer must match expected pattern");
            }
        }
        finally
        {
            NativeMemory.Free(destMem);
        }
    }

    [Fact]
    public unsafe void ExtremeResolutions_8K_FUHD_7680x4320_AllocatesAndBlitsCorrectly()
    {
        // Arrange: 7680x4320 (8K FUHD) -> 132,710,400 bytes (126.56 MB per buffer)
        int width = 7680;
        int height = 4320;
        int stride = width * 4;
        int totalBytes = stride * height;

        using NativeBufferRing ring = new(width, height);
        ring.BufferSizeBytes.Should().Be(132_710_400);

        // Fill back buffer with test color
        ring.ClearBuffer(clearBackBuffer: true, r: 0x8A, g: 0x2B, b: 0xE2, a: 0xFF); // BlueViolet
        ring.CommitBackBuffer();

        void* destMem = NativeMemory.Alloc((nuint)totalBytes);
        NativeMemory.Clear(destMem, (nuint)totalBytes);
        IntPtr destAddress = (IntPtr)destMem;

        try
        {
            // Act
            ring.BlitFrontBuffer(destAddress, stride, height);

            // Assert: Verify 8K frame content
            uint* destUint = (uint*)destMem;
            int totalPixels = width * height;
            uint expectedPixel = 0xFF000000u | (0xE2u << 16) | (0x2Bu << 8) | 0x8Au;

            for (int p = 0; p < totalPixels; p += 500_000)
            {
                destUint[p].Should().Be(expectedPixel, $"Pixel at index {p} in 8K buffer must match expected pattern");
            }
        }
        finally
        {
            NativeMemory.Free(destMem);
        }
    }

    // ========================================================================
    // 3. Unaligned Strides & Padded Memory Safety
    // ========================================================================

    [Fact]
    public unsafe void UnalignedStride_DestinationLargerThanSource_PreservesPaddingBytesIntact()
    {
        // Arrange: Source 100x50 (stride 400), Destination RowBytes 512 (112 padding bytes per row)
        int width = 100;
        int height = 50;
        int srcStride = width * 4; // 400
        int dstStride = 512;
        int dstTotalBytes = dstStride * height;

        using NativeBufferRing ring = new(width, height);
        ring.ClearBuffer(clearBackBuffer: false, r: 0xAB, g: 0xCD, b: 0xEF, a: 0x01);

        void* destMem = NativeMemory.Alloc((nuint)dstTotalBytes);
        // Fill destination entirely with canary byte 0x77
        NativeMemory.Fill(destMem, (nuint)dstTotalBytes, 0x77);
        IntPtr destAddress = (IntPtr)destMem;

        try
        {
            // Act
            ring.BlitFrontBuffer(destAddress, dstStride, height);

            // Assert
            byte* destBytes = (byte*)destMem;
            for (int y = 0; y < height; y++)
            {
                int rowOffset = y * dstStride;

                // Check pixel bytes [0..400)
                for (int x = 0; x < srcStride; x += 4)
                {
                    destBytes[rowOffset + x + 0].Should().Be(0xAB);
                    destBytes[rowOffset + x + 1].Should().Be(0xCD);
                    destBytes[rowOffset + x + 2].Should().Be(0xEF);
                    destBytes[rowOffset + x + 3].Should().Be(0x01);
                }

                // Check padding bytes [400..512) remain untouched canary 0x77
                for (int pad = srcStride; pad < dstStride; pad++)
                {
                    destBytes[rowOffset + pad].Should().Be(0x77, $"Padding byte at row {y}, col {pad} must remain uncorrupted canary");
                }
            }
        }
        finally
        {
            NativeMemory.Free(destMem);
        }
    }

    [Fact]
    public unsafe void UnalignedStride_DestinationShorterAndNarrower_ClipsSafelyWithoutOverflow()
    {
        // Arrange: Source 200x100, Destination 80x40
        int srcWidth = 200;
        int srcHeight = 100;
        int dstWidth = 80;
        int dstHeight = 40;
        int dstStride = dstWidth * 4; // 320
        int dstTotalBytes = dstStride * dstHeight;

        using NativeBufferRing ring = new(srcWidth, srcHeight);
        ring.ClearBuffer(clearBackBuffer: false, r: 0x99, g: 0x88, b: 0x77, a: 0x66);

        // Allocate buffer with guard pages / canaries before and after
        int guardSize = 1024;
        int totalAllocSize = guardSize + dstTotalBytes + guardSize;
        void* rawMem = NativeMemory.Alloc((nuint)totalAllocSize);
        NativeMemory.Fill(rawMem, (nuint)totalAllocSize, 0xCC); // Canary

        byte* basePtr = (byte*)rawMem;
        IntPtr destAddress = (IntPtr)(basePtr + guardSize);

        try
        {
            // Act: Blit with smaller destination height & stride
            ring.BlitFrontBuffer(destAddress, dstStride, dstHeight);

            // Assert: Verify guard zones were not overrun
            for (int g = 0; g < guardSize; g++)
            {
                basePtr[g].Should().Be(0xCC, "Pre-buffer guard zone must not be overwritten!");
            }

            for (int g = guardSize + dstTotalBytes; g < totalAllocSize; g++)
            {
                basePtr[g].Should().Be(0xCC, "Post-buffer guard zone must not be overwritten!");
            }

            // Verify copied pixels
            byte* dstBytes = (byte*)destAddress;
            for (int i = 0; i < dstTotalBytes; i += 4)
            {
                dstBytes[i + 0].Should().Be(0x99);
                dstBytes[i + 1].Should().Be(0x88);
                dstBytes[i + 2].Should().Be(0x77);
                dstBytes[i + 3].Should().Be(0x66);
            }
        }
        finally
        {
            NativeMemory.Free(rawMem);
        }
    }

    // ========================================================================
    // 4. Multi-Threaded High-Concurrency Producer/Consumer Stress Tests
    // ========================================================================

    [Trait("Category", "Manual")]
    [Fact]
    public async Task HighConcurrency_8Threads_ProducersAndConsumers_NoTornFramesOrCorruption()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange: 512x512 ring, 4 producers + 4 consumers running concurrently
        int width = 512;
        int height = 512;
        int stride = width * 4;
        int totalBytes = stride * height;

        using NativeBufferRing ring = new(width, height);
        ring.ClearBuffer(clearBackBuffer: false, 0, 0, 0, 255);
        ring.ClearBuffer(clearBackBuffer: true, 0, 0, 0, 255);

        int isRunning = 1;
        long totalFramesProduced = 0;
        long totalFramesConsumed = 0;
        int corruptionCount = 0;

        Task[] tasks = new Task[8];

        object producerLock = new();

        // 4 Producers: write uniform pixel per frame and commit
        for (int p = 0; p < 4; p++)
        {
            int producerId = p;
            tasks[p] = Task.Run(() =>
            {
                int localFrame = 0;
                while (Volatile.Read(ref isRunning) == 1 && localFrame < 2500)
                {
                    byte color = (byte)((producerId * 64 + localFrame) & 0xFF);
                    lock (producerLock)
                    {
                        ring.ClearBuffer(clearBackBuffer: true, color, color, color, 255);
                        ring.CommitBackBuffer();
                    }
                    Interlocked.Increment(ref totalFramesProduced);
                    localFrame++;
                    Thread.Yield();
                }
            });
        }

        // 4 Consumers: blit front buffer and verify all pixels within a frame are consistent
        for (int c = 4; c < 8; c++)
        {
            tasks[c] = Task.Run(() =>
            {
                IntPtr consumerDest = Marshal.AllocHGlobal(totalBytes);
                try
                {
                    int localConsumed = 0;
                    while (Volatile.Read(ref isRunning) == 1 && localConsumed < 2500)
                    {
                        ring.BlitFrontBuffer(consumerDest, stride, height);

                        // Verify integrity: inspect first and last pixel
                        unsafe
                        {
                            uint* ptr = (uint*)consumerDest;
                            uint firstPixel = ptr[0];
                            uint lastPixel = ptr[width * height - 1];

                            // Alpha must always be 255 (no uninitialized memory or partial writes)
                            byte a1 = (byte)((firstPixel >> 24) & 0xFF);
                            byte a2 = (byte)((lastPixel >> 24) & 0xFF);

                            if (a1 != 255 || a2 != 255)
                            {
                                Interlocked.Increment(ref corruptionCount);
                            }
                        }

                        Interlocked.Increment(ref totalFramesConsumed);
                        localConsumed++;
                        Thread.Yield();
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(consumerDest);
                }
            });
        }

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
        try
        {
            await Task.WhenAll(tasks).WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Timeout safety
        }

        Volatile.Write(ref isRunning, 0);

        // Assert
        totalFramesProduced.Should().BeGreaterThanOrEqualTo(500, "Producers should produce frames under concurrency");
        totalFramesConsumed.Should().BeGreaterThanOrEqualTo(500, "Consumers should consume frames under concurrency");
        corruptionCount.Should().Be(0, "High concurrency producer/consumer swaps must produce ZERO frame corruptions!");
    }

    [Fact]
    public void LockFreeAtomicSwap_16ThreadsContention_AllSwapsExecuteWithoutDeadlock()
    {
        // Arrange
        using NativeBufferRing ring = new(64, 64);
        int threadCount = 16;
        int swapsPerThread = 1000;
        Thread[] threads = new Thread[threadCount];
        int completedThreads = 0;

        for (int t = 0; t < threadCount; t++)
        {
            threads[t] = new Thread(() =>
            {
                for (int s = 0; s < swapsPerThread; s++)
                {
                    ring.CommitBackBuffer();
                }
                Interlocked.Increment(ref completedThreads);
            });
        }

        // Act
        for (int t = 0; t < threadCount; t++)
        {
            threads[t].Start();
        }

        for (int t = 0; t < threadCount; t++)
        {
            threads[t].Join(TimeSpan.FromSeconds(5));
        }

        // Assert
        completedThreads.Should().Be(threadCount, "All 16 threads must complete 1,000 atomic CAS swaps without deadlock!");
        (ring.FrontIndex == 0 || ring.FrontIndex == 1).Should().BeTrue();
    }

    // ========================================================================
    // 5. Concurrent Dynamic Resizing & Memory Safety
    // ========================================================================

    [Trait("Category", "Manual")]
    [Fact]
    public async Task ConcurrentResize_WhileBlittingAndSwapping_MaintainsThreadSafety()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange
        using NativeBufferRing ring = new(800, 600);
        int isRunning = 1;
        int resizeCount = 0;
        int errorCount = 0;

        object syncLock = new();

        // Resize Task (Continuously resizes between various resolutions)
        Task resizeTask = Task.Run(async () =>
        {
            (int w, int h)[] resolutions =
            [
                (640, 480),
                (1280, 720),
                (1920, 1080),
                (1024, 768),
                (800, 600),
                (2560, 1440),
                (3840, 2160),
                (1366, 768)
            ];

            int idx = 0;
            while (Volatile.Read(ref isRunning) == 1 && resizeCount < 50)
            {
                (int w, int h) = resolutions[idx % resolutions.Length];
                try
                {
                    lock (syncLock)
                    {
                        ring.Resize(w, h);
                    }
                    Interlocked.Increment(ref resizeCount);
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref errorCount);
                }
                idx++;
                await Task.Delay(5);
            }
        });

        // Worker Task (Clears, Commits, Blits)
        Task workerTask = Task.Run(() =>
        {
            int maxAlloc = 3840 * 2160 * 4;
            IntPtr destMem = Marshal.AllocHGlobal(maxAlloc);
            try
            {
                while (Volatile.Read(ref isRunning) == 1)
                {
                    try
                    {
                        lock (syncLock)
                        {
                            ring.ClearBuffer(clearBackBuffer: true, 10, 20, 30, 255);
                            ring.CommitBackBuffer();
                            ring.BlitFrontBuffer(destMem, ring.Stride, ring.Height);
                        }
                    }
                    catch (Exception)
                    {
                        Interlocked.Increment(ref errorCount);
                    }
                    Thread.Yield();
                }
            }
            finally
            {
                Marshal.FreeHGlobal(destMem);
            }
        });

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        try
        {
            await resizeTask.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException) { }

        Volatile.Write(ref isRunning, 0);
        await workerTask;

        // Assert
        resizeCount.Should().BeGreaterThanOrEqualTo(20, "Should have executed multiple dynamic resizes");
        errorCount.Should().Be(0, "Concurrent resize and blit operations must not throw unhandled exceptions or crash!");
    }

    // ========================================================================
    // 6. Error Handling & Disposal Idempotency
    // ========================================================================

    [Fact]
    public void InvalidArguments_WithInvalidInput_ThrowExpectedExceptions()
    {
        using NativeBufferRing ring = new(640, 480);

        // Null destination address
        Action actNull = () => ring.BlitFrontBuffer(IntPtr.Zero, 640 * 4, 480);
        actNull.Should().Throw<ArgumentException>();

        // Non-positive destination row bytes
        Action actNegStride = () => ring.BlitFrontBuffer((IntPtr)0x12345678, -100, 480);
        actNegStride.Should().Throw<ArgumentOutOfRangeException>();

        Action actZeroStride = () => ring.BlitFrontBuffer((IntPtr)0x12345678, 0, 480);
        actZeroStride.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ConcurrentDisposal_10Threads_DisposesSafelyWithoutDoubleFree()
    {
        // Arrange
        NativeBufferRing ring = new(1920, 1080);
        Thread[] threads = new Thread[10];

        for (int i = 0; i < 10; i++)
        {
            threads[i] = new Thread(() =>
            {
                ring.Dispose();
            });
        }

        // Act
        for (int i = 0; i < 10; i++)
        {
            threads[i].Start();
        }

        for (int i = 0; i < 10; i++)
        {
            threads[i].Join(TimeSpan.FromSeconds(3));
        }

        // Assert
        ring.IsDisposed.Should().BeTrue();

        Action actAcquire = () => ring.AcquireBackBuffer();
        actAcquire.Should().Throw<ObjectDisposedException>();

        Action actCommit = () => ring.CommitBackBuffer();
        actCommit.Should().Throw<ObjectDisposedException>();

        Action actBlit = () => ring.BlitFrontBuffer((IntPtr)0x1000, 100, 100);
        actBlit.Should().Throw<ObjectDisposedException>();

        Action actClear = () => ring.ClearBuffer(true, 0, 0, 0);
        actClear.Should().Throw<ObjectDisposedException>();

        Action actResize = () => ring.Resize(800, 600);
        actResize.Should().Throw<ObjectDisposedException>();
    }

    // ========================================================================
    // 7. ILockedFramebuffer Mock Implementation Contract
    // ========================================================================

    [Fact]
    public unsafe void BlitFrontBuffer_ILockedFramebuffer_TransfersAccuratelyWithZeroGC()
    {
        // Arrange
        int width = 800;
        int height = 600;
        int stride = width * 4;
        int totalBytes = stride * height;

        using NativeBufferRing ring = new(width, height);
        ring.ClearBuffer(clearBackBuffer: false, r: 0x44, g: 0x55, b: 0x66, a: 0xFF);

        void* destMem = NativeMemory.Alloc((nuint)totalBytes);
        try
        {
            TestLockedFramebuffer fb = new((IntPtr)destMem, new PixelSize(width, height), stride);

            // Warmup
            ring.BlitFrontBuffer(fb);

            // Act: Verify zero GC allocation through interface call
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                ring.BlitFrontBuffer(fb);
            }
            long allocatedAfter = GC.GetAllocatedBytesForCurrentThread();
            long totalAllocated = allocatedAfter - allocatedBefore;

            // Assert
            totalAllocated.Should().Be(0, "BlitFrontBuffer(ILockedFramebuffer) must have zero managed heap allocations!");

            byte* destBytes = (byte*)destMem;
            destBytes[0].Should().Be(0x44);
            destBytes[1].Should().Be(0x55);
            destBytes[2].Should().Be(0x66);
            destBytes[3].Should().Be(0xFF);
        }
        finally
        {
            NativeMemory.Free(destMem);
        }
    }

    private sealed class TestLockedFramebuffer : ILockedFramebuffer
    {
        public IntPtr Address { get; }
        public PixelSize Size { get; }
        public int RowBytes { get; }
        public Vector Dpi => new(96, 96);
        public PixelFormat Format => PixelFormat.Rgba8888;

        public TestLockedFramebuffer(IntPtr address, PixelSize size, int rowBytes)
        {
            Address = address;
            Size = size;
            RowBytes = rowBytes;
        }

        public void Dispose()
        {
        }
    }
}
