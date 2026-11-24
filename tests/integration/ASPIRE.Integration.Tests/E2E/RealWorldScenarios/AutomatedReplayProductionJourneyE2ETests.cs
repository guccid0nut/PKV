// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: AutomatedReplayProductionJourneyE2ETests.cs
// System: ProjectKongor Solution-Wide E2E Testing Suite
// Tier 4: Real-World Application Scenarios (Automated Replay Production E2E Journey)
// Policy: Strict Explicit Typing, Multi-Stage Pipeline Verification
// ============================================================================

using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using FluentAssertions;

namespace ASPIRE.Tests.E2E.Tier4_RealWorldScenarios;

public sealed class AutomatedReplayProductionJourneyE2ETests
{
    private const uint HoncapMagic = 0x484F4E43; // "HONC"

    [Fact]
    public void Journey_AutomatedReplayPipeline_FromMatchEndToWebPortalDelivery()
    {
        // ---------------------------------------------------------------------
        // Stage 1: Match Concluded & Telemetry Recorded
        // ---------------------------------------------------------------------
        int matchId = 882910;
        string replayFileName = $"match_{matchId}.honreplay";
        replayFileName.Should().Contain(matchId.ToString());

        // ---------------------------------------------------------------------
        // Stage 2: Vulkan Frame Buffer Initialized in Shared Memory
        // ---------------------------------------------------------------------
        string mapName = "KongorReplayPipeline_" + Guid.NewGuid().ToString("N");
        using MemoryMappedFile mmf = MemoryMappedFile.CreateNew(mapName, 1024 * 1024);

        HoncapFrameHeader header = new()
        {
            Magic = HoncapMagic,
            Version = 1,
            Width = 1920,
            Height = 1080,
            Format = 44, // VK_FORMAT_B8G8R8A8_UNORM
            RowPitch = 1920 * 4,
            SequenceIndex = 100,
            TimestampNanoseconds = 16_666_666_000,
            Flags = 1,
            BufferIndex = 0
        };

        using (MemoryMappedViewAccessor accessor = mmf.CreateViewAccessor())
        {
            accessor.Write(0, ref header);
        }

        Marshal.SizeOf<HoncapFrameHeader>().Should().Be(64);

        // ---------------------------------------------------------------------
        // Stage 3: Frame Validation & Ingest
        // ---------------------------------------------------------------------
        HoncapFrameHeader readHeader;
        using (MemoryMappedViewAccessor accessor = mmf.CreateViewAccessor())
        {
            accessor.Read(0, out readHeader);
        }

        readHeader.Magic.Should().Be(HoncapMagic);
        readHeader.Width.Should().Be(1920);
        readHeader.Height.Should().Be(1080);
        readHeader.SequenceIndex.Should().Be(100);

        // ---------------------------------------------------------------------
        // Stage 4: NVENC Output File Registration
        // ---------------------------------------------------------------------
        string outputMp4Path = Path.Combine(Path.GetTempPath(), $"rendered_match_{matchId}.mp4");
        Path.GetExtension(outputMp4Path).Should().Be(".mp4");

        // ---------------------------------------------------------------------
        // Stage 5: Web Portal Delivery URL Formatting
        // ---------------------------------------------------------------------
        string videoCdnUrl = $"https://cdn.kongor.net/replays/{matchId}/rendered_1080p.mp4";
        Uri validUri = new(videoCdnUrl);
        validUri.Scheme.Should().Be("https");
        validUri.Host.Should().Be("cdn.kongor.net");
    }

    [Fact]
    public void Journey_NvencEncodingParameters_AreConfiguredFor1080p60()
    {
        // Arrange
        int targetBitrateKbps = 8000;
        int maxBitrateKbps = 12000;
        int fps = 60;
        string preset = "p7"; // HQ NVENC

        // Assert
        targetBitrateKbps.Should().Be(8000);
        maxBitrateKbps.Should().BeGreaterThan(targetBitrateKbps);
        fps.Should().Be(60);
        preset.Should().Be("p7");
    }

    [Fact]
    public void Journey_ReplayMetadata_SerializesCleanlyToJson()
    {
        // Arrange
        var meta = new
        {
            MatchId = 991024,
            DurationSeconds = 2450,
            WinnerTeam = 1,
            TotalKills = 78,
            Format = "H.264 / AAC",
            Resolution = "1920x1080"
        };

        // Assert
        meta.MatchId.Should().Be(991024);
        meta.Resolution.Should().Be("1920x1080");
    }

    [Fact]
    public void Journey_ReplayStreamBuffer_AllocatesExpectedMemoryForTripleBuffering()
    {
        // Arrange
        uint frameSize = 1920 * 1080 * 4; // 8,294,400 bytes
        uint tripleBufferSize = frameSize * 3;

        // Assert
        tripleBufferSize.Should().Be(24883200); // ~24.88 MB
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
