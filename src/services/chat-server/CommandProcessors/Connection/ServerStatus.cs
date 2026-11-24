// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.Connection;

[ChatCommand(ChatProtocol.GameServerToChatServer.NET_CHAT_GS_STATUS)]
internal class ServerStatus(
    IDatabase distributedCacheStore,
    IChatContext chatContext,
    IDiscordPresencePublisherService discordPublisher,
    IQueueArcadeService? queueArcadeService = null) : IAsynchronousCommandProcessor<ChatSession>
{
    public async Task Process(ChatSession session, ChatBuffer buffer)
    {
        ServerStatusRequestData requestData = new(buffer);

        Log.Debug(
            @"Received Status Update From Server ID ""{ServerID}"" - Name: ""{Name}"", Address: ""{Address}:{Port}"", Location: ""{Location}"", Status: {Status}, MatchID: {MatchID}, GameTime: {GameTime}",
            requestData.ServerID, requestData.Name, requestData.Address, requestData.Port, requestData.Location,
            requestData.Status, requestData.MatchID, requestData.CurrentGameTime);

        // Update Session Metadata
        // This is CRITICAL for MatchmakingService to find IDLE servers and for RequestGameInfo (0x00B5) / PlayerSpectate (0x00B8)
        if ((object?)session.ServerMetadata is not null)
        {
            if (string.IsNullOrWhiteSpace(session.ServerMetadata.Location) &&
                !string.IsNullOrWhiteSpace(requestData.Location))
            {
                session.ServerMetadata.Location = requestData.Location.Trim();
            }
            session.ServerMetadata.IsQueueArcade |=
                MatchServer.IsQueueArcadePort(requestData.Port) ||
                MatchServer.IsQueueArcadePort(session.ServerMetadata.LocalPort);

            // Prevent an in-flight pre-load IDLE heartbeat from K2 clobbering SERVER_STATUS_LOADING while CreateMatch is booting
            bool preservePendingArcadeLoad = session.ServerMetadata.IsQueueArcade &&
                                             session.ServerMetadata.Status == ChatProtocol.ServerStatus.SERVER_STATUS_LOADING &&
                                             requestData.Status == ChatProtocol.ServerStatus.SERVER_STATUS_IDLE &&
                                             requestData.MatchID <= 0;
            if (!preservePendingArcadeLoad)
            {
                session.ServerMetadata.Status = requestData.Status;
                session.ServerMetadata.MatchID = requestData.MatchID;
                session.ServerMetadata.GameName = requestData.GameName;
                session.ServerMetadata.MapName = requestData.MapName;
                session.ServerMetadata.GameMode = requestData.GameMode;
                session.ServerMetadata.TeamSize = requestData.TeamSize;
            }

            session.ServerMetadata.CurrentGameTime = requestData.CurrentGameTime;
            session.ServerMetadata.CurrentGamePhase = requestData.CurrentGamePhase;
            session.ServerMetadata.ClientCount = requestData.ClientCount;
            session.ServerMetadata.CasualMode = requestData.CasualMode;
            session.ServerMetadata.Official = requestData.Official;
            session.ServerMetadata.Gated = requestData.Gated;
            session.ServerMetadata.LegionTeamInformation = requestData.LegionTeamInformation;
            session.ServerMetadata.HellbourneTeamInformation = requestData.HellbourneTeamInformation;
            session.ServerMetadata.PlayerInformation =
            [
                requestData.PlayerInformation_01,
                requestData.PlayerInformation_02,
                requestData.PlayerInformation_03,
                requestData.PlayerInformation_04,
                requestData.PlayerInformation_05,
                requestData.PlayerInformation_06,
                requestData.PlayerInformation_07,
                requestData.PlayerInformation_08,
                requestData.PlayerInformation_09,
                requestData.PlayerInformation_10
            ];

            if (session.ServerMetadata.IsQueueArcade)
            {
                queueArcadeService?.NotifyArcadeServerStatus(
                    requestData.ServerID,
                    requestData.MatchID,
                    requestData.Status,
                    requestData.ClientCount);
            }
        }

        if (requestData.MatchID > 0)
        {
            MatchStartData? matchData = await distributedCacheStore.GetMatchStartData(requestData.MatchID);
            bool isNew = matchData is null;

            matchData ??= new MatchStartData
            {
                MatchID = requestData.MatchID,
                MatchName = requestData.GameName,
                ServerID = requestData.ServerID,
                ServerName = requestData.Name,
                HostAccountName = requestData.HostName,
                Map = requestData.MapName,
                Version = requestData.ServerVersion,
                IsCasual = requestData.CasualMode,
                MatchType = 0,
                MatchMode = requestData.GameMode,
                ConnectedPlayersCount = requestData.ClientCount,
                MaximumPlayersCount = requestData.TeamSize * 2
            };

            bool shouldBeStarted = requestData.CurrentGameTime > 0 || requestData.CurrentGamePhase >= 2;
            bool startChanged = matchData.IsStarted != shouldBeStarted;
            bool playersChanged = requestData.ClientCount > 0 &&
                                  matchData.ConnectedPlayersCount != requestData.ClientCount;

            if (isNew || startChanged || playersChanged)
            {
                if (!string.IsNullOrWhiteSpace(requestData.HostName)) matchData.HostAccountName = requestData.HostName;

                matchData.IsStarted = shouldBeStarted;
                matchData.Phase = shouldBeStarted ? 2 : 0;
                if (requestData.ClientCount > 0) matchData.ConnectedPlayersCount = requestData.ClientCount;

                await distributedCacheStore.SetMatchStartData(matchData);

                foreach (ChatSession clientSession in chatContext.ClientChatSessions.Values)
                    if (clientSession.ClientMetadata.MatchID == matchData.MatchID)
                        discordPublisher.PublishPresenceUpdate(clientSession);
            }
        }
    }
}
