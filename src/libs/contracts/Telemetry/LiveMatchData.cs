// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using NEXUS.Contracts.Replay;

namespace NEXUS.Contracts.Telemetry;

/// <summary>
///     Live match telemetry snapshot aggregating players, wards, gold timeline, and objective states.
/// </summary>
public class LiveMatchData
{
    public int MatchID { get; set; }
    public long TickId { get; set; }
    public string ServerName { get; set; } = "US-East #1";
    public string Map { get; set; } = "caldavar";
    public string GameMode { get; set; } = "Ranked (All Pick)";
    public int ElapsedSeconds { get; set; }
    public int ScoreLegion { get; set; }
    public int ScoreHellbourne { get; set; }
    public int GoldAdvantageLegion { get; set; }
    public int GoldAdvantageHellbourne { get; set; }
    public List<LivePlayerState> Players { get; set; } = [];
    public List<LiveCombatEvent> RecentEvents { get; set; } = [];
    public Dictionary<string, bool> Structures { get; set; } = [];
    public List<LiveWardState> ActiveWards { get; set; } = [];
    public List<LiveGoldTimelinePoint> GoldTimeline { get; set; } = [];
    public LiveObjectiveState Objectives { get; set; } = new();
    public DateTimeOffset LastUpdatedUtc { get; set; } = TimeProvider.System.GetUtcNow();

    public string FormattedTime
    {
        get
        {
            TimeSpan span = TimeSpan.FromSeconds(ElapsedSeconds);
            return span.Hours > 0 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"mm\:ss");
        }
    }
}

public class LiveWardState
{
    public string WardId { get; set; } = Guid.NewGuid().ToString("N");
    public int Team { get; set; } // 1 = Legion, 2 = Hellbourne
    public string WardType { get; set; } = "Sight"; // "Sight", "Revelation"
    public float PositionX { get; set; } // Normalized 0.0 to 1.0
    public float PositionY { get; set; } // Normalized 0.0 to 1.0
    public float VisionRadius { get; set; } = 0.10f; // Normalized radius on canvas
    public int PlacedAtSecond { get; set; }
    public int DurationSeconds { get; set; } = 360; // 6-minute ward lifetime
    public int RemainingSeconds => Math.Max(0, DurationSeconds - (CurrentElapsedSeconds - PlacedAtSecond));
    public int CurrentElapsedSeconds { get; set; }
    public string PlacerHeroName { get; set; } = string.Empty;
}

public class LiveGoldTimelinePoint
{
    public int Minute { get; set; }
    public int Second { get; set; }
    public int LegionTotalGold { get; set; }
    public int HellbourneTotalGold { get; set; }
    public int LegionNetWorth { get; set; }
    public int HellbourneNetWorth { get; set; }
    public int GoldAdvantage => LegionNetWorth - HellbourneNetWorth; // Positive = Legion, Negative = Hellbourne
    public int LegionExperience { get; set; }
    public int HellbourneExperience { get; set; }
    public int ExperienceAdvantage => LegionExperience - HellbourneExperience;
}

public class LiveObjectiveState
{
    public bool KongorAlive { get; set; } = true;
    public int KongorRespawnSecondsRemaining { get; set; }
    public string TopRuneType { get; set; } = "Haste"; // "Double Damage", "Haste", "Invisibility", "Regeneration"
    public string BotRuneType { get; set; } = "Double Damage";
}

public class LiveMatchDeltaDto
{
    public int MatchID { get; set; }
    public long TickId { get; set; }
    public int ElapsedSeconds { get; set; }
    public int ScoreLegion { get; set; }
    public int ScoreHellbourne { get; set; }
    public int GoldAdvantageLegion { get; set; }
    public int GoldAdvantageHellbourne { get; set; }
    public List<LivePlayerDeltaState> PlayerDeltas { get; set; } = [];
    public List<LiveCombatEvent> NewEvents { get; set; } = [];
    public List<LiveWardState> AddedWards { get; set; } = [];
    public List<string> ExpiredWardIds { get; set; } = [];
    public LiveGoldTimelinePoint? NewGoldPoint { get; set; }
    public Dictionary<string, bool>? ModifiedStructures { get; set; }
}

public class LivePlayerDeltaState
{
    public int AccountID { get; set; }
    public int Team { get; set; }
    public float PositionX { get; set; }
    public float PositionY { get; set; }
    public float HeadingAngle { get; set; }
    public int HealthPercent { get; set; }
    public int ManaPercent { get; set; }
    public bool IsAlive { get; set; }
    public int RespawnTimer { get; set; }
    public int CurrentGold { get; set; }
    public int Level { get; set; }
    public int Kills { get; set; }
    public int Deaths { get; set; }
    public int Assists { get; set; }
    public int GPM { get; set; }
    public int XPM { get; set; }
    public string? LastCastAbility { get; set; }
    public List<string>? Inventory { get; set; }
}

