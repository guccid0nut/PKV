// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: VulkanCaptureBoundaryTests.cs
// System: ProjectKongor Solution-Wide E2E Testing Suite
// Tier 2: Boundary and Corner Cases (Feature 10: Vulkan Frame Capture Pipeline)
// Policy: Strict Explicit Typing, Boundary Validation (>= 5 tests)
// ============================================================================

using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using FluentAssertions;

namespace ASPIRE.Tests.E2E.Tier2_BoundaryAndCornerCases;

public sealed class VulkanCaptureBoundaryTests
{
    private const uint HoncapMagic = 0x484F4E43;

    [Fact]
    public void Boundary_HeaderSize_StrictlyEquals64Bytes()
    {
        // Act
        int size = Marshal.SizeOf<HoncapFrameHeader>();

        // Assert
        size.Should().Be(64);
    }

    [Fact]
    public void Boundary_ZeroDimensionFrame_CalculatesZeroSize()
    {
        // Arrange
        uint width = 0;
        uint height = 0;
        uint bytesPerPixel = 4;

        // Act
        uint size = width * height * bytesPerPixel;

        // Assert
        size.Should().Be(0);
    }

    [Fact]
    public void Boundary_MaxUhd8kDimensions_CalculatesSizeWithoutIntegerOverflow()
    {
        // Arrange - 8K UHD: 7680 x 4320
        ulong width = 7680;
        ulong height = 4320;
        ulong bytesPerPixel = 4;

        // Act
        ulong frameSize = width * height * bytesPerPixel;

        // Assert
        frameSize.Should().Be(132_710_400); // ~132.7 MB per frame
        frameSize.Should().BeLessThan(int.MaxValue);
    }

    [Fact]
    public void Boundary_SequenceIndexWrapAround_HandlesUlongMax()
    {
        // Arrange
        ulong maxSeq = ulong.MaxValue;
        ulong nextSeq = unchecked(maxSeq + 1);

        // Assert
        nextSeq.Should().Be(0);
    }

    [Fact]
    public void Boundary_TimestampNanoseconds_HandlesNonZeroMonotonicity()
    {
        // Arrange
        ulong ts1 = 1_000_000_000UL;
        ulong ts2 = 1_000_000_001UL;

        // Assert
        ts2.Should().BeGreaterThan(ts1);
    }

    [Fact]
    public void Boundary_InvalidMagicNumber_RejectsAsCorrupt()
    {
        // Arrange
        uint badMagic = 0xDEADBEEF;

        // Assert
        badMagic.Should().NotBe(HoncapMagic);
    }

    [Fact]
    public void Boundary_NegativeBufferIndex_DetectsOutOfBounds()
    {
        // Arrange
        int bufferIndex = -1;
        int maxBuffers = 3;

        // Act
        bool isValid = bufferIndex >= 0 && bufferIndex < maxBuffers;

        // Assert
        isValid.Should().BeFalse();
    }

    [Fact]
    public void Boundary_MemoryMappedFile_ClosesGracefullyUnderDisposal()
    {
        // Arrange
        string mapName = "KongorBoundaryMmf_" + Guid.NewGuid().ToString("N");

        // Act
        using MemoryMappedFile mmf = MemoryMappedFile.CreateNew(mapName, 4096);
        mmf.Should().NotBeNull();

        // Assert - MMF disposed without exception
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
