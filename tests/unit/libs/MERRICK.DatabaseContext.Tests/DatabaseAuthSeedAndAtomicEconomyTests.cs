// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Kongor.Core.Security;
using MERRICK.DatabaseContext.Constants;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Store;
using MERRICK.DatabaseContext.Entities.Utility;
using MERRICK.DatabaseContext.Extensions;
using MERRICK.DatabaseContext.Handlers;
using MERRICK.DatabaseContext.Persistence;
using MERRICK.DatabaseContext.Repositories;
using MERRICK.DatabaseContext.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.Clients;
using NEXUS.Contracts.Domain;
using Xunit;

namespace MERRICK.DatabaseContext.Tests;

public sealed class DatabaseAuthSeedAndAtomicEconomyTests
{
    [Fact]
    public async Task RegisterOrLoginDiscordUserAsync_UnverifiedUsernameMatch_NeverHijacksExistingOrStaffUser()
    {
        // protects: Discord OAuth account-binding zero-trust invariant against unverified handle spoofing and Staff/Admin takeover.
        // fails_when: RegisterOrLoginDiscordUserAsync matches an existing User by DiscordUsername or auto-links an Administrator/Staff user via OAuth email.
        // seam: Real AccountRepository.RegisterOrLoginDiscordUserAsync against MerrickContext.
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();

        Role adminRole = new() { ID = 1, Name = UserRoles.Administrator };
        Role userRole = new() { ID = 2, Name = UserRoles.User };
        db.Roles.AddRange(adminRole, userRole);

        User victimUser = new()
        {
            ID = 10,
            EmailAddress = "victim@kongor.local",
            DiscordId = null,
            DiscordUsername = "TargetHandle",
            Role = userRole,
            RoleID = userRole.ID,
            SRPPasswordSalt = "salt_v",
            SRPPasswordHash = "hash_v",
            PBKDF2PasswordHash = "pbkdf2_v"
        };
        Account victimAccount = new()
        {
            ID = 100,
            UserID = victimUser.ID,
            User = victimUser,
            Name = "VictimPlayer",
            IsMain = true,
            Type = AccountType.Normal
        };

        User staffUser = new()
        {
            ID = 1,
            EmailAddress = "admin@kongor.local",
            DiscordId = null,
            DiscordUsername = "KongorAdmin",
            Role = adminRole,
            RoleID = adminRole.ID,
            SRPPasswordSalt = "salt_a",
            SRPPasswordHash = "hash_a",
            PBKDF2PasswordHash = "pbkdf2_a"
        };
        Account staffAccount = new()
        {
            ID = 1,
            UserID = staffUser.ID,
            User = staffUser,
            Name = "KONGOR",
            IsMain = true,
            Type = AccountType.Staff
        };

        db.Users.AddRange(victimUser, staffUser);
        db.Accounts.AddRange(victimAccount, staffAccount);
        await db.SaveChangesAsync();

        AccountRepository repo = new(db, NullLogger<AccountRepository>.Instance);

        // 1. Attacker spoofs victim's DiscordUsername with a different DiscordId and email
        User? attackerResult = await repo.RegisterOrLoginDiscordUserAsync(new DiscordAuthRequestDto(
            DiscordId: "999888777666",
            DiscordUsername: "TargetHandle",
            Email: "attacker@kongor.local",
            DiscordAvatar: "avatar_a"));

        attackerResult.Should().NotBeNull();
        attackerResult!.ID.Should().NotBe(victimUser.ID);

        User refreshedVictim = await db.Users.AsNoTracking().SingleAsync(u => u.ID == victimUser.ID);
        refreshedVictim.DiscordId.Should().BeNull();

        // 2. Attacker attempts to auto-link Administrator/Staff user via unlinked email
        User? staffAttempt = await repo.RegisterOrLoginDiscordUserAsync(new DiscordAuthRequestDto(
            DiscordId: "555444333222",
            DiscordUsername: "SpoofedAdmin",
            Email: "admin@kongor.local",
            DiscordAvatar: "avatar_b"));

        if (staffAttempt is not null)
        {
            staffAttempt.ID.Should().NotBe(staffUser.ID);
        }

        User refreshedStaff = await db.Users.AsNoTracking().SingleAsync(u => u.ID == staffUser.ID);
        refreshedStaff.DiscordId.Should().BeNull();
    }

