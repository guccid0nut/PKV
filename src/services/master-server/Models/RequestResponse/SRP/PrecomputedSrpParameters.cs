// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace KONGOR.MasterServer.Models.RequestResponse.SRP;

/// <summary>
///     Provides precomputed, cached immutable SRP parameter sets for 2048-bit HoN SRP-6a authentication.
///     Eliminates repeated BigInteger parsing of the 2048-bit hex prime and SHA-256 multiplier calculation per login attempt.
/// </summary>
public static class PrecomputedSrpParameters
{
    /// <summary>
    ///     Cached standard parameters for client proof, verifier generation, and server ephemeral computation.
    /// </summary>
    public static readonly SrpParameters Standard = SrpParameters.Create<SHA256>(
        SRPAuthenticationSessionDataStageOne.SafePrimeNumber,
        SRPAuthenticationSessionDataStageOne.MultiplicativeGroupGenerator);

    /// <summary>
    ///     Cached stage-two parameters with padded generator (g) required by HoN's RFC5054 / cocagne/pysrp compatibility.
    /// </summary>
    public static readonly SrpParameters PaddedGenerator;

    static PrecomputedSrpParameters()
    {
        PaddedGenerator = SrpParameters.Create<SHA256>(
            SRPAuthenticationSessionDataStageOne.SafePrimeNumber,
            SRPAuthenticationSessionDataStageOne.MultiplicativeGroupGenerator);
        PaddedGenerator.Generator = PaddedGenerator.Pad(PaddedGenerator.Generator);
    }
}
