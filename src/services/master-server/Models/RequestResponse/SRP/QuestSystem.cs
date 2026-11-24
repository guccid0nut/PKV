// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

namespace KONGOR.MasterServer.Models.RequestResponse.SRP;

public partial class QuestSystem
{
    /// <summary>
    ///     Unknown.
    /// </summary>
    [PHPProperty("quest_status")]
    public int QuestStatus { get; set; } = 0;

    /// <summary>
    ///     Unknown.
    /// </summary>
    [PHPProperty("leaderboard_status")]
    public int LeaderboardStatus { get; set; } = 0;
}
