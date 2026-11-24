// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

namespace Kongor.Cli.Mcp.Services;

/// <summary>
/// Bitflags specifying which unit attributes changed in this tick.
/// Minimizes serialization and network payload sizes during high-frequency streaming.
/// </summary>
[Flags]
public enum DeltaPropertyMask : uint
{
    None = 0,
    Position = 1 << 0,       // X, Y, Z coordinates changed
    Health = 1 << 1,         // HP percentage changed
    Mana = 1 << 2,           // Mana percentage changed
    Status = 1 << 3,         // Alive/Dead/Stunned state changed
    Inventory = 1 << 4,      // Items / Cooldowns changed
    Scoreboard = 1 << 5,     // Kills, Deaths, Assists, Gold, CS changed
    Vision = 1 << 6,         // Ward vision cone / Fog-of-War changed
    FullSnapshot = 0xFFFFFFFF // Keyframe full state
}

/// <summary>
/// Replicated entity state for a single hero or creep unit.
/// </summary>
public sealed class SpectatorUnitState
{
    public int UnitId { get; set; }
    public string UnitType { get; set; } = string.Empty;
    public int Team { get; set; } // 1 = Legion, 2 = Hellbourne
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float HpPercent { get; set; } = 1.0f;
    public float ManaPercent { get; set; } = 1.0f;
    public bool IsAlive { get; set; } = true;
    public int Kills { get; set; }
    public int Deaths { get; set; }
    public int Assists { get; set; }
}

/// <summary>
/// Individual delta or keyframe tick in the spectator stream.
/// </summary>
public sealed class DeltaFrame
{
    public required uint TickId { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public bool IsKeyframe { get; init; }
    public DeltaPropertyMask PropertyMask { get; init; } = DeltaPropertyMask.FullSnapshot;
    public required List<SpectatorUnitState> Units { get; init; }
}

/// <summary>
/// Monotonic 128-slot circular ring buffer for real-time match spectating.
/// Provides sub-frame packet recovery, desync detection, and dual-stream
/// delay enforcement (0s Live Referee vs 120s Delayed Spectator).
/// </summary>
public sealed class SpectatorRingBuffer
{
    public const int Capacity = 128;
    private const int IndexMask = Capacity - 1; // 127

    private readonly DeltaFrame?[] _buffer = new DeltaFrame?[Capacity];
    private readonly Lock _lock = new();

    private uint _headTick;
    private uint _tailTick;
    private int _count;

    public int Count
    {
        get
        {
            lock (_lock) return _count;
        }
    }

    public uint LatestTickId
    {
        get
        {
            lock (_lock) return _count > 0 ? _headTick : 0;
        }
    }

    public uint EarliestTickId
    {
        get
        {
            lock (_lock) return _count > 0 ? _tailTick : 0;
        }
    }

    /// <summary>
    /// Pushes a delta frame into the 128-slot circular buffer.
    /// Overwrites the oldest slot when full.
    /// </summary>
    public void Push(DeltaFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        lock (_lock)
        {
            int slot = (int)(frame.TickId & IndexMask);
            _buffer[slot] = frame;
            _headTick = frame.TickId;

            if (_count == 0)
            {
                _tailTick = frame.TickId;
                _count = 1;
            }
            else
            {
                if (frame.TickId - _tailTick >= Capacity)
                {
                    _tailTick = frame.TickId - Capacity + 1;
                    _count = Capacity;
                }
                else
                {
                    _count = (int)(_headTick - _tailTick + 1);
                    if (_count > Capacity) _count = Capacity;
                }
            }
        }
    }

    /// <summary>
    /// Retrieves all deltas since the specified tick ID.
    /// If the client is desynced past buffer capacity (>128 ticks), sets requiresFullSnapshot = true.
    /// </summary>
    public List<DeltaFrame> GetDeltasSince(uint sinceTick, out bool requiresFullSnapshot)
    {
        lock (_lock)
        {
            if (_count == 0)
            {
                requiresFullSnapshot = false;
                return [];
            }

            // Client is too far behind (gap > capacity) or asking from 0
            if (sinceTick == 0 || sinceTick < _tailTick)
            {
                requiresFullSnapshot = true;
                // Return latest keyframe + following deltas
                return GetLatestKeyframeSequence();
            }

            // Client is already up to date
            if (sinceTick >= _headTick)
            {
                requiresFullSnapshot = false;
                return [];
            }

            requiresFullSnapshot = false;
            List<DeltaFrame> result = [];
            for (uint t = sinceTick + 1; t <= _headTick; t++)
            {
                int slot = (int)(t & IndexMask);
                var frame = _buffer[slot];
                if (frame != null && frame.TickId == t)
                {
                    result.Add(frame);
                }
            }

            return result;
        }
    }

    /// <summary>
    /// Retrieves the frame closest to (Timestamp - delay) for delayed spectator streams (e.g. 120s delay).
    /// </summary>
    public DeltaFrame? GetDelayedFrame(TimeSpan delay, DateTimeOffset? now = null)
    {
        DateTimeOffset targetTime = (now ?? TimeProvider.System.GetUtcNow()) - delay;

        lock (_lock)
        {
            if (_count == 0) return null;

            DeltaFrame? best = null;
            TimeSpan minDiff = TimeSpan.MaxValue;

            for (uint t = _tailTick; t <= _headTick; t++)
            {
                int slot = (int)(t & IndexMask);
                var frame = _buffer[slot];
                if (frame != null && frame.TickId == t)
                {
                    TimeSpan diff = (frame.Timestamp - targetTime).Duration();
                    if (diff < minDiff)
                    {
                        minDiff = diff;
                        best = frame;
                    }
                }
            }

            return best;
        }
    }

    /// <summary>
    /// Retrieves the most recent frame instantly (Live Referee stream with 0s delay).
    /// </summary>
    public DeltaFrame? GetLatest()
    {
        lock (_lock)
        {
            if (_count == 0) return null;
            int slot = (int)(_headTick & IndexMask);
            return _buffer[slot];
        }
    }

    private List<DeltaFrame> GetLatestKeyframeSequence()
    {
        // Find most recent keyframe
        uint keyframeTick = _headTick;
        for (uint t = _headTick; t >= _tailTick; t--)
        {
            int slot = (int)(t & IndexMask);
            var frame = _buffer[slot];
            if (frame != null && frame.IsKeyframe)
            {
                keyframeTick = t;
                break;
            }
            if (t == 0) break;
        }

        List<DeltaFrame> result = [];
        for (uint t = keyframeTick; t <= _headTick; t++)
        {
            int slot = (int)(t & IndexMask);
            var frame = _buffer[slot];
            if (frame != null && frame.TickId == t)
            {
                result.Add(frame);
            }
        }

        return result;
    }
}
