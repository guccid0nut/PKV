// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using Moq;

namespace ASPIRE.Tests.MERRICK.DatabaseContext.Tests;

public class SessionRepositoryLoggingTests
{
    private readonly Mock<ILogger<SessionRepository>> _mockLogger = new();
    private readonly Mock<IConnectionMultiplexer> _mockMultiplexer = new();
    private readonly Mock<IDatabase> _mockRedisDatabase = new();

    public SessionRepositoryLoggingTests()
    {
        _mockMultiplexer.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(_mockRedisDatabase.Object);
    }

    [Fact]
    public async Task GetAccountNameForSessionCookieAsync_WhenRedisThrows_LogsWarningAndReturnsNull()
    {
        // Arrange
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        _mockRedisDatabase
            .Setup(r => r.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect,
                "Simulated Redis network failure"));

        SessionRepository repo = new(db, _mockMultiplexer.Object, _mockRedisDatabase.Object, _mockLogger.Object);

        // Act
        string? result = await repo.GetAccountNameForSessionCookieAsync("test_session_cookie_12345");

        // Assert
        result.Should().BeNull();
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) =>
                    v.ToString()!.Contains("[SessionRepository] Redis operation failed for cookie prefix")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task SetSessionCookieAsync_WhenRedisThrows_LogsWarningWithoutThrowing()
    {
        // Arrange
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        _mockRedisDatabase
            .Setup(r => r.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(),
                It.IsAny<bool>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisTimeoutException("Simulated Redis write timeout", CommandStatus.Unknown));
        _mockRedisDatabase
            .Setup(r => r.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<Expiration>(),
                It.IsAny<ValueCondition>(), It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisTimeoutException("Simulated Redis write timeout", CommandStatus.Unknown));

        SessionRepository repo = new(db, _mockMultiplexer.Object, _mockRedisDatabase.Object, _mockLogger.Object);

        // Act & Assert
        Func<Task> act = async () => await repo.SetSessionCookieAsync("cookie_abc", "PlayerX", TimeSpan.FromHours(1));
        await act.Should().NotThrowAsync();

        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) =>
                    v.ToString()!.Contains("[SessionRepository] Redis operation failed for cookie prefix")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Trait("Category", "Manual")]
    [Fact]
    public async Task RemoveSessionCookieAsync_WhenRedisThrows_LogsWarningWithoutThrowing()
    {
        if (!NexusTestEnvironment.IsManualTestEnabled) return;
        // Arrange
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        _mockRedisDatabase
            .Setup(r => r.KeyDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.SocketClosed, "Socket closed"));

        SessionRepository repo = new(db, _mockMultiplexer.Object, _mockRedisDatabase.Object, _mockLogger.Object);

        // Act & Assert
        Func<Task> act = async () => await repo.RemoveSessionCookieAsync("cookie_xyz");
        await act.Should().NotThrowAsync();

        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) =>
                    v.ToString()!.Contains("[SessionRepository] Redis operation failed for cookie prefix")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task TokenOperations_IssueAndValidate_SucceedsInDatabase()
    {
        // Arrange
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        User user = new()
        {
            ID = 10,
            EmailAddress = "tokenuser@kongor.net",
            SRPPasswordSalt = "salt123",
            SRPPasswordHash = "hash123",
            PBKDF2PasswordHash = "pbkdf2hash",
            Role = new Role { ID = 1, Name = "User" }
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        SessionRepository repo = new(db, _mockMultiplexer.Object, _mockRedisDatabase.Object, _mockLogger.Object);

        // Act
        Token token = await repo.IssueTokenAsync(10, TokenPurpose.EmailAddressVerification, TimeSpan.FromHours(24));
        Token? validated = await repo.ValidateTokenAsync(token.Value.ToString(), TokenPurpose.EmailAddressVerification);

        // Assert
        token.Should().NotBeNull();
        validated.Should().NotBeNull();
        validated!.EmailAddress.Should().Be("tokenuser@kongor.net");

        // Invalidate
        await repo.InvalidateTokenAsync(token.Value.ToString());
        Token? afterInvalidation =
            await repo.ValidateTokenAsync(token.Value.ToString(), TokenPurpose.EmailAddressVerification);
        afterInvalidation.Should().BeNull();
    }

    [Fact]
    public async Task SessionRepositoryLogging_CleanExpiredTokensAsync_DeletesOnlyConsumedTokens()
    {
        // Arrange
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        Token unconsumedToken = new()
        {
            Purpose = TokenPurpose.EmailAddressVerification,
            EmailAddress = "active@kongor.net",
            Data = "1",
            Value = Guid.NewGuid(),
            TimestampCreated = TimeProvider.System.GetUtcNow(),
            TimestampConsumed = null
        };
        Token consumedToken = new()
        {
            Purpose = TokenPurpose.EmailAddressVerification,
            EmailAddress = "expired@kongor.net",
            Data = "2",
            Value = Guid.NewGuid(),
            TimestampCreated = TimeProvider.System.GetUtcNow().AddDays(-2),
            TimestampConsumed = TimeProvider.System.GetUtcNow().AddDays(-1)
        };

        db.Tokens.AddRange(unconsumedToken, consumedToken);
        await db.SaveChangesAsync();

        SessionRepository repo = new(db, _mockMultiplexer.Object, _mockRedisDatabase.Object, _mockLogger.Object);

        // Act
        await repo.CleanExpiredTokensAsync();

        // Assert
        List<Token> remaining = await db.Tokens.ToListAsync();
        remaining.Should().ContainSingle();
        remaining[0].Value.Should().Be(unconsumedToken.Value);
    }

    [Fact]
    public async Task CleanExpiredTokensAsync_WhenNoConsumedTokensExist_CompletesWithoutThrowing()
    {
        // Arrange
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        SessionRepository repo = new(db, _mockMultiplexer.Object, _mockRedisDatabase.Object, _mockLogger.Object);

        // Act & Assert
        Func<Task> act = async () => await repo.CleanExpiredTokensAsync();
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task TokenOperations_WithInjectedTimeProvider_UsesDeterministicTimestamps()
    {
        // Arrange
        using MerrickContext db = TestDbContextFactory.CreateInMemoryContext();
        User user = new()
        {
            ID = 20,
            EmailAddress = "timetest@kongor.net",
            SRPPasswordSalt = "salt123",
            SRPPasswordHash = "hash123",
            PBKDF2PasswordHash = "pbkdf2hash",
            Role = new Role { ID = 1, Name = "User" }
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        DateTimeOffset fixedNow = new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);
        DeterministicTestTimeProvider testTime = new(fixedNow);
        SessionRepository repo = new(db, _mockMultiplexer.Object, _mockRedisDatabase.Object, _mockLogger.Object,
            testTime);

        // Act
        Token token = await repo.IssueTokenAsync(20, TokenPurpose.EmailAddressVerification, TimeSpan.FromHours(1));

        // Assert
        token.TimestampCreated.Should().Be(fixedNow);

        DateTimeOffset consumedTime = fixedNow.AddMinutes(30);
        testTime.AdvanceTo(consumedTime);
        await repo.InvalidateTokenAsync(token.Value.ToString());

        Token? invalidatedToken = await repo.GetTokenAsync(token.Value.ToString());
        invalidatedToken.Should().NotBeNull();
        invalidatedToken!.TimestampConsumed.Should().Be(consumedTime);
    }

    private sealed class DeterministicTestTimeProvider(DateTimeOffset initialTime) : TimeProvider
    {
        private DateTimeOffset _currentTime = initialTime;

        public override DateTimeOffset GetUtcNow()
        {
            return _currentTime;
        }

        public void AdvanceTo(DateTimeOffset newTime)
        {
            _currentTime = newTime;
        }
    }
}
