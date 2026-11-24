// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using FluentAssertions;
using Kongor.Core.Services.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NEXUS.Contracts.Events;
using Xunit;

namespace Kongor.Core.Tests.Events;

public sealed class EventBusServiceCollectionExtensionsTests
{
    [Fact]
    public void EventBusServiceCollectionExtensions_AddInMemoryEventBus_ShouldRegisterIEventBusAndHostedService()
    {
        // Arrange
        ServiceCollection services = new();

        // Act
        services.AddLogging();
        services.AddInMemoryEventBus(options =>
        {
            options.IsBounded = true;
            options.Capacity = 500;
        });

        ServiceProvider sp = services.BuildServiceProvider();

        // Assert
        IEventBus eventBus = sp.GetRequiredService<IEventBus>();
        eventBus.Should().NotBeNull();
        eventBus.Should().BeOfType<ChannelsEventBus>();

        IEnumerable<IHostedService> hostedServices = sp.GetServices<IHostedService>();
        hostedServices.Should().Contain(h => h is ChannelsEventBus);
    }

    [Fact]
    public void EventBusServiceCollectionExtensions_AddRedisEventBus_ShouldRegisterIEventBusAndHostedService()
    {
        // Arrange
        ServiceCollection services = new();

        // Act
        services.AddLogging();
        services.AddRedisEventBus(options => { options.ChannelPrefix = "custom-events:"; });

        ServiceProvider sp = services.BuildServiceProvider();

        // Assert
        IEventBus eventBus = sp.GetRequiredService<IEventBus>();
        eventBus.Should().NotBeNull();
        eventBus.Should().BeOfType<RedisEventBus>();

        IEnumerable<IHostedService> hostedServices = sp.GetServices<IHostedService>();
        hostedServices.Should().Contain(h => h is RedisEventBus);
    }

    [Fact]
    public void AddEventHandler_SingleEvent_ShouldRegisterHandlerInterface()
    {
        // Arrange
        ServiceCollection services = new();

        // Act
        services.AddEventHandler<MatchCompletedEvent, SampleMatchCompletedHandler>();
        ServiceProvider sp = services.BuildServiceProvider();

        // Assert
        using IServiceScope scope = sp.CreateScope();
        IEventHandler<MatchCompletedEvent> handler =
            scope.ServiceProvider.GetRequiredService<IEventHandler<MatchCompletedEvent>>();
        handler.Should().NotBeNull();
        handler.Should().BeOfType<SampleMatchCompletedHandler>();
    }

    [Fact]
    public void AddEventHandler_MultipleInterfaces_ShouldRegisterAllImplementedHandlers()
    {
        // Arrange
        ServiceCollection services = new();

        // Act
        services.AddEventHandler<MultiEventHandler>();
        ServiceProvider sp = services.BuildServiceProvider();

        // Assert
        using IServiceScope scope = sp.CreateScope();
        IEventHandler<MatchCompletedEvent> matchHandler =
            scope.ServiceProvider.GetRequiredService<IEventHandler<MatchCompletedEvent>>();
        IEventHandler<AccountBannedEvent> banHandler =
            scope.ServiceProvider.GetRequiredService<IEventHandler<AccountBannedEvent>>();

        matchHandler.Should().NotBeNull();
        matchHandler.Should().BeOfType<MultiEventHandler>();
        banHandler.Should().NotBeNull();
        banHandler.Should().BeOfType<MultiEventHandler>();
    }

    private sealed class SampleMatchCompletedHandler : IEventHandler<MatchCompletedEvent>
    {
        public Task HandleAsync(MatchCompletedEvent @event, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class MultiEventHandler : IEventHandler<MatchCompletedEvent>, IEventHandler<AccountBannedEvent>
    {
        public Task HandleAsync(AccountBannedEvent @event, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task HandleAsync(MatchCompletedEvent @event, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}
