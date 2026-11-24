// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using Kongor.Core.Services.Events;
using MERRICK.DatabaseContext.Repositories;
using MERRICK.DatabaseContext.Services;
using NEXUS.Contracts.Data;
using NEXUS.Contracts.Services;
using SendGrid;
using StackExchange.Redis;
using ZORGATH.WebPortal.API.Services.Events;

namespace ZORGATH.WebPortal.API.Extensions;

public static class WebPortalServiceCollectionExtensions
{
    /// <summary>
    ///     Registers Domain, Application, Email, and Security Services for ZORGATH Web Portal API.
    /// </summary>
    public static void AddWebPortalServices(this IServiceCollection services, IConfiguration configuration)
    {
        // Register TimeProvider
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<MERRICK.DatabaseContext.Services.Argon2idHasher>();

        // Register Guide Repository
        services.AddScoped<IGuideRepository, HeroGuideRepository>();

        // Register Asynchronous Domain Event Bus & Subscriptions
        string? redisConn = configuration.GetConnectionString("DISTRIBUTED-CACHE") ??
                            configuration["Aspire:StackExchange:Redis:ConnectionString"];
        if (!string.IsNullOrWhiteSpace(redisConn))
            services.AddRedisEventBus();
        else
            services.AddInMemoryEventBus();
        services.AddEventHandler<WebPortalMatchCompletedEventHandler>();
        // Add SendGrid Client with Resilient HttpClient
        services.AddHttpClient("SendGrid");
        services.AddSingleton<ISendGridClient>(sp =>
        {
            OperationalConfiguration config = configuration
                                                  .GetRequiredSection(OperationalConfiguration.ConfigurationSection)
                                                  .Get<OperationalConfiguration>() ??
                                              throw new InvalidOperationException("Operational Configuration Is NULL");

            string apiKey = !string.IsNullOrWhiteSpace(config.Email.ApiKey)
                ? config.Email.ApiKey
                : "SG.development_placeholder_key_not_set";

            IHttpClientFactory httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
            HttpClient httpClient = httpClientFactory.CreateClient("SendGrid");
            return new SendGridClient(httpClient, apiKey);
        });

        // Add EmailService:
        // - Default / Local / Discord-only mode: ConsoleEmailService (no email required or sent)
        // - Optional community opt-in (non-Dev + Email:SmtpEnabled=true + Provider="Smtp" + SmtpHost): SmtpEmailService (MailKit)
        // - Optional SendGrid backup (non-Dev + valid SendGrid ApiKey): EmailService
        services.AddSingleton<ConsoleEmailService>();
        services.AddSingleton<SmtpEmailService>();
        services.AddSingleton<EmailService>();
        services.AddSingleton<IEmailService>(sp =>
        {
            IWebHostEnvironment env = sp.GetRequiredService<IWebHostEnvironment>();
            if (env.IsDevelopment())
            {
                return sp.GetRequiredService<ConsoleEmailService>();
            }

            OperationalConfiguration config = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<OperationalConfiguration>>().Value;

            bool isSmtpOptedIn = config.Email.SmtpEnabled &&
                                 string.Equals(config.Email.Provider, "Smtp", StringComparison.OrdinalIgnoreCase) &&
                                 !string.IsNullOrWhiteSpace(config.Email.SmtpHost);
            if (isSmtpOptedIn)
            {
                return sp.GetRequiredService<SmtpEmailService>();
            }

            bool isSendGridConfigured = !string.IsNullOrWhiteSpace(config.Email.ApiKey) &&
                                        config.Email.ApiKey != "SENDGRID_API_KEY_PLACEHOLDER" &&
                                        !config.Email.ApiKey.StartsWith("SG.development_placeholder", StringComparison.OrdinalIgnoreCase);

            if (!isSendGridConfigured)
            {
                return sp.GetRequiredService<ConsoleEmailService>();
            }

            return sp.GetRequiredService<EmailService>();
        });

        // Add UserService
        services.AddScoped<IUserService, UserService>();

        // Add AuthenticationService
        services.AddScoped<IAuthenticationService, AuthenticationService>();

        // Register Validators
        services.AddScoped<PasswordValidator>();

        // Register Admin Metrics Service
        services.AddScoped<IAdminMetricsService>(sp =>
        {
            ILogger<AdminMetricsService> logger =
                sp.GetRequiredService<ILogger<AdminMetricsService>>();
            MerrickContext dbContext =
                sp.GetRequiredService<MerrickContext>();
            IConnectionMultiplexer? muxer =
                sp.GetService<IConnectionMultiplexer>();
            TimeProvider timeProvider =
                sp.GetRequiredService<TimeProvider>();
            return new AdminMetricsService(logger, dbContext, multiplexer: muxer, timeProvider: timeProvider);
        });
    }
}
