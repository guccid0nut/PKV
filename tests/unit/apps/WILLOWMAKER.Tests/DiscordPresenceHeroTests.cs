// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Constants;
using DiscordRPC;
using WILLOWMAKER.Core.Services;
using Xunit;

namespace WILLOWMAKER.Tests;

public sealed class DiscordPresenceHeroTests
{
    [Theory]
    [InlineData("Hero_Devourer", "hero_devourer", "Devourer")]
    [InlineData("hero_devourer", "hero_devourer", "Devourer")]
    [InlineData("devourer", "hero_devourer", "Devourer")]
    [InlineData("Hero_Pyromancer", "hero_pyromancer", "Pyromancer")]
    [InlineData("Hero_Chronos", "hero_chronos", "Chronos")]
    [InlineData("Hero_WitchSlayer", "hero_witch_slayer", "Witch Slayer")]
    [InlineData("Hero_Witch_Slayer", "hero_witch_slayer", "Witch Slayer")]
    [InlineData("Hero_FlintBeastwood", "hero_flint_beastwood", "Flint Beastwood")]
    public void DiscordPresenceService_ResolveHero_ResolvesStandardHeroes(string input, string expectedKey, string expectedName)
    {
        (string AssetKey, string DisplayName)? result = DiscordPresenceService.ResolveHero(input);

        Assert.NotNull(result);
        Assert.Equal(expectedKey, result.Value.AssetKey);
        Assert.Equal(expectedName, result.Value.DisplayName);
    }

    [Theory]
    [InlineData("Hero_Rocky", "hero_rocky", "Pebbles")]
    [InlineData("Hero_Hiro", "hero_hiro", "Swiftblade")]
    [InlineData("Hero_Kunas", "hero_kunas", "Thunderbringer")]
    [InlineData("Hero_DwarfMagi", "hero_dwarf_magi", "Blacksmith")]
    [InlineData("Hero_Dwarf_Magi", "hero_dwarf_magi", "Blacksmith")]
    [InlineData("Hero_Babayaga", "hero_babayaga", "Wretched Hag")]
    [InlineData("Hero_Bephelgor", "hero_bephelgor", "Balphagore")]
    [InlineData("Hero_Taint", "hero_taint", "Gravekeeper")]
    [InlineData("Hero_Fade", "hero_fade", "Fayde")]
    [InlineData("Hero_Frosty", "hero_frosty", "Glacius")]
    [InlineData("Hero_Treant", "hero_treant", "Keeper of the Forest")]
    [InlineData("Hero_Wolfman", "hero_wolfman", "War Beast")]
    [InlineData("Hero_Mumra", "hero_mumra", "Pharaoh")]
    [InlineData("Hero_Scar", "hero_scar", "The Madman")]
    [InlineData("Hero_Ebulus", "hero_ebulus", "Slither")]
    [InlineData("Hero_Fairy", "hero_fairy", "Nymphora")]
    [InlineData("Hero_Hunter", "hero_hunter", "Blood Hunter")]
    [InlineData("Hero_Voodoo", "hero_voodoo", "Voodoo Jester")]
    [InlineData("Hero_Krixi", "hero_krixi", "Moon Queen")]
    [InlineData("Hero_Dreadknight", "hero_dreadknight", "Lord Salforis")]
    [InlineData("Hero_Diseasedrider", "hero_diseasedrider", "Plague Rider")]
    public void DiscordPresenceService_ResolveHero_ResolvesAliasesAndInternalSlugsToInGameDisplayNames(string input, string expectedKey,
        string expectedName)
    {
        (string AssetKey, string DisplayName)? result = DiscordPresenceService.ResolveHero(input);

        Assert.NotNull(result);
        Assert.Equal(expectedKey, result.Value.AssetKey);
        Assert.Equal(expectedName, result.Value.DisplayName);
    }

    [Theory]
    [InlineData("SpawnHero Hero_Devourer", "hero_devourer", "Devourer")]
    [InlineData("SelectHero Hero_Pyromancer", "hero_pyromancer", "Pyromancer")]
    [InlineData("Spawning Hero_WitchSlayer for client 0", "hero_witch_slayer", "Witch Slayer")]
    [InlineData("Hero: Hero_Rocky", "hero_rocky", "Pebbles")]
    [InlineData("Selected hero: Hero_Chronos", "hero_chronos", "Chronos")]
    [InlineData("hero_id, STRING: Hero_Hiro", "hero_hiro", "Swiftblade")]
    [InlineData("KILL time:170900 target:\"Creep_LegionMelee\" attacker:\"Hero_Engineer\"", "hero_engineer",
        "Engineer")]
    [InlineData(
        "DAMAGE player:0 team:2 attacker:\"Hero_Engineer\" inflictor:\"Projectile_Engineer_Attack\" damage:585.26",
        "hero_engineer", "Engineer")]
    [InlineData("PLAYER_SELECT player:0 hero:\"Hero_Engineer\"", "hero_engineer", "Engineer")]
    [InlineData("ABILITY_UPGRADE time:0 player:0 team:1 name:\"Ability_Engineer1\" level:1 slot:0", "hero_engineer",
        "Engineer")]
    public void DiscordPresenceService_TryDetectHeroFromLog_DetectsPicksAndSpawns(string line, string expectedKey, string expectedName)
    {
        (string AssetKey, string DisplayName)? result = DiscordPresenceService.TryDetectHeroFromLog(line);

        Assert.NotNull(result);
        Assert.Equal(expectedKey, result.Value.AssetKey);
        Assert.Equal(expectedName, result.Value.DisplayName);
    }

    [Fact]
    public void DiscordPresenceService_ProcessConsoleLogLine_TracksIdentityAndHeroProgression()
    {
        using DiscordPresenceService service = new();

        // 1. Player Login
        service.ProcessConsoleLogLine("Login success: #59 guccidonut hash12345");

        // 2. Queue for Match
        service.ProcessConsoleLogLine("Entering matchmaking pool: Mid Wars");

        // 3. Hero Selection & Spawn in Match
        service.ProcessConsoleLogLine("SpawnHero Hero_Devourer");

        // 4. Return to Main Menu / Disconnect
        service.ProcessConsoleLogLine("Disconnected from game server. Returning to lobby.");
    }

    [Fact]
    public void ProcessConsoleLogLine_PlayerSlotIsolation_IgnoresOpponentHeroPicks()
    {
        using DiscordPresenceService service = new();

        // 1. Login
        service.ProcessConsoleLogLine("Login success: #59 guccidonut hash12345");

        // 2. Match Connect with multiple players
        service.ProcessConsoleLogLine("PLAYER_CONNECT player:0 name:\"guccidonut\" id:59 psr:1500.0000");
        service.ProcessConsoleLogLine("PLAYER_CONNECT player:1 name:\"OpponentBot\" id:60 psr:1500.0000");

        // 3. Player 0 picks Nomad, Player 1 randoms Defiler
        service.ProcessConsoleLogLine("PLAYER_SELECT player:0 hero:\"Hero_Nomad\"");
        service.ProcessConsoleLogLine("PLAYER_RANDOM player:1 hero:\"Hero_Defiler\"");

        // 4. Opponent actions/abilities should not hijack presence
        service.ProcessConsoleLogLine("ABILITY_UPGRADE time:0 player:1 name:\"Ability_Defiler1\" level:1 slot:0");
        service.ProcessConsoleLogLine("EXP_EARNED time:0 player:1 experience:340.00 source:\"Hero_Defiler\" owner:1");

        // 5. Game End resets state cleanly
        service.ProcessConsoleLogLine("GAME_END");
    }

    [Fact]
    public void GetMapAssetKeyFromGameMode_TeamDeathmatch_ReturnsMapTeamDeathmatch()
    {
        string key1 = DiscordPresenceService.GetMapAssetKeyFromGameMode("Team Deathmatch");
        string key2 = DiscordPresenceService.GetMapAssetKeyFromGameMode("team_deathmatch");
        string key3 = DiscordPresenceService.GetMapAssetKeyFromGameMode("Playing Team Deathmatch");

        Assert.Equal("map_teamdeathmatch", key1);
        Assert.Equal("map_teamdeathmatch", key2);
        Assert.Equal("map_teamdeathmatch", key3);
    }

    [Fact]
    public void ProcessConsoleLogLine_ClientOutputHeroPick_TracksPyromancer()
    {
        using DiscordPresenceService service = new();

        // 1. Voice Identity
        service.ProcessConsoleLogLine("Sv: [13:38:14] Voice: Adding client num 0, guccidonut, ID: 1");

        // 2. Team switch
        service.ProcessConsoleLogLine("Sv: [13:38:14] Client #0 : guccidonut : switched to Team 1 (Legion)");

        // 3. Client Hero Pick
        service.ProcessConsoleLogLine("Sv: [13:38:14] Client #0 selected hero Hero_Pyromancer");
    }

    [Fact]
    public void ProcessConsoleLogLine_ResourceLoadingAndAvatarSkins_IgnoredDuringStartup()
    {
        using DiscordPresenceService service = new();

        // 1. Initial Login
        service.ProcessConsoleLogLine("Login success: #59 guccidonut hash12345");

        // 2. Resource definitions streamed during startup should be ignored
        service.ProcessConsoleLogLine(
            "Resource: [16:07:46] Loading Entity definition: /heroes/bubbles/alt12/state_day_set.entity");
        service.ProcessConsoleLogLine(
            "Resource: [16:07:46] Loading Entity definition: /heroes/calamity/ability_01/ability.entity");
        service.ProcessConsoleLogLine(
            "['Hero_Andromeda.soccer_skin'] = { key = 'Hero_Andromeda.soccer_skin', icon = '/heroes/andromeda/icon.tga' }");
        service.ProcessConsoleLogLine("[MODS_______] Discovered and cataloged 180 mods and hero avatar skins.");

        // 3. Game mode should not be corrupted by soccer skin string
        string mode = DiscordPresenceService.GetMapAssetKeyFromGameMode("Hero_Andromeda.soccer_skin");
        Assert.Equal("large", mode);
    }

    [Theory]
    [InlineData("StartGame practice GameName map:caldavar allheroes:true", "Practice Match")]
    [InlineData("StartPractice", "Practice Match")]
    [InlineData("PracticeGame", "Practice Match")]
    [InlineData("mode:practice", "Practice Match")]
    [InlineData("StartGame botmatch GameName map:midwars", "Bot Match")]
    [InlineData("LocalBotsGame", "Bot Match")]
    [InlineData("mode:botmatch", "Bot Match")]
    [InlineData("StartBotMatch", "Bot Match")]
    public void DiscordPresenceService_DetectMatchModeFromLog_DetectsPracticeAndBotMatch(string line, string expectedMode)
    {
        string? result = DiscordPresenceService.DetectMatchModeFromLog(line);
        Assert.Equal(expectedMode, result);
    }

    [Theory]
    [InlineData("StartGame practice GameName", "Forests of Caldavar")]
    [InlineData("StartGame botmatch GameName", "Forests of Caldavar")]
    [InlineData("StartGame botmatch GameName map:midwars", "Mid Wars")]
    [InlineData("StartGame practice GameName map:caldavar", "Forests of Caldavar")]
    public void DiscordPresenceService_DetectGameModeFromLog_DefaultsToCaldavar_WhenPracticeOrBotMatch(string line, string expectedMap)
    {
        string result = DiscordPresenceService.DetectGameModeFromLog(line, string.Empty);
        Assert.Equal(expectedMap, result);
    }

    [Theory]
    [InlineData("Practice Match", "map_caldavar")]
    [InlineData("Bot Match", "map_caldavar")]
    [InlineData("Bot Match - Mid Wars", "map_midwars")]
    [InlineData("Practice Match - Mid Wars", "map_midwars")]
    public void DiscordPresenceService_GetMapAssetKeyFromGameMode_ResolvesPracticeAndBotMatchAssets(string mode, string expectedAsset)
    {
        string asset = DiscordPresenceService.GetMapAssetKeyFromGameMode(mode);
        Assert.Equal(expectedAsset, asset);
    }

