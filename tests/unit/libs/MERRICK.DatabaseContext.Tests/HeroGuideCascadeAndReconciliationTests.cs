// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Game;
using MERRICK.DatabaseContext.Entities.Statistics;
using MERRICK.DatabaseContext.Entities.Utility;
using MERRICK.DatabaseContext.Persistence;
using MERRICK.DatabaseContext.Repositories;
using MERRICK.DatabaseContext.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MERRICK.DatabaseContext.Tests;

/// <summary>
/// Empirical challenge tests authored by teamwork_preview_challenger_m4_2 for Milestone 4 (R4).
/// Adversarially stress-tests:
/// 1. Complete cascade deletions: Account -> HeroGuide -> Items/Abilities and HeroGuide -> Items/Abilities with ZERO orphans.
/// 2. Disconnected graph reconciliation in HeroGuideRepository.SaveGuideAsync (additions, removals, mutations, slot swaps, order swaps).
/// 3. Composite unique constraint validation ([GuideID, SlotType, OrderIndex] and [GuideID, LevelSequence]) and multi-guide isolation.
/// 4. DatabaseInitializer raw DDL, foreign key cascade constraints, and Row Level Security (RLS) policies audit.
/// </summary>
public class HeroGuideCascadeAndReconciliationTests
{
    private static User CreateUser(int id, string email = "challenger_m4@kongor.net") => new()
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

    private static HeroGuide CreateHeroGuide(int id, Account author, string heroId = "Hero_Pyromancer", string name = "Pyro Guide")
    {
        HeroGuide guide = new()
        {
            ID = id,
            AuthorID = author.ID,
            Author = author,
            Name = name,
            HeroName = heroId.Replace("Hero_", string.Empty),
            HeroIdentifier = heroId,
            Intro = "Intro description",
            Content = "In-depth guide content",
            TargetMap = "caldavar",
            Public = true,
            Featured = false,
            Items = [],
            Abilities = []
        };
        author.AuthoredGuides.Add(guide);
        return guide;
    }

    #region 1. Complete Cascade Deletions (Account -> HeroGuide -> Items/Abilities)

    [Trait("Category", "Manual")]
    [Fact]
    public async Task CascadeDelete_DeletingAccountWithMultipleGuides_PurgesAllGuidesItemsAndAbilities_ZeroOrphans()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        string dbName = Guid.NewGuid().ToString("N");
        int accountId = 2001;
        int guide1Id = 3001;
        int guide2Id = 3002;
        int guide3Id = 3003;

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateUser(101, "cascade_m4_1@kongor.net");
            Account account = CreateAccount(accountId, "MultiGuideAuthor", user);

            HeroGuide guide1 = CreateHeroGuide(guide1Id, account, "Hero_Pyromancer", "Pyro Burst");
            HeroGuide guide2 = CreateHeroGuide(guide2Id, account, "Hero_Valkyrie", "Valk Carry");
            HeroGuide guide3 = CreateHeroGuide(guide3Id, account, "Hero_Glacius", "Support Glacius");

            // Populate Guide 1 items (Starting, EarlyGame, Core, Luxury)
            guide1.Items.AddRange([
                new HeroGuideItem { GuideID = guide1Id, SlotType = GuideSlotType.Starting, OrderIndex = 0, ItemCode = "Item_RunesOfTheBlight" },
                new HeroGuideItem { GuideID = guide1Id, SlotType = GuideSlotType.Starting, OrderIndex = 1, ItemCode = "Item_HealthPotion" },
                new HeroGuideItem { GuideID = guide1Id, SlotType = GuideSlotType.EarlyGame, OrderIndex = 0, ItemCode = "Item_Marchers" },
                new HeroGuideItem { GuideID = guide1Id, SlotType = GuideSlotType.Core, OrderIndex = 0, ItemCode = "Item_Spellshards" },
                new HeroGuideItem { GuideID = guide1Id, SlotType = GuideSlotType.Luxury, OrderIndex = 0, ItemCode = "Item_GrimoireOfPower" }
            ]);

            // Populate Guide 1 abilities (levels 1..6)
            for (int lvl = 1; lvl <= 6; lvl++)
            {
                guide1.Abilities.Add(new HeroGuideAbility
                {
                    GuideID = guide1Id,
                    LevelSequence = lvl,
                    AbilitySlot = lvl == 6 ? 4 : (lvl % 3) + 1,
                    AbilityCode = $"Ability_Pyro_{lvl}"
                });
            }

            // Populate Guide 2 items and abilities
            guide2.Items.AddRange([
                new HeroGuideItem { GuideID = guide2Id, SlotType = GuideSlotType.Starting, OrderIndex = 0, ItemCode = "Item_DuckBoots" },
                new HeroGuideItem { GuideID = guide2Id, SlotType = GuideSlotType.Core, OrderIndex = 0, ItemCode = "Item_GeometersBane" }
            ]);
            for (int lvl = 1; lvl <= 4; lvl++)
            {
                guide2.Abilities.Add(new HeroGuideAbility
                {
                    GuideID = guide2Id,
                    LevelSequence = lvl,
                    AbilitySlot = lvl,
                    AbilityCode = $"Ability_Valk_{lvl}"
                });
            }

            // Populate Guide 3 items and abilities
            guide3.Items.Add(new HeroGuideItem { GuideID = guide3Id, SlotType = GuideSlotType.Starting, OrderIndex = 0, ItemCode = "Item_WardOfSight" });
            guide3.Abilities.Add(new HeroGuideAbility { GuideID = guide3Id, LevelSequence = 1, AbilitySlot = 1, AbilityCode = "Ability_Glacius_1" });

