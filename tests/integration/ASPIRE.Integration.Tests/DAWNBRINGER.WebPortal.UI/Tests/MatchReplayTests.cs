// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using DAWNBRINGER.WebPortal.UI.Controllers;
using DAWNBRINGER.WebPortal.UI.Services;
using MERRICK.DatabaseContext.Entities.Statistics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.Clients;

namespace ASPIRE.Tests.DAWNBRINGER.WebPortal.UI.Tests;

public sealed class MatchReplayTests
{
    private static MerrickContext CreateInMemoryContext(string dbName)
    {
        return TestDbContextFactory.CreateInMemoryContext(dbName);
    }

    private static MatchReplayService CreateReplayService(MerrickContext context)
    {
        IPlayerStatsApiClient statsApi = TestApiClientFactory.CreatePlayerStatsApiClient(context);
        return new MatchReplayService(statsApi, NullLogger<MatchReplayService>.Instance);
    }

    private static PlayerStatistics CreateDefaultPlayerStatistics(int accountId, int matchId, string accountName,
        int team, string heroIdentifier)
    {
        return new PlayerStatistics
        {
            MatchID = matchId,
            AccountID = accountId,
            AccountName = accountName,
            ClanID = null,
            ClanTag = "PK",
            Team = team,
            LobbyPosition = 0,
            GroupNumber = 0,
            Benefit = 0,
            HeroProductID = 0,
            MVP = team == 1 ? 1 : 0,
            HeroIdentifier = heroIdentifier,
            Inventory = ["item_steamboots", "item_shrunkenhead"],
            Win = team == 1 ? 1 : 0,
            Loss = team == 1 ? 0 : 1,
            Disconnected = 0,
            Conceded = 0,
            Kicked = 0,
            PublicMatch = 1,
            PublicSkillRatingChange = 1500,
            RankedMatch = 0,
            RankedSkillRatingChange = 0,
            SocialBonus = 0,
            UsedToken = 0,
            ConcedeVotes = 0,
            HeroKills = team == 1 ? 12 : 4,
            HeroDamage = 12000,
            GoldFromHeroKills = 2000,
            HeroAssists = 4,
            HeroExperience = 15000,
            HeroDeaths = team == 1 ? 2 : 8,
            Buybacks = 0,
            GoldLostToDeath = 500,
            SecondsDead = 120,
            TeamCreepKills = 150,
            TeamCreepDamage = 20000,
            TeamCreepGold = 6000,
            TeamCreepExperience = 10000,
            NeutralCreepKills = 20,
            NeutralCreepDamage = 3000,
            NeutralCreepGold = 1000,
            NeutralCreepExperience = 2000,
            BuildingDamage = 1500,
            BuildingsRazed = 1,
            ExperienceFromBuildings = 500,
            GoldFromBuildings = 800,
            Denies = 15,
            ExperienceDenied = 600,
            Gold = 500,
            GoldSpent = 8500,
            Experience = 25000,
            Actions = 6000,
            SecondsPlayed = 1200,
            HeroLevel = team == 1 ? 20 : 16,
            ConsumablesPurchased = 5,
            WardsPlaced = 4,
            FirstBlood = team == 1 ? 1 : 0,
            DoubleKill = 1,
            TripleKill = 0,
            QuadKill = 0,
            Annihilation = 0,
            KillStreak03 = 1,
            KillStreak04 = 0,
            KillStreak05 = 0,
            KillStreak06 = 0,
            KillStreak07 = 0,
            KillStreak08 = 0,
            KillStreak09 = 0,
            KillStreak10 = 0,
            KillStreak15 = 0,
            Smackdown = 0,
            Humiliation = 0,
            Nemesis = 0,
            Retribution = 0,
            Score = 100,
            GameplayStat0 = 0,
            GameplayStat1 = 0,
            GameplayStat2 = 0,
            GameplayStat3 = 0,
            GameplayStat4 = 0,
            GameplayStat5 = 0,
            GameplayStat6 = 0,
            GameplayStat7 = 0,
            GameplayStat8 = 0,
            GameplayStat9 = 0,
            TimeEarningExperience = 1200
        };
    }

