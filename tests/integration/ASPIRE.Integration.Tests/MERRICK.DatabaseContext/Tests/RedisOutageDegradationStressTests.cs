// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Security.Cryptography;
using ASPIRE.Common.Extensions.Cache;
using FluentAssertions;
using Grpc.Core;
using KONGOR.MasterServer.Services.Security;
using Microsoft.AspNetCore.Http;
using Moq;
using NEXUS.Contracts.Auth.V1;
using NEXUS.Contracts.Security;
using SecureRemotePassword;
using ZORGATH.WebPortal.API.Services.Grpc;

namespace ASPIRE.Tests.MERRICK.DatabaseContext.Tests;

public class RedisOutageDegradationStressTests
{
    private readonly Mock<IConnectionMultiplexer> _mockMultiplexer = new();
    private readonly Mock<IDatabase> _mockRedis = new();

    public RedisOutageDegradationStressTests()
    {
        _mockMultiplexer.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(_mockRedis.Object);
    }

    private void SetupRedisStringSetThrows(Exception ex)
    {
        _mockRedis.Setup(r => r.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(),
                It.IsAny<When>(), It.IsAny<CommandFlags>()))
            .ThrowsAsync(ex);
        _mockRedis.Setup(r => r.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(),
                It.IsAny<bool>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
            .ThrowsAsync(ex);
        _mockRedis.Setup(r => r.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<Expiration>(),
                It.IsAny<ValueCondition>(), It.IsAny<CommandFlags>()))
            .ThrowsAsync(ex);
    }

    #region 4. GrpcAuthService Redis Failure & InMemory Fallback Resilience

    [Fact]
    public async Task GrpcAuthService_WhenRedisThrows_FallsBackToInMemorySrpStateAndVerifiesProof()
    {
        // Arrange
        Mock<IAccountRepository> mockAccountRepo = new();
        Role userRole = new() { ID = 1, Name = "User" };
        User user = new()
        {
            ID = 55,
            EmailAddress = "srpuser@kongor.net",
            SRPPasswordSalt = "861c37ec6d049d92cc1c67d195b414f26b572a56358272af3e9c06fcd9bfa053",
            SRPPasswordHash = "c026d36e84d41bf6abcf0dfcfba7aeefdbbbadbf7ea5f6e3c0f4f9f4a9b6c41b",
            PBKDF2PasswordHash = "pbkdf2_hash",
            Role = userRole
        };
        Account account = new()
        {
            ID = 77,
            Name = "SrpPlayer",
            User = user,
            IsMain = true
        };

        mockAccountRepo.Setup(r => r.GetByNicknameAsync("SrpPlayer", It.IsAny<CancellationToken>()))
            .ReturnsAsync(account);

        List<string> warningLogs = [];
        Mock<ILogger<GrpcAuthService>> mockLogger = new();
        mockLogger.Setup(x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Callback<LogLevel, EventId, object, Exception, Delegate>((lvl, eid, state, ex, formatter) =>
            {
                warningLogs.Add(state.ToString()!);
            });

        IOptions<OperationalConfiguration> config = Options.Create(new OperationalConfiguration
        {
            JWT = new OperationalConfigurationJWT
            {
                Issuer = "KongorTest",
                Audience = "KongorTestAudience",
                DurationInHours = 12,
                SigningKey = "SuperSecretDevSigningKey12345678901234567890"
            },
            Email = new OperationalConfigurationEmail
            {
                ApiKey = Guid.NewGuid().ToString("N"),
                FromEmail = "noreply@kongor.net",
                FromName = "Kongor"
            }
        });

        // Setup Redis to fail on both Set and GetDelete
        SetupRedisStringSetThrows(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Redis offline"));

        _mockRedis.Setup(r => r.StringGetDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Redis offline"));

        GrpcAuthService authService = new(mockAccountRepo.Object, config, mockLogger.Object, _mockRedis.Object);

        // Act 1: PreAuth (should fall back to InMemory SRP session storage)
        PreAuthRequest preAuthReq = new()
        {
            AccountName = "SrpPlayer",
            ClientVersion = "4.10.1.0",
            IpAddress = "127.0.0.1"
        };
        Mock<ServerCallContext> mockContext = new();

        PreAuthResponse preAuthRes = await authService.PreAuth(preAuthReq, mockContext.Object);

        // Assert 1
        preAuthRes.Should().NotBeNull();
        preAuthRes.SessionNonce.Should().NotBeNullOrWhiteSpace();

        // Verify structured warning on PreAuth
        warningLogs.Should().Contain(msg =>
            msg.Contains("[GrpcAuthService] Redis operation failed for cookie prefix") &&
            msg.Contains("Fallback: 'InMemory'"));

        // Act 2: Derive genuine SRP-6a client ephemeral and proof M1 against server ephemeral B
        SrpParameters parameters = SrpParameters.Create<SHA256>(
            NexusSrpConstants.SafePrimeNumber, NexusSrpConstants.MultiplicativeGroupGenerator);
        parameters.Generator = parameters.Pad(parameters.Generator);

        SrpClient srpClient = new(parameters);
        SrpEphemeral clientEphemeral = srpClient.GenerateEphemeral();
        string privateClientKey = srpClient.DerivePrivateKey(
            preAuthRes.Salt,
            account.Name,
            user.SRPPasswordHash);
        SrpSession clientSession = srpClient.DeriveSession(
            clientEphemeral.Secret,
            preAuthRes.ServerEphemeralB,
            preAuthRes.Salt,
            account.Name,
            privateClientKey);

        SrpProofRequest proofReq = new()
        {
            AccountName = "SrpPlayer",
            SessionNonce = preAuthRes.SessionNonce,
            ClientEphemeralA = clientEphemeral.Public,
            ClientProofM1 = clientSession.Proof
        };

        SrpProofResponse proofRes = await authService.VerifySrpProof(proofReq, mockContext.Object);

        // Assert 2
        proofRes.Should().NotBeNull();
        proofRes.Success.Should()
            .BeTrue("Genuine SRP-6a authentication must succeed via InMemory session fallback when Redis is down");
        proofRes.SessionToken.Should().NotBeNullOrWhiteSpace();
        proofRes.Nickname.Should().Be("SrpPlayer");
        proofRes.AccountId.Should().Be(77);
    }

    #endregion

    #region 1. SessionRepository Redis Failure & DB Fallback Resilience

    [Fact]
    public async Task SessionRepository_WhenRedisThrowsConnectionException_FallsBackToDatabaseAndResolvesAccount()
    {
        // Arrange
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        User user = new()
        {
            ID = 101,
            EmailAddress = "resilience1@kongor.net",
            SRPPasswordSalt = "salt1",
            SRPPasswordHash = "hash1",
            PBKDF2PasswordHash = "pbkdf2_1",
            Role = new Role { ID = 1, Name = "User" }
        };
        Account account = new()
        {
            ID = 201,
            Name = "KongorChampion",
            User = user,
            IsMain = true
        };
        string sessionCookie = "secret_raw_session_cookie_1234567890abcdef";
        AccountSession session = new()
        {
            ID = 301,
            Account = account,
            SessionCookie = sessionCookie,
            IsActive = true,
            IPAddress = "192.168.1.100",
            TimestampLogin = TimeProvider.System.GetUtcNow()
        };

        db.Users.Add(user);
        db.Accounts.Add(account);
        db.AccountSessions.Add(session);
        await db.SaveChangesAsync();

        List<string> logMessages = [];
        Mock<ILogger<SessionRepository>> mockLogger = new();
        mockLogger.Setup(x => x.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Callback<LogLevel, EventId, object, Exception, Delegate>((lvl, eid, state, ex, formatter) =>
            {
                logMessages.Add(state.ToString()!);
            });

        // Setup Redis to throw RedisConnectionException
        _mockRedis.Setup(r => r.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Cluster down"));

        // Re-hydration in Redis also throws to test resilience
        SetupRedisStringSetThrows(new RedisConnectionException(ConnectionFailureType.UnableToConnect,
            "Cluster down on write"));

        SessionRepository repo = new(db, _mockMultiplexer.Object, _mockRedis.Object, mockLogger.Object);

        // Act
        string? resolvedAccount = await repo.GetAccountNameForSessionCookieAsync(sessionCookie);

        // Assert
        resolvedAccount.Should().Be("KongorChampion");

        // Verify structured warning log
        logMessages.Should().Contain(msg =>
            msg.Contains("[SessionRepository] Redis operation failed for cookie prefix") &&
            msg.Contains("Fallback: 'Database'") &&
            msg.Contains("secret_r..."));

        // Verify zero cleartext leak of full cookie
        foreach (string msg in logMessages)
            msg.Should().NotContain(sessionCookie, "Cleartext session token must never leak into logs");
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task SessionRepository_WhenRedisThrowsAndDatabaseSessionIsInactive_ReturnsNullSafely()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        User user = new()
        {
            ID = 102,
            EmailAddress = "inactive@kongor.net",
            SRPPasswordSalt = "salt2",
            SRPPasswordHash = "hash2",
            PBKDF2PasswordHash = "pbkdf2_2",
            Role = new Role { ID = 1, Name = "User" }
        };
        Account account = new()
        {
            ID = 202,
            Name = "InactivePlayer",
            User = user,
            IsMain = true
        };
        string sessionCookie = "inactive_session_cookie_9999999999";
        AccountSession session = new()
        {
            ID = 302,
            Account = account,
            SessionCookie = sessionCookie,
            IsActive = false, // Inactive session!
            IPAddress = "127.0.0.1",
            TimestampLogin = TimeProvider.System.GetUtcNow()
        };

        db.Users.Add(user);
        db.Accounts.Add(account);
        db.AccountSessions.Add(session);
        await db.SaveChangesAsync();

        _mockRedis.Setup(r => r.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisTimeoutException("Redis timeout", CommandStatus.Unknown));

        SessionRepository repo = new(db, _mockMultiplexer.Object, _mockRedis.Object);

        // Act
        string? result = await repo.GetAccountNameForSessionCookieAsync(sessionCookie);

        // Assert
        result.Should().BeNull("Inactive sessions in database must not be resolved on fallback");
    }

    [Fact]
    public async Task SessionRepository_SetSessionCookie_WhenRedisTimesOut_LogsStructuredWarningWithoutCrashing()
    {
        // Arrange
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        List<string> warningLogs = [];
        Mock<ILogger<SessionRepository>> mockLogger = new();
        mockLogger.Setup(x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Callback<LogLevel, EventId, object, Exception, Delegate>((lvl, eid, state, ex, formatter) =>
            {
                warningLogs.Add(state.ToString()!);
            });

        SetupRedisStringSetThrows(new RedisTimeoutException("Write timed out", CommandStatus.Unknown));

        SessionRepository repo = new(db, _mockMultiplexer.Object, _mockRedis.Object, mockLogger.Object);
        string cookie = "set_cookie_timeout_test_123456";

        // Act & Assert
        Func<Task> act = async () => await repo.SetSessionCookieAsync(cookie, "PlayerOmega", TimeSpan.FromHours(1));
        await act.Should().NotThrowAsync();

        warningLogs.Should().ContainSingle(msg =>
            msg.Contains(
                "[SessionRepository] Redis operation failed for cookie prefix 'set_cook...' (Account: 'PlayerOmega'). Fallback: 'None'"));
    }

    [Fact]
    public async Task SessionRepository_RemoveSessionCookie_WhenRedisFails_DeactivatesSessionInDatabase()
    {
        // Arrange
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        string cookie = "remove_cookie_deactivate_test_987654";
        AccountSession session = new()
        {
            ID = 501,
            SessionCookie = cookie,
            IsActive = true,
            IPAddress = "10.0.0.1",
            TimestampLogin = TimeProvider.System.GetUtcNow()
        };
        db.AccountSessions.Add(session);
        await db.SaveChangesAsync();

        _mockRedis.Setup(r => r.KeyDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.SocketClosed, "Socket closed"));

        SessionRepository repo = new(db, _mockMultiplexer.Object, _mockRedis.Object);

        // Act
        await repo.RemoveSessionCookieAsync(cookie);

        // Assert
        AccountSession? updatedSession = await db.AccountSessions.FindAsync(501);
        updatedSession.Should().NotBeNull();
        updatedSession!.IsActive.Should().BeFalse("Session must be marked inactive in DB even if Redis delete fails");
    }

    #endregion

    #region 2. SessionContextResolver Redis Outage Stress Test

    [Fact]
    public async Task SessionContextResolver_WhenRedisFails_FallsBackToDatabaseAndResolvesFullAccountContext()
    {
        // Arrange
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        Role userRole = new() { ID = 2, Name = "VIP" };
        User user = new()
        {
            ID = 88,
            EmailAddress = "vip@kongor.net",
            SRPPasswordSalt = "saltVip",
            SRPPasswordHash = "hashVip",
            PBKDF2PasswordHash = "pbkdf2Vip",
            Role = userRole
        };
        Account account = new()
        {
            ID = 99,
            Name = "VipPlayer",
            User = user,
            IsMain = true
        };
        string sessionCookie = "context_resolver_session_cookie_777777";
        AccountSession session = new()
        {
            ID = 701,
            Account = account,
            SessionCookie = sessionCookie,
            IsActive = true,
            IPAddress = "127.0.0.1",
            TimestampLogin = TimeProvider.System.GetUtcNow()
        };

        db.Roles.Add(userRole);
        db.Users.Add(user);
        db.Accounts.Add(account);
        db.AccountSessions.Add(session);
        await db.SaveChangesAsync();

        List<string> warningLogs = [];
        Mock<ILogger<SessionContextResolver>> mockLogger = new();
        mockLogger.Setup(x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Callback<LogLevel, EventId, object, Exception, Delegate>((lvl, eid, state, ex, formatter) =>
            {
                warningLogs.Add(state.ToString()!);
            });

        // Setup Redis to fail on Get
        _mockRedis.Setup(r => r.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisServerException("CLUSTERDOWN The cluster is down"));

        // Setup Redis to fail on re-hydration
        SetupRedisStringSetThrows(new RedisServerException("CLUSTERDOWN The cluster is down on write"));

        SessionContextResolver resolver = new(_mockRedis.Object, db, mockLogger.Object);

        DefaultHttpContext httpContext = new();
        httpContext.Request.QueryString = new QueryString($"?cookie={sessionCookie}");

        // Act
        Account? resolved = await resolver.ResolveAccountAsync(httpContext);

        // Assert
        resolved.Should().NotBeNull();
        resolved!.Name.Should().Be("VipPlayer");
        resolved.User.Should().NotBeNull();
        resolved.User.Role.Should().NotBeNull();
        resolved.User.Role!.Name.Should().Be("VIP");

        // Verify HttpContext Items cache was populated
        httpContext.Items["SessionAccountName"].Should().Be("VipPlayer");

        // Verify structured warning log was emitted
        warningLogs.Should().Contain(msg =>
            msg.Contains(
                "[SessionContextResolver] Redis operation failed for cookie prefix 'context_...' (Account: '<unknown>'). Fallback: 'DatabaseContext'"));
    }

    [Fact]
    public async Task SessionContextResolver_WithNullOrEmptyCookie_ReturnsNullWithoutQueryingCache()
    {
        // Arrange
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        Mock<ILogger<SessionContextResolver>> mockLogger = new();
        SessionContextResolver resolver = new(_mockRedis.Object, db, mockLogger.Object);

        DefaultHttpContext httpContext = new(); // No cookie

        // Act
        Account? result = await resolver.ResolveAccountAsync(httpContext);

        // Assert
        result.Should().BeNull();
        _mockRedis.Verify(r => r.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()), Times.Never);
    }

    #endregion

    #region 3. ASPIRE.Common DistributedCacheExtensions Fallback & Masking

    [Fact]
    public async Task DistributedCacheExtensions_GetAccountName_WhenRedisThrows_LogsStructuredWarningAndReturnsNull()
    {
        // Arrange
        List<string> warningLogs = [];
        Mock<ILogger> mockLogger = new();
        mockLogger.Setup(x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Callback<LogLevel, EventId, object, Exception, Delegate>((lvl, eid, state, ex, formatter) =>
            {
                warningLogs.Add(state.ToString()!);
            });

        _mockRedis.Setup(r => r.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisTimeoutException("Simulated Redis timeout", CommandStatus.Unknown));

        string cookie = "raw_cookie_for_cache_ext_test_12345";

        // Act
        string? result = await _mockRedis.Object.GetAccountNameForSessionCookie(cookie, mockLogger.Object);

        // Assert
        result.Should().BeNull();
        warningLogs.Should().ContainSingle(msg =>
            msg.Contains(
                "[DistributedCacheExtensions] Redis operation failed for cookie prefix 'raw_cook...' (Account: '<unknown>'). Fallback: 'None'"));
    }

    [Fact]
    public async Task DistributedCacheExtensions_SetAccountName_WhenRedisThrows_LogsStructuredWarningWithoutCrashing()
    {
        // Arrange
        List<string> warningLogs = [];
        Mock<ILogger> mockLogger = new();
        mockLogger.Setup(x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Callback<LogLevel, EventId, object, Exception, Delegate>((lvl, eid, state, ex, formatter) =>
            {
                warningLogs.Add(state.ToString()!);
            });

        SetupRedisStringSetThrows(new RedisConnectionException(ConnectionFailureType.SocketFailure, "Socket dead"));

        string cookie = "set_cache_ext_failure_cookie";

        // Act & Assert
        Func<Task> act = async () =>
            await _mockRedis.Object.SetAccountNameForSessionCookie(cookie, "PlayerZ", mockLogger.Object);
        await act.Should().NotThrowAsync();

        warningLogs.Should().ContainSingle(msg =>
            msg.Contains(
                "[DistributedCacheExtensions] Redis operation failed for cookie prefix 'set_cach...' (Account: 'PlayerZ'). Fallback: 'None'"));
    }

    #endregion

    #region 5. SecurityMasking Edge Cases & Masking Invariants

    [Theory]
    [InlineData(null, "<none>")]
    [InlineData("", "<none>")]
    [InlineData("   ", "<none>")]
    [InlineData("\t\n", "<none>")]
    [InlineData("a", "a...")]
    [InlineData("12345678", "12345678...")]
    [InlineData("123456789", "12345678...")]
    [InlineData("my_secret_token_value_xyz", "my_secre...")]
    public void SecurityMasking_StandardCases_MatchesExpectedMask(string? input, string expected)
    {
        SecurityMasking.MaskToken(input).Should().Be(expected);
        global::ASPIRE.Common.Security.SecurityMasking.MaskToken(input).Should().Be(expected);
    }

    [Fact]
    public void SecurityMasking_ExtremePrefixLengths_BehavesDeterministically()
    {
        string token = "SuperSecretPlayerSessionToken1234567890";

        // prefixLength 0
        SecurityMasking.MaskToken(token, 0).Should().Be("...");

        // negative prefixLength
        SecurityMasking.MaskToken(token, -5).Should().Be("...");

        // large prefixLength
        SecurityMasking.MaskToken(token, 100).Should().Be($"{token}...");

        // 100,000 character token
        string largeToken = new('x', 100_000);
        string maskedLarge = SecurityMasking.MaskToken(largeToken, 8);
        maskedLarge.Should().Be("xxxxxxxx...");
        maskedLarge.Length.Should().Be(11);
    }

    #endregion
}
