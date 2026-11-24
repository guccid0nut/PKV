// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: AdaptiveJitterBuffer.cs
// System: Kongor Core Netcode - Zero-Allocation Adaptive Sub-Tick Jitter Buffer
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

namespace ASPIRE.Common.Netcode;

/// <summary>
///     High-performance, zero-allocation circular ring buffer that absorbs network arrival jitter
///     and releases datagrams at smoothed playout intervals.
///     Uses an asymmetric EWMA estimator (RFC 3550) to dynamically adjust playout delay between
///     configurable minimum and maximum thresholds without inflating steady-state latency.
/// </summary>
public sealed class AdaptiveJitterBuffer
{
    private const int MaxSlotBufferSize = 2048;

    private struct Slot
    {
        public byte[] Buffer;
        public int Length;
        public uint Sequence;
        public long ScheduledReleaseUs;
        public bool Occupied;
    }

    private readonly Slot[] slots;
    private readonly int capacity;
    private readonly long minDelayUs;
    private readonly long maxDelayUs;

    private int head;
    private int tail;
    private int occupiedCount;

    private long currentJitterUs;
    private long targetPlayoutDelayUs;

    /// <summary>
    ///     The maximum number of datagrams that can be buffered simultaneously.
    /// </summary>
    public int Capacity => capacity;

    /// <summary>
    ///     The number of datagrams currently awaiting playout.
    /// </summary>
    public int OccupiedCount => occupiedCount;

    /// <summary>
    ///     The estimated network jitter in microseconds.
    /// </summary>
    public long CurrentJitterUs => currentJitterUs;

    /// <summary>
    ///     The current target playout delay in microseconds.
    /// </summary>
    public long TargetPlayoutDelayUs => targetPlayoutDelayUs;

    /// <summary>
    ///     Initializes a new zero-allocation adaptive jitter buffer.
    /// </summary>
    /// <param name="capacity">Circular slot capacity (typically 16 to 64).</param>
    /// <param name="minDelayUs">Minimum playout delay in microseconds (0 for pure passthrough on stable networks).</param>
    /// <param name="maxDelayUs">Maximum playout delay in microseconds (e.g. 20,000us / 20ms).</param>
    public AdaptiveJitterBuffer(int capacity = 32, long minDelayUs = 0, long maxDelayUs = 20_000)
    {
        this.capacity = capacity > 0 ? capacity : 32;
        this.minDelayUs = Math.Max(0, minDelayUs);
        this.maxDelayUs = Math.Max(this.minDelayUs, maxDelayUs);
        this.targetPlayoutDelayUs = this.minDelayUs;

        slots = new Slot[this.capacity];
        for (int i = 0; i < this.capacity; i++)
        {
            slots[i] = new Slot
            {
                Buffer = new byte[MaxSlotBufferSize],
                Length = 0,
                Sequence = 0,
                ScheduledReleaseUs = 0,
                Occupied = false
            };
        }
    }

    /// <summary>
    ///     Updates the EWMA jitter estimate based on measured inter-arrival variation.
    ///     Uses asymmetric adaptation: spikes expand the buffer quickly to avoid drops,
    ///     while stability decays the buffer back toward minimum latency.
    /// </summary>
    /// <param name="deltaUs">Difference in arrival delta compared to expected transmit interval.</param>
    public void RecordInterArrivalDelta(long deltaUs)
    {
        long absDelta = Math.Abs(deltaUs);

        if (absDelta > currentJitterUs)
        {
            // Rapid rise (alpha = 0.25)
            currentJitterUs += (absDelta - currentJitterUs) / 4;
        }
        else
        {
            // Smooth decay (beta = 0.0625)
            currentJitterUs += (absDelta - currentJitterUs) / 16;
        }

        targetPlayoutDelayUs = Math.Clamp(currentJitterUs, minDelayUs, maxDelayUs);
    }

    /// <summary>
    ///     Enqueues an incoming datagram and schedules its playout timestamp.
    ///     Zero heap allocations on the hot path.
    /// </summary>
    /// <param name="payload">The datagram bytes.</param>
    /// <param name="sequence">Client or packet sequence number.</param>
    /// <param name="currentTimestampUs">Current monotonic microsecond timestamp.</param>
    /// <param name="droppedAsFull">Set to true if the slot array is saturated and the datagram was dropped.</param>
    /// <returns>True if enqueued successfully; false if dropped due to capacity exhaustion or oversized payload.</returns>
    public bool TryEnqueue(
        ReadOnlySpan<byte> payload,
        uint sequence,
        long currentTimestampUs,
        out bool droppedAsFull)
    {
        if (occupiedCount >= capacity)
        {
            droppedAsFull = true;

            return false;
        }

        if (payload.Length > MaxSlotBufferSize)
        {
            droppedAsFull = false;

            return false;
        }

        payload.CopyTo(slots[tail].Buffer);
        slots[tail].Length = payload.Length;
        slots[tail].Sequence = sequence;
        slots[tail].ScheduledReleaseUs = currentTimestampUs + targetPlayoutDelayUs;
        slots[tail].Occupied = true;

        tail = (tail + 1) % capacity;
        occupiedCount++;
        droppedAsFull = false;

        return true;
    }

    /// <summary>
    ///     Attempts to dequeue the oldest buffered datagram if its scheduled playout time has arrived.
    ///     Zero heap allocations on the hot path.
    /// </summary>
    /// <param name="currentTimestampUs">Current monotonic microsecond timestamp.</param>
    /// <param name="destination">Output buffer for the payload.</param>
    /// <param name="bytesWritten">Number of bytes written to destination.</param>
    /// <param name="sequence">Sequence number of the dequeued packet.</param>
    /// <returns>True if a datagram was ready and dequeued; false otherwise.</returns>
    public bool TryDequeue(
        long currentTimestampUs,
        Span<byte> destination,
        out int bytesWritten,
        out uint sequence)
    {
        if (occupiedCount == 0)
        {
            bytesWritten = 0;
            sequence = 0;

            return false;
        }

        if (currentTimestampUs < slots[head].ScheduledReleaseUs)
        {
            bytesWritten = 0;
            sequence = 0;

            return false;
        }

        int len = slots[head].Length;
        if (destination.Length < len)
        {
            bytesWritten = 0;
            sequence = 0;

            return false;
        }

        slots[head].Buffer.AsSpan(0, len).CopyTo(destination);
        bytesWritten = len;
        sequence = slots[head].Sequence;
        slots[head].Occupied = false;

        head = (head + 1) % capacity;
        occupiedCount--;

        return true;
    }

    /// <summary>
    ///     Immediately drains all buffered datagrams, invoking the callback for each one.
    /// </summary>
    public void DrainAll(Action<ReadOnlyMemory<byte>, uint> onPacket)
    {
        while (occupiedCount > 0)
        {
            int len = slots[head].Length;
            uint seq = slots[head].Sequence;

            onPacket(new ReadOnlyMemory<byte>(slots[head].Buffer, 0, len), seq);

            slots[head].Occupied = false;
            head = (head + 1) % capacity;
            occupiedCount--;
        }
    }

    /// <summary>
    ///     Resets the jitter buffer and clears all slots.
    /// </summary>
    public void Clear()
    {
        for (int i = 0; i < capacity; i++)
        {
            slots[i].Occupied = false;
            slots[i].Length = 0;
            slots[i].Sequence = 0;
            slots[i].ScheduledReleaseUs = 0;
        }

        head = 0;
        tail = 0;
        occupiedCount = 0;
        currentJitterUs = 0;
        targetPlayoutDelayUs = minDelayUs;
    }
}
