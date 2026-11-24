// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Threading.Channels;
using FluentAssertions;
using Kongor.Core.Services.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.Events;
using Xunit;

namespace Kongor.Core.Tests.Events;

public sealed class ChannelsEventBusTests : IAsyncLifetime
{
    private readonly ChannelsEventBus _eventBus = new(
        new ChannelsEventBusOptions { IsBounded = false },
        null,
        NullLogger<ChannelsEventBus>.Instance);

    public Task InitializeAsync()
    {
        return _eventBus.StartAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _eventBus.DisposeAsync();
    }

    [Fact]
    public async Task PublishAsync_WithSubscribers_DispatchesToAllSubscribersConcurrently()
    {
        // Arrange
        TestEventHandler<MatchCompletedEvent> handler1 = new();
        TestEventHandler<MatchCompletedEvent> handler2 = new();

        await _eventBus.SubscribeAsync(handler1);
        await _eventBus.SubscribeAsync(handler2);

        MatchCompletedEvent matchEvent = new(
            5001,
            [1, 2, 3],
            1,
            TimeSpan.FromMinutes(25),
            "ranked"
        );

        // Act
        await _eventBus.PublishAsync(matchEvent);

        MatchCompletedEvent received1 = await handler1.WaitForEventAsync(TimeSpan.FromSeconds(5));
        MatchCompletedEvent received2 = await handler2.WaitForEventAsync(TimeSpan.FromSeconds(5));

        // Assert
        received1.Should().NotBeNull();
        received1.MatchId.Should().Be(5001);
        received2.Should().NotBeNull();
        received2.MatchId.Should().Be(5001);
    }

