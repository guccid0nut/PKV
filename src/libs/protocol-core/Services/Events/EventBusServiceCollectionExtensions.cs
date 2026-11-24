// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace Kongor.Core.Services.Events;

/// <summary>
///     Dependency injection extension methods for registering domain event bus providers and handlers.
/// </summary>
public static class EventBusServiceCollectionExtensions
{
    /// <summary>
    ///     Registers the non-blocking in-memory <see cref="ChannelsEventBus" /> as the <see cref="IEventBus" /> provider.
    /// </summary>
    public static IServiceCollection AddInMemoryEventBus(
        this IServiceCollection services,
        Action<ChannelsEventBusOptions>? configure = null)
    {
        ChannelsEventBusOptions options = new();
        configure?.Invoke(options);

        services.TryAddSingleton(options);
        services.AddSingleton(sp =>
            new ChannelsEventBus(
                sp.GetService<ChannelsEventBusOptions>() ?? options,
                sp,
                sp.GetService<ILogger<ChannelsEventBus>>()
            ));

        services.AddSingleton<IEventBus>(sp => sp.GetRequiredService<ChannelsEventBus>());
        services.AddHostedService(sp => sp.GetRequiredService<ChannelsEventBus>());
        services.AddHostedService<EventBusSubscriptionInitializerHostedService>();

        return services;
    }

    /// <summary>
    ///     Registers the distributed <see cref="RedisEventBus" /> as the <see cref="IEventBus" /> provider.
    /// </summary>
    public static IServiceCollection AddRedisEventBus(
        this IServiceCollection services,
        Action<RedisEventBusOptions>? configure = null)
    {
        RedisEventBusOptions options = new();
        configure?.Invoke(options);

        services.TryAddSingleton(options);
        services.AddSingleton(sp =>
            new RedisEventBus(
                sp.GetService<IConnectionMultiplexer>(),
                sp.GetService<RedisEventBusOptions>() ?? options,
                sp,
                sp.GetService<ILogger<RedisEventBus>>()
            ));

        services.AddSingleton<IEventBus>(sp => sp.GetRequiredService<RedisEventBus>());
        services.AddHostedService(sp => sp.GetRequiredService<RedisEventBus>());
        services.AddHostedService<EventBusSubscriptionInitializerHostedService>();

        return services;
    }

    /// <summary>
    ///     Registers a strongly typed event handler for <typeparamref name="TEvent" />.
    /// </summary>
    public static IServiceCollection AddEventHandler<TEvent, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler>(this IServiceCollection services)
        where TEvent : class, IDomainEvent
        where THandler : class, IEventHandler<TEvent>
    {
        services.AddScoped<IEventHandler<TEvent>, THandler>();
        services.AddScoped<THandler>();
        return services;
    }

    /// <summary>
    ///     Registers an event handler class for all <see cref="IEventHandler{TEvent}" /> interfaces it implements.
    /// </summary>
    public static IServiceCollection AddEventHandler<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.Interfaces)] THandler>(this IServiceCollection services)
        where THandler : class
    {
        Type handlerType = typeof(THandler);
        Type openHandlerInterface = typeof(IEventHandler<>);

        Type[] interfaces = handlerType.GetInterfaces();
        bool registeredAny = false;

        foreach (Type iface in interfaces)
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == openHandlerInterface)
            {
                services.AddScoped(iface, handlerType);
                registeredAny = true;
            }

        if (registeredAny) services.AddScoped<THandler>();

        return services;
    }
}

/// <summary>
///     Hosted service that scans registered <see cref="IEventHandler{TEvent}" /> services and subscribes them to
///     <see cref="IEventBus" />.
/// </summary>
[UnconditionalSuppressMessage("Trimming", "IL2026:MembersAnnotatedWithRequiresUnreferencedCode",
    Justification = "Event bus scans compiled domain event types at startup")]
[UnconditionalSuppressMessage("AOT", "IL3050:MembersAnnotatedWithRequiresDynamicCode",
    Justification = "Event bus subscription resolves registered generic handler services")]
internal sealed class EventBusSubscriptionInitializerHostedService(
    IServiceProvider serviceProvider,
    IEventBus eventBus,
    ILogger<EventBusSubscriptionInitializerHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Find all registered IEventHandler<T> service descriptors
            Type openHandlerType = typeof(IEventHandler<>);
            List<Type> eventTypes = [];

            // We can scan known domain events from NEXUS.Contracts.Events
            IEnumerable<Type> domainEventTypes = typeof(IDomainEvent).Assembly.GetTypes()
                .Where(t => !t.IsAbstract && !t.IsInterface && typeof(IDomainEvent).IsAssignableFrom(t));

            foreach (Type eventType in domainEventTypes)
            {
                Type handlerServiceType = openHandlerType.MakeGenericType(eventType);
                using IServiceScope scope = serviceProvider.CreateScope();
                IEnumerable<object?> handlers = scope.ServiceProvider.GetServices(handlerServiceType);

                if (handlers.Any())
                {
                    // Create an adapter subscriber on the event bus to route through IServiceProvider
                    MethodInfo subscribeMethod = typeof(EventBusSubscriptionInitializerHostedService)
                        .GetMethod(nameof(SubscribeDynamic), BindingFlags.NonPublic | BindingFlags.Instance)!
                        .MakeGenericMethod(eventType);

                    await ((Task)subscribeMethod.Invoke(this, [cancellationToken])!).ConfigureAwait(false);

                    logger.LogInformation(
                        "[EventBusSubscriptionInitializer] Subscribed event bus handler for domain event {EventType}",
                        eventType.Name);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[EventBusSubscriptionInitializer] Failed to initialize event subscriptions");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private async Task SubscribeDynamic<TEvent>(CancellationToken cancellationToken)
        where TEvent : class, IDomainEvent
    {
        await eventBus.SubscribeAsync(new ServiceProviderEventHandlerProxy<TEvent>(serviceProvider), cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>
///     Proxy event handler that resolves scoped <see cref="IEventHandler{TEvent}" /> instances from the root
///     <see cref="IServiceProvider" />.
/// </summary>
public sealed class ServiceProviderEventHandlerProxy<TEvent>(IServiceProvider serviceProvider) : IEventHandler<TEvent>
    where TEvent : class, IDomainEvent
{
    public async Task HandleAsync(TEvent @event, CancellationToken cancellationToken = default)
    {
        List<Type> handlerTypes;
        using (IServiceScope discoveryScope = serviceProvider.CreateScope())
        {
            handlerTypes =
            [
                .. discoveryScope.ServiceProvider.GetServices<IEventHandler<TEvent>>()
                    .Select(h => h.GetType())
                    .Distinct()
            ];
        }

        if (handlerTypes.Count == 0) return;

        List<Task> tasks = [];
        foreach (Type handlerType in handlerTypes)
            tasks.Add(Task.Run(async () =>
            {
                await using AsyncServiceScope handlerScope = serviceProvider.CreateAsyncScope();
                IEventHandler<TEvent>? handler = handlerScope.ServiceProvider.GetServices<IEventHandler<TEvent>>()
                    .FirstOrDefault(h => h.GetType() == handlerType);

                if (handler != null) await handler.HandleAsync(@event, cancellationToken).ConfigureAwait(false);
            }, cancellationToken));

        if (tasks.Count > 0) await Task.WhenAll(tasks).ConfigureAwait(false);
    }
}
