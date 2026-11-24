// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: DeterministicNetSimPrimitives.cs
// System: Kongor Protocol Core - Deterministic Network & Clock Simulation (#224-#226, #256)
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System;
using System.Collections.Generic;
using System.Numerics;

namespace Kongor.Core.Services.Networking;

/// <summary>
/// Network partition topology shapes ported from TigerBeetle (<c>src/testing/packet_simulator.zig</c>) (#224).
/// </summary>
public enum PartitionTopology
{
    None = 0,
    UniformSize = 1,
    IsolateSingle = 2
}

/// <summary>
/// Symmetry of a network partition (#224).
/// </summary>
public enum PartitionSymmetry
{
    Symmetric = 0,
    Asymmetric = 1
}

/// <summary>
/// Wave 4 (#224, #256): Canonical TigerBeetle-inspired directed-matrix packet simulator (<c>links[source * nodeCount + target]</c>)
/// shared across COMPEL voice/UDP relay and Kongor CLI network chaos handlers.
/// Supports asymmetric partition topologies, bidirectional/unidirectional clog ticks, per-link bounded queues with
/// head-drop on overflow, packet replay fault injection, and deterministic exponential jitter delay sampling.
/// </summary>
public sealed class DirectedMatrixPacketSimulator<TPacket>
{
    private readonly record struct QueuedPacket(ulong ReadyAtTick, TPacket Payload);

    private sealed class DirectedLink
    {
        public bool IsEnabled { get; set; } = true;
        public ulong CloggedUntilTick { get; set; }
        public bool HasLastPacket { get; set; }
        public TPacket? LastPacket { get; set; }
        public List<QueuedPacket> Queue { get; } = [];
    }

    private readonly int _nodeCount;
    private readonly int _linkCapacity;
    private readonly DirectedLink[] _links;
    private ulong _currentTick;
    private ulong _prngState;

    public int NodeCount => _nodeCount;
    public int LinkCapacity => _linkCapacity;
    public ulong CurrentTick => _currentTick;
    public ulong OneWayDelayMinTicks { get; }
    public ulong OneWayDelayMeanTicks { get; }
    public long HeadDroppedCount { get; private set; }
    public long PartitionDroppedCount { get; private set; }
    public long DeliveredCount { get; private set; }
    public long ReplayedCount { get; private set; }

    public DirectedMatrixPacketSimulator(
        int nodeCount,
        int linkCapacity = 8,
        ulong oneWayDelayMinTicks = 1UL,
        ulong oneWayDelayMeanTicks = 3UL,
        ulong seed = 0x5EED_CAFE_1234_5678UL)
    {
        if (nodeCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(nodeCount),
                nodeCount,
                "[DirectedMatrixPacketSimulator] nodeCount must be positive.");
        }

