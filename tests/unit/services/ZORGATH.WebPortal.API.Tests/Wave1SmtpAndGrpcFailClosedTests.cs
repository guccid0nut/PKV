// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Services.Distributed;
using FluentAssertions;
using Grpc.Core;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimeKit;
using Moq;
using NEXUS.Contracts.Auth.V1;
using NEXUS.Contracts.Data;
using StackExchange.Redis;
using Xunit;
using ZORGATH.WebPortal.API.Extensions;
using ZORGATH.WebPortal.API.Models.Configuration;
using ZORGATH.WebPortal.API.Services.Email;
using ZORGATH.WebPortal.API.Services.Grpc;

namespace ZORGATH.WebPortal.API.Tests;

public sealed class Wave1SmtpAndGrpcFailClosedTests
{
    [Fact]
    public async Task SmtpEmailService_RemainsDisabledByDefaultForDiscordOnlyMode_AndDeliversWhenExplicitlyOptedIn()
    {
        // protects: Discord-only authentication default (zero SMTP traffic unless SmtpEnabled=true) AND optional MailKit SMTP delivery for community hosters.
        // fails_when: SmtpEmailService connects to SMTP when SmtpEnabled=false, or fails to send RFC 5322 message when SmtpEnabled=true.
        // seam: Real SmtpEmailService + WebPortalServiceCollectionExtensions DI selector + ISmtpClient verification.

        Mock<IWebHostEnvironment> devEnv = new();
        devEnv.SetupGet(e => e.EnvironmentName).Returns(Environments.Development);

        Mock<ISmtpClient> smtpMock = new();

        OperationalConfiguration disabledCfg = new()
        {
            UIBaseUrl = "https://portal.example.com",
            JWT = new OperationalConfigurationJWT { DurationInHours = 24, Issuer = "pkv", Audience = "pkv", SigningKey = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N") },
            Email = new OperationalConfigurationEmail
            {
                ApiKey = string.Empty,
                FromEmail = "noreply@example.com",
                FromName = "PKV",
                SmtpEnabled = false,
                Provider = "Smtp",
                SmtpHost = "smtp.example.com"
            }
        };

        SmtpEmailService disabledSvc = new(
            Options.Create(disabledCfg),
            NullLogger<SmtpEmailService>.Instance,
            devEnv.Object,
            () => smtpMock.Object);

        bool skippedResult = await disabledSvc.SendEmailAddressRegistrationLink("player@example.com", "tok123");
        skippedResult.Should().BeTrue();
        smtpMock.Verify(
            c => c.ConnectAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<SecureSocketOptions>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "SmtpEnabled=false must never open an SMTP socket");

        // Now verify explicit community opt-in (SmtpEnabled=true)
        OperationalConfiguration enabledCfg = new()
        {
            UIBaseUrl = "https://portal.example.com",
            JWT = new OperationalConfigurationJWT { DurationInHours = 24, Issuer = "pkv", Audience = "pkv", SigningKey = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N") },
            Email = new OperationalConfigurationEmail
            {
                ApiKey = string.Empty,
                FromEmail = "noreply@example.com",
                FromName = "PKV",
                SmtpEnabled = true,
                Provider = "Smtp",
                SmtpHost = "smtp.example.com",
                SmtpPort = 587,
                SmtpUseSsl = true
            }
        };

        SmtpEmailService enabledSvc = new(
            Options.Create(enabledCfg),
            NullLogger<SmtpEmailService>.Instance,
            devEnv.Object,
            () => smtpMock.Object);

        bool sentResult = await enabledSvc.SendEmailAddressRegistrationLink("player@example.com", "tok456");
        sentResult.Should().BeTrue();
        smtpMock.Verify(
            c => c.ConnectAsync("smtp.example.com", 587, SecureSocketOptions.StartTls, It.IsAny<CancellationToken>()),
            Times.Once);
        smtpMock.Verify(
            c => c.SendAsync(It.Is<MimeMessage>(m => m.Subject == "Verify Email Address"), It.IsAny<CancellationToken>(), null),
            Times.Once);
    }

    [Fact]
    public async Task RedisRedlockAndGrpcAuth_FailClosedInProductionWhenRedisUnavailable()
    {
        // protects: Multi-replica Production lock exclusivity and JWT revocation integrity during transient Valkey/Redis outages.
        // fails_when: RedisRedlockService grants a split-brain local in-memory lock in Production, or GrpcAuthService treats tokens as unrevoked when Redis throws.
        // seam: Real RedisRedlockService + GrpcAuthService with Production IHostEnvironment and faulting IDatabase.

        Mock<IHostEnvironment> prodEnv = new();
        prodEnv.SetupGet(e => e.EnvironmentName).Returns(Environments.Production);

        Mock<IDatabase> faultingDb = new();
        faultingDb.Setup(d => d.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<bool>(),
                It.IsAny<When>(),
                It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Redis down"));
        faultingDb.Setup(d => d.KeyExistsAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Redis down"));

        RedisRedlockService redlock = new(
            connectionMultiplexer: null,
            database: faultingDb.Object,
            logger: NullLogger<RedisRedlockService>.Instance,
            hostEnvironment: prodEnv.Object);

        await using IDistributedLock handle = await redlock.TryAcquireLockAsync("store:wallet:user:42", TimeSpan.FromSeconds(5));
        handle.IsAcquired.Should().BeFalse("Production Redlock must fail closed when Redis is unreachable instead of granting split-brain local locks");

        string jwtSecret = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        OperationalConfiguration cfg = new()
        {
            JWT = new OperationalConfigurationJWT { DurationInHours = 24, Issuer = "pkv", Audience = "pkv", SigningKey = jwtSecret },
            Email = new OperationalConfigurationEmail { ApiKey = "NONE", FromEmail = "noreply@example.com" }
        };

        GrpcAuthService grpcAuth = new(
            new Mock<IAccountRepository>().Object,
            Options.Create(cfg),
            NullLogger<GrpcAuthService>.Instance,
            distributedCache: faultingDb.Object,
            hostEnvironment: prodEnv.Object);

        // Reflectively invoke IsTokenRevokedAsync to verify fail-closed revocation treatment on Redis outage
        var isRevokedMethod = typeof(GrpcAuthService).GetMethod(
            "IsTokenRevokedAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        bool isRevoked = await (Task<bool>)isRevokedMethod.Invoke(grpcAuth, ["jti-prod-test"])!;
        isRevoked.Should().BeTrue("Production token revocation check must fail closed (treat as revoked) when Redis throws");
    }
}
