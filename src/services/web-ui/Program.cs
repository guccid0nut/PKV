// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.RateLimiting;
using ASPIRE.Common.Extensions;
using ASPIRE.Common.ServiceDefaults;
using DAWNBRINGER.WebPortal.UI.Hubs;
using DAWNBRINGER.WebPortal.UI.Infrastructure;
using DAWNBRINGER.WebPortal.UI.Infrastructure.Handlers;
using DAWNBRINGER.WebPortal.UI.Services;
using DAWNBRINGER.WebPortal.UI.Services.DeveloperIssues;
using DAWNBRINGER.WebPortal.UI.Services.Replay;
using Kongor.Core.Services.Vfs;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.StackExchangeRedis;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.FileProviders;
using NEXUS.Contracts.Constants;
using StackExchange.Redis;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Add Aspire Service Defaults
builder.AddServiceDefaults();

// Add Web API Health Check
builder.Services.AddHealthChecks()
    .AddCheck<WebApiHealthCheck>("web-api", tags: [HealthCheckExtensions.ReadyTag, "api"]);

// Add Redis Client safely (registers only when connection string is configured)
string? redisConn = builder.Configuration.GetConnectionString("DISTRIBUTED-CACHE");
if (!string.IsNullOrEmpty(redisConn))
    builder.AddRedisClient("DISTRIBUTED-CACHE");

// Register Admin Metrics Service as Scoped & Public Population Service as Singleton (direct Valkey + 5s HybridCache)
builder.Services.AddScoped<IAdminMetricsService, WebAdminMetricsService>();
builder.Services.AddSingleton<IPublicPopulationTelemetryService, PublicPopulationTelemetryService>();
builder.Services.AddSingleton<IDeveloperIssueAdminService, DeveloperIssueAdminService>();

// Add SignalR & Hosted Pub/Sub Subscriber
var signalRBuilder = builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
    options.KeepAliveInterval = TimeSpan.FromSeconds(10);
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
    options.MaximumReceiveMessageSize = 64 * 1024; // 64 KB
});

if (!string.IsNullOrEmpty(redisConn))
{
    signalRBuilder.AddStackExchangeRedis(redisConn, options =>
    {
        options.Configuration.ChannelPrefix = RedisChannel.Literal("kongor:signalr:");
    });
}
builder.Services.AddHostedService<WebPortalTelemetrySubscriberService>();
builder.Services.AddSingleton<SupabaseRealtimeClient>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SupabaseRealtimeClient>());

// Add Live Match Telemetry Service & Real-Time Simulation
builder.Services.AddSingleton(sp =>
{
    IConfiguration config = sp.GetRequiredService<IConfiguration>();
    IHostEnvironment env = sp.GetRequiredService<IHostEnvironment>();
    IConnectionMultiplexer? muxer = sp.GetService<IConnectionMultiplexer>();
    ILogger<LiveMatchService> logger = sp.GetRequiredService<ILogger<LiveMatchService>>();
    return new LiveMatchService(config, env, muxer, sp, logger);
});
builder.Services.AddMemoryCache();
builder.Services.AddNexusHybridCache();
builder.Services.AddSingleton<IHonReplayParser, HonReplayParser>();
builder.Services.AddSingleton<ILiveMatchService>(sp => sp.GetRequiredService<LiveMatchService>());
builder.Services.AddScoped<IMatchReplayService, MatchReplayService>();
builder.Services.AddScoped<IReplayEvidenceService, ReplayEvidenceService>();
builder.Services.AddScoped<IMatchTimelineService, MatchTimelineService>();

// Add 3D Game Archive VFS Service & Staff Store Service
builder.Services.AddSingleton<IGameArchiveVfsService, GameArchiveVfsService>();
builder.Services.AddSingleton<IStaffStoreService, StaffStoreService>();
builder.Services.AddSingleton<IAnnouncementService, AnnouncementService>();
builder.Services.AddSingleton<ITwitchStreamService, TwitchStreamService>();
builder.Services.AddSingleton<IDevoWarsRoomManager, DevoWarsRoomManager>();
builder.Services.AddSingleton<IMonkeyCheckersRoomManager, MonkeyCheckersRoomManager>();
builder.Services.AddSingleton<IChessRoomManager, ChessRoomManager>();
builder.Services.AddSingleton<IRiftbreakerRoomManager, RiftbreakerRoomManager>();

// Add MVC Controllers with Views
builder.Services.AddControllersWithViews();

bool isStandalone = builder.Configuration.GetValue<bool>("KONGOR_STANDALONE");

static bool IsFullyOnboardedPortalUser(ClaimsPrincipal user, bool standaloneMode)
{
    if (user.Identity?.IsAuthenticated != true)
    {
        return false;
    }

    if (user.HasClaim("pending_registration", "true"))
    {
        return false;
    }

    bool hasAccountId = !string.IsNullOrWhiteSpace(user.FindFirst("AccountId")?.Value);
    if (standaloneMode)
    {
        return hasAccountId || !string.IsNullOrWhiteSpace(user.Identity.Name);
    }

    bool hasDiscord = !string.IsNullOrWhiteSpace(user.FindFirst("discord_id")?.Value) ||
                      !string.IsNullOrWhiteSpace(user.FindFirst("urn:discord:id")?.Value);
    return hasDiscord && hasAccountId;
}

