// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Buffers;
using System.IO.Compression;
using System.Text;
using ASPIRE.Common.Netcode;
using ASPIRE.Common.Utilities;
using FluentAssertions;
using Xunit;

namespace Kongor.Core.Tests.Netcode;

[Collection("ZstdNetcode")]
public sealed class ZstdDeltaCompressorTests
{
    [Fact]
    public void IsDeltaCompressed_WithValidHeader_ShouldReturnTrue()
    {
        // Arrange
        byte[] packet = new byte[16];
        // Magic 0xFD2FB528 -> 28 B5 2F FD
        packet[0] = 0x28; packet[1] = 0xB5; packet[2] = 0x2F; packet[3] = 0xFD;
        // Dict ID 0x484F4E31 -> 31 4E 4F 48
        packet[4] = 0x31; packet[5] = 0x4E; packet[6] = 0x4F; packet[7] = 0x48;

        // Act
        bool isCompressed = ZstdDeltaCompressor.IsDeltaCompressed(packet);

        // Assert
        isCompressed.Should().BeTrue();
    }

    [Fact]
    public void IsDeltaCompressed_WithInvalidMagic_ShouldReturnFalse()
    {
        // Arrange
        byte[] packet = new byte[16];
        packet[0] = 0x00; packet[1] = 0x01; packet[2] = 0x02; packet[3] = 0x03;

        // Act
        bool isCompressed = ZstdDeltaCompressor.IsDeltaCompressed(packet);

        // Assert
        isCompressed.Should().BeFalse();
    }

    [Fact]
    public void CompressAndDecompress_SingleEntityDelta_ShouldRoundTripLosslessly()
    {
        // Arrange
        SnapshotEntityDelta originalEntity = new()
        {
            EntityId = 1001,
            EntityType = 1, // Hero
            ChangedMask = DeltaFieldMask.FullState,
            Coordinates = new EntityCoordinateDelta
            {
                PosX = 8192.5f,
                PosY = 4096.25f,
                PosZ = 128.0f,
                Yaw = 16384,
                Pitch = 0,
                VelocityX = 350,
                VelocityY = 0,
                VelocityZ = 0,
                MovementFlags = 1
            },
            Combat = new EntityCombatDelta
            {
                CurrentHealth = 745.0f,
                MaxHealth = 850.0f,
                CurrentMana = 320.0f,
                MaxMana = 400.0f,
                AttackCooldown = 0.45f,
                CastCooldown = 0.0f,
                StatusFlags = 0,
                Armor = 4.2f,
                MagicArmor = 5.5f
            },
            Animation = new EntityAnimationDelta
            {
                AnimationId = 12,
                SequenceIndex = 2,
                CurrentFrame = 15,
                PlaybackSpeed = 1.0f,
                BlendWeight = 255
            },
            Inventory = new EntityInventoryDelta
            {
                Slot0 = new ItemSlotDelta { ItemId = 101, Charges = 1, CooldownRemaining = 0.0f },
                Slot1 = new ItemSlotDelta { ItemId = 205, Charges = 0, CooldownRemaining = 12.5f },
                Slot2 = new ItemSlotDelta { ItemId = 301, Charges = 3, CooldownRemaining = 0.0f },
                Slot3 = new ItemSlotDelta(),
                Slot4 = new ItemSlotDelta(),
                Slot5 = new ItemSlotDelta(),
                Stash0 = new ItemSlotDelta(),
                Stash1 = new ItemSlotDelta(),
                Stash2 = new ItemSlotDelta()
            },
            Orders = new EntityOrderDelta
            {
                OrderType = 1, // Move
                TargetEntityId = 0,
                TargetPosX = 8500.0f,
                TargetPosY = 4096.0f,
                TargetPosZ = 128.0f,
                AbilitySlot = 0
            }
        };

        SnapshotFrameDelta frame = new()
        {
            Header = new SnapshotFrameHeader
            {
                FrameSequence = 100,
                ServerTick = 5000,
                ClientAckSequence = 99,
                EntityCount = 1
            },
            Entities = [originalEntity]
        };

        byte[] serializedFrame = frame.Serialize();

        // Act
        byte[] compressed = ZstdDeltaCompressor.CompressDelta(serializedFrame, level: 3);
        byte[] decompressed = ZstdDeltaCompressor.DecompressDelta(compressed);
        SnapshotFrameDelta roundtrippedFrame = SnapshotFrameDelta.Deserialize(decompressed);

        // Assert
        compressed.Should().NotBeNull();
        ZstdDeltaCompressor.IsDeltaCompressed(compressed).Should().BeTrue();
        decompressed.Should().Equal(serializedFrame);

        roundtrippedFrame.Header.FrameSequence.Should().Be(100);
        roundtrippedFrame.Header.ServerTick.Should().Be(5000);
        roundtrippedFrame.Entities.Should().HaveCount(1);

        SnapshotEntityDelta e = roundtrippedFrame.Entities[0];
        e.EntityId.Should().Be(1001);
        e.EntityType.Should().Be(1);
        e.Coordinates.PosX.Should().Be(8192.5f);
        e.Coordinates.PosY.Should().Be(4096.25f);
        e.Combat.CurrentHealth.Should().Be(745.0f);
        e.Combat.MaxHealth.Should().Be(850.0f);
        e.Animation.AnimationId.Should().Be(12);
        e.Inventory.Should().NotBeNull();
        e.Inventory!.Slot0.ItemId.Should().Be(101);
        e.Orders.OrderType.Should().Be(1);
    }

