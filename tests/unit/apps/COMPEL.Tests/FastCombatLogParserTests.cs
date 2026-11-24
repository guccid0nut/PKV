// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using COMPEL.Services.Telemetry;

namespace COMPEL.Tests;

public sealed class FastCombatLogParserTests
{
    [Fact]
    public void TryParseExpEarned_ValidLine_ParsesCorrectly()
    {
        string line = "EXP_EARNED time:15432 x:8192.0 y:8192.0 z:128.0 player:0 team:1 experience:120.5";
        bool ok = FastCombatLogParser.TryParseExpEarned(line.AsSpan(), out FastCombatLogParser.ExpEarnedEvent ev);
        int timeMs = ev.TimeMs;
        float x = ev.X;
        float y = ev.Y;
        float z = ev.Z;
        int slot = ev.PlayerSlot;
        int team = ev.Team;
        float exp = ev.Experience;

        Assert.True(ok);
        Assert.Equal(15432, timeMs);
        Assert.Equal(8192.0f, x);
        Assert.Equal(8192.0f, y);
        Assert.Equal(128.0f, z);
        Assert.Equal(0, slot);
        Assert.Equal(1, team);
        Assert.Equal(120.5f, exp);
    }

    [Fact]
    public void TryParseItemActivate_ValidLine_ParsesCorrectly()
    {
        string line = "ITEM_ACTIVATE time:90000 x:4000.0 y:12000.0 z:50.0 player:3 team:2 item:\"Item_FlamingEye\"";
        bool ok = FastCombatLogParser.TryParseItemActivate(line.AsSpan(), out FastCombatLogParser.ItemActivateEvent ev);
        int timeMs = ev.TimeMs;
        int slot = ev.PlayerSlot;
        int team = ev.Team;
        string itemName = ev.ItemName.ToString();

        Assert.True(ok);
        Assert.Equal(90000, timeMs);
        Assert.Equal(3, slot);
        Assert.Equal(2, team);
        Assert.Equal("Item_FlamingEye", itemName);
    }

    [Fact]
    public void TryParseAbilityActivate_ValidLine_ParsesCorrectly()
    {
        string line =
            "ABILITY_ACTIVATE time:45000 x:100.0 y:200.0 z:0.0 player:1 team:1 ability:\"Ability_WitchSlayer1\"";
        bool ok = FastCombatLogParser.TryParseAbilityActivate(line.AsSpan(),
            out FastCombatLogParser.AbilityActivateEvent ev);
        int timeMs = ev.TimeMs;
        int slot = ev.PlayerSlot;
        string abilityName = ev.AbilityName.ToString();

        Assert.True(ok);
        Assert.Equal(45000, timeMs);
        Assert.Equal(1, slot);
        Assert.Equal("Ability_WitchSlayer1", abilityName);
    }

    [Fact]
    public void TryParseKill_ValidLine_ParsesCorrectly()
    {
        string line =
            "KILL time:60000 x:5000.0 y:5000.0 z:0.0 team:1 target:\"Hero_Flint\" attacker:\"Hero_WitchSlayer\"";
        bool ok = FastCombatLogParser.TryParseKill(line.AsSpan(), out FastCombatLogParser.KillEvent ev);
        int timeMs = ev.TimeMs;
        int team = ev.Team;
        string target = ev.Target.ToString();
        string attacker = ev.Attacker.ToString();

        Assert.True(ok);
        Assert.Equal(60000, timeMs);
        Assert.Equal(1, team);
        Assert.Equal("Hero_Flint", target);
        Assert.Equal("Hero_WitchSlayer", attacker);
    }

    [Fact]
    public void TryParsePlayerConnect_ValidLine_ParsesCorrectly()
    {
        string line = "PLAYER_CONNECT player:0 name:\"guccihost\" id:42";
        bool ok = FastCombatLogParser.TryParsePlayerConnect(line.AsSpan(),
            out FastCombatLogParser.PlayerConnectEvent ev);
        int slot = ev.PlayerSlot;
        string name = ev.Name.ToString();
        int id = ev.AccountId;

        Assert.True(ok);
        Assert.Equal(0, slot);
        Assert.Equal("guccihost", name);
        Assert.Equal(42, id);
    }

