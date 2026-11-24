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
using MERRICK.DatabaseContext.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using NEXUS.Contracts.Clients;
using Xunit;

namespace MERRICK.DatabaseContext.Tests;

public class HeroGuideRelationalDecompositionTests
{
    private static async Task<(Account Author, HeroGuide Guide, MerrickContext Context, string DbName)> CreateAuthorWithGuideAsync(string? dbName = null)
    {
        string resolvedDbName = dbName ?? Guid.NewGuid().ToString("N");
        MerrickContext context = TestDbContextFactory.CreateInMemoryContext(resolvedDbName);

        User user = new()
        {
            ID = 1,
            EmailAddress = "guide_author@kongor.net",
            PBKDF2PasswordHash = "hash",
            SRPPasswordHash = "srp_h",
            SRPPasswordSalt = "srp_s",
            Role = new Role { ID = 1, Name = "User" }
        };

        Account author = new()
        {
            ID = 10,
            UserID = 1,
            User = user,
            Name = "Pyromaniac"
        };

        HeroGuide guide = new()
        {
            ID = 100,
            AuthorID = author.ID,
            Author = author,
            Name = "Mid Pyro Guide",
            HeroName = "Pyromancer",
            HeroIdentifier = "Hero_Pyromancer",
            Intro = "High burst damage build for mid lane dominance.",
            Content = "Focus on landing Phoenix Wave and Dragon Strike combo.",
            TargetMap = "caldavar",
            Public = true,
            Featured = true
        };

        context.Users.Add(user);
        context.Accounts.Add(author);
        context.HeroGuides.Add(guide);
        await context.SaveChangesAsync();

        return (author, guide, context, resolvedDbName);
    }

    // =========================================================================
    // 1. Relational CRUD Tests for misc.HeroGuideItems and misc.HeroGuideAbilities
    // =========================================================================

    [Fact]
    public async Task HeroGuideItems_CreateAndRead_PersistsRelationalRecordsWithOrder()
    {
        (Account author, HeroGuide guide, MerrickContext db, string dbName) = await CreateAuthorWithGuideAsync();
        using (db)
        {
            HeroGuideItem item1 = new()
            {
                GuideID = guide.ID,
                SlotType = GuideSlotType.Starting,
                OrderIndex = 0,
                ItemCode = "Item_RunesOfTheBlight"
            };
            HeroGuideItem item2 = new()
            {
                GuideID = guide.ID,
                SlotType = GuideSlotType.Starting,
                OrderIndex = 1,
                ItemCode = "Item_HealthPotion"
            };
            HeroGuideItem item3 = new()
            {
                GuideID = guide.ID,
                SlotType = GuideSlotType.EarlyGame,
                OrderIndex = 0,
                ItemCode = "Item_Marchers"
            };
            HeroGuideItem item4 = new()
            {
                GuideID = guide.ID,
                SlotType = GuideSlotType.Core,
                OrderIndex = 0,
                ItemCode = "Item_Spellshards"
            };
            HeroGuideItem item5 = new()
            {
                GuideID = guide.ID,
                SlotType = GuideSlotType.Luxury,
                OrderIndex = 0,
                ItemCode = "Item_GrimoireOfPower"
            };

            db.HeroGuideItems.AddRange(item1, item2, item3, item4, item5);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db2 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            List<HeroGuideItem> items = await db2.HeroGuideItems
                .Where(i => i.GuideID == guide.ID)
                .OrderBy(i => i.SlotType)
                .ThenBy(i => i.OrderIndex)
                .ToListAsync();

            items.Should().HaveCount(5);

            // Verify Starting slot items and their order
            List<HeroGuideItem> startingItems = items.Where(i => i.SlotType == GuideSlotType.Starting).ToList();
            startingItems.Should().HaveCount(2);
            startingItems[0].ItemCode.Should().Be("Item_RunesOfTheBlight");
            startingItems[0].OrderIndex.Should().Be(0);
            startingItems[1].ItemCode.Should().Be("Item_HealthPotion");
            startingItems[1].OrderIndex.Should().Be(1);

            // Verify EarlyGame, Core, Luxury
            items.Single(i => i.SlotType == GuideSlotType.EarlyGame).ItemCode.Should().Be("Item_Marchers");
            items.Single(i => i.SlotType == GuideSlotType.Core).ItemCode.Should().Be("Item_Spellshards");
            items.Single(i => i.SlotType == GuideSlotType.Luxury).ItemCode.Should().Be("Item_GrimoireOfPower");
        }
    }

