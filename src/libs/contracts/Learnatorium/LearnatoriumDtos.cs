// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: LearnatoriumDtos.cs
// System: NEXUS Contracts - Learnatorium Replay Playback & Guide DTOs
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

namespace NEXUS.Contracts.Learnatorium;

/// <summary>
///     Comprehensive dynamic hero build guide and leveling progression for the Learnatorium.
/// </summary>
public sealed class LearnatoriumHeroGuideDto
{
    public required string HeroIdentifier { get; set; }
    public required string DisplayName { get; set; }
    public string Role { get; set; } = "Carry / Ganker";
    public int Difficulty { get; set; } = 3;
    public List<string> SkillBuildOrder { get; set; } = [];
    public List<string> StartingItems { get; set; } = [];
    public List<string> CoreItems { get; set; } = [];
    public List<string> SituationalItems { get; set; } = [];
    public List<string> Tips { get; set; } = [];
    public double WinRate { get; set; } = 54.2;
    public double PickRate { get; set; } = 12.8;
}

/// <summary>
///     Position and combat status of a single hero unit at a given timeline frame.
/// </summary>
public sealed class PlaybackHeroPositionDto
{
    public int Slot { get; set; }
    public int AccountID { get; set; }
    public required string AccountName { get; set; }
    public required string HeroIdentifier { get; set; }
    public required string HeroDisplayName { get; set; }
    public int Team { get; set; } = 1;
    public float PositionX { get; set; } = 0.5f;
    public float PositionY { get; set; } = 0.5f;
    public float HeadingAngle { get; set; }
    public float HealthPercent { get; set; } = 100f;
    public float ManaPercent { get; set; } = 100f;
    public bool IsAlive { get; set; } = true;
    public int Level { get; set; } = 1;
    public int Kills { get; set; }
    public int Deaths { get; set; }
    public int Assists { get; set; }
    public int Gold { get; set; }
    public int Gpm { get; set; }
    public int Xpm { get; set; }
    public List<string> Items { get; set; } = [];
}

/// <summary>
///     Combat or objective highlight along the match timeline.
/// </summary>
public sealed class PlaybackCombatHighlightDto
{
    public int MatchTimeSeconds { get; set; }
    public required string EventType { get; set; }
    public required string Description { get; set; }
    public string AttackerHero { get; set; } = string.Empty;
    public string VictimHero { get; set; } = string.Empty;
    public float PositionX { get; set; } = 0.5f;
    public float PositionY { get; set; } = 0.5f;
}

/// <summary>
///     Single sampled frame of 2D minimap coordinates for all 10 players and objectives.
/// </summary>
public sealed class PlaybackFrameDto
{
    public int Second { get; set; }
    public required string ElapsedFormatted { get; set; }
    public List<PlaybackHeroPositionDto> Heroes { get; set; } = [];
    public Dictionary<string, bool> Structures { get; set; } = [];
    public int ScoreLegion { get; set; }
    public int ScoreHellbourne { get; set; }
    public int GoldLeadLegion { get; set; }
}

/// <summary>
///     Complete 2D browser-native match replay playback payload for the Learnatorium minimap viewer.
/// </summary>
public sealed class LearnatoriumReplayPlaybackDto
{
    public int MatchID { get; set; }
    public string Map { get; set; } = "caldavar";
    public string GameMode { get; set; } = "Ranked (All Pick)";
    public int TotalDurationSeconds { get; set; } = 1411;
    public int WinnerTeam { get; set; } = 1;
    public int ScoreLegion { get; set; }
    public int ScoreHellbourne { get; set; }
    public List<PlaybackHeroPositionDto> Players { get; set; } = [];
    public List<PlaybackFrameDto> Frames { get; set; } = [];
    public List<PlaybackCombatHighlightDto> Highlights { get; set; } = [];
}
