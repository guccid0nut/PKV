// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.Connection;

public class ServerManagerHandshakeRequestData(ChatBuffer buffer)
{
    public byte[] CommandBytes { get; init; } = buffer.ReadCommandBytes();

    public int ServerManagerID { get; } = buffer.ReadInt32();

    public string SessionCookie { get; } = buffer.ReadString();

    public int ChatProtocolVersion { get; } = buffer.ReadInt32();

    public MatchServerManagerChatSessionMetadata ToMetadata()
    {
        return new MatchServerManagerChatSessionMetadata
        {
            ServerManagerID = ServerManagerID,
            SessionCookie = SessionCookie,
            ChatProtocolVersion = ChatProtocolVersion
        };
    }
}
