// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using KONGOR.MasterServer.Extensions.Cache;
using KONGOR.MasterServer.Services.Security;
using KONGOR.MasterServer.Tests.Infrastructure;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Persistence;
using MERRICK.DatabaseContext.Repositories;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Moq;
using StackExchange.Redis;
using Xunit;

namespace KONGOR.MasterServer.Tests.Services;

public sealed class SessionNegativeCachingTests
{
    private static (Mock<IDatabase> MockDb, ConcurrentDictionary<string, string> Store) CreateMockRedisStore()
    {
        ConcurrentDictionary<string, string> store = new();
        Mock<IDatabase> mock = new();

        mock.Setup(d => d.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisKey key, CommandFlags _) =>
            {
                return store.TryGetValue(key.ToString(), out string? val) ? (RedisValue)val : RedisValue.Null;
            });

        // Setup the standard 6-parameter IDatabase.StringSetAsync overload
        mock.Setup(d => d.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<bool>(),
                It.IsAny<When>(),
                It.IsAny<CommandFlags>()))
            .Callback<RedisKey, RedisValue, TimeSpan?, bool, When, CommandFlags>((key, val, _, _, _, _) =>
            {
                store[key.ToString()] = val.ToString()!;
            })
            .ReturnsAsync(true);

        // Setup the modern 5-parameter IDatabaseAsync.StringSetAsync overload with Expiration
        mock.Setup(d => d.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<Expiration>(),
                It.IsAny<ValueCondition>(),
                It.IsAny<CommandFlags>()))
            .Callback<RedisKey, RedisValue, Expiration, ValueCondition, CommandFlags>((key, val, _, _, _) =>
            {
                store[key.ToString()] = val.ToString()!;
            })
            .ReturnsAsync(true);

        return (mock, store);
    }

    [Fact]
    public async Task ValidateAccountSessionCookie_WhenCookieIsInvalid_StoresNegativeSentinelInRedis()
    {
        // Arrange
        (Mock<IDatabase> mockDb, ConcurrentDictionary<string, string> store) = CreateMockRedisStore();
        using MerrickContext dbContext = TestDbContextFactory.CreateInMemoryContext();

        const string bogusCookie = "bogus-random-cookie-12345";
        string expectedKey = $"ACCOUNT-SESSION-COOKIE:[\"{bogusCookie}\"]";

        // Act - First request with invalid cookie (cache miss -> database miss -> negative cache write)
        (bool isValid, string? accountName) =
            await mockDb.Object.ValidateAccountSessionCookie(bogusCookie, dbContext, NullLogger.Instance);

        // Assert
        isValid.Should().BeFalse();
        accountName.Should().BeNull();
        store.Should().ContainKey(expectedKey);
        store[expectedKey].Should().Be(DistributedCacheExtensions.InvalidSessionSentinel);
    }

    [Fact]
    public async Task ValidateAccountSessionCookie_WhenNegativeSentinelExists_DoesNotQueryDatabase()
    {
        // Arrange
        (Mock<IDatabase> mockDb, ConcurrentDictionary<string, string> store) = CreateMockRedisStore();
        using MerrickContext dbContext = TestDbContextFactory.CreateInMemoryContext();

        const string badCookie = "attacker-flooding-cookie-999";
        string expectedKey = $"ACCOUNT-SESSION-COOKIE:[\"{badCookie}\"]";

        // Pre-populate negative cache
        store[expectedKey] = DistributedCacheExtensions.InvalidSessionSentinel;

        // Act - Second request with the invalid cookie
        (bool isValid, string? accountName) =
            await mockDb.Object.ValidateAccountSessionCookie(badCookie, dbContext, NullLogger.Instance);

        // Assert
        isValid.Should().BeFalse();
        accountName.Should().BeNull();

        // Verify StringSetAsync was NOT called again (cached sentinel short-circuited before DB)
        mockDb.Verify(d => d.StringSetAsync(
            It.IsAny<RedisKey>(),
            It.IsAny<RedisValue>(),
            It.IsAny<TimeSpan?>(),
            It.IsAny<bool>(),
            It.IsAny<When>(),
            It.IsAny<CommandFlags>()), Times.Never);
        mockDb.Verify(d => d.StringSetAsync(
            It.IsAny<RedisKey>(),
            It.IsAny<RedisValue>(),
            It.IsAny<Expiration>(),
            It.IsAny<ValueCondition>(),
            It.IsAny<CommandFlags>()), Times.Never);
    }

    [Fact]
    public async Task ValidateAccountSessionCookie_WhenCookieIsValid_ResolvesAccountAndPopulatesCache()
    {
        // Arrange
        (Mock<IDatabase> mockDb, ConcurrentDictionary<string, string> store) = CreateMockRedisStore();
        using MerrickContext dbContext = TestDbContextFactory.CreateInMemoryContext();

        User user = new()
        {
            EmailAddress = "valk@kongor.net",
            SRPPasswordSalt = "salt123",
            SRPPasswordHash = "hash123",
            PBKDF2PasswordHash = "pbkdf2_hash"
        };
        await dbContext.Users.AddAsync(user);

        Account account = new()
        {
            Name = "ValkyrieElite",
            User = user
        };
        await dbContext.Accounts.AddAsync(account);

        AccountSession session = new()
        {
            Account = account,
            SessionCookie = "legitimate-session-cookie-777",
            IsActive = true,
            IPAddress = "127.0.0.1",
            TimestampLogin = TimeProvider.System.GetUtcNow()
        };
        await dbContext.AccountSessions.AddAsync(session);
        await dbContext.SaveChangesAsync();

        string expectedKey = $"ACCOUNT-SESSION-COOKIE:[\"{session.SessionCookie}\"]";

        // Act - First request resolves from DB fallback and caches valid account
        (bool isValid, string? accountName) =
            await mockDb.Object.ValidateAccountSessionCookie(session.SessionCookie, dbContext, NullLogger.Instance);

        // Assert
        isValid.Should().BeTrue();
        accountName.Should().Be("ValkyrieElite");
        store.Should().ContainKey(expectedKey);
        store[expectedKey].Should().Be("ValkyrieElite");

        // Act - Second request hits Redis cache directly
        (bool isCachedValid, string? cachedAccountName) =
            await mockDb.Object.ValidateAccountSessionCookie(session.SessionCookie, dbContext, NullLogger.Instance);

        isCachedValid.Should().BeTrue();
        cachedAccountName.Should().Be("ValkyrieElite");
    }

    [Fact]
    public async Task SessionRepository_GetAccountNameForSessionCookieAsync_WithNegativeSentinel_ReturnsNullImmediately()
    {
        // Arrange
        (Mock<IDatabase> mockDb, ConcurrentDictionary<string, string> store) = CreateMockRedisStore();
        using MerrickContext dbContext = TestDbContextFactory.CreateInMemoryContext();

        const string revokedCookie = "revoked-session-cookie-888";
        string key = $"ACCOUNT-SESSION-COOKIE:[\"{revokedCookie}\"]";
        store[key] = SessionRepository.InvalidSessionSentinel;

        SessionRepository repo = new(dbContext, null, mockDb.Object, NullLogger<SessionRepository>.Instance);

        // Act
        string? result = await repo.GetAccountNameForSessionCookieAsync(revokedCookie);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task SessionRepository_GetAccountNameForSessionCookieAsync_WhenNotFoundInDb_CachesSentinel()
    {
        // Arrange
        (Mock<IDatabase> mockDb, ConcurrentDictionary<string, string> store) = CreateMockRedisStore();
        using MerrickContext dbContext = TestDbContextFactory.CreateInMemoryContext();

        const string unknownCookie = "completely-unknown-session-555";
        string expectedKey = $"ACCOUNT-SESSION-COOKIE:[\"{unknownCookie}\"]";

        SessionRepository repo = new(dbContext, null, mockDb.Object, NullLogger<SessionRepository>.Instance);

        // Act
        string? result = await repo.GetAccountNameForSessionCookieAsync(unknownCookie);

        // Assert
        result.Should().BeNull();
        store.Should().ContainKey(expectedKey);
        store[expectedKey].Should().Be(SessionRepository.InvalidSessionSentinel);
    }

    [Fact]
    public async Task SessionContextResolver_WithNegativeSentinel_ShortCircuitsWithoutQueryingAccounts()
    {
        // Arrange
        (Mock<IDatabase> mockDb, ConcurrentDictionary<string, string> store) = CreateMockRedisStore();
        using MerrickContext dbContext = TestDbContextFactory.CreateInMemoryContext();

        const string maliciousCookie = "malicious-probe-cookie-404";
        string key = $"ACCOUNT-SESSION-COOKIE:[\"{maliciousCookie}\"]";
        store[key] = DistributedCacheExtensions.InvalidSessionSentinel;

        SessionContextResolver resolver = new(mockDb.Object, dbContext, NullLogger<SessionContextResolver>.Instance);

        DefaultHttpContext httpContext = new();
        Dictionary<string, StringValues> queryParams = new()
        {
            ["cookie"] = maliciousCookie
        };
        httpContext.Request.Query = new QueryCollection(queryParams);

        // Act
        Account? resolved = await resolver.ResolveAccountAsync(httpContext);

        // Assert
        resolved.Should().BeNull();
    }
}
