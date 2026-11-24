// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.Matchmaking;

/// <summary>
///     Processes loading status updates for a matchmaking group member.
///     When all members reach 100% loading status it automatically joins the queue, complementing
///     <see cref="GroupJoinQueue" /> which handles explicit queue join requests from the group leader.
///     Both paths validusing TRANSMUTANSTEIN.ChatServer.Services;
/// </summary>
[ChatCommand(ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_GROUP_PLAYER_LOADING_STATUS)]
public class GroupPlayerLoadingStatus(
    IMatchmakingService matchmakingService,
    IDiscordPresencePublisherService discordPresencePublisherService)
    : ISynchronousCommandProcessor<ChatSession>
{
    public void Process(ChatSession session, ChatBuffer buffer)
    {
        GroupPlayerLoadingStatusRequestData requestData = new(buffer);

        MatchmakingGroup group = MatchmakingGroup
            .GetByMemberAccountID(matchmakingService, session.Account.ID)
            .SendLoadingStatusUpdate(session, requestData.LoadingPercent);

        discordPresencePublisherService.PublishGroupPresenceUpdate(group);
    }
}
