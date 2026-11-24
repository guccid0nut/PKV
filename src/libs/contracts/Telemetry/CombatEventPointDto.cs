// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.Telemetry;

/// <summary>
///     Individual combat kill, ward, or objective coordinate point on the match minimap heatmap.
/// </summary>
public sealed class CombatEventPointDto
{
    public int Id { get; set; }
    public required int GameTimeSeconds { get; set; }
    public required string FormattedTime { get; set; }
    public required double NormalizedX { get; set; }
    public required double NormalizedY { get; set; }
    public required int KillerTeam { get; set; }
    public required string KillerAccount { get; set; }
    public required string KillerHero { get; set; }
    public required string VictimAccount { get; set; }
    public required string VictimHero { get; set; }
    public List<string> AssistHeroes { get; set; } = [];
    public required string EventType { get; set; }
    public required string Description { get; set; }
}
