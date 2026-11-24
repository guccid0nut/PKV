// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.MatchState;

[ChatCommand(ChatProtocol.GameServerToChatServer.NET_CHAT_GS_MATCH_ONGOING)]
internal class MatchStatus(
    IDatabase distributedCacheStore,
    IChatContext chatContext,
    IDiscordPresencePublisherService discordPublisher,
    IQueueArcadeService? queueArcadeService = null) : IAsynchronousCommandProcessor<ChatSession>
{
    public async Task Process(ChatSession session, ChatBuffer buffer)
    {
        MatchStatusRequestData requestData = new(buffer);

        if ((object?)session.ServerMetadata is not null)
        {
            session.ServerMetadata.CurrentGameTime = requestData.CurrentGameTime;
            session.ServerMetadata.CurrentGamePhase = requestData.Phase;
            if (requestData.MatchId > 0) session.ServerMetadata.MatchID = requestData.MatchId;

            if (session.ServerMetadata.IsQueueArcade)
            {
                queueArcadeService?.NotifyArcadeServerStatus(
                    session.ServerMetadata.ServerID,
                    requestData.MatchId > 0 ? requestData.MatchId : session.ServerMetadata.MatchID,
                    ChatProtocol.ServerStatus.SERVER_STATUS_ACTIVE,
                    session.ServerMetadata.ClientCount);
            }
        }

        if (requestData.MatchId > 0)
        {
            MatchStartData? matchData = await distributedCacheStore.GetMatchStartData(requestData.MatchId);
            if (matchData is not null)
            {
                int newPhase = requestData.CurrentGameTime > 0 || requestData.Phase >= 3 ? 2 :
                    requestData.Phase >= 2 ? 1 : 0;

                if (matchData.Phase != newPhase || !matchData.IsStarted)
                {
                    matchData.Phase = newPhase;
                    matchData.IsStarted = true;
                    await distributedCacheStore.SetMatchStartData(matchData);

                    MatchServer? server = await distributedCacheStore.GetMatchServerByID(matchData.ServerID);
                    string serverAddress = server is not null
                        ? $"{server.IPAddress}:{server.Port}"
                        : session.ServerMetadata != null && !string.IsNullOrEmpty(session.ServerMetadata.Address)
                            ? $"{session.ServerMetadata.Address}:{session.ServerMetadata.Port}"
                            : "";

                    foreach (ChatSession clientSession in chatContext.ClientChatSessions.Values)
                    {
                        if (clientSession.ClientMetadata != null && clientSession.ClientMetadata.MatchID == matchData.MatchID)
                        {
                            if (!string.IsNullOrEmpty(serverAddress))
                            {
                                clientSession.ClientMetadata.MatchServerAddress = serverAddress;
                            }
                            if (clientSession.ClientMetadata.LastKnownClientState != ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME)
                            {
                                clientSession.ClientMetadata.LastKnownClientState = ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME;
                                Log.Information(
                                    "[MatchStatus] Promoting client {AccountName} (ID: {AccountID}) to IN_GAME for Match {MatchID}",
                                    clientSession.Account.Name, clientSession.Account.ID, matchData.MatchID);
                                clientSession.BroadcastConnectionStatusUpdate(ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME, server);
                            }
                            discordPublisher.PublishPresenceUpdate(clientSession);
                        }
                    }
                }
            }
        }
    }
}
