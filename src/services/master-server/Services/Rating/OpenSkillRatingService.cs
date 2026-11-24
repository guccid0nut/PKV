// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using KONGOR.MasterServer.Configuration.Rating;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KONGOR.MasterServer.Services.Rating;

public sealed class OpenSkillRatingService(
    IOptions<OpenSkillConfiguration>? options = null,
    ILogger<OpenSkillRatingService>? logger = null) : IOpenSkillRatingService
{
    private readonly OpenSkillConfiguration _config = options?.Value ?? new OpenSkillConfiguration();
    private static readonly double Sqrt2 = Math.Sqrt(2.0);
    private static readonly double InvSqrt2Pi = 1.0 / Math.Sqrt(2.0 * Math.PI);

    public IReadOnlyList<OpenSkillPlayerResult> CalculateMatchRatings(OpenSkillMatchContext context)
    {
        if (context.Players.Count < 2 || context.WinningTeam is not (1 or 2))
        {
            logger?.LogWarning("[OpenSkill] Match {MatchId} has invalid parameters (Players: {Count}, Winner: {Winner}). Skipping rating updates.",
                context.MatchId, context.Players.Count, context.WinningTeam);

            return [.. context.Players.Select(p => new OpenSkillPlayerResult
            {
                AccountId = p.AccountId,
                AccountName = p.AccountName,
                Team = p.Team,
                MuBefore = p.MuBefore,
                SigmaBefore = p.SigmaBefore,
                PsrBefore = p.PsrBefore,
                MuAfter = p.MuBefore,
                SigmaAfter = p.SigmaBefore,
                PsrAfter = p.PsrBefore,
                DeltaMu = 0.0,
                DeltaSigma = 0.0,
                DeltaPsr = 0.0
            })];
        }

        List<OpenSkillPlayerRating> team1 = [.. context.Players.Where(p => p.Team == 1)];
        List<OpenSkillPlayerRating> team2 = [.. context.Players.Where(p => p.Team == 2)];

        if (team1.Count == 0 || team2.Count == 0)
        {
            logger?.LogWarning("[OpenSkill] Match {MatchId} is missing players on Team 1 or Team 2.", context.MatchId);
            return [];
        }

        double tauSq = _config.Tau * _config.Tau;
        double betaSq = _config.Beta * _config.Beta;

        // Step 1: Add volatility / dynamics factor (\tau^2) to each player's variance
        Dictionary<int, (double Mu, double SigmaSq)> activeStates = [];
        foreach (OpenSkillPlayerRating player in context.Players)
        {
            double currentMu = player.MuBefore > 0 ? player.MuBefore : _config.DefaultMu;
            double currentSigma = player.SigmaBefore > 0 ? player.SigmaBefore : _config.DefaultSigma;
            double adjustedSigmaSq = (currentSigma * currentSigma) + tauSq;
            activeStates[player.AccountId] = (currentMu, adjustedSigmaSq);
        }

        // Step 2: Compute Team Gaussian Distributions
        double muTeam1 = team1.Sum(p => activeStates[p.AccountId].Mu);
        double c1Sq = team1.Sum(p => activeStates[p.AccountId].SigmaSq + betaSq);

        double muTeam2 = team2.Sum(p => activeStates[p.AccountId].Mu);
        double c2Sq = team2.Sum(p => activeStates[p.AccountId].SigmaSq + betaSq);

        double cSq = c1Sq + c2Sq;
        double c = Math.Sqrt(cSq);

        // Step 3: Compute Party Synergy and Inflation Damping
        double synergyTeam1 = CalculateTeamSynergy(team1);
        double synergyTeam2 = CalculateTeamSynergy(team2);
        double deltaSynergy = synergyTeam1 - synergyTeam2;

        // Team 1 perspective expectation with party synergy offset
        double team1EffectiveMu = muTeam1 + (deltaSynergy * _config.Beta);
        double d1 = (team1EffectiveMu - muTeam2) / c;

        // Perspective from winning team
        bool team1Won = context.WinningTeam == 1;
        double t = team1Won ? d1 : -d1;

        double v = CalculateV(t);
        double w = CalculateW(t, v);

        // Compute party inflation damping factors
        double gammaWinner = 1.0;
        double gammaLoser = 1.0;

        if (team1Won)
        {
            if (deltaSynergy > 0.05)
            {
                // Stack won against solo/less coordinated queue -> damp gains and soften losses
                gammaWinner = 1.0 / (1.0 + (_config.PartyDampingFactor * deltaSynergy));
                gammaLoser = gammaWinner;
            }
            else if (deltaSynergy < -0.05)
            {
                // Solo underdogs won against stack -> upset bonus
                gammaWinner = 1.0 + (_config.PartyDampingFactor * Math.Abs(deltaSynergy));
                gammaLoser = gammaWinner;
            }
        }
        else
        {
            // Team 2 won
            double deltaSynergyTeam2 = synergyTeam2 - synergyTeam1;
            if (deltaSynergyTeam2 > 0.05)
            {
                // Stack won against solo queue -> damp gains
                gammaWinner = 1.0 / (1.0 + (_config.PartyDampingFactor * deltaSynergyTeam2));
                gammaLoser = gammaWinner;
            }
            else if (deltaSynergyTeam2 < -0.05)
            {
                // Solo underdogs won against stack -> upset bonus
                gammaWinner = 1.0 + (_config.PartyDampingFactor * Math.Abs(deltaSynergyTeam2));
                gammaLoser = gammaWinner;
            }
        }

        double minSigmaSq = _config.MinSigma * _config.MinSigma;
        List<OpenSkillPlayerResult> results = [];

        foreach (OpenSkillPlayerRating player in context.Players)
        {
            (double playerMu, double playerSigmaSq) = activeStates[player.AccountId];
            bool isWinner = player.Team == context.WinningTeam;
            double gamma = isWinner ? gammaWinner : gammaLoser;

            // Rapid Calibration & Smurf Acceleration
            bool isCalibration = player.MatchesPlayed < _config.RapidCalibrationMatches;
            bool isSmurf = isCalibration && isWinner &&
                           (player.IsMvp || player.Kills >= 10 || (player.Deaths > 0 && (double)player.Kills / player.Deaths >= 3.5));

            double accelerationMultiplier = isSmurf ? _config.SmurfAccelerationMultiplier : 1.0;

            // Compute marginal Delta Mu
            double sign = isWinner ? 1.0 : -1.0;
            double deltaMu = sign * accelerationMultiplier * gamma * (playerSigmaSq / c) * v;
            double newMu = playerMu + deltaMu;

            // Compute marginal Delta Sigma^2
            double deltaSigmaSq = -(playerSigmaSq * playerSigmaSq / cSq) * w;
            double newSigmaSq = Math.Max(minSigmaSq, playerSigmaSq + deltaSigmaSq);
            double newSigma = Math.Sqrt(newSigmaSq);

            // Map conservative skill R = mu - z * sigma to 1500 PSR scale
            double newPsr = ComputeConservativePSR(newMu, newSigma);
            double deltaPsr = Math.Round(newPsr - player.PsrBefore, 3);

            results.Add(new OpenSkillPlayerResult
            {
                AccountId = player.AccountId,
                AccountName = player.AccountName,
                Team = player.Team,
                MuBefore = player.MuBefore,
                SigmaBefore = player.SigmaBefore,
                PsrBefore = player.PsrBefore,
                MuAfter = Math.Round(newMu, 4),
                SigmaAfter = Math.Round(newSigma, 4),
                PsrAfter = Math.Round(newPsr, 3),
                DeltaMu = Math.Round(deltaMu, 4),
                DeltaSigma = Math.Round(newSigma - player.SigmaBefore, 4),
                DeltaPsr = deltaPsr
            });
        }

        return results;
    }

    public double ComputeConservativePSR(double mu, double sigma)
    {
        // Wire compatibility mapping: PSR = BasePSR + Alpha * (\mu - z * \sigma)
        // With default mu_0 = 25.0 and sigma_0 = 8.3333333333: (25 - 3 * 8.3333333333) = 0.0 -> PSR = 1500.000
        double conservativeScore = mu - (_config.ZFactor * sigma);
        double psr = _config.BasePSR + (_config.Alpha * conservativeScore);
        return Math.Round(psr, 3);
    }

    public (double Mu, double Sigma) DeriveInitialStateFromLegacyRating(double legacyRating, int matchesPlayed)
    {
        // For accounts with existing ratings, estimate their uncertainty based on matches played
        // and back-calculate mu such that ComputeConservativePSR(mu, sigma) == legacyRating
        double sigma = _config.DefaultSigma;
        if (matchesPlayed > 0)
        {
            double confidenceFactor = 1.0 / Math.Sqrt(1.0 + (matchesPlayed / 5.0));
            sigma = Math.Max(_config.MinSigma, _config.DefaultSigma * confidenceFactor);
        }

        double conservativeScore = (legacyRating - _config.BasePSR) / _config.Alpha;
        double mu = conservativeScore + (_config.ZFactor * sigma);

        return (Math.Round(mu, 4), Math.Round(sigma, 4));
    }

    private static double CalculateTeamSynergy(IEnumerable<OpenSkillPlayerRating> teamPlayers)
    {
        double synergy = 0.0;
        var groupSizes = teamPlayers
            .GroupBy(p => p.GroupNumber)
            .Select(g => g.Count());

        foreach (int size in groupSizes)
        {
            synergy += size switch
            {
                >= 5 => 1.0,
                4 => 0.70,
                3 => 0.45,
                2 => 0.20,
                _ => 0.0
            };
        }

        return Math.Min(1.0, synergy);
    }

    #region Numerical & Gaussian Probability Functions

    /// <summary>
    ///     Standard normal probability density function: \phi(x) = (1 / \sqrt{2\pi}) * e^(-x^2 / 2).
    /// </summary>
    public static double Pdf(double x)
    {
        return InvSqrt2Pi * Math.Exp(-0.5 * x * x);
    }

    /// <summary>
    ///     Standard normal cumulative distribution function: \Phi(x) = 0.5 * (1 + erf(x / \sqrt{2})).
    /// </summary>
    public static double Cdf(double x)
    {
        return 0.5 * (1.0 + Erf(x / Sqrt2));
    }

    /// <summary>
    ///     Marginal mean update function v(t) = \phi(t) / \Phi(t).
    /// </summary>
    public static double CalculateV(double t)
    {
        if (t < -10.0)
            return -t - (1.0 / t);

        if (t > 10.0)
            return 0.0;

        double cdf = Cdf(t);
        if (cdf < 1e-15)
            return -t;

        return Pdf(t) / cdf;
    }

    /// <summary>
    ///     Marginal variance update function w(t) = v(t) * (v(t) + t).
    /// </summary>
    public static double CalculateW(double t, double v)
    {
        if (t < -10.0)
            return 1.0;

        if (t > 10.0)
            return 0.0;

        double w = v * (v + t);
        return Math.Clamp(w, 0.0, 1.0);
    }

    /// <summary>
    ///     Numerical approximation of Gauss error function erf(x) via Abramowitz and Stegun (formula 7.1.26).
    ///     Maximum error: 1.5e-7.
    /// </summary>
    public static double Erf(double x)
    {
        const double a1 = 0.254829592;
        const double a2 = -0.284496736;
        const double a3 = 1.421413741;
        const double a4 = -1.453152027;
        const double a5 = 1.061405429;
        const double p = 0.3275911;

        int sign = x < 0 ? -1 : 1;
        double absX = Math.Abs(x);

        double polyT = 1.0 / (1.0 + (p * absX));
        double y = 1.0 - (((((((((a5 * polyT) + a4) * polyT) + a3) * polyT) + a2) * polyT) + a1) * polyT * Math.Exp(-absX * absX));

        return sign * y;
    }

    #endregion
}
