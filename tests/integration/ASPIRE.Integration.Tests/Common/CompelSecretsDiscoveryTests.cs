// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using Microsoft.Extensions.Configuration;

namespace ASPIRE.Tests.Common;

public sealed class CompelSecretsDiscoveryTests
{
    [Fact]
    public void CompelSecretsDiscovery_CompelPath_ResolvedFromGameServerCompelPathKey()
    {
        // Arrange
        Dictionary<string, string?> inMemorySettings = new() { { "GameServer:CompelPath", @"C:\Games\COMPEL" } };

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        // Act
        string? resolvedPath = configuration.GetValue<string?>("GameServer:CompelPath") ??
                               configuration.GetValue<string?>("compel-path") ??
                               configuration.GetValue<string?>("Parameters:compel-path");

        // Assert
        Assert.Equal(@"C:\Games\COMPEL", resolvedPath);
    }

    [Fact]
    public void CompelSecretsDiscovery_CompelPath_ResolvedFromUserSecretsCompelPathKey()
    {
        // Arrange
        Dictionary<string, string?> inMemorySettings = new() { { "compel-path", @"C:\UserSecrets\COMPEL" } };

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        // Act
        string? resolvedPath = configuration.GetValue<string?>("GameServer:CompelPath") ??
                               configuration.GetValue<string?>("compel-path") ??
                               configuration.GetValue<string?>("Parameters:compel-path");

        // Assert
        Assert.Equal(@"C:\UserSecrets\COMPEL", resolvedPath);
    }

    [Fact]
    public void CompelSecretsDiscovery_CompelPath_ResolvedFromParametersCompelPathKey()
    {
        // Arrange
        Dictionary<string, string?> inMemorySettings = new() { { "Parameters:compel-path", @"C:\PKD\COMPEL" } };

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        // Act
        string? resolvedPath = configuration.GetValue<string?>("GameServer:CompelPath") ??
                               configuration.GetValue<string?>("compel-path") ??
                               configuration.GetValue<string?>("Parameters:compel-path");

        // Assert
        Assert.Equal(@"C:\PKD\COMPEL", resolvedPath);
    }
}
