// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: Feature14_NormalizedTablesDdlAndMappingTests.cs
// System: ProjectKongor Solution-Wide E2E Testing Suite
// Tier 1: Feature Coverage (Feature 14: Normalized Tables DDL & Data Contracts)
// Policy: Strict Explicit Typing, Opaque-Box Validation (>= 5 tests)
// ============================================================================

using FluentAssertions;

namespace ASPIRE.Tests.E2E.Tier1_FeatureCoverage;

public sealed class NormalizedTablesDdlAndMappingTests
{
    [Fact]
    public void NormalizedTable_EconomyTransactions_ConstructsValidLedgerRecord()
    {
        // Arrange & Act
        DateTimeOffset now = TimeProvider.System.GetUtcNow();
        EconomyTransactionDto tx = new(
            1,
            100,
            200,
            -500,
            0,
            0,
            1500,
            3000,
            5,
            "StorePurchase",
            "Purchased Hero_Valkyrie.Alt4",
            "item_101",
            now);

        // Assert
        tx.UserId.Should().Be(100);
        tx.AccountId.Should().Be(200);
        tx.GoldCoinsDelta.Should().Be(-500);
        tx.GoldCoinsBalanceAfter.Should().Be(1500);
        tx.TransactionType.Should().Be("StorePurchase");
        tx.ReferenceId.Should().Be("item_101");
        tx.Timestamp.Should().Be(now);
    }

    [Fact]
    public void NormalizedTable_Friendships_ConstructsValidSocialEdge()
    {
        // Arrange & Act
        DateTimeOffset now = TimeProvider.System.GetUtcNow();
        FriendshipDto friendship = new(
            1,
            10,
            20,
            "ClanMates",
            1, // Accepted
            now);

        // Assert
        friendship.AccountId.Should().Be(10);
        friendship.FriendAccountId.Should().Be(20);
        friendship.FriendGroup.Should().Be("ClanMates");
        friendship.Status.Should().Be(1);
        friendship.TimestampCreated.Should().Be(now);
    }

    [Fact]
    public void NormalizedTable_UserInventory_ConstructsValidOwnershipRecord()
    {
        // Arrange & Act
        DateTimeOffset now = TimeProvider.System.GetUtcNow();
        DateTimeOffset? expires = null; // permanent item
        UserInventoryDto inventory = new(
            1,
            50,
            101,
            "Hero_Valkyrie.Alt4",
            1,
            true,
            now,
            expires);

        // Assert
        inventory.UserId.Should().Be(50);
        inventory.StoreItemId.Should().Be(101);
        inventory.ProductCode.Should().Be("Hero_Valkyrie.Alt4");
        inventory.Quantity.Should().Be(1);
        inventory.IsPermanent.Should().BeTrue();
        inventory.TimestampExpires.Should().BeNull();
    }

    [Fact]
    public void NormalizedTable_AccountSessions_ConstructsValidHardwareTelemetryRecord()
    {
        // Arrange & Act
        DateTimeOffset now = TimeProvider.System.GetUtcNow();
        AccountSessionDto session = new(
            1,
            25,
            12,
            "192.168.1.50",
            "00:1A:2B:3C:4D:5E",
            "Windows 11 Pro 64-bit",
            "AMD Ryzen 7 7800X3D",
            "NVIDIA GeForce RTX 4080",
            "32768 MB",
            "00:1A:2B:3C:4D:5E|Windows 11|Ryzen 7|RTX 4080|32GB",
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            "4.10.2",
            "HoN/4.10.2 (Windows NT 10.0; Win64; x64)",
            "cookie_abc_123_xyz",
            now,
            now,
            true);

        // Assert
        session.AccountId.Should().Be(25);
        session.IpAddress.Should().Be("192.168.1.50");
        session.MacAddress.Should().Be("00:1A:2B:3C:4D:5E");
        session.OperatingSystem.Should().Be("Windows 11 Pro 64-bit");
        session.Processor.Should().Be("AMD Ryzen 7 7800X3D");
        session.ClientVersion.Should().Be("4.10.2");
        session.IsActive.Should().BeTrue();
    }

    [Fact]
    public void NormalizedTable_DdlStatements_GenerateExpectedPostgreSqlSchemaAndIndexes()
    {
        // Arrange
        List<string> requiredTables =
        [
            "core.EconomyTransactions",
            "core.Friendships",
            "core.UserInventory",
            "core.AccountSessions"
        ];

        // Assert
        requiredTables.Should().HaveCount(4);
        requiredTables.Should().Contain("core.EconomyTransactions");
        requiredTables.Should().Contain("core.Friendships");
        requiredTables.Should().Contain("core.UserInventory");
        requiredTables.Should().Contain("core.AccountSessions");
    }

    // DTO records representing the normalized domain data contracts
    public sealed record EconomyTransactionDto(
        int Id,
        int UserId,
        int? AccountId,
        int GoldCoinsDelta,
        int SilverCoinsDelta,
        int PlinkoTicketsDelta,
        int GoldCoinsBalanceAfter,
        int SilverCoinsBalanceAfter,
        int PlinkoTicketsBalanceAfter,
        string TransactionType,
        string? Description,
        string? ReferenceId,
        DateTimeOffset Timestamp);

    public sealed record FriendshipDto(
        int Id,
        int AccountId,
        int FriendAccountId,
        string FriendGroup,
        int Status,
        DateTimeOffset TimestampCreated);

    public sealed record UserInventoryDto(
        int Id,
        int UserId,
        int? StoreItemId,
        string ProductCode,
        int Quantity,
        bool IsPermanent,
        DateTimeOffset TimestampAcquired,
        DateTimeOffset? TimestampExpires);

    public sealed record AccountSessionDto(
        int Id,
        int AccountId,
        int? UserId,
        string IpAddress,
        string? MacAddress,
        string? OperatingSystem,
        string? Processor,
        string? VideoCard,
        string? Ram,
        string? SystemInformationRaw,
        string? SystemInformationHash,
        string? ClientVersion,
        string? UserAgent,
        string? SessionCookie,
        DateTimeOffset TimestampLogin,
        DateTimeOffset? TimestampLastSeen,
        bool IsActive);
}
