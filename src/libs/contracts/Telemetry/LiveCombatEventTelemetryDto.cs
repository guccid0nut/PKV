// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.Telemetry;

/// <summary>
///     Combat event telemetry (kill, assist, tower destroy, objective kill) emitted during live match play.
/// </summary>
public sealed class LiveCombatEventTelemetryDto
{
    public string EventType { get; set; } = "KILL";
    public string AttackerName { get; set; } = string.Empty;
    public string AttackerHero { get; set; } = string.Empty;
    public string VictimName { get; set; } = string.Empty;
    public string VictimHero { get; set; } = string.Empty;
    public int MatchTimeSeconds { get; set; }
    public string Description { get; set; } = string.Empty;

    public LiveCombatEventTelemetryDto Clone()
    {
        return new LiveCombatEventTelemetryDto
        {
            EventType = EventType,
            AttackerName = AttackerName,
            AttackerHero = AttackerHero,
            VictimName = VictimName,
            VictimHero = VictimHero,
            MatchTimeSeconds = MatchTimeSeconds,
            Description = Description
        };
    }

    public static implicit operator LiveCombatEventTelemetryDto(LiveCombatEvent e)
    {
        return new LiveCombatEventTelemetryDto
        {
            EventType = e.EventType,
            AttackerName = e.AttackerName,
            AttackerHero = e.AttackerHero,
            VictimName = e.VictimName,
            VictimHero = e.VictimHero,
            MatchTimeSeconds = e.MatchTimeSeconds,
            Description = e.Description
        };
    }

    public static implicit operator LiveCombatEvent(LiveCombatEventTelemetryDto d)
    {
        return new LiveCombatEvent
        {
            EventType = d.EventType,
            AttackerName = d.AttackerName,
            AttackerHero = d.AttackerHero,
            VictimName = d.VictimName,
            VictimHero = d.VictimHero,
            MatchTimeSeconds = d.MatchTimeSeconds,
            Description = d.Description
        };
    }
}
