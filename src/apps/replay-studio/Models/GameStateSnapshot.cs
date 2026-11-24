// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: GameStateSnapshot.cs
// System: Replay Studio Desktop GUI Tool
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

namespace ReplayStudio.GUI.Models;

/// <summary>
///     Complete snapshot of the match simulation state at a specific millisecond timestamp.
/// </summary>
public sealed class GameStateSnapshot
{
    public int TimestampMs { get; set; }
    public int MatchTimeSeconds => TimestampMs / 1000;
    public string FormattedTime => $"{TimestampMs / 60000:D2}:{(TimestampMs % 60000) / 1000:D2}";
    public int ScoreLegion { get; set; }
    public int ScoreHellbourne { get; set; }
    public int NetWorthAdvantage { get; set; } // Legion - Hellbourne
    public HeroSnapshot[] Heroes { get; set; } = new HeroSnapshot[10];
    public List<CombatEventMarker> ActiveEvents { get; set; } = [];
    public bool IsKeyframe { get; set; }
    public string KeyframeTag { get; set; } = string.Empty;
    public bool IsEstimatedFallback { get; set; }

    public GameStateSnapshot()
    {
        for (int i = 0; i < 10; i++)
        {
            Heroes[i] = new HeroSnapshot
            {
                ClientIndex = i,
                Team = i < 5 ? 1 : 2
            };
        }
    }

    public GameStateSnapshot Clone()
    {
        GameStateSnapshot copy = new()
        {
            TimestampMs = TimestampMs,
            ScoreLegion = ScoreLegion,
            ScoreHellbourne = ScoreHellbourne,
            NetWorthAdvantage = NetWorthAdvantage,
            IsKeyframe = IsKeyframe,
            KeyframeTag = KeyframeTag,
            IsEstimatedFallback = IsEstimatedFallback,
            ActiveEvents = new List<CombatEventMarker>(ActiveEvents.Count),
            Heroes = new HeroSnapshot[10]
        };

        for (int i = 0; i < ActiveEvents.Count; i++)
        {
            copy.ActiveEvents.Add(ActiveEvents[i].Clone());
        }

        for (int i = 0; i < 10; i++)
        {
            copy.Heroes[i] = Heroes[i]?.Clone() ?? new HeroSnapshot { ClientIndex = i, Team = i < 5 ? 1 : 2 };
        }

        return copy;
    }
}
