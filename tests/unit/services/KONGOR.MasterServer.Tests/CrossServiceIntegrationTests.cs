// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ASPIRE.Common.Serialization.PHP;
using FluentAssertions;
using KONGOR.MasterServer.Controllers.Store;
using KONGOR.MasterServer.Handlers.SRP;
using KONGOR.MasterServer.Helpers.Serialisation;
using KONGOR.MasterServer.Models.RequestResponse.SRP;
using KONGOR.MasterServer.Models.RequestResponse.Stats;
using KONGOR.MasterServer.Services;
using KONGOR.MasterServer.Services.Stats;
using KONGOR.MasterServer.Tests.Infrastructure;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Statistics;
using MERRICK.DatabaseContext.Entities.Utility;
using MERRICK.DatabaseContext.Enumerations;
using MERRICK.DatabaseContext.Persistence;
using MERRICK.DatabaseContext.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using DbRole = MERRICK.DatabaseContext.Entities.Utility.Role;

namespace KONGOR.MasterServer.Tests;

/// <summary>
///     Empirical Adversarial Challenger Test Suite for Milestone 5:
///     1. MasterServer SRP Authentication Stage Two SelectedStoreItemCodes handling and CustomIconSlotID resolution.
///     2. PlayerProfileStatsBuilder ShowSimpleStatsResponse SelectedStoreItemCodes propagation and serialization.
///     3. End-to-end PHP serialization roundtrip of equipped store item codes.
///     4. Child entities (AccountSelectedStoreItem, AccountIpAddress, AccountMacAddress, AccountBlockedPhrase)
///        relational roundtrips, uniqueness, cascade deletion, and maximum column length bounds (zero truncation).
///     5. AccountRepository and InventoryRepository reconciliation of 1NF child collections.
/// </summary>
public sealed class CrossServiceIntegrationTests
{
    private static (User user, Account account) CreateTestUserAndAccount(int accountId = 100, string name = "ChallengerPlayer")
    {
        User user = new()
        {
            ID = accountId,
            EmailAddress = $"{name.ToLowerInvariant()}@kongor.net",
            Role = new DbRole { ID = 2, Name = "User" },
            SRPPasswordSalt = "salt123",
            SRPPasswordHash = "hash123",
            PBKDF2PasswordHash = "pbkdf2_123"
        };

        Account account = new()
        {
            ID = accountId,
            Name = name,
            User = user,
            IsMain = true
        };

        user.Accounts.Add(account);
        return (user, account);
    }

    // =========================================================================
    // 1. SRP Authentication Stage Two Challenges
    // =========================================================================

    [Fact]
    public void GenerateStageTwoResponse_WithEmptySelectedStoreItems_PopulatesEmptyListAndDefaultIcon()
    {
        // Arrange
        (User user, Account account) = CreateTestUserAndAccount();
        account.SelectedStoreItems.Clear();

        SRPAuthenticationHandlers.StageTwoResponseParameters parameters = new()
        {
            Account = account,
            ClanRoster = [],
            ServerProof = "proof_alpha",
            ClientIPAddress = "127.0.0.1",
            ChatServer = ("127.0.0.1", 11031),
            CurrentSeason = "1",
            ICBURL = "http://localhost:55555"
        };

        // Act
        SRPAuthenticationResponseStageTwo response = SRPAuthenticationHandlers.GenerateStageTwoResponse(parameters, out string cookie);

        // Assert
        cookie.Should().NotBeNullOrWhiteSpace();
        response.Should().NotBeNull();
        response.SelectedStoreItems.Should().NotBeNull();
        response.SelectedStoreItems.Should().BeEmpty();
        response.CustomIconSlotID.Should().Be("0");

        // PHP serialization should produce empty selected_upgrades array without exception
        string serialized = PhpSerialization.Serialize(response);
        serialized.Should().Contain("\"selected_upgrades\";a:0:{}");
    }

