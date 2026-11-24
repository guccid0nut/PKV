// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Globalization;
using System.Text.RegularExpressions;
using DAWNBRINGER.WebPortal.UI.Services.Replay;

namespace DAWNBRINGER.WebPortal.UI.Services.LogParser;

public class HonLogParsedState
{
    public int MatchID { get; set; }
    public string Map { get; set; } = "caldavar";
    public string MatchName { get; set; } = string.Empty;
    public string GameMode { get; set; } = string.Empty;
    public int WinnerTeam { get; set; }
    public int ElapsedSeconds { get; set; }
    public Dictionary<int, LivePlayerState> PlayersBySlot { get; set; } = [];
    public Dictionary<int, int> LastDeathTimeMsBySlot { get; set; } = [];
    public Dictionary<int, int> LastSpellPinSecBySlot { get; set; } = [];
    public List<LiveWardState> ActiveWards { get; set; } = [];
    public List<LiveCombatEvent> RecentEvents { get; set; } = [];
    public List<LiveCombatEvent> AllTimelineEvents { get; set; } = [];
    public int ScoreLegion { get; set; }
    public int ScoreHellbourne { get; set; }
    public Dictionary<string, bool> Structures { get; set; } = [];
}

public static partial class HonLogParser
{
    private const float WorldMapDimension = 16384.0f;
    private const int MaxTimelineEvents = 600;

    [GeneratedRegex(@"\^(?:[a-zA-Z*]|[0-9]{3})")]
    private static partial Regex K2ColorCodeRegex();

    // MLog header & lobby events
    [GeneratedRegex(@"INFO_MATCH\s+name:""([^""]*)""\s+id:""(\d+)""")]
    private static partial Regex InfoMatchRegex();

    [GeneratedRegex(@"INFO_MAP\s+name:""([^""]*)""")]
    private static partial Regex InfoMapRegex();

    [GeneratedRegex(@"INFO_SETTINGS\s+mode:""([^""]*)""")]
    private static partial Regex InfoSettingsRegex();

    [GeneratedRegex(@"GAME_END\s+time:(\d+)\s+winner:""?([^""\s]+)""?")]
    private static partial Regex GameEndRegex();

    [GeneratedRegex(@"PLAYER_CONNECT\s+player:(\d+)\s+name:""([^""]*)""\s+id:(\d+)")]
    private static partial Regex PlayerConnectRegex();

    [GeneratedRegex(@"PLAYER_TEAM_CHANGE\s+player:(\d+)\s+team:(\d+)")]
    private static partial Regex PlayerTeamRegex();

    [GeneratedRegex(@"(?:PLAYER_SELECT|PLAYER_RANDOM)\s+player:(\d+)\s+hero:""([^""]*)""")]
    private static partial Regex PlayerSelectRegex();

    [GeneratedRegex(@"(?:PLAYER_REPICK|PLAYER_UNSELECT)\s+player:(\d+)")]
    private static partial Regex PlayerRepickRegex();

    // MLog live gameplay telemetry events
    [GeneratedRegex(
        @"GOLD_EARNED\s+time:(\d+)\s+x:([-\d.]+)\s+y:([-\d.]+)\s+z:([-\d.]+)\s+player:(\d+)\s+team:(\d+)(?:\s+source:""([^""]*)"")?(?:\s+owner:(\d+))?(?:\s+gold:(\d+))?")]
    private static partial Regex GoldEarnedRegex();

    [GeneratedRegex(
        @"EXP_EARNED\s+time:(\d+)\s+x:([-\d.]+)\s+y:([-\d.]+)\s+z:([-\d.]+)\s+player:(\d+)\s+team:(\d+)(?:\s+experience:([-\d.]+))?")]
    private static partial Regex ExpEarnedRegex();

    [GeneratedRegex(
        @"HERO_LEVEL\s+time:(\d+)\s+x:([-\d.]+)\s+y:([-\d.]+)\s+z:([-\d.]+)\s+player:(\d+)\s+team:(\d+)\s+level:(\d+)")]
    private static partial Regex HeroLevelRegex();

    [GeneratedRegex(
        @"ITEM_PURCHASE\s+time:(\d+)\s+x:([-\d.]+)\s+y:([-\d.]+)\s+z:([-\d.]+)\s+player:(\d+)\s+team:(\d+)\s+item:""([^""]*)""(?:\s+cost:(\d+))?")]
    private static partial Regex ItemPurchaseRegex();

    [GeneratedRegex(
        @"ITEM_ASSEMBLE\s+time:(\d+)\s+x:([-\d.]+)\s+y:([-\d.]+)\s+z:([-\d.]+)\s+player:(\d+)\s+team:(\d+)\s+item:""([^""]*)""")]
    private static partial Regex ItemAssembleRegex();

    [GeneratedRegex(
        @"ITEM_SELL\s+time:(\d+)\s+x:([-\d.]+)\s+y:([-\d.]+)\s+z:([-\d.]+)\s+player:(\d+)\s+team:(\d+)\s+item:""([^""]*)""(?:\s+value:(\d+))?")]
    private static partial Regex ItemSellRegex();

    [GeneratedRegex(
        @"ITEM_ACTIVATE\s+time:(\d+)\s+x:([-\d.]+)\s+y:([-\d.]+)\s+z:([-\d.]+)\s+player:(\d+)\s+team:(\d+)\s+item:""([^""]*)""")]
    private static partial Regex ItemActivateRegex();

    [GeneratedRegex(
        @"ABILITY_UPGRADE\s+time:(\d+)\s+x:([-\d.]+)\s+y:([-\d.]+)\s+z:([-\d.]+)\s+player:(\d+)\s+team:(\d+)\s+name:""([^""]*)""(?:\s+level:(\d+))?(?:\s+slot:(\d+))?")]
    private static partial Regex AbilityUpgradeRegex();

    [GeneratedRegex(
        @"ABILITY_ACTIVATE\s+time:(\d+)\s+x:([-\d.]+)\s+y:([-\d.]+)\s+z:([-\d.]+)\s+player:(\d+)\s+team:(\d+)\s+name:""([^""]*)""(?:\s+level:(\d+))?(?:\s+slot:(\d+))?")]
    private static partial Regex AbilityActivateRegex();

    [GeneratedRegex(
        @"HERO_DEATH\s+time:(\d+)\s+x:([-\d.]+)\s+y:([-\d.]+)\s+z:([-\d.]+)\s+player:(\d+)\s+team:(\d+)(?:\s+attacker:""([^""]*)"")?(?:\s+owner:(\d+))?")]
    private static partial Regex HeroDeathRegex();