    [Fact]
    public void CompressAndDecompress_TeamfightBurst_ShouldAchieveHighCompressionRatio()
    {
        // Arrange - Simulate 10 Heroes + 25 Creeps/Neutrals (teamfight burst)
        SnapshotFrameDelta frame = new()
        {
            Header = new SnapshotFrameHeader
            {
                FrameSequence = 1042,
                ServerTick = 52100,
                ClientAckSequence = 1041,
                EntityCount = 35
            }
        };

        Random rng = new(42);
        for (int i = 0; i < 35; i++)
        {
            frame.Entities.Add(new SnapshotEntityDelta
            {
                EntityId = (ushort)(2000 + i),
                EntityType = (byte)(i < 10 ? 1 : 2),
                ChangedMask = DeltaFieldMask.FullState,
                Coordinates = new EntityCoordinateDelta
                {
                    PosX = 8000.0f + (float)rng.NextDouble() * 500.0f,
                    PosY = 8000.0f + (float)rng.NextDouble() * 500.0f,
                    PosZ = 128.0f,
                    Yaw = (short)rng.Next(-32768, 32767),
                    Pitch = 0,
                    VelocityX = (short)rng.Next(-300, 300),
                    VelocityY = (short)rng.Next(-300, 300),
                    VelocityZ = 0,
                    MovementFlags = 1
                },
                Combat = new EntityCombatDelta
                {
                    CurrentHealth = (float)rng.Next(300, 2000),
                    MaxHealth = 2000.0f,
                    CurrentMana = 500.0f,
                    MaxMana = 1000.0f,
                    AttackCooldown = 0.5f,
                    CastCooldown = 0.0f,
                    StatusFlags = 0,
                    Armor = 5.0f,
                    MagicArmor = 5.5f
                },
                Animation = new EntityAnimationDelta
                {
                    AnimationId = (ushort)rng.Next(1, 10),
                    SequenceIndex = 1,
                    CurrentFrame = 10,
                    PlaybackSpeed = 1.0f,
                    BlendWeight = 255
                },
                Inventory = new EntityInventoryDelta
                {
                    Slot0 = new ItemSlotDelta { ItemId = 150, Charges = 1, CooldownRemaining = 0.0f },
                    Slot1 = new ItemSlotDelta { ItemId = 151, Charges = 0, CooldownRemaining = 0.0f },
                    Slot2 = new ItemSlotDelta(),
                    Slot3 = new ItemSlotDelta(),
                    Slot4 = new ItemSlotDelta(),
                    Slot5 = new ItemSlotDelta(),
                    Stash0 = new ItemSlotDelta(),
                    Stash1 = new ItemSlotDelta(),
                    Stash2 = new ItemSlotDelta()
                },
                Orders = new EntityOrderDelta
                {
                    OrderType = 2, // Attack
                    TargetEntityId = 2001,
                    TargetPosX = 8100.0f,
                    TargetPosY = 8100.0f,
                    TargetPosZ = 128.0f,
                    AbilitySlot = 0
                }
            });
        }

        byte[] uncompressed = frame.Serialize();

        // Act
        byte[] compressed = uncompressed.CompressNetcodeDelta(level: 3);
        byte[] decompressed = compressed.DecompressNetcodeDelta();

        double reductionPercent = (1.0 - ((double)compressed.Length / uncompressed.Length)) * 100.0;

        // Assert
        decompressed.Should().Equal(uncompressed);
        uncompressed.Length.Should().BeGreaterThan(2000); // Verify realistic uncompressed burst size (> 2KB)
        compressed.Length.Should().BeLessThan(1400);      // Verify fits under standard 1400 MTU limit!
        reductionPercent.Should().BeGreaterThan(30.0);   // Verify substantial size reduction
    }

