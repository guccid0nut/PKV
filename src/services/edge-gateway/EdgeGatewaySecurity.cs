// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Primitives;
using Yarp.ReverseProxy.Configuration;

namespace KONGOR.EdgeGateway;

/// <summary>
///     Validated rate-limiting options for <c>KONGOR.EdgeGateway</c> (<c>EdgeGateway:RateLimit</c>).
/// </summary>
internal sealed class EdgeGatewayRateLimitOptions
{
    public const string SectionName = "EdgeGateway:RateLimit";

    public bool Enabled { get; set; } = true;
    public int PermitLimit { get; set; } = 240;
    public int WindowSeconds { get; set; } = 60;
    public int SegmentsPerWindow { get; set; } = 6;
    public int QueueLimit { get; set; }
    public int MaxTrackedPartitions { get; set; } = 4096;
    public int OverflowShardCount { get; set; } = 256;
}

/// <summary>
///     Security helpers for IPv6 /64 normalization, internal topology authorization, and header sanitization.
/// </summary>
internal static class EdgeGatewaySecurity
{
    public const string InternalKeyHeaderName = "X-Kongor-Internal-Key";
    public const string LegacyInternalServiceKeyHeaderName = "X-Internal-Service-Key";
    public const string CfConnectingIpHeader = "CF-Connecting-IP";
    public const string CfVisitorHeader = "CF-Visitor";

    /// <summary>
    ///     Normalizes a client IP into a rate-limit partition key:
    ///     unmaps IPv4-mapped IPv6 addresses to IPv4, keeps loopback IPv6 distinct,
    ///     and collapses external IPv6 addresses to their <c>/64</c> subnet prefix to prevent per-host rotation bypass.
    /// </summary>
    public static string NormalizeClientIpPartitionKey(IPAddress? address)
    {
        if (address is null)
        {
            return "unknown";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4().ToString();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return address.ToString();
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (IPAddress.IsLoopback(address))
            {
                return "::1";
            }

            byte[] bytes = address.GetAddressBytes();
            if (bytes.Length == 16)
            {
                for (int i = 8; i < 16; i++)
                {
                    bytes[i] = 0;
                }

                IPAddress prefix = new(bytes);
                return $"{prefix}/64";
            }
        }

        return address.ToString();
    }

    /// <summary>
    ///     Authorizes access to <c>GET /gateway/topology</c>:
    ///     allowed in Development, or in non-Development strictly for loopback peers presenting a matching
    ///     <c>X-Kongor-Internal-Key</c> header when an internal key is configured.
    /// </summary>
    public static bool IsAuthorizedForTopology(
        HttpContext httpContext,
        IHostEnvironment environment,
        IConfiguration configuration,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);

        if (environment.IsDevelopment())
        {
            logger.LogDebug(
                "[EdgeGateway] Authorized /gateway/topology in Development environment for IP '{RemoteIp}'.",
                httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown");
            return true;
        }

        IPAddress? remoteIp = httpContext.Connection.RemoteIpAddress;
        IPAddress? normalizedIp = remoteIp is { IsIPv4MappedToIPv6: true } ? remoteIp.MapToIPv4() : remoteIp;
        if (normalizedIp is null || !IPAddress.IsLoopback(normalizedIp))
        {
            logger.LogWarning(
                "[SECURITY-ALERT] [EdgeGateway] Denied /gateway/topology from non-loopback peer '{RemoteIp}' in environment '{Environment}'.",
                normalizedIp?.ToString() ?? "unknown",
                environment.EnvironmentName);
            return false;
        }

        string? expectedKey = configuration["EdgeGateway:InternalKey"]
                              ?? configuration["KONGOR_INTERNAL_KEY"]
                              ?? configuration["InternalServiceKey"];

        if (!string.IsNullOrWhiteSpace(expectedKey))
        {
            string? providedKey = ExtractInternalKeyHeader(httpContext.Request.Headers);
            if (string.IsNullOrEmpty(providedKey))
            {
                logger.LogWarning(
                    "[SECURITY-ALERT] [EdgeGateway] Denied /gateway/topology from loopback peer '{RemoteIp}': missing '{Header}' header.",
                    normalizedIp.ToString(),
                    InternalKeyHeaderName);
                return false;
            }

            byte[] expectedBytes = Encoding.UTF8.GetBytes(expectedKey);
            byte[] providedBytes = Encoding.UTF8.GetBytes(providedKey);
            if (!CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes))
            {
                logger.LogWarning(
                    "[SECURITY-ALERT] [EdgeGateway] Denied /gateway/topology from loopback peer '{RemoteIp}': invalid '{Header}' value.",
                    normalizedIp.ToString(),
                    InternalKeyHeaderName);
                return false;
            }
        }

