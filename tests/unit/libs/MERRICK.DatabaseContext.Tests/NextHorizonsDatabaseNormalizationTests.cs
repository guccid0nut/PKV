// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: NextHorizonsDatabaseNormalizationTests.cs
// System: MERRICK Unit Tests - Next Horizons 1NF Relational Timeline & Components
// ============================================================================

using FluentAssertions;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Game;
using MERRICK.DatabaseContext.Entities.Statistics;
using MERRICK.DatabaseContext.Entities.Utility;
using MERRICK.DatabaseContext.Persistence;
using MERRICK.DatabaseContext.Repositories;
using MERRICK.DatabaseContext.Services;
using MERRICK.DatabaseContext.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.Data;
using Xunit;

namespace MERRICK.DatabaseContext.Tests;

public class NextHorizonsDatabaseNormalizationTests
{
    private static async Task<(PlayerStatistics Stats, MatchStatistics Match, MerrickContext Context)> CreateMatchAndPlayerStatsAsync()
    {
        MerrickContext context = TestDbContextFactory.CreateInMemoryContext(Guid.NewGuid().ToString("N"));

        User user = new()
        {
            ID = 1,
            EmailAddress = "nexthorizons@kongor.net",
            PBKDF2PasswordHash = "hash",
            SRPPasswordHash = "srp_h",
            SRPPasswordSalt = "srp_s",
            Role = new Role { ID = 1, Name = "User" }
        };

        Account account = new()
        {
            ID = 101,
            UserID = 1,
            User = user,
            Name = "HorizonPlayer"
        };

        MatchStatistics match = new()
        {
            ID = 1,
            MatchID = 88001,
            Map = "caldavar",
            HostAccountName = "Server1"
        };

        PlayerStatistics stats = new()
        {
            ID = 10,
            MatchID = 88001,
            AccountID = 101,
            AccountName = "HorizonPlayer",
            HeroIdentifier = "Hero_Pyromancer",
            HeroLevel = 25,
            Win = 1
        };

        context.Users.Add(user);
        context.Accounts.Add(account);
        context.MatchStatistics.Add(match);
        context.PlayerStatistics.Add(stats);
        await context.SaveChangesAsync();

        return (stats, match, context);
    }

    [Fact]
    public async Task MatchAbilityEvents_RelationalCRUD_PersistsAndNavigates()
    {
        var (stats, _, db) = await CreateMatchAndPlayerStatsAsync();

        stats.AbilityEvents.Add(new MatchAbilityEvent
        {
            PlayerStatisticID = stats.ID,
            MatchID = stats.MatchID,
            AccountID = stats.AccountID,
            HeroIdentifier = "Hero_Pyromancer",
            AbilityIdentifier = "Ability_Pyromancer1",
            SlotIndex = 0,
            Level = 1,
            GameTimeSeconds = 0
        });

        stats.AbilityEvents.Add(new MatchAbilityEvent
        {
            PlayerStatisticID = stats.ID,
            MatchID = stats.MatchID,
            AccountID = stats.AccountID,
            HeroIdentifier = "Hero_Pyromancer",
            AbilityIdentifier = "Ability_Pyromancer2",
            SlotIndex = 1,
            Level = 1,
            GameTimeSeconds = 45
        });

        await db.SaveChangesAsync();

        PlayerStatistics? loaded = await db.PlayerStatistics
            .Include(p => p.AbilityEvents)
            .FirstOrDefaultAsync(p => p.ID == stats.ID);

        loaded.Should().NotBeNull();
        loaded!.AbilityEvents.Should().HaveCount(2);
        loaded.AbilityEvents[0].AbilityIdentifier.Should().Be("Ability_Pyromancer1");
        loaded.AbilityEvents[1].AbilityIdentifier.Should().Be("Ability_Pyromancer2");
    }

