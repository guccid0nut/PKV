// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: DatagramAnomalyShield.cs
// System: COMPEL - Monotonic Datagram Anomaly Shield & Micro-Burst Flood Guard
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using ASPIRE.Common.Netcode;
using COMPEL.Services.Proxy;

namespace COMPEL.Services.Security;

/// <summary>
///     Result of an anomaly shield evaluation.
/// </summary>
public readonly struct AnomalyEvaluationResult
{
    public bool IsAdmitted { get; }
    public int ViolationWeight { get; }
    public string? DropReason { get; }

    private AnomalyEvaluationResult(bool isAdmitted, int violationWeight, string? dropReason)
    {
        IsAdmitted = isAdmitted;
        ViolationWeight = violationWeight;
        DropReason = dropReason;
    }

    public static AnomalyEvaluationResult Admitted()
        => new(true, 0, null);

    public static AnomalyEvaluationResult Dropped(int violationWeight, string dropReason)
        => new(false, violationWeight, dropReason);
}

/// <summary>
///     Guards against packet replay attacks, sequence regression, and micro-burst floods.
///     Combines a 64-bit sliding bitmask filter with a high-resolution burst rate meter.
/// </summary>
public sealed class DatagramAnomalyShield
{
    public const int DefaultBurstLimit = 40;
    public static readonly TimeSpan DefaultBurstWindow = TimeSpan.FromMilliseconds(10);

    private readonly TimeProvider timeProvider;
    private readonly int burstLimit;
    private readonly long burstWindowTicks;

    private SlidingBitmaskAntiReplay antiReplay;
    private long windowStartTimestamp;
    private int burstPacketCount;
    private long lastArrivalTimestamp;
    private uint lastChallenge;

    /// <summary>
    ///     Highest admitted sequence number observed.
    /// </summary>
    public uint MaxSequence => antiReplay.MaxSequence;

    /// <summary>
    ///     Current 64-bit sliding window bitmask.
    /// </summary>
    public ulong WindowMask => antiReplay.WindowMask;

    public DatagramAnomalyShield(
        TimeProvider? timeProvider = null,
        int burstLimit = DefaultBurstLimit,
        TimeSpan? burstWindow = null)
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.burstLimit = burstLimit;
        TimeSpan window = burstWindow ?? DefaultBurstWindow;
        burstWindowTicks = (long)(window.TotalSeconds * this.timeProvider.TimestampFrequency);

        antiReplay = new SlidingBitmaskAntiReplay();
        windowStartTimestamp = this.timeProvider.GetTimestamp();
        lastArrivalTimestamp = windowStartTimestamp;
    }

    /// <summary>
    ///     Evaluates an incoming datagram's sequence under an active challenge.
    ///     If the challenge has rotated, the sequence filter is safely reset.
    /// </summary>
    public AnomalyEvaluationResult Evaluate(uint sequence, uint challenge = 0)
    {
        long now = timeProvider.GetTimestamp();

        // Challenge rotation detection: reset sequence window if challenge changed
        if (challenge != 0 && challenge != lastChallenge)
        {
            antiReplay.Reset();
            lastChallenge = challenge;
        }

        // 1. Check Burst Rate Flood
        long elapsed = now - windowStartTimestamp;
        if (elapsed > burstWindowTicks)
        {
            windowStartTimestamp = now;
            burstPacketCount = 1;
        }
        else
        {
            burstPacketCount++;
            if (burstPacketCount > burstLimit)
            {
                return AnomalyEvaluationResult.Dropped(
                    ViolationScoreContainer.RateLimitViolationWeight,
                    "Burst Flood");
            }
        }

        // 2. Check Sliding Bitmask Anti-Replay
        AntiReplayStatus status = antiReplay.CheckAndAdmit(sequence);
        switch (status)
        {
            case AntiReplayStatus.AcceptedNewest:
            case AntiReplayStatus.AcceptedReordered:
                lastArrivalTimestamp = now;

                return AnomalyEvaluationResult.Admitted();

            case AntiReplayStatus.Duplicate:
                return AnomalyEvaluationResult.Dropped(
                    ViolationScoreContainer.DuplicateViolationWeight,
                    "Duplicate / Replay Attack");

            case AntiReplayStatus.Stale:
                return AnomalyEvaluationResult.Dropped(
                    ViolationScoreContainer.TooShortViolationWeight,
                    "Stale Sequence");

            default:
                return AnomalyEvaluationResult.Admitted();
        }
    }

    /// <summary>
    ///     Resets the filter state and burst counter.
    /// </summary>
    public void Reset()
    {
        antiReplay.Reset();
        windowStartTimestamp = timeProvider.GetTimestamp();
        burstPacketCount = 0;
        lastArrivalTimestamp = windowStartTimestamp;
        lastChallenge = 0;
    }
}
