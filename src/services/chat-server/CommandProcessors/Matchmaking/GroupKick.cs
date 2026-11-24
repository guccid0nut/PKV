// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.Matchmaking;

[ChatCommand(ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_GROUP_KICK)]
public class GroupKick(
    IMatchmakingService matchmakingService,
    IDiscordPresencePublisherService discordPresencePublisherService)
    : ISynchronousCommandProcessor<ChatSession>
{
    public void Process(ChatSession session, ChatBuffer buffer)
    {
        GroupKickRequestData requestData = new(buffer);

        // Retrieve current group
        MatchmakingGroup? group = matchmakingService.GetMatchmakingGroupByMemberID(session.Account.ID);
        if (group == null) return; // Silent fail if not in group

        // Only leader can kick?
        // Logic to kick member at slot:
        group.Kick(matchmakingService, session, requestData.SlotNumber);

        // Notify presence update for remaining group members
        discordPresencePublisherService.PublishGroupPresenceUpdate(group);
    }
}
