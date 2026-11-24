// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace KONGOR.MasterServer.Services.Rating;

public sealed record OpenSkillPlayerRating
{
    public required int AccountId { get; init; }
    public required string AccountName { get; init; }
    public required int Team { get; init; } // 1 = Legion, 2 = Hellbourne
    public int GroupNumber { get; init; } = 1;
    public int GroupSize { get; init; } = 1;
    public double MuBefore { get; init; }
    public double SigmaBefore { get; init; }
    public double PsrBefore { get; init; }
    public int Kills { get; init; }
    public int Deaths { get; init; }
    public int Assists { get; init; }
    public int HeroDamage { get; init; }
    public int TowerDamage { get; init; }
    public bool IsMvp { get; init; }
    public int MatchesPlayed { get; init; }
}

public sealed record OpenSkillPlayerResult
{
    public required int AccountId { get; init; }
    public required string AccountName { get; init; }
    public required int Team { get; init; }
    public required double MuBefore { get; init; }
    public required double SigmaBefore { get; init; }
    public required double PsrBefore { get; init; }
    public required double MuAfter { get; init; }
    public required double SigmaAfter { get; init; }
    public required double PsrAfter { get; init; }
    public required double DeltaMu { get; init; }
    public required double DeltaSigma { get; init; }
    public required double DeltaPsr { get; init; }
}

public sealed record OpenSkillMatchContext
{
    public required int MatchId { get; init; }
    public required int WinningTeam { get; init; } // 1 = Legion, 2 = Hellbourne, 0 = Draw
    public required IReadOnlyList<OpenSkillPlayerRating> Players { get; init; }
    public int DurationSeconds { get; init; } = 1800;
}

public interface IOpenSkillRatingService
{
    /// <summary>
    ///     Calculates Weng-Lin / Plackett-Luce Bayesian rating updates for all players in a match.
    /// </summary>
    IReadOnlyList<OpenSkillPlayerResult> CalculateMatchRatings(OpenSkillMatchContext context);

    /// <summary>
    ///     Computes the conservative skill rating mapped to the HoN 1500 PSR scale.
    ///     R = BasePSR + Alpha * (\mu - z * \sigma).
    /// </summary>
    double ComputeConservativePSR(double mu, double sigma);

    /// <summary>
    ///     Derives a calibrated Bayesian (\mu, \sigma) state from an existing legacy rating.
    /// </summary>
    (double Mu, double Sigma) DeriveInitialStateFromLegacyRating(double legacyRating, int matchesPlayed);
}
