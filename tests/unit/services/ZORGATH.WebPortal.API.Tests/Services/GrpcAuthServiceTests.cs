// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Grpc.Core;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Utility;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NEXUS.Contracts.Auth.V1;
using NEXUS.Contracts.Data;
using NEXUS.Contracts.Security;
using SecureRemotePassword;
using Xunit;
using ZORGATH.WebPortal.API.Models.Configuration;
using ZORGATH.WebPortal.API.Services.Grpc;

namespace ZORGATH.WebPortal.API.Tests.Services;

public sealed class GrpcAuthServiceTests
{
    private readonly Mock<IAccountRepository> _mockAccountRepository;
    private readonly IOptions<OperationalConfiguration> _options;
    private readonly GrpcAuthService _service;

    public GrpcAuthServiceTests()
    {
        _mockAccountRepository = new Mock<IAccountRepository>();
        _options = Options.Create(new OperationalConfiguration
        {
            JWT = new OperationalConfigurationJWT
            {
                SigningKey = "SuperSecretKeyForTestingJwtTokenGeneration1234567890!",
                Issuer = "KongorTestIssuer",
                Audience = "KongorTestAudience",
                DurationInHours = 1
            },
            Email = new OperationalConfigurationEmail
            {
                ApiKey = "test-api-key",
                FromEmail = "noreply@kongor.net",
                FromName = "Kongor Auth"
            }
        });

        _service = new GrpcAuthService(
            _mockAccountRepository.Object,
            _options,
            NullLogger<GrpcAuthService>.Instance,
            null,
            TimeProvider.System);
    }

