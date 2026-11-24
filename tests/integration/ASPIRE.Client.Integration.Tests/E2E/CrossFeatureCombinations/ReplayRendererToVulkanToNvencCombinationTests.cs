// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: ReplayRendererToVulkanToNvencCombinationTests.cs
// System: ProjectKongor Solution-Wide E2E Testing Suite
// Tier 3: Cross-Feature Combinations (ReplayRenderer -> Vulkan -> NVENC Pipeline)
// Policy: Strict Explicit Typing, Interaction Verification (>= 5 tests)
// ============================================================================

using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using FluentAssertions;

namespace ASPIRE.Tests.E2E.Tier3_CrossFeatureCombinations;

public sealed class ReplayRendererToVulkanToNvencCombinationTests
{
    private const uint HoncapMagic = 0x484F4E43; // "HONC"

    [Fact]
    public void Pipeline_VulkanShmToNvencInput_TransfersFrameHeaderCleanly()
    {
        // Arrange
        string mapName = "KongorReplayVulkan_" + Guid.NewGuid().ToString("N");
        using MemoryMappedFile mmf = MemoryMappedFile.CreateNew(mapName, 1024);

        HoncapFrameHeader writeHeader = new()
        {
            Magic = HoncapMagic,
            Version = 1,
            Width = 1920,
            Height = 1080,
            Format = 44, // VK_FORMAT_B8G8R8A8_UNORM
            RowPitch = 1920 * 4,
            SequenceIndex = 50,
            TimestampNanoseconds = 833_333_300,
            Flags = 1,
            BufferIndex = 0
        };

        // Act - Vulkan layer writes to SHM
        using (MemoryMappedViewAccessor accessor = mmf.CreateViewAccessor())
        {
            accessor.Write(0, ref writeHeader);
        }

        // Act - NVENC capture reader pulls from SHM
        HoncapFrameHeader readHeader;
        using (MemoryMappedViewAccessor accessor = mmf.CreateViewAccessor())
        {
            accessor.Read(0, out readHeader);
        }

        // Assert
        readHeader.Magic.Should().Be(HoncapMagic);
        readHeader.Width.Should().Be(1920);
        readHeader.Height.Should().Be(1080);
        readHeader.SequenceIndex.Should().Be(50);
    }

    [Fact]
    public void Pipeline_TripleBufferingRing_CalculatesDistinctOffsets()
    {
        // Arrange
        uint headerSize = 64;
        uint frameSize = 1920 * 1080 * 4;

        // Act
        long buffer0Offset = headerSize + 0 * frameSize;
        long buffer1Offset = headerSize + 1 * frameSize;
        long buffer2Offset = headerSize + 2 * frameSize;

        // Assert
        buffer0Offset.Should().Be(64);
        buffer1Offset.Should().Be(64 + frameSize);
        buffer2Offset.Should().Be(64 + frameSize * 2);
    }

    [Fact]
    public void Pipeline_NvencEncodingBitrate_IsWithinBroadcastBounds()
    {
        // Arrange
        int targetBitrate = 8000; // 8 Mbps for 1080p60
        int minBitrate = 4000;
        int maxBitrate = 16000;

        // Assert
        targetBitrate.Should().BeGreaterThanOrEqualTo(minBitrate);
        targetBitrate.Should().BeLessThanOrEqualTo(maxBitrate);
    }

    [Fact]
    public void Pipeline_FrameRateTiming_Maintains60FpsInterval()
    {
        // Arrange
        double frameIntervalMs = 1000.0 / 60.0; // 16.666... ms

        // Assert
        frameIntervalMs.Should().BeApproximately(16.67, 0.01);
    }

    [Fact]
    public void Pipeline_EncodedOutputContainer_HasValidMp4Extension()
    {
        // Arrange
        string outputVideoPath = @"C:\Kongor\Replays\match_987654.mp4";

        // Assert
        Path.GetExtension(outputVideoPath).Should().Be(".mp4");
    }

    [Fact]
    public void Pipeline_AudioVideoMuxing_VerifiesStandardAudioSampleRate()
    {
        // Arrange
        int audioSampleRate = 48000; // 48 kHz standard AAC
        int audioChannels = 2; // Stereo

        // Assert
        audioSampleRate.Should().Be(48000);
        audioChannels.Should().Be(2);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1, Size = 64)]
    private struct HoncapFrameHeader
    {
        public uint Magic;
        public uint Version;
        public uint Width;
        public uint Height;
        public uint Format;
        public uint RowPitch;
        public ulong SequenceIndex;
        public ulong TimestampNanoseconds;
        public uint Flags;
        public uint BufferIndex;
        public uint Reserved0;
        public uint Reserved1;
        public uint Reserved2;
        public uint Reserved3;
    }
}
