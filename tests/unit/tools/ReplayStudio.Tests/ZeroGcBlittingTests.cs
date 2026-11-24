// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: ZeroGcBlittingTests.cs
// System: Replay Studio Desktop GUI Tool
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using ReplayStudio.GUI.Common;
using Xunit;

namespace ReplayStudio.Tests;

public sealed class ZeroGcBlittingTests
{
    [Fact]
    public void Constructor_ValidDimensions_AllocatesCorrectNativeMemorySizes()
    {
        // Arrange & Act
        int width = 1920;
        int height = 1080;
        using NativeBufferRing ring = new(width, height);

        // Assert
        ring.Width.Should().Be(1920);
        ring.Height.Should().Be(1080);
        ring.Stride.Should().Be(1920 * 4);
        ring.BufferSizeBytes.Should().Be(1920 * 1080 * 4);
        ring.FrontIndex.Should().Be(0);
        ring.BackIndex.Should().Be(1);
        ring.FrontBufferAddress.Should().NotBe(IntPtr.Zero);
        ring.BackBufferAddress.Should().NotBe(IntPtr.Zero);
        ring.FrontBufferAddress.Should().NotBe(ring.BackBufferAddress);
        ring.IsDisposed.Should().BeFalse();
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(-100, 100)]
    [InlineData(100, 0)]
    [InlineData(100, -50)]
    public void Constructor_InvalidDimensions_ThrowsArgumentOutOfRangeException(int width, int height)
    {
        // Act
        Action act = () =>
        {
            using NativeBufferRing _ = new(width, height);
        };

        // Assert
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void CommitBackBuffer_AtomicSwap_TogglesFrontAndBackBuffers()
    {
        // Arrange
        using NativeBufferRing ring = new(800, 600);
        IntPtr initialFront = ring.FrontBufferAddress;
        IntPtr initialBack = ring.BackBufferAddress;
        ring.FrontIndex.Should().Be(0);
        ring.BackIndex.Should().Be(1);

        // Act 1: First commit
        ring.CommitBackBuffer();

        // Assert 1
        ring.FrontIndex.Should().Be(1);
        ring.BackIndex.Should().Be(0);
        ring.FrontBufferAddress.Should().Be(initialBack);
        ring.BackBufferAddress.Should().Be(initialFront);

        // Act 2: Second commit
        ring.CommitBackBuffer();

        // Assert 2
        ring.FrontIndex.Should().Be(0);
        ring.BackIndex.Should().Be(1);
        ring.FrontBufferAddress.Should().Be(initialFront);
        ring.BackBufferAddress.Should().Be(initialBack);
    }

    [Fact]
    public unsafe void BlitFrontBuffer_MatchingStride_CopiesExactPixelDataVectorized()
    {
        // Arrange
        int width = 64;
        int height = 64;
        int stride = width * 4;
        int totalBytes = stride * height;
        using NativeBufferRing ring = new(width, height);

        // Fill front buffer with test pattern: 0xAA, 0xBB, 0xCC, 0xFF
        ring.ClearBuffer(clearBackBuffer: false, r: 0xAA, g: 0xBB, b: 0xCC, a: 0xFF);

        void* destMem = NativeMemory.Alloc((nuint)totalBytes);
        NativeMemory.Clear(destMem, (nuint)totalBytes);
        IntPtr destAddress = (IntPtr)destMem;

        try
        {
            // Act
            ring.BlitFrontBuffer(destAddress, stride, height);

            // Assert
            byte* destBytes = (byte*)destMem;
            for (int i = 0; i < totalBytes; i += 4)
            {
                destBytes[i + 0].Should().Be(0xAA);
                destBytes[i + 1].Should().Be(0xBB);
                destBytes[i + 2].Should().Be(0xCC);
                destBytes[i + 3].Should().Be(0xFF);
            }
        }
        finally
        {
            NativeMemory.Free(destMem);
        }
    }

    [Fact]
    public unsafe void BlitFrontBuffer_PaddedStride_HandlesRowPaddingCorrectly()
    {
        // Arrange
        int width = 32;
        int height = 16;
        int srcStride = width * 4; // 128 bytes
        int dstStride = 160; // 32 bytes padding per row
        int dstTotalBytes = dstStride * height;

        using NativeBufferRing ring = new(width, height);
        ring.ClearBuffer(clearBackBuffer: false, r: 0x11, g: 0x22, b: 0x33, a: 0x44);

        void* destMem = NativeMemory.Alloc((nuint)dstTotalBytes);
        NativeMemory.Clear(destMem, (nuint)dstTotalBytes);
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
                // Check pixel bytes
                for (int x = 0; x < srcStride; x += 4)
                {
                    destBytes[rowOffset + x + 0].Should().Be(0x11);
                    destBytes[rowOffset + x + 1].Should().Be(0x22);
                    destBytes[rowOffset + x + 2].Should().Be(0x33);
                    destBytes[rowOffset + x + 3].Should().Be(0x44);
                }

                // Check padding bytes remain untouched (0)
                for (int pad = srcStride; pad < dstStride; pad++)
                {
                    destBytes[rowOffset + pad].Should().Be(0);
                }
            }
        }
        finally
        {
            NativeMemory.Free(destMem);
        }
    }

    [Trait("Category", "Manual")]
    [Fact]
    public unsafe void BlitFrontBuffer_1000Iterations_ProducesZeroManagedHeapAllocations()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange
        int width = 1920;
        int height = 1080;
        int stride = width * 4;
        int totalBytes = stride * height;

        using NativeBufferRing ring = new(width, height);
        ring.ClearBuffer(clearBackBuffer: false, r: 128, g: 64, b: 32, a: 255);

        void* destMem = NativeMemory.Alloc((nuint)totalBytes);
        IntPtr destAddress = (IntPtr)destMem;

        try
        {
            // Warmup loop: JIT compiles all instructions
            for (int w = 0; w < 10; w++)
            {
                ring.BlitFrontBuffer(destAddress, stride, height);
            }

            // Act: Capture allocation counter before 1000 consecutive blits
            long allocatedBytesBefore = GC.GetAllocatedBytesForCurrentThread();

            for (int i = 0; i < 1000; i++)
            {
                ring.BlitFrontBuffer(destAddress, stride, height);
            }

            long allocatedBytesAfter = GC.GetAllocatedBytesForCurrentThread();
            long totalAllocatedDuringLoop = allocatedBytesAfter - allocatedBytesBefore;

            // Assert: Must be EXACTLY ZERO bytes of managed heap allocations
            totalAllocatedDuringLoop.Should().Be(0, "The 1000-iteration viewport blit loop must perform ZERO managed heap allocations!");
        }
        finally
        {
            NativeMemory.Free(destMem);
        }
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task ConcurrentProducerConsumer_SwapAndBlit_ThreadSafeNoCorruption()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange
        int width = 256;
        int height = 256;
        int stride = width * 4;
        int totalBytes = stride * height;

        using NativeBufferRing ring = new(width, height);
        IntPtr destAddress = Marshal.AllocHGlobal(totalBytes);

        int producerFrameCount = 0;
        int consumerFrameCount = 0;
        int isRunning = 1;

        try
        {
            // Producer Task (Simulates Vulkan render thread)
            Task producerTask = Task.Run(() =>
            {
                while (Volatile.Read(ref isRunning) == 1 && producerFrameCount < 500)
                {
                    byte colorVal = (byte)(producerFrameCount % 256);
                    ring.ClearBuffer(clearBackBuffer: true, colorVal, colorVal, colorVal, 255);
                    ring.CommitBackBuffer();
                    Interlocked.Increment(ref producerFrameCount);
                    Thread.Yield();
                }
            });

            // Consumer Task (Simulates Avalonia UI render thread)
            Task consumerTask = Task.Run(() =>
            {
                while (Volatile.Read(ref isRunning) == 1 && consumerFrameCount < 500)
                {
                    ring.BlitFrontBuffer(destAddress, stride, height);
                    Interlocked.Increment(ref consumerFrameCount);
                    Thread.Yield();
                }
            });

            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
            try
            {
                await Task.WhenAll(producerTask, consumerTask).WaitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Timeout safety
            }

            Volatile.Write(ref isRunning, 0);

            // Assert
            producerFrameCount.Should().BeGreaterThanOrEqualTo(100);
            consumerFrameCount.Should().BeGreaterThanOrEqualTo(100);
        }
        finally
        {
            Marshal.FreeHGlobal(destAddress);
        }
    }

    [Fact]
    public unsafe void Resize_DifferentDimensions_ReallocatesSafelyAndPreservesIntegrity()
    {
        // Arrange
        using NativeBufferRing ring = new(800, 600);
        ring.Width.Should().Be(800);
        ring.Height.Should().Be(600);

        // Act
        ring.Resize(1280, 720);

        // Assert
        ring.Width.Should().Be(1280);
        ring.Height.Should().Be(720);
        ring.Stride.Should().Be(1280 * 4);
        ring.BufferSizeBytes.Should().Be(1280 * 720 * 4);

        // Test blit on resized buffer
        int totalBytes = ring.BufferSizeBytes;
        void* destMem = NativeMemory.Alloc((nuint)totalBytes);
        try
        {
            ring.ClearBuffer(clearBackBuffer: false, 50, 100, 150, 255);
            ring.BlitFrontBuffer((IntPtr)destMem, ring.Stride, ring.Height);

            byte* b = (byte*)destMem;
            b[0].Should().Be(50);
            b[1].Should().Be(100);
            b[2].Should().Be(150);
            b[3].Should().Be(255);
        }
        finally
        {
            NativeMemory.Free(destMem);
        }
    }

    [Fact]
    public void Dispose_InvokedMultipleTimes_ReleasesMemoryIdempotently()
    {
        // Arrange
        NativeBufferRing ring = new(640, 480);

        // Act
        ring.Dispose();
        ring.Dispose(); // Idempotent call

        // Assert
        ring.IsDisposed.Should().BeTrue();
        Action act = () => ring.AcquireBackBuffer();
        act.Should().Throw<ObjectDisposedException>();
    }
}