        if (linkCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(linkCapacity),
                linkCapacity,
                "[DirectedMatrixPacketSimulator] linkCapacity must be positive.");
        }

        _nodeCount = nodeCount;
        _linkCapacity = linkCapacity;
        OneWayDelayMinTicks = Math.Max(1UL, oneWayDelayMinTicks);
        OneWayDelayMeanTicks = Math.Max(OneWayDelayMinTicks, oneWayDelayMeanTicks);
        _prngState = seed == 0UL ? 0x9E3779B97F4A7C15UL : seed;

        _links = new DirectedLink[nodeCount * nodeCount];
        for (int i = 0; i < _links.Length; i++)
        {
            _links[i] = new DirectedLink();
        }
    }

    private int GetLinkIndex(int source, int target)
    {
        if ((uint)source >= (uint)_nodeCount)
        {
            throw new ArgumentOutOfRangeException(nameof(source));
        }

        if ((uint)target >= (uint)_nodeCount)
        {
            throw new ArgumentOutOfRangeException(nameof(target));
        }

        return (source * _nodeCount) + target;
    }

    public bool IsLinkEnabled(int source, int target)
    {
        return _links[GetLinkIndex(source, target)].IsEnabled;
    }

    public void SetLinkEnabled(int source, int target, bool enabled)
    {
        _links[GetLinkIndex(source, target)].IsEnabled = enabled;
    }

    public void ClogFor(int source, int target, ulong ticks)
    {
        DirectedLink link = _links[GetLinkIndex(source, target)];
        link.CloggedUntilTick = _currentTick + ticks;
    }

    public int GetQueuedCount(int source, int target)
    {
        return _links[GetLinkIndex(source, target)].Queue.Count;
    }

    public void ApplyPartition(PartitionTopology topology, PartitionSymmetry symmetry, int isolatedNode = 0)
    {
        int mid = Math.Max(1, _nodeCount / 2);

        for (int src = 0; src < _nodeCount; src++)
        {
            for (int dst = 0; dst < _nodeCount; dst++)
            {
                if (src == dst)
                {
                    _links[(src * _nodeCount) + dst].IsEnabled = true;
                    continue;
                }

                bool enabled = true;
                if (topology == PartitionTopology.UniformSize)
                {
                    bool srcFirstHalf = src < mid;
                    bool dstFirstHalf = dst < mid;
                    if (srcFirstHalf != dstFirstHalf)
                    {
                        enabled = symmetry == PartitionSymmetry.Asymmetric && srcFirstHalf && !dstFirstHalf;
                    }
                }
                else if (topology == PartitionTopology.IsolateSingle)
                {
                    if (src == isolatedNode || dst == isolatedNode)
                    {
                        enabled = symmetry == PartitionSymmetry.Asymmetric && src == isolatedNode;
                    }
                }

                _links[(src * _nodeCount) + dst].IsEnabled = enabled;
            }
        }
    }

    public bool SubmitPacket(int source, int target, TPacket payload)
    {
        DirectedLink link = _links[GetLinkIndex(source, target)];
        if (!link.IsEnabled)
        {
            PartitionDroppedCount++;
            return false;
        }

        if (link.Queue.Count >= _linkCapacity)
        {
            // TigerBeetle PacketSimulator drops the oldest packet at the head of the link queue when full
            link.Queue.RemoveAt(0);
            HeadDroppedCount++;
        }

        link.HasLastPacket = true;
        link.LastPacket = payload;

        ulong delayTicks = SampleDelayTicks();
        ulong readyAt = Math.Max(_currentTick + delayTicks, link.CloggedUntilTick);
        link.Queue.Add(new QueuedPacket(readyAt, payload));
        return true;
    }

    /// <summary>
    /// Replays the last packet sent on the directed path <c>source -&gt; target</c> to simulate network duplicate/replay faults.
    /// </summary>
    public bool ReplayLastPacket(int source, int target)
    {
        DirectedLink link = _links[GetLinkIndex(source, target)];
        if (!link.HasLastPacket)
        {
            return false;
        }

        if (!SubmitPacket(source, target, link.LastPacket!))
        {
            return false;
        }

        ReplayedCount++;
        return true;
    }

    public int Step(Action<int, int, TPacket>? onDeliver = null)
    {
        _currentTick++;
        int deliveredThisTick = 0;

        for (int src = 0; src < _nodeCount; src++)
        {
            for (int dst = 0; dst < _nodeCount; dst++)
            {
                DirectedLink link = _links[(src * _nodeCount) + dst];
                if (_currentTick < link.CloggedUntilTick || link.Queue.Count == 0)
                {
                    continue;
                }

                int index = 0;
                while (index < link.Queue.Count)
                {
                    QueuedPacket pkt = link.Queue[index];
                    if (pkt.ReadyAtTick <= _currentTick)
                    {
                        link.Queue.RemoveAt(index);
                        if (link.IsEnabled)
                        {
                            DeliveredCount++;
                            deliveredThisTick++;
                            onDeliver?.Invoke(src, dst, pkt.Payload);
                        }
                        else
                        {
                            PartitionDroppedCount++;
                        }
                    }
                    else
                    {
                        index++;
                    }
                }
            }
        }

        return deliveredThisTick;
    }

    private ulong SampleDelayTicks()
    {
        ulong span = OneWayDelayMeanTicks > OneWayDelayMinTicks
            ? (OneWayDelayMeanTicks - OneWayDelayMinTicks)
            : 0UL;
        if (span == 0UL)
        {
            return OneWayDelayMinTicks;
        }

        _prngState ^= _prngState << 13;
        _prngState ^= _prngState >> 7;
        _prngState ^= _prngState << 17;
        double u = ((_prngState & 0xFFFFFFUL) + 1.0) / 16777217.0;
        ulong jitter = (ulong)Math.Min(span * 4UL, (ulong)Math.Round(-Math.Log(u) * span));
        return OneWayDelayMinTicks + jitter;
    }
}