    [Fact]
    public async Task PublishAsync_WithNoSubscribers_DoesNotThrow()
    {
        // Arrange
        AccountBannedEvent bannedEvent = new(
            99,
            "SilentBannedUser",
            null,
            "Permanent Ban"
        );

        // Act & Assert
        Func<Task> act = async () => await _eventBus.PublishAsync(bannedEvent);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ChannelsEventBus_UnsubscribeAsync_HandlerNoLongerReceivesEvents()
    {
        // Arrange
        TestEventHandler<AccountBannedEvent> handler = new();
        await _eventBus.SubscribeAsync(handler);

        AccountBannedEvent event1 = new(10, "User1", null, "Reason1");
        await _eventBus.PublishAsync(event1);
        await handler.WaitForEventAsync(TimeSpan.FromSeconds(5));

        // Act
        await _eventBus.UnsubscribeAsync(handler);

        AccountBannedEvent event2 = new(20, "User2", null, "Reason2");
        await _eventBus.PublishAsync(event2);
        await Task.Delay(25);

        // Assert
        handler.ReceivedEvents.Should().HaveCount(1);
        handler.ReceivedEvents[0].AccountId.Should().Be(10);
    }

    [Fact]
    public async Task PublishAsync_WhenOneHandlerThrows_OtherHandlersStillExecute()
    {
        // Arrange
        TaskCompletionSource<bool> faultingTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TestEventHandler<HeroAvatarPurchasedEvent> failingHandler = new(_ =>
        {
            faultingTcs.TrySetResult(true);
            throw new InvalidOperationException("Simulated handler crash");
        });

        TestEventHandler<HeroAvatarPurchasedEvent> healthyHandler = new();

        await _eventBus.SubscribeAsync(failingHandler);
        await _eventBus.SubscribeAsync(healthyHandler);

        HeroAvatarPurchasedEvent purchaseEvent = new(
            100,
            50,
            777,
            "Hero_Flint.Alt1",
            1,
            200
        );

        // Act
        await _eventBus.PublishAsync(purchaseEvent);

        HeroAvatarPurchasedEvent received = await healthyHandler.WaitForEventAsync(TimeSpan.FromSeconds(5));
        bool faultingHandlerExecuted = await faultingTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        faultingHandlerExecuted.Should().BeTrue();
        received.Should().NotBeNull();
        received.ItemCode.Should().Be("Hero_Flint.Alt1");
    }

    [Fact]
    public async Task PublishAsync_WithMultipleDistinctEventTypes_OnlyRoutesToTargetHandlers()
    {
        // Arrange
        TestEventHandler<MatchCompletedEvent> matchHandler = new();
        TestEventHandler<AccountBannedEvent> banHandler = new();

        await _eventBus.SubscribeAsync(matchHandler);
        await _eventBus.SubscribeAsync(banHandler);

        MatchCompletedEvent matchEvent = new(101, [1, 2], 1, TimeSpan.FromMinutes(15), "midwars");
        AccountBannedEvent banEvent = new(202, "Cheater", null, "Wallhack");

        // Act
        await _eventBus.PublishAsync(matchEvent);
        await _eventBus.PublishAsync(banEvent);

        MatchCompletedEvent receivedMatch = await matchHandler.WaitForEventAsync(TimeSpan.FromSeconds(5));
        AccountBannedEvent receivedBan = await banHandler.WaitForEventAsync(TimeSpan.FromSeconds(5));

        // Assert
        receivedMatch.MatchId.Should().Be(101);
        receivedBan.AccountId.Should().Be(202);
        matchHandler.ReceivedEvents.Should().HaveCount(1);
        banHandler.ReceivedEvents.Should().HaveCount(1);
    }

    [Fact]
    public async Task PublishAsync_ConcurrentHighVolume_DispatchesAllEventsWithoutDrop()
    {
        // Arrange
        const int eventCount = 100;
        CountdownEvent countdown = new(eventCount);
        TestEventHandler<MatchCompletedEvent> handler = new(_ => countdown.Signal());

        await _eventBus.SubscribeAsync(handler);

        // Act
        List<Task> publishTasks = [];
        for (int i = 1; i <= eventCount; i++)
        {
            int matchId = i;
            publishTasks.Add(_eventBus.PublishAsync(new MatchCompletedEvent(matchId, [matchId], 1,
                TimeSpan.FromMinutes(10), "ranked")));
        }

        await Task.WhenAll(publishTasks);
        bool completed = countdown.Wait(TimeSpan.FromSeconds(10));

        // Assert
        completed.Should().BeTrue();
        handler.ReceivedEvents.Should().HaveCount(eventCount);
    }

    [Fact]
    public async Task ChannelsEventBus_BoundedCapacity_ProcessesSuccessfully()
    {
        // Arrange
        ChannelsEventBus boundedBus = new(
            new ChannelsEventBusOptions
            {
                IsBounded = true,
                Capacity = 50,
                FullMode = BoundedChannelFullMode.Wait
            },
            null,
            NullLogger<ChannelsEventBus>.Instance);

        try
        {
            await boundedBus.StartAsync(CancellationToken.None);

            TestEventHandler<MatchCompletedEvent> handler = new();
            await boundedBus.SubscribeAsync(handler);

            MatchCompletedEvent evt = new(999, [1], 1, TimeSpan.FromMinutes(20), "casual");
            await boundedBus.PublishAsync(evt);

            MatchCompletedEvent received = await handler.WaitForEventAsync(TimeSpan.FromSeconds(5));
            received.MatchId.Should().Be(999);
        }
        finally
        {
            await boundedBus.DisposeAsync();
        }
    }

    [Fact]
    public async Task PublishAsync_WithMultipleDiScopedHandlers_ExecutesEachInIsolatedScope()
    {
        // Arrange
        ServiceCollection services = new();
        services.AddScoped<ScopedTracker>();
        services.AddScoped<IEventHandler<MatchCompletedEvent>, ScopedHandler1>();
        services.AddScoped<IEventHandler<MatchCompletedEvent>, ScopedHandler2>();
        services.AddScoped<ScopedHandler1>();
        services.AddScoped<ScopedHandler2>();

        using ServiceProvider sp = services.BuildServiceProvider();

        ChannelsEventBus bus = new(
            new ChannelsEventBusOptions { IsBounded = false },
            sp,
            NullLogger<ChannelsEventBus>.Instance);

        await bus.SubscribeAsync(new ServiceProviderEventHandlerProxy<MatchCompletedEvent>(sp));

        try
        {
            await bus.StartAsync(CancellationToken.None);

            MatchCompletedEvent matchEvent = new(
                8888,
                [1, 2],
                1,
                TimeSpan.FromMinutes(10),
                "casual"
            );

            // Act
            await bus.PublishAsync(matchEvent);

            Guid id1 = await ScopedHandler1.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Guid id2 = await ScopedHandler2.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Assert
            id1.Should().NotBeEmpty();
            id2.Should().NotBeEmpty();
            id1.Should().NotBe(id2, "each concurrent handler task must execute within its own isolated IServiceScope");
        }
        finally
        {
            await bus.DisposeAsync();
        }
    }

    [Fact]
    public async Task PublishAsync_WithDiProxy_ExecutesHandlerExactlyOnceWithoutDoubleDispatch()
    {
        // Arrange
        CountTrackingHandler.ExecutionCount = 0;
        ServiceCollection services = new();
        services.AddScoped<IEventHandler<MatchCompletedEvent>, CountTrackingHandler>();
        services.AddScoped<CountTrackingHandler>();

        using ServiceProvider sp = services.BuildServiceProvider();

        ChannelsEventBus bus = new(
            new ChannelsEventBusOptions { IsBounded = false },
            sp,
            NullLogger<ChannelsEventBus>.Instance);

        await bus.SubscribeAsync(new ServiceProviderEventHandlerProxy<MatchCompletedEvent>(sp));
        await bus.StartAsync(CancellationToken.None);

        try
        {
            MatchCompletedEvent matchEvent = new(
                9999,
                [1],
                1,
                TimeSpan.FromMinutes(10),
                "ranked"
            );

            // Act
            await bus.PublishAsync(matchEvent);
            for (int i = 0; i < 40 && CountTrackingHandler.ExecutionCount == 0; i++) await Task.Delay(5);

            // Assert - Must execute exactly once, not twice
            CountTrackingHandler.ExecutionCount.Should().Be(1);
        }
        finally
        {
            await bus.DisposeAsync();
        }
    }

    [Fact]
    public async Task PublishAsync_BeforeStartAsync_BuffersEventsAndProcessesAfterStartAsync()
    {
        // Arrange
        ChannelsEventBus bus = new(
            new ChannelsEventBusOptions { IsBounded = false },
            null,
            NullLogger<ChannelsEventBus>.Instance);

        try
        {
            TestEventHandler<MatchCompletedEvent> handler = new();
            await bus.SubscribeAsync(handler);

            MatchCompletedEvent earlyEvent = new(777, [1], 1, TimeSpan.FromMinutes(15), "midwars");

            // Act - Publish event before host starts
            await bus.PublishAsync(earlyEvent);
            await Task.Delay(25);

            // Event should not be processed yet since StartAsync has not run
            handler.ReceivedEvents.Should().BeEmpty();

            // Start the bus
            await bus.StartAsync(CancellationToken.None);

            MatchCompletedEvent received = await handler.WaitForEventAsync(TimeSpan.FromSeconds(5));

            // Assert
            received.Should().NotBeNull();
            received.MatchId.Should().Be(777);
            handler.ReceivedEvents.Should().HaveCount(1);
        }
        finally
        {
            await bus.DisposeAsync();
        }
    }

    [Fact]
    public async Task StartAsync_WhenCalledConcurrently_IsThreadSafeAndIdempotent()
    {
        // Arrange
        ChannelsEventBus bus = new(
            new ChannelsEventBusOptions { IsBounded = false },
            null,
            NullLogger<ChannelsEventBus>.Instance);

        try
        {
            // Act
            Task[] startTasks = [.. Enumerable.Range(0, 10).Select(_ => bus.StartAsync(CancellationToken.None))];

            Func<Task> act = async () => await Task.WhenAll(startTasks);

            // Assert
            await act.Should().NotThrowAsync();
        }
        finally
        {
            await bus.DisposeAsync();
        }
    }

    [Fact]
    public async Task ChannelsEventBus_StopAsync_GracefullyCompletesAndStopsProcessing()
    {
        // Arrange
        ChannelsEventBus bus = new(
            new ChannelsEventBusOptions { IsBounded = false },
            null,
            NullLogger<ChannelsEventBus>.Instance);

        TestEventHandler<MatchCompletedEvent> handler = new();
        await bus.SubscribeAsync(handler);
        await bus.StartAsync(CancellationToken.None);

        await bus.PublishAsync(new MatchCompletedEvent(1, [1], 1, TimeSpan.FromMinutes(5), "ranked"));
        await handler.WaitForEventAsync(TimeSpan.FromSeconds(5));

        // Act
        Func<Task> stopAct = async () => await bus.StopAsync(CancellationToken.None);

        // Assert
        await stopAct.Should().NotThrowAsync();
        await bus.DisposeAsync();
    }

    private sealed class TestEventHandler<TEvent>(Action<TEvent>? callback = null) : IEventHandler<TEvent>
        where TEvent : class, IDomainEvent
    {
        private readonly Action<TEvent>? _callback = callback;
        private readonly object _lock = new();
        private readonly List<TEvent> _receivedEvents = [];
        private readonly TaskCompletionSource<TEvent> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<TEvent> ReceivedEvents
        {
            get
            {
                lock (_lock)
                {
                    return [.. _receivedEvents];
                }
            }
        }

        public Task HandleAsync(TEvent @event, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                _receivedEvents.Add(@event);
            }

            _callback?.Invoke(@event);
            _tcs.TrySetResult(@event);
            return Task.CompletedTask;
        }

        public Task<TEvent> WaitForEventAsync(TimeSpan timeout)
        {
            return _tcs.Task.WaitAsync(timeout);
        }
    }

    public sealed class ScopedTracker
    {
        public Guid InstanceId { get; } = Guid.NewGuid();
    }

    private sealed class ScopedHandler1(ScopedTracker tracker) : IEventHandler<MatchCompletedEvent>
    {
        public static readonly TaskCompletionSource<Guid> Tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task HandleAsync(MatchCompletedEvent @event, CancellationToken cancellationToken = default)
        {
            Tcs.TrySetResult(tracker.InstanceId);
            return Task.CompletedTask;
        }
    }

    private sealed class ScopedHandler2(ScopedTracker tracker) : IEventHandler<MatchCompletedEvent>
    {
        public static readonly TaskCompletionSource<Guid> Tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task HandleAsync(MatchCompletedEvent @event, CancellationToken cancellationToken = default)
        {
            Tcs.TrySetResult(tracker.InstanceId);
            return Task.CompletedTask;
        }
    }

    private sealed class CountTrackingHandler : IEventHandler<MatchCompletedEvent>
    {
        public static int ExecutionCount;

        public Task HandleAsync(MatchCompletedEvent @event, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref ExecutionCount);
            return Task.CompletedTask;
        }
    }
}
