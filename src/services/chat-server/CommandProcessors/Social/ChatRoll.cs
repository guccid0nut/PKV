// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.Social;

[ChatCommand(ChatProtocol.Command.CHAT_CMD_CHAT_ROLL)]
public class ChatRoll(IChatContext chatContext) : ISynchronousCommandProcessor<ChatSession>
{
    public void Process(ChatSession session, ChatBuffer buffer)
    {
        ChatRollRequestData requestData = new(buffer);

        ChatChannel.Get(chatContext, session, requestData.ChannelID)?.Roll(session, requestData.Parameters);
    }
}
