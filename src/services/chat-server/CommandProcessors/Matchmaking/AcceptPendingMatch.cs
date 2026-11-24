// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using TRANSMUTANSTEIN.ChatServer.Attributes;
using TRANSMUTANSTEIN.ChatServer.Domain.Core;
using TRANSMUTANSTEIN.ChatServer.Internals;
using TRANSMUTANSTEIN.ChatServer.Services;

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.Matchmaking;

[ChatCommand(ChatProtocol.Matchmaking.NET_CHAT_CL_TMM_ACCEPT_PENDING_MATCH)]
internal class AcceptPendingMatch(IMatchmakingService matchmakingService) : IAsynchronousCommandProcessor<ChatSession>
{
    public async Task Process(ChatSession session, ChatBuffer buffer)
    {
        if (session.Account is null)
        {
            Log.Warning("[AcceptPendingMatch] Ignoring native 0x0F05 packet on unauthenticated Session={SessionId}", session.ID);
            return;
        }

        bool handled = await matchmakingService.SubmitPendingMatchAcceptAsync(session.Account.ID).ConfigureAwait(false);
        Log.Debug(
            "[AcceptPendingMatch] Processed native 0x0F05 AcceptPendingMatch for AccountID={AccountID} Handled={Handled}",
            session.Account.ID,
            handled);
    }
}
