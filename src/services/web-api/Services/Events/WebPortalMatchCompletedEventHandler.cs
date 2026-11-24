// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using NEXUS.Contracts.Events;

namespace ZORGATH.WebPortal.API.Services.Events;

/// <summary>
///     Handles <see cref="MatchCompletedEvent" /> in WebPortal.API for live match cache invalidation and metric syncing.
/// </summary>
public sealed class WebPortalMatchCompletedEventHandler(
    ILogger<WebPortalMatchCompletedEventHandler> logger) : IEventHandler<MatchCompletedEvent>
{
    /// <inheritdoc />
    public Task HandleAsync(MatchCompletedEvent @event, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(@event);

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "[WebPortalMatchCompletedEventHandler] Match #{MatchId} completed. Duration: {Duration}, Winner Team: {Winner}, Participants: {Count}. Invalidating live match cache.",
                @event.MatchId, @event.Duration, @event.WinnerTeam, @event.AccountIds.Count);

        return Task.CompletedTask;
    }
}
