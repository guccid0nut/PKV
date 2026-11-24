// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: NetcodeChaosSimulationTests.cs
// System: COMPEL Tests - Network Chaos, Jitter, Packet Loss & Fuzz Resilience Tests
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Buffers.Binary;
using ASPIRE.Common.Netcode;
using COMPEL.Services.Proxy.Congestion;
using FluentAssertions;
using Kongor.Core.Services.Networking;
using Xunit;

namespace COMPEL.Tests;

public sealed class NetcodeChaosSimulationTests
{
    [Fact]
    public void OutOfOrderDatagrams_AreReorderedCorrectly_BySequenceWindow()
    {
        // Simulate a rolling sequence buffer tracking arriving packet sequences
        uint expectedSequence = 100;
        List<uint> arrivingSequences = [102, 100, 104, 101, 103, 105];
        SortedSet<uint> jitterBuffer = [];
        List<uint> processedSequences = [];

        foreach (uint seq in arrivingSequences)
        {
            if (seq >= expectedSequence)
            {
                jitterBuffer.Add(seq);
            }

            while (jitterBuffer.Contains(expectedSequence))
            {
                jitterBuffer.Remove(expectedSequence);
                processedSequences.Add(expectedSequence);
                expectedSequence++;
            }
        }

        // Verify all 6 out-of-order packets were reordered into exact 100..105 sequence
        processedSequences.Should().Equal([100, 101, 102, 103, 104, 105]);
        jitterBuffer.Should().BeEmpty();
    }

    [Fact]
    public void DuplicatePackets_AreIdentifiedAndSuppressed()
    {
        uint lastAcceptedSeq = 200;
        List<uint> stream = [201, 202, 202, 203, 201, 204];
        List<uint> accepted = [];
        int duplicateCount = 0;

        foreach (uint seq in stream)
        {
            if (seq > lastAcceptedSeq)
            {
                accepted.Add(seq);
                lastAcceptedSeq = seq;
            }
            else
            {
                duplicateCount++;
            }
        }

        accepted.Should().Equal([201, 202, 203, 204]);
        duplicateCount.Should().Be(2);
    }

    [Fact]
    public void FuzzedPackets_WithCorruptSubTickMagic_AreSafelyRejectedWithoutThrowing()
    {
        byte[] validPacket = new byte[SubTickCommandPacket.HeaderSize + SubTickInputEvent.BinarySize];
        SubTickInputEvent evt = SubTickInputEvent.CreateMove(
            sequence: 1,
            baseTick: 50,
            subTickOffset: 0.1f,
            timestampUs: 1000L,
            destination: System.Numerics.Vector3.Zero,
            cameraYaw: 0f);

        SubTickCommandPacket.TryWrite(validPacket, 50, [evt], 0, out int bytesWritten);
        bytesWritten.Should().BeGreaterThan(0);

        // Apply chaos mutations
        Random rng = new(1337);
        for (int i = 0; i < 50; i++)
        {
            byte[] fuzzed = (byte[])validPacket.Clone();

            // Corrupt magic or length
            int mutateIndex = rng.Next(0, 4);
            fuzzed[mutateIndex] ^= (byte)rng.Next(1, 255);

            SubTickInputEvent[] outEvents = new SubTickInputEvent[4];
            bool parsed = SubTickCommandPacket.TryParse(fuzzed, out _, outEvents, out int count);

            parsed.Should().BeFalse();
            count.Should().Be(0);
        }
    }

    [Fact]
    public void BurstPacing_SmoothsPacketTransmission_WithoutExceedingMaxDelayClamp()
    {
        ControllableTimeProvider timeProvider = new();
        BbrCongestionController bbr = new(timeProvider);
        UdpTokenBucketPacer pacer = new(
            bbr,
            fixedPacingRateBps: 2_000_000L,
            maxBurstCapacityBytes: 2000,
            maxDelayClampMs: 2.5,
            timeProvider: timeProvider);

        // Simulate 8 snapshot packets arriving simultaneously (1400 bytes each)
        List<PacingDecision> decisions = [];
        for (int i = 0; i < 8; i++)
        {
            PacingDecision decision = pacer.Evaluate(1400);
            decisions.Add(decision);

            // Advance virtual time by the recommended delay (or 0)
            timeProvider.Advance(decision.RecommendedDelay);
            pacer.NotifyTransmissionComplete();
        }

        // All packets must be scheduled safely without any packet exceeding 2.5ms delay
        foreach (PacingDecision d in decisions)
        {
            d.RecommendedDelay.Should().BeLessOrEqualTo(TimeSpan.FromMilliseconds(2.5));
        }

        // First packet sends immediately using burst headroom
        decisions[0].ShouldSendImmediately.Should().BeTrue();
    }

