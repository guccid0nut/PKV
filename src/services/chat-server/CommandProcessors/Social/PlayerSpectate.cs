// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using ASPIRE.Common.Network;
using MERRICK.DatabaseContext.Enumerations;
using MerrickAccountExtensions = MERRICK.DatabaseContext.Extensions.AccountExtensions;
using Log = Serilog.Log;

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.Social;

/// <summary>
///     Handles two-sided player spectate and mentor requests (Command 0x00B8) across Client &lt;-&gt; ChatServer &lt;-&gt; MatchServer.
///     Wire format verified against k2_x64.dll (0x180702f90, 0x18069c600), server k2_x64.dll (0x9841c0), and server game_x64.dll (0x46150):
///     1. Request  (Client -&gt; ChatServer):      uint16(0x00B8) + uint8(0) + string(spectateeName) + uint8(mentorFlag) + string(serverInfo)
///     2. Relay    (ChatServer -&gt; MatchServer): uint16(0x00B8) + uint8(0) + string(requesterName) + string(targetName) + uint8(mentorFlag)
///     3. Callback (MatchServer -&gt; ChatServer): uint16(0x00B8) + uint8(1) + string(requesterName) + string(targetName) + uint8(PSRR_result)
///     4. Response (ChatServer -&gt; Client):      uint16(0x00B8) + uint8(1) + string(spectateeName) + uint8(PSRR_result) [0 trailing bytes]
/// </summary>
[ChatCommand(
    ChatProtocol.Command.CHAT_CMD_PLAYER_SPECTATE_REQUEST,
    AllowedRoles = ChatPeerRole.Client | ChatPeerRole.MatchServer)]
