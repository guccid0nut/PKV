// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using MERRICK.DatabaseContext.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using ConfigurationExtensions = ASPIRE.Common.Extensions.ConfigurationExtensions;

namespace MERRICK.DatabaseContext.Tests;

public sealed class NpgsqlPgBouncerConfigurationTests
{
    [Fact]
    public void AddMerrickDatabase_WhenPgBouncerConfiguredViaPort6543_TunesPoolLimitsAndEnablesNoResetOnClose()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Configuration["Database:Provider"] = "PostgreSQL";
        builder.Configuration["Database:PostgreSQL:ConnectionString"] =
            "Host=aws-0-us-east-1.pooler.supabase.com;Port=6543;Database=postgres;Username=postgres.test;Password=secret;pool_mode=transaction;";

        builder.AddMerrickDatabase();
        using WebApplication app = builder.Build();

        NpgsqlDataSource? dataSource = app.Services.GetService<NpgsqlDataSource>();
        dataSource.Should().NotBeNull();

        NpgsqlConnectionStringBuilder csb = new(dataSource!.ConnectionString);
        csb.Port.Should().Be(6543);
        csb.MaxPoolSize.Should().Be(25, "PgBouncer transaction mode limits client virtual pool size to prevent backend saturation");
        csb.MinPoolSize.Should().Be(2);
        csb.NoResetOnClose.Should().BeTrue("PgBouncer transaction mode requires NoResetOnClose to avoid DISCARD ALL errors across pooled sockets");
    }

    [Fact]
    public void AddMerrickDatabase_WhenPgBouncerConfiguredViaFlag_TunesPoolLimits()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Configuration["Database:Provider"] = "PostgreSQL";
        builder.Configuration["Database:PostgreSQL:UsePgBouncer"] = "true";
        builder.Configuration["Database:PostgreSQL:ConnectionString"] =
            "Host=localhost;Port=5432;Database=kongor_test;Username=postgres;Password=postgres;";

        builder.AddMerrickDatabase();
        using WebApplication app = builder.Build();

        NpgsqlDataSource? dataSource = app.Services.GetService<NpgsqlDataSource>();
        dataSource.Should().NotBeNull();

        NpgsqlConnectionStringBuilder csb = new(dataSource!.ConnectionString);
        csb.MaxPoolSize.Should().Be(25);
        csb.MinPoolSize.Should().Be(2);
        csb.NoResetOnClose.Should().BeTrue();
    }

    [Fact]
    public void AddMerrickDatabase_WhenDirectPostgresConfigured_PreservesDefaultMaxPoolSize100()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Configuration["Database:Provider"] = "PostgreSQL";
        builder.Configuration["Database:PostgreSQL:ConnectionString"] =
            "Host=localhost;Port=5432;Database=kongor_test;Username=postgres;Password=postgres;";

        builder.AddMerrickDatabase();
        using WebApplication app = builder.Build();

        NpgsqlDataSource? dataSource = app.Services.GetService<NpgsqlDataSource>();
        dataSource.Should().NotBeNull();

        NpgsqlConnectionStringBuilder csb = new(dataSource!.ConnectionString);
        csb.MaxPoolSize.Should().Be(100, "Direct connections should retain standard 100-connection client pool");
        csb.MinPoolSize.Should().Be(5);
        csb.NoResetOnClose.Should().BeFalse();
    }

    [Fact]
    public void NormalizePostgreSqlConnectionString_WithSupabasePgBouncerUri_MaintainsPort6543AndAppendsNoResetOnClose()
    {
        string raw = "postgresql://postgres.myuser:secret123@aws-0-us-east-1.pooler.supabase.com:6543/postgres?pgbouncer=true";
        string normalized = ConfigurationExtensions.NormalizePostgreSqlConnectionString(raw);

        normalized.Should().Contain("Port=6543");
        normalized.Should().Contain("No Reset On Close=true");
        normalized.Should().Contain("SSL Mode=Require");
    }
}
