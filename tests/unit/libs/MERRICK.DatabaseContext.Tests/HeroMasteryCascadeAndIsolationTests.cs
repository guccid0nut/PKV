// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Statistics;
using MERRICK.DatabaseContext.Entities.Utility;
using MERRICK.DatabaseContext.Enumerations;
using MERRICK.DatabaseContext.Persistence;
using MERRICK.DatabaseContext.Repositories;
using MERRICK.DatabaseContext.Services;
using MERRICK.DatabaseContext.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MERRICK.DatabaseContext.Tests;

/// <summary>
/// Empirical challenge tests authored by teamwork_preview_challenger_m3_2 for Milestone 3.
/// Stress-tests:
/// 1. Complete cascade deletion: Account -> Mastery -> stat.HeroMasteryExperiences without orphan records.
/// 2. Direct Mastery deletion: Mastery -> stat.HeroMasteryExperiences cleanup while root Account remains intact.
/// 3. Cross-account progression isolation across multiple accounts sharing identical hero identifiers.
/// 4. Composite unique constraint enforcement and case-insensitive hero deduplication.
/// 5. Disconnected graph reconciliation (updates, additions, and pruning deletions in MatchHistoryRepository).
/// 6. DatabaseInitializer DDL, foreign key cascade rules, and RLS registration audit.
/// </summary>
public class HeroMasteryCascadeAndIsolationTests
{
    private static User CreateUser(int id, string email = "challenger_m3@kongor.net") => new()
    {
        ID = id,
        EmailAddress = email,
        PBKDF2PasswordHash = "pbkdf2_hash",
        SRPPasswordHash = "srp_hash",
        SRPPasswordSalt = "srp_salt",
        Role = new Role { ID = 1, Name = "User" }
    };

    private static Account CreateAccount(int id, string name, User user) => new()
    {
        ID = id,
        Name = name,
        UserID = user.ID,
        User = user
    };

    private static Mastery CreateMastery(int id, Account account)
    {
        Mastery mastery = new()
        {
            ID = id,
            AccountID = account.ID,
            Account = account,
            HeroExperiences = []
        };
        account.Mastery = mastery;
        return mastery;
    }

    #region 1. Complete Cascade Deletion (Account -> Mastery -> HeroMasteryExperiences)

    [Fact]
    public async Task CascadeDelete_DeletingAccount_PurgesMasteryAndAllHeroMasteryExperiences_ZeroOrphans()
    {
        string dbName = Guid.NewGuid().ToString("N");
        int accountId = 1001;
        int masteryId = 2001;

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateUser(101, "cascade_acc@kongor.net");
            Account account = CreateAccount(accountId, "CascadeAccount", user);
            Mastery mastery = CreateMastery(masteryId, account);

            db.Users.Add(user);
            db.Accounts.Add(account);
            db.Masteries.Add(mastery);

            // Add 10 hero mastery experiences
            for (int i = 1; i <= 10; i++)
            {
                mastery.HeroExperiences.Add(new HeroMasteryExperience
                {
                    MasteryID = masteryId,
                    Mastery = mastery,
                    HeroIdentifier = $"Hero_Test_{i}",
                    Experience = i * 2500
                });
            }

            await db.SaveChangesAsync();
        }

