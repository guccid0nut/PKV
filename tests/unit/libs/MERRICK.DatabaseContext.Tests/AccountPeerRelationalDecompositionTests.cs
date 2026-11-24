// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Utility;
using MERRICK.DatabaseContext.Enumerations;
using MERRICK.DatabaseContext.Persistence;
using MERRICK.DatabaseContext.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace MERRICK.DatabaseContext.Tests;

public class AccountPeerRelationalDecompositionTests
{
    private static async Task<(Account Account1, Account Account2, Account Account3, MerrickContext Context, string DbName)> CreateAccountsAsync()
    {
        string dbName = Guid.NewGuid().ToString("N");
        MerrickContext context = TestDbContextFactory.CreateInMemoryContext(dbName);

        User user = new()
        {
            ID = 1,
            EmailAddress = "testuser@kongor.net",
            PBKDF2PasswordHash = "hash",
            SRPPasswordHash = "srp_h",
            SRPPasswordSalt = "srp_s",
            Role = new Role { ID = 1, Name = "User" }
        };

        Account account1 = new()
        {
            ID = 10,
            UserID = 1,
            User = user,
            Name = "PrimaryAccount"
        };

        Account account2 = new()
        {
            ID = 20,
            UserID = 1,
            User = user,
            Name = "BannedTarget"
        };

        Account account3 = new()
        {
            ID = 30,
            UserID = 1,
            User = user,
            Name = "IgnoredTarget"
        };

        context.Users.Add(user);
        context.Accounts.AddRange(account1, account2, account3);
        await context.SaveChangesAsync();

        return (account1, account2, account3, context, dbName);
    }

    [Fact]
    public async Task AccountPeer_CreateAndRead_PersistsRelationalRecord()
    {
        (Account a1, Account a2, Account a3, MerrickContext db, string dbName) = await CreateAccountsAsync();
        using (db)
        {
            AccountPeer banned = new()
            {
                AccountID = a1.ID,
                PeerAccountID = a2.ID,
                Type = PeerType.Banned,
                Reason = "Toxic Griefing"
            };

            AccountPeer ignored = new()
            {
                AccountID = a1.ID,
                PeerAccountID = a3.ID,
                Type = PeerType.Ignored,
                Reason = "Spamming Chat"
            };

            db.AccountPeers.AddRange(banned, ignored);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db2 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            List<AccountPeer> peers = await db2.AccountPeers
                .Where(p => p.AccountID == a1.ID)
                .OrderBy(p => p.Type)
                .ToListAsync();

            peers.Should().HaveCount(2);
            peers[0].Type.Should().Be(PeerType.Banned);
            peers[0].PeerAccountID.Should().Be(a2.ID);
            peers[0].Reason.Should().Be("Toxic Griefing");

            peers[1].Type.Should().Be(PeerType.Ignored);
            peers[1].PeerAccountID.Should().Be(a3.ID);
            peers[1].Reason.Should().Be("Spamming Chat");

            // Verify navigation on Account
            Account refreshed = await db2.Accounts
                .Include(a => a.Peers)
                .ThenInclude(p => p.PeerAccount)
                .FirstAsync(a => a.ID == a1.ID);

            refreshed.Peers.Should().HaveCount(2);
            refreshed.BannedPeers.Should().HaveCount(1);
            refreshed.BannedPeers[0].ID.Should().Be(a2.ID);
            refreshed.BannedPeers[0].Name.Should().Be("BannedTarget");
            refreshed.BannedPeers[0].BanReason.Should().Be("Toxic Griefing");

            refreshed.IgnoredPeers.Should().HaveCount(1);
            refreshed.IgnoredPeers[0].ID.Should().Be(a3.ID);
            refreshed.IgnoredPeers[0].Name.Should().Be("IgnoredTarget");
        }
    }

    [Fact]
    public async Task AccountPeer_HelperMethods_AddRemoveAndQueryWorkSeamlessly()
    {
        (Account a1, Account a2, Account a3, MerrickContext db, string dbName) = await CreateAccountsAsync();
        using (db)
        {
            a1.AddBannedPeer(a2.ID, "Griefer");
            a1.AddIgnoredPeer(a3.ID, "Spammer");

            a1.HasBannedPeer(a2.ID).Should().BeTrue();
            a1.HasIgnoredPeer(a3.ID).Should().BeTrue();
            a1.HasBannedPeer(a3.ID).Should().BeFalse();

            await db.SaveChangesAsync();
        }

        using (MerrickContext db2 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            Account loaded = await db2.Accounts
                .Include(a => a.Peers)
                .ThenInclude(p => p.PeerAccount)
                .FirstAsync(a => a.ID == a1.ID);

            loaded.HasBannedPeer(a2.ID).Should().BeTrue();
            loaded.HasIgnoredPeer(a3.ID).Should().BeTrue();

            loaded.RemovePeer(a2.ID, PeerType.Banned).Should().BeTrue();
            loaded.HasBannedPeer(a2.ID).Should().BeFalse();
            loaded.Peers.Should().HaveCount(1);

            await db2.SaveChangesAsync();
        }

        using (MerrickContext db3 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            int remaining = await db3.AccountPeers.CountAsync(p => p.AccountID == a1.ID);
            remaining.Should().Be(1);
        }
    }

