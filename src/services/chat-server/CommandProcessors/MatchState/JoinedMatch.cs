// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MERRICK.DatabaseContext.Extensions;

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.MatchState;

[ChatCommand(ChatProtocol.Command.CHAT_CMD_JOINED_GAME)]
internal class JoinedMatch(
    IDatabase distributedCacheStore,
    IDiscordPresencePublisherService discordPublisher,
    IMatchmakingService matchmakingService) : IAsynchronousCommandProcessor<ChatSession>
{
    public async Task Process(ChatSession session, ChatBuffer buffer)
    {
        JoinedMatchRequestData requestData = new(buffer);

        session.ClientMetadata.MatchName = requestData.MatchName;
        session.ClientMetadata.MatchID = requestData.MatchID;
        session.ClientMetadata.ClientAckedMatchId = requestData.MatchID;
        session.ClientMetadata.AwaitingArcadeDisconnectForRankedPull = false;
        session.ClientMetadata.AwaitingVictoryTransferFromServerId = null;
        session.ClientMetadata.VictoryTransferKickSent = false;
        session.ClientMetadata.PostArcadeVictoryMatchId = null;
        session.ClientMetadata.PostArcadeVictoryServerId = null;
        session.ClientMetadata.PostArcadeVictoryEndedAtUtc = null;
        if (session.ClientMetadata.PendingRankedMatchId == requestData.MatchID)
        {
            session.ClientMetadata.PendingRankedMatchId = null;
            session.ClientMetadata.PendingRankedServerAddress = null;
        }
        session.ClientMetadata.LastKnownClientState = ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME;
        session.ClientMetadata.LastArcadePushAttemptAtUtc = null;
        session.ClientMetadata.ArcadePushCooldownUntilUtc = null;

        MatchServer? server = null;
        if (requestData.MatchID > 0)
        {
            MatchStartData? matchData = await distributedCacheStore.GetMatchStartData(requestData.MatchID);
            if (matchData != null)
            {
                server = await distributedCacheStore.GetMatchServerByID(matchData.ServerID);
                if (server != null)
                {
                    session.ClientMetadata.MatchServerAddress = $"{server.IPAddress}:{server.Port}";
                    Log.Information(
                        "[JoinedMatch] Resolved MatchServerAddress {Address} (Local: {LocalAddress}:{LocalPort}) for account {AccountName} (ID: {AccountID}) in Match {MatchID}",
                        session.ClientMetadata.MatchServerAddress, server.LocalIPAddress ?? "null", server.LocalPort ?? 0,
                        session.Account.Name, session.Account.ID, requestData.MatchID);
                }
            }
        }

        if (server is null &&
            !string.IsNullOrEmpty(session.ClientMetadata.MatchServerAddress) &&
            session.ClientMetadata.MatchServerAddress != "0.0.0.0:0")
        {
            string[] parts = session.ClientMetadata.MatchServerAddress.Split(':');
            if (parts.Length == 2 && int.TryParse(parts[1], out int port))
            {
                server = await distributedCacheStore.GetMatchServerByIPAddressAndPort(parts[0], port);
            }
        }

        Log.Information(
            "[JoinedMatch] Account {AccountName} (ID: {AccountID}) joined game '{MatchName}' (MatchID: {MatchID}, Server: {ServerAddress})",
            session.Account.Name, session.Account.ID, requestData.MatchName, requestData.MatchID,
            session.ClientMetadata.MatchServerAddress ?? "0.0.0.0:0");

        session.BroadcastConnectionStatusUpdate(ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME, server);

        MatchmakingGroup? group = matchmakingService.GetMatchmakingGroupByMemberID(session.Account.ID);
        discordPublisher.PublishPresenceUpdate(session, group);
    }
}
