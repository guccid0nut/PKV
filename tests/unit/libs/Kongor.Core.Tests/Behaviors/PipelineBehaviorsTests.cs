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
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NEXUS.Contracts.CQRS;
using Xunit;

namespace Kongor.Core.Tests.Behaviors;

public sealed class PipelineBehaviorsTests
{
    // Test requests
    public sealed record TestRequest(string Name, int Value) : IRequest<string>;

    public sealed record TestCacheableQuery(string Key, TimeSpan Duration) : IRequest<string>, ICacheableQuery<string>
    {
        public string CacheKey => Key;
        public TimeSpan? CacheDuration => Duration;
    }

    public sealed record TestLockableCommand(string ResourceKey, TimeSpan Expiry, TimeSpan Timeout) : IRequest<bool>, ILockableRequest
    {
        public string LockKey => ResourceKey;
        public TimeSpan LockExpiry => Expiry;
        public TimeSpan LockTimeout => Timeout;
    }

    // =========================================================================
    // 1. ValidationBehavior Tests
    // =========================================================================

    [Fact]
    public async Task ValidationBehavior_NoValidators_CallsNext()
    {
        ValidationBehavior<TestRequest, string> behavior = new(
            [],
            NullLogger<ValidationBehavior<TestRequest, string>>.Instance);

        TestRequest request = new("test", 42);
        bool nextCalled = false;
        RequestHandlerDelegate<string> next = (ct) =>
        {
            nextCalled = true;
            return Task.FromResult("success");
        };

        string result = await behavior.Handle(request, next, CancellationToken.None);

        nextCalled.Should().BeTrue();
        result.Should().Be("success");
    }