    private static (User user, Account account, string plainPassword) CreateTestUserAndAccount(
        string accountName = "TestHero", string password = "SecretPassword123!")
    {
        string salt = SrpInteger.RandomInteger(256 / 2).ToHex();
        string innerHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(password)));
        string srpPasswordHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(salt + innerHash)));

        User user = new()
        {
            ID = 42,
            EmailAddress = "hero@kongor.net",
            Role = new Role { ID = 2, Name = "User" },
            SRPPasswordSalt = salt,
            SRPPasswordHash = srpPasswordHash
        };

        Account account = new()
        {
            ID = 101,
            Name = accountName,
            User = user,
            IsMain = true
        };

        return (user, account, password);
    }

    [Fact]
    public async Task PreAuth_ExistingAccount_ReturnsRealSaltAndEphemeral()
    {
        // Arrange
        (User user, Account account, string _) = CreateTestUserAndAccount("PlayerOne");
        _mockAccountRepository.Setup(r => r.GetByNicknameAsync("PlayerOne", It.IsAny<CancellationToken>()))
            .ReturnsAsync(account);

        PreAuthRequest request = new() { AccountName = "PlayerOne" };
        ServerCallContext callContext = new Mock<ServerCallContext>().Object;

        // Act
        PreAuthResponse response = await _service.PreAuth(request, callContext);

        // Assert
        response.Should().NotBeNull();
        response.Salt.Should().Be(user.SRPPasswordSalt);
        response.ServerEphemeralB.Should().NotBeNullOrWhiteSpace();
        response.SessionNonce.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task PreAuth_NonExistentAccount_ReturnsDummyResponseWithoutCaching()
    {
        // Arrange
        _mockAccountRepository.Setup(r => r.GetByNicknameAsync("GhostAccount", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Account?)null);

        PreAuthRequest request = new() { AccountName = "GhostAccount" };
        ServerCallContext callContext = new Mock<ServerCallContext>().Object;

        // Act
        PreAuthResponse response = await _service.PreAuth(request, callContext);

        // Assert
        response.Should().NotBeNull();
        response.Salt.Should().NotBeNullOrWhiteSpace();
        response.ServerEphemeralB.Should().NotBeNullOrWhiteSpace();
        response.SessionNonce.Should().NotBeNullOrWhiteSpace();

        // Attempting to verify this dummy session should immediately fail closed
        SrpProofRequest proofRequest = new()
        {
            AccountName = "GhostAccount",
            SessionNonce = response.SessionNonce,
            ClientEphemeralA = "1234567890abcdef",
            ClientProofM1 = "abcdef1234567890"
        };

        SrpProofResponse proofResponse = await _service.VerifySrpProof(proofRequest, callContext);
        proofResponse.Success.Should().BeFalse();
    }

    [Fact]
    public async Task VerifySrpProof_ValidProof_SucceedsAndIssuesJwtToken()
    {
        // Arrange
        (User user, Account account, string plainPassword) = CreateTestUserAndAccount("ValidPlayer");
        _mockAccountRepository.Setup(r => r.GetByNicknameAsync("ValidPlayer", It.IsAny<CancellationToken>()))
            .ReturnsAsync(account);

        ServerCallContext callContext = new Mock<ServerCallContext>().Object;

        // 1. Perform PreAuth
        PreAuthResponse preAuthResponse =
            await _service.PreAuth(new PreAuthRequest { AccountName = "ValidPlayer" }, callContext);

        // 2. Perform Client SRP-6a derivation
        SrpParameters parameters = SrpParameters.Create<SHA256>(
            NexusSrpConstants.SafePrimeNumber, NexusSrpConstants.MultiplicativeGroupGenerator);
        parameters.Generator = parameters.Pad(parameters.Generator);

        SrpClient client = new(parameters);
        SrpEphemeral clientEphemeral = client.GenerateEphemeral();

        string privateClientKey = client.DerivePrivateKey(
            preAuthResponse.Salt,
            account.Name,
            user.SRPPasswordHash);

        SrpSession clientSession = client.DeriveSession(
            clientEphemeral.Secret,
            preAuthResponse.ServerEphemeralB,
            preAuthResponse.Salt,
            account.Name,
            privateClientKey);

        // 3. Send proof
        SrpProofRequest proofRequest = new()
        {
            AccountName = "ValidPlayer",
            SessionNonce = preAuthResponse.SessionNonce,
            ClientEphemeralA = clientEphemeral.Public,
            ClientProofM1 = clientSession.Proof
        };

        // Act
        SrpProofResponse proofResponse = await _service.VerifySrpProof(proofRequest, callContext);

        // Assert
        proofResponse.Should().NotBeNull();
        proofResponse.Success.Should().BeTrue();
        proofResponse.ServerProofM2.Should().NotBeNullOrWhiteSpace();
        proofResponse.SessionToken.Should().NotBeNullOrWhiteSpace();
        proofResponse.AccountId.Should().Be(101u);
        proofResponse.Nickname.Should().Be("ValidPlayer");
    }

    [Fact]
    public async Task VerifySrpProof_InvalidProof_FailsAndDoesNotIssueToken()
    {
        // Arrange
        (User user, Account account, string _) = CreateTestUserAndAccount("PlayerTwo");
        _mockAccountRepository.Setup(r => r.GetByNicknameAsync("PlayerTwo", It.IsAny<CancellationToken>()))
            .ReturnsAsync(account);

        ServerCallContext callContext = new Mock<ServerCallContext>().Object;

        // 1. Perform PreAuth
        PreAuthResponse preAuthResponse =
            await _service.PreAuth(new PreAuthRequest { AccountName = "PlayerTwo" }, callContext);

        // 2. Client sends forged / invalid proof
        SrpProofRequest proofRequest = new()
        {
            AccountName = "PlayerTwo",
            SessionNonce = preAuthResponse.SessionNonce,
            ClientEphemeralA = "00112233445566778899aabbccddeeff",
            ClientProofM1 = "deadbeefdeadbeefdeadbeefdeadbeef"
        };

        // Act
        SrpProofResponse proofResponse = await _service.VerifySrpProof(proofRequest, callContext);

        // Assert
        proofResponse.Should().NotBeNull();
        proofResponse.Success.Should().BeFalse();
        proofResponse.SessionToken.Should().BeEmpty();
    }

    [Fact]
    public async Task VerifySrpProof_ReplayAttack_FailsOnSecondAttempt()
    {
        // Arrange
        (User user, Account account, string _) = CreateTestUserAndAccount("ReplayTarget");
        _mockAccountRepository.Setup(r => r.GetByNicknameAsync("ReplayTarget", It.IsAny<CancellationToken>()))
            .ReturnsAsync(account);

        ServerCallContext callContext = new Mock<ServerCallContext>().Object;

        PreAuthResponse preAuthResponse =
            await _service.PreAuth(new PreAuthRequest { AccountName = "ReplayTarget" }, callContext);

        SrpParameters parameters = SrpParameters.Create<SHA256>(
            NexusSrpConstants.SafePrimeNumber, NexusSrpConstants.MultiplicativeGroupGenerator);
        parameters.Generator = parameters.Pad(parameters.Generator);

        SrpClient client = new(parameters);
        SrpEphemeral clientEphemeral = client.GenerateEphemeral();

        string privateClientKey = client.DerivePrivateKey(
            preAuthResponse.Salt,
            account.Name,
            user.SRPPasswordHash);

        SrpSession clientSession = client.DeriveSession(
            clientEphemeral.Secret,
            preAuthResponse.ServerEphemeralB,
            preAuthResponse.Salt,
            account.Name,
            privateClientKey);

        SrpProofRequest proofRequest = new()
        {
            AccountName = "ReplayTarget",
            SessionNonce = preAuthResponse.SessionNonce,
            ClientEphemeralA = clientEphemeral.Public,
            ClientProofM1 = clientSession.Proof
        };

        // Act 1 - First attempt should succeed
        SrpProofResponse firstResponse = await _service.VerifySrpProof(proofRequest, callContext);
        firstResponse.Success.Should().BeTrue();

        // Act 2 - Replay the identical request (nonce was consumed)
        SrpProofResponse secondResponse = await _service.VerifySrpProof(proofRequest, callContext);

        // Assert - Second attempt must fail due to single-use nonce consumption
        secondResponse.Success.Should().BeFalse();
        secondResponse.SessionToken.Should().BeEmpty();
    }

    [Fact]
    public async Task VerifySrpProof_MissingRequiredParameters_ReturnsFailureImmediately()
    {
        // Arrange
        ServerCallContext callContext = new Mock<ServerCallContext>().Object;
        SrpProofRequest request = new()
        {
            AccountName = "",
            SessionNonce = "",
            ClientEphemeralA = "",
            ClientProofM1 = ""
        };

        // Act
        SrpProofResponse response = await _service.VerifySrpProof(request, callContext);

        // Assert
        response.Should().NotBeNull();
        response.Success.Should().BeFalse();
    }
}
