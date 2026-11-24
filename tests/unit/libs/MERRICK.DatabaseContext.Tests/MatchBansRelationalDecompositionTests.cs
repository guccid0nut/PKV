// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using MERRICK.DatabaseContext.Entities.Statistics;
using MERRICK.DatabaseContext.Persistence;
using MERRICK.DatabaseContext.Repositories;
using MERRICK.DatabaseContext.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using NEXUS.Contracts.Data;
using Xunit;

namespace MERRICK.DatabaseContext.Tests;

public class MatchBansRelationalDecompositionTests
{
    private static MerrickContext CreateContext(string? dbName = null)
    {
        return TestDbContextFactory.CreateInMemoryContext(dbName ?? Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public async Task MatchBan_RelationalCRUD_PersistsAndNavigatesToMatchStatistics()
    {
        using MerrickContext db = CreateContext();
        MatchStatistics match = new()
        {
            MatchID = 5001,
            HostAccountName = "HostUser",
            Map = "caldavar",
            Bans =
            [
                new MatchBan { MatchID = 5001, BanOrder = 1, Team = 1, HeroIdentifier = "Hero_Pyromancer" },
                new MatchBan { MatchID = 5001, BanOrder = 2, Team = 2, HeroIdentifier = "Hero_Devourer" }
            ]
        };

        db.MatchStatistics.Add(match);
        await db.SaveChangesAsync();

        List<MatchBan> savedBans = await db.MatchBans.Where(b => b.MatchID == 5001).OrderBy(b => b.BanOrder).ToListAsync();
        savedBans.Should().HaveCount(2);
        savedBans[0].HeroIdentifier.Should().Be("Hero_Pyromancer");
        savedBans[0].Team.Should().Be(1);
        savedBans[1].HeroIdentifier.Should().Be("Hero_Devourer");
        savedBans[1].Team.Should().Be(2);

        MatchBan loadedBan = await db.MatchBans.Include(b => b.Match).FirstAsync(b => b.HeroIdentifier == "Hero_Pyromancer");
        loadedBan.Match.Should().NotBeNull();
        loadedBan.Match.MatchID.Should().Be(5001);
    }

    [Fact]
    public async Task MatchBan_CascadeDelete_RemovingMatchDeletesBans()
    {
        using MerrickContext db = CreateContext();
        MatchStatistics match = new()
        {
            MatchID = 5002,
            HostAccountName = "HostUser",
            Map = "caldavar",
            Bans =
            [
                new MatchBan { MatchID = 5002, BanOrder = 1, Team = 1, HeroIdentifier = "Hero_Valkyrie" }
            ]
        };

        db.MatchStatistics.Add(match);
        await db.SaveChangesAsync();

        (await db.MatchBans.CountAsync(b => b.MatchID == 5002)).Should().Be(1);

        db.MatchStatistics.Remove(match);
        await db.SaveChangesAsync();

        (await db.MatchBans.CountAsync(b => b.MatchID == 5002)).Should().Be(0);
    }

    [Fact]
    public void MatchStatistics_BannedHeroesSync_ParsesStringToBans()
    {
        MatchStatistics match = new()
        {
            MatchID = 5003,
            BannedHeroes = "Hero_Pyromancer,Hero_Devourer,Hero_Hammerstorm"
        };

        match.Bans.Should().HaveCount(3);
        match.Bans[0].HeroIdentifier.Should().Be("Hero_Pyromancer");
        match.Bans[0].BanOrder.Should().Be(1);
        match.Bans[0].Team.Should().Be(1);
        match.Bans[0].MatchID.Should().Be(5003);

        match.Bans[1].HeroIdentifier.Should().Be("Hero_Devourer");
        match.Bans[1].BanOrder.Should().Be(2);
        match.Bans[1].Team.Should().Be(2);
        match.Bans[1].MatchID.Should().Be(5003);

        match.Bans[2].HeroIdentifier.Should().Be("Hero_Hammerstorm");
        match.Bans[2].BanOrder.Should().Be(3);
        match.Bans[2].Team.Should().Be(1);
        match.Bans[2].MatchID.Should().Be(5003);
    }

    [Fact]
    public void MatchStatistics_BannedHeroesSync_GeneratesStringFromBans()
    {
        MatchStatistics match = new()
        {
            MatchID = 5004,
            Bans =
            [
                new MatchBan { MatchID = 5004, BanOrder = 2, Team = 2, HeroIdentifier = "Hero_Devourer" },
                new MatchBan { MatchID = 5004, BanOrder = 1, Team = 1, HeroIdentifier = "Hero_Pyromancer" }
            ]
        };

        match.BannedHeroes.Should().Be("Hero_Pyromancer,Hero_Devourer");
    }

    [Fact]
    public void MatchStatistics_BannedHeroesSync_ClearingStringClearsBans()
    {
        MatchStatistics match = new()
        {
            MatchID = 5005,
            BannedHeroes = "Hero_Pyromancer,Hero_Devourer"
        };
        match.Bans.Should().HaveCount(2);

        match.BannedHeroes = "";
        match.Bans.Should().BeEmpty();
    }

    [Fact]
    public void MatchStatistics_MatchIdPropagation_UpdatesBansWhenMatchIdSetLater()
    {
        MatchStatistics match = new()
        {
            BannedHeroes = "Hero_Accursed,Hero_FlintBeastwood",
            MatchID = 9999
        };

        match.Bans.Should().HaveCount(2);
        match.Bans[0].MatchID.Should().Be(9999);
        match.Bans[1].MatchID.Should().Be(9999);
    }

    [Fact]
    public async Task GetHeroBanRatesAsync_AggregatesBanCountsAndCalculatesBanRates()
    {
        using MerrickContext db = CreateContext();
        MatchHistoryRepository repo = new(db);

        // 4 Matches total:
        // Match 1: Pyro, Devourer
        // Match 2: Pyro, Hammerstorm
        // Match 3: Pyro, Devourer
        // Match 4: Valkyrie
        MatchStatistics m1 = new() { MatchID = 101, HostAccountName = "H1", BannedHeroes = "Hero_Pyromancer,Hero_Devourer" };
        MatchStatistics m2 = new() { MatchID = 102, HostAccountName = "H2", BannedHeroes = "Hero_Pyromancer,Hero_Hammerstorm" };
        MatchStatistics m3 = new() { MatchID = 103, HostAccountName = "H3", BannedHeroes = "Hero_Pyromancer,Hero_Devourer" };
        MatchStatistics m4 = new() { MatchID = 104, HostAccountName = "H4", BannedHeroes = "Hero_Valkyrie" };

        db.MatchStatistics.AddRange(m1, m2, m3, m4);
        await db.SaveChangesAsync();

        IReadOnlyList<HeroBanAggregateDTO> banRates = await repo.GetHeroBanRatesAsync(limit: 10);

        banRates.Should().HaveCount(4);

        // Top 1: Hero_Pyromancer (3 bans out of 4 matches -> 0.75)
        banRates[0].HeroIdentifier.Should().Be("Hero_Pyromancer");
        banRates[0].BanCount.Should().Be(3);
        banRates[0].BanRate.Should().Be(0.75);

        // Top 2: Hero_Devourer (2 bans out of 4 matches -> 0.5)
        banRates[1].HeroIdentifier.Should().Be("Hero_Devourer");
        banRates[1].BanCount.Should().Be(2);
        banRates[1].BanRate.Should().Be(0.5);

        // Hammerstorm & Valkyrie have 1 ban each (0.25)
        HeroBanAggregateDTO hammer = banRates.First(b => b.HeroIdentifier == "Hero_Hammerstorm");
        hammer.BanCount.Should().Be(1);
        hammer.BanRate.Should().Be(0.25);

        HeroBanAggregateDTO valk = banRates.First(b => b.HeroIdentifier == "Hero_Valkyrie");
        valk.BanCount.Should().Be(1);
        valk.BanRate.Should().Be(0.25);
    }

    [Fact]
    public async Task GetHeroBanRatesAsync_RespectsLimitParameter()
    {
        using MerrickContext db = CreateContext();
        MatchHistoryRepository repo = new(db);

        MatchStatistics m1 = new() { MatchID = 201, HostAccountName = "H1", BannedHeroes = "Hero_Pyromancer,Hero_Devourer,Hero_Hammerstorm,Hero_Valkyrie" };
        db.MatchStatistics.Add(m1);
        await db.SaveChangesAsync();

        IReadOnlyList<HeroBanAggregateDTO> banRates = await repo.GetHeroBanRatesAsync(limit: 2);
        banRates.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetHeroBanRatesAsync_ReturnsEmptyWhenNoMatches()
    {
        using MerrickContext db = CreateContext();
        MatchHistoryRepository repo = new(db);

        IReadOnlyList<HeroBanAggregateDTO> banRates = await repo.GetHeroBanRatesAsync();
        banRates.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveMatchAsync_ReconcilesUpdatedBansOnExistingMatch()
    {
        using MerrickContext db = CreateContext();
        MatchHistoryRepository repo = new(db);

        MatchStatistics initial = new()
        {
            MatchID = 301,
            HostAccountName = "Host1",
            BannedHeroes = "Hero_Pyromancer,Hero_Devourer"
        };
        await repo.SaveMatchAsync(initial);

        MatchStatistics? loaded = await repo.GetMatchByIdAsync(301);
        loaded.Should().NotBeNull();
        loaded!.Bans.Should().HaveCount(2);

        // Update bans
        loaded.BannedHeroes = "Hero_Valkyrie,Hero_Hammerstorm,Hero_Chronos";
        await repo.SaveMatchAsync(loaded);

        MatchStatistics? updated = await repo.GetMatchByIdAsync(301);
        updated.Should().NotBeNull();
        updated!.Bans.Should().HaveCount(3);
        updated.BannedHeroes.Should().Be("Hero_Valkyrie,Hero_Hammerstorm,Hero_Chronos");
    }
}
