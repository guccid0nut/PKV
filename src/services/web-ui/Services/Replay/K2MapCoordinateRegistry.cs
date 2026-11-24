// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace DAWNBRINGER.WebPortal.UI.Services.Replay;

/// <summary>
///     Cleanroom K2 CMinimap coordinate registry mapping HoN world coordinates (0..worldDim)
///     into normalized [0..1, 0..1] minimap canvas coordinates (top-left origin, 1-Y inverted)
///     with exact per-map worldconfig minimap padding and calibrated fountain/lane anchors.
/// </summary>
public sealed record K2MapCalibration(
    string CanonicalSlug,
    float WorldDim,
    float PadLeft,
    float PadRight,
    float PadTop,
    float PadBottom,
    bool IsSingleLaneArena,
    float LegionWellU,
    float LegionWellV,
    float HellbourneWellU,
    float HellbourneWellV,
    float ClashCenterU = 0.50f,
    float ClashCenterV = 0.50f);

public sealed record K2MapSpec(
    string Slug,
    string DisplayName,
    float WorldDim,
    (float X, float Y) LegionFountain,
    (float X, float Y) HellbourneFountain,
    (float X, float Y) CenterPosition);

public static class K2MapCoordinateRegistry
{
    private static readonly K2MapCalibration DefaultCaldavar = new(
        CanonicalSlug: "caldavar",
        WorldDim: 16384f,
        PadLeft: 0.025f,
        PadRight: 0.025f,
        PadTop: 0.025f,
        PadBottom: 0.025f,
        IsSingleLaneArena: false,
        LegionWellU: 0.085f,
        LegionWellV: 0.915f,
        HellbourneWellU: 0.915f,
        HellbourneWellV: 0.085f,
        ClashCenterU: 0.485f,
        ClashCenterV: 0.515f);

    private static readonly K2MapCalibration MidWarsCalibration = new(
        CanonicalSlug: "midwars",
        WorldDim: 16384f,
        PadLeft: 0.14f,
        PadRight: 0.18f,
        PadTop: 0.19f,
        PadBottom: 0.13f,
        IsSingleLaneArena: true,
        LegionWellU: 0.095f,
        LegionWellV: 0.785f,
        HellbourneWellU: 0.755f,
        HellbourneWellV: 0.095f,
        ClashCenterU: 0.465f,
        ClashCenterV: 0.455f);

    private static readonly K2MapCalibration DevoWarsCalibration = new(
        CanonicalSlug: "devowars",
        WorldDim: 16384f,
        PadLeft: 0.33f,
        PadRight: 0.38f,
        PadTop: 0.31f,
        PadBottom: 0.38f,
        IsSingleLaneArena: true,
        LegionWellU: 0.230f,
        LegionWellV: 0.715f,
        HellbourneWellU: 0.710f,
        HellbourneWellV: 0.240f,
        ClashCenterU: 0.480f,
        ClashCenterV: 0.485f);

    private static readonly K2MapCalibration TrueDevoCalibration = new(
        CanonicalSlug: "truedevo",
        WorldDim: 16384f,
        PadLeft: 0.38f,
        PadRight: 0.35f,
        PadTop: 0.35f,
        PadBottom: 0.38f,
        IsSingleLaneArena: true,
        LegionWellU: 0.080f,
        LegionWellV: 0.510f,
        HellbourneWellU: 0.920f,
        HellbourneWellV: 0.510f,
        ClashCenterU: 0.500f,
        ClashCenterV: 0.500f);

    private static readonly K2MapCalibration GrimmsCalibration = new(
        CanonicalSlug: "grimmscrossing",
        WorldDim: 16384f,
        PadLeft: 0.225f,
        PadRight: 0.225f,
        PadTop: 0.21f,
        PadBottom: 0.22f,
        IsSingleLaneArena: false,
        LegionWellU: 0.120f,
        LegionWellV: 0.880f,
        HellbourneWellU: 0.880f,
        HellbourneWellV: 0.120f,
        ClashCenterU: 0.500f,
        ClashCenterV: 0.500f);

    private static readonly K2MapCalibration ProphetsCalibration = new(
        CanonicalSlug: "prophets",
        WorldDim: 16384f,
        PadLeft: 0.3125f,
        PadRight: 0.3125f,
        PadTop: 0.3125f,
        PadBottom: 0.3125f,
        IsSingleLaneArena: true,
        LegionWellU: 0.160f,
        LegionWellV: 0.840f,
        HellbourneWellU: 0.840f,
        HellbourneWellV: 0.160f);

    private static readonly K2MapCalibration ArenaCalibration = new(
        CanonicalSlug: "arena",
        WorldDim: 16384f,
        PadLeft: 0.375f,
        PadRight: 0.375f,
        PadTop: 0.375f,
        PadBottom: 0.375f,
        IsSingleLaneArena: true,
        LegionWellU: 0.180f,
        LegionWellV: 0.500f,
        HellbourneWellU: 0.820f,
        HellbourneWellV: 0.500f);