    [Fact]
    public async Task AccountPeer_AccountDelete_CascadesToPeers()
    {
        (Account a1, Account a2, _, MerrickContext db, string dbName) = await CreateAccountsAsync();
        using (db)
        {
            db.AccountPeers.Add(new AccountPeer
            {
                AccountID = a1.ID,
                PeerAccountID = a2.ID,
                Type = PeerType.Banned,
                Reason = "Cascade Test"
            });
            await db.SaveChangesAsync();
        }

        using (MerrickContext db2 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            Account accountToDelete = await db2.Accounts
                .Include(a => a.Peers)
                .FirstAsync(a => a.ID == a1.ID);

            db2.Accounts.Remove(accountToDelete);
            await db2.SaveChangesAsync();
        }

        using (MerrickContext db3 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            List<AccountPeer> remainingPeers = await db3.AccountPeers
                .Where(p => p.AccountID == a1.ID)
                .ToListAsync();

            remainingPeers.Should().BeEmpty();
        }
    }

    [Fact]
    public void AccountPeer_MetadataModel_VerifiesUniqueConstraintAndForeignKeys()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext(Guid.NewGuid().ToString("N"));
        IEntityType? entityType = db.Model.FindEntityType(typeof(AccountPeer));
        entityType.Should().NotBeNull();

        // Verify Table & Schema
        entityType!.GetSchema().Should().Be("core");
        entityType.GetTableName().Should().Be("AccountPeers");

        // Verify Compound Unique Index
        IForeignKey? accountFk = entityType.GetForeignKeys()
            .FirstOrDefault(fk => fk.Properties.Any(p => p.Name == nameof(AccountPeer.AccountID)));
        accountFk.Should().NotBeNull();
        accountFk!.DeleteBehavior.Should().Be(DeleteBehavior.Cascade);

        IForeignKey? peerAccountFk = entityType.GetForeignKeys()
            .FirstOrDefault(fk => fk.Properties.Any(p => p.Name == nameof(AccountPeer.PeerAccountID)));
        peerAccountFk.Should().NotBeNull();
        peerAccountFk!.DeleteBehavior.Should().Be(DeleteBehavior.Restrict);

        // Verify unique compound index: (AccountID, PeerAccountID, Type)
        IIndex? compoundIndex = entityType.GetIndexes()
            .FirstOrDefault(idx => idx.Properties.Select(p => p.Name).SequenceEqual(new[] { "AccountID", "PeerAccountID", "Type" }));
        compoundIndex.Should().NotBeNull();
        compoundIndex!.IsUnique.Should().BeTrue();
    }

    [Fact]
    public async Task AccountPeer_ExpirationAndMaxLimit_EnforcesSixMonthsAndTwentyFiveLimit()
    {
        (Account a1, Account a2, Account a3, MerrickContext db, string dbName) = await CreateAccountsAsync();
        DateTimeOffset now = TimeProvider.System.GetUtcNow();

        using (db)
        {
            // 1. Verify default expiration is approx 6 months
            a1.AddBannedPeer(a2.ID, "Toxicity");
            AccountPeer bannedPeer = a1.Peers.First(p => p.PeerAccountID == a2.ID);
            bannedPeer.TimestampExpires.Should().NotBeNull();
            bannedPeer.TimestampExpires!.Value.Should().BeOnOrAfter(now.AddMonths(5)).And.BeOnOrBefore(now.AddMonths(7));

            // 2. Add an expired peer and verify active filter
            a1.Peers.Add(new AccountPeer
            {
                AccountID = a1.ID,
                PeerAccountID = 999,
                Type = PeerType.Banned,
                Reason = "Expired Ban",
                TimestampCreated = now.AddMonths(-7),
                TimestampExpires = now.AddDays(-1)
            });

            a1.HasBannedPeer(999).Should().BeFalse();
            a1.BannedPeers.Any(p => p.ID == 999).Should().BeFalse();
            a1.HasBannedPeer(a2.ID).Should().BeTrue();

            // 3. Fill up to 25 banned peers
            for (int i = 100; i < 124; i++) // 24 more + a2 = 25
            {
                a1.AddBannedPeer(i, $"Player {i}");
            }

            a1.BannedPeers.Should().HaveCount(25);

            // 4. Adding 26th active banned peer throws InvalidOperationException
            Action add26th = () => a1.AddBannedPeer(199, "26th ban");
            add26th.Should().Throw<InvalidOperationException>()
                .WithMessage("*maximum limit of 25*");

            // 5. Updating existing ban succeeds and updates reason/expiration
            a1.AddBannedPeer(a2.ID, "Updated Reason");
            bannedPeer.Reason.Should().Be("Updated Reason");
        }
    }
}