static bool IsApiOrAjaxRequest(HttpRequest request)
{
    string path = request.Path.Value ?? string.Empty;
    if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/api", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/hubs/", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/tools/replays/", StringComparison.OrdinalIgnoreCase))
    {
        return true;
    }

    if (string.Equals(request.Headers["X-Requested-With"], "XMLHttpRequest", StringComparison.OrdinalIgnoreCase))
    {
        return true;
    }

    string accept = request.Headers.Accept.ToString();
    return accept.Contains("application/json", StringComparison.OrdinalIgnoreCase) &&
           !accept.Contains("text/html", StringComparison.OrdinalIgnoreCase);
}

// Configure Global Discord-Only Portal Gate & Staff Authorization Policies
Microsoft.AspNetCore.Authorization.AuthorizationPolicy discordPortalPolicy =
    new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder(CookieAuthenticationDefaults.AuthenticationScheme)
        .RequireAssertion(ctx => IsFullyOnboardedPortalUser(ctx.User, isStandalone))
        .Build();

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("DiscordAuthenticatedAndOnboarded", discordPortalPolicy);
    options.DefaultPolicy = discordPortalPolicy;
    options.FallbackPolicy = discordPortalPolicy;

    options.AddPolicy(StaffAuthorizationHelper.PolicyName, policy =>
        policy.AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme)
              .RequireAssertion(ctx =>
                  IsFullyOnboardedPortalUser(ctx.User, isStandalone) &&
                  StaffAuthorizationHelper.IsStaff(ctx.User)));
});

// Configure Antiforgery to support AJAX headers
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "RequestVerificationToken";
});

// Add Output Caching, Endpoint-Scoped Rate Limiting & Response Compression
builder.Services.AddOutputCache();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = (context, _) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter =
                ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        ILogger? rlLogger = context.HttpContext.RequestServices
            .GetService<ILoggerFactory>()
            ?.CreateLogger("DAWNBRINGER.RateLimiting");
        rlLogger?.LogWarning(
            "[RateLimiting] Rejected request to {Path} from {RemoteIp} with HTTP 429.",
            context.HttpContext.Request.Path,
            context.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown");
        return ValueTask.CompletedTask;
    };

    options.AddPolicy(PublicPopulationTelemetryService.RateLimitPolicyName, httpContext =>
    {
        string partitionKey = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 60,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            });
    });
});
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
});

// Register Infrastructure-Aware ForwardedHeaders (Cloudflare Tunnel + Docker Bridge + Testing Domains)
builder.Services.ConfigureInfrastructureForwardedHeaders(builder.Configuration);
builder.Services.AddHttpContextAccessor();
builder.Services.AddTransient<InternalServiceAuthHandler>();

// Add Typed HttpClients for ZORGATH API using InternalServiceAuthHandler
string apiBaseUrl = builder.Configuration["services:web-portal-api:http:0"]
                    ?? builder.Configuration["services:web-portal-api:https:0"]
                    ?? builder.Configuration["services:web-portal-api:http"]
                    ?? builder.Configuration["services:web-portal-api:https"]
                    ?? builder.Configuration["WebPortalApi:BaseUrl"]
                    ?? (builder.Environment.IsDevelopment() ? "http://localhost:5200" : "http://web-portal-api");

Uri apiBaseUri = new(apiBaseUrl);

builder.Services.AddHttpClient<IUserApiClient, UserApiClient>(client => { client.BaseAddress = apiBaseUri; })
    .AddHttpMessageHandler<InternalServiceAuthHandler>();

builder.Services.AddHttpClient<IAdminApiClient, AdminApiClient>(client => { client.BaseAddress = apiBaseUri; })
    .AddHttpMessageHandler<InternalServiceAuthHandler>();

builder.Services.AddHttpClient<IStoreApiClient, StoreApiClient>(client => { client.BaseAddress = apiBaseUri; })
    .AddHttpMessageHandler<InternalServiceAuthHandler>();

builder.Services.AddHttpClient<IGuideApiClient, GuideApiClient>(client => { client.BaseAddress = apiBaseUri; })
    .AddHttpMessageHandler<InternalServiceAuthHandler>();

builder.Services
    .AddHttpClient<IPlayerStatsApiClient, PlayerStatsApiClient>(client => { client.BaseAddress = apiBaseUri; })
    .AddHttpMessageHandler<InternalServiceAuthHandler>();

builder.Services.AddHttpClient<ILoreApiClient, LoreApiClient>(client => { client.BaseAddress = apiBaseUri; })
    .AddHttpMessageHandler<InternalServiceAuthHandler>();

builder.Services.AddHttpClient<ILearnatoriumApiClient, LearnatoriumApiClient>(client => { client.BaseAddress = apiBaseUri; })
    .AddHttpMessageHandler<InternalServiceAuthHandler>();

builder.Services.AddHttpClient("ZORGATH", client => { client.BaseAddress = apiBaseUri; })
    .AddHttpMessageHandler<InternalServiceAuthHandler>();

