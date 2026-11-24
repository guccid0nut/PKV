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
using MERRICK.DatabaseContext.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using NEXUS.Contracts.Data;
using NEXUS.Contracts.Domain;
using Xunit;

namespace MERRICK.DatabaseContext.Tests;

/// <summary>
///     Adversarial stress-testing suite for Milestone 3:
///     1. Disconnected entity graph reconciliation in MatchHistoryRepository.SaveMasteryAsync
///     2. Rapid hero progression mutations and in-place child deduplication
///     3. Case-insensitive hero identifiers across storage and queries
///     4. Boundary experience values and level progression calculations
///     5. 50+ heroes aggregate calculation accuracy
///     6. Eager loading and relational cascade isolation
/// </summary>
public class HeroMasteryChallengeTests
{
    private static User CreateTestUser(int id = 1, string email = "user@kongor.test") => new()
    {
        ID = id,
        EmailAddress = email,
        SRPPasswordSalt = "salt",
        SRPPasswordHash = "hash",
        PBKDF2PasswordHash = "pbkdf2",
        Role = new Role { ID = 1, Name = "User" }
    };

    private static async Task<(Account Account, Mastery Mastery, MerrickContext Db, string DbName)> CreateAccountWithMasteryAsync(string? dbName = null)
    {
        string resolvedDbName = dbName ?? Guid.NewGuid().ToString("N");
        MerrickContext db = TestDbContextFactory.CreateInMemoryContext(resolvedDbName);

        User user = CreateTestUser(1, "mastery_challenger@kongor.net");
        Account account = new()
        {
            ID = 101,
            UserID = 1,
            User = user,
            Name = "ChallengerHero"
        };

        Mastery mastery = new()
        {
            ID = 501,
            AccountID = account.ID,
            Account = account,
            HeroExperiences = []
        };
        account.Mastery = mastery;

        db.Users.Add(user);
        db.Accounts.Add(account);
        db.Masteries.Add(mastery);
        await db.SaveChangesAsync();

        return (account, mastery, db, resolvedDbName);
    }

    #region 1. Disconnected Entity Graph Reconciliation in MatchHistoryRepository.SaveMasteryAsync

    [Fact]
    public async Task SaveMasteryAsync_DisconnectedGraph_PrunesRemovedHeroes()
    {
        (Account account, Mastery mastery, MerrickContext db, string dbName) = await CreateAccountWithMasteryAsync();

        // Seed 3 heroes
        using (db)
        {
            db.HeroMasteryExperiences.AddRange(
                new HeroMasteryExperience { MasteryID = mastery.ID, HeroIdentifier = "Hero_Pyromancer", Experience = 10000 },
                new HeroMasteryExperience { MasteryID = mastery.ID, HeroIdentifier = "Hero_Valkyrie", Experience = 5000 },
                new HeroMasteryExperience { MasteryID = mastery.ID, HeroIdentifier = "Hero_FlintBeastwood", Experience = 2000 }
            );
            await db.SaveChangesAsync();
        }

        // In a disconnected context, send incoming mastery with Valkyrie removed
        using (MerrickContext db2 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            MatchHistoryRepository repo = new(db2);

            Mastery disconnectedMastery = new()
            {
                AccountID = account.ID,
                Account = null!,
                HeroExperiences =
                [
                    new HeroMasteryExperience { HeroIdentifier = "Hero_Pyromancer", Experience = 12000 },
                    new HeroMasteryExperience { HeroIdentifier = "Hero_FlintBeastwood", Experience = 3500 }
                ]
            };

            await repo.SaveMasteryAsync(disconnectedMastery);
        }

        // Verify in 3rd context: Valkyrie is pruned, Pyromancer and Flint are updated
        using (MerrickContext db3 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            Mastery? refreshed = await db3.Masteries
                .Include(m => m.HeroExperiences)
                .FirstOrDefaultAsync(m => m.AccountID == account.ID);

            refreshed.Should().NotBeNull();
            refreshed!.HeroExperiences.Should().HaveCount(2);

            refreshed.HeroExperiences.Any(e => e.HeroIdentifier == "Hero_Valkyrie").Should().BeFalse(
                "Hero_Valkyrie must be pruned from database when omitted from incoming disconnected mastery");

            HeroMasteryExperience pyro = refreshed.HeroExperiences.First(e => e.HeroIdentifier == "Hero_Pyromancer");
            pyro.Experience.Should().Be(12000);

            HeroMasteryExperience flint = refreshed.HeroExperiences.First(e => e.HeroIdentifier == "Hero_FlintBeastwood");
            flint.Experience.Should().Be(3500);

            // Verify direct DbSet query has no orphaned Valkyrie records
            List<HeroMasteryExperience> allRecords = await db3.HeroMasteryExperiences
                .Where(e => e.MasteryID == mastery.ID)
                .ToListAsync();
            allRecords.Should().HaveCount(2);
        }
    }