    private static readonly K2MapCalibration MediumBattlegroundCalibration = new(
        CanonicalSlug: "darkwoodvale",
        WorldDim: 16384f,
        PadLeft: 0.15f,
        PadRight: 0.15f,
        PadTop: 0.15f,
        PadBottom: 0.15f,
        IsSingleLaneArena: false,
        LegionWellU: 0.120f,
        LegionWellV: 0.880f,
        HellbourneWellU: 0.880f,
        HellbourneWellV: 0.120f);

    private static readonly K2MapCalibration CtfCalibration = new(
        CanonicalSlug: "capturetheflag",
        WorldDim: 9216f,
        PadLeft: 0.05f,
        PadRight: 0.05f,
        PadTop: 0.05f,
        PadBottom: 0.05f,
        IsSingleLaneArena: true,
        LegionWellU: 0.120f,
        LegionWellV: 0.500f,
        HellbourneWellU: 0.880f,
        HellbourneWellV: 0.500f);

    private static readonly K2MapCalibration CompactArenaCalibration = new(
        CanonicalSlug: "soccer",
        WorldDim: 8192f,
        PadLeft: 0.05f,
        PadRight: 0.05f,
        PadTop: 0.05f,
        PadBottom: 0.05f,
        IsSingleLaneArena: true,
        LegionWellU: 0.140f,
        LegionWellV: 0.500f,
        HellbourneWellU: 0.860f,
        HellbourneWellV: 0.500f);

    public static string NormalizeMapSlug(string? rawMap)
    {
        return HeroAndMapAssetResolver.ResolveMapSlug(rawMap);
    }

    public static K2MapSpec GetMapSpec(string? mapName)
    {
        K2MapCalibration cal = GetCalibration(mapName);
        string displayName = cal.CanonicalSlug switch
        {
            "caldavar" or "caldavar_old" => "Forests of Caldavar",
            "midwars" => "Mid Wars",
            "midwars_10v10" => "Mid Wars (10v10)",
            "midwars_reborn" => "Mid Wars Reborn",
            "midwars_halloween" => "Mid Wars (Halloween)",
            "devowars" => "DevoWars",
            "devowars_10v10" => "DevoWars (10v10)",
            "truedevo" or "truedevo_10v10" => "True DevoWars",
            "grimmscrossing" => "Grimm's Crossing",
            "riftwars" => "Rift Wars",
            "prophets" => "Theerea (Prophets)",
            "darkwoodvale" => "Darkwood Vale",
            "capturetheflag" => "Capture the Flag",
            "soccer" or "strikers" => "HoN Soccer",
            "arena" or "colosseum_duel" => "Gladiator Arena",
            _ => System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(
                cal.CanonicalSlug.Replace('_', ' '))
        };

        return new K2MapSpec(
            Slug: cal.CanonicalSlug,
            DisplayName: displayName,
            WorldDim: cal.WorldDim,
            LegionFountain: (cal.LegionWellU, cal.LegionWellV),
            HellbourneFountain: (cal.HellbourneWellU, cal.HellbourneWellV),
            CenterPosition: (cal.ClashCenterU, cal.ClashCenterV));
    }

    public static K2MapCalibration GetCalibration(string? mapName)
    {
        string slug = NormalizeMapSlug(mapName);
        return slug switch
        {
            "midwars" or "midwars_10v10" or "midwars_reborn" or "midwars_halloween"
                => MidWarsCalibration with { CanonicalSlug = slug },
            "devowars" or "devowars_10v10"
                => DevoWarsCalibration with { CanonicalSlug = slug },
            "truedevo" or "truedevo_10v10"
                => TrueDevoCalibration with { CanonicalSlug = slug },
            "grimmscrossing" or "riftwars" or "valkwars" or "empath_maze" or "thegrimmhunt" or "archers"
                => GrimmsCalibration with { CanonicalSlug = slug },
            "prophets"
                => ProphetsCalibration,
            "arena" or "colosseum_duel"
                => ArenaCalibration with { CanonicalSlug = slug },
            "darkwoodvale" or "persian_pass" or "team_deathmatch" or "watchtower"
                => MediumBattlegroundCalibration with { CanonicalSlug = slug },
            "capturetheflag"
                => CtfCalibration,
            "soccer" or "strikers" or "pebbles_duel"
                => CompactArenaCalibration with { CanonicalSlug = slug },
            _ => DefaultCaldavar with { CanonicalSlug = slug }
        };
    }