// Configure Persistent Data Protection Keyring across Restarts & Cluster Replicas
string dataProtectionKeysPath = Environment.GetEnvironmentVariable("DATA_PROTECTION_KEYS_PATH") ??
                               Path.Combine(builder.Environment.ContentRootPath, ".aspnet", "DataProtection-Keys");
Directory.CreateDirectory(dataProtectionKeysPath);

var dataProtectionBuilder = builder.Services.AddDataProtection()
    .SetApplicationName("DAWNBRINGER")
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath));

if (!string.IsNullOrEmpty(redisConn))
{
    builder.Services.AddOptions<KeyManagementOptions>()
        .Configure<IServiceProvider>((options, sp) =>
        {
            IConnectionMultiplexer? mux = sp.GetService<IConnectionMultiplexer>();
            if (mux != null)
            {
                options.XmlRepository = new RedisXmlRepository(() => mux.GetDatabase(), "kongor:dataprotection:keys");
            }
        });
}

if (isStandalone)
{
    Console.WriteLine("[Standalone] DAWNBRINGER DefaultChallengeScheme=Cookies (LoginPath=/Account/Login)");
}

// Configure Authentication
builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    })
    .AddCookie(cookieOptions =>
    {
        cookieOptions.LoginPath = isStandalone ? "/Account/Login" : "/";
        cookieOptions.AccessDeniedPath = "/";
        cookieOptions.Events = new CookieAuthenticationEvents
        {
            OnRedirectToLogin = context =>
            {
                ILogger authLogger = context.HttpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("DAWNBRINGER.DiscordPortalGate");

                if (IsApiOrAjaxRequest(context.Request))
                {
                    authLogger.LogInformation(
                        "[DiscordPortalGate] Blocked unauthenticated API/AJAX request to '{Path}' -> HTTP 401.",
                        context.Request.Path);
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.Response.ContentType = "application/json; charset=utf-8";
                    return context.Response.WriteAsync("{\"error\":\"Discord authentication required.\"}");
                }

                if (isStandalone)
                {
                    context.Response.Redirect(context.RedirectUri);
                    return Task.CompletedTask;
                }

                string rawTarget = (context.Request.PathBase + context.Request.Path + context.Request.QueryString).ToString();
                string target = string.IsNullOrWhiteSpace(rawTarget) || rawTarget == "/"
                    ? "/"
                    : $"/?returnUrl={Uri.EscapeDataString(rawTarget)}";

                authLogger.LogInformation(
                    "[DiscordPortalGate] Redirecting unauthenticated browser request from '{Path}' to '{RedirectTarget}'.",
                    rawTarget,
                    target);
                context.Response.Redirect(target);
                return Task.CompletedTask;
            },
            OnRedirectToAccessDenied = context =>
            {
                ILogger authLogger = context.HttpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("DAWNBRINGER.DiscordPortalGate");

                ClaimsPrincipal principal = context.HttpContext.User;
                bool hasDiscord = !string.IsNullOrWhiteSpace(principal.FindFirst("discord_id")?.Value) ||
                                  !string.IsNullOrWhiteSpace(principal.FindFirst("urn:discord:id")?.Value);
                bool hasAccountId = !string.IsNullOrWhiteSpace(principal.FindFirst("AccountId")?.Value);

                if (IsApiOrAjaxRequest(context.Request))
                {
                    authLogger.LogWarning(
                        "[DiscordPortalGate] Access denied on API/AJAX '{Path}' (HasDiscord={HasDiscord}, HasAccountId={HasAccountId}) -> HTTP 403.",
                        context.Request.Path,
                        hasDiscord,
                        hasAccountId);
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    context.Response.ContentType = "application/json; charset=utf-8";
                    string msg = hasDiscord && !hasAccountId
                        ? "{\"error\":\"Complete account registration at /Account/Register first.\"}"
                        : "{\"error\":\"Access denied.\"}";
                    return context.Response.WriteAsync(msg);
                }

                if (!isStandalone && hasDiscord && !hasAccountId)
                {
                    authLogger.LogInformation(
                        "[DiscordPortalGate] Redirecting pending Discord user without HoN Account from '{Path}' to '/Account/Register'.",
                        context.Request.Path);
                    context.Response.Redirect("/Account/Register");
                    return Task.CompletedTask;
                }

                authLogger.LogInformation(
                    "[DiscordPortalGate] Redirecting unauthorized request from '{Path}' to '/'.",
                    context.Request.Path);
                context.Response.Redirect("/");
                return Task.CompletedTask;
            }
        };
    })
    .AddCookie("ExternalTwitch", options =>
    {
        options.Cookie.Name = ".AspNetCore.ExternalTwitch";
        options.ExpireTimeSpan = TimeSpan.FromMinutes(5);
    })
    .AddDiscord(options =>
    {
        string? configuredClientId = builder.Configuration["discord-client-id"] ??
                                     builder.Configuration["Authentication:Discord:ClientId"] ??
                                     builder.Configuration["Parameters:discord-client-id"] ??
                                     builder.Configuration["DISCORD_CLIENT_ID"];

        string? configuredClientSecret = builder.Configuration["discord-client-secret"] ??
                                         builder.Configuration["Authentication:Discord:ClientSecret"] ??
                                         builder.Configuration["Parameters:discord-client-secret"] ??
                                         builder.Configuration["DISCORD_CLIENT_SECRET"];

#if !PLAYTEST_BUILD

        if (string.IsNullOrWhiteSpace(configuredClientId) || string.IsNullOrWhiteSpace(configuredClientSecret))
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string[] candidateFiles =
            [
                Path.Combine(appData, "Microsoft", "UserSecrets", "f1e1f81d-ed00-40b6-8d39-0b82a10a6338",
                    "secrets.json"),
                Path.Combine(appData, "Microsoft", "UserSecrets", "00005072-6f6a-6563-7420-4b4f4e474f52",
                    "secrets.json")
            ];

            foreach (string filePath in candidateFiles)
                if (File.Exists(filePath))
                    try
                    {
                        string fileText = File.ReadAllText(filePath);
                        using JsonDocument doc = JsonDocument.Parse(fileText);
                        if (string.IsNullOrWhiteSpace(configuredClientId) &&
                            (doc.RootElement.TryGetProperty("discord-client-id", out JsonElement idElem) ||
                             doc.RootElement.TryGetProperty("Authentication:Discord:ClientId", out idElem)))
                            configuredClientId = idElem.GetString();

                        if (string.IsNullOrWhiteSpace(configuredClientSecret) &&
                            (doc.RootElement.TryGetProperty("discord-client-secret", out JsonElement secElem) ||
                             doc.RootElement.TryGetProperty("Authentication:Discord:ClientSecret", out secElem)))
                            configuredClientSecret = secElem.GetString();
                    }
                    catch
                    {
                        // Ignore parse exceptions on disk fallback
                    }
        }

#endif
        // Provide safe development fallback if unconfigured to prevent OAuthOptions.Validate() from crashing the portal
        options.ClientId = !string.IsNullOrWhiteSpace(configuredClientId)
            ? configuredClientId
            : "development-discord-client-id";

        options.ClientSecret = !string.IsNullOrWhiteSpace(configuredClientSecret)
            ? configuredClientSecret
            : "development-discord-client-secret";

        options.CallbackPath = new PathString("/signin-discord");

        // Map Discord User Information to Claims
        options.ClaimActions.MapJsonKey("urn:discord:avatar", "avatar");
        options.ClaimActions.MapJsonKey("urn:discord:discriminator", "discriminator");

        // Fast seamless SSO: Do not force prompt="consent" on every login
        options.SaveTokens = true;
        options.Events = new OAuthEvents
        {
            OnRemoteFailure = context =>
            {
                ILogger oauthLogger = context.HttpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("DAWNBRINGER.DiscordOAuth");
                oauthLogger.LogWarning(
                    context.Failure,
                    "[DiscordOAuth] Remote OAuth failure or user cancellation; redirecting to '/?discordError=cancelled'.");
                context.Response.Redirect("/?discordError=cancelled");
                context.HandleResponse();
                return Task.CompletedTask;
            },
            OnTicketReceived = async context =>
            {
                ILogger oauthLogger = context.HttpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("DAWNBRINGER.DiscordOAuth");

                if (context.Principal?.Identity is ClaimsIdentity identity)
                {
                    string? discordId = identity.FindFirst("discord_id")?.Value
                                        ?? identity.FindFirst(ClaimTypes.NameIdentifier)?.Value
                                        ?? identity.FindFirst("urn:discord:id")?.Value
                                        ?? identity.FindFirst("id")?.Value
                                        ?? identity.FindFirst("sub")?.Value;
                    string? discordUsername = identity.FindFirst(ClaimTypes.Name)?.Value;
                    string? email = identity.FindFirst(ClaimTypes.Email)?.Value;
                    string? avatar = identity.FindFirst("urn:discord:avatar")?.Value;

                    if (!string.IsNullOrEmpty(discordId))
                    {
                        try
                        {
                            IUserApiClient userApi =
                                context.HttpContext.RequestServices.GetRequiredService<IUserApiClient>();

                            UserDto? existingUser = await userApi.GetUserByDiscordIdAsync(discordId);
                            AccountDto? existingMainAcc = existingUser?.Accounts.FirstOrDefault(a => a.IsMain) ??
                                                          existingUser?.Accounts.FirstOrDefault();

                            if (existingUser != null && existingMainAcc != null)
                            {
                                UserDto user = await userApi.RegisterOrLoginDiscordAsync(new DiscordAuthRequestDto(
                                    discordId,
                                    discordUsername,
                                    email,
                                    avatar
                                )) ?? existingUser;

                                AccountDto mainAcc = user.Accounts.FirstOrDefault(a => a.IsMain) ??
                                                     user.Accounts.FirstOrDefault() ??
                                                     existingMainAcc;

                                if (mainAcc.Type == AccountType.Disabled || mainAcc.IsBanned)
                                {
                                    oauthLogger.LogWarning(
                                        "[DiscordOAuth] Blocked banned/disabled account '{AccountName}' (DiscordId='{DiscordId}').",
                                        mainAcc.Name,
                                        discordId);
                                    context.Response.Redirect("/?discordError=banned");
                                    context.HandleResponse();
                                    return;
                                }

                                Claim? existingNameClaim = identity.FindFirst(ClaimTypes.Name);
                                if (existingNameClaim != null) identity.RemoveClaim(existingNameClaim);
                                identity.AddClaim(new Claim(ClaimTypes.Name, mainAcc.Name));

                                foreach (Claim c in identity.FindAll("AccountId").ToList()) identity.RemoveClaim(c);
                                identity.AddClaim(new Claim("AccountId", mainAcc.ID.ToString()));

                                foreach (Claim c in identity.FindAll("AccountType").ToList()) identity.RemoveClaim(c);
                                identity.AddClaim(new Claim("AccountType", mainAcc.Type.ToString()));

                                foreach (Claim c in identity.FindAll("pending_registration").ToList()) identity.RemoveClaim(c);

                                if (!identity.HasClaim(c => c.Type == "discord_id"))
                                    identity.AddClaim(new Claim("discord_id", !string.IsNullOrEmpty(user.DiscordId) ? user.DiscordId : discordId));

                                if (!identity.HasClaim(c => c.Type == "discord_username") &&
                                    !string.IsNullOrEmpty(user.DiscordUsername ?? discordUsername))
                                    identity.AddClaim(new Claim("discord_username", (user.DiscordUsername ?? discordUsername)!));

                                if (!identity.HasClaim(c => c.Type == "urn:discord:avatar") &&
                                    !string.IsNullOrEmpty(user.DiscordAvatar ?? avatar))
                                    identity.AddClaim(new Claim("urn:discord:avatar", (user.DiscordAvatar ?? avatar)!));

                                bool hasStaffPrivilege = mainAcc.IsStaff ||
                                                         mainAcc.Type is AccountType.Staff or AccountType.GameMaster or AccountType.MatchModerator ||
                                                         user.Accounts.Any(a => a.IsStaff || a.Type is AccountType.Staff or AccountType.GameMaster or AccountType.MatchModerator) ||
                                                         (user.Role != null && (user.Role.Name == UserRoles.Administrator ||
                                                                                user.Role.Name.Equals("Staff", StringComparison.OrdinalIgnoreCase)));
                                if (hasStaffPrivilege)
                                {
                                    identity.AddClaim(new Claim("is_staff", "true"));
                                    identity.AddClaim(new Claim(ClaimTypes.Role, "Staff"));
                                }

                                oauthLogger.LogInformation(
                                    "[DiscordOAuth] Authenticated returning Discord user '{DiscordUsername}' -> HoN Account '{AccountName}' (ID={AccountId}).",
                                    discordUsername,
                                    mainAcc.Name,
                                    mainAcc.ID);
                            }
                            else
                            {
                                // First-time Discord user without a main HoN Account:
                                // Do NOT persist a User row before /Account/Register validates InviteCode!
                                foreach (Claim c in identity.FindAll("AccountId").ToList()) identity.RemoveClaim(c);
                                if (!identity.HasClaim(c => c.Type == "discord_id"))
                                    identity.AddClaim(new Claim("discord_id", discordId));
                                if (!identity.HasClaim(c => c.Type == "discord_username") && !string.IsNullOrEmpty(discordUsername))
                                    identity.AddClaim(new Claim("discord_username", discordUsername));
                                if (!identity.HasClaim("pending_registration", "true"))
                                    identity.AddClaim(new Claim("pending_registration", "true"));

                                string? inviteParam = null;
                                if (!string.IsNullOrEmpty(context.ReturnUri))
                                {
                                    int qIdx = context.ReturnUri.IndexOf('?');
                                    if (qIdx >= 0)
                                    {
                                        Dictionary<string, Microsoft.Extensions.Primitives.StringValues> parsed =
                                            Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(context.ReturnUri[qIdx..]);
                                        if (parsed.TryGetValue("invite", out var invVal) && !string.IsNullOrWhiteSpace(invVal))
                                        {
                                            inviteParam = invVal.ToString().Trim();
                                        }
                                    }
                                }

                                context.ReturnUri = string.IsNullOrEmpty(inviteParam)
                                    ? "/Account/Register"
                                    : $"/Account/Register?invite={Uri.EscapeDataString(inviteParam)}";

                                oauthLogger.LogInformation(
                                    "[DiscordOAuth] First-time Discord login (DiscordId='{DiscordId}', Username='{DiscordUsername}'); routing to '{ReturnUri}' for IGN/InviteCode onboarding.",
                                    discordId,
                                    discordUsername,
                                    context.ReturnUri);
                            }
                        }
                        catch (Exception ex)
                        {
                            oauthLogger.LogError(
                                ex,
                                "[DiscordOAuth] Error enriching Discord OAuth ticket for DiscordId='{DiscordId}'.",
                                discordId);
                        }
                    }
                }
            }
        };
    })
    .AddOAuth("Twitch", options =>
    {
        string? configuredClientId = builder.Configuration["twitch-client-id"] ??
                                     builder.Configuration["Authentication:Twitch:ClientId"] ??
                                     builder.Configuration["Parameters:twitch-client-id"] ??
                                     builder.Configuration["TWITCH_CLIENT_ID"];

        string? configuredClientSecret = builder.Configuration["twitch-client-secret"] ??
                                         builder.Configuration["Authentication:Twitch:ClientSecret"] ??
                                         builder.Configuration["Parameters:twitch-client-secret"] ??
                                         builder.Configuration["TWITCH_CLIENT_SECRET"];

#if !PLAYTEST_BUILD

        if (string.IsNullOrWhiteSpace(configuredClientId) || string.IsNullOrWhiteSpace(configuredClientSecret))
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string[] candidateFiles =
            [
                Path.Combine(appData, "Microsoft", "UserSecrets", "f1e1f81d-ed00-40b6-8d39-0b82a10a6338",
                    "secrets.json"),
                Path.Combine(appData, "Microsoft", "UserSecrets", "00005072-6f6a-6563-7420-4b4f4e474f52",
                    "secrets.json")
            ];

            foreach (string filePath in candidateFiles)
                if (File.Exists(filePath))
                    try
                    {
                        string fileText = File.ReadAllText(filePath);
                        using JsonDocument doc = JsonDocument.Parse(fileText);
                        if (string.IsNullOrWhiteSpace(configuredClientId) &&
                            (doc.RootElement.TryGetProperty("twitch-client-id", out JsonElement idElem) ||
                             doc.RootElement.TryGetProperty("Authentication:Twitch:ClientId", out idElem)))
                            configuredClientId = idElem.GetString();

                        if (string.IsNullOrWhiteSpace(configuredClientSecret) &&
                            (doc.RootElement.TryGetProperty("twitch-client-secret", out JsonElement secElem) ||
                             doc.RootElement.TryGetProperty("Authentication:Twitch:ClientSecret", out secElem)))
                            configuredClientSecret = secElem.GetString();
                    }
                    catch
                    {
                        // Ignore parse exceptions on disk fallback
                    }
        }

#endif
        // Provide safe development fallback if unconfigured to prevent OAuthOptions.Validate() from crashing the portal
        options.ClientId = !string.IsNullOrWhiteSpace(configuredClientId)
            ? configuredClientId
            : "development-twitch-client-id";

        options.ClientSecret = !string.IsNullOrWhiteSpace(configuredClientSecret)
            ? configuredClientSecret
            : "development-twitch-client-secret";

        options.SignInScheme = "ExternalTwitch";
        options.CallbackPath = new PathString("/signin-twitch");
        options.AuthorizationEndpoint = "https://id.twitch.tv/oauth2/authorize";
        options.TokenEndpoint = "https://id.twitch.tv/oauth2/token";
        options.UserInformationEndpoint = "https://api.twitch.tv/helix/users";

        options.ClaimActions.MapCustomJson("urn:twitch:id", user =>
        {
            if (user.TryGetProperty("data", out JsonElement data) && data.GetArrayLength() > 0)
                return data[0].GetProperty("id").GetString();

            return null;
        });

        options.ClaimActions.MapCustomJson(ClaimTypes.Name, user =>
        {
            if (user.TryGetProperty("data", out JsonElement data) && data.GetArrayLength() > 0)
                return data[0].GetProperty("display_name").GetString() ?? data[0].GetProperty("login").GetString();

            return null;
        });

        options.ClaimActions.MapCustomJson("urn:twitch:login", user =>
        {
            if (user.TryGetProperty("data", out JsonElement data) && data.GetArrayLength() > 0)
                return data[0].GetProperty("login").GetString();

            return null;
        });

        options.SaveTokens = true;

        options.Events = new OAuthEvents
        {
            OnRemoteFailure = context =>
            {
                context.Response.Redirect("/Account/Settings");
                context.HandleResponse();
                return Task.CompletedTask;
            },
            OnCreatingTicket = async context =>
            {
                using HttpRequestMessage request = new(HttpMethod.Get, context.Options.UserInformationEndpoint);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.AccessToken);
                request.Headers.Add("Client-Id", context.Options.ClientId);

                using HttpResponseMessage response = await context.Backchannel.SendAsync(request,
                    HttpCompletionOption.ResponseHeadersRead, context.HttpContext.RequestAborted);
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException(
                        $"An error occurred when retrieving Twitch user information ({response.StatusCode}).");

                using JsonDocument payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                context.RunClaimActions(payload.RootElement);
            }
        };
    });