    [Fact]
    public async Task ValidationBehavior_ValidatorsPass_CallsNext()
    {
        Mock<IValidator<TestRequest>> mockValidator = new();
        mockValidator
            .Setup(v => v.ValidateAsync(It.IsAny<ValidationContext<TestRequest>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult());

        ValidationBehavior<TestRequest, string> behavior = new(
            [mockValidator.Object],
            NullLogger<ValidationBehavior<TestRequest, string>>.Instance);

        TestRequest request = new("valid", 100);
        bool nextCalled = false;
        RequestHandlerDelegate<string> next = (ct) =>
        {
            nextCalled = true;
            return Task.FromResult("passed");
        };

        string result = await behavior.Handle(request, next, CancellationToken.None);

        nextCalled.Should().BeTrue();
        result.Should().Be("passed");
    }

    [Fact]
    public async Task ValidationBehavior_ValidationFails_ThrowsValidationException_DoesNotCallNext()
    {
        Mock<IValidator<TestRequest>> mockValidator = new();
        ValidationFailure failure = new("Name", "Name is required");
        mockValidator
            .Setup(v => v.ValidateAsync(It.IsAny<ValidationContext<TestRequest>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult([failure]));

        ValidationBehavior<TestRequest, string> behavior = new(
            [mockValidator.Object],
            NullLogger<ValidationBehavior<TestRequest, string>>.Instance);

        TestRequest request = new("", 0);
        bool nextCalled = false;
        RequestHandlerDelegate<string> next = (ct) =>
        {
            nextCalled = true;
            return Task.FromResult("should not run");
        };

        Func<Task> act = async () => await behavior.Handle(request, next, CancellationToken.None);

        var exAssertions = await act.Should().ThrowAsync<ValidationException>();
        exAssertions.Which.Errors.Should().ContainSingle(e => e.ErrorMessage == "Name is required");
        nextCalled.Should().BeFalse();
    }

    // =========================================================================
    // 2. CachingBehavior Tests
    // =========================================================================

    [Fact]
    public async Task CachingBehavior_NonCacheableRequest_CallsNextDirectly()
    {
        Mock<INexusHybridCache> mockCache = new();
        CachingBehavior<TestRequest, string> behavior = new(
            NullLogger<CachingBehavior<TestRequest, string>>.Instance,
            mockCache.Object);

        TestRequest request = new("non-cacheable", 1);
        bool nextCalled = false;
        RequestHandlerDelegate<string> next = (ct) =>
        {
            nextCalled = true;
            return Task.FromResult("direct");
        };

        string result = await behavior.Handle(request, next, CancellationToken.None);

        nextCalled.Should().BeTrue();
        result.Should().Be("direct");
        mockCache.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CachingBehavior_NullCache_CallsNextDirectly()
    {
        CachingBehavior<TestCacheableQuery, string> behavior = new(
            NullLogger<CachingBehavior<TestCacheableQuery, string>>.Instance,
            null);

        TestCacheableQuery query = new("cache-key-1", TimeSpan.FromMinutes(5));
        bool nextCalled = false;
        RequestHandlerDelegate<string> next = (ct) =>
        {
            nextCalled = true;
            return Task.FromResult("fallback");
        };

        string result = await behavior.Handle(query, next, CancellationToken.None);

        nextCalled.Should().BeTrue();
        result.Should().Be("fallback");
    }

    [Fact]
    public async Task CachingBehavior_CacheableQuery_UsesHybridCache()
    {
        Mock<INexusHybridCache> mockCache = new();
        mockCache
            .Setup(c => c.GetOrCreateAsync(
                "cached-item-42",
                It.IsAny<Func<CancellationToken, Task<string?>>>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("from-cache");

        CachingBehavior<TestCacheableQuery, string> behavior = new(
            NullLogger<CachingBehavior<TestCacheableQuery, string>>.Instance,
            mockCache.Object);

        TestCacheableQuery query = new("cached-item-42", TimeSpan.FromMinutes(10));
        bool nextCalled = false;
        RequestHandlerDelegate<string> next = (ct) =>
        {
            nextCalled = true;
            return Task.FromResult("from-handler");
        };

        string result = await behavior.Handle(query, next, CancellationToken.None);

        result.Should().Be("from-cache");
        nextCalled.Should().BeFalse();
    }

    // =========================================================================
    // 3. DistributedLockBehavior Tests
    // =========================================================================

    [Fact]
    public async Task DistributedLockBehavior_NonLockableRequest_CallsNextDirectly()
    {
        Mock<IDistributedLockService> mockLockService = new();
        DistributedLockBehavior<TestRequest, string> behavior = new(
            NullLogger<DistributedLockBehavior<TestRequest, string>>.Instance,
            mockLockService.Object);

        TestRequest request = new("no-lock", 1);
        bool nextCalled = false;
        RequestHandlerDelegate<string> next = (ct) =>
        {
            nextCalled = true;
            return Task.FromResult("no-lock-result");
        };

        string result = await behavior.Handle(request, next, CancellationToken.None);

        nextCalled.Should().BeTrue();
        result.Should().Be("no-lock-result");
        mockLockService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DistributedLockBehavior_NullLockService_CallsNextDirectly()
    {
        DistributedLockBehavior<TestLockableCommand, bool> behavior = new(
            NullLogger<DistributedLockBehavior<TestLockableCommand, bool>>.Instance,
            null);

        TestLockableCommand command = new("resource:1", TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(5));
        bool nextCalled = false;
        RequestHandlerDelegate<bool> next = (ct) =>
        {
            nextCalled = true;
            return Task.FromResult(true);
        };

        bool result = await behavior.Handle(command, next, CancellationToken.None);

        nextCalled.Should().BeTrue();
        result.Should().BeTrue();
    }

    [Fact]
    public async Task DistributedLockBehavior_LockAcquisitionFails_ThrowsTimeoutException_DoesNotCallNext()
    {
        Mock<IDistributedLockService> mockLockService = new();
        Mock<IDistributedLock> mockLock = new();
        mockLock.Setup(l => l.IsAcquired).Returns(false);

        mockLockService
            .Setup(s => s.AcquireLockAsync(
                "busy-resource",
                It.IsAny<TimeSpan>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockLock.Object);

        DistributedLockBehavior<TestLockableCommand, bool> behavior = new(
            NullLogger<DistributedLockBehavior<TestLockableCommand, bool>>.Instance,
            mockLockService.Object);

        TestLockableCommand command = new("busy-resource", TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(500));
        bool nextCalled = false;
        RequestHandlerDelegate<bool> next = (ct) =>
        {
            nextCalled = true;
            return Task.FromResult(true);
        };

        Func<Task> act = async () => await behavior.Handle(command, next, CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>();
        nextCalled.Should().BeFalse();
    }

    [Fact]
    public async Task DistributedLockBehavior_LockAcquisitionSucceeds_ExecutesHandlerAndDisposesLock()
    {
        Mock<IDistributedLockService> mockLockService = new();
        Mock<IDistributedLock> mockLock = new();
        mockLock.Setup(l => l.IsAcquired).Returns(true);

        mockLockService
            .Setup(s => s.AcquireLockAsync(
                "free-resource",
                TimeSpan.FromMinutes(2),
                TimeSpan.FromSeconds(3),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockLock.Object);

        DistributedLockBehavior<TestLockableCommand, bool> behavior = new(
            NullLogger<DistributedLockBehavior<TestLockableCommand, bool>>.Instance,
            mockLockService.Object);

        TestLockableCommand command = new("free-resource", TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(3));
        bool nextCalled = false;
        RequestHandlerDelegate<bool> next = (ct) =>
        {
            nextCalled = true;
            return Task.FromResult(true);
        };

        bool result = await behavior.Handle(command, next, CancellationToken.None);

        nextCalled.Should().BeTrue();
        result.Should().BeTrue();
        mockLock.Verify(l => l.DisposeAsync(), Times.Once);
    }
}
