// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: ReplayStudioServiceTests.cs
// System: Replay Studio Desktop GUI Tool
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using FluentAssertions;
using ReplayRenderer.Service.Replay;
using ReplayStudio.GUI.Engine;
using ReplayStudio.GUI.Models;
using ReplayStudio.GUI.Services;
using Xunit;

namespace ReplayStudio.Tests;

public sealed class ReplayStudioServiceTests
{
    [Fact]
    public void LoadMetadata_ValidReplayMetadata_PopulatesRosterAndSimulationKeyframes()
    {
        // Arrange
        ReplaySnapshotEngine snapshotEngine = new();
        PlaybackClock clock = new();
        MapEnvironmentManager mapManager = new();
        ReplayStudioService service = new(snapshotEngine, clock, mapManager);

        ReplayMetadata metadata = new()
        {
            MatchId = "12345678",
            GameName = "Champions Cup Final",
            MapName = "caldavar",
            MatchLengthMs = 1800000, // 30 mins
            Winner = "Legion",
            FirstBloodTimeMs = 125000
        };

        // Populate 10 players
        for (int i = 0; i < 10; i++)
        {
            metadata.Players.Add(new ReplayPlayerMetadata
            {
                ClientNum = i,
                Team = i < 5 ? 1 : 2,
                TeamIndex = i % 5,
                Name = $"Player_{i + 1}",
                HeroName = i < 5 ? "Hero_Devourer" : "Hero_Pyromancer",
                Kills = 2 + i,
                Deaths = 1,
                Assists = 5,
                GoldPerMinute = 350 + i * 10
            });
        }

        bool eventRaised = false;
        service.ReplayLoaded += m => eventRaised = true;

        // Act
        bool loaded = service.LoadMetadata(metadata);

        // Assert
        loaded.Should().BeTrue();
        eventRaised.Should().BeTrue();
        service.IsReplayLoaded.Should().BeTrue();
        service.CurrentMetadata.Should().Be(metadata);

        // Check Clock
        clock.TotalDurationMs.Should().Be(1800000);
        clock.CurrentTimeMs.Should().Be(0);

        // Check Roster
        service.Roster.Count.Should().Be(10);
        service.Roster[0].PlayerName.Should().Be("Player_1");
        service.Roster[0].CleanHeroName.Should().Be("Devourer");
        service.Roster[0].Team.Should().Be(1);
        service.Roster[0].TeamName.Should().Be("Legion");
        service.Roster[0].IsPovLocked.Should().BeTrue();

        service.Roster[5].PlayerName.Should().Be("Player_6");
        service.Roster[5].CleanHeroName.Should().Be("Pyromancer");
        service.Roster[5].Team.Should().Be(2);
        service.Roster[5].TeamName.Should().Be("Hellbourne");

        // Check Snapshot Engine Keyframes
        snapshotEngine.KeyframeCount.Should().BeGreaterThan(180);
        GameStateSnapshot snap0 = snapshotEngine.SeekNearestKeyframe(0);
        snap0.Heroes.Length.Should().Be(10);
        snap0.Heroes[0].PlayerName.Should().Be("Player_1");

        // First Blood keyframe check
        GameStateSnapshot fbSnap = snapshotEngine.SeekNearestKeyframe(125000);
        fbSnap.TimestampMs.Should().Be(125000);
        fbSnap.KeyframeTag.Should().Be("First Blood");
        fbSnap.ActiveEvents.Should().ContainSingle(e => e.EventType == CombatEventType.FirstBlood);

        // Verification of fallback estimation flags
        service.HasGenuineTelemetry.Should().BeFalse();
        snap0.IsEstimatedFallback.Should().BeTrue();
        snap0.Heroes[0].IsEstimatedFallback.Should().BeTrue();
        fbSnap.IsEstimatedFallback.Should().BeTrue();
    }