    [Fact]
    public void TryParseTeamJoined_WithServerPrefix_ParsesCorrectly()
    {
        string line1 = "Sv: [12:16:47] Client #0 joined team: 1";
        bool ok1 = FastCombatLogParser.TryParseTeamJoined(line1.AsSpan(), out FastCombatLogParser.TeamJoinedEvent ev1);
        int slot1 = ev1.PlayerSlot;
        int team1 = ev1.Team;
        Assert.True(ok1);
        Assert.Equal(0, slot1);
        Assert.Equal(1, team1);

        string line2 = "Sv: [12:16:48] Client #0 joined team: 2";
        bool ok2 = FastCombatLogParser.TryParseTeamJoined(line2.AsSpan(), out FastCombatLogParser.TeamJoinedEvent ev2);
        int slot2 = ev2.PlayerSlot;
        int team2 = ev2.Team;
        Assert.True(ok2);
        Assert.Equal(0, slot2);
        Assert.Equal(2, team2);
    }

    [Fact]
    public void TryParseVoiceClientAdd_WithServerPrefix_ParsesCorrectly()
    {
        string line = "Sv: [12:16:38] Voice: Adding client num 0, ID: 1";
        bool ok = FastCombatLogParser.TryParseVoiceClientAdd(line.AsSpan(),
            out FastCombatLogParser.VoiceClientAddEvent ev);
        int slot = ev.PlayerSlot;
        int accId = ev.AccountId;
        Assert.True(ok);
        Assert.Equal(0, slot);
        Assert.Equal(1, accId);
    }

    [Fact]
    public void TryParseHeroPick_WithServerPrefix_ParsesCorrectly()
    {
        string line = "Sv: [12:16:50] Client #0 selected hero Hero_Witchslayer";
        bool ok = FastCombatLogParser.TryParseHeroPick(line.AsSpan(), out FastCombatLogParser.HeroPickEvent ev);
        int slot = ev.PlayerSlot;
        string hero = ev.HeroIdentifier.ToString();
        Assert.True(ok);
        Assert.Equal(0, slot);
        Assert.Equal("Hero_Witchslayer", hero);
    }

    [Fact]
    public void TryParseGoldEarned_ValidLine_ParsesCorrectly()
    {
        string line = "Sv: [12:16:55] GOLD_EARNED time:34500 x:4500.0 y:6500.0 z:100.0 player:0 team:1 gold:250";
        bool ok = FastCombatLogParser.TryParseGoldEarned(line.AsSpan(), out FastCombatLogParser.GoldEarnedEvent ev);
        int timeMs = ev.TimeMs;
        int slot = ev.PlayerSlot;
        int team = ev.Team;
        int gold = ev.Gold;
        bool hasCoords = ev.HasCoordinates;

        Assert.True(ok);
        Assert.Equal(34500, timeMs);
        Assert.Equal(0, slot);
        Assert.Equal(1, team);
        Assert.Equal(250, gold);
        Assert.True(hasCoords);
    }

    [Fact]
    public void TryParseItemPurchase_AssembleAndSell_ParsesCorrectly()
    {
        string purchaseLine = "ITEM_PURCHASE time:12000 player:0 team:1 item:\"Item_Marchers\"";
        bool ok1 = FastCombatLogParser.TryParseItemPurchase(purchaseLine.AsSpan(),
            out FastCombatLogParser.ItemPurchaseEvent ev1);
        string item1 = ev1.ItemName.ToString();
        bool isAssemble1 = ev1.IsAssemble;
        bool isSell1 = ev1.IsSell;

        Assert.True(ok1);
        Assert.Equal("Item_Marchers", item1);
        Assert.False(isAssemble1);
        Assert.False(isSell1);

        string assembleLine = "ITEM_ASSEMBLE time:45000 player:0 team:1 item:\"Item_Steamboots\"";
        bool ok2 = FastCombatLogParser.TryParseItemPurchase(assembleLine.AsSpan(),
            out FastCombatLogParser.ItemPurchaseEvent ev2);
        string item2 = ev2.ItemName.ToString();
        bool isAssemble2 = ev2.IsAssemble;

        Assert.True(ok2);
        Assert.Equal("Item_Steamboots", item2);
        Assert.True(isAssemble2);

        string sellLine = "ITEM_SELL time:60000 player:0 team:1 item:\"Item_IronBuckler\"";
        bool ok3 = FastCombatLogParser.TryParseItemPurchase(sellLine.AsSpan(),
            out FastCombatLogParser.ItemPurchaseEvent ev3);
        string item3 = ev3.ItemName.ToString();
        bool isSell3 = ev3.IsSell;

        Assert.True(ok3);
        Assert.Equal("Item_IronBuckler", item3);
        Assert.True(isSell3);
    }