    [Fact]
    public void DirectedMatrixPacketSimulator_EnforcesAsymmetricPartitionsAndQueueHeadDrop()
    {
        DirectedMatrixPacketSimulator<int> sim = new(
            nodeCount: 2,
            linkCapacity: 3,
            oneWayDelayMinTicks: 1,
            oneWayDelayMeanTicks: 1);

        sim.ApplyPartition(
            PartitionTopology.IsolateSingle,
            PartitionSymmetry.Asymmetric,
            isolatedNode: 0);

        // Node 0 -> Node 1 is open (Player A is heard by Player B), but Node 1 -> Node 0 is blocked
        sim.IsLinkEnabled(0, 1).Should().BeTrue();
        sim.IsLinkEnabled(1, 0).Should().BeFalse();

        sim.SubmitPacket(1, 0, 999).Should().BeFalse();
        sim.PartitionDroppedCount.Should().Be(1);

        // Enqueue 4 packets on capacity-3 link 0 -> 1; oldest head packet (10) must be dropped
        sim.SubmitPacket(0, 1, 10).Should().BeTrue();
        sim.SubmitPacket(0, 1, 20).Should().BeTrue();
        sim.SubmitPacket(0, 1, 30).Should().BeTrue();
        sim.SubmitPacket(0, 1, 40).Should().BeTrue();
        sim.HeadDroppedCount.Should().Be(1);

        List<int> delivered = [];
        sim.Step((_, _, payload) => delivered.Add(payload));
        delivered.Should().Equal([20, 30, 40]);

        sim.ReplayLastPacket(0, 1).Should().BeTrue();
        sim.ReplayedCount.Should().Be(1);
    }

    [Fact]
    public void ReplySequenceReassembler_ReordersOutOfOrderSnapshotsAndRejectsDuplicatesInO1()
    {
        ReplySequenceReassembler<string> reassembler = new(initialExpectedSequence: 100UL);
        reassembler.Empty().Should().BeTrue();

        reassembler.Insert(102UL, "snap-102").Should().BeTrue();
        reassembler.Insert(100UL, "snap-100").Should().BeTrue();
        reassembler.Insert(102UL, "snap-102-dup").Should().BeFalse();
        reassembler.Insert(101UL, "snap-101").Should().BeTrue();

        reassembler.DuplicateOrStaleDropCount.Should().Be(1);

        List<string> ordered = [];
        while (reassembler.ReadyToPop())
        {
            (ulong _, string item) = reassembler.Pop();
            ordered.Add(item);
        }

        ordered.Should().Equal(["snap-100", "snap-101", "snap-102"]);
        reassembler.Empty().Should().BeTrue();
    }

    [Fact]
    public void TigerBeetleTimeSim_SimulatesLinearPeriodicAndStepClockSkewDeterministically()
    {
        TigerBeetleTimeSim linearClock = new(
            resolutionNs: 10_000_000L,
            offsetType: TimeOffsetType.Linear,
            amplitudeNs: 50_000L);
        linearClock.Tick(10UL);
        linearClock.ComputeOffsetNs(10UL).Should().Be(500_000L);

        TigerBeetleTimeSim stepClock = new(
            resolutionNs: 10_000_000L,
            offsetType: TimeOffsetType.Step,
            amplitudeNs: -250_000_000L,
            stepAtTick: 5UL);
        stepClock.Tick(4UL);
        long monotonicBeforeStep = stepClock.DriftedMonotonicNs();
        stepClock.ComputeOffsetNs(4UL).Should().Be(0L);
        stepClock.Tick(1UL);
        stepClock.ComputeOffsetNs(5UL).Should().Be(-250_000_000L);
        stepClock.DriftedMonotonicNs().Should().Be(monotonicBeforeStep);
    }
}