            db.Users.Add(user);
            db.Accounts.Add(account);
            db.HeroGuides.AddRange(guide1, guide2, guide3);
            await db.SaveChangesAsync();
        }

        // Verify pre-deletion baseline counts
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            (await db.Accounts.CountAsync(a => a.ID == accountId)).Should().Be(1);
            (await db.HeroGuides.CountAsync(g => g.AuthorID == accountId)).Should().Be(3);
            (await db.HeroGuideItems.CountAsync(i => i.GuideID == guide1Id || i.GuideID == guide2Id || i.GuideID == guide3Id)).Should().Be(5 + 2 + 1);
            (await db.HeroGuideAbilities.CountAsync(a => a.GuideID == guide1Id || a.GuideID == guide2Id || a.GuideID == guide3Id)).Should().Be(6 + 4 + 1);
        }

        // Action: Delete the Account entity
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            Account accountToDelete = await db.Accounts
                .Include(a => a.AuthoredGuides).ThenInclude(g => g.Items)
                .Include(a => a.AuthoredGuides).ThenInclude(g => g.Abilities)
                .FirstAsync(a => a.ID == accountId);

            db.Accounts.Remove(accountToDelete);
            await db.SaveChangesAsync();
        }

        // Assertion: Account, all 3 HeroGuides, and ALL 19 child items/abilities must be completely deleted
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            (await db.Accounts.AnyAsync(a => a.ID == accountId)).Should().BeFalse("Account must be removed");
            (await db.HeroGuides.AnyAsync(g => g.AuthorID == accountId)).Should().BeFalse("All authored guides must be cascade-deleted");

            List<HeroGuideItem> orphanItems = await db.HeroGuideItems
                .Where(i => i.GuideID == guide1Id || i.GuideID == guide2Id || i.GuideID == guide3Id)
                .ToListAsync();
            orphanItems.Should().BeEmpty("HeroGuideItems must have zero orphaned rows after Account cascade deletion");

            List<HeroGuideAbility> orphanAbilities = await db.HeroGuideAbilities
                .Where(a => a.GuideID == guide1Id || a.GuideID == guide2Id || a.GuideID == guide3Id)
                .ToListAsync();
            orphanAbilities.Should().BeEmpty("HeroGuideAbilities must have zero orphaned rows after Account cascade deletion");
        }
    }

    [Fact]
    public async Task CascadeDelete_DeletingHeroGuideDirectly_RemovesItemsAndAbilities_AuthorAccountPreserved()
    {
        string dbName = Guid.NewGuid().ToString("N");
        int accountId = 2002;
        int targetGuideId = 3010;
        int siblingGuideId = 3011;

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateUser(102, "guide_direct@kongor.net");
            Account account = CreateAccount(accountId, "DirectDeleteAcc", user);

            HeroGuide targetGuide = CreateHeroGuide(targetGuideId, account, "Hero_WitchSlayer", "WitchSlayer Nuke");
            HeroGuide siblingGuide = CreateHeroGuide(siblingGuideId, account, "Hero_FlintBeastwood", "Flint Sniper");

            targetGuide.Items.AddRange([
                new HeroGuideItem { GuideID = targetGuideId, SlotType = GuideSlotType.Starting, OrderIndex = 0, ItemCode = "Item_ManaPotion" },
                new HeroGuideItem { GuideID = targetGuideId, SlotType = GuideSlotType.Core, OrderIndex = 0, ItemCode = "Item_Spellshards" }
            ]);
            targetGuide.Abilities.AddRange([
                new HeroGuideAbility { GuideID = targetGuideId, LevelSequence = 1, AbilitySlot = 1, AbilityCode = "Ability_WS_Graveyard" },
                new HeroGuideAbility { GuideID = targetGuideId, LevelSequence = 2, AbilitySlot = 2, AbilityCode = "Ability_WS_Miniaturize" }
            ]);

            siblingGuide.Items.Add(new HeroGuideItem { GuideID = siblingGuideId, SlotType = GuideSlotType.Starting, OrderIndex = 0, ItemCode = "Item_LoggersHatchet" });
            siblingGuide.Abilities.Add(new HeroGuideAbility { GuideID = siblingGuideId, LevelSequence = 1, AbilitySlot = 3, AbilityCode = "Ability_Flint_Deadeye" });

            db.Users.Add(user);
            db.Accounts.Add(account);
            db.HeroGuides.AddRange(targetGuide, siblingGuide);
            await db.SaveChangesAsync();
        }

        // Action: Directly delete target HeroGuide
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            HeroGuide guideToDelete = await db.HeroGuides
                .Include(g => g.Items)
                .Include(g => g.Abilities)
                .FirstAsync(g => g.ID == targetGuideId);

            db.HeroGuides.Remove(guideToDelete);
            await db.SaveChangesAsync();
        }

        // Assertion: Target guide and its children are deleted; Author and sibling guide remain intact
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            Account author = await db.Accounts
                .Include(a => a.AuthoredGuides).ThenInclude(g => g.Items)
                .Include(a => a.AuthoredGuides).ThenInclude(g => g.Abilities)
                .FirstAsync(a => a.ID == accountId);

            author.Should().NotBeNull("Author Account must survive direct guide deletion");
            author.AuthoredGuides.Should().HaveCount(1);
            author.AuthoredGuides.Single().ID.Should().Be(siblingGuideId);

            (await db.HeroGuides.AnyAsync(g => g.ID == targetGuideId)).Should().BeFalse();
            (await db.HeroGuideItems.AnyAsync(i => i.GuideID == targetGuideId)).Should().BeFalse("Deleted guide items must be purged");
            (await db.HeroGuideAbilities.AnyAsync(a => a.GuideID == targetGuideId)).Should().BeFalse("Deleted guide abilities must be purged");

            // Sibling guide intact
            (await db.HeroGuideItems.CountAsync(i => i.GuideID == siblingGuideId)).Should().Be(1);
            (await db.HeroGuideAbilities.CountAsync(a => a.GuideID == siblingGuideId)).Should().Be(1);
        }
    }

    [Fact]
    public async Task CascadeDelete_HeroGuideRepository_DeleteGuideAsync_CascadesChildEntities_ZeroOrphans()
    {
        string dbName = Guid.NewGuid().ToString("N");
        int guideId = 3020;
        int accountId = 2003;

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateUser(103, "repo_delete@kongor.net");
            Account account = CreateAccount(accountId, "RepoDeleteUser", user);
            HeroGuide guide = CreateHeroGuide(guideId, account, "Hero_Devourer", "Mid Pudge");

            guide.Items.AddRange([
                new HeroGuideItem { GuideID = guideId, SlotType = GuideSlotType.Starting, OrderIndex = 0, ItemCode = "Item_ShieldOfTheFive" },
                new HeroGuideItem { GuideID = guideId, SlotType = GuideSlotType.Core, OrderIndex = 0, ItemCode = "Item_HelmOfTheBlackFlock" },
                new HeroGuideItem { GuideID = guideId, SlotType = GuideSlotType.Luxury, OrderIndex = 0, ItemCode = "Item_BehemothsHeart" }
            ]);

            for (int i = 1; i <= 7; i++)
            {
                guide.Abilities.Add(new HeroGuideAbility { GuideID = guideId, LevelSequence = i, AbilitySlot = 1, AbilityCode = $"Devo_Abil_{i}" });
            }

            db.Users.Add(user);
            db.Accounts.Add(account);
            db.HeroGuides.Add(guide);
            await db.SaveChangesAsync();
        }

        // Action: Call HeroGuideRepository.DeleteGuideAsync
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            HeroGuideRepository repo = new(db, NullLogger<HeroGuideRepository>.Instance);
            bool deleted = await repo.DeleteGuideAsync(guideId);
            deleted.Should().BeTrue("DeleteGuideAsync should return true for existing guide");

            // Calling again returns false
            bool reDeleted = await repo.DeleteGuideAsync(guideId);
            reDeleted.Should().BeFalse("DeleteGuideAsync should return false when guide no longer exists");
        }

        // Assertion: Verify 0 orphans in misc.HeroGuideItems and misc.HeroGuideAbilities
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            (await db.HeroGuides.AnyAsync(g => g.ID == guideId)).Should().BeFalse();
            (await db.HeroGuideItems.AnyAsync(i => i.GuideID == guideId)).Should().BeFalse();
            (await db.HeroGuideAbilities.AnyAsync(a => a.GuideID == guideId)).Should().BeFalse();
        }
    }

    [Fact]
    public async Task CascadeDelete_DeepEcosystem_AccountWithGuidesMasteryAndStoreItems_CleansUpAllRelationalLeaves()
    {
        string dbName = Guid.NewGuid().ToString("N");
        int accountId = 2004;
        int masteryId = 4004;
        int guideId = 3030;

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateUser(104, "deep_eco@kongor.net");
            Account account = CreateAccount(accountId, "DeepEcoAuthor", user);

            account.SelectedStoreItems.Add(new AccountSelectedStoreItem { AccountID = accountId, ItemCode = "avatar.pyro_alt" });
            account.IPAddressCollection.Add(new AccountIpAddress { AccountID = accountId, IPAddress = "10.0.0.1" });
            account.MACAddressCollection.Add(new AccountMacAddress { AccountID = accountId, MACAddress = "AA:BB:CC:11:22:33" });
            account.BlockedPhrases.Add(new AccountBlockedPhrase { AccountID = accountId, Phrase = "toxic" });

            Mastery mastery = new() { ID = masteryId, AccountID = accountId, Account = account, HeroExperiences = [] };
            mastery.HeroExperiences.Add(new HeroMasteryExperience { MasteryID = masteryId, HeroIdentifier = "Hero_Pyromancer", Experience = 20000 });
            account.Mastery = mastery;

            HeroGuide guide = CreateHeroGuide(guideId, account, "Hero_Pyromancer", "Full Stack Pyro");
            guide.Items.Add(new HeroGuideItem { GuideID = guideId, SlotType = GuideSlotType.Starting, OrderIndex = 0, ItemCode = "Item_Totem" });
            guide.Abilities.Add(new HeroGuideAbility { GuideID = guideId, LevelSequence = 1, AbilitySlot = 1, AbilityCode = "Ability_Wave" });

            db.Users.Add(user);
            db.Accounts.Add(account);
            db.Masteries.Add(mastery);
            db.HeroGuides.Add(guide);
            await db.SaveChangesAsync();
        }

        // Action: Delete the Account
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            Account acc = await db.Accounts
                .Include(a => a.AuthoredGuides).ThenInclude(g => g.Items)
                .Include(a => a.AuthoredGuides).ThenInclude(g => g.Abilities)
                .Include(a => a.Mastery!).ThenInclude(m => m.HeroExperiences)
                .Include(a => a.SelectedStoreItems)
                .Include(a => a.IPAddressCollection)
                .Include(a => a.MACAddressCollection)
                .Include(a => a.BlockedPhrases)
                .FirstAsync(a => a.ID == accountId);

            db.Accounts.Remove(acc);
            await db.SaveChangesAsync();
        }

        // Assert all 7 related leaf tables are completely purged for this account
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            (await db.Accounts.AnyAsync(a => a.ID == accountId)).Should().BeFalse();
            (await db.HeroGuides.AnyAsync(g => g.AuthorID == accountId)).Should().BeFalse();
            (await db.HeroGuideItems.AnyAsync(i => i.GuideID == guideId)).Should().BeFalse();
            (await db.HeroGuideAbilities.AnyAsync(a => a.GuideID == guideId)).Should().BeFalse();
            (await db.Masteries.AnyAsync(m => m.ID == masteryId)).Should().BeFalse();
            (await db.HeroMasteryExperiences.AnyAsync(h => h.MasteryID == masteryId)).Should().BeFalse();
            (await db.AccountSelectedStoreItems.AnyAsync(s => s.AccountID == accountId)).Should().BeFalse();
            (await db.AccountIpAddresses.AnyAsync(ip => ip.AccountID == accountId)).Should().BeFalse();
            (await db.AccountMacAddresses.AnyAsync(mac => mac.AccountID == accountId)).Should().BeFalse();
            (await db.AccountBlockedPhrases.AnyAsync(p => p.AccountID == accountId)).Should().BeFalse();
        }
    }

    #endregion

    #region 2. Disconnected Graph Reconciliation (HeroGuideRepository.SaveGuideAsync)

    [Fact]
    public async Task GraphReconciliation_DisconnectedGuide_AddedItemsAndAbilities_PersistsFullGraph()
    {
        string dbName = Guid.NewGuid().ToString("N");
        int guideId = 3040;
        int accountId = 2005;

        // 1. Initial State: Guide with 2 Starting items and 2 abilities
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateUser(105, "recon_add@kongor.net");
            Account account = CreateAccount(accountId, "ReconAddAuthor", user);
            HeroGuide guide = CreateHeroGuide(guideId, account, "Hero_Chronos", "Initial Chronos");

            guide.Items.AddRange([
                new HeroGuideItem { GuideID = guideId, SlotType = GuideSlotType.Starting, OrderIndex = 0, ItemCode = "Item_Runes" },
                new HeroGuideItem { GuideID = guideId, SlotType = GuideSlotType.Starting, OrderIndex = 1, ItemCode = "Item_Potion" }
            ]);
            guide.Abilities.AddRange([
                new HeroGuideAbility { GuideID = guideId, LevelSequence = 1, AbilitySlot = 1, AbilityCode = "Chronos_Q" },
                new HeroGuideAbility { GuideID = guideId, LevelSequence = 2, AbilitySlot = 2, AbilityCode = "Chronos_W" }
            ]);

            db.Users.Add(user);
            db.Accounts.Add(account);
            db.HeroGuides.Add(guide);
            await db.SaveChangesAsync();
        }

        // 2. Disconnected Update: Add items across EarlyGame, Core, Luxury, and abilities up to level 6
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            HeroGuideRepository repo = new(db, NullLogger<HeroGuideRepository>.Instance);

            HeroGuide disconnectedGuide = new()
            {
                ID = guideId,
                AuthorID = accountId,
                Name = "Expanded Chronos Build",
                HeroIdentifier = "Hero_Chronos",
                HeroName = "Chronos",
                Intro = "Late game hard carry",
                Items =
                [
                    // Preserved starting items
                    new HeroGuideItem { SlotType = GuideSlotType.Starting, OrderIndex = 0, ItemCode = "Item_Runes" },
                    new HeroGuideItem { SlotType = GuideSlotType.Starting, OrderIndex = 1, ItemCode = "Item_Potion" },
                    // Added items
                    new HeroGuideItem { SlotType = GuideSlotType.EarlyGame, OrderIndex = 0, ItemCode = "Item_Steamboots" },
                    new HeroGuideItem { SlotType = GuideSlotType.Core, OrderIndex = 0, ItemCode = "Item_ElderParasite" },
                    new HeroGuideItem { SlotType = GuideSlotType.Core, OrderIndex = 1, ItemCode = "Item_SavageMace" },
                    new HeroGuideItem { SlotType = GuideSlotType.Luxury, OrderIndex = 0, ItemCode = "Item_Wingbow" }
                ],
                Abilities =
                [
                    new HeroGuideAbility { LevelSequence = 1, AbilitySlot = 1, AbilityCode = "Chronos_Q" },
                    new HeroGuideAbility { LevelSequence = 2, AbilitySlot = 2, AbilityCode = "Chronos_W" },
                    new HeroGuideAbility { LevelSequence = 3, AbilitySlot = 1, AbilityCode = "Chronos_Q" },
                    new HeroGuideAbility { LevelSequence = 4, AbilitySlot = 3, AbilityCode = "Chronos_E" },
                    new HeroGuideAbility { LevelSequence = 5, AbilitySlot = 1, AbilityCode = "Chronos_Q" },
                    new HeroGuideAbility { LevelSequence = 6, AbilitySlot = 4, AbilityCode = "Chronos_Ultimate" }
                ]
            };

            await repo.SaveGuideAsync(disconnectedGuide);
        }

        // 3. Verification in independent context
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            HeroGuideRepository repo = new(db, NullLogger<HeroGuideRepository>.Instance);
            HeroGuide? saved = await repo.GetByIdAsync(guideId);

            saved.Should().NotBeNull();
            saved!.Name.Should().Be("Expanded Chronos Build");
            saved.Items.Should().HaveCount(6);
            saved.Abilities.Should().HaveCount(6);

            // Verify slot distribution
            saved.Items.Count(i => i.SlotType == GuideSlotType.Starting).Should().Be(2);
            saved.Items.Count(i => i.SlotType == GuideSlotType.EarlyGame).Should().Be(1);
            saved.Items.Count(i => i.SlotType == GuideSlotType.Core).Should().Be(2);
            saved.Items.Count(i => i.SlotType == GuideSlotType.Luxury).Should().Be(1);

            // Verify ability level sequence order
            List<HeroGuideAbility> orderedAbilities = saved.Abilities.OrderBy(a => a.LevelSequence).ToList();
            orderedAbilities[5].LevelSequence.Should().Be(6);
            orderedAbilities[5].AbilityCode.Should().Be("Chronos_Ultimate");
        }
    }

    [Fact]
    public async Task GraphReconciliation_DisconnectedGuide_RemovedItemsAndAbilities_PrunesOrphansFromTable()
    {
        string dbName = Guid.NewGuid().ToString("N");
        int guideId = 3041;
        int accountId = 2006;

        // 1. Initial State: Guide with 6 items and 8 abilities
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateUser(106, "recon_remove@kongor.net");
            Account account = CreateAccount(accountId, "ReconRemoveAuthor", user);
            HeroGuide guide = CreateHeroGuide(guideId, account, "Hero_Magmus", "Full Magmus");

            guide.Items.AddRange([
                new HeroGuideItem { GuideID = guideId, SlotType = GuideSlotType.Starting, OrderIndex = 0, ItemCode = "Item_Totem" },
                new HeroGuideItem { GuideID = guideId, SlotType = GuideSlotType.Starting, OrderIndex = 1, ItemCode = "Item_Runes" },
                new HeroGuideItem { GuideID = guideId, SlotType = GuideSlotType.Core, OrderIndex = 0, ItemCode = "Item_PortalKey" },
                new HeroGuideItem { GuideID = guideId, SlotType = GuideSlotType.Core, OrderIndex = 1, ItemCode = "Item_Spellshards" },
                new HeroGuideItem { GuideID = guideId, SlotType = GuideSlotType.Luxury, OrderIndex = 0, ItemCode = "Item_KuldrasSheepstick" },
                new HeroGuideItem { GuideID = guideId, SlotType = GuideSlotType.Luxury, OrderIndex = 1, ItemCode = "Item_BehemothsHeart" }
            ]);

            for (int lvl = 1; lvl <= 8; lvl++)
            {
                guide.Abilities.Add(new HeroGuideAbility { GuideID = guideId, LevelSequence = lvl, AbilitySlot = 1, AbilityCode = $"Magmus_{lvl}" });
            }

            db.Users.Add(user);
            db.Accounts.Add(account);
            db.HeroGuides.Add(guide);
            await db.SaveChangesAsync();
        }

        // 2. Disconnected Update: Prune all Luxury items, prune 1 Starting item, prune abilities down to 3
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            HeroGuideRepository repo = new(db, NullLogger<HeroGuideRepository>.Instance);

            HeroGuide disconnectedGuide = new()
            {
                ID = guideId,
                AuthorID = accountId,
                Name = "Slim Magmus Build",
                HeroIdentifier = "Hero_Magmus",
                Items =
                [
                    new HeroGuideItem { SlotType = GuideSlotType.Starting, OrderIndex = 0, ItemCode = "Item_Totem" },
                    new HeroGuideItem { SlotType = GuideSlotType.Core, OrderIndex = 0, ItemCode = "Item_PortalKey" }
                ],
                Abilities =
                [
                    new HeroGuideAbility { LevelSequence = 1, AbilitySlot = 1, AbilityCode = "Magmus_1" },
                    new HeroGuideAbility { LevelSequence = 2, AbilitySlot = 2, AbilityCode = "Magmus_2" },
                    new HeroGuideAbility { LevelSequence = 3, AbilitySlot = 1, AbilityCode = "Magmus_3" }
                ]
            };

            await repo.SaveGuideAsync(disconnectedGuide);
        }

        // 3. Assertion: Table counts must be exactly 2 items and 3 abilities (zero leftover orphan records)
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            List<HeroGuideItem> tableItems = await db.HeroGuideItems.Where(i => i.GuideID == guideId).ToListAsync();
            List<HeroGuideAbility> tableAbilities = await db.HeroGuideAbilities.Where(a => a.GuideID == guideId).ToListAsync();

            tableItems.Should().HaveCount(2, "Removed items must be completely pruned from database");
            tableItems.Select(i => i.ItemCode).Should().BeEquivalentTo(["Item_Totem", "Item_PortalKey"]);

            tableAbilities.Should().HaveCount(3, "Removed abilities must be completely pruned from database");
            tableAbilities.Select(a => a.LevelSequence).Should().BeEquivalentTo([1, 2, 3]);
        }
    }

    [Fact]
    public async Task GraphReconciliation_DisconnectedGuide_SwappedSlotsAndOrderIndices_NoConstraintCollision()
    {
        string dbName = Guid.NewGuid().ToString("N");
        int guideId = 3042;
        int accountId = 2007;

        // 1. Initial State: Starting items 0="Item_A", 1="Item_B", 2="Item_C"
        //                   Core items 0="Item_D", 1="Item_E"
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateUser(107, "recon_swap@kongor.net");
            Account account = CreateAccount(accountId, "ReconSwapAuthor", user);
            HeroGuide guide = CreateHeroGuide(guideId, account, "Hero_Valkyrie", "Swap Test");

            guide.Items.AddRange([
                new HeroGuideItem { GuideID = guideId, SlotType = GuideSlotType.Starting, OrderIndex = 0, ItemCode = "Item_A" },
                new HeroGuideItem { GuideID = guideId, SlotType = GuideSlotType.Starting, OrderIndex = 1, ItemCode = "Item_B" },
                new HeroGuideItem { GuideID = guideId, SlotType = GuideSlotType.Starting, OrderIndex = 2, ItemCode = "Item_C" },
                new HeroGuideItem { GuideID = guideId, SlotType = GuideSlotType.Core, OrderIndex = 0, ItemCode = "Item_D" },
                new HeroGuideItem { GuideID = guideId, SlotType = GuideSlotType.Core, OrderIndex = 1, ItemCode = "Item_E" }
            ]);

            db.Users.Add(user);
            db.Accounts.Add(account);
            db.HeroGuides.Add(guide);
            await db.SaveChangesAsync();
        }

        // 2. Disconnected Update:
        //    - Reverse order of Starting items: 0="Item_C", 1="Item_B", 2="Item_A"
        //    - Move "Item_D" from Core to Starting (OrderIndex 3)
        //    - Swap Core: 0="Item_E", 1="Item_BrandNew"
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            HeroGuideRepository repo = new(db, NullLogger<HeroGuideRepository>.Instance);

            HeroGuide disconnectedGuide = new()
            {
                ID = guideId,
                AuthorID = accountId,
                Name = "Swapped Build",
                HeroIdentifier = "Hero_Valkyrie",
                Items =
                [
                    new HeroGuideItem { SlotType = GuideSlotType.Starting, OrderIndex = 0, ItemCode = "Item_C" },
                    new HeroGuideItem { SlotType = GuideSlotType.Starting, OrderIndex = 1, ItemCode = "Item_B" },
                    new HeroGuideItem { SlotType = GuideSlotType.Starting, OrderIndex = 2, ItemCode = "Item_A" },
                    new HeroGuideItem { SlotType = GuideSlotType.Starting, OrderIndex = 3, ItemCode = "Item_D" },
                    new HeroGuideItem { SlotType = GuideSlotType.Core, OrderIndex = 0, ItemCode = "Item_E" },
                    new HeroGuideItem { SlotType = GuideSlotType.Core, OrderIndex = 1, ItemCode = "Item_BrandNew" }
                ],
                Abilities = []
            };

            // Must reconcile cleanly without unique index violation on [GuideID, SlotType, OrderIndex]
            Func<Task> act = async () => await repo.SaveGuideAsync(disconnectedGuide);
            await act.Should().NotThrowAsync("Swapping slots and order indices in disconnected graph must not collide on unique index");
        }

        // 3. Verify in independent context
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            HeroGuideRepository repo = new(db, NullLogger<HeroGuideRepository>.Instance);
            HeroGuide? saved = await repo.GetByIdAsync(guideId);

            saved.Should().NotBeNull();
            List<HeroGuideItem> startingItems = saved!.Items
                .Where(i => i.SlotType == GuideSlotType.Starting)
                .OrderBy(i => i.OrderIndex)
                .ToList();

            startingItems.Should().HaveCount(4);
            startingItems[0].ItemCode.Should().Be("Item_C");
            startingItems[1].ItemCode.Should().Be("Item_B");
            startingItems[2].ItemCode.Should().Be("Item_A");
            startingItems[3].ItemCode.Should().Be("Item_D");

            List<HeroGuideItem> coreItems = saved.Items
                .Where(i => i.SlotType == GuideSlotType.Core)
                .OrderBy(i => i.OrderIndex)
                .ToList();
            coreItems.Should().HaveCount(2);
            coreItems[0].ItemCode.Should().Be("Item_E");
            coreItems[1].ItemCode.Should().Be("Item_BrandNew");
        }
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task GraphReconciliation_DisconnectedGuide_SwappedAbilitySequences_ReconcilesCleanly()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        string dbName = Guid.NewGuid().ToString("N");
        int guideId = 3043;
        int accountId = 2008;

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateUser(108, "recon_ability_swap@kongor.net");
            Account account = CreateAccount(accountId, "AbilitySwapAuthor", user);
            HeroGuide guide = CreateHeroGuide(guideId, account, "Hero_Accursed", "Accursed Build");

            guide.Abilities.AddRange([
                new HeroGuideAbility { GuideID = guideId, LevelSequence = 1, AbilitySlot = 1, AbilityCode = "Ability_Cauterize" },
                new HeroGuideAbility { GuideID = guideId, LevelSequence = 2, AbilitySlot = 2, AbilityCode = "Ability_Shield" },
                new HeroGuideAbility { GuideID = guideId, LevelSequence = 3, AbilitySlot = 3, AbilityCode = "Ability_Sear" },
                new HeroGuideAbility { GuideID = guideId, LevelSequence = 4, AbilitySlot = 4, AbilityCode = "Ability_FlameCursed" }
            ]);

            db.Users.Add(user);
            db.Accounts.Add(account);
            db.HeroGuides.Add(guide);
            await db.SaveChangesAsync();
        }

        // Action: Disconnected update completely re-arranging level sequences
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            HeroGuideRepository repo = new(db, NullLogger<HeroGuideRepository>.Instance);

            HeroGuide disconnectedGuide = new()
            {
                ID = guideId,
                AuthorID = accountId,
                Name = "Max Shield First",
                HeroIdentifier = "Hero_Accursed",
                Abilities =
                [
                    new HeroGuideAbility { LevelSequence = 1, AbilitySlot = 2, AbilityCode = "Ability_Shield" },
                    new HeroGuideAbility { LevelSequence = 2, AbilitySlot = 1, AbilityCode = "Ability_Cauterize" },
                    new HeroGuideAbility { LevelSequence = 3, AbilitySlot = 2, AbilityCode = "Ability_Shield" },
                    new HeroGuideAbility { LevelSequence = 4, AbilitySlot = 4, AbilityCode = "Ability_FlameCursed" }
                ]
            };

            await repo.SaveGuideAsync(disconnectedGuide);
        }

        // Assertion: Level sequence mapping is accurately updated
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            HeroGuideRepository repo = new(db, NullLogger<HeroGuideRepository>.Instance);
            HeroGuide? saved = await repo.GetByIdAsync(guideId);

            saved.Should().NotBeNull();
            List<HeroGuideAbility> abilities = saved!.Abilities.OrderBy(a => a.LevelSequence).ToList();
            abilities.Should().HaveCount(4);
            abilities[0].LevelSequence.Should().Be(1);
            abilities[0].AbilityCode.Should().Be("Ability_Shield");
            abilities[1].LevelSequence.Should().Be(2);
            abilities[1].AbilityCode.Should().Be("Ability_Cauterize");
            abilities[2].LevelSequence.Should().Be(3);
            abilities[2].AbilityCode.Should().Be("Ability_Shield");
            abilities[3].LevelSequence.Should().Be(4);
            abilities[3].AbilityCode.Should().Be("Ability_FlameCursed");
        }
    }

    [Fact]
    public async Task GraphReconciliation_DisconnectedGuide_ClearAllItemsAndAbilities_LeavesZeroChildren()
    {
        string dbName = Guid.NewGuid().ToString("N");
        int guideId = 3044;
        int accountId = 2009;

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateUser(109, "recon_clear@kongor.net");
            Account account = CreateAccount(accountId, "ReconClearAuthor", user);
            HeroGuide guide = CreateHeroGuide(guideId, account, "Hero_WretchedHag", "Nuke Build");

            guide.Items.Add(new HeroGuideItem { GuideID = guideId, SlotType = GuideSlotType.Starting, OrderIndex = 0, ItemCode = "Item_Bottle" });
            guide.Abilities.Add(new HeroGuideAbility { GuideID = guideId, LevelSequence = 1, AbilitySlot = 1, AbilityCode = "Hag_Q" });

            db.Users.Add(user);
            db.Accounts.Add(account);
            db.HeroGuides.Add(guide);
            await db.SaveChangesAsync();
        }

        // Action: Disconnected update passing empty Items and empty Abilities
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            HeroGuideRepository repo = new(db, NullLogger<HeroGuideRepository>.Instance);

            HeroGuide disconnectedGuide = new()
            {
                ID = guideId,
                AuthorID = accountId,
                Name = "Bare Bones Guide",
                HeroIdentifier = "Hero_WretchedHag",
                Items = [],
                Abilities = []
            };

            await repo.SaveGuideAsync(disconnectedGuide);
        }

        // Assert: Children completely wiped, parent guide persists
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            HeroGuideRepository repo = new(db, NullLogger<HeroGuideRepository>.Instance);
            HeroGuide? saved = await repo.GetByIdAsync(guideId);

            saved.Should().NotBeNull();
            saved!.Name.Should().Be("Bare Bones Guide");
            saved.Items.Should().BeEmpty();
            saved.Abilities.Should().BeEmpty();

            (await db.HeroGuideItems.AnyAsync(i => i.GuideID == guideId)).Should().BeFalse();
            (await db.HeroGuideAbilities.AnyAsync(a => a.GuideID == guideId)).Should().BeFalse();
        }
    }

    [Fact]
    public async Task GraphReconciliation_MultipleConsecutiveDisconnectedUpdates_ZeroResidualDrift()
    {
        string dbName = Guid.NewGuid().ToString("N");
        int guideId = 3045;
        int accountId = 2010;

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateUser(110, "recon_iterations@kongor.net");
            Account account = CreateAccount(accountId, "IterativeAuthor", user);
            HeroGuide guide = CreateHeroGuide(guideId, account, "Hero_DementedShaman", "Iteration 0");

            db.Users.Add(user);
            db.Accounts.Add(account);
            db.HeroGuides.Add(guide);
            await db.SaveChangesAsync();
        }

        // Perform 4 distinct consecutive updates
        for (int iter = 1; iter <= 4; iter++)
        {
            using MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName);
            HeroGuideRepository repo = new(db, NullLogger<HeroGuideRepository>.Instance);

            HeroGuide incoming = new()
            {
                ID = guideId,
                AuthorID = accountId,
                Name = $"Iteration {iter}",
                HeroIdentifier = "Hero_DementedShaman",
                Items = [],
                Abilities = []
            };

            // Add 'iter' items and 'iter' abilities
            for (int k = 0; k < iter; k++)
            {
                incoming.Items.Add(new HeroGuideItem
                {
                    SlotType = GuideSlotType.Starting,
                    OrderIndex = k,
                    ItemCode = $"Item_Iter_{iter}_{k}"
                });
                incoming.Abilities.Add(new HeroGuideAbility
                {
                    LevelSequence = k + 1,
                    AbilitySlot = (k % 4) + 1,
                    AbilityCode = $"Abil_Iter_{iter}_{k + 1}"
                });
            }

            await repo.SaveGuideAsync(incoming);
        }

        // Final verification: Exactly 4 items and 4 abilities, matching iteration 4
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            HeroGuideRepository repo = new(db, NullLogger<HeroGuideRepository>.Instance);
            HeroGuide? final = await repo.GetByIdAsync(guideId);

            final.Should().NotBeNull();
            final!.Name.Should().Be("Iteration 4");
            final.Items.Should().HaveCount(4);
            final.Abilities.Should().HaveCount(4);

            List<HeroGuideItem> orderedItems = final.Items.OrderBy(i => i.OrderIndex).ToList();
            for (int k = 0; k < 4; k++)
            {
                orderedItems[k].ItemCode.Should().Be($"Item_Iter_4_{k}");
            }

            // Verify total rows in table for this guide is strictly 4 items and 4 abilities (no lingering rows from iter 1, 2, 3)
            (await db.HeroGuideItems.CountAsync(i => i.GuideID == guideId)).Should().Be(4);
            (await db.HeroGuideAbilities.CountAsync(a => a.GuideID == guideId)).Should().Be(4);
        }
    }

    #endregion

    #region 3. Composite Unique Constraints & Multi-Guide Isolation

    [Fact]
    public void CompositeUniqueConstraint_HeroGuideItems_ModelMetadata_DeclaresUQIndex()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName);
        IModel model = db.Model;

        IEntityType entity = model.FindEntityType(typeof(HeroGuideItem))!;
        entity.Should().NotBeNull("HeroGuideItem entity must exist in model");

        IIndex? uqIndex = entity.GetIndexes().FirstOrDefault(idx =>
            idx.IsUnique &&
            idx.Properties.Count == 3 &&
            idx.Properties.Any(p => p.Name == nameof(HeroGuideItem.GuideID) || p.Name == "GuideID" || p.Name == "HeroGuideID") &&
            idx.Properties.Any(p => p.Name == nameof(HeroGuideItem.SlotType)) &&
            idx.Properties.Any(p => p.Name == nameof(HeroGuideItem.OrderIndex)));

        uqIndex.Should().NotBeNull("HeroGuideItem must have a composite unique index on [GuideID, SlotType, OrderIndex]");
        uqIndex!.GetDatabaseName().Should().Be("UQ_HeroGuideItems_GuideID_SlotType_OrderIndex");

        // Verify covering index on GuideID
        IIndex? guideIdIndex = entity.GetIndexes().FirstOrDefault(idx =>
            !idx.IsUnique &&
            idx.Properties.Count == 1 &&
            idx.Properties.Any(p => p.Name == nameof(HeroGuideItem.GuideID)));
        guideIdIndex.Should().NotBeNull("HeroGuideItem must have a covering index on GuideID for efficient cascade and join operations");
    }

    [Fact]
    public void CompositeUniqueConstraint_HeroGuideAbilities_ModelMetadata_DeclaresUQIndex()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName);
        IModel model = db.Model;

        IEntityType entity = model.FindEntityType(typeof(HeroGuideAbility))!;
        entity.Should().NotBeNull("HeroGuideAbility entity must exist in model");

        IIndex? uqIndex = entity.GetIndexes().FirstOrDefault(idx =>
            idx.IsUnique &&
            idx.Properties.Count == 2 &&
            idx.Properties.Any(p => p.Name == nameof(HeroGuideAbility.GuideID) || p.Name == "GuideID" || p.Name == "HeroGuideID") &&
            idx.Properties.Any(p => p.Name == nameof(HeroGuideAbility.LevelSequence)));

        uqIndex.Should().NotBeNull("HeroGuideAbility must have a composite unique index on [GuideID, LevelSequence]");
        uqIndex!.GetDatabaseName().Should().Be("UQ_HeroGuideAbilities_GuideID_LevelSequence");

        // Verify covering index on GuideID
        IIndex? guideIdIndex = entity.GetIndexes().FirstOrDefault(idx =>
            !idx.IsUnique &&
            idx.Properties.Count == 1 &&
            idx.Properties.Any(p => p.Name == nameof(HeroGuideAbility.GuideID)));
        guideIdIndex.Should().NotBeNull("HeroGuideAbility must have a covering index on GuideID for efficient cascade and join operations");
    }

    [Fact]
    public async Task CompositeUniqueConstraint_MultiGuideIsolation_PermitsIdenticalSlotAndSequenceAcrossGuides()
    {
        string dbName = Guid.NewGuid().ToString("N");
        int guide1Id = 3051;
        int guide2Id = 3052;

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            User user = CreateUser(111, "multi_uq@kongor.net");
            Account account = CreateAccount(2011, "MultiUqAuthor", user);

            HeroGuide g1 = CreateHeroGuide(guide1Id, account, "Hero_Pyromancer", "Pyro 1");
            HeroGuide g2 = CreateHeroGuide(guide2Id, account, "Hero_Pyromancer", "Pyro 2");

            // Both guides have Starting Slot OrderIndex 0, 1, 2
            g1.Items.AddRange([
                new HeroGuideItem { GuideID = guide1Id, SlotType = GuideSlotType.Starting, OrderIndex = 0, ItemCode = "Item_Totem" },
                new HeroGuideItem { GuideID = guide1Id, SlotType = GuideSlotType.Starting, OrderIndex = 1, ItemCode = "Item_Runes" },
                new HeroGuideItem { GuideID = guide1Id, SlotType = GuideSlotType.Starting, OrderIndex = 2, ItemCode = "Item_Potion" }
            ]);
            g2.Items.AddRange([
                new HeroGuideItem { GuideID = guide2Id, SlotType = GuideSlotType.Starting, OrderIndex = 0, ItemCode = "Item_Hatchet" },
                new HeroGuideItem { GuideID = guide2Id, SlotType = GuideSlotType.Starting, OrderIndex = 1, ItemCode = "Item_Buckler" },
                new HeroGuideItem { GuideID = guide2Id, SlotType = GuideSlotType.Starting, OrderIndex = 2, ItemCode = "Item_Blight" }
            ]);

            // Both guides have LevelSequences 1..5
            for (int lvl = 1; lvl <= 5; lvl++)
            {
                g1.Abilities.Add(new HeroGuideAbility { GuideID = guide1Id, LevelSequence = lvl, AbilitySlot = 1, AbilityCode = $"G1_Abil_{lvl}" });
                g2.Abilities.Add(new HeroGuideAbility { GuideID = guide2Id, LevelSequence = lvl, AbilitySlot = 2, AbilityCode = $"G2_Abil_{lvl}" });
            }

            db.Users.Add(user);
            db.Accounts.Add(account);
            db.HeroGuides.AddRange(g1, g2);

            Func<Task> act = async () => await db.SaveChangesAsync();
            await act.Should().NotThrowAsync("Different guides sharing identical slot, order, and level sequences must not conflict");
        }

        // Verify independent retrieval
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            List<HeroGuideItem> g1Items = await db.HeroGuideItems.Where(i => i.GuideID == guide1Id).OrderBy(i => i.OrderIndex).ToListAsync();
            List<HeroGuideItem> g2Items = await db.HeroGuideItems.Where(i => i.GuideID == guide2Id).OrderBy(i => i.OrderIndex).ToListAsync();

            g1Items.Should().HaveCount(3);
            g2Items.Should().HaveCount(3);
            g1Items[0].ItemCode.Should().Be("Item_Totem");
            g2Items[0].ItemCode.Should().Be("Item_Hatchet");
        }
    }

    [Fact]
    public void EntitySemantics_HeroGuideItemAndAbility_EqualityAndToString_AreDeterministic()
    {
        HeroGuideItem item1 = new() { GuideID = 10, SlotType = GuideSlotType.Starting, OrderIndex = 0, ItemCode = "Item_Boots" };
        HeroGuideItem item2 = new() { GuideID = 10, SlotType = GuideSlotType.Starting, OrderIndex = 0, ItemCode = "item_boots" }; // case insensitive
        HeroGuideItem item3 = new() { GuideID = 10, SlotType = GuideSlotType.Starting, OrderIndex = 1, ItemCode = "Item_Boots" };

        item1.Should().Be(item2, "Item equality is case-insensitive on ItemCode with identical GuideID, SlotType, and OrderIndex");
        item1.GetHashCode().Should().Be(item2.GetHashCode());
        item1.Should().NotBe(item3);
        item1.ToString().Should().Contain("[Starting #0] Item_Boots (Guide #10)");

        HeroGuideAbility abil1 = new() { GuideID = 10, LevelSequence = 1, AbilitySlot = 1, AbilityCode = "Ability_Wave" };
        HeroGuideAbility abil2 = new() { GuideID = 10, LevelSequence = 1, AbilitySlot = 1, AbilityCode = "ability_wave" };
        HeroGuideAbility abil3 = new() { GuideID = 10, LevelSequence = 2, AbilitySlot = 1, AbilityCode = "Ability_Wave" };

        abil1.Should().Be(abil2);
        abil1.GetHashCode().Should().Be(abil2.GetHashCode());
        abil1.Should().NotBe(abil3);
        abil1.ToString().Should().Contain("[Level 1] Slot 1 (Ability_Wave) (Guide #10)");
    }

    #endregion

    #region 4. DatabaseInitializer DDL, Foreign Key Cascade, and RLS Audit

    [Fact]
    public void DatabaseInitializer_HeroGuideItemsAndAbilities_DDL_Cascade_And_RLS_Verified()
    {
        string? projectRoot = FindProjectRoot();
        projectRoot.Should().NotBeNull("Project root directory must be discoverable");

        string ddlPath = Path.Combine(projectRoot!, "src", "libs", "database-context", "Services", "DatabaseInitializer.cs");
        File.Exists(ddlPath).Should().BeTrue("DatabaseInitializer.cs must exist");
        string ddlCode = File.ReadAllText(ddlPath);

        // 1. DDL Section 2: Table definitions in misc schema with ON DELETE CASCADE
        ddlCode.Should().Contain("\"\"misc\"\".\"\"HeroGuideItems\"\"", "DDL must create misc.HeroGuideItems table");
        ddlCode.Should().Contain("\"\"misc\"\".\"\"HeroGuideAbilities\"\"", "DDL must create misc.HeroGuideAbilities table");
        ddlCode.Should().Contain("\"\"UQ_HeroGuideItems_GuideID_SlotType_OrderIndex\"\" UNIQUE", "DDL must enforce composite unique constraint for items");
        ddlCode.Should().Contain("\"\"UQ_HeroGuideAbilities_GuideID_LevelSequence\"\" UNIQUE", "DDL must enforce composite unique constraint for abilities");
        ddlCode.Should().Contain("REFERENCES \"\"misc\"\".\"\"HeroGuides\"\"(\"\"ID\"\") ON DELETE CASCADE", "DDL foreign keys must cascade delete");

        // 2. DDL Section 2: HeroItem.Cost column integer migration
        ddlCode.Should().Contain("\"\"misc\"\".\"\"HeroItems\"\"", "DDL must manage misc.HeroItems");
        ddlCode.Should().Contain("ALTER COLUMN \"\"Cost\"\" TYPE integer", "DDL must migrate HeroItems.Cost to integer");

        // 3. DDL Section 3: Idempotent Foreign Key constraints
        ddlCode.Should().Contain("FK_HeroGuideItems_HeroGuides_GuideID");
        ddlCode.Should().Contain("FK_HeroGuideAbilities_HeroGuides_GuideID");

        // 4. DDL Section 4: Row-Level Security (RLS) registration and policies
        ddlCode.Should().Contain("['misc', 'HeroGuideItems']", "misc.HeroGuideItems must be registered in tables_to_rls");
        ddlCode.Should().Contain("['misc', 'HeroGuideAbilities']", "misc.HeroGuideAbilities must be registered in tables_to_rls");
        ddlCode.Should().Contain("CREATE POLICY \"\"Public Read HeroGuideItems\"\"", "Public Read RLS policy must be created for HeroGuideItems");
        ddlCode.Should().Contain("CREATE POLICY \"\"Public Read HeroGuideAbilities\"\"", "Public Read RLS policy must be created for HeroGuideAbilities");
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