    [Fact]
    public void DiscordPresenceService_PracticeMatch_HeroSelectionAndActiveGameplay_TracksOptionB()
    {
        using DiscordPresenceService service = new();

        // 1. Login
        service.ProcessConsoleLogLine("Login success: #59 guccidonut hash12345");
        Assert.Equal("guccidonut", service.AccountName);

        // 2. Start Practice match
        service.ProcessConsoleLogLine("StartGame practice GameName map:caldavar allheroes:true");
        Assert.Equal("Practice Match", service.CurrentMatchMode);
        Assert.NotNull(service.CurrentPresence);
        Assert.Equal("In Practice Match", service.CurrentPresence.Details);
        Assert.Equal("guccidonut (Connecting)", service.CurrentPresence.State);
        Assert.Equal("map_caldavar", service.CurrentPresence.Assets?.LargeImageKey);
        Assert.Equal("Forests of Caldavar", service.CurrentPresence.Assets?.LargeImageText);

        // 3. World / map loading -> Hero Selection
        service.ProcessConsoleLogLine("Loading map: caldavar");
        Assert.Equal("Practice Match", service.CurrentMatchMode);
        Assert.Equal("Practice Match (Hero Selection)", service.CurrentPresence?.Details);
        Assert.Equal("guccidonut (Hero Selection)", service.CurrentPresence?.State);

        // 4. Hero pick / spawn
        service.ProcessConsoleLogLine("SpawnHero Hero_Pyromancer");
        Assert.Equal("Practice Match", service.CurrentMatchMode);
        Assert.Equal("Pyromancer", service.SelectedHeroDisplayName);
        Assert.Equal("hero_pyromancer", service.SelectedHeroAssetKey);
        Assert.NotNull(service.CurrentPresence);
        Assert.Equal("Practice Match", service.CurrentPresence.Details);
        Assert.Equal("guccidonut as Pyromancer", service.CurrentPresence.State);
        Assert.Equal("map_caldavar", service.CurrentPresence.Assets?.LargeImageKey);
        Assert.Equal("Forests of Caldavar", service.CurrentPresence.Assets?.LargeImageText);
        Assert.Equal("hero_pyromancer", service.CurrentPresence.Assets?.SmallImageKey);
        Assert.Equal("Pyromancer (guccidonut)", service.CurrentPresence.Assets?.SmallImageText);
    }

    [Fact]
    public void DiscordPresenceService_PracticeMatch_MorphHero_UpdatesHeroIconAndState()
    {
        using DiscordPresenceService service = new();

        // 1. Login & Enter Practice Match
        service.ProcessConsoleLogLine("Login success: #59 guccidonut hash12345");
        service.ProcessConsoleLogLine("StartGame practice GameName map:caldavar allheroes:true");
        service.ProcessConsoleLogLine("SpawnHero Hero_Pyromancer");
        Assert.Equal("guccidonut as Pyromancer", service.CurrentPresence?.State);
        Assert.Equal("hero_pyromancer", service.CurrentPresence?.Assets?.SmallImageKey);

        // 2. Morph to Devourer in practice mode
        service.ProcessConsoleLogLine("Morph 0 Hero_Devourer");
        Assert.Equal("Practice Match", service.CurrentMatchMode);
        Assert.Equal("Devourer", service.SelectedHeroDisplayName);
        Assert.Equal("hero_devourer", service.SelectedHeroAssetKey);
        Assert.NotNull(service.CurrentPresence);
        Assert.Equal("Practice Match", service.CurrentPresence.Details);
        Assert.Equal("guccidonut as Devourer", service.CurrentPresence.State);
        Assert.Equal("hero_devourer", service.CurrentPresence.Assets?.SmallImageKey);
        Assert.Equal("Devourer (guccidonut)", service.CurrentPresence.Assets?.SmallImageText);
    }

    [Fact]
    public void DiscordPresenceService_BotMatch_SlotIsolation_OpponentBotPicksDoNotOverwrite()
    {
        using DiscordPresenceService service = new();

        // 1. Login
        service.ProcessConsoleLogLine("Login success: #59 guccidonut hash12345");

        // 2. Start Bot Match on Mid Wars
        service.ProcessConsoleLogLine("StartGame botmatch GameName map:midwars");
        Assert.Equal("Bot Match", service.CurrentMatchMode);
        Assert.Equal("Mid Wars", service.CurrentGameMode);
        Assert.Equal("map_midwars", service.CurrentPresence?.Assets?.LargeImageKey);
        Assert.Equal("Mid Wars", service.CurrentPresence?.Assets?.LargeImageText);

        // 3. Player (slot 0) picks Witch Slayer
        service.ProcessConsoleLogLine("PLAYER_SELECT player:0 hero:\"Hero_WitchSlayer\"");
        Assert.Equal("Bot Match", service.CurrentMatchMode);
        Assert.Equal("Witch Slayer", service.SelectedHeroDisplayName);
        Assert.Equal("hero_witch_slayer", service.SelectedHeroAssetKey);
        Assert.NotNull(service.CurrentPresence);
        Assert.Equal("Bot Match", service.CurrentPresence.Details);
        Assert.Equal("guccidonut as Witch Slayer", service.CurrentPresence.State);
        Assert.Equal("map_midwars", service.CurrentPresence.Assets?.LargeImageKey);
        Assert.Equal("hero_witch_slayer", service.CurrentPresence.Assets?.SmallImageKey);

        // 4. Opponent bot (slot 1) picks and morphs
        service.ProcessConsoleLogLine("PLAYER_SELECT player:1 hero:\"Hero_Chronos\"");
        service.ProcessConsoleLogLine("Morph 1 Hero_Devourer");

        // Player's hero presence MUST NOT be overwritten by the bot
        Assert.Equal("Witch Slayer", service.SelectedHeroDisplayName);
        Assert.Equal("hero_witch_slayer", service.SelectedHeroAssetKey);
        Assert.Equal("guccidonut as Witch Slayer", service.CurrentPresence.State);
        Assert.Equal("hero_witch_slayer", service.CurrentPresence.Assets?.SmallImageKey);
    }

    [Fact]
    public void DiscordPresenceService_PracticeMatch_Disconnect_ResetsPresenceToLobby()
    {
        using DiscordPresenceService service = new();

        service.ProcessConsoleLogLine("Login success: #59 guccidonut hash12345");
        service.ProcessConsoleLogLine("StartGame practice GameName map:caldavar allheroes:true");
        service.ProcessConsoleLogLine("SpawnHero Hero_Pyromancer");
        Assert.Equal("Practice Match", service.CurrentMatchMode);

        // Disconnect
        service.ProcessConsoleLogLine("Disconnected from server");
        Assert.Null(service.CurrentMatchMode);
        Assert.Null(service.SelectedHeroDisplayName);
        Assert.Null(service.SelectedHeroAssetKey);
        Assert.NotNull(service.CurrentPresence);
        Assert.Equal("In Main Menu", service.CurrentPresence.Details);
        Assert.Equal("guccidonut (In Lobby)", service.CurrentPresence.State);
    }

    [Fact]
    public void DiscordPresenceService_NexusTelemetry_PracticeMatch_UpdatesPresence()
    {
        using DiscordPresenceService service = new();

        service.ProcessConsoleLogLine("Login success: #59 guccidonut hash12345");

        // Telemetry packet for Practice match with stats
        service.ProcessConsoleLogLine("[NEXUS_TELEMETRY] player=guccidonut;hero=Hero_Empath;mode=Practice;map=caldavar;status=Playing;k=3;d=0;a=5");

        Assert.Equal("Practice Match", service.CurrentMatchMode);
        Assert.Equal("Empath", service.SelectedHeroDisplayName);
        Assert.Equal("hero_empath", service.SelectedHeroAssetKey);
        Assert.NotNull(service.CurrentPresence);
        Assert.Equal("Practice Match • 3/0/5", service.CurrentPresence.Details);
        Assert.Equal("guccidonut as Empath", service.CurrentPresence.State);
        Assert.Equal("map_caldavar", service.CurrentPresence.Assets?.LargeImageKey);
        Assert.Equal("hero_empath", service.CurrentPresence.Assets?.SmallImageKey);
    }

    [Fact]
    public void DiscordPresenceService_LivePracticeMatchLogSequence_DetectsMidWarsAndRampage()
    {
        using DiscordPresenceService service = new();

        service.ProcessConsoleLogLine("Login success: #59 guccidonut hash12345");

        // Simulate the exact log sequence from the live practice match on dedicated server port 11235
        service.ProcessConsoleLogLine("Cl: [10:59:46] port, STRING: 11235");
        service.ProcessConsoleLogLine("Cl: [10:59:46] Client loading world \"midwars\"");

        Assert.Equal("Practice Match", service.CurrentMatchMode);
        Assert.Equal("Mid Wars", service.CurrentGameMode);

        // Simulate hero selection guide/cvar log from live extensions console.log
        service.ProcessConsoleLogLine("[11:01:12]   hero_cli_name, STRING: Hero_Rampage");
        service.ProcessConsoleLogLine("[11:01:12]   hero_name, STRING: Rampage");

        Assert.Equal("Practice Match", service.CurrentMatchMode);
        Assert.Equal("Mid Wars", service.CurrentGameMode);
        Assert.Equal("Rampage", service.SelectedHeroDisplayName);
        Assert.Equal("hero_rampage", service.SelectedHeroAssetKey);

        Assert.NotNull(service.CurrentPresence);
        Assert.Equal("Practice Match", service.CurrentPresence.Details);
        Assert.Equal("guccidonut as Rampage", service.CurrentPresence.State);
        Assert.Equal("map_midwars", service.CurrentPresence.Assets?.LargeImageKey);
        Assert.Equal("Mid Wars", service.CurrentPresence.Assets?.LargeImageText);
        Assert.Equal("hero_rampage", service.CurrentPresence.Assets?.SmallImageKey);
        Assert.Equal("Rampage (guccidonut)", service.CurrentPresence.Assets?.SmallImageText);

        // Client shutdown / disconnect resets back to lobby
        service.ProcessConsoleLogLine("Cl: [11:01:20] Sending disconnect notice...");
        Assert.Null(service.CurrentMatchMode);
        Assert.Null(service.SelectedHeroDisplayName);
        Assert.Null(service.SelectedHeroAssetKey);
        Assert.Equal("In Main Menu", service.CurrentPresence.Details);
        Assert.Equal("guccidonut (In Lobby)", service.CurrentPresence.State);
    }

    [Theory]
    [InlineData("Cl: [10:59:46] port, STRING: 11235", "Practice Match")]
    [InlineData("Connecting to 127.0.0.1:11235", "Practice Match")]
    [InlineData("SysBar_LaunchPractice('midwars', false)", "Practice Match")]
    [InlineData("StartGame practice GameName map:midwars", "Practice Match")]
    [InlineData("StartGame practice LocalBotsGame mode:botmatch map:caldavar", "Bot Match")]
    [InlineData("AddFakePlayer 1", "Bot Match")]
    [InlineData("ui_local_bot_game 1", "Bot Match")]
    [InlineData("randombots:4|5", "Bot Match")]
    public void DiscordPresenceService_DetectMatchModeFromLog_RecognizesDedicatedPortAndBotIndicators(string logLine, string expectedMode)
    {
        string? result = DiscordPresenceService.DetectMatchModeFromLog(logLine);
        Assert.Equal(expectedMode, result);
    }

    [Fact]
    public void DiscordPresenceService_PersistentMatchTimers_DoesNotResetOnSubsequentEvents()
    {
        using DiscordPresenceService service = new();

        service.ProcessConsoleLogLine("Login success: #59 guccidonut hash12345");
        service.ProcessConsoleLogLine("StartGame practice GameName map:caldavar allheroes:true");
        service.ProcessConsoleLogLine("SpawnHero Hero_Pyromancer");

        // Active gameplay triggers match timer anchor
        service.ProcessConsoleLogLine("NETCMD_CLIENT_IN_GAME");

        Assert.NotNull(service.MatchStartUtc);
        Assert.Null(service.QueueStartUtc);
        Assert.NotNull(service.CurrentPresence?.Timestamps?.Start);
        DateTime initialStart = service.CurrentPresence.Timestamps.Start.Value;
        Assert.Equal(service.MatchStartUtc.Value, initialStart);

        // Subsequent game events (ability upgrade, hero morph, telemetry packet)
        service.ProcessConsoleLogLine("ABILITY_UPGRADE time:100 player:0 team:1 name:\"Ability_Pyromancer1\" level:2 slot:0");
        Assert.NotNull(service.CurrentPresence?.Timestamps?.Start);
        Assert.Equal(initialStart, service.CurrentPresence.Timestamps.Start.Value);
        Assert.Equal(initialStart, service.MatchStartUtc.Value);

        service.ProcessConsoleLogLine("Morph 0 Hero_Devourer");
        Assert.NotNull(service.CurrentPresence?.Timestamps?.Start);
        Assert.Equal(initialStart, service.CurrentPresence.Timestamps.Start.Value);

        service.ProcessConsoleLogLine("[NEXUS_TELEMETRY] player=guccidonut;hero=Hero_Devourer;mode=Practice;map=caldavar;status=Playing;k=5;d=1;a=2");
        Assert.NotNull(service.CurrentPresence?.Timestamps?.Start);
        Assert.Equal(initialStart, service.CurrentPresence.Timestamps.Start.Value);
        Assert.Equal(initialStart, service.MatchStartUtc.Value);
    }

    [Fact]
    public void DiscordPresenceService_QueueTimer_PreservedDuringQueueingAndClearedOnMatch()
    {
        using DiscordPresenceService service = new();

        service.ProcessConsoleLogLine("Login success: #59 guccidonut hash12345");

        // Enter queue
        service.ProcessConsoleLogLine("Entering matchmaking pool: Mid Wars");
        Assert.NotNull(service.QueueStartUtc);
        Assert.Null(service.MatchStartUtc);
        Assert.NotNull(service.CurrentPresence?.Timestamps?.Start);
        DateTime initialQueueStart = service.CurrentPresence.Timestamps.Start.Value;
        Assert.Equal(service.QueueStartUtc.Value, initialQueueStart);

        // Ongoing queue updates
        service.ProcessConsoleLogLine("Matchmaking update: 10 players searching");
        Assert.NotNull(service.CurrentPresence?.Timestamps?.Start);
        Assert.Equal(initialQueueStart, service.CurrentPresence.Timestamps.Start.Value);
        Assert.Equal(initialQueueStart, service.QueueStartUtc.Value);

        // Transition to Hero Picking Phase
        service.ProcessConsoleLogLine("Entering hero picking phase");
        Assert.Null(service.QueueStartUtc);

        // Spawn hero and start match
        service.ProcessConsoleLogLine("SpawnHero Hero_Pyromancer");
        service.ProcessConsoleLogLine("NETCMD_CLIENT_IN_GAME");
        Assert.Null(service.QueueStartUtc);
        Assert.NotNull(service.MatchStartUtc);
    }

