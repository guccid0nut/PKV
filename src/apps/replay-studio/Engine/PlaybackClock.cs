// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: PlaybackClock.cs
// System: Replay Studio Desktop GUI Tool
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ReplayStudio.GUI.Engine;

/// <summary>
///     Enumerates the playback execution states of the replay timeline.
/// </summary>
public enum PlaybackState
{
    Stopped = 0,
    Playing = 1,
    Paused = 2
}

/// <summary>
///     High-precision playback clock controller supporting variable speed multipliers (0.25x to 8.0x),
///     single-tick stepping (+/- 1 tick @ 50ms), and zero cumulative drift timebase tracking.
/// </summary>
public sealed class PlaybackClock
{
    public const int DefaultTickDeltaMs = 50; // 20 ticks per second

    private readonly ILogger<PlaybackClock> _logger;
    private readonly Lock _lock = new();
    private PlaybackState _state = PlaybackState.Stopped;
    private float _speedMultiplier = 1.0f;
    private double _virtualTimeMs;
    private long _lastTimestampTicks;

    public static readonly float[] ValidSpeeds = [0.25f, 0.5f, 1.0f, 2.0f, 4.0f, 8.0f];

    public int TotalDurationMs { get; set; }
    public int InMarkerMs { get; set; }
    public int OutMarkerMs { get; set; }
    public bool LoopPlayback { get; set; }

    public int CurrentTimeMs
    {
        get
        {
            lock (_lock) return (int)Math.Clamp(_virtualTimeMs, 0, Math.Max(0, TotalDurationMs));
        }
        set
        {
            int clampedValue;
            lock (_lock)
            {
                _virtualTimeMs = Math.Clamp(value, 0, Math.Max(0, TotalDurationMs));
                _lastTimestampTicks = Stopwatch.GetTimestamp();
                clampedValue = (int)_virtualTimeMs;
            }
            TimeUpdated?.Invoke(clampedValue);
        }
    }

    public PlaybackState State
    {
        get { lock (_lock) return _state; }
        private set
        {
            lock (_lock) _state = value;
            StateChanged?.Invoke(value);
        }
    }

    public float SpeedMultiplier
    {
        get { lock (_lock) return _speedMultiplier; }
        set
        {
            float clamped;
            lock (_lock)
            {
                _speedMultiplier = Math.Clamp(value, 0.25f, 8.0f);
                clamped = _speedMultiplier;
            }
            _logger.LogInformation("[PlaybackClock] Speed multiplier set to {Speed:F2}x", clamped);
            SpeedChanged?.Invoke(clamped);
        }
    }

    public bool IsPlaying => State == PlaybackState.Playing;
    public bool IsPaused => State == PlaybackState.Paused;
    public bool IsStopped => State == PlaybackState.Stopped;

    public event Action<int>? TimeUpdated;
    public event Action<PlaybackState>? StateChanged;
    public event Action<float>? SpeedChanged;

    public PlaybackClock(ILogger<PlaybackClock>? logger = null)
    {
        _logger = logger ?? NullLogger<PlaybackClock>.Instance;
    }

    public void Play()
    {
        lock (_lock)
        {
            if (_state == PlaybackState.Playing) return;

            int effectiveEnd = (OutMarkerMs > InMarkerMs && OutMarkerMs <= TotalDurationMs) ? OutMarkerMs : TotalDurationMs;
            if (_virtualTimeMs >= effectiveEnd)
            {
                _virtualTimeMs = InMarkerMs;
            }

            _lastTimestampTicks = Stopwatch.GetTimestamp();
            _state = PlaybackState.Playing;
            _logger.LogInformation("[PlaybackClock] Playback started at {TimeMs}ms (Speed: {Speed}x)", (int)_virtualTimeMs, _speedMultiplier);
        }
        StateChanged?.Invoke(PlaybackState.Playing);
    }

    public void Pause()
    {
        lock (_lock)
        {
            if (_state != PlaybackState.Playing) return;
            _state = PlaybackState.Paused;
            _logger.LogInformation("[PlaybackClock] Playback paused at {TimeMs}ms", (int)_virtualTimeMs);
        }
        StateChanged?.Invoke(PlaybackState.Paused);
    }

    public void TogglePlayPause()
    {
        if (IsPlaying) Pause();
        else Play();
    }

    public void Stop()
    {
        lock (_lock)
        {
            _virtualTimeMs = InMarkerMs;
            _state = PlaybackState.Stopped;
            _logger.LogInformation("[PlaybackClock] Playback stopped at {TimeMs}ms", InMarkerMs);
        }
        StateChanged?.Invoke(PlaybackState.Stopped);
        TimeUpdated?.Invoke(InMarkerMs);
    }

    /// <summary>
    ///     Advances or rewinds playback by a discrete number of ticks (deltaTicks * 50ms).
    /// </summary>
    public void StepTick(int deltaTicks = 1, int tickDeltaMs = DefaultTickDeltaMs)
    {
        int newTime;
        bool stateChanged = false;
        lock (_lock)
        {
            if (_state != PlaybackState.Paused)
            {
                _state = PlaybackState.Paused;
                stateChanged = true;
            }
            _virtualTimeMs = Math.Clamp(_virtualTimeMs + (deltaTicks * tickDeltaMs), 0, TotalDurationMs);
            _lastTimestampTicks = Stopwatch.GetTimestamp();
            newTime = (int)_virtualTimeMs;
            _logger.LogDebug("[PlaybackClock] StepTick {DeltaTicks} ({DeltaMs}ms) -> New Time: {TimeMs}ms", deltaTicks, deltaTicks * tickDeltaMs, newTime);
        }

        if (stateChanged)
        {
            StateChanged?.Invoke(PlaybackState.Paused);
        }

        TimeUpdated?.Invoke(newTime);
    }

    /// <summary>
    ///     Updates the virtual playback clock using an invariant wall-clock frequency delta.
    ///     Guarantees zero cumulative drift over long continuous runs.
    /// </summary>
    public void Update()
    {
        int emitTime = -1;
        PlaybackState transitionState = PlaybackState.Playing;
        bool shouldTransition = false;

        lock (_lock)
        {
            if (_state != PlaybackState.Playing) return;

            long currentTicks = Stopwatch.GetTimestamp();
            long elapsedTicks = currentTicks - _lastTimestampTicks;
            _lastTimestampTicks = currentTicks;

            double elapsedMs = (elapsedTicks * 1000.0) / Stopwatch.Frequency;
            _virtualTimeMs += elapsedMs * _speedMultiplier;

            int effectiveEnd = (OutMarkerMs > InMarkerMs && OutMarkerMs <= TotalDurationMs) ? OutMarkerMs : TotalDurationMs;
            if (_virtualTimeMs >= effectiveEnd && effectiveEnd > 0)
            {
                if (LoopPlayback)
                {
                    _virtualTimeMs = InMarkerMs;
                }
                else
                {
                    _virtualTimeMs = effectiveEnd;
                    _state = PlaybackState.Paused;
                    shouldTransition = true;
                    transitionState = PlaybackState.Paused;
                }
            }

            emitTime = (int)_virtualTimeMs;
        }

        if (shouldTransition)
        {
            StateChanged?.Invoke(transitionState);
        }

        if (emitTime >= 0)
        {
            TimeUpdated?.Invoke(emitTime);
        }
    }
}
