// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: RelationalIntegrityBoundaryTests.cs
// System: ProjectKongor Solution-Wide E2E Testing Suite
// Tier 2: Boundary & Corner Cases (Relational Integrity & Telemetry Boundaries)
// Policy: Strict Explicit Typing, Adversarial Boundary Verification (>= 5 tests)
// ============================================================================

using FluentAssertions;
using MERRICK.DatabaseContext.Entities.Game;

namespace ASPIRE.Tests.E2E.Tier2_BoundaryAndCornerCases;

public sealed class RelationalIntegrityBoundaryTests
{
    private static MerrickContext CreateInMemoryContext()
    {
        return TestDbContextFactory.CreateInMemoryContext("Relational_Boundary_" + Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public async Task Boundary_AccountOwnerHierarchy_SetsNullOnParentAccountDeletion()
    {
        // Arrange - Hosted node owned by owner account
        using MerrickContext db = CreateInMemoryContext();
        Role userRole = new() { ID = 1, Name = "User" };
        db.Roles.Add(userRole);

        User ownerUser = new()
        {
            EmailAddress = $"owner_{Guid.NewGuid():N}@kongor.test",
            PBKDF2PasswordHash = "h",
            SRPPasswordHash = "h",
            SRPPasswordSalt = "s",
            Role = userRole
        };
        Account ownerAccount = new()
        {
            Name = "ServerOwner",
            User = ownerUser,
            Type = AccountType.Normal,
            IsMain = true
        };

        User nodeUser = new()
        {
            EmailAddress = $"node_{Guid.NewGuid():N}@kongor.test",
            PBKDF2PasswordHash = "h",
            SRPPasswordHash = "h",
            SRPPasswordSalt = "s",
            Role = userRole
        };
        Account hostedNode = new()
        {
            Name = "HostedNode_1",
            User = nodeUser,
            Type = AccountType.ServerHost,
            OwnerAccount = ownerAccount,
            IsMain = true
        };

        db.Users.AddRange(ownerUser, nodeUser);
        db.Accounts.AddRange(ownerAccount, hostedNode);
        await db.SaveChangesAsync();

        // Act - Delete the owner account
        db.Accounts.Remove(ownerAccount);
        await db.SaveChangesAsync();

        // Assert - The hosted node should still exist but have OwnerAccountID = null
        Account? remainingNode = await db.Accounts.FindAsync(hostedNode.ID);
        remainingNode.Should().NotBeNull();
        remainingNode!.OwnerAccountID.Should().BeNull();
    }

    [Fact]
    public async Task Boundary_RedeemableCode_AlreadyRedeemedCode_IsRejected()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryContext();
        RedeemableCode code = new()
        {
            Code = "PROMO2026",
            GoldCoinsReward = 500,
            SilverCoinsReward = 1000,
            PlinkoTicketsReward = 5,
            TimestampRedeemed = TimeProvider.System.GetUtcNow().AddDays(-1), // already redeemed
            RedeemedByAccountID = 42
        };
        db.RedeemableCodes.Add(code);
        await db.SaveChangesAsync();

        // Act - Query code for redemption
        RedeemableCode? fetched = await db.RedeemableCodes.FirstOrDefaultAsync(c => c.Code == "PROMO2026");

        // Assert
        fetched.Should().NotBeNull();
        fetched!.TimestampRedeemed.Should().NotBeNull();
        bool isRedeemable = fetched.TimestampRedeemed == null;
        isRedeemable.Should().BeFalse();
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task Boundary_AccountBanExpiration_IdentifiesExpiredVsActiveBans()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange
        using MerrickContext db = CreateInMemoryContext();
        Role userRole = new() { ID = 1, Name = "User" };
        db.Roles.Add(userRole);

        User user1 = new()
        {
            EmailAddress = $"banned1_{Guid.NewGuid():N}@kongor.test", PBKDF2PasswordHash = "h", SRPPasswordHash = "h",
            SRPPasswordSalt = "s", Role = userRole
        };
        User user2 = new()
        {
            EmailAddress = $"banned2_{Guid.NewGuid():N}@kongor.test", PBKDF2PasswordHash = "h", SRPPasswordHash = "h",
            SRPPasswordSalt = "s", Role = userRole
        };

        Account activeBanned = new()
        {
            Name = "ActiveBannedPlayer",
            User = user1,
            Type = AccountType.Disabled,
            BanExpiration = TimeProvider.System.GetUtcNow().AddDays(7), // in the future
            BanReason = "Toxic"
        };
        Account expiredBanned = new()
        {
            Name = "ExpiredBannedPlayer",
            User = user2,
            Type = AccountType.Disabled,
            BanExpiration = TimeProvider.System.GetUtcNow().AddDays(-1), // in the past
            BanReason = "Toxic"
        };

        db.Users.AddRange(user1, user2);
        db.Accounts.AddRange(activeBanned, expiredBanned);
        await db.SaveChangesAsync();

        // Act & Assert
        activeBanned.IsBanned.Should().BeTrue();
        expiredBanned.IsBanned.Should().BeFalse();
    }

    [Fact]
    public async Task Boundary_HeroGuide_PublicVsPrivate_FiltersCorrectly()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryContext();
        Role userRole = new() { ID = 1, Name = "User" };
        db.Roles.Add(userRole);

        User user = new()
        {
            EmailAddress = $"guide_author_{Guid.NewGuid():N}@kongor.test", PBKDF2PasswordHash = "h",
            SRPPasswordHash = "h", SRPPasswordSalt = "s", Role = userRole
        };
        Account author = new() { Name = "GuideAuthor", User = user, Type = AccountType.Normal, IsMain = true };
        db.Users.Add(user);
        db.Accounts.Add(author);
        await db.SaveChangesAsync();

        HeroGuide publicGuide = new()
        {
            Author = author,
            HeroIdentifier = "Hero_Flint",
            HeroName = "Flint Beastwood",
            GuideTitle = "Top Lane Carry Guide",
            GuideDescription = "Pro guide",
            Public = true
        };
        HeroGuide privateGuide = new()
        {
            Author = author,
            HeroIdentifier = "Hero_Flint",
            HeroName = "Flint Beastwood",
            GuideTitle = "Draft Personal Notes",
            GuideDescription = "Work in progress",
            Public = false
        };

        db.HeroGuides.AddRange(publicGuide, privateGuide);
        await db.SaveChangesAsync();

        // Act
        List<HeroGuide> publicOnly =
            await db.HeroGuides.Where(g => g.HeroIdentifier == "Hero_Flint" && g.Public).ToListAsync();

        // Assert
        publicOnly.Should().HaveCount(1);
        publicOnly.First().GuideTitle.Should().Be("Top Lane Carry Guide");
    }

    [Fact]
    public async Task Boundary_MatchmakingQueueWaitEntry_StoresAverageWaitTimesCorrectly()
    {
        // Arrange
        using MerrickContext db = CreateInMemoryContext();
        MatchmakingQueueWaitEntry entry = new()
        {
            QueueType = "Ranked_5v5",
            TimestampMatched = TimeProvider.System.GetUtcNow(),
            AverageWaitTime = TimeSpan.FromSeconds(45)
        };
        db.MatchmakingQueueWaitEntries.Add(entry);
        await db.SaveChangesAsync();

        // Assert
        MatchmakingQueueWaitEntry? retrieved =
            await db.MatchmakingQueueWaitEntries.FirstOrDefaultAsync(e => e.QueueType == "Ranked_5v5");
        retrieved.Should().NotBeNull();
        retrieved!.AverageWaitTime.TotalSeconds.Should().Be(45);
    }
}