    [Fact]
    public async Task MatchFragsAndAssists_RelationalCRUD_PersistsAndCascades()
    {
        var (_, match, db) = await CreateMatchAndPlayerStatsAsync();

        MatchFrag frag = new()
        {
            MatchID = match.MatchID,
            KillerAccountID = 101,
            VictimAccountID = 202,
            GameTimeSeconds = 120,
            FirstBlood = true
        };

        frag.Assists.Add(new MatchFragAssist
        {
            AssistingAccountID = 303
        });

        frag.Assists.Add(new MatchFragAssist
        {
            AssistingAccountID = 404
        });

        match.Frags.Add(frag);
        await db.SaveChangesAsync();

        MatchStatistics? loaded = await db.MatchStatistics
            .Include(m => m.Frags)
                .ThenInclude(f => f.Assists)
            .FirstOrDefaultAsync(m => m.MatchID == match.MatchID);

        loaded.Should().NotBeNull();
        loaded!.Frags.Should().HaveCount(1);
        loaded.Frags[0].FirstBlood.Should().BeTrue();
        loaded.Frags[0].Assists.Should().HaveCount(2);

        // Verify Cascade Delete
        db.MatchStatistics.Remove(loaded);
        await db.SaveChangesAsync();

        (await db.MatchFrags.CountAsync()).Should().Be(0);
        (await db.MatchFragAssists.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task HeroItemComponents_RelationalCRUD_PersistsAndNavigates()
    {
        MerrickContext db = TestDbContextFactory.CreateInMemoryContext(Guid.NewGuid().ToString("N"));

        HeroItem item = new()
        {
            ID = 50,
            Code = "item_firebrand",
            Name = "Firebrand",
            Description = "A blade forged in flame.",
            Flavor = "Blazing fury.",
            IconPath = "firebrand.tga",
            Cost = 2200,
            IsActive = true
        };

        item.Components.Add(new HeroItemComponent
        {
            ParentItemID = item.ID,
            ComponentItemCode = "item_iron_shield",
            Quantity = 1
        });

        item.Components.Add(new HeroItemComponent
        {
            ParentItemID = item.ID,
            ComponentItemCode = "item_blade_of_alacrity",
            Quantity = 2
        });

        db.HeroItems.Add(item);
        await db.SaveChangesAsync();

        HeroItem? loaded = await db.HeroItems
            .Include(i => i.Components)
            .FirstOrDefaultAsync(i => i.ID == item.ID);

        loaded.Should().NotBeNull();
        loaded!.Components.Should().HaveCount(2);
        loaded.Components.First(c => c.ComponentItemCode == "item_blade_of_alacrity").Quantity.Should().Be(2);

        // Verify Cascade Delete
        db.HeroItems.Remove(loaded);
        await db.SaveChangesAsync();

        (await db.HeroItemComponents.CountAsync()).Should().Be(0);
    }

    [Fact]
    public void PlayerStatistics_AbilityHistory_DualChannelSync_WorksBothWays()
    {
        PlayerStatistics stats = new()
        {
            ID = 1,
            MatchID = 100,
            AccountID = 50,
            HeroIdentifier = "Hero_Valkyrie"
        };

        // Setter syncs AbilityEvents
        stats.AbilityHistory =
        [
            new AbilityEvent { HeroName = "Hero_Valkyrie", AbilityName = "Ability_Valkyrie1", SlotIndex = 0, GameTimeSeconds = 0 },
            new AbilityEvent { HeroName = "Hero_Valkyrie", AbilityName = "Ability_Valkyrie2", SlotIndex = 1, GameTimeSeconds = 30 }
        ];

        stats.AbilityEvents.Should().HaveCount(2);
        stats.AbilityEvents[0].AbilityIdentifier.Should().Be("Ability_Valkyrie1");
        stats.AbilityEvents[1].AbilityIdentifier.Should().Be("Ability_Valkyrie2");

        // Getter computes from AbilityEvents
        stats.AbilityEvents.Add(new MatchAbilityEvent
        {
            PlayerStatisticID = 1,
            MatchID = 100,
            AccountID = 50,
            HeroIdentifier = "Hero_Valkyrie",
            AbilityIdentifier = "Ability_Valkyrie4",
            SlotIndex = 3,
            Level = 1,
            GameTimeSeconds = 360
        });

        stats.AbilityHistory.Should().HaveCount(3);
        stats.AbilityHistory.Last().AbilityName.Should().Be("Ability_Valkyrie4");
    }

    [Fact]
    public void MatchStatistics_FragHistory_DualChannelSync_WorksBothWays()
    {
        MatchStatistics match = new()
        {
            ID = 1,
            MatchID = 500,
            Map = "caldavar"
        };

        // Setter syncs Frags
        match.FragHistory =
        [
            new FragEvent { SourceID = 101, TargetID = 202, GameTimeSeconds = 90, SupporterIDs = [303] }
        ];

        match.Frags.Should().HaveCount(1);
        match.Frags[0].KillerAccountID.Should().Be(101);
        match.Frags[0].VictimAccountID.Should().Be(202);
        match.Frags[0].Assists.Should().HaveCount(1);
        match.Frags[0].Assists[0].AssistingAccountID.Should().Be(303);

        // Getter computes from Frags
        match.Frags.Add(new MatchFrag
        {
            MatchID = 500,
            KillerAccountID = 202,
            VictimAccountID = 101,
            GameTimeSeconds = 180
        });

        match.FragHistory.Should().HaveCount(2);
        match.FragHistory[1].SourceID.Should().Be(202);
        match.FragHistory[1].TargetID.Should().Be(101);
    }

    [Fact]
    public void HeroItem_ComponentCodesJson_DualChannelSync_WorksBothWays()
    {
        HeroItem item = new()
        {
            ID = 1,
            Code = "item_dawnbringer",
            Name = "Dawnbringer",
            Description = "The ultimate blade of dawn.",
            Flavor = "Light piercing the darkness.",
            IconPath = "dawnbringer.tga"
        };

        // Setter syncs Components
        item.ComponentCodesJson = "[\"item_frostburn\", \"item_firebrand\"]";

        item.Components.Should().HaveCount(2);
        item.Components.Select(c => c.ComponentItemCode).Should().Contain("item_frostburn");
        item.Components.Select(c => c.ComponentItemCode).Should().Contain("item_firebrand");

        // Getter formats JSON
        item.Components.Add(new HeroItemComponent
        {
            ParentItemID = 1,
            ComponentItemCode = "item_recipe",
            Quantity = 1
        });

        item.ComponentCodesJson.Should().Contain("item_recipe");
    }

    [Fact]
    public async Task MatchHistoryRepository_GetHeroSkillBuildWinRatesAsync_AggregatesAccurately()
    {
        MerrickContext db = TestDbContextFactory.CreateInMemoryContext(Guid.NewGuid().ToString("N"));
        MatchHistoryRepository repo = new(db, NullLogger<MatchHistoryRepository>.Instance);

        MatchStatistics m1 = new() { ID = 1, MatchID = 1001, Map = "caldavar" };
        MatchStatistics m2 = new() { ID = 2, MatchID = 1002, Map = "caldavar" };
        db.MatchStatistics.AddRange(m1, m2);

        PlayerStatistics p1 = new()
        {
            ID = 1, MatchID = 1001, AccountID = 10, AccountName = "P1", HeroIdentifier = "Hero_Pyromancer", Win = 1
        };
        p1.AbilityEvents.Add(new MatchAbilityEvent { AbilityIdentifier = "A1", SlotIndex = 0, Level = 1, GameTimeSeconds = 10, HeroIdentifier = "Hero_Pyromancer" });
        p1.AbilityEvents.Add(new MatchAbilityEvent { AbilityIdentifier = "A2", SlotIndex = 1, Level = 1, GameTimeSeconds = 30, HeroIdentifier = "Hero_Pyromancer" });

        PlayerStatistics p2 = new()
        {
            ID = 2, MatchID = 1002, AccountID = 11, AccountName = "P2", HeroIdentifier = "Hero_Pyromancer", Win = 0
        };
        p2.AbilityEvents.Add(new MatchAbilityEvent { AbilityIdentifier = "A1", SlotIndex = 0, Level = 1, GameTimeSeconds = 12, HeroIdentifier = "Hero_Pyromancer" });
        p2.AbilityEvents.Add(new MatchAbilityEvent { AbilityIdentifier = "A2", SlotIndex = 1, Level = 1, GameTimeSeconds = 35, HeroIdentifier = "Hero_Pyromancer" });

        db.PlayerStatistics.AddRange(p1, p2);
        await db.SaveChangesAsync();

        IReadOnlyList<HeroSkillBuildWinRateDTO> builds = await repo.GetHeroSkillBuildWinRatesAsync("Hero_Pyromancer", level: 1);

        builds.Should().HaveCount(2);
        builds.Should().AllSatisfy(b =>
        {
            b.PickCount.Should().Be(2);
            b.WinsCount.Should().Be(1);
            b.WinRate.Should().Be(0.5);
        });
    }

    [Fact]
    public async Task MatchHistoryRepository_GetPlayerRivalriesAndNemesisAsync_ComputesAccurately()
    {
        MerrickContext db = TestDbContextFactory.CreateInMemoryContext(Guid.NewGuid().ToString("N"));
        MatchHistoryRepository repo = new(db, NullLogger<MatchHistoryRepository>.Instance);

        MatchStatistics m = new() { ID = 1, MatchID = 2001, Map = "caldavar" };
        db.MatchStatistics.Add(m);

        PlayerStatistics p1 = new() { ID = 1, MatchID = 2001, AccountID = 101, AccountName = "PyroMaster", HeroIdentifier = "Hero_Pyromancer", Win = 1 };
        PlayerStatistics p2 = new() { ID = 2, MatchID = 2001, AccountID = 102, AccountName = "DevoKing", HeroIdentifier = "Hero_Devourer", Win = 0 };
        db.PlayerStatistics.AddRange(p1, p2);

        // 101 killed 102 twice
        db.MatchFrags.Add(new MatchFrag { MatchID = 2001, KillerAccountID = 101, VictimAccountID = 102, GameTimeSeconds = 50 });
        db.MatchFrags.Add(new MatchFrag { MatchID = 2001, KillerAccountID = 101, VictimAccountID = 102, GameTimeSeconds = 150 });

        // 102 killed 101 three times
        db.MatchFrags.Add(new MatchFrag { MatchID = 2001, KillerAccountID = 102, VictimAccountID = 101, GameTimeSeconds = 70 });
        db.MatchFrags.Add(new MatchFrag { MatchID = 2001, KillerAccountID = 102, VictimAccountID = 101, GameTimeSeconds = 200 });
        db.MatchFrags.Add(new MatchFrag { MatchID = 2001, KillerAccountID = 102, VictimAccountID = 101, GameTimeSeconds = 300 });

        await db.SaveChangesAsync();

        IReadOnlyList<PlayerRivalryDTO> rivalries = await repo.GetPlayerRivalriesAndNemesisAsync(101);

        rivalries.Should().HaveCount(1);
        PlayerRivalryDTO rivalry = rivalries[0];
        rivalry.OpponentAccountID.Should().Be(102);
        rivalry.KillsOnOpponent.Should().Be(2);
        rivalry.DeathsToOpponent.Should().Be(3);
    }

    [Fact]
    public async Task RecipeTree_RecursiveResolutionAndCycleProtection_OperatesSafely()
    {
        MerrickContext db = TestDbContextFactory.CreateInMemoryContext(Guid.NewGuid().ToString("N"));
        HeroGuideRepository repo = new(db, NullLogger<HeroGuideRepository>.Instance);

        HeroItem parent = new() { ID = 1, Code = "parent_item", Name = "Parent Item", Description = "Parent desc", Flavor = "Parent flavor", IconPath = "parent.tga", Cost = 3000 };
        HeroItem child1 = new() { ID = 2, Code = "child_1", Name = "Child 1", Description = "Child1 desc", Flavor = "Child1 flavor", IconPath = "child1.tga", Cost = 1000 };
        HeroItem child2 = new() { ID = 3, Code = "child_2", Name = "Child 2", Description = "Child2 desc", Flavor = "Child2 flavor", IconPath = "child2.tga", Cost = 1500 };
        HeroItem subChild = new() { ID = 4, Code = "sub_child", Name = "Sub Child", Description = "SubChild desc", Flavor = "SubChild flavor", IconPath = "subchild.tga", Cost = 500 };

        db.HeroItems.AddRange(parent, child1, child2, subChild);

        parent.Components.Add(new HeroItemComponent { ParentItemID = 1, ComponentItemCode = "child_1", Quantity = 1 });
        parent.Components.Add(new HeroItemComponent { ParentItemID = 1, ComponentItemCode = "child_2", Quantity = 1 });
        child1.Components.Add(new HeroItemComponent { ParentItemID = 2, ComponentItemCode = "sub_child", Quantity = 2 });

        await db.SaveChangesAsync();

        ItemRecipeNodeDTO? tree = await repo.ResolveRecipeTreeAsync("parent_item");

        tree.Should().NotBeNull();
        tree!.ItemCode.Should().Be("parent_item");
        tree.Cost.Should().Be(3000);
        tree.Components.Should().HaveCount(2);

        ItemRecipeNodeDTO child1Node = tree.Components.First(c => c.ComponentItemCode == "child_1").SubRecipe!;
        child1Node.Should().NotBeNull();
        child1Node.Components.Should().HaveCount(1);
        child1Node.Components[0].ComponentItemCode.Should().Be("sub_child");
        child1Node.Components[0].Quantity.Should().Be(2);

        // Test cycle safety (A -> B -> A)
        subChild.Components.Add(new HeroItemComponent { ParentItemID = 4, ComponentItemCode = "parent_item", Quantity = 1 });
        await db.SaveChangesAsync();

        ItemRecipeNodeDTO? cycleProtectedTree = await repo.ResolveRecipeTreeAsync("parent_item");
        cycleProtectedTree.Should().NotBeNull();
        cycleProtectedTree!.ItemCode.Should().Be("parent_item");
    }

    [Fact]
    public void DuckDbLakehouse_AnalyticalPushdownQueries_GenerateValidSql()
    {
        SupabaseDuckDbLakehouseService service = new(NullLogger<SupabaseDuckDbLakehouseService>.Instance);

        string abilityImpactSql = service.GenerateAbilityDamageImpactQuery(88001, "Ability_Pyromancer1");
        abilityImpactSql.Should().Contain("stat.\"MatchAbilityEvents\"");
        abilityImpactSql.Should().Contain("stat.\"PlayerStatistics\"");
        abilityImpactSql.Should().Contain("duckdb.match_combat_events");
        abilityImpactSql.Should().Contain("Ability_Pyromancer1");

        string fragClusterSql = service.GenerateFragClusteringQuery(88001);
        fragClusterSql.Should().Contain("stat.\"MatchFrags\"");
        fragClusterSql.Should().Contain("duckdb.match_combat_events");

        string synergySql = service.GenerateItemAndSkillSynergyQuery(15, "Item_PortalKey", "Ability_Pyromancer1");
        synergySql.Should().Contain("stat.\"MatchItemEvents\"");
        synergySql.Should().Contain("stat.\"MatchAbilityEvents\"");
        synergySql.Should().Contain("Item_PortalKey");
        synergySql.Should().Contain("Ability_Pyromancer1");
    }
}
