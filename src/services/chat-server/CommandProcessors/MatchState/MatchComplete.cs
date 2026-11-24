// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.MatchState;

[ChatCommand(ChatProtocol.GameServerToChatServer.NET_CHAT_GS_MATCH_ENDED)]
internal class MatchComplete(
    ILogger<MatchComplete> logger,
    IMatchmakingService matchmakingService,
    IChatContext chatContext,
    ITelemetryPublisherService? telemetryPublisher = null,
    IQueueArcadeService? queueArcadeService = null)
    : ISynchronousCommandProcessor<ChatSession>
{
    public void Process(ChatSession session, ChatBuffer buffer)
    {
        try
        {
            MatchCompleteRequestData requestData = new(buffer);

            // Queue Arcade warm matches are ephemeral drop-in sessions; never touch real MatchID, IsInGame, or online status
            // for players who rotated through the Arcade roster via 0x0517 and may now be playing their real match!
            if (session.ServerMetadata?.IsQueueArcade == true)
            {
                logger.LogInformation(
                    "[MatchComplete] Queue Arcade match {MatchID} ended on ServerID={ServerID}; isolating from real player match states.",
                    requestData.MatchID,
                    session.ServerMetadata.ServerID);
                queueArcadeService?.OnArcadeMatchEnded(session, requestData.MatchID, "match_complete");
                return;
            }

            if (telemetryPublisher != null) _ = telemetryPublisher.OnMatchEndedAsync(requestData.MatchID);

            // Update Player Availability States (Mark Players As Available After Match Ends)
            foreach (int accountID in requestData.AccountIDs)
            {
                MatchmakingGroup? group = matchmakingService.GetMatchmakingGroupByMemberID(accountID);
                MatchmakingGroupMember? member = group?.Members.SingleOrDefault(m => m.Account.ID == accountID);
                if (member is { IsInGame: true })
                {
                    member.IsInGame = false;
                    group?.MulticastUpdate(accountID, ChatProtocol.TMMUpdateType.TMM_PARTIAL_GROUP_UPDATE);
                }
            }

            // Update Connected Client Sessions back to CONNECTED (Online)
            foreach (ChatSession clientSession in chatContext.ClientChatSessions.Values)
            {
                if (clientSession.ClientMetadata != null && (clientSession.ClientMetadata.MatchID == requestData.MatchID || requestData.AccountIDs.Contains(clientSession.Account.ID)))
                {
                    clientSession.ClientMetadata.MatchID = null;
                    clientSession.ClientMetadata.MatchName = null;
                    clientSession.ClientMetadata.MatchServerAddress = null;
                    clientSession.ClientMetadata.LastKnownClientState = ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_CONNECTED;

                    logger.LogInformation(
                        "[MatchComplete] Resetting status to CONNECTED for client {AccountName} (ID: {AccountID})",
                        clientSession.Account.Name, clientSession.Account.ID);

                    clientSession.BroadcastConnectionStatusUpdate(ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_CONNECTED);
                }
            }

            // Mark Server As Available For New Match Allocation
            if (session.ServerMetadata is not null)
            {
                if (session.ServerMetadata.IsQueueArcade && queueArcadeService is not null)
                {
                    queueArcadeService.OnArcadeMatchEnded(session, requestData.MatchID, "match_complete");
                }
                else
                {
                    session.ServerMetadata.Status = ChatProtocol.ServerStatus.SERVER_STATUS_IDLE;
                }
            }
        }
        catch (InvalidDataException ex)
        {
            if (logger.IsEnabled(LogLevel.Warning))
                logger.LogWarning(ex,
                    "Failed to process MatchComplete (0x0515) packet: {Message}. Ignoring invalid packet.", ex.Message);
        }

        // NOTE: Statistics submission and MMR/PSR updates are handled by KONGOR.MasterServer/Controllers/StatsRequesterController.
    }
}
