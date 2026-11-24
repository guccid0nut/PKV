// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using KONGOR.MasterServer.Configuration.Rating;
using KONGOR.MasterServer.Services.Rating;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace KONGOR.MasterServer.Tests.Services;

public sealed class OpenSkillRatingServiceTests
{
    private readonly OpenSkillRatingService _service;
    private readonly OpenSkillConfiguration _config;

    public OpenSkillRatingServiceTests()
    {
        _config = new OpenSkillConfiguration();
        IOptions<OpenSkillConfiguration> options = Microsoft.Extensions.Options.Options.Create(_config);
        _service = new OpenSkillRatingService(options, NullLogger<OpenSkillRatingService>.Instance);
    }

    [Fact]
    public void ComputeConservativePSR_DefaultUncalibratedState_ReturnsExact1500()
    {
        // Arrange & Act
        double psr = _service.ComputeConservativePSR(_config.DefaultMu, _config.DefaultSigma);

        // Assert
        psr.Should().Be(1500.0);
    }

    [Fact]
    public void CalculateMatchRatings_SymmetricTeams_WinnersGainAndLosersLoseExpectedPSR()
    {
        // Arrange
        List<OpenSkillPlayerRating> players = [];

        // Team 1 (Winners)
        for (int i = 1; i <= 5; i++)
        {
            players.Add(new OpenSkillPlayerRating
            {
                AccountId = i,
                AccountName = $"Legion_{i}",
                Team = 1,
                GroupNumber = i, // All solo
                GroupSize = 1,
                MuBefore = 25.0,
                SigmaBefore = 25.0 / 3.0,
                PsrBefore = 1500.0,
                MatchesPlayed = 0
            });
        }

        // Team 2 (Losers)
        for (int i = 6; i <= 10; i++)
        {
            players.Add(new OpenSkillPlayerRating
            {
                AccountId = i,
                AccountName = $"Hellbourne_{i}",
                Team = 2,
                GroupNumber = i, // All solo
                GroupSize = 1,
                MuBefore = 25.0,
                SigmaBefore = 25.0 / 3.0,
                PsrBefore = 1500.0,
                MatchesPlayed = 0
            });
        }

        OpenSkillMatchContext context = new()
        {
            MatchId = 99901,
            WinningTeam = 1,
            Players = players,
            DurationSeconds = 1800
        };

        // Act
        IReadOnlyList<OpenSkillPlayerResult> results = _service.CalculateMatchRatings(context);

        // Assert
        results.Should().HaveCount(10);

        var winners = results.Where(r => r.Team == 1).ToList();
        var losers = results.Where(r => r.Team == 2).ToList();

        // Winners gain PSR and mu
        foreach (var winner in winners)
        {
            winner.DeltaMu.Should().BePositive();
            winner.DeltaPsr.Should().BePositive();
            winner.SigmaAfter.Should().BeLessThan(winner.SigmaBefore); // Uncertainty collapses
            winner.PsrAfter.Should().BeGreaterThan(1500.0);
        }

        // Losers lose mu
        foreach (var loser in losers)
        {
            loser.DeltaMu.Should().BeNegative();
            loser.SigmaAfter.Should().BeLessThan(loser.SigmaBefore); // Uncertainty collapses
            loser.MuAfter.Should().BeLessThan(25.0);
        }

        // Symmetry check
        winners[0].DeltaMu.Should().BeApproximately(-losers[0].DeltaMu, 0.001);
    }

