// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Behaviors;
using ASPIRE.Common.Caching;
using ASPIRE.Common.Services.Distributed;
using FluentAssertions;
using FluentValidation;
using FluentValidation.Results;
using MediatR;
using Microsoft.Extensions.Logging;
using Moq;
using NEXUS.Contracts.CQRS;
using Xunit;

namespace Kongor.Core.Tests;

public sealed class PipelineBehaviorsTests
{
    public record TestRequest(string Message) : IRequest<string>;

    public record TestLockableRequest(string ResourceId) : IRequest<string>, ILockableRequest
    {
        public string LockKey => $"lock:{ResourceId}";
        public TimeSpan LockExpiry => TimeSpan.FromSeconds(5);
        public TimeSpan LockTimeout => TimeSpan.FromSeconds(2);
    }

    public record TestCacheableRequest(string QueryKey) : ICacheableQuery<string>
    {
        public string CacheKey => $"cache:{QueryKey}";
        public TimeSpan CacheDuration => TimeSpan.FromMinutes(1);
    }

    [Fact]
    public async Task LoggingBehavior_ExecutesNext_AndReturnsResponse()
    {
        Mock<ILogger<LoggingBehavior<TestRequest, string>>> loggerMock = new();
        LoggingBehavior<TestRequest, string> behavior = new(loggerMock.Object);

        TestRequest request = new("Hello MediatR");
        string response = await behavior.Handle(
            request,
            ct => Task.FromResult("World"),
            CancellationToken.None);

        response.Should().Be("World");
    }

    [Fact]
    public async Task LoggingBehavior_WhenNextThrows_RethrowsAndLogs()
    {
        Mock<ILogger<LoggingBehavior<TestRequest, string>>> loggerMock = new();
        LoggingBehavior<TestRequest, string> behavior = new(loggerMock.Object);

        TestRequest request = new("Failing Request");

        Func<Task> act = async () => await behavior.Handle(
            request,
            ct => throw new InvalidOperationException("Boom"),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Boom");
    }

    [Fact]
    public async Task ValidationBehavior_WhenNoValidators_ExecutesNext()
    {
        Mock<ILogger<ValidationBehavior<TestRequest, string>>> loggerMock = new();
        ValidationBehavior<TestRequest, string> behavior = new([], loggerMock.Object);

        TestRequest request = new("Valid");
        string response = await behavior.Handle(
            request,
            ct => Task.FromResult("OK"),
            CancellationToken.None);

        response.Should().Be("OK");
    }

    [Fact]
    public async Task ValidationBehavior_WhenValidationFails_ThrowsValidationException()
    {
        Mock<ILogger<ValidationBehavior<TestRequest, string>>> loggerMock = new();
        Mock<IValidator<TestRequest>> validatorMock = new();

        validatorMock
            .Setup(v => v.ValidateAsync(It.IsAny<ValidationContext<TestRequest>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult([new ValidationFailure("Message", "Message is required")]));

        ValidationBehavior<TestRequest, string> behavior = new([validatorMock.Object], loggerMock.Object);

        TestRequest request = new("");

        Func<Task> act = async () => await behavior.Handle(
            request,
            ct => Task.FromResult("OK"),
            CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task PerformanceBehavior_ExecutesNext_ReturnsResult()
    {
        Mock<ILogger<PerformanceBehavior<TestRequest, string>>> loggerMock = new();
        PerformanceBehavior<TestRequest, string> behavior = new(loggerMock.Object);

        TestRequest request = new("Perf test");
        string response = await behavior.Handle(
            request,
            ct => Task.FromResult("Fast"),
            CancellationToken.None);

        response.Should().Be("Fast");
    }

    [Fact]
    public async Task DistributedLockBehavior_WhenLockAcquired_ExecutesNext()
    {
        Mock<ILogger<DistributedLockBehavior<TestLockableRequest, string>>> loggerMock = new();
        Mock<IDistributedLockService> lockServiceMock = new();
        Mock<IDistributedLock> lockMock = new();

        lockMock.SetupGet(l => l.IsAcquired).Returns(true);
        lockServiceMock
            .Setup(s => s.AcquireLockAsync(
                "lock:res1",
                It.IsAny<TimeSpan>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(lockMock.Object);

        DistributedLockBehavior<TestLockableRequest, string> behavior = new(
            loggerMock.Object,
            lockServiceMock.Object);

        TestLockableRequest request = new("res1");
        string response = await behavior.Handle(
            request,
            ct => Task.FromResult("Locked Result"),
            CancellationToken.None);

        response.Should().Be("Locked Result");
        lockMock.Verify(l => l.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task DistributedLockBehavior_WhenLockFails_ThrowsTimeoutException()
    {
        Mock<ILogger<DistributedLockBehavior<TestLockableRequest, string>>> loggerMock = new();
        Mock<IDistributedLockService> lockServiceMock = new();
        Mock<IDistributedLock> lockMock = new();

        lockMock.SetupGet(l => l.IsAcquired).Returns(false);
        lockServiceMock
            .Setup(s => s.AcquireLockAsync(
                It.IsAny<string>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(lockMock.Object);

        DistributedLockBehavior<TestLockableRequest, string> behavior = new(
            loggerMock.Object,
            lockServiceMock.Object);

        TestLockableRequest request = new("res1");
        Func<Task> act = async () => await behavior.Handle(
            request,
            ct => Task.FromResult("Should not run"),
            CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>();
    }

    [Fact]
    public async Task CachingBehavior_WhenCacheMiss_ExecutesNextAndPopulates()
    {
        Mock<ILogger<CachingBehavior<TestCacheableRequest, string>>> loggerMock = new();
        Mock<INexusHybridCache> cacheMock = new();

        cacheMock
            .Setup(c => c.GetOrCreateAsync(
                "cache:item1",
                It.IsAny<Func<CancellationToken, Task<string?>>>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, Func<CancellationToken, Task<string?>>, TimeSpan?, TimeSpan?, CancellationToken>(
                async (key, factory, l1, l2, ct) => await factory(ct));

        CachingBehavior<TestCacheableRequest, string> behavior = new(loggerMock.Object, cacheMock.Object);

        TestCacheableRequest request = new("item1");
        string response = await behavior.Handle(
            request,
            ct => Task.FromResult("Computed Result"),
            CancellationToken.None);

        response.Should().Be("Computed Result");
    }
}
