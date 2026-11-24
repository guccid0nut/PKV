// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using System.Net;
using ASPIRE.Tests.InProcess;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Moq;
using IDatabase = StackExchange.Redis.IDatabase;

namespace ASPIRE.Tests.KONGOR.MasterServer.Infrastructure;

/// <summary>
///     Provides Test Dependencies For KONGOR Master Server Tests
/// </summary>
public static class KONGORServiceProvider
{
    private static readonly InMemoryDatabaseRoot SharedDatabaseRoot = new();
    private static readonly string CachedContentRoot;

    static KONGORServiceProvider()
    {
        Environment.SetEnvironmentVariable("INFRASTRUCTURE_GATEWAY", "localhost");
        Environment.SetEnvironmentVariable("CHAT_SERVER_HOST", "localhost");
        Environment.SetEnvironmentVariable("CHAT_SERVER_PORT_CLIENT", "11031");
        Environment.SetEnvironmentVariable("CHAT_SERVER_PORT_MATCH_SERVER", "11032");
        Environment.SetEnvironmentVariable("CHAT_SERVER_PORT_MATCH_SERVER_MANAGER", "11033");
        Environment.SetEnvironmentVariable("APPLICATION_URL", "http://0.0.0.0/");

        string contentRoot = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(contentRoot) && !File.Exists(Path.Combine(contentRoot, "ProjectKongor.slnxx")) && !File.Exists(Path.Combine(contentRoot, "ProjectKongor.slnx")))
        {
            string? parent = Directory.GetParent(contentRoot)?.FullName;
            if (parent == null || parent == contentRoot) break;

            contentRoot = parent;
        }

        CachedContentRoot = Path.Combine(contentRoot, "src", "services", "master-server");
    }

    /// <summary>
    ///     Creates An Instance Of The KONGOR Master Server With In-Memory Dependencies
    /// </summary>
    public static WebApplicationFactory<KONGORAssemblyMarker> CreateOrchestratedInstance(string? identifier = null)
    {
        string databaseName = identifier ?? Guid.CreateVersion7().ToString();
        string contentRoot = CachedContentRoot;

        // Replace Database Context And Distributed Cache With In-Memory Implementations
        WebApplicationFactory<KONGORAssemblyMarker> webApplicationFactory =
            new WebApplicationFactory<KONGORAssemblyMarker>().WithWebHostBuilder(builder =>
            {
                builder.UseContentRoot(contentRoot);
                builder.UseSetting("INFRASTRUCTURE_GATEWAY", "localhost")
                    .ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["IS_TEST_ENVIRONMENT"] = "true",
                            ["RateLimiting:AuthPerIpPerMinute"] = "1000",
                            ["Store:UnlockAllItems"] = "false",
                            ["CHAT_SERVER_HOST"] = "localhost",
                            ["CHAT_SERVER_PORT_CLIENT"] = "11031",
                            ["CHAT_SERVER_PORT_MATCH_SERVER"] = "11032",
                            ["CHAT_SERVER_PORT_MATCH_SERVER_MANAGER"] = "11033",
                            ["APPLICATION_URL"] = "http://0.0.0.0/",
                            ["INFRASTRUCTURE_GATEWAY"] = "localhost",
                            ["Serilog:MinimumLevel:Default"] = "Information",
                            ["Serilog:MinimumLevel:Override:Microsoft"] = "Warning",
                            ["Serilog:MinimumLevel:Override:Microsoft.AspNetCore"] = "Warning",
                            ["Serilog:MinimumLevel:Override:Microsoft.EntityFrameworkCore"] = "Warning",
                            ["Logging:LogLevel:Default"] = "Warning",
                            ["Logging:LogLevel:Microsoft"] = "Warning",
                            ["Logging:LogLevel:Microsoft.EntityFrameworkCore"] = "Warning"
                        }))
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

                        // Remove MerrickContext Registration
                        foreach (ServiceDescriptor descriptor in services.Where(databaseContextPredicate).ToList())
                            services.Remove(descriptor);

                        services.AddDbContext<MerrickContext>(options =>
                        {
                            options.UseModel(TestDbContextFactory.SharedModel);
                            options.UseInMemoryDatabase(databaseName, SharedDatabaseRoot);
                        });

                        bool distributedCachePredicate(ServiceDescriptor descriptor)
                        {
                            return descriptor.ServiceType == typeof(IConnectionMultiplexer) ||
                                   descriptor.ServiceType == typeof(IDatabase);
                        }

                        // Remove IConnectionMultiplexer And IDatabase Registrations
                        foreach (ServiceDescriptor descriptor in services.Where(distributedCachePredicate).ToList())
                            services.Remove(descriptor);

                        // Register In-Process Distributed Cache Database
                        services.AddSingleton<IDatabase, InProcessDistributedCacheStore>();

                        // Register Mocked IConnectionMultiplexer
                        services.AddSingleton(sp =>
                        {
                            IDatabase database = sp.GetRequiredService<IDatabase>();
                            Mock<IConnectionMultiplexer> mockMultiplexer = new();
                            mockMultiplexer.Setup(x => x.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
                                .Returns(database);

                            Mock<ISubscriber> mockSubscriber = new();
                            mockMultiplexer.Setup(x => x.GetSubscriber(It.IsAny<object>()))
                                .Returns(mockSubscriber.Object);

                            return mockMultiplexer.Object;
                        });

                        // Add Middleware To Set Fake Remote IP Address
                        services.AddSingleton<IStartupFilter>(new RemoteIPAddressStartupFilter());

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

