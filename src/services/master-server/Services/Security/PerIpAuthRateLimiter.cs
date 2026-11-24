// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;

namespace KONGOR.MasterServer.Services.Security;

/// <summary>
/// Decision returned by <see cref="IPerIpAuthRateLimiter"/> when evaluating an authentication or account-creation request.
/// </summary>
public readonly record struct AuthRateLimitDecision(
    bool IsAllowed,
    int RemainingAttempts,
    int RetryAfterSeconds,
    string NormalizedClientIp);

/// <summary>
/// Per-IP sliding-window rate limiter protecting high-value authentication functions dispatched through
/// <c>client_requester.php</c> (<c>auth</c>, <c>pre_auth</c>, <c>srpauth</c>, <c>switch_auth</c>, <c>auto_login</c>)
/// against credential-stuffing and brute-force attacks while leaving non-auth gameplay polling unaffected.
/// </summary>
public interface IPerIpAuthRateLimiter
{
    bool IsAuthFunction(string? functionName);

    AuthRateLimitDecision TryAcquire(string? remoteIp, string functionName);
}

public sealed class PerIpAuthRateLimiter : IPerIpAuthRateLimiter
{
    public const int DefaultMaxAttemptsPerWindow = 30;
    public const int DefaultWindowSeconds = 60;
    public const int MaxTrackedIpBuckets = 8192;

    private static readonly HashSet<string> AuthFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "auth",
        "pre_auth",
        "srpauth",
        "srp_auth",
        "switch_auth",
        "auto_login",
        "auth_token",
        "new_account"
    };

    private sealed class IpWindowState
    {
        public readonly Queue<long> AttemptTicks = new();
        public long LastSeenTicks;
    }

    private readonly ConcurrentDictionary<string, IpWindowState> _buckets = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PerIpAuthRateLimiter>? _logger;
    private readonly int _maxAttemptsPerWindow;
    private readonly TimeSpan _window;
    private int _pruneCounter;

    public PerIpAuthRateLimiter(
        IConfiguration? configuration = null,
        TimeProvider? timeProvider = null,
        ILogger<PerIpAuthRateLimiter>? logger = null,
        int? maxAttemptsOverride = null,
        TimeSpan? windowOverride = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;

        int configuredMax = maxAttemptsOverride
            ?? configuration?.GetValue<int?>("RateLimiting:AuthPerIpPerMinute")
            ?? DefaultMaxAttemptsPerWindow;
        _maxAttemptsPerWindow = Math.Clamp(configuredMax, 1, 1000);

        int configuredWindowSec = configuration?.GetValue<int?>("RateLimiting:AuthWindowSeconds")
            ?? DefaultWindowSeconds;
        _window = windowOverride ?? TimeSpan.FromSeconds(Math.Clamp(configuredWindowSec, 5, 3600));
    }

    public bool IsAuthFunction(string? functionName)
        => !string.IsNullOrWhiteSpace(functionName) && AuthFunctions.Contains(functionName.Trim());

    public AuthRateLimitDecision TryAcquire(string? remoteIp, string functionName)
    {
        string normalizedIp = NormalizeIp(remoteIp);
        if (!IsAuthFunction(functionName))
        {
            return new AuthRateLimitDecision(true, _maxAttemptsPerWindow, 0, normalizedIp);
        }

        long nowTicks = _timeProvider.GetUtcNow().UtcTicks;
        long cutoffTicks = nowTicks - _window.Ticks;

        // Periodically prune expired buckets and bound dictionary cardinality against IP-flood memory exhaustion.
        if (Interlocked.Increment(ref _pruneCounter) % 256 == 0 || _buckets.Count >= MaxTrackedIpBuckets)
        {
            PruneStaleBuckets(cutoffTicks);
        }

        string bucketKey = _buckets.Count >= MaxTrackedIpBuckets && !_buckets.ContainsKey(normalizedIp)
            ? $"overflow-{Math.Abs(StringComparer.Ordinal.GetHashCode(normalizedIp)) % 128}"
            : normalizedIp;

        IpWindowState state = _buckets.GetOrAdd(bucketKey, _ => new IpWindowState());
        lock (state)
        {
            while (state.AttemptTicks.Count > 0 && state.AttemptTicks.Peek() <= cutoffTicks)
            {
                state.AttemptTicks.Dequeue();
            }

            state.LastSeenTicks = nowTicks;

            if (state.AttemptTicks.Count >= _maxAttemptsPerWindow)
            {
                long oldestTicks = state.AttemptTicks.Peek();
                long retryTicks = Math.Max(TimeSpan.TicksPerSecond, (oldestTicks + _window.Ticks) - nowTicks);
                int retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(TimeSpan.FromTicks(retryTicks).TotalSeconds));

                _logger?.LogWarning(
                    "[SECURITY-ALERT] [PerIpAuthRateLimiter] Blocked auth brute-force attempt from IP '{ClientIp}' on f='{Function}' ({Attempts}/{Limit} in {WindowSec}s; Retry-After={RetryAfter}s).",
                    normalizedIp,
                    functionName,
                    state.AttemptTicks.Count,
                    _maxAttemptsPerWindow,
                    (int)_window.TotalSeconds,
                    retryAfterSeconds);

                return new AuthRateLimitDecision(false, 0, retryAfterSeconds, normalizedIp);
            }

            state.AttemptTicks.Enqueue(nowTicks);
            int remaining = Math.Max(0, _maxAttemptsPerWindow - state.AttemptTicks.Count);
            return new AuthRateLimitDecision(true, remaining, 0, normalizedIp);
        }
    }

    private void PruneStaleBuckets(long cutoffTicks)
    {
        foreach (KeyValuePair<string, IpWindowState> kvp in _buckets)
        {
            bool shouldRemove = false;
            lock (kvp.Value)
            {
                if (kvp.Value.LastSeenTicks <= cutoffTicks)
                {
                    shouldRemove = true;
                }
            }

            if (shouldRemove)
            {
                _buckets.TryRemove(kvp.Key, out _);
            }
        }
    }

    private static string NormalizeIp(string? rawIp)
    {
        if (string.IsNullOrWhiteSpace(rawIp))
        {
            return "unknown";
        }

        string trimmed = rawIp.Trim();
        if (System.Net.IPAddress.TryParse(trimmed, out System.Net.IPAddress? parsed))
        {
            return parsed.IsIPv4MappedToIPv6 ? parsed.MapToIPv4().ToString() : parsed.ToString();
        }

        return trimmed;
    }
}
