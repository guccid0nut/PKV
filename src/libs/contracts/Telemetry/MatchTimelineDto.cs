// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.Telemetry;

/// <summary>
///     End-of-match economy, experience, and milestone timeline DTO.
/// </summary>
public sealed class MatchTimelineDto
{
    public required int MatchId { get; set; }
    public required int DurationSeconds { get; set; }
    public List<int> MinuteTimestamps { get; set; } = [];
    public List<int> LegionGoldAdvantage { get; set; } = [];
    public List<int> LegionXpAdvantage { get; set; } = [];
    public List<int> LegionCumulativeGold { get; set; } = [];
    public List<int> HellbourneCumulativeGold { get; set; } = [];
    public List<int> LegionCumulativeXp { get; set; } = [];
    public List<int> HellbourneCumulativeXp { get; set; } = [];
    public List<TimelineMilestoneDto> Milestones { get; set; } = [];
}
