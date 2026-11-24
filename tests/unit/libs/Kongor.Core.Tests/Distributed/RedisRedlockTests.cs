// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Services.Distributed;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;
using Xunit;

namespace Kongor.Core.Tests.Distributed;

public sealed class RedisRedlockTests
{
    [Fact]
    public async Task AcquireLockAsync_InMemoryFallback_AcquiresAndReleasesDeterministically()
    {
        // Arrange
        RedisRedlockService service = new(connectionMultiplexer: null, database: null);
        string resource = "test:account:101:checkout";

        // Act
        IDistributedLock lock1 = await service.AcquireLockAsync(
            resource,
            expiry: TimeSpan.FromSeconds(5),
            timeout: TimeSpan.FromSeconds(1));

        // Assert
        lock1.IsAcquired.Should().BeTrue();
        lock1.Resource.Should().Be(resource);
        lock1.LockToken.Should().NotBeNullOrWhiteSpace();
        lock1.ValidityRemaining.Should().BeGreaterThan(TimeSpan.Zero);

        // Second acquisition on same resource should fail immediately
        IDistributedLock lock2 = await service.AcquireLockAsync(
            resource,
            expiry: TimeSpan.FromSeconds(5),
            timeout: TimeSpan.FromMilliseconds(50));

        lock2.IsAcquired.Should().BeFalse();

        // Release first lock
        await lock1.DisposeAsync();

        // Third acquisition should now succeed
        IDistributedLock lock3 = await service.AcquireLockAsync(
            resource,
            expiry: TimeSpan.FromSeconds(5),
            timeout: TimeSpan.FromMilliseconds(200));

        lock3.IsAcquired.Should().BeTrue();
        await lock3.DisposeAsync();
    }

