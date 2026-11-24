// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Statistics;
using MERRICK.DatabaseContext.Persistence;
using MERRICK.DatabaseContext.Repositories;
using MERRICK.DatabaseContext.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using NEXUS.Contracts.Data;
using Xunit;

namespace MERRICK.DatabaseContext.Tests;

public class MatchHistoryRepositoryExpandedTests
{
    [Fact]
    public async Task MatchHistoryRepositoryExpanded_GetHeroUsageAggregatesAsync_ReturnsCorrectPicksWinsLosses()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            db.PlayerStatistics.AddRange(
                new PlayerStatistics { ID = 1, MatchID = 101, AccountID = 1, HeroIdentifier = "Hero_Accursed", Win = 1, Loss = 0 },
                new PlayerStatistics { ID = 2, MatchID = 102, AccountID = 2, HeroIdentifier = "Hero_Accursed", Win = 1, Loss = 0 },
                new PlayerStatistics { ID = 3, MatchID = 103, AccountID = 3, HeroIdentifier = "Hero_Accursed", Win = 0, Loss = 1 },
                new PlayerStatistics { ID = 4, MatchID = 101, AccountID = 4, HeroIdentifier = "Hero_FlintBeastwood", Win = 0, Loss = 1 },
                new PlayerStatistics { ID = 5, MatchID = 102, AccountID = 5, HeroIdentifier = "Hero_FlintBeastwood", Win = 1, Loss = 0 }
            );
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            MatchHistoryRepository repo = new(db);
            IReadOnlyList<HeroUsageAggregateDTO> aggregates = await repo.GetHeroUsageAggregatesAsync();

            aggregates.Should().HaveCount(2);
            HeroUsageAggregateDTO? accursed = aggregates.FirstOrDefault(a => a.HeroName == "Hero_Accursed");
            accursed.Should().NotBeNull();
            accursed!.TotalUse.Should().Be(3);
            accursed.TotalWins.Should().Be(2);
            accursed.TotalLosses.Should().Be(1);

