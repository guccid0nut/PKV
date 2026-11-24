// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Net;
using IPNetwork = System.Net.IPNetwork;
using ASPIRE.Common.Resilience;
using ASPIRE.Common.Telemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using Pyroscope;
using Pyroscope.OpenTelemetry;
using System.Reflection;
using ASPIRE.Common.Behaviors;
using ASPIRE.Common.Extensions;
using MediatR;

namespace ASPIRE.Common.ServiceDefaults;

// ReSharper disable UnusedMethodReturnValue.Global
// ReSharper disable UnusedMethodReturnValue.Local
// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable ConvertToExtensionBlock

// Adds common Aspire services: service discovery, resilience, health checks, and OpenTelemetry.
// This project should be referenced by each service project in your solution.
// To learn more about using this project, see https://aka.ms/dotnet/aspire/service-defaults
public static class ServiceExtensions
{
    private const string HealthEndpointPath = "/health";
    private const string AlivenessEndpointPath = "/alive";
    private static readonly string[] TrustedProxyConfigKeys = ["ReverseProxy:TrustedProxies", "ForwardedHeaders:KnownProxies"];
    private static readonly string[] PeerProxyConfigKeys = ["INFRASTRUCTURE_GATEWAY", "ReverseProxy:TrustedProxies", "ForwardedHeaders:KnownProxies"];
    private static readonly char[] ProxyListSeparators = [',', ';'];

