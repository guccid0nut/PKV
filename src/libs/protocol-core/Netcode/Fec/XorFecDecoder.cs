// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: XorFecDecoder.cs
// System: Kongor Core Netcode - Zero-Allocation XOR Forward Error Correction Decoder
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

namespace ASPIRE.Common.Netcode;

/// <summary>
/// Action emitted by XorFecDecoder for each incoming datagram.
/// </summary>
public enum FecPacketAction
{
    PassThrough,
    PacketRecovered,
    ParityAcknowledged,
    DuplicateIgnored,
    Unrecoverable,
    Malformed
}

/// <summary>
/// Result structure containing action, payload slice, and recovered packet index.
/// </summary>
public readonly ref struct FecDecodeResult
{
    public FecPacketAction Action { get; }
    public ReadOnlySpan<byte> DeliveredPayload { get; }
    public int RecoveredIndex { get; }

    public FecDecodeResult(FecPacketAction action, ReadOnlySpan<byte> payload = default, int recoveredIndex = -1)
    {
        Action = action;
        DeliveredPayload = payload;
        RecoveredIndex = recoveredIndex;
    }
}

/// <summary>
/// Diagnostic statistics for Forward Error Correction receiver.
/// </summary>
public readonly record struct FecDecoderStats(
    long TotalPacketsReceived,
    long DataPacketsDelivered,
    long ParityPacketsReceived,
    long PacketsRecovered,
    long UnrecoverableBlocks,
    long DuplicatePacketsDropped);

/// <summary>
/// Zero-allocation, high-performance sliding window FEC decoder.
/// Passes intact data packets through immediately with 0ms latency.
/// Reconstructs single dropped packets on parity arrival.
/// Drops late jitter duplicates automatically.
/// </summary>
public sealed class XorFecDecoder
{
    private const int RingSize = 16;
    private const int RingMask = RingSize - 1;
    private const int MaxDatagramPayloadSize = 1400;

    private readonly FecBlockSlot[] _slots = new FecBlockSlot[RingSize];
    private readonly byte[] _reconstructedBuffer = new byte[MaxDatagramPayloadSize];
    private readonly ushort[] _tempLengths = new ushort[16];

    private long _totalPacketsReceived;
    private long _dataPacketsDelivered;
    private long _parityPacketsReceived;
    private long _packetsRecovered;
    private long _unrecoverableBlocks;
    private long _duplicatePacketsDropped;

    public FecDecoderStats Stats => new(
        Volatile.Read(ref _totalPacketsReceived),
        Volatile.Read(ref _dataPacketsDelivered),
        Volatile.Read(ref _parityPacketsReceived),
        Volatile.Read(ref _packetsRecovered),
        Volatile.Read(ref _unrecoverableBlocks),
        Volatile.Read(ref _duplicatePacketsDropped));

    public XorFecDecoder()
    {
        for (int i = 0; i < RingSize; i++)
        {
            _slots[i] = new FecBlockSlot();
        }
    }