    [Fact]
    public async Task AcquireLockAsync_WithMockRedis_SetsNXAndExecutesLuaRelease()
    {
        // Arrange
        Mock<IConnectionMultiplexer> mockMultiplexer = new();
        Mock<IDatabase> mockDatabase = new();
        mockMultiplexer.SetupGet(m => m.IsConnected).Returns(true);
        mockMultiplexer.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(mockDatabase.Object);

        mockDatabase.Setup(d => d.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<TimeSpan?>(),
                false,
                When.NotExists,
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);

        mockDatabase.Setup(d => d.ScriptEvaluateAsync(
                It.IsAny<string>(),
                It.IsAny<RedisKey[]>(),
                It.IsAny<RedisValue[]>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisResult.Create(1));

        RedisRedlockService service = new(
            connectionMultiplexer: mockMultiplexer.Object,
            database: mockDatabase.Object,
            timeProvider: TimeProvider.System,
            logger: NullLogger<RedisRedlockService>.Instance);

        string resource = "test:account:202:store";

        // Act
        await using (IDistributedLock distLock = await service.AcquireLockAsync(
            resource,
            expiry: TimeSpan.FromSeconds(10),
            timeout: TimeSpan.FromSeconds(1)))
        {
            distLock.IsAcquired.Should().BeTrue();
            mockDatabase.Verify(d => d.StringSetAsync(
                It.Is<RedisKey>(k => k.ToString() == resource),
                It.IsAny<RedisValue>(),
                It.Is<TimeSpan?>(t => t == TimeSpan.FromSeconds(10)),
                false,
                When.NotExists,
                It.IsAny<CommandFlags>()), Times.Once);
        }

        // Assert release script called upon exiting await using
        mockDatabase.Verify(d => d.ScriptEvaluateAsync(
            It.Is<string>(s => s.Contains("redis.call")),
            It.Is<RedisKey[]>(k => k.Length == 1 && k[0].ToString() == resource),
            It.IsAny<RedisValue[]>(),
            It.IsAny<CommandFlags>()), Times.Once);
    }

    [Fact]
    public async Task AcquireLockAsync_WhenRedisContended_TimesOutAndReturnsUnacquired()
    {
        // Arrange
        Mock<IConnectionMultiplexer> mockMultiplexer = new();
        Mock<IDatabase> mockDatabase = new();
        mockMultiplexer.SetupGet(m => m.IsConnected).Returns(true);
        mockMultiplexer.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(mockDatabase.Object);

        // Always returns false (contention)
        mockDatabase.Setup(d => d.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<TimeSpan?>(),
                false,
                When.NotExists,
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(false);

        RedisRedlockService service = new(
            connectionMultiplexer: mockMultiplexer.Object,
            database: mockDatabase.Object,
            timeProvider: TimeProvider.System,
            logger: NullLogger<RedisRedlockService>.Instance);

        // Act
        IDistributedLock lockResult = await service.AcquireLockAsync(
            "test:contended:resource",
            expiry: TimeSpan.FromSeconds(5),
            timeout: TimeSpan.FromMilliseconds(60));

        // Assert
        lockResult.IsAcquired.Should().BeFalse();
        lockResult.ValidityRemaining.Should().Be(TimeSpan.Zero);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
    }

    [Fact]
    public async Task AcquireLockAsync_WhenInMemoryLeaseExpiresUnderConcurrency_ExactlyOneCallerTakesOver()
    {
        ManualTimeProvider clock = new();
        RedisRedlockService service = new(connectionMultiplexer: null, database: null, timeProvider: clock);
        string resource = "test:expired:takeover";

        IDistributedLock initial = await service.TryAcquireLockAsync(resource, TimeSpan.FromSeconds(5));
        initial.IsAcquired.Should().BeTrue();

        clock.Advance(TimeSpan.FromSeconds(6));

        Task<IDistributedLock>[] contenders = Enumerable.Range(0, 16)
            .Select(_ => service.TryAcquireLockAsync(resource, TimeSpan.FromSeconds(10)))
            .ToArray();

        IDistributedLock[] results = await Task.WhenAll(contenders);
        results.Count(r => r.IsAcquired).Should().Be(1);

        IDistributedLock winner = results.Single(r => r.IsAcquired);
        await initial.DisposeAsync(); // Stale token must not release winner's lock

        IDistributedLock probeWhileWinnerActive = await service.TryAcquireLockAsync(resource, TimeSpan.FromSeconds(5));
        probeWhileWinnerActive.IsAcquired.Should().BeFalse();

        await winner.DisposeAsync();

        IDistributedLock afterWinnerDispose = await service.TryAcquireLockAsync(resource, TimeSpan.FromSeconds(5));
        afterWinnerDispose.IsAcquired.Should().BeTrue();
        await afterWinnerDispose.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_WhenRedisReconnectsAfterFallbackAcquire_ReleasesInMemoryEntryWithoutCallingRedis()
    {
        bool redisConnected = false;
        Mock<IConnectionMultiplexer> mockMultiplexer = new();
        Mock<IDatabase> mockDatabase = new();
        mockMultiplexer.SetupGet(m => m.IsConnected).Returns(() => redisConnected);
        mockMultiplexer.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(mockDatabase.Object);

        RedisRedlockService service = new(
            connectionMultiplexer: mockMultiplexer.Object,
            database: mockDatabase.Object,
            timeProvider: TimeProvider.System,
            logger: NullLogger<RedisRedlockService>.Instance);

        string resource = "test:transient:redis:reconnect";

        // Acquire while Redis is disconnected -> uses in-memory fallback
        IDistributedLock fallbackLock = await service.TryAcquireLockAsync(resource, TimeSpan.FromSeconds(30));
        fallbackLock.IsAcquired.Should().BeTrue();

        // Simulate Redis reconnecting before DisposeAsync, then disconnecting again
        redisConnected = true;
        await fallbackLock.DisposeAsync();

        mockDatabase.Verify(d => d.ScriptEvaluateAsync(
            It.IsAny<string>(),
            It.IsAny<RedisKey[]>(),
            It.IsAny<RedisValue[]>(),
            It.IsAny<CommandFlags>()), Times.Never);

        redisConnected = false;
        IDistributedLock reacquired = await service.TryAcquireLockAsync(resource, TimeSpan.FromSeconds(5));
        reacquired.IsAcquired.Should().BeTrue();
        await reacquired.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_WhenRedisLuaScriptingDisabled_ReleasesViaWatchMultiExecAndAllowsImmediateNextUpload()
    {
        // Protects: Back-to-back Cloud Save uploads when Redis rejects EVAL ("ERR This instance has Lua scripting support disabled").
        // Fails when: ReleaseLockAsync swallows RedisServerException into _inMemoryLocks and leaves the Redis key orphaned for 10s.
        string? heldToken = null;
        Mock<IConnectionMultiplexer> mockMultiplexer = new();
        Mock<IDatabase> mockDatabase = new();
        Mock<ITransaction> mockTransaction = new();
        mockMultiplexer.SetupGet(m => m.IsConnected).Returns(true);
        mockMultiplexer.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(mockDatabase.Object);

        mockDatabase.Setup(d => d.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<TimeSpan?>(),
                false,
                When.NotExists,
                It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisKey _, RedisValue val, TimeSpan? _, bool _, When _, CommandFlags _) =>
            {
                if (heldToken != null) return false;
                heldToken = val.ToString();
                return true;
            });

        mockDatabase.Setup(d => d.ScriptEvaluateAsync(
                It.IsAny<string>(),
                It.IsAny<RedisKey[]>(),
                It.IsAny<RedisValue[]>(),
                It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisServerException("ERR This instance has Lua scripting support disabled"));

        mockDatabase.Setup(d => d.CreateTransaction(It.IsAny<object>())).Returns(mockTransaction.Object);
        mockTransaction.Setup(t => t.AddCondition(It.IsAny<Condition>()))
            .Returns((ConditionResult)null!);
        mockTransaction.Setup(t => t.KeyDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);
        mockTransaction.Setup(t => t.ExecuteAsync(It.IsAny<CommandFlags>()))
            .ReturnsAsync(() =>
            {
                heldToken = null;
                return true;
            });

        RedisRedlockService service = new(
            connectionMultiplexer: mockMultiplexer.Object,
            database: mockDatabase.Object,
            timeProvider: TimeProvider.System,
            logger: NullLogger<RedisRedlockService>.Instance);

        string resource = "lock:account:59:cloud_storage";

        // 1st upload acquires and releases via Lua-free WATCH/MULTI/EXEC fallback
        await using (IDistributedLock upload1 = await service.AcquireLockAsync(resource, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2)))
        {
            upload1.IsAcquired.Should().BeTrue();
        }
        heldToken.Should().BeNull("Lua-free transaction fallback must delete the Redis lock immediately upon DisposeAsync");

        // 2nd immediate upload succeeds without waiting for lease expiry, and skips EVAL permanently once _luaScriptingDisabled is latched
        await using (IDistributedLock upload2 = await service.AcquireLockAsync(resource, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(50)))
        {
            upload2.IsAcquired.Should().BeTrue();
        }
        heldToken.Should().BeNull();

        mockDatabase.Verify(d => d.ScriptEvaluateAsync(
            It.IsAny<string>(),
            It.IsAny<RedisKey[]>(),
            It.IsAny<RedisValue[]>(),
            It.IsAny<CommandFlags>()), Times.Once);
        mockTransaction.Verify(t => t.ExecuteAsync(It.IsAny<CommandFlags>()), Times.Exactly(2));
    }
}