    [Fact]
    public void CalculateMatchRatings_RapidCalibration_HighUncertaintyPlayerMovesFasterThanCalibratedPlayer()
    {
        // Arrange: Team 1 has one new player (high uncertainty) and four calibrated players (low uncertainty)
        List<OpenSkillPlayerRating> players =
        [
            new OpenSkillPlayerRating
            {
                AccountId = 1,
                AccountName = "Newbie",
                Team = 1,
                GroupNumber = 1,
                GroupSize = 1,
                MuBefore = 25.0,
                SigmaBefore = 8.3333, // High uncertainty
                PsrBefore = 1500.0,
                MatchesPlayed = 0
            }
        ];

        for (int i = 2; i <= 5; i++)
        {
            players.Add(new OpenSkillPlayerRating
            {
                AccountId = i,
                AccountName = $"Veteran_{i}",
                Team = 1,
                GroupNumber = i,
                GroupSize = 1,
                MuBefore = 25.0,
                SigmaBefore = 2.0, // Low uncertainty (converged)
                PsrBefore = 1880.0,
                MatchesPlayed = 50
            });
        }

        for (int i = 6; i <= 10; i++)
        {
            players.Add(new OpenSkillPlayerRating
            {
                AccountId = i,
                AccountName = $"Opponent_{i}",
                Team = 2,
                GroupNumber = i,
                GroupSize = 1,
                MuBefore = 25.0,
                SigmaBefore = 2.0,
                PsrBefore = 1880.0,
                MatchesPlayed = 50
            });
        }

        OpenSkillMatchContext context = new()
        {
            MatchId = 99902,
            WinningTeam = 1,
            Players = players,
            DurationSeconds = 1800
        };

        // Act
        IReadOnlyList<OpenSkillPlayerResult> results = _service.CalculateMatchRatings(context);

        // Assert
        var newbieResult = results.Single(r => r.AccountId == 1);
        var veteranResult = results.First(r => r.AccountId == 2);

        // Newbie's delta mu should be substantially higher than veteran's delta mu (proportional to sigma^2)
        newbieResult.DeltaMu.Should().BeGreaterThan(veteranResult.DeltaMu * 5.0);
    }

    [Fact]
    public void CalculateMatchRatings_PartyInflationDamping_CoordinatedStackGainIsDiscountedVsSoloQueue()
    {
        // Scenario A: 5-Stack on Team 1 vs 5 Solos on Team 2. Team 1 wins.
        List<OpenSkillPlayerRating> stackPlayers = [];
        for (int i = 1; i <= 5; i++)
        {
            stackPlayers.Add(new OpenSkillPlayerRating
            {
                AccountId = i,
                AccountName = $"Stack_{i}",
                Team = 1,
                GroupNumber = 100, // Coordinated 5-stack
                GroupSize = 5,
                MuBefore = 25.0,
                SigmaBefore = 4.0,
                PsrBefore = 1760.0,
                MatchesPlayed = 20
            });
        }
        for (int i = 6; i <= 10; i++)
        {
            stackPlayers.Add(new OpenSkillPlayerRating
            {
                AccountId = i,
                AccountName = $"Solo_{i}",
                Team = 2,
                GroupNumber = i, // Solo queue
                GroupSize = 1,
                MuBefore = 25.0,
                SigmaBefore = 4.0,
                PsrBefore = 1760.0,
                MatchesPlayed = 20
            });
        }

        OpenSkillMatchContext stackContext = new()
        {
            MatchId = 99903,
            WinningTeam = 1,
            Players = stackPlayers,
            DurationSeconds = 1800
        };

        // Scenario B: 5 Solos on Team 1 vs 5 Solos on Team 2. Team 1 wins.
        List<OpenSkillPlayerRating> soloPlayers = [];
        for (int i = 1; i <= 5; i++)
        {
            soloPlayers.Add(new OpenSkillPlayerRating
            {
                AccountId = i,
                AccountName = $"SoloA_{i}",
                Team = 1,
                GroupNumber = i, // Solo queue
                GroupSize = 1,
                MuBefore = 25.0,
                SigmaBefore = 4.0,
                PsrBefore = 1760.0,
                MatchesPlayed = 20
            });
        }
        for (int i = 6; i <= 10; i++)
        {
            soloPlayers.Add(new OpenSkillPlayerRating
            {
                AccountId = i,
                AccountName = $"SoloB_{i}",
                Team = 2,
                GroupNumber = i + 10, // Solo queue
                GroupSize = 1,
                MuBefore = 25.0,
                SigmaBefore = 4.0,
                PsrBefore = 1760.0,
                MatchesPlayed = 20
            });
        }

        OpenSkillMatchContext soloContext = new()
        {
            MatchId = 99904,
            WinningTeam = 1,
            Players = soloPlayers,
            DurationSeconds = 1800
        };

        // Act
        IReadOnlyList<OpenSkillPlayerResult> stackResults = _service.CalculateMatchRatings(stackContext);
        IReadOnlyList<OpenSkillPlayerResult> soloResults = _service.CalculateMatchRatings(soloContext);

        // Assert: Coordinated 5-stack win delta should be damped compared to pure solo queue win delta
        double stackWinnerDeltaMu = stackResults.First(r => r.Team == 1).DeltaMu;
        double soloWinnerDeltaMu = soloResults.First(r => r.Team == 1).DeltaMu;

        stackWinnerDeltaMu.Should().BeLessThan(soloWinnerDeltaMu);
    }