    /// <summary>
    /// Processes an incoming raw UDP datagram (data or parity).
    /// </summary>
    public bool TryProcessIncomingPacket(
        ReadOnlySpan<byte> datagram,
        out FecDecodeResult result)
    {
        Interlocked.Increment(ref _totalPacketsReceived);

        if (!FecPacketHeader.TryParse(datagram, out FecPacketHeader header, _tempLengths, out int headerSize))
        {
            result = new FecDecodeResult(FecPacketAction.Malformed);
            return false;
        }

        ReadOnlySpan<byte> payload = datagram.Slice(headerSize);
        int slotIndex = header.GroupId & RingMask;
        FecBlockSlot slot = _slots[slotIndex];

        // If slot contains an older block, reset it
        if (slot.GroupId != header.GroupId)
        {
            slot.Reset(header.GroupId, header.BlockSize);
        }

        if (!header.IsParity)
        {
            // Data packet processing
            byte idx = header.PacketIndex;
            if (idx >= header.BlockSize)
            {
                result = new FecDecodeResult(FecPacketAction.Malformed);
                return false;
            }

            // Check if already emitted (either received directly earlier or recovered from parity)
            if (slot.IsEmitted(idx))
            {
                Interlocked.Increment(ref _duplicatePacketsDropped);
                result = new FecDecodeResult(FecPacketAction.DuplicateIgnored);
                return true;
            }

            slot.StoreDataPacket(idx, payload);
            slot.MarkEmitted(idx);
            Interlocked.Increment(ref _dataPacketsDelivered);

            result = new FecDecodeResult(FecPacketAction.PassThrough, payload, idx);
            return true;
        }
        else
        {
            // Parity packet processing
            Interlocked.Increment(ref _parityPacketsReceived);
            slot.StoreParity(payload, _tempLengths.AsSpan(0, header.BlockSize));

            // Determine how many data packets are missing
            int missingCount = 0;
            int missingIdx = -1;

            for (int i = 0; i < header.BlockSize; i++)
            {
                if (!slot.IsEmitted(i))
                {
                    missingCount++;
                    missingIdx = i;
                }
            }

            if (missingCount == 0)
            {
                // All packets in this block have already been delivered
                result = new FecDecodeResult(FecPacketAction.ParityAcknowledged);
                return true;
            }

            if (missingCount == 1)
            {
                // Exactly 1 packet missing - RECOVER INSTANTLY!
                ushort missingLength = slot.PacketLengths[missingIdx];
                int maxLen = Math.Min((int)slot.ParityLength, MaxDatagramPayloadSize);

                // Copy parity into reconstruction buffer
                slot.ParityBuffer.AsSpan(0, maxLen).CopyTo(_reconstructedBuffer);

                // XOR against all received data packets
                for (int i = 0; i < header.BlockSize; i++)
                {
                    if (i != missingIdx)
                    {
                        ReadOnlySpan<byte> otherData = slot.GetDataSpan(i);
                        XorFecMath.AccumulateXor(_reconstructedBuffer.AsSpan(0, otherData.Length), otherData);
                    }
                }

                slot.MarkEmitted(missingIdx);
                Interlocked.Increment(ref _packetsRecovered);
                Interlocked.Increment(ref _dataPacketsDelivered);

                ReadOnlySpan<byte> recoveredSpan = _reconstructedBuffer.AsSpan(0, missingLength);
                result = new FecDecodeResult(FecPacketAction.PacketRecovered, recoveredSpan, missingIdx);
                return true;
            }

            // 2 or more packets missing - unrecoverable via single parity
            Interlocked.Increment(ref _unrecoverableBlocks);
            result = new FecDecodeResult(FecPacketAction.Unrecoverable);
            return true;
        }
    }

    private sealed class FecBlockSlot
    {
        public ushort GroupId { get; private set; }
        public byte BlockSize { get; private set; }
        public ushort EmittedMask { get; private set; }
        public ushort[] PacketLengths { get; } = new ushort[16];
        public byte[][] DataBuffers { get; } = new byte[16][];
        public byte[] ParityBuffer { get; } = new byte[MaxDatagramPayloadSize];
        public ushort ParityLength { get; private set; }

        public FecBlockSlot()
        {
            for (int i = 0; i < 16; i++)
            {
                DataBuffers[i] = new byte[MaxDatagramPayloadSize];
            }
        }

        public void Reset(ushort groupId, byte blockSize)
        {
            GroupId = groupId;
            BlockSize = blockSize;
            EmittedMask = 0;
            ParityLength = 0;
            Array.Clear(PacketLengths, 0, PacketLengths.Length);
        }

        public bool IsEmitted(int index) => (EmittedMask & (1 << index)) != 0;

        public void MarkEmitted(int index)
        {
            EmittedMask |= (ushort)(1 << index);
        }

        public void StoreDataPacket(int index, ReadOnlySpan<byte> payload)
        {
            PacketLengths[index] = (ushort)payload.Length;
            payload.CopyTo(DataBuffers[index]);
        }

        public void StoreParity(ReadOnlySpan<byte> parityPayload, ReadOnlySpan<ushort> lengths)
        {
            ParityLength = (ushort)parityPayload.Length;
            parityPayload.CopyTo(ParityBuffer);
            lengths.CopyTo(PacketLengths);
        }

        public ReadOnlySpan<byte> GetDataSpan(int index)
        {
            return DataBuffers[index].AsSpan(0, PacketLengths[index]);
        }
    }
}
