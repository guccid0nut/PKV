// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.Matchmaking;

[ChatCommand(ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_GROUP_CREATE)]
public class GroupCreate(
    IMatchmakingService matchmakingService,
    IDiscordPresencePublisherService discordPresencePublisherService,
    MerrickContext merrick)
    : ISynchronousCommandProcessor<ChatSession>
{
    public void Process(ChatSession session, ChatBuffer buffer)
    {
        GroupCreateRequestData requestData = new(buffer);

        MatchmakingGroup group = MatchmakingGroup
            .Create(matchmakingService, session, merrick, requestData.ToGroupInformation());

        discordPresencePublisherService.PublishGroupPresenceUpdate(group);
    }
}
