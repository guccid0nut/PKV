// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.MatchState;

[ChatCommand(ChatProtocol.Command.CHAT_CMD_LEFT_GAME)]
internal class LeftMatch(
    IDiscordPresencePublisherService discordPublisher,
    IMatchmakingService matchmakingService,
    IQueueArcadeService? queueArcadeService = null) : ISynchronousCommandProcessor<ChatSession>
{
    public void Process(ChatSession session, ChatBuffer buffer)
    {
        if (queueArcadeService?.ReleasePlayerFromArcade(session.Account.ID, "client_left_match", pauseCurrentQueueWait: true) == true)
        {
            Log.Information(
                "[LeftMatch] Suppressing stale LeftMatch state reset for AccountID={AccountID} ('{AccountName}') during active Queue Arcade server-to-server Victory transfer (ActiveArcadeMatchID={ActiveArcadeMatchID}, ActiveArcadeServerID={ActiveArcadeServerID})",
                session.Account.ID,
                session.Account.Name,
                session.ClientMetadata.ActiveArcadeMatchId ?? -1,
                session.ClientMetadata.ActiveArcadeServerId ?? -1);
            return;
        }

        session.ClientMetadata.ClientAckedMatchId = null;
        session.ClientMetadata.ClientAckedServerAddress = null;
        session.ClientMetadata.AwaitingArcadeDisconnectForRankedPull = false;
        if (session.ClientMetadata.PendingRankedMatchId is int pendingRankedMatchId)
        {
            session.ClientMetadata.MatchID = pendingRankedMatchId;
            session.ClientMetadata.MatchName = null;
            session.ClientMetadata.MatchServerAddress = session.ClientMetadata.PendingRankedServerAddress;
        }
        else
        {
            session.ClientMetadata.MatchID = null;
            session.ClientMetadata.MatchName = null;
            session.ClientMetadata.MatchServerAddress = null;
        }
        session.ClientMetadata.LastArcadePushAttemptAtUtc = null;
        session.ClientMetadata.LastKnownClientState = ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_CONNECTED;

        session.BroadcastConnectionStatusUpdate(ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_CONNECTED);

        MatchmakingGroup? group = matchmakingService.GetMatchmakingGroupByMemberID(session.Account.ID);
        discordPublisher.PublishPresenceUpdate(session, group, true);
    }
}
