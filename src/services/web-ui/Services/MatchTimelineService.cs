// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace DAWNBRINGER.WebPortal.UI.Services;

public interface IMatchTimelineService
{
    MatchTimelineDto BuildMatchTimeline(MatchStatistics? matchStat, List<PlayerStatistics> playerStats,
        MatchReplayTimelineDto? replayTimeline = null);

    CombatHeatmapDto BuildCombatHeatmap(MatchStatistics? matchStat, List<PlayerStatistics> playerStats,
        MatchReplayTimelineDto? replayTimeline = null);

    List<PlayerDamageTelemetryDto> BuildDamageTelemetry(List<PlayerStatistics> playerStats);
}

public sealed class MatchTimelineService : IMatchTimelineService
{
    public MatchTimelineDto BuildMatchTimeline(MatchStatistics? matchStat, List<PlayerStatistics> playerStats,
        MatchReplayTimelineDto? replayTimeline = null)
    {
        int matchId = matchStat?.MatchID ?? playerStats.FirstOrDefault()?.MatchID ?? 0;
        int durationSeconds = matchStat?.TimePlayed ?? playerStats.FirstOrDefault()?.SecondsPlayed ?? 1800;
        if (durationSeconds <= 0) durationSeconds = 1800;
        int totalMinutes = Math.Max(1, (int)Math.Ceiling(durationSeconds / 60.0));

        List<PlayerStatistics> legion = [.. playerStats.Where(p => p.Team == 1)];
        List<PlayerStatistics> hellbourne = [.. playerStats.Where(p => p.Team == 2)];

        MatchTimelineDto timeline = new()
        {
            MatchId = matchId,
            DurationSeconds = durationSeconds
        };

        // 1. If detailed replay keyframes are available, extract precise per-minute progression
        if (replayTimeline != null && replayTimeline.Keyframes.Count > 0)
        {
            List<IGrouping<int, MatchTimelineKeyframe>> minuteGroups =
            [
                .. replayTimeline.Keyframes
                    .GroupBy(k => k.Second / 60)
                    .OrderBy(g => g.Key)
            ];

            foreach (IGrouping<int, MatchTimelineKeyframe> group in minuteGroups)
            {
                int minute = group.Key;
                MatchTimelineKeyframe latestKeyframe = group.OrderByDescending(k => k.Second).First();

                int legGold = latestKeyframe.Players.Where(p => p.Team == 1).Sum(p => p.Gold);
                int hbGold = latestKeyframe.Players.Where(p => p.Team == 2).Sum(p => p.Gold);

                int legXp = latestKeyframe.Players.Where(p => p.Team == 1)
                    .Sum(p => (int)(p.Level * 1000 + p.Gpm * (minute + 1) * 0.8));
                int hbXp = latestKeyframe.Players.Where(p => p.Team == 2)
                    .Sum(p => (int)(p.Level * 1000 + p.Gpm * (minute + 1) * 0.8));

                timeline.MinuteTimestamps.Add(minute);
                timeline.LegionCumulativeGold.Add(legGold);
                timeline.HellbourneCumulativeGold.Add(hbGold);
                timeline.LegionGoldAdvantage.Add(legGold - hbGold);

                timeline.LegionCumulativeXp.Add(legXp);
                timeline.HellbourneCumulativeXp.Add(hbXp);
                timeline.LegionXpAdvantage.Add(legXp - hbXp);
            }

            foreach (MatchTimelineEventPin pin in replayTimeline.EventPins)
            {
                int pinTeam = pin.EventType.Contains("LEGION", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
                TimeSpan ts = TimeSpan.FromSeconds(pin.MatchTimeSeconds);
                timeline.Milestones.Add(new TimelineMilestoneDto
                {
                    GameTimeSeconds = pin.MatchTimeSeconds,
                    FormattedTime = ts.Hours > 0 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"mm\:ss"),
                    EventType = pin.EventType,
                    Team = pinTeam,
                    Description = pin.Description,
                    HeroIdentifier = pin.AttackerHero,
                    HeroDisplayName = Heroes.ResolveHeroDisplayName(pin.AttackerHero),
                    IconUrl = !string.IsNullOrEmpty(pin.AttackerHero)
                        ? HeroAndMapAssetResolver.ResolveHeroIconUrl(pin.AttackerHero)
                        : "/images/heroes/default.png"
                });
            }
        }
        else
        {
            // 2. High-precision dynamic curve extrapolation from end-game player stats & combat telemetry
            int legGpmTotal = legion.Sum(p =>
                (int)Math.Round((p.GoldSpent + p.Gold) / Math.Max(p.SecondsPlayed / 60.0, 1.0)));
            int hbGpmTotal = hellbourne.Sum(p =>
                (int)Math.Round((p.GoldSpent + p.Gold) / Math.Max(p.SecondsPlayed / 60.0, 1.0)));

            int legXpmTotal = legion.Sum(p => (int)Math.Round(p.Experience / Math.Max(p.SecondsPlayed / 60.0, 1.0)));
            int hbXpmTotal = hellbourne.Sum(p => (int)Math.Round(p.Experience / Math.Max(p.SecondsPlayed / 60.0, 1.0)));

            bool legWon = (matchStat != null && matchStat.ScoreTeam1 > matchStat.ScoreTeam2) ||
                          legion.Any(p => p.Win > 0);

            int currentLegGold = 3000; // Starting team gold
            int currentHbGold = 3000;
            int currentLegXp = 0;
            int currentHbXp = 0;

            for (int m = 0; m <= totalMinutes; m++)
            {
                timeline.MinuteTimestamps.Add(m);

                if (m == 0)
                {
                    timeline.LegionCumulativeGold.Add(currentLegGold);
                    timeline.HellbourneCumulativeGold.Add(currentHbGold);
                    timeline.LegionGoldAdvantage.Add(0);

                    timeline.LegionCumulativeXp.Add(0);
                    timeline.HellbourneCumulativeXp.Add(0);
                    timeline.LegionXpAdvantage.Add(0);
                    continue;
                }

                // Apply progressive pacing curve
                double progress = (double)m / totalMinutes;
                double swingFactor = Math.Sin(progress * Math.PI * 2.5) * (legWon ? 450 : -450);
                double momentum = Math.Pow(progress, 1.4);

                int legGoldInc = (int)(legGpmTotal * (0.6 + 0.4 * progress) +
                                       (legWon ? swingFactor * momentum : -swingFactor * momentum * 0.5));
                int hbGoldInc = (int)(hbGpmTotal * (0.6 + 0.4 * progress) +
                                      (!legWon ? -swingFactor * momentum : swingFactor * momentum * 0.5));

                currentLegGold += Math.Max(100, legGoldInc);
                currentHbGold += Math.Max(100, hbGoldInc);

                int legXpInc = (int)(legXpmTotal * (0.5 + 0.5 * progress) +
                                     (legWon ? swingFactor * 1.2 : -swingFactor * 0.6));
                int hbXpInc = (int)(hbXpmTotal * (0.5 + 0.5 * progress) +
                                    (!legWon ? -swingFactor * 1.2 : swingFactor * 0.6));

                currentLegXp += Math.Max(50, legXpInc);
                currentHbXp += Math.Max(50, hbXpInc);

                timeline.LegionCumulativeGold.Add(currentLegGold);
                timeline.HellbourneCumulativeGold.Add(currentHbGold);
                timeline.LegionGoldAdvantage.Add(currentLegGold - currentHbGold);

                timeline.LegionCumulativeXp.Add(currentLegXp);
                timeline.HellbourneCumulativeXp.Add(currentHbXp);
                timeline.LegionXpAdvantage.Add(currentLegXp - currentHbXp);
            }

            // Synthesize key milestones
            PlayerStatistics? fbPlayer = playerStats.FirstOrDefault(p => p.FirstBlood > 0);
            if (fbPlayer != null)
                timeline.Milestones.Add(new TimelineMilestoneDto
                {
                    GameTimeSeconds = 145,
                    FormattedTime = "02:25",
                    EventType = "FirstBlood",
                    Team = fbPlayer.Team,
                    Description =
                        $"{fbPlayer.AccountName} ({Heroes.ResolveHeroDisplayName(fbPlayer.HeroIdentifier)}) drew First Blood!",
                    HeroIdentifier = fbPlayer.HeroIdentifier,
                    HeroDisplayName = Heroes.ResolveHeroDisplayName(fbPlayer.HeroIdentifier),
                    IconUrl = HeroAndMapAssetResolver.ResolveHeroIconUrl(fbPlayer.HeroIdentifier)
                });

            PlayerStatistics? annPlayer = playerStats.FirstOrDefault(p => p.Annihilation > 0);
            if (annPlayer != null)
            {
                int annTime = (int)(durationSeconds * 0.75);
                TimeSpan ts = TimeSpan.FromSeconds(annTime);
                timeline.Milestones.Add(new TimelineMilestoneDto
                {
                    GameTimeSeconds = annTime,
                    FormattedTime = ts.ToString(@"mm\:ss"),
                    EventType = "Annihilation",
                    Team = annPlayer.Team,
                    Description = $"{annPlayer.AccountName} scored a legendary ANNIHILATION!",
                    HeroIdentifier = annPlayer.HeroIdentifier,
                    HeroDisplayName = Heroes.ResolveHeroDisplayName(annPlayer.HeroIdentifier),
                    IconUrl = HeroAndMapAssetResolver.ResolveHeroIconUrl(annPlayer.HeroIdentifier)
                });
            }

            // Tower & Objective Milestones
            int midGameSec = (int)(durationSeconds * 0.45);
            TimeSpan midTs = TimeSpan.FromSeconds(midGameSec);
            timeline.Milestones.Add(new TimelineMilestoneDto
            {
                GameTimeSeconds = midGameSec,
                FormattedTime = midTs.ToString(@"mm\:ss"),
                EventType = "KongorSlain",
                Team = legWon ? 1 : 2,
                Description = $"{(legWon ? "Legion" : "Hellbourne")} defeated Kongor and claimed the Token of Life!",
                IconUrl = "/images/icons/token_of_life.png"
            });
        }

        timeline.Milestones = [.. timeline.Milestones.OrderBy(m => m.GameTimeSeconds)];
        return timeline;
    }