    [Fact]
    public void TryParseAbilityUpgrade_ValidLine_ParsesCorrectly()
    {
        string line = "ABILITY_UPGRADE time:1000 player:0 team:1 name:\"Ability_Pyromancer1\" level:1";
        bool ok = FastCombatLogParser.TryParseAbilityUpgrade(line.AsSpan(),
            out FastCombatLogParser.AbilityUpgradeEvent ev);
        int slot = ev.PlayerSlot;
        int team = ev.Team;
        string ability = ev.AbilityName.ToString();
        int level = ev.Level;

        Assert.True(ok);
        Assert.Equal(0, slot);
        Assert.Equal(1, team);
        Assert.Equal("Ability_Pyromancer1", ability);
        Assert.Equal(1, level);
    }

    [Fact]
    public void TryParseHeroLevel_ValidLine_ParsesCorrectly()
    {
        string line = "HERO_LEVEL time:75000 x:5000.0 y:5000.0 z:0.0 player:0 team:1 level:6";
        bool ok = FastCombatLogParser.TryParseHeroLevel(line.AsSpan(), out FastCombatLogParser.HeroLevelEvent ev);
        int slot = ev.PlayerSlot;
        int team = ev.Team;
        int level = ev.Level;

        Assert.True(ok);
        Assert.Equal(0, slot);
        Assert.Equal(1, team);
        Assert.Equal(6, level);
    }

    [Fact]
    public void TryParseChatMessage_ValidLines_ParsesCorrectly()
    {
        string line1 = "[ALL] guccidonut: Good luck and have fun!";
        bool ok1 = FastCombatLogParser.TryParseChatMessage(line1.AsSpan(),
            out FastCombatLogParser.ChatMessageEvent ev1);
        string channel1 = ev1.Channel.ToString();
        string name1 = ev1.AccountName.ToString();

        Assert.True(ok1);
        Assert.Equal("[ALL]", channel1);
        Assert.Equal("guccidonut", name1);

        string line2 = "[TEAM] teammate1: Ward top rune please";
        bool ok2 = FastCombatLogParser.TryParseChatMessage(line2.AsSpan(),
            out FastCombatLogParser.ChatMessageEvent ev2);
        string channel2 = ev2.Channel.ToString();
        string name2 = ev2.AccountName.ToString();

        Assert.True(ok2);
        Assert.Equal("[TEAM]", channel2);
        Assert.Equal("teammate1", name2);
    }

    [Fact]
    public void TryParseMatchId_VariousFormats_ParsesCorrectly()
    {
        string line1 = "StartGame - MatchID [59]";
        bool ok1 = FastCombatLogParser.TryParseMatchId(line1.AsSpan(), out int id1);
        Assert.True(ok1);
        Assert.Equal(59, id1);

        string line2 = "Attached to active game server log: Slave2_M2060568725_console.clog (Match #2060568725)";
        bool ok2 = FastCombatLogParser.TryParseMatchId(line2.AsSpan(), out int id2);

        string line3 = "M2060568725.log";
        bool ok3 = FastCombatLogParser.TryParseMatchId(line3.AsSpan(), out int id3);
        Assert.True(ok3);
        Assert.Equal(2060568725, id3);

        string line4 = "Sv: [23:13:38] match ID:1001";
        bool ok4 = FastCombatLogParser.TryParseMatchId(line4.AsSpan(), out int id4);
        Assert.True(ok4);
        Assert.Equal(1001, id4);

        string line5 = "match_id, INT: 1001";
        bool ok5 = FastCombatLogParser.TryParseMatchId(line5.AsSpan(), out int id5);
        Assert.True(ok5);
        Assert.Equal(1001, id5);
    }

    [Fact]
    public void CleanLine_WithAnsiEscapes_StripsCorrectlyAndParsesCombatEvent()
    {
        // Colored console output containing ANSI escape sequences
        string rawLine = "\x1B[32mEXP_EARNED\x1B[0m \x1B[1mtime:15432\x1B[0m x:8192.0 y:8192.0 z:128.0 player:0 team:1 experience:120.5";

        Span<char> scratch = stackalloc char[rawLine.Length];
        ReadOnlySpan<char> cleaned = FastCombatLogParser.CleanLine(rawLine.AsSpan(), scratch, out int charsWritten);

        Assert.True(charsWritten > 0);
        Assert.Equal("EXP_EARNED time:15432 x:8192.0 y:8192.0 z:128.0 player:0 team:1 experience:120.5", cleaned.ToString());

        bool ok = FastCombatLogParser.TryParseExpEarned(cleaned, out FastCombatLogParser.ExpEarnedEvent ev);
        Assert.True(ok);
        Assert.Equal(15432, ev.TimeMs);
        Assert.Equal(8192.0f, ev.X);
        Assert.Equal(120.5f, ev.Experience);
    }
}
