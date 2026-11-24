// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using Log = Serilog.Log;

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.Social;

/// <summary>
///     Handles friend removal notification (Command 0x000E - CHAT_CMD_NOTIFY_BUDDY_REMOVE).
///     Ensures bidirectional core.Friendships rows and both live ChatSessions' Account.Friendships caches
///     are immediately pruned, and sends 0x000C DISCONNECTED status updates unless the accounts share a clan.
/// </summary>
[ChatCommand(ChatProtocol.Command.CHAT_CMD_NOTIFY_BUDDY_REMOVE)]
internal sealed class RemoveFriend(
    IChatContext chatContext,
    MerrickContext? merrick = null) : IAsynchronousCommandProcessor<ChatSession>
{
    public async Task Process(ChatSession session, ChatBuffer buffer)
    {
        RemoveFriendRequestData requestData = new(buffer);

        if ((object?)session.Account is null || requestData.RemovedFriendAccountID <= 0) return;

        int requesterId = session.Account.ID;
        int removedId = requestData.RemovedFriendAccountID;

        if (merrick is not null)
        {
            List<Friendship> rows = await merrick.Friendships
                .Where(f => (f.AccountID == requesterId && f.FriendAccountID == removedId) ||
                            (f.AccountID == removedId && f.FriendAccountID == requesterId))
                .ToListAsync();

            if (rows.Count > 0)
            {
                merrick.Friendships.RemoveRange(rows);
                await merrick.SaveChangesAsync();
            }
        }

        int removedFromRequester = session.Account.Friendships?.RemoveAll(f => f.FriendAccountID == removedId) ?? 0;

        ChatSession? exFriendSession = chatContext.ClientChatSessions.Values.FirstOrDefault(s =>
            (object?)s.Account is not null && s.Account.ID == removedId);

        int removedFromTarget = 0;
        if (exFriendSession is not null)
        {
            removedFromTarget = exFriendSession.Account.Friendships?.RemoveAll(f => f.FriendAccountID == requesterId) ?? 0;

            bool shareClan = session.Account.Clan?.ID is > 0 &&
                             session.Account.Clan.ID == exFriendSession.Account.Clan?.ID;

            if (!shareClan)
            {
                session.SendDirectStatusUpdateTo(
                    exFriendSession,
                    ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_DISCONNECTED);
                exFriendSession.SendDirectStatusUpdateTo(
                    session,
                    ChatProtocol.ChatClientStatus.CHAT_CLIENT_STATUS_DISCONNECTED);
            }
        }

        Log.Information(
            "[RemoveFriend] Processed 0x000E removal between '{Requester}' (ID: {RequesterID}, Evicted={EvictedReq}) and PeerID #{RemovedID} (Online={PeerOnline}, Evicted={EvictedPeer})",
            session.Account.Name, requesterId, removedFromRequester, removedId, exFriendSession is not null, removedFromTarget);
    }
}