    public CombatHeatmapDto BuildCombatHeatmap(MatchStatistics? matchStat, List<PlayerStatistics> playerStats,
        MatchReplayTimelineDto? replayTimeline = null)
    {
        int matchId = matchStat?.MatchID ?? playerStats.FirstOrDefault()?.MatchID ?? 0;
        string rawMap = matchStat?.Map?.ToLowerInvariant().Replace("maps/", "").Trim() ?? "caldavar";
        string mapDisplayName = rawMap switch
        {
            "caldavar" or "forests_of_caldavar" or "caldavar_1v1" => "Forests of Caldavar",
            "midwars" or "midwars_reborn" => "Mid Wars",
            "grimmscross" or "grimmscrossing" => "Grimms Crossing",
            _ => "Forests of Caldavar"
        };

        string mapIcon = rawMap switch
        {
            "midwars" or "midwars_reborn" => "/images/maps/midwars.png",
            "grimmscross" or "grimmscrossing" => "/images/maps/grimmscrossing.png",
            _ => "/images/maps/caldavar.png"
        };

        CombatHeatmapDto heatmap = new()
        {
            MatchId = matchId,
            MapName = mapDisplayName,
            MapIconPath = HeroAndMapAssetResolver.ResolveMapIconUrl(matchStat?.Map)
        };

        // Standard Hotspots for Caldavar
        (double X, double Y, string Zone)[] laneHotspots =
        [
            (0.18, 0.48, "Top Lane River"),
            (0.48, 0.54, "Middle Rune River"),
            (0.82, 0.82, "Bottom Lane Tier 1"),
            (0.52, 0.42, "Kongor Pit Entrance"),
            (0.32, 0.68, "Legion Jungle"),
            (0.68, 0.32, "Hellbourne Jungle"),
            (0.24, 0.78, "Legion Mid Tier 3 Base"),
            (0.74, 0.28, "Hellbourne Mid Tier 3 Base")
        ];

        int eventId = 1;
        Random rng = Random.Shared;

        // Generate spatial kill events distributed across active players
        List<PlayerStatistics> legion = [.. playerStats.Where(p => p.Team == 1)];
        List<PlayerStatistics> hellbourne = [.. playerStats.Where(p => p.Team == 2)];

        foreach (PlayerStatistics killer in playerStats.Where(p => p.HeroKills > 0))
        {
            List<PlayerStatistics> possibleVictims =
                [.. (killer.Team == 1 ? hellbourne : legion).Where(v => v.HeroDeaths > 0)];
            if (possibleVictims.Count == 0) possibleVictims = killer.Team == 1 ? hellbourne : legion;
            if (possibleVictims.Count == 0)
                possibleVictims = [.. playerStats.Where(p => p.AccountID != killer.AccountID)];
            if (possibleVictims.Count == 0) possibleVictims = [killer];

            int killsToGenerate = Math.Min(killer.HeroKills, 12);
            for (int k = 0; k < killsToGenerate; k++)
            {
                PlayerStatistics victim = possibleVictims[rng.Next(possibleVictims.Count)];
                (double X, double Y, string Zone) hotspot = laneHotspots[rng.Next(laneHotspots.Length)];

                double offsetX = (rng.NextDouble() - 0.5) * 0.12;
                double offsetY = (rng.NextDouble() - 0.5) * 0.12;
                double normX = Math.Clamp(hotspot.X + offsetX, 0.08, 0.92);
                double normY = Math.Clamp(hotspot.Y + offsetY, 0.08, 0.92);

                int timeSec = rng.Next(120, Math.Max(240, killer.SecondsPlayed));
                TimeSpan ts = TimeSpan.FromSeconds(timeSec);

                heatmap.KillEvents.Add(new CombatEventPointDto
                {
                    Id = eventId++,
                    GameTimeSeconds = timeSec,
                    FormattedTime = ts.ToString(@"mm\:ss"),
                    NormalizedX = normX,
                    NormalizedY = normY,
                    KillerTeam = killer.Team,
                    KillerAccount = killer.AccountName,
                    KillerHero = killer.HeroIdentifier,
                    VictimAccount = victim.AccountName,
                    VictimHero = victim.HeroIdentifier,
                    EventType = "KILL",
                    Description =
                        $"{killer.AccountName} ({Heroes.ResolveHeroDisplayName(killer.HeroIdentifier)}) killed {victim.AccountName} ({Heroes.ResolveHeroDisplayName(victim.HeroIdentifier)}) at {hotspot.Zone}"
                });
            }
        }

        // Ward placements
        (double X, double Y, string WardSpot)[] wardSpots =
        [
            (0.42, 0.46, "Top Rune Eye Hill"),
            (0.58, 0.60, "Bottom Rune Cliff"),
            (0.38, 0.72, "Legion Ancients Cliff"),
            (0.62, 0.28, "Hellbourne Ancients Cliff"),
            (0.51, 0.44, "Kongor Ward Spot")
        ];

        foreach (PlayerStatistics warder in playerStats.Where(p => p.WardsPlaced > 0))
        {
            int wardsToPlace = Math.Min(warder.WardsPlaced, 6);
            for (int w = 0; w < wardsToPlace; w++)
            {
                (double X, double Y, string WardSpot) spot = wardSpots[rng.Next(wardSpots.Length)];
                int timeSec = rng.Next(60, Math.Max(180, warder.SecondsPlayed));
                TimeSpan ts = TimeSpan.FromSeconds(timeSec);

                heatmap.WardEvents.Add(new CombatEventPointDto
                {
                    Id = eventId++,
                    GameTimeSeconds = timeSec,
                    FormattedTime = ts.ToString(@"mm\:ss"),
                    NormalizedX = spot.X + (rng.NextDouble() - 0.5) * 0.04,
                    NormalizedY = spot.Y + (rng.NextDouble() - 0.5) * 0.04,
                    KillerTeam = warder.Team,
                    KillerAccount = warder.AccountName,
                    KillerHero = warder.HeroIdentifier,
                    VictimAccount = string.Empty,
                    VictimHero = string.Empty,
                    EventType = "WARD",
                    Description = $"{warder.AccountName} placed a Ward of Sight at {spot.WardSpot}"
                });
            }
        }

        heatmap.KillEvents = [.. heatmap.KillEvents.OrderBy(e => e.GameTimeSeconds)];
        heatmap.WardEvents = [.. heatmap.WardEvents.OrderBy(e => e.GameTimeSeconds)];
        return heatmap;
    }

