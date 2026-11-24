// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Grpc.Challenger.Tests.Infrastructure;
using Grpc.Core;
using MERRICK.DatabaseContext.Constants;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Utility;
using MERRICK.DatabaseContext.Persistence;
using MERRICK.DatabaseContext.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NEXUS.Contracts.Auth.V1;
using NEXUS.Contracts.Domain;
using NEXUS.Contracts.Security;
using SecureRemotePassword;
using Xunit;
using ZORGATH.WebPortal.API.Models.Configuration;
using ZORGATH.WebPortal.API.Services.Grpc;

namespace Grpc.Challenger.Tests;

public sealed class AuthServiceChallengerTests
{
    private static MerrickContext CreateInMemoryDb(string name)
    {
        MerrickContext ctx = TestDbContextFactory.CreateInMemoryContext(name);
        ctx.Database.EnsureCreated();
        return ctx;
    }

    private static IOptions<OperationalConfiguration> CreateDefaultConfig()
    {
        return Options.Create(new OperationalConfiguration
        {
            JWT = new OperationalConfigurationJWT
            {
                SigningKey = "CHALLENGER_SECRET_KEY_SUPER_SECURE_AT_LEAST_32_BYTES_LONG_2026!",
                Issuer = "https://portal.api.kongor.net",
                Audience = "https://kongor.net",
                DurationInHours = 12
            },
            Email = new OperationalConfigurationEmail
            {
                ApiKey = Guid.NewGuid().ToString("N"),
                FromEmail = "noreply@kongor.net",
                FromName = "Kongor"
            }
        });
    }

