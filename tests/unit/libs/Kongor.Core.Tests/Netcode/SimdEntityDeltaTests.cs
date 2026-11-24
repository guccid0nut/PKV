// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using ASPIRE.Common.Netcode;
using FluentAssertions;
using Xunit;

namespace Kongor.Core.Tests.Netcode;

public sealed class SimdEntityDeltaTests
{
    [Fact]
    public void SimdEntityDelta_HardwareTier_IsRecognizedAndValid()
    {
        string tier = SimdEntityDelta.GetActiveHardwareTier();
        tier.Should().NotBeNullOrWhiteSpace();

        if (SimdEntityDelta.IsAvx512Supported)
            tier.Should().Contain("AVX-512");
        else if (SimdEntityDelta.IsAvx2Supported)
            tier.Should().Contain("AVX2");
        else if (SimdEntityDelta.IsVector128Supported)
            tier.Should().Contain("Vector128");
        else
            tier.Should().Be("Scalar Lookup");
    }

    [Fact]
    public void SimdEntityDelta_ComponentSizes_MatchStructConstants()
    {
        SimdEntityDelta.CoordinatesSize.Should().Be(EntityCoordinateDelta.Size);
        SimdEntityDelta.CombatSize.Should().Be(EntityCombatDelta.Size);
        SimdEntityDelta.AnimationSize.Should().Be(EntityAnimationDelta.Size);
        SimdEntityDelta.InventorySize.Should().Be(EntityInventoryDelta.Size);
        SimdEntityDelta.OrdersSize.Should().Be(EntityOrderDelta.Size);
        SimdEntityDelta.BaseEntityOverhead.Should().Be(7);
    }

