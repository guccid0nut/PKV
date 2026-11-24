// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Store;
using MERRICK.DatabaseContext.Persistence;
using MERRICK.DatabaseContext.Repositories;
using MERRICK.DatabaseContext.Tests.Infrastructure;
using NEXUS.Contracts.Domain;
using Xunit;

namespace MERRICK.DatabaseContext.Tests;

public class StoreRepositoryConcurrencyTests
{
    [Fact]
    public async Task StoreRepository_ConcurrentAccess_SynchronizesAcrossParallelTasksWithoutCollisions()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext seedDb = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            seedDb.StoreItems.AddRange(
                new StoreItem { Id = 1, Name = "Item 1", Code = "Item.1", LocalContent = "item_1", Enabled = true, Purchasable = true },
                new StoreItem { Id = 2, Name = "Item 2", Code = "Item.2", LocalContent = "item_2", Enabled = true, Purchasable = true },
                new StoreItem { Id = 3, Name = "Item 3", Code = "Item.3", LocalContent = "item_3", Enabled = true, Purchasable = false },
                new StoreItem { Id = 4, Name = "Item 4", Code = "Item.4", LocalContent = "item_4", Enabled = false, Purchasable = false }
            );
            await seedDb.SaveChangesAsync();
        }

        using MerrickContext sharedDb = TestDbContextFactory.CreateInMemoryContext(dbName);
        using StoreRepository repo = new(sharedDb);

        const int concurrency = 25;
        Task<object?>[] tasks = new Task<object?>[concurrency];

        for (int i = 0; i < concurrency; i++)
        {
            int index = i;
            tasks[i] = Task.Run(async () =>
            {
                return (index % 5) switch
                {
                    0 => (object?)await repo.GetStoreCatalogAsync(),
                    1 => await repo.GetEnabledProductsAsync(),
                    2 => await repo.GetPurchasableProductsAsync(),
                    3 => await repo.GetProductByIdAsync(1),
                    _ => await repo.GetProductByCodeAsync("Item.2")
                };
            });
        }

        object?[] results = await Task.WhenAll(tasks);
        results.Should().HaveCount(concurrency);
        results.Should().AllSatisfy(r => r.Should().NotBeNull());
    }

    [Fact]
    public async Task ClanRepository_ConcurrentAccess_SynchronizesAcrossParallelTasksWithoutCollisions()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext seedDb = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            Clan clan = new() { ID = 10, Name = "AlphaClan", Tag = "ALPHA" };
            seedDb.Clans.Add(clan);
            await seedDb.SaveChangesAsync();
        }

        using MerrickContext sharedDb = TestDbContextFactory.CreateInMemoryContext(dbName);
        using ClanRepository repo = new(sharedDb);

        const int concurrency = 25;
        Task<Clan?>[] tasks = new Task<Clan?>[concurrency];

        for (int i = 0; i < concurrency; i++)
        {
            int index = i;
            tasks[i] = Task.Run(async () =>
            {
                return (index % 3) switch
                {
                    0 => await repo.GetByIdAsync(10),
                    1 => await repo.GetByNameAsync("alphaclan"),
                    _ => await repo.GetByTagAsync("alpha")
                };
            });
        }

        Clan?[] results = await Task.WhenAll(tasks);
        results.Should().HaveCount(concurrency);
        results.Should().AllSatisfy(r =>
        {
            r.Should().NotBeNull();
            r!.ID.Should().Be(10);
            r.Name.Should().Be("AlphaClan");
        });
    }

    [Fact]
    public void StoreRepository_Dispose_CanBeCalledMultipleTimesSafely()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        StoreRepository repo = new(db);
        repo.Dispose();
        repo.Dispose();
    }

    [Fact]
    public void ClanRepository_Dispose_CanBeCalledMultipleTimesSafely()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        ClanRepository repo = new(db);
        repo.Dispose();
        repo.Dispose();
    }
}
