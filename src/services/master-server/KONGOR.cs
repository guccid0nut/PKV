// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using ASPIRE.Common.Extensions;
using ASPIRE.Common.Models.Replay;
using ASPIRE.Common.Services.EntityCache;
using ASPIRE.Common.Telemetry;
using Kongor.Core.Services.Events;
using KONGOR.MasterServer.Configuration.Rating;
using KONGOR.MasterServer.Extensions;
using KONGOR.MasterServer.Hubs;
using KONGOR.MasterServer.Infrastructure;
using KONGOR.MasterServer.Serialization;
using KONGOR.MasterServer.Services.Background;
using KONGOR.MasterServer.Services.Economy;
using KONGOR.MasterServer.Services.Events;
using KONGOR.MasterServer.Services.Grpc;
using KONGOR.MasterServer.Services.Mastery;
using KONGOR.MasterServer.Services.Matches;
using KONGOR.MasterServer.Services.Network;
using KONGOR.MasterServer.Services.Quests;
using KONGOR.MasterServer.Services.Rating;
using KONGOR.MasterServer.Services.Replay;
using KONGOR.MasterServer.Services.Security;
using KONGOR.MasterServer.Services.Stats;
using KONGOR.MasterServer.Services.Store;
using KONGOR.MasterServer.Services.Telemetry;
using MERRICK.DatabaseContext.Extensions;
using Microsoft.Extensions.Primitives;
using NEXUS.Contracts.Economy;
using NEXUS.Contracts.Security;
using NEXUS.Contracts.Services;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.OpenTelemetry;
using Scalar.AspNetCore;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace KONGOR.MasterServer;

public static class KONGOR
{
    // The Count Of Seconds Since The UNIX Epoch (Epochalypse = 19.01.2038 @ 03:14:07 UTC)
    public static long ServerStartEpochTime { get; private set; }

    public static async Task Main(string[] args)
    {
        // Create The Application Builder
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

        // Load Solution User Secrets (DB connection strings, API keys, host credentials)
#if !PLAYTEST_BUILD
        builder.Configuration.AddUserSecrets(NEXUS.Contracts.Security.UserSecretsConstants.SolutionUserSecretsId, reloadOnChange: true);
#endif
        builder.Configuration.AddEnvironmentVariables();
        builder.Configuration.AddCommandLine(args);

        // Configure Serilog for Console and File logging
        LoggerConfiguration loggerConfig = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Information)
            .MinimumLevel.Override("StackExchange.Redis", LogEventLevel.Fatal)
            .ReadFrom.Configuration(builder.Configuration)
            .Enrich.FromLogContext()
            .WriteTo.Console(builder.Configuration.GetValue<bool>("IS_TEST_ENVIRONMENT")
                ? LogEventLevel.Fatal
                : LogEventLevel.Information)
            .WriteTo.File("logs/master_server.log",
                rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                retainedFileCountLimit: 20,
                rollOnFileSizeLimit: true);