        logger.LogInformation(
            "[EdgeGateway] Authorized /gateway/topology for loopback peer '{RemoteIp}' in environment '{Environment}' (InternalKeyConfigured={HasKey}).",
            normalizedIp.ToString(),
            environment.EnvironmentName,
            !string.IsNullOrWhiteSpace(expectedKey));
        return true;
    }

    private static string? ExtractInternalKeyHeader(IHeaderDictionary headers)
    {
        if (headers.TryGetValue(InternalKeyHeaderName, out StringValues primaryValues) &&
            !StringValues.IsNullOrEmpty(primaryValues))
        {
            return primaryValues.ToString().Trim();
        }

        if (headers.TryGetValue(LegacyInternalServiceKeyHeaderName, out StringValues legacyValues) &&
            !StringValues.IsNullOrEmpty(legacyValues))
        {
            return legacyValues.ToString().Trim();
        }

        return null;
    }
}

/// <summary>
///     Bounds active rate-limit partition keys to prevent unbounded memory growth under distributed IP floods.
///     Mirrors <c>ZORGATH.WebPortal.API.Constants.RateLimiterPolicies</c> hash-bucket overflow bounding.
/// </summary>
internal sealed class EdgePartitionKeyTracker
{
    private readonly Lock _lock = new();
    private readonly HashSet<string> _trackedKeys = new(StringComparer.Ordinal);
    private long _windowStartTicks = Environment.TickCount64;
    private bool _loggedOverflowInWindow;

    public string BoundPartitionKey(string normalizedKey, EdgeGatewayRateLimitOptions options, ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        long nowTicks = Environment.TickCount64;
        long windowMs = Math.Max(60_000L, options.WindowSeconds * 1000L);
        int maxCap = Math.Clamp(options.MaxTrackedPartitions, 16, 65_536);
        int shardCount = Math.Clamp(options.OverflowShardCount, 1, 4096);

        lock (_lock)
        {
            if (nowTicks - _windowStartTicks >= windowMs)
            {
                _trackedKeys.Clear();
                _windowStartTicks = nowTicks;
                _loggedOverflowInWindow = false;
            }

            if (_trackedKeys.Contains(normalizedKey))
            {
                return normalizedKey;
            }

            if (_trackedKeys.Count < maxCap)
            {
                _trackedKeys.Add(normalizedKey);
                return normalizedKey;
            }

            uint shard = (uint)StringComparer.Ordinal.GetHashCode(normalizedKey) % (uint)shardCount;
            if (!_loggedOverflowInWindow)
            {
                _loggedOverflowInWindow = true;
                logger?.LogWarning(
                    "[SECURITY-ALERT] [EdgeGateway] Active IP partition cap ({MaxCap}) reached. Mapping excess client keys into {ShardCount} bounded overflow buckets.",
                    maxCap,
                    shardCount);
            }

            return $"overflow:edge-ip:{shard}";
        }
    }
}