            HeroUsageAggregateDTO? flint = aggregates.FirstOrDefault(a => a.HeroName == "Hero_FlintBeastwood");
            flint.Should().NotBeNull();
            flint!.TotalUse.Should().Be(2);
            flint.TotalWins.Should().Be(1);
            flint.TotalLosses.Should().Be(1);
        }
    }

    [Fact]
    public async Task MatchHistoryRepositoryExpanded_GetPlayerStatisticsForAwardSummaryAsync_MatchesAllVariants()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            db.PlayerStatistics.AddRange(
                new PlayerStatistics { ID = 1, MatchID = 1, AccountID = 10, AccountName = "Player10" },
                new PlayerStatistics { ID = 2, MatchID = 2, AccountID = 10, AccountName = "Player10" },
                new PlayerStatistics { ID = 3, MatchID = 3, AccountID = 99, AccountName = "[TAG]Player10" },
                new PlayerStatistics { ID = 4, MatchID = 4, AccountID = 999, AccountName = "CompletelyDifferent" }
            );
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            MatchHistoryRepository repo = new(db);
            IReadOnlyList<PlayerStatistics> results = await repo.GetPlayerStatisticsForAwardSummaryAsync(10, "Player10", "Player10");
            results.Should().HaveCount(3);
            results.Should().NotContain(ps => ps.ID == 4);
        }
    }

    [Fact]
    public async Task GetPlayerStatisticsByMatchIdsAsync_And_GetMatchesByIdsAsync_ReturnMatchingEntities()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            db.MatchStatistics.AddRange(
                new MatchStatistics { ID = 1, MatchID = 1001, Map = "caldavar" },
                new MatchStatistics { ID = 2, MatchID = 1002, Map = "midwars" },
                new MatchStatistics { ID = 3, MatchID = 1003, Map = "riftwars" }
            );
            db.PlayerStatistics.AddRange(
                new PlayerStatistics { ID = 1, MatchID = 1001, AccountID = 1 },
                new PlayerStatistics { ID = 2, MatchID = 1001, AccountID = 2 },
                new PlayerStatistics { ID = 3, MatchID = 1002, AccountID = 1 }
            );
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            MatchHistoryRepository repo = new(db);

            IReadOnlyList<MatchStatistics> matches = await repo.GetMatchesByIdsAsync([1001, 1002]);
            matches.Should().HaveCount(2);
            matches.Should().Contain(m => m.MatchID == 1001);
            matches.Should().Contain(m => m.MatchID == 1002);

            IReadOnlyList<PlayerStatistics> players = await repo.GetPlayerStatisticsByMatchIdsAsync([1001]);
            players.Should().HaveCount(2);
        }
    }

    [Fact]
    public async Task MatchHistoryRepositoryExpanded_GetRecentMatchIdsByAccountAsync_ReturnsOrderedIdsWithinLimit()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            for (int i = 1; i <= 25; i++)
            {
                db.PlayerStatistics.Add(new PlayerStatistics { ID = i, MatchID = 1000 + i, AccountID = 55 });
            }
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            MatchHistoryRepository repo = new(db);
            IReadOnlyList<int> recentIds = await repo.GetRecentMatchIdsByAccountAsync(55, null, 10);
            recentIds.Should().HaveCount(10);
            recentIds[0].Should().Be(1025);
            recentIds[9].Should().Be(1016);
        }
    }

    [Fact]
    public async Task MatchHistoryRepositoryExpanded_GetPlayerMatchHistoryOverviewAsync_FiltersAndFallbacks()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            db.MatchStatistics.AddRange(
                new MatchStatistics { ID = 1, MatchID = 201, Map = "caldavar", GameMode = "caldavar" },
                new MatchStatistics { ID = 2, MatchID = 202, Map = "midwars", GameMode = "midwars" }
            );
            db.PlayerStatistics.AddRange(
                new PlayerStatistics { ID = 1, MatchID = 201, AccountID = 77, AccountName = "P77", RankedMatch = 1, PublicMatch = 0, HeroKills = 5 },
                new PlayerStatistics { ID = 2, MatchID = 202, AccountID = 77, AccountName = "P77", RankedMatch = 0, PublicMatch = 1, HeroKills = 10 }
            );
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            MatchHistoryRepository repo = new(db);

            // Filter for Ranked
            IReadOnlyList<PlayerMatchRecordDTO> rankedOnly = await repo.GetPlayerMatchHistoryOverviewAsync(77, "P77", "ranked", "1", 100);
            rankedOnly.Should().HaveCount(1);
            rankedOnly[0].MatchID.Should().Be(201);
            rankedOnly[0].HeroKills.Should().Be(5);

            // Filter for Public / Casual
            IReadOnlyList<PlayerMatchRecordDTO> casualOnly = await repo.GetPlayerMatchHistoryOverviewAsync(77, "P77", "public", null, 100);
            casualOnly.Should().HaveCount(1);
            casualOnly[0].MatchID.Should().Be(202);
            casualOnly[0].HeroKills.Should().Be(10);

            // Filter for Season/Campaign (matches ranked Caldavar match 201)
            IReadOnlyList<PlayerMatchRecordDTO> fallbackResult = await repo.GetPlayerMatchHistoryOverviewAsync(77, "P77", "campaign", "0", 100);
            fallbackResult.Should().HaveCount(1);
        }
    }

    [Fact]
    public async Task MatchHistoryRepositoryExpanded_GetPlayerHeroStatisticsAsync_FiltersByAccountAndHero()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            db.PlayerStatistics.AddRange(
                new PlayerStatistics { ID = 1, MatchID = 301, AccountID = 88, HeroIdentifier = "Hero_Valkyrie", HeroKills = 12 },
                new PlayerStatistics { ID = 2, MatchID = 302, AccountID = 88, HeroIdentifier = "Hero_Valkyrie", HeroKills = 8 },
                new PlayerStatistics { ID = 3, MatchID = 303, AccountID = 88, HeroIdentifier = "Hero_Devourer", HeroKills = 15 },
                new PlayerStatistics { ID = 4, MatchID = 304, AccountID = 99, HeroIdentifier = "Hero_Valkyrie", HeroKills = 2 }
            );
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            MatchHistoryRepository repo = new(db);
            IReadOnlyList<PlayerStatistics> valkStats = await repo.GetPlayerHeroStatisticsAsync(88, "Hero_Valkyrie");
            valkStats.Should().HaveCount(2);
            valkStats.All(ps => ps.AccountID == 88 && ps.HeroIdentifier == "Hero_Valkyrie").Should().BeTrue();

            IReadOnlyList<PlayerStatistics> allStats = await repo.GetAllPlayerStatisticsForAccountAsync(88);
            allStats.Should().HaveCount(3);
        }
    }

    [Fact]
    public async Task MatchHistoryRepositoryExpanded_UpdateMatchReplayUrlAsync_UpdatesUrlAndFileSize()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            db.MatchStatistics.Add(new MatchStatistics { ID = 1, MatchID = 5000, Map = "caldavar" });
            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            MatchHistoryRepository repo = new(db);
            bool updated = await repo.UpdateMatchReplayUrlAsync(5000, "https://cdn.kongor.net/replays/M5000.honreplay", 4096000);
            updated.Should().BeTrue();

            MatchStatistics? refreshed = await db.MatchStatistics.FirstOrDefaultAsync(m => m.MatchID == 5000);
            refreshed.Should().NotBeNull();
            refreshed!.ReplayUrl.Should().Be("https://cdn.kongor.net/replays/M5000.honreplay");
            refreshed.FileSize.Should().Be(4096000);
        }
    }

    [Fact]
    public async Task MatchHistoryRepositoryExpanded_GetPlayerMatchHistoryOverviewAsync_FiltersCorrectlyPerWebsiteFilterModes()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            db.MatchStatistics.AddRange(
                new MatchStatistics { ID = 1, MatchID = 1001, Map = "caldavar" },
                new MatchStatistics { ID = 2, MatchID = 1002, Map = "midwars" },
                new MatchStatistics { ID = 3, MatchID = 1003, Map = "devowars" },
                new MatchStatistics { ID = 4, MatchID = 1004, Map = "caldavar" }
            );

            db.PlayerStatistics.AddRange(
                new PlayerStatistics { ID = 1, MatchID = 1001, AccountID = 77, AccountName = "TestHero", RankedMatch = 1, PublicMatch = 0, Win = 1 },
                new PlayerStatistics { ID = 2, MatchID = 1002, AccountID = 77, AccountName = "TestHero", RankedMatch = 1, PublicMatch = 0, Win = 1 },
                new PlayerStatistics { ID = 3, MatchID = 1003, AccountID = 77, AccountName = "TestHero", RankedMatch = 0, PublicMatch = 1, Win = 0 },
                new PlayerStatistics { ID = 4, MatchID = 1004, AccountID = 77, AccountName = "TestHero", RankedMatch = 0, PublicMatch = 1, Win = 1 }
            );

            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            MatchHistoryRepository repo = new(db);

            // 1. caldavar_ranked -> only match 1001
            var focRanked = await repo.GetPlayerMatchHistoryOverviewAsync(77, "TestHero", "caldavar_ranked", "1");
            focRanked.Should().HaveCount(1);
            focRanked[0].MatchID.Should().Be(1001);

            // 2. midwars_ranked -> only match 1002
            var mwRanked = await repo.GetPlayerMatchHistoryOverviewAsync(77, "TestHero", "midwars_ranked", "1");
            mwRanked.Should().HaveCount(1);
            mwRanked[0].MatchID.Should().Be(1002);

            // 3. devowars -> only match 1003
            var devo = await repo.GetPlayerMatchHistoryOverviewAsync(77, "TestHero", "devowars", "0");
            devo.Should().HaveCount(1);
            devo[0].MatchID.Should().Be(1003);

            // 4. all_public -> matches 1003 and 1004
            var pub = await repo.GetPlayerMatchHistoryOverviewAsync(77, "TestHero", "all_public", "0");
            pub.Should().HaveCount(2);
            pub.Select(m => m.MatchID).Should().Contain([1003, 1004]);

            // 5. riftwars_ranked (no matches) -> must return EMPTY, NOT fallback to all
            var riftRanked = await repo.GetPlayerMatchHistoryOverviewAsync(77, "TestHero", "riftwars_ranked", "1");
            riftRanked.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task MatchHistoryRepositoryExpanded_GetPlayerMatchHistoryOverviewAsync_ReturnsCasualAndCmMatchesForQuickmatchFilter()
    {
        string dbName = Guid.NewGuid().ToString("N");
        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            db.MatchStatistics.AddRange(
                new MatchStatistics { ID = 1, MatchID = 2001, Map = "caldavar", GameMode = "casual" },
                new MatchStatistics { ID = 2, MatchID = 2002, Map = "caldavar", GameMode = "cm" },
                new MatchStatistics { ID = 3, MatchID = 2003, Map = "caldavar", GameMode = "quickmatch" },
                new MatchStatistics { ID = 4, MatchID = 2004, Map = "caldavar", GameMode = "caldavar" },
                new MatchStatistics { ID = 5, MatchID = 2005, Map = "midwars", GameMode = "casual" }
            );

            db.PlayerStatistics.AddRange(
                new PlayerStatistics { ID = 1, MatchID = 2001, AccountID = 88, AccountName = "QuickTester", RankedMatch = 0, PublicMatch = 1, Win = 1 },
                new PlayerStatistics { ID = 2, MatchID = 2002, AccountID = 88, AccountName = "QuickTester", RankedMatch = 0, PublicMatch = 1, Win = 0 },
                new PlayerStatistics { ID = 3, MatchID = 2003, AccountID = 88, AccountName = "QuickTester", RankedMatch = 1, PublicMatch = 0, Win = 1 },
                new PlayerStatistics { ID = 4, MatchID = 2004, AccountID = 88, AccountName = "QuickTester", RankedMatch = 1, PublicMatch = 0, Win = 1 },
                new PlayerStatistics { ID = 5, MatchID = 2005, AccountID = 88, AccountName = "QuickTester", RankedMatch = 0, PublicMatch = 1, Win = 1 }
            );

            await db.SaveChangesAsync();
        }

        using (MerrickContext db = TestDbContextFactory.CreateInMemoryContext(dbName))
        {
            MatchHistoryRepository repo = new(db);

            IReadOnlyList<PlayerMatchRecordDTO> quickmatchRows =
                await repo.GetPlayerMatchHistoryOverviewAsync(88, "QuickTester", "caldavar_casual", "1");

            quickmatchRows.Should().HaveCount(3);
            quickmatchRows.Select(m => m.MatchID).Should().BeEquivalentTo([2001, 2002, 2003]);
        }
    }
}