WebApplication app = builder.Build();

// Apply Infrastructure-Aware ForwardedHeaders first so HSTS, ExceptionHandler, and Auth see real client IP/scheme
app.UseInfrastructureForwardedHeaders(app.Configuration);

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    if (!builder.Configuration.GetValue<bool>("KONGOR_ALLOW_HTTP"))
    {
        app.UseHsts();
    }
    else
    {
        Console.WriteLine("[Standalone] DAWNBRINGER HTTP mode enabled (KONGOR_ALLOW_HTTP=true); skipping HSTS and HTTPS redirection");
    }
}

app.UseResponseCompression();
if (!app.Environment.IsDevelopment())
{
    if (!builder.Configuration.GetValue<bool>("KONGOR_ALLOW_HTTP"))
    {
        app.UseHttpsRedirection();
    }
}
Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider staticContentTypeProvider = new();
staticContentTypeProvider.Mappings[".glb"] = "model/gltf-binary";
staticContentTypeProvider.Mappings[".gltf"] = "model/gltf+json";
staticContentTypeProvider.Mappings[".ogg"] = "audio/ogg";

List<IFileProvider> webStaticProviders = [];
bool forceCleanroomAssets = CleanroomPlaceholderAssets.IsForceCleanroomPlaceholders(builder.Configuration);
string? dawnbringerOverlayRoot = CleanroomPlaceholderAssets.ResolveDawnbringerOverlayRoot(
    builder.Environment.ContentRootPath,
    builder.Configuration);