/// <summary>
/// Wave 4 (#225, #256): Canonical TigerBeetle-inspired O(1) circular bitset + ring buffer sequence tracker
/// (<c>src/vsr/client_replies.zig</c>, <c>ClientRepliesMax = 64</c>) for reassembling out-of-order UDP snapshots
/// and rejecting duplicate/stale retransmissions with zero per-packet heap allocations.
/// </summary>
public sealed class ReplySequenceReassembler<T>
{
    public const int ClientRepliesMax = 64;

    private readonly T[] _ring = new T[ClientRepliesMax];
    private ulong _baseSequence;
    private ulong _presentMask;

    public ulong BaseSequence => _baseSequence;
    public ulong PresentMask => _presentMask;
    public long DuplicateOrStaleDropCount { get; private set; }
    public long OverflowDropCount { get; private set; }
    public int Count => BitOperations.PopCount(_presentMask);

    public ReplySequenceReassembler(ulong initialExpectedSequence = 0UL)
    {
        _baseSequence = initialExpectedSequence;
    }

    public bool Empty() => _presentMask == 0UL;

    public bool Full() => _presentMask == ulong.MaxValue;

    public bool ReadyToPop() => (_presentMask & 1UL) != 0UL;

    public bool Contains(ulong sequence)
    {
        if (sequence < _baseSequence)
        {
            return false;
        }

        ulong offset = sequence - _baseSequence;
        if (offset >= ClientRepliesMax)
        {
            return false;
        }

        return (_presentMask & (1UL << (int)offset)) != 0UL;
    }

    public bool Insert(ulong sequence, T item)
    {
        if (sequence < _baseSequence)
        {
            DuplicateOrStaleDropCount++;
            return false;
        }

        ulong offset = sequence - _baseSequence;
        if (offset >= ClientRepliesMax)
        {
            OverflowDropCount++;
            return false;
        }

        ulong bit = 1UL << (int)offset;
        if ((_presentMask & bit) != 0UL)
        {
            DuplicateOrStaleDropCount++;
            return false;
        }

        _presentMask |= bit;
        _ring[(int)(sequence % (ulong)ClientRepliesMax)] = item;
        return true;
    }

    public (ulong Sequence, T Item) Peek()
    {
        if (!ReadyToPop())
        {
            throw new InvalidOperationException(
                $"[ReplySequenceReassembler] Next expected sequence {_baseSequence} has not arrived yet.");
        }

        int slot = (int)(_baseSequence % (ulong)ClientRepliesMax);
        return (_baseSequence, _ring[slot]);
    }

    public (ulong Sequence, T Item) Pop()
    {
        if (!ReadyToPop())
        {
            throw new InvalidOperationException(
                $"[ReplySequenceReassembler] Next expected sequence {_baseSequence} has not arrived yet.");
        }

        ulong seq = _baseSequence;
        int slot = (int)(seq % (ulong)ClientRepliesMax);
        T item = _ring[slot];
        _ring[slot] = default!;
        _presentMask >>= 1;
        _baseSequence++;
        return (seq, item);
    }
}

/// <summary>
/// Clock offset drift profiles ported from TigerBeetle (<c>src/testing/time.zig</c>) (#226).
/// </summary>
public enum TimeOffsetType
{
    Linear = 0,
    Periodic = 1,
    Step = 2,
    NonIdeal = 3
}

/// <summary>
/// Wave 4 (#226, #256): Canonical TigerBeetle-inspired deterministic virtual clock simulator (<c>TimeSim</c>)
/// supporting linear drift (<c>A * t + B</c>), periodic sine-wave NTP oscillation (<c>A * sin(2*pi*t/P)</c>),
/// quantum step jumps (<c>t &gt;= C</c>), non-ideal parabolic + phase-noise thermal crystal drift, and monotonic clamping.
/// </summary>
public sealed class TigerBeetleTimeSim
{
    public long ResolutionNs { get; }
    public TimeOffsetType OffsetType { get; }
    public long AmplitudeNs { get; }
    public long ConstantOffsetNs { get; }
    public ulong PeriodTicks { get; }
    public ulong StepAtTick { get; }
    public long EpochNs { get; }

