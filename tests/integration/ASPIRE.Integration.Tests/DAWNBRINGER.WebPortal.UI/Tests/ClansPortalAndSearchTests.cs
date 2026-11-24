// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Security.Claims;
using ASPIRE.Common.Enumerations.Statistics;
using DAWNBRINGER.WebPortal.UI.Controllers;
using DAWNBRINGER.WebPortal.UI.Models;
using FluentAssertions;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Statistics;
using MERRICK.DatabaseContext.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.Clients;
using NEXUS.Contracts.CQRS.PlayerStats;
using NEXUS.Contracts.Domain;
using NEXUS.Contracts.DTOs;
using global::ZORGATH.WebPortal.API.Features.PlayerStats;

namespace ASPIRE.Tests.DAWNBRINGER.WebPortal.UI.Tests;

[Collection("DawnbringerE2E")]
public sealed class ClansPortalAndSearchTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private static (User User, Account Account) CreateAccount(
        string name,
        Clan? clan,
        ClanTier tier,
        AccountType type = AccountType.Normal,
        double? matchmakingMmr = null,
        int mmPlayed = 0,
        int mmWon = 0,
        double? publicMmr = null,
        int pubPlayed = 0,
        int pubWon = 0,
        DateTimeOffset? lastActive = null,
        DateTimeOffset? joinedClan = null)
    {
        User user = new()
        {
            EmailAddress = $"{name.ToLowerInvariant()}@kongor.test",
            SRPPasswordSalt = "salt",
            SRPPasswordHash = "hash",
            PBKDF2PasswordHash = "pbkdf2_hash"
        };

        Account account = new()
        {
            Name = name,
            User = user,
            Type = type,
            Clan = clan,
            ClanTier = tier,
            TimestampCreated = FixedNow.AddMonths(-6),
            TimestampLastActive = lastActive ?? FixedNow.AddHours(-2),
            TimestampJoinedClan = joinedClan ?? (clan is not null ? FixedNow.AddMonths(-2) : null)
        };

        if (matchmakingMmr.HasValue || mmPlayed > 0)
        {
            account.Statistics.Add(new AccountStatistics
            {
                Account = account,
                Type = AccountStatisticsType.Matchmaking,
                SkillRating = matchmakingMmr ?? 0,
                MatchesPlayed = mmPlayed,
                MatchesWon = mmWon,
                MatchesLost = Math.Max(0, mmPlayed - mmWon)
            });
        }

        if (publicMmr.HasValue || pubPlayed > 0)
        {
            account.Statistics.Add(new AccountStatistics
            {
                Account = account,
                Type = AccountStatisticsType.Public,
                SkillRating = publicMmr ?? 0,
                MatchesPlayed = pubPlayed,
                MatchesWon = pubWon,
                MatchesLost = Math.Max(0, pubPlayed - pubWon)
            });
        }

        return (user, account);
    }

    [Fact]
    public async Task GetClanDirectoryHandler_ExcludesEmptyAndDisabledOnlyClans_AndRespectsCompetitiveQualification()
    {
        await using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();

        // 1. Qualified competitive clan (3 ranked members, avg MMR = 1800)
        Clan vanguard = new() { Name = "Kongor Vanguard", Tag = "K", TimestampCreated = FixedNow.AddDays(-90) };
        (_, Account v1) = CreateAccount("LeaderMilo", vanguard, ClanTier.Leader, matchmakingMmr: 1900, mmPlayed: 100, mmWon: 65, lastActive: FixedNow.AddHours(-1));
        (_, Account v2) = CreateAccount("OfficerKestrel", vanguard, ClanTier.Officer, matchmakingMmr: 1800, mmPlayed: 100, mmWon: 60, lastActive: FixedNow.AddDays(-3));
        (_, Account v3) = CreateAccount("MemberPebs", vanguard, ClanTier.Member, matchmakingMmr: 1700, mmPlayed: 50, mmWon: 25, lastActive: FixedNow.AddDays(-10));
        // Anonymized/disabled member still pointing at ClanID must be excluded!
        (_, Account vDeleted) = CreateAccount("del_999", vanguard, ClanTier.Member, type: AccountType.Disabled, matchmakingMmr: 2500, mmPlayed: 100, mmWon: 99);

        // 2. Provisional high-MMR duo (2 ranked members, avg MMR = 1975 -> MUST sort AFTER qualified clans in MMR sort!)
        Clan duo = new() { Name = "Two Man Army", Tag = "DUO", TimestampCreated = FixedNow.AddDays(-10) };
        (_, Account d1) = CreateAccount("SoloKing", duo, ClanTier.Leader, matchmakingMmr: 2000, mmPlayed: 40, mmWon: 32);
        (_, Account d2) = CreateAccount("DuoPal", duo, ClanTier.Officer, matchmakingMmr: 1950, mmPlayed: 40, mmWon: 28);

        // 3. Unranked leaderless clan (original leader disabled/anonymized)
        Clan leaderless = new() { Name = "Ghost Squad", Tag = "GHO", TimestampCreated = FixedNow.AddDays(-5) };
        (_, Account gLeaderDel) = CreateAccount("del_404", leaderless, ClanTier.Leader, type: AccountType.Disabled);
        (_, Account gMem) = CreateAccount("ActiveSurvivor", leaderless, ClanTier.Member, lastActive: FixedNow.AddDays(-45));

        // 4. Empty clan (0 members) and Disabled-only clan -> both must be hidden
        Clan emptyClan = new() { Name = "Empty Clan", Tag = "EMP", TimestampCreated = FixedNow.AddDays(-200) };
        Clan disabledOnly = new() { Name = "Deleted Only", Tag = "DEL", TimestampCreated = FixedNow.AddDays(-100) };
        (_, Account delOnlyAcc) = CreateAccount("del_500", disabledOnly, ClanTier.Leader, type: AccountType.Disabled);

        db.Clans.AddRange(vanguard, duo, leaderless, emptyClan, disabledOnly);
        db.Accounts.AddRange(v1, v2, v3, vDeleted, d1, d2, gLeaderDel, gMem, delOnlyAcc);
        await db.SaveChangesAsync();

        GetClanDirectoryHandler handler = new(db, NullLogger<GetClanDirectoryHandler>.Instance, new FixedTimeProvider(FixedNow));

        // Default sort: "mmr"
        ClanDirectoryResultDto byMmr = await handler.Handle(new GetClanDirectoryQuery(1, 24, null, "mmr"), CancellationToken.None);

        byMmr.TotalCount.Should().Be(3, "empty and disabled-only clans must be hidden");
        byMmr.TotalActiveMembers.Should().Be(6);
        byMmr.HighestQualifiedAvgMmr.Should().Be(1800.0);

        byMmr.Items.Select(c => c.Name).Should().ContainInOrder("Kongor Vanguard", "Two Man Army", "Ghost Squad");

        ClanSummaryDto vanguardDto = byMmr.Items[0];
        vanguardDto.MemberCount.Should().Be(3, "disabled/anonymized member must not be counted");
        vanguardDto.AverageMmr.Should().Be(1800.0);
        vanguardDto.IsCompetitiveQualified.Should().BeTrue();
        vanguardDto.CombinedMemberMatches.Should().Be(250);
        vanguardDto.CombinedWinRate.Should().Be(60.0);
        vanguardDto.OfficerCount.Should().Be(1);
        vanguardDto.LeaderName.Should().Be("LeaderMilo");
        vanguardDto.ActivityBucket.Should().Be("Active today");

        ClanSummaryDto duoDto = byMmr.Items[1];
        duoDto.IsCompetitiveQualified.Should().BeFalse("only 2 ranked members (< 3 minimum)");
        duoDto.AverageMmr.Should().Be(1975.0);

        ClanSummaryDto ghostDto = byMmr.Items[2];
        ghostDto.LeaderName.Should().BeNull("disabled leader must not appear");
        ghostDto.AverageMmr.Should().BeNull("no ranked members -> Unranked");
        ghostDto.ActivityBucket.Should().Be("Inactive 30d+");

        // Newest sort orders Ghost Squad first regardless of provisional status
        ClanDirectoryResultDto byNewest = await handler.Handle(new GetClanDirectoryQuery(1, 24, null, "newest"), CancellationToken.None);
        byNewest.Items[0].Name.Should().Be("Ghost Squad");

        // Search by bracketed tag [DUO] or leader name
        ClanDirectoryResultDto searchTag = await handler.Handle(new GetClanDirectoryQuery(1, 24, "[DUO]", "mmr"), CancellationToken.None);
        searchTag.Items.Should().ContainSingle(c => c.Tag == "DUO");
    }

    [Fact]
    public async Task GetClanDetailHandler_OrdersRosterByTierAndMmr_UsesCoarseActivityBuckets_AndRejectsEmptyClan()
    {
        await using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();

        Clan clan = new() { Name = "Legion Watch", Tag = "LGN", TimestampCreated = FixedNow.AddDays(-60) };
        (_, Account memLow) = CreateAccount("BetaMember", clan, ClanTier.Member, publicMmr: 1550, pubPlayed: 20, pubWon: 10, lastActive: FixedNow.AddDays(-40));
        (_, Account memHigh) = CreateAccount("AlphaMember", clan, ClanTier.Member, matchmakingMmr: 1750, mmPlayed: 80, mmWon: 48, lastActive: FixedNow.AddDays(-12));
        (_, Account officer) = CreateAccount("OfficerZ", clan, ClanTier.Officer, matchmakingMmr: 1810, mmPlayed: 100, mmWon: 55, lastActive: FixedNow.AddDays(-2));
        (_, Account leader) = CreateAccount("CommanderJ", clan, ClanTier.Leader, matchmakingMmr: 1860, mmPlayed: 120, mmWon: 72, lastActive: FixedNow.AddHours(-3));
        (_, Account hostBot) = CreateAccount("host_01", clan, ClanTier.Member, type: AccountType.ServerHost);

        Clan emptyClan = new() { Name = "Ghost Empty", Tag = "ZERO", TimestampCreated = FixedNow.AddDays(-30) };

        db.Clans.AddRange(clan, emptyClan);
        db.Accounts.AddRange(memLow, memHigh, officer, leader, hostBot);
        await db.SaveChangesAsync();

        GetClanDetailHandler handler = new(db, null, NullLogger<GetClanDetailHandler>.Instance, new FixedTimeProvider(FixedNow));

        ClanDetailDto? detail = await handler.Handle(new GetClanDetailQuery(clan.ID), CancellationToken.None);
        detail.Should().NotBeNull();
        detail!.Members.Select(m => m.Name).Should().ContainInOrder("CommanderJ", "OfficerZ", "AlphaMember", "BetaMember");
        detail.Members.Should().NotContain(m => m.Name == "host_01", "ServerHost accounts must be excluded");

        detail.Members[0].ActivityBucket.Should().Be("Active today");
        detail.Members[1].ActivityBucket.Should().Be("Active this week");
        detail.Members[2].ActivityBucket.Should().Be("Active this month");
        detail.Members[3].ActivityBucket.Should().Be("Inactive 30d+");
        detail.Members[3].SkillRating.Should().Be(1550.0, "public rating fallback must apply when matchmaking rating is absent");

        ClanDetailDto? missing = await handler.Handle(new GetClanDetailQuery(emptyClan.ID), CancellationToken.None);
        missing.Should().BeNull("empty clans must return null/404");
    }

    [Fact]
    public async Task GlobalSearch_SupportsBracketedOneCharTag_PrefixRanking_EmptyBracketGuard_AndSizeLimitedCache()
    {
        await using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();

        // Unique clan tags: exact 1-char tag [K] vs prefix tag [KA]
        Clan exactKongor = new() { Name = "Kongor Vanguard", Tag = "K", TimestampCreated = FixedNow.AddDays(-100) };
        (_, Account b1) = CreateAccount("BigOne", exactKongor, ClanTier.Leader, matchmakingMmr: 1800, mmPlayed: 20, mmWon: 12);
        (_, Account b2) = CreateAccount("BigTwo", exactKongor, ClanTier.Officer, matchmakingMmr: 1750, mmPlayed: 20, mmWon: 11);
        (_, Account b3) = CreateAccount("BigThree", exactKongor, ClanTier.Member, matchmakingMmr: 1700, mmPlayed: 20, mmWon: 10);

        Clan academyKongor = new() { Name = "Kongor Academy", Tag = "KA", TimestampCreated = FixedNow.AddDays(-20) };
        (_, Account s1) = CreateAccount("SmallOne", academyKongor, ClanTier.Leader, matchmakingMmr: 1600, mmPlayed: 10, mmWon: 5);

        db.Clans.AddRange(exactKongor, academyKongor);
        db.Accounts.AddRange(b1, b2, b3, s1);
        await db.SaveChangesAsync();

        IPlayerStatsApiClient statsClient = TestApiClientFactory.CreatePlayerStatsApiClient(db);
        IUserApiClient userClient = TestApiClientFactory.CreateUserApiClient(db);
        IGuideApiClient guideClient = TestApiClientFactory.CreateGuideApiClient(db);
        IAdminApiClient adminClient = TestApiClientFactory.CreateAdminApiClient(db);
        IConfiguration config = new ConfigurationBuilder().Build();

        // Explicitly exercise SizeLimit=1000 MemoryCache to verify Size=1 is always set!
        using MemoryCache sizeLimitedCache = new(new MemoryCacheOptions { SizeLimit = 1000 });
        HomeController controller = new(
            statsClient,
            userClient,
            guideClient,
            adminClient,
            config,
            logger: NullLogger<HomeController>.Instance,
            memoryCache: sizeLimitedCache)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        // 1. Bracketed 1-char tag [K] scopes strictly to clans and ranks exact tag [K] above prefix tag [KA]
        IActionResult bracketRes = await controller.SearchGlobal("[K]");
        JsonResult bracketJson = bracketRes.Should().BeOfType<JsonResult>().Subject;
        List<HomeController.GlobalSearchResultItem> bracketItems =
            ((List<object>)bracketJson.Value!).Cast<HomeController.GlobalSearchResultItem>().ToList();

        bracketItems.Should().HaveCount(2);
        bracketItems.Should().OnlyContain(r => r.Type == "clan" && r.Badge == "CLAN");
        bracketItems[0].Url.Should().Be($"/clans/{exactKongor.ID}");
        bracketItems[0].Name.Should().Be("[K] Kongor Vanguard");
        bracketItems[1].Url.Should().Be($"/clans/{academyKongor.ID}");
        bracketItems[1].Name.Should().Be("[KA] Kongor Academy");

        // 2. Empty brackets "[]" and "[[" must return empty (never match all clans!)
        IActionResult emptyBracketRes = await controller.SearchGlobal("[]");
        ((Array)((JsonResult)emptyBracketRes).Value!).Length.Should().Be(0);

        IActionResult doubleOpenRes = await controller.SearchGlobal("[[");
        ((Array)((JsonResult)doubleOpenRes).Value!).Length.Should().Be(0);

        // 3. Searching "clans" returns the Clans static navigation page
        IActionResult pageRes = await controller.SearchGlobal("clans");
        List<HomeController.GlobalSearchResultItem> pageItems =
            ((List<object>)((JsonResult)pageRes).Value!).Cast<HomeController.GlobalSearchResultItem>().ToList();
        pageItems.Should().Contain(r => r.Type == "page" && r.Url == "/clans" && r.Name == "Clans");
    }

    [Fact]
    public async Task ClansController_PopulatesMyClanPill_AndReturns404ForMissingClan()
    {
        await using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();

        Clan clan = new() { Name = "Dawnbringer Elite", Tag = "DBE", TimestampCreated = FixedNow.AddDays(-50) };
        (_, Account me) = CreateAccount("PortalHero", clan, ClanTier.Leader, matchmakingMmr: 1850, mmPlayed: 30, mmWon: 20);
        db.Clans.Add(clan);
        db.Accounts.Add(me);
        await db.SaveChangesAsync();

        IPlayerStatsApiClient statsClient = TestApiClientFactory.CreatePlayerStatsApiClient(db);
        ClansController controller = new(statsClient, NullLogger<ClansController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "PortalHero")], "TestAuth"))
                }
            }
        };

        ViewResult indexView = (await controller.Index()).Should().BeOfType<ViewResult>().Subject;
        ClanDirectoryViewModel indexVm = indexView.Model.Should().BeOfType<ClanDirectoryViewModel>().Subject;
        indexVm.MyClanId.Should().Be(clan.ID);
        indexVm.MyClanTag.Should().Be("DBE");
        indexVm.MyClanName.Should().Be("Dawnbringer Elite");
        indexVm.Directory.Items.Should().ContainSingle(c => c.Id == clan.ID);

        ViewResult detailView = (await controller.Detail(clan.ID)).Should().BeOfType<ViewResult>().Subject;
        ClanDetailViewModel detailVm = detailView.Model.Should().BeOfType<ClanDetailViewModel>().Subject;
        detailVm.IsMyClan.Should().BeTrue();
        detailVm.Clan.Should().NotBeNull();
        detailVm.Clan!.Members.Should().ContainSingle(m => m.Name == "PortalHero");

        ViewResult missingView = (await controller.Detail(999999)).Should().BeOfType<ViewResult>().Subject;
        controller.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        missingView.Model.Should().BeOfType<ClanDetailViewModel>().Subject.Clan.Should().BeNull();
    }

    [Fact]
    public async Task PlayerStatsApiController_ViaLocalMediator_ServesClanDirectoryAndDetailEndpoints()
    {
        await using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();

        Clan clan = new() { Name = "Api Test Clan", Tag = "ATC", TimestampCreated = FixedNow.AddDays(-15) };
        (_, Account leader) = CreateAccount("ApiLeader", clan, ClanTier.Leader, matchmakingMmr: 1780, mmPlayed: 25, mmWon: 15);
        db.Clans.Add(clan);
        db.Accounts.Add(leader);
        await db.SaveChangesAsync();

        global::MERRICK.DatabaseContext.Repositories.MatchHistoryRepository matchRepo = new(db);
        global::MERRICK.DatabaseContext.Repositories.AccountRepository accountRepo = new(db);
        LocalPlayerStatsMediator mediator = new(db, matchRepo, accountRepo, timeProvider: new FixedTimeProvider(FixedNow));
        global::ZORGATH.WebPortal.API.Controllers.PlayerStatsApiController apiController = new(mediator);

        OkObjectResult dirOk = (await apiController.GetClanDirectory(1, 24, null, "mmr")).Should().BeOfType<OkObjectResult>().Subject;
        ClanDirectoryResultDto dirDto = dirOk.Value.Should().BeOfType<ClanDirectoryResultDto>().Subject;
        dirDto.Items.Should().ContainSingle(c => c.Tag == "ATC");

        OkObjectResult detailOk = (await apiController.GetClanDetail(clan.ID)).Should().BeOfType<OkObjectResult>().Subject;
        ClanDetailDto detailDto = detailOk.Value.Should().BeOfType<ClanDetailDto>().Subject;
        detailDto.Summary.Name.Should().Be("Api Test Clan");
        detailDto.Members.Should().ContainSingle(m => m.Name == "ApiLeader");

        (await apiController.GetClanDetail(999999)).Should().BeOfType<NotFoundObjectResult>();
        (await apiController.GetClanDetail(0)).Should().BeOfType<BadRequestObjectResult>();
    }
}