    private static MatchStatistics CreateDefaultMatchStatistics(int matchId)
    {
        return new MatchStatistics
        {
            MatchID = matchId,
            ServerID = 1,
            HostAccountName = "HostPlayer",
            Map = "caldavar",
            MapVersion = "1.0",
            TimePlayed = 1200,
            FileSize = 50000,
            FileName = $"M{matchId}.honreplay",
            ConnectionState = 1,
            Version = "4.10.1",
            AveragePSR = 1500,
            AveragePSRTeamOne = 1500,
            AveragePSRTeamTwo = 1500,
            GameMode = "Ranked",
            ScoreTeam1 = 25,
            ScoreTeam2 = 18,
            TeamScoreGoal = 0,
            PlayerScoreGoal = 0,
            NumberOfRounds = 1,
            ReleaseStage = "Live",
            BannedHeroes = null,
            AwardMostAnnihilations = null,
            AwardMostQuadKills = null,
            AwardLargestKillStreak = null,
            AwardMostSmackdowns = null,
            AwardMostKills = null,
            AwardMostAssists = null,
            AwardLeastDeaths = null,
            AwardMostBuildingDamage = null,
            AwardMostWardsKilled = null,
            AwardMostHeroDamageDealt = null,
            AwardHighestCreepScore = null
        };
    }

    [Fact]
    public async Task GetMatchReplayTimelineAsync_WithDemoMatchId_ReturnsTimelineWithKeyframesAndPins()
    {
        // Arrange
        await using MerrickContext context =
            CreateInMemoryContext(
                nameof(GetMatchReplayTimelineAsync_WithDemoMatchId_ReturnsTimelineWithKeyframesAndPins));
        MatchReplayService service = CreateReplayService(context);

        // Act
        MatchReplayTimelineDto? result = await service.GetMatchReplayTimelineAsync(2060568725);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2060568725, result.MatchID);
        Assert.Equal("midwars", result.Map);
        Assert.NotEmpty(result.Players);
        Assert.NotEmpty(result.Keyframes);
        Assert.NotEmpty(result.EventPins);
        Assert.True(result.TotalDurationSeconds > 0);

        // Verify player hero identities and stats
        ReplayPlayerSummary hag = Assert.Single(result.Players, p => p.AccountName == "ClickBotAlpha");
        Assert.Equal("hero_babayaga", hag.HeroIdentifier);
        Assert.Equal("Wretched Hag", hag.HeroDisplayName);
        Assert.Equal(21, hag.FinalLevel);
        Assert.Equal(3, hag.FinalKills);
        Assert.Equal(15, hag.FinalDeaths);

        ReplayPlayerSummary bubbles = Assert.Single(result.Players, p => p.AccountName == "guccidonut");
        Assert.Equal("hero_bubbles", bubbles.HeroIdentifier);
        Assert.Equal("Bubbles", bubbles.HeroDisplayName);
        Assert.Equal(25, bubbles.FinalLevel);
        Assert.Equal(15, bubbles.FinalKills);
        Assert.Equal(4, bubbles.FinalDeaths);

        // Verify keyframes have valid interpolated unit coordinates
        MatchTimelineKeyframe firstFrame = result.Keyframes[0];
        Assert.Equal(0, firstFrame.Second);
        Assert.NotEmpty(firstFrame.Players);
        foreach (ReplayPlayerSnapshot player in firstFrame.Players)
        {
            Assert.InRange(player.PositionX, 0f, 1f);
            Assert.InRange(player.PositionY, 0f, 1f);
            Assert.InRange(player.HealthPercent, 0f, 100f);
        }
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task GetMatchReplayTimelineAsync_WithDatabaseEntities_GeneratesAccurateTimeline()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;

        // Arrange
        await using MerrickContext context =
            CreateInMemoryContext(nameof(GetMatchReplayTimelineAsync_WithDatabaseEntities_GeneratesAccurateTimeline));

        context.MatchStatistics.Add(CreateDefaultMatchStatistics(999999));
        context.PlayerStatistics.AddRange(
            CreateDefaultPlayerStatistics(1001, 999999, "PlayerOne", 1, "hero_hunter"),
            CreateDefaultPlayerStatistics(2002, 999999, "PlayerTwo", 2, "hero_armadon")
        );

