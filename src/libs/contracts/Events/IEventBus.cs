// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.Events;

/// <summary>
///     Provides asynchronous publishing, subscribing, and unsubscribing capabilities for cross-service domain events.
/// </summary>
public interface IEventBus
{
    /// <summary>
    ///     Publishes a domain event asynchronously to all registered local and distributed subscribers.
    /// </summary>
    /// <typeparam name="TEvent">The domain event type.</typeparam>
    /// <param name="event">The domain event instance.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous publication operation.</returns>
    Task PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default)
        where TEvent : class, IDomainEvent;

    /// <summary>
    ///     Subscribes an event handler instance to domain events of type <typeparamref name="TEvent" />.
    /// </summary>
    /// <typeparam name="TEvent">The domain event type.</typeparam>
    /// <param name="handler">The handler instance to execute upon event delivery.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous subscription operation.</returns>
    Task SubscribeAsync<TEvent>(IEventHandler<TEvent> handler, CancellationToken cancellationToken = default)
        where TEvent : class, IDomainEvent;

    /// <summary>
    ///     Unsubscribes a previously registered event handler instance from domain events of type
    ///     <typeparamref name="TEvent" />.
    /// </summary>
    /// <typeparam name="TEvent">The domain event type.</typeparam>
    /// <param name="handler">The handler instance to remove.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous unsubscribe operation.</returns>
    Task UnsubscribeAsync<TEvent>(IEventHandler<TEvent> handler, CancellationToken cancellationToken = default)
        where TEvent : class, IDomainEvent;
}
