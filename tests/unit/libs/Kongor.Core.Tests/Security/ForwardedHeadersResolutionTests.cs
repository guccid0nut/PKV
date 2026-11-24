// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net;
using ASPIRE.Common.ServiceDefaults;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Kongor.Core.Tests.Security;

public sealed class ForwardedHeadersResolutionTests
{
    [Fact]
    public void ConfigureForwardedHeaders_WhenProxyIsDirectIp_ParsesDirectlyWithoutDns()
    {
        // Arrange
        ServiceCollection services = new();
        Dictionary<string, string?> inMemorySettings = new()
        {
            ["INFRASTRUCTURE_GATEWAY"] = "192.168.42.100"
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        // Act
        services.ConfigureInfrastructureForwardedHeaders(configuration);
        ServiceProvider provider = services.BuildServiceProvider();
        ForwardedHeadersOptions options = provider.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        // Assert
        options.KnownProxies.Should().Contain(IPAddress.Parse("192.168.42.100"));
        options.ForwardLimit.Should().Be(4);
    }

    [Fact]
    public void ConfigureForwardedHeaders_WhenProxyIsLocalhost_BindsLoopbacksWithoutDns()
    {
        // Arrange
        ServiceCollection services = new();
        Dictionary<string, string?> inMemorySettings = new()
        {
            ["INFRASTRUCTURE_GATEWAY"] = "localhost"
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        // Act
        services.ConfigureInfrastructureForwardedHeaders(configuration);
        ServiceProvider provider = services.BuildServiceProvider();
        ForwardedHeadersOptions options = provider.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        // Assert
        options.KnownProxies.Should().Contain(IPAddress.Loopback);
        options.KnownProxies.Should().Contain(IPAddress.IPv6Loopback);
    }

    [Fact]
    public void ConfigureForwardedHeaders_WhenProxyIsUnresolvableHost_DoesNotThrowFatalException()
    {
        // Arrange
        ServiceCollection services = new();
        Dictionary<string, string?> inMemorySettings = new()
        {
            ["INFRASTRUCTURE_GATEWAY"] = "non-existent-proxy-hostname-99999.invalid"
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        // Act & Assert (Should not throw InvalidOperationException)
        services.ConfigureInfrastructureForwardedHeaders(configuration);
        ServiceProvider provider = services.BuildServiceProvider();
        var act = () => provider.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        act.Should().NotThrow();
        ForwardedHeadersOptions options = act();
        options.ForwardLimit.Should().Be(4);
    }
}
