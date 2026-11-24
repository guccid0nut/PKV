// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: ReplaySnapshotEngine.cs
// System: Replay Studio Desktop GUI Tool
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Numerics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReplayStudio.GUI.Models;

namespace ReplayStudio.GUI.Engine;

/// <summary>
///     High-performance sparse keyframe snapshot ring cache and sub-millisecond state interpolator.
/// </summary>
public sealed class ReplaySnapshotEngine
{
    private readonly ILogger<ReplaySnapshotEngine> _logger;
    private GameStateSnapshot[] _sortedKeyframes = [];
    private readonly GameStateSnapshot[] _ringBuffer;
    private int _ringBufferIndex;
    private readonly Lock _syncLock = new();

    public int TotalDurationMs { get; private set; }
    public int KeyframeCount => _sortedKeyframes.Length;
    public IReadOnlyList<GameStateSnapshot> Keyframes => _sortedKeyframes;

    public ReplaySnapshotEngine(int ringBufferSize = 128, ILogger<ReplaySnapshotEngine>? logger = null)
    {
        _logger = logger ?? NullLogger<ReplaySnapshotEngine>.Instance;
        _ringBuffer = new GameStateSnapshot[Math.Max(16, ringBufferSize)];
        for (int i = 0; i < _ringBuffer.Length; i++)
        {
            _ringBuffer[i] = new GameStateSnapshot();
        }
        _logger.LogInformation("[ReplaySnapshotEngine] Initialized with circular ring buffer capacity {Capacity}", _ringBuffer.Length);
    }

    /// <summary>
    ///     Loads and indexes a collection of match keyframe snapshots.
    /// </summary>
    public void LoadKeyframes(IEnumerable<GameStateSnapshot> snapshots, int totalDurationMs)
    {
        ArgumentNullException.ThrowIfNull(snapshots);

        lock (_syncLock)
        {
            _sortedKeyframes = snapshots.OrderBy(s => s.TimestampMs).ToArray();
            TotalDurationMs = Math.Max(0, totalDurationMs);

            if (_sortedKeyframes.Length == 0)
            {
                GameStateSnapshot initial = new() { TimestampMs = 0, IsKeyframe = true, KeyframeTag = "Origin" };
                for (int i = 0; i < 10; i++)
                {
                    initial.Heroes[i] = new HeroSnapshot { ClientIndex = i, Team = i < 5 ? 1 : 2 };
                }
                _sortedKeyframes = [initial];
            }

            _logger.LogInformation(
                "[ReplaySnapshotEngine] Indexed {Count} keyframes across duration {DurationMs}ms (Span: {StartMs}ms -> {EndMs}ms)",
                _sortedKeyframes.Length,
                TotalDurationMs,
                _sortedKeyframes[0].TimestampMs,
                _sortedKeyframes[^1].TimestampMs);
        }
    }

    /// <summary>
    ///     Performs an O(log N) binary search to find the nearest preceding keyframe.
    /// </summary>
    public GameStateSnapshot SeekNearestKeyframe(int targetTimeMs)
    {
        lock (_syncLock)
        {
            if (_sortedKeyframes.Length == 0) return new GameStateSnapshot();

            if (targetTimeMs <= _sortedKeyframes[0].TimestampMs)
            {
                return _sortedKeyframes[0];
            }

            if (targetTimeMs >= _sortedKeyframes[^1].TimestampMs)
            {
                return _sortedKeyframes[^1];
            }

            int low = 0;
            int high = _sortedKeyframes.Length - 1;
            int bestIndex = 0;

            while (low <= high)
            {
                int mid = (low + high) / 2;
                if (_sortedKeyframes[mid].TimestampMs <= targetTimeMs)
                {
                    bestIndex = mid;
                    low = mid + 1;
                }
                else
                {
                    high = mid - 1;
                }
            }

            return _sortedKeyframes[bestIndex];
        }
    }

    /// <summary>
    ///     Retrieves the bounding keyframes (S0, S1) and normalized progression alpha for an arbitrary timestamp.
    /// </summary>
    public (GameStateSnapshot S0, GameStateSnapshot S1, float Alpha) GetSurroundingKeyframes(int targetTimeMs)
    {
        lock (_syncLock)
        {
            if (_sortedKeyframes.Length == 0)
            {
                GameStateSnapshot empty = new();
                return (empty, empty, 0f);
            }

            if (targetTimeMs <= _sortedKeyframes[0].TimestampMs)
            {
                return (_sortedKeyframes[0], _sortedKeyframes[0], 0f);
            }

            int lastIdx = _sortedKeyframes.Length - 1;
            if (targetTimeMs >= _sortedKeyframes[lastIdx].TimestampMs)
            {
                return (_sortedKeyframes[lastIdx], _sortedKeyframes[lastIdx], 0f);
            }

            int low = 0;
            int high = lastIdx;
            int idx0 = 0;

            while (low <= high)
            {
                int mid = (low + high) / 2;
                if (_sortedKeyframes[mid].TimestampMs <= targetTimeMs)
                {
                    idx0 = mid;
                    low = mid + 1;
                }
                else
                {
                    high = mid - 1;
                }
            }

            int idx1 = Math.Min(idx0 + 1, lastIdx);
            GameStateSnapshot s0 = _sortedKeyframes[idx0];
            GameStateSnapshot s1 = _sortedKeyframes[idx1];

            int deltaT = s1.TimestampMs - s0.TimestampMs;
            float alpha = deltaT > 0 ? Math.Clamp((float)(targetTimeMs - s0.TimestampMs) / deltaT, 0f, 1f) : 0f;

            return (s0, s1, alpha);
        }
    }

