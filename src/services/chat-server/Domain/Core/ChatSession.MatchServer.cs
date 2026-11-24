// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace TRANSMUTANSTEIN.ChatServer.Domain.Core;

public partial class ChatSession
{
    /// <summary>
    ///     Gets set after a successful match server handshake.
    ///     Contains metadata about the match server connected to this chat session.
    /// </summary>
    public virtual MatchServerChatSessionMetadata ServerMetadata { get; set; } = null!;

    public async Task TerminateMatchServer(IDatabase distributedCacheStore)
    {
        if (ServerMetadata is null)
        {
            Disconnect();
            Dispose();
            return;
        }

        int serverId = ServerMetadata.ServerID;
        bool hasActiveSession = _chatContext.MatchServerChatSessions.TryGetValue(serverId, out ChatSession? activeSession);

        if (hasActiveSession && activeSession is not null && !ReferenceEquals(activeSession, this) && activeSession.ID != ID)
        {
            Log.Warning(
                "[SECURITY-ALERT] Session {SessionID} attempted TerminateMatchServer for Server ID \"{ServerID}\" owned by live Session {OwnerSessionID}; blocking pool and Redis eviction",
                ID,
                serverId,
                activeSession.ID);

            Disconnect();
            Dispose();
            return;
        }

        if (!hasActiveSession && !IsAuthenticated)
        {
            Log.Warning(
                "[SECURITY-ALERT] Unauthenticated Session {SessionID} attempted TerminateMatchServer for Server ID \"{ServerID}\"; blocking Redis eviction",
                ID,
                serverId);

            Disconnect();
            Dispose();
            return;
        }

        bool removedFromPool = hasActiveSession
                               && activeSession is not null
                               && ((ICollection<KeyValuePair<int, ChatSession>>)_chatContext.MatchServerChatSessions)
                               .Remove(new KeyValuePair<int, ChatSession>(serverId, activeSession));

        // Keep distributedCacheStore referenced for ServerDisconnect signature compatibility;
        // Redis MatchServer registration is intentionally preserved across transient chat disconnects.
        _ = distributedCacheStore;

        if (removedFromPool && activeSession is not null)
        {
            _ = _telemetryPublisher?.OnMatchServerDisconnectedAsync(serverId, ServerMetadata.Address);
            Log.Warning(
                @"Match Server ID ""{ServerID}"" Removed From Chat Pool (Transient Chat Disconnect; Preserving Redis MatchServer Session Cookie ""{SessionPrefix}"" And Not Sending Quit)",
                serverId,
                NEXUS.Contracts.Security.SecurityMasking.MaskToken(ServerMetadata.SessionCookie));

            if (activeSession.ServerMetadata?.SessionCookie != ServerMetadata.SessionCookie)
            {
                Log.Warning(@"Match Server ID ""{ServerID}"" Had A Mismatched Session Cookie", serverId);
            }
        }
        else
        {
            Log.Warning(
                @"Match Server ID ""{ServerID}"" Attempted To Disconnect But Was Not Found In The Match Server Pool",
                serverId);
        }

        // Disconnect And Dispose The Chat Session Socket Without Terminating The Running Match Server
        Disconnect();
        Dispose();
        await Task.CompletedTask;

        Log.Information(@"Match Server ID ""{ServerID}"" Chat Session Closed Cleanly (Redis Session Preserved)", serverId);
    }
}
