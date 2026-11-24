// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Security.Claims;
using DAWNBRINGER.WebPortal.UI.Controllers;
using DAWNBRINGER.WebPortal.UI.Hubs;
using DAWNBRINGER.WebPortal.UI.Services;
using MERRICK.DatabaseContext.Entities.Statistics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ASPIRE.Tests.DAWNBRINGER.WebPortal.UI.Tests;

public sealed class LiveMatchesTests
{
    private static LiveMatchService CreateLiveMatchService()
    {
        return new LiveMatchService();
    }

    private static LiveController CreateLiveController(LiveMatchService service, bool isStaff = true)
    {
        LiveController controller = new(service, NullLogger<LiveController>.Instance);
        List<Claim> claims = [];
        if (isStaff)
        {
            claims.Add(new Claim("is_staff", "true"));
            claims.Add(new Claim(ClaimTypes.Role, "Staff"));
            claims.Add(new Claim(ClaimTypes.Name, "TestStaffUser"));
        }
        ClaimsIdentity identity = new(claims, isStaff ? "TestAuth" : null);
        ClaimsPrincipal principal = new(identity);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
        return controller;
    }

    [Fact]
    public async Task LiveMatches_LiveIndex_ReturnsViewWithActiveLiveMatches()
    {
        // Arrange
        LiveMatchService service = CreateLiveMatchService();
        service.GetAllMatchesInternal()[2060568725] = new LiveMatchData { MatchID = 2060568725, Map = "midwars" };
        LiveController controller = CreateLiveController(service);

        // Act
        IActionResult result = await controller.Index();

        // Assert
        ViewResult viewResult = Assert.IsType<ViewResult>(result);
        List<LiveMatchData> model = Assert.IsType<List<LiveMatchData>>(viewResult.Model, false);
        Assert.NotEmpty(model);
        Assert.Contains(model, m => m.MatchID == 2060568725);
    }

    [Fact]
    public async Task LiveSpectate_WithValidMatchId_ReturnsViewWithMatchSnapshot()
    {
        // Arrange
        LiveMatchService service = CreateLiveMatchService();
        service.GetAllMatchesInternal()[2060568725] = new LiveMatchData
        {
            MatchID = 2060568725,
            Map = "midwars",
            Players =
            [
                new LivePlayerState { AccountName = "ClickBotAlpha", Team = 1 },
                new LivePlayerState { AccountName = "guccidonut", Team = 2 }
            ]
        };
        LiveController controller = CreateLiveController(service);

        // Act
        IActionResult result = await controller.Spectate(2060568725);

        // Assert
        ViewResult viewResult = Assert.IsType<ViewResult>(result);
        LiveMatchData model = Assert.IsType<LiveMatchData>(viewResult.Model);
        Assert.Equal(2060568725, model.MatchID);
        Assert.Equal("midwars", model.Map);
        Assert.NotEmpty(model.Players);
        Assert.Contains(model.Players, p => p is { AccountName: "ClickBotAlpha", Team: 1 });
        Assert.Contains(model.Players, p => p is { AccountName: "guccidonut", Team: 2 });
    }

    [Fact]
    public async Task LiveSpectate_WithInvalidMatchId_ReturnsNotFound()
    {
        // Arrange
        LiveMatchService service = CreateLiveMatchService();
        LiveController controller = CreateLiveController(service);

        // Act
        IActionResult result = await controller.Spectate(99999999);

        // Assert
        NotFoundObjectResult notFound = Assert.IsType<NotFoundObjectResult>(result);
        Assert.Contains("99999999", notFound.Value?.ToString() ?? "");
    }

    [Fact]
    public async Task GetLiveMatchesApi_WhenExecuted_ReturnsJsonSummary()
    {
        // Arrange
        LiveMatchService service = CreateLiveMatchService();
        LiveController controller = CreateLiveController(service);

        // Act
        IActionResult result = await controller.GetLiveMatchesApi();

        // Assert
        JsonResult jsonResult = Assert.IsType<JsonResult>(result);
        Assert.NotNull(jsonResult.Value);
    }

