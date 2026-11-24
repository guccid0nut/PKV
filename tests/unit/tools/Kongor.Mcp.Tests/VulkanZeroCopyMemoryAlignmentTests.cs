// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

// ============================================================================
// File: VulkanZeroCopyMemoryAlignmentTests.cs
// System: ProjectKongor Developer CLI & TAO MCP Server
// Component: Vulkan VK_EXT_external_memory_host Direct GPU DMA Zero-Copy Alignment Tests
// Policy: Strict Explicit Typing (NO 'var'), Sub-Second Execution
// ============================================================================

using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FluentAssertions;
using Kongor.Cli.Mcp.Services;
using NEXUS.Contracts.Graphics;
using Xunit;

namespace Kongor.Mcp.Tests;

[SupportedOSPlatform("windows")]
public sealed class VulkanZeroCopyMemoryAlignmentTests
{
    private const uint Win32AllocationGranularity = 0x10000; // 64 KB (65,536 bytes)
    private const uint VulkanMinHostPointerAlignment = 0x1000; // 4 KB (4,096 bytes)
    private const uint CacheLineAlignment = 64; // 64 bytes

    [Fact]
    public void FrameHeader_StructureSize_IsExactly64Bytes()
    {
        int headerSize = Marshal.SizeOf<FrameHeader>();
        headerSize.Should().Be(64, "C++ HONCAP_FRAME_HEADER requires exact 64-byte binary wire layout");
    }

    [Fact]
    public void FrameHeader_FieldOffsets_MatchNativeBinaryLayout()
    {
        Marshal.OffsetOf<FrameHeader>(nameof(FrameHeader.Magic)).ToInt32().Should().Be(0);
        Marshal.OffsetOf<FrameHeader>(nameof(FrameHeader.Version)).ToInt32().Should().Be(4);
        Marshal.OffsetOf<FrameHeader>(nameof(FrameHeader.Width)).ToInt32().Should().Be(8);
        Marshal.OffsetOf<FrameHeader>(nameof(FrameHeader.Height)).ToInt32().Should().Be(12);
        Marshal.OffsetOf<FrameHeader>(nameof(FrameHeader.Format)).ToInt32().Should().Be(16);
        Marshal.OffsetOf<FrameHeader>(nameof(FrameHeader.RowPitch)).ToInt32().Should().Be(20);
        Marshal.OffsetOf<FrameHeader>(nameof(FrameHeader.FrameIndex)).ToInt32().Should().Be(24);
        Marshal.OffsetOf<FrameHeader>(nameof(FrameHeader.TimestampNs)).ToInt32().Should().Be(32);
        Marshal.OffsetOf<FrameHeader>(nameof(FrameHeader.DataSizeBytes)).ToInt32().Should().Be(40);
        Marshal.OffsetOf<FrameHeader>(nameof(FrameHeader.BufferSlotIndex)).ToInt32().Should().Be(44);
        Marshal.OffsetOf<FrameHeader>(nameof(FrameHeader.Flags)).ToInt32().Should().Be(48);
        Marshal.OffsetOf<FrameHeader>(nameof(FrameHeader.Reserved0)).ToInt32().Should().Be(52);
        Marshal.OffsetOf<FrameHeader>(nameof(FrameHeader.Reserved1)).ToInt32().Should().Be(56);
    }

    [Theory]
    [InlineData(1280, 720, 3)]   // 720p HD
    [InlineData(1920, 1080, 3)]  // 1080p FHD
    [InlineData(2560, 1440, 3)]  // 1440p QHD
    [InlineData(3840, 2160, 3)]  // 4K UHD
    public void MemoryAllocation_SectionSizingAndStride_SatisfiesAlignmentConstraints(int width, int height, int ringSlots)
    {
        ulong frameSizeBytes = (ulong)width * (ulong)height * 4UL;
        ulong headerSize = (ulong)Marshal.SizeOf<FrameHeader>(); // 64 bytes

        // Calculate slot stride aligned to 64 bytes
        ulong rawSlotStride = headerSize + frameSizeBytes;
        ulong slotStrideBytes = (rawSlotStride + (CacheLineAlignment - 1)) & ~(CacheLineAlignment - 1);

        // Verify slot stride is 64-byte aligned
        (slotStrideBytes % CacheLineAlignment).Should().Be(0);

        // Calculate total shared memory rounded up to 64 KB Win32 section granularity
        ulong rawTotalBytes = slotStrideBytes * (ulong)ringSlots;
        ulong totalSharedMemBytes = (rawTotalBytes + (Win32AllocationGranularity - 1)) & ~(Win32AllocationGranularity - 1);

        // Verify total size is a multiple of Win32 64 KB granularity and Vulkan 4 KB host pointer alignment
        (totalSharedMemBytes % Win32AllocationGranularity).Should().Be(0);
        (totalSharedMemBytes % VulkanMinHostPointerAlignment).Should().Be(0);

        // Verify each slot's base offset and pixel data offset
        for (uint slot = 0; slot < (uint)ringSlots; slot++)
        {
            ulong slotBaseOffset = (ulong)slot * slotStrideBytes;
            ulong pixelDataOffset = slotBaseOffset + headerSize;

            (slotBaseOffset % CacheLineAlignment).Should().Be(0, $"Slot {slot} base offset must be 64-byte aligned");
            (pixelDataOffset % CacheLineAlignment).Should().Be(0, $"Slot {slot} pixel data offset must be 64-byte aligned");
            (pixelDataOffset % 4UL).Should().Be(0, $"Slot {slot} pixel data offset must be 4-byte texel aligned");
        }
    }

