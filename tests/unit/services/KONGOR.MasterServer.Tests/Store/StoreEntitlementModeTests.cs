// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using FluentAssertions;
using KONGOR.MasterServer.Services.Store;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Store;
using MERRICK.DatabaseContext.Extensions;
using MERRICK.DatabaseContext.Persistence;
using MERRICK.DatabaseContext.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.Clients;
using NEXUS.Contracts.CQRS.Store;
using NEXUS.Contracts.Domain;
using NEXUS.Contracts.Store;
using Xunit;

namespace KONGOR.MasterServer.Tests.Store;

public sealed class StoreEntitlementModeTests
{
    // protects: PreservationFreeAll default unlocking public cosmetics while strictly denying Staff/GM/Clan/SBT restricted entitlements (including candycane, frostburnlogo, gmshield, Garena Staff, HLL Staff) to regular users, and EarnedProgression mode locking unowned public items.
    // fails_when: ExpandOwnedItems grants Staff Name Colors or Staff Icons to Normal accounts, or EarnedProgression mode leaks unowned avatars.
    // seam: Real StoreEntitlementPolicy + UpgradesResolutionService.ExpandOwnedItems + real StoreItemsConfiguration.json catalog.
    [Fact]
    public void ExpandOwnedItems_PreservationFreeAll_UnlocksPublicCosmetics_ButLocksStaffAndCandycaneForNormalUsers()
    {
        HashSet<string> normalOwned = UpgradesResolutionService.ExpandOwnedItems(
            rawItems: [],
            unlockAllItems: true,
            accountType: AccountType.Normal,
            clanTag: null);

        // Public cosmetics remain unlocked for everyone by default
        normalOwned.Should().Contain("Hero_Pyromancer.Female");
        normalOwned.Should().Contain("Flamboyant");

        // Staff, GM, SBT, and candycane entitlements MUST NOT leak to regular accounts
        normalOwned.Should().NotContain("frostburnlogo");
        normalOwned.Should().NotContain("cc.frostburnlogo");
        normalOwned.Should().NotContain("cs.frostburnlogo");
        normalOwned.Should().NotContain("gmshield");
        normalOwned.Should().NotContain("cc.gmshield");
        normalOwned.Should().NotContain("gmgold");
        normalOwned.Should().NotContain("banhammer");
        normalOwned.Should().NotContain("techtinker");
        normalOwned.Should().NotContain("candycane");
        normalOwned.Should().NotContain("cc.candycane");
        normalOwned.Should().NotContain("sbteye");
        normalOwned.Should().NotContain("Garena Staff");
        normalOwned.Should().NotContain("HLL Staff");

        // Staff accounts DO receive Staff/GM/candycane entitlements
        HashSet<string> staffOwned = UpgradesResolutionService.ExpandOwnedItems(
            rawItems: [],
            unlockAllItems: true,
            accountType: AccountType.Staff,
            clanTag: "GUCI");

        staffOwned.Should().Contain("frostburnlogo");
        staffOwned.Should().Contain("gmshield");
        staffOwned.Should().Contain("candycane");
        staffOwned.Should().Contain("Garena Staff");
        staffOwned.Should().Contain("HLL Staff");

        // EarnedProgression backup mode grants ONLY canonical defaults + owned inventory
        HashSet<string> progressionOwned = UpgradesResolutionService.ExpandOwnedItems(
            rawItems: ["Hero_Krixi.Sexy"],
            unlockAllItems: false,
            accountType: AccountType.Normal,
            clanTag: null);

        progressionOwned.Should().Contain("Hero_Krixi.Sexy");
        progressionOwned.Should().Contain("aa.Hero_Krixi.Sexy");
        progressionOwned.Should().NotContain("Hero_Pyromancer.Female");
    }

