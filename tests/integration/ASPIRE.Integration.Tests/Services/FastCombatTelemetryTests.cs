// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json;

namespace ASPIRE.Tests.Services;

public sealed class FastCombatTelemetryTests
{
    [Fact]
    public void FastCombatTelemetry_LiveServerTickPayloadDto_SerializesAndDeserializesCorrectly()
    {
        LiveServerTickPayloadDto payload = new()
        {
            MatchID = 2060568744,
            ElapsedSeconds = 1420,
            ScoreLegion = 15,
            ScoreHellbourne = 12,
            GoldAdvantageLegion = 2500,
            GoldAdvantageHellbourne = 0,
            Players =
            [
                new LivePlayerTelemetryDto
                {
                    Slot = 0,
                    AccountID = 58,
                    AccountName = "guccidonut",
                    HeroIdentifier = "hero_empath",
                    Team = 1,
                    Level = 14,
                    PositionX = 0.512f,
                    PositionY = 0.488f,
                    CurrentGold = 1850,
                    GPM = 425,
                    Kills = 4,
                    Deaths = 1,
                    Assists = 11,
                    Inventory = ["Item_Steamboots", "Item_Astrolabe", "Item_PowerSupply"]
                },
                new LivePlayerTelemetryDto
                {
                    Slot = 5,
                    AccountID = 59,
                    AccountName = "ClickBotAlpha",
                    HeroIdentifier = "hero_nomad",
                    Team = 2,
                    Level = 16,
                    PositionX = 0.742f,
                    PositionY = 0.225f,
                    CurrentGold = 3400,
                    GPM = 610,
                    Kills = 10,
                    Deaths = 2,
                    Assists = 3,
                    Inventory = ["Item_GhostMarchers", "Item_Shieldbreaker", "Item_SavageMace"]
                }
            ],
            ActiveWards =
            [
                new LiveWardTelemetryDto
                {
                    WardType = "Sight",
                    Team = 1,
                    PositionX = 0.450f,
                    PositionY = 0.550f,
                    ExpiresInSeconds = 220
                }
            ],
            RecentEvents =
            [
                new LiveCombatEventTelemetryDto
                {
                    EventType = "KILL",
                    AttackerName = "ClickBotAlpha",
                    AttackerHero = "hero_nomad",
                    VictimName = "Legionnaire",
                    VictimHero = "hero_legionnaire",
                    MatchTimeSeconds = 1410,
                    Description = "💀 ClickBotAlpha (Nomad) defeated Legionnaire (Legionnaire)!"
                }
            ]
        };

        string json = JsonSerializer.Serialize(payload);
        Assert.NotNull(json);
        Assert.NotEmpty(json);

        LiveServerTickPayloadDto? deserialized = JsonSerializer.Deserialize<LiveServerTickPayloadDto>(json);
        Assert.NotNull(deserialized);
        Assert.Equal(2060568744, deserialized.MatchID);
        Assert.Equal(1420, deserialized.ElapsedSeconds);
        Assert.Equal(15, deserialized.ScoreLegion);
        Assert.Equal(12, deserialized.ScoreHellbourne);
        Assert.Equal(2, deserialized.Players.Count);
        Assert.Single(deserialized.ActiveWards);
        Assert.Single(deserialized.RecentEvents);

        LivePlayerTelemetryDto p0 = deserialized.Players[0];
        Assert.Equal("guccidonut", p0.AccountName);
        Assert.Equal("hero_empath", p0.HeroIdentifier);
        Assert.Equal(1, p0.Team);
        Assert.Equal(3, p0.Inventory.Count);
    }

    [Fact]
    public void FastCombatTelemetry_CoordinateNormalization_CalculatesRadarFractionsAccurately()
    {
        const float WorldMapDimension = 16384.0f;

        // Origin (0,0) in K2 map coordinates
        float k2X1 = 0.0f;
        float k2Y1 = 0.0f;
        float normX1 = Math.Clamp(k2X1 / WorldMapDimension, 0.0f, 1.0f);
        float normY1 = Math.Clamp(1.0f - k2Y1 / WorldMapDimension, 0.0f, 1.0f);

        Assert.Equal(0.0f, normX1);
        Assert.Equal(1.0f, normY1);

        // Center map (8192, 8192)
        float k2X2 = 8192.0f;
        float k2Y2 = 8192.0f;
        float normX2 = Math.Clamp(k2X2 / WorldMapDimension, 0.0f, 1.0f);
        float normY2 = Math.Clamp(1.0f - k2Y2 / WorldMapDimension, 0.0f, 1.0f);

        Assert.Equal(0.5f, normX2);
        Assert.Equal(0.5f, normY2);

        // Top right max bounds (16384, 16384)
        float k2X3 = 16384.0f;
        float k2Y3 = 16384.0f;
        float normX3 = Math.Clamp(k2X3 / WorldMapDimension, 0.0f, 1.0f);
        float normY3 = Math.Clamp(1.0f - k2Y3 / WorldMapDimension, 0.0f, 1.0f);

        Assert.Equal(1.0f, normX3);
        Assert.Equal(0.0f, normY3);
    }

    [Fact]
    public void FastCombatTelemetry_LuaBridgeFormat_ParsesValidKeyValuePairs()
    {
        string luaTelemetry = "[NEXUS_TELEMETRY] hero=hero_witchslayer;level=11;k=5;d=2;a=8;hp=1120;max_hp=1120";
        int idx = luaTelemetry.IndexOf("[NEXUS_TELEMETRY]", StringComparison.OrdinalIgnoreCase);
        Assert.True(idx >= 0);

        string payload = luaTelemetry.Substring(idx + "[NEXUS_TELEMETRY]".Length).Trim();
        Dictionary<string, string> parsed = new(StringComparer.OrdinalIgnoreCase);
        string[] pairs = payload.Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (string pair in pairs)
        {
            string[] kv = pair.Split('=', 2);
            if (kv.Length == 2) parsed[kv[0].Trim()] = kv[1].Trim();
        }

        Assert.Equal("hero_witchslayer", parsed["hero"]);
        Assert.Equal("11", parsed["level"]);
        Assert.Equal("5", parsed["k"]);
        Assert.Equal("2", parsed["d"]);
        Assert.Equal("8", parsed["a"]);
        Assert.Equal("1120", parsed["hp"]);
        Assert.Equal("1120", parsed["max_hp"]);
    }
}