    [Fact]
    public void DiscordPresenceService_DynamicPartySize_UpdatesAcrossSignalRAndLogs()
    {
        using DiscordPresenceService service = new();

        service.ProcessConsoleLogLine("Login success: #59 guccidonut hash12345");
        Assert.Equal(1, service.PartySize);
        Assert.Equal(5, service.PartyMax);

        // 1. Direct UpdateParty invocation
        service.UpdateParty(3, 5, "party_alpha");
        Assert.Equal(3, service.PartySize);
        Assert.Equal(5, service.PartyMax);
        Assert.Equal("party_alpha", service.PartyId);
        Assert.Equal(3, service.CurrentPresence?.Party?.Size);
        Assert.Equal(5, service.CurrentPresence?.Party?.Max);
        Assert.Equal("party_alpha", service.CurrentPresence?.Party?.ID);

        // 2. Console log party_size cvar
        service.ProcessConsoleLogLine("party_size, INT: 4");
        Assert.Equal(4, service.PartySize);
        Assert.Equal(4, service.CurrentPresence?.Party?.Size);

        // 3. Console log slash format (e.g. "Party: 2/5")
        service.ProcessConsoleLogLine("Party: 2/5");
        Assert.Equal(2, service.PartySize);
        Assert.Equal(5, service.PartyMax);
        Assert.Equal(2, service.CurrentPresence?.Party?.Size);

        // 4. SignalR PresenceDto update
        service.ProcessPresenceUpdate(new PresenceDto
        {
            AccountId = 59,
            State = "In Lobby",
            Details = "In Main Menu",
            PartySize = 4,
            PartyMax = 5,
            PartyId = "party_sig_99"
        });
        Assert.Equal(4, service.PartySize);
        Assert.Equal(5, service.PartyMax);
        Assert.Equal("party_sig_99", service.PartyId);
        Assert.Equal(4, service.CurrentPresence?.Party?.Size);
        Assert.Equal("party_sig_99", service.CurrentPresence?.Party?.ID);

        // 5. Party leave log resets to solo (solo omits Discord Party metadata)
        service.ProcessConsoleLogLine("Left party");
        Assert.Equal(1, service.PartySize);
        Assert.Equal(5, service.PartyMax);
        Assert.Null(service.CurrentPresence?.Party);
    }

    [Fact]
    public void DiscordPresenceService_Disconnect_ClearsTimersAndResetsParty()
    {
        using DiscordPresenceService service = new();

        service.ProcessConsoleLogLine("Login success: #59 guccidonut hash12345");
        service.ProcessConsoleLogLine("StartGame practice GameName map:caldavar allheroes:true");
        service.ProcessConsoleLogLine("SpawnHero Hero_Pyromancer");
        service.ProcessConsoleLogLine("NETCMD_CLIENT_IN_GAME");
        service.UpdateParty(3, 5, "party_temp");

        Assert.NotNull(service.MatchStartUtc);
        Assert.Equal(3, service.PartySize);

        // Disconnect from game server
        service.ProcessConsoleLogLine("Disconnected from game server. Returning to lobby.");

        Assert.Null(service.MatchStartUtc);
        Assert.Null(service.QueueStartUtc);
        Assert.Equal("In Main Menu", service.CurrentPresence?.Details);
        Assert.Equal("guccidonut (In Lobby)", service.CurrentPresence?.State);

        // Disband party resets party size to solo (solo omits Discord Party metadata)
        service.ProcessConsoleLogLine("Party disbanded");
        Assert.Equal(1, service.PartySize);
        Assert.Equal(5, service.PartyMax);
        Assert.Null(service.CurrentPresence?.Party);
    }

    [Fact]
    public void ProcessConsoleLogLine_StartupGlobalsAndInterfaceLoads_DoNotTriggerCaptureTheFlagOrBotMatch()
    {
        using DiscordPresenceService service = new();

        // 1. Login in main menu
        service.ProcessConsoleLogLine("Login success: #59 guccidonut hash12345");
        Assert.Equal("guccidonut", service.AccountName);
        Assert.Null(service.CurrentMatchMode);
        Assert.Equal(string.Empty, service.CurrentGameMode);
        Assert.Equal("In Main Menu", service.CurrentPresence?.Details);
        Assert.Equal("guccidonut (In Lobby)", service.CurrentPresence?.State);
        Assert.Equal("large", service.CurrentPresence?.Assets?.LargeImageKey);
        Assert.Equal("Heroes of Newerth", service.CurrentPresence?.Assets?.LargeImageText);

        // 2. Startup globals dump containing substring collisions ("SetHeroSelectFilters" -> "ctF", "RewardStat_CTFCalculation", "SetBotDifficulty", "StartGame")
        service.ProcessConsoleLogLine("[01:44:22] [NEXUS_GLOBALS] RewardStat_CTFCalculation, SetBotDifficulty, SetHeroSelectFavoriteHeroes, SetHeroSelectFilters, StartGame");
        service.ProcessConsoleLogLine("Loading Interface '/ui/fe2/main_v2.interface'");
        service.ProcessConsoleLogLine("Loading Interface '/ui/fe2/store2.interface'");
        service.ProcessConsoleLogLine("[MAIN-ACCOUNT] Switched subaccount to guccidonut");

        // Must remain in Main Menu with the standard Heroes of Newerth logo, never Capture the Flag or Bot Match
        Assert.Null(service.CurrentMatchMode);
        Assert.Equal(string.Empty, service.CurrentGameMode);
        Assert.Equal("In Main Menu", service.CurrentPresence?.Details);
        Assert.Equal("guccidonut (In Lobby)", service.CurrentPresence?.State);
        Assert.Equal("large", service.CurrentPresence?.Assets?.LargeImageKey);
        Assert.Equal("Heroes of Newerth", service.CurrentPresence?.Assets?.LargeImageText);
    }

    [Theory]
    [InlineData("SetHeroSelectFilters", "")]
    [InlineData("RewardStat_CTFCalculation", "")]
    [InlineData("SelectFavorite", "")]
    [InlineData("[NEXUS_GLOBALS] StartGame, SetBotDifficulty, SetHeroSelectFilters", "")]
    [InlineData("Loading map: capturetheflag", "Capture the Flag")]
    [InlineData("Entering matchmaking pool: CTF", "Capture the Flag")]
    public void DetectGameModeFromLog_SubstringCollisions_DoNotFalsePositiveCaptureTheFlag(string line, string expectedMode)
    {
        string result = DiscordPresenceService.DetectGameModeFromLog(line, string.Empty);
        Assert.Equal(expectedMode, result);
    }

    [Fact]
    public void ProcessPresenceUpdate_OnlineInMenusIdle_NormalizesToInMainMenuWithLargeLogo()
    {
        using DiscordPresenceService service = new();
        service.ProcessConsoleLogLine("Login success: #59 guccidonut hash12345");

        service.ProcessPresenceUpdate(new PresenceDto
        {
            AccountId = 59,
            State = "Online in Menus",
            Details = "Idle",
            LargeImageKey = "large",
            SmallImageKey = "small"
        });

        Assert.NotNull(service.CurrentPresence);
        Assert.Equal("In Main Menu", service.CurrentPresence.Details);
        Assert.Equal("guccidonut (In Lobby)", service.CurrentPresence.State);
        Assert.Equal("large", service.CurrentPresence.Assets?.LargeImageKey);
        Assert.Equal("Heroes of Newerth", service.CurrentPresence.Assets?.LargeImageText);
    }

    [Fact]
    public void All139SharedHeroes_ResolveToValidDiscordPortalAssetKeys()
    {
        // protects: Complete 139-hero parity between ASPIRE.Common.Constants.Heroes and DiscordPresenceService.ResolveHero + ValidPortalAssetKeys.
        // fails_when: Any hero in Heroes.AllHeroIdentifiers() fails to resolve or maps to a slug missing from ValidPortalAssetKeys (e.g. Hero_Bombardier, Hero_Gwynplaine, Hero_Electrician, Hero_PollywogPriest, Hero_Yogi).
        // seam: Heroes.AllHeroIdentifiers() -> DiscordPresenceService.ResolveHero -> DiscordPresenceService.ValidPortalAssetKeys.

        List<string> allIds = Heroes.AllHeroIdentifiers();
        Assert.True(allIds.Count >= 139, $"Expected at least 139 shared heroes, got {allIds.Count}.");

        foreach (string heroId in allIds)
        {
            (string AssetKey, string DisplayName)? resolved = DiscordPresenceService.ResolveHero(heroId);
            Assert.True(resolved.HasValue, $"Hero '{heroId}' returned null from DiscordPresenceService.ResolveHero.");
            Assert.True(
                DiscordPresenceService.ValidPortalAssetKeys.Contains(resolved.Value.AssetKey),
                $"Hero '{heroId}' resolved to '{resolved.Value.AssetKey}' which is missing from ValidPortalAssetKeys.");
            Assert.False(string.IsNullOrWhiteSpace(resolved.Value.DisplayName), $"Hero '{heroId}' resolved to blank DisplayName.");
        }

        Assert.Equal(("hero_bomb", "Bombardier"), DiscordPresenceService.ResolveHero("Hero_Bombardier"));
        Assert.Equal(("hero_electrician", "Electrician"), DiscordPresenceService.ResolveHero("Hero_Electrician"));
        Assert.Equal(("hero_gwynplaine", "Gwynplaine"), DiscordPresenceService.ResolveHero("Hero_Gwynplaine"));
        Assert.Equal(("hero_pollywogpriest", "Pollywog Priest"), DiscordPresenceService.ResolveHero("Hero_PollywogPriest"));
        Assert.Equal(("hero_yogi", "Wildsoul"), DiscordPresenceService.ResolveHero("Hero_Yogi"));
    }

    [Fact]
    public void AltAvatarModifiersAndInGameLocalizedNames_PreserveBaseHeroPortraitAndShowAvatarInTooltip()
    {
        // protects: Alt Avatar resolution across hero select (.AltN / .pog_skin / aa.Hero_*) and in-game GetViewHeroName() localized avatar names.
        // fails_when: Selecting an Alt Avatar drops SmallImageKey back to 'small' or overwrites _selectedHeroAssetKey with an unmapped localized avatar string like 'Abyssal Bubbles' or 'Pimp Slayer'.
        // seam: DiscordPresenceService.ResolveHero + TryDetectHeroFromLog + ProcessConsoleLogLine ([NEXUS_TELEMETRY]).

        Assert.Equal(("hero_wolfman", "War Beast"), DiscordPresenceService.ResolveHero("Hero_Wolfman.pog_skin"));
        Assert.Equal(("hero_bubbles", "Bubbles"), DiscordPresenceService.ResolveHero("Hero_Bubbles.Alt16"));
        Assert.Equal(("hero_andromeda", "Andromeda"), DiscordPresenceService.ResolveHero("Hero_Andromeda.soccer_skin"));
        Assert.Equal(("hero_mimix", "Xemplar"), DiscordPresenceService.ResolveHero("aa.Hero_Mimix.Alt"));
        Assert.Equal(("hero_witch_slayer", "Witch Slayer"), DiscordPresenceService.TryDetectHeroFromLog("SpawnHero Hero_WitchSlayer.Alt3"));

        using DiscordPresenceService service = new();
        service.ProcessConsoleLogLine("Login success: #59 [PK]guccidonut hash12345");
        service.ProcessConsoleLogLine("[NEXUS_TELEMETRY] hero=Hero_Bubbles.Alt16;avatar=Abyssal Bubbles;player=[PK]guccidonut;mode=midwars;status=HeroSelect");

        Assert.Equal("hero_bubbles", service.SelectedHeroAssetKey);
        Assert.Equal("Bubbles", service.SelectedHeroDisplayName);
        Assert.Equal("Abyssal Bubbles", service.SelectedAvatarDisplayName);
        Assert.Equal("hero_bubbles", service.CurrentPresence?.Assets?.SmallImageKey);
        Assert.Equal("Abyssal Bubbles • Bubbles ([PK]guccidonut)", service.CurrentPresence?.Assets?.SmallImageText);

        // Even if an unpatched mod/script emits GetViewHeroName() ('Abyssal Bubbles') as hero=, base portrait stays locked to hero_bubbles!
        service.ProcessConsoleLogLine("[NEXUS_TELEMETRY] hero=Abyssal Bubbles;player=[PK]guccidonut;mode=midwars;status=InGame;level=11;k=6;d=1;a=9");
        Assert.Equal("hero_bubbles", service.SelectedHeroAssetKey);
        Assert.Equal("hero_bubbles", service.CurrentPresence?.Assets?.SmallImageKey);
        Assert.Equal("guccidonut as Bubbles (Lvl 11)", service.CurrentPresence?.State);
        Assert.Equal("Playing Mid Wars • 6/1/9", service.CurrentPresence?.Details);
        Assert.Contains("Abyssal Bubbles", service.CurrentPresence?.Assets?.SmallImageText ?? string.Empty);
    }