    [Fact]
    public async Task CreateHostApplicationAsync_UnknownServerPrefix_ReturnsNullWithoutAccount1Fallback()
    {
        // protects: ApplyHost ownership isolation so unknown server prefixes never attach to Account ID #1.
        // fails_when: CreateHostApplicationAsync falls back to db.Accounts.FirstOrDefaultAsync() when no matching account exists.
        // seam: Real AccountRepository.CreateHostApplicationAsync against MerrickContext seeded with Account #1.
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();

        Role adminRole = new() { ID = 1, Name = UserRoles.Administrator };
        User adminUser = new()
        {
            ID = 1,
            EmailAddress = "admin@kongor.local",
            Role = adminRole,
            RoleID = adminRole.ID,
            SRPPasswordSalt = "salt_1",
            SRPPasswordHash = "hash_1",
            PBKDF2PasswordHash = "pbkdf2_1"
        };
        Account adminAccount = new()
        {
            ID = 1,
            UserID = 1,
            User = adminUser,
            Name = "KONGOR",
            IsMain = true,
            Type = AccountType.Staff
        };
        db.Roles.Add(adminRole);
        db.Users.Add(adminUser);
        db.Accounts.Add(adminAccount);
        await db.SaveChangesAsync();

        AccountRepository repo = new(db, NullLogger<AccountRepository>.Instance);

        var result = await repo.CreateHostApplicationAsync("unknown_applicant_xyz", "EU", 4, "Test application");

        result.Should().BeNull();
        (await db.HostApplications.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task DtoMapping_And_PurchaseStoreItem_PriceIsGold_PriceInMMPIsSilver_And_MissingItemRejected()
    {
        // protects: StoreItem Gold (Price) vs Silver (PriceInMMP) parity and rejection of missing/disabled catalog items.
        // fails_when: ToDto swaps Price/PriceInMMP or PurchaseStoreItemAsync fabricates a 500-gold charge when StoreItem lookup returns null.
        // seam: Real DtoMappingExtensions.ToDto + InventoryRepository.PurchaseStoreItemAsync against MerrickContext.
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();

        User user = new()
        {
            ID = 20,
            EmailAddress = "shopper@kongor.local",
            Role = new Role { ID = 2, Name = UserRoles.User },
            SRPPasswordSalt = "s",
            SRPPasswordHash = "h",
            PBKDF2PasswordHash = "p",
            GoldCoins = 1000,
            SilverCoins = 4000
        };
        Account account = new()
        {
            ID = 200,
            UserID = user.ID,
            User = user,
            Name = "TestShopper",
            IsMain = true
        };
        StoreItem item = new()
        {
            Id = 77,
            Name = "Arctic Valkyrie",
            Code = "aa.Hero_Valkyrie.Arctic",
            UpgradeType = 3,
            LocalContent = "heroes/valkyrie/arctic",
            Price = 420,
            PriceInMMP = 2600,
            Enabled = true,
            Purchasable = true
        };

        db.Users.Add(user);
        db.Accounts.Add(account);
        db.StoreItems.Add(item);
        await db.SaveChangesAsync();

        StoreItemDto dto = item.ToDto();
        dto.GoldCost.Should().Be(420);
        dto.SilverCost.Should().Be(2600);

        InventoryRepository repo = new(db, NullLogger<InventoryRepository>.Instance);

        // Missing item must be rejected without charging 500 gold
        PurchaseItemResultDto missingPurchase = await repo.PurchaseStoreItemAsync(user.ID, 999999, "gold");
        missingPurchase.Success.Should().BeFalse();

        User afterMissing = await db.Users.AsNoTracking().SingleAsync(u => u.ID == user.ID);
        afterMissing.GoldCoins.Should().Be(1000);
        afterMissing.SilverCoins.Should().Be(4000);

        // Valid silver purchase deducts PriceInMMP (2600), leaving 1400 silver and 1000 gold
        PurchaseItemResultDto silverPurchase = await repo.PurchaseStoreItemAsync(user.ID, 77, "silver");
        silverPurchase.Success.Should().BeTrue();
        silverPurchase.NewSilver.Should().Be(1400);
        silverPurchase.NewGold.Should().Be(1000);
    }

    [Fact]
    public async Task EquipStoreItemAsync_WhenUnlockAllItemsDisabled_EnforcesInventoryOwnership()
    {
        // protects: Cosmetic loadout authorization when Store:UnlockAllItems is false.
        // fails_when: EquipStoreItemAsync equips an unowned StoreItem onto Account.SelectedStoreItems without checking UserInventory.
        // seam: Real InventoryRepository.EquipStoreItemAsync with IConfiguration (Store:UnlockAllItems=false) against MerrickContext.
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();

        User user = new()
        {
            ID = 30,
            EmailAddress = "equipper@kongor.local",
            Role = new Role { ID = 2, Name = UserRoles.User },
            SRPPasswordSalt = "s",
            SRPPasswordHash = "h",
            PBKDF2PasswordHash = "p"
        };
        Account account = new()
        {
            ID = 300,
            UserID = user.ID,
            User = user,
            Name = "EquipGuardPlayer",
            IsMain = true
        };
        StoreItem crownItem = new()
        {
            Id = 88,
            Name = "Gold Crown Icon",
            Code = "ai.gold_crown",
            LocalContent = "gold_crown",
            Price = 300,
            PriceInMMP = 1500,
            Enabled = true,
            Purchasable = true
        };

        db.Users.Add(user);
        db.Accounts.Add(account);
        db.StoreItems.Add(crownItem);
        await db.SaveChangesAsync();

        IConfiguration lockedConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Store:UnlockAllItems"] = "false"
            })
            .Build();

        InventoryRepository repo = new(db, NullLogger<InventoryRepository>.Instance, lockedConfig);

        EquipItemResultDto unownedAttempt = await repo.EquipStoreItemAsync(account.Name, 88, "ai.gold_crown");
        unownedAttempt.Success.Should().BeFalse();

        IReadOnlyList<string> selectedAfterReject = await repo.GetSelectedStoreItemsAsync(account.ID);
        selectedAfterReject.Should().NotContain("ai.gold_crown");

        // Grant ownership and verify equip succeeds
        await repo.GrantItemAsync(account.ID, "ai.gold_crown");
        EquipItemResultDto ownedAttempt = await repo.EquipStoreItemAsync(account.Name, 88, "ai.gold_crown");
        ownedAttempt.Success.Should().BeTrue();
        ownedAttempt.SelectedItems.Should().Contain("ai.gold_crown");
    }

