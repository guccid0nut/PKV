// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.Channels;

[ChatCommand(ChatProtocol.Command.CHAT_CMD_CHANNEL_SILENCE_USER)]
public class SilenceChannelMember(IChatContext chatContext) : ISynchronousCommandProcessor<ChatSession>
{
    public void Process(ChatSession session, ChatBuffer buffer)
    {
        SilenceChannelMemberRequestData requestData = new(buffer);

        ChatChannel? channel = ChatChannel.Get(chatContext, session, requestData.ChannelId);

        if (channel is null) return;

        // Find The Target Account ID By Name
        // We look in the channel members first as silencing only applies to current members
        ChatChannelMember? targetMember = channel.Members.Values
            .FirstOrDefault(m => m.Account.Name.Equals(requestData.TargetName, StringComparison.OrdinalIgnoreCase));

        // Fallback: Try stripped name
        if (targetMember is null && requestData.TargetName.Contains(']'))
        {
            int closingBracketIndex = requestData.TargetName.IndexOf(']');
            if (closingBracketIndex >= 0 && closingBracketIndex < requestData.TargetName.Length - 1)
            {
                string strippedName = requestData.TargetName[(closingBracketIndex + 1)..];

                targetMember = channel.Members.Values
                    .FirstOrDefault(m => m.Account.Name.Equals(strippedName, StringComparison.OrdinalIgnoreCase));
            }
        }

        if (targetMember is null)
        {
            Log.Warning("SilenceChannelMember: Target user {TargetName} not found in channel {ChannelId} (requested by {Requester})",
                requestData.TargetName, channel.ID, session.Account?.Name);
            return;
        }

        channel.Silence(session, targetMember.Account.ID, requestData.DurationMilliseconds);
    }
}
