// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.Social;

/// <summary>
///     Handles friend approval requests.
///     Approves a pending friend request from another player, creating a bi-directional friendship.
/// </summary>
[ChatCommand(ChatProtocol.Command.CHAT_CMD_REQUEST_BUDDY_APPROVE)]
public class ApproveFriend(MerrickContext merrick, IDatabase distributedCacheStore, IChatContext chatContext)
    : IAsynchronousCommandProcessor<ChatSession>
{
    public async Task Process(ChatSession session, ChatBuffer buffer)
    {
        ApproveFriendRequestData requestData = new(buffer);

        await Friend
            .WithAccountName(requestData.FriendNickname)
            .Approve(session, merrick, distributedCacheStore, chatContext);
    }
}
