// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// Project Kongor Wave 3B: Opt-In YARP HTTP Edge Gateway (KONGOR.EdgeGateway)
//
// Architectural Scope Boundary:
//   - Proxies ONLY stateless HTTP/HTTPS & SignalR/WebSocket surfaces:
//       * /api/master/*     -> KONGOR.MasterServer     (:55555)
//       * /hubs/presence/*  -> KONGOR.MasterServer     (:55555, SignalR)
//       * /api/*            -> ZORGATH.WebPortal.API   (:5200)
//       * /*                -> DAWNBRINGER.WebPortal.UI (:5266, Blazor/SignalR)
//   - Binary TCP ChatServer (:11031-11033) and UDP Compel/GameServer (:11235+)
//     MUST remain direct non-HTTP bindings (never routed through YARP).
// ============================================================================

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;
using ASPIRE.Common.ServiceDefaults;
using KONGOR.EdgeGateway;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Model;

const string edgeRateLimitPolicyName = "edge-ip";

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddServiceDiscovery();
builder.Services.ConfigureInfrastructureForwardedHeaders(builder.Configuration);

builder.Services.AddOptions<EdgeGatewayRateLimitOptions>()
    .Bind(builder.Configuration.GetSection(EdgeGatewayRateLimitOptions.SectionName))
    .PostConfigure(opts =>
    {
        opts.PermitLimit = Math.Clamp(opts.PermitLimit, 1, 100_000);
        opts.WindowSeconds = Math.Clamp(opts.WindowSeconds, 1, 3600);
        opts.SegmentsPerWindow = Math.Clamp(opts.SegmentsPerWindow, 1, 60);
        opts.QueueLimit = Math.Clamp(opts.QueueLimit, 0, 1000);
        opts.MaxTrackedPartitions = Math.Clamp(opts.MaxTrackedPartitions, 16, 65_536);
        opts.OverflowShardCount = Math.Clamp(opts.OverflowShardCount, 1, 4096);
    })
    .ValidateOnStart();

builder.Services.AddSingleton<EdgePartitionKeyTracker>();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = (context, _) =>
    {
        EdgeGatewayRateLimitOptions rlOpts = context.HttpContext.RequestServices
            .GetRequiredService<IOptionsMonitor<EdgeGatewayRateLimitOptions>>()
            .CurrentValue;

        int retryAfterSeconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter)
            ? Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
            : rlOpts.WindowSeconds;

        context.HttpContext.Response.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);

        ILogger? rlLogger = context.HttpContext.RequestServices
            .GetService<ILoggerFactory>()?
            .CreateLogger("Security.EdgeGatewayRateLimiter");
        rlLogger?.LogWarning(
            "[SECURITY-ALERT] [EdgeGateway] Rate limit rejected HTTP {Method} {Path} from IP '{RemoteIp}' (Partition='{Partition}', 429 Too Many Requests, Retry-After={RetryAfter}s).",
            context.HttpContext.Request.Method,
            context.HttpContext.Request.Path,
            context.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            EdgeGatewaySecurity.NormalizeClientIpPartitionKey(context.HttpContext.Connection.RemoteIpAddress),
            retryAfterSeconds);

        return ValueTask.CompletedTask;
    };

    options.AddPolicy(edgeRateLimitPolicyName, httpContext =>
    {
        EdgeGatewayRateLimitOptions rlOpts = httpContext.RequestServices
            .GetRequiredService<IOptionsMonitor<EdgeGatewayRateLimitOptions>>()
            .CurrentValue;

        if (!rlOpts.Enabled)
        {
            return RateLimitPartition.GetNoLimiter("disabled");
        }

        EdgePartitionKeyTracker tracker = httpContext.RequestServices.GetRequiredService<EdgePartitionKeyTracker>();
        ILogger? rlLogger = httpContext.RequestServices
            .GetService<ILoggerFactory>()?
            .CreateLogger("Security.EdgeGatewayRateLimiter");

        string normalizedIpKey = EdgeGatewaySecurity.NormalizeClientIpPartitionKey(httpContext.Connection.RemoteIpAddress);
        string boundedKey = tracker.BoundPartitionKey(normalizedIpKey, rlOpts, rlLogger);

        return RateLimitPartition.GetSlidingWindowLimiter(
            partitionKey: boundedKey,
            factory: _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = rlOpts.PermitLimit,
                Window = TimeSpan.FromSeconds(rlOpts.WindowSeconds),
                SegmentsPerWindow = rlOpts.SegmentsPerWindow,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = rlOpts.QueueLimit,
                AutoReplenishment = true
            });
    });
});

