// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.Channels;

public class SilenceChannelMemberRequestData(ChatBuffer buffer)
{
    public byte[] CommandBytes { get; init; } = buffer.ReadCommandBytes();

    public int ChannelId { get; } = buffer.ReadInt32();

    public string TargetName { get; } = buffer.ReadString();

    public int DurationMilliseconds { get; } = buffer.ReadInt32();
}
