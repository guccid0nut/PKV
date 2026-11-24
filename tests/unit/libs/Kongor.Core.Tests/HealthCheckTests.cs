// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.IO;
using System.Text.Json;
using ASPIRE.Common.Extensions;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Kongor.Core.Tests;

public class HealthCheckTests
{
    [Fact]
    public void AddNexusDefaultHealthChecks_RegistersBasicProbes_WhenNoConnectionsConfigured()
    {
        // Arrange
        var builder = Host.CreateApplicationBuilder();

        // Act
        builder.AddNexusDefaultHealthChecks();
        var host = builder.Build();

        var options = host.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;

        // Assert
        options.Registrations.Should().Contain(r => r.Name == "self");
        options.Registrations.Should().Contain(r => r.Name == "circuit-breakers");
        options.Registrations.Should().Contain(r => r.Name == "background-workers");
    }

    [Fact]
    public void AddNexusDefaultHealthChecks_RegistersDatabaseAndRedis_WhenConfigured()
    {
        // Arrange
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["ConnectionStrings:MERRICK"] = "Host=localhost;Database=kongor;Username=postgres;Password=postgres";
        builder.Configuration["ConnectionStrings:DISTRIBUTED-CACHE"] = "localhost:28298";
        builder.Configuration["IS_TEST_ENVIRONMENT"] = "false";

        // Act
        builder.AddNexusDefaultHealthChecks();
        var host = builder.Build();

        var options = host.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;

        // Assert
        options.Registrations.Should().Contain(r => r.Name == "database");
        options.Registrations.Should().Contain(r => r.Name == "redis");
    }

    [Fact]
    public async Task WriteJsonResponse_ProducesCorrectJsonStructure()
    {
        // Arrange
        var entries = new Dictionary<string, HealthReportEntry>
        {
            ["database"] = new(
                HealthStatus.Healthy,
                "Database is operational",
                TimeSpan.FromMilliseconds(15),
                null,
                null),
            ["redis"] = new(
                HealthStatus.Degraded,
                "Latency spike",
                TimeSpan.FromMilliseconds(250),
                null,
                null)
        };

        var report = new HealthReport(entries, TimeSpan.FromMilliseconds(265));

        var context = new DefaultHttpContext();
        using var stream = new MemoryStream();
        context.Response.Body = stream;

        // Act
        await HealthCheckExtensions.WriteJsonResponse(context, report);

        // Assert
        stream.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(stream);
        string json = await reader.ReadToEndAsync();

        json.Should().NotBeNullOrWhiteSpace();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("status").GetString().Should().Be("Degraded");
        root.GetProperty("entries").TryGetProperty("database", out var dbElem).Should().BeTrue();
        dbElem.GetProperty("status").GetString().Should().Be("Healthy");
        root.GetProperty("entries").TryGetProperty("redis", out var redisElem).Should().BeTrue();
        redisElem.GetProperty("status").GetString().Should().Be("Degraded");
    }
}