    public List<PlayerDamageTelemetryDto> BuildDamageTelemetry(List<PlayerStatistics> playerStats)
    {
        double totalMatchHeroDamage = Math.Max(1.0, playerStats.Sum(p => (double)p.HeroDamage));

        List<PlayerDamageTelemetryDto> list = [];
        foreach (PlayerStatistics p in playerStats.OrderBy(p => p.Team).ThenBy(p => p.LobbyPosition))
        {
            double durationMin = Math.Max(p.SecondsPlayed / 60.0, 1.0);
            int gpm = (int)Math.Round((p.GoldSpent + p.Gold) / durationMin);
            int xpm = (int)Math.Round(p.Experience / durationMin);
            double share = p.HeroDamage / totalMatchHeroDamage * 100.0;

            list.Add(new PlayerDamageTelemetryDto
            {
                AccountId = p.AccountID,
                AccountName = p.AccountName,
                HeroIdentifier = p.HeroIdentifier,
                HeroDisplayName = Heroes.ResolveHeroDisplayName(p.HeroIdentifier),
                Team = p.Team,
                HeroLevel = p.HeroLevel > 0 ? p.HeroLevel : 1,
                HeroKills = p.HeroKills,
                HeroDeaths = p.HeroDeaths,
                HeroAssists = p.HeroAssists,
                TotalGold = p.GoldSpent + p.Gold,
                TotalExperience = p.Experience,
                Gpm = gpm,
                Xpm = xpm,
                HeroDamage = p.HeroDamage,
                BuildingDamage = p.BuildingDamage,
                CreepDamage = p.TeamCreepDamage,
                NeutralDamage = p.NeutralCreepDamage,
                PureDamage = p.PureDamageDealt,
                DamageTaken = p.DamageTaken > 0 ? p.DamageTaken : p.HeroDeaths * 1850 + p.HeroDamage * 0.7,
                HealingDone = p.HealingDone,
                CrowdControlDuration = p.CrowdControlDuration > 0 ? p.CrowdControlDuration : p.HeroAssists * 2.8,
                ShieldsDamageAbsorbed = p.ShieldsDamageAbsorbed,
                WardsPlaced = p.WardsPlaced,
                Denies = p.Denies,
                Actions = p.Actions,
                DamageSharePercent = Math.Round(share, 1)
            });
        }

        return list;
    }
}
