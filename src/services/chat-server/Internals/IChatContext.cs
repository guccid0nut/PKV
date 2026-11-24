// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace TRANSMUTANSTEIN.ChatServer.Internals;

/// <summary>
///     Defines the shared state context for the Chat Server (Sessions and Channels).
///     Scoped as Singleton in Production, but isolated per-host in Tests.
/// </summary>
public interface IChatContext
{
    ConcurrentDictionary<string, ChatSession> ClientChatSessions { get; }

    ConcurrentDictionary<int, ChatSession> MatchServerChatSessions { get; }

    ConcurrentDictionary<int, ChatSession> MatchServerManagerChatSessions { get; }

    ConcurrentDictionary<string, ChatChannel> ChatChannels { get; }

    ConcurrentDictionary<int, ChatChannel> ChatChannelsByID { get; }
}
