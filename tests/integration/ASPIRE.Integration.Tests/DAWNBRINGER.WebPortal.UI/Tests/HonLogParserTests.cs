// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using DAWNBRINGER.WebPortal.UI.Services.LogParser;

namespace ASPIRE.Tests.DAWNBRINGER.WebPortal.UI.Tests;

public sealed class HonLogParserTests
{
    [Fact]
    public void ParseLine_InfoMatch_ParsesMatchIdAndName()
    {
        HonLogParsedState state = new();
        bool result = HonLogParser.ParseLine(@"INFO_MATCH name:""mw "" id:""2060568744""", state);

        Assert.True(result);
        Assert.Equal(2060568744, state.MatchID);
        Assert.Equal("mw ", state.MatchName);
    }

    [Fact]
    public void ParseLine_PlayerConnectAndTeamChange_ParsesHellbourneTeam()
    {
        HonLogParsedState state = new();
        HonLogParser.ParseLine(@"INFO_MAP name:""midwars""", state);
        HonLogParser.ParseLine(@"PLAYER_CONNECT player:0 name:""guccidonut"" id:58 psr:1500.0000", state);
        HonLogParser.ParseLine("PLAYER_TEAM_CHANGE player:0 team:2", state);

        Assert.True(state.PlayersBySlot.ContainsKey(0));
        LivePlayerState player = state.PlayersBySlot[0];
        Assert.Equal("guccidonut", player.AccountName);
        Assert.Equal(58, player.AccountID);
        Assert.Equal(2, player.Team); // Hellbourne
        Assert.True(player.PositionX > 0.7f); // Hellbourne spawn
        Assert.True(player.PositionY < 0.3f);
    }

    [Fact]
    public void ParseLine_PlayerSelect_ParsesEmpathHero()
    {
        HonLogParsedState state = new();
        HonLogParser.ParseLine(@"PLAYER_CONNECT player:0 name:""guccidonut"" id:58 psr:1500.0000", state);
        HonLogParser.ParseLine(@"PLAYER_SELECT player:0 hero:""Hero_Empath""", state);

        Assert.True(state.PlayersBySlot.ContainsKey(0));
        LivePlayerState player = state.PlayersBySlot[0];
        Assert.Equal("hero_empath", player.HeroIdentifier);
        Assert.Equal("Empath", player.HeroDisplayName);
        Assert.NotEmpty(player.Abilities);
    }

    [Fact]
    public void ParseLine_ExpEarned_UpdatesPlayerCoordinatesInRealTime()
    {
        HonLogParsedState state = new();
        HonLogParser.ParseLine(@"PLAYER_CONNECT player:0 name:""guccidonut"" id:58 psr:1500.0000", state);
        // Empath at Mid River
        HonLogParser.ParseLine("EXP_EARNED time:15000 x:8015 y:8111 z:0 player:0 team:2 experience:2.00", state);

        Assert.True(state.PlayersBySlot.ContainsKey(0));
        LivePlayerState player = state.PlayersBySlot[0];
        Assert.InRange(player.PositionX, 0.45f, 0.55f); // Mid River X
        Assert.InRange(player.PositionY, 0.45f, 0.55f); // Mid River Y
        Assert.Equal(15, state.ElapsedSeconds);
    }

    [Fact]
    public void ParseLine_ItemActivate_DropsWardAtExactCoordinates()
    {
        HonLogParsedState state = new();
        HonLogParser.ParseLine(@"PLAYER_CONNECT player:0 name:""guccidonut"" id:58 psr:1500.0000", state);
        // Empath activates Flaming Eye (Sight Ward) at river ramp
        HonLogParser.ParseLine(@"ITEM_ACTIVATE time:0 x:7914 y:9071 z:0 player:0 team:2 item:""Item_FlamingEye""",
            state);

        Assert.Single(state.ActiveWards);
        LiveWardState ward = state.ActiveWards[0];
        Assert.Equal("Sight", ward.WardType);
        Assert.Equal(2, ward.Team);
        Assert.Equal("guccidonut", ward.PlacerHeroName);
        Assert.InRange(ward.PositionX, 0.45f, 0.52f);
        Assert.InRange(ward.PositionY, 0.40f, 0.50f);
    }

