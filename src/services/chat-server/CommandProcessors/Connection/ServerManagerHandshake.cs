// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using NEXUS.Contracts.Security;

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.Connection;

[ChatCommand(
    ChatProtocol.ServerManagerToChatServer.NET_CHAT_SM_CONNECT,
    AllowUnauthenticated = true,
    AllowedRoles = ChatPeerRole.ServerManager)]
internal class ServerManagerHandshake(
    IDatabase distributedCacheStore,
    MerrickContext databaseContext,
    IChatContext chatContext)
    : IAsynchronousCommandProcessor<ChatSession>
{
    public async Task Process(ChatSession session, ChatBuffer buffer)
    {
        ServerManagerHandshakeRequestData requestData = new(buffer);

        // Validate Protocol Version BEFORE populating session metadata or touching active manager pool
        if (requestData.ChatProtocolVersion != ChatProtocol.CHAT_PROTOCOL_EXTERNAL_VERSION)
        {
            Log.Warning(
                @"[SECURITY-ALERT] Match Server Manager ID ""{ServerManagerID}"" Chat Protocol Version Mismatch on Session {SessionID} (Expected: ""{ExpectedVersion}"", Received: ""{ReceivedVersion}"")",
                requestData.ServerManagerID,
                session.ID,
                ChatProtocol.CHAT_PROTOCOL_EXTERNAL_VERSION,
                requestData.ChatProtocolVersion);

            RejectPreAuthSession(
                session,
                $@"Chat Protocol Version Mismatch: Expected ""{ChatProtocol.CHAT_PROTOCOL_EXTERNAL_VERSION}"", Received ""{requestData.ChatProtocolVersion}""");
            return;
        }

        MatchServerManager? manager = !string.IsNullOrWhiteSpace(requestData.SessionCookie)
            ? await distributedCacheStore.GetMatchServerManagerBySessionCookie(requestData.SessionCookie)
            : null;

        // Validate Server Manager Cookie Against Distributed Cache
        if (manager is null)
        {
            Log.Warning(
                @"[SECURITY-ALERT] Match Server Manager ID ""{ServerManagerID}"" Cookie ""{Cookie}"" Is Invalid on Session {SessionID}; rejecting without touching live pool",
                requestData.ServerManagerID,
                SecurityMasking.MaskToken(requestData.SessionCookie),
                session.ID);

            RejectPreAuthSession(session, "The Session Cookie Provided For Chat Server Manager Authentication Is Invalid");
            return;
        }

        // Validate Server Manager ID Match
        if (manager.ID != requestData.ServerManagerID)
        {
            Log.Warning(
                @"[SECURITY-ALERT] Match Server Manager ID ""{ServerManagerID}"" Does Not Match Cookie Owner Manager ID ""{CookieManagerID}"" (Cookie: ""{Cookie}"", Session: {SessionID}); rejecting without touching live pool",
                requestData.ServerManagerID,
                manager.ID,
                SecurityMasking.MaskToken(requestData.SessionCookie),
                session.ID);

            RejectPreAuthSession(session, $@"Match Server Manager ID Mismatch: Received ""{manager.ID}""");
            return;
        }

        Account? hostAccount = await databaseContext.Accounts.FindAsync(manager.HostAccountID);

        // Validate Host Account Against Database
        if (hostAccount is null)
        {
            Log.Warning(
                @"[SECURITY-ALERT] Could Not Find Host Account ID ""{HostAccountID}"" For Match Server Manager ID ""{ServerManagerID}"" on Session {SessionID}",
                manager.HostAccountID,
                requestData.ServerManagerID,
                session.ID);

            RejectPreAuthSession(session, "Match Server Manager Host Account Not Found");
            return;
        }

        // Validate Match Hosting Permissions BEFORE assigning Account or ManagerMetadata
        if (hostAccount.Type != AccountType.ServerHost)
        {
            Log.Warning(
                @"[SECURITY-ALERT] Host Account ID ""{HostAccountID}"" For Match Server Manager ID ""{ServerManagerID}"" Does Not Have Match Hosting Permissions on Session {SessionID}",
                manager.HostAccountID,
                requestData.ServerManagerID,
                session.ID);

            RejectPreAuthSession(session, "The Match Server Manager Host Account Does Not Have Match Hosting Permissions");
            return;
        }

        // Validate Host Account ID Match
        if (hostAccount.ID != manager.HostAccountID)
        {
            Log.Warning(
                @"[SECURITY-ALERT] Match Server Manager Host Account ID ""{ReceivedHostAccountID}"" Does Not Match Expected ""{ExpectedHostAccountID}"" For Manager ID ""{ServerManagerID}"" on Session {SessionID}",
                manager.HostAccountID,
                hostAccount.ID,
                requestData.ServerManagerID,
                session.ID);

            RejectPreAuthSession(
                session,
                $@"Match Server Manager Host Account ID Mismatch: Received ""{manager.HostAccountID}""");
            return;
        }

        // Authentication Succeeded: Safely replace any stale duplicate session for the same ServerManagerID without wiping Redis
        if (chatContext.MatchServerManagerChatSessions.TryGetValue(
                requestData.ServerManagerID,
                out ChatSession? existingSession) &&
            !ReferenceEquals(existingSession, session))
        {
            Log.Information(
                @"Disconnecting Duplicate Match Server Manager Instance With ID ""{ServerID}"" And Address ""{Address}"" (OldSession={OldSessionID}, NewSession={NewSessionID})",
                requestData.ServerManagerID,
                manager.IPAddress,
                existingSession.ID,
                session.ID);

            ((ICollection<KeyValuePair<int, ChatSession>>)chatContext.MatchServerManagerChatSessions)
                .Remove(new KeyValuePair<int, ChatSession>(requestData.ServerManagerID, existingSession));

            existingSession.Disconnect();
            existingSession.Dispose();
        }

        // Populate authenticated session identity and register Match Server Manager
        session.Account = hostAccount;
        session.ManagerMetadata = requestData.ToMetadata();
        session.MarkAuthenticated(ChatPeerRole.ServerManager);
        chatContext.MatchServerManagerChatSessions[requestData.ServerManagerID] = session;

        Log.Information(
            @"Match Server Manager Connection Accepted - Manager ID: ""{ManagerID}"", Host Account: ""{HostAccountName}"", Address: ""{Address}""",
            requestData.ServerManagerID, manager.HostAccountName, manager.IPAddress);

        ChatBuffer acceptResponse = new();
        acceptResponse.WriteCommand(ChatProtocol.ChatServerToServerManager.NET_CHAT_SM_ACCEPT);
        session.Send(acceptResponse);

        // Send Match Server Manager Options
        ChatBuffer optionsResponse = new();

        optionsResponse.WriteCommand(ChatProtocol.ChatServerToServerManager.NET_CHAT_SM_OPTIONS);

        optionsResponse.WriteInt8(Convert.ToByte(true)); // Submit Stats Enabled
        optionsResponse.WriteInt8(Convert.ToByte(true)); // Upload Replays Enabled
        optionsResponse.WriteInt8(Convert.ToByte(false)); // Upload To FTP On Demand Enabled
        optionsResponse.WriteInt8(Convert.ToByte(true)); // Upload To HTTP On Demand Enabled
        optionsResponse.WriteInt8(Convert.ToByte(true)); // Resubmit Stats Enabled
        optionsResponse.WriteInt32(1); // Stats Resubmit Match ID Cut-Off (Minimum Match ID For Resubmission)
        // Note(protocol): Default resubmit cut-off initialized to 1; dynamically bounded against latest database match watermark on sync.

        session.Send(optionsResponse);

        Log.Debug(
            @"Match Server Manager ID ""{ManagerID}"" Options - Statistics: Enabled, Replays: Enabled, HTTP Upload: Enabled",
            requestData.ServerManagerID);
    }

    private static void RejectPreAuthSession(ChatSession session, string reason)
    {
        ChatBuffer rejectResponse = new();
        rejectResponse.WriteCommand(ChatProtocol.ChatServerToGameServer.NET_CHAT_GS_REJECT);
        rejectResponse.WriteString(reason);

        session.Send(rejectResponse);
        session.Disconnect();
        session.Dispose();
    }
}
