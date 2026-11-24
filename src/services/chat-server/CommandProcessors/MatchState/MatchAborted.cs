// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.MatchState;

[ChatCommand(ChatProtocol.GameServerToChatServer.NET_CHAT_GS_MATCH_ABORTED)]
internal class MatchAborted(
    IChatContext chatContext,
    ITelemetryPublisherService? telemetryPublisher = null,
    IQueueArcadeService? queueArcadeService = null)
    : ISynchronousCommandProcessor<ChatSession>
{
    public void Process(ChatSession session, ChatBuffer buffer)
    {
        MatchAbortedRequestData requestData = new(buffer);

        Log.Warning("Match Aborted - MatchupID: {MatchupID}, Reason: {Reason}", requestData.MatchupID,
            requestData.Reason);

        if (telemetryPublisher != null) _ = telemetryPublisher.OnMatchEndedAsync(requestData.MatchupID);

        if (session.ServerMetadata?.IsQueueArcade == true)
        {
            queueArcadeService?.OnArcadeMatchEnded(session, requestData.MatchupID, $"match_aborted:{requestData.Reason}");
        }

        foreach (ChatSession clientSession in chatContext.ClientChatSessions.Values)
        {
            if (clientSession.ClientMetadata != null && clientSession.ClientMetadata.MatchID == requestData.MatchupID)
            {
                clientSession.ClientMetadata.MatchID = null;
                clientSession.ClientMetadata.MatchName = null;
                clientSession.ClientMetadata.MatchServerAddress = null;
                clientSession.ClientMetadata.LastKnownClientState = ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_CONNECTED;

                Log.Information(
                    "[MatchAborted] Resetting status to CONNECTED for client {AccountName} (ID: {AccountID})",
                    clientSession.Account.Name, clientSession.Account.ID);

                clientSession.BroadcastConnectionStatusUpdate(ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_CONNECTED);
            }
        }
    }
}
