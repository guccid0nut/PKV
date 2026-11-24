// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json;
using FluentAssertions;
using Kongor.Core.Services.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NEXUS.Contracts.Events;
using StackExchange.Redis;
using Xunit;

namespace Kongor.Core.Tests.Events;

public sealed class RedisEventBusTests : IAsyncDisposable
{
    private readonly Mock<IConnectionMultiplexer> _mockMultiplexer = new();
    private readonly Mock<ISubscriber> _mockSubscriber = new();

    public RedisEventBusTests()
    {
        _mockMultiplexer.Setup(m => m.GetSubscriber(It.IsAny<object>())).Returns(_mockSubscriber.Object);
    }

    public async ValueTask DisposeAsync()
    {
        await Task.CompletedTask;
    }

    [Fact]
    public async Task PublishAsync_WithMockSubscriber_PublishesJsonPayloadToCorrectRedisChannel()
    {
        // Arrange
        string capturedChannel = string.Empty;
        string capturedPayload = string.Empty;

        _mockSubscriber
            .Setup(s => s.PublishAsync(It.IsAny<RedisChannel>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
            .Callback<RedisChannel, RedisValue, CommandFlags>((ch, val, _) =>
            {
                capturedChannel = ch.ToString();
                capturedPayload = val.ToString();
            })
            .ReturnsAsync(1);

        RedisEventBus bus = new(
            _mockMultiplexer.Object,
            new RedisEventBusOptions { ChannelPrefix = "events:" },
            null,
            NullLogger<RedisEventBus>.Instance);

        MatchCompletedEvent matchEvent = new(
            77123,
            [10, 20, 30],
            2,
            TimeSpan.FromMinutes(40),
            "ranked",
            1,
            "127.0.0.1:11235"
        );

        // Act
        await bus.PublishAsync(matchEvent);

        // Assert
        capturedChannel.Should().Be("events:MatchCompletedEvent");
        capturedPayload.Should().NotBeNullOrWhiteSpace();

        using JsonDocument doc = JsonDocument.Parse(capturedPayload);
        doc.RootElement.GetProperty("MatchId").GetInt32().Should().Be(77123);
        doc.RootElement.GetProperty("WinnerTeam").GetInt32().Should().Be(2);
        doc.RootElement.GetProperty("GameMode").GetString().Should().Be("ranked");
    }

    [Fact]
    public async Task PublishAsync_WhenSubscriberNull_FallbackDispatchesLocally()
    {
        // Arrange
        RedisEventBus bus = new(
            null,
            new RedisEventBusOptions { ChannelPrefix = "events:" },
            null,
            NullLogger<RedisEventBus>.Instance);

        TestEventHandler<AccountBannedEvent> handler = new();
        await bus.SubscribeAsync(handler);

        AccountBannedEvent banEvent = new(1234, "LocalBannedUser", null, "Testing local dispatch");

        // Act
        await bus.PublishAsync(banEvent);
        AccountBannedEvent received = await handler.WaitForEventAsync(TimeSpan.FromSeconds(5));

        // Assert
        received.Should().NotBeNull();
        received.AccountId.Should().Be(1234);
        received.AccountName.Should().Be("LocalBannedUser");
    }

    [Fact]
    public async Task PublishAsync_HeroAvatarPurchasedEvent_SerializesCorrectly()
    {
        // Arrange
        string capturedPayload = string.Empty;

        _mockSubscriber
            .Setup(s => s.PublishAsync(It.IsAny<RedisChannel>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
            .Callback<RedisChannel, RedisValue, CommandFlags>((_, val, _) => { capturedPayload = val.ToString(); })
            .ReturnsAsync(1);

        RedisEventBus bus = new(
            _mockMultiplexer.Object,
            new RedisEventBusOptions { ChannelPrefix = "events:" },
            null,
            NullLogger<RedisEventBus>.Instance);

        HeroAvatarPurchasedEvent purchaseEvent = new(
            55,
            22,
            9005,
            "Hero_Devourer.Alt3",
            0,
            450
        );

        // Act
        await bus.PublishAsync(purchaseEvent);

        // Assert
        using JsonDocument doc = JsonDocument.Parse(capturedPayload);
        doc.RootElement.GetProperty("AccountId").GetInt32().Should().Be(55);
        doc.RootElement.GetProperty("ProductId").GetInt32().Should().Be(9005);
        doc.RootElement.GetProperty("ItemCode").GetString().Should().Be("Hero_Devourer.Alt3");
        doc.RootElement.GetProperty("CostAmount").GetInt32().Should().Be(450);
    }

    [Fact]
    public async Task PublishAsync_WithMultipleDiScopedHandlers_ExecutesEachInIsolatedScope()
    {
        // Arrange
        ServiceCollection services = new();
        services.AddScoped<RedisScopedTracker>();
        services.AddScoped<IEventHandler<MatchCompletedEvent>, RedisScopedHandler1>();
        services.AddScoped<IEventHandler<MatchCompletedEvent>, RedisScopedHandler2>();
        services.AddScoped<RedisScopedHandler1>();
        services.AddScoped<RedisScopedHandler2>();

        using ServiceProvider sp = services.BuildServiceProvider();

        RedisEventBus bus = new(
            null, // fallback to local dispatch
            new RedisEventBusOptions { ChannelPrefix = "events:" },
            sp,
            NullLogger<RedisEventBus>.Instance);

        await bus.SubscribeAsync(new ServiceProviderEventHandlerProxy<MatchCompletedEvent>(sp));

        try
        {
            MatchCompletedEvent matchEvent = new(
                9999,
                [10, 20],
                2,
                TimeSpan.FromMinutes(15),
                "ranked"
            );

            // Act
            await bus.PublishAsync(matchEvent);

            Guid id1 = await RedisScopedHandler1.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Guid id2 = await RedisScopedHandler2.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

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
        RedisCountTrackingHandler.ExecutionCount = 0;
        ServiceCollection services = new();
        services.AddScoped<IEventHandler<MatchCompletedEvent>, RedisCountTrackingHandler>();
        services.AddScoped<RedisCountTrackingHandler>();

        using ServiceProvider sp = services.BuildServiceProvider();

        RedisEventBus bus = new(
            null,
            new RedisEventBusOptions { ChannelPrefix = "events:" },
            sp,
            NullLogger<RedisEventBus>.Instance);

        await bus.SubscribeAsync(new ServiceProviderEventHandlerProxy<MatchCompletedEvent>(sp));

        try
        {
            MatchCompletedEvent matchEvent = new(
                7777,
                [1],
                1,
                TimeSpan.FromMinutes(10),
                "ranked"
            );

            // Act
            await bus.PublishAsync(matchEvent);
            for (int i = 0; i < 40 && RedisCountTrackingHandler.ExecutionCount == 0; i++) await Task.Delay(5);

            // Assert - Must execute exactly once
            RedisCountTrackingHandler.ExecutionCount.Should().Be(1);
        }
        finally
        {
            await bus.DisposeAsync();
        }
    }

    private sealed class TestEventHandler<TEvent> : IEventHandler<TEvent>
        where TEvent : class, IDomainEvent
    {
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

            _tcs.TrySetResult(@event);
            return Task.CompletedTask;
        }

        public Task<TEvent> WaitForEventAsync(TimeSpan timeout)
        {
            return _tcs.Task.WaitAsync(timeout);
        }
    }

    public sealed class RedisScopedTracker
    {
        public Guid InstanceId { get; } = Guid.NewGuid();
    }

    private sealed class RedisScopedHandler1(RedisScopedTracker tracker) : IEventHandler<MatchCompletedEvent>
    {
        public static readonly TaskCompletionSource<Guid> Tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task HandleAsync(MatchCompletedEvent @event, CancellationToken cancellationToken = default)
        {
            Tcs.TrySetResult(tracker.InstanceId);
            return Task.CompletedTask;
        }
    }

    private sealed class RedisScopedHandler2(RedisScopedTracker tracker) : IEventHandler<MatchCompletedEvent>
    {
        public static readonly TaskCompletionSource<Guid> Tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task HandleAsync(MatchCompletedEvent @event, CancellationToken cancellationToken = default)
        {
            Tcs.TrySetResult(tracker.InstanceId);
            return Task.CompletedTask;
        }
    }

    private sealed class RedisCountTrackingHandler : IEventHandler<MatchCompletedEvent>
    {
        public static int ExecutionCount;

        public Task HandleAsync(MatchCompletedEvent @event, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref ExecutionCount);
            return Task.CompletedTask;
        }
    }
}