string inPlaceHeroesDir = Path.Combine(
    builder.Environment.WebRootPath ?? Path.Combine(builder.Environment.ContentRootPath, "wwwroot"),
    "images",
    "heroes");
bool hasInPlaceRetailImages = !forceCleanroomAssets &&
                              Directory.Exists(inPlaceHeroesDir) &&
                              Directory.EnumerateFiles(inPlaceHeroesDir).Any();
if (!string.IsNullOrWhiteSpace(dawnbringerOverlayRoot))
{
    webStaticProviders.Add(new PhysicalFileProvider(dawnbringerOverlayRoot));
    Console.WriteLine($"[DAWNBRINGER] Mounted local retail asset overlay from '{dawnbringerOverlayRoot}'.");
}
else if (hasInPlaceRetailImages)
{
    Console.WriteLine($"[DAWNBRINGER] Serving in-place retail web assets from '{inPlaceHeroesDir}'.");
}
else
{
    Console.WriteLine("[DAWNBRINGER] Running in Cleanroom Placeholder Mode (deterministic SVG monogram badges & procedural 3D glTF crystals active).");
}
if (builder.Environment.WebRootFileProvider != null)
{
    webStaticProviders.Add(builder.Environment.WebRootFileProvider);
}

IFileProvider? compositeWebStaticProvider = webStaticProviders.Count > 1
    ? new CompositeFileProvider(webStaticProviders)
    : webStaticProviders.FirstOrDefault();