    [Fact]
    public void LiveMatches_CoordinateNormalization_TranslatesK2ToCanvasBoundsAccurately()
    {
        // Center of map (0, 0) -> (0.5, 0.5)
        (float centerX, float centerY) = CoordinateNormalizationHelper.NormalizeCoordinates(0f, 0f);
        Assert.Equal(0.5f, centerX, 2);
        Assert.Equal(0.5f, centerY, 2);

        // Top-Left corner of map (-8000, 8000) -> (0.0, 0.0)
        (float topLeftX, float topLeftY) = CoordinateNormalizationHelper.NormalizeCoordinates(-8000f, 8000f);
        Assert.Equal(0.0f, topLeftX, 2);
        Assert.Equal(0.0f, topLeftY, 2);

        // Bottom-Right corner of map (8000, -8000) -> (1.0, 1.0)
        (float botRightX, float botRightY) = CoordinateNormalizationHelper.NormalizeCoordinates(8000f, -8000f);
        Assert.Equal(1.0f, botRightX, 2);
        Assert.Equal(1.0f, botRightY, 2);

        // Clamping check
        (float clampedX, float clampedY) = CoordinateNormalizationHelper.NormalizeCoordinates(-10000f, -10000f);
        Assert.Equal(0.0f, clampedX);
        Assert.Equal(1.0f, clampedY);
    }

    [Fact]
    public void MatchStatistics_VideoUrl_PropertyPersistsAndExposesHasVideo()
    {
        // Arrange & Act
        MatchStatistics matchStat = new()
        {
            MatchID = 100,
            ServerID = 1,
            HostAccountName = "ServerHost",
            Map = "caldavar",
            MapVersion = "1.0",
            TimePlayed = 1200,
            FileSize = 50000,
            FileName = "M100.honreplay",
            ConnectionState = 1,
            Version = "4.10.1",
            AveragePSR = 1500,
            AveragePSRTeamOne = 1500,
            AveragePSRTeamTwo = 1500,
            GameMode = "Ranked",
            ScoreTeam1 = 20,
            ScoreTeam2 = 15,
            TeamScoreGoal = 0,
            PlayerScoreGoal = 0,
            NumberOfRounds = 1,
            ReleaseStage = "Release",
            BannedHeroes = null,
            AwardMostAnnihilations = 0,
            AwardMostQuadKills = 0,
            AwardLargestKillStreak = 0,
            AwardMostSmackdowns = 0,
            AwardMostKills = 0,
            AwardMostAssists = 0,
            AwardLeastDeaths = 0,
            AwardMostBuildingDamage = 0,
            AwardMostWardsKilled = 0,
            AwardMostHeroDamageDealt = 0,
            AwardHighestCreepScore = 0,
            VideoUrl = "/videos/M100.mp4"
        };

        // Assert
        Assert.Equal("/videos/M100.mp4", matchStat.VideoUrl);
        Assert.True(matchStat.HasVideo);
    }

    [Fact]
    public void LiveMatchesHub_GetMatchGroupName_ReturnsFormattedGroup()
    {
        string groupName = LiveMatchesHub.GetMatchGroupName(2060568725);
        Assert.Equal("Match_2060568725", groupName);
    }

    [Fact]
    public async Task LiveMatchesHub_JoinMatch_SendsSnapshotToCaller()
    {
        // Arrange
        Mock<ILiveMatchService> mockService = new();
        Mock<IHubCallerClients<ILiveMatchesClient>> mockClients = new();
        Mock<ILiveMatchesClient> mockCaller = new();
        Mock<IGroupManager> mockGroups = new();
        Mock<HubCallerContext> mockContext = new();

        mockContext.Setup(c => c.ConnectionId).Returns("conn-spectator-777");
        mockContext.Setup(c => c.User).Returns(new ClaimsPrincipal(new ClaimsIdentity([new Claim("is_staff", "true"), new Claim(ClaimTypes.Role, "Staff")], "TestAuth")));
        mockClients.Setup(c => c.Caller).Returns(mockCaller.Object);

        LiveMatchData testMatch = new() { MatchID = 2060568725, Map = "midwars", TickId = 1500 };
        mockService.Setup(s => s.GetLiveMatchSnapshotAsync(2060568725))
            .ReturnsAsync(testMatch);

        LiveMatchesHub hub = new(mockService.Object, NullLogger<LiveMatchesHub>.Instance)
        {
            Context = mockContext.Object,
            Clients = mockClients.Object,
            Groups = mockGroups.Object
        };

        // Act
        await hub.JoinMatch(2060568725);

        // Assert
        mockGroups.Verify(g => g.AddToGroupAsync("conn-spectator-777", "Match_2060568725", CancellationToken.None),
            Times.Once);
        mockCaller.Verify(
            c => c.OnMatchSnapshot(It.Is<LiveMatchData>(m => m.MatchID == 2060568725 && m.TickId == 1500)), Times.Once);
    }