public class LivePlayerState
{
    public int AccountID { get; set; }
    public string AccountName { get; set; } = string.Empty;
    public int Team { get; set; } // 1 = Legion, 2 = Hellbourne
    public string HeroIdentifier { get; set; } = string.Empty;
    public string HeroDisplayName { get; set; } = string.Empty;
    public int Level { get; set; } = 1;
    public int Kills { get; set; }
    public int Deaths { get; set; }
    public int Assists { get; set; }
    public int GPM { get; set; }
    public int XPM { get; set; }
    public int CurrentGold { get; set; }
    public List<string> Inventory { get; set; } = [];
    public List<ReplayPlayerAbilityDto> Abilities { get; set; } = [];
    public float PositionX { get; set; } // Normalized 0.0 to 1.0
    public float PositionY { get; set; } // Normalized 0.0 to 1.0
    public float HeadingAngle { get; set; }
    public string? LastCastAbility { get; set; }
    public int HealthPercent { get; set; } = 100;
    public int ManaPercent { get; set; } = 100;
    public bool IsAlive { get; set; } = true;
    public int RespawnTimer { get; set; }
}

public class LiveCombatEvent
{
    public string EventType { get; set; } = "KILL"; // KILL, TOWER, BOSSKILL, MULTIKILL
    public string AttackerName { get; set; } = string.Empty;
    public string AttackerHero { get; set; } = string.Empty;
    public string VictimName { get; set; } = string.Empty;
    public string VictimHero { get; set; } = string.Empty;
    public int MatchTimeSeconds { get; set; }
    public string Description { get; set; } = string.Empty;
    public float PositionX { get; set; } = 0.5f;
    public float PositionY { get; set; } = 0.5f;
    public int Team { get; set; }
    public string AbilityOrItem { get; set; } = string.Empty;
}

internal static class CoordinateNormalizationHelper
{
    private const float MapMin = -8000f;
    private const float MapMax = 8000f;
    private const float MapSpan = MapMax - MapMin;

    public static (float X, float Y) NormalizeCoordinates(float k2X, float k2Y)
    {
        float normX = Math.Clamp((k2X - MapMin) / MapSpan, 0.0f, 1.0f);
        float normY = Math.Clamp(1.0f - (k2Y - MapMin) / MapSpan, 0.0f, 1.0f);
        return (normX, normY);
    }
}

public class LiveTelemetryReportDto
{
    public int MatchID { get; set; }
    public int AccountID { get; set; } = 1;
    public string? AccountName { get; set; }
    public int Team { get; set; } = 1;
    public string? HeroIdentifier { get; set; }
    public float? PositionX { get; set; }
    public float? PositionY { get; set; }
    public float? HeadingAngle { get; set; }
    public int? HealthPercent { get; set; }
    public int? ManaPercent { get; set; }
    public int? Level { get; set; }
    public int? CurrentGold { get; set; }
    public int? Kills { get; set; }
    public int? Deaths { get; set; }
    public int? Assists { get; set; }
    public int? GPM { get; set; }
    public int? XPM { get; set; }
    public List<string>? Inventory { get; set; }
    public List<LiveWardState>? Wards { get; set; }
    public string? CombatEventDescription { get; set; }
}

public class LiveServerPlayerTickDto
{
    public int Slot { get; set; }
    public int AccountID { get; set; }
    public string AccountName { get; set; } = string.Empty;
    public int Team { get; set; } = 1;
    public string HeroIdentifier { get; set; } = string.Empty;
    public int Level { get; set; } = 1;
    public float PositionX { get; set; }
    public float PositionY { get; set; }
    public float HeadingAngle { get; set; }
    public int HealthPercent { get; set; } = 100;
    public int ManaPercent { get; set; } = 100;
    public bool IsAlive { get; set; } = true;
    public int RespawnTimer { get; set; }
    public int CurrentGold { get; set; }
    public int Kills { get; set; }
    public int Deaths { get; set; }
    public int Assists { get; set; }
    public int GPM { get; set; }
    public int XPM { get; set; }
    public string? LastCastAbility { get; set; }
    public int LastCastTimeMs { get; set; }
    public Dictionary<string, int> AbilityLevels { get; set; } = [];
    public List<string> Inventory { get; set; } = [];
}