    [Fact]
    public async Task HeroGuideAbilities_CreateAndRead_PersistsLevelSequenceProgression()
    {
        (Account author, HeroGuide guide, MerrickContext db, string dbName) = await CreateAuthorWithGuideAsync();
        using (db)
        {
            List<HeroGuideAbility> abilities =
            [
                new() { GuideID = guide.ID, LevelSequence = 1, AbilityCode = "Ability_Pyromancer1" },
                new() { GuideID = guide.ID, LevelSequence = 2, AbilityCode = "Ability_Pyromancer2" },
                new() { GuideID = guide.ID, LevelSequence = 3, AbilityCode = "Ability_Pyromancer1" },
                new() { GuideID = guide.ID, LevelSequence = 4, AbilityCode = "Ability_Pyromancer3" },
                new() { GuideID = guide.ID, LevelSequence = 5, AbilityCode = "Ability_Pyromancer1" },
                new() { GuideID = guide.ID, LevelSequence = 6, AbilityCode = "Ability_Pyromancer4" }
            ];

            db.HeroGuideAbilities.AddRange(abilities);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db2 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            List<HeroGuideAbility> abilities = await db2.HeroGuideAbilities
                .Where(a => a.GuideID == guide.ID)
                .OrderBy(a => a.LevelSequence)
                .ToListAsync();

            abilities.Should().HaveCount(6);
            abilities[0].LevelSequence.Should().Be(1);
            abilities[0].AbilityCode.Should().Be("Ability_Pyromancer1");
            abilities[1].LevelSequence.Should().Be(2);
            abilities[1].AbilityCode.Should().Be("Ability_Pyromancer2");
            abilities[5].LevelSequence.Should().Be(6);
            abilities[5].AbilityCode.Should().Be("Ability_Pyromancer4");
        }
    }

    [Fact]
    public async Task HeroGuideItems_UpdateAndRemove_MutatesRelationalItemWithoutAffectingGuide()
    {
        (Account author, HeroGuide guide, MerrickContext db, string dbName) = await CreateAuthorWithGuideAsync();
        int itemId;

        using (db)
        {
            HeroGuideItem item = new()
            {
                GuideID = guide.ID,
                SlotType = GuideSlotType.Starting,
                OrderIndex = 0,
                ItemCode = "Item_LoggersHatchet"
            };
            db.HeroGuideItems.Add(item);
            await db.SaveChangesAsync();
            itemId = item.ID;
        }

        // Update item code
        using (MerrickContext db2 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            HeroGuideItem record = await db2.HeroGuideItems.FirstAsync(i => i.ID == itemId);
            record.ItemCode = "Item_IronBuckler";
            await db2.SaveChangesAsync();
        }

        // Verify update and delete individual record
        using (MerrickContext db3 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            HeroGuideItem record = await db3.HeroGuideItems.FirstAsync(i => i.ID == itemId);
            record.ItemCode.Should().Be("Item_IronBuckler");

            db3.HeroGuideItems.Remove(record);
            await db3.SaveChangesAsync();
        }

        // Verify removal while parent HeroGuide remains intact
        using (MerrickContext db4 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            (await db4.HeroGuideItems.AnyAsync(i => i.ID == itemId)).Should().BeFalse();
            (await db4.HeroGuides.AnyAsync(g => g.ID == guide.ID)).Should().BeTrue("Parent HeroGuide must remain intact when child item is deleted");
        }
    }

    [Fact]
    public async Task HeroGuideAbilities_UpdateAndRemove_MutatesRelationalAbilityWithoutAffectingGuide()
    {
        (Account author, HeroGuide guide, MerrickContext db, string dbName) = await CreateAuthorWithGuideAsync();
        int abilityId;

        using (db)
        {
            HeroGuideAbility ability = new()
            {
                GuideID = guide.ID,
                LevelSequence = 1,
                AbilityCode = "Ability_Pyromancer1"
            };
            db.HeroGuideAbilities.Add(ability);
            await db.SaveChangesAsync();
            abilityId = ability.ID;
        }

        // Update ability code
        using (MerrickContext db2 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            HeroGuideAbility record = await db2.HeroGuideAbilities.FirstAsync(a => a.ID == abilityId);
            record.AbilityCode = "Ability_Pyromancer2";
            await db2.SaveChangesAsync();
        }

        // Verify update and delete individual record
        using (MerrickContext db3 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            HeroGuideAbility record = await db3.HeroGuideAbilities.FirstAsync(a => a.ID == abilityId);
            record.AbilityCode.Should().Be("Ability_Pyromancer2");

            db3.HeroGuideAbilities.Remove(record);
            await db3.SaveChangesAsync();
        }

        // Verify removal while parent HeroGuide remains intact
        using (MerrickContext db4 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            (await db4.HeroGuideAbilities.AnyAsync(a => a.ID == abilityId)).Should().BeFalse();
            (await db4.HeroGuides.AnyAsync(g => g.ID == guide.ID)).Should().BeTrue("Parent HeroGuide must remain intact when child ability is deleted");
        }
    }

