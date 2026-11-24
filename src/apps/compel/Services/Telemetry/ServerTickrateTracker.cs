// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: ServerTickrateTracker.cs
// System: COMPEL - High-Performance Dedicated Server Tickrate & Jitter Tracker
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

namespace COMPEL.Services.Telemetry;

/// <summary>
/// High-resolution, zero-allocation circular buffer monitoring game server simulation tickrates,
/// frame intervals, execution durations, and tick jitter with sub-millisecond precision.
/// </summary>
public sealed class ServerTickrateTracker
{
    private readonly int _targetTickRate;
    private readonly int _windowSize;
    private readonly TimeProvider _timeProvider;
    private readonly double[] _samples;
    private readonly Lock _syncLock = new();

    private int _head;
    private int _count;
    private long _lastTimestamp;
    private long _slowTickCount;

    private double _currentTickrateHz;
    private double _averageTickDurationMs;
    private double _tickVarianceMs;
    private double _p95TickDurationMs;
    private double _p99TickDurationMs;

    /// <summary>
    /// Initializes a new instance of <see cref="ServerTickrateTracker"/>.
    /// </summary>
    /// <param name="targetTickRate">Expected server simulation tickrate (e.g. 20 Hz, 30 Hz, 60 Hz).</param>
    /// <param name="windowSize">Number of samples in the rolling sliding window (default 64).</param>
    /// <param name="timeProvider">Optional time provider for deterministic testing.</param>
    public ServerTickrateTracker(int targetTickRate = 20, int windowSize = 64, TimeProvider? timeProvider = null)
    {
        _targetTickRate = Math.Clamp(targetTickRate, 10, 128);
        _windowSize = Math.Max(16, windowSize);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _samples = new double[_windowSize];

        _currentTickrateHz = _targetTickRate;
        _averageTickDurationMs = 1000.0 / _targetTickRate;
        _p95TickDurationMs = _averageTickDurationMs;
        _p99TickDurationMs = _averageTickDurationMs;
    }

    /// <summary>
    /// Expected simulation tickrate in Hertz (e.g. 60 or 20).
    /// </summary>
    public int TargetTickRate => _targetTickRate;

    /// <summary>
    /// Expected duration of a single simulation frame in milliseconds.
    /// </summary>
    public double TargetTickDurationMs => 1000.0 / _targetTickRate;

    /// <summary>
    /// Threshold duration above which a tick is classified as lagging/slow (1.25x target).
    /// </summary>
    public double SlowTickThresholdMs => TargetTickDurationMs * 1.25;

    /// <summary>
    /// Rolling dedicated server simulation tickrate in Hertz.
    /// </summary>
    public double CurrentTickrateHz
    {
        get
        {
            lock (_syncLock)
            {
                return _currentTickrateHz;
            }
        }
    }

    /// <summary>
    /// Rolling average frame tick execution duration in milliseconds.
    /// </summary>
    public double AverageTickDurationMs
    {
        get
        {
            lock (_syncLock)
            {
                return _averageTickDurationMs;
            }
        }
    }

    /// <summary>
    /// Rolling standard deviation of tick intervals in milliseconds (tick jitter).
    /// </summary>
    public double TickVarianceMs
    {
        get
        {
            lock (_syncLock)
            {
                return _tickVarianceMs;
            }
        }
    }

    /// <summary>
    /// 95th percentile tick duration in milliseconds.
    /// </summary>
    public double P95TickDurationMs
    {
        get
        {
            lock (_syncLock)
            {
                return _p95TickDurationMs;
            }
        }
    }

    /// <summary>
    /// 99th percentile tick duration in milliseconds.
    /// </summary>
    public double P99TickDurationMs
    {
        get
        {
            lock (_syncLock)
            {
                return _p99TickDurationMs;
            }
        }
    }

    /// <summary>
    /// Cumulative count of simulation ticks exceeding 1.25x target duration.
    /// </summary>
    public long SlowTickCount => Interlocked.Read(ref _slowTickCount);

