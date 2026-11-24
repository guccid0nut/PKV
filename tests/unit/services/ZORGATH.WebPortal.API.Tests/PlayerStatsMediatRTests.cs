// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Enumerations.Statistics;
using FluentAssertions;
using Kongor.Core.Services.Session;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Moq;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Game;
using MERRICK.DatabaseContext.Entities.Statistics;
using MERRICK.DatabaseContext.Persistence;
using NEXUS.Contracts.Clients;
using NEXUS.Contracts.CQRS.PlayerStats;
using NEXUS.Contracts.Data;
using NEXUS.Contracts.Domain;
using NEXUS.Contracts.DTOs;
using NEXUS.Contracts.Telemetry;
using Xunit;
using ZORGATH.WebPortal.API.Controllers;
using ZORGATH.WebPortal.API.Features.PlayerStats;

namespace ZORGATH.WebPortal.API.Tests;

public sealed class PlayerStatsMediatRTests
{
    [Fact]
    public async Task GetAccountStatisticsHandler_ReturnsMappedDto()
    {
        Mock<IMatchHistoryRepository> mockRepo = new();
        AccountStatistics dummyStats = new()
        {
            ID = 1,
            AccountID = 42,
            Type = AccountStatisticsType.Matchmaking,
            MatchesPlayed = 100,
            MatchesWon = 60,
            MatchesLost = 40,
            SkillRating = 1750
        };

        mockRepo.Setup(r => r.GetAccountStatisticsAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(dummyStats);

        GetAccountStatisticsHandler handler = new(mockRepo.Object);
        AccountStatisticsDto? result = await handler.Handle(new GetAccountStatisticsQuery(42), CancellationToken.None);

        result.Should().NotBeNull();
        result!.AccountID.Should().Be(42);
        result.MatchesPlayed.Should().Be(100);
        result.MatchesWon.Should().Be(60);
    }

    [Fact]
    public async Task GetTopRankedAccountsHandler_ReturnsMappedList()
    {
        Mock<IMatchHistoryRepository> mockRepo = new();
        AccountStatistics s1 = new() { ID = 1, AccountID = 10, Type = AccountStatisticsType.Matchmaking, SkillRating = 1900 };
        AccountStatistics s2 = new() { ID = 2, AccountID = 20, Type = AccountStatisticsType.Matchmaking, SkillRating = 1850 };

        mockRepo.Setup(r => r.GetTopRankedAccountsAsync(2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AccountStatistics> { s1, s2 });

        GetTopRankedAccountsHandler handler = new(mockRepo.Object);
        IReadOnlyList<AccountStatisticsDto> result =
            await handler.Handle(new GetTopRankedAccountsQuery(2), CancellationToken.None);

        result.Should().HaveCount(2);
        result[0].AccountID.Should().Be(10);
        result[1].AccountID.Should().Be(20);
    }

    [Fact]
    public async Task GetRecentMatchesHandler_ReturnsMappedList()
    {
        Mock<IMatchHistoryRepository> mockRepo = new();
        MatchStatistics m1 = new() { ID = 1, MatchID = 1001, Map = "caldavar" };

        mockRepo.Setup(r => r.GetRecentMatchesAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MatchStatistics> { m1 });

        GetRecentMatchesHandler handler = new(mockRepo.Object);
        IReadOnlyList<MatchStatisticsDto> result =
            await handler.Handle(new GetRecentMatchesQuery(10), CancellationToken.None);

        result.Should().HaveCount(1);
        result[0].MatchID.Should().Be(1001);
    }

    [Fact]
    public async Task GetAccountMatchesHandler_ReturnsMappedList()
    {
        Mock<IMatchHistoryRepository> mockRepo = new();
        MatchStatistics m1 = new() { ID = 1, MatchID = 2002, Map = "midwars" };

        mockRepo.Setup(r => r.GetRecentMatchesByAccountIdAsync(55, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MatchStatistics> { m1 });

        GetAccountMatchesHandler handler = new(mockRepo.Object);
        IReadOnlyList<MatchStatisticsDto> result =
            await handler.Handle(new GetAccountMatchesQuery(55, 5), CancellationToken.None);

        result.Should().HaveCount(1);
        result[0].MatchID.Should().Be(2002);
    }

    [Fact]
    public async Task GetMatchDetailsHandler_ReturnsMappedDto()
    {
        Mock<IMatchHistoryRepository> mockRepo = new();
        MatchStatistics m1 = new() { ID = 1, MatchID = 3003, Map = "caldavar", ScoreTeam1 = 30, ScoreTeam2 = 25 };

        mockRepo.Setup(r => r.GetMatchByIdAsync(3003, It.IsAny<CancellationToken>()))
            .ReturnsAsync(m1);

        GetMatchDetailsHandler handler = new(mockRepo.Object);
        MatchStatisticsDto? result = await handler.Handle(new GetMatchDetailsQuery(3003), CancellationToken.None);

        result.Should().NotBeNull();
        result!.MatchID.Should().Be(3003);
    }

    [Fact]
    public async Task GetMatchPlayerStatsHandler_ReturnsMappedList()
    {
        Mock<IMatchHistoryRepository> mockRepo = new();
        PlayerStatistics p1 = new() { ID = 1, MatchID = 4004, AccountID = 100, HeroIdentifier = "Hero_Pyromancer" };

        mockRepo.Setup(r => r.GetPlayerStatisticsForMatchAsync(4004, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PlayerStatistics> { p1 });

        GetMatchPlayerStatsHandler handler = new(mockRepo.Object);
        IReadOnlyList<PlayerStatisticsDto> result =
            await handler.Handle(new GetMatchPlayerStatsQuery(4004), CancellationToken.None);

        result.Should().HaveCount(1);
        result[0].HeroIdentifier.Should().Be("Hero_Pyromancer");
    }

    [Fact]
    public async Task GetRecentPlayerStatsHandler_ReturnsMappedList()
    {
        Mock<IMatchHistoryRepository> mockRepo = new();
        PlayerStatistics p1 = new() { ID = 1, MatchID = 5005, AccountID = 200, HeroIdentifier = "Hero_Valkyrie" };

        mockRepo.Setup(r => r.GetRecentPlayerStatisticsByAccountIdAsync(200, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PlayerStatistics> { p1 });

        GetRecentPlayerStatsHandler handler = new(mockRepo.Object);
        IReadOnlyList<PlayerStatisticsDto> result =
            await handler.Handle(new GetRecentPlayerStatsQuery(200, 5), CancellationToken.None);

        result.Should().HaveCount(1);
        result[0].HeroIdentifier.Should().Be("Hero_Valkyrie");
    }

    [Fact]
    public async Task GetSubmittedMatchIdsHandler_ReturnsExistingIds()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        db.MatchStatistics.Add(new MatchStatistics { ID = 1, MatchID = 100 });
        db.MatchStatistics.Add(new MatchStatistics { ID = 2, MatchID = 200 });
        await db.SaveChangesAsync();

        GetSubmittedMatchIdsHandler handler = new(db);
        List<int> result = await handler.Handle(new GetSubmittedMatchIdsQuery([100, 200, 300, 400]), CancellationToken.None);

        result.Should().Contain([100, 200]);
        result.Should().NotContain(300);
    }

    [Fact]
    public async Task GetTotalMatchesCountHandler_ReturnsCount()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        db.MatchStatistics.Add(new MatchStatistics { ID = 1, MatchID = 10 });
        db.MatchStatistics.Add(new MatchStatistics { ID = 2, MatchID = 20 });
        db.MatchStatistics.Add(new MatchStatistics { ID = 3, MatchID = 30 });
        await db.SaveChangesAsync();

        GetTotalMatchesCountHandler handler = new(db);
        int count = await handler.Handle(new GetTotalMatchesCountQuery(), CancellationToken.None);

        count.Should().Be(3);
    }

    [Fact]
    public async Task SearchAccountsHandler_ReturnsMappedList()
    {
        Mock<IAccountRepository> mockRepo = new();
        Account acc = new()
        {
            ID = 1,
            Name = "KongorPro",
            User = new User
            {
                ID = 1,
                EmailAddress = "pro@k.net",
                SRPPasswordSalt = "s",
                SRPPasswordHash = "h",
                PBKDF2PasswordHash = "p"
            }
        };

        mockRepo.Setup(r => r.SearchAccountsAsync("Kongor", 1, 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Account> { acc });

        SearchAccountsHandler handler = new(mockRepo.Object);
        IReadOnlyList<AccountDto> result =
            await handler.Handle(new SearchAccountsQuery("Kongor", 1, 10), CancellationToken.None);

        result.Should().HaveCount(1);
        result[0].Name.Should().Be("KongorPro");
    }

    [Fact]
    public async Task GetAccountByIgnHandler_ReturnsMappedDto()
    {
        Mock<IAccountRepository> mockRepo = new();
        Account acc = new()
        {
            ID = 2,
            Name = "MoonQueen",
            User = new User
            {
                ID = 2,
                EmailAddress = "mq@k.net",
                SRPPasswordSalt = "s",
                SRPPasswordHash = "h",
                PBKDF2PasswordHash = "p"
            }
        };

        mockRepo.Setup(r => r.GetByNicknameAsync("MoonQueen", It.IsAny<CancellationToken>()))
            .ReturnsAsync(acc);

        GetAccountByIgnHandler handler = new(mockRepo.Object);
        AccountDto? result = await handler.Handle(new GetAccountByIgnQuery("MoonQueen"), CancellationToken.None);

        result.Should().NotBeNull();
        result!.Name.Should().Be("MoonQueen");
    }

    [Fact]
    public async Task MatchEvidenceHandlers_GetAndSave_WorkCorrectly()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();

        SaveMatchEvidenceHandler saveHandler = new(db);
        MatchEvidenceDto dto = new()
        {
            MatchID = 777,
            ChatLogJson = "[\"gl hf\"]",
            HasReplay = true,
            HasVoice = false,
            VoiceTrackLegionUrl = "http://cdn/legion.ogg"
        };

        bool saved = await saveHandler.Handle(new SaveMatchEvidenceCommand(777, dto), CancellationToken.None);
        saved.Should().BeTrue();

        GetMatchEvidenceHandler getHandler = new(db);
        MatchEvidenceDto? retrieved = await getHandler.Handle(new GetMatchEvidenceQuery(777), CancellationToken.None);

        retrieved.Should().NotBeNull();
        retrieved!.MatchID.Should().Be(777);
        retrieved.HasReplay.Should().BeTrue();
        retrieved.VoiceTrackLegionUrl.Should().Be("http://cdn/legion.ogg");
    }

    [Fact]
    public async Task GetServerHostsHandler_ReturnsApprovedHosts()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        User user1 = new()
        {
            ID = 10,
            EmailAddress = "host1@kongor.net",
            SRPPasswordSalt = "s",
            SRPPasswordHash = "h",
            PBKDF2PasswordHash = "p"
        };
        Account account1 = new()
        {
            ID = 100,
            Name = "HostAlpha",
            User = user1
        };
        db.Users.Add(user1);
        db.Accounts.Add(account1);

        db.HostApplications.Add(new HostApplication
        {
            ID = 1,
            UserID = 10,
            User = user1,
            MainAccountID = 100,
            MainAccount = account1,
            HostUsername = "HostAlpha",
            Location = "US East",
            Status = HostApplicationStatus.Approved
        });
        db.HostApplications.Add(new HostApplication
        {
            ID = 2,
            UserID = 10,
            User = user1,
            MainAccountID = 100,
            MainAccount = account1,
            HostUsername = "HostBeta",
            Location = "EU West",
            Status = HostApplicationStatus.Pending
        });
        await db.SaveChangesAsync();

        GetServerHostsHandler handler = new(db);
        List<ServerHostDto> hosts = await handler.Handle(new GetServerHostsQuery(), CancellationToken.None);

        hosts.Should().HaveCount(1);
        hosts[0].HostAccountName.Should().Be("HostAlpha");
    }

    [Fact]
    public async Task HeroAndItemCompendiumHandlers_ReturnCompendiumData()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        db.HeroDetails.Add(new HeroDetail
        {
            ID = 1,
            HeroCode = "Hero_Magmus",
            DisplayName = "Magmus",
            PrimaryAttribute = "Strength",
            AbilitiesJson = "[]",
            IconPath = "/icons/magmus.png",
            SplashImagePath = "/splash/magmus.png",
            Lore = "Ancient warrior of volcanic stone."
        });
        db.HeroItems.Add(new HeroItem
        {
            ID = 1,
            Code = "Item_PortalKey",
            Name = "Portal Key",
            Cost = 2150,
            Description = "Blinks the hero.",
            Flavor = "A mystic key.",
            IconPath = "/icons/portalkey.png"
        });
        await db.SaveChangesAsync();

        GetAllHeroDetailsHandler allHeroes = new(db);
        var heroList = await allHeroes.Handle(new GetAllHeroDetailsQuery(), CancellationToken.None);
        heroList.Should().ContainSingle(h => h.DisplayName == "Magmus");

        GetHeroDetailHandler singleHero = new(db);
        var hero = await singleHero.Handle(new GetHeroDetailQuery("Hero_Magmus"), CancellationToken.None);
        hero.Should().NotBeNull();
        hero!.DisplayName.Should().Be("Magmus");

        GetAllHeroItemsHandler allItems = new(db);
        var itemList = await allItems.Handle(new GetAllHeroItemsQuery(), CancellationToken.None);
        itemList.Should().ContainSingle(i => i.DisplayName == "Portal Key");
    }

    [Fact]
    public async Task SubmitPlayerReportHandler_PersistsReport()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        SubmitPlayerReportHandler handler = new(db);

        PlayerReportDto reportDto = new()
        {
            ReporterAccountID = 1,
            ReporterAccountName = "Reporter",
            ReportedAccountID = 2,
            ReportedAccountName = "Offender",
            MatchID = 888,
            Reason = "Griefing",
            Description = "Sold items and bought wards"
        };

        bool success = await handler.Handle(new SubmitPlayerReportCommand(reportDto), CancellationToken.None);
        success.Should().BeTrue();

        db.PlayerReports.Should().ContainSingle(r => r.MatchID == 888 && r.ReportedAccountName == "Offender");
    }