internal sealed class PlayerSpectate(
    IChatContext chatContext,
    IDatabase? distributedCacheStore = null,
    MerrickContext? merrick = null,
    TimeProvider? timeProvider = null) : IAsynchronousCommandProcessor<ChatSession>
{
    private static readonly TimeSpan PendingRequestLifetime = TimeSpan.FromSeconds(45);

    private static readonly ConcurrentDictionary<string, PendingSpectateRequest> PendingRequests =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task Process(ChatSession session, ChatBuffer buffer)
    {
        _ = buffer.ReadCommandBytes();
        byte subType = buffer.ReadInt8();

        if (subType == (byte)ChatProtocol.PlayerSpectateRequest.PLAYER_SPECTATE_REQUEST)
        {
            await ProcessClientSpectateRequestAsync(session, buffer);
            return;
        }

        if (subType == (byte)ChatProtocol.PlayerSpectateRequest.PLAYER_SPECTATE_REQUEST_RESPONSE)
        {
            await ProcessMatchServerSpectateResponseAsync(session, buffer);
            return;
        }

        Log.Warning(
            "[PlayerSpectate] Ignoring unknown 0x00B8 SubType={SubType} on Session {SessionID} (PeerRole={PeerRole})",
            subType, session.ID, session.PeerRole);
    }

    private async Task ProcessClientSpectateRequestAsync(ChatSession session, ChatBuffer buffer)
    {
        if (session.PeerRole is ChatPeerRole.MatchServer or ChatPeerRole.ServerManager)
        {
            Log.Warning(
                "[SECURITY-ALERT] Blocked non-client 0x00B8 SubType=0 spectate request from Session {SessionID} (PeerRole={PeerRole})",
                session.ID, session.PeerRole);
            return;
        }

        if ((object?)session.Account is null) return;

        string rawSpectateeName = buffer.ReadString();
        byte mentorFlag = buffer.ReadInt8();
        _ = buffer.ReadString(); // trailing serverInfo (often empty on request)

        string strippedRequesterName = StripClanTag(session.Account.Name);
        string strippedSpectateeName = StripClanTag(rawSpectateeName);

        bool isMentorRequest = mentorFlag != 0;

        if (string.IsNullOrWhiteSpace(strippedSpectateeName) ||
            string.Equals(strippedRequesterName, strippedSpectateeName, StringComparison.OrdinalIgnoreCase))
        {
            Log.Warning(
                "[PlayerSpectate] Denied self/empty spectate request from '{Requester}' (RawTarget='{RawTarget}', Mentor={Mentor})",
                session.Account.Name, rawSpectateeName, isMentorRequest);
            SendSpectateResponse(session, strippedSpectateeName, ChatProtocol.PlayerSpectateRequestResponse.PSRR_DENY);
            return;
        }

        ChatSession? targetSession = chatContext.ClientChatSessions.Values
            .FirstOrDefault(s => (object?)s.Account is not null &&
                                 string.Equals(s.Account.Name, strippedSpectateeName, StringComparison.OrdinalIgnoreCase));

        if (targetSession is null ||
            targetSession.ClientMetadata is null ||
            targetSession.ClientMetadata.LastKnownClientState != ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME ||
            string.IsNullOrWhiteSpace(targetSession.ClientMetadata.MatchServerAddress) ||
            targetSession.ClientMetadata.MatchServerAddress == "0.0.0.0:0")
        {
            Log.Warning(
                "[PlayerSpectate] Denied '{Requester}' -> '{Target}' (Mentor={Mentor}): target offline, not IN_GAME, or server address unavailable",
                session.Account.Name, strippedSpectateeName, isMentorRequest);
            SendSpectateResponse(session, strippedSpectateeName, ChatProtocol.PlayerSpectateRequestResponse.PSRR_DENY);
            return;
        }

        string canonicalTargetName = StripClanTag(targetSession.Account.Name);

        if (!session.Account.IsStaff)
        {
            if (targetSession.ClientMetadata.ClientChatModeState is ChatProtocol.ChatModeType.CHAT_MODE_INVISIBLE)
            {
                Log.Information(
                    "[PlayerSpectate] Denied '{Requester}' -> '{Target}' (Mentor={Mentor}): target is CHAT_MODE_INVISIBLE",
                    session.Account.Name, targetSession.Account.Name, isMentorRequest);
                SendSpectateResponse(session, strippedSpectateeName, ChatProtocol.PlayerSpectateRequestResponse.PSRR_DENY);
                return;
            }

            if (targetSession.Account.HasIgnoredPeer(session.Account.ID) ||
                targetSession.Account.HasBannedPeer(session.Account.ID))
            {
                Log.Information(
                    "[PlayerSpectate] Denied '{Requester}' (ID: {RequesterID}) -> '{Target}' (ID: {TargetID}, Mentor={Mentor}): requester is ignored or banned by target",
                    session.Account.Name, session.Account.ID, targetSession.Account.Name, targetSession.Account.ID, isMentorRequest);
                SendSpectateResponse(session, strippedSpectateeName, ChatProtocol.PlayerSpectateRequestResponse.PSRR_DENY);
                return;
            }

            if (isMentorRequest)
            {
                bool isMutualFriend = await HasMutualAcceptedFriendshipAsync(
                    session.Account,
                    targetSession.Account,
                    merrick);

                if (!isMutualFriend)
                {
                    Log.Information(
                        "[PlayerSpectate] Denied MENTOR request '{Requester}' (ID: {RequesterID}) -> '{Target}' (ID: {TargetID}): mutual friendship required",
                        session.Account.Name, session.Account.ID, targetSession.Account.Name, targetSession.Account.ID);
                    SendSpectateResponse(session, strippedSpectateeName, ChatProtocol.PlayerSpectateRequestResponse.PSRR_DENY);
                    return;
                }
            }
        }

        MatchStartData? matchData = null;
        MatchServer? matchServer = null;
        int parsedPort = 0;
        string[] addressParts = targetSession.ClientMetadata.MatchServerAddress.Split(':');
        if (addressParts.Length == 2)
        {
            _ = int.TryParse(addressParts[1], out parsedPort);
        }

        if (distributedCacheStore is not null)
        {
            if (targetSession.ClientMetadata.MatchID is > 0)
            {
                matchData = await distributedCacheStore.GetMatchStartData(targetSession.ClientMetadata.MatchID.Value);
                if (matchData is not null)
                {
                    matchServer = await distributedCacheStore.GetMatchServerByID(matchData.ServerID);
                }
            }

            if (matchServer is null && addressParts.Length == 2 && parsedPort > 0)
            {
                matchServer = await distributedCacheStore.GetMatchServerByIPAddressAndPort(addressParts[0], parsedPort);
            }
        }

        ChatSession? matchServerSession = ResolveServerSession(
            chatContext,
            matchData?.ServerID ?? matchServer?.ID,
            targetSession.ClientMetadata.MatchID,
            targetSession.ClientMetadata.MatchServerAddress,
            targetSession.Account.ID,
            canonicalTargetName);

        MatchServerChatSessionMetadata? serverMeta = matchServerSession?.ServerMetadata;

        if (!session.Account.IsStaff)
        {
            bool isTargetInQueueArcade =
                matchServer?.IsQueueArcadeServer() == true ||
                serverMeta?.IsQueueArcade == true ||
                targetSession.ClientMetadata.ActiveArcadeServerId.HasValue ||
                (parsedPort > 0 && MatchServer.IsQueueArcadePort(parsedPort));

            if (isTargetInQueueArcade)
            {
                Log.Information(
                    "[PlayerSpectate] Denied non-staff spectate request '{Requester}' (ID: {RequesterID}, Mentor={Mentor}) -> '{Target}' (ID: {TargetID}): target is in Queue Arcade (ServerID={ServerID}, MatchID={MatchID})",
                    session.Account.Name,
                    session.Account.ID,
                    isMentorRequest,
                    targetSession.Account.Name,
                    targetSession.Account.ID,
                    matchServer?.ID ?? serverMeta?.ServerID ?? targetSession.ClientMetadata.ActiveArcadeServerId ?? -1,
                    targetSession.ClientMetadata.MatchID ?? 0);
                SendSpectateResponse(session, strippedSpectateeName, ChatProtocol.PlayerSpectateRequestResponse.PSRR_DENY);
                return;
            }

            int maxPlayers = matchData is { MaximumPlayersCount: > 0 }
                ? matchData.MaximumPlayersCount
                : (serverMeta?.TeamSize ?? 5) * 2;
            int maxSpectators = serverMeta?.MaxSpectators ?? 10;
            int connectedClients = Math.Max(matchData?.ConnectedPlayersCount ?? 0, serverMeta?.ClientCount ?? 0);

            if (maxSpectators >= 0 && connectedClients >= maxPlayers + maxSpectators)
            {
                Log.Warning(
                    "[PlayerSpectate] Denied '{Requester}' -> '{Target}' (Mentor={Mentor}): server spectator capacity full (Connected={Connected}, MaxPlayers={MaxPlayers}, MaxSpectators={MaxSpectators})",
                    session.Account.Name, targetSession.Account.Name, isMentorRequest, connectedClients, maxPlayers, maxSpectators);
                SendSpectateResponse(session, strippedSpectateeName, ChatProtocol.PlayerSpectateRequestResponse.PSRR_FULL_SERVER);
                return;
            }
        }

        if (matchServerSession is null)
        {
            Log.Warning(
                "[PlayerSpectate] Denied '{Requester}' -> '{Target}' (Mentor={Mentor}): live MatchServer chat session unavailable for MatchID={MatchID}, StoredAddr='{StoredAddr}'",
                session.Account.Name,
                targetSession.Account.Name,
                isMentorRequest,
                targetSession.ClientMetadata.MatchID,
                targetSession.ClientMetadata.MatchServerAddress);
            SendSpectateResponse(session, strippedSpectateeName, ChatProtocol.PlayerSpectateRequestResponse.PSRR_DENY);
            return;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        PruneExpiredPendingRequests(now);

        // Native client k2_x64.dll 0x18069c600 compares the 0x00B8 SubType=1 spectateeName CASE-SENSITIVELY
        // (raw wchar_t equality) against StripClanTag(CHost->m_sPendingPlayerSpectate) stored at 0x180702f90.
        // We MUST echo back strippedSpectateeName (exact caller casing) on 0x00B8 SubType=1, otherwise 'F' != 'f'
        // causes CChatManager to drop PSRR_ALLOW silently without calling CChatManager::JoinGame (0x180702d30).
        string pendingKey = BuildPendingKey(matchServerSession.ID, strippedRequesterName, canonicalTargetName);
        PendingRequests[pendingKey] = new PendingSpectateRequest(
            RequesterAccountId: session.Account.ID,
            RequesterWireName: strippedRequesterName,
            TargetAccountId: targetSession.Account.ID,
            TargetWireName: strippedSpectateeName,
            CanonicalTargetName: canonicalTargetName,
            IsMentor: isMentorRequest,
            ServerId: serverMeta?.ServerID ?? matchData?.ServerID ?? matchServer?.ID,
            MatchId: targetSession.ClientMetadata.MatchID,
            ExpiresAtUtc: now.Add(PendingRequestLifetime));

        // Native server game_x64.dll HandlePlayerSpectateRequest (0x46264 + 0x46288) compares
        // StripClanTag(LowerString(playerName)) case-sensitively against sTargetPlayerName without
        // lowercasing the incoming packet string; wire names on 0x00B8 SubType=0 must be lowercase.
        ChatBuffer relayRequest = new();
        relayRequest.WriteCommand(ChatProtocol.Command.CHAT_CMD_PLAYER_SPECTATE_REQUEST);
        relayRequest.WriteInt8((byte)ChatProtocol.PlayerSpectateRequest.PLAYER_SPECTATE_REQUEST);
        relayRequest.WriteString(strippedRequesterName.ToLowerInvariant());
        relayRequest.WriteString(canonicalTargetName.ToLowerInvariant());
        relayRequest.WriteInt8(isMentorRequest ? (byte)1 : (byte)0);

        bool dispatched = matchServerSession.Send(relayRequest);
        if (!dispatched)
        {
            PendingRequests.TryRemove(pendingKey, out _);
            Log.Warning(
                "[PlayerSpectate] Failed sending 0x00B8 SubType=0 relay to MatchServer Session {ServerSessionID} (ServerID={ServerID}) for '{Requester}' -> '{Target}'",
                matchServerSession.ID,
                serverMeta?.ServerID,
                strippedRequesterName,
                canonicalTargetName);
            SendSpectateResponse(session, strippedSpectateeName, ChatProtocol.PlayerSpectateRequestResponse.PSRR_DENY);
            return;
        }

        Log.Information(
            "[PlayerSpectate] Relayed 0x00B8 SubType=0 '{Requester}' (ID: {RequesterID}) -> MatchServer Session {ServerSessionID} (ServerID={ServerID}, MatchID={MatchID}) for '{Target}' (Mentor={Mentor})",
            strippedRequesterName,
            session.Account.ID,
            matchServerSession.ID,
            serverMeta?.ServerID ?? matchData?.ServerID,
            targetSession.ClientMetadata.MatchID,
            canonicalTargetName,
            isMentorRequest);
    }

    private async Task ProcessMatchServerSpectateResponseAsync(ChatSession matchServerSession, ChatBuffer buffer)
    {
        bool isMatchServer = matchServerSession.PeerRole == ChatPeerRole.MatchServer ||
                             (matchServerSession.PeerRole == ChatPeerRole.None &&
                              (object?)matchServerSession.ServerMetadata is not null);

        if (!isMatchServer)
        {
            Log.Warning(
                "[SECURITY-ALERT] Blocked non-MatchServer 0x00B8 SubType=1 response on Session {SessionID} (PeerRole={PeerRole}, AccountID={AccountID})",
                matchServerSession.ID,
                matchServerSession.PeerRole,
                (object?)matchServerSession.Account is not null ? matchServerSession.Account.ID : 0);
            return;
        }

        string rawRequesterName = buffer.ReadString();
        string rawTargetName = buffer.ReadString();
        byte resultByte = buffer.ReadInt8();

        string strippedRequesterName = StripClanTag(rawRequesterName);
        string strippedTargetName = StripClanTag(rawTargetName);

        string pendingKey = BuildPendingKey(matchServerSession.ID, strippedRequesterName, strippedTargetName);
        if (!PendingRequests.TryRemove(pendingKey, out PendingSpectateRequest? pending) ||
            pending.ExpiresAtUtc < _timeProvider.GetUtcNow())
        {
            Log.Warning(
                "[PlayerSpectate] Dropped unsolicited or expired 0x00B8 SubType=1 from MatchServer Session {ServerSessionID} (ServerID={ServerID}) for '{Requester}' -> '{Target}' (Result={Result})",
                matchServerSession.ID,
                matchServerSession.ServerMetadata?.ServerID,
                rawRequesterName,
                rawTargetName,
                resultByte);
            return;
        }

        ChatSession? requesterSession = chatContext.ClientChatSessions.Values
            .FirstOrDefault(s => (object?)s.Account is not null &&
                                 (s.Account.ID == pending.RequesterAccountId ||
                                  string.Equals(s.Account.Name, pending.RequesterWireName, StringComparison.OrdinalIgnoreCase)));

        if (requesterSession is null)
        {
            Log.Information(
                "[PlayerSpectate] Requester '{Requester}' (ID: {RequesterID}) disconnected before MatchServer 0x00B8 SubType=1 response arrived",
                pending.RequesterWireName,
                pending.RequesterAccountId);
            return;
        }

        ChatProtocol.PlayerSpectateRequestResponse result = (ChatProtocol.PlayerSpectateRequestResponse)resultByte;
        if (result == ChatProtocol.PlayerSpectateRequestResponse.PSRR_ALLOW)
        {
            ChatSession? targetSession = chatContext.ClientChatSessions.Values
                .FirstOrDefault(s => (object?)s.Account is not null &&
                                     (s.Account.ID == pending.TargetAccountId ||
                                      string.Equals(s.Account.Name, pending.CanonicalTargetName, StringComparison.OrdinalIgnoreCase)));

            MatchServer? matchServer = await ResolveMatchServerRecordAsync(
                distributedCacheStore,
                matchServerSession.ServerMetadata?.ServerID ?? pending.ServerId,
                targetSession?.ClientMetadata?.MatchID ?? pending.MatchId,
                targetSession?.ClientMetadata?.MatchServerAddress);

            if (targetSession is not null)
            {
                // Hydrate spectator's cached CChatManager peer entry (0x000C) with split-horizon server IP:port right before PSRR_ALLOW
                targetSession.SendDirectStatusUpdateTo(
                    requesterSession,
                    ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME,
                    matchServer,
                    bypassInvisibleCheck: true);
            }

            IPAddress? spectatorIp = requesterSession.GetRemoteClientIP();
            string storedAddress = targetSession?.ClientMetadata?.MatchServerAddress
                                   ?? (matchServerSession.ServerMetadata is { Port: > 0 } sm
                                       ? $"{sm.Address}:{sm.Port}"
                                       : string.Empty);
            string resolvedSpectateAddress = SplitHorizonEndpointResolver.ResolveStoredServerAddressForClient(
                storedAddress,
                spectatorIp,
                matchServer);

            Log.Information(
                "[PlayerSpectate] ALLOW '{Requester}' ({SpectatorIp}) -> '{Target}' (WireName='{WireName}', Mentor={Mentor}, ServerID={ServerID}, StoredServer={RawServer} -> Resolved={ResolvedServer})",
                requesterSession.Account.Name,
                spectatorIp?.ToString() ?? "null",
                pending.CanonicalTargetName,
                pending.TargetWireName,
                pending.IsMentor,
                matchServerSession.ServerMetadata?.ServerID ?? pending.ServerId,
                storedAddress,
                resolvedSpectateAddress);
        }
        else
        {
            Log.Information(
                "[PlayerSpectate] MatchServer response {Result} ({ResultByte}) for '{Requester}' -> '{Target}' (WireName='{WireName}', Mentor={Mentor}, ServerID={ServerID})",
                result,
                resultByte,
                requesterSession.Account.Name,
                pending.CanonicalTargetName,
                pending.TargetWireName,
                pending.IsMentor,
                matchServerSession.ServerMetadata?.ServerID ?? pending.ServerId);
        }

        SendSpectateResponse(requesterSession, pending.TargetWireName, result);
    }

    private static string StripClanTag(string rawName)
    {
        return MerrickAccountExtensions
            .SeparateClanTagFromAccountName(rawName.Trim())
            .AccountName
            .Trim();
    }

    private static string BuildPendingKey(Guid matchServerSessionId, string strippedRequesterName, string strippedTargetName)
    {
        return $"{matchServerSessionId:N}:{strippedRequesterName}:{strippedTargetName}";
    }

    private static void PruneExpiredPendingRequests(DateTimeOffset now)
    {
        foreach (KeyValuePair<string, PendingSpectateRequest> kvp in PendingRequests)
        {
            if (kvp.Value.ExpiresAtUtc < now)
            {
                PendingRequests.TryRemove(kvp.Key, out _);
            }
        }
    }

    private static async Task<MatchServer?> ResolveMatchServerRecordAsync(
        IDatabase? distributedCacheStore,
        int? serverId,
        int? matchId,
        string? storedServerAddress)
    {
        if (distributedCacheStore is null) return null;

        if (serverId is > 0)
        {
            MatchServer? byId = await distributedCacheStore.GetMatchServerByID(serverId.Value);
            if (byId is not null) return byId;
        }

        if (matchId is > 0)
        {
            MatchStartData? matchData = await distributedCacheStore.GetMatchStartData(matchId.Value);
            if (matchData is not null)
            {
                MatchServer? byMatch = await distributedCacheStore.GetMatchServerByID(matchData.ServerID);
                if (byMatch is not null) return byMatch;
            }
        }

        if (!string.IsNullOrWhiteSpace(storedServerAddress))
        {
            string[] parts = storedServerAddress.Split(':');
            if (parts.Length == 2 && int.TryParse(parts[1], out int port))
            {
                return await distributedCacheStore.GetMatchServerByIPAddressAndPort(parts[0], port);
            }
        }

        return null;
    }

    private static void SendSpectateResponse(
        ChatSession requesterSession,
        string wireSpectateeName,
        ChatProtocol.PlayerSpectateRequestResponse result)
    {
        ChatBuffer response = new();
        response.WriteCommand(ChatProtocol.Command.CHAT_CMD_PLAYER_SPECTATE_REQUEST);
        response.WriteInt8((byte)ChatProtocol.PlayerSpectateRequest.PLAYER_SPECTATE_REQUEST_RESPONSE);
        response.WriteString(wireSpectateeName);
        response.WriteInt8((byte)result);
        requesterSession.Send(response);
    }

    private static async Task<bool> HasMutualAcceptedFriendshipAsync(
        Account requester,
        Account target,
        MerrickContext? merrick)
    {
        bool requesterHasTarget = (requester.Friendships ?? [])
            .Any(f => f.FriendAccountID == target.ID && f.Status == FriendshipStatus.Accepted);
        bool targetHasRequester = (target.Friendships ?? [])
            .Any(f => f.FriendAccountID == requester.ID && f.Status == FriendshipStatus.Accepted);

        if (requesterHasTarget && targetHasRequester) return true;
        if (merrick is null) return false;

        if (!requesterHasTarget)
        {
            requesterHasTarget = await merrick.Friendships
                .AsNoTracking()
                .AnyAsync(f => f.AccountID == requester.ID &&
                               f.FriendAccountID == target.ID &&
                               f.Status == FriendshipStatus.Accepted);
        }

        if (!targetHasRequester)
        {
            targetHasRequester = await merrick.Friendships
                .AsNoTracking()
                .AnyAsync(f => f.AccountID == target.ID &&
                               f.FriendAccountID == requester.ID &&
                               f.Status == FriendshipStatus.Accepted);
        }

        return requesterHasTarget && targetHasRequester;
    }

    private static ChatSession? ResolveServerSession(
        IChatContext chatContext,
        int? serverId,
        int? matchId,
        string? serverAddress,
        int targetAccountId,
        string strippedTargetName)
    {
        if (chatContext.MatchServerChatSessions is null)
        {
            return null;
        }

        if (serverId is > 0 &&
            chatContext.MatchServerChatSessions.TryGetValue(serverId.Value, out ChatSession? byIdSession) &&
            byIdSession.ServerMetadata is not null)
        {
            return byIdSession;
        }

        int? parsedPort = null;
        bool isLoopbackAddr = false;
        if (!string.IsNullOrWhiteSpace(serverAddress))
        {
            string[] parts = serverAddress.Split(':');
            if (parts.Length == 2 && int.TryParse(parts[1], out int p))
            {
                parsedPort = p;
                isLoopbackAddr = string.Equals(parts[0], "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(parts[0], "localhost", StringComparison.OrdinalIgnoreCase);
            }
        }

        ChatSession? loopbackPortCandidate = null;
        string accountPrefix = $"{targetAccountId}|";

        foreach (ChatSession serverSession in chatContext.MatchServerChatSessions.Values)
        {
            MatchServerChatSessionMetadata? meta = serverSession.ServerMetadata;
            if (meta is null) continue;

            if (matchId is > 0 && meta.MatchID == matchId.Value)
            {
                return serverSession;
            }

            if (!string.IsNullOrWhiteSpace(serverAddress))
            {
                if (string.Equals($"{meta.Address}:{meta.Port}", serverAddress, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrWhiteSpace(meta.LocalAddress) &&
                     string.Equals($"{meta.LocalAddress}:{meta.LocalPort}", serverAddress, StringComparison.OrdinalIgnoreCase)))
                {
                    return serverSession;
                }
            }

            if (meta.PlayerInformation is not null)
            {
                foreach (string slot in meta.PlayerInformation)
                {
                    if (string.IsNullOrWhiteSpace(slot)) continue;
                    if (slot.StartsWith(accountPrefix, StringComparison.Ordinal))
                    {
                        return serverSession;
                    }

                    string[] fields = slot.Split('|');
                    if (fields.Length >= 2)
                    {
                        string slotName = StripClanTag(fields[1]);
                        if (string.Equals(slotName, strippedTargetName, StringComparison.OrdinalIgnoreCase))
                        {
                            return serverSession;
                        }
                    }
                }
            }

            if (isLoopbackAddr && parsedPort is > 0 && (meta.Port == parsedPort.Value || meta.LocalPort == parsedPort.Value))
            {
                loopbackPortCandidate ??= serverSession;
            }
        }

        return loopbackPortCandidate;
    }

    private sealed record PendingSpectateRequest(
        int RequesterAccountId,
        string RequesterWireName,
        int TargetAccountId,
        string TargetWireName,
        string CanonicalTargetName,
        bool IsMentor,
        int? ServerId,
        int? MatchId,
        DateTimeOffset ExpiresAtUtc);
}