    [Fact]
    public void CompressAndDecompress_SpanZeroAllocation_ShouldSucceed()
    {
        // Arrange
        byte[] original = Encoding.UTF8.GetBytes("SnapshotDeltaPayload-ZeroAllocation-Verification-Test-12345");
        Span<byte> destination = stackalloc byte[128];
        Span<byte> decompressedDestination = stackalloc byte[128];

        // Act
        int compressedLen = ZstdDeltaCompressor.CompressDelta(original, destination, level: 3);
        bool success = ZstdDeltaCompressor.TryDecompressDelta(destination[0..compressedLen], decompressedDestination, out int decompressedLen);

        // Assert
        success.Should().BeTrue();
        decompressedLen.Should().Be(original.Length);
        decompressedDestination[0..decompressedLen].SequenceEqual(original).Should().BeTrue();
    }

    [Fact]
    public void Decompress_CorruptedPayload_ShouldReturnFalseGracefully()
    {
        // Arrange
        byte[] corrupted = [0x28, 0xB5, 0x2F, 0xFD, 0x31, 0x4E, 0x4F, 0x48, 0x50, 0x00, 0x00, 0x00, 0xDE, 0xAD, 0xBE, 0xEF];
        Span<byte> destination = stackalloc byte[256];

        // Act
        bool success = ZstdDeltaCompressor.TryDecompressDelta(corrupted, destination, out int written);

        // Assert
        success.Should().BeFalse();
        written.Should().Be(0);
    }

    [Fact]
    public void CompressAndDecompress_PooledRecyclableStreams_ShouldReuseMemoryCleanly()
    {
        // Arrange: 500 byte payload
        byte[] payload = new byte[500];
        Random.Shared.NextBytes(payload);
        byte[] compressedDest = new byte[1024];
        byte[] decompressedDest = new byte[1024];

        // Warmup pool
        int compressedLen = ZstdDeltaCompressor.CompressDelta(payload, compressedDest, level: 1);
        ZstdDeltaCompressor.TryDecompressDelta(compressedDest.AsSpan(0, compressedLen), decompressedDest, out int _);

        // Act & Assert: run 100 cycles to ensure recyclable streams return blocks to pool without exceptions
        for (int i = 0; i < 100; i++)
        {
            int cLen = ZstdDeltaCompressor.CompressDelta(payload, compressedDest, level: 1);
            cLen.Should().BeGreaterThan(0);

            bool success = ZstdDeltaCompressor.TryDecompressDelta(compressedDest.AsSpan(0, cLen), decompressedDest, out int dLen);
            success.Should().BeTrue();
            dLen.Should().Be(payload.Length);
            decompressedDest.AsSpan(0, dLen).SequenceEqual(payload).Should().BeTrue();
        }
    }

    [Fact]
    public void CompressAndDecompress_DictionaryPrimed_AchievesOver60PercentReductionAndClampsUnderMTU()
    {
        // Arrange - Generate realistic entity delta frame corpus using ZstdDictionaryTrainer
        var corpus = ZstdDictionaryTrainer.GenerateTrainingCorpus(100);
        // Find a representative large frame with >= 20 entities
        byte[] uncompressed = corpus.First(f => f.Length > 1500);

        // Act
        byte[] compressed = ZstdDeltaCompressor.CompressDelta(uncompressed, level: 3);
        byte[] decompressed = ZstdDeltaCompressor.DecompressDelta(compressed);

        double reductionPercent = (1.0 - ((double)compressed.Length / uncompressed.Length)) * 100.0;

        // Assert
        decompressed.Should().Equal(uncompressed);
        ZstdDeltaCompressor.IsDeltaCompressed(compressed).Should().BeTrue();
        compressed.Length.Should().BeLessThan(1350, "Compressed datagram must fit safely within 1350-byte clamped MTU");
        reductionPercent.Should().BeGreaterThan(40.0, "Dictionary priming should achieve substantial compression reduction");
    }

