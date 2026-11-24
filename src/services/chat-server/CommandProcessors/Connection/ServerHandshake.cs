// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using NEXUS.Contracts.Security;

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.Connection;

[ChatCommand(
    ChatProtocol.GameServerToChatServer.NET_CHAT_GS_CONNECT,
    AllowUnauthenticated = true,
    AllowedRoles = ChatPeerRole.MatchServer)]
internal class ServerHandshake(
    IDatabase distributedCacheStore,
    MerrickContext databaseContext,
    IChatContext chatContext,
    ITelemetryPublisherService? telemetryPublisher = null,
    IQueueArcadeService? queueArcadeService = null)
    : IAsynchronousCommandProcessor<ChatSession>
{
    public async Task Process(ChatSession session, ChatBuffer buffer)
    {
        ServerHandshakeRequestData requestData = new(buffer);

        // Validate Protocol Version BEFORE populating session metadata or touching active server pool
        if (requestData.ChatProtocolVersion != ChatProtocol.CHAT_PROTOCOL_EXTERNAL_VERSION)
        {
            Log.Warning(
                @"[SECURITY-ALERT] Match Server ID ""{ServerID}"" Chat Protocol Version Mismatch on Session {SessionID} (Expected: ""{ExpectedVersion}"", Received: ""{ReceivedVersion}"")",
                requestData.ServerId, session.ID, ChatProtocol.CHAT_PROTOCOL_EXTERNAL_VERSION, requestData.ChatProtocolVersion);

            RejectPreAuthSession(
                session,
                $@"Chat Protocol Version Mismatch: Expected ""{ChatProtocol.CHAT_PROTOCOL_EXTERNAL_VERSION}"", Received ""{requestData.ChatProtocolVersion}""");
            return;
        }

        MatchServer? server = !string.IsNullOrWhiteSpace(requestData.SessionCookie)
            ? await distributedCacheStore.GetMatchServerBySessionCookie(requestData.SessionCookie)
            : null;

        // Validate Server Cookie Against Distributed Cache
        if (server is null)
        {
            Log.Warning(
                @"[SECURITY-ALERT] Match Server ID ""{ServerID}"" Cookie ""{Cookie}"" Is Invalid on Session {SessionID}; rejecting without touching live pool",
                requestData.ServerId,
                SecurityMasking.MaskToken(requestData.SessionCookie),
                session.ID);

            RejectPreAuthSession(session, "The Session Cookie Provided For Chat Server Authentication Is Invalid");
            return;
        }

        // Validate Server ID Match
        if (server.ID != requestData.ServerId)
        {
            Log.Warning(
                @"[SECURITY-ALERT] Match Server ID ""{ServerID}"" Does Not Match Cookie Owner Server ID ""{CookieServerID}"" (Cookie: ""{Cookie}"", Session: {SessionID}); rejecting without touching live pool",
                requestData.ServerId,
                server.ID,
                SecurityMasking.MaskToken(requestData.SessionCookie),
                session.ID);

            RejectPreAuthSession(session, $@"Match Server ID Mismatch: Received ""{server.ID}""");
            return;
        }

        Account? hostAccount = await databaseContext.Accounts.FindAsync(server.HostAccountID);

        // Validate Host Account Against Database
        if (hostAccount is null)
        {
            Log.Warning(
                @"[SECURITY-ALERT] Could Not Find Host Account ID ""{HostAccountID}"" For Match Server ID ""{ServerID}"" on Session {SessionID}",
                server.HostAccountID,
                requestData.ServerId,
                session.ID);

            RejectPreAuthSession(session, "Match Server Host Account Not Found");
            return;
        }

        // Validate Match Hosting Permissions BEFORE assigning Account or ServerMetadata
        if (hostAccount.Type != AccountType.ServerHost)
        {
            Log.Warning(
                @"[SECURITY-ALERT] Host Account ID ""{HostAccountID}"" For Match Server ID ""{ServerID}"" Does Not Have Match Hosting Permissions on Session {SessionID}",
                server.HostAccountID,
                requestData.ServerId,
                session.ID);

            RejectPreAuthSession(session, "The Match Server Host Account Does Not Have Match Hosting Permissions");
            return;
        }

        // Validate Host Account ID Match
        if (hostAccount.ID != server.HostAccountID)
        {
            Log.Warning(
                @"[SECURITY-ALERT] Match Server Host Account ID ""{ReceivedHostAccountID}"" Does Not Match Expected ""{ExpectedHostAccountID}"" For Match Server ID ""{ServerID}"" on Session {SessionID}",
                server.HostAccountID,
                hostAccount.ID,
                requestData.ServerId,
                session.ID);

            RejectPreAuthSession(session, $@"Match Server Host Account ID Mismatch: Received ""{server.HostAccountID}""");
            return;
        }

        // Authentication Succeeded: Now safely replace any stale duplicate session for the same ServerId
        if (chatContext.MatchServerChatSessions.TryGetValue(requestData.ServerId, out ChatSession? existingSession) &&
            !ReferenceEquals(existingSession, session))
        {
            Log.Information(
                @"Disconnecting Duplicate Match Server Instance With ID ""{ServerID}"" And Address ""{Address}:{Port}"" (OldSession={OldSessionID}, NewSession={NewSessionID})",
                requestData.ServerId,
                server.IPAddress,
                server.Port,
                existingSession.ID,
                session.ID);

            ((ICollection<KeyValuePair<int, ChatSession>>)chatContext.MatchServerChatSessions)
                .Remove(new KeyValuePair<int, ChatSession>(requestData.ServerId, existingSession));

            existingSession.Disconnect();
            existingSession.Dispose();
        }

        // Populate authenticated session identity and register Match Server
        MatchServerChatSessionMetadata metadata = requestData.ToMetadata();
        metadata.Address = server.IPAddress;
        metadata.Port = server.Port;
        metadata.LocalAddress = server.LocalIPAddress ?? string.Empty;
        metadata.LocalPort = server.LocalPort ?? server.Port;
        metadata.Location = server.Location ?? string.Empty;
        metadata.Status = ChatProtocol.ServerStatus.SERVER_STATUS_IDLE;
        metadata.IsQueueArcade = server.IsQueueArcadeServer() || MatchServerChatSessionMetadata.DetectQueueArcade(
            server.Name,
            null,
            server.Port,
            metadata.LocalPort,
            server.HostAccountName,
            server.Instance);

        session.Account = hostAccount;
        session.ServerMetadata = metadata;
        session.MarkAuthenticated(ChatPeerRole.MatchServer);
        chatContext.MatchServerChatSessions[requestData.ServerId] = session;

        _ = telemetryPublisher?.OnMatchServerConnectedAsync(requestData.ServerId, session.ServerMetadata.Location);

        Log.Information(
            @"Match Server Connection Accepted - Server ID: ""{ServerID}"", Host Account: ""{HostAccountName}"", Instance: {Instance}, Address: ""{Address}:{Port}"", Location: ""{Location}"", IsQueueArcade: {IsQueueArcade}",
            requestData.ServerId, server.HostAccountName, server.Instance, server.IPAddress, server.Port, server.Location, metadata.IsQueueArcade);

        ChatBuffer acceptResponse = new();
        acceptResponse.WriteCommand(ChatProtocol.ChatServerToGameServer.NET_CHAT_GS_ACCEPT);
        session.Send(acceptResponse);

        string activeServerName = !string.IsNullOrWhiteSpace(server.Name)
            ? server.Name
            : Random.Shared.Next().ToString("X8");

        if (metadata.IsQueueArcade && !activeServerName.Contains("QUEUE ARCADE", StringComparison.OrdinalIgnoreCase))
        {
            activeServerName = $"QUEUE ARCADE {activeServerName}";
        }

        if (metadata.IsQueueArcade)
        {
            ChatBuffer arcadeModeCommand = new();
            arcadeModeCommand.WriteCommand(ChatProtocol.ChatServerToGameServer.NET_CHAT_GS_REMOTE_COMMAND);
            arcadeModeCommand.WriteString(requestData.SessionCookie);
            arcadeModeCommand.WriteString("kongor_arcade_mode 1");
            session.Send(arcadeModeCommand);
        }

        ChatBuffer remoteCommand = new();

        string[] commands = metadata.IsQueueArcade
            ?
            [
                "svr_submitMatchStatItems false",
                "svr_submitMatchStatAbilities false",
                "svr_submitMatchStatFrags false",
                "sv_afkTimeout 0",
                "sv_afkWarningTime 0",
                "svr_recordReplay false",
                "g_voteKickCooldownTime 99999999",
                $"svr_name \"{activeServerName}\"",
                "echo Project KONGOR Queue Arcade Configuration Was Injected Successfully"
            ]
            :
            [
                "svr_submitMatchStatItems true",
                "svr_submitMatchStatAbilities true",
                "svr_submitMatchStatFrags true",
                $"svr_name \"{activeServerName}\"",
                "echo Project KONGOR Remote Configuration Was Injected Successfully"
            ];

        remoteCommand.WriteCommand(ChatProtocol.ChatServerToGameServer.NET_CHAT_GS_REMOTE_COMMAND);
        remoteCommand.WriteString(requestData.SessionCookie);
        remoteCommand.WriteString(string.Join(";", commands));

        // Inject The Match Server With Remote Configuration
        session.Send(remoteCommand);

        server.Name = activeServerName;

        // Update The Match Server Name In The Distributed Cache
        await distributedCacheStore.SetMatchServer(server.HostAccountName, server);

        // Update Metadata
        session.ServerMetadata.Address = server.IPAddress;
        session.ServerMetadata.Port = server.Port;
        session.ServerMetadata.LocalAddress = server.LocalIPAddress ?? string.Empty;
        session.ServerMetadata.LocalPort = server.LocalPort ?? server.Port;

        if (session.ServerMetadata.IsQueueArcade)
        {
            queueArcadeService?.OnArcadeServerHandshake(session);
        }
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