/// <summary>
///     Resolves Aspire <c>.WithReference(...)</c> service discovery endpoints (<c>services:&lt;name&gt;:http:0</c>)
///     and environment overrides into YARP cluster destinations while preserving explicit <c>ReverseProxy</c> overrides.
/// </summary>
internal sealed class AspireClusterDestinationConfigFilter(
    IConfiguration configuration,
    ILogger<AspireClusterDestinationConfigFilter> logger)
    : IProxyConfigFilter
{
    private static readonly Dictionary<string, (string AspireServiceName, string EnvOverrideKey, string DefaultLoopback)> ClusterMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["master-server"] = ("master-server", "EDGE_MASTER_SERVER_URL", "http://127.0.0.1:55555/"),
            ["web-api"] = ("web-portal-api", "EDGE_WEB_API_URL", "http://127.0.0.1:5200/"),
            ["web-ui"] = ("web-portal-ui", "EDGE_WEB_UI_URL", "http://127.0.0.1:5266/")
        };

    public ValueTask<RouteConfig> ConfigureRouteAsync(RouteConfig route, ClusterConfig? cluster, CancellationToken cancel)
    {
        return ValueTask.FromResult(route);
    }

    public ValueTask<ClusterConfig> ConfigureClusterAsync(ClusterConfig cluster, CancellationToken cancel)
    {
        if (!ClusterMap.TryGetValue(cluster.ClusterId, out (string AspireServiceName, string EnvOverrideKey, string DefaultLoopback) mapping))
        {
            return ValueTask.FromResult(cluster);
        }

        string? resolvedAddress = ResolvePreferredAddress(mapping.AspireServiceName, mapping.EnvOverrideKey);
        if (string.IsNullOrWhiteSpace(resolvedAddress))
        {
            return ValueTask.FromResult(cluster);
        }

        string normalizedResolved = resolvedAddress.EndsWith('/') ? resolvedAddress : resolvedAddress + "/";

        Dictionary<string, DestinationConfig> updatedDestinations = cluster.Destinations is not null
            ? new Dictionary<string, DestinationConfig>(cluster.Destinations, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, DestinationConfig>(StringComparer.OrdinalIgnoreCase);

        if (updatedDestinations.TryGetValue("primary", out DestinationConfig? currentPrimary))
        {
            string currentNormalized = (currentPrimary.Address ?? string.Empty).TrimEnd('/') + "/";
            bool isDefaultFallback = string.IsNullOrWhiteSpace(currentPrimary.Address) ||
                                     string.Equals(currentNormalized, mapping.DefaultLoopback, StringComparison.OrdinalIgnoreCase);

            if (!isDefaultFallback)
            {
                logger.LogDebug(
                    "[EdgeGateway] Cluster '{ClusterId}' retains explicit destination override '{ExplicitAddress}' (ignoring Aspire discovery '{DiscoveredAddress}').",
                    cluster.ClusterId,
                    currentPrimary.Address,
                    normalizedResolved);
                return ValueTask.FromResult(cluster);
            }

            updatedDestinations["primary"] = currentPrimary with { Address = normalizedResolved };
            logger.LogInformation(
                "[EdgeGateway] Cluster '{ClusterId}' resolved destination 'primary' via Aspire/env service discovery: '{PreviousAddress}' -> '{ResolvedAddress}'.",
                cluster.ClusterId,
                currentPrimary.Address,
                normalizedResolved);
        }
        else
        {
            updatedDestinations["primary"] = new DestinationConfig { Address = normalizedResolved };
            logger.LogInformation(
                "[EdgeGateway] Cluster '{ClusterId}' populated destination 'primary' via Aspire/env service discovery: '{ResolvedAddress}'.",
                cluster.ClusterId,
                normalizedResolved);
        }

        return ValueTask.FromResult(cluster with { Destinations = updatedDestinations });
    }

    private string? ResolvePreferredAddress(string aspireServiceName, string envOverrideKey)
    {
        string? explicitEnv = configuration[envOverrideKey];
        if (!string.IsNullOrWhiteSpace(explicitEnv))
        {
            return explicitEnv.Trim();
        }

        string? aspireHttp = configuration[$"services:{aspireServiceName}:http:0"];
        if (!string.IsNullOrWhiteSpace(aspireHttp))
        {
            return aspireHttp.Trim();
        }

        string? aspireHttps = configuration[$"services:{aspireServiceName}:https:0"];
        if (!string.IsNullOrWhiteSpace(aspireHttps))
        {
            return aspireHttps.Trim();
        }

        return null;
    }
}
