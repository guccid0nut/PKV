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
using Microsoft.EntityFrameworkCore;
using NEXUS.Contracts.Data;
using Xunit;

namespace MERRICK.DatabaseContext.Tests;

public class MatchInventoryAndItemTimelineTests
{
    private static async Task<(PlayerStatistics Stats, MatchStatistics Match, MerrickContext Context)> CreateMatchAndPlayerStatsAsync()
    {
        MerrickContext context = TestDbContextFactory.CreateInMemoryContext(Guid.NewGuid().ToString("N"));

        User user = new()
        {
            ID = 1,
            EmailAddress = "item_test@kongor.net",
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
            Name = "ItemPlayer"
        };

        MatchStatistics match = new()
        {
            ID = 1,
            MatchID = 5001,
            Map = "caldavar",
            HostAccountName = "Server1"
        };

        PlayerStatistics stats = new()
        {
            ID = 10,
            MatchID = 5001,
            AccountID = 101,
            AccountName = "ItemPlayer",
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
    public async Task MatchPlayerInventory_RelationalCRUD_PersistsAndNavigates()
    {
        var (stats, _, db) = await CreateMatchAndPlayerStatsAsync();

        stats.Inventories.Add(new MatchPlayerInventory
        {
            PlayerStatisticID = stats.ID,
            MatchID = stats.MatchID,
            AccountID = stats.AccountID,
            SlotIndex = 0,
            ItemIdentifier = "Item_PortalKey",
            Charges = 0
        });
        stats.Inventories.Add(new MatchPlayerInventory
        {
            PlayerStatisticID = stats.ID,
            MatchID = stats.MatchID,
            AccountID = stats.AccountID,
            SlotIndex = 1,
            ItemIdentifier = "Item_PostHaste",
            Charges = 0
        });

        await db.SaveChangesAsync();

        List<MatchPlayerInventory> loaded = await db.MatchPlayerInventories
            .Include(i => i.PlayerStatistic)
            .Where(i => i.PlayerStatisticID == stats.ID)
            .OrderBy(i => i.SlotIndex)
            .ToListAsync();

        loaded.Should().HaveCount(2);
        loaded[0].ItemIdentifier.Should().Be("Item_PortalKey");
        loaded[0].SlotIndex.Should().Be(0);
        loaded[0].PlayerStatistic.HeroIdentifier.Should().Be("Hero_Pyromancer");
        loaded[1].ItemIdentifier.Should().Be("Item_PostHaste");
        loaded[1].SlotIndex.Should().Be(1);
    }

    [Fact]
    public async Task MatchItemEvent_RelationalCRUD_PersistsAndNavigates()
    {
        var (stats, _, db) = await CreateMatchAndPlayerStatsAsync();

        stats.ItemEvents.Add(new MatchItemEvent
        {
            PlayerStatisticID = stats.ID,
            MatchID = stats.MatchID,
            AccountID = stats.AccountID,
            GameTimeSeconds = 60,
            EventType = 1, // Purchased
            ItemIdentifier = "Item_MinorTotem"
        });
        stats.ItemEvents.Add(new MatchItemEvent
        {
            PlayerStatisticID = stats.ID,
            MatchID = stats.MatchID,
            AccountID = stats.AccountID,
            GameTimeSeconds = 650,
            EventType = 1, // Purchased
            ItemIdentifier = "Item_PortalKey"
        });
        stats.ItemEvents.Add(new MatchItemEvent
        {
            PlayerStatisticID = stats.ID,
            MatchID = stats.MatchID,
            AccountID = stats.AccountID,
            GameTimeSeconds = 720,
            EventType = 2, // Sold
            ItemIdentifier = "Item_MinorTotem"
        });

        await db.SaveChangesAsync();

        List<MatchItemEvent> events = await db.MatchItemEvents
            .Include(e => e.PlayerStatistic)
            .Where(e => e.PlayerStatisticID == stats.ID)
            .OrderBy(e => e.GameTimeSeconds)
            .ToListAsync();

        events.Should().HaveCount(3);
        events[0].ItemIdentifier.Should().Be("Item_MinorTotem");
        events[0].Action.Should().Be("Purchased");
        events[1].ItemIdentifier.Should().Be("Item_PortalKey");
        events[2].ItemIdentifier.Should().Be("Item_MinorTotem");
        events[2].Action.Should().Be("Sold");
    }

    [Fact]
    public void PlayerStatistics_Inventory_BidirectionalCompatibility()
    {
        PlayerStatistics ps = new()
        {
            MatchID = 5002,
            AccountID = 202
        };

        // Setting legacy Inventory list should synchronize to Inventories navigation collection
        ps.Inventory = ["Item_PortalKey", "Item_PostHaste", "Item_Spellshards"];

        ps.Inventories.Should().HaveCount(3);
        ps.Inventories[0].SlotIndex.Should().Be(0);
        ps.Inventories[0].ItemIdentifier.Should().Be("Item_PortalKey");
        ps.Inventories[0].MatchID.Should().Be(5002);
        ps.Inventories[0].AccountID.Should().Be(202);

        // Getting legacy Inventory should project from Inventories navigation collection
        List<string> readBack = ps.Inventory;
        readBack.Should().Equal(["Item_PortalKey", "Item_PostHaste", "Item_Spellshards"]);
    }

    [Fact]
    public void PlayerStatistics_ItemHistory_BidirectionalCompatibility()
    {
        PlayerStatistics ps = new()
        {
            MatchID = 5003,
            AccountID = 303
        };

        // Setting legacy ItemHistory list should synchronize to ItemEvents navigation collection
        ps.ItemHistory =
        [
            new ItemEvent { ItemName = "Item_MinorTotem", GameTimeSeconds = 0, EventType = 1 },
            new ItemEvent { ItemName = "Item_PortalKey", GameTimeSeconds = 600, EventType = 1 }
        ];

        ps.ItemEvents.Should().HaveCount(2);
        ps.ItemEvents[0].ItemIdentifier.Should().Be("Item_MinorTotem");
        ps.ItemEvents[0].GameTimeSeconds.Should().Be(0);
        ps.ItemEvents[0].EventType.Should().Be(1);
        ps.ItemEvents[0].MatchID.Should().Be(5003);
        ps.ItemEvents[0].AccountID.Should().Be(303);

        ps.ItemEvents[1].ItemIdentifier.Should().Be("Item_PortalKey");
        ps.ItemEvents[1].GameTimeSeconds.Should().Be(600);

        // Getting legacy ItemHistory should project from ItemEvents
        List<ItemEvent>? readBack = ps.ItemHistory;
        readBack.Should().NotBeNull();
        readBack!.Should().HaveCount(2);
        readBack[0].ItemName.Should().Be("Item_MinorTotem");
        readBack[1].ItemName.Should().Be("Item_PortalKey");
    }

    [Fact]
    public async Task MatchHistoryRepository_GetTopItemBuildsByHeroAsync_AggregatesPopularityAndWins()
    {
        MerrickContext db = TestDbContextFactory.CreateInMemoryContext(Guid.NewGuid().ToString("N"));

        User user = new()
        {
            ID = 1,
            EmailAddress = "agg_test@kongor.net",
            PBKDF2PasswordHash = "hash",
            SRPPasswordHash = "srp_h",
            SRPPasswordSalt = "srp_s",
            Role = new Role { ID = 1, Name = "User" }
        };
        Account account = new() { ID = 101, UserID = 1, User = user, Name = "P1" };
        db.Users.Add(user);
        db.Accounts.Add(account);

        // Match 1: Pyro won with PortalKey & PostHaste
        PlayerStatistics ps1 = new()
        {
            ID = 1,
            MatchID = 1,
            AccountID = 101,
            AccountName = "P1",
            HeroIdentifier = "Hero_Pyromancer",
            Win = 1,
            Inventories =
            [
                new MatchPlayerInventory { ID = 1, PlayerStatisticID = 1, MatchID = 1, AccountID = 101, SlotIndex = 0, ItemIdentifier = "Item_PortalKey" },
                new MatchPlayerInventory { ID = 2, PlayerStatisticID = 1, MatchID = 1, AccountID = 101, SlotIndex = 1, ItemIdentifier = "Item_PostHaste" }
            ]
        };

        // Match 2: Pyro lost with PortalKey & GrimoireOfPower
        PlayerStatistics ps2 = new()
        {
            ID = 2,
            MatchID = 2,
            AccountID = 101,
            AccountName = "P1",
            HeroIdentifier = "Hero_Pyromancer",
            Win = 0,
            Inventories =
            [
                new MatchPlayerInventory { ID = 3, PlayerStatisticID = 2, MatchID = 2, AccountID = 101, SlotIndex = 0, ItemIdentifier = "Item_PortalKey" },
                new MatchPlayerInventory { ID = 4, PlayerStatisticID = 2, MatchID = 2, AccountID = 101, SlotIndex = 1, ItemIdentifier = "Item_GrimoireOfPower" }
            ]
        };

        db.PlayerStatistics.AddRange(ps1, ps2);
        db.MatchPlayerInventories.AddRange(ps1.Inventories);
        db.MatchPlayerInventories.AddRange(ps2.Inventories);
        await db.SaveChangesAsync();

        MatchHistoryRepository repo = new(db);
        IReadOnlyList<HeroItemPopularityDTO> popularItems = await repo.GetTopItemBuildsByHeroAsync("Hero_Pyromancer");

        popularItems.Should().HaveCount(3);
        popularItems[0].ItemIdentifier.Should().Be("Item_PortalKey");
        popularItems[0].MatchesCount.Should().Be(2);
        popularItems[0].WinsCount.Should().Be(1);
        popularItems[0].WinRate.Should().BeApproximately(0.5, 0.01);

        HeroItemPopularityDTO postHaste = popularItems.First(i => i.ItemIdentifier == "Item_PostHaste");
        postHaste.MatchesCount.Should().Be(1);
        postHaste.WinsCount.Should().Be(1);
        postHaste.WinRate.Should().Be(1.0);
    }

    [Fact]
    public async Task MatchHistoryRepository_GetAverageItemTimingsAsync_AggregatesTimings()
    {
        MerrickContext db = TestDbContextFactory.CreateInMemoryContext(Guid.NewGuid().ToString("N"));

        User user = new()
        {
            ID = 1,
            EmailAddress = "timing_test@kongor.net",
            PBKDF2PasswordHash = "hash",
            SRPPasswordHash = "srp_h",
            SRPPasswordSalt = "srp_s",
            Role = new Role { ID = 1, Name = "User" }
        };
        Account account = new() { ID = 101, UserID = 1, User = user, Name = "P1" };
        db.Users.Add(user);
        db.Accounts.Add(account);

        PlayerStatistics ps1 = new()
        {
            ID = 1,
            MatchID = 1,
            AccountID = 101,
            AccountName = "P1",
            HeroIdentifier = "Hero_Pyromancer",
            ItemEvents =
            [
                new MatchItemEvent { ID = 1, PlayerStatisticID = 1, MatchID = 1, AccountID = 101, GameTimeSeconds = 540, EventType = 1, ItemIdentifier = "Item_PortalKey" }
            ]
        };

        PlayerStatistics ps2 = new()
        {
            ID = 2,
            MatchID = 2,
            AccountID = 101,
            AccountName = "P1",
            HeroIdentifier = "Hero_Pyromancer",
            ItemEvents =
            [
                new MatchItemEvent { ID = 2, PlayerStatisticID = 2, MatchID = 2, AccountID = 101, GameTimeSeconds = 660, EventType = 1, ItemIdentifier = "Item_PortalKey" }
            ]
        };

        db.PlayerStatistics.AddRange(ps1, ps2);
        db.MatchItemEvents.AddRange(ps1.ItemEvents);
        db.MatchItemEvents.AddRange(ps2.ItemEvents);
        await db.SaveChangesAsync();

        MatchHistoryRepository repo = new(db);
        IReadOnlyList<HeroItemTimingDTO> timings = await repo.GetAverageItemTimingsAsync("Hero_Pyromancer");

        timings.Should().HaveCount(1);
        timings[0].ItemIdentifier.Should().Be("Item_PortalKey");
        timings[0].PurchasesCount.Should().Be(2);
        timings[0].AverageGameTimeSeconds.Should().Be(600.0);
        timings[0].FastestGameTimeSeconds.Should().Be(540);
    }

    [Fact]
    public async Task CascadeDelete_RemovingPlayerStatistics_CascadesToInventoriesAndItemEvents()
    {
        var (stats, _, db) = await CreateMatchAndPlayerStatsAsync();

        stats.Inventories.Add(new MatchPlayerInventory
        {
            PlayerStatisticID = stats.ID,
            MatchID = stats.MatchID,
            AccountID = stats.AccountID,
            SlotIndex = 0,
            ItemIdentifier = "Item_PortalKey"
        });

        stats.ItemEvents.Add(new MatchItemEvent
        {
            PlayerStatisticID = stats.ID,
            MatchID = stats.MatchID,
            AccountID = stats.AccountID,
            GameTimeSeconds = 500,
            EventType = 1,
            ItemIdentifier = "Item_PortalKey"
        });

        await db.SaveChangesAsync();

        (await db.MatchPlayerInventories.CountAsync(i => i.PlayerStatisticID == stats.ID)).Should().Be(1);
        (await db.MatchItemEvents.CountAsync(e => e.PlayerStatisticID == stats.ID)).Should().Be(1);

        db.PlayerStatistics.Remove(stats);
        await db.SaveChangesAsync();

        (await db.MatchPlayerInventories.CountAsync(i => i.PlayerStatisticID == stats.ID)).Should().Be(0);
        (await db.MatchItemEvents.CountAsync(e => e.PlayerStatisticID == stats.ID)).Should().Be(0);
    }
}