    [GeneratedRegex(
        @"HERO_RESPAWN\s+time:(\d+)\s+x:([-\d.]+)\s+y:([-\d.]+)\s+z:([-\d.]+)\s+player:(\d+)\s+team:(\d+)(?:\s+duration:(\d+))?")]
    private static partial Regex HeroRespawnRegex();

    [GeneratedRegex(
        @"HERO_ASSIST\s+time:(\d+)\s+x:([-\d.]+)\s+y:([-\d.]+)\s+z:([-\d.]+)\s+player:(\d+)\s+team:(\d+)")]
    private static partial Regex HeroAssistRegex();

    [GeneratedRegex(
        @"KILL\s+time:(\d+)\s+x:([-\d.]+)\s+y:([-\d.]+)\s+z:([-\d.]+)\s+(?:player:(\d+)\s+)?team:(\d+)\s+target:""([^""]*)""\s+attacker:""([^""]*)""(?:\s+owner:(\d+))?")]
    private static partial Regex KillRegex();

    [GeneratedRegex(
        @"AWARD_FIRST_BLOOD\s+time:(\d+)\s+x:([-\d.]+)\s+y:([-\d.]+)\s+z:([-\d.]+)\s+player:(\d+)\s+team:(\d+)")]
    private static partial Regex AwardFirstBloodRegex();

    [GeneratedRegex(
        @"AWARD_MULTI_KILL\s+time:(\d+)\s+x:([-\d.]+)\s+y:([-\d.]+)\s+z:([-\d.]+)\s+player:(\d+)\s+team:(\d+)\s+count:(\d+)")]
    private static partial Regex AwardMultiKillRegex();

    [GeneratedRegex(
        @"HERO_POWERUP\s+time:(\d+)\s+x:([-\d.]+)\s+y:([-\d.]+)\s+z:([-\d.]+)\s+player:(\d+)\s+team:(\d+)\s+type:""([^""]*)""")]
    private static partial Regex HeroPowerupRegex();

    // Console / Slave log format
    [GeneratedRegex(@"(?:match\s*ID:?\s*|id:""?|StartGame\s*-\s*MatchID\s*\[?)(\d{6,12})", RegexOptions.IgnoreCase)]
    private static partial Regex ConsoleMatchIdRegex();

