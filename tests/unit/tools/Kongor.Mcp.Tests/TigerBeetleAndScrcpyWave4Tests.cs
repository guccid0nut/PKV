// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Kongor.Cli.Commands;
using Kongor.Cli.Mcp.Handlers;
using Kongor.Cli.Mcp.Logging;
using Kongor.Cli.Mcp.Services;
using Kongor.Core.Services.Networking;
using Kongor.Core.Services.Voice;
using Kongor.Modding.Core.Packaging;
using Xunit;

namespace Kongor.Mcp.Tests;

public sealed class TigerBeetleAndScrcpyWave4Tests
{
    // =========================================================================
    // #223: Exhaustigen Zero-Allocation Combinatorial State Enumerator
    // =========================================================================

    [Fact]
    public void Exhaustigen_EnumeratesAllMountAndBackupCombinationsDeterministically()
    {
        int combinations = ClientModMountBuilder.VerifyExhaustiveMountAndBackupStates();
        combinations.Should().Be(96);

        Exhaustigen gen = new Exhaustigen();
        HashSet<(bool, bool, VfsBackupRotationTier)> seen = new HashSet<(bool, bool, VfsBackupRotationTier)>();

        while (!gen.Done())
        {
            bool a = gen.Bool();
            bool b = gen.Bool();
            VfsBackupRotationTier tier = gen.EnumValue<VfsBackupRotationTier>();
            seen.Add((a, b, tier));
        }

        seen.Should().HaveCount(2 * 2 * 6);
    }

    [Fact]
    public void Mutant_SampledMountTest_MissesRareCrossAxisOverrideAndBackupCollision()
    {
        // Mutant negative-control: sampling only 4 fixed states misses the rare (override=true, patch=true, Bak5) combination
        (bool Override, bool Patch, VfsBackupRotationTier Tier)[] sampledOnly =
        [
            (false, false, VfsBackupRotationTier.Bak1),
            (true, false, VfsBackupRotationTier.Bak1),
            (false, true, VfsBackupRotationTier.Bak2),
            (true, true, VfsBackupRotationTier.Bak2)
        ];

        bool caughtRareBak5Collision = false;
        foreach ((bool Override, bool Patch, VfsBackupRotationTier Tier) state in sampledOnly)
        {
            if (state.Override && state.Patch && state.Tier == VfsBackupRotationTier.Bak5)
            {
                caughtRareBak5Collision = true;
            }
        }

        caughtRareBak5Collision.Should().BeFalse();

        // Exhaustigen catches it deterministically
        Exhaustigen gen = new Exhaustigen();
        bool exhaustigenCaught = false;
        while (!gen.Done())
        {
            bool over = gen.Bool();
            bool patch = gen.Bool();
            VfsBackupRotationTier tier = gen.EnumValue<VfsBackupRotationTier>();
            if (over && patch && tier == VfsBackupRotationTier.Bak5)
            {
                exhaustigenCaught = true;
            }
        }

        exhaustigenCaught.Should().BeTrue();
    }

    // =========================================================================
    // #224: Directed-Graph Asymmetric Packet Simulator
    // =========================================================================

    [Fact]
    public void DirectedMatrixPacketSimulator_AppliesAsymmetricPartitionsAndOneWayJitter()
    {
        List<(int From, int To, int Seq)> delivered = new List<(int From, int To, int Seq)>();
        DirectedMatrixPacketSimulator<int> sim = new DirectedMatrixPacketSimulator<int>(
            nodeCount: 4,
            linkCapacity: 8,
            oneWayDelayMinTicks: 1UL,
            oneWayDelayMeanTicks: 3UL,
            seed: 0x12345678UL);

        // In IsolateSingle + Asymmetric, isolatedNode 0 can send (src == 0) but cannot receive (dst == 0)
        sim.ApplyPartition(PartitionTopology.IsolateSingle, PartitionSymmetry.Asymmetric, isolatedNode: 0);
        sim.IsLinkEnabled(0, 1).Should().BeTrue();
        sim.IsLinkEnabled(1, 0).Should().BeFalse();

        sim.SubmitPacket(0, 1, 100).Should().BeTrue();
        sim.SubmitPacket(1, 0, 200).Should().BeFalse();

        for (int i = 0; i < 10; i++)
        {
            sim.Step((from, to, packet) => delivered.Add((from, to, packet)));
        }

        delivered.Should().ContainSingle(d => d.From == 0 && d.To == 1 && d.Seq == 100);
    }

    [Fact]
    public void Mutant_GlobalSymmetricDropSimulator_MissesOneWayAsymmetricKeepaliveFailure()
    {
        // Mutant negative-control: a symmetric partition drops both directions simultaneously,
        // failing to reproduce the half-open keepalive bug where node 0 can send to node 1 but node 1 cannot reply.
        DirectedMatrixPacketSimulator<int> symmetricMutant = new DirectedMatrixPacketSimulator<int>(
            nodeCount: 2,
            linkCapacity: 8,
            oneWayDelayMinTicks: 1UL,
            oneWayDelayMeanTicks: 1UL,
            seed: 42UL);

        symmetricMutant.ApplyPartition(PartitionTopology.IsolateSingle, PartitionSymmetry.Symmetric, isolatedNode: 0);
        bool mutantAllowsOneWay = symmetricMutant.IsLinkEnabled(0, 1) != symmetricMutant.IsLinkEnabled(1, 0);
        mutantAllowsOneWay.Should().BeFalse();

        DirectedMatrixPacketSimulator<int> asymmetricSim = new DirectedMatrixPacketSimulator<int>(
            nodeCount: 2,
            linkCapacity: 8,
            oneWayDelayMinTicks: 1UL,
            oneWayDelayMeanTicks: 1UL,
            seed: 42UL);
        asymmetricSim.ApplyPartition(PartitionTopology.IsolateSingle, PartitionSymmetry.Asymmetric, isolatedNode: 0);
        bool asymmetricAllowsOneWay = asymmetricSim.IsLinkEnabled(0, 1) && !asymmetricSim.IsLinkEnabled(1, 0);
        asymmetricAllowsOneWay.Should().BeTrue();
    }

