// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: UdpTokenBucketPacer.cs
// System: COMPEL Proxy - Microsecond High-Resolution UDP Token Bucket Pacer
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

namespace COMPEL.Services.Proxy.Congestion;

/// <summary>
///     Pacing decision returned when evaluating a datagram transmission.
/// </summary>
public readonly record struct PacingDecision(
    bool ShouldSendImmediately,
    TimeSpan RecommendedDelay,
    long CurrentTokensBytes);

/// <summary>
///     High-precision microsecond Token Bucket Pacer.
///     Instead of emitting 10+ snapshot datagrams simultaneously in under 0.05ms (which triggers
///     bufferbloat and packet drops on home Wi-Fi), the pacer spaces packets evenly across the tick.
///     Features an unconditional hard delay ceiling (MaxDelayClampMs) and queue ceiling to guarantee
///     that artificial latency is never introduced into competitive gameplay.
/// </summary>
public sealed class UdpTokenBucketPacer
{
    private const long Scale = 1_000L; // 1,000 fractional units per byte

    private readonly TimeProvider _timeProvider;
    private readonly BbrCongestionController? _bbr;
    private readonly long _fixedPacingRateBps;
    private readonly TimeSpan _maxDelayClamp;
    private readonly int _maxBurstCapacityBytes;

    private long _tokenUnits;
    private long _lastRefillTicks;
    private int _queuedPacketsCount;

    public TimeSpan MaxDelayClamp => _maxDelayClamp;

    public UdpTokenBucketPacer(
        BbrCongestionController? bbr = null,
        long fixedPacingRateBps = 2_000_000L, // 2 MB/s default
        int maxBurstCapacityBytes = 32_768, // 32 KB burst headroom
        double maxDelayClampMs = 2.5, // 2.5ms maximum allowable pacing delay
        TimeProvider? timeProvider = null)
    {
        _bbr = bbr;
        _fixedPacingRateBps = Math.Max(128_000L, fixedPacingRateBps);
        _maxBurstCapacityBytes = maxBurstCapacityBytes;
        _maxDelayClamp = TimeSpan.FromMilliseconds(maxDelayClampMs);
        _timeProvider = timeProvider ?? TimeProvider.System;

        _lastRefillTicks = _timeProvider.GetTimestamp();
        _tokenUnits = _maxBurstCapacityBytes * Scale;
    }

    /// <summary>
    ///     Evaluates whether the datagram should be transmitted immediately or paced with a sub-millisecond delay.
    ///     Guaranteed never to delay longer than MaxDelayClamp, and immediately flushes if queue backlog builds up.
    /// </summary>
    public PacingDecision Evaluate(int datagramSizeBytes)
    {
        if (datagramSizeBytes <= 0)
        {
            return new PacingDecision(true, TimeSpan.Zero, _tokenUnits / Scale);
        }

        long nowTicks = _timeProvider.GetTimestamp();
        RefillTokens(nowTicks);

        long requiredUnits = datagramSizeBytes * Scale;
        long currentUnits = Volatile.Read(ref _tokenUnits);

        // Safety Guard 1: If backlog is high, flush immediately to prevent latency build-up
        if (Volatile.Read(ref _queuedPacketsCount) >= 3)
        {
            Interlocked.Exchange(ref _tokenUnits, 0);
            return new PacingDecision(true, TimeSpan.Zero, 0);
        }

        // If sufficient tokens exist, send immediately
        if (currentUnits >= requiredUnits)
        {
            Interlocked.Add(ref _tokenUnits, -requiredUnits);
            return new PacingDecision(true, TimeSpan.Zero, (_tokenUnits / Scale));
        }

        // Calculate needed pacing delay to accumulate missing tokens
        long deficitUnits = requiredUnits - currentUnits;
        long currentPacingRateBps = _bbr?.GetPacingRateBytesPerSec() ?? _fixedPacingRateBps;
        double secondsNeeded = (deficitUnits / (double)Scale) / currentPacingRateBps;
        TimeSpan delay = TimeSpan.FromSeconds(secondsNeeded);

        // Safety Guard 2: Clamp to strict maximum delay ceiling
        if (delay > _maxDelayClamp)
        {
            delay = _maxDelayClamp;
        }

        Interlocked.Increment(ref _queuedPacketsCount);
        return new PacingDecision(false, delay, currentUnits / Scale);
    }

    /// <summary>
    ///     Notifies the pacer that a queued/delayed datagram has completed transmission.
    /// </summary>
    public void NotifyTransmissionComplete()
    {
        int current = Volatile.Read(ref _queuedPacketsCount);
        if (current > 0)
        {
            Interlocked.Decrement(ref _queuedPacketsCount);
        }
    }

    private void RefillTokens(long nowTicks)
    {
        long elapsedTicks = nowTicks - Volatile.Read(ref _lastRefillTicks);
        if (elapsedTicks <= 0)
        {
            return;
        }

        Volatile.Write(ref _lastRefillTicks, nowTicks);

        double elapsedSeconds = elapsedTicks / (double)_timeProvider.TimestampFrequency;
        long currentPacingRateBps = _bbr?.GetPacingRateBytesPerSec() ?? _fixedPacingRateBps;
        long tokensToAddUnits = (long)(elapsedSeconds * currentPacingRateBps * Scale);

        if (tokensToAddUnits > 0)
        {
            long maxCapacityUnits = _maxBurstCapacityBytes * Scale;
            long oldState = Volatile.Read(ref _tokenUnits);
            while (true)
            {
                long newState = Math.Min(maxCapacityUnits, oldState + tokensToAddUnits);
                long current = Interlocked.CompareExchange(ref _tokenUnits, newState, oldState);
                if (current == oldState)
                {
                    break;
                }
                oldState = current;
            }
        }
    }
}