    [Fact]
    public void CalculateMatchRatings_SmurfAcceleration_HighKdrMvpAcceleratesCalibration()
    {
        // Arrange
        List<OpenSkillPlayerRating> players =
        [
            new OpenSkillPlayerRating
            {
                AccountId = 1,
                AccountName = "SmurfCandidate",
                Team = 1,
                GroupNumber = 1,
                GroupSize = 1,
                MuBefore = 25.0,
                SigmaBefore = 8.3333,
                PsrBefore = 1500.0,
                MatchesPlayed = 3,
                IsMvp = true,
                Kills = 15,
                Deaths = 1,
                Assists = 8
            },
            new OpenSkillPlayerRating
            {
                AccountId = 2,
                AccountName = "NormalNewbie",
                Team = 1,
                GroupNumber = 2,
                GroupSize = 1,
                MuBefore = 25.0,
                SigmaBefore = 8.3333,
                PsrBefore = 1500.0,
                MatchesPlayed = 3,
                IsMvp = false,
                Kills = 3,
                Deaths = 5,
                Assists = 4
            }
        ];

        for (int i = 3; i <= 5; i++)
        {
            players.Add(new OpenSkillPlayerRating
            {
                AccountId = i,
                AccountName = $"Teammate_{i}",
                Team = 1,
                GroupNumber = i,
                GroupSize = 1,
                MuBefore = 25.0,
                SigmaBefore = 4.0,
                PsrBefore = 1760.0,
                MatchesPlayed = 20
            });
        }

        for (int i = 6; i <= 10; i++)
        {
            players.Add(new OpenSkillPlayerRating
            {
                AccountId = i,
                AccountName = $"Opponent_{i}",
                Team = 2,
                GroupNumber = i,
                GroupSize = 1,
                MuBefore = 25.0,
                SigmaBefore = 4.0,
                PsrBefore = 1760.0,
                MatchesPlayed = 20
            });
        }

        OpenSkillMatchContext context = new()
        {
            MatchId = 99905,
            WinningTeam = 1,
            Players = players,
            DurationSeconds = 1800
        };

        // Act
        IReadOnlyList<OpenSkillPlayerResult> results = _service.CalculateMatchRatings(context);

        // Assert
        var smurfResult = results.Single(r => r.AccountId == 1);
        var normalResult = results.Single(r => r.AccountId == 2);

        // Smurf should receive acceleration multiplier
        smurfResult.DeltaMu.Should().BeGreaterThan(normalResult.DeltaMu);
        smurfResult.DeltaMu.Should().BeApproximately(normalResult.DeltaMu * _config.SmurfAccelerationMultiplier, 0.05);
    }

    [Theory]
    [InlineData(1250.0, 0)]
    [InlineData(1500.0, 0)]
    [InlineData(1650.0, 20)]
    [InlineData(1800.0, 50)]
    [InlineData(1950.0, 100)]
    public void OpenSkillRatingService_DeriveInitialStateFromLegacyRating_ProducesExactMatchingConservativePSR(double legacyRating, int matchesPlayed)
    {
        // Act
        (double derivedMu, double derivedSigma) = _service.DeriveInitialStateFromLegacyRating(legacyRating, matchesPlayed);
        double computedPsr = _service.ComputeConservativePSR(derivedMu, derivedSigma);

        // Assert: Computed PSR should be within 0.1 PSR points of the legacy rating
        computedPsr.Should().BeApproximately(legacyRating, 0.1);
    }
}
