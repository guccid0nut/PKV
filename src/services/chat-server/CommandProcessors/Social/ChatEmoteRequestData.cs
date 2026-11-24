// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.Social;

public class ChatEmoteRequestData(ChatBuffer buffer)
{
    public byte[] CommandBytes { get; init; } = buffer.ReadCommandBytes();

    public string Message { get; } = buffer.ReadString();

    public int ChannelID { get; } = buffer.ReadInt32();
}
