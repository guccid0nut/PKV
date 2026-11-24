// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Statistics;
using MERRICK.DatabaseContext.Entities.Utility;
using MERRICK.DatabaseContext.Persistence;
using MERRICK.DatabaseContext.Repositories;
using MERRICK.DatabaseContext.Tests.Infrastructure;
using NEXUS.Contracts.Domain;
using Xunit;

namespace MERRICK.DatabaseContext.Tests;

public class InviteRepositoryTests
{
    private static User CreateTestUser(int id, string email, bool isStaff = false)
    {
        var user = new User
        {
            ID = id,
            EmailAddress = email,
            SRPPasswordSalt = "salt",
            SRPPasswordHash = "srphash",
            PBKDF2PasswordHash = "pbkdf2hash",
            RoleID = isStaff ? 2 : 1
        };
        var account = new Account
        {
            ID = id * 10,
            UserID = id,
            User = user,
            Name = $"Player{id}",
            Type = isStaff ? AccountType.Staff : AccountType.Normal,
            IsMain = true
        };
        user.Accounts = [account];
        return user;
    }

    [Fact]
    public async Task GetUserQuotaStatus_Under100Games_CannotGenerate()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            var user = CreateTestUser(1, "p1@k.net");
            db.Users.Add(user);
            db.AccountStatistics.Add(new AccountStatistics
            {
                AccountID = 10,
                Type = AccountStatisticsType.Matchmaking,
                MatchesPlayed = 99
            });
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            InviteRepository repo = new(db);
            var quota = await repo.GetUserQuotaStatusAsync(1);

