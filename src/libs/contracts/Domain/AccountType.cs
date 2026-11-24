// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace NEXUS.Contracts.Domain;

public enum AccountType
{
    // built-in types
    Disabled = 0, // prevented from logging into HoN
    Trial = 1, // has limited access to in-game functions
    ServerHost = 2, // has permissions to host game servers
    Normal = 3, // free-to-play account
    Legacy = 4, // purchased HoN during the beta
    Staff = 5, // has access to admin functions and can execute admin commands
    GameMaster = 6, // can suspend players
    MatchModerator = 7, // can spectate and moderate matches
    MatchCaster = 8, // can spectate matches

    // custom types
    Guest = 100, // built-in accounts used for quick play
    Streamer = 101, // has top priority in the matchmaking queue
    VIP = 102 // has top priority in the matchmaking queue
}
