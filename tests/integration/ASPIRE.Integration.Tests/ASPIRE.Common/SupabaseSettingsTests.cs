// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Constants;
using ASPIRE.Common.Extensions;
using ASPIRE.Common.Models.Supabase;
using Microsoft.Extensions.Configuration;

namespace ASPIRE.Tests.ASPIRE.Common;

public sealed class SupabaseSettingsTests
{
    [Fact]
    public void SupabaseSettings_GetPublicObjectUrl_ReturnsCanonicalPublicUrl()
    {
        // Arrange
        SupabaseSettings settings = new() { Url = "https://mytestproject.supabase.co", StorageBucket = "replays" };

        // Act
        string publicUrl = settings.GetPublicObjectUrl("M12345.honreplay");

        // Assert
        Assert.Equal("https://mytestproject.supabase.co/storage/v1/object/public/replays/M12345.honreplay", publicUrl);
    }

    [Fact]
    public void SupabaseSettings_GetRestStorageEndpoint_ReturnsCanonicalUploadUrl()
    {
        // Arrange
        SupabaseSettings settings = new() { Url = "https://mytestproject.supabase.co/", StorageBucket = "replays" };

        // Act
        string uploadUrl = settings.GetRestStorageEndpoint();

        // Assert
        Assert.Equal("https://mytestproject.supabase.co/storage/v1/object/replays", uploadUrl);
    }

    [Fact]
    public void ConfigurationExtensions_GetDatabaseProvider_DefaultsToPostgreSql()
    {
        // Arrange
        IConfiguration config = new ConfigurationBuilder().Build();

        // Act
        string provider = config.GetDatabaseProvider();

        // Assert
        Assert.Equal(ConfigurationConstants.ProviderPostgreSql, provider);
        Assert.True(config.IsPostgreSqlOrSupabase());
    }

    [Theory]
    [InlineData("PostgreSQL", true)]
    [InlineData("postgresql", true)]
    [InlineData("Supabase", true)]
    [InlineData("supabase", true)]
    [InlineData("SqlServer", false)]
    [InlineData("sqlserver", false)]
    public void ConfigurationExtensions_IsPostgreSqlOrSupabase_MatchesCorrectly(string providerName, bool expected)
    {
        // Arrange
        Dictionary<string, string?> inMemory = new() { ["Database:Provider"] = providerName };
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(inMemory).Build();

        // Act
        bool result = config.IsPostgreSqlOrSupabase();

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ConfigurationExtensions_GetSupabaseSettings_BindsCorrectly()
    {
        // Arrange
        Dictionary<string, string?> inMemory = new()
        {
            ["Supabase:Url"] = "https://nexus.supabase.co",
            ["Supabase:AnonKey"] = "anon-key-sample",
            ["Supabase:ServiceRoleKey"] = "service-role-sample",
            ["Supabase:StorageBucket"] = "custom-replays",
            ["Supabase:DatabaseConnectionString"] = "Host=db.nexus.supabase.co;Port=6543;Database=postgres;"
        };
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(inMemory).Build();

        // Act
        SupabaseSettings settings = config.GetSupabaseSettings();

        // Assert
        Assert.Equal("https://nexus.supabase.co", settings.Url);
        Assert.Equal("anon-key-sample", settings.AnonKey);
        Assert.Equal("service-role-sample", settings.ServiceRoleKey);
        Assert.Equal("custom-replays", settings.StorageBucket);
        Assert.Equal("Host=db.nexus.supabase.co;Port=6543;Database=postgres;", settings.DatabaseConnectionString);
    }
}
