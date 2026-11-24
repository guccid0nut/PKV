// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Statistics;
using MERRICK.DatabaseContext.Entities.Utility;
using MERRICK.DatabaseContext.Persistence;
using MERRICK.DatabaseContext.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using NEXUS.Contracts.Domain;
using Xunit;

namespace MERRICK.DatabaseContext.Tests;

public class HeroMasteryRelationalDecompositionTests
{
    private static async Task<(Account Account, Mastery Mastery, MerrickContext Context, string DbName)> CreateAccountWithMasteryAsync(string? dbName = null)
    {
        string resolvedDbName = dbName ?? Guid.NewGuid().ToString("N");
        MerrickContext context = TestDbContextFactory.CreateInMemoryContext(resolvedDbName);

        User user = new()
        {
            ID = 1,
            EmailAddress = "mastery_test@kongor.net",
            PBKDF2PasswordHash = "hash",
            SRPPasswordHash = "srp_h",
            SRPPasswordSalt = "srp_s",
            Role = new Role { ID = 1, Name = "User" }
        };

        Account account = new()
        {
            ID = 10,
            UserID = 1,
            User = user,
            Name = "MasteryHero"
        };

        Mastery mastery = new()
        {
            ID = 100,
            AccountID = account.ID,
            Account = account,
            HeroExperiences = []
        };
        account.Mastery = mastery;

        context.Users.Add(user);
        context.Accounts.Add(account);
        context.Masteries.Add(mastery);
        await context.SaveChangesAsync();

        return (account, mastery, context, resolvedDbName);
    }

    // =========================================================================
    // 1. Relational CRUD Tests for stat.HeroMasteryExperiences
    // =========================================================================

    [Fact]
    public async Task HeroMasteryExperience_CreateAndRead_PersistsRelationalRecords()
    {
        (Account account, Mastery mastery, MerrickContext db, string dbName) = await CreateAccountWithMasteryAsync();
        using (db)
        {
            HeroMasteryExperience exp1 = new()
            {
                MasteryID = mastery.ID,
                HeroIdentifier = "Hero_Pyromancer",
                Experience = 15000
            };
            HeroMasteryExperience exp2 = new()
            {
                MasteryID = mastery.ID,
                HeroIdentifier = "Hero_Accursed",
                Experience = 36100
            };
            HeroMasteryExperience exp3 = new()
            {
                MasteryID = mastery.ID,
                HeroIdentifier = "Hero_Valkyrie",
                Experience = 5000
            };

            db.HeroMasteryExperiences.AddRange(exp1, exp2, exp3);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db2 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            List<HeroMasteryExperience> experiences = await db2.HeroMasteryExperiences
                .Where(e => e.MasteryID == mastery.ID)
                .OrderBy(e => e.HeroIdentifier)
                .ToListAsync();

            experiences.Should().HaveCount(3);

            experiences[0].HeroIdentifier.Should().Be("Hero_Accursed");
            experiences[0].Experience.Should().Be(36100);
            experiences[0].MasteryID.Should().Be(mastery.ID);

            experiences[1].HeroIdentifier.Should().Be("Hero_Pyromancer");
            experiences[1].Experience.Should().Be(15000);
            experiences[1].MasteryID.Should().Be(mastery.ID);

            experiences[2].HeroIdentifier.Should().Be("Hero_Valkyrie");
            experiences[2].Experience.Should().Be(5000);
            experiences[2].MasteryID.Should().Be(mastery.ID);

            // Verify eager loading via parent navigation collection
            Mastery refreshedMastery = await db2.Masteries
                .Include(m => m.HeroExperiences)
                .FirstAsync(m => m.ID == mastery.ID);

            refreshedMastery.HeroExperiences.Should().HaveCount(3);
            refreshedMastery.HeroExperiences.Select(e => e.HeroIdentifier)
                .Should().Contain(["Hero_Accursed", "Hero_Pyromancer", "Hero_Valkyrie"]);
        }
    }