    [Fact]
    public void All35ShippedMaps_ResolveExpectedAssetKeysDisplayTitlesAndLivePortalFallbacks()
    {
        // protects: Complete 35-map coverage (14 existing portal maps + 11 standalone custom maps + 10 variant/10v10/seasonal maps) plus un-uploaded portal asset fallback.
        // fails_when: Any shipped .s2z map falls through to generic 'large' / blank mode, or an un-uploaded custom map renders a broken image box instead of graceful fallback.
        // seam: DiscordPresenceService.DetectGameModeFromLog + GetMapAssetKeyFromGameMode + SetLivePortalAssetKeys.

        (string MapStem, string ExpectedTitle, string ExpectedAssetKey)[] all35Maps =
        [
            ("archers", "Archer Wars", "map_archers"),
            ("arena", "HoN Arena", "map_arena"),
            ("caldavar", "Forests of Caldavar", "map_caldavar"),
            ("caldavar_halloween", "Forests of Caldavar (Halloween)", "map_caldavar"),
            ("caldavar_old", "Forests of Caldavar (Classic)", "map_caldavar"),
            ("caldavar_reborn", "Forests of Caldavar Reborn", "map_caldavar"),
            ("caldavar_wtf", "Forests of Caldavar (WTF)", "map_caldavar"),
            ("capturetheflag", "Capture the Flag", "map_capturetheflag"),
            ("colosseum_duel", "Colosseum Duel", "map_colosseumduel"),
            ("darkwoodvale", "Darkwood Vale", "map_darkwoodvale"),
            ("devowars", "Devo Wars", "map_devowars"),
            ("devowars_10v10", "Devo Wars (10v10 Chaos)", "map_devowars"),
            ("empath_maze", "Empath Maze", "map_empathmaze"),
            ("enfos", "Enfo's Team Survival", "map_enfos"),
            ("grimmscrossing", "Grimm's Crossing", "map_grimmscrossing"),
            ("mids_omg", "Mid Wars Classic", "map_midwars"),
            ("midwars", "Mid Wars", "map_midwars"),
            ("midwars_10v10", "Mid Wars (10v10 Chaos)", "map_midwars"),
            ("midwars_halloween", "Mid Wars (Halloween)", "map_midwars"),
            ("midwars_reborn", "Mid Wars Reborn", "map_midwars"),
            ("nightmarish_coop", "Nightmarish Co-Op", "map_nightmarishcoop"),
            ("pebbles_duel", "Pebbles Duel", "map_pebblesduel"),
            ("persian_pass", "Persian Pass", "map_persianpass"),
            ("prophets", "Prophets", "map_prophets"),
            ("riftwars", "Rift Wars", "map_riftwars"),
            ("soccer", "Strikers", "map_strikers"),
            ("spotlight", "Spotlight", "map_spotlight"),
            ("team_deathmatch", "Team Deathmatch", "map_teamdeathmatch"),
            ("thegrimmhunt", "The Grimm Hunt", "map_thegrimmhunt"),
            ("three_kingdoms", "Three Kingdoms", "map_threekingdoms"),
            ("truedevo", "True Devo Wars", "map_truedevo"),
            ("truedevo_10v10", "True Devo Wars (10v10 Chaos)", "map_truedevo"),
            ("tutorial", "Tutorial", "map_tutorial"),
            ("valkwars", "Valkyrie Wars", "map_valkwars"),
            ("watchtower", "Watchtower", "map_watchtower")
        ];

        Assert.Equal(35, all35Maps.Length);

        foreach ((string stem, string expectedTitle, string expectedAsset) in all35Maps)
        {
            string detectedTitle = DiscordPresenceService.DetectGameModeFromLog($"Loading map: {stem}", string.Empty);
            Assert.Equal(expectedTitle, detectedTitle);
            string resolvedAsset = DiscordPresenceService.GetMapAssetKeyFromGameMode(detectedTitle);
            Assert.Equal(expectedAsset, resolvedAsset);
            Assert.True(DiscordPresenceService.ValidPortalAssetKeys.Contains(resolvedAsset), $"Missing '{resolvedAsset}' in ValidPortalAssetKeys.");
        }

        // Verify live portal manifest fallback: if 'map_truedevo' & 'hero_gwynplaine' are not yet uploaded to Discord Portal,
        // 'map_truedevo' falls back to 'map_devowars' (not 'large') and 'hero_gwynplaine' falls back to 'small', then auto-upgrades once uploaded!
        using DiscordPresenceService service = new();
        service.SetLivePortalAssetKeys(["large", "small", "map_devowars", "hero_bubbles"]);
        service.ProcessConsoleLogLine("Login success: #59 guccidonut hash12345");
        service.ProcessConsoleLogLine("[NEXUS_TELEMETRY] hero=Hero_Gwynplaine;player=guccidonut;mode=truedevo;status=InGame;level=5;k=2;d=0;a=1");

        Assert.Equal("map_devowars", service.CurrentPresence?.Assets?.LargeImageKey);
        Assert.Equal("True Devo Wars", service.CurrentPresence?.Assets?.LargeImageText);
        Assert.Equal("small", service.CurrentPresence?.Assets?.SmallImageKey);

        service.SetLivePortalAssetKeys(["large", "small", "map_devowars", "map_truedevo", "hero_gwynplaine"]);
        Assert.Equal("map_truedevo", service.CurrentPresence?.Assets?.LargeImageKey);
        Assert.Equal("hero_gwynplaine", service.CurrentPresence?.Assets?.SmallImageKey);
    }

    [Fact]
    public void ClanTagFormatting_StripsClanTagFromVisibleState_PreservesFullClanIdentityInSmallImageTooltip()
    {
        // protects: Clean username display (without [CLAN] prefix) on all visible DRP State lines while preserving [CLAN]username inside SmallImageText hover tooltip and AccountName.
        // fails_when: [CLAN] tag leaks onto visible State strings or gets lost from AccountName / SmallImageText tooltip.
        // seam: DiscordPresenceService.SetAccountName + CleanAccountName + ProcessConsoleLogLine.

        using DiscordPresenceService service = new();
        service.ProcessConsoleLogLine("Login success: #59 [KONGOR]guccidonut hash12345");

        Assert.Equal("[KONGOR]guccidonut", service.AccountName);
        Assert.Equal("guccidonut", service.CleanAccountName);
        Assert.True(service.IsLocalPlayerName("guccidonut"));
        Assert.True(service.IsLocalPlayerName("[KONGOR]guccidonut"));
        Assert.Equal("guccidonut (In Lobby)", service.CurrentPresence?.State);
        Assert.Equal("[KONGOR]guccidonut", service.CurrentPresence?.Assets?.SmallImageText);

        service.ProcessConsoleLogLine("Entering matchmaking pool: Mid Wars");
        Assert.Equal("guccidonut in Queue", service.CurrentPresence?.State);
        Assert.Equal("[KONGOR]guccidonut", service.CurrentPresence?.Assets?.SmallImageText);

        service.ProcessConsoleLogLine("SpawnHero Hero_Bubbles");
        Assert.Equal("guccidonut as Bubbles", service.CurrentPresence?.State);
        Assert.Equal("Bubbles ([KONGOR]guccidonut)", service.CurrentPresence?.Assets?.SmallImageText);
    }

    [Fact]
    public void StreamerPrivacyMode_LiveToggle_RedactsAndRestoresHandleClanTagKdaAndParty_WithStatusDisplayDetails()
    {
        // protects: Live Streamer Privacy Mode redaction and un-redaction (handle, clan tag, KDA, Party) plus StatusDisplayType.Details compact member-list rendering.
        // fails_when: Enabling privacy mode leaks username/clan/KDA/Party, or disabling privacy mode fails to restore the original unredacted state.
        // seam: DiscordPresenceService.SetStreamerPrivacyMode + SanitizeAndFormatPresencePayload.

        using DiscordPresenceService service = new();
        service.ProcessConsoleLogLine("Login success: #59 [PK]guccidonut hash12345");
        service.UpdateParty(3, 5, "party_secret_123");
        service.ProcessConsoleLogLine("[NEXUS_TELEMETRY] hero=Hero_Bubbles.Alt16;avatar=Abyssal Bubbles;player=[PK]guccidonut;mode=midwars;status=InGame;level=16;k=12;d=2;a=14");

        Assert.Equal(StatusDisplayType.Details, service.CurrentPresence?.StatusDisplay);
        Assert.Equal("Playing Mid Wars • 12/2/14", service.CurrentPresence?.Details);
        Assert.Equal("guccidonut as Bubbles (Lvl 16)", service.CurrentPresence?.State);
        Assert.Equal("Abyssal Bubbles • Bubbles ([PK]guccidonut)", service.CurrentPresence?.Assets?.SmallImageText);
        Assert.NotNull(service.CurrentPresence?.Party);

        // Toggle Privacy ON -> redacts handle, clan tag, KDA, and party immediately
        service.SetStreamerPrivacyMode(true);
        Assert.Equal(StatusDisplayType.Details, service.CurrentPresence?.StatusDisplay);
        Assert.Equal("Playing Mid Wars", service.CurrentPresence?.Details);
        Assert.Equal("Playing as Bubbles (Lvl 16)", service.CurrentPresence?.State);
        Assert.Equal("Abyssal Bubbles • Bubbles", service.CurrentPresence?.Assets?.SmallImageText);
        Assert.Null(service.CurrentPresence?.Party);

        // Toggle Privacy OFF -> restores full identity, KDA, and party immediately
        service.SetStreamerPrivacyMode(false);
        Assert.Equal("Playing Mid Wars • 12/2/14", service.CurrentPresence?.Details);
        Assert.Equal("guccidonut as Bubbles (Lvl 16)", service.CurrentPresence?.State);
        Assert.Equal("Abyssal Bubbles • Bubbles ([PK]guccidonut)", service.CurrentPresence?.Assets?.SmallImageText);
        Assert.NotNull(service.CurrentPresence?.Party);
        Assert.Equal(3, service.CurrentPresence?.Party?.Size);
    }

    [Fact]
    public void StateMachineHardening_PoolLogsEngineMapTicksSignalRHeartbeatsHostedLobbySpectatingAndNexusAppId()
    {
        // protects: DRP state machine resilience against 'Thread pool'/'Hero pool' false positives, engine map ticks, hero-blind SignalR PresenceUpdated heartbeats, HostedLobby/Spectating states, and NEXUS Control Panel App ID sync.
        // fails_when: 'Thread pool' triggers matchmaking queue, UpdateMapContext wipes hero selection, SignalR overwrites local Hero/Level/KDA with 'In a Game', or NEXUS DiscordClientId fails validation.
        // seam: DiscordPresenceService.ProcessConsoleLogLine + UpdateMapContext + ProcessPresenceUpdate + SetCustomDiscordAppId.

        using DiscordPresenceService service = new();
        Assert.Equal("1003760104199422052", service.ActiveDiscordAppId);
        service.SetCustomDiscordAppId("123456789012345678");
        Assert.Equal("123456789012345678", service.ActiveDiscordAppId);
        service.SetCustomDiscordAppId("not-a-snowflake");
        Assert.Equal("1003760104199422052", service.ActiveDiscordAppId);

        service.ProcessConsoleLogLine("Login success: #59 guccidonut hash12345");

        // 1. Generic 'pool' logs must NOT trigger matchmaking queue
        service.ProcessConsoleLogLine("[K2] Thread pool initialized with 8 worker threads");
        service.ProcessConsoleLogLine("[UI] Hero pool filter refreshed (139 heroes)");
        Assert.Null(service.QueueStartUtc);
        Assert.Equal("In Main Menu", service.CurrentPresence?.Details);

        // 2. HostedLobby & Spectating telemetry states
        service.ProcessConsoleLogLine("[NEXUS_TELEMETRY] mode=pebblesduel;status=HostedLobby");
        Assert.Equal("Custom Lobby (Pebbles Duel)", service.CurrentPresence?.Details);
        Assert.Equal("map_pebblesduel", service.CurrentPresence?.Assets?.LargeImageKey);

        service.ProcessConsoleLogLine("[NEXUS_TELEMETRY] mode=enfos;status=Spectating");
        Assert.Equal("Spectating Enfo's Team Survival", service.CurrentPresence?.Details);
        Assert.Equal("map_enfos", service.CurrentPresence?.Assets?.LargeImageKey);

        // 3. Active match + UpdateMapContext + hero-blind SignalR PresenceUpdated must preserve Hero, Level, and KDA
        service.ProcessConsoleLogLine("[NEXUS_TELEMETRY] hero=Hero_Gwynplaine;player=guccidonut;mode=midwars;status=InGame;level=14;k=8;d=1;a=5");
        service.UpdateMapContext("midwars_10v10");
        Assert.Equal("hero_gwynplaine", service.SelectedHeroAssetKey);
        Assert.Equal("Mid Wars (10v10 Chaos)", service.CurrentPresence?.Assets?.LargeImageText);

        service.ProcessPresenceUpdate(new PresenceDto
        {
            AccountId = 59,
            State = "In a Game",
            Details = "TMM - Mid Wars",
            LargeImageKey = "map_midwars",
            SmallImageKey = "small"
        });

        Assert.Equal("hero_gwynplaine", service.CurrentPresence?.Assets?.SmallImageKey);
        Assert.Equal("guccidonut as Gwynplaine (Lvl 14)", service.CurrentPresence?.State);
        Assert.Contains("8/1/5", service.CurrentPresence?.Details ?? string.Empty);
    }

