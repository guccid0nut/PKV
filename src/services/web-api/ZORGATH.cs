// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using System.Text;
using ASPIRE.Common.Extensions;
using ASPIRE.Common.ServiceDefaults;
using FluentValidation;
using System.Threading.RateLimiting;
using Kongor.Core.Services.Session;
using StackExchange.Redis;
using ZORGATH.WebPortal.API.Infrastructure;
using ZORGATH.WebPortal.API.Services.Grpc;
using ZORGATH.WebPortal.API.Services.Learnatorium;
using Scalar.AspNetCore;

namespace ZORGATH.WebPortal.API;
// Added for ISendGridClient

public static class ZORGATH
{
    private static readonly char[] OriginListSeparators = [',', ';'];

    public static void Main(string[] args)
    {
        // Create The Application Builder
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

        // Suppress routine EF Core SQL command Info/Debug noise while preserving SlowQueryInterceptor warnings
        builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);

        bool isStandalone = builder.Configuration.GetValue<bool>("KONGOR_STANDALONE");

        // Enforce Internal Service Key in Non-Development Environments or Standalone Mode
        string? internalServiceKey = builder.Configuration["InternalServiceKey"];
        ValidateInternalServiceKey(internalServiceKey, builder.Environment.IsDevelopment(), isStandalone);

        if (isStandalone || !builder.Environment.IsDevelopment())
        {
            string resolvedKey = ResolveJwtSigningKey(builder.Configuration);
            ValidateJwtSigningKey(resolvedKey, isStandalone, builder.Environment.IsDevelopment());
            Console.WriteLine(isStandalone
                ? "[Standalone] ZORGATH fail-closed secret checks passed (InternalServiceKey + jwt-signing-key)"
                : "[ZORGATH] Non-Development fail-closed secret checks passed (InternalServiceKey + jwt-signing-key)");
        }

        // Map User-Defined Configuration Section
        builder.Services.Configure<OperationalConfiguration>(
            builder.Configuration.GetRequiredSection(OperationalConfiguration.ConfigurationSection));

        // Add Aspire Service Defaults
        builder.AddServiceDefaults();
        builder.AddMediatRPipeline(typeof(ZORGATH).Assembly);
        builder.Services.AddValidatorsFromAssembly(typeof(Features.PlayerStats.GetPlayerProfileValidator).Assembly);

        // Add Redis Client if configured, or fallback to distributed memory cache
        string? redisConn = builder.Configuration.GetConnectionString("DISTRIBUTED-CACHE") ??
                            builder.Configuration["Aspire:StackExchange:Redis:ConnectionString"];
        if (!string.IsNullOrWhiteSpace(redisConn))
        {
            builder.AddRedisClient("DISTRIBUTED-CACHE");

            // Register IDatabase From IConnectionMultiplexer
            builder.Services.AddSingleton(serviceProvider =>
            {
                IConnectionMultiplexer? connectionMultiplexer = serviceProvider.GetService<IConnectionMultiplexer>();
                return connectionMultiplexer?.GetDatabase()!;
            });
        }
        else
        {
            builder.Services.AddDistributedMemoryCache();
        }

        // Add The Database Context with Multi-Provider Support (SQL Server & PostgreSQL / Supabase)
        builder.AddMerrickDatabase();

        // Add Memory Cache & Hybrid Cache Services
        builder.Services.AddNexusHybridCache();
        builder.Services.AddRedisMatchSessionCache();
        builder.Services.AddScoped<ILearnatoriumService, LearnatoriumService>();
        builder.Services.AddScoped<IAccountPrivacyService, AccountPrivacyService>();
        builder.Services.AddScoped<Services.Stats.IMatchStatsReadProjectionService, Services.Stats.DapperMatchStatsReadProjectionService>();