async Task<bool> TryServeHydratedAliasOrCleanroomBadgeAsync(HttpContext context, bool forceCleanroomOnly)
{
    PathString reqPath = context.Request.Path;
    if ((!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method)) ||
        !reqPath.HasValue)
    {
        return false;
    }

    string rawVal = reqPath.Value;
    bool isImagesRoute = rawVal.StartsWith("/images/", StringComparison.OrdinalIgnoreCase);
    string? trimmed = isImagesRoute
        ? rawVal["/images/".Length..]
        : rawVal.Length > 1 && rawVal[0] == '/'
            ? rawVal[1..]
            : null;

    if (string.IsNullOrEmpty(trimmed))
    {
        return false;
    }

    int slashIdx = trimmed.IndexOf('/');
    if (slashIdx <= 0 || slashIdx >= trimmed.Length - 1)
    {
        return false;
    }

    string category = trimmed[..slashIdx];
    string slug = trimmed[(slashIdx + 1)..];
    string ext = Path.GetExtension(slug);
    bool validImageExt = isImagesRoute ||
                         ext.Equals(".tga", StringComparison.OrdinalIgnoreCase) ||
                         ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                         ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                         ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                         ext.Equals(".webp", StringComparison.OrdinalIgnoreCase) ||
                         ext.Equals(".dds", StringComparison.OrdinalIgnoreCase) ||
                         ext.Equals(".svg", StringComparison.OrdinalIgnoreCase);

    if (!validImageExt ||
        (!category.Equals("heroes", StringComparison.OrdinalIgnoreCase) &&
         !category.Equals("items", StringComparison.OrdinalIgnoreCase) &&
         !category.Equals("abilities", StringComparison.OrdinalIgnoreCase) &&
         !category.Equals("maps", StringComparison.OrdinalIgnoreCase)))
    {
        return false;
    }

    string badgeCategory = category;
    string badgeLabel = slug;

    if (HeroAndMapAssetResolver.TryResolveStaticImageCandidate(
            rawVal,
            compositeWebStaticProvider,
            forceCleanroomOnly,
            out IFileInfo? resolvedAssetFile,
            out string normalizedCategory,
            out string normalizedLabel))
    {
        if (!string.IsNullOrWhiteSpace(normalizedCategory))
        {
            badgeCategory = normalizedCategory;
        }

        if (!string.IsNullOrWhiteSpace(normalizedLabel))
        {
            badgeLabel = normalizedLabel;
        }

        if (!forceCleanroomOnly && resolvedAssetFile is { Exists: true, IsDirectory: false })
        {
            ILogger<Program>? assetLogger = context.RequestServices.GetService<ILogger<Program>>();
            assetLogger?.LogDebug(
                "[DAWNBRINGER.AssetResolver] Resolved static image alias '{RequestPath}' -> hydrated asset '{AssetName}' ({Length} bytes).",
                rawVal, resolvedAssetFile.Name, resolvedAssetFile.Length);

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = staticContentTypeProvider.TryGetContentType(resolvedAssetFile.Name, out string? mime)
                ? mime
                : "image/png";
            context.Response.ContentLength = resolvedAssetFile.Length;
            context.Response.Headers.CacheControl = context.Request.Query.ContainsKey("v")
                ? "public, max-age=31536000, immutable"
                : "public, max-age=86400, stale-while-revalidate=604800";

            if (HttpMethods.IsGet(context.Request.Method))
            {
                if (!string.IsNullOrWhiteSpace(resolvedAssetFile.PhysicalPath))
                {
                    await context.Response.SendFileAsync(resolvedAssetFile.PhysicalPath, context.RequestAborted);
                }
                else
                {
                    await using Stream assetStream = resolvedAssetFile.CreateReadStream();
                    await assetStream.CopyToAsync(context.Response.Body, context.RequestAborted);
                }
            }

            return true;
        }
    }

    string svg = CleanroomPlaceholderAssets.BuildSvgBadge(badgeCategory, badgeLabel);
    context.Response.StatusCode = StatusCodes.Status200OK;
    context.Response.ContentType = "image/svg+xml; charset=utf-8";
    context.Response.Headers.CacheControl = "public, max-age=3600";
    if (HttpMethods.IsGet(context.Request.Method))
    {
        await context.Response.WriteAsync(svg, context.RequestAborted);
    }

    return true;
}

