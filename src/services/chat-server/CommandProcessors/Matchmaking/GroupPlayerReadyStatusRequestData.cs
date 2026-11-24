// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.Matchmaking;

public class GroupPlayerReadyStatusRequestData(ChatBuffer buffer)
{
    public byte[] CommandBytes { get; init; } = buffer.ReadCommandBytes();

    public byte ReadyStatus { get; init; } = buffer.ReadInt8();

    public ChatProtocol.TMMGameType GameType { get; } = (ChatProtocol.TMMGameType)buffer.ReadInt8();
}