    [Fact]
    public void FastBus11CharTruncation_NeverClobbersFullAccountName_AndAcceptsStrikersTelemetry()
    {
        // protects: Authoritative full HoN handles (e.g. [GUCI]guccidonut) and in-game [NEXUS_TELEMETRY] hero portrait resolution in Bot Matches.
        // fails_when: KongorTelemetryState.PlayerName[12] 11-char shared-memory truncation ("[GUCI]gucci") overwrites _accountName or causes ProcessNexusTelemetry to reject player=[GUCI]guccidonut as foreign, or avatar=Hero_Empath pollutes the Alt Avatar tooltip.
        // seam: DiscordPresenceService.ProcessConsoleLogLine -> ProcessFastBusState -> ProcessNexusTelemetry.

        using DiscordPresenceService service = new();

        service.ProcessConsoleLogLine("[HOMEPAGE_LUA] AccountInfo watch fired -> accountId: 59, nickname: [GUCI]guccidonut, isLoggedIn: true");
        Assert.Equal("[GUCI]guccidonut", service.AccountName);
        Assert.Equal("guccidonut", service.CleanAccountName);
        Assert.True(service.HasAuthoritativeAccountName);

        // Shared-memory FastBus emits 11-char truncated "[GUCI]gucci" every 500ms; must NEVER downgrade full handle
        service.ProcessFastBusState(gamePhase: 5, activeMatchId: 0, playerName: "[GUCI]gucci", currentMap: "soccer");
        Assert.Equal("[GUCI]guccidonut", service.AccountName);
        Assert.Equal("guccidonut", service.CleanAccountName);

        // Bot match telemetry with base-hero entity token avatar=Hero_Empath + allied bot AltInfo noise
        service.ProcessConsoleLogLine("StartGame local mode:botmatch map:soccer");
        service.ProcessConsoleLogLine("[NEXUS_TELEMETRY] hero=empath;avatar=Hero_Empath;player=[GUCI]guccidonut;mode=soccer;status=InGame;level=5;k=2;d=0;a=4");
        service.ProcessConsoleLogLine("[NEXUS_TELEMETRY] hero=devourer;player=DevourerBot;level=6;mode=soccer;status=InGame");

        Assert.Equal("guccidonut", service.CleanAccountName);
        Assert.Equal("Bot Match • 2/0/4", service.CurrentPresence?.Details);
        Assert.Equal("guccidonut as Empath (Lvl 5)", service.CurrentPresence?.State);
        Assert.Equal("map_strikers", service.CurrentPresence?.Assets?.LargeImageKey);
        Assert.Equal("Strikers", service.CurrentPresence?.Assets?.LargeImageText);
        Assert.Equal("hero_empath", service.CurrentPresence?.Assets?.SmallImageKey);
        Assert.Equal("Empath ([GUCI]guccidonut)", service.CurrentPresence?.Assets?.SmallImageText);
        Assert.Null(service.SelectedAvatarDisplayName);
    }

    [Fact]
    public void FastBus11CharSeedFirst_UpgradesToFullNameOnTelemetryOrPipeIdentity()
    {
        // protects: Live auto-upgrade from provisional 11-char FastBus player prefix ("[GUCI]gucci") when WILLOWMAKER attaches mid-session before login logs.
        // fails_when: IsLocalPlayerName rejects "[GUCI]guccidonut" / "guccidonut" against "[GUCI]gucci", leaving Discord stuck showing "gucci".
        // seam: DiscordPresenceService.ProcessFastBusState -> ProcessConsoleLogLine (pipe + NewUI Lobby_V2 telemetry).

        using DiscordPresenceService service = new();

        service.ProcessFastBusState(gamePhase: 2, activeMatchId: 77, playerName: "[GUCI]gucci", currentMap: "caldavar");
        Assert.False(service.HasAuthoritativeAccountName);
        Assert.Equal("gucci (Hero Selection)", service.CurrentPresence?.State);

        // Authoritative pipe identity with bare handle preserves [GUCI] clan tag while upgrading clean handle to guccidonut
        service.ProcessConsoleLogLine("^g59|guccidonut|5");
        Assert.True(service.HasAuthoritativeAccountName);
        Assert.Equal("[GUCI]guccidonut", service.AccountName);
        Assert.Equal("guccidonut", service.CleanAccountName);
        Assert.Equal("guccidonut (Hero Selection)", service.CurrentPresence?.State);

        // NewUI Lobby_V2 hero lock-in + Alt Avatar selection + repick
        service.ProcessConsoleLogLine("[NEXUS_TELEMETRY] status=HeroSelect;player=[GUCI]guccidonut;mode=caldavar;hero=Hero_Bubbles");
        Assert.Equal("hero_bubbles", service.CurrentPresence?.Assets?.SmallImageKey);
        Assert.Equal("guccidonut as Bubbles", service.CurrentPresence?.State);
        Assert.Equal("Bubbles ([GUCI]guccidonut)", service.CurrentPresence?.Assets?.SmallImageText);

        service.ProcessConsoleLogLine("[NEXUS_TELEMETRY] status=HeroSelect;player=[GUCI]guccidonut;mode=caldavar;hero=Hero_Bubbles.Alt16;avatar=Abyssal Bubbles");
        Assert.Equal("hero_bubbles", service.CurrentPresence?.Assets?.SmallImageKey);
        Assert.Equal("Abyssal Bubbles • Bubbles ([GUCI]guccidonut)", service.CurrentPresence?.Assets?.SmallImageText);

        service.ProcessConsoleLogLine("[NEXUS_TELEMETRY] status=HeroSelect;player=[GUCI]guccidonut;mode=caldavar;repick=1");
        Assert.Equal("small", service.CurrentPresence?.Assets?.SmallImageKey);
        Assert.Equal("guccidonut (Hero Selection)", service.CurrentPresence?.State);
    }

    [Fact]
    public void ConfigPreload_AllowsGuccidonut_AndRejectsKongorBootPlaceholders()
    {
        // protects: Saved login.cfg handle preloading for real accounts while blocking engine boot placeholders ("Kongor", "KongorSrv").
        // fails_when: Hardcoded handle filters reject "guccidonut", or startup "Kongor" placeholder leaks into initial presence.
        // seam: DiscordPresenceService.IsDisallowedPreloadedConfigName and IsPlaceholderPlayerName.

        if (!string.Equals(Environment.UserName, "guccidonut", StringComparison.OrdinalIgnoreCase))
        {
            Assert.False(DiscordPresenceService.IsDisallowedPreloadedConfigName("guccidonut"));
            Assert.False(DiscordPresenceService.IsDisallowedPreloadedConfigName("[GUCI]guccidonut"));
        }

        Assert.True(DiscordPresenceService.IsPlaceholderPlayerName("Kongor"));
        Assert.True(DiscordPresenceService.IsPlaceholderPlayerName("KongorSrv"));
        Assert.True(DiscordPresenceService.IsDisallowedPreloadedConfigName("Kongor"));
        Assert.True(DiscordPresenceService.IsDisallowedPreloadedConfigName("KongorSrv"));
    }

    [Fact]
    public void QueueArcade_ShowsInQueuePreservesElapsedQueueTimer_AndCoalescesBurstWithoutDroppingHero()
    {
        // protects: Queue Arcade Rich Presence contract ("In Queue • <Map>", "<CleanName> as <Hero> (In Queue)", continuous queue Start timestamp) and 4-per-20s Discord IPC burst coalescing.
        // fails_when: "Clearing all state data" resets presence to Main Menu, "[KONGOR-BOTS]" flips Queue Arcade to "Bot Match", hero spawn overwrites QueueStartUtc, or the 5th+ rapid IPC update drops SmallImageKey.
        // seam: DiscordPresenceService.BeginLogBatch/EndLogBatch + ProcessConsoleLogLine + FlushPendingPresenceIfReady.

        using DiscordPresenceService service = new();
        DateTime queueEnteredUtc = new(2026, 11, 15, 8, 0, 0, DateTimeKind.Utc);
        DateTime burstTimeUtc = queueEnteredUtc.AddMinutes(4);

        service.ProcessConsoleLogLine("Login success: #59 [GUCI]guccidonut");
        service.ProcessConsoleLogLine("Entering matchmaking queue");
        service.SetQueueStartTime(queueEnteredUtc);

        // Simulate rapid log batch during Queue Arcade warm-up join
        service.BeginLogBatch();
        try
        {
            service.ProcessConsoleLogLine("Clearing all state data");
            service.ProcessConsoleLogLine("CWorld::StartLoad - devowars_10v10");
            service.ProcessConsoleLogLine("[KONGOR-BOTS] Map 'devowars_10v10' detected -> starting non-5v5 botmatch");
            service.ProcessConsoleLogLine("QUEUE ARCADE #Q2 — Devo Wars (10v10 Chaos) warm-up while you stay in queue!");
            service.ProcessConsoleLogLine("SpawnHero: Hero_Devourer");
            service.ProcessConsoleLogLine("[NEXUS_TELEMETRY] hero=devourer;avatar=Hero_Devourer;player=[GUCI]guccidonut;mode=devowars_10v10;arcade=1;status=InGame;level=3;k=1;d=0;a=2");
        }
        finally
        {
            service.EndLogBatch(burstTimeUtc);
        }

        Assert.True(service.IsQueueArcadeActive);
        Assert.Null(service.CurrentMatchMode);
        Assert.Equal("In Queue • Devo Wars (10v10 Chaos)", service.CurrentPresence?.Details);
        Assert.Equal("guccidonut as Devourer (In Queue)", service.CurrentPresence?.State);
        Assert.Equal("map_devowars", service.CurrentPresence?.Assets?.LargeImageKey);
        Assert.Equal("Queue Arcade • Devo Wars (10v10 Chaos)", service.CurrentPresence?.Assets?.LargeImageText);
        Assert.Equal("hero_devourer", service.CurrentPresence?.Assets?.SmallImageKey);
        Assert.Equal("Devourer ([GUCI]guccidonut)", service.CurrentPresence?.Assets?.SmallImageText);
        Assert.Equal(queueEnteredUtc, service.CurrentPresence?.Timestamps?.Start);

        // Streamer Privacy redacts handle in Queue Arcade while keeping "In Queue as <Hero>" and original queue timer
        service.SetStreamerPrivacyMode(true);
        Assert.Equal("In Queue as Devourer", service.CurrentPresence?.State);
        Assert.Equal("Devourer", service.CurrentPresence?.Assets?.SmallImageText);
        Assert.Equal(queueEnteredUtc, service.CurrentPresence?.Timestamps?.Start);
        service.SetStreamerPrivacyMode(false);

        // Push extra rapid transitions to exceed 4 sends in 20s and prove trailing-edge flush delivers final hero icon
        service.ProcessConsoleLogLine("[NEXUS_TELEMETRY] status=HeroSelect;player=[GUCI]guccidonut;mode=midwars_10v10;arcade=1;hero=Hero_Pebbles");
        service.ProcessConsoleLogLine("[NEXUS_TELEMETRY] status=InGame;player=[GUCI]guccidonut;mode=midwars_10v10;arcade=1;hero=Hero_FlintBeastwood");
        if (service.HasPendingRateLimitedPresence)
        {
            bool flushed = service.FlushPendingPresenceIfReady(DateTime.UtcNow.AddSeconds(21));
            Assert.True(flushed);
        }

        Assert.False(service.HasPendingRateLimitedPresence);
        Assert.Equal("hero_flint_beastwood", service.LastDispatchedIpcPresence?.Assets?.SmallImageKey);
        Assert.Equal("In Queue • Mid Wars (10v10 Chaos)", service.LastDispatchedIpcPresence?.Details);
        Assert.Equal("guccidonut as Flint Beastwood (In Queue)", service.LastDispatchedIpcPresence?.State);
        Assert.Equal(queueEnteredUtc, service.LastDispatchedIpcPresence?.Timestamps?.Start);

        // Round end / server hop preserves continuous queue timer across Queue Arcade map rotation
        service.ProcessConsoleLogLine("MATCH_END");
        Assert.True(service.IsQueueArcadeActive);
        Assert.Equal("guccidonut in Queue", service.CurrentPresence?.State);
        Assert.Equal(queueEnteredUtc, service.CurrentPresence?.Timestamps?.Start);

        // Pulling player into real match exits Queue Arcade and clears the warm-up queue timer
        service.ProcessConsoleLogLine("[QUEUE ARCADE] RANKED MATCH FOUND (Caldavar)! Pulling you out of Queue Arcade...");
        Assert.False(service.IsQueueArcadeActive);
        Assert.Null(service.QueueStartUtc);
        Assert.Equal("Joining Game (Forests of Caldavar)...", service.CurrentPresence?.Details);
    }