    [Fact]
    public async Task VulkanMemoryMappedFrameGrabber_ExtractsDirectGpuDmaZeroCopyFrame_Successfully()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        string uniqueId = Guid.NewGuid().ToString("N");
        string mapName = $"Local\\TestHonFrameBuffer_{uniqueId}";
        string readyPrefix = $"Local\\TestHonFrameReady_{uniqueId}";
        string donePrefix = $"Local\\TestHonFrameDone_{uniqueId}";

        int width = 128;
        int height = 128;
        int headerSize = Marshal.SizeOf<FrameHeader>(); // 64 bytes
        int pixelDataSize = width * height * 4;
        int slotStride = (headerSize + pixelDataSize + 63) & ~63;
        int slotCount = 3;
        long totalMapSize = (long)((slotStride * slotCount + 0xFFFF) & ~0xFFFF);

        using MemoryMappedFile mmf = MemoryMappedFile.CreateNew(mapName, totalMapSize, MemoryMappedFileAccess.ReadWrite);
        using MemoryMappedViewAccessor accessor = mmf.CreateViewAccessor(0, totalMapSize, MemoryMappedFileAccess.ReadWrite);

        EventWaitHandle[] readyEvents = new EventWaitHandle[slotCount];
        EventWaitHandle[] doneEvents = new EventWaitHandle[slotCount];

        for (int i = 0; i < slotCount; i++)
        {
            readyEvents[i] = new EventWaitHandle(false, EventResetMode.AutoReset, $"{readyPrefix}_{i}");
            doneEvents[i] = new EventWaitHandle(true, EventResetMode.AutoReset, $"{donePrefix}_{i}");
        }

        try
        {
            // Populate Slot 0 with Tier 0 Zero-Copy DMA FrameHeader
            FrameHeader writeHeader = new()
            {
                Magic = FrameHeader.ExpectedMagicHonc,
                Version = FrameHeader.ExpectedVersion,
                Width = (uint)width,
                Height = (uint)height,
                Format = FrameHeader.FormatB8G8R8A8Unorm,
                RowPitch = (uint)(width * 4),
                FrameIndex = 42,
                TimestampNs = 1_234_567_890UL,
                DataSizeBytes = (uint)pixelDataSize,
                BufferSlotIndex = 0,
                Flags = FrameHeader.FlagZeroCopyDma // 0x1 indicates Tier 0 Direct GPU DMA
            };

            accessor.Write(0, ref writeHeader);

            byte[] testPixels = new byte[pixelDataSize];
            for (int p = 0; p < pixelDataSize; p += 4)
            {
                testPixels[p] = 200;     // B
                testPixels[p + 1] = 100; // G
                testPixels[p + 2] = 50;  // R
                testPixels[p + 3] = 255; // A
            }
            accessor.WriteArray(headerSize, testPixels, 0, pixelDataSize);

            // Signal Slot 0 Ready
            readyEvents[0].Set();

            VulkanMemoryMappedFrameGrabber grabber = new();
            VulkanFrameResult? frame = await grabber.GrabSingleFrameFromMapAsync(
                mapName,
                readyPrefix,
                donePrefix,
                slotCount: slotCount,
                timeoutMs: 1500);

            frame.Should().NotBeNull();
            frame!.Success.Should().BeTrue();
            frame.Width.Should().Be(width);
            frame.Height.Should().Be(height);
            frame.FrameIndex.Should().Be(42UL);
            frame.BufferSlotIndex.Should().Be(0);
            frame.PixelData.Should().NotBeNull();
            frame.PixelData.Length.Should().Be(pixelDataSize);

            // Verify pixel values match
            frame.PixelData[0].Should().Be(200);
            frame.PixelData[1].Should().Be(100);
            frame.PixelData[2].Should().Be(50);
            frame.PixelData[3].Should().Be(255);
        }
        finally
        {
            for (int i = 0; i < slotCount; i++)
            {
                readyEvents[i]?.Dispose();
                doneEvents[i]?.Dispose();
            }
        }
    }
}
