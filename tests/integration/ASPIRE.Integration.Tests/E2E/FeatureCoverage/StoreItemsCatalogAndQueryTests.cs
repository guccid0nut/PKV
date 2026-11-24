// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: Feature11_StoreItemsCatalogAndQueryTests.cs
// System: ProjectKongor Solution-Wide E2E Testing Suite
// Tier 1: Feature Coverage (Feature 11: data.StoreItems Table & Catalog Queries)
// Policy: Strict Explicit Typing, Opaque-Box Validation (>= 5 tests)
// ============================================================================

using FluentAssertions;
using MERRICK.DatabaseContext.Entities.Store;

namespace ASPIRE.Tests.E2E.Tier1_FeatureCoverage;

public sealed class StoreItemsCatalogAndQueryTests
{
    private static MerrickContext CreateInMemoryContext(string? dbName = null)
    {
        return TestDbContextFactory.CreateInMemoryContext(dbName ?? "StoreItems_" + Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public async Task StoreItems_AddAndRetrieveCatalogEntry_SavesAllFieldsAccurately()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryContext();
        StoreItem item = new()
        {
            Name = "Valkyrie Arctic Frost",
            Code = "Hero_Valkyrie.Alt4",
            UpgradeId = 101,
            UpgradeType = 1,
            LocalContent = "hero/valkyrie/alt4",
            Purchasable = true,
            Price = 650,
            PriceInMMP = 3500,
            Premium = true,
            Enabled = true,
            IsBundle = false,
            ProductsRequired = null
        };

        // Act
        db.StoreItems.Add(item);
        await db.SaveChangesAsync();

        // Assert
        StoreItem? retrieved =
            await db.StoreItems.AsNoTracking().FirstOrDefaultAsync(i => i.Code == "Hero_Valkyrie.Alt4");
        retrieved.Should().NotBeNull();
        retrieved!.Id.Should().BeGreaterThan(0);
        retrieved.Name.Should().Be("Valkyrie Arctic Frost");
        retrieved.Price.Should().Be(650);
        retrieved.PriceInMMP.Should().Be(3500);
        retrieved.Premium.Should().BeTrue();
        retrieved.Enabled.Should().BeTrue();
        retrieved.Purchasable.Should().BeTrue();
    }

    [Fact]
    public async Task StoreItems_GetStoreCatalogAsync_ReturnsOnlyConfiguredCatalogItems()
    {
        // Arrange
        string dbName = "Catalog_" + Guid.NewGuid().ToString("N");
        using (MerrickContext seedDb = CreateInMemoryContext(dbName))
        {
            seedDb.StoreItems.AddRange(
                new StoreItem
                {
                    Name = "Flint Boomstick", Code = "Hero_Flint.Alt1", LocalContent = "hero/flint/alt1", Price = 400,
                    PriceInMMP = 2000, Purchasable = true, Enabled = true
                },
                new StoreItem
                {
                    Name = "Devourer Clown", Code = "Hero_Devourer.Alt2", LocalContent = "hero/devourer/alt2",
                    Price = 500, PriceInMMP = 2500, Purchasable = true, Enabled = true
                },
                new StoreItem
                {
                    Name = "Hidden Courier", Code = "Courier.Secret", LocalContent = "couriers/secret", Price = 1000,
                    PriceInMMP = 5000, Purchasable = false, Enabled = false
                }
            );
            await seedDb.SaveChangesAsync();
        }

        using MerrickContext queryDb = CreateInMemoryContext(dbName);
        InventoryRepository repository = new(queryDb);

        // Act
        IReadOnlyList<StoreItem> catalog = await repository.GetStoreCatalogAsync();

        // Assert
        catalog.Should().HaveCount(3);
        catalog.Select(c => c.Code).Should().Contain(["Hero_Flint.Alt1", "Hero_Devourer.Alt2", "Courier.Secret"]);
    }

    [Fact]
    public async Task StoreItems_GetStoreItemByCodeAsync_MatchesExactOrNameCaseInsensitively()
    {
        // Arrange
        string dbName = "CodeLookup_" + Guid.NewGuid().ToString("N");
        using (MerrickContext seedDb = CreateInMemoryContext(dbName))
        {
            seedDb.StoreItems.Add(new StoreItem
            {
                Name = "Pebbles Mountain King",
                Code = "Hero_Pebbles.Alt1",
                LocalContent = "hero/pebbles/alt1",
                Price = 550,
                PriceInMMP = 2800,
                Purchasable = true,
                Enabled = true
            });
            await seedDb.SaveChangesAsync();
        }

        using MerrickContext queryDb = CreateInMemoryContext(dbName);
        InventoryRepository repository = new(queryDb);

        // Act
        StoreItem? byCode = await repository.GetStoreItemByCodeAsync("Hero_Pebbles.Alt1");
        StoreItem? byName = await repository.GetStoreItemByCodeAsync("Pebbles Mountain King");

        // Assert
        byCode.Should().NotBeNull();
        byCode!.Code.Should().Be("Hero_Pebbles.Alt1");
        byName.Should().NotBeNull();
        byName!.Name.Should().Be("Pebbles Mountain King");
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task StoreItems_BundleProductsRequired_StoresAndRetrievesCsvCorrectly()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange
        using MerrickContext db = CreateInMemoryContext();
        StoreItem bundle = new()
        {
            Name = "Mega Mid Wars Bundle",
            Code = "Bundle.MidWars2026",
            UpgradeId = 0,
            UpgradeType = 0,
            LocalContent = "bundles/midwars",
            Purchasable = true,
            Price = 1500,
            PriceInMMP = 8000,
            Premium = true,
            Enabled = true,
            IsBundle = true,
            ProductsRequired = "Hero_Pyromancer.Alt2,Hero_WitchSlayer.Alt1,Announcer.Badass"
        };

        // Act
        db.StoreItems.Add(bundle);
        await db.SaveChangesAsync();

        // Assert
        StoreItem? retrieved =
            await db.StoreItems.AsNoTracking().FirstOrDefaultAsync(i => i.Code == "Bundle.MidWars2026");
        retrieved.Should().NotBeNull();
        retrieved!.IsBundle.Should().BeTrue();
        retrieved.ProductsRequired.Should().NotBeNull();
        string[] requiredItems = retrieved.ProductsRequired!.Split(',');
        requiredItems.Should().HaveCount(3);
        requiredItems.Should().Contain(["Hero_Pyromancer.Alt2", "Hero_WitchSlayer.Alt1", "Announcer.Badass"]);
    }

    [Fact]
    public async Task StoreItems_DisabledAndNonPurchasableItems_AreFilterableViaLinq()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryContext();
        db.StoreItems.AddRange(
            new StoreItem
            {
                Name = "Active Item", Code = "Item.Active", LocalContent = "items/active", Price = 100,
                Purchasable = true, Enabled = true
            },
            new StoreItem
            {
                Name = "Disabled Item", Code = "Item.Disabled", LocalContent = "items/disabled", Price = 100,
                Purchasable = true, Enabled = false
            },
            new StoreItem
            {
                Name = "Promo Only Item", Code = "Item.Promo", LocalContent = "items/promo", Price = 0,
                Purchasable = false, Enabled = true
            }
        );
        await db.SaveChangesAsync();

        // Act
        List<StoreItem> purchasableActive = await db.StoreItems
            .AsNoTracking()
            .Where(i => i.Purchasable && i.Enabled)
            .ToListAsync();

        // Assert
        purchasableActive.Should().HaveCount(1);
        purchasableActive.First().Code.Should().Be("Item.Active");
    }

    [Fact]
    public async Task StoreItems_EmptyCatalog_ReturnsEmptyListWithoutException()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryContext();
        InventoryRepository repository = new(db);

        // Act
        IReadOnlyList<StoreItem> catalog = await repository.GetStoreCatalogAsync();

        // Assert
        catalog.Should().BeEmpty();
    }
}