    [Fact]
    public async Task HeroGuide_MultipleGuides_SlotAndSequenceIsolation()
    {
        string dbName = Guid.NewGuid().ToString("N");
        MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName);

        using (db)
        {
            Role sharedRole = new() { ID = 1, Name = "User" };
            User u1 = new() { ID = 1, EmailAddress = "u1@k.net", PBKDF2PasswordHash = "h", SRPPasswordHash = "s", SRPPasswordSalt = "st", Role = sharedRole };
            Account a1 = new() { ID = 10, UserID = 1, User = u1, Name = "Acc1" };

            HeroGuide g1 = new() { ID = 101, AuthorID = 10, Author = a1, Name = "Guide 1", HeroIdentifier = "Hero_Pyromancer" };
            HeroGuide g2 = new() { ID = 102, AuthorID = 10, Author = a1, Name = "Guide 2", HeroIdentifier = "Hero_Pyromancer" };

            db.Users.Add(u1);
            db.Accounts.Add(a1);
            db.HeroGuides.AddRange(g1, g2);

            db.HeroGuideItems.AddRange(
                new HeroGuideItem { GuideID = 101, SlotType = GuideSlotType.Starting, OrderIndex = 0, ItemCode = "Item_CrushingClaws" },
                new HeroGuideItem { GuideID = 102, SlotType = GuideSlotType.Starting, OrderIndex = 0, ItemCode = "Item_DuckBoots" }
            );

            db.HeroGuideAbilities.AddRange(
                new HeroGuideAbility { GuideID = 101, LevelSequence = 1, AbilityCode = "Ability_Pyromancer1" },
                new HeroGuideAbility { GuideID = 102, LevelSequence = 1, AbilityCode = "Ability_Pyromancer2" }
            );

            await db.SaveChangesAsync();
        }

