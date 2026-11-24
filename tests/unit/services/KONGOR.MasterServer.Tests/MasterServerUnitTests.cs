// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using KONGOR.MasterServer.Models.RequestResponse.SRP;
using SecureRemotePassword;
using Xunit;

namespace KONGOR.MasterServer.Tests;

public sealed class MasterServerUnitTests
{
    [Fact]
    public void MasterServerUnit_MasterServerAssembly_ShouldLoadSuccessfully()
    {
        typeof(MasterServerUnitTests).Assembly.Should().NotBeNull();
    }

    [Fact]
    public void PrecomputedSrpParameters_StandardAndPadded_AreInitializedWithValidConstants()
    {
        PrecomputedSrpParameters.Standard.Should().NotBeNull();
        PrecomputedSrpParameters.Standard.Prime.Should().NotBeNull();
        PrecomputedSrpParameters.Standard.Generator.Should().NotBeNull();
        PrecomputedSrpParameters.Standard.Multiplier.Should().NotBeNull();

        PrecomputedSrpParameters.PaddedGenerator.Should().NotBeNull();
        PrecomputedSrpParameters.PaddedGenerator.Prime.Should().Be(PrecomputedSrpParameters.Standard.Prime);
        PrecomputedSrpParameters.PaddedGenerator.Generator.Should().NotBeNull();
    }

    [Fact]
    public void SRPAuthenticationSessionDataStageOne_ComputesVerifierAndEphemeral_Successfully()
    {
        string salt = SRPAuthenticationSessionDataStageOne.GenerateSRPSessionSalt();
        string login = "testuser";
        string passwordHash = "d41d8cd98f00b204e9800998ecf8427e";

        string verifier = SRPAuthenticationSessionDataStageOne.ComputeVerifier(salt, login, passwordHash);
        verifier.Should().NotBeNullOrWhiteSpace();

        (string privateKey, string publicKey) = SRPAuthenticationSessionDataStageOne.ComputeServerEphemeral(verifier);
        privateKey.Should().NotBeNullOrWhiteSpace();
        publicKey.Should().NotBeNullOrWhiteSpace();
    }
}

