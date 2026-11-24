// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: DuckDbAnalyticsApiControllerTests.cs
// System: ZORGATH Web Portal API Unit Tests - DuckDB Lakehouse Controller
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using FluentAssertions;
using MERRICK.DatabaseContext.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using ZORGATH.WebPortal.API.Controllers;

namespace ZORGATH.WebPortal.API.Tests.Services;

public sealed class DuckDbAnalyticsApiControllerTests
{
    private readonly Mock<IDuckDbAnalyticsService> _mockAnalyticsService = new();

    private DuckDbAnalyticsApiController CreateController()
    {
        return new DuckDbAnalyticsApiController(
            _mockAnalyticsService.Object,
            NullLogger<DuckDbAnalyticsApiController>.Instance);
    }

    [Fact]
    public async Task GetMatchBreakdown_ValidMatchId_ReturnsOkWithBreakdown()
    {
        // Arrange
        long matchId = 2060568725;
        List<MatchCombatPlayerBreakdownDto> expectedBreakdown =
        [
            new MatchCombatPlayerBreakdownDto(0, 42, 10, 5, 2, 25000f, 4, 1),
            new MatchCombatPlayerBreakdownDto(1, 35, 3, 12, 4, 12000f, 8, 0)
        ];

        _mockAnalyticsService
            .Setup(s => s.GetMatchCombatBreakdownAsync(matchId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedBreakdown);

        DuckDbAnalyticsApiController controller = CreateController();

        // Act
        IActionResult result = await controller.GetMatchBreakdown(matchId);

        // Assert
        OkObjectResult okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        IReadOnlyList<MatchCombatPlayerBreakdownDto> payload =
            okResult.Value.Should().BeAssignableTo<IReadOnlyList<MatchCombatPlayerBreakdownDto>>().Subject;
        payload.Should().HaveCount(2);
        payload[0].HeroId.Should().Be(42);
        payload[0].Kills.Should().Be(10);
    }

    [Fact]
    public async Task GetMatchBreakdown_InvalidMatchId_ReturnsBadRequest()
    {
        // Arrange
        DuckDbAnalyticsApiController controller = CreateController();

        // Act
        IActionResult result = await controller.GetMatchBreakdown(0);

        // Assert
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task GetPlayerCombatSummary_ValidPlayerWithData_ReturnsOk()
    {
        // Arrange
        int accountId = 1001;
        long matchId = 2060568725;
        PlayerCombatSummaryDto expectedSummary = new(
            AccountId: accountId,
            PlayerIndex: 0,
            HeroId: 42,
            Kills: 15,
            Assists: 8,
            Deaths: 3,
            TotalDamage: 32000f,
            WardsPlaced: 6,
            TotalMatches: 1);

        _mockAnalyticsService
            .Setup(s => s.GetPlayerCombatSummaryAsync(accountId, matchId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedSummary);

        DuckDbAnalyticsApiController controller = CreateController();

        // Act
        IActionResult result = await controller.GetPlayerCombatSummary(accountId, matchId);

        // Assert
        OkObjectResult okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        PlayerCombatSummaryDto payload = okResult.Value.Should().BeOfType<PlayerCombatSummaryDto>().Subject;
        payload.AccountId.Should().Be(accountId);
        payload.Kills.Should().Be(15);
    }

    [Fact]
    public async Task GetPlayerCombatSummary_NoDataFound_ReturnsNotFound()
    {
        // Arrange
        int accountId = 9999;
        _mockAnalyticsService
            .Setup(s => s.GetPlayerCombatSummaryAsync(accountId, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((PlayerCombatSummaryDto?)null);

        DuckDbAnalyticsApiController controller = CreateController();

        // Act
        IActionResult result = await controller.GetPlayerCombatSummary(accountId, null);

        // Assert
        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task GetHeroMatchup_ReturnsOkWithMatchup()
    {
        // Arrange
        short heroId = 42;
        short enemyHeroId = 35;
        HeroMatchupWinrateDto expectedMatchup = new(
            HeroId: heroId,
            EnemyHeroId: enemyHeroId,
            KillsOnEnemy: 12,
            DeathsToEnemy: 8,
            DamageToEnemy: 18000f,
            SharedMatches: 5,
            KillDeathRatio: 1.5);

        _mockAnalyticsService
            .Setup(s => s.GetHeroMatchupAsync(heroId, enemyHeroId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedMatchup);

        DuckDbAnalyticsApiController controller = CreateController();

        // Act
        IActionResult result = await controller.GetHeroMatchup(heroId, enemyHeroId);

        // Assert
        OkObjectResult okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        HeroMatchupWinrateDto payload = okResult.Value.Should().BeOfType<HeroMatchupWinrateDto>().Subject;
        payload.KillDeathRatio.Should().Be(1.5);
    }

    [Fact]
    public async Task GetMatchHeatmap_ValidMatchId_ReturnsOkWithHeatmapPoints()
    {
        // Arrange
        long matchId = 2060568725;
        List<CombatHeatmapPointDto> expectedPoints =
        [
            new CombatHeatmapPointDto(EventType: 1, PositionX: 5000f, PositionY: 5000f, Frequency: 12),
            new CombatHeatmapPointDto(EventType: 4, PositionX: 8192f, PositionY: 8192f, Frequency: 4)
        ];

        _mockAnalyticsService
            .Setup(s => s.GetCombatHeatmapAsync(matchId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedPoints);

        DuckDbAnalyticsApiController controller = CreateController();

        // Act
        IActionResult result = await controller.GetMatchHeatmap(matchId);

        // Assert
        OkObjectResult okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        IReadOnlyList<CombatHeatmapPointDto> points =
            okResult.Value.Should().BeAssignableTo<IReadOnlyList<CombatHeatmapPointDto>>().Subject;
        points.Should().HaveCount(2);
        points[0].PositionX.Should().Be(5000f);
        points[0].Frequency.Should().Be(12);
    }

    [Fact]
    public async Task GetMatchHeatmap_InvalidMatchId_ReturnsBadRequest()
    {
        // Arrange
        DuckDbAnalyticsApiController controller = CreateController();

        // Act
        IActionResult result = await controller.GetMatchHeatmap(-1);

        // Assert
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task GetHeroMatchupMatrix_ValidHeroId_ReturnsOkWithMatrix()
    {
        // Arrange
        short heroId = 42;
        List<HeroMatchupMatrixEntryDto> expectedMatrix =
        [
            new HeroMatchupMatrixEntryDto(
                HeroId: heroId,
                OpponentHeroId: 35,
                KillsAgainst: 15,
                DeathsTo: 10,
                DamageDealt: 25000f,
                TotalMatches: 8,
                KillDeathRatio: 1.5,
                WinRatePercent: 62.5),
            new HeroMatchupMatrixEntryDto(
                HeroId: heroId,
                OpponentHeroId: 10,
                KillsAgainst: 8,
                DeathsTo: 12,
                DamageDealt: 18000f,
                TotalMatches: 6,
                KillDeathRatio: 0.67,
                WinRatePercent: 33.3)
        ];

        _mockAnalyticsService
            .Setup(s => s.GetHeroMatchupMatrixAsync(heroId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedMatrix);

        DuckDbAnalyticsApiController controller = CreateController();

        // Act
        IActionResult result = await controller.GetHeroMatchupMatrix(heroId);

        // Assert
        OkObjectResult okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        IReadOnlyList<HeroMatchupMatrixEntryDto> matrix =
            okResult.Value.Should().BeAssignableTo<IReadOnlyList<HeroMatchupMatrixEntryDto>>().Subject;
        matrix.Should().HaveCount(2);
        matrix[0].OpponentHeroId.Should().Be(35);
        matrix[0].WinRatePercent.Should().Be(62.5);
    }

    [Fact]
    public async Task GetHeroMatchupMatrix_InvalidHeroId_ReturnsBadRequest()
    {
        // Arrange
        DuckDbAnalyticsApiController controller = CreateController();

        // Act
        IActionResult result = await controller.GetHeroMatchupMatrix(0);

        // Assert
        result.Should().BeOfType<BadRequestObjectResult>();
    }
}