            quota.CanGenerate.Should().BeFalse();
            quota.TotalGames.Should().Be(99);
            quota.MaxAllowedActiveCodes.Should().Be(0);
            quota.RemainingCapacity.Should().Be(0);
        }
    }

    [Fact]
    public async Task GetUserQuotaStatus_TierCalculations_MatchExpectations()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            var user = CreateTestUser(2, "p2@k.net");
            db.Users.Add(user);
            db.AccountStatistics.Add(new AccountStatistics
            {
                AccountID = 20,
                Type = AccountStatisticsType.Matchmaking,
                MatchesPlayed = 250 // Tier 2 (up to 2 codes)
            });
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            InviteRepository repo = new(db);
            var quota = await repo.GetUserQuotaStatusAsync(2);

            quota.CanGenerate.Should().BeTrue();
            quota.TotalGames.Should().Be(250);
            quota.MaxAllowedActiveCodes.Should().Be(2);
            quota.ActiveUnusedCodes.Should().Be(0);
            quota.RemainingCapacity.Should().Be(2);
        }
    }

    [Fact]
    public async Task GetUserQuotaStatus_StaffAccount_AlwaysGetsFiveCodes()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            var staff = CreateTestUser(3, "staff@k.net", isStaff: true);
            db.Users.Add(staff);
            // 0 matches played
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            InviteRepository repo = new(db);
            var quota = await repo.GetUserQuotaStatusAsync(3);

            quota.CanGenerate.Should().BeTrue();
            quota.MaxAllowedActiveCodes.Should().Be(5);
            quota.RemainingCapacity.Should().Be(5);
        }
    }

    [Fact]
    public async Task CreateInviteCode_CreatesValidFormattedCode_AndDecrementsQuota()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            var user = CreateTestUser(4, "p4@k.net");
            db.Users.Add(user);
            db.AccountStatistics.Add(new AccountStatistics
            {
                AccountID = 40,
                Type = AccountStatisticsType.Matchmaking,
                MatchesPlayed = 100 // Tier 1: 1 code allowed
            });
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            InviteRepository repo = new(db);
            var invite = await repo.CreateInviteCodeAsync(4, "127.0.0.1");

            invite.Should().NotBeNull();
            invite!.Code.Should().StartWith("KONGOR-");
            invite.Code.Should().HaveLength(16);

            // Generating again should fail because holding cap 1 is reached
            var invite2 = await repo.CreateInviteCodeAsync(4, "127.0.0.1");
            invite2.Should().BeNull();
        }
    }

    [Fact]
    public async Task ValidateAndRedeemInviteCode_WorksAndTracksLineage()
    {
        string dbName = Guid.NewGuid().ToString("N");
        string inviteCode;

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            var inviter = CreateTestUser(5, "inviter@k.net", isStaff: true);
            var invitee = CreateTestUser(6, "invitee@k.net");
            db.Users.AddRange(inviter, invitee);
            await db.SaveChangesAsync();

            InviteRepository repo = new(db);
            var invite = await repo.CreateInviteCodeAsync(5, "127.0.0.1");
            inviteCode = invite!.Code;
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            InviteRepository repo = new(db);

            // 1. Validation should succeed
            var validation = await repo.ValidateInviteCodeAsync(inviteCode);
            validation.IsValid.Should().BeTrue();

            // 2. Redemption should succeed
            var redeemed = await repo.RedeemInviteCodeAsync(inviteCode, 6, 60, "127.0.0.1");
            redeemed.Should().BeTrue();

            // 3. User 6 should have InvitedByUserID set to 5
            var user6 = await db.Users.FindAsync(6);
            user6.Should().NotBeNull();
            user6!.InvitedByUserID.Should().Be(5);

            // 4. Second redemption should fail
            var redeemedAgain = await repo.RedeemInviteCodeAsync(inviteCode, 7, 70, "127.0.0.1");
            redeemedAgain.Should().BeFalse();

            // 5. Subsequent validation should show invalid
            var validationAfter = await repo.ValidateInviteCodeAsync(inviteCode);
            validationAfter.IsValid.Should().BeFalse();
        }
    }

    [Fact]
    public async Task LineageTreeAndPruning_TracksLineageAndRevokesUnusedCodes()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            // Root User 10 invites User 20, User 20 invites User 30
            var u10 = CreateTestUser(10, "u10@k.net", isStaff: true);
            var u20 = CreateTestUser(20, "u20@k.net");
            u20.InvitedByUserID = 10;
            var u30 = CreateTestUser(30, "u30@k.net");
            u30.InvitedByUserID = 20;

            db.Users.AddRange(u10, u20, u30);

            // Add unredeemed code from u20
            db.UserInvites.Add(new UserInvite
            {
                Code = "KONGOR-TEST-PEND",
                CreatedByUserID = 20,
                TimestampCreated = TimeProvider.System.GetUtcNow(),
                IsRevoked = false
            });

            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            InviteRepository repo = new(db);

            // Build tree from u10
            var lineageUsers = await repo.GetInviteLineageTreeAsync(10);
            lineageUsers.Should().NotBeNull();
            lineageUsers.Select(u => u.ID).Should().Contain([10, 20, 30]);

            // Revoke unused codes for u20
            var revokedCount = await repo.RevokeUnusedInvitesForUserAsync(20, "Disciplinary action");
            revokedCount.Should().Be(1);

            // Verify code is revoked
            var code = db.UserInvites.First(i => i.Code == "KONGOR-TEST-PEND");
            code.IsRevoked.Should().BeTrue();
            code.RevocationReason.Should().Be("Disciplinary action");
        }
    }

    [Fact]
    public async Task SharedWeeklyInviteCode_Kongor1111_AllowsMultiplePlayersForOneWeek_ThenExpires()
    {
        // protects: KONGOR-1111 multi-use community invite code works for unlimited players for 7 days and expires after 1 week without consuming staff personal quota.
        // fails_when: First player redemption sets TimestampRedeemed burning KONGOR-1111, or expiration after 7 days is not enforced.
        // seam: Real SeedDataHandlers.EnsureSharedInviteCodesAsync + InviteRepository against MerrickContext with controlled TimeProvider.
        string dbName = Guid.NewGuid().ToString("N");
        ManualTimeProvider clock = new(UserInvite.SharedWeeklyInviteCreatedUtc.AddHours(1));

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            db.Roles.AddRange(
                new Role { ID = 1, Name = Constants.UserRoles.User },
                new Role { ID = 2, Name = Constants.UserRoles.Administrator });

            User admin = CreateTestUser(1, "admin@kongor.local", isStaff: true);
            User playerA = CreateTestUser(101, "playera@k.net");
            User playerB = CreateTestUser(102, "playerb@k.net");
            User playerC = CreateTestUser(103, "playerc@k.net");
            db.Users.AddRange(admin, playerA, playerB, playerC);
            await db.SaveChangesAsync();

            await Handlers.SeedDataHandlers.EnsureSharedInviteCodesAsync(
                db,
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            InviteRepository repo = new(db, timeProvider: clock);

            // Admin's 5 personal invite slots remain untouched by the system multi-use code
            var adminQuota = await repo.GetUserQuotaStatusAsync(1);
            adminQuota.RemainingCapacity.Should().Be(5);
            adminQuota.ActiveUnusedCodes.Should().Be(0);

            // Player A redeems KONGOR-1111 (even lowercase/padded)
            (await repo.RedeemInviteCodeAsync("  kongor-1111 ", 101, 1010, "127.0.0.1")).Should().BeTrue();

            // At +6 days 23 hours, Player B ALSO redeems KONGOR-1111 successfully
            clock.UtcNow = UserInvite.SharedWeeklyInviteCreatedUtc.AddDays(6).AddHours(23);
            var validNearEnd = await repo.ValidateInviteCodeAsync("KONGOR-1111");
            validNearEnd.IsValid.Should().BeTrue();
            (await repo.RedeemInviteCodeAsync("KONGOR-1111", 102, 1020, "127.0.0.2")).Should().BeTrue();

            (await db.Users.FindAsync(101))!.InvitedByUserID.Should().Be(1);
            (await db.Users.FindAsync(102))!.InvitedByUserID.Should().Be(1);

            // Exact 1-week boundary (+7 days): KONGOR-1111 expires automatically
            clock.UtcNow = UserInvite.SharedWeeklyInviteExpiresUtc;
            var expiredAtBoundary = await repo.ValidateInviteCodeAsync("KONGOR-1111");
            expiredAtBoundary.IsValid.Should().BeFalse();
            expiredAtBoundary.Message.Should().Be("This invite code has expired.");
            (await repo.RedeemInviteCodeAsync("KONGOR-1111", 103, 1030, "127.0.0.3")).Should().BeFalse();
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset initialUtc) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = initialUtc;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
