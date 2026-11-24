// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: InflightPacketTracker.cs
// System: Kongor Core Netcode - Microsecond Inflight Packet Ring Buffer & BBR Feedback
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

namespace ASPIRE.Common.Netcode;

/// <summary>
///     Recorded state of an in-flight datagram transmitted over the network.
/// </summary>
public struct InflightPacketEntry
{
    public uint Sequence;
    public ushort SizeBytes;
    public long SendTimestampUs;
    public bool IsAcked;
    public bool IsValid;
}

/// <summary>
///     High-performance zero-allocation ring buffer tracking in-flight packets, byte weights,
///     send timestamps, and microsecond RTT delivery measurements for closed-loop BBR congestion control.
/// </summary>
public sealed class InflightPacketTracker
{
    public const int DefaultCapacity = 256;
    public const long DefaultMaxLostAgeUs = 200_000L; // 200 ms max age before unacked packets are deemed lost

    private readonly InflightPacketEntry[] _entries;
    private readonly uint _mask;
    private readonly Lock _sync = new();

    private long _inflightBytes;
    private uint _highestSentSequence;
    private uint _highestAckedSequence;

    private ulong _recentOutcomes;
    private int _recentSampleCount;
    private int _consecutiveLosses;
    private long _totalPacketsSent;
    private long _totalPacketsAcked;
    private long _totalPacketsLost;

    public int Capacity => _entries.Length;
    public long InflightBytes
    {
        get
        {
            lock (_sync)
            {
                return _inflightBytes;
            }
        }
    }

    public uint HighestSentSequence
    {
        get
        {
            lock (_sync)
            {
                return _highestSentSequence;
            }
        }
    }

    public uint HighestAckedSequence
    {
        get
        {
            lock (_sync)
            {
                return _highestAckedSequence;
            }
        }
    }

    public int ConsecutiveLossCount
    {
        get
        {
            lock (_sync)
            {
                return _consecutiveLosses;
            }
        }
    }

    public double RecentLossRate
    {
        get
        {
            lock (_sync)
            {
                if (_recentSampleCount == 0) return 0.0;
                ulong mask = _recentSampleCount == 64 ? ulong.MaxValue : (1UL << _recentSampleCount) - 1UL;
                int delivered = System.Numerics.BitOperations.PopCount(_recentOutcomes & mask);
                int lost = _recentSampleCount - delivered;
                return (double)lost / _recentSampleCount;
            }
        }
    }

    public long TotalPacketsSent
    {
        get
        {
            lock (_sync)
            {
                return _totalPacketsSent;
            }
        }
    }

    public long TotalPacketsAcked
    {
        get
        {
            lock (_sync)
            {
                return _totalPacketsAcked;
            }
        }
    }

    public long TotalPacketsLost
    {
        get
        {
            lock (_sync)
            {
                return _totalPacketsLost;
            }
        }
    }

    public void GetCongestionSignals(out long inflightBytes, out double lossRate, out int consecutiveLosses)
    {
        lock (_sync)
        {
            inflightBytes = _inflightBytes;
            consecutiveLosses = _consecutiveLosses;
            if (_recentSampleCount == 0)
            {
                lossRate = 0.0;
            }
            else
            {
                ulong mask = _recentSampleCount == 64 ? ulong.MaxValue : (1UL << _recentSampleCount) - 1UL;
                int delivered = System.Numerics.BitOperations.PopCount(_recentOutcomes & mask);
                int lost = _recentSampleCount - delivered;
                lossRate = (double)lost / _recentSampleCount;
            }
        }
    }

    public InflightPacketTracker(int capacity = DefaultCapacity)
    {
        if (capacity <= 0 || (capacity & (capacity - 1)) != 0)
        {
            throw new ArgumentException("Capacity must be a positive power of 2.", nameof(capacity));
        }

        _entries = new InflightPacketEntry[capacity];
        _mask = (uint)(capacity - 1);
    }

    /// <summary>
    ///     Records an outgoing packet transmission, evicting any stale overwritten slot.
    /// </summary>
    public void TrackPacket(uint sequence, ushort sizeBytes, long sendTimestampUs)
    {
        lock (_sync)
        {
            int index = (int)(sequence & _mask);
            InflightPacketEntry existing = _entries[index];

            // If overwriting a valid slot that was never acknowledged, reclaim its inflight bytes
            if (existing.IsValid && !existing.IsAcked)
            {
                _inflightBytes = Math.Max(0, _inflightBytes - existing.SizeBytes);
            }

            _entries[index] = new InflightPacketEntry
            {
                Sequence = sequence,
                SizeBytes = sizeBytes,
                SendTimestampUs = sendTimestampUs,
                IsAcked = false,
                IsValid = true
            };

            _inflightBytes += sizeBytes;
            _totalPacketsSent++;

            if ((int)(sequence - _highestSentSequence) > 0 || _highestSentSequence == 0)
            {
                _highestSentSequence = sequence;
            }
        }
    }

