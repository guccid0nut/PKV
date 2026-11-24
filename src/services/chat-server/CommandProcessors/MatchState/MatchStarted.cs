// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.MatchState;

[ChatCommand(ChatProtocol.GameServerToChatServer.NET_CHAT_GS_MATCH_STARTED)]
public class MatchStarted(
    IDatabase distributedCacheStore,
    IChatContext chatContext,
    IDiscordPresencePublisherService discordPublisher,
    ITelemetryPublisherService? telemetryPublisher = null) : IAsynchronousCommandProcessor<ChatSession>
{
    public async Task Process(ChatSession session, ChatBuffer buffer)
    {
        MatchStartedRequestData requestData = new(buffer);

        Log.Information("Match Started - MatchupID: {MatchupID}", requestData.MatchupID);

        if (telemetryPublisher != null) await telemetryPublisher.OnMatchStartedAsync(requestData.MatchupID);

        MatchStartData? matchData = await distributedCacheStore.GetMatchStartData(requestData.MatchupID);
        if (matchData is not null)
        {
            matchData.Phase = 1; // Selecting a Hero phase
            matchData.IsStarted = true;
            await distributedCacheStore.SetMatchStartData(matchData);

            MatchServer? server = await distributedCacheStore.GetMatchServerByID(matchData.ServerID);
            string serverAddress = server is not null
                ? $"{server.IPAddress}:{server.Port}"
                : session.ServerMetadata != null && !string.IsNullOrEmpty(session.ServerMetadata.Address)
                    ? $"{session.ServerMetadata.Address}:{session.ServerMetadata.Port}"
                    : "";

            Log.Information(
                "[MatchStarted] MatchupID {MatchupID} (MatchID {MatchID}) on Server {ServerAddress} started. Updating client sessions...",
                requestData.MatchupID, matchData.MatchID, serverAddress);

            foreach (ChatSession clientSession in chatContext.ClientChatSessions.Values)
            {
                if (clientSession.ClientMetadata != null && (clientSession.ClientMetadata.MatchID == matchData.MatchID || clientSession.ClientMetadata.MatchID == requestData.MatchupID))
                {
                    clientSession.ClientMetadata.MatchID = matchData.MatchID;
                    clientSession.ClientMetadata.MatchName = matchData.MatchName;
                    if (!string.IsNullOrEmpty(serverAddress))
                    {
                        clientSession.ClientMetadata.MatchServerAddress = serverAddress;
                    }
                    clientSession.ClientMetadata.LastKnownClientState = ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME;

                    Log.Information(
                        "[MatchStarted] Broadcasting IN_GAME for client {AccountName} (ID: {AccountID}) in Match {MatchID} (Server: {ServerAddress})",
                        clientSession.Account.Name, clientSession.Account.ID, matchData.MatchID, clientSession.ClientMetadata.MatchServerAddress ?? "0.0.0.0:0");

                    clientSession.BroadcastConnectionStatusUpdate(ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME, server);
                    discordPublisher.PublishPresenceUpdate(clientSession);
                }
            }
        }
    }
}
