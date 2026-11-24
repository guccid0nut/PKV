// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using MERRICK.DatabaseContext.Entities;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Statistics;
using MERRICK.DatabaseContext.Extensions;
using MERRICK.DatabaseContext.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace MERRICK.DatabaseContext.Tests;

public sealed class NpgsqlDataSourcePoolingTests
{
    [Fact]
    public void AddMerrickDatabase_WhenPostgresConfigured_RegistersSingletonNpgsqlDataSourceWithPoolLimits()
    {
        // Arrange
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Configuration["Database:Provider"] = "PostgreSQL";
        builder.Configuration["Database:PostgreSQL:ConnectionString"] =
            "Host=localhost;Port=5432;Database=kongor_test;Username=postgres;Password=postgres;";

        // Act
        builder.AddMerrickDatabase();
        using WebApplication app = builder.Build();

        // Assert
        NpgsqlDataSource? dataSource = app.Services.GetService<NpgsqlDataSource>();
        dataSource.Should().NotBeNull("NpgsqlDataSource should be registered as a singleton service in DI");
        dataSource!.ConnectionString.Should().Contain("Host=localhost");
    }

    [Fact]
    public void MerrickContext_OnModelCreating_WhenNpgsqlProvider_ConfiguresDeclarativeIndexesAndFilters()
    {
        // Arrange
        DbContextOptionsBuilder<MerrickContext> optionsBuilder = new();
        optionsBuilder.UseNpgsql("Host=localhost;Database=kongor_model_test;Username=postgres;Password=postgres;");

        // Act
        using MerrickContext context = new(optionsBuilder.Options);
        IModel model = context.Model;

        // Assert 1: Filtered index IX_HostApplications_Approved
        IEntityType? hostAppEntity = model.FindEntityType(typeof(HostApplication));
        hostAppEntity.Should().NotBeNull();
        IIndex? approvedIdx = hostAppEntity!.GetIndexes().FirstOrDefault(i => i.GetDatabaseName() == "IX_HostApplications_Approved");
        approvedIdx.Should().NotBeNull("IX_HostApplications_Approved should be declared on HostApplication");
        approvedIdx!.GetFilter().Should().Be("\"Status\" = 2");

        // Assert 2: GIN Trigram index on core.Accounts (Name)
        IEntityType? accountEntity = model.FindEntityType(typeof(Account));
        accountEntity.Should().NotBeNull();
        IIndex? accountTrgmIdx = accountEntity!.GetIndexes().FirstOrDefault(i => i.GetDatabaseName() == "IX_Accounts_Name_Trgm");
        accountTrgmIdx.Should().NotBeNull("IX_Accounts_Name_Trgm should be configured on Account");
        accountTrgmIdx!.GetMethod().Should().Be("gin");

        // Assert 3: GIN Trigram index on core.Clans (Name)
        IEntityType? clanEntity = model.FindEntityType(typeof(Clan));
        clanEntity.Should().NotBeNull();
        IIndex? clanTrgmIdx = clanEntity!.GetIndexes().FirstOrDefault(i => i.GetDatabaseName() == "IX_Clans_Name_Trgm");
        clanTrgmIdx.Should().NotBeNull("IX_Clans_Name_Trgm should be configured on Clan");
        clanTrgmIdx!.GetMethod().Should().Be("gin");

        // Assert 4: GIN Trigram index on core.Users (DiscordUsername)
        IEntityType? userEntity = model.FindEntityType(typeof(User));
        userEntity.Should().NotBeNull();
        IIndex? userTrgmIdx = userEntity!.GetIndexes().FirstOrDefault(i => i.GetDatabaseName() == "IX_Users_DiscordUsername_Trgm");
        userTrgmIdx.Should().NotBeNull("IX_Users_DiscordUsername_Trgm should be configured on User");
        userTrgmIdx!.GetMethod().Should().Be("gin");

        // Assert 5: BRIN index on stat.MatchStatistics (TimestampRecorded)
        IEntityType? matchStatsEntity = model.FindEntityType(typeof(MatchStatistics));
        matchStatsEntity.Should().NotBeNull();
        IIndex? brinIdx = matchStatsEntity!.GetIndexes().FirstOrDefault(i => i.GetDatabaseName() == "IX_MatchStatistics_TimestampRecorded_Brin");
        brinIdx.Should().NotBeNull("IX_MatchStatistics_TimestampRecorded_Brin should be configured on MatchStatistics");
        brinIdx!.GetMethod().Should().Be("brin");
    }

    [Fact]
    public void MerrickContext_OnModelCreating_WhenInMemoryProvider_FallsBackCleanlyWithoutNpgsqlIndexes()
    {
        // Arrange
        DbContextOptionsBuilder<MerrickContext> optionsBuilder = new();
        optionsBuilder.UseInMemoryDatabase(Guid.NewGuid().ToString("N"));

        // Act & Assert
        using MerrickContext context = new(optionsBuilder.Options);
        IModel model = context.Model;

        IEntityType? accountEntity = model.FindEntityType(typeof(Account));
        accountEntity.Should().NotBeNull();
        // In-memory provider should have standard index IX_Accounts_Name but NOT Npgsql-specific GIN method
        IIndex? standardNameIdx = accountEntity!.GetIndexes().FirstOrDefault(i => i.GetDatabaseName() == "IX_Accounts_Name");
        standardNameIdx.Should().NotBeNull();
        IIndex? accountTrgmIdx = accountEntity.GetIndexes().FirstOrDefault(i => i.GetDatabaseName() == "IX_Accounts_Name_Trgm");
        accountTrgmIdx.Should().BeNull("GIN Trigram index should not be registered under InMemory provider");
    }

    [Fact]
    public void AddMerrickDatabase_WhenSupabaseSessionPooler_CapsPoolSizeAndIdleLifetimeAndSharesHealthCheckDataSource()
    {
        // Arrange: Supavisor Session Mode (port 5432 on *.pooler.supabase.com) has a shared pool_size: 15 limit
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Configuration["Database:Provider"] = "PostgreSQL";
        builder.Configuration["SUPABASE_DB_CONNECTION_STRING"] =
            "postgresql://postgres.projref@aws-0-us-west-1.pooler.supabase.com:6543/postgres";

        // Act
        builder.AddMerrickDatabase();
        using WebApplication app = builder.Build();

        // Assert
        NpgsqlDataSource? dataSource = app.Services.GetService<NpgsqlDataSource>();
        dataSource.Should().NotBeNull();
        NpgsqlConnectionStringBuilder csb = new(dataSource!.ConnectionString);
        csb.Port.Should().Be(5432, "Supavisor URI without pgbouncer=true normalizes to session mode port 5432");
        csb.MaxPoolSize.Should().Be(3, "Session mode per-service MaxPoolSize must stay tightly capped to prevent EMAXCONNSESSION");
        csb.MinPoolSize.Should().Be(1, "MinPoolSize must stay at 1 with KeepAlive=30 to keep one warm socket and avoid cold SCRAM handshakes");
        csb.KeepAlive.Should().Be(30);
        csb.ConnectionIdleLifetime.Should().Be(30);
        csb.ConnectionPruningInterval.Should().Be(10);
    }
}
