// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using NEXUS.Contracts.Security;

namespace ZORGATH.WebPortal.API.Constants;

public static class RateLimiterPolicies
{
    public const string Relaxed = "Relaxed Rate Limit";
    public const string Strict = "Strict Rate Limit";
    public const string ForwardedForHeader = "X-Forwarded-For";
    public const string CfConnectingIpHeader = "CF-Connecting-IP";

    public static bool IsValidInternalServiceKey(
        HttpContext context,
        IConfiguration? configuration = null,
        IHostEnvironment? hostEnvironment = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        configuration ??= context.RequestServices?.GetService<IConfiguration>();
        hostEnvironment ??= context.RequestServices?.GetService<IHostEnvironment>();
        logger ??= context.RequestServices?.GetService<ILoggerFactory>()?.CreateLogger(nameof(RateLimiterPolicies));

        bool isStandalone = configuration?.GetValue<bool>("KONGOR_STANDALONE") ?? false;
        bool isDevelopment = (hostEnvironment?.IsDevelopment() ?? false) && !isStandalone;

        string? expectedKey = configuration?["InternalServiceKey"];
        if (string.IsNullOrWhiteSpace(expectedKey) ||
            string.Equals(expectedKey, "...", StringComparison.Ordinal))
        {
            if (isDevelopment)
            {
                expectedKey = SecurityConstants.DefaultInternalServiceKey;
            }
            else
            {
                return false;
            }
        }
        else if (!isDevelopment &&
                 string.Equals(expectedKey, SecurityConstants.DefaultInternalServiceKey, StringComparison.Ordinal))
        {
            logger?.LogError(
                "[RateLimiterPolicies] Default development InternalServiceKey is forbidden in non-Development/standalone mode.");
            return false;
        }

        if (!context.Request.Headers.TryGetValue(SecurityConstants.InternalServiceKeyHeader,
                out StringValues extractedKeyValues) ||
            StringValues.IsNullOrEmpty(extractedKeyValues))
        {
            return false;
        }

        string extractedKey = extractedKeyValues.ToString();
        if (string.IsNullOrEmpty(extractedKey))
        {
            return false;
        }

        byte[] expectedBytes = Encoding.UTF8.GetBytes(expectedKey);
        byte[] extractedBytes = Encoding.UTF8.GetBytes(extractedKey);

        if (!CryptographicOperations.FixedTimeEquals(expectedBytes, extractedBytes))
        {
            logger?.LogWarning(
                "[RateLimiterPolicies] Rejected invalid '{Header}' rate-limit bypass attempt from IP '{RemoteIp}'.",
                SecurityConstants.InternalServiceKeyHeader,
                context.Connection.RemoteIpAddress?.ToString() ?? "anonymous");
            return false;
        }

        return true;
    }

    public const int MaxTrackedIpPartitions = 4096;
    public const int OverflowShardCount = 256;

    private static readonly Lock s_partitionWindowLock = new();
    private static HashSet<string> s_relaxedTrackedKeys = new(StringComparer.Ordinal);
    private static HashSet<string> s_strictTrackedKeys = new(StringComparer.Ordinal);
    private static long s_partitionWindowStartTicks = Environment.TickCount64;
    private static bool s_loggedOverflowAlertInWindow;

    public static bool TryExtractForwardedEndUserIp(HttpContext context, out string clientIp)
    {
        ArgumentNullException.ThrowIfNull(context);
        clientIp = string.Empty;

        if (!IsValidInternalServiceKey(context))
        {
            return false;
        }

        if (context.Request.Headers.TryGetValue(CfConnectingIpHeader, out StringValues cfValues) &&
            !StringValues.IsNullOrEmpty(cfValues))
        {
            string candidate = cfValues.ToString().Trim();
            if (IPAddress.TryParse(candidate, out IPAddress? parsedCf))
            {
                clientIp = parsedCf.IsIPv4MappedToIPv6 ? parsedCf.MapToIPv4().ToString() : parsedCf.ToString();
                return true;
            }
        }

        if (context.Request.Headers.TryGetValue(ForwardedForHeader, out StringValues xffValues) &&
            !StringValues.IsNullOrEmpty(xffValues))
        {
            string rawXff = xffValues.ToString();
            foreach (string segment in rawXff.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (IPAddress.TryParse(segment, out IPAddress? parsedXff))
                {
                    clientIp = parsedXff.IsIPv4MappedToIPv6 ? parsedXff.MapToIPv4().ToString() : parsedXff.ToString();
                    return true;
                }
            }
        }

        return false;
    }

