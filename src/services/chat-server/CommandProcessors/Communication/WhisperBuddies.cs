// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MERRICK.DatabaseContext.Enumerations;

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.Communication;

[ChatCommand(ChatProtocol.Command.CHAT_CMD_WHISPER_BUDDIES)]
public class WhisperBuddies(IChatContext chatContext) : ISynchronousCommandProcessor<ChatSession>
{
    public void Process(ChatSession session, ChatBuffer buffer)
    {
        WhisperBuddiesRequestData requestData = new(buffer);

        if ((object?)session.Account is null) return;

        Whisper whisper = Whisper.Create(requestData.Message);

        foreach (ChatSession friendSession in (session.Account.Friendships ?? [])
                     .Where(friend => friend.Status == FriendshipStatus.Accepted)
                     .Select(friend => chatContext
                         .ClientChatSessions.Values
                         .FirstOrDefault(s => (object?)s.Account is not null && s.Account.ID == friend.FriendAccountID))
                     .OfType<ChatSession>())
            whisper.Send(session, friendSession);
    }
}
