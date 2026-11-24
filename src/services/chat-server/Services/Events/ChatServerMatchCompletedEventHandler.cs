// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using NEXUS.Contracts.Events;

namespace TRANSMUTANSTEIN.ChatServer.Services.Events;

/// <summary>
///     Handles <see cref="MatchCompletedEvent" /> in ChatServer by updating player availability states.
/// </summary>
public sealed class ChatServerMatchCompletedEventHandler(
    IMatchmakingService matchmakingService,
    ILogger<ChatServerMatchCompletedEventHandler> logger) : IEventHandler<MatchCompletedEvent>
{
    /// <inheritdoc />
    public Task HandleAsync(MatchCompletedEvent @event, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(@event);

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "[ChatServerMatchCompletedEventHandler] Received MatchCompletedEvent for Match #{MatchId}, GameMode '{GameMode}', Account Count: {Count}",
                @event.MatchId, @event.GameMode, @event.AccountIds.Count);

        int updatedCount = 0;
        foreach (int accountID in @event.AccountIds)
        {
            MatchmakingGroup? group = matchmakingService.GetMatchmakingGroupByMemberID(accountID);
            MatchmakingGroupMember? member = group?.Members.SingleOrDefault(m => m.Account.ID == accountID);
            if (member is { IsInGame: true })
            {
                member.IsInGame = false;
                group?.MulticastUpdate(accountID, ChatProtocol.TMMUpdateType.TMM_PARTIAL_GROUP_UPDATE);
                updatedCount++;
            }
        }

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "[ChatServerMatchCompletedEventHandler] Reset in-game status for {UpdatedCount} players from Match #{MatchId}",
                updatedCount, @event.MatchId);

        return Task.CompletedTask;
    }
}
