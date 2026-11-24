// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using TRANSMUTANSTEIN.ChatServer.Domain.Clans;

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.Clans;

[ChatCommand(ChatProtocol.Command.CHAT_CMD_CLAN_ADD_REJECTED)]
public class ClanAddRejected(IPendingClanService pendingClanService, IChatContext chatContext)
    : IAsynchronousCommandProcessor<ChatSession>
{
    public Task Process(ChatSession session, ChatBuffer buffer)
    {
        Account account = session.Account;

        pendingClanService.RemoveObsoledPendingClanInvites();

        string? inviteKey = pendingClanService.GetPendingClanInviteKeyForUser(account);

        if (inviteKey != null)
        {
            PendingClanInvite? invite = pendingClanService.GetPendingClanInvite(inviteKey);

            if (invite != null)
            {
                // Notify Inviter
                ChatSession? inviterSession =
                    chatContext.ClientChatSessions.Values.FirstOrDefault(cs =>
                        cs.Account.ID == invite.InitiatorAccountId);
                inviterSession?.Send(new ClanAddRejectedResponse(account.Name));
            }

            pendingClanService.RemovePendingClanInvite(inviteKey);
        }

        return Task.CompletedTask;
    }
}
