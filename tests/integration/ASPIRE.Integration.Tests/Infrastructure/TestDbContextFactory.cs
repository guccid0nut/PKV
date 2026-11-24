// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace ASPIRE.Tests.Infrastructure;

/// <summary>
///     Provides shared EF Core in-memory database context and options
///     to reuse the compiled model and internal provider cache across test runs.
/// </summary>
public static class TestDbContextFactory
{
    private static readonly InMemoryDatabaseRoot SharedRoot = new();
    public static readonly IModel SharedModel;

    static TestDbContextFactory()
    {
        DbContextOptionsBuilder<MerrickContext> builder = new();
        builder.UseInMemoryDatabase("PrecompiledModelInitDb", SharedRoot);
        using MerrickContext ctx = new(builder.Options);
        SharedModel = ctx.Model;
    }

    public static bool IsContainerAvailable => PostgreSqlContainerFixture.Shared.IsAvailable;
    public static string? ConnectionString => PostgreSqlContainerFixture.Shared.ConnectionString;

    public static DbContextOptions<MerrickContext> CreateOptions(string? dbName = null)
    {
        return new DbContextOptionsBuilder<MerrickContext>()
            .UseModel(SharedModel)
            .UseInMemoryDatabase(dbName ?? Guid.NewGuid().ToString("N"), SharedRoot)
            .EnableSensitiveDataLogging(false)
            .Options;
    }

    public static DbContextOptions<MerrickContext> CreatePostgreSqlOptions(string? schemaName = null)
    {
        DbContextOptionsBuilder<MerrickContext> builder = new();
        if (PostgreSqlContainerFixture.Shared.IsAvailable && !string.IsNullOrWhiteSpace(PostgreSqlContainerFixture.Shared.ConnectionString))
        {
            builder.UseNpgsql(PostgreSqlContainerFixture.Shared.ConnectionString);
        }
        else
        {
            builder.UseModel(SharedModel)
                .UseInMemoryDatabase(schemaName ?? Guid.NewGuid().ToString("N"), SharedRoot);
        }
        return builder.Options;
    }

    public static MerrickContext CreateContext(string? dbName = null)
    {
        return new MerrickContext(CreateOptions(dbName));
    }

    public static MerrickContext CreateInMemoryContext(string? dbName = null)
    {
        return new MerrickContext(CreateOptions(dbName));
    }
}