    /// <summary>
    ///     Projects K2 world coordinates (x, y) into top-left origin [0..1] minimap canvas coordinates (u, v)
    ///     matching k2_x64.dll CMinimap::GetCursorInWorld + worldconfig minimap padding.
    /// </summary>
    public static (float U, float V) WorldToMinimap(string? mapName, float worldX, float worldY)
    {
        K2MapCalibration cal = GetCalibration(mapName);
        float spanX = Math.Max(0.05f, 1.0f - cal.PadLeft - cal.PadRight);
        float spanY = Math.Max(0.05f, 1.0f - cal.PadBottom - cal.PadTop);

        float rawNormX = (worldX / cal.WorldDim - cal.PadLeft) / spanX;
        float rawNormY = (worldY / cal.WorldDim - cal.PadBottom) / spanY;

        float u = Math.Clamp(rawNormX, 0.0f, 1.0f);
        float v = Math.Clamp(1.0f - rawNormY, 0.0f, 1.0f);
        return (u, v);
    }

    public static (float U, float V) GetTeamFountain(string? mapName, int team)
    {
        K2MapCalibration cal = GetCalibration(mapName);
        return team == 2
            ? (cal.HellbourneWellU, cal.HellbourneWellV)
            : (cal.LegionWellU, cal.LegionWellV);
    }

    /// <summary>
    ///     Computes calibrated 2D minimap position &amp; HP% when reconstructing timelines from database stats.
    ///     Guarantees Legion spawns bottom-left and Hellbourne spawns top-right (proper 1-Y canvas orientation).
    /// </summary>
    public static (float U, float V, float HealthPercent, bool IsAlive) CalculateReconstructedState(
        string? mapName,
        int team,
        int slotIndex,
        int accountId,
        int second,
        int durationSeconds,
        int winnerTeam,
        IReadOnlyList<(int deathSec, int respawnSec)>? deaths = null)
    {
        float progress = durationSeconds > 0 ? Math.Clamp((float)second / durationSeconds, 0f, 1f) : 1f;
        int seed = accountId != 0 ? accountId + slotIndex * 13 : slotIndex + 1;
        (float u, float v, float hp) = CalculateReconstructedState(
            mapName, team, seed, progress, second, durationSeconds, winnerTeam, deaths);
        return (u, v, hp, hp > 0.01f);
    }

    public static (float U, float V, float HealthPercent) CalculateReconstructedState(
        string? mapName,
        int team,
        int accountOrSlotSeed,
        float progress,
        int second,
        int durationSeconds,
        int winnerTeam,
        IReadOnlyList<(int deathSec, int respawnSec)>? deaths = null)
    {
        K2MapCalibration cal = GetCalibration(mapName);
        (float wellU, float wellV) = team == 2
            ? (cal.HellbourneWellU, cal.HellbourneWellV)
            : (cal.LegionWellU, cal.LegionWellV);

        if (deaths != null)
        {
            for (int i = 0; i < deaths.Count; i++)
            {
                (int dSec, int rSec) = deaths[i];
                if (second >= dSec && second < rSec)
                {
                    return (wellU, wellV, 0f);
                }

                if (second >= dSec - 6 && second < dSec)
                {
                    (float fightU, float fightV) = GetCalibratedLanePoint(cal, team, accountOrSlotSeed, progress, second, winnerTeam);
                    float dropRatio = 1.0f - (second - (dSec - 6f)) / 6f;
                    return (fightU, fightV, Math.Clamp(dropRatio * 60f, 5f, 65f));
                }

                if (second >= rSec && second < rSec + 8)
                {
                    (float laneU, float laneV) = GetCalibratedLanePoint(cal, team, accountOrSlotSeed, progress, second, winnerTeam);
                    float walkT = (second - rSec) / 8f;
                    return (wellU + (laneU - wellU) * walkT, wellV + (laneV - wellV) * walkT, 100f);
                }
            }
        }

        // Pre-horn spawn & walk-out (scaled for short bot/test matches too)
        int spawnHold = Math.Min(8, Math.Max(2, durationSeconds / 25));
        int walkOutEnd = Math.Min(20, spawnHold + Math.Max(4, durationSeconds / 15));

        if (second <= spawnHold)
        {
            float jx = (float)Math.Sin(accountOrSlotSeed * 1.7f + second * 0.4f) * 0.012f;
            float jy = (float)Math.Cos(accountOrSlotSeed * 2.1f + second * 0.4f) * 0.012f;
            return (Math.Clamp(wellU + jx, 0.03f, 0.97f), Math.Clamp(wellV + jy, 0.03f, 0.97f), 100f);
        }

        if (second < walkOutEnd)
        {
            float walkT = (second - spawnHold) / (float)Math.Max(1, walkOutEnd - spawnHold);
            (float laneU, float laneV) = GetCalibratedLanePoint(cal, team, accountOrSlotSeed, progress, second, winnerTeam);
            return (wellU + (laneU - wellU) * walkT, wellV + (laneV - wellV) * walkT, 100f);
        }

        (float activeU, float activeV) = GetCalibratedLanePoint(cal, team, accountOrSlotSeed, progress, second, winnerTeam);
        float hp = Math.Clamp(78f + (float)Math.Sin(second * 0.27f + accountOrSlotSeed * 1.3f) * 18f, 32f, 100f);
        return (activeU, activeV, hp);
    }