    [Theory]
    [InlineData("ai.custom_icon:1", "1")]
    [InlineData("ai.custom_icon:2", "2")]
    [InlineData("ai.custom_icon:5", "5")]
    [InlineData("AI.CUSTOM_ICON:7", "7")]
    [InlineData("ai.custom_icon:", "1")] // Empty slot suffix defaults to 1
    public void GenerateStageTwoResponse_WithExplicitCustomIcon_ResolvesCustomSlotCorrectly(string iconCode, string expectedSlot)
    {
        // Arrange
        (User user, Account account) = CreateTestUserAndAccount();
        account.SetSelectedStoreItems([iconCode, "c.chat_red"]);

        SRPAuthenticationHandlers.StageTwoResponseParameters parameters = new()
        {
            Account = account,
            ClanRoster = [],
            ServerProof = "proof_beta",
            ClientIPAddress = "127.0.0.1",
            ChatServer = ("127.0.0.1", 11031),
            CurrentSeason = "1"
        };

        // Act
        SRPAuthenticationResponseStageTwo response = SRPAuthenticationHandlers.GenerateStageTwoResponse(parameters, out _);

        // Assert
        response.CustomIconSlotID.Should().Be(expectedSlot);
        response.SelectedStoreItems.Should().Contain(iconCode);
    }

    [Fact]
    public void GenerateStageTwoResponse_WithStandardStoreIcon_ReturnsSlotZeroEvenWithDiscordAvatar()
    {
        // Arrange: Explicit non-custom store icon (e.g. ai.beer) overrides Discord avatar fallback
        (User user, Account account) = CreateTestUserAndAccount();
        user.DiscordAvatar = "discord_avatar_hash_123";
        user.DiscordId = "123456789012345678";
        account.SetSelectedStoreItems(["ai.beer", "taunt.kongor"]);

        SRPAuthenticationHandlers.StageTwoResponseParameters parameters = new()
        {
            Account = account,
            ClanRoster = [],
            ServerProof = "proof_gamma",
            ClientIPAddress = "127.0.0.1",
            ChatServer = ("127.0.0.1", 11031),
            CurrentSeason = "1"
        };

        // Act
        SRPAuthenticationResponseStageTwo response = SRPAuthenticationHandlers.GenerateStageTwoResponse(parameters, out _);

        // Assert
        response.CustomIconSlotID.Should().Be("0", "Explicit non-custom icon ai.beer overrides custom icon slot");
        response.SelectedStoreItems.Should().BeEquivalentTo(["ai.beer", "taunt.kongor"]);
    }

    [Fact]
    public void GenerateStageTwoResponse_WithDefaultIconAndDiscordAvatar_FallsBackToSlotOne()
    {
        // Arrange: ai.Default Icon is filtered out, allowing Discord avatar fallback
        (User user, Account account) = CreateTestUserAndAccount();
        user.DiscordAvatar = "discord_avatar_hash_123";
        account.SetSelectedStoreItems(["ai.Default Icon", "c.chat_blue"]);

        SRPAuthenticationHandlers.StageTwoResponseParameters parameters = new()
        {
            Account = account,
            ClanRoster = [],
            ServerProof = "proof_delta",
            ClientIPAddress = "127.0.0.1",
            ChatServer = ("127.0.0.1", 11031),
            CurrentSeason = "1"
        };

        // Act
        SRPAuthenticationResponseStageTwo response = SRPAuthenticationHandlers.GenerateStageTwoResponse(parameters, out _);

        // Assert
        response.CustomIconSlotID.Should().Be("1", "ai.Default Icon should not prevent Discord avatar custom icon slot 1");
    }

    [Fact]
    public void GenerateStageTwoResponse_WithMultipleSelectedItems_PreservesAllCodesAndSerializesToPhp()
    {
        // Arrange
        (User user, Account account) = CreateTestUserAndAccount();
        List<string> testCodes =
        [
            "aa.Hero_Devourer.alt",
            "c.chat_gold",
            "taunt.kongor",
            "ai.custom_icon:2",
            "announcer.flamboyant",
            "ward.gold_fish"
        ];
        account.SetSelectedStoreItems(testCodes);

        SRPAuthenticationHandlers.StageTwoResponseParameters parameters = new()
        {
            Account = account,
            ClanRoster = [],
            ServerProof = "proof_epsilon",
            ClientIPAddress = "10.0.0.1",
            ChatServer = ("127.0.0.1", 11031),
            CurrentSeason = "2"
        };

        // Act
        SRPAuthenticationResponseStageTwo response = SRPAuthenticationHandlers.GenerateStageTwoResponse(parameters, out _);

        // Assert
        response.SelectedStoreItems.Should().BeEquivalentTo(testCodes);
        response.CustomIconSlotID.Should().Be("2");

        string serialized = PhpSerialization.Serialize(response);
        serialized.Should().Contain("\"selected_upgrades\";a:6:{");
        foreach (string code in testCodes)
        {
            serialized.Should().Contain(code);
        }
    }

