// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.MatchState;

public class JoinedMatchRequestData(ChatBuffer buffer)
{
    // Don't Add The Player To The Match Chat Channel If They Have Joined As A Spectator Or A Mentor

    public byte[] CommandBytes { get; init; } = buffer.ReadCommandBytes();

    public string MatchName { get; init; } = buffer.ReadString();

    public int MatchID { get; } = buffer.ReadInt32();

    public bool JoinMatchChannel { get; init; } = buffer.ReadInt8() is not 0;
}
