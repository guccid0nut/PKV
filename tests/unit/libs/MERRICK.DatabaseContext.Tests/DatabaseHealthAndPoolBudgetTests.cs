// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Serialization;
using ASPIRE.Common.Extensions;
using ASPIRE.Common.ServiceDefaults;
using FluentAssertions;
using MERRICK.DatabaseContext.Extensions;
using MERRICK.DatabaseContext.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;
using ConfigurationExtensions = ASPIRE.Common.Extensions.ConfigurationExtensions;

namespace MERRICK.DatabaseContext.Tests;

public sealed class DatabaseHealthAndPoolBudgetTests
{
    private const string SupavisorUri = "postgresql://postgres.proj:secret@aws-0-us-east-1.pooler.supabase.com:5432/postgres";
    private const string SupavisorAdo = "Host=aws-0-us-east-1.pooler.supabase.com;Port=5432;Database=postgres;Username=postgres.proj;Password=secret;";

    // protects: Supavisor Session Pooler warm connection budget (4 services * MinPoolSize=1 = 4 warm sockets, 4 * MaxPoolSize=3 = 12 <= 15 cap) with KeepAlive=30.
    // fails_when: MinPoolSize reverts to 0 (causing 15s socket pruning & cold TLS+SCRAM handshakes), MaxPoolSize exceeds 3, or KeepAlive != 30.
    // seam: Real ConfigurationExtensions.NormalizePostgreSqlConnectionString + MerrickDatabaseExtensions.AddMerrickDatabase.
    [Fact]
    public void SupavisorSessionPooler_ConnectionStringAndMerrickDatabase_EnforceWarmPoolBudget()
    {
        string normalizedUri = ConfigurationExtensions.NormalizePostgreSqlConnectionString(SupavisorUri);
        NpgsqlConnectionStringBuilder uriBuilder = new(normalizedUri);
        uriBuilder.MinPoolSize.Should().Be(1);
        uriBuilder.MaxPoolSize.Should().Be(3);
        uriBuilder.KeepAlive.Should().Be(30);

        string normalizedAdo = ConfigurationExtensions.NormalizePostgreSqlConnectionString(SupavisorAdo);
        NpgsqlConnectionStringBuilder adoBuilder = new(normalizedAdo);
        adoBuilder.MinPoolSize.Should().Be(1);
        adoBuilder.MaxPoolSize.Should().Be(3);
        adoBuilder.KeepAlive.Should().Be(30);

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = "Supabase",
            ["Supabase:DatabaseConnectionString"] = SupavisorUri
        });

        builder.AddMerrickDatabase();

        using ServiceProvider sp = builder.Services.BuildServiceProvider();
        NpgsqlDataSource dataSource = sp.GetRequiredService<NpgsqlDataSource>();
        NpgsqlConnectionStringBuilder dsBuilder = new(dataSource.ConnectionString);
        dsBuilder.MinPoolSize.Should().Be(1);
        dsBuilder.MaxPoolSize.Should().Be(3);
        dsBuilder.KeepAlive.Should().Be(30);
    }

    // protects: Single NpgsqlDataSource per host regardless of AddServiceDefaults vs AddMerrickDatabase order, poll interval > timeout, and DI-owned disposal.
    // fails_when: AddServiceDefaults() eagerly creates a second NpgsqlDataSource, merrick-db-npgsql duplicate check remains, or standalone DataSource leaks on shutdown.
    // seam: Real WebApplicationBuilder + AddServiceDefaults() + AddMerrickDatabase() + ServiceProvider disposal.
    [Fact]
    public async Task AddServiceDefaults_And_AddMerrickDatabase_RegisterSinglePoolAndCleanDisposal()
    {
        // Case 1: AddServiceDefaults() BEFORE AddMerrickDatabase() (real production order in KONGOR/TRANSMUTANSTEIN/ZORGATH)
        WebApplicationBuilder combinedBuilder = WebApplication.CreateBuilder();
        combinedBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = "Supabase",
            ["Supabase:DatabaseConnectionString"] = SupavisorUri,
            ["IS_TEST_ENVIRONMENT"] = "false"
        });

        combinedBuilder.AddServiceDefaults();
        combinedBuilder.AddMerrickDatabase();

        combinedBuilder.Services.Count(sd => sd.ServiceType == typeof(NpgsqlDataSource)).Should().Be(1);

        await using (ServiceProvider sp = combinedBuilder.Services.BuildServiceProvider())
        {
            HealthCheckServiceOptions hcOptions = sp.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;
            hcOptions.Registrations.Should().ContainSingle(r => r.Name == "database");
            hcOptions.Registrations.Should().NotContain(r => r.Name == "merrick-db-npgsql");

            HealthCheckRegistration dbReg = hcOptions.Registrations.Single(r => r.Name == "database");
            dbReg.Timeout.Should().Be(TimeSpan.FromSeconds(10));

            HealthCheckPublisherOptions pubOptions = sp.GetRequiredService<IOptions<HealthCheckPublisherOptions>>().Value;
            pubOptions.Period.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(15));
            pubOptions.Period.Should().BeGreaterThan(dbReg.Timeout);
        }

        // Case 2: Standalone AddServiceDefaults() WITHOUT AddMerrickDatabase() owns and disposes its single fallback NpgsqlDataSource.
        WebApplicationBuilder standaloneBuilder = WebApplication.CreateBuilder();
        standaloneBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = "Supabase",
            ["Supabase:DatabaseConnectionString"] = SupavisorUri,
            ["IS_TEST_ENVIRONMENT"] = "false"
        });

        standaloneBuilder.AddServiceDefaults();
        standaloneBuilder.Services.Count(sd => sd.ServiceType == typeof(NpgsqlDataSource)).Should().Be(1);

        NpgsqlDataSource resolvedFallback;
        await using (ServiceProvider standaloneSp = standaloneBuilder.Services.BuildServiceProvider())
        {
            resolvedFallback = standaloneSp.GetRequiredService<NpgsqlDataSource>();
        }

        Action openAfterDispose = () => resolvedFallback.OpenConnection();
        openAfterDispose.Should().Throw<ObjectDisposedException>();
    }

    // protects: Accurate health status classification separating transient single timeouts (Degraded) from auth/socket/DNS/sustained outages (Unhealthy).
    // fails_when: Blanket Degraded masks 28P01/DNS/connection-refused/3-consecutive-timeouts, or single transient timeout returns Unhealthy.
    // seam: Real ClassifiedPostgreSqlHealthCheck with real NpgsqlDataSource + fault injection probe.
    [Fact]
    public async Task ClassifiedPostgreSqlHealthCheck_ClassifiesTransientTimeoutsVsRealOutages()
    {
        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create("Host=127.0.0.1;Port=5432;Database=merrick;Username=u;Password=p;");
        ServiceCollection services = new();
        services.AddSingleton(dataSource);
        await using ServiceProvider sp = services.BuildServiceProvider();

        Exception? nextException = null;
        ClassifiedPostgreSqlHealthCheck check = new(
            sp,
            TimeSpan.FromSeconds(10),
            (_, _) => nextException is not null ? Task.FromException(nextException) : Task.CompletedTask);

        HealthCheckContext context = new()
        {
            Registration = new HealthCheckRegistration("database", check, HealthStatus.Unhealthy, ["ready", "db"])
        };

        // 1st and 2nd consecutive timeouts -> Degraded
        nextException = new OperationCanceledException("transient connect timeout 1");
        (await check.CheckHealthAsync(context)).Status.Should().Be(HealthStatus.Degraded);
        check.ConsecutiveTimeouts.Should().Be(1);

        nextException = new TimeoutException("transient connect timeout 2");
        (await check.CheckHealthAsync(context)).Status.Should().Be(HealthStatus.Degraded);
        check.ConsecutiveTimeouts.Should().Be(2);

        // 3rd consecutive timeout -> Unhealthy
        nextException = new OperationCanceledException("sustained timeout 3");
        (await check.CheckHealthAsync(context)).Status.Should().Be(HealthStatus.Unhealthy);
        check.ConsecutiveTimeouts.Should().Be(3);

        // Healthy probe resets consecutive timeout counter
        nextException = null;
        (await check.CheckHealthAsync(context)).Status.Should().Be(HealthStatus.Healthy);
        check.ConsecutiveTimeouts.Should().Be(0);

        // Single timeout after recovery is Degraded again
        nextException = new SocketException((int)SocketError.TimedOut);
        (await check.CheckHealthAsync(context)).Status.Should().Be(HealthStatus.Degraded);

        // Auth failure (28P01) -> Unhealthy immediately
        nextException = CreatePostgresException("28P01", "password authentication failed");
        (await check.CheckHealthAsync(context)).Status.Should().Be(HealthStatus.Unhealthy);

        // Connection refused -> Unhealthy immediately
        nextException = new SocketException((int)SocketError.ConnectionRefused);
        (await check.CheckHealthAsync(context)).Status.Should().Be(HealthStatus.Unhealthy);

        // DNS failure -> Unhealthy immediately
        nextException = new SocketException((int)SocketError.HostNotFound);
        (await check.CheckHealthAsync(context)).Status.Should().Be(HealthStatus.Unhealthy);
    }

    // protects: Startup readiness health check avoiding false [ERR] logs while DatabaseInitializer runs cleanly.
    // fails_when: IsCompleted=false and LastError=null returns HealthStatus.Unhealthy.
    // seam: Real DatabaseInitializer + DatabaseReadinessHealthCheck.
    [Fact]
    public async Task DatabaseReadinessHealthCheck_ReturnsDegradedDuringStartup_UnhealthyOnError_HealthyOnCompletion()
    {
        await using ServiceProvider sp = new ServiceCollection().BuildServiceProvider();
        DatabaseInitializer initializer = new(sp, NullLogger<DatabaseInitializer>.Instance);
        DatabaseReadinessHealthCheck check = new(initializer);
        HealthCheckContext context = new()
        {
            Registration = new HealthCheckRegistration("merrick-db-initializer", check, HealthStatus.Unhealthy, ["ready", "db"])
        };

        initializer.SetCompletedForTesting(completed: false, lastError: null);
        HealthCheckResult inProgress = await check.CheckHealthAsync(context);
        inProgress.Status.Should().Be(HealthStatus.Degraded);
        inProgress.Description.Should().Be("MERRICK database initialization is still in progress.");

        initializer.SetCompletedForTesting(completed: false, lastError: "Connection refused");
        HealthCheckResult errored = await check.CheckHealthAsync(context);
        errored.Status.Should().Be(HealthStatus.Unhealthy);
        errored.Description.Should().Be("Connection refused");

        initializer.SetCompletedForTesting(completed: true, lastError: null);
        HealthCheckResult completed = await check.CheckHealthAsync(context);
        completed.Status.Should().Be(HealthStatus.Healthy);
    }

    private static PostgresException CreatePostgresException(string sqlState, string message)
    {
        return new PostgresException(message, "FATAL", "FATAL", sqlState);
    }
}