    [Fact]
    public async Task SaveMasteryAsync_DisconnectedGraph_AddsModifiesAndRemovesSimultaneously()
    {
        (Account account, Mastery mastery, MerrickContext db, string dbName) = await CreateAccountWithMasteryAsync();

        // Seed 3 heroes
        using (db)
        {
            db.HeroMasteryExperiences.AddRange(
                new HeroMasteryExperience { MasteryID = mastery.ID, HeroIdentifier = "Hero_Devourer", Experience = 3000 },
                new HeroMasteryExperience { MasteryID = mastery.ID, HeroIdentifier = "Hero_Glacius", Experience = 7000 },
                new HeroMasteryExperience { MasteryID = mastery.ID, HeroIdentifier = "Hero_Hammerstorm", Experience = 1500 }
            );
            await db.SaveChangesAsync();
        }

        // Disconnected update:
        // - Devourer: 3000 -> 6000 (modified)
        // - Hammerstorm: removed (pruned)
        // - Glacius: 7000 (unchanged)
        // - WitchSlayer: 4500 (added new)
        // - Valkyrie: 18500 (added new)
        using (MerrickContext db2 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            MatchHistoryRepository repo = new(db2);

            Mastery disconnectedMastery = new()
            {
                AccountID = account.ID,
                Account = null!,
                HeroExperiences =
                [
                    new HeroMasteryExperience { HeroIdentifier = "Hero_Devourer", Experience = 6000 },
                    new HeroMasteryExperience { HeroIdentifier = "Hero_Glacius", Experience = 7000 },
                    new HeroMasteryExperience { HeroIdentifier = "Hero_WitchSlayer", Experience = 4500 },
                    new HeroMasteryExperience { HeroIdentifier = "Hero_Valkyrie", Experience = 18500 }
                ]
            };

            await repo.SaveMasteryAsync(disconnectedMastery);
        }

        // Verify in 3rd context
        using (MerrickContext db3 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            MatchHistoryRepository repo = new(db3);
            Mastery? refreshed = await repo.GetMasteryByAccountIdAsync(account.ID);

            refreshed.Should().NotBeNull();
            refreshed!.HeroExperiences.Should().HaveCount(4);

            refreshed.GetHeroExperienceByHeroIdentifier("Hero_Devourer").Should().Be(6000);
            refreshed.GetHeroExperienceByHeroIdentifier("Hero_Glacius").Should().Be(7000);
            refreshed.GetHeroExperienceByHeroIdentifier("Hero_WitchSlayer").Should().Be(4500);
            refreshed.GetHeroExperienceByHeroIdentifier("Hero_Valkyrie").Should().Be(18500);
            refreshed.GetHeroExperienceByHeroIdentifier("Hero_Hammerstorm").Should().Be(0, "Hammerstorm was removed and must not exist");

            // Verify foreign keys are properly set on newly added records
            List<HeroMasteryExperience> records = await db3.HeroMasteryExperiences
                .Where(e => e.MasteryID == mastery.ID)
                .ToListAsync();
            records.Should().HaveCount(4);
            records.All(e => e.MasteryID == mastery.ID).Should().BeTrue();
        }
    }

    [Fact]
    public async Task SaveMasteryAsync_CaseInsensitiveReconciliation_ModifiesWithoutDuplicates()
    {
        (Account account, Mastery mastery, MerrickContext db, string dbName) = await CreateAccountWithMasteryAsync();

        // Seed with lowercase identifier
        using (db)
        {
            db.HeroMasteryExperiences.Add(new HeroMasteryExperience
            {
                MasteryID = mastery.ID,
                HeroIdentifier = "hero_pyromancer",
                Experience = 5000
            });
            await db.SaveChangesAsync();
        }

        // Disconnected update with uppercase identifier
        using (MerrickContext db2 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            MatchHistoryRepository repo = new(db2);

            Mastery disconnectedMastery = new()
            {
                AccountID = account.ID,
                Account = null!,
                HeroExperiences =
                [
                    new HeroMasteryExperience { HeroIdentifier = "HERO_PYROMANCER", Experience = 10000 }
                ]
            };

            await repo.SaveMasteryAsync(disconnectedMastery);
        }

        // Verify in 3rd context: single record updated, zero duplicates
        using (MerrickContext db3 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            List<HeroMasteryExperience> records = await db3.HeroMasteryExperiences
                .Where(e => e.MasteryID == mastery.ID)
                .ToListAsync();

            records.Should().HaveCount(1, "Case variation must not create duplicate child rows");
            records[0].Experience.Should().Be(10000);
        }
    }