    [Fact]
    public async Task LiveMatchesHub_JoinOverview_SendsActiveMatchesToCaller()
    {
        // Arrange
        Mock<ILiveMatchService> mockService = new();
        Mock<IHubCallerClients<ILiveMatchesClient>> mockClients = new();
        Mock<ILiveMatchesClient> mockCaller = new();
        Mock<IGroupManager> mockGroups = new();
        Mock<HubCallerContext> mockContext = new();

        mockContext.Setup(c => c.ConnectionId).Returns("conn-overview-123");
        mockContext.Setup(c => c.User).Returns(new ClaimsPrincipal(new ClaimsIdentity([new Claim("is_staff", "true"), new Claim(ClaimTypes.Role, "Staff")], "TestAuth")));
        mockClients.Setup(c => c.Caller).Returns(mockCaller.Object);

        List<LiveMatchData> activeMatches = [new() { MatchID = 100 }, new() { MatchID = 200 }];
        mockService.Setup(s => s.GetActiveLiveMatchesAsync()).ReturnsAsync(activeMatches);

        LiveMatchesHub hub = new(mockService.Object, NullLogger<LiveMatchesHub>.Instance)
        {
            Context = mockContext.Object,
            Clients = mockClients.Object,
            Groups = mockGroups.Object
        };

        // Act
        await hub.JoinOverview();

        // Assert
        mockGroups.Verify(
            g => g.AddToGroupAsync("conn-overview-123", LiveMatchesHub.OverviewGroupName, CancellationToken.None),
            Times.Once);
        mockCaller.Verify(c => c.OnOverviewUpdated(It.Is<List<LiveMatchData>>(list => list.Count == 2)), Times.Once);
    }

    [Fact]
    public async Task LiveMatchesHub_SubmitMatchTick_AppliesTickToService()
    {
        // Arrange
        Mock<ILiveMatchService> mockService = new();
        Mock<HubCallerContext> mockContext = new();
        mockContext.Setup(c => c.ConnectionId).Returns("conn-staff-tick");
        mockContext.Setup(c => c.User).Returns(new ClaimsPrincipal(new ClaimsIdentity([new Claim("is_staff", "true"), new Claim(ClaimTypes.Role, "Staff")], "TestAuth")));
        LiveMatchesHub hub = new(mockService.Object, NullLogger<LiveMatchesHub>.Instance)
        {
            Context = mockContext.Object
        };

        LiveServerTickPayloadDto payload = new()
        {
            MatchID = 2060568761,
            Players = [new LiveServerPlayerTickDto { Slot = 0, AccountID = 1, Team = 1 }]
        };

        // Act
        await hub.SubmitMatchTick(payload);

        // Assert
        mockService.Verify(s => s.ApplyServerTickAsync(It.Is<LiveServerTickPayloadDto>(p => p.MatchID == 2060568761)),
            Times.Once);
    }

    [Fact]
    public void LiveMatchModels_WardLifecycle_RemainingSecondsCalculatesCorrectly()
    {
        // Arrange
        LiveWardState ward = new()
        {
            WardId = "ward-test-1",
            Team = 1,
            WardType = "Sight",
            DurationSeconds = 360,
            PlacedAtSecond = 100,
            CurrentElapsedSeconds = 250
        };

        // Act & Assert
        Assert.Equal(210, ward.RemainingSeconds);

        // When elapsed time exceeds duration
        ward.CurrentElapsedSeconds = 500;
        Assert.Equal(0, ward.RemainingSeconds);
    }

    [Fact]
    public void LiveMatchModels_GoldAdvantage_CalculatesTeamDeltas()
    {
        // Arrange
        LiveGoldTimelinePoint point = new()
        {
            Minute = 15,
            Second = 900,
            LegionNetWorth = 28500,
            HellbourneNetWorth = 24000,
            LegionExperience = 32000,
            HellbourneExperience = 30000
        };

        // Assert
        Assert.Equal(4500, point.GoldAdvantage);
        Assert.Equal(2000, point.ExperienceAdvantage);
    }