    private static (float U, float V) GetCalibratedLanePoint(
        K2MapCalibration cal,
        int team,
        int seed,
        float progress,
        int second,
        int winnerTeam)
    {
        float dirSign = winnerTeam == 1 ? 1f : -1f;

        if (cal.IsSingleLaneArena)
        {
            // Single-lane / arena clash between Legion well and Hellbourne well through ClashCenter
            float du = cal.HellbourneWellU - cal.LegionWellU;
            float dv = cal.HellbourneWellV - cal.LegionWellV;

            float wave = (float)Math.Sin(second * (2.0 * Math.PI / 28.0)) * 0.05f;
            float push = dirSign * progress * 0.18f;
            float teamStandoff = team == 1 ? -0.045f : 0.045f;
            float trade = (float)Math.Sin(second * 1.3f + seed) * 0.018f;

            float t = Math.Clamp(0.50f + push + wave + teamStandoff + trade, 0.12f, 0.88f);
            float baseU = cal.LegionWellU + du * t;
            float baseV = cal.LegionWellV + dv * t;

            // Slight perpendicular spread for 5v5 / 10v10 heroes across the lane width
            float len = MathF.Max(0.001f, MathF.Sqrt(du * du + dv * dv));
            float perpU = -dv / len;
            float perpV = du / len;
            float spread = (float)Math.Cos(seed * 2.3f + second * 0.35f) * 0.028f;

            return (
                Math.Clamp(baseU + perpU * spread, 0.04f, 0.96f),
                Math.Clamp(baseV + perpV * spread, 0.04f, 0.96f));
        }

        // 3-Lane Caldavar / Grimms Crossing topology (top-left canvas origin: Legion = bottom-left, Hellbourne = top-right)
        int lane = Math.Abs(seed) % 3; // 0 = Mid, 1 = Top, 2 = Bot
        float laneT = Math.Clamp(
            0.50f + dirSign * progress * 0.20f + (float)Math.Sin(second * 0.12f + seed) * 0.07f + (team == 1 ? -0.035f : 0.035f),
            0.12f,
            0.88f);

        float u;
        float v;
        if (lane == 0)
        {
            // Mid lane: bottom-left (LegionWellU, LegionWellV) -> top-right (HellbourneWellU, HellbourneWellV)
            u = cal.LegionWellU + (cal.HellbourneWellU - cal.LegionWellU) * laneT;
            v = cal.LegionWellV + (cal.HellbourneWellV - cal.LegionWellV) * laneT;
        }
        else if (lane == 1)
        {
            // Top lane: goes up the left edge (u ~ 0.12, v: 0.88 -> 0.14), then right along top edge (u: 0.12 -> 0.88, v ~ 0.14)
            if (laneT < 0.5f)
            {
                float seg = laneT / 0.5f;
                u = cal.LegionWellU + 0.03f * seg;
                v = cal.LegionWellV + (0.14f - cal.LegionWellV) * seg;
            }
            else
            {
                float seg = (laneT - 0.5f) / 0.5f;
                u = (cal.LegionWellU + 0.03f) + (cal.HellbourneWellU - (cal.LegionWellU + 0.03f)) * seg;
                v = 0.14f + (cal.HellbourneWellV - 0.14f) * seg;
            }
        }
        else
        {
            // Bot lane: goes right along bottom edge (u: 0.12 -> 0.88, v ~ 0.88), then up right edge (u ~ 0.88, v: 0.88 -> 0.12)
            if (laneT < 0.5f)
            {
                float seg = laneT / 0.5f;
                u = cal.LegionWellU + (0.87f - cal.LegionWellU) * seg;
                v = cal.LegionWellV - 0.02f * seg;
            }
            else
            {
                float seg = (laneT - 0.5f) / 0.5f;
                u = 0.87f + (cal.HellbourneWellU - 0.87f) * seg;
                v = (cal.LegionWellV - 0.02f) + (cal.HellbourneWellV - (cal.LegionWellV - 0.02f)) * seg;
            }
        }

        float microU = (float)Math.Sin(second * 0.9f + seed * 1.7f) * 0.012f;
        float microV = (float)Math.Cos(second * 0.8f + seed * 1.9f) * 0.012f;
        return (Math.Clamp(u + microU, 0.04f, 0.96f), Math.Clamp(v + microV, 0.04f, 0.96f));
    }
}