    [Fact]
    public async Task SaveMasteryAsync_InitialMasteryCreation_WhenMasteryDoesNotExist()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName);

        // Account with NO mastery
        User user = CreateTestUser(10, "fresh_user@kongor.net");
        Account freshAccount = new() { ID = 888, UserID = 10, User = user, Name = "FreshPlayer" };
        db.Users.Add(user);
        db.Accounts.Add(freshAccount);
        await db.SaveChangesAsync();

        // Call SaveMasteryAsync for fresh account
        using (MerrickContext db2 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            MatchHistoryRepository repo = new(db2);

            Mastery newMastery = new()
            {
                AccountID = freshAccount.ID,
                Account = null!,
                HeroExperiences =
                [
                    new HeroMasteryExperience { HeroIdentifier = "Hero_Magmus", Experience = 4000 },
                    new HeroMasteryExperience { HeroIdentifier = "Hero_Pebbles", Experience = 8000 }
                ]
            };

            await repo.SaveMasteryAsync(newMastery);
        }

        // Verify creation
        using (MerrickContext db3 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            MatchHistoryRepository repo = new(db3);
            Mastery? loaded = await repo.GetMasteryByAccountIdAsync(freshAccount.ID);

            loaded.Should().NotBeNull();
            loaded!.AccountID.Should().Be(freshAccount.ID);
            loaded.HeroExperiences.Should().HaveCount(2);
            loaded.GetHeroExperienceByHeroIdentifier("Hero_Magmus").Should().Be(4000);
            loaded.GetHeroExperienceByHeroIdentifier("Hero_Pebbles").Should().Be(8000);
        }
    }

    [Fact]
    public async Task SaveMasteryAsync_EmptyHeroExperiences_ClearsAllProgressionPreservingMastery()
    {
        (Account account, Mastery mastery, MerrickContext db, string dbName) = await CreateAccountWithMasteryAsync();

        // Seed 2 heroes
        using (db)
        {
            db.HeroMasteryExperiences.AddRange(
                new HeroMasteryExperience { MasteryID = mastery.ID, HeroIdentifier = "Hero_Scout", Experience = 15000 },
                new HeroMasteryExperience { MasteryID = mastery.ID, HeroIdentifier = "Hero_NightHound", Experience = 8000 }
            );
            await db.SaveChangesAsync();
        }

        // Save with empty list
        using (MerrickContext db2 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            MatchHistoryRepository repo = new(db2);
            Mastery emptyMastery = new()
            {
                AccountID = account.ID,
                Account = null!,
                HeroExperiences = []
            };
            await repo.SaveMasteryAsync(emptyMastery);
        }

        // Verify in 3rd context
        using (MerrickContext db3 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            MatchHistoryRepository repo = new(db3);
            Mastery? refreshed = await repo.GetMasteryByAccountIdAsync(account.ID);

            refreshed.Should().NotBeNull();
            refreshed!.HeroExperiences.Should().BeEmpty();

            List<HeroMasteryExperience> orphaned = await db3.HeroMasteryExperiences
                .Where(e => e.MasteryID == mastery.ID)
                .ToListAsync();
            orphaned.Should().BeEmpty("All child experiences should be pruned");
        }
    }

    #endregion

    #region 2. Rapid Progression Mutations & In-Place Deduplication

    [Fact]
    public void Mastery_RapidMutations_DeduplicatesInPlaceWithCaseVariations()
    {
        Mastery mastery = new()
        {
            ID = 1,
            AccountID = 10,
            Account = null!,
            HeroExperiences = []
        };

        string[] casingVariants =
        [
            "hero_pyromancer",
            "Hero_Pyromancer",
            "HERO_PYROMANCER",
            "Hero_PYROMANCER",
            "hErO_pYrOmAnCeR"
        ];

        // 100 rapid mutations with mixed casing
        for (int i = 1; i <= 100; i++)
        {
            string heroName = casingVariants[i % casingVariants.Length];
            mastery.SetHeroExperienceByHeroIdentifier(heroName, i * 100);
        }

        mastery.HeroExperiences.Should().HaveCount(1, "Rapid mutations across case variations must update the single existing entry");
        mastery.GetHeroExperienceByHeroIdentifier("Hero_Pyromancer").Should().Be(10000);
        mastery.GetHeroExperienceByHeroIdentifier("hero_pyromancer").Should().Be(10000);
        mastery.GetHeroExperienceByHeroIdentifier("HERO_PYROMANCER").Should().Be(10000);
    }

    [Fact]
    public async Task SaveMasteryAsync_RapidSequentialSaves_MaintainsSingleChildRow()
    {
        (Account account, Mastery mastery, MerrickContext db, string dbName) = await CreateAccountWithMasteryAsync();
        db.Dispose();

        // 15 rapid sequential saves via repository
        for (int i = 1; i <= 15; i++)
        {
            using MerrickContext context = TestDbContextFactory.CreateInMemoryContext(dbName);
            MatchHistoryRepository repo = new(context);

            Mastery m = new()
            {
                AccountID = account.ID,
                Account = null!,
                HeroExperiences =
                [
                    new HeroMasteryExperience { HeroIdentifier = "Hero_Behemoth", Experience = i * 1000 }
                ]
            };

            await repo.SaveMasteryAsync(m);
        }

        using (MerrickContext verifyContext = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            List<HeroMasteryExperience> records = await verifyContext.HeroMasteryExperiences
                .Where(e => e.MasteryID == mastery.ID)
                .ToListAsync();

            records.Should().HaveCount(1, "Sequential rapid saves on same hero must update in-place without row proliferation");
            records[0].Experience.Should().Be(15000);
            records[0].HeroIdentifier.Should().Be("Hero_Behemoth");
        }
    }

    #endregion

    #region 3. Boundary Experience Values and Level Progression

    [Theory]
    [InlineData(-1000, 0)]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(1399, 0)]
    [InlineData(1400, 1)]
    [InlineData(2999, 1)]
    [InlineData(3000, 2)]
    [InlineData(4999, 2)]
    [InlineData(5000, 3)]
    [InlineData(6999, 3)]
    [InlineData(7000, 4)]
    [InlineData(9099, 4)]
    [InlineData(9100, 5)]
    [InlineData(11299, 5)]
    [InlineData(11300, 6)]
    [InlineData(13599, 6)]
    [InlineData(13600, 7)]
    [InlineData(15999, 7)]
    [InlineData(16000, 8)]
    [InlineData(18499, 8)]
    [InlineData(18500, 9)]
    [InlineData(20999, 9)]
    [InlineData(21000, 10)]
    [InlineData(23599, 10)]
    [InlineData(23600, 11)]
    [InlineData(26399, 11)]
    [InlineData(26400, 12)]
    [InlineData(29399, 12)]
    [InlineData(29400, 13)]
    [InlineData(32599, 13)]
    [InlineData(32600, 14)]
    [InlineData(36099, 14)]
    [InlineData(36100, 15)]
    [InlineData(50000, 15)]
    [InlineData(100000, 15)]
    [InlineData(int.MaxValue, 15)]
    public void Mastery_GetLevelFromExperience_AllBoundaryConditions(int experience, int expectedLevel)
    {
        Mastery.GetLevelFromExperience(experience).Should().Be(expectedLevel);
    }

    [Theory]
    [InlineData(0, 0, 1400)]
    [InlineData(1400, 1400, 3000)]
    [InlineData(2999, 1400, 3000)]
    [InlineData(3000, 3000, 5000)]
    [InlineData(36100, 36100, 36100)]
    [InlineData(50000, 36100, 36100)]
    [InlineData(-50, 0, 1400)]
    public void Mastery_LevelBoundaries_AccurateAcrossTiers(int experience, int expectedLower, int expectedUpper)
    {
        Mastery.GetLowerLevelBoundaryFromExperience(experience).Should().Be(expectedLower);
        Mastery.GetUpperLevelBoundaryFromExperience(experience).Should().Be(expectedUpper);
    }

    #endregion

    #region 4. 50+ Heroes Stress Scale Test

    [Fact]
    public void Mastery_FiftyPlusHeroes_AggregateCalculationsStressTest()
    {
        Mastery mastery = new()
        {
            ID = 1,
            AccountID = 10,
            Account = null!,
            HeroExperiences = []
        };

        int expectedTotalLevel = 0;
        int expectedTotalExp = 0;
        int expectedMaxCount = 0;

        // Populate 120 unique heroes with varied XP
        for (int i = 1; i <= 120; i++)
        {
            string heroId = $"Hero_Test_{i:D3}";
            // Rotate through 0 XP, mid-levels, threshold levels, and max levels (>= 36100)
            int exp = (i % 6) switch
            {
                0 => 0,
                1 => 1400,       // Level 1
                2 => 7000,       // Level 4
                3 => 21000,      // Level 10
                4 => 36100,      // Level 15 (Max)
                5 => 50000 + i,  // Level 15 (Over-max)
                _ => 0
            };

            mastery.HeroExperiences.Add(new HeroMasteryExperience
            {
                MasteryID = 1,
                HeroIdentifier = heroId,
                Experience = exp
            });

            int level = Mastery.GetLevelFromExperience(exp);
            expectedTotalLevel += level;
            expectedTotalExp += exp;
            if (level == Mastery.MaximumMasteryLevel)
                expectedMaxCount++;
        }

        mastery.HeroExperiences.Should().HaveCount(120);
        mastery.TotalMasteryLevel().Should().Be(expectedTotalLevel);
        mastery.TotalMasteryExperience().Should().Be(expectedTotalExp);
        mastery.HeroesAtMaximumMasteryCount().Should().Be(expectedMaxCount);

        float expectedPercentage = (float)expectedMaxCount / 120f * 100f;
        mastery.HeroesAtMaximumMasteryPercentage(120).Should().BeApproximately(expectedPercentage, 0.001f);
    }

    [Theory]
    [InlineData(0, 0, 0f)]
    [InlineData(-1, 0, 0f)]
    [InlineData(10, 0, 0f)]
    [InlineData(10, 1, 10f)]
    [InlineData(10, 10, 100f)]
    public void Mastery_HeroesAtMaximumMasteryPercentage_EdgeCases(int totalHeroCount, int maxHeroes, float expectedRatio)
    {
        Mastery mastery = new()
        {
            ID = 1,
            AccountID = 1,
            Account = null!,
            HeroExperiences = []
        };

        for (int i = 0; i < maxHeroes; i++)
        {
            mastery.HeroExperiences.Add(new HeroMasteryExperience
            {
                MasteryID = 1,
                HeroIdentifier = $"Hero_Max_{i}",
                Experience = 36100
            });
        }

        mastery.HeroesAtMaximumMasteryPercentage(totalHeroCount).Should().Be(expectedRatio);
    }

    #endregion

    #region 5. Eager Loading & AccountRepository Integration

    [Fact]
    public async Task AccountRepository_EagerLoads_MasteryAndHeroExperiences()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateTestUser(50, "eager_user@kongor.net");
            Account account = new() { ID = 999, UserID = 50, User = user, Name = "EagerAccount" };
            Mastery mastery = new() { ID = 777, AccountID = 999, Account = account, HeroExperiences = [] };

            mastery.HeroExperiences.Add(new HeroMasteryExperience { MasteryID = 777, HeroIdentifier = "Hero_Scout", Experience = 9100 });
            mastery.HeroExperiences.Add(new HeroMasteryExperience { MasteryID = 777, HeroIdentifier = "Hero_Magebane", Experience = 16000 });

            account.Mastery = mastery;
            db.Users.Add(user);
            db.Accounts.Add(account);
            db.Masteries.Add(mastery);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db2 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            AccountRepository accountRepo = new(db2);

            Account? loaded = await accountRepo.GetByIdAsync(999);
            loaded.Should().NotBeNull();
            loaded!.Mastery.Should().NotBeNull();
            loaded.Mastery!.HeroExperiences.Should().HaveCount(2);
            loaded.Mastery.GetHeroExperienceByHeroIdentifier("Hero_Scout").Should().Be(9100);
            loaded.Mastery.GetHeroExperienceByHeroIdentifier("Hero_Magebane").Should().Be(16000);

            Account? loadedByName = await accountRepo.GetByNicknameAsync("EagerAccount");
            loadedByName.Should().NotBeNull();
            loadedByName!.Mastery.Should().NotBeNull();
            loadedByName.Mastery!.HeroExperiences.Should().HaveCount(2);
        }
    }

    #endregion
}
