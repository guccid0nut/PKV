// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.MatchState;

[ChatCommand(ChatProtocol.GameServerToChatServer.NET_CHAT_GS_ANNOUNCE_MATCH)]
internal class AnnounceMatch(
    IMatchmakingService matchmakingService,
    IQueueArcadeService? queueArcadeService = null) : ISynchronousCommandProcessor<ChatSession>
{
    public void Process(ChatSession session, ChatBuffer buffer)
    {
        AnnounceMatchRequestData requestData = new(buffer);

        Log.Information("Match Announced - MatchupID: {MatchupID}, MatchID: {MatchID}, Groups: {GroupCount}",
            requestData.MatchupID, requestData.MatchID, requestData.GroupIDs.Count);

        if (session.ServerMetadata is null) return;

        if (requestData.MatchupID < 0 || session.ServerMetadata.IsQueueArcade)
        {
            queueArcadeService?.ConfirmArcadeMatchReady(session, requestData.MatchupID, requestData.MatchID);
            return;
        }

        matchmakingService.ConfirmMatch(requestData.MatchupID, requestData.MatchID,
            session.ServerMetadata.Address, session.ServerMetadata.Port,
            session.ServerMetadata.LocalAddress, session.ServerMetadata.LocalPort);
    }
}
