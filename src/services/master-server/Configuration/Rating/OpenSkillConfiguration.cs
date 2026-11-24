// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace KONGOR.MasterServer.Configuration.Rating;

public class OpenSkillConfiguration
{
    public const string SectionName = "OpenSkill";

    /// <summary>
    ///     Gets or sets whether the OpenSkill Bayesian rating engine is enabled.
    ///     If false, falls back to legacy rating.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     Initial prior mean skill (\mu_0). Default is 25.0.
    /// </summary>
    public double DefaultMu { get; set; } = 25.0;

    /// <summary>
    ///     Initial uncertainty standard deviation (\sigma_0 = \mu_0 / 3). Default is 8.333333333333334.
    /// </summary>
    public double DefaultSigma { get; set; } = 25.0 / 3.0;

    /// <summary>
    ///     Performance variance per player (\beta = \sigma_0 / 2). Default is 4.166666666666667.
    /// </summary>
    public double Beta { get; set; } = 25.0 / 6.0;

    /// <summary>
    ///     Additive dynamics factor / volatility per match (\tau = \sigma_0 / 100). Default is 0.08333333333333333.
    /// </summary>
    public double Tau { get; set; } = 25.0 / 300.0;

    /// <summary>
    ///     Conservative rating multiplier (z). Default is 3.0 for 99.7% lower confidence bound.
    /// </summary>
    public double ZFactor { get; set; } = 3.0;

    /// <summary>
    ///     Base anchor for the HoN wire PSR scale. Default is 1500.0.
    /// </summary>
    public double BasePSR { get; set; } = 1500.0;

    /// <summary>
    ///     Multiplier mapping conservative skill (R = \mu - 3\sigma) to HoN PSR points.
    ///     Default is 20.0 (~200 PSR points per standard deviation).
    /// </summary>
    public double Alpha { get; set; } = 20.0;

    /// <summary>
    ///     Minimum uncertainty threshold to prevent rating stagnation. Default is 1.0.
    /// </summary>
    public double MinSigma { get; set; } = 1.0;

    /// <summary>
    ///     Damping factor applied to rating gains for coordinated stacks (duos, trios, 5-stacks)
    ///     competing against solo-queue players. Default is 0.25.
    /// </summary>
    public double PartyDampingFactor { get; set; } = 0.25;

    /// <summary>
    ///     Number of matches designated as rapid calibration. Default is 10.
    /// </summary>
    public int RapidCalibrationMatches { get; set; } = 10;

    /// <summary>
    ///     Multiplier applied to \Delta \mu when a calibration-phase player displays dominant MVP/KDR metrics (smurf acceleration).
    ///     Default is 1.35.
    /// </summary>
    public double SmurfAccelerationMultiplier { get; set; } = 1.35;
}
