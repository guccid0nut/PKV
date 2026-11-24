// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.Events;

/// <summary>
///     Defines a handler for processing domain events of type <typeparamref name="TEvent" />.
/// </summary>
/// <typeparam name="TEvent">The specific domain event type to handle.</typeparam>
public interface IEventHandler<in TEvent> where TEvent : class, IDomainEvent
{
    /// <summary>
    ///     Asynchronously handles the specified domain event.
    /// </summary>
    /// <param name="event">The domain event payload to process.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous handle operation.</returns>
    Task HandleAsync(TEvent @event, CancellationToken cancellationToken = default);
}