    private ulong _ticks;
    private ulong _prngState;
    private long _lastMonotonicNs;

    public ulong Ticks => _ticks;
    public long LastClampedMonotonicNs => _lastMonotonicNs;

    public TigerBeetleTimeSim(
        long resolutionNs = 10_000_000L,
        TimeOffsetType offsetType = TimeOffsetType.Linear,
        long amplitudeNs = 100_000L,
        ulong periodTicks = 100UL,
        ulong stepAtTick = 50UL,
        long epochNs = 1_700_000_000_000_000_000L,
        ulong seed = 0x71BE_71BE_1234_9876UL,
        long constantOffsetNs = 0L)
    {
        if (resolutionNs <= 0L)
        {
            throw new ArgumentOutOfRangeException(
                nameof(resolutionNs),
                resolutionNs,
                "[TigerBeetleTimeSim] resolutionNs must be positive.");
        }

        ResolutionNs = resolutionNs;
        OffsetType = offsetType;
        AmplitudeNs = amplitudeNs;
        ConstantOffsetNs = constantOffsetNs;
        PeriodTicks = Math.Max(1UL, periodTicks);
        StepAtTick = stepAtTick;
        EpochNs = epochNs;
        _prngState = seed == 0UL ? 0xCAFEBABE12345678UL : seed;
    }

    public void Tick(ulong deltaTicks = 1UL)
    {
        _ticks += deltaTicks;
        long rawNs = ((long)_ticks * ResolutionNs) + ComputeOffsetNs(_ticks);
        _lastMonotonicNs = Math.Max(_lastMonotonicNs, rawNs);
    }

    public long MonotonicNs()
    {
        return (long)_ticks * ResolutionNs;
    }

    /// <summary>
    /// Returns the drifted monotonic clock clamped so it never moves backward (<c>Math.Max(_lastMonotonicNs, rawNs)</c>).
    /// </summary>
    public long DriftedMonotonicNs()
    {
        long rawNs = ((long)_ticks * ResolutionNs) + ComputeOffsetNs(_ticks);
        _lastMonotonicNs = Math.Max(_lastMonotonicNs, rawNs);
        return _lastMonotonicNs;
    }

    public long ComputeOffsetNs(ulong atTick)
    {
        switch (OffsetType)
        {
            case TimeOffsetType.Linear:
                return ((long)atTick * AmplitudeNs) + ConstantOffsetNs;

            case TimeOffsetType.Periodic:
            {
                double radians = (2.0 * Math.PI * (double)atTick) / (double)PeriodTicks;
                return (long)Math.Round(AmplitudeNs * Math.Sin(radians)) + ConstantOffsetNs;
            }

            case TimeOffsetType.Step:
                return (atTick >= StepAtTick ? AmplitudeNs : 0L) + ConstantOffsetNs;

            case TimeOffsetType.NonIdeal:
            {
                double radians = (2.0 * Math.PI * (double)atTick) / (double)PeriodTicks;
                long periodicComponent = (long)Math.Round(AmplitudeNs * Math.Sin(radians));
                long linearDrift = (long)atTick * (AmplitudeNs / 10L);
                long parabolicDrift = (long)(atTick * atTick) * (AmplitudeNs / 100L);
                ulong mixed = _prngState ^ (atTick * 0x9E3779B97F4A7C15UL);
                mixed ^= mixed >> 30;
                mixed *= 0xBF58476D1CE4E5B9UL;
                mixed ^= mixed >> 27;
                long jitterBound = Math.Max(1L, Math.Abs(AmplitudeNs) / 5L);
                long jitter = (long)(mixed % (ulong)(jitterBound * 2L + 1L)) - jitterBound;
                return linearDrift + parabolicDrift + periodicComponent + jitter + ConstantOffsetNs;
            }

            default:
                return 0L;
        }
    }

    public long RealtimeNs()
    {
        return EpochNs + MonotonicNs() + ComputeOffsetNs(_ticks);
    }
}
