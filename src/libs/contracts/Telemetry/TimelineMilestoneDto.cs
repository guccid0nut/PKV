// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.Telemetry;

/// <summary>
///     Major match milestone event marker displayed along the post-match progression timeline.
/// </summary>
public sealed class TimelineMilestoneDto
{
    public required int GameTimeSeconds { get; set; }
    public required string FormattedTime { get; set; }
    public required string EventType { get; set; }
    public required int Team { get; set; }
    public required string Description { get; set; }
    public string? HeroIdentifier { get; set; }
    public string? HeroDisplayName { get; set; }
    public string? IconUrl { get; set; }
}
