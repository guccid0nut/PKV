// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using Kongor.Core.Services.Events;
using NEXUS.Contracts.Events;
using Xunit;

namespace Kongor.Core.Tests.Events;

public sealed class DomainEventHandlerInvokerCacheTests
{
    [Fact]
    public async Task DomainEventHandlerInvokerCache_GetInvoker_CompilesAndExecutesHandlerDirectlyWithoutReflectionWrapper()
    {
        // Arrange
        TestSampleHandler handler = new();
        TestSampleEvent evt = new(42, "Hello World");
        using CancellationTokenSource cts = new();

        // Act
        Func<object, IDomainEvent, CancellationToken, Task> invoker =
            DomainEventHandlerInvokerCache.GetInvoker(typeof(TestSampleEvent));

        await invoker(handler, evt, cts.Token);

        // Assert
        handler.ReceivedEvent.Should().NotBeNull();
        handler.ReceivedEvent!.Value.Should().Be(42);
        handler.ReceivedEvent.Message.Should().Be("Hello World");
        handler.ReceivedToken.Should().Be(cts.Token);
    }

    [Fact]
    public void GetInvoker_SubsequentCalls_ReturnCachedDelegateInstance()
    {
        // Act
        Func<object, IDomainEvent, CancellationToken, Task> invoker1 =
            DomainEventHandlerInvokerCache.GetInvoker(typeof(TestSampleEvent));
        Func<object, IDomainEvent, CancellationToken, Task> invoker2 =
            DomainEventHandlerInvokerCache.GetInvoker(typeof(TestSampleEvent));

        // Assert
        invoker1.Should().BeSameAs(invoker2);
    }

    [Fact]
    public async Task GetInvoker_WithDifferentEventTypes_CompilesDistinctInvokers()
    {
        // Arrange
        TestSampleHandler handler1 = new();
        AnotherHandler handler2 = new();

        TestSampleEvent evt1 = new(100, "Event 1");
        AnotherSampleEvent evt2 = new("Payload 2");

        // Act
        Func<object, IDomainEvent, CancellationToken, Task> invoker1 =
            DomainEventHandlerInvokerCache.GetInvoker(typeof(TestSampleEvent));
        Func<object, IDomainEvent, CancellationToken, Task> invoker2 =
            DomainEventHandlerInvokerCache.GetInvoker(typeof(AnotherSampleEvent));

        await invoker1(handler1, evt1, CancellationToken.None);
        await invoker2(handler2, evt2, CancellationToken.None);

        // Assert
        invoker1.Should().NotBeSameAs(invoker2);
        handler1.ReceivedEvent!.Value.Should().Be(100);
        handler2.ReceivedEvent!.Payload.Should().Be("Payload 2");
    }

    [Fact]
    public async Task GetInvoker_WhenHandlerThrows_RethrowsDirectExceptionWithoutTargetInvocationException()
    {
        // Arrange
        ThrowingHandler handler = new();
        TestSampleEvent evt = new(1, "Fail");

        Func<object, IDomainEvent, CancellationToken, Task> invoker =
            DomainEventHandlerInvokerCache.GetInvoker(typeof(TestSampleEvent));

        // Act & Assert
        Func<Task> act = async () => await invoker(handler, evt, CancellationToken.None);

        // Does NOT throw TargetInvocationException (as reflection does), throws raw InvalidOperationException
        await act.Should().ThrowExactlyAsync<InvalidOperationException>()
            .WithMessage("Test handler intentional failure");
    }

    private sealed record TestSampleEvent(int Value, string Message) : IDomainEvent
    {
        public Guid EventId { get; } = Guid.NewGuid();
        public DateTimeOffset OccurredAt { get; } = TimeProvider.System.GetUtcNow();
    }

    private sealed record AnotherSampleEvent(string Payload) : IDomainEvent
    {
        public Guid EventId { get; } = Guid.NewGuid();
        public DateTimeOffset OccurredAt { get; } = TimeProvider.System.GetUtcNow();
    }

    private sealed class TestSampleHandler : IEventHandler<TestSampleEvent>
    {
        public TestSampleEvent? ReceivedEvent { get; private set; }
        public CancellationToken ReceivedToken { get; private set; }

        public Task HandleAsync(TestSampleEvent @event, CancellationToken cancellationToken = default)
        {
            ReceivedEvent = @event;
            ReceivedToken = cancellationToken;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingHandler : IEventHandler<TestSampleEvent>
    {
        public Task HandleAsync(TestSampleEvent @event, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Test handler intentional failure");
        }
    }

    private sealed class AnotherHandler : IEventHandler<AnotherSampleEvent>
    {
        public AnotherSampleEvent? ReceivedEvent { get; private set; }

        public Task HandleAsync(AnotherSampleEvent @event, CancellationToken cancellationToken = default)
        {
            ReceivedEvent = @event;
            return Task.CompletedTask;
        }
    }
}
