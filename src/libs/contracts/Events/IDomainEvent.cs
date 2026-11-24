// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.Events;

/// <summary>
///     Represents an asynchronous domain event carrying metadata about when and where an event occurred.
/// </summary>
public interface IDomainEvent
{
    /// <summary>
    ///     Gets the unique identifier for this specific event occurrence.
    /// </summary>
    Guid EventId { get; }

    /// <summary>
    ///     Gets the UTC timestamp when the domain event occurred.
    /// </summary>
    DateTimeOffset OccurredAt { get; }
}