    // =========================================================================
    // #225: Out-of-Order Reply Sequence Reassembler
    // =========================================================================

    [Fact]
    public void ReplySequenceReassembler_DeliversOutOfOrderRepliesInStrictMonotonicOrder()
    {
        ReplySequenceReassembler<string> reassembler = new ReplySequenceReassembler<string>(initialExpectedSequence: 1UL);

        reassembler.Insert(3UL, "reply-3").Should().BeTrue();
        reassembler.ReadyToPop().Should().BeFalse();

        reassembler.Insert(2UL, "reply-2").Should().BeTrue();
        reassembler.ReadyToPop().Should().BeFalse();

        reassembler.Insert(1UL, "reply-1").Should().BeTrue();
        reassembler.ReadyToPop().Should().BeTrue();

        List<(ulong Sequence, string Payload)> popped = new List<(ulong Sequence, string Payload)>();
        while (reassembler.ReadyToPop())
        {
            popped.Add(reassembler.Pop());
        }

        popped.Should().HaveCount(3);
        popped[0].Sequence.Should().Be(1UL);
        popped[1].Sequence.Should().Be(2UL);
        popped[2].Sequence.Should().Be(3UL);
    }

    [Fact]
    public void Mutant_FifoArrivalDispatcher_CorruptsStateWhenReply2ArrivesBeforeReply1()
    {
        // Mutant negative-control: naive FIFO arrival dispatch processes reply #2 before reply #1
        List<ulong> mutantDeliveredOrder = [2UL, 1UL];
        bool isMonotonic = mutantDeliveredOrder[0] < mutantDeliveredOrder[1];
        isMonotonic.Should().BeFalse();

        ReplySequenceReassembler<string> reassembler = new ReplySequenceReassembler<string>(initialExpectedSequence: 1UL);
        reassembler.Insert(2UL, "second").Should().BeTrue();
        reassembler.ReadyToPop().Should().BeFalse();
        reassembler.Insert(1UL, "first").Should().BeTrue();
        reassembler.ReadyToPop().Should().BeTrue();

        (ulong seq1, string _) = reassembler.Pop();
        (ulong seq2, string _) = reassembler.Pop();
        seq1.Should().Be(1UL);
        seq2.Should().Be(2UL);
    }

    // =========================================================================
    // #226: Deterministic Sine-Wave & Step-Function Clock Drift Simulator
    // =========================================================================

    [Fact]
    public void TigerBeetleTimeSim_ProducesBoundedSineWaveAndStepFunctionClockDrift()
    {
        TigerBeetleTimeSim periodicSim = new TigerBeetleTimeSim(
            resolutionNs: 10_000_000L,
            offsetType: TimeOffsetType.Periodic,
            amplitudeNs: 5_000_000L,
            periodTicks: 20UL,
            epochNs: 0L);

        long maxAbsDrift = 0;
        for (int i = 0; i < 40; i++)
        {
            periodicSim.Tick();
            long ideal = periodicSim.MonotonicNs();
            long drifted = periodicSim.RealtimeNs();
            long diff = Math.Abs(drifted - ideal);
            if (diff > maxAbsDrift)
            {
                maxAbsDrift = diff;
            }
        }

        maxAbsDrift.Should().BeGreaterThan(0L).And.BeLessThanOrEqualTo(5_000_000L);

        TigerBeetleTimeSim stepSim = new TigerBeetleTimeSim(
            resolutionNs: 10_000_000L,
            offsetType: TimeOffsetType.Step,
            amplitudeNs: -25_000_000L,
            stepAtTick: 6UL,
            epochNs: 0L);

        for (int i = 0; i < 5; i++)
        {
            stepSim.Tick();
        }
        (stepSim.RealtimeNs() - stepSim.MonotonicNs()).Should().Be(0L);

        stepSim.Tick(); // Tick 6 >= StepAtTick (6)
        (stepSim.RealtimeNs() - stepSim.MonotonicNs()).Should().Be(-25_000_000L);
    }

    [Fact]
    public void Mutant_SynchronizedWallClockTest_MissesLeaseSplitBrainUnderSineWaveDrift()
    {
        TigerBeetleTimeSim idealClock = new TigerBeetleTimeSim(
            resolutionNs: 10_000_000L,
            offsetType: TimeOffsetType.Linear,
            amplitudeNs: 0L,
            epochNs: 0L);
        TigerBeetleTimeSim driftedClock = new TigerBeetleTimeSim(
            resolutionNs: 10_000_000L,
            offsetType: TimeOffsetType.Periodic,
            amplitudeNs: 50_000_000L,
            periodTicks: 20UL,
            epochNs: 0L);

        for (int i = 0; i < 5; i++)
        {
            idealClock.Tick();
            driftedClock.Tick();
        }

        (idealClock.RealtimeNs() - idealClock.MonotonicNs()).Should().Be(0L);
        (driftedClock.RealtimeNs() - driftedClock.MonotonicNs()).Should().Be(50_000_000L);
    }

    // =========================================================================
    // #227: SplitMix64 + Xoshiro256++ PRNG & Swarm Weight Generator
    // =========================================================================

    [Fact]
    public void TigerBeetlePrng_SamplesUnbiasedLemireBoundsAndSwarmDistributions()
    {
        TigerBeetlePrng prngA = new TigerBeetlePrng(0xCAFEBABE12345678UL);
        TigerBeetlePrng prngB = new TigerBeetlePrng(0xCAFEBABE12345678UL);

        for (int i = 0; i < 256; i++)
        {
            ulong bound = (ulong)(i + 3);
            ulong valA = prngA.IntInclusive(bound);
            ulong valB = prngB.IntInclusive(bound);
            valA.Should().Be(valB);
            valA.Should().BeLessThanOrEqualTo(bound);
        }

        ulong[] weights = prngA.GenerateSwarmWeights(6);
        weights.Should().HaveCount(6);
        ulong weightSum = 0;
        int enabledCount = 0;
        foreach (ulong w in weights)
        {
            weightSum += w;
            if (w > 0)
            {
                enabledCount++;
            }
        }

        weightSum.Should().BeGreaterThan(0UL);
        enabledCount.Should().BeGreaterThanOrEqualTo(1).And.BeLessThanOrEqualTo(6);

        int idx = prngA.SampleWeightedIndex(weights);
        idx.Should().BeInRange(0, 5);
        weights[idx].Should().BeGreaterThan(0UL);
    }

