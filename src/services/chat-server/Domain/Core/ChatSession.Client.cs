// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using System.Globalization;
using ASPIRE.Common.Network;
using MERRICK.DatabaseContext.Enumerations;
using MERRICK.DatabaseContext.Extensions;

namespace TRANSMUTANSTEIN.ChatServer.Domain.Core;

public partial class ChatSession
{
    private readonly Lock _cleanupLock = new();

    /// <summary>
    ///     Gets set after a successful client handshake following the <see cref="Accept" /> method.
    ///     Contains metadata about the client connected to this chat session.
    ///     This property is NULL before authentication, but is guaranteed non-NULL after <see cref="Accept" /> is called.
    /// </summary>
    public ClientChatSessionMetadata ClientMetadata { get; set; } = null!;

    /// <summary>
    ///     Tracks the channel IDs that this client is currently a member of.
    ///     The maximum number of channels cannot exceed the value of <see cref="ChatProtocol.MAX_CHANNELS_PER_CLIENT" />.
    /// </summary>
    public HashSet<int> CurrentChannels { get; set; } = [];

    public void Accept(Account account)
    {
        // Link The Account To The Chat Session
        Account = account;
        MarkAuthenticated(ChatPeerRole.Client);

        // Add The Chat Session To The Chat Sessions Collection
        _chatContext.ClientChatSessions.AddOrUpdate(account.Name, this, (key, existing) => this);
        _ = _telemetryPublisher?.OnClientConnectedAsync(account.Name);
        _ = _distributedChatState?.RegisterSessionAsync(this);

        ChatBuffer accept = new();

        accept.WriteCommand(ChatProtocol.ChatServerToClient.NET_CHAT_CL_ACCEPT);

        Send(accept);

        // Send Options And Remote Commands To Client (Must Be Sent Before Initial Status)
        SendOptionsAndRemoteCommands();

        // Get The Connection Status Of All Friends And Clan Members That Are Currently Online (Excluding Invisible Clients)
        ReceiveFriendAndClanMemberConnectionStatus();

        // Notify Self, Clan Members, And Friends That This Client Is Now Connected
        BroadcastConnectionStatusUpdate(ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_CONNECTED);
    }