if (forceCleanroomAssets)
{
    app.Use(async (context, next) =>
    {
        if (await TryServeHydratedAliasOrCleanroomBadgeAsync(context, forceCleanroomOnly: true))
        {
            return;
        }

        await next();
    });
}

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = compositeWebStaticProvider,
    ContentTypeProvider = staticContentTypeProvider,
    OnPrepareResponse = ctx =>
    {
        if (ctx.Context.Request.Query.ContainsKey("v"))
        {
            ctx.Context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            return;
        }

        string reqPath = ctx.Context.Request.Path.Value ?? string.Empty;
        string ext = Path.GetExtension(reqPath);
        bool isCacheableAsset =
            reqPath.StartsWith("/images/", StringComparison.OrdinalIgnoreCase) ||
            reqPath.StartsWith("/fonts/", StringComparison.OrdinalIgnoreCase) ||
            reqPath.StartsWith("/lib/", StringComparison.OrdinalIgnoreCase) ||
            reqPath.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".svg", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".webp", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".ico", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".woff2", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".woff", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".glb", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".ogg", StringComparison.OrdinalIgnoreCase);

        ctx.Context.Response.Headers.CacheControl = isCacheableAsset
            ? "public, max-age=86400, stale-while-revalidate=604800"
            : "no-cache";
    }
});

