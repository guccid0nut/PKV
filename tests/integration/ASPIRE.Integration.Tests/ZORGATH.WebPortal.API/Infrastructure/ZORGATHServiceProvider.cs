// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net;
using ASPIRE.Tests.InProcess;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Moq;
using SendGrid;
using SendGrid.Helpers.Mail;
using IDatabase = StackExchange.Redis.IDatabase;

namespace ASPIRE.Tests.ZORGATH.WebPortal.API.Infrastructure;

/// <summary>
///     Provides Test Dependencies For ZORGATH Web Portal API Tests
/// </summary>
public static class ZORGATHServiceProvider
{
    private static readonly InMemoryDatabaseRoot SharedDatabaseRoot = new();
    private static readonly string CachedContentRoot;

    static ZORGATHServiceProvider()
    {
        string contentRoot = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(contentRoot) && !File.Exists(Path.Combine(contentRoot, "ProjectKongor.slnxx")) && !File.Exists(Path.Combine(contentRoot, "ProjectKongor.slnx")))
        {
            string? parent = Directory.GetParent(contentRoot)?.FullName;
            if (parent == null || parent == contentRoot) break;

            contentRoot = parent;
        }

        CachedContentRoot = Path.Combine(contentRoot, "src", "services", "web-api");
    }

    /// <summary>
    ///     Creates An Instance Of The ZORGATH Web Portal API With In-Memory Dependencies
    /// </summary>
    public static WebApplicationFactory<IZorgathAssemblyMarker> CreateOrchestratedInstance(string? identifier = null)
    {
        string databaseName = identifier ?? Guid.CreateVersion7().ToString();
        string contentRoot = CachedContentRoot;

        // Replace Database Context And Distributed Cache With In-Memory Implementations
        WebApplicationFactory<IZorgathAssemblyMarker> webApplicationFactory =
            new WebApplicationFactory<IZorgathAssemblyMarker>().WithWebHostBuilder(builder =>
            {
                builder.UseContentRoot(contentRoot);
                builder.UseSetting("INFRASTRUCTURE_GATEWAY", "localhost")
                    .UseSetting("IS_TEST_ENVIRONMENT", "true")
                    .UseSetting("Operational:JWT:SigningKey",
                        "TEST_SIGNING_KEY_MUST_BE_LONG_ENOUGH_FOR_HMAC_SHA256_AT_LEAST_32_BYTES")
                    .ConfigureLogging(logging =>
                    {
                        logging.ClearProviders();
                        logging.SetMinimumLevel(LogLevel.Warning);
                    })
                    .ConfigureServices(services =>
                    {
                        bool databaseContextPredicate(ServiceDescriptor descriptor)
                        {
                            return descriptor.ServiceType.FullName?.Contains(nameof(MerrickContext)) is true ||
                                   descriptor.ImplementationType?.FullName?.Contains(nameof(MerrickContext)) is true;
                        }

                        foreach (ServiceDescriptor descriptor in services.Where(databaseContextPredicate).ToList())
                            services.Remove(descriptor);

                        services.AddDbContextPool<MerrickContext>(options =>
                        {
                            options.UseModel(TestDbContextFactory.SharedModel);
                            options.UseInMemoryDatabase(databaseName, SharedDatabaseRoot);
                        });

                        bool distributedCachePredicate(ServiceDescriptor descriptor)
                        {
                            return descriptor.ServiceType == typeof(IConnectionMultiplexer) ||
                                   descriptor.ServiceType == typeof(IDatabase);
                        }

                        foreach (ServiceDescriptor descriptor in services.Where(distributedCachePredicate).ToList())
                            services.Remove(descriptor);

                        // Register In-Process Distributed Cache Database
                        services.AddSingleton<IDatabase, InProcessDistributedCacheStore>();

                        // Mock ISendGridClient to avoid actual API calls
                        services.RemoveAll<ISendGridClient>();
                        Mock<ISendGridClient> mockSendGridClient = new();
                        mockSendGridClient
                            .Setup(client =>
                                client.SendEmailAsync(It.IsAny<SendGridMessage>(), It.IsAny<CancellationToken>()))
                            .ReturnsAsync(new Response(HttpStatusCode.Accepted, null, null));
                        services.AddSingleton(mockSendGridClient.Object);

                        // Fast-path Health Checks for test runner (eliminate 15s remote probe timeout)
                        services.PostConfigure<HealthCheckServiceOptions>(options =>
                        {
                            List<HealthCheckRegistration> toRemove =
                            [
                                .. options.Registrations
                                    .Where(r => r.Name.Contains("redis", StringComparison.OrdinalIgnoreCase) ||
                                                r.Name.Contains("npgsql", StringComparison.OrdinalIgnoreCase) ||
                                                r.Name.Contains("sqlserver", StringComparison.OrdinalIgnoreCase) ||
                                                r.Name.Contains("Database", StringComparison.OrdinalIgnoreCase))
                            ];
                            foreach (HealthCheckRegistration reg in toRemove) options.Registrations.Remove(reg);
                        });

                        // Mock IHttpClientFactory to return immediate response and avoid external network calls
                        services.RemoveAll<IHttpClientFactory>();
                        Mock<IHttpClientFactory> mockHttpClientFactory = new();
                        HttpClient stubClient = new(new StubHttpMessageHandler());
                        mockHttpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(stubClient);
                        services.AddSingleton(mockHttpClientFactory.Object);
                    });
            });

        using IServiceScope scope = webApplicationFactory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<MerrickContext>().Database.EnsureCreated();

        return webApplicationFactory;
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}