    // =========================================================================
    // 2. PlayerProfileStatsBuilder Challenges
    // =========================================================================

    [Fact]
    public void CreateShowSimpleStatsResponse_WithSelectedStoreItems_TransfersCodesFaithfully()
    {
        // Arrange
        (User user, Account account) = CreateTestUserAndAccount();
        List<string> codes = ["aa.Hero_Pyromancer.alt", "c.chat_red", "ai.custom_icon:3"];
        account.SetSelectedStoreItems(codes);

        Mock<IHeroDefinitionService> heroDefinitionsMock = new();
        heroDefinitionsMock.Setup(h => h.GetHeroIdentifier(It.IsAny<uint>())).Returns("Hero_Pyromancer");
        heroDefinitionsMock.Setup(h => h.GetBaseHeroId(It.IsAny<uint>())).Returns(1u);

        PlayerStatisticsAggregatedDTO stats = new()
        {
            RankedMatches = 15,
            TotalMatches = 15,
            RankedSeconds = 3600,
            RankedExp = 12000,
            RankedKills = 50,
            RankedDeaths = 20,
            RankedAssists = 30,
            TopHeroes = []
        };

        // Act
        ShowSimpleStatsResponse response = PlayerProfileStatsBuilder.Instance.CreateShowSimpleStatsResponse(
            account,
            stats,
            currentSeason: 1,
            heroDefinitionsMock.Object);

        // Assert
        response.Should().NotBeNull();
        response.SelectedStoreItems.Should().NotBeNull();
        response.SelectedStoreItems.Should().BeEquivalentTo(codes);
        response.CustomIconSlotID.Should().Be("3");

        // Verify PHP serialization output
        string serialized = PhpSerialization.Serialize(response);
        serialized.Should().Contain("\"selected_upgrades\";a:3:{");
        foreach (string code in codes)
        {
            serialized.Should().Contain(code);
        }
    }

    [Fact]
    public void CreateShowSimpleStatsResponse_WithEmptySelectedItems_ProducesEmptySelectedUpgrades()
    {
        // Arrange
        (User user, Account account) = CreateTestUserAndAccount();
        account.SelectedStoreItems.Clear();

        Mock<IHeroDefinitionService> heroDefinitionsMock = new();
        heroDefinitionsMock.Setup(h => h.GetHeroIdentifier(It.IsAny<uint>())).Returns("Hero_Pyromancer");
        heroDefinitionsMock.Setup(h => h.GetBaseHeroId(It.IsAny<uint>())).Returns(1u);

        PlayerStatisticsAggregatedDTO stats = new()
        {
            RankedMatches = 5,
            TotalMatches = 5,
            TopHeroes = []
        };

        // Act
        ShowSimpleStatsResponse response = PlayerProfileStatsBuilder.Instance.CreateShowSimpleStatsResponse(
            account,
            stats,
            currentSeason: 1,
            heroDefinitionsMock.Object);

        // Assert
        response.SelectedStoreItems.Should().NotBeNull();
        response.SelectedStoreItems.Should().BeEmpty();
        response.CustomIconSlotID.Should().Be("0");

        string serialized = PhpSerialization.Serialize(response);
        serialized.Should().Contain("\"selected_upgrades\";a:0:{}");
    }

    // =========================================================================
    // 3. Child Entities Relational Roundtrip & Truncation Bounds
    // =========================================================================

    [Fact]
    public async Task AccountSelectedStoreItem_100CharItemCode_RoundtripsCleanlyWithoutTruncation()
    {
        // Arrange: MaxLength(100) boundary test
        string dbName = Guid.NewGuid().ToString("N");
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName);