        using (MerrickContext db2 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            HeroGuideItem g1Item = await db2.HeroGuideItems.FirstAsync(i => i.GuideID == 101 && i.SlotType == GuideSlotType.Starting);
            HeroGuideItem g2Item = await db2.HeroGuideItems.FirstAsync(i => i.GuideID == 102 && i.SlotType == GuideSlotType.Starting);
            g1Item.ItemCode.Should().Be("Item_CrushingClaws");
            g2Item.ItemCode.Should().Be("Item_DuckBoots");

            HeroGuideAbility g1Abil = await db2.HeroGuideAbilities.FirstAsync(a => a.GuideID == 101 && a.LevelSequence == 1);
            HeroGuideAbility g2Abil = await db2.HeroGuideAbilities.FirstAsync(a => a.GuideID == 102 && a.LevelSequence == 1);
            g1Abil.AbilityCode.Should().Be("Ability_Pyromancer1");
            g2Abil.AbilityCode.Should().Be("Ability_Pyromancer2");
        }
    }

    // =========================================================================
    // 2. HeroItem.Cost Integer Operations, Sorting, and Filtering
    // =========================================================================

    [Fact]
    public async Task HeroItem_Cost_PersistsAndQueriesAsInteger()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            List<HeroItem> items =
            [
                new() { ID = 1, Code = "Item_MinorTotem", Name = "Minor Totem", Cost = 53, Description = "+1 All Stats", Flavor = "", IconPath = "totem.png" },
                new() { ID = 2, Code = "Item_Marchers", Name = "Marchers", Cost = 500, Description = "+50 Move Speed", Flavor = "", IconPath = "marchers.png" },
                new() { ID = 3, Code = "Item_SavageMace", Name = "Savage Mace", Cost = 5400, Description = "True strike", Flavor = "", IconPath = "mace.png" }
            ];

            db.HeroItems.AddRange(items);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db2 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            List<HeroItem> cheapItems = await db2.HeroItems.Where(i => i.Cost < 1000).OrderBy(i => i.Cost).ToListAsync();
            cheapItems.Should().HaveCount(2);
            cheapItems[0].Cost.Should().Be(53);
            cheapItems[1].Cost.Should().Be(500);

            HeroItem expensiveItem = await db2.HeroItems.FirstAsync(i => i.Cost > 5000);
            expensiveItem.Code.Should().Be("Item_SavageMace");
            expensiveItem.Cost.Should().Be(5400);
        }
    }

    [Fact]
    public async Task HeroItem_Cost_NumericalSortingVsLexicographical()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            List<HeroItem> items =
            [
                new() { ID = 10, Code = "Item_1", Name = "Item 1000", Cost = 1000, Description = "", Flavor = "", IconPath = "" },
                new() { ID = 20, Code = "Item_2", Name = "Item 500", Cost = 500, Description = "", Flavor = "", IconPath = "" },
                new() { ID = 30, Code = "Item_3", Name = "Item 53", Cost = 53, Description = "", Flavor = "", IconPath = "" },
                new() { ID = 40, Code = "Item_4", Name = "Item 2200", Cost = 2200, Description = "", Flavor = "", IconPath = "" }
            ];

            db.HeroItems.AddRange(items);
            await db.SaveChangesAsync();
        }

        using (MerrickContext db2 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            List<HeroItem> sortedItems = await db2.HeroItems.OrderBy(i => i.Cost).ToListAsync();

            // In string/lexicographical sorting: "1000" < "2200" < "500" < "53"
            // In integer numerical sorting: 53 < 500 < 1000 < 2200
            sortedItems.Select(i => i.Cost).Should().ContainInOrder([53, 500, 1000, 2200]);

            int totalCost = await db2.HeroItems.SumAsync(i => i.Cost);
            totalCost.Should().Be(53 + 500 + 1000 + 2200);
        }
    }

    // =========================================================================
    // 3. Foreign Key Cascade Deletion Tests
    // =========================================================================

    [Fact]
    public async Task HeroGuide_CascadeDelete_DeletesAllChildItemsAndAbilities()
    {
        (Account author, HeroGuide guide, MerrickContext db, string dbName) = await CreateAuthorWithGuideAsync();

        using (db)
        {
            db.HeroGuideItems.AddRange(
                new HeroGuideItem { GuideID = guide.ID, SlotType = GuideSlotType.Starting, OrderIndex = 0, ItemCode = "Item_1" },
                new HeroGuideItem { GuideID = guide.ID, SlotType = GuideSlotType.EarlyGame, OrderIndex = 0, ItemCode = "Item_2" },
                new HeroGuideItem { GuideID = guide.ID, SlotType = GuideSlotType.Core, OrderIndex = 0, ItemCode = "Item_3" },
                new HeroGuideItem { GuideID = guide.ID, SlotType = GuideSlotType.Luxury, OrderIndex = 0, ItemCode = "Item_4" }
            );

            db.HeroGuideAbilities.AddRange(
                new HeroGuideAbility { GuideID = guide.ID, LevelSequence = 1, AbilityCode = "Ability_1" },
                new HeroGuideAbility { GuideID = guide.ID, LevelSequence = 2, AbilityCode = "Ability_2" }
            );

            await db.SaveChangesAsync();
        }

        // Delete parent HeroGuide
        using (MerrickContext db2 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            HeroGuide g = await db2.HeroGuides
                .Include(x => x.Items)
                .Include(x => x.Abilities)
                .FirstAsync(x => x.ID == guide.ID);

            db2.HeroGuides.Remove(g);
            await db2.SaveChangesAsync();
        }

        // Verify all child HeroGuideItem and HeroGuideAbility records are cascaded and removed
        using (MerrickContext db3 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            (await db3.HeroGuides.AnyAsync(g => g.ID == guide.ID)).Should().BeFalse();
            (await db3.HeroGuideItems.AnyAsync(i => i.GuideID == guide.ID)).Should().BeFalse(
                "Deleting HeroGuide must cascade-delete all related HeroGuideItems");
            (await db3.HeroGuideAbilities.AnyAsync(a => a.GuideID == guide.ID)).Should().BeFalse(
                "Deleting HeroGuide must cascade-delete all related HeroGuideAbilities");
        }
    }

    [Fact]
    public async Task Account_CascadeDelete_CascadesThroughHeroGuideToItemsAndAbilities()
    {
        (Account author, HeroGuide guide, MerrickContext db, string dbName) = await CreateAuthorWithGuideAsync();

        using (db)
        {
            db.HeroGuideItems.Add(new HeroGuideItem { GuideID = guide.ID, SlotType = GuideSlotType.Starting, OrderIndex = 0, ItemCode = "Item_Runes" });
            db.HeroGuideAbilities.Add(new HeroGuideAbility { GuideID = guide.ID, LevelSequence = 1, AbilityCode = "Ability_1" });
            await db.SaveChangesAsync();
        }

        // Delete parent Account
        using (MerrickContext db2 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            Account acc = await db2.Accounts
                .Include(a => a.AuthoredGuides)
                .ThenInclude(g => g.Items)
                .Include(a => a.AuthoredGuides)
                .ThenInclude(g => g.Abilities)
                .FirstAsync(a => a.ID == author.ID);

            db2.Accounts.Remove(acc);
            await db2.SaveChangesAsync();
        }

        // Verify full cascade across Account -> HeroGuide -> HeroGuideItems and HeroGuideAbilities
        using (MerrickContext db3 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            (await db3.Accounts.AnyAsync(a => a.ID == author.ID)).Should().BeFalse();
            (await db3.HeroGuides.AnyAsync(g => g.ID == guide.ID)).Should().BeFalse();
            (await db3.HeroGuideItems.AnyAsync(i => i.GuideID == guide.ID)).Should().BeFalse();
            (await db3.HeroGuideAbilities.AnyAsync(a => a.GuideID == guide.ID)).Should().BeFalse();
        }
    }

    [Fact]
    public async Task HeroGuide_CascadeDelete_Isolation_OtherGuidesRetained()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            Role sharedRole = new() { ID = 1, Name = "User" };
            User u = new() { ID = 1, EmailAddress = "u@k.net", PBKDF2PasswordHash = "h", SRPPasswordHash = "s", SRPPasswordSalt = "st", Role = sharedRole };
            Account a = new() { ID = 10, UserID = 1, User = u, Name = "Acc" };

            HeroGuide g1 = new() { ID = 101, AuthorID = 10, Author = a, Name = "G1", HeroIdentifier = "Hero_Pyromancer" };
            HeroGuide g2 = new() { ID = 102, AuthorID = 10, Author = a, Name = "G2", HeroIdentifier = "Hero_Pyromancer" };

            db.Users.Add(u);
            db.Accounts.Add(a);
            db.HeroGuides.AddRange(g1, g2);

            db.HeroGuideItems.AddRange(
                new HeroGuideItem { GuideID = 101, SlotType = GuideSlotType.Starting, OrderIndex = 0, ItemCode = "G1_Item" },
                new HeroGuideItem { GuideID = 102, SlotType = GuideSlotType.Starting, OrderIndex = 0, ItemCode = "G2_Item" }
            );

            db.HeroGuideAbilities.AddRange(
                new HeroGuideAbility { GuideID = 101, LevelSequence = 1, AbilityCode = "G1_Abil" },
                new HeroGuideAbility { GuideID = 102, LevelSequence = 1, AbilityCode = "G2_Abil" }
            );

            await db.SaveChangesAsync();
        }

        // Delete Guide 1
        using (MerrickContext db2 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            HeroGuide g1 = await db2.HeroGuides
                .Include(g => g.Items)
                .Include(g => g.Abilities)
                .FirstAsync(g => g.ID == 101);

            db2.HeroGuides.Remove(g1);
            await db2.SaveChangesAsync();
        }

        // Verify Guide 2 and its items/abilities remain completely untouched
        using (MerrickContext db3 = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            (await db3.HeroGuides.AnyAsync(g => g.ID == 101)).Should().BeFalse();
            (await db3.HeroGuideItems.AnyAsync(i => i.GuideID == 101)).Should().BeFalse();
            (await db3.HeroGuideAbilities.AnyAsync(a => a.GuideID == 101)).Should().BeFalse();

            (await db3.HeroGuides.AnyAsync(g => g.ID == 102)).Should().BeTrue();
            (await db3.HeroGuideItems.AnyAsync(i => i.GuideID == 102)).Should().BeTrue();
            (await db3.HeroGuideAbilities.AnyAsync(a => a.GuideID == 102)).Should().BeTrue();

            HeroGuideItem g2Item = await db3.HeroGuideItems.FirstAsync(i => i.GuideID == 102);
            g2Item.ItemCode.Should().Be("G2_Item");
            HeroGuideAbility g2Abil = await db3.HeroGuideAbilities.FirstAsync(a => a.GuideID == 102);
            g2Abil.AbilityCode.Should().Be("G2_Abil");
        }
    }

    // =========================================================================
    // 4. External Serialization Helpers (Pipe/Comma Delimited Strings)
    // =========================================================================

    [Fact]
    public void HeroGuide_ExternalSerialization_StartingItems_DelimitedString()
    {
        HeroGuide guide = new()
        {
            ID = 1,
            Items =
            [
                new HeroGuideItem { GuideID = 1, SlotType = GuideSlotType.Starting, OrderIndex = 0, ItemCode = "Item_RunesOfTheBlight" },
                new HeroGuideItem { GuideID = 1, SlotType = GuideSlotType.Starting, OrderIndex = 1, ItemCode = "Item_HealthPotion" },
                new HeroGuideItem { GuideID = 1, SlotType = GuideSlotType.Starting, OrderIndex = 2, ItemCode = "Item_IronBuckler" }
            ]
        };

        guide.StartingItems.Should().Be("Item_RunesOfTheBlight,Item_HealthPotion,Item_IronBuckler");

        // Mutate via delimited string setter
        guide.StartingItems = "Item_CrushingClaws,Item_DuckBoots";
        List<HeroGuideItem> updatedStarting = guide.Items.Where(i => i.SlotType == GuideSlotType.Starting).OrderBy(i => i.OrderIndex).ToList();
        updatedStarting.Should().HaveCount(2);
        updatedStarting[0].ItemCode.Should().Be("Item_CrushingClaws");
        updatedStarting[0].OrderIndex.Should().Be(0);
        updatedStarting[1].ItemCode.Should().Be("Item_DuckBoots");
        updatedStarting[1].OrderIndex.Should().Be(1);
    }

    [Fact]
    public void HeroGuide_ExternalSerialization_AllItemSlots_IndependentSerialization()
    {
        HeroGuide guide = new()
        {
            ID = 1,
            StartingItems = "Item_RunesOfTheBlight,Item_HealthPotion",
            EarlyGameItems = "Item_Marchers,Item_SoulscreamRing",
            CoreItems = "Item_Spellshards,Item_EnhancedMarchers",
            LuxuryItems = "Item_GrimoireOfPower,Item_FrostfieldPlate"
        };

        guide.StartingItems.Should().Be("Item_RunesOfTheBlight,Item_HealthPotion");
        guide.EarlyGameItems.Should().Be("Item_Marchers,Item_SoulscreamRing");
        guide.CoreItems.Should().Be("Item_Spellshards,Item_EnhancedMarchers");
        guide.LuxuryItems.Should().Be("Item_GrimoireOfPower,Item_FrostfieldPlate");

        // Modifying one slot does not alter other slots
        guide.EarlyGameItems = "Item_Steamboots";
        guide.StartingItems.Should().Be("Item_RunesOfTheBlight,Item_HealthPotion");
        guide.EarlyGameItems.Should().Be("Item_Steamboots");
        guide.CoreItems.Should().Be("Item_Spellshards,Item_EnhancedMarchers");
        guide.LuxuryItems.Should().Be("Item_GrimoireOfPower,Item_FrostfieldPlate");
    }

    [Fact]
    public void HeroGuide_ExternalSerialization_AbilityQueue_DelimitedString()
    {
        HeroGuide guide = new()
        {
            ID = 1,
            AbilityQueue = "Ability_Pyromancer1,Ability_Pyromancer2,Ability_Pyromancer1,Ability_Pyromancer3,Ability_Pyromancer1,Ability_Pyromancer4"
        };

        guide.AbilityQueue.Should().Be("Ability_Pyromancer1,Ability_Pyromancer2,Ability_Pyromancer1,Ability_Pyromancer3,Ability_Pyromancer1,Ability_Pyromancer4");
        guide.Abilities.Should().HaveCount(6);
        guide.Abilities[0].LevelSequence.Should().Be(1);
        guide.Abilities[0].AbilityCode.Should().Be("Ability_Pyromancer1");
        guide.Abilities[5].LevelSequence.Should().Be(6);
        guide.Abilities[5].AbilityCode.Should().Be("Ability_Pyromancer4");
    }

    [Fact]
    public void HeroGuideDto_MappingExtension_PreservesFullCompatibility()
    {
        HeroGuide guide = new()
        {
            ID = 10,
            Name = "Competitive Pyro",
            HeroIdentifier = "Hero_Pyromancer",
            HeroName = "Pyromancer",
            Intro = "Intro text",
            Content = "Detailed content",
            StartingItems = "Item_RunesOfTheBlight,Item_HealthPotion",
            EarlyGameItems = "Item_Marchers",
            CoreItems = "Item_Spellshards",
            LuxuryItems = "Item_GrimoireOfPower",
            AbilityQueue = "Ability_Pyromancer1,Ability_Pyromancer2",
            Public = true,
            Featured = true,
            Author = new Account
            {
                ID = 5,
                Name = "ProPlayer",
                User = new User
                {
                    EmailAddress = "pro@kongor.net",
                    PBKDF2PasswordHash = "hash",
                    SRPPasswordHash = "salt",
                    SRPPasswordSalt = "verifier"
                }
            }
        };

        HeroGuideDto dto = guide.ToDto();

        dto.ID.Should().Be(10);
        dto.Name.Should().Be("Competitive Pyro");
        dto.StartingItems.Should().Be("Item_RunesOfTheBlight,Item_HealthPotion");
        dto.EarlyGameItems.Should().Be("Item_Marchers");
        dto.CoreItems.Should().Be("Item_Spellshards");
        dto.LuxuryItems.Should().Be("Item_GrimoireOfPower");
        dto.AbilityQueue.Should().Be("Ability_Pyromancer1,Ability_Pyromancer2");
        dto.AuthorAccountName.Should().Be("ProPlayer");
    }

    [Fact]
    public void HeroItemDto_MappingExtension_NumericCost()
    {
        HeroItem item = new()
        {
            ID = 1,
            Code = "Item_PostHaste",
            Name = "Post Haste",
            Cost = 2200,
            RecipeCost = 2200,
            Description = "Teleport boots",
            Flavor = "",
            IconPath = "posthaste.png"
        };

        HeroItemDto dto = item.ToDto();
        dto.Cost.Should().Be(2200);
        dto.ItemIdentifier.Should().Be("Item_PostHaste");
    }

    // =========================================================================
    // 5. Composite Unique Constraint and Model Metadata Tests
    // =========================================================================

    [Fact]
    public void HeroGuideItem_ModelUniqueIndex_IsConfiguredOnGuideIdSlotTypeAndOrderIndex()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName);
        IModel model = db.Model;

        IEntityType entity = model.FindEntityType(typeof(HeroGuideItem))!;
        entity.Should().NotBeNull("HeroGuideItem entity must be registered in MerrickContext model");

        // Verify composite unique index [GuideID, SlotType, OrderIndex]
        entity.GetIndexes().Should().Contain(idx =>
            idx.IsUnique &&
            idx.Properties.Count == 3 &&
            (idx.Properties.Any(p => p.Name == nameof(HeroGuideItem.GuideID) || p.Name == "GuideID" || p.Name == "HeroGuideID")) &&
            idx.Properties.Any(p => p.Name == nameof(HeroGuideItem.SlotType)) &&
            idx.Properties.Any(p => p.Name == nameof(HeroGuideItem.OrderIndex)),
            "HeroGuideItem must define a composite unique index on [GuideID, SlotType, OrderIndex]");
    }

    [Fact]
    public void HeroGuideAbility_ModelUniqueIndex_IsConfiguredOnGuideIdAndLevelSequence()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName);
        IModel model = db.Model;

        IEntityType entity = model.FindEntityType(typeof(HeroGuideAbility))!;
        entity.Should().NotBeNull("HeroGuideAbility entity must be registered in MerrickContext model");

        // Verify composite unique index [GuideID, LevelSequence]
        entity.GetIndexes().Should().Contain(idx =>
            idx.IsUnique &&
            idx.Properties.Count == 2 &&
            (idx.Properties.Any(p => p.Name == nameof(HeroGuideAbility.GuideID) || p.Name == "GuideID" || p.Name == "HeroGuideID")) &&
            idx.Properties.Any(p => p.Name == nameof(HeroGuideAbility.LevelSequence)),
            "HeroGuideAbility must define a composite unique index on [GuideID, LevelSequence]");
    }
}
