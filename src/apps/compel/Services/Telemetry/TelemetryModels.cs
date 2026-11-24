// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace COMPEL.Services.Telemetry;

public sealed class LiveServerTickPayload
{
    public int MatchID { get; set; }
    public string? Map { get; set; }
    public string? GameMode { get; set; }
    public int ElapsedSeconds { get; set; }
    public int ScoreLegion { get; set; }
    public int ScoreHellbourne { get; set; }
    public int GoldAdvantageLegion { get; set; }
    public int GoldAdvantageHellbourne { get; set; }
    public List<LivePlayerTelemetry> Players { get; set; } = [];
    public List<LiveWardTelemetry> ActiveWards { get; set; } = [];
    public List<LiveCombatEventTelemetry> RecentEvents { get; set; } = [];
    public Dictionary<string, bool> Structures { get; set; } = [];

    public LiveServerTickPayload Clone()
    {
        LiveServerTickPayload clone = new()
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

        foreach (LivePlayerTelemetry p in Players) clone.Players.Add(p.Clone());

        foreach (LiveWardTelemetry w in ActiveWards) clone.ActiveWards.Add(w.Clone());

        foreach (LiveCombatEventTelemetry e in RecentEvents) clone.RecentEvents.Add(e.Clone());

        return clone;
    }

    public NEXUS.Contracts.Telemetry.LiveServerTickPayloadDto ToDto()
    {
        NEXUS.Contracts.Telemetry.LiveServerTickPayloadDto dto = new()
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

        foreach (LivePlayerTelemetry p in Players)
        {
            dto.Players.Add(new NEXUS.Contracts.Telemetry.LivePlayerTelemetryDto
            {
                Slot = p.Slot,
                AccountID = p.AccountID,
                AccountName = p.AccountName,
                Team = p.Team,
                HeroIdentifier = p.HeroIdentifier,
                Level = p.Level,
                PositionX = p.PositionX,
                PositionY = p.PositionY,
                HeadingAngle = p.HeadingAngle,
                HealthPercent = p.HealthPercent,
                ManaPercent = p.ManaPercent,
                IsAlive = p.IsAlive,
                RespawnTimer = p.RespawnTimer,
                CurrentGold = p.CurrentGold,
                Kills = p.Kills,
                Deaths = p.Deaths,
                Assists = p.Assists,
                GPM = p.GPM,
                XPM = p.XPM,
                LastCastAbility = p.LastCastAbility,
                LastCastTimeMs = p.LastCastTimeMs,
                TotalExperience = p.TotalExperience,
                AbilityLevels = new Dictionary<string, int>(p.AbilityLevels),
                Inventory = [.. p.Inventory]
            });
        }

        foreach (LiveWardTelemetry w in ActiveWards)
        {
            dto.ActiveWards.Add(new NEXUS.Contracts.Telemetry.LiveWardTelemetryDto
            {
                WardType = w.WardType,
                Team = w.Team,
                PositionX = w.PositionX,
                PositionY = w.PositionY,
                VisionRadius = w.VisionRadius,
                PlacerHeroName = w.PlacerHeroName,
                ExpiresInSeconds = w.ExpiresInSeconds
            });
        }

        foreach (LiveCombatEventTelemetry e in RecentEvents)
        {
            dto.RecentEvents.Add(new NEXUS.Contracts.Telemetry.LiveCombatEventTelemetryDto
            {
                EventType = e.EventType,
                AttackerName = e.AttackerName,
                AttackerHero = e.AttackerHero,
                VictimName = e.VictimName,
                VictimHero = e.VictimHero,
                MatchTimeSeconds = e.MatchTimeSeconds,
                Description = e.Description
            });
        }

        return dto;
    }
}

public sealed class LivePlayerTelemetry
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

    public LivePlayerTelemetry Clone()
    {
        return new LivePlayerTelemetry
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
}

public sealed class LiveWardTelemetry
{
    public string WardType { get; set; } = "Sight";
    public int Team { get; set; } = 1;
    public float PositionX { get; set; }
    public float PositionY { get; set; }
    public float VisionRadius { get; set; } = 0.085f;
    public string PlacerHeroName { get; set; } = string.Empty;
    public int ExpiresInSeconds { get; set; } = 360;

    public LiveWardTelemetry Clone()
    {
        return new LiveWardTelemetry
        {
            WardType = WardType,
            Team = Team,
            PositionX = PositionX,
            PositionY = PositionY,
            VisionRadius = VisionRadius,
            PlacerHeroName = PlacerHeroName,
            ExpiresInSeconds = ExpiresInSeconds
        };
    }
}

public sealed class LiveCombatEventTelemetry
{
    public string EventType { get; set; } = "KILL";
    public string AttackerName { get; set; } = string.Empty;
    public string AttackerHero { get; set; } = string.Empty;
    public string VictimName { get; set; } = string.Empty;
    public string VictimHero { get; set; } = string.Empty;
    public int MatchTimeSeconds { get; set; }
    public string Description { get; set; } = string.Empty;

    public LiveCombatEventTelemetry Clone()
    {
        return new LiveCombatEventTelemetry
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
}
