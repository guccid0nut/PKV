// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.Telemetry;

/// <summary>
///     Full server tick telemetry payload containing active players, wards, combat events, and structure states.
/// </summary>
public sealed class LiveServerTickPayloadDto
{
    public int MatchID { get; set; }
    public string? Map { get; set; }
    public string? GameMode { get; set; }
    public int ElapsedSeconds { get; set; }
    public int ScoreLegion { get; set; }
    public int ScoreHellbourne { get; set; }
    public int GoldAdvantageLegion { get; set; }
    public int GoldAdvantageHellbourne { get; set; }
    public List<LivePlayerTelemetryDto> Players { get; set; } = [];
    public List<LiveWardTelemetryDto> ActiveWards { get; set; } = [];
    public List<LiveCombatEventTelemetryDto> RecentEvents { get; set; } = [];
    public Dictionary<string, bool> Structures { get; set; } = [];

    public LiveServerTickPayloadDto Clone()
    {
        LiveServerTickPayloadDto clone = new()
        {
            MatchID = MatchID,
            Map = Map,
            GameMode = GameMode,
            ElapsedSeconds = ElapsedSeconds,
            ScoreLegion = ScoreLegion,
            ScoreHellbourne = ScoreHellbourne,
            GoldAdvantageLegion = GoldAdvantageLegion,
            GoldAdvantageHellbourne = GoldAdvantageHellbourne,
            Structures = new Dictionary<string, bool>(Structures)
        };

        foreach (LivePlayerTelemetryDto p in Players) clone.Players.Add(p.Clone());

        foreach (LiveWardTelemetryDto w in ActiveWards) clone.ActiveWards.Add(w.Clone());

        foreach (LiveCombatEventTelemetryDto e in RecentEvents) clone.RecentEvents.Add(e.Clone());

        return clone;
    }
}
