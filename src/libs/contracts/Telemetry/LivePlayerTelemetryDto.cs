// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace NEXUS.Contracts.Telemetry;

/// <summary>
///     Real-time live player telemetry reported on every match tick.
/// </summary>
public sealed class LivePlayerTelemetryDto
{
    public int Slot { get; set; }
    public int AccountID { get; set; }
    public string AccountName { get; set; } = string.Empty;
    public int Team { get; set; } = 1;
    public string HeroIdentifier { get; set; } = string.Empty;
    public int Level { get; set; } = 1;
    public float PositionX { get; set; } = 0.5f;
    public float PositionY { get; set; } = 0.5f;
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
    public float TotalExperience { get; set; }
    public Dictionary<string, int> AbilityLevels { get; set; } = [];
    public List<string> Inventory { get; set; } = [];

    public LivePlayerTelemetryDto Clone()
    {
        return new LivePlayerTelemetryDto
        {
            Slot = Slot,
            AccountID = AccountID,
            AccountName = AccountName,
            Team = Team,
            HeroIdentifier = HeroIdentifier,
            Level = Level,
            PositionX = PositionX,
            PositionY = PositionY,
            HeadingAngle = HeadingAngle,
            HealthPercent = HealthPercent,
            ManaPercent = ManaPercent,
            IsAlive = IsAlive,
            RespawnTimer = RespawnTimer,
            CurrentGold = CurrentGold,
            Kills = Kills,
            Deaths = Deaths,
            Assists = Assists,
            GPM = GPM,
            XPM = XPM,
            LastCastAbility = LastCastAbility,
            LastCastTimeMs = LastCastTimeMs,
            TotalExperience = TotalExperience,
            AbilityLevels = new Dictionary<string, int>(AbilityLevels),
            Inventory = [.. Inventory]
        };
    }

    public static implicit operator LivePlayerTelemetryDto(LiveServerPlayerTickDto t)
    {
        return new LivePlayerTelemetryDto
        {
            Slot = t.Slot,
            AccountID = t.AccountID,
            AccountName = t.AccountName,
            Team = t.Team,
            HeroIdentifier = t.HeroIdentifier,
            Level = t.Level,
            PositionX = t.PositionX,
            PositionY = t.PositionY,
            HeadingAngle = t.HeadingAngle,
            HealthPercent = t.HealthPercent,
            ManaPercent = t.ManaPercent,
            IsAlive = t.IsAlive,
            RespawnTimer = t.RespawnTimer,
            CurrentGold = t.CurrentGold,
            Kills = t.Kills,
            Deaths = t.Deaths,
            Assists = t.Assists,
            GPM = t.GPM,
            XPM = t.XPM,
            LastCastAbility = t.LastCastAbility,
            LastCastTimeMs = t.LastCastTimeMs,
            AbilityLevels = new Dictionary<string, int>(t.AbilityLevels),
            Inventory = [.. t.Inventory]
        };
    }

    public static implicit operator LiveServerPlayerTickDto(LivePlayerTelemetryDto d)
    {
        return new LiveServerPlayerTickDto
        {
            Slot = d.Slot,
            AccountID = d.AccountID,
            AccountName = d.AccountName,
            Team = d.Team,
            HeroIdentifier = d.HeroIdentifier,
            Level = d.Level,
            PositionX = d.PositionX,
            PositionY = d.PositionY,
            HeadingAngle = d.HeadingAngle,
            HealthPercent = d.HealthPercent,
            ManaPercent = d.ManaPercent,
            IsAlive = d.IsAlive,
            RespawnTimer = d.RespawnTimer,
            CurrentGold = d.CurrentGold,
            Kills = d.Kills,
            Deaths = d.Deaths,
            Assists = d.Assists,
            GPM = d.GPM,
            XPM = d.XPM,
            LastCastAbility = d.LastCastAbility,
            LastCastTimeMs = d.LastCastTimeMs,
            AbilityLevels = new Dictionary<string, int>(d.AbilityLevels),
            Inventory = [.. d.Inventory]
        };
    }
}
