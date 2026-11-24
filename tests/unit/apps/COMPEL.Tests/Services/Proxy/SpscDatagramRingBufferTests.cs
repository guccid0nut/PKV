// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: SpscDatagramRingBufferTests.cs
// System: COMPEL Tests - Zero-Allocation SPSC Datagram Ring Buffer Unit Tests
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using COMPEL.Services.Proxy;
using FluentAssertions;
using Xunit;

namespace COMPEL.Tests.Services.Proxy;

public sealed class SpscDatagramRingBufferTests
{
    [Fact]
    public void CapacityMustBePowerOfTwo()
    {
        Action invalidZero = () => new SpscDatagramRingBuffer(capacity: 0);
        Action invalidNonPowerOfTwo = () => new SpscDatagramRingBuffer(capacity: 100);
        Action validPowerOfTwo = () => new SpscDatagramRingBuffer(capacity: 64);

        invalidZero.Should().Throw<ArgumentException>();
        invalidNonPowerOfTwo.Should().Throw<ArgumentException>();
        validPowerOfTwo.Should().NotThrow();
    }

    [Fact]
    public void EnqueueAndDequeue_SinglePacket_PreservesAllMetadataAndData()
    {
        using SpscDatagramRingBuffer ringBuffer = new(capacity: 16);
        byte[] payload = Encoding.UTF8.GetBytes("KONGOR_TEST_PAYLOAD_001");
        ushort sequence = 42;
        long timestampUs = 123456789;
        bool isModern = true;

        bool enqueued = ringBuffer.TryEnqueue(payload, sequence, timestampUs, isModern);
        enqueued.Should().BeTrue();

        bool dequeued = ringBuffer.TryDequeue(
            out ReadOnlyMemory<byte> dequeuedData,
            out ushort dequeuedSeq,
            out long dequeuedTs,
            out bool dequeuedModern);

        dequeued.Should().BeTrue();
        dequeuedData.ToArray().Should().Equal(payload);
        dequeuedSeq.Should().Be(sequence);
        dequeuedTs.Should().Be(timestampUs);
        dequeuedModern.Should().Be(isModern);

        // Buffer should now be empty
        bool secondDequeue = ringBuffer.TryDequeue(out _, out _, out _, out _);
        secondDequeue.Should().BeFalse();
    }

    [Fact]
    public void Enqueue_WhenBufferFull_DropsPacketAndIncrementsDrops()
    {
        using SpscDatagramRingBuffer ringBuffer = new(capacity: 4);
        byte[] payload = [0x01, 0x02, 0x03, 0x04];

        for (int i = 0; i < 4; i++)
        {
            bool ok = ringBuffer.TryEnqueue(payload, (ushort)i);
            ok.Should().BeTrue();
        }

        // 5th enqueue should drop because capacity is 4
        bool dropped = ringBuffer.TryEnqueue(payload, 99);
        dropped.Should().BeFalse();
        ringBuffer.Drops.Should().Be(1);

        // Dequeue one slot
        bool dequeued = ringBuffer.TryDequeue(out _, out _, out _, out _);
        dequeued.Should().BeTrue();

        // Enqueue should now succeed again
        bool recovered = ringBuffer.TryEnqueue(payload, 100);
        recovered.Should().BeTrue();
    }

    [Fact]
    public void ZeroAllocation_HotPath_ShouldAllocateZeroBytesOnSteadyState()
    {
        using SpscDatagramRingBuffer ringBuffer = new(capacity: 64);
        byte[] samplePacket = new byte[128];
        Random.Shared.NextBytes(samplePacket);

        // Warm up JIT and any internal structures
        for (int i = 0; i < 100; i++)
        {
            ringBuffer.TryEnqueue(samplePacket, (ushort)i, i * 1000, true);
            ringBuffer.TryDequeue(out _, out _, out _, out _);
        }

        // Measure allocations across 1,000 steady-state enqueue/dequeue cycles
        long beforeAlloc = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < 1_000; i++)
        {
            ringBuffer.TryEnqueue(samplePacket, (ushort)i, i * 1000, true);
            ringBuffer.TryDequeue(out _, out _, out _, out _);
        }

        long afterAlloc = GC.GetAllocatedBytesForCurrentThread();
        long totalAllocated = afterAlloc - beforeAlloc;

        totalAllocated.Should().Be(0, "the SPSC ring buffer steady-state hot path must be strictly zero-allocation");
    }

    [Fact]
    public async Task ProducerConsumerAsync_HighThroughputBurst_MaintainsFifoOrderingWithoutLoss()
    {
        const int packetCount = 1_000;
        using SpscDatagramRingBuffer ringBuffer = new(capacity: 1024);
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        int receivedCount = 0;

        Task consumer = Task.Run(async () =>
        {
            ushort expectedSequence = 0;
            while (receivedCount < packetCount && await ringBuffer.WaitToReadAsync(cts.Token))
            {
                while (ringBuffer.TryDequeue(out ReadOnlyMemory<byte> datagram, out ushort seq, out _, out _))
                {
                    seq.Should().Be(expectedSequence);
                    datagram.Length.Should().Be(64);
                    expectedSequence++;
                    receivedCount++;
                }
            }
        }, cts.Token);

        Task producer = Task.Run(async () =>
        {
            byte[] packet = new byte[64];
            for (ushort i = 0; i < packetCount; i++)
            {
                packet[0] = (byte)(i & 0xFF);
                packet[1] = (byte)((i >> 8) & 0xFF);

                while (!ringBuffer.TryEnqueue(packet, i))
                {
                    await Task.Delay(1, cts.Token);
                }
            }
        }, cts.Token);

        await Task.WhenAll(producer, consumer);

        receivedCount.Should().Be(packetCount);
        ringBuffer.Drops.Should().Be(0);
    }

    [Fact]
    public void BurstOverflow_DropsExcessDatagramsAccurately()
    {
        using SpscDatagramRingBuffer ringBuffer = new(capacity: 16);
        byte[] packet = new byte[32];

        for (ushort i = 0; i < 20; i++)
        {
            ringBuffer.TryEnqueue(packet, i);
        }

        // 16 slots should have enqueued, 4 should have dropped
        ringBuffer.Drops.Should().Be(4);
    }

    [Fact]
    public async Task Dispose_SignalsConsumerGracefully()
    {
        SpscDatagramRingBuffer ringBuffer = new(capacity: 16);
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));

        Task<bool> waitingTask = Task.Run(async () => await ringBuffer.WaitToReadAsync(cts.Token));

        // Let the consumer start waiting
        await Task.Delay(50);
        waitingTask.IsCompleted.Should().BeFalse();

        // Disposing the buffer should wake the waiter with false
        ringBuffer.Dispose();

        bool result = await waitingTask;
        result.Should().BeFalse();
    }
}
