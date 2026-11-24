// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.Channels;

[ChatCommand(ChatProtocol.Command.CHAT_CMD_JOIN_CHANNEL)]
internal class JoinChannel(IChatContext chatContext) : ISynchronousCommandProcessor<ChatSession>
{
    public void Process(ChatSession session, ChatBuffer buffer)
    {
        JoinChannelRequestData requestData = new(buffer);

        string channelName = SanitizeChannelName(requestData.ChannelName);
        if (string.IsNullOrWhiteSpace(channelName)) return;

        ChatChannel
            .GetOrCreate(chatContext, session, channelName)
            .Join(session);
    }

    public static string SanitizeChannelName(string? rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName)) return string.Empty;
        string trimmed = rawName.Trim();
        if (trimmed.Length > 32) trimmed = trimmed[..32];

        if (trimmed.Contains("..") || trimmed.Contains('/') || trimmed.Contains('\\') ||
            trimmed.Contains('<') || trimmed.Contains('>') || trimmed.Contains('"') || trimmed.Contains('\''))
        {
            return string.Empty;
        }

        return trimmed;
    }
}
