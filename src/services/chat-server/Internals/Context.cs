// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace TRANSMUTANSTEIN.ChatServer.Internals;

public static class Context
{
    public static ConcurrentDictionary<string, ChatSession> ClientChatSessions { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public static ConcurrentDictionary<int, ChatSession> MatchServerChatSessions { get; set; } = [];

    public static ConcurrentDictionary<int, ChatSession> MatchServerManagerChatSessions { get; set; } = [];

    public static ConcurrentDictionary<string, ChatChannel> ChatChannels { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public static ConcurrentDictionary<int, ChatChannel> ChatChannelsByID { get; set; } = [];
}