        (User user, Account account) = CreateTestUserAndAccount(201, "StoreItemBoundaryTester");
        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        // Construct exact 100-character item code
        string exact100CharItemCode = "aa." + new string('X', 80) + ".Hero_Flint_CustomSkin_2026";
        exact100CharItemCode = exact100CharItemCode[..100]; // Ensure exact length = 100
        exact100CharItemCode.Length.Should().Be(100);

        account.AddSelectedStoreItem(exact100CharItemCode);
        await db.SaveChangesAsync();

        // Act: Reload from fresh context
        using MerrickContext verifyDb = TestDbContextFactory.CreateInMemoryContext(dbName);
        Account? reloaded = await verifyDb.Accounts
            .Include(a => a.SelectedStoreItems)
            .FirstOrDefaultAsync(a => a.ID == account.ID);

        // Assert: Exact 100 chars preserved without data loss or truncation
        reloaded.Should().NotBeNull();
        reloaded!.SelectedStoreItems.Should().ContainSingle();
        reloaded.SelectedStoreItems[0].ItemCode.Should().Be(exact100CharItemCode);
        reloaded.SelectedStoreItems[0].ItemCode.Length.Should().Be(100);
        reloaded.SelectedStoreItemCodes.Should().Contain(exact100CharItemCode);
    }

    [Fact]
    public async Task AccountSelectedStoreItem_SpecialCharactersInCodes_RoundtripCleanly()
    {
        // Arrange: Test characters commonly found in store upgrade codes (. : _ - #)
        string dbName = Guid.NewGuid().ToString("N");
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName);

        (User user, Account account) = CreateTestUserAndAccount(202, "SpecialCharTester");
        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        List<string> specialCodes =
        [
            "ai.custom_icon:99",
            "c.chat_#FF5500",
            "taunt.super-kongor_2026.special",
            "announcer.flamboyant_v2.0",
            "ward.kongor:eye-of-the-beast"
        ];

        account.SetSelectedStoreItems(specialCodes);
        await db.SaveChangesAsync();

        // Act: Reload
        using MerrickContext verifyDb = TestDbContextFactory.CreateInMemoryContext(dbName);
        Account? reloaded = await verifyDb.Accounts
            .Include(a => a.SelectedStoreItems)
            .FirstOrDefaultAsync(a => a.ID == account.ID);

        // Assert
        reloaded.Should().NotBeNull();
        reloaded!.SelectedStoreItemCodes.Should().BeEquivalentTo(specialCodes);
    }

    [Fact]
    public async Task AccountIpAddress_50CharAddress_RoundtripsCleanly()
    {
        // Arrange: MaxLength(50) boundary test for IP addresses
        string dbName = Guid.NewGuid().ToString("N");
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName);

        (User user, Account account) = CreateTestUserAndAccount(203, "IpBoundaryTester");
        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        string ipv4 = "192.168.1.100";
        string ipv6 = "2001:0db8:85a3:0000:0000:8a2e:0370:7334"; // 39 chars
        string ipv6Mapped = "::ffff:192.0.2.128"; // 18 chars
        string exact50CharIp = new string('a', 46) + ":123";
        exact50CharIp = exact50CharIp[..50];
        exact50CharIp.Length.Should().Be(50);

        account.AddIPAddress(ipv4);
        account.AddIPAddress(ipv6);
        account.AddIPAddress(ipv6Mapped);
        account.AddIPAddress(exact50CharIp);
        await db.SaveChangesAsync();

        // Act: Reload
        using MerrickContext verifyDb = TestDbContextFactory.CreateInMemoryContext(dbName);
        Account? reloaded = await verifyDb.Accounts
            .Include(a => a.IPAddressCollection)
            .FirstOrDefaultAsync(a => a.ID == account.ID);

        // Assert
        reloaded.Should().NotBeNull();
        reloaded!.IPAddressStrings.Should().Contain(ipv4);
        reloaded.IPAddressStrings.Should().Contain(ipv6);
        reloaded.IPAddressStrings.Should().Contain(ipv6Mapped);
        reloaded.IPAddressStrings.Should().Contain(exact50CharIp);
        reloaded.IPAddressCollection.First(ip => ip.IPAddress == exact50CharIp).IPAddress.Length.Should().Be(50);
    }

    [Fact]
    public async Task AccountMacAddress_50CharAddress_RoundtripsCleanly()
    {
        // Arrange: MaxLength(50) boundary test for MAC addresses
        string dbName = Guid.NewGuid().ToString("N");
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName);

        (User user, Account account) = CreateTestUserAndAccount(204, "MacBoundaryTester");
        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        string standardMac = "00:1A:2B:3C:4D:5E";
        string hyphenMac = "00-1A-2B-3C-4D-5E";
        string exact50CharMac = new string('F', 46) + "-123";
        exact50CharMac = exact50CharMac[..50];
        exact50CharMac.Length.Should().Be(50);

        account.AddMACAddress(standardMac);
        account.AddMACAddress(hyphenMac);
        account.AddMACAddress(exact50CharMac);
        await db.SaveChangesAsync();

        // Act: Reload
        using MerrickContext verifyDb = TestDbContextFactory.CreateInMemoryContext(dbName);
        Account? reloaded = await verifyDb.Accounts
            .Include(a => a.MACAddressCollection)
            .FirstOrDefaultAsync(a => a.ID == account.ID);

        // Assert
        reloaded.Should().NotBeNull();
        reloaded!.MACAddressStrings.Should().Contain(standardMac);
        reloaded.MACAddressStrings.Should().Contain(hyphenMac);
        reloaded.MACAddressStrings.Should().Contain(exact50CharMac);
        reloaded.MACAddressCollection.First(m => m.MACAddress == exact50CharMac).MACAddress.Length.Should().Be(50);
    }

    [Fact]
    public async Task AccountBlockedPhrase_200CharPhrase_RoundtripsCleanlyAndClampsExcess()
    {
        // Arrange: MaxLength(200) boundary test for BlockedPhrases
        string dbName = Guid.NewGuid().ToString("N");
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName);

        (User user, Account account) = CreateTestUserAndAccount(205, "PhraseBoundaryTester");
        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        // 1. Exact 200-char phrase
        string exact200Phrase = new string('P', 200);
        account.AddBlockedPhrase(exact200Phrase);

        // 2. Phrase > 200 chars: should be safely clamped to 200 to protect DB
        string excessPhrase = new string('E', 250);
        account.AddBlockedPhrase(excessPhrase);

        await db.SaveChangesAsync();

        // Act: Reload
        using MerrickContext verifyDb = TestDbContextFactory.CreateInMemoryContext(dbName);
        Account? reloaded = await verifyDb.Accounts
            .Include(a => a.BlockedPhrases)
            .FirstOrDefaultAsync(a => a.ID == account.ID);

        // Assert
        reloaded.Should().NotBeNull();
        reloaded!.BlockedPhrases.Should().HaveCount(2);
        reloaded.BlockedPhraseStrings.Should().Contain(exact200Phrase);
        reloaded.BlockedPhrases.First(p => p.Phrase == exact200Phrase).Phrase.Length.Should().Be(200);

        // Clamped phrase
        string expectedClamped = excessPhrase[..200];
        reloaded.BlockedPhraseStrings.Should().Contain(expectedClamped);
        reloaded.BlockedPhrases.First(p => p.Phrase == expectedClamped).Phrase.Length.Should().Be(200);
    }

    [Fact]
    public void Account_SetSelectedStoreItems_TrimsDeduplicatesAndFiltersWhitespace()
    {
        // Arrange
        (User user, Account account) = CreateTestUserAndAccount();

        List<string> input =
        [
            "  c.gold  ",
            "C.GOLD", // Duplicate case-insensitive
            "",
            "   ",
            "taunt.kongor",
            "TAUNT.KONGOR", // Duplicate case-insensitive
            "ai.custom_icon:1"
        ];

        // Act
        account.SetSelectedStoreItems(input);

        // Assert
        account.SelectedStoreItemCodes.Should().BeEquivalentTo(["c.gold", "taunt.kongor", "ai.custom_icon:1"]);
        account.SelectedStoreItems.Should().HaveCount(3);
    }

    [Fact]
    public async Task Account_CascadeDeletion_DeletesAllChildEntities()
    {
        // Arrange
        string dbName = Guid.NewGuid().ToString("N");
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName);

        (User user, Account account) = CreateTestUserAndAccount(206, "CascadeTester");
        account.SetSelectedStoreItems(["item_1", "item_2"]);
        account.AddIPAddress("192.168.1.1");
        account.AddMACAddress("00:11:22:33:44:55");
        account.AddBlockedPhrase("no bad language");

        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        int accountId = account.ID;

        // Verify child rows exist
        db.AccountSelectedStoreItems.Count(i => i.AccountID == accountId).Should().Be(2);
        db.AccountIpAddresses.Count(ip => ip.AccountID == accountId).Should().Be(1);
        db.AccountMacAddresses.Count(m => m.AccountID == accountId).Should().Be(1);
        db.AccountBlockedPhrases.Count(p => p.AccountID == accountId).Should().Be(1);

        // Act: Delete Account
        db.Accounts.Remove(account);
        await db.SaveChangesAsync();

        // Assert: All child entities cascade deleted
        db.AccountSelectedStoreItems.Count(i => i.AccountID == accountId).Should().Be(0);
        db.AccountIpAddresses.Count(ip => ip.AccountID == accountId).Should().Be(0);
        db.AccountMacAddresses.Count(m => m.AccountID == accountId).Should().Be(0);
        db.AccountBlockedPhrases.Count(p => p.AccountID == accountId).Should().Be(0);
    }

    // =========================================================================
    // 4. Repository Reconciliation of 1NF Collections
    // =========================================================================

    [Fact]
    public async Task AccountRepository_UpdateAsync_ReconcilesSelectedStoreItemsCorrectly()
    {
        // Arrange
        string dbName = Guid.NewGuid().ToString("N");
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName);
        AccountRepository repo = new(db, NullLogger<AccountRepository>.Instance);

        (User user, Account account) = CreateTestUserAndAccount(207, "RepoReconciliationTester");
        account.SetSelectedStoreItems(["item_alpha", "item_beta"]);
        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        // Act: Modify items (keep beta, remove alpha, add gamma)
        account.SetSelectedStoreItems(["item_beta", "item_gamma"]);
        await repo.UpdateAsync(account);

        // Assert: Reload and check
        using MerrickContext verifyDb = TestDbContextFactory.CreateInMemoryContext(dbName);
        Account? reloaded = await verifyDb.Accounts
            .Include(a => a.SelectedStoreItems)
            .FirstOrDefaultAsync(a => a.ID == account.ID);

        reloaded.Should().NotBeNull();
        reloaded!.SelectedStoreItemCodes.Should().BeEquivalentTo(["item_beta", "item_gamma"]);
        reloaded.SelectedStoreItemCodes.Should().NotContain("item_alpha");
    }

    [Fact]
    public async Task InventoryRepository_SetAndGetSelectedStoreItems_RoundtripsViaRelationalTable()
    {
        // Arrange
        string dbName = Guid.NewGuid().ToString("N");
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName);
        InventoryRepository repo = new(db, NullLogger<InventoryRepository>.Instance);

        (User user, Account account) = CreateTestUserAndAccount(208, "InventoryRepoTester");
        db.Users.Add(user);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        List<string> desiredItems = ["taunt.kongor", "c.chat_purple", "ai.custom_icon:4"];

        // Act: Set via InventoryRepository
        await repo.SetSelectedStoreItemsAsync(account.ID, desiredItems);

        // Assert: Read via InventoryRepository
        IReadOnlyList<string> retrieved = await repo.GetSelectedStoreItemsAsync(account.ID);
        retrieved.Should().BeEquivalentTo(desiredItems);

        // Assert: Direct query on relational table
        List<string> directFromDb = await db.AccountSelectedStoreItems
            .Where(i => i.AccountID == account.ID)
            .Select(i => i.ItemCode)
            .ToListAsync();
        directFromDb.Should().BeEquivalentTo(desiredItems);
    }
}