    [Theory]
    [InlineData("VICTORY")]
    [InlineData("DEFEAT")]
    [InlineData("MATCH_END")]
    [InlineData("BASE_DESTROYED")]
    [InlineData("Game Finished")]
    public void MatchEnd_NonQueueArcade_ReturnsImmediatelyToInMainMenu(string endLogLine)
    {
        // protects: Non-Queue-Arcade match completion returns immediately to "In Main Menu" without lingering "Post-Game Stats".
        // fails_when: Regression restores "Match Finished" / "Post-Game Stats" or leaves stale hero/match timer active.
        // seam: DiscordPresenceService.ProcessConsoleLogLine + UpdateLocalPresence + DiscordRPC RichPresence state.
        using DiscordPresenceService service = new();
        service.ProcessConsoleLogLine("Login success: #59 [GUCI]guccidonut");
        service.ProcessConsoleLogLine("[NEXUS_TELEMETRY] status=InGame;player=[GUCI]guccidonut;mode=caldavar;arcade=0;hero=Hero_Devourer");
        Assert.Equal("Playing Forests of Caldavar", service.CurrentPresence?.Details);
        Assert.NotNull(service.MatchStartUtc);

        service.ProcessConsoleLogLine(endLogLine);

        Assert.Equal("In Main Menu", service.CurrentPresence?.Details);
        Assert.Equal("guccidonut (In Lobby)", service.CurrentPresence?.State);
        Assert.Null(service.MatchStartUtc);
    }

    [Fact]
    public void MidMatchReconnect_MatchSecTelemetry_AnchorsDiscordTimerToRealGameClock()
    {
        // protects: Reconnecting mid-match syncs Discord timer to Game.lastMatchTime while ignoring small <10s tick jitter and keeping Queue Arcade continuous.
        // fails_when: matchsec telemetry fails to adjust _matchStartUtc, resets on <10s jitter, or overwrites Queue Arcade continuous queue timer.
        // seam: DiscordPresenceService.ProcessConsoleLogLine ([NEXUS_TELEMETRY] matchsec/prematch parser) + MatchStartUtc/QueueStartUtc.
        using DiscordPresenceService service = new();
        service.ProcessConsoleLogLine("Login success: #59 [GUCI]guccidonut");

        // Prematch phase does not snap the active match timer
        service.ProcessConsoleLogLine("[NEXUS_TELEMETRY] status=InGame;player=[GUCI]guccidonut;mode=caldavar;arcade=0;hero=Hero_Devourer;matchsec=420;prematch=1");
        DateTime? prematchStart = service.MatchStartUtc;
        Assert.NotNull(prematchStart);
        Assert.True((DateTime.UtcNow - prematchStart.Value).TotalSeconds < 10.0);

        // Live match (prematch=0, matchsec=1350) anchors MatchStartUtc ~22.5 minutes ago
        service.ProcessConsoleLogLine("[NEXUS_TELEMETRY] status=InGame;player=[GUCI]guccidonut;mode=caldavar;arcade=0;hero=Hero_Devourer;matchsec=1350;prematch=0");
        DateTime anchoredStart = service.MatchStartUtc!.Value;
        double anchoredElapsed = (DateTime.UtcNow - anchoredStart).TotalSeconds;
        Assert.InRange(anchoredElapsed, 1345.0, 1355.0);
        Assert.Equal(anchoredStart, service.CurrentPresence?.Timestamps?.Start);

        // Follow-up tick with <= 10s drift does not mutate MatchStartUtc (prevents Discord timer flicker)
        service.ProcessConsoleLogLine("[NEXUS_TELEMETRY] status=InGame;player=[GUCI]guccidonut;mode=caldavar;arcade=0;hero=Hero_Devourer;matchsec=1352;prematch=0");
        Assert.Equal(anchoredStart, service.MatchStartUtc!.Value);

        // Queue Arcade warm-up keeps continuous QueueStartUtc rather than switching to matchsec
        DateTime queueEnteredUtc = DateTime.UtcNow.AddMinutes(-6);
        service.SetQueueStartTime(queueEnteredUtc);
        service.ProcessConsoleLogLine("[NEXUS_TELEMETRY] status=InGame;player=[GUCI]guccidonut;mode=devowars_10v10;arcade=1;hero=Hero_Devourer;matchsec=120;prematch=0");
        Assert.True(service.IsQueueArcadeActive);
        Assert.Equal(queueEnteredUtc, service.QueueStartUtc);
        Assert.Equal(queueEnteredUtc, service.CurrentPresence?.Timestamps?.Start);
    }

    [Theory]
    [InlineData("caldavar_hallow", "Forests of Caldavar (Halloween)", "map_caldavar")]
    [InlineData("midwars_hallowe", "Mid Wars (Halloween)", "map_midwars")]
    [InlineData("nightmarish_coo", "Nightmarish Co-Op", "map_nightmarishcoop")]
    [InlineData("Nightmarish_004", "Nightmarish Co-Op", "map_nightmarishcoop")]
    public void FastBus15CharTruncatedMaps_ResolveExactHalloweenAndNightmarishModes(
        string truncatedMapName,
        string expectedMode,
        string expectedAssetKey)
    {
        // protects: 15-char TelemetryRingHeader.CurrentMap truncation still resolves Halloween and Nightmarish Co-Op modes and Portal map art keys.
        // fails_when: Map mode detection or Discord Portal asset resolution misses 15-char truncated fast-bus strings.
        // seam: DiscordPresenceService.DetectGameModeFromLog + DiscordPresenceService.GetMapAssetKeyFromGameMode.
        string detectedMode = DiscordPresenceService.DetectGameModeFromLog($"CWorld::StartLoad - {truncatedMapName}", string.Empty);
        Assert.Equal(expectedMode, detectedMode);
        Assert.Equal(expectedAssetKey, DiscordPresenceService.GetMapAssetKeyFromGameMode(detectedMode));
        Assert.Equal(expectedAssetKey, DiscordPresenceService.GetMapAssetKeyFromGameMode(truncatedMapName));
    }