    /// <summary>
    ///     Processes an inbound ACK receipt from a modern packet header.
    ///     Calculates delivery bytes and measured RTT, and reaps lost preceding packets.
    /// </summary>
    public bool TryProcessAck(
        uint ackSequence,
        uint ackTimestampEchoUs,
        long receiveTimestampUs,
        out long bytesAcked,
        out TimeSpan measuredRtt)
    {
        lock (_sync)
        {
            bytesAcked = 0;
            measuredRtt = TimeSpan.Zero;

            if (ackSequence == 0)
            {
                return false;
            }

            int index = (int)(ackSequence & _mask);
            InflightPacketEntry entry = _entries[index];

            if (!entry.IsValid || entry.Sequence != ackSequence || entry.IsAcked)
            {
                return false;
            }

            // 1. Mark as acknowledged and reclaim inflight bytes
            _entries[index].IsAcked = true;
            bytesAcked = entry.SizeBytes;
            _inflightBytes = Math.Max(0, _inflightBytes - bytesAcked);
            _totalPacketsAcked++;
            _recentOutcomes = (_recentOutcomes << 1) | 1UL;
            if (_recentSampleCount < 64)
            {
                _recentSampleCount++;
            }
            _consecutiveLosses = 0;

            if ((int)(ackSequence - _highestAckedSequence) > 0 || _highestAckedSequence == 0)
            {
                _highestAckedSequence = ackSequence;
            }

            // 2. Measure Round-Trip Time (RTT)
            long rttUs;
            if (ackTimestampEchoUs > 0)
            {
                uint receiveUs32 = (uint)(receiveTimestampUs & 0xFFFFFFFF);
                uint deltaUs = receiveUs32 - ackTimestampEchoUs;

                // Validate reasonable bound (< 10 seconds)
                if (deltaUs < 10_000_000)
                {
                    rttUs = deltaUs;
                }
                else if (receiveTimestampUs >= entry.SendTimestampUs)
                {
                    rttUs = receiveTimestampUs - entry.SendTimestampUs;
                }
                else
                {
                    rttUs = 1000;
                }
            }
            else if (receiveTimestampUs >= entry.SendTimestampUs)
            {
                rttUs = receiveTimestampUs - entry.SendTimestampUs;
            }
            else
            {
                rttUs = 1000;
            }

            rttUs = Math.Clamp(rttUs, 100L, 5_000_000L); // clamp between 0.1ms and 5s
            measuredRtt = TimeSpan.FromMicroseconds(rttUs);

            // 3. Reap dropped / unacknowledged packets prior to ackSequence
            ReapLostPacketsUnsafe(ackSequence, receiveTimestampUs, rttUs);

            // 4. Reset consecutive loss counter because this ACK confirms the link is currently delivering packets
            _consecutiveLosses = 0;

            return true;
        }
    }

    private void ReapLostPacketsUnsafe(uint latestAckSeq, long receiveTimestampUs, long rttUs)
    {
        long lossDelayUs = Math.Max(rttUs + (rttUs / 4), 25_000L); // 1.25x RTT or min 25ms tolerance

        for (int i = 0; i < _entries.Length; i++)
        {
            if (_entries[i].IsValid && !_entries[i].IsAcked)
            {
                int diff = (int)(latestAckSeq - _entries[i].Sequence);
                if (diff > 0)
                {
                    long ageUs = receiveTimestampUs - _entries[i].SendTimestampUs;
                    // Lost if:
                    // 1. Age exceeds 1.25x RTT (RFC 9002 time threshold)
                    // 2. OR 3+ packets ahead have been ACKed (RFC 9002 packet threshold)
                    // 3. OR age exceeds unconditional 200ms stale ceiling
                    if (ageUs > lossDelayUs || diff >= 3 || ageUs > DefaultMaxLostAgeUs)
                    {
                        _inflightBytes = Math.Max(0, _inflightBytes - _entries[i].SizeBytes);
                        _entries[i].IsValid = false;
                        _totalPacketsLost++;
                        _recentOutcomes = (_recentOutcomes << 1);
                        if (_recentSampleCount < 64)
                        {
                            _recentSampleCount++;
                        }
                        _consecutiveLosses++;
                    }
                }
            }
        }
    }

    /// <summary>
    ///     Resets all tracking state.
    /// </summary>
    public void Reset()
    {
        lock (_sync)
        {
            Array.Clear(_entries);
            _inflightBytes = 0;
            _highestSentSequence = 0;
            _highestAckedSequence = 0;
            _recentOutcomes = 0UL;
            _recentSampleCount = 0;
            _consecutiveLosses = 0;
            _totalPacketsSent = 0;
            _totalPacketsAcked = 0;
            _totalPacketsLost = 0;
        }
    }
}