    [GeneratedRegex(@"Name:\s*([a-zA-Z0-9_\-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ConsoleNameRegex();

    [GeneratedRegex(@"Voice:\s*Adding\s+client\s+num\s+(\d+),\s*(?:([a-zA-Z0-9_\-]+),\s*)?ID:\s*(\d+)",
        RegexOptions.IgnoreCase)]
    private static partial Regex VoiceClientAddRegex();

    [GeneratedRegex(@"CLIENT\s+([a-zA-Z0-9_\-]+)\s+ip:", RegexOptions.IgnoreCase)]
    private static partial Regex DiagnosticsClientRegex();

    [GeneratedRegex(@"\[(?:ALL|TEAM)\]\s+([a-zA-Z0-9_\-]+):", RegexOptions.IgnoreCase)]
    private static partial Regex ChatMessageRegex();

    [GeneratedRegex(
        @"Client\s*#?(\d+)\s+(?:joined\s+team:\s*(\d+)|:\s*([a-zA-Z0-9_\-]+)\s*:\s*switched\s+to\s+team\s+(\d+))",
        RegexOptions.IgnoreCase)]
    private static partial Regex ConsoleTeamJoinedRegex();

    [GeneratedRegex(@"Client\s*#?(\d+)\s+(?:repicked|unselected)", RegexOptions.IgnoreCase)]
    private static partial Regex ConsoleRepickRegex();

    [GeneratedRegex(@"(?:CWorld::StartLoad\s*-\s*|map:)([a-zA-Z0-9_]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ConsoleMapRegex();

    [GeneratedRegex(@"Client\s*#?(\d+)\s+(?:selected|picked)\s+(?:hero:?\s*)?(Hero_[a-zA-Z0-9_]+)",
        RegexOptions.IgnoreCase)]
    private static partial Regex ConsoleHeroPickRegex();

    public static string StripK2Colors(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        return K2ColorCodeRegex().Replace(raw, string.Empty);
    }

    public static (float X, float Y) ConvertK2ToNormalizedScreen(float k2X, float k2Y)
    {
        float normX = Math.Clamp(k2X / WorldMapDimension, 0.0f, 1.0f);
        float normY = Math.Clamp(1.0f - k2Y / WorldMapDimension, 0.0f, 1.0f);
        return (normX, normY);
    }

    public static (float X, float Y) ConvertK2ToNormalizedScreen(string? mapName, float k2X, float k2Y)
    {
        return K2MapCoordinateRegistry.WorldToMinimap(mapName, k2X, k2Y);
    }

    public static bool ParseLine(string rawLine, HonLogParsedState state)
    {
        if (string.IsNullOrWhiteSpace(rawLine)) return false;

        string line = rawLine.Trim();

        // 1. INFO_MATCH or Console Match ID
        Match infoMatch = InfoMatchRegex().Match(line);
        if (infoMatch.Success)
        {
            state.MatchName = StripK2Colors(infoMatch.Groups[1].Value);
            if (int.TryParse(infoMatch.Groups[2].Value, out int mid)) state.MatchID = mid;
            return true;
        }

        Match cMatch = ConsoleMatchIdRegex().Match(line);
        if (cMatch.Success && int.TryParse(cMatch.Groups[1].Value, out int cmid))
        {
            state.MatchID = cmid;
            return true;
        }

        // 2. INFO_MAP or Console Map
        Match infoMap = InfoMapRegex().Match(line);
        if (infoMap.Success)
        {
            state.Map = infoMap.Groups[1].Value.ToLowerInvariant().Trim();
            return true;
        }

        Match cMap = ConsoleMapRegex().Match(line);
        if (cMap.Success)
        {
            state.Map = cMap.Groups[1].Value.ToLowerInvariant().Trim();
            return true;
        }

        // 2.5 INFO_SETTINGS & GAME_END
        Match infoSettings = InfoSettingsRegex().Match(line);
        if (infoSettings.Success)
        {
            state.GameMode = FormatGameMode(infoSettings.Groups[1].Value);
            return true;
        }

        Match gameEnd = GameEndRegex().Match(line);
        if (gameEnd.Success)
        {
            if (int.TryParse(gameEnd.Groups[1].Value, out int endMs))
            {
                state.ElapsedSeconds = Math.Max(state.ElapsedSeconds, endMs / 1000);
            }

            string rawWinner = gameEnd.Groups[2].Value.Trim();
            if (rawWinner == "1" || rawWinner.Contains("legion", StringComparison.OrdinalIgnoreCase))
            {
                state.WinnerTeam = 1;
            }
            else if (rawWinner == "2" || rawWinner.Contains("hellbourne", StringComparison.OrdinalIgnoreCase))
            {
                state.WinnerTeam = 2;
            }

            (float baseU, float baseV) = K2MapCoordinateRegistry.GetTeamFountain(
                state.Map,
                state.WinnerTeam == 1 ? 2 : 1);
            RecordCombatEvent(state, new LiveCombatEvent
            {
                EventType = "VICTORY",
                MatchTimeSeconds = state.ElapsedSeconds,
                Team = state.WinnerTeam,
                PositionX = baseU,
                PositionY = baseV,
                Description = $"Victory for {(state.WinnerTeam == 1 ? "The Legion" : "The Hellbourne")}!"
            });
            return true;
        }

        // 3. PLAYER_CONNECT or Console Name
        Match pConn = PlayerConnectRegex().Match(line);
        if (pConn.Success)
        {
            int slot = int.Parse(pConn.Groups[1].Value);
            string name = StripK2Colors(pConn.Groups[2].Value).Trim();
            int accountId = int.Parse(pConn.Groups[3].Value);

            EnsurePlayerExists(state, slot, accountId, name);
            return true;
        }

        Match vClient = VoiceClientAddRegex().Match(line);
        if (vClient.Success)
        {
            int slot = int.Parse(vClient.Groups[1].Value);
            string explicitName = vClient.Groups[2].Success ? vClient.Groups[2].Value : string.Empty;
            int accountId = int.Parse(vClient.Groups[3].Value);
            string defaultName = !string.IsNullOrEmpty(explicitName)
                ? explicitName
                : accountId == 1 || slot == 0
                    ? "guccidonut"
                    : $"Player #{slot + 1}";
            EnsurePlayerExists(state, slot, accountId, defaultName);
            return true;
        }

        Match diagClient = DiagnosticsClientRegex().Match(line);
        if (diagClient.Success)
        {
            string name = diagClient.Groups[1].Value;
            EnsurePlayerExists(state, 0, 1, name);
            return true;
        }

        Match cChat = ChatMessageRegex().Match(line);
        if (cChat.Success)
        {
            string name = cChat.Groups[1].Value;
            EnsurePlayerExists(state, 0, 1, name);
            return true;
        }

        Match cName = ConsoleNameRegex().Match(line);
        if (cName.Success)
        {
            string name = cName.Groups[1].Value;
            EnsurePlayerExists(state, 0, 1, name);
            return true;
        }

        // 4. PLAYER_TEAM_CHANGE or Console Team Joined
        Match pTeam = PlayerTeamRegex().Match(line);
        if (pTeam.Success)
        {
            int slot = int.Parse(pTeam.Groups[1].Value);
            int team = int.Parse(pTeam.Groups[2].Value);
            ApplyTeamChange(state, slot, team, explicitConnectExpected: false);
            return true;
        }

        Match cTeam = ConsoleTeamJoinedRegex().Match(line);
        if (cTeam.Success)
        {
            int slot = int.Parse(cTeam.Groups[1].Value);
            int team = 0;
            if (!string.IsNullOrEmpty(cTeam.Groups[2].Value))
            {
                team = int.Parse(cTeam.Groups[2].Value);
            }
            else if (!string.IsNullOrEmpty(cTeam.Groups[4].Value))
            {
                team = int.Parse(cTeam.Groups[4].Value);
                string explicitName = cTeam.Groups[3].Value;
                EnsurePlayerExists(state, slot, 1, explicitName);
            }

            ApplyTeamChange(state, slot, team, explicitConnectExpected: true);
            return true;
        }

        // 5. PLAYER_REPICK or Console Repick
        Match pRepick = PlayerRepickRegex().Match(line);
        if (pRepick.Success)
        {
            int slot = int.Parse(pRepick.Groups[1].Value);
            ApplyRepick(state, slot);
            return true;
        }

        Match cRepick = ConsoleRepickRegex().Match(line);
        if (cRepick.Success)
        {
            int slot = int.Parse(cRepick.Groups[1].Value);
            ApplyRepick(state, slot);
            return true;
        }

        // 6. PLAYER_SELECT or PLAYER_RANDOM or Console Hero Pick
        Match pSelect = PlayerSelectRegex().Match(line);
        if (pSelect.Success)
        {
            int slot = int.Parse(pSelect.Groups[1].Value);
            string rawHero = pSelect.Groups[2].Value;
            ApplyHeroSelect(state, slot, rawHero);
            return true;
        }

        Match cHero = ConsoleHeroPickRegex().Match(line);
        if (cHero.Success)
        {
            int slot = int.Parse(cHero.Groups[1].Value);
            string rawHero = cHero.Groups[2].Value;
            ApplyHeroSelect(state, slot, rawHero);
            return true;
        }

        // 7. GOLD_EARNED (Auto-discovers bot/10v10 player slots, heroes, coordinates, and gold!)
        Match goldEarned = GoldEarnedRegex().Match(line);
        if (goldEarned.Success)
        {
            int timeMs = int.Parse(goldEarned.Groups[1].Value);
            float k2X = float.Parse(goldEarned.Groups[2].Value, CultureInfo.InvariantCulture);
            float k2Y = float.Parse(goldEarned.Groups[3].Value, CultureInfo.InvariantCulture);
            int slot = int.Parse(goldEarned.Groups[5].Value);
            int team = int.Parse(goldEarned.Groups[6].Value);
            string source = goldEarned.Groups[7].Success ? goldEarned.Groups[7].Value : string.Empty;
            int owner = goldEarned.Groups[8].Success ? int.Parse(goldEarned.Groups[8].Value) : slot;
            int gold = goldEarned.Groups[9].Success ? int.Parse(goldEarned.Groups[9].Value) : 0;

            state.ElapsedSeconds = Math.Max(state.ElapsedSeconds, timeMs / 1000);
            LivePlayerState p = EnsureGameplaySlotExists(state, slot, team);
            UpdatePlayerPosition(state, p, team, k2X, k2Y);

            if (gold > 0)
            {
                p.CurrentGold += gold;
                int mins = Math.Max(1, state.ElapsedSeconds / 60);
                p.GPM = Math.Max(p.GPM, p.CurrentGold / mins);
            }

            if (owner == slot &&
                source.StartsWith("Hero_", StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrEmpty(p.HeroIdentifier))
            {
                ApplyHeroSelect(state, slot, source, fallbackBotName: true);
            }

            return true;
        }

        // 8. HERO_LEVEL
        Match heroLevel = HeroLevelRegex().Match(line);
        if (heroLevel.Success)
        {
            int timeMs = int.Parse(heroLevel.Groups[1].Value);
            float k2X = float.Parse(heroLevel.Groups[2].Value, CultureInfo.InvariantCulture);
            float k2Y = float.Parse(heroLevel.Groups[3].Value, CultureInfo.InvariantCulture);
            int slot = int.Parse(heroLevel.Groups[5].Value);
            int team = int.Parse(heroLevel.Groups[6].Value);
            int level = int.Parse(heroLevel.Groups[7].Value);

            state.ElapsedSeconds = Math.Max(state.ElapsedSeconds, timeMs / 1000);
            LivePlayerState p = EnsureGameplaySlotExists(state, slot, team);
            p.Level = Math.Max(p.Level, level);
            UpdatePlayerPosition(state, p, team, k2X, k2Y);
            return true;
        }

        // 9. EXP_EARNED (Live Player Coordinates!)
        Match expEarned = ExpEarnedRegex().Match(line);
        if (expEarned.Success)
        {
            int timeMs = int.Parse(expEarned.Groups[1].Value);
            float k2X = float.Parse(expEarned.Groups[2].Value, CultureInfo.InvariantCulture);
            float k2Y = float.Parse(expEarned.Groups[3].Value, CultureInfo.InvariantCulture);
            int slot = int.Parse(expEarned.Groups[5].Value);
            int team = int.Parse(expEarned.Groups[6].Value);

            state.ElapsedSeconds = Math.Max(state.ElapsedSeconds, timeMs / 1000);
            LivePlayerState p = EnsureGameplaySlotExists(state, slot, team);
            UpdatePlayerPosition(state, p, team, k2X, k2Y);
            int mins = Math.Max(1, state.ElapsedSeconds / 60);
            p.XPM = Math.Max(220, p.Level * 280 / mins);
            return true;
        }

        // 10. ITEM_PURCHASE / ITEM_ASSEMBLE / ITEM_SELL
        Match itemPurch = ItemPurchaseRegex().Match(line);
        if (itemPurch.Success)
        {
            int timeMs = int.Parse(itemPurch.Groups[1].Value);
            float k2X = float.Parse(itemPurch.Groups[2].Value, CultureInfo.InvariantCulture);
            float k2Y = float.Parse(itemPurch.Groups[3].Value, CultureInfo.InvariantCulture);
            int slot = int.Parse(itemPurch.Groups[5].Value);
            int team = int.Parse(itemPurch.Groups[6].Value);
            string rawItem = itemPurch.Groups[7].Value;
            int cost = itemPurch.Groups[8].Success ? int.Parse(itemPurch.Groups[8].Value) : 0;

            state.ElapsedSeconds = Math.Max(state.ElapsedSeconds, timeMs / 1000);
            LivePlayerState p = EnsureGameplaySlotExists(state, slot, team);
            UpdatePlayerPosition(state, p, team, k2X, k2Y);

            string item = rawItem.ToLowerInvariant();
            if (!item.EndsWith("_recipe", StringComparison.OrdinalIgnoreCase))
            {
                if (!p.Inventory.Contains(item))
                {
                    if (p.Inventory.Count < 6)
                    {
                        p.Inventory.Add(item);
                    }
                    else if (cost >= 1000)
                    {
                        p.Inventory[^1] = item;
                    }
                }
            }

            if (cost >= 1000 && !item.EndsWith("_recipe", StringComparison.OrdinalIgnoreCase))
            {
                (float normX, float normY) = ConvertK2ToNormalizedScreen(state.Map, k2X, k2Y);
                RecordCombatEvent(state, new LiveCombatEvent
                {
                    EventType = "ITEM",
                    AttackerName = p.AccountName,
                    AttackerHero = p.HeroIdentifier,
                    MatchTimeSeconds = timeMs / 1000,
                    PositionX = normX,
                    PositionY = normY,
                    Team = team,
                    AbilityOrItem = rawItem,
                    Description = $"{p.AccountName} purchased {FormatEntityLabel(rawItem)} ({cost}g)"
                }, includeInRecentBanner: false);
            }

            return true;
        }

        Match itemAssemble = ItemAssembleRegex().Match(line);
        if (itemAssemble.Success)
        {
            int timeMs = int.Parse(itemAssemble.Groups[1].Value);
            float k2X = float.Parse(itemAssemble.Groups[2].Value, CultureInfo.InvariantCulture);
            float k2Y = float.Parse(itemAssemble.Groups[3].Value, CultureInfo.InvariantCulture);
            int slot = int.Parse(itemAssemble.Groups[5].Value);
            int team = int.Parse(itemAssemble.Groups[6].Value);
            string rawItem = itemAssemble.Groups[7].Value;

            state.ElapsedSeconds = Math.Max(state.ElapsedSeconds, timeMs / 1000);
            LivePlayerState p = EnsureGameplaySlotExists(state, slot, team);
            string item = rawItem.ToLowerInvariant();
            if (!p.Inventory.Contains(item))
            {
                if (p.Inventory.Count >= 6)
                {
                    p.Inventory.RemoveAt(0);
                }

                p.Inventory.Add(item);
            }

            (float normX, float normY) = ConvertK2ToNormalizedScreen(state.Map, k2X, k2Y);
            RecordCombatEvent(state, new LiveCombatEvent
            {
                EventType = "ITEM",
                AttackerName = p.AccountName,
                AttackerHero = p.HeroIdentifier,
                MatchTimeSeconds = timeMs / 1000,
                PositionX = normX,
                PositionY = normY,
                Team = team,
                AbilityOrItem = rawItem,
                Description = $"{p.AccountName} completed {FormatEntityLabel(rawItem)}!"
            }, includeInRecentBanner: false);

            return true;
        }

        Match itemSell = ItemSellRegex().Match(line);
        if (itemSell.Success)
        {
            int slot = int.Parse(itemSell.Groups[5].Value);
            string item = itemSell.Groups[7].Value.ToLowerInvariant();
            if (state.PlayersBySlot.TryGetValue(slot, out LivePlayerState? p))
            {
                p.Inventory.Remove(item);
            }

            return true;
        }

        // 11. ITEM_ACTIVATE (Wards, Potions, Portals!)
        Match itemAct = ItemActivateRegex().Match(line);
        if (itemAct.Success)
        {
            int timeMs = int.Parse(itemAct.Groups[1].Value);
            float k2X = float.Parse(itemAct.Groups[2].Value, CultureInfo.InvariantCulture);
            float k2Y = float.Parse(itemAct.Groups[3].Value, CultureInfo.InvariantCulture);
            int slot = int.Parse(itemAct.Groups[5].Value);
            int team = int.Parse(itemAct.Groups[6].Value);
            string item = itemAct.Groups[7].Value;

            state.ElapsedSeconds = Math.Max(state.ElapsedSeconds, timeMs / 1000);
            (float normX, float normY) = ConvertK2ToNormalizedScreen(state.Map, k2X, k2Y);

            if (state.PlayersBySlot.TryGetValue(slot, out LivePlayerState? actingPlayer))
            {
                UpdatePlayerPosition(state, actingPlayer, team, k2X, k2Y);
                if (item.Contains("PortalKey", StringComparison.OrdinalIgnoreCase) ||
                    item.Contains("ShrunkenHead", StringComparison.OrdinalIgnoreCase) ||
                    item.Contains("Astrolabe", StringComparison.OrdinalIgnoreCase) ||
                    item.Contains("PostHaste", StringComparison.OrdinalIgnoreCase))
                {
                    actingPlayer.LastCastAbility = FormatEntityLabel(item);
                }
            }

            if (item.Contains("FlamingEye", StringComparison.OrdinalIgnoreCase) ||
                item.Contains("WardSight", StringComparison.OrdinalIgnoreCase) ||
                item.Contains("VeiledRot", StringComparison.OrdinalIgnoreCase) ||
                item.Contains("Ward", StringComparison.OrdinalIgnoreCase))
            {
                string wardType =
                    item.Contains("VeiledRot", StringComparison.OrdinalIgnoreCase) ||
                    item.Contains("WardRev", StringComparison.OrdinalIgnoreCase)
                        ? "Revelation"
                        : "Sight";
                string placerName = state.PlayersBySlot.TryGetValue(slot, out LivePlayerState? p)
                    ? p.AccountName
                    : $"Player #{slot + 1}";

                state.ActiveWards.Add(new LiveWardState
                {
                    WardType = wardType,
                    Team = team,
                    PositionX = normX,
                    PositionY = normY,
                    PlacedAtSecond = timeMs / 1000,
                    CurrentElapsedSeconds = state.ElapsedSeconds,
                    VisionRadius = wardType == "Sight" ? 0.12f : 0.10f,
                    PlacerHeroName = placerName
                });
            }

            return true;
        }

        // 12. ABILITY_UPGRADE & ABILITY_ACTIVATE
        Match abUpgrade = AbilityUpgradeRegex().Match(line);
        if (abUpgrade.Success)
        {
            int timeMs = int.Parse(abUpgrade.Groups[1].Value);
            float k2X = float.Parse(abUpgrade.Groups[2].Value, CultureInfo.InvariantCulture);
            float k2Y = float.Parse(abUpgrade.Groups[3].Value, CultureInfo.InvariantCulture);
            int slot = int.Parse(abUpgrade.Groups[5].Value);
            int team = int.Parse(abUpgrade.Groups[6].Value);
            string abName = abUpgrade.Groups[7].Value;
            int abLevel = abUpgrade.Groups[8].Success ? int.Parse(abUpgrade.Groups[8].Value) : 1;
            int abSlot = abUpgrade.Groups[9].Success ? int.Parse(abUpgrade.Groups[9].Value) : -1;

            state.ElapsedSeconds = Math.Max(state.ElapsedSeconds, timeMs / 1000);
            LivePlayerState p = EnsureGameplaySlotExists(state, slot, team);
            UpdatePlayerPosition(state, p, team, k2X, k2Y);
            p.LastCastAbility = abName;

            EnsureAbilitySlotsInitialized(p);
            int targetIdx = abSlot is >= 0 and < 4 ? abSlot : InferAbilitySlotIndex(abName);
            if (targetIdx is >= 0 and < 4 && targetIdx < p.Abilities.Count)
            {
                p.Abilities[targetIdx].Level = Math.Max(p.Abilities[targetIdx].Level, abLevel);
                p.Abilities[targetIdx].Name = FormatEntityLabel(abName);
            }

            return true;
        }

        Match abActivate = AbilityActivateRegex().Match(line);
        if (abActivate.Success)
        {
            int timeMs = int.Parse(abActivate.Groups[1].Value);
            float k2X = float.Parse(abActivate.Groups[2].Value, CultureInfo.InvariantCulture);
            float k2Y = float.Parse(abActivate.Groups[3].Value, CultureInfo.InvariantCulture);
            int slot = int.Parse(abActivate.Groups[5].Value);
            int team = int.Parse(abActivate.Groups[6].Value);
            string abName = abActivate.Groups[7].Value;
            int abSlot = abActivate.Groups[9].Success ? int.Parse(abActivate.Groups[9].Value) : -1;

            int sec = timeMs / 1000;
            state.ElapsedSeconds = Math.Max(state.ElapsedSeconds, sec);
            LivePlayerState p = EnsureGameplaySlotExists(state, slot, team);
            UpdatePlayerPosition(state, p, team, k2X, k2Y);

            string friendlySpell = FormatEntityLabel(abName);
            p.LastCastAbility = friendlySpell;

            EnsureAbilitySlotsInitialized(p);
            int targetIdx = abSlot is >= 0 and < 4 ? abSlot : InferAbilitySlotIndex(abName);
            if (targetIdx is >= 0 and < 4 && targetIdx < p.Abilities.Count)
            {
                p.Abilities[targetIdx].Name = friendlySpell;
                p.Abilities[targetIdx].CooldownRemaining = Math.Max(2, p.Abilities[targetIdx].CooldownTotal / 2);
            }

            // Record ultimate & signature spell casts cleanly with per-slot cooldown throttling to prevent feed spam
            bool isUltimate = targetIdx == 3 || abSlot == 3;
            int minPinGapSec = isUltimate ? 8 : 15;
            int lastPinSec = state.LastSpellPinSecBySlot.TryGetValue(slot, out int prevSec) ? prevSec : -999;
            if (sec - lastPinSec >= minPinGapSec)
            {
                state.LastSpellPinSecBySlot[slot] = sec;
                (float normX, float normY) = ConvertK2ToNormalizedScreen(state.Map, k2X, k2Y);
                RecordCombatEvent(state, new LiveCombatEvent
                {
                    EventType = "SPELL",
                    AttackerName = p.AccountName,
                    AttackerHero = p.HeroIdentifier,
                    MatchTimeSeconds = sec,
                    PositionX = normX,
                    PositionY = normY,
                    Team = team,
                    AbilityOrItem = abName,
                    Description = $"{p.AccountName} unleashed {friendlySpell}!"
                }, includeInRecentBanner: false);
            }

            return true;
        }

        // 13. HERO_DEATH, HERO_RESPAWN, HERO_ASSIST
        Match heroDeath = HeroDeathRegex().Match(line);
        if (heroDeath.Success)
        {
            int timeMs = int.Parse(heroDeath.Groups[1].Value);
            float k2X = float.Parse(heroDeath.Groups[2].Value, CultureInfo.InvariantCulture);
            float k2Y = float.Parse(heroDeath.Groups[3].Value, CultureInfo.InvariantCulture);
            int slot = int.Parse(heroDeath.Groups[5].Value);
            int team = int.Parse(heroDeath.Groups[6].Value);

            state.ElapsedSeconds = Math.Max(state.ElapsedSeconds, timeMs / 1000);
            LivePlayerState p = EnsureGameplaySlotExists(state, slot, team);
            UpdatePlayerPosition(state, p, team, k2X, k2Y);
            p.IsAlive = false;
            p.HealthPercent = 0;
            p.ManaPercent = 0;
            p.Deaths++;
            state.LastDeathTimeMsBySlot[slot] = timeMs;
            return true;
        }

        Match heroRespawn = HeroRespawnRegex().Match(line);
        if (heroRespawn.Success)
        {
            int timeMs = int.Parse(heroRespawn.Groups[1].Value);
            int slot = int.Parse(heroRespawn.Groups[5].Value);
            int team = int.Parse(heroRespawn.Groups[6].Value);

            state.ElapsedSeconds = Math.Max(state.ElapsedSeconds, timeMs / 1000);
            LivePlayerState p = EnsureGameplaySlotExists(state, slot, team);
            (float wellU, float wellV) = K2MapCoordinateRegistry.GetTeamFountain(state.Map, team);
            p.PositionX = wellU;
            p.PositionY = wellV;
            p.IsAlive = true;
            p.HealthPercent = 100;
            p.ManaPercent = 100;
            p.LastCastAbility = null;
            return true;
        }

        Match heroAssist = HeroAssistRegex().Match(line);
        if (heroAssist.Success)
        {
            int timeMs = int.Parse(heroAssist.Groups[1].Value);
            int slot = int.Parse(heroAssist.Groups[5].Value);
            int team = int.Parse(heroAssist.Groups[6].Value);

            state.ElapsedSeconds = Math.Max(state.ElapsedSeconds, timeMs / 1000);
            LivePlayerState p = EnsureGameplaySlotExists(state, slot, team);
            p.Assists++;
            return true;
        }

        // 14. KILL (Hero Takedowns & Structure Destructions)
        Match kill = KillRegex().Match(line);
        if (kill.Success)
        {
            int timeMs = int.Parse(kill.Groups[1].Value);
            float k2X = float.Parse(kill.Groups[2].Value, CultureInfo.InvariantCulture);
            float k2Y = float.Parse(kill.Groups[3].Value, CultureInfo.InvariantCulture);
            int? killerSlot = kill.Groups[5].Success ? int.Parse(kill.Groups[5].Value) : null;
            int killerTeam = int.Parse(kill.Groups[6].Value);
            string target = kill.Groups[7].Value;
            string attacker = kill.Groups[8].Value;
            int? victimSlot = kill.Groups[9].Success ? int.Parse(kill.Groups[9].Value) : null;

            int sec = timeMs / 1000;
            state.ElapsedSeconds = Math.Max(state.ElapsedSeconds, sec);
            (float normX, float normY) = ConvertK2ToNormalizedScreen(state.Map, k2X, k2Y);

            if (target.StartsWith("Hero_", StringComparison.OrdinalIgnoreCase))
            {
                if (killerTeam == 1)
                {
                    state.ScoreLegion++;
                }
                else if (killerTeam == 2)
                {
                    state.ScoreHellbourne++;
                }

                string attackerLabel = FormatEntityLabel(attacker);
                if (killerSlot.HasValue)
                {
                    LivePlayerState kp = EnsureGameplaySlotExists(state, killerSlot.Value, killerTeam);
                    UpdatePlayerPosition(state, kp, killerTeam, k2X, k2Y);
                    kp.Kills++;
                    if (string.IsNullOrEmpty(kp.HeroIdentifier) &&
                        attacker.StartsWith("Hero_", StringComparison.OrdinalIgnoreCase))
                    {
                        ApplyHeroSelect(state, killerSlot.Value, attacker, fallbackBotName: true);
                    }

                    attackerLabel = $"{kp.AccountName} ({kp.HeroDisplayName})";
                }

                string victimLabel = FormatEntityLabel(target);
                if (victimSlot.HasValue)
                {
                    int victimTeam = killerTeam == 1 ? 2 : 1;
                    LivePlayerState vp = EnsureGameplaySlotExists(state, victimSlot.Value, victimTeam);
                    if (string.IsNullOrEmpty(vp.HeroIdentifier))
                    {
                        ApplyHeroSelect(state, victimSlot.Value, target, fallbackBotName: true);
                    }

                    if (!state.LastDeathTimeMsBySlot.TryGetValue(victimSlot.Value, out int lastDeathMs) ||
                        Math.Abs(lastDeathMs - timeMs) > 250)
                    {
                        vp.Deaths++;
                        vp.IsAlive = false;
                        vp.HealthPercent = 0;
                        state.LastDeathTimeMsBySlot[victimSlot.Value] = timeMs;
                    }

                    victimLabel = $"{vp.AccountName} ({vp.HeroDisplayName})";
                }

                RecordCombatEvent(state, new LiveCombatEvent
                {
                    EventType = "KILL",
                    AttackerName = attackerLabel,
                    AttackerHero = attacker,
                    VictimName = victimLabel,
                    VictimHero = target,
                    MatchTimeSeconds = sec,
                    PositionX = normX,
                    PositionY = normY,
                    Team = killerTeam,
                    Description = $"{attackerLabel} slew {victimLabel}!"
                });
            }
            else if (target.StartsWith("Building_", StringComparison.OrdinalIgnoreCase))
            {
                string structKey = target.ToLowerInvariant();
                state.Structures[structKey] = false;
                string teamName = killerTeam == 1 ? "Legion" : "Hellbourne";
                RecordCombatEvent(state, new LiveCombatEvent
                {
                    EventType = "TOWER",
                    AttackerName = teamName,
                    AttackerHero = attacker,
                    VictimName = FormatEntityLabel(target),
                    VictimHero = target,
                    MatchTimeSeconds = sec,
                    PositionX = normX,
                    PositionY = normY,
                    Team = killerTeam,
                    Description = $"{teamName} destroyed {FormatEntityLabel(target)}!"
                });
            }

            return true;
        }

        // 15. AWARDS & RUNES (FIRST BLOOD, MULTI-KILLS, POWERUPS)
        Match firstBlood = AwardFirstBloodRegex().Match(line);
        if (firstBlood.Success)
        {
            int timeMs = int.Parse(firstBlood.Groups[1].Value);
            float k2X = float.Parse(firstBlood.Groups[2].Value, CultureInfo.InvariantCulture);
            float k2Y = float.Parse(firstBlood.Groups[3].Value, CultureInfo.InvariantCulture);
            int slot = int.Parse(firstBlood.Groups[5].Value);
            int team = int.Parse(firstBlood.Groups[6].Value);

            (float normX, float normY) = ConvertK2ToNormalizedScreen(state.Map, k2X, k2Y);
            LivePlayerState p = EnsureGameplaySlotExists(state, slot, team);
            RecordCombatEvent(state, new LiveCombatEvent
            {
                EventType = "FIRST_BLOOD",
                AttackerName = p.AccountName,
                AttackerHero = p.HeroIdentifier,
                MatchTimeSeconds = timeMs / 1000,
                PositionX = normX,
                PositionY = normY,
                Team = team,
                Description = $"FIRST BLOOD! {p.AccountName} drew first blood!"
            });
            return true;
        }

        Match multiKill = AwardMultiKillRegex().Match(line);
        if (multiKill.Success)
        {
            int timeMs = int.Parse(multiKill.Groups[1].Value);
            float k2X = float.Parse(multiKill.Groups[2].Value, CultureInfo.InvariantCulture);
            float k2Y = float.Parse(multiKill.Groups[3].Value, CultureInfo.InvariantCulture);
            int slot = int.Parse(multiKill.Groups[5].Value);
            int team = int.Parse(multiKill.Groups[6].Value);
            int count = int.Parse(multiKill.Groups[7].Value);

            (float normX, float normY) = ConvertK2ToNormalizedScreen(state.Map, k2X, k2Y);
            LivePlayerState p = EnsureGameplaySlotExists(state, slot, team);
            string streakLabel = count switch
            {
                2 => "DOUBLE TAP",
                3 => "HAT-TRICK",
                4 => "QUAD KILL",
                >= 5 => "ANNIHILATION",
                _ => "MULTI-KILL"
            };
            RecordCombatEvent(state, new LiveCombatEvent
            {
                EventType = "MULTIKILL",
                AttackerName = p.AccountName,
                AttackerHero = p.HeroIdentifier,
                MatchTimeSeconds = timeMs / 1000,
                PositionX = normX,
                PositionY = normY,
                Team = team,
                Description = $"{streakLabel}! {p.AccountName} ({p.HeroDisplayName})!"
            });
            return true;
        }

        Match powerup = HeroPowerupRegex().Match(line);
        if (powerup.Success)
        {
            int timeMs = int.Parse(powerup.Groups[1].Value);
            float k2X = float.Parse(powerup.Groups[2].Value, CultureInfo.InvariantCulture);
            float k2Y = float.Parse(powerup.Groups[3].Value, CultureInfo.InvariantCulture);
            int slot = int.Parse(powerup.Groups[5].Value);
            int team = int.Parse(powerup.Groups[6].Value);
            string runeType = powerup.Groups[7].Value;

            (float normX, float normY) = ConvertK2ToNormalizedScreen(state.Map, k2X, k2Y);
            LivePlayerState p = EnsureGameplaySlotExists(state, slot, team);
            RecordCombatEvent(state, new LiveCombatEvent
            {
                EventType = "RUNE",
                AttackerName = p.AccountName,
                AttackerHero = p.HeroIdentifier,
                MatchTimeSeconds = timeMs / 1000,
                PositionX = normX,
                PositionY = normY,
                Team = team,
                AbilityOrItem = runeType,
                Description = $"{p.AccountName} bottled {FormatEntityLabel(runeType)} Rune"
            }, includeInRecentBanner: false);
            return true;
        }

        return false;
    }

    private static void RecordCombatEvent(HonLogParsedState state, LiveCombatEvent ev, bool includeInRecentBanner = true)
    {
        if (state.AllTimelineEvents.Count < MaxTimelineEvents)
        {
            state.AllTimelineEvents.Add(ev);
        }

        if (includeInRecentBanner)
        {
            state.RecentEvents.Insert(0, ev);
            if (state.RecentEvents.Count > 25)
            {
                state.RecentEvents.RemoveAt(state.RecentEvents.Count - 1);
            }
        }
    }

    private static LivePlayerState EnsureGameplaySlotExists(HonLogParsedState state, int slot, int team)
    {
        if (!state.PlayersBySlot.TryGetValue(slot, out LivePlayerState? p))
        {
            (float wellU, float wellV) = K2MapCoordinateRegistry.GetTeamFountain(state.Map, team);
            p = new LivePlayerState
            {
                AccountID = 1000 + slot,
                AccountName = $"Slot #{slot + 1}",
                Team = team is 1 or 2 ? team : (slot % 2 == 0 ? 1 : 2),
                HeroIdentifier = string.Empty,
                HeroDisplayName = "Hero",
                Level = 1,
                PositionX = wellU,
                PositionY = wellV,
                HeadingAngle = team == 2 ? -2.356f : 0.785f,
                HealthPercent = 100,
                ManaPercent = 100,
                IsAlive = true,
                CurrentGold = 0
            };
            state.PlayersBySlot[slot] = p;
        }
        else if (team is 1 or 2)
        {
            p.Team = team;
        }

        return p;
    }

    private static void UpdatePlayerPosition(HonLogParsedState state, LivePlayerState p, int team, float k2X, float k2Y)
    {
        if (team is 1 or 2) p.Team = team;
        (float normX, float normY) = ConvertK2ToNormalizedScreen(state.Map, k2X, k2Y);
        float dx = normX - p.PositionX;
        float dy = normY - p.PositionY;
        if (MathF.Abs(dx) > 0.001f || MathF.Abs(dy) > 0.001f)
        {
            p.HeadingAngle = MathF.Atan2(dy, dx);
        }

        p.PositionX = normX;
        p.PositionY = normY;
    }

    private static void EnsurePlayerExists(HonLogParsedState state, int slot, int accountId, string name)
    {
        if (!state.PlayersBySlot.TryGetValue(slot, out LivePlayerState? p))
        {
            (float wellU, float wellV) = K2MapCoordinateRegistry.GetTeamFountain(state.Map, 1);
            p = new LivePlayerState
            {
                AccountID = accountId > 0 ? accountId : slot + 1,
                AccountName = !string.IsNullOrEmpty(name) ? name : $"Player #{slot + 1}",
                Team = 1,
                HeroIdentifier = string.Empty,
                HeroDisplayName = "Picking Hero...",
                Level = 1,
                PositionX = wellU,
                PositionY = wellV,
                HeadingAngle = 0.785f,
                HealthPercent = 100,
                ManaPercent = 100,
                IsAlive = true,
                CurrentGold = 0
            };
            state.PlayersBySlot[slot] = p;
        }
        else
        {
            if (accountId > 0 && (p.AccountID <= 0 || p.AccountID >= 1000)) p.AccountID = accountId;
            if (!string.IsNullOrEmpty(name)) p.AccountName = name;
        }
    }

    private static void ApplyTeamChange(HonLogParsedState state, int slot, int team, bool explicitConnectExpected)
    {
        if (!state.PlayersBySlot.TryGetValue(slot, out LivePlayerState? p))
        {
            string fallbackName = explicitConnectExpected ? "guccidonut" : $"Player #{slot + 1}";
            EnsurePlayerExists(state, slot, explicitConnectExpected ? slot + 1 : 1000 + slot, fallbackName);
            p = state.PlayersBySlot[slot];
        }

        p.Team = team;
        (float wellU, float wellV) = K2MapCoordinateRegistry.GetTeamFountain(state.Map, team);
        p.PositionX = wellU;
        p.PositionY = wellV;
        p.HeadingAngle = team == 2 ? -2.356f : 0.785f;
    }

    private static void ApplyHeroSelect(HonLogParsedState state, int slot, string rawHero, bool fallbackBotName = false)
    {
        if (!state.PlayersBySlot.TryGetValue(slot, out LivePlayerState? p))
        {
            EnsurePlayerExists(state, slot, 1000 + slot, fallbackBotName ? string.Empty : "guccidonut");
            p = state.PlayersBySlot[slot];
        }

        string cleanHero = rawHero.ToLowerInvariant().Replace("hero_", "").Trim();
        string heroId = "hero_" + cleanHero;
        p.HeroIdentifier = heroId;
        p.HeroDisplayName = Heroes.ResolveHeroDisplayName(heroId);
        if (fallbackBotName && (string.IsNullOrEmpty(p.AccountName) || p.AccountName.StartsWith("Slot #", StringComparison.Ordinal)))
        {
            p.AccountName = $"{p.HeroDisplayName} #{slot + 1}";
        }

        p.Abilities = HonReplayParser.GetHeroBaseAbilities(heroId, initialLevel: 1);
    }

    private static void EnsureAbilitySlotsInitialized(LivePlayerState p)
    {
        if (p.Abilities.Count == 4) return;
        p.Abilities = HonReplayParser.GetHeroBaseAbilities(
            !string.IsNullOrEmpty(p.HeroIdentifier) ? p.HeroIdentifier : "hero_devourer",
            initialLevel: 0);
    }

    private static int InferAbilitySlotIndex(string rawAbilityName)
    {
        if (rawAbilityName.EndsWith('1') || rawAbilityName.Contains("_Q", StringComparison.OrdinalIgnoreCase)) return 0;
        if (rawAbilityName.EndsWith('2') || rawAbilityName.Contains("_W", StringComparison.OrdinalIgnoreCase)) return 1;
        if (rawAbilityName.EndsWith('3') || rawAbilityName.Contains("_E", StringComparison.OrdinalIgnoreCase)) return 2;
        if (rawAbilityName.EndsWith('4') || rawAbilityName.Contains("_R", StringComparison.OrdinalIgnoreCase)) return 3;
        return 0;
    }

    private static string FormatGameMode(string rawMode)
    {
        string cleaned = rawMode.Replace("Mode_", "", StringComparison.OrdinalIgnoreCase).Replace("_", " ").Trim();
        return cleaned switch
        {
            "BotMatch" or "Bot" => "Bot Match",
            "AllPick" => "All Pick",
            "SingleDraft" => "Single Draft",
            "BanningPick" => "Banning Pick",
            _ => string.IsNullOrEmpty(cleaned) ? "Public Match" : cleaned
        };
    }

    public static string FormatEntityLabel(string? rawIdentifier)
    {
        if (string.IsNullOrWhiteSpace(rawIdentifier)) return "Unknown";
        string s = rawIdentifier.Trim();
        foreach (string prefix in new[] { "Hero_", "Item_", "Ability_", "Building_", "Powerup_", "Gadget_" })
        {
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                s = s[prefix.Length..];
                break;
            }
        }

        if (rawIdentifier.StartsWith("Hero_", StringComparison.OrdinalIgnoreCase))
        {
            string resolved = Heroes.ResolveHeroDisplayName(rawIdentifier);
            if (!string.IsNullOrWhiteSpace(resolved)) return resolved;
        }

        // Convert PascalCase / underscores into readable words
        s = s.Replace("_", " ");
        s = Regex.Replace(s, @"([a-z])([A-Z])", "$1 $2");
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    private static void ApplyRepick(HonLogParsedState state, int slot)
    {
        if (state.PlayersBySlot.TryGetValue(slot, out LivePlayerState? p))
        {
            p.HeroIdentifier = string.Empty;
            p.HeroDisplayName = "Picking Hero...";
            p.Abilities = [];
        }
    }
}
