// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.Social;

public class RemoveFriendRequestData(ChatBuffer buffer)
{
    public byte[] CommandBytes { get; init; } = buffer.ReadCommandBytes();

    public int RemovedFriendAccountID { get; init; } = buffer.ReadInt32();

    /// <summary>
    ///     The ID of the notification for removing another player from the client's friend list.
    ///     Used for managing notifications while the client is offline; to be received on next login.
    /// </summary>
    public int RequesterNotificationID { get; init; } = buffer.ReadInt32();

    /// <summary>
    ///     The ID of the notification for being removed from another player's friend list.
    ///     Used for managing notifications while the client is offline; to be received on next login.
    /// </summary>
    public int RemovedFriendNotificationID { get; init; } = buffer.ReadInt32();
}
