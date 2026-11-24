// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.MatchState;

public class UploadRequestData(ChatBuffer buffer)
{
    public byte[] CommandBytes { get; } = buffer.ReadCommandBytes();
    public int MatchID { get; } = buffer.ReadInt32();
    public string TargetType { get; } = buffer.ReadString();
}
