// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Reflection;
using FluentAssertions;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Game;
using MERRICK.DatabaseContext.Entities.Utility;
using MERRICK.DatabaseContext.Extensions;
using MERRICK.DatabaseContext.Persistence;
using MERRICK.DatabaseContext.Repositories;
using MERRICK.DatabaseContext.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using NEXUS.Contracts.Clients;
using Xunit;

namespace MERRICK.DatabaseContext.Tests;

/// <summary>
///     Empirical Adversarial Challenger Test Suite for Milestone 4 (R4: HeroGuide & HeroItem Schema Normalization):
///     1. Rapid item mutations: sequential adds, removals, re-orderings across all 5 item slots.
///     2. Disconnected and connected entity graph reconciliation in HeroGuideRepository.
///     3. Delimited string roundtrips: empty strings, whitespace, commas, pipes, mixed, trailing, and JSON brackets.
///     4. AbilityQueue and SkillBuild alias tokenization, key mapping, and 25-level progression.
///     5. HeroItem.Cost boundary tests: 0, negative values, int.MaxValue/MinValue, and LINQ arithmetic.
///     6. Value equality, HashCode semantics, and DTO mapping integrity.
///     7. EF Core metadata, unique composite index configurations, and cascade deletion.
/// </summary>
public class HeroGuideChallengeTests
{
    private static async Task<(Account Author, HeroGuide Guide, MerrickContext Db, string DbName)> CreateAuthorWithGuideAsync(string? dbName = null)
    {
        string resolvedDbName = dbName ?? Guid.NewGuid().ToString("N");
        MerrickContext db = TestDbContextFactory.CreateInMemoryContext(resolvedDbName);

        User user = new()
        {
            ID = 1,
            EmailAddress = "m4_challenger@kongor.net",
            PBKDF2PasswordHash = "pbkdf2_sample_hash",
            SRPPasswordHash = "srp_sample_hash",
            SRPPasswordSalt = "srp_sample_salt",
            Role = new Role { ID = 1, Name = "User" }
        };

        Account author = new()
        {
            ID = 10,
            UserID = 1,
            User = user,
            Name = "AdversarialChallenger"
        };

        HeroGuide guide = new()
        {
            ID = 100,
            AuthorID = author.ID,
            Author = author,
            Name = "Initial Benchmark Guide",
            HeroName = "Pyromancer",
            HeroIdentifier = "Hero_Pyromancer",
            Intro = "Initial guide introduction",
            Content = "Initial guide strategy and mechanics",
            TargetMap = "caldavar",
            Public = true,
            Featured = true
        };

        db.Users.Add(user);
        db.Accounts.Add(author);
        db.HeroGuides.Add(guide);
        await db.SaveChangesAsync();

        return (author, guide, db, resolvedDbName);
    }

    private static HeroItem CreateHeroItem(int id, string code, string name, int cost, int recipeCost = 0) => new()
    {
        ID = id,
        Code = code,
        Name = name,
        Cost = cost,
        RecipeCost = recipeCost,
        Description = "Description for " + name,
        Flavor = "Flavor for " + name,
        IconPath = code + ".png"
    };

    #region 1. Rapid Item Mutations & Re-ordering Stress Tests

    [Fact]
    public async Task RapidMutations_SequentialAddsRemovalsAndReordering_MaintainsConsistentStateAcrossContexts()
    {
        (Account author, HeroGuide guide, MerrickContext initialDb, string dbName) = await CreateAuthorWithGuideAsync();
        initialDb.Dispose();

        // Step 1: Initial Population across all 5 slots
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            HeroGuideRepository repo = new(db);
            HeroGuide g = (await repo.GetByIdAsync(guide.ID))!;
            g.StartingItems = "Item_RunesOfTheBlight,Item_HealthPotion,Item_IronBuckler";
            g.EarlyGameItems = "Item_Marchers,Item_SoulscreamRing";
            g.CoreItems = "Item_Spellshards,Item_EnhancedMarchers,Item_Astrolabe";
            g.LuxuryItems = "Item_GrimoireOfPower,Item_FrostfieldPlate";
            g.SituationalItems = "Item_Ward,Item_DustOfAppearance";

            await repo.SaveGuideAsync(g);
        }