    /// <summary>
    /// Whether the dedicated server has failed to tick for longer than 3x target duration or 500ms.
    /// </summary>
    public bool IsStalled
    {
        get
        {
            long last = Volatile.Read(ref _lastTimestamp);
            if (last == 0) return false;

            long elapsedTicks = _timeProvider.GetTimestamp() - last;
            double elapsedMs = (double)elapsedTicks / _timeProvider.TimestampFrequency * 1000.0;
            return elapsedMs > Math.Max(50.0, TargetTickDurationMs * 3.0);
        }
    }

    /// <summary>
    /// Records a simulation tick event using high-resolution timestamps.
    /// Measures the elapsed interval since the previous tick and updates rolling statistics.
    /// </summary>
    public void RecordTick()
    {
        long now = _timeProvider.GetTimestamp();

        lock (_syncLock)
        {
            if (_lastTimestamp == 0)
            {
                _lastTimestamp = now;
                return;
            }

            long deltaTicks = now - _lastTimestamp;
            _lastTimestamp = now;

            double durationMs = (double)deltaTicks / _timeProvider.TimestampFrequency * 1000.0;
            RecordDurationInternal(durationMs);
        }
    }

    /// <summary>
    /// Explicitly records a measured frame simulation duration in milliseconds.
    /// </summary>
    public void RecordTickDuration(double durationMs)
    {
        lock (_syncLock)
        {
            RecordDurationInternal(durationMs);
        }
    }

    private void RecordDurationInternal(double durationMs)
    {
        _samples[_head] = durationMs;
        _head = (_head + 1) % _windowSize;
        if (_count < _windowSize) _count++;

        if (durationMs > SlowTickThresholdMs)
        {
            Interlocked.Increment(ref _slowTickCount);
            CompelMetrics.ServerSlowTicks.Add(1);
        }

        // Compute rolling mean
        double sum = 0.0;
        for (int i = 0; i < _count; i++)
        {
            sum += _samples[i];
        }

        double mean = sum / _count;
        _averageTickDurationMs = mean;

        if (mean > 0.0001)
        {
            _currentTickrateHz = 1000.0 / mean;
        }

        // Compute rolling variance and standard deviation
        double sumSquaredDiff = 0.0;
        for (int i = 0; i < _count; i++)
        {
            double diff = _samples[i] - mean;
            sumSquaredDiff += diff * diff;
        }

        _tickVarianceMs = Math.Sqrt(sumSquaredDiff / _count);

        // Compute percentiles using stack allocation (zero GC heap allocation)
        if (_count <= 128)
        {
            Span<double> sorted = stackalloc double[_count];
            _samples.AsSpan(0, _count).CopyTo(sorted);
            sorted.Sort();

            int p95Index = Math.Min(_count - 1, (int)(_count * 0.95));
            int p99Index = Math.Min(_count - 1, (int)(_count * 0.99));

            _p95TickDurationMs = sorted[p95Index];
            _p99TickDurationMs = sorted[p99Index];
        }

        // Emit to OpenTelemetry metrics
        CompelMetrics.ServerTickDurationMs.Record(durationMs);
        CompelMetrics.ServerTickrateHz.Record(_currentTickrateHz);
        CompelMetrics.ServerTickVarianceMs.Record(_tickVarianceMs);
    }

    /// <summary>
    /// Clears all rolling tick samples and resets counters.
    /// </summary>
    public void Reset()
    {
        lock (_syncLock)
        {
            Array.Clear(_samples);
            _head = 0;
            _count = 0;
            _lastTimestamp = 0;
            _currentTickrateHz = _targetTickRate;
            _averageTickDurationMs = TargetTickDurationMs;
            _tickVarianceMs = 0.0;
            _p95TickDurationMs = _averageTickDurationMs;
            _p99TickDurationMs = _averageTickDurationMs;
            Interlocked.Exchange(ref _slowTickCount, 0);
        }
    }
}