        string? kongorOtlpEndpoint = builder.Configuration["GRAFANA_OTLP_ENDPOINT"] ??
                                     builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] ??
                                     Environment.GetEnvironmentVariable("GRAFANA_OTLP_ENDPOINT") ??
                                     Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        if (!string.IsNullOrWhiteSpace(kongorOtlpEndpoint) &&
            !builder.Configuration.GetValue<bool>("IS_TEST_ENVIRONMENT"))
        {
            string? kongorHeaders = builder.Configuration["GRAFANA_OTLP_HEADERS"] ??
                                    builder.Configuration["OTEL_EXPORTER_OTLP_HEADERS"] ??
                                    Environment.GetEnvironmentVariable("GRAFANA_OTLP_HEADERS") ??
                                    Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_HEADERS");
            string kongorNamespace = builder.Configuration["OTEL_SERVICE_NAMESPACE"] ??
                                     Environment.GetEnvironmentVariable("OTEL_SERVICE_NAMESPACE") ??
                                     "NEXUS";
            string baseEndpoint = ServiceExtensions.GetBaseOtlpEndpoint(kongorOtlpEndpoint);
            Dictionary<string, string> headersDict = [];
            if (!string.IsNullOrWhiteSpace(kongorHeaders))
            {
                string cleanHeaders = kongorHeaders.Contains("%20", StringComparison.OrdinalIgnoreCase)
                    ? Uri.UnescapeDataString(kongorHeaders)
                    : kongorHeaders;
                foreach (string pair in cleanHeaders.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] kv = pair.Split('=', 2);
                    if (kv.Length == 2) headersDict[kv[0].Trim()] = kv[1].Trim();
                }
            }

            string kongorServiceName = builder.Configuration["OTEL_SERVICE_NAME"] ??
                                       Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME") ??
                                       builder.Environment.ApplicationName;

            loggerConfig.WriteTo.OpenTelemetry(options =>
            {
                options.Endpoint = baseEndpoint.TrimEnd('/') + "/v1/logs";
                options.Protocol = OtlpProtocol.HttpProtobuf;
                options.Headers = headersDict;
                options.ResourceAttributes = new Dictionary<string, object>
                {
                    ["service.name"] = kongorServiceName,
                    ["service.namespace"] = kongorNamespace,
                    ["service.instance.id"] = $"{kongorServiceName}-{Environment.MachineName}",
                    ["host.id"] = Environment.MachineName.ToLowerInvariant(),
                    ["host.name"] = Environment.MachineName,
                    ["grafana.host.id"] = Environment.MachineName.ToLowerInvariant(),
                    ["deployment.environment"] = builder.Environment.EnvironmentName,
                    ["deployment.environment.name"] = builder.Environment.EnvironmentName
                };
            });
        }

        Log.Logger = loggerConfig.CreateLogger();
        Log.Information("[Standalone] Configuration precedence: user-secrets < env < args");
        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog(Log.Logger);

        // Prevent BackgroundService transient failures from terminating the Kestrel web host
        builder.Services.Configure<HostOptions>(options =>
        {
            options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore;
        });

        // Set Static ServerStartEpochTime Property
        ServerStartEpochTime = TimeProvider.System.GetUtcNow().ToUnixTimeSeconds();

        // Map User-Defined Configuration Section
        builder.Services.Configure<OperationalConfiguration>(
            builder.Configuration.GetRequiredSection(OperationalConfiguration.ConfigurationSection));
        builder.Services.PostConfigure<OperationalConfiguration>(options =>
        {
            options.CDN ??= new OperationalConfigurationCDN
            {
                Host = "http://localhost:55555",
                PrimaryPatchURL = "http://localhost:55555/patch",
                SecondaryPatchURL = "http://localhost:55555/patch"
            };
            string msAddress = builder.Configuration.GetMasterServerAddress();
            if (!string.IsNullOrWhiteSpace(msAddress))
            {
                string trimmed = msAddress.TrimEnd('/');
                if (string.IsNullOrWhiteSpace(options.CDN.Host) || options.CDN.Host == "http://localhost:55555" ||
                    options.CDN.Host == "http://api.kongor.net")
                {
                    options.CDN.Host = trimmed;
                    options.CDN.PrimaryPatchURL = $"{trimmed}/patch";
                    options.CDN.SecondaryPatchURL = $"{trimmed}/patch";
                }
            }
        });

        // Add Aspire Service Defaults
        builder.AddServiceDefaults();

        // Add The Database Context with Multi-Provider Support (SQL Server & PostgreSQL / Supabase)
        builder.AddMerrickDatabase();

        // Add Distributed Cache; The Connection String Maps To The "distributed-cache" Resource Defined In ASPIRE.ApplicationHost
        builder.AddRedisClient("DISTRIBUTED-CACHE");

        // Register IDatabase From IConnectionMultiplexer
        builder.Services.AddSingleton(serviceProvider =>
            serviceProvider.GetRequiredService<IConnectionMultiplexer>().GetDatabase());

        // Register Distributed Redlock Service
        builder.Services.AddDistributedLockService();

        // Add Memory Cache & Hybrid Cache Services
        builder.Services.AddNexusHybridCache();

        // Register MediatR CQRS Pipeline with Logging, Validation, Caching & Distributed Locking
        builder.AddMediatRPipeline(typeof(KONGOR).Assembly);
        builder.Services.AddValidatorsFromAssembly(typeof(KONGOR).Assembly);

        // Register Asynchronous Domain Event Bus & Subscriptions
        builder.Services.AddRedisEventBus();
        builder.Services.AddEventHandler<MasterServerAccountBannedEventHandler>();
        builder.Services.AddEventHandler<MasterServerUserLoggedInEventHandler>();

        // Register Network Topology Resolver Service (Public IP & Chat Routing Background Engine)
        builder.Services.AddSingleton<NetworkTopologyResolverService>();
        builder.Services.AddSingleton<INetworkTopologyResolver>(sp => sp.GetRequiredService<NetworkTopologyResolverService>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<NetworkTopologyResolverService>());

        // Register Pre-Baked Entity Registry and Asynchronous Pre-Warming Engine
        builder.Services.AddPrebakedEntityServices();

        // Register Hero Definition Service (Dynamic Upgrades.JSON mapping)
        builder.Services.AddSingleton<IHeroDefinitionService, HeroDefinitionService>();
        builder.Services.AddSingleton<IStoreCatalogService, StoreCatalogService>();
        builder.Services.AddSingleton<IMasteryRewardService, MasteryRewardService>();
        builder.Services.AddSingleton<IPlayerProfileStatsBuilder, PlayerProfileStatsBuilder>();
        builder.Services.AddSingleton<ILegacyProtocolFormatter, LegacyProtocolFormatter>();
        builder.Services.AddScoped<ISessionContextResolver, SessionContextResolver>();
        builder.Services.AddSingleton<MERRICK.DatabaseContext.Services.Argon2idHasher>();
        builder.Services.AddSingleton<IAccountPasswordVerifier, AccountPasswordVerifier>();

        // Initialize Store & Item Progression Mode toggle (Sandbox vs Progression)
        UpgradesResolutionService.GetUnlockAllSetting(builder.Configuration);

        // Register Economy Service
        builder.Services.AddScoped<IEconomyService, EconomyService>();

        // Register Player Statistics Service
        builder.Services.AddScoped<IPlayerStatisticsService, PlayerStatisticsService>();

        // Register OpenSkill Bayesian Rating Service
        builder.Services.Configure<OpenSkillConfiguration>(
            builder.Configuration.GetSection(OpenSkillConfiguration.SectionName));
        builder.Services.AddSingleton<IOpenSkillRatingService, OpenSkillRatingService>();

        // Register Decomposed Match Processing Sub-Services
        builder.Services.AddScoped<IMatchIngestionHandler, MatchIngestionHandler>();
        builder.Services.AddScoped<IHeroMasteryProgressionService, HeroMasteryProgressionService>();
        builder.Services.AddScoped<IOpenSkillRatingCalculator, OpenSkillRatingCalculator>();
        builder.Services.AddScoped<IReplayPersistenceWorker, ReplayPersistenceWorker>();

        // Register Match Processing Service
        builder.Services.AddScoped<IMatchProcessingService>(sp => new MatchProcessingService(
            sp.GetRequiredService<IMatchIngestionHandler>(),
            sp.GetRequiredService<IHeroMasteryProgressionService>(),
            sp.GetRequiredService<IOpenSkillRatingCalculator>(),
            sp.GetRequiredService<IReplayPersistenceWorker>(),
            sp.GetRequiredService<ILogger<MatchProcessingService>>(),
            sp.GetRequiredService<MerrickContext>(),
            sp.GetRequiredService<IWarEffortProgressionService>()));
        builder.Services.AddScoped<MatchProcessingService>(sp => (MatchProcessingService)sp.GetRequiredService<IMatchProcessingService>());

        // Register Asynchronous Match Processing Channel, Dead-Letter Queue & Worker
        builder.Services.Configure<MatchProcessingChannelOptions>(
            builder.Configuration.GetSection("MatchProcessingChannel"));
        builder.Services.PostConfigure<MatchProcessingChannelOptions>(options =>
        {
            string? explicitFlag = builder.Configuration["MatchProcessingChannel:RequireDurableStreams"];
            if (string.IsNullOrWhiteSpace(explicitFlag) &&
                builder.Environment.IsProduction() &&
                !string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("DISTRIBUTED-CACHE")))
            {
                options.RequireDurableStreams = true;
            }
        });
        builder.Services.AddSingleton<IMatchProcessingChannel, MatchProcessingChannel>();
        builder.Services.AddHealthChecks()
            .AddCheck<MatchProcessingChannelHealthCheck>("match-processing-streams", tags: ["ready", "cache"]);
        builder.Services.AddSingleton<IMatchDeadLetterQueue, MatchDeadLetterQueue>();
        builder.Services.AddHostedService<MatchProcessingBackgroundWorker>();

        // Register Match ID Initializer Service
        builder.Services.AddHostedService<MatchIdInitializerService>();

        // Register Discord Presence Subscriber Service
        builder.Services.AddHostedService<DiscordPresenceRedisSubscriber>();

        // Register Periodic Telemetry Tracker & Worker
        builder.Services.AddSingleton<IMasterServerRequestTracker, MasterServerRequestTracker>();
        builder.Services.AddHostedService<MasterServerTelemetryWorker>();

        // Add Rate Limiting Service To Protect Against Abuse And DoS Attacks
        builder.Services.AddSingleton<IPerIpAuthRateLimiter, PerIpAuthRateLimiter>();
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = (context, _) =>
            {
                ILogger? rlLogger = context.HttpContext.RequestServices
                    .GetService<ILoggerFactory>()?
                    .CreateLogger("Security.RateLimiter");
                rlLogger?.LogWarning(
                    "[SECURITY-ALERT] [MasterServer] Rate limit rejected HTTP {Method} {Path} from IP '{RemoteIp}' (429 Too Many Requests).",
                    context.HttpContext.Request.Method,
                    context.HttpContext.Request.Path,
                    context.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown");
                return ValueTask.CompletedTask;
            };

            // Per-IP Token Bucket For Legacy Client Polling And Master Server Endpoints
            options.AddPolicy(RateLimiterPolicies.PerIpTokenBucket, context =>
                RateLimitPartition.GetTokenBucketLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new TokenBucketRateLimiterOptions
                    {
                        TokenLimit = 60,
                        QueueLimit = 0,
                        ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                        TokensPerPeriod = 10,
                        AutoReplenishment = true
                    }));

            // Relaxed Limits For General API Endpoints (IP-Partitioned to prevent cross-player global bucket exhaustion)
            options.AddPolicy(RateLimiterPolicies.Relaxed, context =>
                RateLimitPartition.GetSlidingWindowLimiter(
                    partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
                    factory: _ => new SlidingWindowRateLimiterOptions
                    {
                        PermitLimit = 100,
                        Window = TimeSpan.FromMinutes(1),
                        SegmentsPerWindow = 6, // 10 Seconds Per Sliding Window Segment
                        QueueLimit = 10,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                    }));

            // Strict Limits For Authentication And Other Sensitive Endpoints (IP-Partitioned)
            options.AddPolicy(RateLimiterPolicies.Strict, context =>
                RateLimitPartition.GetSlidingWindowLimiter(
                    partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
                    factory: _ => new SlidingWindowRateLimiterOptions
                    {
                        PermitLimit = 5,
                        Window = TimeSpan.FromMinutes(1),
                        SegmentsPerWindow = 6, // 10 Seconds Per Sliding Window Segment
                        QueueLimit = 0,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                    }));
        });

        // Add HTTP Request/Response Logging For Debugging In Development
        if (builder.Environment.IsDevelopment())
            builder.Services.AddHttpLogging(options =>
            {
                options.LoggingFields = HttpLoggingFields.RequestPropertiesAndHeaders |
                                        HttpLoggingFields.ResponsePropertiesAndHeaders;
                options.RequestBodyLogLimit = 4096; /* 4KB Request Body Limit */
                options.ResponseBodyLogLimit = 4096; /* 4KB Response Body Limit */
            });

        // Register Master Server Client Request Handlers & War Effort Options
        builder.Services.Configure<NEXUS.Contracts.Quests.WarEffortOptions>(
            builder.Configuration.GetSection(NEXUS.Contracts.Quests.WarEffortOptions.SectionName));
        builder.Services.AddMasterServerHandlers();

        // Register HTTP Client and gRPC Services
        builder.Services.AddHttpClient();
        builder.Services.AddHttpClient("ReplayRendererClient");
        builder.Services.AddGrpc();

        // Register Replay Storage Provider
        builder.Services.AddReplayStorage(builder.Configuration);

        // Register Replay-to-Video Converter & Settings
        builder.Services.Configure<ReplayConverterSettings>(builder.Configuration.GetSection("ReplayConverter"));
        builder.Services.Configure<VideoStorageSettings>(builder.Configuration.GetSection("VideoStorage"));
        builder.Services.AddScoped<IReplayToVideoConverter, ReplayToVideoConverter>();

        // Add MVC Controllers Support
        builder.Services.AddControllers();

        // Add SignalR Support
        builder.Services.AddSignalR()
            .AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions.TypeInfoResolver = MasterServerJsonContext.Default;
            });

        // Add ProblemDetails Support Across All Environments (Required For /error Global Exception Handler)
        builder.Services.AddProblemDetails();

        // Add OpenAPI Documentation Generation
        builder.Services.AddOpenApi();

        // Configure Forwarded Headers For Reverse Proxy Support
        builder.Services.ConfigureInfrastructureForwardedHeaders(builder.Configuration);

        // Multi-Interface Binding: Bind to 0.0.0.0 so local containers, K3s pods, and LAN nodes can reach MasterServer
        int masterServerPort = builder.Configuration.GetValue<int?>("MasterServer:Port") ?? 55555;
        string? envUrls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? builder.Configuration["Kestrel:Endpoints:Http:Url"];
        if (string.IsNullOrWhiteSpace(envUrls))
        {
            builder.WebHost.UseUrls($"http://0.0.0.0:{masterServerPort}");
        }

        // Build The Application
        WebApplication application = builder.Build();
        bool startupUnlockAllItems = UpgradesResolutionService.InitializeGlobalUnlockAllSetting(application.Configuration);
        application.Logger.LogInformation("[Startup] Store.UnlockAllItems default initialized to {UnlockAllItems}", startupUnlockAllItems);

        // Enable Forwarded Headers Middleware For Reverse Proxy Support
        application.UseForwardedHeaders();

        // Disable Proxy Compression (Cloudflare) For Legacy Engine Compatibility & Track Telemetry
        application.Use(async (context, next) =>
        {
            UpgradesResolutionService.UnlockAllItems =
                UpgradesResolutionService.ReadUnlockAllSetting(context.RequestServices.GetService<IConfiguration>());
            context.Response.Headers.Append("Cache-Control", "no-transform");
            NexusMetrics.MasterServerRequestsTotal.Add(1);
            context.RequestServices.GetService<IMasterServerRequestTracker>()?.TrackRequest();

            await next();
        });

        if (application.Services.GetService<IConnectionMultiplexer>() is { } connectionMultiplexer)
        {
            // Purge All Session Cookies From Cache At Startup To Prevent Stale Authentication Data
            // Only Run If IConnectionMultiplexer Is Registered (Skipped In Tests That Use In-Process Test Doubles)

            // await connectionMultiplexer.PurgeSessionCookies();
        }

        // Configure Development-Specific Middleware
        if (application.Environment.IsDevelopment())
        {
            // Show Detailed Error Pages In Development
            application.UseDeveloperExceptionPage();

            // Enable Custom Exception Logging To Expose Messages In Structured Logs (Must Be After Exception Handler To Catch Exceptions Before They Are Swallowed)
            application.UseMiddleware<ExceptionLoggingMiddleware>();

            // Enable HTTP Request/Response Logging
            application.UseHttpLogging();
        }

        else
        {
            // Use Global Exception Handler In Production
            application.UseExceptionHandler("/error");

            // Enable Custom Exception Logging To Expose Messages In Structured Logs
            application.UseMiddleware<ExceptionLoggingMiddleware>();
        }

        // Enable Native OpenAPI & Interactive Scalar Documentation Strictly in Development (or Explicit Opt-In)
        if (application.Environment.IsDevelopment() ||
            builder.Configuration.GetValue("Operational:EnableOpenApiInProduction", false))
        {
            application.MapOpenApi();
            application.MapScalarApiReference(options =>
            {
                options.WithTitle("KONGOR Master Server API")
                    .WithTheme(ScalarTheme.Moon);
            });

            // Redirect legacy /swagger endpoint to modern Scalar interface
            application.MapGet("/swagger", () => Results.Redirect("/scalar/v1")).ExcludeFromDescription();

            // Serve Static Files For Swagger CSS
            application.UseStaticFiles(new StaticFileOptions
            {
                FileProvider =
                    new PhysicalFileProvider(Path.Combine(builder.Environment.ContentRootPath, "Resources", "CSS")),
                RequestPath = "/swagger"
            });
        }

        // Serve Replay Files
        List<IFileProvider> replayFileProviders = [];

        string? configUploadDir = builder.Configuration["ReplayStorage:LocalDisk:UploadDirectory"];
        if (!string.IsNullOrWhiteSpace(configUploadDir) && Directory.Exists(configUploadDir))
            replayFileProviders.Add(new PhysicalFileProvider(configUploadDir));

        string clientUserDataReplayPath = Path.Combine(builder.Environment.ContentRootPath, "games", "client",
            "userdata", "game", "replays");
        if (Directory.Exists(clientUserDataReplayPath) && !replayFileProviders.OfType<PhysicalFileProvider>()
                .Any(p => string.Equals(p.Root, clientUserDataReplayPath, StringComparison.OrdinalIgnoreCase)))
            replayFileProviders.Add(new PhysicalFileProvider(clientUserDataReplayPath));

        string localUserDataReplayPath =
            Path.Combine(builder.Environment.ContentRootPath, "userdata", "game", "replays");
        if (Directory.Exists(localUserDataReplayPath) && !replayFileProviders.OfType<PhysicalFileProvider>()
                .Any(p => string.Equals(p.Root, localUserDataReplayPath, StringComparison.OrdinalIgnoreCase)))
            replayFileProviders.Add(new PhysicalFileProvider(localUserDataReplayPath));

        string userDocsReplayPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Heroes of Newerth x64", "game", "replays"
        );
        if (Directory.Exists(userDocsReplayPath) && !replayFileProviders.OfType<PhysicalFileProvider>()
                .Any(p => string.Equals(p.Root, userDocsReplayPath, StringComparison.OrdinalIgnoreCase)))
            replayFileProviders.Add(new PhysicalFileProvider(userDocsReplayPath));

        string oneDriveDocsReplayPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "OneDrive", "Documents", "Heroes of Newerth x64", "game", "replays"
        );
        if (Directory.Exists(oneDriveDocsReplayPath) && !replayFileProviders.OfType<PhysicalFileProvider>()
                .Any(p => string.Equals(p.Root, oneDriveDocsReplayPath, StringComparison.OrdinalIgnoreCase)))
            replayFileProviders.Add(new PhysicalFileProvider(oneDriveDocsReplayPath));

        string defaultReplayPath = Path.Combine(AppContext.BaseDirectory, "data", "replays");
        if (!Directory.Exists(defaultReplayPath)) Directory.CreateDirectory(defaultReplayPath);

        replayFileProviders.Add(new PhysicalFileProvider(defaultReplayPath));

        application.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new CompositeFileProvider(replayFileProviders),
            RequestPath = "/replays",
            ServeUnknownFileTypes = true,
            OnPrepareResponse = ctx =>
            {
                ctx.Context.Response.Headers.Append("Access-Control-Allow-Origin", "*");
                ctx.Context.Response.Headers.Append("Access-Control-Allow-Methods", "GET, HEAD, OPTIONS");
                if (ctx.File.Name.EndsWith(".honreplay", StringComparison.OrdinalIgnoreCase))
                {
                    ctx.Context.Response.ContentType = "application/octet-stream";
                    ctx.Context.Response.Headers.Append("Content-Disposition",
                        $"attachment; filename=\"{ctx.File.Name}\"");
                }
            }
        });

        // Serve Video Files (/videos)
        List<IFileProvider> videoFileProviders = [];
        string? configVideoDir = builder.Configuration["VideoStorage:LocalDisk:UploadDirectory"];
        if (!string.IsNullOrWhiteSpace(configVideoDir) && Directory.Exists(configVideoDir))
            videoFileProviders.Add(new PhysicalFileProvider(configVideoDir));

        string clientUserDataVideoPath = Path.Combine(builder.Environment.ContentRootPath, "games", "client",
            "userdata", "game", "videos");
        if (Directory.Exists(clientUserDataVideoPath) && !videoFileProviders.OfType<PhysicalFileProvider>()
                .Any(p => string.Equals(p.Root, clientUserDataVideoPath, StringComparison.OrdinalIgnoreCase)))
            videoFileProviders.Add(new PhysicalFileProvider(clientUserDataVideoPath));

        string localUserDataVideoPath = Path.Combine(builder.Environment.ContentRootPath, "userdata", "game", "videos");
        if (Directory.Exists(localUserDataVideoPath) && !videoFileProviders.OfType<PhysicalFileProvider>()
                .Any(p => string.Equals(p.Root, localUserDataVideoPath, StringComparison.OrdinalIgnoreCase)))
            videoFileProviders.Add(new PhysicalFileProvider(localUserDataVideoPath));

        string oneDriveVideoPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "OneDrive", "Documents", "Heroes of Newerth x64", "game", "videos"
        );
        if (Directory.Exists(oneDriveVideoPath) && !videoFileProviders.OfType<PhysicalFileProvider>()
                .Any(p => string.Equals(p.Root, oneDriveVideoPath, StringComparison.OrdinalIgnoreCase)))
            videoFileProviders.Add(new PhysicalFileProvider(oneDriveVideoPath));

        string defaultVideoPath = Path.Combine(AppContext.BaseDirectory, "artifacts", "videos");
        if (!Directory.Exists(defaultVideoPath)) Directory.CreateDirectory(defaultVideoPath);

        videoFileProviders.Add(new PhysicalFileProvider(defaultVideoPath));

        application.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new CompositeFileProvider(videoFileProviders),
            RequestPath = "/videos",
            ServeUnknownFileTypes = true
        });

        // Serve Local CDN Files When Configured (e.g. {ROOT}/KONGOR/CDN_Content at /cdn)
        LocalCDNFileHandler.Use(application, application.Services.GetRequiredService<IOptions<OperationalConfiguration>>().Value.CDN);

        // Enable Rate Limiting (Before Other Processing)
        application.UseRateLimiter();

        // Create A Scope To Configure HTTPS Redirection Strategy
        if (!application.Environment.IsDevelopment() && !builder.Configuration.GetValue<bool>("KONGOR_ALLOW_HTTP"))
        {
            // Enforce HTTPS With Strict Transport Security
            application.UseHsts();

            // Automatically Redirect HTTP Requests To HTTPS
            application.UseHttpsRedirection();
        }
        else if (!application.Environment.IsDevelopment() && builder.Configuration.GetValue<bool>("KONGOR_ALLOW_HTTP"))
        {
            Log.Information("[Standalone] HTTP mode enabled (KONGOR_ALLOW_HTTP=true); skipping HSTS and HTTPS redirection");
        }

        // Add Security Headers Middleware
        application.Use(async (context, next) =>
        {
            IHeaderDictionary headers = context.Response.Headers;

            // Prevent MIME Type Sniffing
            headers.XContentTypeOptions = "nosniff";

            // Control Referrer Information
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";

            // Apply Restrictive CSP Only To API Endpoints
            if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
                headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none';";

            await next();
        });

        // Register Custom Request Logging Middleware
        application.UseRequestLogging();

        // Map Aspire Default Health Check Endpoints
        application.MapDefaultEndpoints();

        // Map Global Fallback Error Endpoint For Production UseExceptionHandler("/error")
        application.Map("/error", () => Results.Problem(statusCode: StatusCodes.Status500InternalServerError))
            .ExcludeFromDescription();

        // Map MVC Controllers With Rate Limiting (AscensionController Owns GET/POST/HEAD "/")
        application.MapControllers().RequireRateLimiting(RateLimiterPolicies.PerIpTokenBucket);

        // Map SignalR Hubs
        application.MapHub<PresenceHub>("/hubs/presence");

        // Map gRPC Services (Disabled by default; opt-in via Kongor:EnableExperimentalGrpcStubs=true)
        if (builder.Configuration.GetValue<bool>("Kongor:EnableExperimentalGrpcStubs"))
        {
            Log.Warning(
                "[KONGOR] Experimental gRPC endpoint GrpcCdnService is ENABLED via Kongor:EnableExperimentalGrpcStubs=true.");
            application.MapGrpcService<GrpcCdnService>();
        }
        else
        {
            Log.Information(
                "[KONGOR] Experimental gRPC endpoint GrpcCdnService is disabled by default (set Kongor:EnableExperimentalGrpcStubs=true to enable).");
        }

        // Map Replay Render Completion Callback Endpoint (Authenticated via X-Internal-Service-Key)
        application.MapPost("/api/replays/render-callback",
                async (HttpContext httpContext, ReplayRenderCallbackDto callback, MerrickContext dbContext,
                    IConfiguration config, IHostEnvironment hostEnvironment, ILoggerFactory loggerFactory) =>
                {
                    ILogger logger = loggerFactory.CreateLogger("KONGOR.ReplayCallback");
                    string? extractedKey = httpContext.Request.Headers.TryGetValue(
                        SecurityConstants.InternalServiceKeyHeader,
                        out StringValues headerValues)
                        ? headerValues.ToString()
                        : null;

                    if (!IsAuthorizedRenderCallback(extractedKey, config["InternalServiceKey"],
                            hostEnvironment.IsDevelopment(), logger,
                            httpContext.Connection.RemoteIpAddress?.ToString()))
                    {
                        return Results.Unauthorized();
                    }

                    if (!IsAllowedReplayVideoUrl(callback.VideoUrl))
                    {
                        logger.LogWarning(
                            "[ReplayCallback] Rejected unsafe VideoUrl '{VideoUrl}' for Match #{MatchId} from IP '{RemoteIp}'.",
                            callback.VideoUrl, callback.MatchId, httpContext.Connection.RemoteIpAddress);
                        return Results.BadRequest(new { error = "Invalid video URL scheme or path." });
                    }

                    logger.LogInformation(
                        "[ReplayCallback] Received authenticated render callback for Match #{MatchId}, Status: '{Status}', Video: '{VideoUrl}'",
                        callback.MatchId, callback.Status, callback.VideoUrl);

                    if (callback.MatchId > 0 && callback.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase))
                    {
                        MatchStatistics? match = await dbContext.MatchStatistics.FirstOrDefaultAsync(
                            m => m.MatchID == callback.MatchId, httpContext.RequestAborted);
                        if (match != null)
                        {
                            match.VideoUrl = string.IsNullOrWhiteSpace(callback.VideoUrl)
                                ? $"/videos/M{callback.MatchId}.mp4"
                                : callback.VideoUrl;
                            await dbContext.SaveChangesAsync(httpContext.RequestAborted);
                            logger.LogInformation(
                                "[ReplayCallback] Match #{MatchId} video URL updated in database to: {VideoUrl}",
                                callback.MatchId, match.VideoUrl);
                        }
                    }

                    return Results.Ok(new { success = true, matchId = callback.MatchId, status = callback.Status });
                })
            .RequireRateLimiting(RateLimiterPolicies.Strict);

        // Run The Application
        await application.RunAsync();
    }

    internal static bool IsAuthorizedRenderCallback(
        string? extractedKey,
        string? configuredKey,
        bool isDevelopment,
        ILogger? logger = null,
        string? remoteIp = null)
    {
        string? expectedKey = configuredKey;
        if (string.IsNullOrWhiteSpace(expectedKey) ||
            string.Equals(expectedKey, "...", StringComparison.Ordinal))
        {
            if (isDevelopment)
            {
                expectedKey = SecurityConstants.DefaultInternalServiceKey;
                logger?.LogWarning(
                    "[ReplayCallback] 'InternalServiceKey' is unconfigured; allowing development default key in Development environment only.");
            }
            else
            {
                logger?.LogError(
                    "[ReplayCallback] CRITICAL SECURITY REJECTION: 'InternalServiceKey' is missing in non-Development configuration. Rejecting callback from IP '{RemoteIp}'.",
                    remoteIp ?? "<unknown>");
                return false;
            }
        }
        else if (!isDevelopment &&
                 string.Equals(expectedKey, SecurityConstants.DefaultInternalServiceKey, StringComparison.Ordinal))
        {
            logger?.LogError(
                "[ReplayCallback] CRITICAL SECURITY REJECTION: Default development 'InternalServiceKey' is forbidden in non-Development environments. Rejecting callback from IP '{RemoteIp}'.",
                remoteIp ?? "<unknown>");
            return false;
        }

        if (string.IsNullOrEmpty(extractedKey))
        {
            logger?.LogWarning(
                "[ReplayCallback] Missing '{Header}' header on render-callback from IP '{RemoteIp}'.",
                SecurityConstants.InternalServiceKeyHeader, remoteIp ?? "<unknown>");
            return false;
        }

        byte[] expectedBytes = Encoding.UTF8.GetBytes(expectedKey);
        byte[] extractedBytes = Encoding.UTF8.GetBytes(extractedKey);

        if (!CryptographicOperations.FixedTimeEquals(expectedBytes, extractedBytes))
        {
            logger?.LogWarning(
                "[ReplayCallback] Invalid '{Header}' supplied on render-callback from IP '{RemoteIp}'.",
                SecurityConstants.InternalServiceKeyHeader, remoteIp ?? "<unknown>");
            return false;
        }

        return true;
    }

    internal static bool IsAllowedReplayVideoUrl(string? videoUrl)
    {
        if (string.IsNullOrWhiteSpace(videoUrl)) return true;

        string trimmed = videoUrl.Trim();
        if (trimmed.Contains('\r', StringComparison.Ordinal) ||
            trimmed.Contains('\n', StringComparison.Ordinal))
            return false;

        if (trimmed.StartsWith("/videos/", StringComparison.OrdinalIgnoreCase))
        {
            return !trimmed.Contains("..", StringComparison.Ordinal) &&
                   !trimmed.Contains('\\', StringComparison.Ordinal) &&
                   !trimmed.Contains("://", StringComparison.Ordinal);
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? parsedUri))
        {
            return string.Equals(parsedUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(parsedUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }
}