    static ServiceExtensions()
    {
        ActivitySource.AddActivityListener(new ActivityListener
        {
            ShouldListenTo = source =>
                source.Name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
                source.Name.StartsWith("Microsoft.Data.SqlClient", StringComparison.Ordinal) ||
                source.Name.StartsWith("SqlClientDiagnosticListener", StringComparison.Ordinal) ||
                source.Name.Contains("SqlClient", StringComparison.Ordinal) ||
                source.Name.StartsWith("Npgsql", StringComparison.Ordinal) ||
                source.Name.Contains("Npgsql", StringComparison.Ordinal) ||
                source.Name.Contains("PostgreSQL", StringComparison.Ordinal),
            Sample = (ref _) => ActivitySamplingResult.AllData,
            ActivityStarted = activity =>
            {
                string dbSystem = "mssql";
                string sourceName = activity.Source.Name;
                if (sourceName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) ||
                    sourceName.Contains("PostgreSQL", StringComparison.OrdinalIgnoreCase))
                {
                    dbSystem = "postgresql";
                }
                else
                {
                    string? provider = Environment.GetEnvironmentVariable("MERRICK_DB_PROVIDER") ??
                                       Environment.GetEnvironmentVariable("DB_PROVIDER");
                    if (string.Equals(provider, "Postgres", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(provider, "PostgreSQL", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(provider, "Supabase", StringComparison.OrdinalIgnoreCase))
                    {
                        dbSystem = "postgresql";
                    }
                }

                activity.SetTag("db.system", dbSystem);
                activity.SetTag("db.name", "MERRICK");
                activity.SetTag("code.namespace", "MERRICK.DatabaseContext");
                activity.SetTag("code.function", "EFCoreCommand");
            }
        });
    }

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.Configure<HostOptions>(options =>
        {
            options.ShutdownTimeout = TimeSpan.FromSeconds(15);
            // BackgroundServiceExceptionBehavior uses default StopHost; resilience guaranteed via ResilientBackgroundService
        });

        builder.ConfigureOpenTelemetry();

        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();

        // Configure Circuit Breaker Discord Alerting Webhook if configured
        string? alertWebhookUrl = builder.Configuration["Alerts:DiscordWebhookUrl"]
                                  ?? builder.Configuration["DISCORD_ALERT_WEBHOOK_URL"]
                                  ?? builder.Configuration["ALERT_WEBHOOK_URL"];
        if (!string.IsNullOrWhiteSpace(alertWebhookUrl))
        {
            NexusCircuitBreakerAlertDispatcher.ConfigureWebhookUrl(alertWebhookUrl);
        }

        // Register Polly v8 zero-allocation resilience pipelines
        builder.Services.AddNexusResiliencePipelines();

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            // Turn on resilience by default
            http.AddStandardResilienceHandler();

            // Turn on service discovery by default
            http.AddServiceDiscovery();
        });

        // Uncomment the following to restrict the allowed schemes for service discovery.
        // builder.Services.Configure<ServiceDiscoveryOptions>(options =>
        // {
        //     options.AllowedSchemes = ["https"];
        // });

        return builder;
    }

    /// <summary>
    ///     Registers MediatR and the cross-cutting pipeline behaviors (Logging, Validation,
    ///     DistributedLock, Caching, and Performance).
    /// </summary>
    public static TBuilder AddMediatRPipeline<TBuilder>(
        this TBuilder builder,
        params Assembly[] additionalAssemblies)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddMediatR(cfg =>
        {
            List<Assembly> assemblies = [typeof(LoggingBehavior<,>).Assembly];
            if (additionalAssemblies is { Length: > 0 })
            {
                assemblies.AddRange(additionalAssemblies);
            }
            cfg.RegisterServicesFromAssemblies(assemblies.Distinct().ToArray());

            cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
            cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
            cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(DistributedLockBehavior<,>));
            cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(CachingBehavior<,>));
            cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(PerformanceBehavior<,>));
        });

        return builder;
    }

    /// <summary>
    ///     Scans the specified assembly for FluentValidation validators and registers them in the DI container.
    /// </summary>
    public static IServiceCollection AddValidatorsFromAssembly(
        this IServiceCollection services,
        Assembly assembly,
        ServiceLifetime lifetime = ServiceLifetime.Transient)
    {
        var scanner = FluentValidation.AssemblyScanner.FindValidatorsInAssembly(assembly);
        foreach (var result in scanner)
        {
            services.Add(new ServiceDescriptor(result.InterfaceType, result.ValidatorType, lifetime));
        }
        return services;
    }

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        string serviceName = builder.Environment.ApplicationName;

        string? profilingEnabledSetting = builder.Configuration["PYROSCOPE_PROFILING_ENABLED"] ??
                                          Environment.GetEnvironmentVariable("PYROSCOPE_PROFILING_ENABLED");
        bool isPyroscopeEnabled = string.Equals(profilingEnabledSetting, "true", StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(profilingEnabledSetting, "1", StringComparison.OrdinalIgnoreCase);

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddProcessInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter(NexusMetrics.MeterName)
                .AddMeter("COMPEL.ServerEngine")
                .AddMeter("Microsoft.EntityFrameworkCore")
                .AddMeter("Npgsql")
                .AddMeter("StackExchange.Redis")
                .AddMeter("StackExchange.Redis.Server"))
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource(serviceName)
                    .AddSource(NexusTracing.ActivitySourceName)
                    .AddSource("COMPEL")
                    .AddSource("COMPEL.ServerEngine")
                    .AddSource("KONGOR.MasterServer")
                    .AddSource("ZORGATH.WebPortal.API")
                    .AddSource("DAWNBRINGER.WebPortal.UI")
                    .AddSource("NEXUS.Redis")
                    .AddSource("NEXUS.Database")
                    .AddSource("NEXUS.HybridCache")
                    .AddSource("NEXUS.MasterServer")
                    .AddSource("NEXUS.ChatServer")
                    .AddSource("NEXUS.WebPortal")
                    .AddSource("Npgsql")
                    .AddSource("Npgsql.OpenTelemetry")
                    .AddSource("StackExchange.Redis")
                    .AddSource("StackExchange.Redis.Server")
                    .AddSource("opentelemetry-cpp")
                    .AddSource("k2-game-server")
                    .AddSource("Microsoft.EntityFrameworkCore")
                    .AddSource("Microsoft.EntityFrameworkCore.Database.Command")
                    .AddSource("Microsoft.Data.SqlClient")
                    .AddSource("SqlClientDiagnosticListener")
                    .AddSource(MicrosecondAllocationProfiler.ActivitySourceName)
                    .AddSource(EbpfAllocationTraceAdapter.ActivitySourceName)
                    .AddAspNetCoreInstrumentation(options => { options.RecordException = true; })
                    .AddHttpClientInstrumentation();

                if (isPyroscopeEnabled) tracing.AddProcessor(new PyroscopeSpanProcessor());
            });

        builder.AddOpenTelemetryExporters();

        if (isPyroscopeEnabled) ConfigurePyroscopeContinuousProfiling(builder, serviceName);
        ConfigureDotnetMonitorContinuousProfiling(builder, serviceName);

        // Emit an initial startup trace span to ensure immediate span availability in Grafana Tempo
        NexusTracing.EmitStartupTrace(serviceName);

        return builder;
    }

    public static void ConfigureDotnetMonitorContinuousProfiling<TBuilder>(TBuilder builder, string serviceName)
        where TBuilder : IHostApplicationBuilder
    {
        try
        {
            string? monitorEnabled = builder.Configuration["DOTNET_MONITOR_ENABLED"] ??
                                     Environment.GetEnvironmentVariable("DOTNET_MONITOR_ENABLED");

            bool isEnabled = string.Equals(monitorEnabled, "true", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(monitorEnabled, "1", StringComparison.OrdinalIgnoreCase);

            if (isEnabled)
            {
                string? diagPort = builder.Configuration["DOTNET_DiagnosticPorts"] ??
                                   Environment.GetEnvironmentVariable("DOTNET_DiagnosticPorts");
                if (string.IsNullOrWhiteSpace(diagPort))
                {
                    string defaultPort = OperatingSystem.IsWindows()
                        ? @"\\.\pipe\dotnet-monitor-pipe,connect"
                        : "/tmp/dotnet-monitor.sock,connect";
                    Environment.SetEnvironmentVariable("DOTNET_DiagnosticPorts", defaultPort);
                }
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[DOTNET-MONITOR] Configuration notice: {ex.Message}");
        }
    }

    public static void ConfigurePyroscopeContinuousProfiling<TBuilder>(TBuilder builder, string serviceName)
        where TBuilder : IHostApplicationBuilder
    {
        try
        {
            string? profilingEnabledSetting = builder.Configuration["PYROSCOPE_PROFILING_ENABLED"] ??
                                              Environment.GetEnvironmentVariable("PYROSCOPE_PROFILING_ENABLED");

            bool isEnabled = string.Equals(profilingEnabledSetting, "true", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(profilingEnabledSetting, "1", StringComparison.OrdinalIgnoreCase);

            if (!isEnabled)
            {
                Environment.SetEnvironmentVariable("PYROSCOPE_PROFILING_ENABLED", "false");
                return;
            }

            string? serverAddress = builder.Configuration["PYROSCOPE_SERVER_ADDRESS"] ??
                                    Environment.GetEnvironmentVariable("PYROSCOPE_SERVER_ADDRESS");

            if (string.IsNullOrWhiteSpace(serverAddress)) serverAddress = "http://localhost:4040";

            Environment.SetEnvironmentVariable("PYROSCOPE_SERVER_ADDRESS", serverAddress);
            Environment.SetEnvironmentVariable("PYROSCOPE_APPLICATION_NAME", serviceName);
            Environment.SetEnvironmentVariable("PYROSCOPE_PROFILING_ENABLED", "true");

            string? authUser = builder.Configuration["PYROSCOPE_BASIC_AUTH_USER"] ??
                               Environment.GetEnvironmentVariable("PYROSCOPE_BASIC_AUTH_USER");
            string? authPassword = builder.Configuration["PYROSCOPE_BASIC_AUTH_PASSWORD"] ??
                                   Environment.GetEnvironmentVariable("PYROSCOPE_BASIC_AUTH_PASSWORD");
            string? authToken = builder.Configuration["PYROSCOPE_AUTH_TOKEN"] ??
                                Environment.GetEnvironmentVariable("PYROSCOPE_AUTH_TOKEN");

            try
            {
                if (!string.IsNullOrWhiteSpace(authUser) && !string.IsNullOrWhiteSpace(authPassword))
                    Profiler.Instance.SetBasicAuth(authUser, authPassword);
                else if (!string.IsNullOrWhiteSpace(authToken)) Profiler.Instance.SetAuthToken(authToken);

                Profiler.Instance.SetCPUTrackingEnabled(true);
                Profiler.Instance.SetAllocationTrackingEnabled(true);
                Profiler.Instance.SetContentionTrackingEnabled(true);
                Profiler.Instance.SetExceptionTrackingEnabled(true);
            }
            catch (DllNotFoundException)
            {
                // Native Pyroscope profiler binary not installed on this host environment
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[PYROSCOPE] Profiler start notification: {ex.Message}");
        }
    }

    public static Dictionary<string, object> CreateNexusResourceAttributes(
        string serviceName,
        string serviceNamespace = "NEXUS",
        string? serviceVersion = null,
        string? deploymentEnv = null)
    {
        serviceVersion ??= typeof(ServiceExtensions).Assembly.GetName().Version?.ToString() ?? "1.0.0";
        deploymentEnv ??= "Development";

        return new Dictionary<string, object>
        {
            ["service.name"] = serviceName,
            ["service.namespace"] = serviceNamespace,
            ["service.version"] = serviceVersion,
            ["service.instance.id"] = $"{serviceName}-{Environment.MachineName}",
            ["host.id"] = Environment.MachineName.ToLowerInvariant(),
            ["host.name"] = Environment.MachineName,
            ["grafana.host.id"] = Environment.MachineName.ToLowerInvariant(),
            ["deployment.environment"] = deploymentEnv,
            ["deployment.environment.name"] = deploymentEnv
        };
    }

    public static ResourceBuilder CreateNexusResourceBuilder(
        string serviceName,
        string serviceNamespace = "NEXUS",
        string? serviceVersion = null,
        string? deploymentEnv = null)
    {
        serviceVersion ??= typeof(ServiceExtensions).Assembly.GetName().Version?.ToString() ?? "1.0.0";
        deploymentEnv ??= "Development";

        return ResourceBuilder.CreateDefault()
            .AddService(serviceName, serviceNamespace, serviceVersion)
            .AddAttributes(CreateNexusResourceAttributes(serviceName, serviceNamespace, serviceVersion, deploymentEnv));
    }

    public static string GetBaseOtlpEndpoint(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return endpoint;

        string clean = endpoint.Trim();
        if (clean.EndsWith('/')) clean = clean.TrimEnd('/');

        if (clean.EndsWith("/v1/traces", StringComparison.OrdinalIgnoreCase))
            clean = clean[..^"/v1/traces".Length];
        else if (clean.EndsWith("/v1/metrics", StringComparison.OrdinalIgnoreCase))
            clean = clean[..^"/v1/metrics".Length];
        else if (clean.EndsWith("/v1/logs", StringComparison.OrdinalIgnoreCase)) clean = clean[..^"/v1/logs".Length];

        if (clean.EndsWith('/')) clean = clean.TrimEnd('/');

        return clean + "/";
    }

    private static TBuilder AddOpenTelemetryExporters<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        if (IsTestRunner()) return builder;

        // 1. Aspire Local Dashboard OTLP Exporter (Port 18889 gRPC)
        string? localOtlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] ??
                                    Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        if (string.IsNullOrWhiteSpace(localOtlpEndpoint)) localOtlpEndpoint = "http://localhost:18889";

        string? localProtocol = builder.Configuration["OTEL_EXPORTER_OTLP_PROTOCOL"] ??
                                Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_PROTOCOL");

        void ConfigureLocalOtlp(OtlpExporterOptions options)
        {
            options.TimeoutMilliseconds = 3000;
            if (Uri.TryCreate(localOtlpEndpoint, UriKind.Absolute, out Uri? uri)) options.Endpoint = uri;

            if (string.Equals(localProtocol, "http/protobuf", StringComparison.OrdinalIgnoreCase) ||
                localOtlpEndpoint.EndsWith(":4318", StringComparison.OrdinalIgnoreCase) ||
                localOtlpEndpoint.EndsWith(":18890", StringComparison.OrdinalIgnoreCase))
                options.Protocol = OtlpExportProtocol.HttpProtobuf;
            else
                options.Protocol = OtlpExportProtocol.Grpc;
        }

        // Register Primary Aspire Dashboard Exporter
        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics.AddOtlpExporter(ConfigureLocalOtlp))
            .WithTracing(tracing => tracing.AddOtlpExporter(ConfigureLocalOtlp));

        builder.Logging.AddOpenTelemetry(logging => { logging.AddOtlpExporter(ConfigureLocalOtlp); });

        // 2. Remote Grafana Cloud Exporter (if configured in Secrets or Environment)
        string? grafanaEndpoint = builder.Configuration["GRAFANA_OTLP_ENDPOINT"] ??
                                  Environment.GetEnvironmentVariable("GRAFANA_OTLP_ENDPOINT");
        string? grafanaHeaders = builder.Configuration["GRAFANA_OTLP_HEADERS"] ??
                                 Environment.GetEnvironmentVariable("GRAFANA_OTLP_HEADERS");
        bool enableGrafanaMetrics = string.Equals(builder.Configuration["GRAFANA_ENABLE_METRICS"], "true",
                                        StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(Environment.GetEnvironmentVariable("GRAFANA_ENABLE_METRICS"), "true",
                                        StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(grafanaEndpoint))
        {
            string baseEndpoint = GetBaseOtlpEndpoint(grafanaEndpoint);
            string cleanHeaders = !string.IsNullOrWhiteSpace(grafanaHeaders) &&
                                  grafanaHeaders.Contains("%20", StringComparison.OrdinalIgnoreCase)
                ? Uri.UnescapeDataString(grafanaHeaders)
                : grafanaHeaders ?? string.Empty;

            void ConfigureGrafanaOtlp(OtlpExporterOptions options, string signalPath)
            {
                options.TimeoutMilliseconds = 3000;
                string endpointUrl = baseEndpoint.TrimEnd('/') + signalPath;
                if (Uri.TryCreate(endpointUrl, UriKind.Absolute, out Uri? uri)) options.Endpoint = uri;
                options.Protocol = OtlpExportProtocol.HttpProtobuf;
                if (!string.IsNullOrWhiteSpace(cleanHeaders)) options.Headers = cleanHeaders;
            }

            // Cost Guardrail: High-cardinality .NET runtime metrics are NOT exported to Grafana Cloud by default to prevent Prometheus time-series explosion bills
            if (enableGrafanaMetrics)
                builder.Services.AddOpenTelemetry()
                    .WithMetrics(metrics =>
                        metrics.AddOtlpExporter(options => ConfigureGrafanaOtlp(options, "/v1/metrics")));

            // Traces and Logs (Generous free tier / zero metric series cardinality risk)
            builder.Services.AddOpenTelemetry()
                .WithTracing(tracing =>
                    tracing.AddOtlpExporter(options => ConfigureGrafanaOtlp(options, "/v1/traces")));

            builder.Logging.AddOpenTelemetry(logging =>
            {
                logging.AddOtlpExporter(options => ConfigureGrafanaOtlp(options, "/v1/logs"));
            });
        }

        return builder;
    }

    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.AddNexusDefaultHealthChecks();
        return builder;
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        app.MapNexusHealthCheckEndpoints();
        return app;
    }

    /// <summary>
    ///     Configures Forwarded Headers middleware options for reverse proxy support (Cloudflare / Docker Bridge / Infrastructure Gateway).
    /// </summary>
    public static IServiceCollection ConfigureInfrastructureForwardedHeaders(this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto |
                                       ForwardedHeaders.XForwardedHost;

            bool trustAll = IsTrustAllProxiesEnabled(configuration);
            if (trustAll)
            {
                options.KnownIPNetworks.Clear();
                options.KnownProxies.Clear();
                options.ForwardLimit = null;
                return;
            }

            // Trust RFC1918 private container / VPC subnets, link-local, IPv6 ULA, and local loopback addresses
            options.KnownIPNetworks.Add(new IPNetwork(IPAddress.Parse("10.0.0.0"), 8));
            options.KnownIPNetworks.Add(new IPNetwork(IPAddress.Parse("172.16.0.0"), 12));
            options.KnownIPNetworks.Add(new IPNetwork(IPAddress.Parse("192.168.0.0"), 16));
            options.KnownIPNetworks.Add(new IPNetwork(IPAddress.Parse("169.254.0.0"), 16));
            options.KnownIPNetworks.Add(new IPNetwork(IPAddress.Loopback, 8));
            options.KnownIPNetworks.Add(new IPNetwork(IPAddress.IPv6Loopback, 128));
            options.KnownIPNetworks.Add(new IPNetwork(IPAddress.Parse("fc00::"), 7));
            options.KnownIPNetworks.Add(new IPNetwork(IPAddress.Parse("fe80::"), 10));

            foreach (string key in TrustedProxyConfigKeys)
            {
                string? rawProxies = configuration[key] ?? Environment.GetEnvironmentVariable(key.Replace(':', '_'));
                if (string.IsNullOrWhiteSpace(rawProxies)) continue;

                foreach (string entry in rawProxies.Split(ProxyListSeparators,
                             StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (IPAddress.TryParse(entry, out IPAddress? parsedProxyIp))
                    {
                        options.KnownProxies.Add(parsedProxyIp.IsIPv4MappedToIPv6
                            ? parsedProxyIp.MapToIPv4()
                            : parsedProxyIp);
                    }
                }
            }

            string proxy = configuration["INFRASTRUCTURE_GATEWAY"]
                           ?? Environment.GetEnvironmentVariable("INFRASTRUCTURE_GATEWAY")
                           ?? "localhost";

            // Tier 1: Direct IP Fast-Path (skips DNS lookup entirely)
            if (IPAddress.TryParse(proxy, out var directIp))
            {
                if (directIp.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                {
                    options.KnownProxies.Add(directIp.IsIPv4MappedToIPv6 ? directIp.MapToIPv4() : directIp);
                }
            }
            // Tier 2: Localhost Fast-Path (binds loopback IPv4 and IPv6 without DNS network stack delay)
            else if (proxy.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            {
                options.KnownProxies.Add(IPAddress.Loopback);
                options.KnownProxies.Add(IPAddress.IPv6Loopback);
            }
            // Tier 3: Custom domain hostname lookup with non-fatal fallback
            else
            {
                try
                {
                    IPAddress[] proxyResolvedAddresses = Dns.GetHostAddresses(proxy);
                    foreach (IPAddress proxyResolvedAddress in proxyResolvedAddresses)
                    {
                        if (proxyResolvedAddress.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                        {
                            options.KnownProxies.Add(proxyResolvedAddress.IsIPv4MappedToIPv6
                                ? proxyResolvedAddress.MapToIPv4()
                                : proxyResolvedAddress);
                        }
                    }
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine($"[ServiceExtensions] Warning: Failed to resolve proxy host '{proxy}': {exception.Message}. Proceeding without additional proxy binding.");
                }
            }

            // Allow up to 4 proxy hops by default (e.g. Viewer -> Cloudflare Edge -> Cloudflared Tunnel -> Docker Bridge -> Service)
            string? forwardLimitRaw = configuration["ForwardedHeaders:ForwardLimit"];
            options.ForwardLimit = int.TryParse(forwardLimitRaw, out int configuredLimit) && configuredLimit > 0
                ? configuredLimit
                : 4;
        });

        return services;
    }

    /// <summary>
    ///     Enables infrastructure-aware ForwardedHeaders plus Cloudflare Tunnel (<c>CF-Connecting-IP</c> / <c>CF-Visitor</c>)
    ///     and IPv4-mapped IPv6 Docker bridge normalization when the TCP socket peer is a trusted proxy.
    /// </summary>
    public static Microsoft.AspNetCore.Builder.IApplicationBuilder UseInfrastructureForwardedHeaders(
        this Microsoft.AspNetCore.Builder.IApplicationBuilder app,
        IConfiguration? configuration = null)
    {
        // Step 1: Unmap ::ffff:<ipv4> before ForwardedHeadersMiddleware evaluates IPv4 KnownIPNetworks CIDRs,
        // and stash whether the raw socket peer was a trusted proxy before RemoteIpAddress is rewritten.
        const string TrustedPeerKey = "__NexusTrustedProxySocketPeer";
        const string OriginalPeerIpKey = "__NexusOriginalProxySocketIp";

        app.Use(async (context, next) =>
        {
            IPAddress? rawPeer = context.Connection.RemoteIpAddress;
            if (rawPeer is { IsIPv4MappedToIPv6: true })
            {
                rawPeer = rawPeer.MapToIPv4();
                context.Connection.RemoteIpAddress = rawPeer;
            }

            IConfiguration? effectiveConfig = configuration ?? context.RequestServices.GetService<IConfiguration>();
            if (rawPeer is not null && IsTrustedInfrastructureProxyPeer(rawPeer, effectiveConfig))
            {
                context.Items[TrustedPeerKey] = true;
                context.Items[OriginalPeerIpKey] = rawPeer.ToString();
            }

            await next();
        });

        // Step 2: Standard ASP.NET Core X-Forwarded-For / X-Forwarded-Proto / X-Forwarded-Host processing
        app.UseForwardedHeaders();

        // Step 3: Promote CF-Connecting-IP and CF-Visitor https scheme when arriving from a verified trusted proxy socket
        app.Use(async (context, next) =>
        {
            if (context.Items.TryGetValue(TrustedPeerKey, out object? trustedFlag) && trustedFlag is true)
            {
                if (context.Request.Headers.TryGetValue("CF-Connecting-IP", out Microsoft.Extensions.Primitives.StringValues cfIpVals) &&
                    !Microsoft.Extensions.Primitives.StringValues.IsNullOrEmpty(cfIpVals))
                {
                    string firstCfToken = cfIpVals.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
                    if (IPAddress.TryParse(firstCfToken, out IPAddress? cfIp))
                    {
                        context.Connection.RemoteIpAddress = cfIp.IsIPv4MappedToIPv6 ? cfIp.MapToIPv4() : cfIp;
                    }
                }

                if (!string.Equals(context.Request.Scheme, "https", StringComparison.OrdinalIgnoreCase) &&
                    context.Request.Headers.TryGetValue("CF-Visitor", out Microsoft.Extensions.Primitives.StringValues cfVisitorVals) &&
                    cfVisitorVals.ToString().Contains("\"https\"", StringComparison.OrdinalIgnoreCase))
                {
                    context.Request.Scheme = "https";
                }

                ILogger? proxyLogger = context.RequestServices
                    .GetService<ILoggerFactory>()
                    ?.CreateLogger("ASPIRE.ForwardedHeaders");
                if (proxyLogger?.IsEnabled(LogLevel.Debug) == true)
                {
                    proxyLogger.LogDebug(
                        "[ForwardedHeaders] Trusted proxy socket '{ProxyIp}' -> Scheme='{Scheme}', Host='{Host}', ClientIp='{ClientIp}'.",
                        context.Items[OriginalPeerIpKey] ?? "unknown",
                        context.Request.Scheme,
                        context.Request.Host.Value,
                        context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
                }
            }

            await next();
        });

        return app;
    }

    public static bool IsTrustedInfrastructureProxyPeer(IPAddress? ip, IConfiguration? configuration = null)
    {
        if (ip is null) return false;
        if (IsTrustAllProxiesEnabled(configuration)) return true;

        IPAddress normalized = ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
        if (IPAddress.IsLoopback(normalized)) return true;

        if (normalized.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = normalized.GetAddressBytes();
            if (b[0] == 10) return true;
            if (b[0] == 172 && b[1] is >= 16 and <= 31) return true;
            if (b[0] == 192 && b[1] == 168) return true;
            if (b[0] == 169 && b[1] == 254) return true;
        }
        else if (normalized.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (normalized.IsIPv6LinkLocal || normalized.IsIPv6SiteLocal) return true;
            byte[] b = normalized.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return true;
        }

        if (configuration is not null)
        {
            foreach (string key in PeerProxyConfigKeys)
            {
                string? raw = configuration[key];
                if (string.IsNullOrWhiteSpace(raw)) continue;
                foreach (string entry in raw.Split(ProxyListSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (IPAddress.TryParse(entry, out IPAddress? configuredIp))
                    {
                        IPAddress normConfigured = configuredIp.IsIPv4MappedToIPv6 ? configuredIp.MapToIPv4() : configuredIp;
                        if (normConfigured.Equals(normalized)) return true;
                    }
                }
            }
        }

        return false;
    }

    private static bool IsTrustAllProxiesEnabled(IConfiguration? configuration)
    {
        string? trustAllConfig = configuration?["TRUST_ALL_PROXIES"]
                                 ?? Environment.GetEnvironmentVariable("TRUST_ALL_PROXIES");
        return !string.IsNullOrWhiteSpace(trustAllConfig) &&
               ((bool.TryParse(trustAllConfig, out bool parsedTrustAll) && parsedTrustAll) ||
                string.Equals(trustAllConfig, "1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(trustAllConfig, "true", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsTestRunner()
    {
        return AppDomain.CurrentDomain.GetAssemblies().Any(assembly =>
        {
            string? name = assembly.GetName().Name;
            return name != null && (name.Contains("xunit", StringComparison.OrdinalIgnoreCase) ||
                                    name.Contains("testhost", StringComparison.OrdinalIgnoreCase) ||
                                    name.Contains("vstest", StringComparison.OrdinalIgnoreCase));
        });
    }
}

public sealed class OtelDiagnosticListener : EventListener
{
    private static OtelDiagnosticListener? _instance;

    public static void EnsureInitialized(IConfiguration? configuration = null)
    {
        string? enabledSetting = configuration?["OTEL_DIAGNOSTICS_ENABLED"] ??
                                 Environment.GetEnvironmentVariable("OTEL_DIAGNOSTICS_ENABLED");

        bool isExplicitlyEnabled = string.Equals(enabledSetting, "true", StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(enabledSetting, "1", StringComparison.OrdinalIgnoreCase);

        if (!isExplicitlyEnabled) return;

        _instance ??= new OtelDiagnosticListener();
    }

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name.StartsWith("OpenTelemetry", StringComparison.OrdinalIgnoreCase))
            EnableEvents(eventSource, EventLevel.Verbose, EventKeywords.All);
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        try
        {
            string message = eventData is { Message: not null, Payload.Count: > 0 }
                ? string.Format(eventData.Message, [.. eventData.Payload])
                : eventData.EventName ?? "UnknownEvent";
            Console.WriteLine($"[OTEL DIAGNOSTIC] {eventData.EventSource.Name} -> {message}");
        }
        catch
        {
            Console.WriteLine($"[OTEL DIAGNOSTIC] {eventData.EventSource.Name} -> Event ID {eventData.EventId}");
        }
    }
}
