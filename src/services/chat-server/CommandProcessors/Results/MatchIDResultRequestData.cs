// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.Results;

public class MatchIdResultRequestData(ChatBuffer buffer)
{
    public byte[] CommandBytes { get; init; } = buffer.ReadCommandBytes();

    public ChatProtocol.MatchIDResult Result { get; } = (ChatProtocol.MatchIDResult)buffer.ReadInt8();

    public int RequestTimeMilliseconds { get; } = buffer.ReadInt32();
}