    [Fact]
    public void TryParseKgkfBinary_ValidMagicAndCrc32_ExtractsKeyframesWithGenuineTelemetry()
    {
        // Arrange: Build valid KGKF binary with players array
        string json = "{\"gamePhase\":7,\"serverTick\":2400,\"matchId\":998877,\"uptimeMs\":120000,\"connectedClients\":10,\"port\":11235," +
                      "\"players\":[{\"slot\":0,\"team\":1,\"hero\":\"Hero_Devourer\",\"name\":\"P1\",\"x\":1500.5,\"y\":2500.25,\"z\":120.0,\"health\":950.0,\"maxHealth\":1000.0,\"level\":5,\"heading\":90.0,\"isAlive\":true}]}";
        byte[] payloadBytes = System.Text.Encoding.UTF8.GetBytes(json);
        uint crc = ReplayStudioService.ComputeCrc32(payloadBytes);

        byte[] binary = new byte[40 + payloadBytes.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(binary.AsSpan(0, 4), 0x4B474B46); // "KGKF"
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(binary.AsSpan(4, 4), 1);          // Version
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(binary.AsSpan(8, 4), 42);         // Sequence
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(binary.AsSpan(12, 4), 2400);      // Tick
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(binary.AsSpan(16, 8), 998877);    // MatchId
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(binary.AsSpan(24, 8), DateTimeOffset.UtcNow.UtcTicks);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(binary.AsSpan(32, 4), (uint)payloadBytes.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(binary.AsSpan(36, 4), crc);
        payloadBytes.CopyTo(binary, 40);

        // Act
        bool success = ReplayStudioService.TryParseKgkfBinary(binary, out List<GameStateSnapshot> keyframes);

        // Assert
        success.Should().BeTrue();
        keyframes.Should().ContainSingle();
        GameStateSnapshot frame = keyframes[0];
        frame.IsEstimatedFallback.Should().BeFalse();
        frame.KeyframeTag.Should().Contain("KGKF Seq 42");

        HeroSnapshot h0 = frame.Heroes[0];
        h0.ClientIndex.Should().Be(0);
        h0.Team.Should().Be(1);
        h0.HeroName.Should().Be("Hero_Devourer");
        h0.PlayerName.Should().Be("P1");
        h0.Position.X.Should().BeApproximately(1500.5f, 0.01f);
        h0.Position.Y.Should().BeApproximately(2500.25f, 0.01f);
        h0.Position.Z.Should().BeApproximately(120.0f, 0.01f);
        h0.Health.Should().Be(950.0f);
        h0.Level.Should().Be(5);
        h0.IsEstimatedFallback.Should().BeFalse();
    }

    [Fact]
    public void TryParseKgkfBinary_CorruptedCrc32_RejectsKeyframe()
    {
        // Arrange
        string json = "{\"gamePhase\":7,\"serverTick\":100,\"players\":[]}";
        byte[] payloadBytes = System.Text.Encoding.UTF8.GetBytes(json);
        uint crc = ReplayStudioService.ComputeCrc32(payloadBytes);

        byte[] binary = new byte[40 + payloadBytes.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(binary.AsSpan(0, 4), 0x4B474B46);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(binary.AsSpan(4, 4), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(binary.AsSpan(8, 4), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(binary.AsSpan(12, 4), 100);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(binary.AsSpan(16, 8), 12345);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(binary.AsSpan(24, 8), DateTimeOffset.UtcNow.UtcTicks);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(binary.AsSpan(32, 4), (uint)payloadBytes.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(binary.AsSpan(36, 4), crc ^ 0xFFFFFFFF); // Corrupt CRC
        payloadBytes.CopyTo(binary, 40);

        // Act
        bool success = ReplayStudioService.TryParseKgkfBinary(binary, out List<GameStateSnapshot> keyframes);

        // Assert
        success.Should().BeFalse();
        keyframes.Should().BeEmpty();
    }

    [Fact]
    public void ExtractKeyframesFromReplayData_ValidS2R2Coordinates_MarksKeyframesAsEstimatedFallback()
    {
        // Arrange: S2R2 replaydata header + 3D coordinates (12000, 8000, 100)
        byte[] data = new byte[32];
        System.Text.Encoding.ASCII.GetBytes("S2R2").CopyTo(data, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4, 4), 1); // Version 1
        BitConverter.GetBytes(12000.0f).CopyTo(data, 8);
        BitConverter.GetBytes(8000.0f).CopyTo(data, 12);
        BitConverter.GetBytes(100.0f).CopyTo(data, 16);

        ReplayMetadata metadata = new()
        {
            MatchId = "5555",
            GameName = "S2R2 Test",
            MapName = "caldavar",
            MatchLengthMs = 30000
        };

        // Act
        List<GameStateSnapshot> frames = ReplayStudioService.ExtractKeyframesFromReplayData(data, metadata);

        // Assert
        frames.Should().NotBeEmpty();
        frames[0].IsEstimatedFallback.Should().BeTrue();
        frames[0].Heroes[0].IsEstimatedFallback.Should().BeTrue();
        frames[0].KeyframeTag.Should().Contain("Estimated S2R2 Keyframe");
        frames[0].Heroes[0].Position.X.Should().BeInRange(-12f, 12f);
    }

    [Fact]
    public void ExtractKeyframesFromReplayData_InvalidMagic_ReturnsEmptyKeyframes()
    {
        // Arrange: Invalid magic "BAD1"
        byte[] data = new byte[32];
        System.Text.Encoding.ASCII.GetBytes("BAD1").CopyTo(data, 0);

        ReplayMetadata metadata = new()
        {
            MatchId = "5555",
            MatchLengthMs = 30000
        };

        // Act
        List<GameStateSnapshot> frames = ReplayStudioService.ExtractKeyframesFromReplayData(data, metadata);

        // Assert
        frames.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0xFFFFFFFFu)]
    [InlineData(0xFFFFFFF0u)]
    [InlineData(0xFFFFFFD8u)]
    [InlineData(0x80000000u)]
    [InlineData(0x7FFFFFFFu)]
    [InlineData(65537u)]
    [InlineData(1000000u)]
    public void TryParseKgkfBinary_AdversarialPayloadSizes_RejectsWithoutException(uint maliciousPayloadSize)
    {
        // Arrange: Valid magic "KGKF" but maliciously crafted payloadSize
        byte[] binary = new byte[40];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(binary.AsSpan(0, 4), 0x4B474B46); // "KGKF"
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(binary.AsSpan(4, 4), 1);          // Version
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(binary.AsSpan(8, 4), 1);          // Sequence
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(binary.AsSpan(12, 4), 100);       // Tick
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(binary.AsSpan(16, 8), 12345);     // MatchId
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(binary.AsSpan(24, 8), DateTimeOffset.UtcNow.UtcTicks);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(binary.AsSpan(32, 4), maliciousPayloadSize);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(binary.AsSpan(36, 4), 0x12345678);

        // Act & Assert
        try
        {
            bool success = ReplayStudioService.TryParseKgkfBinary(binary, out List<GameStateSnapshot> keyframes);
            success.Should().BeFalse();
            keyframes.Should().BeEmpty();
        }
        catch (System.Exception ex)
        {
            Assert.Fail($"Adversarial payload size {maliciousPayloadSize} caused unhandled exception: {ex}");
        }
    }

    [Fact]
    public void TryParseKgkfBinary_FuzzRandomMutations_NeverThrows()
    {
        System.Random rng = new(1337);
        byte[] buffer = new byte[256];

        for (int iteration = 0; iteration < 500; iteration++)
        {
            rng.NextBytes(buffer);

            if (iteration % 2 == 0)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), 0x4B474B46);
            }

            int testLen = rng.Next(0, buffer.Length + 1);
            ReadOnlySpan<byte> slice = buffer.AsSpan(0, testLen);

            try
            {
                ReplayStudioService.TryParseKgkfBinary(slice, out List<GameStateSnapshot> _);
            }
            catch (System.Exception ex)
            {
                Assert.Fail($"Random fuzz input at iteration {iteration} threw exception: {ex}");
            }
        }
    }
}
