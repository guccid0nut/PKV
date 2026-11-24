// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using MERRICK.DatabaseContext.Extensions;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.OpenTelemetry;
using TRANSMUTANSTEIN.ChatServer.Services.Grpc;
using Log = Serilog.Log;

namespace TRANSMUTANSTEIN.ChatServer;

public class TRANSMUTANSTEIN
{
    public static void Main(string[] args)
    {
        // Create The Application Builder
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
#if !PLAYTEST_BUILD
        builder.Configuration.AddUserSecrets<TRANSMUTANSTEIN>(true);
#endif
        builder.Configuration.AddEnvironmentVariables();
        builder.Configuration.AddCommandLine(args);
        string appUrl = builder.Configuration["APPLICATION_URL"] ?? "https://0.0.0.0:5554";
        builder.WebHost.UseUrls(appUrl);


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
            .WriteTo.File("logs/chat_server.log",
                rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                retainedFileCountLimit: 20,
                rollOnFileSizeLimit: true);

        string? chatOtlpEndpoint = builder.Configuration["GRAFANA_OTLP_ENDPOINT"] ??
                                   builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] ??
                                   Environment.GetEnvironmentVariable("GRAFANA_OTLP_ENDPOINT") ??
                                   Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        if (!string.IsNullOrWhiteSpace(chatOtlpEndpoint) &&
            !builder.Configuration.GetValue<bool>("IS_TEST_ENVIRONMENT"))
        {
            string? chatHeaders = builder.Configuration["GRAFANA_OTLP_HEADERS"] ??
                                  builder.Configuration["OTEL_EXPORTER_OTLP_HEADERS"] ??
                                  Environment.GetEnvironmentVariable("GRAFANA_OTLP_HEADERS") ??
                                  Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_HEADERS");
            string chatNamespace = builder.Configuration["OTEL_SERVICE_NAMESPACE"] ??
                                   Environment.GetEnvironmentVariable("OTEL_SERVICE_NAMESPACE") ??
                                   "NEXUS";
            string baseEndpoint = ServiceExtensions.GetBaseOtlpEndpoint(chatOtlpEndpoint);
            Dictionary<string, string> headersDict = [];
            if (!string.IsNullOrWhiteSpace(chatHeaders))
            {
                string cleanHeaders = chatHeaders.Contains("%20", StringComparison.OrdinalIgnoreCase)
                    ? Uri.UnescapeDataString(chatHeaders)
                    : chatHeaders;
                foreach (string pair in cleanHeaders.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] kv = pair.Split('=', 2);
                    if (kv.Length == 2) headersDict[kv[0].Trim()] = kv[1].Trim();
                }
            }

            string chatServiceName = builder.Configuration["OTEL_SERVICE_NAME"] ??
                                     Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME") ??
                                     builder.Environment.ApplicationName;

            loggerConfig.WriteTo.OpenTelemetry(options =>
            {
                options.Endpoint = baseEndpoint.TrimEnd('/') + "/v1/logs";
                options.Protocol = OtlpProtocol.HttpProtobuf;
                options.Headers = headersDict;
                options.ResourceAttributes = new Dictionary<string, object>
                {
                    ["service.name"] = chatServiceName,
                    ["service.namespace"] = chatNamespace,
                    ["service.instance.id"] = $"{chatServiceName}-{Environment.MachineName}",
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

        // Add Aspire Service Defaults
        builder.AddServiceDefaults();

        // Add The Database Context with Multi-Provider Support (SQL Server & PostgreSQL / Supabase)
        builder.AddMerrickDatabase();

        // Add Distributed Cache; The Connection String Maps To The "distributed-cache" Resource Defined In ASPIRE.ApplicationHost
        builder.AddRedisClient("DISTRIBUTED-CACHE");

        // Register Chat Server Services
        builder.Services.AddChatServerServices();

        // Register gRPC Services
        builder.Services.AddGrpc();

        // Add ProblemDetails Support For Production /error Handler
        builder.Services.AddProblemDetails();

        // Configure Forwarded Headers For Reverse Proxy Support
        builder.Services.ConfigureInfrastructureForwardedHeaders(builder.Configuration);

        // Build The Application
        WebApplication application = builder.Build();

        // Enable Forwarded Headers Middleware For Reverse Proxy Support
        application.UseForwardedHeaders();

        // Configure Development-Specific Middleware
        if (application.Environment.IsDevelopment())
            // Show Detailed Error Pages In Development
            application.UseDeveloperExceptionPage();

        else
            // Use Global Exception Handler In Production
            application.UseExceptionHandler("/error");

        if (!builder.Configuration.GetValue<bool>("KONGOR_ALLOW_HTTP"))
        {
            // Enforce HTTPS With Strict Transport Security
            application.UseHsts();

            // Automatically Redirect HTTP Requests To HTTPS
            application.UseHttpsRedirection();
        }
        else
        {
            Log.Information(
                "[Standalone] HTTP mode enabled (KONGOR_ALLOW_HTTP=true); skipping HSTS and HTTPS redirection");
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

        // Map Aspire Default Health Check Endpoints
        application.MapDefaultEndpoints();

        // Map Global Fallback Error Endpoint For Production UseExceptionHandler("/error")
        application.Map("/error", () => Results.Problem(statusCode: StatusCodes.Status500InternalServerError))
            .ExcludeFromDescription();

        // Map gRPC Services (Disabled by default; opt-in via Kongor:EnableExperimentalGrpcStubs=true)
        if (builder.Configuration.GetValue<bool>("Kongor:EnableExperimentalGrpcStubs"))
        {
            Log.Warning(
                "[TRANSMUTANSTEIN] Experimental gRPC endpoints (GrpcMatchmakingService, GrpcTelemetryStreamService) are ENABLED via Kongor:EnableExperimentalGrpcStubs=true.");
            application.MapGrpcService<GrpcMatchmakingService>();
            application.MapGrpcService<GrpcTelemetryStreamService>();
        }
        else
        {
            Log.Information(
                "[TRANSMUTANSTEIN] Experimental gRPC endpoints (GrpcMatchmakingService, GrpcTelemetryStreamService) are disabled by default (set Kongor:EnableExperimentalGrpcStubs=true to enable).");
        }

        // Run The Application
        application.Run();
    }
}