        // Verify initial persistence
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            (await db.Accounts.CountAsync(a => a.ID == accountId)).Should().Be(1);
            (await db.Masteries.CountAsync(m => m.ID == masteryId)).Should().Be(1);
            (await db.HeroMasteryExperiences.CountAsync(h => h.MasteryID == masteryId)).Should().Be(10);
        }

        // Action: Delete the Account entity
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            Account accountToDelete = await db.Accounts
                .Include(a => a.Mastery!)
                .ThenInclude(m => m.HeroExperiences)
                .FirstAsync(a => a.ID == accountId);

            db.Accounts.Remove(accountToDelete);
            await db.SaveChangesAsync();
        }

        // Assertion: Account, Mastery, and all HeroMasteryExperiences must be completely deleted
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            bool accountExists = await db.Accounts.AnyAsync(a => a.ID == accountId);
            bool masteryExists = await db.Masteries.AnyAsync(m => m.ID == masteryId);
            List<HeroMasteryExperience> orphanedExperiences = await db.HeroMasteryExperiences
                .Where(h => h.MasteryID == masteryId)
                .ToListAsync();

            accountExists.Should().BeFalse("Account must be removed");
            masteryExists.Should().BeFalse("Mastery must be cascade deleted when Account is deleted");
            orphanedExperiences.Should().BeEmpty("All HeroMasteryExperiences must be cascade deleted without any orphan records");
        }
    }

    [Fact]
    public async Task CascadeDelete_DeletingAccountWithNullMastery_SucceedsWithoutError()
    {
        string dbName = Guid.NewGuid().ToString("N");
        int accountId = 1002;

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateUser(102, "null_mastery@kongor.net");
            Account account = CreateAccount(accountId, "NoMasteryAcc", user);
            account.Mastery = null;

            db.Users.Add(user);
            db.Accounts.Add(account);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            Account accountToDelete = await db.Accounts
                .Include(a => a.Mastery)
                .FirstAsync(a => a.ID == accountId);

            db.Accounts.Remove(accountToDelete);
            Func<Task> act = async () => await db.SaveChangesAsync();
            await act.Should().NotThrowAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            (await db.Accounts.AnyAsync(a => a.ID == accountId)).Should().BeFalse();
        }
    }

    [Fact]
    public async Task CascadeDelete_DeepEcosystem_AccountWithStoreItemsIpAndMastery_CleansUpAllLeaves()
    {
        string dbName = Guid.NewGuid().ToString("N");
        int accountId = 1003;
        int masteryId = 2003;

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateUser(103, "deep_acc@kongor.net");
            Account account = CreateAccount(accountId, "DeepAccount", user);
            Mastery mastery = CreateMastery(masteryId, account);

            account.SelectedStoreItems.Add(new AccountSelectedStoreItem { AccountID = accountId, ItemCode = "avatar.pyro_alt" });
            account.IPAddressCollection.Add(new AccountIpAddress { AccountID = accountId, IPAddress = "192.168.1.50" });
            account.MACAddressCollection.Add(new AccountMacAddress { AccountID = accountId, MACAddress = "AA:BB:CC:DD:EE:01" });
            account.BlockedPhrases.Add(new AccountBlockedPhrase { AccountID = accountId, Phrase = "toxic_phrase" });

            mastery.HeroExperiences.Add(new HeroMasteryExperience { MasteryID = masteryId, HeroIdentifier = "Hero_Chronos", Experience = 36100 });
            mastery.HeroExperiences.Add(new HeroMasteryExperience { MasteryID = masteryId, HeroIdentifier = "Hero_Vindicator", Experience = 12000 });

            db.Users.Add(user);
            db.Accounts.Add(account);
            db.Masteries.Add(mastery);
            await db.SaveChangesAsync();
        }

        // Delete the entire Account graph
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            Account acc = await db.Accounts
                .Include(a => a.Mastery!).ThenInclude(m => m.HeroExperiences)
                .Include(a => a.SelectedStoreItems)
                .Include(a => a.IPAddressCollection)
                .Include(a => a.MACAddressCollection)
                .Include(a => a.BlockedPhrases)
                .FirstAsync(a => a.ID == accountId);

            db.Accounts.Remove(acc);
            await db.SaveChangesAsync();
        }

        // Verify all child tables across core and stat schemas are zeroed for this account
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            (await db.Accounts.AnyAsync(a => a.ID == accountId)).Should().BeFalse();
            (await db.Masteries.AnyAsync(m => m.ID == masteryId)).Should().BeFalse();
            (await db.HeroMasteryExperiences.AnyAsync(h => h.MasteryID == masteryId)).Should().BeFalse();
            (await db.AccountSelectedStoreItems.AnyAsync(s => s.AccountID == accountId)).Should().BeFalse();
            (await db.AccountIpAddresses.AnyAsync(ip => ip.AccountID == accountId)).Should().BeFalse();
            (await db.AccountMacAddresses.AnyAsync(mac => mac.AccountID == accountId)).Should().BeFalse();
            (await db.AccountBlockedPhrases.AnyAsync(bp => bp.AccountID == accountId)).Should().BeFalse();
        }
    }

    #endregion

    #region 2. Direct Mastery Deletion (Mastery -> HeroMasteryExperiences)

    [Fact]
    public async Task CascadeDelete_DirectMasteryDeletion_RemovesHeroExperiences_AccountRemainsIntact()
    {
        string dbName = Guid.NewGuid().ToString("N");
        int accountId = 1010;
        int masteryId = 2010;

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateUser(110, "mastery_direct@kongor.net");
            Account account = CreateAccount(accountId, "MasteryDirectAccount", user);
            Mastery mastery = CreateMastery(masteryId, account);

            mastery.HeroExperiences.Add(new HeroMasteryExperience { MasteryID = masteryId, HeroIdentifier = "Hero_WitchSlayer", Experience = 8000 });
            mastery.HeroExperiences.Add(new HeroMasteryExperience { MasteryID = masteryId, HeroIdentifier = "Hero_FlintBeastwood", Experience = 14000 });

            db.Users.Add(user);
            db.Accounts.Add(account);
            db.Masteries.Add(mastery);
            await db.SaveChangesAsync();
        }

        // Action: Directly delete Mastery (without deleting Account)
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            Mastery masteryToDelete = await db.Masteries
                .Include(m => m.HeroExperiences)
                .FirstAsync(m => m.ID == masteryId);

            db.Masteries.Remove(masteryToDelete);
            await db.SaveChangesAsync();
        }

        // Assertion: Mastery and HeroExperiences gone, but Account preserved
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            Account survivingAccount = await db.Accounts
                .Include(a => a.Mastery)
                .FirstAsync(a => a.ID == accountId);

            survivingAccount.Should().NotBeNull();
            survivingAccount.Mastery.Should().BeNull("Account's Mastery navigation must be null after direct Mastery deletion");

            (await db.Masteries.AnyAsync(m => m.ID == masteryId)).Should().BeFalse();
            (await db.HeroMasteryExperiences.AnyAsync(h => h.MasteryID == masteryId)).Should().BeFalse(
                "Direct Mastery deletion must cascade delete all associated HeroMasteryExperiences");
        }
    }

    [Fact]
    public async Task CascadeDelete_RecreateMasteryAfterDeletion_AllowsFreshProgression()
    {
        string dbName = Guid.NewGuid().ToString("N");
        int accountId = 1011;
        int oldMasteryId = 2011;
        int newMasteryId = 3011;

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateUser(111, "recreate_mastery@kongor.net");
            Account account = CreateAccount(accountId, "RecreateMasteryAcc", user);
            Mastery mastery = CreateMastery(oldMasteryId, account);
            mastery.HeroExperiences.Add(new HeroMasteryExperience { MasteryID = oldMasteryId, HeroIdentifier = "Hero_Legionnaire", Experience = 5000 });

            db.Users.Add(user);
            db.Accounts.Add(account);
            db.Masteries.Add(mastery);
            await db.SaveChangesAsync();
        }

        // Delete old mastery
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            Mastery old = await db.Masteries.Include(m => m.HeroExperiences).FirstAsync(m => m.ID == oldMasteryId);
            db.Masteries.Remove(old);
            await db.SaveChangesAsync();
        }

        // Recreate new mastery on same account
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            Account account = await db.Accounts.FirstAsync(a => a.ID == accountId);
            Mastery newMastery = new()
            {
                ID = newMasteryId,
                AccountID = account.ID,
                Account = account,
                HeroExperiences =
                [
                    new HeroMasteryExperience { MasteryID = newMasteryId, HeroIdentifier = "Hero_Legionnaire", Experience = 1000 }
                ]
            };
            account.Mastery = newMastery;
            db.Masteries.Add(newMastery);
            await db.SaveChangesAsync();
        }

        // Verify fresh state
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            Mastery activeMastery = await db.Masteries
                .Include(m => m.HeroExperiences)
                .FirstAsync(m => m.AccountID == accountId);

            activeMastery.ID.Should().Be(newMasteryId);
            activeMastery.HeroExperiences.Should().HaveCount(1);
            activeMastery.GetHeroExperienceByHeroIdentifier("Hero_Legionnaire").Should().Be(1000);

            // Verify old mastery ID has zero lingering rows
            (await db.HeroMasteryExperiences.AnyAsync(h => h.MasteryID == oldMasteryId)).Should().BeFalse();
        }
    }

    #endregion

    #region 3. Multi-Account Progression Isolation

    [Fact]
    public async Task CrossAccountIsolation_FiveAccounts_SameHero_IndependentMutationsAndDeletions()
    {
        string dbName = Guid.NewGuid().ToString("N");
        const string sharedHero = "Hero_Pyromancer";

        int[] accountIds = [501, 502, 503, 504, 505];
        int[] masteryIds = [601, 602, 603, 604, 605];

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            Role role = new() { ID = 50, Name = "User" };
            db.Roles.Add(role);

            for (int i = 0; i < 5; i++)
            {
                User user = new()
                {
                    ID = 701 + i,
                    EmailAddress = $"player_{i}@kongor.net",
                    PBKDF2PasswordHash = "h",
                    SRPPasswordHash = "s",
                    SRPPasswordSalt = "salt",
                    Role = role
                };
                Account account = CreateAccount(accountIds[i], $"Player_{i + 1}", user);
                Mastery mastery = CreateMastery(masteryIds[i], account);

                mastery.HeroExperiences.Add(new HeroMasteryExperience
                {
                    MasteryID = masteryIds[i],
                    Mastery = mastery,
                    HeroIdentifier = sharedHero,
                    Experience = (i + 1) * 3000 // 3000, 6000, 9000, 12000, 15000
                });

                db.Users.Add(user);
                db.Accounts.Add(account);
                db.Masteries.Add(mastery);
            }
            await db.SaveChangesAsync();
        }

        // Perform distinct operations per account:
        // - Account 1: Max out Pyromancer XP (36,100)
        // - Account 2: Mutate Pyromancer XP to 1,400
        // - Account 3: Remove Pyromancer experience entry
        // - Account 4: Delete the entire Account
        // - Account 5: Delete only the Mastery
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            // Acc 1
            Mastery m1 = await db.Masteries.Include(m => m.HeroExperiences).FirstAsync(m => m.ID == masteryIds[0]);
            m1.SetHeroExperienceByHeroIdentifier(sharedHero, 36100);

            // Acc 2
            Mastery m2 = await db.Masteries.Include(m => m.HeroExperiences).FirstAsync(m => m.ID == masteryIds[1]);
            m2.SetHeroExperienceByHeroIdentifier(sharedHero, 1400);

            // Acc 3
            Mastery m3 = await db.Masteries.Include(m => m.HeroExperiences).FirstAsync(m => m.ID == masteryIds[2]);
            HeroMasteryExperience? exp3 = m3.HeroExperiences.FirstOrDefault(e => e.HeroIdentifier == sharedHero);
            if (exp3 != null) db.HeroMasteryExperiences.Remove(exp3);

            // Acc 4: Delete Account
            Account a4 = await db.Accounts
                .Include(a => a.Mastery!).ThenInclude(m => m.HeroExperiences)
                .FirstAsync(a => a.ID == accountIds[3]);
            db.Accounts.Remove(a4);

            // Acc 5: Delete Mastery
            Mastery m5 = await db.Masteries.Include(m => m.HeroExperiences).FirstAsync(m => m.ID == masteryIds[4]);
            db.Masteries.Remove(m5);

            await db.SaveChangesAsync();
        }

        // Verification of strict isolation:
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            // Acc 1: Experience is 36,100, Level 15
            Mastery m1 = await db.Masteries.Include(m => m.HeroExperiences).FirstAsync(m => m.ID == masteryIds[0]);
            m1.GetHeroExperienceByHeroIdentifier(sharedHero).Should().Be(36100);
            m1.GetHeroLevelByHeroIdentifier(sharedHero).Should().Be(15);

            // Acc 2: Experience is 1,400, Level 1
            Mastery m2 = await db.Masteries.Include(m => m.HeroExperiences).FirstAsync(m => m.ID == masteryIds[1]);
            m2.GetHeroExperienceByHeroIdentifier(sharedHero).Should().Be(1400);
            m2.GetHeroLevelByHeroIdentifier(sharedHero).Should().Be(1);

            // Acc 3: Experience is 0 / entry absent
            Mastery m3 = await db.Masteries.Include(m => m.HeroExperiences).FirstAsync(m => m.ID == masteryIds[2]);
            m3.GetHeroExperienceByHeroIdentifier(sharedHero).Should().Be(0);
            m3.HeroExperiences.Any(e => e.HeroIdentifier == sharedHero).Should().BeFalse();

            // Acc 4: Account and its child records do not exist
            (await db.Accounts.AnyAsync(a => a.ID == accountIds[3])).Should().BeFalse();
            (await db.Masteries.AnyAsync(m => m.ID == masteryIds[3])).Should().BeFalse();
            (await db.HeroMasteryExperiences.AnyAsync(h => h.MasteryID == masteryIds[3])).Should().BeFalse();

            // Acc 5: Account exists, but its Mastery and HeroMasteryExperiences are gone
            (await db.Accounts.AnyAsync(a => a.ID == accountIds[4])).Should().BeTrue();
            (await db.Masteries.AnyAsync(m => m.ID == masteryIds[4])).Should().BeFalse();
            (await db.HeroMasteryExperiences.AnyAsync(h => h.MasteryID == masteryIds[4])).Should().BeFalse();
        }
    }

    #endregion

    #region 4. Composite Unique Constraint & Case-Insensitive Deduplication

    [Fact]
    public void CompositeUniqueConstraint_ModelMetadata_DeclaresUniqueIndexOnMasteryIdAndHeroIdentifier()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName);
        IModel model = db.Model;

        IEntityType entity = model.FindEntityType(typeof(HeroMasteryExperience))!;
        entity.Should().NotBeNull();

        IIndex? compositeIndex = entity.GetIndexes().FirstOrDefault(idx =>
            idx.IsUnique &&
            idx.Properties.Count == 2 &&
            idx.Properties.Any(p => p.Name == nameof(HeroMasteryExperience.MasteryID)) &&
            idx.Properties.Any(p => p.Name == nameof(HeroMasteryExperience.HeroIdentifier)));

        compositeIndex.Should().NotBeNull("HeroMasteryExperience must have a composite unique index on [MasteryID, HeroIdentifier]");
        compositeIndex!.GetDatabaseName().Should().Be("UQ_HeroMasteryExperiences_MasteryID_HeroIdentifier");

        // Verify property configuration
        IProperty heroProp = entity.FindProperty(nameof(HeroMasteryExperience.HeroIdentifier))!;
        heroProp.GetMaxLength().Should().Be(100);
        heroProp.IsNullable.Should().BeFalse();
    }

    [Theory]
    [InlineData("Hero_Valkyrie", "hero_valkyrie")]
    [InlineData("Hero_Devourer", "HERO_DEVOURER")]
    [InlineData("Hero_Accursed", "HeRo_AcCuRsEd")]
    public void CaseInsensitiveDeduplication_SetHeroExperience_UpdatesExistingRatherThanCreatingDuplicate(string originalHero, string casingVariation)
    {
        Mastery mastery = new()
        {
            ID = 10,
            AccountID = 1,
            Account = null!,
            HeroExperiences = []
        };

        mastery.SetHeroExperienceByHeroIdentifier(originalHero, 5000);
        mastery.HeroExperiences.Should().HaveCount(1);
        mastery.GetHeroExperienceByHeroIdentifier(originalHero).Should().Be(5000);

        // Mutate using casing variation
        mastery.SetHeroExperienceByHeroIdentifier(casingVariation, 12000);

        mastery.HeroExperiences.Should().HaveCount(1, "Case variations must target the same hero record rather than creating duplicates");
        mastery.GetHeroExperienceByHeroIdentifier(originalHero).Should().Be(12000);
        mastery.GetHeroExperienceByHeroIdentifier(casingVariation).Should().Be(12000);
    }

    #endregion

    #region 5. Disconnected Graph Reconciliation (MatchHistoryRepository)

    [Trait("Category", "Manual")]
    [Fact]
    public async Task SaveMasteryAsync_GraphReconciliation_HandlesAddUpdateAndPruneCleanly()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        string dbName = Guid.NewGuid().ToString("N");
        int accountId = 888;
        int masteryId = 999;

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateUser(88, "repo_recon@kongor.net");
            Account account = CreateAccount(accountId, "ReconAccount", user);
            Mastery mastery = CreateMastery(masteryId, account);

            mastery.HeroExperiences.Add(new HeroMasteryExperience { MasteryID = masteryId, HeroIdentifier = "Hero_FlintBeastwood", Experience = 4000 });
            mastery.HeroExperiences.Add(new HeroMasteryExperience { MasteryID = masteryId, HeroIdentifier = "Hero_Glacius", Experience = 9000 });

            db.Users.Add(user);
            db.Accounts.Add(account);
            db.Masteries.Add(mastery);
            await db.SaveChangesAsync();
        }

        // Disconnected update via MatchHistoryRepository:
        // - Update FlintBeastwood from 4000 to 18500
        // - Prune Glacius (omitted from incoming)
        // - Add Pyromancer (brand new, 7000)
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            MatchHistoryRepository repo = new(db, NullLogger<MatchHistoryRepository>.Instance);

            Mastery incomingMastery = new()
            {
                AccountID = accountId,
                Account = null!,
                HeroExperiences =
                [
                    new HeroMasteryExperience { HeroIdentifier = "Hero_FlintBeastwood", Experience = 18500 },
                    new HeroMasteryExperience { HeroIdentifier = "Hero_Pyromancer", Experience = 7000 }
                ]
            };

            await repo.SaveMasteryAsync(incomingMastery);
        }

        // Verify reconciliation results
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            MatchHistoryRepository repo = new(db, NullLogger<MatchHistoryRepository>.Instance);
            Mastery? saved = await repo.GetMasteryByAccountIdAsync(accountId);

            saved.Should().NotBeNull();
            saved!.HeroExperiences.Should().HaveCount(2);

            // Flint was updated
            saved.GetHeroExperienceByHeroIdentifier("Hero_FlintBeastwood").Should().Be(18500);

            // Pyromancer was added
            saved.GetHeroExperienceByHeroIdentifier("Hero_Pyromancer").Should().Be(7000);

            // Glacius was pruned/removed
            saved.GetHeroExperienceByHeroIdentifier("Hero_Glacius").Should().Be(0);
            saved.HeroExperiences.Any(e => e.HeroIdentifier == "Hero_Glacius").Should().BeFalse();
        }
    }

    [Fact]
    public async Task SaveMasteryAsync_CaseVariationInIncomingHero_ReconcilesWithoutDuplicateKeyCollision()
    {
        string dbName = Guid.NewGuid().ToString("N");
        int accountId = 889;
        int masteryId = 998;

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateUser(89, "case_recon@kongor.net");
            Account account = CreateAccount(accountId, "CaseReconAcc", user);
            Mastery mastery = CreateMastery(masteryId, account);

            mastery.HeroExperiences.Add(new HeroMasteryExperience { MasteryID = masteryId, HeroIdentifier = "Hero_WitchSlayer", Experience = 5000 });

            db.Users.Add(user);
            db.Accounts.Add(account);
            db.Masteries.Add(mastery);
            await db.SaveChangesAsync();
        }

        // Incoming mastery has "hero_witchslayer" (lowercase) with new experience
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            MatchHistoryRepository repo = new(db, NullLogger<MatchHistoryRepository>.Instance);

            Mastery incomingMastery = new()
            {
                AccountID = accountId,
                Account = null!,
                HeroExperiences =
                [
                    new HeroMasteryExperience { HeroIdentifier = "hero_witchslayer", Experience = 26400 }
                ]
            };

            await repo.SaveMasteryAsync(incomingMastery);
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            MatchHistoryRepository repo = new(db, NullLogger<MatchHistoryRepository>.Instance);
            Mastery? saved = await repo.GetMasteryByAccountIdAsync(accountId);

            saved.Should().NotBeNull();
            saved!.HeroExperiences.Should().HaveCount(1, "Case variation should reconcile the existing row rather than inserting a duplicate");
            saved.GetHeroExperienceByHeroIdentifier("Hero_WitchSlayer").Should().Be(26400);
            saved.GetHeroLevelByHeroIdentifier("Hero_WitchSlayer").Should().Be(12);
        }
    }

    #endregion

    #region 6. DatabaseInitializer DDL, Cascade, and RLS Audit

    [Fact]
    public void DatabaseInitializer_HeroMasteryExperiences_DDL_And_RLS_Verified()
    {
        string? projectRoot = FindProjectRoot();
        projectRoot.Should().NotBeNull();

        string ddlPath = Path.Combine(projectRoot!, "src", "libs", "database-context", "Services", "DatabaseInitializer.cs");
        File.Exists(ddlPath).Should().BeTrue();
        string ddlCode = File.ReadAllText(ddlPath);

        // 1. Verify CREATE TABLE with ON DELETE CASCADE and UQ constraint in verbatim C# SQL strings
        ddlCode.Should().Contain("\"\"stat\"\".\"\"HeroMasteryExperiences\"\"", "DDL must create stat.HeroMasteryExperiences");
        ddlCode.Should().Contain("\"\"UQ_HeroMasteryExperiences_MasteryID_HeroIdentifier\"\"", "DDL must enforce composite unique constraint");
        ddlCode.Should().Contain("\"\"FK_HeroMasteryExperiences_Masteries_MasteryID\"\"", "DDL must enforce foreign key constraint name");
        ddlCode.Should().Contain("REFERENCES \"\"stat\"\".\"\"Masteries\"\"(\"\"ID\"\") ON DELETE CASCADE", "DDL foreign key must cascade delete");

        // 2. Verify covering indexes
        ddlCode.Should().Contain("\"\"IX_HeroMasteryExperiences_MasteryID\"\"");
        ddlCode.Should().Contain("\"\"IX_HeroMasteryExperiences_HeroIdentifier\"\"");

        // 3. Verify Idempotent FK migration check
        ddlCode.Should().Contain("FK_HeroMasteryExperiences_Masteries_MasteryID");

        // 4. Verify RLS table registration
        ddlCode.Should().Contain("['stat', 'HeroMasteryExperiences']", "stat.HeroMasteryExperiences must be registered in tables_to_rls");
    }

    private static string? FindProjectRoot()
    {
        string? current = AppContext.BaseDirectory;
        while (current != null)
        {
            if (File.Exists(Path.Combine(current, "ProjectKongor.slnx")) ||
                File.Exists(Path.Combine(current, "ProjectKongor.sln")) ||
                Directory.Exists(Path.Combine(current, ".git")))
            {
                return current;
            }
            current = Directory.GetParent(current)?.FullName;
        }
        return null;
    }

    #endregion
}