        // Add Rate Limiting Service To Protect Against Abuse And DoS Attacks
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = (context, _) =>
            {
                ILogger? rlLogger = context.HttpContext.RequestServices
                    .GetService<ILoggerFactory>()?
                    .CreateLogger("Security.RateLimiter");
                rlLogger?.LogWarning(
                    "[SECURITY-ALERT] [WebPortalAPI] Rate limit rejected HTTP {Method} {Path} from IP '{RemoteIp}' (429 Too Many Requests).",
                    context.HttpContext.Request.Method,
                    context.HttpContext.Request.Path,
                    context.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown");
                return ValueTask.CompletedTask;
            };

            // Relaxed Limits For General API Endpoints (IP-Partitioned with Internal Service Bypass)
            options.AddPolicy(RateLimiterPolicies.Relaxed, RateLimiterPolicies.CreateRelaxedPartition);

            // Strict Limits For Authentication And Other Sensitive Endpoints (IP-Partitioned, Forwarded-IP Aware for Authenticated Portal)
            options.AddPolicy(RateLimiterPolicies.Strict, RateLimiterPolicies.CreateStrictPartition);
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

        // Set CORS Policy Name
        const string corsPolicyName = "Allow-All";

        // Set CORS Origins
        List<string> corsOriginsList;
        string? corsAllowedOriginsEnv = builder.Configuration["CORS_ALLOWED_ORIGINS"];
        if (!string.IsNullOrWhiteSpace(corsAllowedOriginsEnv))
        {
            corsOriginsList = corsAllowedOriginsEnv
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            Console.WriteLine($"[Standalone] Using CORS_ALLOWED_ORIGINS ({corsOriginsList.Count} origins)");
        }
        else
        {
            corsOriginsList = builder.Environment.IsDevelopment()
                ?
                [
                    "https://localhost:5553", "https://localhost:5554", "http://localhost:5555", "http://localhost:55555",
                    "https://localhost:5556", "https://localhost:5557", "http://localhost:5266", "http://127.0.0.1:5266",
                    "http://localhost:5200", "https://localhost:5201",
                    "http://localhost:5080", "http://127.0.0.1:5080",
                    "https://newbs.io", "https://www.newbs.io", "https://masterserver.newbs.io",
                    "https://test.newbs.io", "https://staging.newbs.io", "https://dev.newbs.io"
                ]
                :
                [
                    "https://database.kongor.net", "https://chat.kongor.net", "http://api.kongor.net",
                    "https://portal.api.kongor.net", "https://portal.ui.kongor.net"
                ];

            string configuredMasterServer = builder.Configuration.GetMasterServerAddress();
            if (!string.IsNullOrWhiteSpace(configuredMasterServer) && !corsOriginsList.Contains(configuredMasterServer))
                corsOriginsList.Add(configuredMasterServer.TrimEnd('/'));
        }

        string? configuredTestingDomain = builder.Configuration["WebPortal:TestingDomain"] ??
                                          builder.Configuration["TESTING_DOMAIN"];
        if (!string.IsNullOrWhiteSpace(configuredTestingDomain))
        {
            foreach (string rawDomain in configuredTestingDomain.Split(OriginListSeparators,
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string cleanDomain = rawDomain.TrimEnd('/');
                if (cleanDomain.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    cleanDomain.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    if (!corsOriginsList.Contains(cleanDomain, StringComparer.OrdinalIgnoreCase))
                        corsOriginsList.Add(cleanDomain);
                }
                else
                {
                    string httpsOrigin = $"https://{cleanDomain}";
                    string httpOrigin = $"http://{cleanDomain}";
                    if (!corsOriginsList.Contains(httpsOrigin, StringComparer.OrdinalIgnoreCase))
                        corsOriginsList.Add(httpsOrigin);
                    if (!corsOriginsList.Contains(httpOrigin, StringComparer.OrdinalIgnoreCase))
                        corsOriginsList.Add(httpOrigin);
                }
            }
        }

        string[] corsOrigins = [.. corsOriginsList];

        // Add CORS Policy To Allow Cross-Origin Requests
        builder.Services.AddCors(options =>
        {
            options.AddPolicy(corsPolicyName,
                configuration => configuration.WithOrigins(corsOrigins)
                    .AllowAnyHeader().AllowAnyMethod().AllowCredentials());
        });

        // Register Custom Output Cache Policies
        builder.Services.AddSingleton<AllowAuthenticatedOutputCachePolicy>();

        // Add Server-Side Output Caching
        builder.Services.AddOutputCache(options =>
        {
            options.AddPolicy(OutputCachePolicies.CacheForThirtySeconds,
                policy => policy.Cache().Expire(TimeSpan.FromSeconds(30)));
            options.AddPolicy(OutputCachePolicies.CacheForFiveMinutes,
                policy => policy.AddPolicy<AllowAuthenticatedOutputCachePolicy>()
                    .Expire(TimeSpan.FromMinutes(5))
                    .SetVaryByHeader("Authorization"));
            options.AddPolicy(OutputCachePolicies.CacheForOneDay,
                policy => policy.Cache().Expire(TimeSpan.FromDays(1)));
            options.AddPolicy(OutputCachePolicies.CacheForOneWeek,
                policy => policy.Cache().Expire(TimeSpan.FromDays(7)));
        });

        // Implement Username And Password Validation Policies
        if (builder.Environment.IsDevelopment())
            builder.Services.Configure<IdentityOptions>(options =>
            {
                options.User.RequireUniqueEmail = false;
                options.User.AllowedUserNameCharacters =
                    "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_`";

                options.Password.RequiredLength = 0;
                options.Password.RequiredUniqueChars = 0;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireDigit = false;

                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.Zero;
            });

        else
            builder.Services.Configure<IdentityOptions>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.User.AllowedUserNameCharacters =
                    "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_`";

                options.Password.RequiredLength = 8;
                options.Password.RequiredUniqueChars = 4;
                options.Password.RequireNonAlphanumeric = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireUppercase = true;
                options.Password.RequireDigit = true;

                options.Lockout.MaxFailedAccessAttempts = 3;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
            });

