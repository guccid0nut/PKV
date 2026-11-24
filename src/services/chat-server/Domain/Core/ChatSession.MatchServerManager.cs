// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace TRANSMUTANSTEIN.ChatServer.Domain.Core;

public partial class ChatSession
{
    /// <summary>
    ///     Gets set after a successful match server manager handshake.
    ///     Contains metadata about the match server manager connected to this chat session.
    /// </summary>
    public MatchServerManagerChatSessionMetadata ManagerMetadata { get; set; } = null!;

    public async Task TerminateMatchServerManager(IDatabase distributedCacheStore)
    {
        if (ManagerMetadata is null)
        {
            Disconnect();
            Dispose();
            return;
        }

        int managerId = ManagerMetadata.ServerManagerID;
        bool hasActiveSession = _chatContext.MatchServerManagerChatSessions.TryGetValue(managerId, out ChatSession? activeSession);

        if (hasActiveSession && activeSession is not null && !ReferenceEquals(activeSession, this) && activeSession.ID != ID)
        {
            Log.Warning(
                "[SECURITY-ALERT] Session {SessionID} attempted TerminateMatchServerManager for Manager ID \"{ServerManagerID}\" owned by live Session {OwnerSessionID}; blocking pool and Redis eviction",
                ID,
                managerId,
                activeSession.ID);

            Disconnect();
            Dispose();
            return;
        }

        if (!hasActiveSession && !IsAuthenticated)
        {
            Log.Warning(
                "[SECURITY-ALERT] Unauthenticated Session {SessionID} attempted TerminateMatchServerManager for Manager ID \"{ServerManagerID}\"; blocking Redis eviction",
                ID,
                managerId);

            Disconnect();
            Dispose();
            return;
        }

        bool removedFromPool = hasActiveSession
                               && activeSession is not null
                               && ((ICollection<KeyValuePair<int, ChatSession>>)_chatContext.MatchServerManagerChatSessions)
                               .Remove(new KeyValuePair<int, ChatSession>(managerId, activeSession));

        await RemoveMatchServerManager(distributedCacheStore);

        if (removedFromPool && activeSession is not null)
        {
            Log.Information(
                @"Match Server Manager ID ""{ServerManagerID}"" Was Removed From The Match Server Manager Pool",
                managerId);

            if (activeSession.ManagerMetadata?.SessionCookie != ManagerMetadata.SessionCookie)
            {
                Log.Warning(@"Match Server Manager ID ""{ServerManagerID}"" Had A Mismatched Session Cookie",
                    managerId);

                Disconnect();
                Dispose();
                return;
            }

            ChatBuffer acknowledgementResponse = new();

            acknowledgementResponse.WriteCommand(ChatProtocol.ChatServerToServerManager.NET_CHAT_SM_REMOTE_COMMAND);
            acknowledgementResponse.WriteString(ManagerMetadata.SessionCookie);
            acknowledgementResponse.WriteString("quit");

            // Send Disconnect Acknowledgement To Match Server Manager
            Send(acknowledgementResponse);
        }
        else
        {
            Log.Warning(
                @"Match Server Manager ID ""{ServerManagerID}"" Attempted To Disconnect But Was Not Found In The Match Server Manager Pool",
                managerId);
        }

        // Disconnect And Dispose The Chat Session
        Disconnect();
        Dispose();

        Log.Information(@"Match Server Manager ID ""{ServerManagerID}"" Has Disconnected Gracefully", managerId);
    }

    private async Task RemoveMatchServerManager(IDatabase distributedCacheStore)
    {
        // Remove Match Server Manager From The Distributed Cache
        // Match Server Children Are Also Implicitly Removed From The Distributed Cache
        await distributedCacheStore.RemoveMatchServerManagerByID(ManagerMetadata.ServerManagerID);
    }
}
