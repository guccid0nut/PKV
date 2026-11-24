// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace TRANSMUTANSTEIN.ChatServer.Attributes;

[Flags]
public enum ChatPeerRole : byte
{
    None = 0,
    Client = 1 << 0,
    MatchServer = 1 << 1,
    ServerManager = 1 << 2,
    Any = Client | MatchServer | ServerManager
}

[AttributeUsage(AttributeTargets.Class)]
public class ChatCommandAttribute(ushort command) : Attribute
{
    public ushort Command { get; init; } = command;

    public bool AllowUnauthenticated { get; init; }

    public ChatPeerRole AllowedRoles { get; init; } = ResolveDefaultAllowedRoles(command);

    public static ChatPeerRole ResolveDefaultAllowedRoles(ushort command) => command switch
    {
        >= 0x0500 and <= 0x05FF => ChatPeerRole.MatchServer,
        >= 0x1600 and <= 0x16FF => ChatPeerRole.ServerManager,
        0x2A00 or 0x2A01 => ChatPeerRole.Any,
        _ => ChatPeerRole.Client
    };
}
