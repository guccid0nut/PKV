// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using KONGOR.MasterServer.Services.Store;
using MERRICK.DatabaseContext.Entities.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KONGOR.MasterServer.Tests.Services;

public sealed class BundleCatalogServiceTests
{
    private readonly BundleCatalogService _service = new(NullLogger<BundleCatalogService>.Instance);

    [Fact]
    public void GetBundleContent_ForSuperDollBoxBundle4689_ReturnsConfiguredPlushieAvatars()
    {
        // Act
        var result = _service.GetBundleContent(4689, null);

        // Assert
        result.Should().NotBeNull();
        result.Info.BundleID.Should().Be(4689);
        result.List.Select(x => x.ProductID).Should().BeEquivalentTo([4593, 4613, 4621, 4636, 4640, 4654, 4658, 4666, 4670]);
    }

    [Fact]
    public void GetBundleContent_ForSuperDollBoxBundle4689_VerifiesGrabBagProperties()
    {
        // Act
        var result = _service.GetBundleContent(4689, null);

        // Assert
        result.Should().NotBeNull();
        result.Info.GrabBag.Should().BeTrue();
        result.Info.PrizeQuantity.Should().Be(1);
        result.Info.UiTheme.Should().Be("lucky_bundle");
        result.Info.Model.Should().Be("/ui/fe2/grabbag/lucky_bundle/footlocker.mdf");
        result.List.Should().HaveCount(9);
        result.List.Should().OnlyContain(item => !string.IsNullOrEmpty(item.ItemCode) && item.ProductID > 0);
    }

    [Fact]
    public void GetBundleContent_ForHalloweenBundle2609_ReturnsMappedItems()
    {
        // Act
        var result = _service.GetBundleContent(2609, null);

        // Assert
        result.Should().NotBeNull();
        result.Info.BundleID.Should().Be(2609);
        result.List.Select(x => x.ProductID).Should().BeEquivalentTo([2450, 2452, 2448, 2454, 2597]);
    }

    [Theory]
    [InlineData(2609, 5)]
    [InlineData(2692, 8)]
    [InlineData(2908, 6)]
    [InlineData(2842, 5)]
    [InlineData(2865, 5)]
    [InlineData(3054, 5)]
    [InlineData(3146, 5)]
    [InlineData(4689, 9)]
    public void GetBundleContent_ForAllKnownBundles_ResolvesExpectedItemCounts(int bundleId, int expectedCount)
    {
        // Act
        var result = _service.GetBundleContent(bundleId, null);

        // Assert
        result.Should().NotBeNull();
        result.Info.BundleID.Should().Be(bundleId);
        result.List.Should().NotBeNull();
        result.List.Should().HaveCount(expectedCount);
        result.List.Should().OnlyContain(item => item.ProductID > 0 && !string.IsNullOrEmpty(item.ItemCode));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-99999)]
    [InlineData(999999)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void GetBundleContent_ForNonExistentBundle_NeverThrowsAndReturnsFallbackAvatars(int invalidBundleId)
    {
        // Act
        var result = _service.GetBundleContent(invalidBundleId, null);

        // Assert
        result.Should().NotBeNull();
        result.Info.Should().NotBeNull();
        result.Info.BundleID.Should().Be(invalidBundleId);
        result.Info.GrabBag.Should().BeFalse();
        result.List.Should().NotBeNull();
        result.List.Should().NotBeEmpty();
        result.List.Count.Should().BeGreaterThanOrEqualTo(1);
        result.List.Should().OnlyContain(item => item.ProductID > 0 && !string.IsNullOrEmpty(item.ItemCode));
    }

    [Fact]
    public void GetBundleContent_WithAccountOwnership_CorrectlyComputesOwnershipAndMaxPurchasable()
    {
        // Arrange: User owns 2 of the 9 Plushie avatars
        var user = new User
        {
            EmailAddress = "challenger@kongor.net",
            SRPPasswordSalt = "salt",
            SRPPasswordHash = "hash",
            InventoryItems =
            [
                new UserInventory { ItemCode = "Hero_Behemoth.Alt13", Quantity = 1, IsPermanent = true },
                new UserInventory { ItemCode = "Hero_Devourer.Alt13", Quantity = 1, IsPermanent = true }
            ]
        };
        var account = new Account
        {
            Name = "ChallengerTest",
            User = user
        };

        // Act
        var result = _service.GetBundleContent(4689, account);

        // Assert
        result.Should().NotBeNull();
        result.Info.IsOwned.Should().BeFalse();
        result.Info.MaxPurchasable.Should().Be(7); // 9 total - 2 owned = 7 unowned
        result.List.Should().Contain(x => x.ProductID == 4593 && x.IsOwned); // Plushie Behe
        result.List.Should().Contain(x => x.ProductID == 4621 && x.IsOwned); // Plushie Devo
        result.List.Should().Contain(x => x.ProductID == 4613 && !x.IsOwned); // Plushie Night Hound (unowned)
    }

    [Fact]
    public void GetBundleContent_WhenUserOwnsAllBundleItems_SetsIsOwnedTrueAndMaxPurchasableZero()
    {
        // Arrange: User owns all 9 Plushie avatars
        var user = new User
        {
            EmailAddress = "allowner@kongor.net",
            SRPPasswordSalt = "salt",
            SRPPasswordHash = "hash",
            InventoryItems =
            [
                new UserInventory { ItemCode = "Hero_Behemoth.Alt13", Quantity = 1, IsPermanent = true },
                new UserInventory { ItemCode = "Hero_Hantumon.Alt11", Quantity = 1, IsPermanent = true },
                new UserInventory { ItemCode = "Hero_Devourer.Alt13", Quantity = 1, IsPermanent = true },
                new UserInventory { ItemCode = "Hero_Cthulhuphant.Alt7", Quantity = 1, IsPermanent = true },
                new UserInventory { ItemCode = "Hero_Pestilence.Alt9", Quantity = 1, IsPermanent = true },
                new UserInventory { ItemCode = "Hero_Deadwood.Alt8", Quantity = 1, IsPermanent = true },
                new UserInventory { ItemCode = "Hero_Panda.Alt10", Quantity = 1, IsPermanent = true },
                new UserInventory { ItemCode = "Hero_Hydromancer.Alt8", Quantity = 1, IsPermanent = true },
                new UserInventory { ItemCode = "Hero_Bubbles.Alt12", Quantity = 1, IsPermanent = true }
            ]
        };
        var account = new Account
        {
            Name = "AllOwnerTest",
            User = user
        };

        // Act
        var result = _service.GetBundleContent(4689, account);

        // Assert
        result.Should().NotBeNull();
        result.Info.IsOwned.Should().BeTrue();
        result.Info.MaxPurchasable.Should().Be(0);
        result.List.Should().OnlyContain(x => x.IsOwned);
    }
}

