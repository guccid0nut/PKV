// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.Telemetry;

/// <summary>
///     Spatial combat density and heatmap DTO containing kill events, ward placements, and objective takes.
/// </summary>
public sealed class CombatHeatmapDto
{
    public required int MatchId { get; set; }
    public required string MapName { get; set; }
    public required string MapIconPath { get; set; }
    public List<CombatEventPointDto> KillEvents { get; set; } = [];
    public List<CombatEventPointDto> WardEvents { get; set; } = [];
    public List<CombatEventPointDto> ObjectiveEvents { get; set; } = [];
}
