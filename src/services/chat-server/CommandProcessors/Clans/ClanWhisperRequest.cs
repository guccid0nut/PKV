// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using TRANSMUTANSTEIN.ChatServer.Domain.Clans;
using TRANSMUTANSTEIN.ChatServer.Infrastructure.Distributed;

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.Clans;

[ChatCommand(ChatProtocol.Command.CHAT_CMD_CLAN_WHISPER)]
public class ClanWhisperRequest(
    IChatContext chatContext,
    IDistributedChatStateService? distributedState = null)
    : ISynchronousCommandProcessor<ChatSession>
{
    public void Process(ChatSession session, ChatBuffer buffer)
    {
        buffer.ReadCommandBytes(); // Skip Header
        string message = buffer.ReadString();
        Account? account = (Account?)session.Account;

        if (account?.Clan is null)
        {
            session.Send(new ClanWhisperFailedResponse());
            return;
        }

        // Broadcast to all local online clan members (excluding self)
        ClanWhisperResponse response = new(account.ID, message);

        foreach (ChatSession clientSession in chatContext.ClientChatSessions.Values)
            if (clientSession.Account is { Clan: not null } &&
                clientSession.Account.Clan.ID == account.Clan.ID &&
                clientSession.Account.ID != account.ID &&
                !clientSession.Account.HasIgnoredPeer(account.ID))
                clientSession.Send(response);

        // Broadcast to remote nodes hosting clan members
        if (distributedState != null)
        {
            _ = distributedState.PublishClanWhisperAsync(account.Clan.ID, account.ID, account.Name, message);
        }
    }
}
