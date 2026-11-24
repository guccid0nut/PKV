// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.Matchmaking;

[ChatCommand(ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_GROUP_LEAVE)]
public class GroupLeave(
    IMatchmakingService matchmakingService,
    IDiscordPresencePublisherService discordPresencePublisherService)
    : ISynchronousCommandProcessor<ChatSession>
{
    public void Process(ChatSession session, ChatBuffer buffer)
    {
        if ((object?)session.Account is null) return;

        MatchmakingGroup? group = matchmakingService.GetMatchmakingGroupByMemberID(session.Account.ID);
        if (group != null)
        {
            group.RemoveMember(matchmakingService, session.Account.ID);
            discordPresencePublisherService.PublishGroupPresenceUpdate(group);
        }

        discordPresencePublisherService.PublishPresenceUpdate(session);
    }
}
