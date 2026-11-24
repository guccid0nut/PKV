// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using System.Diagnostics.CodeAnalysis;

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.Matchmaking;

[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "Matches legacy protocol command name")]
[ChatCommand(ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_GROUP_LEAVE_QUEUE)]
internal class GroupLeaveQueue(
    IMatchmakingService matchmakingService,
    IDiscordPresencePublisherService discordPresencePublisherService,
    IQueueArcadeService? queueArcadeService = null)
    : ISynchronousCommandProcessor<ChatSession>
{
    public void Process(ChatSession session, ChatBuffer buffer)
    {
        GroupLeaveQueueRequestData requestData = new(buffer);

        MatchmakingGroup? group = matchmakingService.GetMatchmakingGroupByMemberID(session.Account.ID);

        if (group is null) return;

        // Remove Group From Queue
        foreach (MatchmakingGroupMember member in group.Members)
        {
            if (member.Session?.ClientMetadata is not null)
            {
                member.Session.ClientMetadata.PauseArcadeUntilNextQueueUtc = null;
                member.Session.ClientMetadata.LastVoluntarilyLeftArcadeMatchId = null;
            }
            queueArcadeService?.ReleasePlayerFromArcade(member.Account.ID, "group_left_queue", pauseCurrentQueueWait: false);
        }

        group.LeaveQueue(session.Account.ID);

        // Send Full Group Update To Reflect New Player States
        group.MulticastUpdate(session.Account.ID, ChatProtocol.TMMUpdateType.TMM_FULL_GROUP_UPDATE);

        discordPresencePublisherService.PublishGroupPresenceUpdate(group);
        discordPresencePublisherService.PublishPresenceUpdate(session, forceReset: true);
    }
}