    [Fact]
    public async Task HeroMasteryExperience_UpdateAndRemove_MutatesRelationalRecord()
    {
        (Account account, Mastery mastery, MerrickContext db, string dbName) = await CreateAccountWithMasteryAsync();
        int recordId;

        using (db)
        {
            HeroMasteryExperience exp = new()
            {
                MasteryID = mastery.ID,
                HeroIdentifier = "Hero_Devourer",
                Experience = 2000
            };
            db.HeroMasteryExperiences.Add(exp);
            await db.SaveChangesAsync();
            recordId = exp.ID;
        }

        // Update experience
        using (MerrickContext db2 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            HeroMasteryExperience record = await db2.HeroMasteryExperiences.FirstAsync(e => e.ID == recordId);
            record.Experience = 14000;
            await db2.SaveChangesAsync();
        }

        // Verify update and delete individual record
        using (MerrickContext db3 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            HeroMasteryExperience record = await db3.HeroMasteryExperiences.FirstAsync(e => e.ID == recordId);
            record.Experience.Should().Be(14000);

            db3.HeroMasteryExperiences.Remove(record);
            await db3.SaveChangesAsync();
        }

        // Verify removal while parent Mastery remains intact
        using (MerrickContext db4 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            (await db4.HeroMasteryExperiences.AnyAsync(e => e.ID == recordId)).Should().BeFalse();
            (await db4.Masteries.AnyAsync(m => m.ID == mastery.ID)).Should().BeTrue("Parent Mastery must remain intact when child experience is deleted");
        }
    }

    [Fact]
    public async Task HeroMasteryExperience_MultipleAccounts_IsolatedProgression()
    {
        string dbName = Guid.NewGuid().ToString("N");
        MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName);

        using (db)
        {
            Role sharedRole = new() { ID = 1, Name = "User" };
            User user1 = new()
            {
                ID = 101,
                EmailAddress = "p1@kongor.net",
                PBKDF2PasswordHash = "h",
                SRPPasswordHash = "s",
                SRPPasswordSalt = "salt",
                Role = sharedRole
            };
            User user2 = new()
            {
                ID = 102,
                EmailAddress = "p2@kongor.net",
                PBKDF2PasswordHash = "h",
                SRPPasswordHash = "s",
                SRPPasswordSalt = "salt",
                Role = sharedRole
            };

            Account acc1 = new() { ID = 201, UserID = 101, User = user1, Name = "PlayerA" };
            Account acc2 = new() { ID = 202, UserID = 102, User = user2, Name = "PlayerB" };

            Mastery m1 = new() { ID = 301, AccountID = 201, Account = acc1, HeroExperiences = [] };
            Mastery m2 = new() { ID = 302, AccountID = 202, Account = acc2, HeroExperiences = [] };

            db.Users.AddRange(user1, user2);
            db.Accounts.AddRange(acc1, acc2);
            db.Masteries.AddRange(m1, m2);

            db.HeroMasteryExperiences.AddRange(
                new HeroMasteryExperience { MasteryID = 301, HeroIdentifier = "Hero_Pyromancer", Experience = 10000 },
                new HeroMasteryExperience { MasteryID = 302, HeroIdentifier = "Hero_Pyromancer", Experience = 25000 }
            );

            await db.SaveChangesAsync();
        }

