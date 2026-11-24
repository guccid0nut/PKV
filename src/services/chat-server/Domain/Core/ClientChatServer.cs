// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace TRANSMUTANSTEIN.ChatServer.Domain.Core;

/// <summary>
///     TCP server for handling client connections.
///     Creates ClientChatSession instances for each incoming connection.
/// </summary>
public class ClientChatServer(IServiceProvider serviceProvider, IPAddress address, int port) : TCPServer(address, port)
{
    protected override TCPSession CreateSession()
    {
        return new ChatSession(this, serviceProvider);
    }

    protected override void OnStarted()
    {
        Log.Information("Chat Server Has Started Accepting Client Connections On {Endpoint}", Endpoint);

        base.OnStarted();
    }

    protected override void OnError(SocketError socketError)
    {
        Log.Error("Chat Server Has Encountered A Client Connection Socket Error: {SocketError}", socketError);

        base.OnError(socketError);
    }
}
