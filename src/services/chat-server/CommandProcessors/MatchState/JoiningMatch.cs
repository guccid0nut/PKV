// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.MatchState;

[ChatCommand(ChatProtocol.Command.CHAT_CMD_JOINING_GAME)]
internal class JoiningMatch(
    IDiscordPresencePublisherService discordPublisher,
    IMatchmakingService matchmakingService) : ISynchronousCommandProcessor<ChatSession>
{
    public void Process(ChatSession session, ChatBuffer buffer)
    {
        JoiningMatchRequestData requestData = new(buffer);

        session.ClientMetadata.LastKnownClientState = ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_JOINING_GAME;
        session.ClientMetadata.MatchServerAddress = requestData.ServerAddress;
        session.ClientMetadata.ClientAckedServerAddress = requestData.ServerAddress;
        session.ClientMetadata.AwaitingArcadeDisconnectForRankedPull = false;
        session.ClientMetadata.AwaitingVictoryTransferFromServerId = null;
        session.ClientMetadata.VictoryTransferKickSent = false;
        session.ClientMetadata.PostArcadeVictoryMatchId = null;
        session.ClientMetadata.PostArcadeVictoryServerId = null;
        session.ClientMetadata.PostArcadeVictoryEndedAtUtc = null;
        if (!string.IsNullOrEmpty(session.ClientMetadata.PendingRankedServerAddress) &&
            string.Equals(requestData.ServerAddress, session.ClientMetadata.PendingRankedServerAddress, StringComparison.OrdinalIgnoreCase))
        {
            session.ClientMetadata.PendingRankedMatchId = null;
            session.ClientMetadata.PendingRankedServerAddress = null;
        }

        session.BroadcastConnectionStatusUpdate(ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_JOINING_GAME);

        MatchmakingGroup? group = matchmakingService.GetMatchmakingGroupByMemberID(session.Account.ID);
        discordPublisher.PublishPresenceUpdate(session, group);
    }
}
