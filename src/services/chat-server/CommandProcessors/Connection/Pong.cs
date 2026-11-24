// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.Connection;

[ChatCommand(ChatProtocol.Bidirectional.NET_CHAT_PONG, AllowUnauthenticated = true, AllowedRoles = ChatPeerRole.Any)]
public class Pong : ISynchronousCommandProcessor<ChatSession>
{
    public void Process(ChatSession session, ChatBuffer buffer)
    {
        // No-op: The purpose of receiving a Pong is simply to keep the connection alive,
        // which happens implicitly by receiving data on the socket.
    }
}
