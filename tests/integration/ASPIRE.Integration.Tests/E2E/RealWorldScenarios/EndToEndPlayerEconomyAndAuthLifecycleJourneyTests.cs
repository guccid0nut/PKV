// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: EndToEndPlayerEconomyAndAuthLifecycleJourneyTests.cs
// System: ProjectKongor Solution-Wide E2E Testing Suite
// Tier 4: Real-World Application Scenarios (Player Economy & Auth Lifecycle Journey)
// Policy: Strict Explicit Typing, Multi-Stage Pipeline Verification
// ============================================================================

using FluentAssertions;
using MERRICK.DatabaseContext.Entities.Store;

namespace ASPIRE.Tests.E2E.Tier4_RealWorldScenarios;

public sealed class EndToEndPlayerEconomyAndAuthLifecycleJourneyTests
{
    private static MerrickContext CreateInMemoryDbContext()
    {
        return TestDbContextFactory.CreateInMemoryContext("Full_Player_Journey_" + Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public async Task CompletePlayerJourney_RegistrationToAuthToCatalogToPurchaseToEquip_SucceedsAllStages()
    {
        // ---------------------------------------------------------------------
        // Stage 1: Registration & Account Provisioning
        // ---------------------------------------------------------------------
        using MerrickContext db = CreateInMemoryDbContext();

        string username = "KongorHero_" + Guid.NewGuid().ToString("N")[..8];
        string email = $"{username.ToLowerInvariant()}@kongor.net";

        Role userRole = new() { ID = 1, Name = "User" };
        db.Roles.Add(userRole);

        User user = new()
        {
            EmailAddress = email,
            PBKDF2PasswordHash = "argon2id$v=19$m=65536,t=3,p=4$dummyhash12345",
            SRPPasswordHash = "srp_verifier_hash_sample_abc123",
            SRPPasswordSalt = "srp_salt_sample_xyz789",
            Role = userRole,
            GoldCoins = 0,
            SilverCoins = 0,
            PlinkoTickets = 5,
            InventoryItems = []
        };

        Account account = new()
        {
            Name = username,
            User = user,
            Type = AccountType.Normal,
            IsMain = true,
            SelectedStoreItems = [],
            IPAddressCollection = [],
            MACAddressCollection = [],
            SystemInformationCollection = [],
            SystemInformationHashCollection = []
        };

        // Seed Store Catalog
        StoreItem valkyrieSkin = new()
        {
            Name = "Valkyrie Arctic Frost",
            Code = "Hero_Valkyrie.Alt4",
            UpgradeId = 101,
            UpgradeType = 1,
            LocalContent = "hero/valkyrie/alt4",
            Purchasable = true,
            Price = 450,
            PriceInMMP = 2500,
            Premium = true,
            Enabled = true
        };

        // Seed Promo Code
        RedeemableCode welcomePromo = new()
        {
            Code = "WELCOME_HERO",
            GoldCoinsReward = 500,
            SilverCoinsReward = 2000,
            PlinkoTicketsReward = 5
        };

        db.Users.Add(user);
        db.Accounts.Add(account);
        db.StoreItems.Add(valkyrieSkin);
        db.RedeemableCodes.Add(welcomePromo);
        await db.SaveChangesAsync();

        user.ID.Should().BeGreaterThan(0);
        account.ID.Should().BeGreaterThan(0);

        // ---------------------------------------------------------------------
        // Stage 2: SRP Wire Protocol Handshake & Payload Deserialization
        // ---------------------------------------------------------------------
        string authRequestPayload =
            $"a:3:{{s:6:\"action\";s:5:\"login\";s:8:\"username\";s:{username.Length}:\"{username}\";s:7:\"version\";s:5:\"4.1.0\";}}";
        object? deserialized = PhpDeserializer.Deserialize(authRequestPayload);
        deserialized.Should().BeOfType<PhpHashtable>();
        PhpHashtable authTable = (PhpHashtable)deserialized!;
        authTable["username"]!.ToString().Should().Be(username);

        // ---------------------------------------------------------------------
        // Stage 3: Hardware Telemetry Audit Logging
        // ---------------------------------------------------------------------
        string clientIp = "192.168.1.77";
        string sysInfoRaw = "00-11-22-33-44-55|Windows 11 Pro|AMD Ryzen 9|RTX 4090|32GB";
        string hwidHash = "deadbeefcafebabe1234567890abcdef";

        string[] sysInfoParts = sysInfoRaw.Split('|');
        account.IPAddressCollection.Add(clientIp);
        account.MACAddressCollection.Add(sysInfoParts[0]);
        account.SystemInformationCollection.Add(string.Join('|', sysInfoParts.Skip(1)));
        account.SystemInformationHashCollection.Add(hwidHash);
        account.TimestampLastActive = TimeProvider.System.GetUtcNow();
        await db.SaveChangesAsync();

        account.IPAddressCollection.Should().Contain(clientIp);
        account.MACAddressCollection.Should().Contain("00-11-22-33-44-55");

        // ---------------------------------------------------------------------
        // Stage 4: Store Catalog Browsing
        // ---------------------------------------------------------------------
        InventoryRepository inventoryRepo = new(db);
        IReadOnlyList<StoreItem> catalog = await inventoryRepo.GetStoreCatalogAsync();
        catalog.Should().NotBeEmpty();
        catalog.Should().Contain(i => i.Code == "Hero_Valkyrie.Alt4");

        // ---------------------------------------------------------------------
        // Stage 5: Promo Code Redemption
        // ---------------------------------------------------------------------
        RedeemableCode? promo = await db.RedeemableCodes.FirstOrDefaultAsync(c => c.Code == "WELCOME_HERO");
        promo.Should().NotBeNull();
        promo!.TimestampRedeemed.Should().BeNull();

        await inventoryRepo.AddCurrenciesAsync(account.ID, promo.GoldCoinsReward, promo.SilverCoinsReward,
            promo.PlinkoTicketsReward);
        promo.TimestampRedeemed = TimeProvider.System.GetUtcNow();
        promo.RedeemedByAccountID = account.ID;
        await db.SaveChangesAsync();

        User? afterPromoUser = await db.Users.FindAsync(user.ID);
        afterPromoUser!.GoldCoins.Should().Be(500);
        afterPromoUser.SilverCoins.Should().Be(2000);
        afterPromoUser.PlinkoTickets.Should().Be(10); // 5 initial + 5 reward

        // ---------------------------------------------------------------------
        // Stage 6: Store Item Purchase (Gold)
        // ---------------------------------------------------------------------
        StoreItem? targetItem = await inventoryRepo.GetStoreItemByCodeAsync("Hero_Valkyrie.Alt4");
        targetItem.Should().NotBeNull();

        bool purchaseSuccess = await inventoryRepo.DeductCurrenciesAsync(account.ID, targetItem!.Price, 0, 0);
        purchaseSuccess.Should().BeTrue();

        await inventoryRepo.GrantItemAsync(account.ID, targetItem.Code);

        // ---------------------------------------------------------------------
        // Stage 7: User Inventory & Ownership Verification
        // ---------------------------------------------------------------------
        bool ownsItem = await inventoryRepo.HasItemAsync(account.ID, "Hero_Valkyrie.Alt4");
        ownsItem.Should().BeTrue();

        IReadOnlyList<string> ownedCodes = await inventoryRepo.GetOwnedItemCodesAsync(account.ID);
        ownedCodes.Should().Contain("Hero_Valkyrie.Alt4");

        // ---------------------------------------------------------------------
        // Stage 8: Financial Balance Verification
        // ---------------------------------------------------------------------
        User? finalUser = await db.Users.FindAsync(user.ID);
        finalUser!.GoldCoins.Should().Be(50); // 500 - 450
        finalUser.SilverCoins.Should().Be(2000);
        finalUser.PlinkoTickets.Should().Be(10);

        // ---------------------------------------------------------------------
        // Stage 9: Store Item Equipping
        // ---------------------------------------------------------------------
        await inventoryRepo.SetSelectedStoreItemsAsync(account.ID, ["Hero_Valkyrie.Alt4"]);
        IReadOnlyList<string> selectedItems = await inventoryRepo.GetSelectedStoreItemsAsync(account.ID);
        selectedItems.Should().Contain("Hero_Valkyrie.Alt4");
    }
}
