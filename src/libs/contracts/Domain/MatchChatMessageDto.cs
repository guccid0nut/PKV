// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.Domain;

/// <summary>
///     Parsed chat message entry extracted from match replays or live chat stream.
/// </summary>
public sealed class MatchChatMessageDto
{
    public int TimestampMs { get; set; }

    public string FormattedTime { get; set; } = "00:00";

    public byte PlayerSlot { get; set; }

    public int? AccountID { get; set; }

    public string PlayerName { get; set; } = string.Empty;

    public byte Team { get; set; } = 1; // 1 = Legion, 2 = Hellbourne, 0 = Spec

    public string TeamName => Team switch
    {
        1 => "Legion",
        2 => "Hellbourne",
        _ => "Spectator"
    };

    public string Channel { get; set; } = "ALL"; // ALL, TEAM, WHISPER

    public string Message { get; set; } = string.Empty;

    public bool IsReportedPlayer { get; set; }
}