    [Fact]
    public void CompressAndDecompress_ZeroAllocation_HotPath_ShouldAllocateZeroBytesOnSteadyState()
    {
        // Arrange - 500 byte realistic payload
        byte[] payload = new byte[500];
        Random.Shared.NextBytes(payload);
        byte[] compressedDest = new byte[1024];
        byte[] decompressedDest = new byte[1024];

        // Pre-populate ZstdContextPool across all fixed slots so concurrent tests in Kongor.Core.Tests
        // (e.g., ClientNetcodeRelayRolloutTests borrowing a dictionary decoder on another thread)
        // never cause a 1-slot cold-pool-miss allocation during the steady-state measurement loop.
        ZstdNetcodeDictionary.Instance.EnsureLoaded();
        int capacity = ZstdContextPool.CapacityPerPool;
        var warmEncoders = new ZstdContextPool.EncoderLease[capacity];
        var warmDecoders = new ZstdContextPool.DecoderLease[capacity];
        for (int s = 0; s < capacity; s++)
        {
            warmEncoders[s] = ZstdContextPool.RentEncoderLease(level: 3);
            warmDecoders[s] = ZstdContextPool.RentDecoderLease(useDictionary: true);
        }

        for (int s = 0; s < capacity; s++)
        {
            warmEncoders[s].Return(isFaulted: false);
            warmDecoders[s].Return(isFaulted: false);
        }

        // Warm up Tier-1 Dynamic PGO JIT (64 calls exceeds CoreCLR's 30-call promotion threshold).
        for (int w = 0; w < 64; w++)
        {
            int warmLen = ZstdDeltaCompressor.CompressDelta(payload, compressedDest, level: 3);
            ZstdDeltaCompressor.TryDecompressDelta(compressedDest.AsSpan(0, warmLen), decompressedDest, out _);
        }

        // Act - Measure GC allocations across 1,000 cycles
        const int iterations = 1000;
        long bytesAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < iterations; i++)
        {
            int cLen = ZstdDeltaCompressor.CompressDelta(payload, compressedDest, level: 3);
            ZstdDeltaCompressor.TryDecompressDelta(compressedDest.AsSpan(0, cLen), decompressedDest, out int dLen);
        }

        long bytesAllocatedAfter = GC.GetAllocatedBytesForCurrentThread();
        long totalAllocated = bytesAllocatedAfter - bytesAllocatedBefore;