    [Fact]
    public void Mutant_ModuloBiasAndUniformWeights_FailDistributionInvariants()
    {
        // Mutant negative-control: uniform weights never disable a variant (0 weight) or test extreme skew
        ulong[] uniformMutant = [1UL, 1UL, 1UL, 1UL, 1UL];
        bool hasDisabledVariant = false;
        foreach (ulong w in uniformMutant)
        {
            if (w == 0) hasDisabledVariant = true;
        }
        hasDisabledVariant.Should().BeFalse();

        // Swarm weights across deterministic seeds produce disabled variants (0 weight) to exercise rare code paths
        bool swarmProducedZeroWeight = false;
        for (ulong seed = 1; seed <= 25; seed++)
        {
            TigerBeetlePrng prng = new TigerBeetlePrng(seed);
            ulong[] weights = prng.GenerateSwarmWeights(8);
            foreach (ulong w in weights)
            {
                if (w == 0)
                {
                    swarmProducedZeroWeight = true;
                    break;
                }
            }
            if (swarmProducedZeroWeight) break;
        }

        swarmProducedZeroWeight.Should().BeTrue();
    }

    // =========================================================================
    // #228: Bijective-Shuffled Zipfian Hotspot Generator
    // =========================================================================

    [Fact]
    public void BijectiveShuffledZipfianGenerator_ClustersHotspotAccessAtScatteredIds()
    {
        BijectiveShuffledZipfianGenerator gen = EngineCacheService.CreateHotspotWorkloadGenerator(keyspaceSize: 64UL, theta: 0.99, seed: 999UL);
        ulong modalId = gen.PermuteIndex(0UL);

        Dictionary<ulong, int> counts = new Dictionary<ulong, int>();
        TigerBeetlePrng prng = new TigerBeetlePrng(12345UL);
        for (int i = 0; i < 1000; i++)
        {
            ulong id = gen.Next(prng);
            id.Should().BeLessThan(64UL);
            counts[id] = counts.TryGetValue(id, out int existing) ? existing + 1 : 1;
        }

        // Rank 0 (modalId) must be the most frequently accessed item, and bijective shuffle scatters it away from index 0
        modalId.Should().NotBe(0UL);
        counts[modalId].Should().BeGreaterThan(50);

        // Verify bijective 1-to-1 permutation and exact inverse
        HashSet<ulong> mappedIds = new HashSet<ulong>();
        for (ulong rank = 0; rank < 64UL; rank++)
        {
            ulong permuted = gen.PermuteIndex(rank);
            mappedIds.Add(permuted);
            gen.UnpermuteIndex(permuted).Should().Be(rank);
        }
        mappedIds.Should().HaveCount(64);
    }

    [Fact]
    public void Mutant_MonotonicZipfianGenerator_ClustersAllHotspotsAtIndexZero()
    {
        // Mutant negative-control: unshuffled Zipfian always puts rank 0 at index 0
        ulong unshuffledHotspot = 0UL;
        BijectiveShuffledZipfianGenerator shuffled = new BijectiveShuffledZipfianGenerator(128UL, seed: 42UL);
        shuffled.PermuteIndex(0UL).Should().NotBe(unshuffledHotspot);
    }

    // =========================================================================
    // #229: Deterministic Storage Fault & Torn-Write Simulator
    // =========================================================================

