// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace KONGOR.MasterServer.Models.RequestResponse.SRP;

/// <summary>
///     Exposes the constants, properties, and methods required for Secure Remote Password protocol authentication, stage
///     two.
/// </summary>
public class SRPAuthenticationSessionDataStageTwo
{
    public SRPAuthenticationSessionDataStageTwo(SRPAuthenticationSessionDataStageOne stageOneData, string clientProof)
    {
        ClientProof = clientProof;

        SrpServer server = new(PrecomputedSrpParameters.PaddedGenerator);

        try
        {
            SrpSession serverSession = server.DeriveSession
            (
                stageOneData.ServerPrivateEphemeral,
                stageOneData.ClientPublicEphemeral,
                stageOneData.SessionSalt,
                stageOneData.LoginIdentifier,
                stageOneData.Verifier,
                clientProof
            );

            ServerProof = serverSession.Proof;
        }

        catch
        {
            ServerProof = null;
        }
    }

    /// <summary>
    ///     M1 : the client's proof; the server should verify this value and use it to compute M2 (the server's proof)
    /// </summary>
    public string ClientProof { get; init; }

    /// <summary>
    ///     M2 : the server's proof; the client should verify this value and use it to complete the SRP challenge exchange
    /// </summary>
    public string? ServerProof { get; init; }
}