        // Add JWT Bearer Authentication Configuration
        builder.Services
            .AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultScheme = JwtBearerDefaults.AuthenticationScheme;
            })
            .AddJwtBearer(options =>
            {
                // Get Operational Configuration For JWT Settings
                OperationalConfiguration configuration = builder.Configuration
                                                             .GetRequiredSection(OperationalConfiguration
                                                                 .ConfigurationSection)
                                                             .Get<OperationalConfiguration>() ??
                                                         throw new InvalidOperationException(
                                                             "Operational Configuration Is NULL");

                string jwtKey = configuration.JWT?.SigningKey ?? string.Empty;
                if (string.IsNullOrWhiteSpace(jwtKey) || jwtKey == "MANAGED_BY_ASPIRE" || jwtKey == "..." || Encoding.UTF8.GetByteCount(jwtKey) < 32)
                {
                    jwtKey = builder.Configuration["jwt-signing-key"]
                             ?? builder.Configuration["Parameters:jwt-signing-key"]
                             ?? (!isStandalone && builder.Environment.IsDevelopment()
                                 ? "NEXUS-Default-Development-JWT-SigningKey-Phrase-32BytesLong!"
                                 : string.Empty);
                }

                ValidateJwtSigningKey(jwtKey, isStandalone, builder.Environment.IsDevelopment());

                // Configure JWT Validation Parameters
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = configuration.JWT?.Issuer ?? "https://localhost:5556",
                    ValidateIssuer = true,
                    ValidAudience = configuration.JWT?.Audience ?? "https://localhost:5557",
                    ValidateAudience = true,
                    ClockSkew = TimeSpan.Zero,
                    ValidateLifetime = true
                };
            });

        // Add Authorization Policies For Role-Based Access Control
        builder.Services.AddAuthorizationBuilder()
            .AddPolicy(UserRoles.Administrator, policy => policy.RequireClaim(Claims.UserRole, UserRoles.Administrator))
            .AddPolicy(UserRoles.User, policy => policy.RequireClaim(Claims.UserRole, UserRoles.User))
            .AddPolicy(UserRoles.AllRoles,
                policy => policy.RequireClaim(Claims.UserRole, UserRoles.AllRoles.Split(',')));

        // Enable MVC Controllers
        builder.Services.AddControllers();

        // Add Comprehensive Error Response Detail In Development Environment
        if (builder.Environment.IsDevelopment()) builder.Services.AddProblemDetails();

        // Add OpenAPI Documentation Generation
        builder.Services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer((document, context, ct) =>
            {
                document.Info.Title = "ZORGATH Web Portal API";
                document.Info.Description = "REST API for the Project Kongor web portal. " +
                    "Handles player accounts, authentication, store, statistics, guides, and administration.";
                document.Info.Version = $"v{NEXUS.Contracts.Domain.NexusVersionInfo.PlatformVersion}";
                return Task.CompletedTask;
            });
        });

        // Add Web Portal API Services
        builder.Services.AddWebPortalServices(builder.Configuration);

        // Add gRPC Services
        builder.Services.AddGrpc();

        // Configure Forwarded Headers For Reverse Proxy Support
        builder.Services.ConfigureInfrastructureForwardedHeaders(builder.Configuration);

        // Build The Application
        WebApplication application = builder.Build();

        // Enable Forwarded Headers Middleware For Reverse Proxy Support (Cloudflare Tunnel + Docker Bridge)
        application.UseInfrastructureForwardedHeaders(application.Configuration);

        // Configure Development-Specific Middleware
        if (application.Environment.IsDevelopment())
        {
            // Show Detailed Error Pages In Development
            application.UseDeveloperExceptionPage();

            // Enable HTTP Request/Response Logging
            application.UseHttpLogging();
        }

        else
        {
            // Use Global Exception Handler In Production
            application.UseExceptionHandler("/error");
        }

        // Enable Native OpenAPI & Interactive Scalar Documentation Strictly in Development (or Explicit Opt-In)
        if (application.Environment.IsDevelopment() ||
            builder.Configuration.GetValue("Operational:EnableOpenApiInProduction", false))
        {
            application.MapOpenApi();
            application.MapScalarApiReference(options =>
            {
                options.WithTitle("ZORGATH Web Portal API")
                    .WithTheme(ScalarTheme.Moon);
            });

            // Redirect legacy /swagger endpoint to modern Scalar interface
            application.MapGet("/swagger", () => Results.Redirect("/scalar/v1")).ExcludeFromDescription();
        }

        // Map Production Global Error Handler Endpoint (RFC 7807)
        application.Map("/error", (HttpContext context) =>
        {
            var exceptionHandlerFeature = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
            string traceId = System.Diagnostics.Activity.Current?.Id ?? context.TraceIdentifier;

            return Results.Problem(
                title: "An unexpected error occurred while processing the request.",
                statusCode: StatusCodes.Status500InternalServerError,
                instance: context.Request.Path,
                extensions: new Dictionary<string, object?>
                {
                    ["traceId"] = traceId
                });
        }).ExcludeFromDescription();

        // Serve Static Files For Swagger CSS
        string swaggerCssPath = Path.Combine(builder.Environment.ContentRootPath, "Resources", "CSS");
        if (Directory.Exists(swaggerCssPath))
            application.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(swaggerCssPath),
                RequestPath = "/swagger"
            });

        // Enable Rate Limiting (Before Other Processing)
        application.UseRateLimiter();

        if (!application.Environment.IsDevelopment())
        {
            if (!builder.Configuration.GetValue<bool>("KONGOR_ALLOW_HTTP"))
            {
                // Enforce HTTPS With Strict Transport Security
                application.UseHsts();

                // Automatically Redirect HTTP Requests To HTTPS
                application.UseHttpsRedirection();
            }
            else
            {
                Console.WriteLine("[Standalone] ZORGATH HTTP mode enabled (KONGOR_ALLOW_HTTP=true); skipping HSTS and HTTPS redirection");
            }
        }

        // Enable CORS Policy
        application.UseCors(corsPolicyName);

        // Enable Output Caching
        application.UseOutputCache();

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

        // Require Authentication To Access Non-Public Resources
        application.UseAuthentication();

        // Require Authorization To Access Role-Specific Resources
        application.UseAuthorization();

        // Map Aspire Default Health Check Endpoints
        application.MapDefaultEndpoints();

        // Map gRPC Services (Disabled by default; opt-in via Kongor:EnableExperimentalGrpcStubs=true)
        if (builder.Configuration.GetValue<bool>("Kongor:EnableExperimentalGrpcStubs"))
        {
            application.Logger.LogWarning(
                "[ZORGATH] Experimental gRPC endpoint GrpcAuthService is ENABLED via Kongor:EnableExperimentalGrpcStubs=true.");
            application.MapGrpcService<GrpcAuthService>();
        }
        else
        {
            application.Logger.LogInformation(
                "[ZORGATH] Experimental gRPC endpoint GrpcAuthService is disabled by default (set Kongor:EnableExperimentalGrpcStubs=true to enable).");
        }

        // Map MVC Controllers With Rate Limiting
        application.MapControllers().RequireRateLimiting(RateLimiterPolicies.Relaxed);

        // Run The Application
        application.Run();
    }

    public static void ValidateInternalServiceKey(string? internalServiceKey, bool isDevelopment, bool isStandalone)
    {
        if (!isDevelopment || isStandalone)
        {
            if (string.IsNullOrWhiteSpace(internalServiceKey) ||
                string.Equals(internalServiceKey, "...", StringComparison.Ordinal) ||
                string.Equals(internalServiceKey, SecurityConstants.DefaultInternalServiceKey,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    isStandalone
                        ? "[Standalone] 'InternalServiceKey' must be explicitly configured with a non-default secret in standalone mode. Configure userdata/secrets/nexus_secrets.json or pass via environment."
                        : "FATAL SECURITY MISCONFIGURATION: 'InternalServiceKey' must be explicitly configured with a non-default secret in non-development environments.");
            }
        }
    }

    public static string ResolveJwtSigningKey(IConfiguration configuration)
    {
        string? key = configuration["OperationalConfiguration:JWT:SigningKey"]
                      ?? configuration["Operational:JWT:SigningKey"];
        if (string.IsNullOrWhiteSpace(key) || key == "MANAGED_BY_ASPIRE" || key == "...")
        {
            key = configuration["jwt-signing-key"]
                  ?? configuration["Parameters:jwt-signing-key"];
        }

        return key ?? string.Empty;
    }

    public static void ValidateJwtSigningKey(string? jwtKey, bool isStandalone, bool isDevelopment = true)
    {
        if (isStandalone || !isDevelopment)
        {
            if (string.IsNullOrWhiteSpace(jwtKey) ||
                string.Equals(jwtKey, "MANAGED_BY_ASPIRE", StringComparison.Ordinal) ||
                string.Equals(jwtKey, "...", StringComparison.Ordinal) ||
                string.Equals(jwtKey, "NEXUS-Default-Development-JWT-SigningKey-Phrase-32BytesLong!", StringComparison.Ordinal) ||
                Encoding.UTF8.GetByteCount(jwtKey) < 32)
            {
                throw new InvalidOperationException(
                    isStandalone
                        ? "[Standalone] JWT signing key ('jwt-signing-key' or 'OperationalConfiguration:JWT:SigningKey') must be configured with a non-default secret of at least 32 bytes in standalone mode. Configure userdata/secrets/nexus_secrets.json or pass via environment."
                        : "FATAL SECURITY MISCONFIGURATION: JWT signing key ('jwt-signing-key' or 'OperationalConfiguration:JWT:SigningKey') must be configured with a non-default secret of at least 32 bytes in non-development environments.");
            }
        }
    }
}
