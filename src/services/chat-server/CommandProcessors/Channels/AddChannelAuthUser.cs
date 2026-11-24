// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.Channels;

[ChatCommand(ChatProtocol.Command.CHAT_CMD_CHANNEL_ADD_AUTH_USER)]
public class AddChannelAuthUser(MerrickContext merrick, IChatContext chatContext)
    : IAsynchronousCommandProcessor<ChatSession>
{
    public async Task Process(ChatSession session, ChatBuffer buffer)
    {
        AddChannelAuthUserRequestData requestData = new(buffer);

        int targetAccountId;
        string targetName = requestData.TargetName;

        ChatSession? targetSession = chatContext.ClientChatSessions.Values
            .FirstOrDefault(chatSession =>
                chatSession.Account.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase));

        // Fallback: Try checking for name without clan tag
        if (targetSession == null && targetName.Contains(']'))
        {
            int closingBracketIndex = targetName.IndexOf(']');
            if (closingBracketIndex >= 0 && closingBracketIndex < targetName.Length - 1)
            {
                string strippedName = targetName[(closingBracketIndex + 1)..];
                targetSession = chatContext.ClientChatSessions.Values
                    .FirstOrDefault(chatSession =>
                        chatSession.Account.Name.Equals(strippedName, StringComparison.OrdinalIgnoreCase));

                if (targetSession != null) targetName = strippedName;
            }
        }

        if (targetSession != null)
        {
            targetAccountId = targetSession.Account.ID;
            targetName = targetSession.Account.Name;
        }
        else
        {
            Account? account = await merrick.Accounts
                .FirstOrDefaultAsync(a => a.Name == targetName);

            // Fallback for DB: Try stripped name
            if (account == null && targetName.Contains(']'))
            {
                int closingBracketIndex = targetName.IndexOf(']');
                if (closingBracketIndex >= 0 && closingBracketIndex < targetName.Length - 1)
                {
                    string strippedName = targetName[(closingBracketIndex + 1)..];

                    account = await merrick.Accounts
                        .FirstOrDefaultAsync(a => a.Name == strippedName);

                    if (account != null) targetName = strippedName;
                }
            }

            if (account == null)
            {
                // Send Failure
                ChatBuffer fail = new();
                fail.WriteCommand(ChatProtocol.Command.CHAT_CMD_CHANNEL_ADD_AUTH_FAIL);
                fail.WriteString(requestData.ChannelName);
                fail.WriteString(targetName);
                session.Send(fail);
                return;
            }

            targetAccountId = account.ID;
            targetName = account.Name;
        }

        ChatChannel? channel = ChatChannel.Get(chatContext, session, requestData.ChannelName);

        channel?.AddAuthUser(session, targetAccountId, targetName);
    }
}
