// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.Channels;

[ChatCommand(ChatProtocol.Command.CHAT_CMD_CHANNEL_TOPIC)]
public class SetChannelTopic(IChatContext chatContext) : ISynchronousCommandProcessor<ChatSession>
{
    public void Process(ChatSession session, ChatBuffer buffer)
    {
        SetChannelTopicRequestData requestData = new(buffer);

        // Find Channel
        ChatChannel? channel = ChatChannel.Get(chatContext, session, requestData.ChannelId);

        // If Channel Not Found or User Not Member (Get checks membership), do nothing?
        // Legacy: ChannelTopicRequest checks if channel exists.

        if (channel is null) return;

        // Check Permissions
        // ChatChannel has members. Need to check if user is admin/op.
        // ChatChannel.Members is ConcurrentDictionary<string, ChatChannelMember>

        if (!channel.Members.TryGetValue(session.Account.Name, out ChatChannelMember? member)) return;

        // Legacy: if (!member.IsOp && !member.IsAdmin && !member.IsStaff) -> Rejected.
        // ChatChannelMember has IsAdministrator (level >= Officer).

        if (!member.HasElevatedPrivileges())
        {
            Log.Warning("User {UserName} attempted to set topic for channel {ChannelId} without elevated privileges",
                session.Account.Name, channel.ID);
            // Note(protocol): Legacy protocol specifies silent drop for unauthorized topic update attempts.
            return;
        }

        // Update Topic (bounded to 255 characters)
        string topic = requestData.Topic ?? string.Empty;
        if (topic.Length > 255) topic = topic[..255];
        channel.Topic = topic;

        // Broadcast New Topic To Channel
        ChatBuffer broadcast = new();
        broadcast.WriteCommand(ChatProtocol.Command.CHAT_CMD_CHANNEL_TOPIC);
        broadcast.WriteInt32(channel.ID);
        broadcast.WriteString(topic);

        channel.BroadcastMessage(broadcast);
    }
}
