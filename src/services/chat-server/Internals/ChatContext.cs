// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace TRANSMUTANSTEIN.ChatServer.Internals;

public class ChatContext : IChatContext
{
    public ConcurrentDictionary<string, ChatSession> ClientChatSessions => Context.ClientChatSessions;

    public ConcurrentDictionary<int, ChatSession> MatchServerChatSessions => Context.MatchServerChatSessions;

    public ConcurrentDictionary<int, ChatSession> MatchServerManagerChatSessions =>
        Context.MatchServerManagerChatSessions;

    public ConcurrentDictionary<string, ChatChannel> ChatChannels => Context.ChatChannels;

    public ConcurrentDictionary<int, ChatChannel> ChatChannelsByID => Context.ChatChannelsByID;
}