    public static RateLimitPartition<string> CreateRelaxedPartition(HttpContext context)
    {
        bool isTrustedService = IsValidInternalServiceKey(context);
        if (isTrustedService)
        {
            // When DAWNBRINGER proxies an end-user browser request with X-Forwarded-For, partition per end-user IP
            // so a single abusive client cannot exhaust global resources or ride an unlimited service pipe.
            if (TryExtractForwardedEndUserIp(context, out string forwardedIp))
            {
                string boundedFwdKey = BoundPartitionKey($"fwd:{forwardedIp}", Relaxed, context);
                return CreateTokenBucket(boundedFwdKey);
            }

            // Pure internal service-to-service background/health request without an end-user IP
            return RateLimitPartition.GetNoLimiter("internal_service");
        }

        string partitionKey = BoundPartitionKey(NormalizeRemoteIp(context.Connection.RemoteIpAddress), Relaxed, context);
        return CreateTokenBucket(partitionKey);
    }

    public static RateLimitPartition<string> CreateStrictPartition(HttpContext context)
    {
        string rawPartitionKey = IsValidInternalServiceKey(context) &&
                                 TryExtractForwardedEndUserIp(context, out string forwardedIp)
            ? $"fwd:{forwardedIp}"
            : NormalizeRemoteIp(context.Connection.RemoteIpAddress);

        string partitionKey = BoundPartitionKey(rawPartitionKey, Strict, context);

        return RateLimitPartition.GetSlidingWindowLimiter(
            partitionKey: partitionKey,
            factory: _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 6, // 10 Seconds Per Sliding Window Segment
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst
            });
    }

    private static string BoundPartitionKey(string rawKey, string policyScope, HttpContext context)
    {
        long nowTicks = Environment.TickCount64;
        lock (s_partitionWindowLock)
        {
            if (nowTicks - s_partitionWindowStartTicks >= 60_000)
            {
                s_relaxedTrackedKeys.Clear();
                s_strictTrackedKeys.Clear();
                s_partitionWindowStartTicks = nowTicks;
                s_loggedOverflowAlertInWindow = false;
            }

            HashSet<string> trackedSet = string.Equals(policyScope, Strict, StringComparison.Ordinal)
                ? s_strictTrackedKeys
                : s_relaxedTrackedKeys;

            if (trackedSet.Contains(rawKey))
            {
                return rawKey;
            }

            if (trackedSet.Count < MaxTrackedIpPartitions)
            {
                trackedSet.Add(rawKey);
                return rawKey;
            }

            uint shard = (uint)StringComparer.Ordinal.GetHashCode(rawKey) % OverflowShardCount;
            if (!s_loggedOverflowAlertInWindow)
            {
                s_loggedOverflowAlertInWindow = true;
                ILoggerFactory? loggerFactory = context.RequestServices?.GetService<ILoggerFactory>();
                loggerFactory?.CreateLogger(nameof(RateLimiterPolicies)).LogWarning(
                    "[SECURITY-ALERT] [RateLimiterPolicies] Active IP partition cap ({MaxCap}) reached for policy '{Policy}'. Mapping excess IPs to {ShardCount} bounded overflow buckets.",
                    MaxTrackedIpPartitions,
                    policyScope,
                    OverflowShardCount);
            }

            return $"overflow:{policyScope}:{shard}";
        }
    }

    private static string NormalizeRemoteIp(IPAddress? address)
    {
        if (address is null) return "anonymous";
        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4().ToString() : address.ToString();
    }

    private static RateLimitPartition<string> CreateTokenBucket(string partitionKey)
    {
        return RateLimitPartition.GetTokenBucketLimiter(
            partitionKey: partitionKey,
            factory: _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = 100,
                ReplenishmentPeriod = TimeSpan.FromSeconds(10),
                TokensPerPeriod = 20,
                AutoReplenishment = true,
                QueueLimit = 10,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst
            });
    }
}