    [Fact]
    public async Task UpdateUserAsync_StaleDetachedEntity_DoesNotOverwriteConcurrentCurrencyDeltas()
    {
        // protects: User coin/ticket balances against lost-update clobbering when stale detached User profile entities are saved.
        // fails_when: AccountRepository.UpdateUserAsync calls db.Users.Update(staleUser) and overwrites GoldCoins/SilverCoins/PlinkoTickets.
        // seam: Real AccountRepository.UpdateUserAsync + InventoryRepository.AddCurrenciesAsync across isolated MerrickContext instances.
        string dbName = "StaleUserCoinGuard_" + Guid.NewGuid().ToString("N");

        using (MerrickContext seedDb = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = new()
            {
                ID = 40,
                EmailAddress = "concurrent@kongor.local",
                Role = new Role { ID = 2, Name = UserRoles.User },
                SRPPasswordSalt = "s",
                SRPPasswordHash = "h",
                PBKDF2PasswordHash = "p",
                GoldCoins = 100,
                SilverCoins = 500,
                PlinkoTickets = 2
            };
            Account account = new()
            {
                ID = 400,
                UserID = 40,
                User = user,
                Name = "ConcurrentPlayer",
                IsMain = true
            };
            seedDb.Users.Add(user);
            seedDb.Accounts.Add(account);
            await seedDb.SaveChangesAsync();
        }

        User staleDetached;
        using (MerrickContext readDb = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            AccountRepository readRepo = new(readDb, NullLogger<AccountRepository>.Instance);
            staleDetached = (await readRepo.GetUserByIdAsync(40))!;
        }

        // Concurrent atomic reward grant in another context
        using (MerrickContext rewardDb = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            InventoryRepository invRepo = new(rewardDb, NullLogger<InventoryRepository>.Instance);
            await invRepo.AddCurrenciesAsync(400, goldDelta: 250, silverDelta: 1000, plinkoDelta: 5);
        }

        // Now save non-currency profile update on the stale detached User
        staleDetached.DiscordAvatar = "updated_avatar_hash";
        using (MerrickContext profileDb = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            AccountRepository profileRepo = new(profileDb, NullLogger<AccountRepository>.Instance);
            await profileRepo.UpdateUserAsync(staleDetached);
        }

        using (MerrickContext verifyDb = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User finalUser = await verifyDb.Users.AsNoTracking().SingleAsync(u => u.ID == 40);
            finalUser.DiscordAvatar.Should().Be("updated_avatar_hash");
            finalUser.GoldCoins.Should().Be(350);
            finalUser.SilverCoins.Should().Be(1500);
            finalUser.PlinkoTickets.Should().Be(7);
        }
    }

