// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.Connection;

public class ServerManagerStatusRequestData(ChatBuffer buffer)
{
    public byte[] CommandBytes { get; init; } = buffer.ReadCommandBytes();

    public int ServerManagerId { get; } = buffer.ReadInt32();

    public string ServerLogin { get; init; } = buffer.ReadString();

    public string Location { get; } = buffer.ReadString();

    public string Name { get; } = buffer.ReadString();

    public string Version { get; } = buffer.ReadString();

    public string Address { get; } = buffer.ReadString();

    public short Port { get; } = buffer.ReadInt16();

    public bool ShuttingDown { get; } = buffer.ReadInt8() is not 0;
}