    // protects: InventoryRepository.EquipItemAsync, AccountExtensions sanitization, backward-compatible Store:UnlockAllItems -> StoreMode mapping, and UserId-scoped PurchaseStoreProductCommand lock key.
    // fails_when: Normal user equips cc.frostburnlogo/cs.gmshield/cc.candycane in PreservationFreeAll mode, Store:UnlockAllItems=false fails to map to EarnedProgression, or PurchaseStoreProductCommand locks per AccountId instead of UserId when UserId is provided.
    // seam: Real InventoryRepository.EquipItemAsync + EF Core InMemory MerrickContext + AccountExtensions + PurchaseStoreProductCommand.
    [Fact]
    public async System.Threading.Tasks.Task EquipItemAsync_AndAccountExtensions_EnforceStaffBoundaries_AndWalletLocksUserId()
    {
        DbContextOptions<MerrickContext> options = new DbContextOptionsBuilder<MerrickContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using MerrickContext db = new(options);

        User user = new()
        {
            ID = 42,
            EmailAddress = "player@kongor.local",
            SRPPasswordSalt = "s",
            SRPPasswordHash = "h",
            PBKDF2PasswordHash = "p"
        };
        Account normalAcc = new()
        {
            ID = 420,
            Name = "RegularHero",
            UserID = 42,
            User = user,
            Type = AccountType.Normal,
            IsMain = true,
            SelectedStoreItems =
            [
                new AccountSelectedStoreItem { AccountID = 420, ItemCode = "cs.gmshield" },
                new AccountSelectedStoreItem { AccountID = 420, ItemCode = "cc.candycane" },
                new AccountSelectedStoreItem { AccountID = 420, ItemCode = "ai.Garena Staff" }
            ]
        };
        db.Users.Add(user);
        db.Accounts.Add(normalAcc);
        db.StoreItems.Add(new MERRICK.DatabaseContext.Entities.Store.StoreItem
        {
            Id = 172,
            Code = "frostburnlogo",
            Name = "Frostburn Logo",
            UpgradeType = 0,
            LocalContent = "/ui/icons/frostburn.tga",
            Price = 0,
            PriceInMMP = 0,
            Purchasable = false,
            Enabled = true
        });
        db.StoreItems.Add(new MERRICK.DatabaseContext.Entities.Store.StoreItem
        {
            Id = 185,
            Code = "diamond",
            Name = "Diamond Blue Color",
            UpgradeType = 0,
            LocalContent = "/ui/icons/diamond.tga",
            Price = 100,
            PriceInMMP = 500,
            Purchasable = true,
            Enabled = true
        });
        await db.SaveChangesAsync();

        // AccountExtensions must strip unauthorized staff symbols, colors, and icons
        normalAcc.GetChatSymbol().Should().BeEmpty();
        normalAcc.GetNameColour().Should().Be("cc.white");
        normalAcc.GetIcon().Should().NotBe("ai.Garena Staff");

        IConfiguration freeAllCfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Store:UnlockAllItems"] = "true"
            })
            .Build();
        StoreEntitlementPolicy.ResolveMode(freeAllCfg).Should().Be(StoreMode.PreservationFreeAll);

        IConfiguration earnedCfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Store:UnlockAllItems"] = "false"
            })
            .Build();
        StoreEntitlementPolicy.ResolveMode(earnedCfg).Should().Be(StoreMode.EarnedProgression);

        InventoryRepository repo = new(db, NullLogger<InventoryRepository>.Instance, freeAllCfg);

        // Regular account blocked from equipping restricted staff name color even when Store:UnlockAllItems=true
        EquipItemResultDto deniedStaff = await repo.EquipStoreItemAsync("RegularHero", 172, "cc.frostburnlogo");
        deniedStaff.Success.Should().BeFalse();

        // Regular account CAN equip public Diamond Blue color free under PreservationFreeAll
        EquipItemResultDto allowedPublic = await repo.EquipStoreItemAsync("RegularHero", 185, "cc.diamond");
        allowedPublic.Success.Should().BeTrue();

        // UserId-scoped wallet checkout lock prevents cross-subaccount race on shared User coins
        PurchaseStoreProductCommand cmdSubA = new(AccountId: 420, ProductId: 185, Currency: "1", CategoryId: 0, CurrentPage: 1, HostTime: "1", UserId: 42);
        PurchaseStoreProductCommand cmdSubB = new(AccountId: 421, ProductId: 185, Currency: "1", CategoryId: 0, CurrentPage: 1, HostTime: "2", UserId: 42);
        cmdSubA.LockKey.Should().Be("lock:user:42:store_checkout");
        cmdSubB.LockKey.Should().Be(cmdSubA.LockKey);
    }
}