    public IPAddress? GetRemoteClientIP()
    {
        try
        {
            if (Socket?.RemoteEndPoint is IPEndPoint ep)
            {
                return ep.Address.IsIPv4MappedToIPv6 ? ep.Address.MapToIPv4() : ep.Address;
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[ChatSession] Socket.RemoteEndPoint unavailable; falling back to ClientMetadata.RemoteIP.");
        }

        string? remoteIp = ClientMetadata?.RemoteIP;
        if (!string.IsNullOrWhiteSpace(remoteIp) && IPAddress.TryParse(remoteIp, out IPAddress? parsed))
        {
            return parsed.IsIPv4MappedToIPv6 ? parsed.MapToIPv4() : parsed;
        }

        return null;
    }

    public async Task<ChatSession> PrepareToJoinMatch(IDatabase distributedCacheStore, string serverAddress)
    {
        List<string> serverAddressParts = [.. serverAddress.Split(':')];

        if (serverAddressParts.Count is not 2)
        {
            Log.Error(@"[BUG] Invalid Match Server Address ""{MatchServerAddress}""", serverAddress);

            return this;
        }

        string serverIPAddress = serverAddressParts.First();
        int serverPort = Convert.ToInt32(serverAddressParts.Last(), CultureInfo.InvariantCulture);

        MatchServer? server = await distributedCacheStore.GetMatchServerByIPAddressAndPort(serverIPAddress, serverPort);

        if (server is null)
        {
            Log.Error(
                @"[BUG] Client Account ID ""{AccountID}"" Attempted To Join Match On Unknown Server ""{ServerAddress}""",
                Account.ID, serverAddress);

            return this;
        }

        // Store Match Server Info In Metadata
        ClientMetadata.MatchServerAddress = $"{server.IPAddress}:{server.Port}";

        BroadcastConnectionStatusUpdate(ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_JOINING_GAME, server);

        return this;
    }

    public async Task<ChatSession> JoinMatch(IDatabase distributedCacheStore, int matchID)
    {
        MatchStartData? matchData = await distributedCacheStore.GetMatchStartData(matchID);

        if (matchData is null)
        {
            Log.Error("[BUG] Match ID {MatchID} Not Found In Cache For Session {SessionID}", matchID, ID);

            return this;
        }

        MatchServer? server = await distributedCacheStore.GetMatchServerByID(matchData.ServerID);

        if (server is null)
        {
            Log.Error(@"[BUG] Client Account ID ""{AccountID}"" Attempted To Join Match On Unknown Server", Account.ID);

            return this;
        }

        // Store Match ID In Metadata
        ClientMetadata.MatchID = matchID;
        ClientMetadata.MatchServerAddress ??= $"{server.IPAddress}:{server.Port}";

        BroadcastConnectionStatusUpdate(ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME, server);

        return this;
    }

    public ChatSession LeaveMatch()
    {
        // Clear Match Metadata
        ClientMetadata.MatchID = null;
        ClientMetadata.MatchServerAddress = null;

        BroadcastConnectionStatusUpdate(ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_CONNECTED);

        return this;
    }

    public ChatSession Reject(ChatProtocol.ChatRejectReason reason)
    {
        ChatBuffer reject = new();

        reject.WriteCommand(ChatProtocol.ChatServerToClient.NET_CHAT_CL_REJECT);
        reject.WriteInt8(Convert.ToByte(reason, CultureInfo.InvariantCulture)); // Rejection Reason

        Send(reject);

        return this;
    }

    public void LogOut()
    {
        BroadcastConnectionStatusUpdate(ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_DISCONNECTED);

        ChatBuffer logout = new();

        logout.WriteCommand(ChatProtocol.Command.CHAT_CMD_LOGOUT);
        logout.WriteBool(true); // Whether Or Not To Log Out Staff Accounts

        Send(logout);

        Log.Information("Dispatched logout packet to Session {SessionId} (Account: {AccountId})", ID, Account?.ID);
        // Note(protocol): Connection status update to friends and clan members is broadcasted via BroadcastConnectionStatusUpdate above.
    }

    public void Cleanup()
    {
        lock (_cleanupLock)
        {
            Account? account = (Account?)Account; // Cache Account to local variable

            if (account is not null && ServerMetadata is null && ManagerMetadata is null)
            {
                try
                {
                    // Get All Chat Channels The Client Is A Member Of
                    List<ChatChannel> channels =
                        [.. _chatContext.ChatChannels.Values.Where(channel => channel.Members.ContainsKey(account.Name))];

                    // Remove The Client From All Chat Channels They Are A Member Of
                    foreach (ChatChannel channel in channels) channel.Leave(_chatContext, this);

                    // Remove From Matchmaking Group If In One
                    _matchmakingService.GetMatchmakingGroup(account.ID)?.RemoveMember(_matchmakingService, account.ID);
                    _queueArcadeService?.ReleasePlayerFromArcade(account.ID, "client_session_cleanup", pauseCurrentQueueWait: false);

                    // Log The Client Out And Disconnect The Chat Session
                    LogOut();

                    // Remove The Chat Session From The Chat Sessions Collection
                    bool removedClientSession = _chatContext.ClientChatSessions.TryGetValue(account.Name, out ChatSession? activeClientSession)
                                                && (ReferenceEquals(activeClientSession, this) || activeClientSession.ID == ID)
                                                && ((ICollection<KeyValuePair<string, ChatSession>>)_chatContext.ClientChatSessions).Remove(new KeyValuePair<string, ChatSession>(account.Name, activeClientSession));
                    if (!removedClientSession)
                        Log.Debug(@"Chat Session For Account Name ""{AccountName}"" Was Already Removed", account.Name);
                    else
                        _ = _telemetryPublisher?.OnClientDisconnectedAsync(account.Name);

                    _ = _distributedChatState?.UnregisterSessionAsync(account.Name, account.ID);
                }
                catch (ObjectDisposedException)
                {
                    // Ignored - Application is likely shutting down (e.g. during test teardown)
                }
                finally
                {
                    // Prevent Double-Cleanup
                    _isAuthenticated = false;
                    Account = null!;
                }
            }

            // Clean up abruptly dropped Match Server session
            if (ServerMetadata is not null)
            {
                try
                {
                    bool ownsServerRegistration = _chatContext.MatchServerChatSessions.TryGetValue(ServerMetadata.ServerID, out ChatSession? activeServerSession)
                                                  && (ReferenceEquals(activeServerSession, this) || activeServerSession.ID == ID)
                                                  && ((ICollection<KeyValuePair<int, ChatSession>>)_chatContext.MatchServerChatSessions).Remove(new KeyValuePair<int, ChatSession>(ServerMetadata.ServerID, activeServerSession));
                    if (!ownsServerRegistration && activeServerSession is not null)
                    {
                        Log.Warning("[SECURITY-ALERT] Session {SessionId} attempted Cleanup eviction for Match Server ID {ServerID} owned by live Session {OwnerSessionId}; skipping eviction",
                            ID, ServerMetadata.ServerID, activeServerSession.ID);
                    }
                    if (ownsServerRegistration)
                    {
                        Log.Warning(@"[ChatSession] Match Server ID ""{ServerID}"" ({Address}:{Port}) disconnected unceremoniously; purged from active chat pool while preserving Redis MatchServer session for reconnect",
                            ServerMetadata.ServerID, ServerMetadata.Address, ServerMetadata.Port);
                        _ = _telemetryPublisher?.OnMatchServerDisconnectedAsync(ServerMetadata.ServerID, ServerMetadata.Address);

                        // Redis MatchServer registration is intentionally preserved across transient chat disconnects; master-server reaper handles stale servers.





                    }
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Error while cleaning up abruptly disconnected Match Server {ServerID}", ServerMetadata.ServerID);
                }
            }

            // Clean up abruptly dropped Match Server Manager session
            if (ManagerMetadata is not null)
            {
                try
                {
                    bool ownsManagerRegistration = _chatContext.MatchServerManagerChatSessions.TryGetValue(ManagerMetadata.ServerManagerID, out ChatSession? activeManagerSession)
                                                   && (ReferenceEquals(activeManagerSession, this) || activeManagerSession.ID == ID)
                                                   && ((ICollection<KeyValuePair<int, ChatSession>>)_chatContext.MatchServerManagerChatSessions).Remove(new KeyValuePair<int, ChatSession>(ManagerMetadata.ServerManagerID, activeManagerSession));
                    if (!ownsManagerRegistration && activeManagerSession is not null)
                    {
                        Log.Warning("[SECURITY-ALERT] Session {SessionId} attempted Cleanup eviction for Match Server Manager ID {ServerManagerID} owned by live Session {OwnerSessionId}; skipping eviction",
                            ID, ManagerMetadata.ServerManagerID, activeManagerSession.ID);
                    }
                    if (ownsManagerRegistration)
                    {
                        Log.Warning(@"[ChatSession] Match Server Manager ID ""{ServerManagerID}"" disconnected unceremoniously; purged from active pool",
                            ManagerMetadata.ServerManagerID);
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Error while cleaning up abruptly disconnected Match Server Manager {ServerManagerID}", ManagerMetadata.ServerManagerID);
                }
            }
        }
    }

    public void TerminateClient()
    {
        Cleanup();

        Disconnect();

        // Dispose Of The Chat Session
        Dispose();
    }


    /// <summary>
    ///     Retrieves a list of chat sessions for all online peers who are observing the current account.
    ///     This includes:
    ///     1. Users who have added the current account as a friend (Reverse Lookup).
    ///     2. Clan members (Mutual).
    /// </summary>
    /// <remarks>
    ///     Duplicate sessions are removed from the returned list.
    ///     The current account is excluded from the list.
    /// </remarks>
    /// <returns>
    ///     A list of <see cref="ChatSession" /> objects representing active chat sessions of observers.
    ///     The list is empty if no such sessions are available.
    /// </returns>
    private List<ChatSession> GetOnlineObservers()
    {
        Account? currentAccount = (Account?)Account;
        if (currentAccount is null) return [];

        // Get All Clan Member IDs (Excluding Self) - Mutual Relationship
        HashSet<int> clanMemberIDs =
        [
            .. (currentAccount.Clan?.Members ?? [])
            .Where(clanMember => clanMember.ID != currentAccount.ID)
            .Select(clanMember => clanMember.ID)
        ];

        // Find Sessions That Should Be Notified
        List<ChatSession> observerSessions =
        [
            .. _chatContext.ClientChatSessions.Values
                .Where(chatSession =>
                    (object?)chatSession.Account is not null &&
                    chatSession.Account.ID != currentAccount.ID &&
                    (
                        // Case 1: They have friended ME (Reverse Lookup via authoritative Friendships)
                        (chatSession.Account.Friendships ?? []).Any(f => f.FriendAccountID == currentAccount.ID && f.Status == FriendshipStatus.Accepted) ||
                        // Case 2: They are in my CLAN (Mutual)
                        clanMemberIDs.Contains(chatSession.Account.ID)
                    )
                )
        ];

        return observerSessions;
    }

    /// <summary>
    ///     Sends the client's connection status to all friends and clan members that are currently online.
    ///     Also sends the client's connection status to the client itself, so they can see their own status.
    /// </summary>
    public void BroadcastConnectionStatusUpdate(ChatProtocol.ChatClientStatus status, MatchServer? matchServer = null)
    {
        ClientChatSessionMetadata? metadata = (ClientChatSessionMetadata?)ClientMetadata;
        Account? currentAccount = (Account?)Account;

        if (metadata is null || currentAccount is null) return;

        ClientMetadata.LastKnownClientState = status;

        if (ClientMetadata.ClientChatModeState is ChatProtocol.ChatModeType.CHAT_MODE_INVISIBLE) return;

        // Get All Online Observers (People who friend ME or are in my CLAN)
        List<ChatSession> onlineObserverSessions = GetOnlineObservers();

        LogBroadcastStatusUpdate(status, Account.ID, Account.Name, onlineObserverSessions.Count);
        Log.Information(
            "[BroadcastStatus] Account '{AccountName}' (ID: {AccountID}) broadcasting status {Status} to {ObserverCount} observers (Server: {ServerAddress}, MatchID: {MatchID}, MatchName: '{MatchName}')",
            currentAccount.Name, currentAccount.ID, status, onlineObserverSessions.Count,
            ClientMetadata.MatchServerAddress ?? "0.0.0.0:0", ClientMetadata.MatchID ?? 0, ClientMetadata.MatchName ?? "");

        // Broadcast to Discord RPC
        MatchmakingGroup? group = _matchmakingService.GetMatchmakingGroupByMemberID(Account.ID);
        _discordPublisher.PublishPresenceUpdate(this, group,
            status is ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_DISCONNECTED
                or ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_CONNECTED);

        string effectiveServerAddress = matchServer is not null
            ? $"{matchServer.IPAddress}:{matchServer.Port}"
            : ClientMetadata.MatchServerAddress ?? "0.0.0.0:0";

        _ = _distributedChatState?.UpdatePresenceAsync(
            currentAccount.ID,
            currentAccount.Name,
            status,
            ClientMetadata.ClientChatModeState,
            status is ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_JOINING_GAME or ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME ? effectiveServerAddress : null,
            status is ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME ? ClientMetadata.MatchID : null);

        bool includesServerAddress = status is ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_JOINING_GAME
            or ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME;

        ChatBuffer BuildStatusUpdatePacket(string? resolvedServerAddress)
        {
            ChatBuffer packet = new();
            packet.WriteCommand(ChatProtocol.Command.CHAT_CMD_UPDATE_STATUS);
            packet.WriteInt32(Account.ID); // Client's Account ID
            packet.WriteInt8(Convert.ToByte(status, CultureInfo.InvariantCulture)); // Client's Status
            packet.WriteInt8(Account.GetChatClientFlags()); // Client's Flags (Chat Client Type)
            packet.WriteInt32(Account.Clan?.ID ?? 0); // Client's Clan ID
            packet.WriteString(Account.Clan?.Name ?? string.Empty); // Client's Clan Name
            packet.WriteString(Account.GetChatSymbolNoPrefixCode()); // Chat Symbol
            packet.WriteString(Account.GetNameColourNoPrefixCode()); // Name Colour
            packet.WriteString(Account.GetIconNoPrefixCode()); // Account Icon

            if (includesServerAddress)
            {
                packet.WriteString(resolvedServerAddress ?? "0.0.0.0:0");

                if (status is ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME)
                {
                    packet.WriteString(ClientMetadata.MatchName ?? string.Empty); // Match Name (Legacy: GameName)
                    packet.WriteInt32(ClientMetadata.MatchID ?? 0); // Match ID
                    packet.WriteInt8(0); // 0 - done, 1 - more data about the match to follow
                }
            }

            packet.WriteInt32(Account.AscensionLevel); // Client's Ascension Level
            return packet;
        }

        if (!includesServerAddress)
        {
            ChatBuffer sharedUpdate = BuildStatusUpdatePacket(null);
            foreach (ChatSession onlineObserverSession in onlineObserverSessions) onlineObserverSession.Send(sharedUpdate);
            Send(sharedUpdate);
            return;
        }

        Dictionary<string, ChatBuffer> packetByAddress = new(StringComparer.Ordinal);
        ChatBuffer GetOrBuildForRecipient(ChatSession recipientSession)
        {
            IPAddress? recipientIp = recipientSession.GetRemoteClientIP();
            string resolvedAddress = SplitHorizonEndpointResolver.ResolveStoredServerAddressForClient(
                effectiveServerAddress,
                recipientIp,
                matchServer);

            if (!string.Equals(resolvedAddress, effectiveServerAddress, StringComparison.Ordinal))
            {
                Log.Debug(
                    "[ChatRouting] Split-horizon status address for recipient '{RecipientName}' ({RecipientIp}): '{RawAddress}' -> '{ResolvedAddress}'",
                    recipientSession.Account?.Name ?? "unknown",
                    recipientIp?.ToString() ?? "null",
                    effectiveServerAddress,
                    resolvedAddress);
            }

            if (!packetByAddress.TryGetValue(resolvedAddress, out ChatBuffer? cachedPacket))
            {
                cachedPacket = BuildStatusUpdatePacket(resolvedAddress);
                packetByAddress[resolvedAddress] = cachedPacket;
            }

            return cachedPacket;
        }

        foreach (ChatSession onlineObserverSession in onlineObserverSessions)
        {
            onlineObserverSession.Send(GetOrBuildForRecipient(onlineObserverSession));
        }

        // Also send to self so the client knows its status has changed
        Send(GetOrBuildForRecipient(this));
    }

    /// <summary>
    ///     Broadcasts a DISCONNECTED status packet to online observers when transitioning into Invisible mode,
    ///     without overwriting <see cref="ClientChatSessionMetadata.LastKnownClientState" /> or disconnecting self,
    ///     and updates distributed Redis presence to reflect <see cref="ChatProtocol.ChatModeType.CHAT_MODE_INVISIBLE" />.
    /// </summary>
    public void BroadcastInvisibleDisconnect()
    {
        ClientChatSessionMetadata? metadata = (ClientChatSessionMetadata?)ClientMetadata;
        Account? currentAccount = (Account?)Account;
        if (metadata is null || currentAccount is null) return;

        List<ChatSession> onlineObserverSessions = GetOnlineObservers();

        Log.Information(
            "[BroadcastStatus] Account '{AccountName}' (ID: {AccountID}) entering CHAT_MODE_INVISIBLE; masking as DISCONNECTED to {ObserverCount} observers while preserving LastKnownClientState={RealState}",
            currentAccount.Name, currentAccount.ID, onlineObserverSessions.Count, metadata.LastKnownClientState);

        _ = _distributedChatState?.UpdatePresenceAsync(
            currentAccount.ID,
            currentAccount.Name,
            metadata.LastKnownClientState,
            ChatProtocol.ChatModeType.CHAT_MODE_INVISIBLE,
            metadata.LastKnownClientState is ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_JOINING_GAME
                or ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME
                ? metadata.MatchServerAddress
                : null,
            metadata.LastKnownClientState is ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME
                ? metadata.MatchID
                : null);

        ChatBuffer packet = BuildDirectStatusUpdatePacket(
            ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_DISCONNECTED,
            null);

        foreach (ChatSession observer in onlineObserverSessions)
        {
            observer.Send(packet);
        }
    }

    /// <summary>
    ///     Sends a single-recipient <c>0x000C</c> (<see cref="ChatProtocol.Command.CHAT_CMD_UPDATE_STATUS" />) packet
    ///     describing this session's status to <paramref name="recipientSession" />, applying per-recipient
    ///     split-horizon server address resolution and respecting Invisible mode unless overridden.
    /// </summary>
    public void SendDirectStatusUpdateTo(
        ChatSession recipientSession,
        ChatProtocol.ChatClientStatus? statusOverride = null,
        MatchServer? matchServer = null,
        bool bypassInvisibleCheck = false)
    {
        Account? currentAccount = (Account?)Account;
        ClientChatSessionMetadata? metadata = (ClientChatSessionMetadata?)ClientMetadata;
        if (currentAccount is null || (object?)recipientSession.Account is null) return;

        ChatProtocol.ChatClientStatus effectiveStatus;
        if (statusOverride.HasValue)
        {
            effectiveStatus = statusOverride.Value;
        }
        else if (!bypassInvisibleCheck &&
                 metadata?.ClientChatModeState is ChatProtocol.ChatModeType.CHAT_MODE_INVISIBLE)
        {
            effectiveStatus = ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_DISCONNECTED;
        }
        else
        {
            effectiveStatus = metadata?.LastKnownClientState ?? ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_CONNECTED;
        }

        string? resolvedAddress = null;
        if (effectiveStatus is ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_JOINING_GAME
            or ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME)
        {
            string rawAddress = matchServer is not null
                ? $"{matchServer.IPAddress}:{matchServer.Port}"
                : metadata?.MatchServerAddress ?? "0.0.0.0:0";

            resolvedAddress = SplitHorizonEndpointResolver.ResolveStoredServerAddressForClient(
                rawAddress,
                recipientSession.GetRemoteClientIP(),
                matchServer);
        }

        ChatBuffer packet = BuildDirectStatusUpdatePacket(effectiveStatus, resolvedAddress);
        recipientSession.Send(packet);

        Log.Information(
            "[DirectStatusUpdate] Sent 0x000C ({Status}) for '{SubjectName}' (ID: {SubjectID}, Server: {ServerAddress}) -> '{RecipientName}' (ID: {RecipientID})",
            effectiveStatus,
            currentAccount.Name,
            currentAccount.ID,
            resolvedAddress ?? "none",
            recipientSession.Account.Name,
            recipientSession.Account.ID);
    }

    private ChatBuffer BuildDirectStatusUpdatePacket(
        ChatProtocol.ChatClientStatus status,
        string? resolvedServerAddress)
    {
        ChatBuffer packet = new();
        packet.WriteCommand(ChatProtocol.Command.CHAT_CMD_UPDATE_STATUS);
        packet.WriteInt32(Account.ID);
        packet.WriteInt8(Convert.ToByte(status, CultureInfo.InvariantCulture));
        packet.WriteInt8(Account.GetChatClientFlags());
        packet.WriteInt32(Account.Clan?.ID ?? 0);
        packet.WriteString(Account.Clan?.Name ?? string.Empty);
        packet.WriteString(Account.GetChatSymbolNoPrefixCode());
        packet.WriteString(Account.GetNameColourNoPrefixCode());
        packet.WriteString(Account.GetIconNoPrefixCode());

        if (status is ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_JOINING_GAME
            or ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME)
        {
            packet.WriteString(resolvedServerAddress ?? "0.0.0.0:0");

            if (status is ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME)
            {
                packet.WriteString(ClientMetadata?.MatchName ?? string.Empty);
                packet.WriteInt32(ClientMetadata?.MatchID ?? 0);
                packet.WriteInt8(0);
            }
        }

        packet.WriteInt32(Account.AscensionLevel);
        return packet;
    }

    /// <summary>
    ///     Receive the connection status of all friends and clan members that are currently online.
    ///     Does not include invisible clients.
    /// </summary>
    public void ReceiveFriendAndClanMemberConnectionStatus(MatchServer? matchServer = null)
    {
        List<int> clanMemberIDs = [.. Account.Clan?.Members.Select(clanMember => clanMember.ID) ?? []];
        List<int> friendIDs = [.. (Account.Friendships ?? []).Where(f => f.Status == FriendshipStatus.Accepted).Select(friend => friend.FriendAccountID)];

        List<ChatSession> onlinePeerSessions =
        [
            .. _chatContext.ClientChatSessions.Values
                .Where(chatSession => friendIDs.Any(friendID => friendID == chatSession.Account.ID) ||
                                      clanMemberIDs.Any(clanMemberID => clanMemberID == chatSession.Account.ID))
                .Where(chatSession =>
                    chatSession.ClientMetadata.ClientChatModeState is not ChatProtocol.ChatModeType.CHAT_MODE_INVISIBLE)
                .Where(chatSession => chatSession.Account.ID != Account.ID)
                .Select(chatSession => chatSession).Distinct()
        ]; // Get All Online Friends And Clan Members That Are Not Invisible

        IPAddress? recipientClientIp = GetRemoteClientIP();
        ChatBuffer update = new();

        update.WriteCommand(ChatProtocol.Command.CHAT_CMD_INITIAL_STATUS);
        update.WriteInt32(onlinePeerSessions.Count); // Number Of Online Peers

        foreach (ChatSession onlinePeerSession in onlinePeerSessions)
        {
            ChatProtocol.ChatClientStatus status = onlinePeerSession.ClientMetadata.LastKnownClientState;

            update.WriteInt32(onlinePeerSession.Account.ID); // Client's Account ID
            update.WriteInt8(Convert.ToByte(status, CultureInfo.InvariantCulture)); // Client's Status
            update.WriteInt8(onlinePeerSession.Account.GetChatClientFlags()); // Client's Flags (Chat Client Type)
            update.WriteString(onlinePeerSession.Account.GetNameColourNoPrefixCode()); // Name Colour
            update.WriteString(onlinePeerSession.Account.GetIconNoPrefixCode()); // Account Icon

            if (status is ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_JOINING_GAME
                or ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME)
            {
                string? matchServerAddress = onlinePeerSession.ClientMetadata.MatchServerAddress;

                if (string.IsNullOrEmpty(matchServerAddress))
                {
                    // Fallback If Metadata Is Missing (Should Not Happen With Fix)
                    Log.Error(@"[BUG] Peer Account ""{PeerName}"" Is {Status} But MatchServerAddress Is Missing",
                        onlinePeerSession.Account.Name, status);
                    update.WriteString("0.0.0.0:0");
                }
                else
                {
                    string resolvedPeerAddress = SplitHorizonEndpointResolver.ResolveStoredServerAddressForClient(
                        matchServerAddress,
                        recipientClientIp);
                    update.WriteString(resolvedPeerAddress);
                }

                if (status is ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_IN_GAME)
                {
                    update.WriteString(onlinePeerSession.ClientMetadata.MatchName ??
                                       string.Empty); // Match Name (Legacy: GameName)
                    update.WriteInt32(onlinePeerSession.ClientMetadata.MatchID ?? 0); // Match ID
                }
            }

            update.WriteInt32(onlinePeerSession.Account.AscensionLevel); // Client's Ascension Level
        }

        Send(update);
    }

    public void SendOptionsAndRemoteCommands()
    {
        ChatBuffer options = new();

        string[] commands =
        [
            "cg_24hourClock true",
            "con_showNet false",
            "http_printDebugInfo false",
            "php_printDebugInfo false",
            "sys_dumpOnFatal true"
        ];

        options.WriteCommand(ChatProtocol.Command.CHAT_CMD_OPTIONS);
        options.WriteBool(false); // Upload To FTP On Demand (e.g. replays)
        options.WriteBool(true); // Upload To HTTP On Demand (e.g. replays)
        IConfiguration? cfg = serviceProvider?.GetService<IConfiguration>();
        bool warEffortEnabled = !bool.TryParse(cfg?["WarEffort:Enabled"], out bool parsedWarEffort) || parsedWarEffort;
        options.WriteBool(warEffortEnabled); // Quests Are Enabled
        options.WriteBool(warEffortEnabled); // Quest Ladder Is Enabled
        options.WriteBool(false); // Override Connect Resend Time
        options.WriteBool(true); // Enable Messages
        options.WriteString(string.Join('|', commands)); // Dynamic List Of Console Commands For The Client To Execute

        Send(options);
    }
}