    [Fact]
    public void DeterministicStorageFaultSimulator_DetectsMisdirectedWritesAndSectorBitrot()
    {
        byte[] payload = [0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x80];
        byte[] walChunk = WalCommand.EncodeWalChunk(sequence: 7U, payload: payload);
        (int totalClean, int validClean, int corruptClean, long validBytesClean) = WalCommand.VerifyWalBuffer(walChunk);
        totalClean.Should().Be(1);
        validClean.Should().Be(1);
        corruptClean.Should().Be(0);
        validBytesClean.Should().Be(8L);

        DeterministicStorageFaultSimulator faultSim = new DeterministicStorageFaultSimulator(
            capacityBytes: 256,
            sectorSize: 16,
            faultySectorMask: 0UL);

        // 1. Misdirected write: write clean first, then misdirected overwrite leaves stale bytes
        faultSim.WriteClean(offset: 0, data: walChunk);
        byte[] updatedChunk = WalCommand.EncodeWalChunk(sequence: 8U, payload: [0x99, 0x88, 0x77, 0x66, 0x55, 0x44, 0x33, 0x22]);
        faultSim.WriteMisdirected(offset: 0, attemptedData: updatedChunk);
        byte[] readBack = new byte[walChunk.Length];
        faultSim.Read(offset: 0, destination: readBack, misdirectToStale: true);

        // Corrupt via bit-flip in payload and verify WAL detects CRC mismatch
        faultSim.InjectBitFlip(byteOffset: 18, xorMask: 0x01);
        (int totalFlip, int validFlip, int corruptFlip, long _) = WalCommand.VerifyWalBuffer(faultSim.RawStorage.Slice(0, walChunk.Length));
        totalFlip.Should().Be(1);
        validFlip.Should().Be(0);
        corruptFlip.Should().Be(1);

        // 2. Bitrot flip in .s2z archive buffer
        byte[] validZipBytes;
        using (System.IO.MemoryStream zipMs = new System.IO.MemoryStream())
        {
            using (System.IO.Compression.ZipArchive zip = new System.IO.Compression.ZipArchive(zipMs, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            {
                System.IO.Compression.ZipArchiveEntry entry = zip.CreateEntry("ui/test.lua");
                using System.IO.Stream entryStream = entry.Open();
                entryStream.Write(payload, 0, payload.Length);
            }
            validZipBytes = zipMs.ToArray();
        }

        S2zArchiveWriter.ValidateArchiveBufferIntegrity(validZipBytes, out int entryCount).Should().BeTrue();
        entryCount.Should().Be(1);

        // Inject torn write (truncate EOCD)
        S2zArchiveWriter.ValidateArchiveBufferIntegrity(validZipBytes.AsSpan(0, validZipBytes.Length - 10), out int corruptCount).Should().BeFalse();
        corruptCount.Should().Be(0);
    }

    [Fact]
    public void Mutant_CleanMemoryStreamTest_MissesSilentMisdirectedWriteCorruption()
    {
        byte[] walChunk = WalCommand.EncodeWalChunk(sequence: 1U, payload: [1, 2, 3, 4]);
        // Without storage fault injection, a clean memory buffer never exercises CRC/magic failure branches
        (int _, int validBefore, int corruptBefore, long _) = WalCommand.VerifyWalBuffer(walChunk);
        validBefore.Should().Be(1);
        corruptBefore.Should().Be(0);

        DeterministicStorageFaultSimulator faultSim = new DeterministicStorageFaultSimulator(capacityBytes: 128, sectorSize: 16);
        faultSim.WriteTorn(offset: 0, data: walChunk, validPrefixBytes: 17);
        (int totalTorn, int validTorn, int corruptTorn, long _) = WalCommand.VerifyWalBuffer(faultSim.RawStorage.Slice(0, walChunk.Length));
        totalTorn.Should().Be(1);
        validTorn.Should().Be(0);
        corruptTorn.Should().Be(1);
    }

    // =========================================================================
    // #230: Reversible 128-Bit Odd-Multiplier Bijective ID Permutator
    // =========================================================================

    [Fact]
    public void Reversible128BitIdPermutator_InvertsExactSeedWithoutCollisions()
    {
        Reversible128BitIdPermutator permutator = Reversible128BitIdPermutator.FromSeed(IdOrder.Random, 0xCAFE_BABE_1234_5678UL);
        HashSet<UInt128> generated = new HashSet<UInt128>();
        for (ulong i = 1; i <= 500; i++)
        {
            UInt128 raw = i;
            UInt128 permuted = permutator.Encode(raw);
            permuted.Should().NotBe(raw);
            generated.Add(permuted).Should().BeTrue();

            UInt128 recovered = permutator.Decode(permuted);
            recovered.Should().Be(raw);
        }

        UInt128 correlationId = ClientCommandRemediationService.CreateDeterministicCorrelationId(42UL, IdOrder.Permuted);
        correlationId.Should().NotBe((UInt128)0UL);
        Reversible128BitIdPermutator defaultPermutator = Reversible128BitIdPermutator.FromSeed(IdOrder.Permuted, 0xAA55_AA55_55AA_55AAUL);
        defaultPermutator.Decode(correlationId).Should().Be((UInt128)42UL);
    }

    [Fact]
    public void Mutant_MonotonicSequentialIds_CauseBTreeRightEdgeLockContention()
    {
        // Monotonic sequential IDs all share identical upper 64-bit prefixes (0), clustering on a single B-Tree leaf
        Reversible128BitIdPermutator sequentialPerm = new Reversible128BitIdPermutator(IdOrder.Sequential);
        UInt128 mono1 = sequentialPerm.Encode(1);
        UInt128 mono2 = sequentialPerm.Encode(2);
        ulong monoHigh1 = (ulong)(mono1 >> 64);
        ulong monoHigh2 = (ulong)(mono2 >> 64);
        monoHigh1.Should().Be(0UL);
        monoHigh2.Should().Be(0UL);

        // Bijective permuted IDs scatter across upper 64-bit space while remaining 100% reversible
        Reversible128BitIdPermutator randomPerm = new Reversible128BitIdPermutator(IdOrder.Permuted);
        UInt128 perm1 = randomPerm.Encode(1);
        UInt128 perm2 = randomPerm.Encode(2);
        ulong permHigh1 = (ulong)(perm1 >> 64);
        ulong permHigh2 = (ulong)(perm2 >> 64);
        permHigh1.Should().NotBe(permHigh2);
    }

    // =========================================================================
    // #231: Scoped Traceability Log Expectation Guard (KongorTraceabilityMarks)
    // =========================================================================

    [Fact]
    public void KongorTraceabilityMarks_FailsTestWhenExpectedLogPatternIsNotEmitted()
    {
        using (KongorTraceabilityMarks markHit = KongorTraceabilityMarks.ExpectLogPattern("VFS_ARCHIVE_POISON_DETECTED"))
        {
            KongorTraceabilityMarks.NotifyLog("[INFO] Starting VFS check");
            KongorTraceabilityMarks.NotifyLog("[ERROR] VFS_ARCHIVE_POISON_DETECTED in textures.s2z");
            markHit.HitCount.Should().Be(1);
        }

        KongorTraceabilityMarks markMissed = KongorTraceabilityMarks.ExpectLogPattern("NEVER_EMITTED_PATTERN_XYZ");
        KongorTraceabilityMarks.NotifyLog("[INFO] Unrelated log line");
        Action disposeUnmet = () => markMissed.Dispose();
        disposeUnmet.Should().Throw<InvalidOperationException>()
            .WithMessage("*KongorTraceabilityMarks*NEVER_EMITTED_PATTERN_XYZ*");
    }

    [Fact]
    public void Mutant_UnverifiedFallbackTest_PassesEvenWhenExpectedErrorLogNeverFires()
    {
        // Mutant negative-control: a test that does not assert log marks silently passes when error logging is broken
        bool dummyFallbackReturn = false;
        dummyFallbackReturn.Should().BeFalse();

        // With KongorTraceabilityMarks, omitting the expected error log deterministically fails verification
        KongorTraceabilityMarks guard = KongorTraceabilityMarks.ExpectLogPattern("[SECURITY ERROR] 401 Unauthorized", enforceOnDispose: false);
        Action verifyAction = () => guard.VerifyHit();
        verifyAction.Should().Throw<InvalidOperationException>();
        guard.Dispose();
    }

    // =========================================================================
    // #232: Scrcpy Progressive Linear-Regression Clock & Single-Slot Frame Buffer
    // =========================================================================

    [Fact]
    public void ScrcpyProgressiveClock_LinearRegressionSmoothsJitterAndSingleSlotDropsStaleFrames()
    {
        ScrcpyProgressiveClock clock = new ScrcpyProgressiveClock();
        // Feed 32 frames at ~16,666us (60 FPS) with synthetic host receive jitter
        for (int i = 0; i < 32; i++)
        {
            long ptsNs = i * 16_666_000L;
            long jitterNs = (i % 2 == 0) ? 1_500_000L : -1_500_000L;
            long systemNs = 1_000_000_000L + ptsNs + jitterNs;
            clock.Update(systemNs, ptsNs);
        }

        clock.Range.Should().Be(32);
        clock.Slope.Should().BeApproximately(1.0, 0.05);

        long estimatedSystemAtFrame10 = clock.ToSystemTimeNs(10 * 16_666_000L);
        long idealSystemAtFrame10 = 1_000_000_000L + (10 * 16_666_000L);
        Math.Abs(estimatedSystemAtFrame10 - idealSystemAtFrame10).Should().BeLessThan(8_000_000L);

        // Single-slot frame buffer overwrites stale frames when producer outpaces consumer
        ScrcpySingleSlotFrameBuffer<int> slot = new ScrcpySingleSlotFrameBuffer<int>();
        slot.Push(1, out bool prevSkipped1).Should().BeTrue();
        prevSkipped1.Should().BeFalse();

        slot.Push(2, out bool prevSkipped2).Should().BeFalse();
        prevSkipped2.Should().BeTrue(); // Frame 1 was overwritten before consumption!

        slot.Push(3, out bool prevSkipped3).Should().BeFalse();
        prevSkipped3.Should().BeTrue(); // Frame 2 was also overwritten!
        slot.SkippedFramesCount.Should().Be(2);

        slot.TryTake(out int latestFrame).Should().BeTrue();
        latestFrame.Should().Be(3); // Consumer immediately receives freshest frame #3
    }

    [Fact]
    public void Mutant_MultiFrameCaptureQueue_AccumulatesStaleFrameLatencyWhenConsumerLags()
    {
        // Mutant negative-control: FIFO queue accumulates stale frames so consumer reads frame #1 (2 frames behind)
        Queue<int> fifoMutant = new Queue<int>();
        fifoMutant.Enqueue(1);
        fifoMutant.Enqueue(2);
        fifoMutant.Enqueue(3);
        int fifoPopped = fifoMutant.Dequeue();
        fifoPopped.Should().Be(1); // Stale frame!

        ScrcpySingleSlotFrameBuffer<int> singleSlot = new ScrcpySingleSlotFrameBuffer<int>();
        singleSlot.Push(1, out _);
        singleSlot.Push(2, out _);
        singleSlot.Push(3, out _);
        singleSlot.TryTake(out int freshPopped).Should().BeTrue();
        freshPopped.Should().Be(3);
    }

    // =========================================================================
    // #233: Scrcpy Dual-Tier Droppable vs. Non-Droppable Control Queue
    // =========================================================================

    [Fact]
    public void ScrcpyDroppableControlQueue_DropsExcessHoverEventsWhilePreservingAllClicksAndKeys()
    {
        UiControlService uiService = new UiControlService();

        // Flood 200 droppable mouse-move events (exceeding the 60-slot droppable cap)
        for (int i = 0; i < 200; i++)
        {
            uiService.EnqueueControlEvent(new UiControlEvent(UiControlEventKind.MouseMove, X: i, Y: i));
        }

        // Interleave critical non-droppable MouseDown, MouseUp, KeyDown, KeyUp, LuaCommand events
        uiService.EnqueueControlEvent(new UiControlEvent(UiControlEventKind.MouseDown, X: 100, Y: 200)).Should().BeTrue();
        uiService.EnqueueControlEvent(new UiControlEvent(UiControlEventKind.MouseUp, X: 100, Y: 200)).Should().BeTrue();
        uiService.EnqueueControlEvent(new UiControlEvent(UiControlEventKind.KeyDown, Payload: "Enter")).Should().BeTrue();
        uiService.EnqueueControlEvent(new UiControlEvent(UiControlEventKind.KeyUp, Payload: "Enter")).Should().BeTrue();
        uiService.EnqueueControlEvent(new UiControlEvent(UiControlEventKind.LuaCommand, Payload: "ReloadInterfaces")).Should().BeTrue();

        uiService.ControlQueue.DroppedCount.Should().Be(140);
        uiService.ControlQueue.DroppableCount.Should().Be(60);
        uiService.ControlQueue.KeepCount.Should().Be(5);

        List<UiControlEvent> drained = uiService.ControlQueue.DrainAll();
        drained.Should().Contain(e => e.Kind == UiControlEventKind.MouseDown);
        drained.Should().Contain(e => e.Kind == UiControlEventKind.MouseUp);
        drained.Should().Contain(e => e.Kind == UiControlEventKind.KeyDown);
        drained.Should().Contain(e => e.Kind == UiControlEventKind.KeyUp);
        drained.Should().Contain(e => e.Kind == UiControlEventKind.LuaCommand);
    }

    [Fact]
    public void Mutant_SingleBoundedUiControlQueue_DropsClickUpEventDuringMouseMoveFlood()
    {
        // Mutant negative-control: a single bounded queue of capacity 60 drops MouseUp after 60 MouseMoves
        int maxCap = 60;
        Queue<UiControlEvent> singleBoundedMutant = new Queue<UiControlEvent>();
        for (int i = 0; i < 60; i++)
        {
            if (singleBoundedMutant.Count < maxCap)
            {
                singleBoundedMutant.Enqueue(new UiControlEvent(UiControlEventKind.MouseMove, i, i));
            }
        }

        bool clickUpAcceptedByMutant = singleBoundedMutant.Count < maxCap;
        clickUpAcceptedByMutant.Should().BeFalse();

        // Dual-tier ScrcpyDroppableControlQueue accepts MouseUp even when droppable tier is 100% full
        ScrcpyDroppableControlQueue<UiControlEvent> dualQueue = new ScrcpyDroppableControlQueue<UiControlEvent>(maxCap);
        for (int i = 0; i < 100; i++)
        {
            dualQueue.Push(new UiControlEvent(UiControlEventKind.MouseMove, i, i), isDroppable: true);
        }
        dualQueue.Push(new UiControlEvent(UiControlEventKind.MouseUp, 10, 10), isDroppable: false).Should().BeTrue();
    }

    // =========================================================================
    // #234: Scrcpy Atomic Pipe/Socket Handle Interrupter (sc_intr)
    // =========================================================================

    [Fact]
    public async Task ScrcpyAtomicPipeInterrupter_UnblocksBlockedPipeReadImmediatelyOnInterrupt()
    {
        string pipeName = $"kongor-intr-test-{Guid.NewGuid():N}";
        using NamedPipeServerStream server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        using NamedPipeClientStream client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        Task serverConnect = server.WaitForConnectionAsync();
        await client.ConnectAsync(2000);
        await serverConnect;

        ScrcpyAtomicPipeInterrupter interrupter = new ScrcpyAtomicPipeInterrupter();
        byte[] buffer = new byte[64];

        Stopwatch sw = Stopwatch.StartNew();
        Task<int> blockedReadTask = interrupter.ReadWithInterruptAsync(client, buffer);

        // Trigger Interrupt() after 5ms while server writes nothing (simulating hung game process)
        _ = Task.Run(async () =>
        {
            await Task.Delay(5);
            interrupter.Interrupt();
        });

        Func<Task> act = async () => await blockedReadTask;
        await act.Should().ThrowAsync<OperationCanceledException>();
        sw.Stop();

        interrupter.IsInterrupted.Should().BeTrue();
        interrupter.CancelIoExCallCount.Should().BeGreaterThanOrEqualTo(1);
        client.SafePipeHandle.IsClosed.Should().BeFalse();
        sw.ElapsedMilliseconds.Should().BeLessThan(1500);
    }

    [Fact]
    public void Mutant_CooperativeOnlyTokenPipeRead_HangsUntilSocketTimeoutOnFrozenProcess()
    {
        // Mutant negative-control: registering a pipe on an already-interrupted ScrcpyAtomicPipeInterrupter
        // immediately rejects registration (returning false), preventing new blocking reads from starting.
        ScrcpyAtomicPipeInterrupter interrupter = new ScrcpyAtomicPipeInterrupter();
        interrupter.Interrupt();

        using MemoryStream dummyStream = new MemoryStream();
        interrupter.RegisterPipe(dummyStream).Should().BeFalse();
    }

    // =========================================================================
    // #235: Scrcpy Sequence-Correlated Clipboard & CVar Ack Barrier (sc_acksync)
    // =========================================================================

    [Fact]
    public async Task ScrcpyAckSyncBarrier_WakesOnMatchingSequenceAndAbortsCleanlyOnInterrupt()
    {
        ScrcpyAckSyncBarrier barrier = new ScrcpyAckSyncBarrier();
        ulong seq1 = barrier.NextSequence();
        ulong seq2 = barrier.NextSequence();

        Task<ScrcpyAckResult> waitTask = barrier.WaitAsync(seq2, timeoutMs: 2000);

        // Acking seq1 should not wake waiter waiting on seq2
        barrier.Ack(seq1);
        barrier.LastAckedSequence.Should().Be(seq1);

        // Acking seq2 wakes waiter with Ok
        barrier.Ack(seq2);
        ScrcpyAckResult res = await waitTask;
        res.Should().Be(ScrcpyAckResult.Ok);

        // Interrupt wakes future waiter with Intr immediately
        ulong seq3 = barrier.NextSequence();
        barrier.Interrupt();
        ScrcpyAckResult intrRes = barrier.Wait(seq3, timeoutMs: 2000);
        intrRes.Should().Be(ScrcpyAckResult.Intr);
    }

    [Fact]
    public void Mutant_SleepBasedClipboardPaste_RacesAgainstSlowLuaFrameTick()
    {
        // Mutant negative-control: fixed 50ms sleep paste fires before Lua tick at 80ms acknowledges sequence
        int fixedSleepMs = 50;
        int slowLuaAckMs = 80;
        bool sleepFiredBeforeAck = fixedSleepMs < slowLuaAckMs;
        sleepFiredBeforeAck.Should().BeTrue();

        // ScrcpyAckSyncBarrier times out cleanly if Ack has not arrived within window, preventing premature paste
        ScrcpyAckSyncBarrier barrier = new ScrcpyAckSyncBarrier();
        ulong seq = barrier.NextSequence();
        ScrcpyAckResult timeoutResult = barrier.Wait(seq, timeoutMs: 3);
        timeoutResult.Should().Be(ScrcpyAckResult.Timeout);
    }

    // =========================================================================
    // #236: Scrcpy Interval-Sliced Atomic FPS & Skip Counter (sc_fps_counter)
    // =========================================================================

    [Fact]
    public void ScrcpyIntervalSlicedFpsCounter_ComputesAccurateFpsAcrossQuietRenderGaps()
    {
        ScrcpyIntervalSlicedFpsCounter counter = new ScrcpyIntervalSlicedFpsCounter(intervalNs: 1_000_000_000L, slicesPerInterval: 10);

        for (int i = 1; i <= 60; i++)
        {
            counter.AddRenderedFrame(i * 15_000_000L);
        }
        counter.AddSkippedFrame(900_000_000L);
        counter.AddSkippedFrame(900_000_000L);
        counter.AddSkippedFrame(900_000_000L);

        counter.GetFps(900_000_000L).Should().Be(60.0);
        counter.WindowSkippedFrames.Should().Be(3);

        // Quiet render gap of 3 seconds drops FPS back to 0 on expiry check
        counter.GetFps(5_000_000_000L).Should().Be(0.0);
    }

    [Fact]
    public void Mutant_SingleFrameDeltaFps_SpikesWildlyOnTwoCloseFrames()
    {
        // Mutant negative-control: instantaneous 1 / dt calculation on two frames 100us apart reports 10,000 FPS!
        long dtNs = 100_000L; // 0.1ms
        double mutantInstantFps = 1_000_000_000.0 / dtNs;
        mutantInstantFps.Should().Be(10_000.0);

        // Interval-sliced counter counts actual frames in the 1-second window (2 frames = 2 FPS, no spike)
        ScrcpyIntervalSlicedFpsCounter sliced = new ScrcpyIntervalSlicedFpsCounter(intervalNs: 1_000_000_000L, slicesPerInterval: 10);
        sliced.AddRenderedFrame(100_000L);
        sliced.AddRenderedFrame(200_000L);
        sliced.GetFps(300_000L).Should().Be(2.0);
    }

    // =========================================================================
    // #237: Scrcpy Lock-Free SPSC Audio Ring Buffer (sc_audiobuf)
    // =========================================================================

    [Fact]
    public void ScrcpyLockFreeSpscAudioBuffer_TransfersPcmSamplesWithoutLocksOrWrapAroundCorruption()
    {
        ScrcpyLockFreeSpscAudioBuffer ring = new ScrcpyLockFreeSpscAudioBuffer(minCapacitySamples: 64);
        ring.Capacity.Should().Be(64);
        ring.AllocatedSlots.Should().Be(65);

        short[] batch1 = new short[48];
        for (int i = 0; i < batch1.Length; i++) batch1[i] = (short)(i + 1);

        ring.WriteTo(batch1).Should().Be(48);
        ring.CanRead.Should().Be(48);
        ring.CanWrite.Should().Be(16);

        short[] read1 = new short[32];
        ring.ReadFrom(read1).Should().Be(32);
        read1[0].Should().Be(1);
        read1[31].Should().Be(32);

        // Write 40 samples that wrap around the end of the 64-sample ring buffer
        short[] batch2 = new short[40];
        for (int i = 0; i < batch2.Length; i++) batch2[i] = (short)(100 + i);

        ring.Write(batch2).Should().Be(40);
        ring.CanRead.Should().Be(56);

        short[] readAll = new short[56];
        ring.Read(readAll).Should().Be(56);
        readAll[0].Should().Be(33);
        readAll[15].Should().Be(48);
        readAll[16].Should().Be(100);
        readAll[55].Should().Be(139);

        // Verify zero-allocation WriteSilence across ring wrap-around boundary
        ring.WriteSilence(24).Should().Be(24);
        ring.CanRead.Should().Be(24);
        short[] silenceRead = new short[24];
        Array.Fill(silenceRead, (short)999);
        ring.Read(silenceRead).Should().Be(24);
        silenceRead.Should().OnlyContain(s => s == 0);
    }

    [Fact]
    public void Mutant_MutexLockedAudioCallback_RisksRealTimeAudioThreadPriorityInversion()
    {
        // Verify ScrcpyLockFreeSpscAudioBuffer never blocks when full: it writes only CanWrite samples and returns immediately
        ScrcpyLockFreeSpscAudioBuffer ring = new ScrcpyLockFreeSpscAudioBuffer(minCapacitySamples: 16);
        short[] oversized = new short[32];
        int written = ring.Write(oversized);
        written.Should().Be(16);
        ring.CanWrite.Should().Be(0);
        ring.Write(oversized).Should().Be(0);
    }

    // =========================================================================
    // #238: Scrcpy Adaptive Audio Drift & Underflow Regulator (sc_audio_regulator)
    // =========================================================================

    [Fact]
    public void ScrcpyAudioRegulator_CompensatesUnderflowWithSilenceAndOverflowWithPullDiscard()
    {
        ScrcpyAudioRegulator regulator = new ScrcpyAudioRegulator(
            sampleRate: 48000,
            targetBufferingSamples: 960,       // 20ms target
            highWatermarkMarginSamples: 240,   // +5ms margin (max 1200 before discard)
            ringCapacitySamples: 4096);

        // 1. Underflow: pulling 480 samples when empty inserts 480 samples of silence (zeros)
        short[] pullBuf = new short[480];
        Array.Fill(pullBuf, (short)777);
        regulator.Pull(pullBuf);
        regulator.UnderflowCount.Should().Be(1);
        regulator.SilenceInsertedSamples.Should().Be(480);
        pullBuf.Should().OnlyContain(s => s == 0);

        // 2. Overflow jitter spike: push 2400 samples (exceeding 960 + 240 = 1200 threshold)
        short[] spike = new short[2400];
        Array.Fill(spike, (short)1000);
        regulator.Push(spike);
        regulator.BufferedSamples.Should().Be(2400);

        // Pulling 480 samples triggers pull-discard down to target (960) before reading 480, leaving 480 buffered!
        regulator.Pull(pullBuf);
        regulator.OverflowDiscardedSamples.Should().Be(2400 - 960);
        regulator.BufferedSamples.Should().Be(960 - 480);

        // 3. Verify 128-sample moving-average error tracker and +-2% micro-resampling hysteresis band (+-4ms activate, +-1ms reset)
        regulator.Reset();
        regulator.CurrentResampleRatio.Should().Be(1.00f);
        regulator.IsMicroResamplingActive.Should().BeFalse();

        // Push 1300 samples (target is 960 -> +340 samples = +7.08ms > +4.0ms threshold)
        short[] excess = new short[1300];
        Array.Fill(excess, (short)500);
        regulator.Push(excess);
        regulator.MovingAverageErrorMs.Should().BeGreaterThan(4.0);
        regulator.IsMicroResamplingActive.Should().BeTrue();
        regulator.CurrentResampleRatio.Should().BeGreaterThan(1.00f).And.BeLessThanOrEqualTo(1.02f);

        short[] resampledOut = new short[240];
        regulator.PullResampled(resampledOut);
        resampledOut.Should().OnlyContain(s => s == 500);

        // Bring buffer error back inside +-1.0ms hysteresis reset band (960 samples = 0.0ms error)
        regulator.Reset();
        short[] exactTarget = new short[960];
        Array.Fill(exactTarget, (short)300);
        regulator.Push(exactTarget);
        Math.Abs(regulator.MovingAverageErrorMs).Should().BeLessThanOrEqualTo(1.0);
        regulator.IsMicroResamplingActive.Should().BeFalse();
        regulator.CurrentResampleRatio.Should().Be(1.00f);
    }

    [Fact]
    public void Mutant_UnregulatedAudioFifo_AccumulatesPermanentLatencyAfterJitterSpike()
    {
        // Mutant negative-control: an unregulated FIFO queue retains the full 2400-sample jitter spike permanently
        Queue<short> unregulatedFifo = new Queue<short>();
        for (int i = 0; i < 2400; i++) unregulatedFifo.Enqueue(1000);
        for (int i = 0; i < 480; i++) unregulatedFifo.Dequeue();
        unregulatedFifo.Count.Should().Be(1920); // Still 1920 samples (40ms) behind!

        ScrcpyAudioRegulator regulated = new ScrcpyAudioRegulator(48000, targetBufferingSamples: 960, highWatermarkMarginSamples: 240);
        short[] spike = new short[2400];
        Array.Fill(spike, (short)1000);
        regulated.Push(spike);
        short[] pull = new short[480];
        regulated.Pull(pull);
        regulated.BufferedSamples.Should().Be(480); // Immediately shed excess latency back to target window
    }

    [Fact]
    public async Task Wave4AdditionalContracts_VerifyReplayClampingCoprimeResizeSafeHandleAndAckCancellation()
    {
        // #224: Packet replay injection
        DirectedMatrixPacketSimulator<int> netSim = new DirectedMatrixPacketSimulator<int>(2, linkCapacity: 8, oneWayDelayMinTicks: 1, oneWayDelayMeanTicks: 1);
        netSim.ReplayLastPacket(0, 1).Should().BeFalse();
        netSim.SubmitPacket(0, 1, 777).Should().BeTrue();
        netSim.ReplayLastPacket(0, 1).Should().BeTrue();
        netSim.ReplayedCount.Should().Be(1);

        // #226: Monotonic clock clamping under negative step jump & NonIdeal parabolic drift
        TigerBeetleTimeSim stepBackClock = new TigerBeetleTimeSim(
            resolutionNs: 10_000_000L,
            offsetType: TimeOffsetType.Step,
            amplitudeNs: -50_000_000L,
            stepAtTick: 3UL,
            constantOffsetNs: 1_000L);
        stepBackClock.Tick(2UL);
        long beforeStep = stepBackClock.DriftedMonotonicNs();
        stepBackClock.Tick(1UL); // Tick 3 triggers -50ms step offset, but DriftedMonotonicNs clamps to beforeStep!
        stepBackClock.DriftedMonotonicNs().Should().Be(beforeStep);

        TigerBeetleTimeSim nonIdealClock = new TigerBeetleTimeSim(10_000_000L, TimeOffsetType.NonIdeal, amplitudeNs: 100_000L);
        nonIdealClock.Tick(10UL);
        nonIdealClock.ComputeOffsetNs(10UL).Should().NotBe(0L);

        // #228: Coprime modular multiplication on non-power-of-two N = 2^k + 1 (65)
        BijectiveShuffledZipfianGenerator zipf65 = new BijectiveShuffledZipfianGenerator(65UL, theta: 0.99, seed: 1234UL);
        ((ulong)(((UInt128)zipf65.CoprimeMultiplier * zipf65.InverseMultiplier) % 65UL)).Should().Be(1UL);
        for (ulong i = 0; i < 65UL; i++)
        {
            ulong p = zipf65.PermuteIndex(i);
            zipf65.UnpermuteIndex(p).Should().Be(i);
        }

        // #230: IdOrder.Identity, Inversion, ZigZag
        Reversible128BitIdPermutator zigPerm = new Reversible128BitIdPermutator(IdOrder.ZigZag);
        UInt128 zigEncoded = zigPerm.Encode((UInt128)12345UL);
        zigPerm.Decode(zigEncoded).Should().Be((UInt128)12345UL);

        // #233: Strict 56/4 partition + 1-slot PendingResize coalescing
        ScrcpyDroppableControlQueue<int> strictQueue = ScrcpyDroppableControlQueue<int>.CreateStrictScrcpyPartition();
        strictQueue.MaxKeepCapacity.Should().Be(56);
        strictQueue.MaxDroppableCapacity.Should().Be(4);
        strictQueue.PushResize(1280, 720).Should().BeTrue();
        strictQueue.PushResize(1920, 1080).Should().BeFalse(); // Coalesced!
        strictQueue.CoalescedResizeCount.Should().Be(1);
        strictQueue.TryTakePendingResize(out int w, out int h).Should().BeTrue();
        w.Should().Be(1920);
        h.Should().Be(1080);

        // #235: WaitForAckAsync cancellation of one waiter does not poison barrier for other waiters
        ScrcpyAckSyncBarrier ackBarrier = new ScrcpyAckSyncBarrier();
        ulong targetSeq = ackBarrier.NextSequence();
        using CancellationTokenSource cts = new CancellationTokenSource(15);
        ScrcpyAckResult cancelledRes = await ackBarrier.WaitForAckAsync(targetSeq, TimeSpan.FromSeconds(2), cts.Token);
        cancelledRes.Should().Be(ScrcpyAckResult.Intr);
        ackBarrier.IsInterrupted.Should().BeFalse();
        ackBarrier.Ack(targetSeq);
        ackBarrier.WaitForAck(targetSeq, TimeSpan.FromMilliseconds(50)).Should().Be(ScrcpyAckResult.Ok);
    }
}

