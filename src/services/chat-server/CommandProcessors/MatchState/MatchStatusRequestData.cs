// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.MatchState;

public class MatchStatusRequestData(ChatBuffer buffer)
{
    public byte[] CommandBytes { get; init; } = buffer.ReadCommandBytes();

    public int ServerId { get; init; } = buffer.ReadInt32();

    public int MatchId { get; init; } = buffer.ReadInt32();

    public int Phase { get; init; } = buffer.ReadInt32();

    public int CurrentGameTime { get; init; } = buffer.ReadInt32();
}