    [Fact]
    public void HonLogParser_ParseFullLogSequence_MatchesUserSessionAccurately()
    {
        HonLogParsedState state = new();
        string[] lines =
        [
            @"INFO_MATCH name:""mw "" id:""2060568744""",
            @"INFO_MAP name:""midwars"" version:""0.0.0""",
            @"PLAYER_CONNECT player:0 name:""guccidonut"" id:58 psr:1500.0000",
            @"PLAYER_CONNECT player:1 name:""FakePlayer1"" id:101 psr:1500.0000",
            "PLAYER_TEAM_CHANGE player:0 team:2",
            "PLAYER_TEAM_CHANGE player:1 team:1",
            @"PLAYER_SELECT player:0 hero:""Hero_Empath""",
            @"PLAYER_RANDOM player:1 hero:""Hero_Chi""",
            "HERO_LEVEL time:0 x:8599 y:11849 z:232 player:0 team:2 level:3",
            @"ITEM_PURCHASE time:0 x:8731 y:11611 z:232 player:0 team:2 item:""Item_Marchers"" cost:500",
            @"ITEM_ACTIVATE time:0 x:7914 y:9071 z:0 player:0 team:2 item:""Item_FlamingEye""",
            "EXP_EARNED time:24000 x:8015 y:8111 z:0 player:0 team:2 experience:2.00"
        ];

        foreach (string line in lines) HonLogParser.ParseLine(line, state);

        Assert.Equal(2060568744, state.MatchID);
        Assert.Equal(2, state.PlayersBySlot.Count);

        LivePlayerState empath = state.PlayersBySlot[0];
        Assert.Equal("guccidonut", empath.AccountName);
        Assert.Equal(2, empath.Team); // Hellbourne
        Assert.Equal("hero_empath", empath.HeroIdentifier);
        Assert.Equal("Empath", empath.HeroDisplayName);
        Assert.Equal(3, empath.Level);
        Assert.Contains("item_marchers", empath.Inventory);
        Assert.InRange(empath.PositionX, 0.45f, 0.55f);
        Assert.InRange(empath.PositionY, 0.45f, 0.55f);

        Assert.Single(state.ActiveWards);
        Assert.Equal("Sight", state.ActiveWards[0].WardType);
        Assert.Equal(24, state.ElapsedSeconds);
    }

    [Fact]
    public void ParseLine_ConsoleClientJoinedTeam_SwitchesTeamImmediately()
    {
        HonLogParsedState state = new();
        HonLogParser.ParseLine("Name: guccidonut", state);
        HonLogParser.ParseLine("match ID:2060568745", state);
        HonLogParser.ParseLine("map:midwars", state);

        Assert.Equal(2060568745, state.MatchID);
        Assert.Equal("midwars", state.Map);

        // Join Legion
        HonLogParser.ParseLine("Sv: [08:45:50] Client #0 joined team: 1", state);
        Assert.Equal(1, state.PlayersBySlot[0].Team);
        Assert.True(state.PlayersBySlot[0].PositionX < 0.3f); // Legion fountain

        // Switch to Hellbourne
        HonLogParser.ParseLine("Sv: [08:45:58] Client #0 joined team: 2", state);
        Assert.Equal(2, state.PlayersBySlot[0].Team);
        Assert.True(state.PlayersBySlot[0].PositionX > 0.7f); // Hellbourne fountain
        Assert.True(state.PlayersBySlot[0].PositionY < 0.3f);

        // Switch back to Legion
        HonLogParser.ParseLine("Sv: [08:46:22] Client #0 joined team: 1", state);
        Assert.Equal(1, state.PlayersBySlot[0].Team);

        // Switch to Hellbourne again
        HonLogParser.ParseLine("Sv: [08:46:26] Client #0 joined team: 2", state);
        Assert.Equal(2, state.PlayersBySlot[0].Team);
    }