    [Fact]
    public async Task GetApprovedStreamersHandler_ReturnsStreamerLogins()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        User streamerUser = new()
        {
            ID = 99,
            EmailAddress = "streamer@k.net",
            SRPPasswordSalt = "s",
            SRPPasswordHash = "h",
            PBKDF2PasswordHash = "p"
        };
        Account streamerAcc = new()
        {
            ID = 99,
            Name = "PopularStreamer",
            User = streamerUser
        };
        db.Users.Add(streamerUser);
        db.Accounts.Add(streamerAcc);
        db.StreamerApplications.Add(new StreamerApplication
        {
            ID = 1,
            MainAccount = streamerAcc,
            MainAccountID = 99,
            Status = StreamerApplicationStatus.Approved
        });
        await db.SaveChangesAsync();

        GetApprovedStreamersHandler handler = new(db);
        List<string> result = await handler.Handle(new GetApprovedStreamersQuery(), CancellationToken.None);

        result.Should().Contain("PopularStreamer");
    }

    [Fact]
    public async Task GetLiveClusterTelemetryHandler_ReturnsComputedTelemetry()
    {
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        GetLiveClusterTelemetryHandler handler = new(db);

        LiveTelemetryDataDto telemetry = await handler.Handle(new GetLiveClusterTelemetryQuery(), CancellationToken.None);
        telemetry.OnlinePlayers.Should().BeGreaterThanOrEqualTo(1);
        telemetry.MatchesInProgress.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task GetLiveMatchTelemetryHandler_ReturnsCachedState()
    {
        Mock<IRedisMatchSessionCacheService> mockCache = new();
        LiveServerTickPayloadDto payload = new()
        {
            MatchID = 999,
            Map = "caldavar"
        };

        mockCache.Setup(c => c.GetLiveMatchStateAsync(999, It.IsAny<CancellationToken>()))
            .ReturnsAsync(payload);

        GetLiveMatchTelemetryHandler handler = new(mockCache.Object);
        LiveServerTickPayloadDto? result = await handler.Handle(new GetLiveMatchTelemetryQuery(999), CancellationToken.None);

        result.Should().NotBeNull();
        result!.MatchID.Should().Be(999);
        result.Map.Should().Be("caldavar");
    }

    [Fact]
    public async Task PlayerStatsApiController_Endpoints_DelegateToMediatR()
    {
        Mock<IMediator> mockMediator = new();

        mockMediator
            .Setup(m => m.Send(It.IsAny<GetPlayerProfileQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlayerProfileSummaryDto(
                new AccountDto { ID = 1, Name = "Hero" },
                new UserDto { ID = 1, EmailAddress = "h@k.net" },
                null, null, null, [], [], [], null, null,
                new HostTelemetryResultDto(false, false, [], [], [], 0, 0, 0, 0, null, null, "host"),
                []));

        mockMediator
            .Setup(m => m.Send(It.IsAny<GetAccountStatisticsQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountStatisticsDto { AccountID = 1, MatchesPlayed = 50 });

        mockMediator
            .Setup(m => m.Send(It.IsAny<GetTopRankedAccountsQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AccountStatisticsDto>());

        mockMediator
            .Setup(m => m.Send(It.IsAny<GetRecentMatchesQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MatchStatisticsDto>());

        mockMediator
            .Setup(m => m.Send(It.IsAny<GetAccountMatchesQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MatchStatisticsDto>());

        mockMediator
            .Setup(m => m.Send(It.IsAny<GetMatchDetailsQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MatchStatisticsDto { MatchID = 123 });

        mockMediator
            .Setup(m => m.Send(It.IsAny<GetMatchPlayerStatsQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PlayerStatisticsDto>());

        mockMediator
            .Setup(m => m.Send(It.IsAny<GetRecentPlayerStatsQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PlayerStatisticsDto>());

        mockMediator
            .Setup(m => m.Send(It.IsAny<GetSubmittedMatchIdsQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([1, 2]);

        mockMediator
            .Setup(m => m.Send(It.IsAny<GetMatchEvidenceQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MatchEvidenceDto { MatchID = 123 });

        mockMediator
            .Setup(m => m.Send(It.IsAny<SaveMatchEvidenceCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        mockMediator
            .Setup(m => m.Send(It.IsAny<GetServerHostsQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ServerHostDto>());

        mockMediator
            .Setup(m => m.Send(It.IsAny<GetAllHeroDetailsQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<HeroDetailDto>());

        mockMediator
            .Setup(m => m.Send(It.IsAny<GetHeroDetailQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HeroDetailDto { HeroIdentifier = "Hero_Test" });

        mockMediator
            .Setup(m => m.Send(It.IsAny<GetAllHeroItemsQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<HeroItemDto>());

        mockMediator
            .Setup(m => m.Send(It.IsAny<SubmitPlayerReportCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        mockMediator
            .Setup(m => m.Send(It.IsAny<GetApprovedStreamersQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["Streamer1"]);

        mockMediator
            .Setup(m => m.Send(It.IsAny<GetLiveClusterTelemetryQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LiveTelemetryDataDto(10, 5, 2, ["US East"], DateTimeOffset.UtcNow));

        mockMediator
            .Setup(m => m.Send(It.IsAny<SearchAccountsQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AccountDto>());

        mockMediator
            .Setup(m => m.Send(It.IsAny<GetAccountByIgnQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountDto { ID = 1, Name = "FoundHero" });

        mockMediator
            .Setup(m => m.Send(It.IsAny<GetTotalMatchesCountQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(999);

        mockMediator
            .Setup(m => m.Send(It.IsAny<GetLiveMatchTelemetryQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LiveServerTickPayloadDto { MatchID = 123 });

        mockMediator
            .Setup(m => m.Send(It.IsAny<GetMatchCustomStatsQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CustomMatchStatsResponseDto(
                123,
                new CustomMatchBannerDto(123, "caldavar", "Caldavar", "standard", 1, false, false, 10, 1, 0, "Legion", "Hellbourne", "1 - 0", 1800, "normal"),
                [],
                []));

        mockMediator
            .Setup(m => m.Send(It.IsAny<GetPlayerMapStatsQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlayerMapStatsResponseDto(
                1, "Hero", "caldavar", "Caldavar", "standard", 1, 10, 6, 4, 60.0, 18000, 50, 20, 40, null, [], [], [], []));

        mockMediator
            .Setup(m => m.Send(It.IsAny<GetPlayerMapsOverviewQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new PlayerMapOverviewItemDto("caldavar", "Caldavar", "standard", 10, 6, 4, 60.0, DateTimeOffset.UtcNow)]);

        PlayerStatsApiController controller = new(mockMediator.Object);

        // Verify representative actions delegate to mediator
        var profileRes = await controller.GetPlayerProfile("Hero");
        profileRes.Should().BeOfType<OkObjectResult>();

        var statsRes = await controller.GetAccountStatistics(1);
        statsRes.Should().BeOfType<OkObjectResult>();

        var topRes = await controller.GetTopRanked();
        topRes.Should().BeOfType<OkObjectResult>();

        var recentRes = await controller.GetRecentMatches();
        recentRes.Should().BeOfType<OkObjectResult>();

        var matchDetailRes = await controller.GetMatchById(123);
        matchDetailRes.Should().BeOfType<OkObjectResult>();

        var evidenceRes = await controller.GetMatchEvidence(123);
        evidenceRes.Should().BeOfType<OkObjectResult>();

        var saveEvidenceRes = await controller.SaveMatchEvidence(new MatchEvidenceDto { MatchID = 123 });
        saveEvidenceRes.Should().BeOfType<OkObjectResult>();

        var hostsRes = await controller.GetServerHosts();
        hostsRes.Should().BeOfType<OkObjectResult>();

        var heroesRes = await controller.GetAllHeroDetails();
        heroesRes.Should().BeOfType<OkObjectResult>();

        var reportRes = await controller.SubmitPlayerReport(new PlayerReportDto { MatchID = 123 });
        reportRes.Should().BeOfType<OkObjectResult>();

        var telemetryRes = await controller.GetLiveTelemetry();
        telemetryRes.Should().BeOfType<OkObjectResult>();

        var liveMatchRes = await controller.GetLiveMatchTelemetry(123);
        liveMatchRes.Should().BeOfType<OkObjectResult>();

        var countRes = await controller.GetTotalMatchesCount();
        countRes.Should().BeOfType<OkObjectResult>();

        var customMatchRes = await controller.GetMatchCustomStats(123);
        customMatchRes.Should().BeOfType<OkObjectResult>();

        var playerMapStatsRes = await controller.GetPlayerMapStats("Hero", "caldavar");
        playerMapStatsRes.Should().BeOfType<OkObjectResult>();

        var playerMapsRes = await controller.GetPlayerMapsOverview("Hero");
        playerMapsRes.Should().BeOfType<OkObjectResult>();

        (await controller.GetMatchCustomStats(0)).Should().BeOfType<BadRequestObjectResult>();
        (await controller.GetPlayerMapStats("  ")).Should().BeOfType<BadRequestObjectResult>();
        (await controller.GetPlayerMapsOverview("  ")).Should().BeOfType<BadRequestObjectResult>();

        mockMediator.Verify(m => m.Send(It.IsAny<GetPlayerProfileQuery>(), It.IsAny<CancellationToken>()), Times.Once);
        mockMediator.Verify(m => m.Send(It.IsAny<GetAccountStatisticsQuery>(), It.IsAny<CancellationToken>()), Times.Once);
        mockMediator.Verify(m => m.Send(It.IsAny<GetTopRankedAccountsQuery>(), It.IsAny<CancellationToken>()), Times.Once);
        mockMediator.Verify(m => m.Send(It.IsAny<GetRecentMatchesQuery>(), It.IsAny<CancellationToken>()), Times.Once);
        mockMediator.Verify(m => m.Send(It.IsAny<GetMatchDetailsQuery>(), It.IsAny<CancellationToken>()), Times.Once);
        mockMediator.Verify(m => m.Send(It.IsAny<GetMatchEvidenceQuery>(), It.IsAny<CancellationToken>()), Times.Once);
        mockMediator.Verify(m => m.Send(It.IsAny<SaveMatchEvidenceCommand>(), It.IsAny<CancellationToken>()), Times.Once);
        mockMediator.Verify(m => m.Send(It.IsAny<GetServerHostsQuery>(), It.IsAny<CancellationToken>()), Times.Once);
        mockMediator.Verify(m => m.Send(It.IsAny<GetAllHeroDetailsQuery>(), It.IsAny<CancellationToken>()), Times.Once);
        mockMediator.Verify(m => m.Send(It.IsAny<SubmitPlayerReportCommand>(), It.IsAny<CancellationToken>()), Times.Once);
        mockMediator.Verify(m => m.Send(It.IsAny<GetLiveClusterTelemetryQuery>(), It.IsAny<CancellationToken>()), Times.Once);
        mockMediator.Verify(m => m.Send(It.IsAny<GetLiveMatchTelemetryQuery>(), It.IsAny<CancellationToken>()), Times.Once);
        mockMediator.Verify(m => m.Send(It.IsAny<GetTotalMatchesCountQuery>(), It.IsAny<CancellationToken>()), Times.Once);
        mockMediator.Verify(m => m.Send(It.IsAny<GetMatchCustomStatsQuery>(), It.IsAny<CancellationToken>()), Times.Once);
        mockMediator.Verify(m => m.Send(It.IsAny<GetPlayerMapStatsQuery>(), It.IsAny<CancellationToken>()), Times.Once);
        mockMediator.Verify(m => m.Send(It.IsAny<GetPlayerMapsOverviewQuery>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CustomMapStatsHandlers_DelegateToRepository_AndReturnResults()
    {
        // protects: CustomMapStats controller->MediatR->ICustomMapStatsRepository contract in ZORGATH.WebPortal.API
        // fails_when: handlers fail to forward arguments, null out responses, or skip cancellation tokens
        // seam: GetMatchCustomStatsHandler / GetPlayerMapStatsHandler / GetPlayerMapsOverviewHandler -> ICustomMapStatsRepository
        Mock<ICustomMapStatsRepository> mockRepo = new();
        CustomMatchStatsResponseDto matchDto = new(
            77,
            new CustomMatchBannerDto(77, "soccer", "Soccer", "soccer", 1, false, false, 6, 3, 2, "Legion", "Hellbourne", "3 - 2", 900, "normal"),
            [],
            []);
        PlayerMapStatsResponseDto mapStatsDto = new(
            5, "Tester", "soccer", "Soccer", "soccer", 1, 4, 3, 1, 75.0, 3600, 12, 4, 8, null, [], [], [], []);
        IReadOnlyList<PlayerMapOverviewItemDto> overviewDtos =
            [new PlayerMapOverviewItemDto("soccer", "Soccer", "soccer", 4, 3, 1, 75.0, DateTimeOffset.UtcNow)];

        mockRepo.Setup(r => r.GetMatchCustomStatsAsync(77, null, It.IsAny<CancellationToken>())).ReturnsAsync(matchDto);
        mockRepo.Setup(r => r.GetPlayerMapStatsAsync("Tester", "soccer", null, It.IsAny<CancellationToken>())).ReturnsAsync(mapStatsDto);
        mockRepo.Setup(r => r.GetPlayerMapOverviewAsync("Tester", It.IsAny<CancellationToken>())).ReturnsAsync(overviewDtos);

        GetMatchCustomStatsHandler matchHandler = new(mockRepo.Object);
        GetPlayerMapStatsHandler playerMapHandler = new(mockRepo.Object);
        GetPlayerMapsOverviewHandler overviewHandler = new(mockRepo.Object);

        (await matchHandler.Handle(new GetMatchCustomStatsQuery(77), CancellationToken.None)).Should().BeSameAs(matchDto);
        (await playerMapHandler.Handle(new GetPlayerMapStatsQuery("Tester", "soccer"), CancellationToken.None)).Should().BeSameAs(mapStatsDto);
        (await overviewHandler.Handle(new GetPlayerMapsOverviewQuery("Tester"), CancellationToken.None)).Should().BeSameAs(overviewDtos);
    }
}