    /// <summary>
    ///     Computes an interpolated snapshot at any arbitrary sub-millisecond timestamp using the zero-GC ring buffer.
    /// </summary>
    public GameStateSnapshot GetInterpolatedSnapshot(int targetTimeMs)
    {
        (GameStateSnapshot s0, GameStateSnapshot s1, float alpha) = GetSurroundingKeyframes(targetTimeMs);

        lock (_syncLock)
        {
            GameStateSnapshot target = _ringBuffer[_ringBufferIndex];
            _ringBufferIndex = (_ringBufferIndex + 1) % _ringBuffer.Length;

            target.TimestampMs = targetTimeMs;
            target.ScoreLegion = alpha < 0.5f ? s0.ScoreLegion : s1.ScoreLegion;
            target.ScoreHellbourne = alpha < 0.5f ? s0.ScoreHellbourne : s1.ScoreHellbourne;
            target.NetWorthAdvantage = (int)Math.Round(s0.NetWorthAdvantage + (s1.NetWorthAdvantage - s0.NetWorthAdvantage) * alpha);
            target.IsKeyframe = alpha == 0f || alpha == 1f;
            target.KeyframeTag = alpha == 0f ? s0.KeyframeTag : (alpha == 1f ? s1.KeyframeTag : string.Empty);

            // Populate active events from surrounding keyframe
            target.ActiveEvents.Clear();
            if (alpha < 0.5f)
            {
                for (int e = 0; e < s0.ActiveEvents.Count; e++)
                {
                    target.ActiveEvents.Add(s0.ActiveEvents[e]);
                }
            }
            else
            {
                for (int e = 0; e < s1.ActiveEvents.Count; e++)
                {
                    target.ActiveEvents.Add(s1.ActiveEvents[e]);
                }
            }

            for (int i = 0; i < 10; i++)
            {
                HeroSnapshot h0 = s0.Heroes[i];
                HeroSnapshot h1 = s1.Heroes[i];

                target.Heroes[i] ??= new HeroSnapshot { ClientIndex = i };
                InterpolateHeroInto(h0, h1, alpha, target.Heroes[i]);
            }

            return target;
        }
    }

    /// <summary>
    ///     Interpolates a hero's state without heap allocations into a preallocated destination snapshot.
    /// </summary>
    public static void InterpolateHeroInto(HeroSnapshot h0, HeroSnapshot h1, float alpha, HeroSnapshot dest)
    {
        float clampedAlpha = Math.Clamp(alpha, 0.0f, 1.0f);

        dest.ClientIndex = h0.ClientIndex;
        dest.Team = h0.Team;
        dest.PlayerName = h0.PlayerName;
        dest.HeroName = h0.HeroName;
        dest.MaxHealth = h0.MaxHealth;
        dest.MaxMana = h0.MaxMana;

        // Position Lerp
        dest.Position = Vector3.Lerp(h0.Position, h1.Position, clampedAlpha);

        // Heading Shortest-Arc Lerp
        dest.HeadingAngle = InterpolateHeading(h0.HeadingAngle, h1.HeadingAngle, clampedAlpha);

        // HP & MP Lerp
        dest.Health = Math.Clamp(h0.Health + (h1.Health - h0.Health) * clampedAlpha, 0f, h0.MaxHealth);
        dest.Mana = Math.Clamp(h0.Mana + (h1.Mana - h0.Mana) * clampedAlpha, 0f, h0.MaxMana);

        // Discrete attributes
        dest.IsAlive = clampedAlpha < 0.95f ? h0.IsAlive : h1.IsAlive;
        dest.Level = clampedAlpha < 0.5f ? h0.Level : h1.Level;
        dest.Kills = clampedAlpha < 0.5f ? h0.Kills : h1.Kills;
        dest.Deaths = clampedAlpha < 0.5f ? h0.Deaths : h1.Deaths;
        dest.Assists = clampedAlpha < 0.5f ? h0.Assists : h1.Assists;
        dest.Gold = (int)(h0.Gold + (h1.Gold - h0.Gold) * clampedAlpha);
        dest.NetWorth = (int)(h0.NetWorth + (h1.NetWorth - h0.NetWorth) * clampedAlpha);
        dest.GoldPerMinute = h0.GoldPerMinute;
        dest.ExpPerMinute = h0.ExpPerMinute;
        dest.RespawnTimerMs = (int)(h0.RespawnTimerMs + (h1.RespawnTimerMs - h0.RespawnTimerMs) * clampedAlpha);
        dest.ActiveAnimation = (dest.Position != h0.Position && dest.IsAlive) ? "walk" : (dest.IsAlive ? "idle" : "death");

        // Copy item array
        dest.Items = h0.Items;
    }

    /// <summary>
    ///     Shortest-arc heading angle interpolation preventing wrap-around spin.
    ///     Delta = ((angle1 - angle0 + 540) % 360) - 180.
    /// </summary>
    public static float InterpolateHeading(float angle0, float angle1, float alpha)
    {
        float diff = ((angle1 - angle0 + 540f) % 360f) - 180f;
        float result = (angle0 + diff * alpha + 360f) % 360f;
        return result;
    }

    /// <summary>
    ///     Creates a readonly HeroRenderState struct for viewport rendering.
    /// </summary>
    public static HeroRenderState CreateRenderState(HeroSnapshot h)
    {
        return new HeroRenderState(
            h.ClientIndex,
            h.Team,
            h.PlayerName,
            h.CleanHeroName,
            h.Position,
            h.HeadingAngle,
            h.Health,
            h.MaxHealth,
            h.Mana,
            h.MaxMana,
            h.HealthPercent,
            h.ManaPercent,
            h.IsAlive,
            h.ActiveAnimation
        );
    }
}