    [Fact]
    public void ParseLine_PlayerRepickAndReselect_UpdatesHeroStateCorrectly()
    {
        HonLogParsedState state = new();
        HonLogParser.ParseLine(@"PLAYER_CONNECT player:0 name:""guccidonut"" id:58 psr:1500.0000", state);

        // 1. Initial selection: Empath
        HonLogParser.ParseLine(@"PLAYER_SELECT player:0 hero:""Hero_Empath""", state);
        Assert.Equal("hero_empath", state.PlayersBySlot[0].HeroIdentifier);
        Assert.Equal("Empath", state.PlayersBySlot[0].HeroDisplayName);

        // 2. Click Repick
        HonLogParser.ParseLine("PLAYER_REPICK player:0", state);
        Assert.Equal(string.Empty, state.PlayersBySlot[0].HeroIdentifier);
        Assert.Equal("Picking Hero...", state.PlayersBySlot[0].HeroDisplayName);
        Assert.Empty(state.PlayersBySlot[0].Abilities);

        // 3. Select new hero: Bubbles
        HonLogParser.ParseLine(@"PLAYER_SELECT player:0 hero:""Hero_Bubbles""", state);
        Assert.Equal("hero_bubbles", state.PlayersBySlot[0].HeroIdentifier);
        Assert.Equal("Bubbles", state.PlayersBySlot[0].HeroDisplayName);
        Assert.NotEmpty(state.PlayersBySlot[0].Abilities);
    }

    [Fact]
    public void ParseLine_VoiceAndChatAndTeamJoined_MapsClientCorrectlyWithZeroStartingGold()
    {
        HonLogParsedState state = new();

        // 1. Voice adds client 0 with ID 1
        bool voiceParsed = HonLogParser.ParseLine("Sv: [13:14:46] Voice: Adding client num 0, ID: 1", state);
        Assert.True(voiceParsed);
        Assert.True(state.PlayersBySlot.ContainsKey(0));
        Assert.Equal(1, state.PlayersBySlot[0].AccountID);
        Assert.Equal(0, state.PlayersBySlot[0].CurrentGold);

        // 2. Chat message resolves name
        bool chatParsed = HonLogParser.ParseLine("Sv: [13:14:47] [ALL] guccidonut: hi", state);
        Assert.True(chatParsed);
        Assert.Equal("guccidonut", state.PlayersBySlot[0].AccountName);

        // 3. Team join to Hellbourne (Team 2)
        bool teamParsed = HonLogParser.ParseLine("Sv: [13:14:48] Client #0 joined team: 2", state);
        Assert.True(teamParsed);
        Assert.Equal(2, state.PlayersBySlot[0].Team);
        Assert.True(state.PlayersBySlot[0].PositionX > 0.7f); // Hellbourne base
        Assert.True(state.PlayersBySlot[0].PositionY < 0.3f);
        Assert.Equal(0, state.PlayersBySlot[0].CurrentGold);
    }

    [Fact]
    public void ParseLine_VoiceAddingClientWithExplicitUsername_MapsImmediately()
    {
        HonLogParsedState state = new();

        bool parsed = HonLogParser.ParseLine("Sv: [13:14:46] Voice: Adding client num 0, guccidonut, ID: 1", state);
        Assert.True(parsed);
        Assert.True(state.PlayersBySlot.ContainsKey(0));
        Assert.Equal("guccidonut", state.PlayersBySlot[0].AccountName);
        Assert.Equal(1, state.PlayersBySlot[0].AccountID);
        Assert.Equal(0, state.PlayersBySlot[0].CurrentGold);
    }
}
