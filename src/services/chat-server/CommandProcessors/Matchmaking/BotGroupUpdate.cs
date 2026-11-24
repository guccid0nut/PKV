// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using Log = Serilog.Log;

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.Matchmaking;

/// <summary>
///     Handles a bot roster change made by the group leader in a bot-match group (Command 0x0E07).
/// </summary>
[ChatCommand(ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_BOT_GROUP_UPDATE)]
public class BotGroupUpdate(
    IMatchmakingService matchmakingService,
    IDiscordPresencePublisherService discordPresencePublisherService)
    : ISynchronousCommandProcessor<ChatSession>
{
    public void Process(ChatSession session, ChatBuffer buffer)
    {
        BotGroupUpdateRequestData requestData = new(buffer);

        if ((object?)session.Account is null) return;

        MatchmakingGroup? group = matchmakingService.GetMatchmakingGroupByMemberID(session.Account.ID);

        if (group is null)
        {
            Log.Debug(@"No Matchmaking Group Found For Account ID ""{AccountID}"" During Bot Update",
                session.Account.ID);
            return;
        }

        // Only The Group Leader May Change The Group's Bot Roster
        if (group.Leader.Account.ID != session.Account.ID)
        {
            Log.Warning(
                @"Account ID ""{AccountID}"" Attempted To Change The Bot Roster For Group {GroupGUID} But Is Not Leader",
                session.Account.ID, group.GUID);
            return;
        }

        group.UpdateBot(requestData.Team, requestData.Slot, requestData.BotName);
        discordPresencePublisherService.PublishGroupPresenceUpdate(group);
    }
}

file class BotGroupUpdateRequestData(ChatBuffer buffer)
{
    public byte[] CommandBytes { get; init; } = buffer.ReadCommandBytes();

    public byte Team { get; } = buffer.ReadInt8();

    public byte Slot { get; } = buffer.ReadInt8();

    public string BotName { get; } = buffer.ReadString();
}