    [Fact]
    public void SimdEntityDelta_SingleEntitySizing_MatchesHasFlagCalculationForEveryPermutation()
    {
        // Test all 32 possible bitmask combinations
        for (uint m = 0; m < 32; m++)
        {
            DeltaFieldMask mask = (DeltaFieldMask)m;

            int expected = 7;
            if (mask.HasFlag(DeltaFieldMask.Coordinates)) expected += EntityCoordinateDelta.Size;
            if (mask.HasFlag(DeltaFieldMask.Combat)) expected += EntityCombatDelta.Size;
            if (mask.HasFlag(DeltaFieldMask.Animation)) expected += EntityAnimationDelta.Size;
            if (mask.HasFlag(DeltaFieldMask.Inventory)) expected += EntityInventoryDelta.Size;
            if (mask.HasFlag(DeltaFieldMask.Orders)) expected += EntityOrderDelta.Size;

            int actual = SimdEntityDelta.GetTotalSerializedSize(mask);
            actual.Should().Be(expected, $"Mismatch for mask {mask} ({m})");

            SnapshotEntityDelta entity = new() { ChangedMask = mask };
            entity.GetSerializedSize().Should().Be(expected);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(64)]
    [InlineData(128)]
    [InlineData(256)]
    [InlineData(1000)]
    public void SimdEntityDelta_BatchVectorSizing_MatchesScalarSumAcrossVariableLengths(int count)
    {
        DeltaFieldMask[] masks = new DeltaFieldMask[count];
        Random rng = new(1337 + count);

        int expectedTotal = 0;
        for (int i = 0; i < count; i++)
        {
            DeltaFieldMask mask = (DeltaFieldMask)(rng.Next(0, 32));
            masks[i] = mask;
            expectedTotal += SimdEntityDelta.GetTotalSerializedSize(mask);
        }

        int simdTotal = SimdEntityDelta.ComputeTotalSerializedSize(masks);
        simdTotal.Should().Be(expectedTotal, $"Batch sizing mismatch for length {count}");
    }

    [Fact]
    public void SimdEntityDelta_ActiveEntityFiltering_MatchesScalarFilter()
    {
        const int count = 250;
        DeltaFieldMask[] masks = new DeltaFieldMask[count];
        Random rng = new(999);

        List<int> expectedActive = [];
        for (int i = 0; i < count; i++)
        {
            // ~30% entities active, ~70% inactive (DeltaFieldMask.None)
            if (rng.NextDouble() < 0.3)
            {
                masks[i] = (DeltaFieldMask)rng.Next(1, 32);
                expectedActive.Add(i);
            }
            else
            {
                masks[i] = DeltaFieldMask.None;
            }
        }

        Span<int> activeIndices = stackalloc int[count];
        int activeCount = SimdEntityDelta.FilterActiveEntities(masks, activeIndices);

        activeCount.Should().Be(expectedActive.Count);
        for (int i = 0; i < activeCount; i++)
        {
            activeIndices[i].Should().Be(expectedActive[i]);
        }
    }

    [Fact]
    public void SimdEntityDelta_SnapshotFrameDelta_SerializationRoundtrip()
    {
        SnapshotFrameDelta frame = new()
        {
            Header = new SnapshotFrameHeader
            {
                FrameSequence = 100,
                ServerTick = 6000,
                ClientAckSequence = 99,
                EntityCount = 3
            },
            Entities = [
                new SnapshotEntityDelta
                {
                    EntityId = 1,
                    EntityType = 1, // Hero
                    ChangedMask = DeltaFieldMask.Coordinates | DeltaFieldMask.Combat,
                    Coordinates = new EntityCoordinateDelta { PosX = 100f, PosY = 200f, PosZ = 50f },
                    Combat = new EntityCombatDelta { CurrentHealth = 600f, MaxHealth = 600f, CurrentMana = 300f, MaxMana = 300f }
                },
                new SnapshotEntityDelta
                {
                    EntityId = 2,
                    EntityType = 2, // Creep
                    ChangedMask = DeltaFieldMask.Coordinates,
                    Coordinates = new EntityCoordinateDelta { PosX = 300f, PosY = 400f, PosZ = 10f }
                },
                new SnapshotEntityDelta
                {
                    EntityId = 3,
                    EntityType = 1, // Hero
                    ChangedMask = DeltaFieldMask.FullState,
                    Coordinates = new EntityCoordinateDelta { PosX = 500f, PosY = 600f, PosZ = 20f },
                    Combat = new EntityCombatDelta { CurrentHealth = 450f, MaxHealth = 700f },
                    Animation = new EntityAnimationDelta { AnimationId = 5, SequenceIndex = 1 },
                    Inventory = new EntityInventoryDelta { Slot0 = new ItemSlotDelta { ItemId = 42, Charges = 1 } },
                    Orders = new EntityOrderDelta { OrderType = 1, TargetPosX = 550f, TargetPosY = 650f }
                }
            ]
        };

        int expectedSize = frame.GetSerializedSize();
        byte[] bytes = frame.Serialize();
        bytes.Length.Should().Be(expectedSize);

        SnapshotFrameDelta deserialized = SnapshotFrameDelta.Deserialize(bytes);
        deserialized.Header.FrameSequence.Should().Be(100u);
        deserialized.Header.ServerTick.Should().Be(6000u);
        deserialized.Header.EntityCount.Should().Be(3);
        deserialized.Entities.Should().HaveCount(3);

        deserialized.Entities[0].EntityId.Should().Be(1);
        deserialized.Entities[0].Coordinates.PosX.Should().Be(100f);
        deserialized.Entities[0].Combat.CurrentHealth.Should().Be(600f);

        deserialized.Entities[2].EntityId.Should().Be(3);
        deserialized.Entities[2].ChangedMask.Should().Be(DeltaFieldMask.FullState);
        deserialized.Entities[2].Inventory!.Slot0.ItemId.Should().Be(42);
    }

    [Fact]
    public void SimdEntityDelta_Benchmark_10000EntitiesSizedUnder5Milliseconds()
    {
        DeltaFieldMask[] masks = new DeltaFieldMask[10_000];
        Random rng = new(42);
        for (int i = 0; i < masks.Length; i++)
        {
            masks[i] = (DeltaFieldMask)rng.Next(0, 32);
        }

        // Warmup
        _ = SimdEntityDelta.ComputeTotalSerializedSize(masks);

        Stopwatch sw = Stopwatch.StartNew();
        for (int run = 0; run < 10; run++)
        {
            _ = SimdEntityDelta.ComputeTotalSerializedSize(masks);
        }
        sw.Stop();

        // 10 runs of 10,000 entities (100,000 entities evaluated) with generous headroom for concurrent test execution
        sw.ElapsedMilliseconds.Should().BeLessThan(250);
    }
}
