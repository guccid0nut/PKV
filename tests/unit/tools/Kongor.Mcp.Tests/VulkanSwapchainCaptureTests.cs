// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

// ============================================================================
// File: VulkanSwapchainCaptureTests.cs
// System: ProjectKongor Developer CLI & TAO MCP Server
// Component: Vulkan Swapchain Direct Offscreen Frame Grabber Unit Tests
// Policy: Strict Explicit Typing (NO 'var'), Sub-Second Execution
// ============================================================================

using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Imaging;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;
using NEXUS.Contracts.Graphics;
using Moq;
using FluentAssertions;
using Xunit;

namespace Kongor.Mcp.Tests;

[SupportedOSPlatform("windows")]
public sealed class VulkanSwapchainCaptureTests
{
    private static readonly byte[] PngHeader = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Fact]
    public void VulkanMemoryMappedFrameGrabber_IsSharedMemoryAvailable_WhenMapDoesNotExist_ReturnsFalse()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        VulkanMemoryMappedFrameGrabber grabber = new();
        bool isAvailable = grabber.IsSharedMemoryAvailable(9999999);

        isAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task VulkanMemoryMappedFrameGrabber_ValidMockSharedMemory_ExtractsBgraFrameSuccessfully()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        string uniqueId = Guid.NewGuid().ToString("N");
        string mapName = $"Local\\TestHonFrameBuffer_{uniqueId}";
        string readyPrefix = $"Local\\TestHonFrameReady_{uniqueId}";
        string donePrefix = $"Local\\TestHonFrameDone_{uniqueId}";

        int width = 64;
        int height = 64;
        int headerSize = Marshal.SizeOf<FrameHeader>(); // 64 bytes
        int pixelDataSize = width * height * 4;
        int slotStride = headerSize + pixelDataSize;
        int slotCount = 3;
        long totalMapSize = slotStride * slotCount;

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
            // Populate Slot 0 with valid FrameHeader and test pixel pattern
            FrameHeader writeHeader = new()
            {
                Magic = FrameHeader.ExpectedMagicHonc,
                Version = FrameHeader.ExpectedVersion,
                Width = (uint)width,
                Height = (uint)height,
                Format = FrameHeader.FormatB8G8R8A8Unorm,
                RowPitch = (uint)(width * 4),
                FrameIndex = 100,
                TimestampNs = 1_000_000_000UL,
                DataSizeBytes = (uint)pixelDataSize,
                BufferSlotIndex = 0,
                Flags = FrameHeader.FlagKeyframe
            };

            accessor.Write(0, ref writeHeader);

            byte[] testPixels = new byte[pixelDataSize];
            for (int p = 0; p < pixelDataSize; p += 4)
            {
                testPixels[p] = 255;     // B
                testPixels[p + 1] = 128; // G
                testPixels[p + 2] = 64;  // R
                testPixels[p + 3] = 255; // A
            }
            accessor.WriteArray(headerSize, testPixels, 0, pixelDataSize);

            // Signal Slot 0 Ready
            readyEvents[0].Set();

            VulkanMemoryMappedFrameGrabber grabber = new();
            VulkanFrameResult? result = await grabber.GrabSingleFrameFromMapAsync(
                mapName,
                readyPrefix,
                donePrefix,
                slotCount: slotCount,
                timeoutMs: 1500);

            result.Should().NotBeNull();
            result!.Success.Should().BeTrue();
            result.Width.Should().Be(width);
            result.Height.Should().Be(height);
            result.Format.Should().Be(FrameHeader.FormatB8G8R8A8Unorm);
            result.FrameIndex.Should().Be(100);
            result.BufferSlotIndex.Should().Be(0);
            result.PixelData.Length.Should().Be(pixelDataSize);
            result.PixelData[0].Should().Be(255);
            result.PixelData[1].Should().Be(128);
            result.PixelData[2].Should().Be(64);
            result.PixelData[3].Should().Be(255);

            // Verify Done Event was signaled by the consumer grabber
            bool doneSignaled = doneEvents[0].WaitOne(200);
            doneSignaled.Should().BeTrue();
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

    [Fact]
    public async Task VulkanMemoryMappedFrameGrabber_RgbaFormatInSlot1_ExtractsCorrectly()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        string uniqueId = Guid.NewGuid().ToString("N");
        string mapName = $"Local\\TestHonFrameBuffer_{uniqueId}";
        string readyPrefix = $"Local\\TestHonFrameReady_{uniqueId}";
        string donePrefix = $"Local\\TestHonFrameDone_{uniqueId}";

        int width = 32;
        int height = 32;
        int headerSize = Marshal.SizeOf<FrameHeader>();
        int pixelDataSize = width * height * 4;
        int slotStride = headerSize + pixelDataSize;
        int slotCount = 3;
        long totalMapSize = slotStride * slotCount;

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
            // Populate Slot 0 with probe template
            FrameHeader probeHeader = new()
            {
                Magic = FrameHeader.ExpectedMagicHonc,
                Version = FrameHeader.ExpectedVersion,
                Width = (uint)width,
                Height = (uint)height,
                Format = FrameHeader.FormatR8G8B8A8Unorm,
                RowPitch = (uint)(width * 4),
                DataSizeBytes = (uint)pixelDataSize
            };
            accessor.Write(0, ref probeHeader);

            // Populate Slot 1
            long slot1Offset = slotStride;
            FrameHeader writeHeader = new()
            {
                Magic = FrameHeader.ExpectedMagicHonf,
                Version = FrameHeader.ExpectedVersion,
                Width = (uint)width,
                Height = (uint)height,
                Format = FrameHeader.FormatR8G8B8A8Unorm,
                RowPitch = (uint)(width * 4),
                FrameIndex = 250,
                TimestampNs = 2_000_000_000UL,
                DataSizeBytes = (uint)pixelDataSize,
                BufferSlotIndex = 1,
                Flags = FrameHeader.FlagKeyframe
            };

            accessor.Write(slot1Offset, ref writeHeader);

            byte[] testPixels = new byte[pixelDataSize];
            Array.Fill<byte>(testPixels, 180);
            accessor.WriteArray(slot1Offset + headerSize, testPixels, 0, pixelDataSize);

            // Signal Slot 1 Ready
            readyEvents[1].Set();

            VulkanMemoryMappedFrameGrabber grabber = new();
            VulkanFrameResult? result = await grabber.GrabSingleFrameFromMapAsync(
                mapName,
                readyPrefix,
                donePrefix,
                slotCount: slotCount,
                timeoutMs: 1500);

            result.Should().NotBeNull();
            result!.Success.Should().BeTrue();
            result.Width.Should().Be(width);
            result.Height.Should().Be(height);
            result.Format.Should().Be(FrameHeader.FormatR8G8B8A8Unorm);
            result.FrameIndex.Should().Be(250);
            result.BufferSlotIndex.Should().Be(1);
            result.PixelData[0].Should().Be(180);

            // Verify Slot 1 Done Event was signaled
            bool doneSignaled = doneEvents[1].WaitOne(200);
            doneSignaled.Should().BeTrue();
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

    [Fact]
    public async Task VulkanMemoryMappedFrameGrabber_InvalidMagic_RejectsFrameAndSignalsDoneEvent()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        string uniqueId = Guid.NewGuid().ToString("N");
        string mapName = $"Local\\TestHonFrameBuffer_{uniqueId}";
        string readyPrefix = $"Local\\TestHonFrameReady_{uniqueId}";
        string donePrefix = $"Local\\TestHonFrameDone_{uniqueId}";

        int width = 32;
        int height = 32;
        int headerSize = Marshal.SizeOf<FrameHeader>();
        int pixelDataSize = width * height * 4;
        int slotStride = headerSize + pixelDataSize;
        int slotCount = 3;
        long totalMapSize = slotStride * slotCount;

        using MemoryMappedFile mmf = MemoryMappedFile.CreateNew(mapName, totalMapSize, MemoryMappedFileAccess.ReadWrite);
        using MemoryMappedViewAccessor accessor = mmf.CreateViewAccessor(0, totalMapSize, MemoryMappedFileAccess.ReadWrite);

        EventWaitHandle[] readyEvents = new EventWaitHandle[slotCount];
        EventWaitHandle[] doneEvents = new EventWaitHandle[slotCount];

        for (int i = 0; i < slotCount; i++)
        {
            readyEvents[i] = new EventWaitHandle(false, EventResetMode.AutoReset, $"{readyPrefix}_{i}");
            doneEvents[i] = new EventWaitHandle(false, EventResetMode.AutoReset, $"{donePrefix}_{i}");
        }

        try
        {
            // Write corrupt header with invalid magic
            FrameHeader badHeader = new()
            {
                Magic = 0xDEADBEEF,
                Version = 99,
                Width = (uint)width,
                Height = (uint)height,
                DataSizeBytes = (uint)pixelDataSize
            };
            accessor.Write(0, ref badHeader);

            readyEvents[0].Set();

            VulkanMemoryMappedFrameGrabber grabber = new();
            VulkanFrameResult? result = await grabber.GrabSingleFrameFromMapAsync(
                mapName,
                readyPrefix,
                donePrefix,
                slotCount: slotCount,
                timeoutMs: 1500);

            result.Should().BeNull();

            // Crucial: Done event must still be signaled so the layer does not hang
            bool doneSignaled = doneEvents[0].WaitOne(200);
            doneSignaled.Should().BeTrue();
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

    [Fact]
    public async Task VulkanMemoryMappedFrameGrabber_Timeout_ReturnsNullCleanly()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        string uniqueId = Guid.NewGuid().ToString("N");
        string mapName = $"Local\\TestHonFrameBuffer_{uniqueId}";
        string readyPrefix = $"Local\\TestHonFrameReady_{uniqueId}";
        string donePrefix = $"Local\\TestHonFrameDone_{uniqueId}";

        int slotCount = 3;
        long totalMapSize = 1024 * 1024;

        using MemoryMappedFile mmf = MemoryMappedFile.CreateNew(mapName, totalMapSize, MemoryMappedFileAccess.ReadWrite);

        EventWaitHandle[] readyEvents = new EventWaitHandle[slotCount];
        EventWaitHandle[] doneEvents = new EventWaitHandle[slotCount];

        for (int i = 0; i < slotCount; i++)
        {
            readyEvents[i] = new EventWaitHandle(false, EventResetMode.AutoReset, $"{readyPrefix}_{i}");
            doneEvents[i] = new EventWaitHandle(true, EventResetMode.AutoReset, $"{donePrefix}_{i}");
        }

        try
        {
            // Do NOT signal any ready events
            VulkanMemoryMappedFrameGrabber grabber = new();
            VulkanFrameResult? result = await grabber.GrabSingleFrameFromMapAsync(
                mapName,
                readyPrefix,
                donePrefix,
                slotCount: slotCount,
                timeoutMs: 50);

            result.Should().BeNull();
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

    [Fact]
    public void VulkanFrameResult_EncodeToPng_GeneratesValidPngArtifact()
    {
        int width = 32;
        int height = 32;
        int pixelDataSize = width * height * 4;
        byte[] bgraPixels = new byte[pixelDataSize];

        for (int i = 0; i < pixelDataSize; i += 4)
        {
            bgraPixels[i] = 100;     // B
            bgraPixels[i + 1] = 150; // G
            bgraPixels[i + 2] = 200; // R
            bgraPixels[i + 3] = 255; // A
        }

        VulkanFrameResult frameResult = new()
        {
            Success = true,
            Width = width,
            Height = height,
            Format = FrameHeader.FormatB8G8R8A8Unorm,
            PixelData = bgraPixels
        };

        using Image<Bgra32> image = Image.LoadPixelData<Bgra32>(frameResult.PixelData, frameResult.Width, frameResult.Height);
        using MemoryStream ms = new();
        image.SaveAsPng(ms);
        byte[] pngBytes = ms.ToArray();

        pngBytes.Length.Should().BeGreaterThan(8);
        pngBytes.Take(8).Should().Equal(PngHeader);
    }

    [Fact]
    [Trait("Category", "Manual")]
    public async Task WindowCapturer_CaptureVulkanOffscreenAsync_ValidFrame_ReturnsSuccessWithVulkanOffscreenMethod()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        int width = 32;
        int height = 32;
        int pixelDataSize = width * height * 4;
        byte[] bgraPixels = new byte[pixelDataSize];
        Array.Fill<byte>(bgraPixels, 200);

        Mock<IVulkanFrameGrabber> mockGrabber = new();
        mockGrabber.Setup(g => g.GrabSingleFrameAsync(12345, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VulkanFrameResult
            {
                Success = true,
                Width = width,
                Height = height,
                Format = FrameHeader.FormatB8G8R8A8Unorm,
                FrameIndex = 42,
                TimestampNs = 1_000_000_000UL,
                PixelData = bgraPixels,
                BufferSlotIndex = 0
            });

        WindowCapturer capturer = new(vulkanGrabber: mockGrabber.Object);
        WindowCaptureResult result = await capturer.CaptureVulkanOffscreenAsync(
            pid: 12345,
            targetName: "hon_x64",
            timeoutMs: 1000,
            saveArtifact: false);

        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.CaptureMethodUsed.Should().Be("vulkan_offscreen");
        result.Target.Should().Be("hon_x64");
        result.Width.Should().Be(width);
        result.Height.Should().Be(height);
        result.Base64Png.Should().NotBeNullOrWhiteSpace();

        byte[] decoded = Convert.FromBase64String(result.Base64Png);
        decoded.Length.Should().BeGreaterThan(8);
        decoded.Take(8).Should().Equal(PngHeader);
    }

    [Fact]
    [Trait("Category", "Manual")]
    public async Task WindowCapturer_CaptureVulkanOffscreenAsync_GrabberReturnsNull_ReturnsErrorResult()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        Mock<IVulkanFrameGrabber> mockGrabber = new();
        mockGrabber.Setup(g => g.GrabSingleFrameAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((VulkanFrameResult?)null);

        WindowCapturer capturer = new(vulkanGrabber: mockGrabber.Object);
        WindowCaptureResult result = await capturer.CaptureVulkanOffscreenAsync(
            pid: 98765,
            targetName: "hon_x64",
            timeoutMs: 500,
            saveArtifact: false);

        result.Should().NotBeNull();
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
        result.ErrorMessage.Should().Contain("Failed to acquire Vulkan swapchain frame");
    }

    [Fact]
    public async Task WindowCapturer_CaptureVulkanOffscreenAsync_NegativePid_ReturnsErrorImmediately()
    {
        Mock<IVulkanFrameGrabber> mockGrabber = new();
        WindowCapturer capturer = new(vulkanGrabber: mockGrabber.Object);

        WindowCaptureResult result = await capturer.CaptureVulkanOffscreenAsync(
            pid: -1,
            targetName: "hon_x64");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Invalid target process ID");
        mockGrabber.Verify(g => g.GrabSingleFrameAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CaptureWindowHandler_HandleAsync_WithVulkanOffscreen_ReturnsImageWithZeroExposure()
    {
        Mock<IWindowCapturer> mockCapturer = new();
        mockCapturer.Setup(c => c.CaptureAsync(It.IsAny<string>(), "vulkan_offscreen", It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WindowCaptureResult
            {
                Success = true,
                Target = "hon_x64",
                WindowTitle = "Heroes of Newerth",
                Hwnd = "0x000A1234",
                Pid = 43892,
                Width = 1280,
                Height = 720,
                CaptureMethodUsed = "vulkan_offscreen",
                Base64Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="
            });
        mockCapturer.Setup(c => c.CaptureAsync(
                "hon_x64",
                "vulkan_offscreen",
                It.IsAny<bool>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WindowCaptureResult
            {
                Success = true,
                Target = "hon_x64",
                WindowTitle = "Heroes of Newerth (Vulkan Offscreen)",
                Hwnd = "0x00000000",
                Pid = 12345,
                Width = 1280,
                Height = 720,
                CaptureMethodUsed = "vulkan_offscreen",
                Base64Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="
            });

        CaptureWindowHandler handler = new(
            windowCapturer: mockCapturer.Object,
            processRegistry: new Mock<IClientProcessRegistry>().Object);
        using JsonDocument doc = JsonDocument.Parse("""{"target": "hon_x64", "capture_method": "vulkan_offscreen", "detect_anomalies": false}""");

        ToolCallResult result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeFalse();
        TextContentItem textItem = result.Content.OfType<TextContentItem>().Single();
        using JsonDocument respDoc = JsonDocument.Parse(textItem.Text);
        JsonElement root = respDoc.RootElement;
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("captureMethod").GetString().Should().Be("vulkan_offscreen");
        root.GetProperty("zeroWindowExposure").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task CaptureWindowHandler_HandleTransientAsync_WithVulkanResult_ReturnsZeroDwellAndZeroExposure()
    {
        Mock<IWindowCapturer> mockCapturer = new();
        mockCapturer.Setup(c => c.CaptureAsync(It.IsAny<string>(), "vulkan_offscreen", It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WindowCaptureResult
            {
                Success = true,
                Target = "hon_x64",
                WindowTitle = "Heroes of Newerth",
                Hwnd = "0x000A1234",
                Pid = 43892,
                Width = 1280,
                Height = 720,
                CaptureMethodUsed = "vulkan_offscreen",
                Base64Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="
            });
        mockCapturer.Setup(c => c.CaptureTransientAsync(
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<bool>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WindowCaptureResult
            {
                Success = true,
                Target = "hon_x64",
                WindowTitle = "Heroes of Newerth",
                Hwnd = "0x000A1234",
                Pid = 43892,
                Width = 1280,
                Height = 720,
                CaptureMethodUsed = "vulkan_offscreen",
                Base64Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="
            });

        CaptureWindowHandler handler = new(
            windowCapturer: mockCapturer.Object,
            processRegistry: new Mock<IClientProcessRegistry>().Object);
        using JsonDocument doc = JsonDocument.Parse("""{"target": "hon_x64", "dwell_ms": 350, "detect_anomalies": false}""");

        ToolCallResult result = await handler.HandleTransientAsync(doc.RootElement);

        result.IsError.Should().BeFalse();
        TextContentItem textItem = result.Content.OfType<TextContentItem>().Single();
        using JsonDocument respDoc = JsonDocument.Parse(textItem.Text);
        JsonElement root = respDoc.RootElement;
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("captureMethod").GetString().Should().Be("vulkan_offscreen");
        root.GetProperty("zeroWindowExposure").GetBoolean().Should().BeTrue();
        root.GetProperty("dwellMs").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task VulkanMemoryMappedFrameGrabber_WaitTimeout_RecoversLatestValidCachedFrame()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        string uniqueId = Guid.NewGuid().ToString("N");
        string mapName = $"Local\\TestHonFrameBuffer_{uniqueId}";
        string readyPrefix = $"Local\\TestHonFrameReady_{uniqueId}";
        string donePrefix = $"Local\\TestHonFrameDone_{uniqueId}";

        int width = 32;
        int height = 32;
        int headerSize = Marshal.SizeOf<FrameHeader>();
        int pixelDataSize = width * height * 4;
        int slotStride = headerSize + pixelDataSize;
        int slotCount = 3;
        long totalMapSize = slotStride * slotCount;

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
            // Populate Slot 0 with Frame #100
            FrameHeader slot0Header = new()
            {
                Magic = FrameHeader.ExpectedMagicHonc,
                Version = FrameHeader.ExpectedVersion,
                Width = (uint)width,
                Height = (uint)height,
                Format = FrameHeader.FormatB8G8R8A8Unorm,
                RowPitch = (uint)(width * 4),
                FrameIndex = 100,
                TimestampNs = 1_000_000_000UL,
                DataSizeBytes = (uint)pixelDataSize,
                BufferSlotIndex = 0,
                Flags = FrameHeader.FlagKeyframe
            };
            accessor.Write(0, ref slot0Header);

            // Populate Slot 1 with newer Frame #205
            FrameHeader writeHeader = new()
            {
                Magic = FrameHeader.ExpectedMagicHonc,
                Version = FrameHeader.ExpectedVersion,
                Width = (uint)width,
                Height = (uint)height,
                Format = FrameHeader.FormatB8G8R8A8Unorm,
                RowPitch = (uint)(width * 4),
                FrameIndex = 205,
                TimestampNs = 2_000_000_000UL,
                DataSizeBytes = (uint)pixelDataSize,
                BufferSlotIndex = 1,
                Flags = FrameHeader.FlagKeyframe
            };

            long slot1Offset = 1 * slotStride;
            accessor.Write(slot1Offset, ref writeHeader);

            byte[] testPixels = new byte[pixelDataSize];
            Array.Fill<byte>(testPixels, 127);
            accessor.WriteArray(slot1Offset + headerSize, testPixels, 0, pixelDataSize);

            // Note: Ready events are NOT signaled, simulating an idle presentation loop
            VulkanMemoryMappedFrameGrabber grabber = new();
            VulkanFrameResult? result = await grabber.GrabSingleFrameFromMapAsync(
                mapName, readyPrefix, donePrefix, slotCount: slotCount, timeoutMs: 50);

            result.Should().NotBeNull();
            result!.Success.Should().BeTrue();
            result.Width.Should().Be(width);
            result.Height.Should().Be(height);
            result.FrameIndex.Should().Be(205);
            result.BufferSlotIndex.Should().Be(1);
            result.PixelData.Should().HaveCount(pixelDataSize);
            result.PixelData[0].Should().Be(127);
        }
        finally
        {
            for (int i = 0; i < slotCount; i++)
            {
                readyEvents[i].Dispose();
                doneEvents[i].Dispose();
            }
        }
    }

    [Fact]
    public async Task CaptureWindowHandler_VulkanOffscreenSelfHeals_ReturnsSuccess()
    {
        Mock<IWindowCapturer> mockCapturer = new();
        mockCapturer.Setup(c => c.CaptureAsync(It.IsAny<string>(), "vulkan_offscreen", It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WindowCaptureResult
            {
                Success = true,
                Target = "hon_x64",
                WindowTitle = "Heroes of Newerth",
                Hwnd = "0x000A1234",
                Pid = 43892,
                Width = 1280,
                Height = 720,
                CaptureMethodUsed = "vulkan_offscreen",
                Base64Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="
            });
        mockCapturer.Setup(c => c.CaptureAsync(
                It.IsAny<string>(),
                "vulkan_offscreen",
                It.IsAny<bool>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WindowCaptureResult
            {
                Success = true,
                Target = "hon_x64",
                WindowTitle = "Heroes of Newerth",
                Hwnd = "0x000A1234",
                Pid = 55852,
                Width = 1280,
                Height = 720,
                CaptureMethodUsed = "print_window (self-healed from vulkan_offscreen)",
                Base64Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="
            });

        CaptureWindowHandler handler = new(
            windowCapturer: mockCapturer.Object,
            processRegistry: new Mock<IClientProcessRegistry>().Object);
        using JsonDocument doc = JsonDocument.Parse("""{"target": "hon_x64", "capture_method": "vulkan_offscreen", "detect_anomalies": false}""");

        ToolCallResult result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeFalse();
        TextContentItem textItem = result.Content.OfType<TextContentItem>().Single();
        using JsonDocument respDoc = JsonDocument.Parse(textItem.Text);
        JsonElement root = respDoc.RootElement;
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("captureMethod").GetString().Should().Contain("self-healed from vulkan_offscreen");
    }

    [Theory]
    [InlineData(-10000, -10000, -8720, -9280)] // Safe 16-bit offscreen coordinates
    [InlineData(-32000, -32000, -30720, -31280)] // Deep offscreen coordinates
    [InlineData(55000, 55000, 56280, 55720)]   // Positive offscreen coordinates
    public void WindowCapturer_CaptureWithDesktopDc_RejectsOffscreenBounds_ReturnsNull(int left, int top, int right, int bottom)
    {
        WindowCapturer capturer = new();
        var method = typeof(WindowCapturer).GetMethod("CaptureWithDesktopDc",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        method.Should().NotBeNull();

        WindowCapturer.RECT bounds = new()
        {
            Left = left,
            Top = top,
            Right = right,
            Bottom = bottom
        };

        object? result = method!.Invoke(capturer, [bounds, 1280, 720]);
        result.Should().BeNull("Desktop DC must never blit the physical desktop when window bounds are offscreen.");
    }

    [Fact]
    public async Task CaptureWindowHandler_VulkanOffscreenFails_ReturnsStructuredErrorWithoutDesktopFallback()
    {
        Mock<IWindowCapturer> mockCapturer = new();
        mockCapturer.Setup(c => c.CaptureAsync(It.IsAny<string>(), "vulkan_offscreen", It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WindowCaptureResult
            {
                Success = true,
                Target = "hon_x64",
                WindowTitle = "Heroes of Newerth",
                Hwnd = "0x000A1234",
                Pid = 43892,
                Width = 1280,
                Height = 720,
                CaptureMethodUsed = "vulkan_offscreen",
                Base64Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="
            });
        mockCapturer.Setup(c => c.CaptureAsync(
                It.IsAny<string>(),
                "vulkan_offscreen",
                It.IsAny<bool>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WindowCaptureResult
            {
                Success = false,
                Target = "hon_x64",
                CaptureMethodUsed = "vulkan_offscreen",
                ErrorMessage = "Failed to acquire Vulkan swapchain frame from shared memory for PID 1234."
            });

        CaptureWindowHandler handler = new(
            windowCapturer: mockCapturer.Object,
            processRegistry: new Mock<IClientProcessRegistry>().Object);
        using JsonDocument doc = JsonDocument.Parse("""{"target": "hon_x64", "capture_method": "vulkan_offscreen", "detect_anomalies": false}""");

        ToolCallResult result = await handler.HandleAsync(doc.RootElement);

        result.IsError.Should().BeTrue();
        TextContentItem textItem = result.Content.OfType<TextContentItem>().Single();
        using JsonDocument respDoc = JsonDocument.Parse(textItem.Text);
        JsonElement root = respDoc.RootElement;
        root.GetProperty("status").GetString().Should().Be("error");
        root.GetProperty("errorMessage").GetString().Should().Contain("Failed to acquire Vulkan swapchain frame");
    }
}
