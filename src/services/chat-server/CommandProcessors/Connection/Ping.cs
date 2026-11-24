// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.Connection;

[ChatCommand(ChatProtocol.Bidirectional.NET_CHAT_PING, AllowUnauthenticated = true, AllowedRoles = ChatPeerRole.Any)]
public class Ping : ISynchronousCommandProcessor<ChatSession>
{
    public void Process(ChatSession session, ChatBuffer buffer)
    {
        // Log.Information("[PING] Command Received from Session {SessionID}", session.ID);
        ChatBuffer pong = new();
        pong.WriteCommand(ChatProtocol.Bidirectional.NET_CHAT_PONG);

        // Echo back any payload (e.g. timestamp)
        // buffer.Data contains [CommandID (2 bytes)] + [Payload]
        // We skip the first 2 bytes of the incoming buffer
        if (buffer.Size > 2) pong.Append(buffer.Data.AsSpan(2, (int)buffer.Size - 2));

        session.Send(pong);
        // Log.Information("[PING] Pong Sent to Session {SessionID}", session.ID);
    }
}
