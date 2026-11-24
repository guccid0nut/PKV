// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MERRICK.DatabaseContext.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace Grpc.Challenger.Tests.Infrastructure;

public static class TestDbContextFactory
{
    private static readonly InMemoryDatabaseRoot SharedRoot = new();
    public static readonly IModel SharedModel;

    static TestDbContextFactory()
    {
        DbContextOptionsBuilder<MerrickContext> builder = new();
        builder.UseInMemoryDatabase("PrecompiledModelInitDb_GrpcTests", SharedRoot);
        using MerrickContext ctx = new(builder.Options);
        SharedModel = ctx.Model;
    }

    public static DbContextOptions<MerrickContext> CreateOptions(string? dbName = null)
    {
        return new DbContextOptionsBuilder<MerrickContext>()
            .UseModel(SharedModel)
            .UseInMemoryDatabase(dbName ?? Guid.NewGuid().ToString("N"), SharedRoot)
            .EnableSensitiveDataLogging(false)
            .Options;
    }

    public static MerrickContext CreateInMemoryContext(string? dbName = null)
    {
        return new MerrickContext(CreateOptions(dbName));
    }
}
