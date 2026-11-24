// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;

namespace TRANSMUTANSTEIN.ChatServer.Domain.Core;

/// <summary>
///     High-performance, non-allocating token-bucket rate limiter for individual TCP chat sessions.
///     Enforces maximum packet throughput rates while absorbing transient bursts, preventing
///     packet flooding attacks without inducing TCP zero-window backpressure that would trip
///     legacy K2 client (<c>cgame_x64.dll</c>) socket send timeouts.
/// </summary>
public sealed class SessionTokenBucketRateLimiter
{
    private readonly double _refillRatePerSecond;
    private readonly double _maxBurstCapacity;
    private readonly Lock _lock = new();

    private double _availableTokens;
    private long _lastRefillTimestamp;
    private int _consecutiveViolations;

    /// <summary>
    ///     Initializes a new instance of the <see cref="SessionTokenBucketRateLimiter" />.
    /// </summary>
    /// <param name="refillRatePerSecond">Sustainable allowed packet rate per second (default: 50).</param>
    /// <param name="maxBurstCapacity">Maximum burst packet capacity permitted (default: 100).</param>
    public SessionTokenBucketRateLimiter(double refillRatePerSecond = 50.0, double maxBurstCapacity = 100.0)
    {
        _refillRatePerSecond = Math.Max(1.0, refillRatePerSecond);
        _maxBurstCapacity = Math.Max(1.0, maxBurstCapacity);
        _availableTokens = _maxBurstCapacity;
        _lastRefillTimestamp = Stopwatch.GetTimestamp();
    }

    /// <summary>
    ///     Gets the total consecutive violation count accumulated by this session without a successful drain.
    /// </summary>
    public int ConsecutiveViolations
    {
        get
        {
            lock (_lock)
            {
                return _consecutiveViolations;
            }
        }
    }

    /// <summary>
    ///     Attempts to consume the specified number of tokens from the bucket.
    /// </summary>
    /// <param name="tokens">Number of tokens to consume (default: 1).</param>
    /// <returns><c>true</c> if sufficient tokens were available and consumed; otherwise, <c>false</c>.</returns>
    public bool TryConsume(int tokens = 1)
    {
        lock (_lock)
        {
            RefillTokens();

            if (_availableTokens >= tokens)
            {
                _availableTokens -= tokens;
                if (_consecutiveViolations > 0)
                {
                    _consecutiveViolations = Math.Max(0, _consecutiveViolations - 1);
                }
                return true;
            }

            _consecutiveViolations++;
            return false;
        }
    }

    /// <summary>
    ///     Refills tokens based on elapsed high-resolution timer ticks. Must be called under <see cref="_lock" />.
    /// </summary>
    private void RefillTokens()
    {
        long currentTimestamp = Stopwatch.GetTimestamp();
        long elapsedTicks = currentTimestamp - _lastRefillTimestamp;

        if (elapsedTicks <= 0) return;

        double elapsedSeconds = (double)elapsedTicks / Stopwatch.Frequency;
        double tokensToAdd = elapsedSeconds * _refillRatePerSecond;

        if (tokensToAdd > 0.0)
        {
            _availableTokens = Math.Min(_maxBurstCapacity, _availableTokens + tokensToAdd);
            _lastRefillTimestamp = currentTimestamp;
        }
    }

    /// <summary>
    ///     Resets the token bucket to full capacity and clears violation counters.
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _availableTokens = _maxBurstCapacity;
            _lastRefillTimestamp = Stopwatch.GetTimestamp();
            _consecutiveViolations = 0;
        }
    }
}