    [Fact]
    public void LiveMatchDeltaDto_StructureAndPlayerDeltas_CarryAccurateState()
    {
        // Arrange
        LiveMatchDeltaDto delta = new()
        {
            MatchID = 2060568725,
            TickId = 1050,
            ElapsedSeconds = 1420,
            ScoreLegion = 4,
            ScoreHellbourne = 16,
            PlayerDeltas =
            [
                new LivePlayerDeltaState
                {
                    AccountID = 1077,
                    PositionX = 0.470f,
                    PositionY = 0.470f,
                    HealthPercent = 90,
                    ManaPercent = 65,
                    CurrentGold = 13200
                }
            ],
            AddedWards =
            [
                new LiveWardState { WardId = "new-ward-1", Team = 1, PositionX = 0.45f, PositionY = 0.45f }
            ],
            ExpiredWardIds = ["old-ward-99"]
        };

        // Assert
        Assert.Equal(2060568725, delta.MatchID);
        Assert.Equal(1050, delta.TickId);
        Assert.Single(delta.PlayerDeltas);
        Assert.Equal(1077, delta.PlayerDeltas[0].AccountID);
        Assert.Single(delta.AddedWards);
        Assert.Single(delta.ExpiredWardIds);
    }

    [Fact]
    public async Task LiveMatchService_ApplyServerTick_SwitchesTeamDynamically()
    {
        // Arrange
        LiveMatchService service = CreateLiveMatchService();
        const int matchId = 2060568754;

        // 1. Initial tick with Legion (Team 1)
        LiveServerTickPayloadDto tick1 = new()
        {
            MatchID = matchId,
            ElapsedSeconds = 0,
            Players =
            [
                new LiveServerPlayerTickDto
                {
                    Slot = 0,
                    AccountID = 1,
                    AccountName = "guccidonut",
                    Team = 1,
                    PositionX = 0.203f,
                    PositionY = 0.788f
                }
            ]
        };

        bool result1 = await service.ApplyServerTickAsync(tick1);
        Assert.True(result1);

        LiveMatchData? snapshot1 = await service.GetLiveMatchSnapshotAsync(matchId);
        Assert.NotNull(snapshot1);
        Assert.Single(snapshot1.Players);
        Assert.Equal(1, snapshot1.Players[0].Team);
        Assert.Equal(0, snapshot1.Players[0].CurrentGold);

        // 2. Subsequent tick switching to Hellbourne (Team 2)
        LiveServerTickPayloadDto tick2 = new()
        {
            MatchID = matchId,
            ElapsedSeconds = 0,
            Players =
            [
                new LiveServerPlayerTickDto
                {
                    Slot = 0,
                    AccountID = 1,
                    AccountName = "guccidonut",
                    Team = 2,
                    PositionX = 0.797f,
                    PositionY = 0.212f
                }
            ]
        };

        bool result2 = await service.ApplyServerTickAsync(tick2);
        Assert.True(result2);

        LiveMatchData? snapshot2 = await service.GetLiveMatchSnapshotAsync(matchId);
        Assert.NotNull(snapshot2);
        Assert.Single(snapshot2.Players);
        Assert.Equal(2, snapshot2.Players[0].Team);
        Assert.Equal(0, snapshot2.Players[0].CurrentGold);
        Assert.Equal(0.797f, snapshot2.Players[0].PositionX, 3);
        Assert.Equal(0.212f, snapshot2.Players[0].PositionY, 3);
    }

    [Fact]
    public async Task LiveMatchService_ApplyTelemetryReport_SwitchesTeamForExistingPlayer()
    {
        // Arrange
        LiveMatchService service = CreateLiveMatchService();
        const int matchId = 2060568755;

        // 1. Initial report as Legion
        LiveTelemetryReportDto report1 = new()
        {
            MatchID = matchId,
            AccountID = 1,
            AccountName = "guccidonut",
            Team = 1,
            PositionX = 0.203f,
            PositionY = 0.788f
        };

        bool result1 = await service.ApplyTelemetryReportAsync(report1);
        Assert.True(result1);

        LiveMatchData? snapshot1 = await service.GetLiveMatchSnapshotAsync(matchId);
        Assert.NotNull(snapshot1);
        Assert.Equal(1, snapshot1.Players[0].Team);

        // 2. Update report as Hellbourne
        LiveTelemetryReportDto report2 = new()
        {
            MatchID = matchId,
            AccountID = 1,
            AccountName = "guccidonut",
            Team = 2,
            PositionX = 0.797f,
            PositionY = 0.212f
        };

        bool result2 = await service.ApplyTelemetryReportAsync(report2);
        Assert.True(result2);

        LiveMatchData? snapshot2 = await service.GetLiveMatchSnapshotAsync(matchId);
        Assert.NotNull(snapshot2);
        Assert.Equal(2, snapshot2.Players[0].Team);
    }
}