    [Fact]
    public void NewUiLifecycle_Party_Queue_Banning_HostedLobby_And_LiveSpectate()
    {
        // protects: NewUI Party/Queue/HostedLobby/BanningPhase/Spectating telemetry states, live preview callbacks, and Streamer Privacy redaction.
        // fails_when: NewUI phases fall back to generic Lobby/In Match or leak player handles through OnActivityUpdated when privacy mode is active.
        // seam: DiscordPresenceService.ProcessConsoleLogLine ([NEXUS_TELEMETRY]) + OnActivityUpdated + SetStreamerPrivacyMode.
        using DiscordPresenceService service = new();
        List<(string Details, string State)> updates = [];
        service.OnActivityUpdated += (details, state) => updates.Add((details, state));

        service.ProcessConsoleLogLine("Login success: #59 [GUCI]guccidonut");

        service.ProcessConsoleLogLine("[NEXUS_TELEMETRY] status=Party;player=[GUCI]guccidonut;partysize=3;partymax=5");
        Assert.Equal("In Main Menu", service.CurrentPresence?.Details);
        Assert.Equal("guccidonut (In Party)", service.CurrentPresence?.State);
        Assert.Equal(3, service.CurrentPresence?.Party?.Size);
        Assert.Equal(5, service.CurrentPresence?.Party?.Max);

        service.ProcessConsoleLogLine("[NEXUS_TELEMETRY] status=Queue;player=[GUCI]guccidonut;mode=caldavar;partysize=3;partymax=5");
        Assert.Equal("In Matchmaking (Forests of Caldavar)", service.CurrentPresence?.Details);
        Assert.Equal("guccidonut in Queue", service.CurrentPresence?.State);

        service.ProcessConsoleLogLine("[NEXUS_TELEMETRY] status=HostedLobby;player=[GUCI]guccidonut;mode=midwars;partysize=4;partymax=10");
        Assert.Equal("Custom Lobby (Mid Wars)", service.CurrentPresence?.Details);
        Assert.Equal("guccidonut (Waiting for Players)", service.CurrentPresence?.State);
        Assert.Equal(4, service.CurrentPresence?.Party?.Size);
        Assert.Equal(10, service.CurrentPresence?.Party?.Max);

        service.ProcessConsoleLogLine("[NEXUS_TELEMETRY] status=BanningPhase;player=[GUCI]guccidonut;mode=caldavar");
        Assert.Equal("Banning Phase (Forests of Caldavar)", service.CurrentPresence?.Details);
        Assert.Equal("guccidonut (Banning Phase)", service.CurrentPresence?.State);

        service.ProcessConsoleLogLine("[NEXUS_TELEMETRY] status=Spectating;player=[GUCI]guccidonut;mode=caldavar");
        Assert.Equal("Spectating Forests of Caldavar", service.CurrentPresence?.Details);
        Assert.Equal("guccidonut (Watching Match)", service.CurrentPresence?.State);

        service.SetStreamerPrivacyMode(true);
        Assert.DoesNotContain("guccidonut", updates[^1].State, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Watching Match", updates[^1].State);
    }

    [Fact]
    public async Task DiscordRestartResync_And_MapEditorPresence_WorkAsAligned()
    {
        // protects: K2 World Editor DRP initialization, Discord IPC disconnect/reconnect auto-resync, and empty preview emission on shutdown.
        // fails_when: Map Editor does not publish "In Map Editor", OnReady stays blocked by 3s/15s dedup gate, or DisposeAsync leaves stale UI preview text.
        // seam: DiscordPresenceService.InitializeAsync(isMapEditor: true) + HandleDiscordIpcClosed + HandleDiscordIpcReady + DisposeAsync.
        using DiscordPresenceService service = new();
        List<(string Details, string State)> updates = [];
        service.OnActivityUpdated += (details, state) => updates.Add((details, state));

        await service.InitializeAsync(null, isMapEditor: true);

        Assert.True(service.IsMapEditor);
        Assert.Equal("In Map Editor", service.CurrentPresence?.Details);
        Assert.Equal("World Editor", service.CurrentPresence?.State);
        Assert.Equal("K2 World Editor", service.CurrentPresence?.Assets?.LargeImageText);
        Assert.Contains(updates, u => u.Details == "In Map Editor" && u.State == "World Editor");

        int countBeforeDisconnect = updates.Count;
        service.HandleDiscordIpcClosed("Simulated Discord restart");
        Assert.False(service.IsDiscordIpcConnected);

        service.HandleDiscordIpcReady();
        Assert.True(service.IsDiscordIpcConnected);
        Assert.True(updates.Count > countBeforeDisconnect, "OnReady should bypass IPC dedup gate and force immediate resync.");
        Assert.Equal(("In Map Editor", "World Editor"), updates[^1]);

        await service.DisposeAsync();
        Assert.False(service.IsDiscordIpcConnected);
        Assert.Equal((string.Empty, string.Empty), updates[^1]);
    }

    [Fact]
    public void RealPlaytestLogReplay_QueueArcade_MainMenu_StrikersBotMatch_And_CustomLobby22()
    {
        // protects: Real-world chronological session transitions never leak Queue Arcade or Hosted Lobby (2 of 22) slots into Main Menu, Strikers Bot Match, or Practice Match.
        // fails_when: [QUEUE_ARCADE][STATE_SYNC], [TacticalHud] Hiding banner, _stats_is_queue_arcade_match=false, trailing InGame logs, accountId:1 local server, or arena_Hero_* corrupts DRP.
        // seam: DiscordPresenceService.ProcessFastBusState (0..4 canonical phases) + ProcessConsoleLogLine (real console.log lines) + [NEXUS_TELEMETRY] + ProcessPresenceUpdate.
        using DiscordPresenceService service = new();
        service.OnActivityUpdated += (_, _) => { };

        // 1. Online login establishes authoritative online Account ID 59 and full handle
        service.ProcessConsoleLogLine("Login success: #59 [GUCI]guccidonut");
        service.ProcessConsoleLogLine("[HOMEPAGE_LUA] AccountInfo watch fired -> accountId: 59, nickname: [GUCI]guccidonut");
        Assert.Equal(59, service.AccountId);
        Assert.True(service.HasAuthoritativeOnlineAccountId);

        // 2. Enter genuine Queue Arcade warm-up (Grimm Hunt) -> Shows "In Queue • The Grimm Hunt" with continuous timer
        service.ProcessConsoleLogLine("Entering matchmaking queue");
        DateTime qStart = new(2026, 11, 16, 15, 50, 39, DateTimeKind.Utc);
        service.SetQueueStartTime(qStart);
        service.ProcessConsoleLogLine("Joining game '^990QUEUE ARCADE [thegrimmhunt] #^279'.");
        service.ProcessConsoleLogLine("^y[MATCH_STATS] LobbyGameInfo detected Queue Arcade match (mid=1416, server=QUEUE ARCADE #Q1, game=QUEUE ARCADE [thegrimmhunt] #)^*");
        service.ProcessConsoleLogLine("[NEXUS_TELEMETRY] status=HeroSelect;player=[GUCI]guccidonut;mode=thegrimmhunt;arcade=1;hero=Hero_Tarot");
        Assert.True(service.IsQueueArcadeActive);
        Assert.Equal("In Queue • The Grimm Hunt", service.CurrentPresence?.Details);
        Assert.Equal("guccidonut as Tarot (In Queue)", service.CurrentPresence?.State);
        Assert.Equal(qStart, service.CurrentPresence?.Timestamps?.Start);

        // 3. Disconnect from Queue Arcade to Main Menu; [QUEUE_ARCADE][STATE_SYNC] & [BOOT_PRELOAD] & SignalR stale Queue Arcade heartbeat must NOT trigger In Queue!
        service.ProcessConsoleLogLine("[MATCH_STATS] AfterMatchStatsTrigger suppressed for Queue Arcade match: mid=4294967295");
        service.ProcessConsoleLogLine("Cl: [QUEUE_ARCADE] Clean disconnect confirmed -> disabling auto-pull until re-enabled");
        service.ProcessConsoleLogLine("Sending disconnect notice...");
        service.ProcessFastBusState(gamePhase: 0, activeMatchId: 0, currentMap: string.Empty, playerName: "[GUCI]gucci");
        service.ProcessConsoleLogLine("[QUEUE_ARCADE][STATE_SYNC] source=fresh_login prev=false next=true label=ARCADE: ON");
        service.ProcessConsoleLogLine("Cl: [BOOT_PRELOAD] Menu open is instant (0 ms) at select_a_mode (modal queue PreloadWorld OFF - Queue Arcade / match load handles world assets).");
        service.ProcessPresenceUpdate(new PresenceDto
        {
            AccountId = 59,
            State = "Online in Menus",
            Details = "In Main Menu"
        });

        Assert.False(service.IsQueueArcadeActive);
        Assert.Equal("In Main Menu", service.CurrentPresence?.Details);
        Assert.Equal("guccidonut (In Lobby)", service.CurrentPresence?.State);
        Assert.Null(service.CurrentPresence?.Party);

        // 4. Launch Strikers Bot Match -> [TacticalHud] Hiding banner (inQueue=false) + arcade=0 keep DRP strictly in Bot Match as Aluna
        service.ProcessFastBusState(gamePhase: 1, activeMatchId: 0, currentMap: "soccer", playerName: "[GUCI]gucci");
        Assert.Equal("Joining Game (Strikers)...", service.CurrentPresence?.Details);
        Assert.Equal("map_strikers", service.CurrentPresence?.Assets?.LargeImageKey);
        service.ProcessConsoleLogLine("StartGame local mode:botmatch map:soccer");
        service.ProcessConsoleLogLine("CGame: [MATCH_STATS] OnConnectionStatus serverName=Unnamed_Server, targetPort=nil, pendingCreate=false -> _stats_is_queue_arcade_match=false");
        service.ProcessFastBusState(gamePhase: 3, activeMatchId: 0, currentMap: "soccer", playerName: "[GUCI]gucci");
        service.ProcessConsoleLogLine("CGame: [NEXUS_TELEMETRY] hero=Aluna;player=[GUCI]guccidonut;level=1;mode=soccer;arcade=0;status=InGame");
        service.ProcessConsoleLogLine("[TacticalHud] [QueueArcade] Hiding top-left Queue Arcade banner (inQueue=false, popAlert=false)");

        Assert.False(service.IsQueueArcadeActive);
        Assert.Equal("Bot Match", service.CurrentPresence?.Details);
        Assert.Equal("guccidonut as Aluna (Lvl 1)", service.CurrentPresence?.State);
        Assert.Equal("hero_aluna", service.CurrentPresence?.Assets?.SmallImageKey);
        Assert.Equal("map_strikers", service.CurrentPresence?.Assets?.LargeImageKey);

        // Even if a delayed SignalR Queue Arcade packet arrives mid-match, it must be ignored while in confirmed non-arcade match
        service.ProcessPresenceUpdate(new PresenceDto
        {
            AccountId = 59,
            State = "In Matchmaking Queue",
            Details = "In Queue • Strikers"
        });
        Assert.False(service.IsQueueArcadeActive);
        Assert.Equal("Bot Match", service.CurrentPresence?.Details);

        // 5. Return to Main Menu (FastBus phase 3 -> 0) -> trailing closed-match InGame log line & local-server accountId:1 must be ignored
        service.ProcessFastBusState(gamePhase: 0, activeMatchId: 0, currentMap: string.Empty, playerName: "[GUCI]gucci");
        service.ProcessConsoleLogLine("[NEXUS_TELEMETRY] hero=arena_Hero_Engineer;player=[GUCI]guccidonut;level=3;mode=arena;arcade=0;status=InGame");
        service.ProcessConsoleLogLine("[HOMEPAGE_LUA] AccountInfo watch fired -> accountId: 1, nickname: [GUCI]guccidonut");
        Assert.Equal(59, service.AccountId);
        Assert.Equal("In Main Menu", service.CurrentPresence?.Details);
        Assert.Equal("guccidonut (In Lobby)", service.CurrentPresence?.State);

        // 6. Custom Lobby 22-slot Watchtower -> shows (2 of 22); leaving lobby or starting Practice Match clears (2 of 22) completely
        service.ProcessConsoleLogLine("[NEXUS_TELEMETRY] status=HostedLobby;player=[GUCI]guccidonut;mode=watchtower;slots=2/22;arcade=0");
        Assert.Equal("Custom Lobby (Watchtower)", service.CurrentPresence?.Details);
        Assert.Equal(2, service.CurrentPresence?.Party?.Size);
        Assert.Equal(22, service.CurrentPresence?.Party?.Max);

        // Start Practice Match on Arena: clears 2/22 slots, ignores picker dummies ("Hero Select", Hero_Enfos_Dummy), resolves arena_Hero_Engineer cleanly
        service.ProcessConsoleLogLine("StartPractice mode:practice map:arena");
        service.ProcessFastBusState(gamePhase: 2, activeMatchId: 0, currentMap: "arena", playerName: "[GUCI]gucci");
        service.ProcessConsoleLogLine("[NEXUS_TELEMETRY] status=HeroSelect;player=[GUCI]guccidonut;mode=arena;arcade=0;hero=Hero_Enfos_Dummy");
        service.ProcessConsoleLogLine("[NEXUS_TELEMETRY] status=HeroSelect;player=[GUCI]guccidonut;mode=arena;arcade=0;hero=Hero Select");
        Assert.Null(service.CurrentPresence?.Party);
        Assert.Null(service.SelectedHeroAssetKey);
        Assert.Equal("Practice Match (Hero Selection)", service.CurrentPresence?.Details);
        Assert.Equal("guccidonut (Hero Selection)", service.CurrentPresence?.State);

        service.ProcessFastBusState(gamePhase: 3, activeMatchId: 0, currentMap: "arena", playerName: "[GUCI]gucci");
        service.ProcessConsoleLogLine("[NEXUS_TELEMETRY] hero=arena_Hero_Engineer;avatar=arena_Hero_Engineer;player=[GUCI]guccidonut;level=3;mode=arena;arcade=0;status=InGame");
        Assert.Equal("hero_engineer", service.CurrentPresence?.Assets?.SmallImageKey);
        Assert.Equal("Engineer ([GUCI]guccidonut)", service.CurrentPresence?.Assets?.SmallImageText);
        Assert.Equal("Practice Match", service.CurrentPresence?.Details);
        Assert.Equal("guccidonut as Engineer (Lvl 3)", service.CurrentPresence?.State);
    }

    [Fact]
    public void AccountSwitch_ResetsAccountIdLock_And_LegacyStatsSuppressionWithQueueArcadeTextStillClears()
    {
        // protects: Explicit Login success switches Account ID cleanly, _stats_is_queue_arcade_match=false clears even if "QUEUE ARCADE" appears on the same line, and FastBus Phase 4 ends cleanly.
        // fails_when: Account ID lock blocks switching to another account or '!Contains("QUEUE ARCADE")' prevents serverName=QUEUE ARCADE clear from resetting flag.
        // seam: DiscordPresenceService.ProcessConsoleLogLine + ProcessFastBusState (canonical phase 4 post-game).
        using DiscordPresenceService service = new();

        service.ProcessConsoleLogLine("Login success: #59 [GUCI]guccidonut");
        Assert.Equal(59, service.AccountId);

        // Local server accountId: 1 must not override 59
        service.ProcessConsoleLogLine("[HOMEPAGE_LUA] AccountInfo watch fired -> accountId: 1, nickname: [GUCI]guccidonut");
        Assert.Equal(59, service.AccountId);

        // Switching to a second online account must update AccountId
        service.ProcessConsoleLogLine("Login success: #99 AltTester");
        Assert.Equal(99, service.AccountId);
        Assert.Equal("AltTester", service.CleanAccountName);

        // _stats_is_queue_arcade_match=false clears even when "QUEUE ARCADE" text is also on the line
        service.ProcessConsoleLogLine("^y[MATCH_STATS] LobbyGameInfo detected Queue Arcade match (mid=10, server=QUEUE ARCADE #Q1)^*");
        Assert.True(service.IsQueueArcadeActive);
        service.ProcessConsoleLogLine("Clearing Queue Arcade status: _stats_is_queue_arcade_match=false");
        Assert.False(service.IsQueueArcadeActive);

        // FastBus phase 4 (Post-Game Summary) returns immediately to Main Menu for non-arcade matches
        service.ProcessFastBusState(gamePhase: 3, activeMatchId: 500, currentMap: "caldavar", playerName: "AltTester");
        Assert.Equal("Playing Forests of Caldavar", service.CurrentPresence?.Details);
        service.ProcessFastBusState(gamePhase: 4, activeMatchId: 500, currentMap: "caldavar", playerName: "AltTester");
        Assert.Equal("In Main Menu", service.CurrentPresence?.Details);
    }

    [Fact]
    public void HeroRegistry_DeduplicatedFromProtocolCoreHeroes_PreservesAll140PortalAssetsAndLegacyAliases()
    {
        // protects: Single-source derivation of DiscordPresenceService.HeroRegistry + ValidPortalAssetKeys from ASPIRE.Common.Constants.Heroes.
        // fails_when: Any canonical Hero_* identifier, snake_case Discord portal override, Gwynplaine, or legacy alias fails to resolve or drops out of ValidPortalAssetKeys.
        // seam: Heroes.AllHeroIdentifiers() -> DiscordPresenceService.ResolveHero() + DiscordPresenceService.ValidPortalAssetKeys.
        IReadOnlySet<string> portalKeys = DiscordPresenceService.ValidPortalAssetKeys;
        Assert.Contains("hero_pollywog_priest", portalKeys);
        Assert.Contains("map_caldavar", portalKeys);
        Assert.Contains("map_watchtower", portalKeys);

        foreach (string identifier in Heroes.AllHeroIdentifiers())
        {
            (string AssetKey, string DisplayName)? resolved = DiscordPresenceService.ResolveHero(identifier);
            Assert.NotNull(resolved);
            Assert.Contains(resolved.Value.AssetKey, portalKeys);
            Assert.False(string.IsNullOrWhiteSpace(resolved.Value.DisplayName));
        }

        Assert.Equal(("hero_bomb", "Bombardier"), DiscordPresenceService.ResolveHero("bombardier"));
        Assert.Equal(("hero_bomb", "Bombardier"), DiscordPresenceService.ResolveHero("bomb"));
        Assert.Equal(("hero_pollywogpriest", "Pollywog Priest"), DiscordPresenceService.ResolveHero("pollywog_priest"));
        Assert.Equal(("hero_yogi", "Wildsoul"), DiscordPresenceService.ResolveHero("wildsoul"));
        Assert.Equal(("hero_gwynplaine", "Gwynplaine"), DiscordPresenceService.ResolveHero("Hero_Gwynplaine"));
        Assert.Equal(("hero_treant", "Keeper of the Forest"), DiscordPresenceService.ResolveHero("Hero_Treant"));
    }

    [Fact]
    public void ConsoleLog_DevLinesAndAssetPaths_DoNotCorruptGameModeOrWipeHeroInStrikersArcade()
    {
        using var service = new DiscordPresenceService();
        service.ProcessConsoleLogLine("Entering matchmaking (Mid Wars)");
        service.ProcessConsoleLogLine("StartGame local mode:normal map:soccer");
        service.ProcessConsoleLogLine("CGame: [NEXUS_TELEMETRY] status=InGame;player=[GUCI]guccidonut;mode=soccer;arcade=1;hero=Hero_Pyromancer;level=7");

        Assert.Equal("In Queue • Strikers", service.CurrentPresence?.Details);
        Assert.Equal("map_strikers", service.CurrentPresence?.Assets?.LargeImageKey);
        Assert.Equal("hero_pyromancer", service.CurrentPresence?.Assets?.SmallImageKey);

        // Replay 2026-11-18 Dev: and asset-path console lines that previously poisoned map mode and wiped hero
        service.ProcessConsoleLogLine("Dev: Rewinding effect /triggers/custom_maps/capturetheflag/effects/prepare.effect");
        service.ProcessConsoleLogLine("Dev: [WORLD_LOAD] Loaded custom Capture the Flag trigger asset");
        service.ProcessConsoleLogLine("Loading asset /world/props/capturetheflag/banner.material");
        service.ProcessConsoleLogLine("Loading asset /items/capturetheflag/flag.model");
        service.ProcessConsoleLogLine("Loading asset /buildings/capturetheflag/pedestal.clip");
        service.ProcessConsoleLogLine("Playing /triggers/custom_maps/capturetheflag/sounds/horn.ogg");
        service.ProcessConsoleLogLine("Shader warmup for capturetheflag/textures/flag.anim and sound.wav / voice.mp3");
        service.ProcessConsoleLogLine("Preloaded script hint mentioning Capture the Flag outside world load");

        Assert.Equal("In Queue • Strikers", service.CurrentPresence?.Details);
        Assert.Equal("map_strikers", service.CurrentPresence?.Assets?.LargeImageKey);
        Assert.Equal("hero_pyromancer", service.CurrentPresence?.Assets?.SmallImageKey);
        Assert.Equal("Mid Wars", service.QueuedGameMode);

        // Helper-level asset-path guards (must fall back to neutral "large", never "map_capturetheflag")
        Assert.Equal("large", DiscordPresenceService.GetMapAssetKeyFromGameMode("/triggers/custom_maps/capturetheflag/effects/prepare.effect"));
        Assert.Equal(string.Empty, DiscordPresenceService.DetectGameModeFromLog("Dev: Rewinding effect /triggers/custom_maps/capturetheflag/effects/prepare.effect", string.Empty));
        Assert.Equal(string.Empty, DiscordPresenceService.DetectMatchModeFromLog("Dev: Loading /world/botmatch/stage.effect", string.Empty));
    }

    [Fact]
    public void ConsoleLog_TmmGroupBotDifficultyAndRandomizeBots_DoNotMisclassifyOnlineMatchAsBotMatch()
    {
        using var service = new DiscordPresenceService();

        // Normal online TMM group creation emits Cl: BotDifficulty: 3 and Cl: RandomizeBots: 1
        service.ProcessConsoleLogLine("Cl: BotDifficulty: 3");
        service.ProcessConsoleLogLine("Cl: RandomizeBots: 1");
        service.ProcessConsoleLogLine("CClient: Setting cvar randombots:0|0");
        Assert.Equal(string.Empty, DiscordPresenceService.DetectMatchModeFromLog("Cl: BotDifficulty: 3", string.Empty));
        Assert.Equal(string.Empty, DiscordPresenceService.DetectMatchModeFromLog("Cl: RandomizeBots: 1", string.Empty));
        Assert.Equal(string.Empty, DiscordPresenceService.DetectMatchModeFromLog("CClient: Setting cvar randombots:0|0", string.Empty));

        service.ProcessConsoleLogLine("CGame: [NEXUS_TELEMETRY] status=InGame;player=[GUCI]guccidonut;mode=caldavar;matchtype=TMM;hero=Hero_Valkyrie;level=3");
        Assert.Equal("Playing Forests of Caldavar", service.CurrentPresence?.Details);
        Assert.NotEqual("Bot Match", service.CurrentMatchMode);

        // Actual bot count token (>0) must still detect Bot Match
        Assert.Equal("Bot Match", DiscordPresenceService.DetectMatchModeFromLog("StartGame local mode:normal map:soccer randombots:4|5", string.Empty));
    }

    [Fact]
    public void FastBusZeroMatchIdAndRemotePort11235_DoNotOverwriteHostedLobbyOrQueueArcadeWithPracticeMatch()
    {
        // Case A: Hosted Lobby with activeMatchId == 0 during FastBus phase 1 -> 2 -> 3
        using var lobbyService = new DiscordPresenceService();
        lobbyService.ProcessPresenceUpdate(new PresenceDto
        {
            AccountId = 59,
            State = "In Hosted Lobby (3/10)",
            Details = "Hosted Match - Mid Wars",
            LargeImageKey = "map_midwars",
            LargeImageText = "Mid Wars",
            PartySize = 3,
            PartyMax = 10
        });
        lobbyService.ProcessFastBusState(gamePhase: 1, activeMatchId: 0, currentMap: "midwars", playerName: "[GUCI]guccidonut");
        lobbyService.ProcessFastBusState(gamePhase: 2, activeMatchId: 0, currentMap: "midwars", playerName: "[GUCI]guccidonut");
        lobbyService.ProcessFastBusState(gamePhase: 3, activeMatchId: 0, currentMap: "midwars", playerName: "[GUCI]guccidonut");

        Assert.NotEqual("Practice Match", lobbyService.CurrentMatchMode);

        // Case B: Queue Arcade with activeMatchId == 0 and FastBus phase 3
        using var arcadeService = new DiscordPresenceService();
        arcadeService.ProcessConsoleLogLine("Entering matchmaking (Mid Wars)");
        arcadeService.ProcessConsoleLogLine("StartGame local mode:normal map:soccer");
        arcadeService.ProcessConsoleLogLine("CGame: [NEXUS_TELEMETRY] status=InGame;player=[GUCI]guccidonut;mode=soccer;arcade=1;hero=Hero_Aluna;level=2");
        arcadeService.ProcessFastBusState(gamePhase: 3, activeMatchId: 0, currentMap: "soccer", playerName: "[GUCI]guccidonut");

        Assert.Equal("In Queue • Strikers", arcadeService.CurrentPresence?.Details);
        Assert.NotEqual("Practice Match", arcadeService.CurrentMatchMode);

        // Case C: Remote non-loopback server using port 11235 must NOT be treated as Practice Match
        Assert.Equal(string.Empty, DiscordPresenceService.DetectMatchModeFromLog("Connecting to 203.0.113.42:11235", string.Empty));
        Assert.Equal(string.Empty, DiscordPresenceService.DetectMatchModeFromLog("CSocket::Connect host=198.51.100.10:11235", string.Empty));

        // Local loopback host MUST still detect Practice Match
        Assert.Equal("Practice Match", DiscordPresenceService.DetectMatchModeFromLog("Connecting to 127.0.0.1:11235", string.Empty));
    }

    [Fact]
    public void QueueArcade_DisconnectBackToMidWarsQueue_PreservesQueuedModeAndContinuousQueueTimer()
    {
        using var service = new DiscordPresenceService();

        // 1. Enter Mid Wars queue first so QueueStartUtc is established
        service.ProcessConsoleLogLine("Entering matchmaking (Mid Wars)");
        Assert.Equal("In Matchmaking (Mid Wars)", service.CurrentPresence?.Details);
        Assert.Equal("Mid Wars", service.QueuedGameMode);
        DateTime? initialQueueStart = service.CurrentPresence?.Timestamps?.Start;
        Assert.NotNull(initialQueueStart);

        // 2. Hop into Queue Arcade (Strikers) while still in Mid Wars queue
        service.ProcessConsoleLogLine("StartGame local mode:normal map:soccer");
        service.ProcessConsoleLogLine("CGame: [NEXUS_TELEMETRY] status=InGame;player=[GUCI]guccidonut;mode=soccer;arcade=1;hero=Hero_Aluna;level=4");
        Assert.True(service.IsQueueArcadeActive);
        Assert.Equal("In Queue • Strikers", service.CurrentPresence?.Details);
        Assert.Equal("Mid Wars", service.QueuedGameMode);

        // 3. FastBus transitions 3 -> 0 BEFORE delayed disconnect log arrives 200ms later
        service.ProcessFastBusState(gamePhase: 0, activeMatchId: 0, currentMap: string.Empty, playerName: "[GUCI]guccidonut");
        service.ProcessConsoleLogLine("Sending disconnect notice");
        service.ProcessConsoleLogLine("Disconnected from server");

        Assert.False(service.IsQueueArcadeActive);
        Assert.Equal("Mid Wars", service.QueuedGameMode);
        Assert.Equal("Mid Wars", service.CurrentGameMode);
        Assert.Equal("In Matchmaking (Mid Wars)", service.CurrentPresence?.Details);
        Assert.Equal(initialQueueStart, service.CurrentPresence?.Timestamps?.Start);

        // 4. Hop back into Strikers then leave via manual button -> Clean disconnect confirmed preserves Mid Wars queue too
        service.ProcessFastBusState(gamePhase: 3, activeMatchId: 0, currentMap: "soccer", playerName: "[GUCI]guccidonut");
        service.ProcessConsoleLogLine("CGame: [NEXUS_TELEMETRY] status=InGame;player=[GUCI]guccidonut;mode=soccer;arcade=1;hero=Hero_Aluna;level=1");
        Assert.True(service.IsQueueArcadeActive);
        service.ProcessFastBusState(gamePhase: 0, activeMatchId: 0, currentMap: string.Empty, playerName: "[GUCI]guccidonut");
        service.ProcessConsoleLogLine("[QUEUE_ARCADE] Manual leave from Queue Arcade mini-game");
        service.ProcessConsoleLogLine("Clean disconnect confirmed");

        Assert.False(service.IsQueueArcadeActive);
        Assert.Equal("In Matchmaking (Mid Wars)", service.CurrentPresence?.Details);
        Assert.Equal(initialQueueStart, service.CurrentPresence?.Timestamps?.Start);

        // 5. Explicit cancel of matchmaking clears queue state back to Main Menu
        service.ProcessConsoleLogLine("[TacticalHud] Left matchmaking");
        Assert.Equal(string.Empty, service.QueuedGameMode);
        Assert.Equal("In Main Menu", service.CurrentPresence?.Details);
    }

    [Fact]
    public void Telemetry_InGameWithoutAvatarField_PreservesResolvedAltAvatarForSameBaseHero()
    {
        using var service = new DiscordPresenceService();

        // HeroSelect carries alt avatar metadata
        service.ProcessConsoleLogLine("CGame: [NEXUS_TELEMETRY] status=HeroSelect;player=[GUCI]guccidonut;mode=caldavar;matchtype=TMM;hero=Hero_Gladiator.Alt3;avatar=Riftshards Gladiator");
        Assert.Equal("Riftshards Gladiator", service.SelectedAvatarDisplayName);

        // Subsequent InGame heartbeat emits base hero without avatar= field
        service.ProcessConsoleLogLine("CGame: [NEXUS_TELEMETRY] hero=Hero_Gladiator;player=[GUCI]guccidonut;mode=caldavar;matchtype=TMM;status=InGame;level=5;k=1;d=0;a=2");
        service.ProcessConsoleLogLine("NETCMD_CLIENT_IN_GAME");

        Assert.Equal("Riftshards Gladiator", service.SelectedAvatarDisplayName);
        Assert.Equal("hero_gladiator", service.CurrentPresence?.Assets?.SmallImageKey);
        Assert.Contains("Riftshards Gladiator", service.CurrentPresence?.Assets?.SmallImageText);
        Assert.Contains("The Gladiator", service.CurrentPresence?.Assets?.SmallImageText);

        // Switching to a genuinely different hero clears stale alt avatar display name
        service.ProcessConsoleLogLine("CGame: [NEXUS_TELEMETRY] hero=Hero_Valkyrie;player=[GUCI]guccidonut;mode=caldavar;status=InGame;level=1");
        Assert.True(string.IsNullOrEmpty(service.SelectedAvatarDisplayName));
        Assert.Equal("hero_valkyrie", service.CurrentPresence?.Assets?.SmallImageKey);
    }

    [Fact]
    public void DiscordPresenceService_RejectsUintMinusOneAccountId_AndExtractsSessionCookieHeaderWithoutQueryString()
    {
        Assert.False(DiscordPresenceService.IsValidOnlineAccountId(0));
        Assert.False(DiscordPresenceService.IsValidOnlineAccountId(-1));
        Assert.False(DiscordPresenceService.IsValidOnlineAccountId(4294967295L));
        Assert.True(DiscordPresenceService.IsValidOnlineAccountId(59));

        const string sampleCookie = "0123456789abcdef0123456789abcdef";
        Assert.Equal(sampleCookie, DiscordPresenceService.ExtractNormalizedSessionCookie($"[{sampleCookie}:deadbeef1234]"));
        Assert.Null(DiscordPresenceService.ExtractNormalizedSessionCookie("short"));

        using DiscordPresenceService service = new();

        // Pre-login (uint)-1 sentinel 4294967295 must NEVER be stored as a valid AccountId
        service.ProcessConsoleLogLine("[HOMEPAGE_LUA] accountId: 4294967295");
        service.ProcessConsoleLogLine("account_id=4294967295");
        service.ProcessConsoleLogLine("^y4294967295|Player|0");
        Assert.Equal(0L, service.AccountId);
        Assert.False(service.HasSessionCookie);

        // Authenticated login line populates valid AccountId and session cookie (for X-Kongor-Cookie header)
        service.ProcessConsoleLogLine($"Login success: #59 guccidonut [{sampleCookie}:9988776655443322]");
        Assert.Equal(59L, service.AccountId);
        Assert.Equal("guccidonut", service.AccountName);
        Assert.True(service.HasSessionCookie);
    }
}