    [Fact]
    public async Task SeedDataHandlers_ProductionEnvironment_SkipsStaticStaffDemoAccounts_And_StandaloneGeneratesUniqueHostCredential()
    {
        // protects: Production database bootstrap against known static SRP/PBKDF2 hashes and admin/host credential reuse.
        // fails_when: EnsureStaffAccountsAsync seeds static admin@kongor.local outside Development, or EnsureStandaloneBootstrapAsync reuses admin credentials for HOST.
        // seam: Real SeedDataHandlers.EnsureStaffAccountsAsync and EnsureStandaloneBootstrapAsync against MerrickContext.
        using MerrickContext prodDb = TestDbContextFactory.CreateInMemoryContext();

        string syntheticAdminCred = "adm-" + Guid.NewGuid().ToString("N");
        IConfiguration prodConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Production",
                ["KONGOR_STANDALONE"] = "true",
                ["KONGOR_BOOTSTRAP_ADMIN_PASSWORD"] = syntheticAdminCred
            })
            .Build();

        // 1. Static EnsureStaffAccountsAsync must NOT create static demo accounts in Production
        await SeedDataHandlers.EnsureStaffAccountsAsync(prodDb, NullLogger.Instance, CancellationToken.None, prodConfig);
        (await prodDb.Users.AnyAsync(u => u.EmailAddress == "guest@project.kongor")).Should().BeFalse();
        (await prodDb.Accounts.AnyAsync(a => a.Name == "guccidonut")).Should().BeFalse();

        // 2. Standalone bootstrap must create KONGOR and HOST with distinct credentials when KONGOR_BOOTSTRAP_HOST_PASSWORD is omitted
        await SeedDataHandlers.EnsureStandaloneBootstrapAsync(prodDb, prodConfig, NullLogger.Instance, CancellationToken.None);

        Account? kongorAdmin = await prodDb.Accounts.Include(a => a.User).FirstOrDefaultAsync(a => a.Name == "Admin");
        Account? hostAccount = await prodDb.Accounts.Include(a => a.User).FirstOrDefaultAsync(a => a.Name == "kongor_host");

        kongorAdmin.Should().NotBeNull();
        hostAccount.Should().NotBeNull();

        string adminCandidateForHost = S2Crypto.ComputeSrpFromPlaintext(syntheticAdminCred, hostAccount!.User.SRPPasswordSalt);
        hostAccount.User.SRPPasswordHash.Should().NotBe(adminCandidateForHost,
            "Non-dev standalone bootstrap must generate a distinct CSPRNG credential for HOST when KONGOR_BOOTSTRAP_HOST_PASSWORD is omitted");
    }
}
