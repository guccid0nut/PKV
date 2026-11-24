// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Constants;
using ASPIRE.Common.Extensions;
using Microsoft.Extensions.Configuration;

namespace ASPIRE.Tests.ASPIRE.Common;

public sealed class ConfigurationExtensionsTests
{
    [Fact]
    public void GetMasterServerAddress_WithMasterServerSection_ReturnsConfiguredValue()
    {
        // Arrange
        Dictionary<string, string?> inMemorySettings = new()
        {
            [ConfigurationConstants.MasterServerAddressKey] = "https://api.kongor.net"
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        // Act
        string result = configuration.GetMasterServerAddress();

        // Assert
        Assert.Equal("https://api.kongor.net", result);
    }

    [Fact]
    public void GetMasterServerAddress_WithEnvironmentVariable_ReturnsConfiguredValue()
    {
        // Arrange
        Dictionary<string, string?> inMemorySettings = new()
        {
            [ConfigurationConstants.MasterServerAddressEnv] = "http://127.0.0.1:55555"
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        // Act
        string result = configuration.GetMasterServerAddress();

        // Assert
        Assert.Equal("http://127.0.0.1:55555", result);
    }

    [Fact]
    public void GetMasterServerAddress_WithInfrastructureGateway_BuildsHttpUrl()
    {
        // Arrange
        Dictionary<string, string?> inMemorySettings = new()
        {
            [ConfigurationConstants.InfrastructureGatewayKey] = "masterserver.newbs.io"
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        // Act
        string result = configuration.GetMasterServerAddress();

        // Assert
        Assert.Equal("http://masterserver.newbs.io", result);
    }

    [Fact]
    public void GetMasterServerAddress_WithEmptyConfiguration_ReturnsFallback()
    {
        // Arrange
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([])
            .Build();

        // Act
        string result = configuration.GetMasterServerAddress("http://localhost:55555");

        // Assert
        Assert.Equal("http://localhost:55555", result);
    }

    [Fact]
    public void GetInfrastructureGateway_WithInfrastructureSection_ReturnsGateway()
    {
        // Arrange
        Dictionary<string, string?> inMemorySettings = new()
        {
            [ConfigurationConstants.InfrastructureGatewayKey] = "gateway.kongor.net"
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        // Act
        string result = configuration.GetInfrastructureGateway();

        // Assert
        Assert.Equal("gateway.kongor.net", result);
    }

    [Fact]
    public void GetInfrastructureGateway_FromMasterServerAddressUrl_ExtractsHostAuthority()
    {
        // Arrange
        Dictionary<string, string?> inMemorySettings = new()
        {
            [ConfigurationConstants.MasterServerAddressKey] = "http://localhost:55555"
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        // Act
        string result = configuration.GetInfrastructureGateway();

        // Assert
        Assert.Equal("localhost:55555", result);
    }

    [Fact]
    public void GetInfrastructureGateway_WithNoConfig_ReturnsFallback()
    {
        // Arrange
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([])
            .Build();

        // Act
        string result = configuration.GetInfrastructureGateway("masterserver.newbs.io");

        // Assert
        Assert.Equal("masterserver.newbs.io", result);
    }
}
