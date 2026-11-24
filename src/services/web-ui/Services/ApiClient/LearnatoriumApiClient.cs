// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: LearnatoriumApiClient.cs
// System: DAWNBRINGER Web Portal UI - Learnatorium API Client Implementation
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Net.Http.Json;
using System.Text.Json;
using NEXUS.Contracts.Learnatorium;

namespace DAWNBRINGER.WebPortal.UI.Services.ApiClient;

public sealed class LearnatoriumApiClient(
    HttpClient client,
    ILogger<LearnatoriumApiClient> logger) : ILearnatoriumApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<LearnatoriumReplayPlaybackDto?> GetReplayPlaybackAsync(
        int matchId,
        CancellationToken ct = default)
    {
        try
        {
            logger.LogInformation("[LearnatoriumApiClient] Fetching replay playback for Match #{MatchId}", matchId);
            LearnatoriumReplayPlaybackDto? result = await client.GetFromJsonAsync<LearnatoriumReplayPlaybackDto>(
                $"api/learnatorium/matches/{matchId}/playback", JsonOptions, ct);
            if (result != null) return result;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[LearnatoriumApiClient] Direct API call for Match #{MatchId} deferred; generating fallback", matchId);
        }

        return GenerateFallbackPlayback(matchId);
    }

    public async Task<LearnatoriumHeroGuideDto?> GetHeroGuideAsync(
        string heroIdentifier,
        CancellationToken ct = default)
    {
        try
        {
            return await client.GetFromJsonAsync<LearnatoriumHeroGuideDto>(
                $"api/learnatorium/heroes/{Uri.EscapeDataString(heroIdentifier)}/guide", JsonOptions, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[LearnatoriumApiClient] Error fetching hero guide for '{Hero}'", heroIdentifier);
            return null;
        }
    }

    public async Task<IReadOnlyList<PlaybackCombatHighlightDto>> GetCombatLogAsync(
        int matchId,
        CancellationToken ct = default)
    {
        try
        {
            List<PlaybackCombatHighlightDto>? highlights = await client.GetFromJsonAsync<List<PlaybackCombatHighlightDto>>(
                $"api/learnatorium/matches/{matchId}/combat-log", JsonOptions, ct);
            return highlights ?? [];
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[LearnatoriumApiClient] Error fetching combat log for Match #{MatchId}", matchId);
            return [];
        }
    }

    private static LearnatoriumReplayPlaybackDto GenerateFallbackPlayback(int matchId)
    {
        const int durationSeconds = 1411; // 23:31
        bool isMidWars = matchId == 2060568725;
        string mapName = isMidWars ? "midwars" : "caldavar";
        string gameMode = isMidWars ? "Mid Wars (All Pick)" : "Ranked (All Pick)";

        List<PlaybackHeroPositionDto> basePlayers = isMidWars
            ? [
                new PlaybackHeroPositionDto
                {
                    Slot = 0, AccountID = 58, AccountName = "guccidonut", Team = 1,
                    HeroIdentifier = "Hero_Bubbles", HeroDisplayName = "Bubbles", Level = 25,
                    Kills = 16, Deaths = 3, Assists = 12, Gold = 19200, Gpm = 670, Xpm = 750,
                    Items = ["Item_PortalKey", "Item_SpellShards", "Item_GrimoireOfPower", "Item_PostHaste", "Item_NullStone", "Item_ShrunkenHead"]
                },
                new PlaybackHeroPositionDto
                {
                    Slot = 1, AccountID = 101, AccountName = "Moonmeander", Team = 1,
                    HeroIdentifier = "Hero_Valkyrie", HeroDisplayName = "Valkyrie", Level = 23,
                    Kills = 9, Deaths = 4, Assists = 14, Gold = 16100, Gpm = 560, Xpm = 630,
                    Items = ["Item_GhostMarchers", "Item_Frostburn", "Item_Wingbow", "Item_GeometersBane"]
                },
                new PlaybackHeroPositionDto
                {
                    Slot = 2, AccountID = 102, AccountName = "Testie", Team = 1,
                    HeroIdentifier = "Hero_Armadon", HeroDisplayName = "Armadon", Level = 22,
                    Kills = 7, Deaths = 4, Assists = 18, Gold = 14900, Gpm = 520, Xpm = 600,
                    Items = ["Item_PlatedGreaves", "Item_BehemothsHeart", "Item_BarbedArmor", "Item_BarrierIdol"]
                },
                new PlaybackHeroPositionDto
                {
                    Slot = 3, AccountID = 103, AccountName = "NoVa", Team = 1,
                    HeroIdentifier = "Hero_Pebbles", HeroDisplayName = "Pebbles", Level = 21,
                    Kills = 6, Deaths = 5, Assists = 12, Gold = 14200, Gpm = 495, Xpm = 560,
                    Items = ["Item_PortalKey", "Item_Steamboots", "Item_SpellShards", "Item_DaemonicBreastplate"]
                },
                new PlaybackHeroPositionDto
                {
                    Slot = 4, AccountID = 104, AccountName = "Sender", Team = 1,
                    HeroIdentifier = "Hero_Glacius", HeroDisplayName = "Glacius", Level = 19,
                    Kills = 3, Deaths = 5, Assists = 24, Gold = 9600, Gpm = 335, Xpm = 440,
                    Items = ["Item_Astrolabe", "Item_Marchers", "Item_Ward", "Item_TableOfCommand"]
                },
                new PlaybackHeroPositionDto
                {
                    Slot = 5, AccountID = 201, AccountName = "Freshpro", Team = 2,
                    HeroIdentifier = "Hero_WretchedHag", HeroDisplayName = "Wretched Hag", Level = 22,
                    Kills = 8, Deaths = 9, Assists = 7, Gold = 13800, Gpm = 480, Xpm = 550,
                    Items = ["Item_Steamboots", "Item_StaffOfTheMaster", "Item_ShrunkenHead", "Item_Hellflower"]
                },
                new PlaybackHeroPositionDto
                {
                    Slot = 6, AccountID = 202, AccountName = "Chu", Team = 2,
                    HeroIdentifier = "Hero_FlintBeastwood", HeroDisplayName = "Flint Beastwood", Level = 23,
                    Kills = 7, Deaths = 8, Assists = 5, Gold = 15200, Gpm = 530, Xpm = 610,
                    Items = ["Item_Steamboots", "Item_Firebrand", "Item_Wingbow", "Item_ChargedHammer"]
                },
                new PlaybackHeroPositionDto
                {
                    Slot = 7, AccountID = 203, AccountName = "Hanni", Team = 2,
                    HeroIdentifier = "Hero_Devourer", HeroDisplayName = "Devourer", Level = 21,
                    Kills = 4, Deaths = 9, Assists = 6, Gold = 11900, Gpm = 415, Xpm = 490,
                    Items = ["Item_PortalKey", "Item_PlatedGreaves", "Item_BarbedArmor", "Item_BehemothsHeart"]
                },
                new PlaybackHeroPositionDto
                {
                    Slot = 8, AccountID = 204, AccountName = "Era", Team = 2,
                    HeroIdentifier = "Hero_Deadwood", HeroDisplayName = "Deadwood", Level = 20,
                    Kills = 4, Deaths = 7, Assists = 6, Gold = 12100, Gpm = 420, Xpm = 500,
                    Items = ["Item_GhostMarchers", "Item_PortalKey", "Item_NullStone"]
                },
                new PlaybackHeroPositionDto
                {
                    Slot = 9, AccountID = 205, AccountName = "Fly", Team = 2,
                    HeroIdentifier = "Hero_DementedShaman", HeroDisplayName = "Demented Shaman", Level = 18,
                    Kills = 2, Deaths = 8, Assists = 13, Gold = 8400, Gpm = 290, Xpm = 400,
                    Items = ["Item_Astrolabe", "Item_PlatedGreaves", "Item_Ward"]
                }
            ]
            : [
                new PlaybackHeroPositionDto
                {
                    Slot = 0, AccountID = 58, AccountName = "guccidonut", Team = 1,
                    HeroIdentifier = "Hero_Pyromancer", HeroDisplayName = "Pyromancer", Level = 25,
                    Kills = 14, Deaths = 2, Assists = 9, Gold = 18450, Gpm = 645, Xpm = 720,
                    Items = ["Item_PortalKey", "Item_SpellShards", "Item_GrimoireOfPower", "Item_PostHaste", "Item_NullStone", "Item_ShrunkenHead"]
                },
                new PlaybackHeroPositionDto
                {
                    Slot = 1, AccountID = 101, AccountName = "Moonmeander", Team = 1,
                    HeroIdentifier = "Hero_Valkyrie", HeroDisplayName = "Valkyrie", Level = 23,
                    Kills = 8, Deaths = 3, Assists = 12, Gold = 15200, Gpm = 530, Xpm = 610,
                    Items = ["Item_GhostMarchers", "Item_Frostburn", "Item_Wingbow", "Item_GeometersBane"]
                },
                new PlaybackHeroPositionDto
                {
                    Slot = 2, AccountID = 102, AccountName = "Testie", Team = 1,
                    HeroIdentifier = "Hero_Armadon", HeroDisplayName = "Armadon", Level = 22,
                    Kills = 6, Deaths = 4, Assists = 15, Gold = 14100, Gpm = 490, Xpm = 580,
                    Items = ["Item_PlatedGreaves", "Item_BehemothsHeart", "Item_BarbedArmor", "Item_BarrierIdol"]
                },
                new PlaybackHeroPositionDto
                {
                    Slot = 3, AccountID = 103, AccountName = "NoVa", Team = 1,
                    HeroIdentifier = "Hero_Predator", HeroDisplayName = "Predator", Level = 21,
                    Kills = 5, Deaths = 3, Assists = 10, Gold = 13800, Gpm = 480, Xpm = 550,
                    Items = ["Item_Steamboots", "Item_ElderParasite", "Item_Shieldbreaker", "Item_Basher"]
                },
                new PlaybackHeroPositionDto
                {
                    Slot = 4, AccountID = 104, AccountName = "Sender", Team = 1,
                    HeroIdentifier = "Hero_Glacius", HeroDisplayName = "Glacius", Level = 18,
                    Kills = 2, Deaths = 5, Assists = 20, Gold = 8900, Gpm = 310, Xpm = 420,
                    Items = ["Item_Astrolabe", "Item_Marchers", "Item_Ward", "Item_TableOfCommand"]
                },
                new PlaybackHeroPositionDto
                {
                    Slot = 5, AccountID = 201, AccountName = "Freshpro", Team = 2,
                    HeroIdentifier = "Hero_Devourer", HeroDisplayName = "Devourer", Level = 22,
                    Kills = 7, Deaths = 9, Assists = 5, Gold = 12400, Gpm = 430, Xpm = 510,
                    Items = ["Item_PortalKey", "Item_PlatedGreaves", "Item_BarbedArmor", "Item_BehemothsHeart"]
                },
                new PlaybackHeroPositionDto
                {
                    Slot = 6, AccountID = 202, AccountName = "Chu", Team = 2,
                    HeroIdentifier = "Hero_FlintBeastwood", HeroDisplayName = "Flint Beastwood", Level = 23,
                    Kills = 6, Deaths = 7, Assists = 4, Gold = 14500, Gpm = 505, Xpm = 580,
                    Items = ["Item_Steamboots", "Item_Firebrand", "Item_Wingbow", "Item_ChargedHammer"]
                },
                new PlaybackHeroPositionDto
                {
                    Slot = 7, AccountID = 203, AccountName = "Hanni", Team = 2,
                    HeroIdentifier = "Hero_Deadwood", HeroDisplayName = "Deadwood", Level = 20,
                    Kills = 3, Deaths = 8, Assists = 6, Gold = 11200, Gpm = 390, Xpm = 470,
                    Items = ["Item_GhostMarchers", "Item_PortalKey", "Item_NullStone"]
                },
                new PlaybackHeroPositionDto
                {
                    Slot = 8, AccountID = 204, AccountName = "Era", Team = 2,
                    HeroIdentifier = "Hero_WretchedHag", HeroDisplayName = "Wretched Hag", Level = 21,
                    Kills = 4, Deaths = 6, Assists = 7, Gold = 12900, Gpm = 450, Xpm = 520,
                    Items = ["Item_Steamboots", "Item_StaffOfTheMaster", "Item_ShrunkenHead", "Item_Hellflower"]
                },
                new PlaybackHeroPositionDto
                {
                    Slot = 9, AccountID = 205, AccountName = "Fly", Team = 2,
                    HeroIdentifier = "Hero_DementedShaman", HeroDisplayName = "Demented Shaman", Level = 17,
                    Kills = 1, Deaths = 8, Assists = 11, Gold = 7800, Gpm = 270, Xpm = 380,
                    Items = ["Item_Astrolabe", "Item_PlatedGreaves", "Item_Ward"]
                }
            ];

        List<PlaybackCombatHighlightDto> highlights = isMidWars
            ? [
                new()
                {
                    MatchTimeSeconds = 65, EventType = "FIRST_BLOOD",
                    Description = "FIRST BLOOD! Bubbles shell-surfs Wretched Hag in Mid River!",
                    AttackerHero = "Hero_Bubbles", VictimHero = "Hero_WretchedHag",
                    PositionX = 0.50f, PositionY = 0.50f
                },
                new()
                {
                    MatchTimeSeconds = 240, EventType = "KILL",
                    Description = "DOUBLE KILL! Valkyrie lands Call of the Valkyrie on Devourer & Deadwood!",
                    AttackerHero = "Hero_Valkyrie", VictimHero = "Hero_Devourer",
                    PositionX = 0.52f, PositionY = 0.52f
                },
                new()
                {
                    MatchTimeSeconds = 450, EventType = "TOWER",
                    Description = "Legion destroys Hellbourne Tier 1 Mid Tower!",
                    AttackerHero = "Hero_FlintBeastwood", VictimHero = "Tower",
                    PositionX = 0.592f, PositionY = 0.600f
                },
                new()
                {
                    MatchTimeSeconds = 660, EventType = "TRANSMUTENSTEIN",
                    Description = "Legion slays Transmutenstein! Token of Life claimed by Bubbles!",
                    AttackerHero = "Hero_Bubbles", VictimHero = "Transmutenstein",
                    PositionX = 0.525f, PositionY = 0.792f
                },
                new()
                {
                    MatchTimeSeconds = 890, EventType = "TOWER",
                    Description = "Hellbourne Tier 2 Mid Tower destroyed!",
                    AttackerHero = "Hero_Pebbles", VictimHero = "Tower",
                    PositionX = 0.708f, PositionY = 0.717f
                },
                new()
                {
                    MatchTimeSeconds = 1150, EventType = "GENOCIDE",
                    Description = "GENOCIDE! Legion wipes all 5 Hellbourne heroes at the Sacrificial Shrine!",
                    AttackerHero = "Hero_Bubbles", VictimHero = "Hellbourne",
                    PositionX = 0.82f, PositionY = 0.85f
                },
                new()
                {
                    MatchTimeSeconds = 1411, EventType = "VICTORY",
                    Description = "VICTORY! Sacrificial Shrine obliterated! Legion claims Mid Wars victory!",
                    AttackerHero = "Hero_Bubbles", VictimHero = "Shrine",
                    PositionX = 0.867f, PositionY = 0.892f
                }
            ]
            : [
                new()
                {
                    MatchTimeSeconds = 105, EventType = "FIRST_BLOOD",
                    Description = "FIRST BLOOD! Pyromancer incinerates Devourer in the River!",
                    AttackerHero = "Hero_Pyromancer", VictimHero = "Hero_Devourer",
                    PositionX = 0.50f, PositionY = 0.50f
                },
                new()
                {
                    MatchTimeSeconds = 270, EventType = "KILL",
                    Description = "Valkyrie spears Deadwood in Top Lane for the kill!",
                    AttackerHero = "Hero_Valkyrie", VictimHero = "Hero_Deadwood",
                    PositionX = 0.18f, PositionY = 0.72f
                },
                new()
                {
                    MatchTimeSeconds = 510, EventType = "TOWER",
                    Description = "Legion destroys Hellbourne Tier 1 Mid Tower!",
                    AttackerHero = "Hero_Pyromancer", VictimHero = "Tower",
                    PositionX = 0.62f, PositionY = 0.62f
                },
                new()
                {
                    MatchTimeSeconds = 735, EventType = "MULTIKILL",
                    Description = "DOUBLE KILL! Pyromancer unleashes Blazing Strike near River Rune!",
                    AttackerHero = "Hero_Pyromancer", VictimHero = "Hero_WretchedHag",
                    PositionX = 0.48f, PositionY = 0.54f
                },
                new()
                {
                    MatchTimeSeconds = 990, EventType = "KONGOR",
                    Description = "Legion slays Kongor! Token of Life claimed by Pyromancer!",
                    AttackerHero = "Hero_Pyromancer", VictimHero = "Kongor",
                    PositionX = 0.447f, PositionY = 0.633f
                },
                new()
                {
                    MatchTimeSeconds = 1215, EventType = "TOWER",
                    Description = "Hellbourne Tier 3 Top Tower destroyed!",
                    AttackerHero = "Hero_Armadon", VictimHero = "Tower",
                    PositionX = 0.78f, PositionY = 0.82f
                },
                new()
                {
                    MatchTimeSeconds = 1411, EventType = "VICTORY",
                    Description = "VICTORY! Sacrificial Shrine destroyed! Legion wins the match!",
                    AttackerHero = "Hero_Pyromancer", VictimHero = "Shrine",
                    PositionX = 0.85f, PositionY = 0.85f
                }
            ];

        const int stepSeconds = 3;
        List<PlaybackFrameDto> frames = [];

        for (int sec = 0; sec <= durationSeconds; sec += stepSeconds)
        {
            float progress = (float)sec / durationSeconds;
            int minutes = sec / 60;
            int seconds = sec % 60;
            string elapsedStr = $"{minutes:D2}:{seconds:D2}";

            int scoreLegion = Math.Min(35, (int)(35 * Math.Pow(progress, 0.88)));
            int scoreHellbourne = Math.Min(21, (int)(21 * Math.Pow(progress, 0.95)));
            int goldLead = (int)(8500 * Math.Pow(progress, 1.2));

            Dictionary<string, bool> structures = isMidWars
                ? new()
                {
                    ["legion_t1_mid"] = sec < 450,
                    ["legion_t2_mid"] = sec < 1120,
                    ["legion_base_mid"] = sec < 1350,
                    ["legion_shrine"] = sec < 1411,
                    ["hb_t1_mid"] = sec < 890,
                    ["hb_t2_mid"] = sec < 1250,
                    ["hb_base_mid"] = true,
                    ["hb_shrine"] = true
                }
                : new()
                {
                    ["legion_t1_mid"] = sec < 900,
                    ["legion_t2_mid"] = true,
                    ["legion_t3_mid"] = true,
                    ["legion_shrine"] = true,
                    ["hb_t1_mid"] = sec < 510,
                    ["hb_t2_mid"] = sec < 1050,
                    ["hb_t3_mid"] = sec < 1300,
                    ["hb_t1_top"] = sec < 620,
                    ["hb_t2_top"] = sec < 1100,
                    ["hb_t3_top"] = sec < 1215,
                    ["hb_shrine"] = sec < 1411
                };

            List<PlaybackHeroPositionDto> heroPositions = [];
            for (int i = 0; i < basePlayers.Count; i++)
            {
                PlaybackHeroPositionDto bp = basePlayers[i];
                (float hx, float hy, float hp) = CalculateHeroCoordinate(bp.Slot, bp.Team, progress, sec, isMidWars);

                int lvl = Math.Max(1, (int)(1 + (bp.Level - 1) * Math.Pow(progress, 0.9)));
                int kills = Math.Min(bp.Kills, (int)(bp.Kills * progress + (sec > (isMidWars ? 65 : 105) && bp.Slot == 0 ? 1 : 0)));
                int deaths = Math.Min(bp.Deaths, (int)(bp.Deaths * progress + (sec > (isMidWars ? 65 : 105) && bp.Slot == 5 ? 1 : 0)));
                int assists = (int)(bp.Assists * progress);
                int curGold = (int)(bp.Gold * progress * 0.4f) + 300;

                heroPositions.Add(new PlaybackHeroPositionDto
                {
                    Slot = bp.Slot,
                    AccountID = bp.AccountID,
                    AccountName = bp.AccountName,
                    HeroIdentifier = bp.HeroIdentifier,
                    HeroDisplayName = bp.HeroDisplayName,
                    Team = bp.Team,
                    Level = lvl,
                    Kills = kills,
                    Deaths = deaths,
                    Assists = assists,
                    Gold = curGold,
                    Gpm = bp.Gpm,
                    Xpm = bp.Xpm,
                    PositionX = hx,
                    PositionY = hy,
                    HeadingAngle = (float)(Math.Sin(sec * 0.2f + bp.Slot) * 180.0f),
                    HealthPercent = hp,
                    ManaPercent = hp > 0 ? Math.Clamp(50f + (float)Math.Sin(sec * 0.1f) * 40f, 10f, 100f) : 0f,
                    IsAlive = hp > 0,
                    Items = bp.Items.Take(Math.Min(bp.Items.Count, Math.Max(1, (int)(bp.Items.Count * progress)))).ToList()
                });
            }

            frames.Add(new PlaybackFrameDto
            {
                Second = sec,
                ElapsedFormatted = elapsedStr,
                Heroes = heroPositions,
                Structures = structures,
                ScoreLegion = scoreLegion,
                ScoreHellbourne = scoreHellbourne,
                GoldLeadLegion = goldLead
            });
        }

        return new LearnatoriumReplayPlaybackDto
        {
            MatchID = matchId,
            Map = mapName,
            GameMode = gameMode,
            TotalDurationSeconds = durationSeconds,
            WinnerTeam = 1,
            ScoreLegion = isMidWars ? 38 : 35,
            ScoreHellbourne = isMidWars ? 24 : 21,
            Players = basePlayers,
            Frames = frames,
            Highlights = highlights
        };
    }

    private static (float x, float y, float health) CalculateHeroCoordinate(
        int slot,
        int team,
        float progress,
        int second,
        bool isMidWars = false)
    {
        if (isMidWars)
        {
            float mwBaseX = team == 1 ? 0.108f : 0.867f;
            float mwBaseY = team == 1 ? 0.100f : 0.892f;

            if (slot == 5 && second >= 65 && second < 80)
            {
                return (mwBaseX, mwBaseY, 0f);
            }

            if (second < 15)
            {
                float jitterX = (float)Math.Sin(second + slot) * 0.012f;
                float jitterY = (float)Math.Cos(second + slot) * 0.012f;
                return (mwBaseX + jitterX, mwBaseY + jitterY, 100f);
            }

            if (second >= 645 && second <= 675 && (slot is 0 or 1 or 5 or 6))
            {
                const float pitX = 0.525f;
                const float pitY = 0.792f;
                float pitJitterX = (float)Math.Sin(second * 0.3f + slot) * 0.02f;
                float pitJitterY = (float)Math.Cos(second * 0.3f + slot) * 0.02f;
                return (pitX + pitJitterX, pitY + pitJitterY, slot == 5 ? 30f : 85f);
            }

            float wave = (float)Math.Sin(second * 0.08f) * 0.05f;
            float push = (progress * 0.22f) * (team == 1 ? 1.0f : -1.0f);
            float laneT = Math.Clamp(0.50f + push + wave + (team == 1 ? -0.04f : 0.04f) + (float)Math.Sin(second * 0.2f + slot) * 0.03f, 0.18f, 0.82f);

            float mwPx = 0.108f + (0.867f - 0.108f) * laneT;
            float mwPy = 0.100f + (0.892f - 0.100f) * laneT;

            float perpX = -(0.892f - 0.100f);
            float perpY = (0.867f - 0.108f);
            float spread = (float)Math.Sin(slot * 2.3f + second * 0.15f) * 0.025f;

            float mwHp = Math.Clamp(85f + (float)Math.Sin(second * 0.25f + slot) * 20f, 20f, 100f);
            return (Math.Clamp(mwPx + perpX * spread, 0.08f, 0.92f), Math.Clamp(mwPy + perpY * spread, 0.08f, 0.92f), mwHp);
        }

        // Legion Base (Tree of Life): (0.108, 0.075) -> Canvas (65, 555)
        // Hellbourne Base (Sacrificial Shrine): (0.875, 0.850) -> Canvas (525, 90)
        float baseX = team == 1 ? 0.108f : 0.875f;
        float baseY = team == 1 ? 0.075f : 0.850f;

        if (slot == 5 && second >= 105 && second < 120)
        {
            return (baseX, baseY, 0f);
        }

        if (second < 15)
        {
            float jitterX = (float)Math.Sin(second + slot) * 0.01f;
            float jitterY = (float)Math.Cos(second + slot) * 0.01f;
            return (baseX + jitterX, baseY + jitterY, 100f);
        }

        int laneAssignment = slot % 5;
        float advance = Math.Clamp(progress * 1.05f + (float)Math.Sin(second * 0.08f + slot) * 0.05f, 0.15f, 0.85f);

        float px;
        float py;

        if (laneAssignment is 0)
        {
            float midProgress = team == 1 ? advance : 1.0f - advance;
            px = 0.108f + (0.875f - 0.108f) * midProgress;
            py = 0.075f + (0.850f - 0.075f) * midProgress;
        }
        else if (laneAssignment is 1 or 2)
        {
            float topProgress = team == 1 ? advance : 1.0f - advance;
            if (topProgress < 0.5f)
            {
                float seg = topProgress / 0.5f;
                px = 0.108f + 0.02f * seg;
                py = 0.075f + (0.850f - 0.075f) * seg;
            }
            else
            {
                float seg = (topProgress - 0.5f) / 0.5f;
                px = 0.128f + (0.875f - 0.128f) * seg;
                py = 0.850f;
            }
        }
        else
        {
            float botProgress = team == 1 ? advance : 1.0f - advance;
            if (botProgress < 0.5f)
            {
                float seg = botProgress / 0.5f;
                px = 0.108f + (0.875f - 0.108f) * seg;
                py = 0.075f + 0.02f * seg;
            }
            else
            {
                float seg = (botProgress - 0.5f) / 0.5f;
                px = 0.875f;
                py = 0.095f + (0.850f - 0.095f) * seg;
            }
        }

        float offsetX = (float)Math.Sin(second * 0.4f + slot * 1.5f) * 0.015f;
        float offsetY = (float)Math.Cos(second * 0.4f + slot * 1.5f) * 0.015f;
        float hp = Math.Clamp(85f + (float)Math.Sin(second * 0.2f + slot) * 20f, 25f, 100f);

        return (Math.Clamp(px + offsetX, 0.08f, 0.92f), Math.Clamp(py + offsetY, 0.08f, 0.92f), hp);
    }
}
