// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using MERRICK.DatabaseContext.Extensions;
using MERRICK.DatabaseContext.Services;

namespace MERRICK.DatabaseContext;

/// <summary>
///     My name is Merrick, and I manage the store ... the data store.
/// </summary>
public static class MERRICK
{
    public static async Task Main(string[] args)
    {
        // Create The Application Builder
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

        // Add Aspire Service Defaults
        builder.AddServiceDefaults();

        // Add The Database Context with Multi-Provider Support (SQL Server & PostgreSQL / Supabase)
        builder.AddMerrickDatabase();

        // Add Database Initializer Telemetry
        builder.Services.AddOpenTelemetry()
            .WithTracing(tracing => tracing.AddSource(DatabaseInitializer.ActivitySourceName));

        // Register Database Initializer As Singleton Service For Dependency Injection
        builder.Services.AddSingleton<DatabaseInitializer>();
        builder.Services.AddSingleton<Argon2idHasher>();

        // Register Database Initializer As Hosted Service For Background Execution At Application Startup
        builder.Services.AddHostedService(provider => provider.GetRequiredService<DatabaseInitializer>());

        // Add Database Health Check
        builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("MERRICK Database Health Check");

        // Configure Forwarded Headers For Reverse Proxy Support
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

        // Enforce HTTPS With Strict Transport Security
        application.UseHsts();

        // Automatically Redirect HTTP Requests To HTTPS
        application.UseHttpsRedirection();

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

        // Run The Application
        await application.RunAsync();
    }
}