        // Verify Step 1 in fresh context
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            HeroGuide g = (await new HeroGuideRepository(db).GetByIdAsync(guide.ID))!;
            g.Items.Should().HaveCount(12);
            g.StartingItems.Should().Be("Item_RunesOfTheBlight,Item_HealthPotion,Item_IronBuckler");
            g.EarlyGameItems.Should().Be("Item_Marchers,Item_SoulscreamRing");
            g.CoreItems.Should().Be("Item_Spellshards,Item_EnhancedMarchers,Item_Astrolabe");
            g.LuxuryItems.Should().Be("Item_GrimoireOfPower,Item_FrostfieldPlate");
            g.SituationalItems.Should().Be("Item_Ward,Item_DustOfAppearance");
        }

        // Step 2: Reverse order in Starting items
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            HeroGuideRepository repo = new(db);
            HeroGuide g = (await repo.GetByIdAsync(guide.ID))!;
            g.StartingItems = "Item_IronBuckler,Item_HealthPotion,Item_RunesOfTheBlight";
            await repo.SaveGuideAsync(g);
        }

        // Verify Step 2: OrderIndices are 0, 1, 2 for the reversed items
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            HeroGuide g = (await new HeroGuideRepository(db).GetByIdAsync(guide.ID))!;
            List<HeroGuideItem> starting = g.Items.Where(i => i.SlotType == GuideSlotType.Starting).OrderBy(i => i.OrderIndex).ToList();
            starting.Should().HaveCount(3);
            starting[0].ItemCode.Should().Be("Item_IronBuckler");
            starting[0].OrderIndex.Should().Be(0);
            starting[1].ItemCode.Should().Be("Item_HealthPotion");
            starting[1].OrderIndex.Should().Be(1);
            starting[2].ItemCode.Should().Be("Item_RunesOfTheBlight");
            starting[2].OrderIndex.Should().Be(2);
        }

        // Step 3: Remove all items from EarlyGame (empty string)
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            HeroGuideRepository repo = new(db);
            HeroGuide g = (await repo.GetByIdAsync(guide.ID))!;
            g.EarlyGameItems = "";
            await repo.SaveGuideAsync(g);
        }

        // Verify Step 3: EarlyGame items are pruned completely from database table
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            HeroGuide g = (await new HeroGuideRepository(db).GetByIdAsync(guide.ID))!;
            g.EarlyGameItems.Should().BeEmpty();
            g.Items.Where(i => i.SlotType == GuideSlotType.EarlyGame).Should().BeEmpty();
            // Other slots remain intact (3 starting + 3 core + 2 luxury + 2 situational = 10)
            g.Items.Should().HaveCount(10);
        }

        // Step 4: Rapid bulk additions in Luxury slot (10 items)
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            HeroGuideRepository repo = new(db);
            HeroGuide g = (await repo.GetByIdAsync(guide.ID))!;
            g.LuxuryItems = "Item_L1,Item_L2,Item_L3,Item_L4,Item_L5,Item_L6,Item_L7,Item_L8,Item_L9,Item_L10";
            await repo.SaveGuideAsync(g);
        }

        // Verify Step 4: Luxury slot contains 10 items ordered 0..9
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            HeroGuide g = (await new HeroGuideRepository(db).GetByIdAsync(guide.ID))!;
            List<HeroGuideItem> luxury = g.Items.Where(i => i.SlotType == GuideSlotType.Luxury).OrderBy(i => i.OrderIndex).ToList();
            luxury.Should().HaveCount(10);
            for (int i = 0; i < 10; i++)
            {
                luxury[i].ItemCode.Should().Be($"Item_L{i + 1}");
                luxury[i].OrderIndex.Should().Be(i);
            }
        }

        // Step 5: Clear all 5 slots completely
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            HeroGuideRepository repo = new(db);
            HeroGuide g = (await repo.GetByIdAsync(guide.ID))!;
            g.StartingItems = "";
            g.EarlyGameItems = "";
            g.CoreItems = "";
            g.LuxuryItems = "";
            g.SituationalItems = "";
            await repo.SaveGuideAsync(g);
        }

        // Verify Step 5: Table misc.HeroGuideItems has 0 records for this guide
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            List<HeroGuideItem> items = await db.HeroGuideItems.Where(i => i.GuideID == guide.ID).ToListAsync();
            items.Should().BeEmpty("All items must be purged when all slot strings are cleared");
        }
    }

    [Fact]
    public async Task RapidMutations_ConnectedTracking_ModificationsInSameDbContext_PersistAccurately()
    {
        (Account author, HeroGuide guide, MerrickContext db, string dbName) = await CreateAuthorWithGuideAsync();

        using (db)
        {
            HeroGuideRepository repo = new(db);
            HeroGuide g = (await repo.GetByIdAsync(guide.ID))!;

            // Mutate items in connected tracked instance
            g.StartingItems = "Item_Boots,Item_Tango";
            g.CoreItems = "Item_Spellshards";
            await repo.SaveGuideAsync(g);

            // Re-query within the same DbContext session
            List<HeroGuideItem> trackedItems = await db.HeroGuideItems.Where(i => i.GuideID == guide.ID).ToListAsync();
            trackedItems.Should().HaveCount(3);
            trackedItems.Count(i => i.SlotType == GuideSlotType.Starting).Should().Be(2);
            trackedItems.Count(i => i.SlotType == GuideSlotType.Core).Should().Be(1);

            // Mutate again within same session: replace Starting items with single item
            g.StartingItems = "Item_CrushingClaws";
            await repo.SaveGuideAsync(g);

            List<HeroGuideItem> updatedItems = await db.HeroGuideItems.Where(i => i.GuideID == guide.ID).ToListAsync();
            updatedItems.Should().HaveCount(2);
            updatedItems.Single(i => i.SlotType == GuideSlotType.Starting).ItemCode.Should().Be("Item_CrushingClaws");
        }

        // Confirm persistence in independent fresh context
        using (MerrickContext freshDb = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            HeroGuide reloaded = (await new HeroGuideRepository(freshDb).GetByIdAsync(guide.ID))!;
            reloaded.StartingItems.Should().Be("Item_CrushingClaws");
            reloaded.CoreItems.Should().Be("Item_Spellshards");
        }
    }

    [Fact]
    public async Task RapidMutations_SlotIsolation_ModifyingSingleSlotPreservesOtherFourSlots()
    {
        (Account author, HeroGuide guide, MerrickContext db, string dbName) = await CreateAuthorWithGuideAsync();

        using (db)
        {
            HeroGuideRepository repo = new(db);
            HeroGuide g = (await repo.GetByIdAsync(guide.ID))!;
            g.StartingItems = "Item_S1,Item_S2";
            g.EarlyGameItems = "Item_E1,Item_E2";
            g.CoreItems = "Item_C1,Item_C2";
            g.LuxuryItems = "Item_L1,Item_L2";
            g.SituationalItems = "Item_Sit1,Item_Sit2";
            await repo.SaveGuideAsync(g);
        }

        // Mutate ONLY Core slot
        using (MerrickContext db2 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            HeroGuideRepository repo = new(db2);
            HeroGuide g = (await repo.GetByIdAsync(guide.ID))!;
            g.CoreItems = "Item_C_BrandNew";
            await repo.SaveGuideAsync(g);
        }

        // Verify that Starting, EarlyGame, Luxury, and Situational are 100% untouched
        using (MerrickContext db3 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            HeroGuide g = (await new HeroGuideRepository(db3).GetByIdAsync(guide.ID))!;
            g.StartingItems.Should().Be("Item_S1,Item_S2");
            g.EarlyGameItems.Should().Be("Item_E1,Item_E2");
            g.CoreItems.Should().Be("Item_C_BrandNew");
            g.LuxuryItems.Should().Be("Item_L1,Item_L2");
            g.SituationalItems.Should().Be("Item_Sit1,Item_Sit2");
        }
    }

    [Fact]
    public async Task RapidMutations_MultiGuideIsolation_ConcurrentUpdatesDoNotInterfere()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User u = new() { ID = 1, EmailAddress = "u@k.net", PBKDF2PasswordHash = "h", SRPPasswordHash = "s", SRPPasswordSalt = "st", Role = new Role { ID = 1, Name = "User" } };
            Account a = new() { ID = 10, UserID = 1, User = u, Name = "Player" };
            HeroGuide g1 = new() { ID = 201, AuthorID = 10, Author = a, Name = "Guide 1", HeroIdentifier = "Hero_Pyromancer", StartingItems = "Item_G1_A,Item_G1_B" };
            HeroGuide g2 = new() { ID = 202, AuthorID = 10, Author = a, Name = "Guide 2", HeroIdentifier = "Hero_Pyromancer", StartingItems = "Item_G2_A,Item_G2_B" };

            db.Users.Add(u);
            db.Accounts.Add(a);
            db.HeroGuides.AddRange(g1, g2);
            await db.SaveChangesAsync();
        }

        // Mutate Guide 1 only
        using (MerrickContext db2 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            HeroGuideRepository repo = new(db2);
            HeroGuide g1 = (await repo.GetByIdAsync(201))!;
            g1.StartingItems = "Item_G1_Mutated";
            await repo.SaveGuideAsync(g1);
        }

        // Verify Guide 2 was completely unaffected
        using (MerrickContext db3 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            HeroGuideRepository repo = new(db3);
            HeroGuide g1 = (await repo.GetByIdAsync(201))!;
            HeroGuide g2 = (await repo.GetByIdAsync(202))!;

            g1.StartingItems.Should().Be("Item_G1_Mutated");
            g2.StartingItems.Should().Be("Item_G2_A,Item_G2_B");
        }
    }

    #endregion

    #region 2. Delimited String Roundtrips & Pathological Delimiters

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n  \t")]
    [InlineData(",")]
    [InlineData(",,,")]
    [InlineData("|")]
    [InlineData("|||")]
    [InlineData(",|,|,")]
    [InlineData("  ,  |  ,  \t ")]
    public void DelimitedString_EmptyAndWhitespaceVariants_YieldEmptyCollectionsAndStrings(string? input)
    {
        HeroGuide guide = new()
        {
            StartingItems = input!,
            EarlyGameItems = input!,
            CoreItems = input!,
            LuxuryItems = input!,
            SituationalItems = input!,
            AbilityQueue = input!
        };

        guide.StartingItems.Should().BeEmpty();
        guide.EarlyGameItems.Should().BeEmpty();
        guide.CoreItems.Should().BeEmpty();
        guide.LuxuryItems.Should().BeEmpty();
        guide.SituationalItems.Should().BeEmpty();
        guide.AbilityQueue.Should().BeEmpty();

        guide.Items.Should().BeEmpty();
        guide.Abilities.Should().BeEmpty();
    }

    [Fact]
    public void DelimitedString_SingleItem_RoundtripsAccurately()
    {
        HeroGuide guide = new() { StartingItems = "Item_RunesOfTheBlight" };

        guide.Items.Should().HaveCount(1);
        guide.Items[0].SlotType.Should().Be(GuideSlotType.Starting);
        guide.Items[0].OrderIndex.Should().Be(0);
        guide.Items[0].ItemCode.Should().Be("Item_RunesOfTheBlight");
        guide.StartingItems.Should().Be("Item_RunesOfTheBlight");
    }

    [Fact]
    public void DelimitedString_CommaPipesAndMixedDelimiters_ParsesAndSerializesConsistently()
    {
        HeroGuide guide = new();

        // Pure comma
        guide.StartingItems = "Item_A,Item_B,Item_C";
        guide.StartingItems.Should().Be("Item_A,Item_B,Item_C");
        guide.Items.Where(i => i.SlotType == GuideSlotType.Starting).Should().HaveCount(3);

        // Pure pipe
        guide.EarlyGameItems = "Item_D|Item_E|Item_F";
        guide.EarlyGameItems.Should().Be("Item_D,Item_E,Item_F");
        guide.Items.Where(i => i.SlotType == GuideSlotType.EarlyGame).Should().HaveCount(3);

        // Mixed comma and pipe
        guide.CoreItems = "Item_1,Item_2|Item_3,Item_4|Item_5";
        guide.CoreItems.Should().Be("Item_1,Item_2,Item_3,Item_4,Item_5");
        guide.Items.Where(i => i.SlotType == GuideSlotType.Core).Should().HaveCount(5);
    }

    [Fact]
    public void DelimitedString_PathologicalLeadingTrailingAndRepeatedDelimiters_CleansesTokens()
    {
        HeroGuide guide = new()
        {
            StartingItems = ",,,Item_Alpha|||,Item_Beta,,,|Item_Gamma,,,||"
        };

        guide.StartingItems.Should().Be("Item_Alpha,Item_Beta,Item_Gamma");
        List<HeroGuideItem> items = guide.Items.Where(i => i.SlotType == GuideSlotType.Starting).OrderBy(i => i.OrderIndex).ToList();
        items.Should().HaveCount(3);
        items[0].ItemCode.Should().Be("Item_Alpha");
        items[0].OrderIndex.Should().Be(0);
        items[1].ItemCode.Should().Be("Item_Beta");
        items[1].OrderIndex.Should().Be(1);
        items[2].ItemCode.Should().Be("Item_Gamma");
        items[2].OrderIndex.Should().Be(2);
    }

    [Fact]
    public void DelimitedString_JsonArrayAndBracketedFormats_StripsFormattingAndTokenizes()
    {
        HeroGuide guide = new()
        {
            StartingItems = "[\"Item_Steamboots\", \"Item_Spellshards\", \"Item_PortalKey\"]",
            CoreItems = "[Item_A, Item_B, Item_C]"
        };

        guide.StartingItems.Should().Be("Item_Steamboots,Item_Spellshards,Item_PortalKey");
        guide.CoreItems.Should().Be("Item_A,Item_B,Item_C");
    }

    [Fact]
    public void DelimitedString_WhitespacePaddedTokens_TrimsEveryToken()
    {
        HeroGuide guide = new()
        {
            StartingItems = "   Item_1   ,   Item_2\t | \n  Item_3   "
        };

        guide.StartingItems.Should().Be("Item_1,Item_2,Item_3");
        List<HeroGuideItem> items = guide.Items.Where(i => i.SlotType == GuideSlotType.Starting).OrderBy(i => i.OrderIndex).ToList();
        items[0].ItemCode.Should().Be("Item_1");
        items[1].ItemCode.Should().Be("Item_2");
        items[2].ItemCode.Should().Be("Item_3");
    }

    [Fact]
    public void DelimitedString_SkillBuildAlias_IsFullyBiDirectionallySynchronizedWithAbilityQueue()
    {
        HeroGuide guide = new();

        // Setting SkillBuild updates AbilityQueue
        guide.SkillBuild = "Ability_Pyromancer1,Ability_Pyromancer2";
        guide.AbilityQueue.Should().Be("Ability_Pyromancer1,Ability_Pyromancer2");
        guide.SkillBuild.Should().Be("Ability_Pyromancer1,Ability_Pyromancer2");

        // Setting AbilityQueue updates SkillBuild
        guide.AbilityQueue = "Ability_Pyromancer3,Ability_Pyromancer4";
        guide.SkillBuild.Should().Be("Ability_Pyromancer3,Ability_Pyromancer4");
        guide.AbilityQueue.Should().Be("Ability_Pyromancer3,Ability_Pyromancer4");
    }

    [Fact]
    public void DelimitedString_MaxLengthItemCode_PersistsWithoutTruncation()
    {
        string longCode = new('A', 100); // exactly 100 chars (MaxLength)
        HeroGuide guide = new() { StartingItems = longCode };

        guide.StartingItems.Should().Be(longCode);
        guide.Items.Single().ItemCode.Should().Be(longCode);
    }

    #endregion

    #region 3. AbilityQueue Parsing & Progression Edge Cases

    [Fact]
    public void AbilityQueue_KeyLetterAndSlotParsing_MapsToAccurateAbilitySlots()
    {
        HeroGuide guide = new()
        {
            AbilityQueue = "Q, W, E, R, Ultimate, stats, AttributeBoost, +"
        };

        List<HeroGuideAbility> abilities = [.. guide.Abilities.OrderBy(a => a.LevelSequence)];
        abilities.Should().HaveCount(8);

        abilities[0].AbilitySlot.Should().Be(1); // Q
        abilities[1].AbilitySlot.Should().Be(2); // W
        abilities[2].AbilitySlot.Should().Be(3); // E
        abilities[3].AbilitySlot.Should().Be(4); // R
        abilities[4].AbilitySlot.Should().Be(4); // Ultimate
        abilities[5].AbilitySlot.Should().Be(5); // stats
        abilities[6].AbilitySlot.Should().Be(5); // AttributeBoost
        abilities[7].AbilitySlot.Should().Be(5); // +
    }

    [Fact]
    public void AbilityQueue_NumericTokensAndEndingDigits_ResolvesSlotCorrectly()
    {
        HeroGuide guide = new()
        {
            AbilityQueue = "1, 2, 3, 4, 5, Ability_Accursed1, Ability_Accursed2, Ability_Accursed4"
        };

        List<HeroGuideAbility> abilities = [.. guide.Abilities.OrderBy(a => a.LevelSequence)];
        abilities.Should().HaveCount(8);

        abilities[0].AbilitySlot.Should().Be(1);
        abilities[1].AbilitySlot.Should().Be(2);
        abilities[2].AbilitySlot.Should().Be(3);
        abilities[3].AbilitySlot.Should().Be(4);
        abilities[4].AbilitySlot.Should().Be(5);
        abilities[5].AbilitySlot.Should().Be(1);
        abilities[6].AbilitySlot.Should().Be(2);
        abilities[7].AbilitySlot.Should().Be(4);
    }

    [Fact]
    public async Task AbilityQueue_Full25LevelProgression_MaintainsStrictLevelSequences()
    {
        string[] levelCodes =
        [
            "Ability_Pyro1", "Ability_Pyro2", "Ability_Pyro1", "Ability_Pyro3", "Ability_Pyro1", "Ability_Pyro4", // 1..6
            "Ability_Pyro1", "Ability_Pyro2", "Ability_Pyro2", "Ability_Pyro2", "Ability_Pyro4", "Ability_Pyro3", // 7..12
            "Ability_Pyro3", "Ability_Pyro3", "Stats", "Ability_Pyro4", "Stats", "Stats",                          // 13..18
            "Stats", "Stats", "Stats", "Stats", "Stats", "Stats", "Stats"                                          // 19..25
        ];

        string rawQueue = string.Join(',', levelCodes);
        (Account author, HeroGuide guide, MerrickContext db, string dbName) = await CreateAuthorWithGuideAsync();

        using (db)
        {
            HeroGuideRepository repo = new(db);
            HeroGuide g = (await repo.GetByIdAsync(guide.ID))!;
            g.AbilityQueue = rawQueue;
            await repo.SaveGuideAsync(g);
        }

        // Verify across independent database context
        using (MerrickContext db2 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            HeroGuide g = (await new HeroGuideRepository(db2).GetByIdAsync(guide.ID))!;
            g.Abilities.Should().HaveCount(25);
            g.AbilityQueue.Should().Be(rawQueue);

            List<HeroGuideAbility> abilities = [.. g.Abilities.OrderBy(a => a.LevelSequence)];
            for (int i = 0; i < 25; i++)
            {
                abilities[i].LevelSequence.Should().Be(i + 1);
                abilities[i].AbilityCode.Should().Be(levelCodes[i]);
            }
        }
    }

    [Fact]
    public void AbilityQueue_EmptyCodeWithSlot_FallsBackToSlotStringInGetter()
    {
        HeroGuide guide = new()
        {
            ID = 1,
            Abilities =
            [
                new HeroGuideAbility { GuideID = 1, LevelSequence = 1, AbilitySlot = 1, AbilityCode = "" },
                new HeroGuideAbility { GuideID = 1, LevelSequence = 2, AbilitySlot = 2, AbilityCode = "  " },
                new HeroGuideAbility { GuideID = 1, LevelSequence = 3, AbilitySlot = 4, AbilityCode = "Ability_Pyro4" }
            ]
        };

        // When code is empty or whitespace, GetAbilitiesQueue falls back to AbilitySlot if > 0
        guide.AbilityQueue.Should().Be("1,2,Ability_Pyro4");
    }

    #endregion

    #region 4. HeroItem.Cost Boundary & Arithmetic Operations

    [Fact]
    public async Task HeroItem_Cost_ZeroBoundary_PersistsAndQueriesCorrectly()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            HeroItem freeItem = CreateHeroItem(1, "Item_WardOfSight", "Ward of Sight", 0);
            HeroItem normalItem = CreateHeroItem(2, "Item_Marchers", "Marchers", 500);

            db.HeroItems.AddRange(freeItem, normalItem);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db2 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            HeroItem item = await db2.HeroItems.SingleAsync(i => i.Cost == 0);
            item.Code.Should().Be("Item_WardOfSight");
            item.Cost.Should().Be(0);

            List<HeroItem> nonZero = await db2.HeroItems.Where(i => i.Cost > 0).ToListAsync();
            nonZero.Should().HaveCount(1);
            nonZero[0].Cost.Should().Be(500);
        }
    }

    [Fact]
    public async Task HeroItem_Cost_NegativeValues_PersistsAndQueriesCorrectly()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            HeroItem unpurchasable = CreateHeroItem(1, "Item_Unpurchasable", "Debug Item", -1);
            HeroItem debtItem = CreateHeroItem(2, "Item_Debt", "Debt Token", -500);
            HeroItem normalItem = CreateHeroItem(3, "Item_MinorTotem", "Minor Totem", 53);

            db.HeroItems.AddRange(unpurchasable, debtItem, normalItem);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db2 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            List<HeroItem> negativeItems = await db2.HeroItems.Where(i => i.Cost < 0).OrderBy(i => i.Cost).ToListAsync();
            negativeItems.Should().HaveCount(2);
            negativeItems[0].Cost.Should().Be(-500);
            negativeItems[1].Cost.Should().Be(-1);
        }
    }

    [Fact]
    public async Task HeroItem_Cost_Extremes_IntMaxValueAndMinValue()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            HeroItem maxCostItem = CreateHeroItem(1, "Item_InfinityBlade", "Infinity Blade", int.MaxValue);
            HeroItem minCostItem = CreateHeroItem(2, "Item_BlackHole", "Black Hole", int.MinValue);

            db.HeroItems.AddRange(maxCostItem, minCostItem);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db2 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            HeroItem maxItem = await db2.HeroItems.FirstAsync(i => i.Cost == int.MaxValue);
            maxItem.Cost.Should().Be(int.MaxValue);

            HeroItem minItem = await db2.HeroItems.FirstAsync(i => i.Cost == int.MinValue);
            minItem.Cost.Should().Be(int.MinValue);
        }
    }

    [Fact]
    public async Task HeroItem_Cost_ArithmeticAggregations_SumAverageMinMax()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            List<HeroItem> items =
            [
                CreateHeroItem(1, "Item_1", "I1", 0),
                CreateHeroItem(2, "Item_2", "I2", 100),
                CreateHeroItem(3, "Item_3", "I3", 250),
                CreateHeroItem(4, "Item_4", "I4", 650),
                CreateHeroItem(5, "Item_5", "I5", 1500),
                CreateHeroItem(6, "Item_6", "I6", 4200)
            ];

            db.HeroItems.AddRange(items);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db2 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            long sum = await db2.HeroItems.SumAsync(i => (long)i.Cost);
            double avg = await db2.HeroItems.AverageAsync(i => (double)i.Cost);
            int min = await db2.HeroItems.MinAsync(i => i.Cost);
            int max = await db2.HeroItems.MaxAsync(i => i.Cost);

            sum.Should().Be(0 + 100 + 250 + 650 + 1500 + 4200); // 6700
            avg.Should().BeApproximately(6700.0 / 6.0, 0.001);
            min.Should().Be(0);
            max.Should().Be(4200);
        }
    }

    [Fact]
    public async Task HeroItem_Cost_ArithmeticFiltersAndModulo_ExecutesInLinq()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            List<HeroItem> items =
            [
                CreateHeroItem(1, "Item_Even", "Even 100", 100),
                CreateHeroItem(2, "Item_Odd", "Odd 101", 101),
                CreateHeroItem(3, "Item_Multiplier", "Mult 600", 600)
            ];

            db.HeroItems.AddRange(items);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db2 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            // Arithmetic multiplication filter
            List<HeroItem> highMultiplied = await db2.HeroItems.Where(i => i.Cost * 2 > 1000).ToListAsync();
            highMultiplied.Should().ContainSingle().Which.Cost.Should().Be(600);

            // Modulo filter (even costs)
            List<HeroItem> evenCosts = await db2.HeroItems.Where(i => i.Cost % 2 == 0).ToListAsync();
            evenCosts.Should().HaveCount(2);

            // Offset arithmetic filter
            List<HeroItem> offsetCheck = await db2.HeroItems.Where(i => i.Cost - 50 < 100).ToListAsync();
            offsetCheck.Should().HaveCount(2); // 100 - 50 = 50 < 100, 101 - 50 = 51 < 100
        }
    }

    [Fact]
    public async Task HeroItem_Cost_InPlaceArithmeticMutation_SavesCorrectly()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            db.HeroItems.Add(CreateHeroItem(1, "Item_PostHaste", "Post Haste", 2000, 500));
            await db.SaveChangesAsync();
        }

        // Apply price increase (+200 gold)
        using (MerrickContext db2 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            HeroItem item = await db2.HeroItems.FirstAsync(i => i.ID == 1);
            item.Cost += 200;
            item.RecipeCost += 200;
            await db2.SaveChangesAsync();
        }

        // Verify update in fresh context
        using (MerrickContext db3 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            HeroItem item = await db3.HeroItems.FirstAsync(i => i.ID == 1);
            item.Cost.Should().Be(2200);
            item.RecipeCost.Should().Be(700);
        }
    }

    #endregion

    #region 5. Value Equality, HashCode, and DTO Mapping Robustness

    [Fact]
    public void HeroGuideItem_EqualityAndHashCode_CaseInsensitiveItemCodeAndFieldEquivalence()
    {
        HeroGuideItem itemA = new()
        {
            GuideID = 10,
            SlotType = GuideSlotType.Starting,
            OrderIndex = 0,
            ItemCode = "Item_RunesOfTheBlight"
        };

        HeroGuideItem itemB = new()
        {
            GuideID = 10,
            SlotType = GuideSlotType.Starting,
            OrderIndex = 0,
            ItemCode = "item_runesoftheblight" // lower-case
        };

        HeroGuideItem itemDiffSlot = new()
        {
            GuideID = 10,
            SlotType = GuideSlotType.EarlyGame,
            OrderIndex = 0,
            ItemCode = "Item_RunesOfTheBlight"
        };

        HeroGuideItem itemDiffOrder = new()
        {
            GuideID = 10,
            SlotType = GuideSlotType.Starting,
            OrderIndex = 1,
            ItemCode = "Item_RunesOfTheBlight"
        };

        itemA.Equals(itemB).Should().BeTrue("HeroGuideItem value equality should be case-insensitive on ItemCode");
        itemA.GetHashCode().Should().Be(itemB.GetHashCode(), "Equal HeroGuideItems must produce identical hash codes");

        itemA.Equals(itemDiffSlot).Should().BeFalse("Different SlotType must not be equal");
        itemA.Equals(itemDiffOrder).Should().BeFalse("Different OrderIndex must not be equal");
        itemA.Equals(null).Should().BeFalse();
    }

    [Fact]
    public void HeroGuideAbility_EqualityAndHashCode_CaseInsensitiveAbilityCodeAndFieldEquivalence()
    {
        HeroGuideAbility abilA = new()
        {
            GuideID = 10,
            LevelSequence = 1,
            AbilitySlot = 1,
            AbilityCode = "Ability_Pyromancer1"
        };

        HeroGuideAbility abilB = new()
        {
            GuideID = 10,
            LevelSequence = 1,
            AbilitySlot = 1,
            AbilityCode = "ability_pyromancer1" // lower-case
        };

        HeroGuideAbility abilDiffSequence = new()
        {
            GuideID = 10,
            LevelSequence = 2,
            AbilitySlot = 1,
            AbilityCode = "Ability_Pyromancer1"
        };

        abilA.Equals(abilB).Should().BeTrue("HeroGuideAbility value equality should be case-insensitive on AbilityCode");
        abilA.GetHashCode().Should().Be(abilB.GetHashCode(), "Equal HeroGuideAbilities must produce identical hash codes");

        abilA.Equals(abilDiffSequence).Should().BeFalse("Different LevelSequence must not be equal");
        abilA.Equals(null).Should().BeFalse();
    }

    [Fact]
    public void HeroGuideDto_FullGraphDtoMapping_MapsAllSlotsAndMetadataCorrectly()
    {
        HeroGuide guide = new()
        {
            ID = 42,
            Name = "Grandmaster Pyromancer",
            HeroIdentifier = "Hero_Pyromancer",
            HeroName = "Pyromancer",
            Intro = "Top tier competitive guide.",
            Content = "Play aggressive in mid lane.",
            StartingItems = "Item_RunesOfTheBlight,Item_HealthPotion",
            EarlyGameItems = "Item_Marchers",
            CoreItems = "Item_Spellshards,Item_PortalKey",
            LuxuryItems = "Item_GrimoireOfPower",
            SituationalItems = "Item_WardOfSight",
            AbilityQueue = "Ability_Pyro1,Ability_Pyro2,Ability_Pyro1",
            Public = true,
            Featured = true,
            UpVotes = 420,
            DownVotes = 69,
            Author = new Account
            {
                ID = 7,
                Name = "GrandmasterUser",
                User = new User
                {
                    ID = 7,
                    EmailAddress = "gm@kongor.net",
                    PBKDF2PasswordHash = "h",
                    SRPPasswordHash = "s",
                    SRPPasswordSalt = "st"
                }
            }
        };

        HeroGuideDto dto = guide.ToDto();

        dto.ID.Should().Be(42);
        dto.Name.Should().Be("Grandmaster Pyromancer");
        dto.StartingItems.Should().Be("Item_RunesOfTheBlight,Item_HealthPotion");
        dto.EarlyGameItems.Should().Be("Item_Marchers");
        dto.CoreItems.Should().Be("Item_Spellshards,Item_PortalKey");
        dto.LuxuryItems.Should().Be("Item_GrimoireOfPower");
        dto.AbilityQueue.Should().Be("Ability_Pyro1,Ability_Pyro2,Ability_Pyro1");
        dto.Official.Should().BeTrue();
        dto.Public.Should().BeTrue();
        dto.Upvotes.Should().Be(420);
        dto.Downvotes.Should().Be(69);
        dto.AuthorAccountName.Should().Be("GrandmasterUser");
    }

    [Fact]
    public void HeroItemDto_ExtremeCosts_MapsIntCostAccurately()
    {
        HeroItem itemMax = CreateHeroItem(1, "Item_Max", "Max", int.MaxValue);
        HeroItem itemZero = CreateHeroItem(2, "Item_Zero", "Zero", 0);
        HeroItem itemNeg = CreateHeroItem(3, "Item_Neg", "Neg", -1);

        itemMax.ToDto().Cost.Should().Be(int.MaxValue);
        itemZero.ToDto().Cost.Should().Be(0);
        itemNeg.ToDto().Cost.Should().Be(-1);
    }

    #endregion

    #region 6. Model Constraints and Cascade Deletion Verification

    [Fact]
    public async Task HeroGuide_CascadeDelete_E2E_RemovesAllItemsAndAbilitiesInRelationalStore()
    {
        (Account author, HeroGuide guide, MerrickContext db, string dbName) = await CreateAuthorWithGuideAsync();

        using (db)
        {
            HeroGuideRepository repo = new(db);
            HeroGuide g = (await repo.GetByIdAsync(guide.ID))!;
            g.StartingItems = "Item_1,Item_2";
            g.EarlyGameItems = "Item_3";
            g.CoreItems = "Item_4";
            g.LuxuryItems = "Item_5";
            g.SituationalItems = "Item_6";
            g.AbilityQueue = "Ability_1,Ability_2,Ability_3";
            await repo.SaveGuideAsync(g);
        }

        // Delete guide via repository
        using (MerrickContext db2 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            HeroGuideRepository repo = new(db2);
            bool deleted = await repo.DeleteGuideAsync(guide.ID);
            deleted.Should().BeTrue();
        }

        // Verify that parent HeroGuide and all child items and abilities are 100% gone
        using (MerrickContext db3 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            (await db3.HeroGuides.AnyAsync(g => g.ID == guide.ID)).Should().BeFalse();
            (await db3.HeroGuideItems.AnyAsync(i => i.GuideID == guide.ID)).Should().BeFalse();
            (await db3.HeroGuideAbilities.AnyAsync(a => a.GuideID == guide.ID)).Should().BeFalse();
        }
    }

    [Fact]
    public void Model_HeroItem_Cost_IsClrIntAndMappedAsInteger()
    {
        // 1. Reflection verification: Cost property on HeroItem is System.Int32
        PropertyInfo costProp = typeof(HeroItem).GetProperty(nameof(HeroItem.Cost))!;
        costProp.Should().NotBeNull();
        costProp.PropertyType.Should().Be<int>("HeroItem.Cost must be strongly-typed as System.Int32 in C#");

        // 2. EF Core Model verification
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        IEntityType entity = db.Model.FindEntityType(typeof(HeroItem))!;
        IProperty property = entity.FindProperty(nameof(HeroItem.Cost))!;
        property.ClrType.Should().Be<int>();
        property.IsNullable.Should().BeFalse();
    }

    [Fact]
    public void Model_HeroGuideItem_UniqueConstraint_PreventsDuplicateSlotAndOrderIndex()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        IEntityType entity = db.Model.FindEntityType(typeof(HeroGuideItem))!;

        entity.GetIndexes().Should().Contain(idx =>
            idx.IsUnique &&
            idx.Properties.Count == 3 &&
            idx.Properties.Any(p => p.Name == nameof(HeroGuideItem.GuideID) || p.Name == "GuideID" || p.Name == "HeroGuideID") &&
            idx.Properties.Any(p => p.Name == nameof(HeroGuideItem.SlotType)) &&
            idx.Properties.Any(p => p.Name == nameof(HeroGuideItem.OrderIndex)),
            "HeroGuideItem must define a composite unique index on [GuideID, SlotType, OrderIndex]");
    }

    [Fact]
    public void Model_HeroGuideAbility_UniqueConstraint_PreventsDuplicateLevelSequence()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        IEntityType entity = db.Model.FindEntityType(typeof(HeroGuideAbility))!;

        entity.GetIndexes().Should().Contain(idx =>
            idx.IsUnique &&
            idx.Properties.Count == 2 &&
            idx.Properties.Any(p => p.Name == nameof(HeroGuideAbility.GuideID) || p.Name == "GuideID" || p.Name == "HeroGuideID") &&
            idx.Properties.Any(p => p.Name == nameof(HeroGuideAbility.LevelSequence)),
            "HeroGuideAbility must define a composite unique index on [GuideID, LevelSequence]");
    }

    #endregion
}
