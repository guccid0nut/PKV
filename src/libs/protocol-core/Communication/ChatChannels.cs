// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace ASPIRE.Common.Communication;

// ReSharper disable InconsistentNaming
// ReSharper disable UnusedMember.Global
public static class ChatChannels
{
    // "KONGOR" is a special channel name that maps to "KONGOR 1", then to "KONGOR 2" if "KONGOR 1" is full, then to "KONGOR 3" if "KONGOR 2" is full, and so on.
    public const string GeneralChannel = "KONGOR"; // Note(protocol): Dynamically mapped to numbered overflow channels ("KONGOR 1", "KONGOR 2", etc.) by the channel balancer.

    public const string GameMastersChannel = "GAME MASTERS";
    public const string GuestsChannel = "GUESTS";

    public const string ServerHostsChannel = "HOSTS"; // Note(protocol): Dedicated administrative channel reserved for querying and coordinating server host nodes.

    public const string StreamersChannel = "STREAMERS";
    public const string VIPChannel = "VIP";

    // "TERMINAL" is a special channel from which chat server commands can be executed.
    public const string StaffChannel = "TERMINAL"; // Note(protocol): Dedicated staff operations channel supporting terminal execution commands.

    public static readonly string[] AllDefaultChannels =
    [
        GeneralChannel, GameMastersChannel, GuestsChannel, ServerHostsChannel, StreamersChannel, VIPChannel,
        StaffChannel
    ];
}
