// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace TRANSMUTANSTEIN.ChatServer.Domain.Matchmaking;

public class MatchmakingGroupMember
{
    public Account Account { get; init; }

    public ChatSession Session { get; init; }

    public MatchmakingGroupMember(ChatSession session)
    {
        Session = session;
        Account = session.Account;
    }

    public MatchmakingGroupMember(Account account)
    {
        Account = account;
        Session = null!;
    }

    public byte Slot { get; set; }

    public bool IsLeader { get; set; }

    public bool IsReady { get; set; }

    public bool IsInGame { get; set; }

    public bool IsEligibleForMatchmaking { get; set; }

    public byte LoadingPercent { get; set; }

    public double Rating { get; set; }

    public string Country { get; set; } = "NEWERTH";

    /// <summary>
    ///     Whether Or Not The Group Member Has Access To All Of The Group's Game Modes
    /// </summary>
    public bool HasGameModeAccess { get; set; } = true;

    /// <summary>
    ///     The Group Member's Game Mode Access, Delimited By "|" (e.g. "true|true|false")
    /// </summary>
    public string GameModeAccess { get; set; } = "true";
}