    private static (string saltHex, string hashHex) CreateValidSrpCredentials(string seed)
    {
        string saltHex = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("salt:" + seed)));
        string hashHex = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("hash:" + seed)));
        return (saltHex, hashHex);
    }

    private static (string clientPublicA, string clientProofM1) DeriveClientSrpProof(
        string accountName,
        string saltHex,
        string passwordHashHex,
        string serverEphemeralB)
    {
        SrpParameters parameters = SrpParameters.Create<SHA256>(
            NexusSrpConstants.SafePrimeNumber, NexusSrpConstants.MultiplicativeGroupGenerator);
        parameters.Generator = parameters.Pad(parameters.Generator);

        SrpClient srpClient = new(parameters);
        SrpEphemeral clientEphemeral = srpClient.GenerateEphemeral();
        string privateClientKey = srpClient.DerivePrivateKey(saltHex, accountName, passwordHashHex);
        SrpSession clientSession = srpClient.DeriveSession(
            clientEphemeral.Secret,
            serverEphemeralB,
            saltHex,
            accountName,
            privateClientKey);

        return (clientEphemeral.Public, clientSession.Proof);
    }

    [Fact]
    public async Task PreAuth_WithNonExistentAccount_ReturnsDefaultSaltWithoutCrashing()
    {
        using MerrickContext db = CreateInMemoryDb("PreAuth_NonExistent");
        GrpcAuthService service = new(new AccountRepository(db), CreateDefaultConfig(),
            NullLogger<GrpcAuthService>.Instance);
        ServerCallContext ctx = new AdversarialServerCallContext();

        PreAuthRequest req = new()
        {
            AccountName = "GhostUser_" + Guid.NewGuid().ToString("N"),
            ClientVersion = "4.10.1.0",
            IpAddress = "192.168.1.100"
        };

        PreAuthResponse response = await service.PreAuth(req, ctx);

        response.Should().NotBeNull();
        response.Salt.Should().NotBeNullOrWhiteSpace();
        response.ServerEphemeralB.Should().NotBeNullOrWhiteSpace();
        response.SessionNonce.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!@#$%^&*()_+-=[]{}|;':\",./<>?")]
    [InlineData("'; DROP TABLE Accounts; --")]
    public async Task PreAuth_WithMalformedAccountNames_HandlesGracefully(string maliciousName)
    {
        using MerrickContext db = CreateInMemoryDb("PreAuth_Malformed_" + Guid.NewGuid().ToString("N"));
        GrpcAuthService service = new(new AccountRepository(db), CreateDefaultConfig(),
            NullLogger<GrpcAuthService>.Instance);
        ServerCallContext ctx = new AdversarialServerCallContext();

        PreAuthRequest req = new()
        {
            AccountName = maliciousName,
            ClientVersion = "99.99.99",
            IpAddress = "255.255.255.255"
        };

        PreAuthResponse response = await service.PreAuth(req, ctx);

        response.Should().NotBeNull();
        response.Salt.Should().NotBeNullOrWhiteSpace();
        response.SessionNonce.Should().NotBeNullOrWhiteSpace();
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task VerifySrpProof_WithNonExistentAccount_ReturnsSuccessFalse()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        using MerrickContext db = CreateInMemoryDb("VerifySrp_NonExistent");
        GrpcAuthService service = new(new AccountRepository(db), CreateDefaultConfig(),
            NullLogger<GrpcAuthService>.Instance);
        ServerCallContext ctx = new AdversarialServerCallContext();

        SrpProofRequest req = new()
        {
            AccountName = "NonExistentUser",
            ClientEphemeralA = "abcdef123456",
            ClientProofM1 = "proof_m1",
            SessionNonce = "nonce_123"
        };

        SrpProofResponse response = await service.VerifySrpProof(req, ctx);

        response.Should().NotBeNull();
        response.Success.Should().BeFalse();
        response.SessionToken.Should().BeEmpty();
    }

    [Theory]
    [InlineData("ephemeral_a", "proof_m1")]
    [InlineData("xyz_non_hex", "0123456789abcdef")]
    [InlineData("0123456789abcdef", "xyz_non_hex")]
    public async Task VerifySrpProof_ExistingAccountWithNonHexOrSyntheticProof_RejectsFailClosed(
        string clientEphemeralA,
        string clientProofM1)
    {
        // protects: Zero-password SRP authentication bypass prevention for real existing accounts
        // fails_when: GrpcAuthService mints synthetic_proof_m2 when ClientProofM1/ClientEphemeralA are non-hex
        // seam: Real AccountRepository + GrpcAuthService PreAuth -> VerifySrpProof state flow
        using MerrickContext db = CreateInMemoryDb("VerifySrp_SyntheticReject_" + Guid.NewGuid().ToString("N"));
        Role adminRole = await db.Roles.FirstAsync(r => r.ID == 1);
        (string saltHex, string hashHex) = CreateValidSrpCredentials("AdminTarget");

        User adminUser = new()
        {
            ID = 900,
            EmailAddress = "admin@kongor.net",
            Role = adminRole,
            SRPPasswordSalt = saltHex,
            SRPPasswordHash = hashHex,
            PBKDF2PasswordHash = "pbkdf2_admin"
        };
        Account adminAccount = new()
        {
            ID = 901,
            Name = "AdminTarget",
            User = adminUser,
            IsMain = true,
            Type = AccountType.Normal
        };
        await db.Users.AddAsync(adminUser);
        await db.Accounts.AddAsync(adminAccount);
        await db.SaveChangesAsync();

        GrpcAuthService service = new(new AccountRepository(db), CreateDefaultConfig(),
            NullLogger<GrpcAuthService>.Instance);
        ServerCallContext ctx = new AdversarialServerCallContext();

        PreAuthResponse pre = await service.PreAuth(new PreAuthRequest
        {
            AccountName = "AdminTarget",
            ClientVersion = "4.10.1.0",
            IpAddress = "10.0.0.9"
        }, ctx);

        SrpProofResponse forgedAttempt = await service.VerifySrpProof(new SrpProofRequest
        {
            AccountName = "AdminTarget",
            SessionNonce = pre.SessionNonce,
            ClientEphemeralA = clientEphemeralA,
            ClientProofM1 = clientProofM1
        }, ctx);

        forgedAttempt.Success.Should().BeFalse("Non-hex or synthetic proofs must never mint JWTs");
        forgedAttempt.SessionToken.Should().BeEmpty();
        forgedAttempt.ServerProofM2.Should().BeEmpty();
    }

    [Fact]
    public async Task ExchangeDiscordCode_WhenUpstreamUnconfigured_RejectsFailClosed()
    {
        // protects: Unauthenticated AccountId=1 impersonation via stub Discord OAuth endpoint
        // fails_when: ExchangeDiscordCode returns Success=true or non-empty SessionToken without Discord API check
        // seam: GrpcAuthService.ExchangeDiscordCode
        using MerrickContext db = CreateInMemoryDb("Discord_FailClosed_" + Guid.NewGuid().ToString("N"));
        GrpcAuthService service = new(new AccountRepository(db), CreateDefaultConfig(),
            NullLogger<GrpcAuthService>.Instance);
        ServerCallContext ctx = new AdversarialServerCallContext();

        DiscordExchangeResponse response = await service.ExchangeDiscordCode(new DiscordExchangeRequest
        {
            AuthorizationCode = "any_arbitrary_string",
            RedirectUri = "https://kongor.net/callback",
            CodeVerifier = "verifier"
        }, ctx);

        response.Success.Should().BeFalse();
        response.SessionToken.Should().BeEmpty();
        response.AccountId.Should().Be(0u);
    }

    [Fact]
    public async Task InvalidateSession_RevokesIssuedJwtAndEmitsDualRoleClaims()
    {
        // protects: Real JWT revocation on logout/invalidation and ZORGATH "user_role" + ClaimTypes.Role parity
        // fails_when: InvalidateSession is a no-op or issued JWT lacks Claims.UserRole
        // seam: GrpcAuthService PreAuth -> VerifySrpProof -> ValidateSessionToken -> InvalidateSession
        using MerrickContext db = CreateInMemoryDb("Invalidate_Revoke_" + Guid.NewGuid().ToString("N"));
        Role adminRole = await db.Roles.FirstAsync(r => r.ID == 1);
        (string saltHex, string hashHex) = CreateValidSrpCredentials("RevokeHero");

        User user = new()
        {
            ID = 950,
            EmailAddress = "revoke@kongor.net",
            Role = adminRole,
            SRPPasswordSalt = saltHex,
            SRPPasswordHash = hashHex,
            PBKDF2PasswordHash = "pbkdf2_revoke"
        };
        Account account = new()
        {
            ID = 951,
            Name = "RevokeHero",
            User = user,
            IsMain = true,
            Type = AccountType.Normal
        };
        await db.Users.AddAsync(user);
        await db.Accounts.AddAsync(account);
        await db.SaveChangesAsync();

        GrpcAuthService service = new(new AccountRepository(db), CreateDefaultConfig(),
            NullLogger<GrpcAuthService>.Instance);
        ServerCallContext ctx = new AdversarialServerCallContext();

        PreAuthResponse pre = await service.PreAuth(new PreAuthRequest { AccountName = "RevokeHero" }, ctx);
        (string clientPublicA, string clientProofM1) =
            DeriveClientSrpProof("RevokeHero", pre.Salt, hashHex, pre.ServerEphemeralB);

        SrpProofResponse srp = await service.VerifySrpProof(new SrpProofRequest
        {
            AccountName = "RevokeHero",
            SessionNonce = pre.SessionNonce,
            ClientEphemeralA = clientPublicA,
            ClientProofM1 = clientProofM1
        }, ctx);

        srp.Success.Should().BeTrue();
        JwtSecurityToken parsedJwt = new JwtSecurityTokenHandler().ReadJwtToken(srp.SessionToken);
        parsedJwt.Claims.Should().Contain(c => c.Type == Claims.UserRole && c.Value == adminRole.Name);
        parsedJwt.Claims.Should().Contain(c => c.Type == ClaimTypes.Role && c.Value == adminRole.Name);

        ValidateTokenResponse beforeRevoke = await service.ValidateSessionToken(
            new ValidateTokenRequest { SessionToken = srp.SessionToken, RequiredPermission = adminRole.Name }, ctx);
        beforeRevoke.IsValid.Should().BeTrue();

        InvalidateSessionResponse invalidate = await service.InvalidateSession(
            new InvalidateSessionRequest { SessionToken = srp.SessionToken }, ctx);
        invalidate.Success.Should().BeTrue();

        ValidateTokenResponse afterRevoke = await service.ValidateSessionToken(
            new ValidateTokenRequest { SessionToken = srp.SessionToken }, ctx);
        afterRevoke.IsValid.Should().BeFalse("Revoked JWT must be rejected immediately by ValidateSessionToken");
    }

    [Fact]
    public async Task ValidateSessionToken_WithTamperedOrMalformedTokens_ReturnsInvalid()
    {
        using MerrickContext db = CreateInMemoryDb("ValidateToken_Adversarial");
        GrpcAuthService service = new(new AccountRepository(db), CreateDefaultConfig(),
            NullLogger<GrpcAuthService>.Instance);
        ServerCallContext ctx = new AdversarialServerCallContext();

        string[] malformedTokens =
        [
            "",
            "   ",
            "not.a.valid.jwt.token",
            "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.invalidpayload.invalidsig",
            "eyJhbGciOiJub25lIiwidHlwIjoiSldUIn0.eyJzdWIiOiIxMjM0NTY3ODkwIiwibmFtZSI6IkpvaG4gRG9lIiwiaWF0IjoxNTE2MjM5MDIyfQ.", // None algorithm attack
            Guid.NewGuid().ToString()
        ];

        foreach (string token in malformedTokens)
        {
            ValidateTokenRequest req = new()
            {
                SessionToken = token,
                RequiredPermission = "Admin"
            };

            ValidateTokenResponse response = await service.ValidateSessionToken(req, ctx);

            response.Should().NotBeNull();
            response.IsValid.Should().BeFalse();
        }
    }

    [Fact]
    public async Task ValidateSessionToken_WithPermissionCheck_EnforcesRoleCorrectly()
    {
        using MerrickContext db = CreateInMemoryDb("ValidateToken_Roles");
        IOptions<OperationalConfiguration> config = CreateDefaultConfig();
        GrpcAuthService service = new(new AccountRepository(db), config, NullLogger<GrpcAuthService>.Instance);
        ServerCallContext ctx = new AdversarialServerCallContext();

        // Generate token with User role
        List<Claim> claims =
        [
            new(JwtRegisteredClaimNames.Sub, "NormalPlayer", ClaimValueTypes.String),
            new(JwtRegisteredClaimNames.Email, "player@kongor.net", ClaimValueTypes.Email),
            new("account_id", "400", ClaimValueTypes.String),
            new(ClaimTypes.Role, "User")
        ];

        JwtSecurityToken token = new(
            config.Value.JWT.Issuer,
            config.Value.JWT.Audience,
            claims,
            expires: TimeProvider.System.GetUtcNow().UtcDateTime.AddHours(2),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config.Value.JWT.SigningKey)),
                SecurityAlgorithms.HmacSha256)
        );

        string rawToken = new JwtSecurityTokenHandler().WriteToken(token);

        // 1. Check with matching role
        ValidateTokenResponse resMatch = await service.ValidateSessionToken(new ValidateTokenRequest
        {
            SessionToken = rawToken,
            RequiredPermission = "User"
        }, ctx);

        resMatch.IsValid.Should().BeTrue();
        resMatch.AccountId.Should().Be(400);
        resMatch.Nickname.Should().Be("NormalPlayer");

        // 2. Check with mismatching role
        ValidateTokenResponse resAdmin = await service.ValidateSessionToken(new ValidateTokenRequest
        {
            SessionToken = rawToken,
            RequiredPermission = "Administrator"
        }, ctx);

        resAdmin.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task AuthService_HighConcurrencyStress_MaintainsStability()
    {
        string dbName = "AuthService_Concurrency_" + Guid.NewGuid().ToString("N");
        using MerrickContext db = CreateInMemoryDb(dbName);

        Role role = await db.Roles.FirstAsync(r => r.ID == 2);
        Dictionary<int, (string saltHex, string hashHex)> credentialsByIndex = new();

        for (int i = 1; i <= 12; i++)
        {
            (string saltHex, string hashHex) creds = CreateValidSrpCredentials($"stress_{i}");
            credentialsByIndex[i] = creds;

            User user = new()
            {
                ID = i,
                EmailAddress = $"stress_{i}@kongor.net",
                Role = role,
                SRPPasswordSalt = creds.saltHex,
                SRPPasswordHash = creds.hashHex,
                PBKDF2PasswordHash = "pbkdf2_" + i
            };
            await db.Users.AddAsync(user);

            Account account = new()
            {
                ID = i * 10,
                Name = $"StressUser_{i}",
                User = user,
                IsMain = true,
                Type = AccountType.Normal
            };
            await db.Accounts.AddAsync(account);
        }

        await db.SaveChangesAsync();

        IOptions<OperationalConfiguration> config = CreateDefaultConfig();
        ServerCallContext ctx = new AdversarialServerCallContext();

        List<Task> tasks = [];
        for (int i = 0; i < 24; i++)
        {
            int index = i % 12 + 1;
            (string expectedSalt, string passwordHashHex) = credentialsByIndex[index];
            tasks.Add(Task.Run(async () =>
            {
                using MerrickContext scopedDb = CreateInMemoryDb(dbName);
                GrpcAuthService service = new(new AccountRepository(scopedDb), config,
                    NullLogger<GrpcAuthService>.Instance);

                string accountName = $"StressUser_{index}";
                PreAuthResponse pre = await service.PreAuth(new PreAuthRequest
                {
                    AccountName = accountName,
                    ClientVersion = "4.10.1.0",
                    IpAddress = "10.0.0.1"
                }, ctx);

                pre.Salt.Should().Be(expectedSalt);

                (string clientPublicA, string clientProofM1) =
                    DeriveClientSrpProof(accountName, pre.Salt, passwordHashHex, pre.ServerEphemeralB);

                SrpProofResponse srp = await service.VerifySrpProof(new SrpProofRequest
                {
                    AccountName = accountName,
                    ClientEphemeralA = clientPublicA,
                    ClientProofM1 = clientProofM1,
                    SessionNonce = pre.SessionNonce
                }, ctx);

                srp.Success.Should().BeTrue();
                srp.AccountId.Should().Be((uint)(index * 10));

                ValidateTokenResponse val = await service.ValidateSessionToken(new ValidateTokenRequest
                {
                    SessionToken = srp.SessionToken
                }, ctx);

                val.IsValid.Should().BeTrue();
                val.AccountId.Should().Be((uint)(index * 10));
            }));
        }

        await Task.WhenAll(tasks);
    }
}