// Hydrated alias resolver + Cleanroom deterministic SVG monogram fallback when retail icon assets (/images/{heroes,items,abilities,maps}/*) are absent
app.Use(async (context, next) =>
{
    if (await TryServeHydratedAliasOrCleanroomBadgeAsync(context, forceCleanroomOnly: forceCleanroomAssets))
    {
        return;
    }

    await next();
});

// Serve Video Files (/videos)
List<IFileProvider> videoFileProviders = [];
string? configVideoDir = builder.Configuration["VideoStorage:LocalDisk:UploadDirectory"];
if (!string.IsNullOrWhiteSpace(configVideoDir) && Directory.Exists(configVideoDir))
    videoFileProviders.Add(new PhysicalFileProvider(configVideoDir));

string clientUserDataVideoPath =
    Path.Combine(builder.Environment.ContentRootPath, "games", "client", "userdata", "game", "videos");
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

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new CompositeFileProvider(videoFileProviders),
    RequestPath = "/videos",
    ServeUnknownFileTypes = true
});

app.UseOutputCache();

app.UseRouting();

app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

app.MapHub<TelemetryHub>("/hubs/telemetry");
app.MapHub<LiveMatchesHub>("/hubs/live-matches");
app.MapHub<DevoWarsHub>("/hubs/devo-wars");
app.MapHub<MonkeyCheckersHub>("/hubs/monkey-checkers");
app.MapHub<KongorChessHub>("/hubs/kongor-chess");
app.MapHub<RiftbreakerHub>("/hubs/riftbreaker");
app.MapHub<DeveloperIssuesHub>("/hubs/developer-issues");

app.MapControllerRoute(
    "login",
    "login",
    new { controller = "Account", action = "Login" });

app.MapControllerRoute(
    "default",
    "{controller=Home}/{action=Index}/{id?}");

app.MapGroup(string.Empty).AllowAnonymous().MapNexusHealthCheckEndpoints();

app.Run();

namespace DAWNBRINGER.WebPortal.UI
{
    public interface IDawnBringerAssemblyMarker;
}

[SuppressMessage("Microsoft.Design", "ASP0027",
    Justification = "Required for WebApplicationFactory integration testing")]
public partial class Program;