builder.Services
    .AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
    .AddConfigFilter<AspireClusterDestinationConfigFilter>();

WebApplication app = builder.Build();

ILogger<Program> startupLogger = app.Services.GetRequiredService<ILogger<Program>>();
ILogger securityLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Security.EdgeGateway");
ILogger proxyTelemetryLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Telemetry.EdgeGatewayProxy");

// 1. Always emit defensive response security headers (covers proxied, error, 403, 429, and health responses).
app.Use(async (httpContext, next) =>
{
    httpContext.Response.OnStarting(() =>
    {
        httpContext.Response.Headers.TryAdd("X-Content-Type-Options", "nosniff");
        return Task.CompletedTask;
    });
    await next();
});

// 2. Anti-spoofing guard BEFORE forwarded headers processing:
//    Strip forged CF-Connecting-IP and CF-Visitor headers whenever the raw TCP socket peer
//    is NOT a verified trusted infrastructure proxy (exact ServiceExtensions.IsTrustedInfrastructureProxyPeer semantics).
app.Use(async (httpContext, next) =>
{
    IPAddress? rawPeer = httpContext.Connection.RemoteIpAddress;
    bool isTrustedProxyPeer = ServiceExtensions.IsTrustedInfrastructureProxyPeer(rawPeer, app.Configuration);
    if (!isTrustedProxyPeer)
    {
        bool hadCfIp = httpContext.Request.Headers.Remove(EdgeGatewaySecurity.CfConnectingIpHeader);
        bool hadCfVisitor = httpContext.Request.Headers.Remove(EdgeGatewaySecurity.CfVisitorHeader);
        if (hadCfIp || hadCfVisitor)
        {
            securityLogger.LogWarning(
                "[SECURITY-ALERT] [EdgeGateway] Stripped spoofed Cloudflare header(s) (CF-Connecting-IP={StrippedIp}, CF-Visitor={StrippedVisitor}) from untrusted peer '{RawPeer}' on {Method} {Path}.",
                hadCfIp,
                hadCfVisitor,
                rawPeer?.ToString() ?? "unknown",
                httpContext.Request.Method,
                httpContext.Request.Path.Value);
        }
    }

    await next();
});

// 3. Process verified X-Forwarded-* and trusted Cloudflare headers into RemoteIpAddress/Scheme.
app.UseInfrastructureForwardedHeaders(app.Configuration);
app.UseRouting();
app.UseRateLimiter();

app.MapDefaultEndpoints();

app.MapGet("/healthz", () => Results.Ok(new
{
    status = "healthy",
    service = "KONGOR.EdgeGateway",
    transportBoundary = "HTTP-only (binary TCP :11031-11033 and UDP :11235+ remain direct)"
}));