        await context.SaveChangesAsync();

        MatchReplayService service = CreateReplayService(context);

        // Act
        MatchReplayTimelineDto? result = await service.GetMatchReplayTimelineAsync(999999);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(999999, result.MatchID);
        Assert.Equal("caldavar", result.Map);
        Assert.Equal(1200, result.TotalDurationSeconds);
        Assert.Equal(1, result.WinnerTeam);
        Assert.Equal(2, result.Players.Count);
        Assert.NotEmpty(result.Keyframes);
        Assert.NotEmpty(result.EventPins);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task GetMatchReplayTelemetry_ApiEndpoint_ReturnsReplayDto()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange
        await using MerrickContext context =
            CreateInMemoryContext(nameof(GetMatchReplayTelemetry_ApiEndpoint_ReturnsReplayDto));
        MatchReplayService replayService = CreateReplayService(context);
        IConfiguration config = new ConfigurationBuilder().Build();
        IUserApiClient userApi = TestApiClientFactory.CreateUserApiClient(context);
        IAdminApiClient adminApi = TestApiClientFactory.CreateAdminApiClient(context);
        IPlayerStatsApiClient statsApi = TestApiClientFactory.CreatePlayerStatsApiClient(context);
        IGuideApiClient guideApi = TestApiClientFactory.CreateGuideApiClient(context);
        HomeController controller = new(statsApi, userApi, guideApi, adminApi, config, replayService, null, null,
            NullLogger<HomeController>.Instance);

        // Act
        IActionResult result = await controller.GetMatchReplayTelemetry(2060568725);

        // Assert
        JsonResult jsonResult = Assert.IsType<JsonResult>(result);
        MatchReplayTimelineDto dto = Assert.IsType<MatchReplayTimelineDto>(jsonResult.Value);
        Assert.Equal(2060568725, dto.MatchID);
        Assert.NotEmpty(dto.Keyframes);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task GetMatchReplayTimelineAsync_EndgameKeyframe_MatchesMatchHistoryInventoryAndStatsExact()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange
        await using MerrickContext context =
            CreateInMemoryContext(
                nameof(GetMatchReplayTimelineAsync_EndgameKeyframe_MatchesMatchHistoryInventoryAndStatsExact));

        context.MatchStatistics.Add(CreateDefaultMatchStatistics(888888));
        context.PlayerStatistics.AddRange(
            CreateDefaultPlayerStatistics(1001, 888888, "PlayerOne", 1, "hero_hunter"),
            CreateDefaultPlayerStatistics(2002, 888888, "PlayerTwo", 2, "hero_armadon")
        );

        await context.SaveChangesAsync();

        MatchReplayService service = CreateReplayService(context);

        // Act
        MatchReplayTimelineDto? result = await service.GetMatchReplayTimelineAsync(888888);

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result.Keyframes);

        MatchTimelineKeyframe lastFrame = result.Keyframes[^1];
        Assert.Equal(result.TotalDurationSeconds, lastFrame.Second);

        foreach (ReplayPlayerSummary summary in result.Players)
        {
            ReplayPlayerSnapshot? endSnapshot = lastFrame.Players.FirstOrDefault(p => p.AccountID == summary.AccountID);
            Assert.NotNull(endSnapshot);

            // Exact match history alignment
            Assert.Equal(summary.FinalLevel, endSnapshot.Level);
            Assert.Equal(summary.FinalKills, endSnapshot.Kills);
            Assert.Equal(summary.FinalDeaths, endSnapshot.Deaths);
            Assert.Equal(summary.FinalGpm, endSnapshot.Gpm);
            Assert.Equal(summary.FinalXpm, endSnapshot.Xpm);
            Assert.Equal(summary.FinalGold, endSnapshot.Gold);

            // Inventory match exact
            Assert.Equal(summary.FinalItems.Count, endSnapshot.Items.Count);
            for (int i = 0; i < summary.FinalItems.Count; i++)
                Assert.Equal(summary.FinalItems[i], endSnapshot.Items[i]);
        }
    }
}