        using (MerrickContext db2 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            HeroMasteryExperience p1Pyro = await db2.HeroMasteryExperiences
                .FirstAsync(e => e.MasteryID == 301 && e.HeroIdentifier == "Hero_Pyromancer");
            HeroMasteryExperience p2Pyro = await db2.HeroMasteryExperiences
                .FirstAsync(e => e.MasteryID == 302 && e.HeroIdentifier == "Hero_Pyromancer");

            p1Pyro.Experience.Should().Be(10000);
            p2Pyro.Experience.Should().Be(25000);
        }
    }

    // =========================================================================
    // 2. In-Memory Domain Calculation and Mutation Helpers on Mastery
    // =========================================================================

    [Fact]
    public void Mastery_GetHeroExperienceByHeroIdentifier_ReturnsExperienceOrZero()
    {
        Mastery mastery = new()
        {
            ID = 1,
            AccountID = 10,
            Account = null!,
            HeroExperiences =
            [
                new HeroMasteryExperience { MasteryID = 1, HeroIdentifier = "Hero_Accursed", Experience = 18500 }
            ]
        };

        mastery.GetHeroExperienceByHeroIdentifier("Hero_Accursed").Should().Be(18500);
        mastery.GetHeroExperienceByHeroIdentifier("Hero_NonExistent").Should().Be(0);
    }

    [Fact]
    public void Mastery_GetHeroLevelByHeroIdentifier_CalculatesProgressionLevels()
    {
        Mastery mastery = new()
        {
            ID = 1,
            AccountID = 10,
            Account = null!,
            HeroExperiences =
            [
                new HeroMasteryExperience { MasteryID = 1, HeroIdentifier = "Hero_L0", Experience = 0 },
                new HeroMasteryExperience { MasteryID = 1, HeroIdentifier = "Hero_L1", Experience = 1400 },
                new HeroMasteryExperience { MasteryID = 1, HeroIdentifier = "Hero_L2", Experience = 3000 },
                new HeroMasteryExperience { MasteryID = 1, HeroIdentifier = "Hero_L3", Experience = 5000 },
                new HeroMasteryExperience { MasteryID = 1, HeroIdentifier = "Hero_L4", Experience = 7000 },
                new HeroMasteryExperience { MasteryID = 1, HeroIdentifier = "Hero_L5", Experience = 9100 },
                new HeroMasteryExperience { MasteryID = 1, HeroIdentifier = "Hero_L6", Experience = 11300 },
                new HeroMasteryExperience { MasteryID = 1, HeroIdentifier = "Hero_L7", Experience = 13600 },
                new HeroMasteryExperience { MasteryID = 1, HeroIdentifier = "Hero_L8", Experience = 16000 },
                new HeroMasteryExperience { MasteryID = 1, HeroIdentifier = "Hero_L9", Experience = 18500 },
                new HeroMasteryExperience { MasteryID = 1, HeroIdentifier = "Hero_L10", Experience = 21000 },
                new HeroMasteryExperience { MasteryID = 1, HeroIdentifier = "Hero_L11", Experience = 23600 },
                new HeroMasteryExperience { MasteryID = 1, HeroIdentifier = "Hero_L12", Experience = 26400 },
                new HeroMasteryExperience { MasteryID = 1, HeroIdentifier = "Hero_L13", Experience = 29400 },
                new HeroMasteryExperience { MasteryID = 1, HeroIdentifier = "Hero_L14", Experience = 32600 },
                new HeroMasteryExperience { MasteryID = 1, HeroIdentifier = "Hero_L15", Experience = 36100 },
                new HeroMasteryExperience { MasteryID = 1, HeroIdentifier = "Hero_L15_Over", Experience = 99999 }
            ]
        };

        mastery.GetHeroLevelByHeroIdentifier("Hero_L0").Should().Be(0);
        mastery.GetHeroLevelByHeroIdentifier("Hero_L1").Should().Be(1);
        mastery.GetHeroLevelByHeroIdentifier("Hero_L2").Should().Be(2);
        mastery.GetHeroLevelByHeroIdentifier("Hero_L3").Should().Be(3);
        mastery.GetHeroLevelByHeroIdentifier("Hero_L4").Should().Be(4);
        mastery.GetHeroLevelByHeroIdentifier("Hero_L5").Should().Be(5);
        mastery.GetHeroLevelByHeroIdentifier("Hero_L6").Should().Be(6);
        mastery.GetHeroLevelByHeroIdentifier("Hero_L7").Should().Be(7);
        mastery.GetHeroLevelByHeroIdentifier("Hero_L8").Should().Be(8);
        mastery.GetHeroLevelByHeroIdentifier("Hero_L9").Should().Be(9);
        mastery.GetHeroLevelByHeroIdentifier("Hero_L10").Should().Be(10);
        mastery.GetHeroLevelByHeroIdentifier("Hero_L11").Should().Be(11);
        mastery.GetHeroLevelByHeroIdentifier("Hero_L12").Should().Be(12);
        mastery.GetHeroLevelByHeroIdentifier("Hero_L13").Should().Be(13);
        mastery.GetHeroLevelByHeroIdentifier("Hero_L14").Should().Be(14);
        mastery.GetHeroLevelByHeroIdentifier("Hero_L15").Should().Be(15);
        mastery.GetHeroLevelByHeroIdentifier("Hero_L15_Over").Should().Be(15);
        mastery.GetHeroLevelByHeroIdentifier("Hero_Unplayed").Should().Be(0);
    }

    [Fact]
    public void Mastery_SetHeroExperienceByHeroIdentifier_CreatesOrUpdatesChildRecord()
    {
        Mastery mastery = new()
        {
            ID = 1,
            AccountID = 10,
            Account = null!,
            HeroExperiences = []
        };

        // Create new entry
        mastery.SetHeroExperienceByHeroIdentifier("Hero_WitchSlayer", 4000);
        mastery.HeroExperiences.Should().HaveCount(1);
        mastery.GetHeroExperienceByHeroIdentifier("Hero_WitchSlayer").Should().Be(4000);
        mastery.GetHeroLevelByHeroIdentifier("Hero_WitchSlayer").Should().Be(2);

        // Update existing entry without creating duplicates
        mastery.SetHeroExperienceByHeroIdentifier("Hero_WitchSlayer", 8000);
        mastery.HeroExperiences.Should().HaveCount(1, "Updating an existing hero must not append duplicate child rows");
        mastery.GetHeroExperienceByHeroIdentifier("Hero_WitchSlayer").Should().Be(8000);
        mastery.GetHeroLevelByHeroIdentifier("Hero_WitchSlayer").Should().Be(4);

        // Add second distinct hero
        mastery.SetHeroExperienceByHeroIdentifier("Hero_Chronos", 22000);
        mastery.HeroExperiences.Should().HaveCount(2);
        mastery.GetHeroExperienceByHeroIdentifier("Hero_Chronos").Should().Be(22000);
        mastery.GetHeroLevelByHeroIdentifier("Hero_Chronos").Should().Be(10);
    }

    [Fact]
    public void Mastery_TotalMasteryLevel_SumsAllHeroLevelsAccurately()
    {
        Mastery mastery = new()
        {
            ID = 1,
            AccountID = 10,
            Account = null!,
            HeroExperiences =
            [
                new HeroMasteryExperience { MasteryID = 1, HeroIdentifier = "Hero_Magmus", Experience = 36100 }, // Level 15
                new HeroMasteryExperience { MasteryID = 1, HeroIdentifier = "Hero_Glacius", Experience = 7000 },  // Level 4
                new HeroMasteryExperience { MasteryID = 1, HeroIdentifier = "Hero_Pebbles", Experience = 1400 }   // Level 1
            ]
        };

        // 15 + 4 + 1 = 20
        mastery.TotalMasteryLevel().Should().Be(20);
    }

    [Fact]
    public void Mastery_TotalMasteryExperience_SumsAllHeroExperienceAccurately()
    {
        Mastery mastery = new()
        {
            ID = 1,
            AccountID = 10,
            Account = null!,
            HeroExperiences =
            [
                new HeroMasteryExperience { MasteryID = 1, HeroIdentifier = "Hero_Magmus", Experience = 36100 },
                new HeroMasteryExperience { MasteryID = 1, HeroIdentifier = "Hero_Glacius", Experience = 7000 },
                new HeroMasteryExperience { MasteryID = 1, HeroIdentifier = "Hero_Pebbles", Experience = 1400 }
            ]
        };

        mastery.TotalMasteryExperience().Should().Be(36100 + 7000 + 1400);
    }

    [Fact]
    public void Mastery_HeroesAtMaximumMasteryCount_CountsOnlyMaxLevelHeroes()
    {
        Mastery mastery = new()
        {
            ID = 1,
            AccountID = 10,
            Account = null!,
            HeroExperiences =
            [
                new HeroMasteryExperience { MasteryID = 1, HeroIdentifier = "Hero_Max1", Experience = 36100 },
                new HeroMasteryExperience { MasteryID = 1, HeroIdentifier = "Hero_Max2", Experience = 50000 },
                new HeroMasteryExperience { MasteryID = 1, HeroIdentifier = "Hero_AlmostMax", Experience = 36099 }, // Level 14
                new HeroMasteryExperience { MasteryID = 1, HeroIdentifier = "Hero_Mid", Experience = 10000 }
            ]
        };

        mastery.HeroesAtMaximumMasteryCount().Should().Be(2);
    }

    [Fact]
    public void Mastery_HeroesAtMaximumMasteryPercentage_CalculatesAccurateRatio()
    {
        Mastery mastery = new()
        {
            ID = 1,
            AccountID = 10,
            Account = null!,
            HeroExperiences =
            [
                new HeroMasteryExperience { MasteryID = 1, HeroIdentifier = "Hero_1", Experience = 36100 },
                new HeroMasteryExperience { MasteryID = 1, HeroIdentifier = "Hero_2", Experience = 40000 }
            ]
        };

        // 2 heroes at max out of 10 total heroes = 20.0%
        mastery.HeroesAtMaximumMasteryPercentage(10).Should().Be(20.0f);

        // Guard against division by zero
        mastery.HeroesAtMaximumMasteryPercentage(0).Should().Be(0.0f);
        mastery.HeroesAtMaximumMasteryPercentage(-5).Should().Be(0.0f);
    }

    [Fact]
    public void Mastery_CalculateMatchAndBonusExperience_ScalesCorrectly()
    {
        Mastery mastery = new()
        {
            ID = 1,
            AccountID = 10,
            Account = null!,
            HeroExperiences =
            [
                new HeroMasteryExperience { MasteryID = 1, HeroIdentifier = "Hero_Max", Experience = 36100 }
            ]
        };

        // Matchmaking base exp: heroLevel * 20 => 5 * 20 = 100
        mastery.CalculateMatchExperience(AccountStatisticsType.Matchmaking, 5).Should().Be(100);

        // MidWars base exp: heroLevel * 10 => 5 * 10 = 50
        mastery.CalculateMatchExperience(AccountStatisticsType.MidWars, 5).Should().Be(50);

        // Bonus exp: 1 max hero out of 10 = 10% ratio -> tier 10-20% = 20 bonus
        mastery.CalculateBonusExperience(AccountStatisticsType.Matchmaking, 10).Should().Be(20);

        // Combined match and bonus
        mastery.CalculateMatchAndBonusExperience(AccountStatisticsType.Matchmaking, 5, 10).Should().Be(120);

        // Boost is 2x combined
        mastery.CalculateRegularMasteryBoostExperience(AccountStatisticsType.Matchmaking, 5, 10).Should().Be(240);
    }

    // =========================================================================
    // 3. Composite Unique Constraint and Model Metadata Tests
    // =========================================================================

    [Fact]
    public void HeroMasteryExperience_ModelUniqueIndex_IsConfiguredOnMasteryIdAndHeroIdentifier()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName);
        IModel model = db.Model;

        IEntityType entity = model.FindEntityType(typeof(HeroMasteryExperience))!;
        entity.Should().NotBeNull("HeroMasteryExperience entity must be registered in MerrickContext model");

        // Verify composite unique index [MasteryID, HeroIdentifier]
        entity.GetIndexes().Should().Contain(idx =>
            idx.IsUnique &&
            idx.Properties.Count == 2 &&
            idx.Properties.Any(p => p.Name == nameof(HeroMasteryExperience.MasteryID)) &&
            idx.Properties.Any(p => p.Name == nameof(HeroMasteryExperience.HeroIdentifier)),
            "HeroMasteryExperience must define a composite unique index on [MasteryID, HeroIdentifier]");
    }

    [Fact]
    public void HeroMasteryExperience_CompositeUniqueness_DomainEnforcementPreventsDuplicateRows()
    {
        Mastery mastery = new()
        {
            ID = 1,
            AccountID = 10,
            Account = null!,
            HeroExperiences = []
        };

        mastery.SetHeroExperienceByHeroIdentifier("Hero_Flint", 1000);
        mastery.SetHeroExperienceByHeroIdentifier("Hero_Flint", 2500);
        mastery.SetHeroExperienceByHeroIdentifier("Hero_Flint", 5000);

        mastery.HeroExperiences.Should().HaveCount(1, "Domain mutation must enforce single record per HeroIdentifier within a Mastery");
        mastery.HeroExperiences.Single().Experience.Should().Be(5000);
    }

    // =========================================================================
    // 4. Foreign Key Cascade Deletion Tests
    // =========================================================================

    [Fact]
    public async Task HeroMasteryExperience_CascadeDelete_RemovedWhenMasteryDeleted()
    {
        (Account account, Mastery mastery, MerrickContext db, string dbName) = await CreateAccountWithMasteryAsync();

        using (db)
        {
            db.HeroMasteryExperiences.AddRange(
                new HeroMasteryExperience { MasteryID = mastery.ID, HeroIdentifier = "Hero_Scout", Experience = 5000 },
                new HeroMasteryExperience { MasteryID = mastery.ID, HeroIdentifier = "Hero_Magebane", Experience = 8000 },
                new HeroMasteryExperience { MasteryID = mastery.ID, HeroIdentifier = "Hero_NightHound", Experience = 12000 }
            );
            await db.SaveChangesAsync();
        }

        // Delete parent Mastery
        using (MerrickContext db2 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            Mastery m = await db2.Masteries
                .Include(x => x.HeroExperiences)
                .FirstAsync(x => x.ID == mastery.ID);

            db2.Masteries.Remove(m);
            await db2.SaveChangesAsync();
        }

        // Verify all child HeroMasteryExperience records are cascaded and removed
        using (MerrickContext db3 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            List<HeroMasteryExperience> orphaned = await db3.HeroMasteryExperiences
                .Where(e => e.MasteryID == mastery.ID)
                .ToListAsync();

            orphaned.Should().BeEmpty("Deleting Mastery must cascade-delete all related HeroMasteryExperiences");
        }
    }

    [Fact]
    public async Task HeroMasteryExperience_CascadeDelete_RemovedWhenAccountDeleted()
    {
        (Account account, Mastery mastery, MerrickContext db, string dbName) = await CreateAccountWithMasteryAsync();

        using (db)
        {
            db.HeroMasteryExperiences.AddRange(
                new HeroMasteryExperience { MasteryID = mastery.ID, HeroIdentifier = "Hero_Armadon", Experience = 9000 },
                new HeroMasteryExperience { MasteryID = mastery.ID, HeroIdentifier = "Hero_Behemoth", Experience = 18000 }
            );
            await db.SaveChangesAsync();
        }

        // Delete root Account (should cascade to Mastery, which cascades to HeroMasteryExperiences)
        using (MerrickContext db2 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            Account acc = await db2.Accounts
                .Include(a => a.Mastery!)
                .ThenInclude(m => m.HeroExperiences)
                .FirstAsync(a => a.ID == account.ID);

            db2.Accounts.Remove(acc);
            await db2.SaveChangesAsync();
        }

        // Verify full cascade across Account -> Mastery -> HeroMasteryExperiences
        using (MerrickContext db3 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            (await db3.Masteries.AnyAsync(m => m.ID == mastery.ID)).Should().BeFalse();
            (await db3.HeroMasteryExperiences.AnyAsync(e => e.MasteryID == mastery.ID)).Should().BeFalse(
                "Account deletion must cascade-delete Mastery and all child HeroMasteryExperiences");
        }
    }

    [Fact]
    public async Task HeroMasteryExperience_CascadeDelete_Isolation_OtherAccountsUnaffected()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            Role sharedRole = new() { ID = 1, Name = "User" };
            User u1 = new() { ID = 1, EmailAddress = "u1@k.net", PBKDF2PasswordHash = "h", SRPPasswordHash = "s", SRPPasswordSalt = "st", Role = sharedRole };
            User u2 = new() { ID = 2, EmailAddress = "u2@k.net", PBKDF2PasswordHash = "h", SRPPasswordHash = "s", SRPPasswordSalt = "st", Role = sharedRole };

            Account a1 = new() { ID = 10, UserID = 1, User = u1, Name = "Acc1" };
            Account a2 = new() { ID = 20, UserID = 2, User = u2, Name = "Acc2" };

            Mastery m1 = new() { ID = 100, AccountID = 10, Account = a1, HeroExperiences = [] };
            Mastery m2 = new() { ID = 200, AccountID = 20, Account = a2, HeroExperiences = [] };

            db.Users.AddRange(u1, u2);
            db.Accounts.AddRange(a1, a2);
            db.Masteries.AddRange(m1, m2);

            db.HeroMasteryExperiences.AddRange(
                new HeroMasteryExperience { MasteryID = 100, HeroIdentifier = "Hero_Pyro", Experience = 5000 },
                new HeroMasteryExperience { MasteryID = 200, HeroIdentifier = "Hero_Pyro", Experience = 8000 }
            );
            await db.SaveChangesAsync();
        }

        // Delete Account 1
        using (MerrickContext db2 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            Account a1 = await db2.Accounts
                .Include(a => a.Mastery!)
                .ThenInclude(m => m.HeroExperiences)
                .FirstAsync(a => a.ID == 10);

            db2.Accounts.Remove(a1);
            await db2.SaveChangesAsync();
        }

        // Verify Account 2 and Mastery 2 records are completely unaffected
        using (MerrickContext db3 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            (await db3.Accounts.AnyAsync(a => a.ID == 10)).Should().BeFalse();
            (await db3.Masteries.AnyAsync(m => m.ID == 100)).Should().BeFalse();
            (await db3.HeroMasteryExperiences.AnyAsync(e => e.MasteryID == 100)).Should().BeFalse();

            (await db3.Accounts.AnyAsync(a => a.ID == 20)).Should().BeTrue();
            (await db3.Masteries.AnyAsync(m => m.ID == 200)).Should().BeTrue();
            HeroMasteryExperience a2Exp = await db3.HeroMasteryExperiences.FirstAsync(e => e.MasteryID == 200);
            a2Exp.HeroIdentifier.Should().Be("Hero_Pyro");
            a2Exp.Experience.Should().Be(8000);
        }
    }

    // =========================================================================
    // 5. Navigation Fix-Up and Entity Relationship Integrity
    // =========================================================================

    [Fact]
    public void HeroMasteryExperience_NavigationFixup_BiDirectionalIntegrity()
    {
        Mastery mastery = new()
        {
            ID = 50,
            AccountID = 1,
            Account = null!,
            HeroExperiences = []
        };

        HeroMasteryExperience exp = new()
        {
            ID = 1,
            MasteryID = 50,
            Mastery = mastery,
            HeroIdentifier = "Hero_Predator",
            Experience = 12000
        };

        mastery.HeroExperiences.Add(exp);

        exp.Mastery.Should().BeSameAs(mastery);
        mastery.HeroExperiences.Should().Contain(exp);
        exp.MasteryID.Should().Be(mastery.ID);
    }
}