app.MapGet("/readyz", (IProxyConfigProvider proxyConfigProvider, IProxyStateLookup proxyStateLookup) =>
{
    IProxyConfig cfg = proxyConfigProvider.GetConfig();
    if (cfg.Routes.Count == 0 || cfg.Clusters.Count == 0)
    {
        proxyTelemetryLogger.LogWarning(
            "[EdgeGateway] Readiness probe UNREADY: empty proxy configuration (Routes={Routes}, Clusters={Clusters}).",
            cfg.Routes.Count,
            cfg.Clusters.Count);
        return Results.Json(
            new { status = "unready", reason = "empty_proxy_config", routes = cfg.Routes.Count, clusters = cfg.Clusters.Count },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    List<object> clusterHealth = new(cfg.Clusters.Count);
    bool allClustersHaveAvailableDestination = true;
    List<string> unhealthyClusters = [];

    foreach (ClusterConfig clusterCfg in cfg.Clusters)
    {
        bool found = proxyStateLookup.TryGetCluster(clusterCfg.ClusterId, out ClusterState? clusterState);
        int totalDestinations = found && clusterState is not null
            ? clusterState.DestinationsState.AllDestinations.Count
            : 0;
        int availableDestinations = found && clusterState is not null
            ? clusterState.DestinationsState.AvailableDestinations.Count
            : 0;
        int unhealthyDestinations = found && clusterState is not null
            ? clusterState.DestinationsState.AllDestinations.Count(d => d.Health.Passive == DestinationHealth.Unhealthy)
            : 0;

        bool clusterReady = availableDestinations > 0;
        if (!clusterReady)
        {
            allClustersHaveAvailableDestination = false;
            unhealthyClusters.Add(clusterCfg.ClusterId);
        }

        clusterHealth.Add(new
        {
            clusterId = clusterCfg.ClusterId,
            totalDestinations,
            availableDestinations,
            unhealthyDestinations,
            ready = clusterReady
        });
    }

    if (!allClustersHaveAvailableDestination)
    {
        proxyTelemetryLogger.LogWarning(
            "[EdgeGateway] Readiness probe UNREADY: cluster(s) without available destinations: [{UnhealthyClusters}].",
            string.Join(", ", unhealthyClusters));
        return Results.Json(
            new
            {
                status = "unready",
                reason = "no_available_cluster_destinations",
                unhealthyClusters,
                routes = cfg.Routes.Count,
                clusters = cfg.Clusters.Count,
                clusterHealth
            },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    return Results.Ok(new
    {
        status = "ready",
        routes = cfg.Routes.Count,
        clusters = cfg.Clusters.Count,
        clusterHealth
    });
});

app.MapGet("/gateway/topology", (
    HttpContext httpContext,
    IProxyConfigProvider proxyConfigProvider,
    IOptionsMonitor<EdgeGatewayRateLimitOptions> rateLimitOptionsMonitor) =>
{
    if (!EdgeGatewaySecurity.IsAuthorizedForTopology(httpContext, app.Environment, app.Configuration, securityLogger))
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    IProxyConfig cfg = proxyConfigProvider.GetConfig();
    EdgeGatewayRateLimitOptions rlOpts = rateLimitOptionsMonitor.CurrentValue;

    return Results.Ok(new
    {
        rateLimitEnabled = rlOpts.Enabled,
        permitLimit = rlOpts.PermitLimit,
        windowSeconds = rlOpts.WindowSeconds,
        maxTrackedPartitions = rlOpts.MaxTrackedPartitions,
        routes = cfg.Routes.Select(r => new { r.RouteId, r.ClusterId, path = r.Match.Path }),
        clusters = cfg.Clusters.Select(c => new
        {
            c.ClusterId,
            destinations = c.Destinations?.ToDictionary(d => d.Key, d => d.Value.Address)
        }),
        directBinaryPortsNotProxied = Program.DirectBinaryPortsNotProxied
    });
}).RequireRateLimiting(edgeRateLimitPolicyName);

app.MapReverseProxy(proxyPipeline =>
{
    // Belt-and-suspenders: strip Cloudflare raw headers before forwarding so loopback upstreams
    // only receive EdgeGateway's canonical X-Forwarded-* headers, and log full forwarder telemetry.
    proxyPipeline.Use(async (httpContext, next) =>
    {
        httpContext.Request.Headers.Remove(EdgeGatewaySecurity.CfConnectingIpHeader);
        httpContext.Request.Headers.Remove(EdgeGatewaySecurity.CfVisitorHeader);

        Stopwatch sw = Stopwatch.StartNew();
        await next();
        sw.Stop();

        IReverseProxyFeature? proxyFeature = httpContext.Features.Get<IReverseProxyFeature>();
        IForwarderErrorFeature? errorFeature = httpContext.Features.Get<IForwarderErrorFeature>();

        string routeId = proxyFeature?.Route?.Config?.RouteId ?? "unknown";
        string clusterId = proxyFeature?.Cluster?.Config?.ClusterId ?? "unknown";
        string destinationAddress = proxyFeature?.ProxiedDestination?.Model?.Config?.Address ?? "unknown";

        if (errorFeature is not null)
        {
            proxyTelemetryLogger.LogError(
                errorFeature.Exception,
                "[EdgeGateway] Reverse proxy forwarder error: ForwarderError={ForwarderError} StatusCode={StatusCode} Method={Method} Path={Path} Route={RouteId} Cluster={ClusterId} Destination={Destination} ClientIp={ClientIp} ElapsedMs={ElapsedMs:F2}",
                errorFeature.Error,
                httpContext.Response.StatusCode,
                httpContext.Request.Method,
                httpContext.Request.Path.Value,
                routeId,
                clusterId,
                destinationAddress,
                httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                sw.Elapsed.TotalMilliseconds);
        }
        else if (httpContext.Response.StatusCode >= StatusCodes.Status500InternalServerError)
        {
            proxyTelemetryLogger.LogWarning(
                "[EdgeGateway] Upstream returned 5xx: StatusCode={StatusCode} Method={Method} Path={Path} Route={RouteId} Cluster={ClusterId} Destination={Destination} ElapsedMs={ElapsedMs:F2}",
                httpContext.Response.StatusCode,
                httpContext.Request.Method,
                httpContext.Request.Path.Value,
                routeId,
                clusterId,
                destinationAddress,
                sw.Elapsed.TotalMilliseconds);
        }
        else if (proxyTelemetryLogger.IsEnabled(LogLevel.Debug))
        {
            proxyTelemetryLogger.LogDebug(
                "[EdgeGateway] Proxied {Method} {Path} -> Route={RouteId} Cluster={ClusterId} Destination={Destination} StatusCode={StatusCode} ElapsedMs={ElapsedMs:F2}",
                httpContext.Request.Method,
                httpContext.Request.Path.Value,
                routeId,
                clusterId,
                destinationAddress,
                httpContext.Response.StatusCode,
                sw.Elapsed.TotalMilliseconds);
        }
    });

    proxyPipeline.UseSessionAffinity();
    proxyPipeline.UseLoadBalancing();
    proxyPipeline.UsePassiveHealthChecks();
});

EdgeGatewayRateLimitOptions startupRlOpts = app.Services
    .GetRequiredService<IOptions<EdgeGatewayRateLimitOptions>>()
    .Value;

startupLogger.LogInformation(
    "[EdgeGateway] Initialized opt-in YARP HTTP gateway (Environment={Env}, RateLimitEnabled={Enabled}, PermitLimit={Permit}/{Window}s, MaxTrackedPartitions={MaxPartitions}; binary TCP/UDP remain direct).",
    app.Environment.EnvironmentName,
    startupRlOpts.Enabled,
    startupRlOpts.PermitLimit,
    startupRlOpts.WindowSeconds,
    startupRlOpts.MaxTrackedPartitions);

app.Run();

#pragma warning disable CA1050 // Top-level entry point Program marker required by WebApplicationFactory<Program>
public partial class Program
{
    internal static readonly int[] DirectBinaryPortsNotProxied = [11031, 11032, 11033, 11235];
}
#pragma warning restore CA1050