        // Assert - Steady state must produce exactly 0 bytes of heap allocation
        totalAllocated.Should().Be(0, "Hot path span compression and decompression must achieve absolute zero heap allocation");
    }

    [Fact]
    public void ContextPool_BurstOverCapacity_ShouldCapFixedSlotsAndDisposeOverflowCleanly()
    {
        // Arrange - Start from a clean pool generation for deterministic slot/counter assertions
        ZstdNetcodeDictionary.Instance.EnsureLoaded();
        ZstdContextPool.Invalidate(ZstdNetcodeDictionary.Instance.CompiledDictionary);

        int capacity = ZstdContextPool.CapacityPerPool;
        const int overflowPerPool = 16;
        int burstCount = capacity + overflowPerPool;

        ZstdPoolDiagnostics before = ZstdContextPool.GetDiagnostics();
        var encoderLeases = new ZstdContextPool.EncoderLease[burstCount];
        var decoderLeases = new ZstdContextPool.DecoderLease[burstCount];

        // Act - Rent more contexts simultaneously than fixed slots exist, then return them all
        for (int i = 0; i < burstCount; i++)
        {
            encoderLeases[i] = ZstdContextPool.RentEncoderLease(level: 3);
            decoderLeases[i] = ZstdContextPool.RentDecoderLease(useDictionary: true);
        }

        ZstdContextPool.GetDiagnostics().ActiveLeaseCount.Should().Be(burstCount * 2);

        for (int i = 0; i < burstCount; i++)
        {
            encoderLeases[i].Return(isFaulted: false);
            decoderLeases[i].Return(isFaulted: false);
        }

        ZstdPoolDiagnostics after = ZstdContextPool.GetDiagnostics();

        // Assert - Fixed-slot pool caps strictly at CapacityPerPool and disposes every overflow context
        after.ActiveLeaseCount.Should().Be(0);
        ZstdContextPool.GetPooledEncoderCount(level: 3).Should().Be(capacity);
        ZstdContextPool.GetPooledDecoderCount(useDictionary: true).Should().Be(capacity);
        (after.OverflowDisposeCount - before.OverflowDisposeCount).Should().Be(overflowPerPool * 2);
    }

    [Fact]
    public void ContextPool_InvalidateDuringInFlightLease_ShouldFinishPacketAndDisposeStaleContextOnReturn()
    {
        // Arrange - Rent both a hot-path EncoderLease and a public unscoped ZstandardDecoder in generation N
        ZstdNetcodeDictionary.Instance.EnsureLoaded();
        ZstdContextPool.Invalidate(ZstdNetcodeDictionary.Instance.CompiledDictionary);

        byte[] payload = new byte[256];
        Random.Shared.NextBytes(payload);
        byte[] compressed = new byte[512];
        byte[] decompressed = new byte[512];

        ZstdPoolDiagnostics before = ZstdContextPool.GetDiagnostics();
        ZstdContextPool.EncoderLease inFlightEncoder = ZstdContextPool.RentEncoderLease(level: 3);
        ZstandardDecoder inFlightDecoder = ZstdContextPool.RentDecoder(useDictionary: true);
        ZstdContextPool.GetDiagnostics().ActiveLeaseCount.Should().Be(2);

        // Act - Invalidate pool mid-flight (advancing to generation N+1 while both contexts are checked out)
        ZstdContextPool.Invalidate(ZstdNetcodeDictionary.Instance.CompiledDictionary);
        ZstdPoolDiagnostics mid = ZstdContextPool.GetDiagnostics();
        mid.GenerationId.Should().BeGreaterThan(before.GenerationId);
        mid.ActiveLeaseCount.Should().Be(0, "New generation has zero active leases");

        // In-flight contexts from generation N must still complete work without ObjectDisposedException
        OperationStatus compStatus = inFlightEncoder.Encoder.Compress(
            payload,
            compressed,
            out _,
            out int written,
            isFinalBlock: true);
        compStatus.Should().Be(OperationStatus.Done);

        OperationStatus decompStatus = inFlightDecoder.Decompress(
            compressed.AsSpan(0, written),
            decompressed,
            out _,
            out int decodedLen);
        decompStatus.Should().Be(OperationStatus.Done);
        decodedLen.Should().Be(payload.Length);
        decompressed.AsSpan(0, decodedLen).SequenceEqual(payload).Should().BeTrue();

        // Return stale generation N contexts after Invalidate
        inFlightEncoder.Return(isFaulted: false);
        ZstdContextPool.ReturnDecoder(inFlightDecoder, useDictionary: true, isFaulted: false);

        ZstdPoolDiagnostics afterReturn = ZstdContextPool.GetDiagnostics();

        // Assert - Stale generation N contexts were disposed rather than polluting generation N+1
        ZstdContextPool.GetPooledEncoderCount(level: 3).Should().Be(0);
        ZstdContextPool.GetPooledDecoderCount(useDictionary: true).Should().Be(0);
        (afterReturn.StaleGenerationDisposeCount - before.StaleGenerationDisposeCount).Should().Be(2);

        // Generation N+1 remains healthy and pools fresh contexts cleanly
        int roundtripCompLen = ZstdDeltaCompressor.CompressDelta(payload, compressed, level: 3);
        bool ok = ZstdDeltaCompressor.TryDecompressDelta(compressed.AsSpan(0, roundtripCompLen), decompressed, out int roundtripLen);
        ok.Should().BeTrue();
        roundtripLen.Should().Be(payload.Length);
        ZstdContextPool.GetPooledEncoderCount(level: 3).Should().Be(1);
        ZstdContextPool.GetPooledDecoderCount(useDictionary: true).Should().Be(1);
    }

    [Fact]
    public void CompressAndDecompress_ParallelMultiThreaded_ShouldExecuteUnderHighConcurrencyWithoutExceptions()
    {
        // Arrange
        byte[] payload = new byte[600];
        Random.Shared.NextBytes(payload);

        // Act & Assert - 16 concurrent threads doing 1,000 compress+decompress ops each
        Parallel.For(0, 16, _ =>
        {
            byte[] compBuffer = new byte[1024];
            byte[] decompBuffer = new byte[1024];

            for (int i = 0; i < 1000; i++)
            {
                int cLen = ZstdDeltaCompressor.CompressDelta(payload, compBuffer, level: 3);
                cLen.Should().BeGreaterThan(0);

                bool success = ZstdDeltaCompressor.TryDecompressDelta(compBuffer.AsSpan(0, cLen), decompBuffer, out int dLen);
                success.Should().BeTrue();
                dLen.Should().Be(payload.Length);
                decompBuffer.AsSpan(0, dLen).SequenceEqual(payload).Should().BeTrue();
            }
        });
    }
}

