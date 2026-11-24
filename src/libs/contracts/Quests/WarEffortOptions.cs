// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.Quests;

/// <summary>
///     Runtime configuration governing the War Effort quest &amp; reward cycle across MasterServer and ChatServer.
/// </summary>
public sealed class WarEffortOptions
{
    public const string SectionName = "WarEffort";

    /// <summary>
    ///     Master kill switch. When <c>false</c>, ChatServer disables the midbar button via
    ///     <c>CHAT_CMD_OPTIONS</c> and MasterServer returns the legacy disabled sentinel.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     Development/E2E testing override only. Must remain <c>false</c> in Production so bot matches,
    ///     Practice games, and Queue Arcade games cannot be farmed for War Effort points.
    /// </summary>
    public bool CountBotMatches { get; set; } = false;

    /// <summary>
    ///     When <c>true</c>, War Effort exclusive cosmetics (Contender/Winner account icons,
    ///     Dragon's Treasure Ward, Gnome Courier) remain earnable through War Effort progression
    ///     even when <c>Store:UnlockAllItems</c> is enabled.
    /// </summary>
    public bool ExcludeExclusiveCosmeticsFromUnlockAll { get; set; } = true;

    /// <summary>
    ///     Length of each War Effort cycle in UTC days (default: 28 days).
    /// </summary>
    public int CycleLengthDays { get; set; } = 28;

    /// <summary>
    ///     Maximum banked quest rerolls per account (daily +1 granted at 00:00 UTC).
    /// </summary>
    public int DailyRerollCap { get; set; } = 2;

    /// <summary>
    ///     Minimum number of human (non-bot) players required in a match for War Effort progression.
    /// </summary>
    public int MinimumHumanPlayers { get; set; } = 6;

    /// <summary>
    ///     Minimum match duration in seconds required for War Effort progression (default: 10 minutes).
    /// </summary>
    public int MinimumMatchDurationSeconds { get; set; } = 600;
}
