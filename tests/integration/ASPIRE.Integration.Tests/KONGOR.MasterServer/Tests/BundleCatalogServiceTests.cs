// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json;
using KONGOR.MasterServer.Models.RequestResponse.Store;
using KONGOR.MasterServer.Services.Store;
using Microsoft.Extensions.Logging.Abstractions;
using MERRICK.DatabaseContext.Entities.Store;

namespace ASPIRE.Tests.KONGOR.MasterServer.Tests;

public sealed class BundleCatalogServiceTests

{
    [Fact]
    public void GetBundleContent_HeroGrabBag_ResolvesAvatarsAndGrabBagFlag()

    {
        // Arrange

        BundleCatalogService service = new(NullLogger<BundleCatalogService>.Instance);

        const int bubblesGrabBagId = 4550; // Bubbles Grab Bag


        // Act

        BundleContentResponse response = service.GetBundleContent(bubblesGrabBagId, null);


        // Assert

        Assert.NotNull(response);

        Assert.NotNull(response.Info);

        Assert.NotNull(response.List);

        Assert.Equal(bubblesGrabBagId, response.Info.BundleID);

        Assert.True(response.Info.GrabBag);

        Assert.Equal(1, response.Info.PrizeQuantity);

        Assert.NotEmpty(response.List);

        Assert.All(response.List,
            item => Assert.StartsWith("aa.Hero_Bubbles.", item.ItemCode, StringComparison.OrdinalIgnoreCase));
    }


    [Fact]
    public void GetBundleContent_SwiftbladeGrabBag_ResolvesHiroAvatars()

    {
        // Arrange

        BundleCatalogService service = new(NullLogger<BundleCatalogService>.Instance);

        const int swiftbladeGrabBagId = 4549; // Swiftblade Grab Bag


        // Act

        BundleContentResponse response = service.GetBundleContent(swiftbladeGrabBagId, null);


        // Assert

        Assert.NotNull(response);

        Assert.True(response.Info.GrabBag);

        Assert.NotEmpty(response.List);

        Assert.All(response.List,
            item => Assert.StartsWith("aa.Hero_Hiro.", item.ItemCode, StringComparison.OrdinalIgnoreCase));
    }


    [Fact]
    public void GetBundleContent_HalloweenBundle_ResolvesExactFiveHalloweenAvatars()

    {
        // Arrange

        BundleCatalogService service = new(NullLogger<BundleCatalogService>.Instance);

        const int halloweenBundleId = 2609; // Halloween Party Bundle


        // Act

        BundleContentResponse response = service.GetBundleContent(halloweenBundleId, null);


        // Assert

        Assert.NotNull(response);

        Assert.NotNull(response.Info);

        Assert.Equal(halloweenBundleId, response.Info.BundleID);

        Assert.False(response.Info.GrabBag);

        Assert.Equal(1900, response.Info.GoldCoins);

        Assert.Equal(11000, response.Info.SilverCoins);

        Assert.Equal(5, response.List.Count);


        // Verify the 5 constituent avatars

        Assert.Contains(response.List, item => item.ProductID == 2450); // Ellie Envy

        Assert.Contains(response.List, item => item.ProductID == 2452); // Bonnie Blood Hunter

        Assert.Contains(response.List, item => item.ProductID == 2448); // Preda Tori

        Assert.Contains(response.List, item => item.ProductID == 2454); // Lynn X

        Assert.Contains(response.List, item => item.ProductID == 2597); // Thriller Rhapsody
    }


    [Fact]
    public void GetBundleContent_SupportWardBundle_WhenUserOwnsAllWards_ReturnsAllOwnedAndZeroPurchasable()

    {
        // Arrange

        BundleCatalogService service = new(NullLogger<BundleCatalogService>.Instance);

        const int supportWardBundleId = 2692;


        Account account = new()

        {
            Name = "TestUser",

            IsMain = true,

            User = new User

            {
                EmailAddress = "test@example.com",

                Role = new Role { Name = "User" },

                SRPPasswordSalt = "salt",

                SRPPasswordHash = "hash",

                InventoryItems =
                [
                    new UserInventory { ItemCode = "w.chronos_ward", GrantSource = "Test" },
                    new UserInventory { ItemCode = "w.wildsoul_ward", GrantSource = "Test" },
                    new UserInventory { ItemCode = "w.plague_rider_ward", GrantSource = "Test" },
                    new UserInventory { ItemCode = "w.rally_ward", GrantSource = "Test" },
                    new UserInventory { ItemCode = "w.wretched_hag_ward", GrantSource = "Test" },
                    new UserInventory { ItemCode = "w.lookout_ward", GrantSource = "Test" },
                    new UserInventory { ItemCode = "w.vizmo_ward", GrantSource = "Test" },
                    new UserInventory { ItemCode = "w.sentry_ward", GrantSource = "Test" }
                ]
            }
        };


        // Act

        BundleContentResponse response = service.GetBundleContent(supportWardBundleId, account);


        // Assert

        Assert.NotNull(response);

        Assert.True(response.Info.IsOwned);

        Assert.Equal(0, response.Info.MaxPurchasable);

        Assert.Equal(8, response.List.Count);

        Assert.All(response.List, item => Assert.True(item.IsOwned));
    }


    [Fact]
    public void GetBundleContent_SupportWardBundle_WhenUserOwnsSomeWards_MarksOnlyThoseAsOwned()

    {
        // Arrange

        BundleCatalogService service = new(NullLogger<BundleCatalogService>.Instance);

        const int supportWardBundleId = 2692;


        Account account = new()

        {
            Name = "TestUser",

            IsMain = true,

            User = new User

            {
                EmailAddress = "test@example.com",

                Role = new Role { Name = "User" },

                SRPPasswordSalt = "salt",

                SRPPasswordHash = "hash",

                InventoryItems =
                [
                    new UserInventory { ItemCode = "w.chronos_ward", GrantSource = "Test" },
                    new UserInventory { ItemCode = "w.wildsoul_ward", GrantSource = "Test" }
                ]
            }
        };


        // Act

        BundleContentResponse response = service.GetBundleContent(supportWardBundleId, account);


        // Assert

        Assert.NotNull(response);

        Assert.False(response.Info.IsOwned);

        Assert.Equal(1, response.Info.MaxPurchasable);

        Assert.Equal(8, response.List.Count);

        Assert.Equal(2, response.List.Count(x => x.IsOwned));

        Assert.Equal(6, response.List.Count(x => !x.IsOwned));
    }


    [Fact]
    public void GetBundleContent_Serialization_ProducesValidJsonStructure()

    {
        // Arrange

        BundleCatalogService service = new(NullLogger<BundleCatalogService>.Instance);

        BundleContentResponse response = service.GetBundleContent(4689, null);


        // Act

        string json = JsonSerializer.Serialize(response);


        // Assert

        Assert.Contains("\"info\":", json);

        Assert.Contains("\"bundle_id\":", json);

        Assert.Contains("\"grab_bag\":", json);

        Assert.Contains("\"list\":", json);

        Assert.Contains("\"product_id\":", json);
    }
}
