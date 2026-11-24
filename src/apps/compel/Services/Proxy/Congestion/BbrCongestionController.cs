// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: BbrCongestionController.cs
// System: COMPEL Proxy - BBR (Bottleneck Bandwidth and RTT) UDP Congestion Controller
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

namespace COMPEL.Services.Proxy.Congestion;

/// <summary>
///     BBR state machine phases.
/// </summary>
public enum BbrPhase : byte
{
    Startup = 0,
    Drain = 1,
    ProbeBW = 2,
    ProbeRTT = 3
}

/// <summary>
///     Google BBR (Bottleneck Bandwidth and RTT) congestion control model tailored for high-tick UDP game netcode.
///     Unlike loss-based congestion controls (CUBIC/Reno) which induce bufferbloat and packet drops,
///     BBR estimates the physical line bottleneck and paces packets to maintain zero-queue propagation latency.
/// </summary>
public sealed class BbrCongestionController
{
    // BBR Constants
    public const double HighGain = 2.885; // 2 / ln(2)
    public const double DrainGain = 0.35; // 1 / HighGain
    public const double ProbeBwGainUp = 1.25;
    public const double ProbeBwGainDown = 0.75;
    public const double ProbeBwGainCruise = 1.0;

    public const long DefaultInitialBandwidthBps = 1_000_000L; // 1 MB/s initial probe
    public const long MinPacingRateBps = 128_000L; // 128 KB/s floor to prevent game starvation
    public const int MinCwndBytes = 14_000; // ~10 packets minimum in flight

    private static readonly double[] ProbeBwGainCycle = [ProbeBwGainUp, ProbeBwGainDown, ProbeBwGainCruise, ProbeBwGainCruise];

    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _minRttFilterWindow = TimeSpan.FromSeconds(10);
    private readonly TimeSpan _probeRttDuration = TimeSpan.FromMilliseconds(200);

    private BbrPhase _phase = BbrPhase.Startup;
    private double _pacingGain = HighGain;
    private int _probeBwIndex;

    private long _maxBandwidthBps = DefaultInitialBandwidthBps;
    private TimeSpan _minRtt = TimeSpan.FromMilliseconds(50);
    private long _minRttTimestampTicks;
    private long _probeRttStartTicks;

    private long _roundCount;
    private long _bytesDeliveredInRound;
    private long _roundStartTicks;
    private long _previousRoundMaxBw;
    private long _drainStartRound;

    public BbrPhase Phase => _phase;
    public double PacingGain => _pacingGain;
    public long MaxBandwidthBytesPerSec => Volatile.Read(ref _maxBandwidthBps);
    public TimeSpan MinRtt => _minRtt;

    public BbrCongestionController(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _minRttTimestampTicks = _timeProvider.GetTimestamp();
        _roundStartTicks = _timeProvider.GetTimestamp();
    }

    /// <summary>
    ///     Returns the computed pacing rate in bytes per second.
    /// </summary>
    public long GetPacingRateBytesPerSec()
    {
        long rate = (long)(Volatile.Read(ref _maxBandwidthBps) * _pacingGain);
        return Math.Max(MinPacingRateBps, rate);
    }

    /// <summary>
    ///     Returns the target congestion window (Bandwidth-Delay Product + headroom) in bytes.
    /// </summary>
    public int GetTargetCongestionWindowBytes()
    {
        if (_phase == BbrPhase.ProbeRTT)
        {
            return MinCwndBytes;
        }

        double bdp = Volatile.Read(ref _maxBandwidthBps) * _minRtt.TotalSeconds;
        int target = (int)Math.Round(bdp * 2.0); // 2x BDP headroom for burst tolerances
        return Math.Max(MinCwndBytes, target);
    }

    /// <summary>
    ///     Processes an acknowledged packet burst, updating bandwidth and RTT filters.
    /// </summary>
    public void OnAckReceived(long bytesAcked, TimeSpan measuredRtt)
    {
        if (bytesAcked <= 0)
        {
            return;
        }

        long nowTicks = _timeProvider.GetTimestamp();

        // 1. Update Min RTT if a strictly lower sample is observed
        if (measuredRtt > TimeSpan.Zero && measuredRtt < _minRtt)
        {
            _minRtt = measuredRtt;
            _minRttTimestampTicks = nowTicks;
        }

        // 2. Measure delivery rate for this sample
        if (measuredRtt.TotalSeconds > 0)
        {
            long sampleBw = (long)(bytesAcked / measuredRtt.TotalSeconds);
            if (sampleBw > Volatile.Read(ref _maxBandwidthBps))
            {
                Volatile.Write(ref _maxBandwidthBps, sampleBw);
            }
        }

        _bytesDeliveredInRound += bytesAcked;

        // 3. Advance delivery round every RTT interval
        if (_timeProvider.GetElapsedTime(_roundStartTicks) >= _minRtt)
        {
            AdvanceRound(nowTicks);
        }

        // 4. Update BBR state machine
        UpdatePhase(nowTicks, measuredRtt);
    }

    private void AdvanceRound(long nowTicks)
    {
        _roundCount++;
        _roundStartTicks = nowTicks;
        _previousRoundMaxBw = Volatile.Read(ref _maxBandwidthBps);
        _bytesDeliveredInRound = 0;
    }

    private void UpdatePhase(long nowTicks, TimeSpan measuredRtt)
    {
        switch (_phase)
        {
            case BbrPhase.Startup:
                // If bandwidth growth slows (< 25% increase over round), transition to Drain
                if (_roundCount >= 3 && Volatile.Read(ref _maxBandwidthBps) < (long)(_previousRoundMaxBw * 1.25))
                {
                    _phase = BbrPhase.Drain;
                    _pacingGain = DrainGain;
                    _drainStartRound = _roundCount;
                }
                break;

            case BbrPhase.Drain:
                // Stay in Drain for at least one full round to drain bottleneck buffer before ProbeBW
                if (_roundCount > _drainStartRound)
                {
                    _phase = BbrPhase.ProbeBW;
                    _probeBwIndex = 0;
                    _pacingGain = ProbeBwGainCycle[_probeBwIndex];
                }
                break;

            case BbrPhase.ProbeBW:
                // Check if min RTT has expired (needs ProbeRTT refresh)
                if (_timeProvider.GetElapsedTime(_minRttTimestampTicks) > _minRttFilterWindow)
                {
                    _phase = BbrPhase.ProbeRTT;
                    _pacingGain = ProbeBwGainCruise;
                    _probeRttStartTicks = nowTicks;
                    break;
                }

                // Cycle pacing gain every round in ProbeBW
                _probeBwIndex = (_probeBwIndex + 1) % ProbeBwGainCycle.Length;
                _pacingGain = ProbeBwGainCycle[_probeBwIndex];
                break;

            case BbrPhase.ProbeRTT:
                if (measuredRtt > TimeSpan.Zero && measuredRtt < _minRtt)
                {
                    _minRtt = measuredRtt;
                }

                // Maintain ProbeRTT for 200ms to let bottleneck queue drain
                if (_timeProvider.GetElapsedTime(_probeRttStartTicks) >= _probeRttDuration)
                {
                    _minRttTimestampTicks = nowTicks;
                    _phase = BbrPhase.ProbeBW;
                    _probeBwIndex = 0;
                    _pacingGain = ProbeBwGainCycle[_probeBwIndex];
                }
                break;
        }
    }
}
