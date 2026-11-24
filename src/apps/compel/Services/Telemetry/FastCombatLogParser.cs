// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using ASPIRE.Common.Utilities;

namespace COMPEL.Services.Telemetry;

/// <summary>
///     High-performance, zero-allocation Span-based log parser for Heroes of Newerth game server log events.
///     Replaces repetitive Regular Expression captures with direct Span slicing and CultureInfo.InvariantCulture parsing.
/// </summary>
public static class FastCombatLogParser
{
    private static readonly SearchValues<char> SpaceOrQuote = SearchValues.Create(' ', '\t', '\r', '\n', '"');

    /// <summary>
    ///     Strips ANSI terminal escape sequences using SIMD vector acceleration.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ReadOnlySpan<char> CleanLine(ReadOnlySpan<char> line, Span<char> scratch, out int charsWritten)
    {
        return SimdAnsiStripper.StripAnsi(line, scratch, out charsWritten);
    }

    public static bool TryParseExpEarned(ReadOnlySpan<char> line, out ExpEarnedEvent ev)
    {
        ev = default;
        int idx = line.IndexOf("EXP_EARNED", StringComparison.Ordinal);
        if (idx < 0)
            return false;

        ReadOnlySpan<char> remaining = line[(idx + "EXP_EARNED".Length)..].TrimStart();

        if (!TryExtractInt(remaining, "time:", out int timeMs)) return false;
        if (!TryExtractFloat(remaining, "x:", out float x)) return false;
        if (!TryExtractFloat(remaining, "y:", out float y)) return false;
        if (!TryExtractFloat(remaining, "z:", out float z)) return false;
        if (!TryExtractInt(remaining, "player:", out int player)) return false;
        if (!TryExtractInt(remaining, "team:", out int team)) return false;

        float exp = 2.0f;
        if (TryExtractFloat(remaining, "experience:", out float parsedExp)) exp = parsedExp;

        ev = new ExpEarnedEvent(timeMs, x, y, z, player, team, exp);
        return true;
    }

    public static bool TryParseGoldEarned(ReadOnlySpan<char> line, out GoldEarnedEvent ev)
    {
        ev = default;
        int idx = line.IndexOf("GOLD_EARNED", StringComparison.Ordinal);
        if (idx < 0)
            return false;

        ReadOnlySpan<char> remaining = line[(idx + "GOLD_EARNED".Length)..].TrimStart();

        if (!TryExtractInt(remaining, "time:", out int timeMs)) return false;
        bool hasX = TryExtractFloat(remaining, "x:", out float x);
        bool hasY = TryExtractFloat(remaining, "y:", out float y);
        TryExtractFloat(remaining, "z:", out float z);
        if (!TryExtractInt(remaining, "player:", out int player)) return false;
        if (!TryExtractInt(remaining, "team:", out int team)) return false;
        if (!TryExtractInt(remaining, "gold:", out int gold)) return false;

        ev = new GoldEarnedEvent(timeMs, x, y, z, player, team, gold, hasX && hasY);
        return true;
    }

    public static bool TryParseItemActivate(ReadOnlySpan<char> line, out ItemActivateEvent ev)
    {
        ev = default;
        int idx = line.IndexOf("ITEM_ACTIVATE", StringComparison.Ordinal);
        if (idx < 0)
            return false;

        ReadOnlySpan<char> remaining = line[(idx + "ITEM_ACTIVATE".Length)..].TrimStart();

        if (!TryExtractInt(remaining, "time:", out int timeMs)) return false;
        if (!TryExtractFloat(remaining, "x:", out float x)) return false;
        if (!TryExtractFloat(remaining, "y:", out float y)) return false;
        if (!TryExtractFloat(remaining, "z:", out float z)) return false;
        if (!TryExtractInt(remaining, "player:", out int player)) return false;
        if (!TryExtractInt(remaining, "team:", out int team)) return false;
        if (!TryExtractQuotedString(remaining, "item:", out ReadOnlySpan<char> itemName)) return false;

        ev = new ItemActivateEvent(timeMs, x, y, z, player, team, itemName);
        return true;
    }

    public static bool TryParseItemPurchase(ReadOnlySpan<char> line, out ItemPurchaseEvent ev)
    {
        ev = default;
        int idx = line.IndexOf("ITEM_PURCHASE", StringComparison.Ordinal);
        bool isPurchase = idx >= 0;
        bool isAssemble = false;
        bool isSell = false;
        int prefixLen = "ITEM_PURCHASE".Length;

        if (!isPurchase)
        {
            idx = line.IndexOf("ITEM_ASSEMBLE", StringComparison.Ordinal);
            isAssemble = idx >= 0;
            prefixLen = "ITEM_ASSEMBLE".Length;
        }

        if (!isPurchase && !isAssemble)
        {
            idx = line.IndexOf("ITEM_SELL", StringComparison.Ordinal);
            isSell = idx >= 0;
            prefixLen = "ITEM_SELL".Length;
        }

        if (idx < 0)
            return false;

        ReadOnlySpan<char> remaining = line[(idx + prefixLen)..].TrimStart();

        if (!TryExtractInt(remaining, "time:", out int timeMs)) return false;
        TryExtractFloat(remaining, "x:", out float x);
        TryExtractFloat(remaining, "y:", out float y);
        TryExtractFloat(remaining, "z:", out float z);
        if (!TryExtractInt(remaining, "player:", out int player)) return false;
        TryExtractInt(remaining, "team:", out int team);
        if (!TryExtractQuotedString(remaining, "item:", out ReadOnlySpan<char> itemName)) return false;

        ev = new ItemPurchaseEvent(timeMs, x, y, z, player, team, itemName, isSell, isAssemble);
        return true;
    }

    public static bool TryParseAbilityActivate(ReadOnlySpan<char> line, out AbilityActivateEvent ev)
    {
        ev = default;
        int idx = line.IndexOf("ABILITY_ACTIVATE", StringComparison.Ordinal);
        if (idx < 0)
            return false;

        ReadOnlySpan<char> remaining = line[(idx + "ABILITY_ACTIVATE".Length)..].TrimStart();

        if (!TryExtractInt(remaining, "time:", out int timeMs)) return false;
        TryExtractFloat(remaining, "x:", out float x);
        TryExtractFloat(remaining, "y:", out float y);
        TryExtractFloat(remaining, "z:", out float z);
        if (!TryExtractInt(remaining, "player:", out int player)) return false;
        TryExtractInt(remaining, "team:", out int team);

        // Match both name:"..." and ability:"..."
        if (!TryExtractQuotedString(remaining, "name:", out ReadOnlySpan<char> abilityName) &&
            !TryExtractQuotedString(remaining, "ability:", out abilityName))
            return false;

        ev = new AbilityActivateEvent(timeMs, x, y, z, player, team, abilityName);
        return true;
    }

    public static bool TryParseAbilityUpgrade(ReadOnlySpan<char> line, out AbilityUpgradeEvent ev)
    {
        ev = default;
        int idx = line.IndexOf("ABILITY_UPGRADE", StringComparison.Ordinal);
        if (idx < 0)
            return false;

        ReadOnlySpan<char> remaining = line[(idx + "ABILITY_UPGRADE".Length)..].TrimStart();

        TryExtractInt(remaining, "time:", out int timeMs);
        if (!TryExtractInt(remaining, "player:", out int player)) return false;
        TryExtractInt(remaining, "team:", out int team);

        if (!TryExtractQuotedString(remaining, "name:", out ReadOnlySpan<char> abilityName) &&
            !TryExtractQuotedString(remaining, "ability:", out abilityName))
            return false;

        int level = 1;
        if (TryExtractInt(remaining, "level:", out int parsedLevel)) level = parsedLevel;

        ev = new AbilityUpgradeEvent(timeMs, player, team, abilityName, level);
        return true;
    }

    public static bool TryParseKill(ReadOnlySpan<char> line, out KillEvent ev)
    {
        ev = default;
        int idx = line.IndexOf("KILL", StringComparison.Ordinal);
        if (idx < 0)
            return false;

        ReadOnlySpan<char> remaining = line[(idx + "KILL".Length)..].TrimStart();

        if (!TryExtractInt(remaining, "time:", out int timeMs)) return false;
        TryExtractFloat(remaining, "x:", out float x);
        TryExtractFloat(remaining, "y:", out float y);
        TryExtractFloat(remaining, "z:", out float z);
        TryExtractInt(remaining, "team:", out int team);
        if (!TryExtractQuotedString(remaining, "target:", out ReadOnlySpan<char> target)) return false;
        if (!TryExtractQuotedString(remaining, "attacker:", out ReadOnlySpan<char> attacker)) return false;

        ev = new KillEvent(timeMs, x, y, z, team, target, attacker);
        return true;
    }

    public static bool TryParseHeroLevel(ReadOnlySpan<char> line, out HeroLevelEvent ev)
    {
        ev = default;
        int idx = line.IndexOf("HERO_LEVEL", StringComparison.Ordinal);
        if (idx < 0)
            return false;

        ReadOnlySpan<char> remaining = line[(idx + "HERO_LEVEL".Length)..].TrimStart();

        if (!TryExtractInt(remaining, "time:", out int timeMs)) return false;
        bool hasX = TryExtractFloat(remaining, "x:", out float x);
        bool hasY = TryExtractFloat(remaining, "y:", out float y);
        TryExtractFloat(remaining, "z:", out float z);
        if (!TryExtractInt(remaining, "player:", out int player)) return false;
        if (!TryExtractInt(remaining, "team:", out int team)) return false;
        if (!TryExtractInt(remaining, "level:", out int level)) return false;

        ev = new HeroLevelEvent(timeMs, x, y, z, player, team, level, hasX && hasY);
        return true;
    }

    public static bool TryParsePlayerConnect(ReadOnlySpan<char> line, out PlayerConnectEvent ev)
    {
        ev = default;
        int idx = line.IndexOf("PLAYER_CONNECT", StringComparison.Ordinal);
        if (idx < 0)
            return false;

        ReadOnlySpan<char> remaining = line[(idx + "PLAYER_CONNECT".Length)..].TrimStart();

        if (!TryExtractInt(remaining, "player:", out int player)) return false;
        if (!TryExtractQuotedString(remaining, "name:", out ReadOnlySpan<char> name)) return false;
        if (!TryExtractInt(remaining, "id:", out int accountId)) return false;

        ev = new PlayerConnectEvent(player, name, accountId);
        return true;
    }

    public static bool TryParsePlayerTeamChange(ReadOnlySpan<char> line, out PlayerTeamChangeEvent ev)
    {
        ev = default;
        int idx = line.IndexOf("PLAYER_TEAM_CHANGE", StringComparison.Ordinal);
        if (idx < 0)
            return false;

        ReadOnlySpan<char> remaining = line[(idx + "PLAYER_TEAM_CHANGE".Length)..].TrimStart();

        if (!TryExtractInt(remaining, "player:", out int player)) return false;
        if (!TryExtractInt(remaining, "team:", out int team)) return false;

        ev = new PlayerTeamChangeEvent(player, team);
        return true;
    }

    public static bool TryParsePlayerSelect(ReadOnlySpan<char> line, out PlayerSelectEvent ev)
    {
        ev = default;
        int idx = line.IndexOf("PLAYER_SELECT", StringComparison.Ordinal);
        int prefixLen = "PLAYER_SELECT".Length;

        if (idx < 0)
        {
            idx = line.IndexOf("PLAYER_RANDOM", StringComparison.Ordinal);
            prefixLen = "PLAYER_RANDOM".Length;
        }

        if (idx < 0)
            return false;

        ReadOnlySpan<char> remaining = line[(idx + prefixLen)..].TrimStart();

        if (!TryExtractInt(remaining, "player:", out int player)) return false;
        if (!TryExtractQuotedString(remaining, "hero:", out ReadOnlySpan<char> hero)) return false;

        ev = new PlayerSelectEvent(player, hero);
        return true;
    }

    public static bool TryParseVoiceClientAdd(ReadOnlySpan<char> line, out VoiceClientAddEvent ev)
    {
        ev = default;
        int addClientIdx = line.IndexOf("Adding client num", StringComparison.OrdinalIgnoreCase);
        if (addClientIdx < 0)
            return false;

        ReadOnlySpan<char> afterAdd = line[(addClientIdx + "Adding client num".Length)..].TrimStart();
        int spaceOrComma = afterAdd.IndexOfAny([' ', ',', '\t']);
        ReadOnlySpan<char> slotToken = spaceOrComma >= 0 ? afterAdd[..spaceOrComma] : afterAdd;

        if (!int.TryParse(slotToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out int slot))
            return false;

        int idIdx = line.IndexOf("ID:", StringComparison.OrdinalIgnoreCase);
        if (idIdx < 0)
            return false;

        ReadOnlySpan<char> afterId = line[(idIdx + "ID:".Length)..].TrimStart();
        int endId = afterId.IndexOfAny(SpaceOrQuote);
        ReadOnlySpan<char> idToken = endId >= 0 ? afterId[..endId] : afterId;

        if (!int.TryParse(idToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out int accountId))
            return false;

        ReadOnlySpan<char> explicitName = [];
        if (spaceOrComma >= 0)
        {
            ReadOnlySpan<char> middle = afterAdd[spaceOrComma..];
            int idPosInMiddle = middle.IndexOf("ID:", StringComparison.OrdinalIgnoreCase);
            if (idPosInMiddle > 0)
            {
                ReadOnlySpan<char> candidate = middle[..idPosInMiddle].Trim([' ', ',', '\t']);
                if (!candidate.IsEmpty) explicitName = candidate;
            }
        }

        ev = new VoiceClientAddEvent(slot, explicitName, accountId);
        return true;
    }

    public static bool TryParseTeamJoined(ReadOnlySpan<char> line, out TeamJoinedEvent ev)
    {
        ev = default;
        int clientIdx = line.IndexOf("Client", StringComparison.OrdinalIgnoreCase);
        if (clientIdx < 0)
            return false;

        ReadOnlySpan<char> clientLine = line[clientIdx..];
        int joinedTeamIdx = clientLine.IndexOf("joined team:", StringComparison.OrdinalIgnoreCase);
        if (joinedTeamIdx < 0)
            return false;

        ReadOnlySpan<char> clientPart = clientLine["Client".Length..joinedTeamIdx].Trim([' ', '#', '\t']);
        if (!int.TryParse(clientPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out int slot))
            return false;

        ReadOnlySpan<char> teamPart = clientLine[(joinedTeamIdx + "joined team:".Length)..].TrimStart();
        int endTeam = teamPart.IndexOfAny(SpaceOrQuote);
        ReadOnlySpan<char> teamToken = endTeam >= 0 ? teamPart[..endTeam] : teamPart;

        if (!int.TryParse(teamToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out int team))
            return false;

        ev = new TeamJoinedEvent(slot, team);
        return true;
    }

    public static bool TryParseRepick(ReadOnlySpan<char> line, out RepickEvent ev)
    {
        ev = default;
        int clientIdx = line.IndexOf("Client", StringComparison.OrdinalIgnoreCase);
        if (clientIdx < 0)
            return false;

        ReadOnlySpan<char> clientLine = line[clientIdx..];
        int repickedIdx = clientLine.IndexOf("repicked", StringComparison.OrdinalIgnoreCase);
        if (repickedIdx < 0) repickedIdx = clientLine.IndexOf("unselected", StringComparison.OrdinalIgnoreCase);

        if (repickedIdx < 0)
            return false;

        ReadOnlySpan<char> clientPart = clientLine["Client".Length..repickedIdx].Trim([' ', '#', '\t']);
        if (!int.TryParse(clientPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out int slot))
            return false;

        ev = new RepickEvent(slot);
        return true;
    }

    public static bool TryParseHeroPick(ReadOnlySpan<char> line, out HeroPickEvent ev)
    {
        ev = default;
        int clientIdx = line.IndexOf("Client", StringComparison.OrdinalIgnoreCase);
        if (clientIdx < 0)
            return false;

        ReadOnlySpan<char> clientLine = line[clientIdx..];
        int selectedIdx = clientLine.IndexOf("selected", StringComparison.OrdinalIgnoreCase);
        int selLen = "selected".Length;
        if (selectedIdx < 0)
        {
            selectedIdx = clientLine.IndexOf("picked", StringComparison.OrdinalIgnoreCase);
            selLen = "picked".Length;
        }

        if (selectedIdx < 0)
            return false;

        ReadOnlySpan<char> clientPart = clientLine["Client".Length..selectedIdx].Trim([' ', '#', '\t']);
        if (!int.TryParse(clientPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out int slot))
            return false;

        ReadOnlySpan<char> afterKeyword = clientLine[(selectedIdx + selLen)..].TrimStart();
        if (afterKeyword.StartsWith("hero:", StringComparison.OrdinalIgnoreCase))
            afterKeyword = afterKeyword["hero:".Length..].TrimStart();

        int heroIdx = afterKeyword.IndexOf("Hero_", StringComparison.OrdinalIgnoreCase);
        if (heroIdx < 0)
            return false;

        ReadOnlySpan<char> heroSpan = afterKeyword[heroIdx..];
        int endHero = heroSpan.IndexOfAny(SpaceOrQuote);
        ReadOnlySpan<char> heroToken = endHero >= 0 ? heroSpan[..endHero] : heroSpan;

        ev = new HeroPickEvent(slot, heroToken);
        return true;
    }

    public static bool TryParseChatMessage(ReadOnlySpan<char> line, out ChatMessageEvent ev)
    {
        ev = default;
        int allIdx = line.IndexOf("[ALL]", StringComparison.OrdinalIgnoreCase);
        int teamIdx = line.IndexOf("[TEAM]", StringComparison.OrdinalIgnoreCase);

        if (allIdx >= 0)
        {
            ReadOnlySpan<char> afterTag = line[(allIdx + "[ALL]".Length)..].TrimStart();
            int colonIdx = afterTag.IndexOf(':');
            if (colonIdx > 0)
            {
                ReadOnlySpan<char> name = afterTag[..colonIdx].Trim();
                ev = new ChatMessageEvent(line.Slice(allIdx, "[ALL]".Length), name);
                return true;
            }
        }
        else if (teamIdx >= 0)
        {
            ReadOnlySpan<char> afterTag = line[(teamIdx + "[TEAM]".Length)..].TrimStart();
            int colonIdx = afterTag.IndexOf(':');
            if (colonIdx > 0)
            {
                ReadOnlySpan<char> name = afterTag[..colonIdx].Trim();
                ev = new ChatMessageEvent(line.Slice(teamIdx, "[TEAM]".Length), name);
                return true;
            }
        }

        return false;
    }

    public static bool TryParseMatchId(ReadOnlySpan<char> line, out int matchId)
    {
        matchId = 0;
        int idx = line.IndexOf("match id:", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            ReadOnlySpan<char> afterKey = line[(idx + "match id:".Length)..].TrimStart(" :=\"\t");
            return TryParseMatchIdToken(afterKey, out matchId);
        }

        idx = line.IndexOf("startgame - matchid:", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            ReadOnlySpan<char> afterKey = line[(idx + "startgame - matchid:".Length)..].TrimStart(" :=\"\t");
            return TryParseMatchIdToken(afterKey, out matchId);
        }

        idx = line.IndexOf("MatchID:", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            ReadOnlySpan<char> afterKey = line[(idx + "MatchID:".Length)..].TrimStart(" :=\"\t");
            return TryParseMatchIdToken(afterKey, out matchId);
        }

        idx = line.IndexOf("MatchID", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            ReadOnlySpan<char> afterKey = line[(idx + "MatchID".Length)..].TrimStart(" :=\"\t");
            return TryParseMatchIdToken(afterKey, out matchId);
        }

        idx = line.IndexOf("match_id", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            ReadOnlySpan<char> afterKey = line[(idx + "match_id".Length)..].TrimStart(" :,[=\"\tINT");
            if (TryParseMatchIdToken(afterKey, out matchId))
                return true;
        }

        if (line.StartsWith("M", StringComparison.Ordinal) && line.Length >= 7 && char.IsDigit(line[1]))
            return TryParseDigits(line[1..], out matchId, requireClosingBracket: false);

        return false;
    }

    private static bool TryParseMatchIdToken(ReadOnlySpan<char> afterKey, out int matchId)
    {
        bool hasOpeningBracket = !afterKey.IsEmpty && afterKey[0] == '[';
        if (hasOpeningBracket)
        {
            afterKey = afterKey[1..];
        }

        return TryParseDigits(afterKey, out matchId, requireClosingBracket: hasOpeningBracket);
    }

    private static bool TryParseDigits(ReadOnlySpan<char> span, out int value, bool requireClosingBracket = false)
    {
        value = 0;
        int endIdx = 0;
        while (endIdx < span.Length && char.IsDigit(span[endIdx])) endIdx++;

        if (endIdx < 1)
            return false;

        if (requireClosingBracket)
        {
            if (endIdx >= span.Length || span[endIdx] != ']')
                return false;

            int afterBracketIdx = endIdx + 1;
            if (afterBracketIdx < span.Length && !IsAllowedTrailingDelimiter(span[afterBracketIdx]))
                return false;
        }
        else if (endIdx < span.Length && !IsAllowedTrailingDelimiter(span[endIdx]))
        {
            // Reject unmatched ']' (e.g. "match_id, INT: 143426]") or attached garbage tokens without a delimiter.
            return false;
        }

        return int.TryParse(span[..endIdx], NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool IsAllowedTrailingDelimiter(char c) =>
        char.IsWhiteSpace(c) || c is ',' or ';' or '.' or ')';

    private static bool TryExtractInt(ReadOnlySpan<char> span, string key, out int value)
    {
        value = 0;
        int idx = span.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return false;

        ReadOnlySpan<char> afterKey = span[(idx + key.Length)..].TrimStart();
        int endIdx = afterKey.IndexOfAny(SpaceOrQuote);
        ReadOnlySpan<char> token = endIdx >= 0 ? afterKey[..endIdx] : afterKey;

        return int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryExtractFloat(ReadOnlySpan<char> span, string key, out float value)
    {
        value = 0.0f;
        int idx = span.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return false;

        ReadOnlySpan<char> afterKey = span[(idx + key.Length)..].TrimStart();
        int endIdx = afterKey.IndexOfAny(SpaceOrQuote);
        ReadOnlySpan<char> token = endIdx >= 0 ? afterKey[..endIdx] : afterKey;

        return float.TryParse(token, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture,
            out value);
    }

    private static bool TryExtractQuotedString(ReadOnlySpan<char> span, string key, out ReadOnlySpan<char> value)
    {
        value = default;
        int idx = span.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return false;

        ReadOnlySpan<char> afterKey = span[(idx + key.Length)..].TrimStart();
        if (afterKey.IsEmpty) return false;

        if (afterKey[0] == '"')
        {
            ReadOnlySpan<char> insideQuote = afterKey[1..];
            int closingQuote = insideQuote.IndexOf('"');
            if (closingQuote >= 0)
            {
                value = insideQuote[..closingQuote];
                return true;
            }

            value = insideQuote;
            return true;
        }

        int spaceIdx = afterKey.IndexOf(' ');
        value = spaceIdx >= 0 ? afterKey[..spaceIdx] : afterKey;
        return true;
    }

    public readonly ref struct ExpEarnedEvent(
        int timeMs,
        float x,
        float y,
        float z,
        int playerSlot,
        int team,
        float experience)
    {
        public readonly int TimeMs = timeMs;
        public readonly float X = x;
        public readonly float Y = y;
        public readonly float Z = z;
        public readonly int PlayerSlot = playerSlot;
        public readonly int Team = team;
        public readonly float Experience = experience;
        public readonly bool Success = true;
    }

    public readonly ref struct GoldEarnedEvent(
        int timeMs,
        float x,
        float y,
        float z,
        int playerSlot,
        int team,
        int gold,
        bool hasCoordinates)
    {
        public readonly int TimeMs = timeMs;
        public readonly float X = x;
        public readonly float Y = y;
        public readonly float Z = z;
        public readonly int PlayerSlot = playerSlot;
        public readonly int Team = team;
        public readonly int Gold = gold;
        public readonly bool HasCoordinates = hasCoordinates;
        public readonly bool Success = true;
    }

    public readonly ref struct ItemActivateEvent
    {
        public readonly int TimeMs;
        public readonly float X;
        public readonly float Y;
        public readonly float Z;
        public readonly int PlayerSlot;
        public readonly int Team;
        public readonly ReadOnlySpan<char> ItemName;
        public readonly bool Success;

        public ItemActivateEvent(int timeMs, float x, float y, float z, int playerSlot, int team,
            ReadOnlySpan<char> itemName)
        {
            TimeMs = timeMs;
            X = x;
            Y = y;
            Z = z;
            PlayerSlot = playerSlot;
            Team = team;
            ItemName = itemName;
            Success = true;
        }
    }

    public readonly ref struct ItemPurchaseEvent
    {
        public readonly int TimeMs;
        public readonly float X;
        public readonly float Y;
        public readonly float Z;
        public readonly int PlayerSlot;
        public readonly int Team;
        public readonly ReadOnlySpan<char> ItemName;
        public readonly bool IsSell;
        public readonly bool IsAssemble;
        public readonly bool Success;

        public ItemPurchaseEvent(int timeMs, float x, float y, float z, int playerSlot, int team,
            ReadOnlySpan<char> itemName, bool isSell, bool isAssemble)
        {
            TimeMs = timeMs;
            X = x;
            Y = y;
            Z = z;
            PlayerSlot = playerSlot;
            Team = team;
            ItemName = itemName;
            IsSell = isSell;
            IsAssemble = isAssemble;
            Success = true;
        }
    }

    public readonly ref struct AbilityActivateEvent
    {
        public readonly int TimeMs;
        public readonly float X;
        public readonly float Y;
        public readonly float Z;
        public readonly int PlayerSlot;
        public readonly int Team;
        public readonly ReadOnlySpan<char> AbilityName;
        public readonly bool Success;

        public AbilityActivateEvent(int timeMs, float x, float y, float z, int playerSlot, int team,
            ReadOnlySpan<char> abilityName)
        {
            TimeMs = timeMs;
            X = x;
            Y = y;
            Z = z;
            PlayerSlot = playerSlot;
            Team = team;
            AbilityName = abilityName;
            Success = true;
        }
    }

    public readonly ref struct AbilityUpgradeEvent
    {
        public readonly int TimeMs;
        public readonly int PlayerSlot;
        public readonly int Team;
        public readonly ReadOnlySpan<char> AbilityName;
        public readonly int Level;
        public readonly bool Success;

        public AbilityUpgradeEvent(int timeMs, int playerSlot, int team, ReadOnlySpan<char> abilityName, int level)
        {
            TimeMs = timeMs;
            PlayerSlot = playerSlot;
            Team = team;
            AbilityName = abilityName;
            Level = level;
            Success = true;
        }
    }

    public readonly ref struct KillEvent
    {
        public readonly int TimeMs;
        public readonly float X;
        public readonly float Y;
        public readonly float Z;
        public readonly int Team;
        public readonly ReadOnlySpan<char> Target;
        public readonly ReadOnlySpan<char> Attacker;
        public readonly bool Success;

        public KillEvent(int timeMs, float x, float y, float z, int team, ReadOnlySpan<char> target,
            ReadOnlySpan<char> attacker)
        {
            TimeMs = timeMs;
            X = x;
            Y = y;
            Z = z;
            Team = team;
            Target = target;
            Attacker = attacker;
            Success = true;
        }
    }

    public readonly ref struct HeroLevelEvent(
        int timeMs,
        float x,
        float y,
        float z,
        int playerSlot,
        int team,
        int level,
        bool hasCoordinates = true)
    {
        public readonly int TimeMs = timeMs;
        public readonly float X = x;
        public readonly float Y = y;
        public readonly float Z = z;
        public readonly int PlayerSlot = playerSlot;
        public readonly int Team = team;
        public readonly int Level = level;
        public readonly bool HasCoordinates = hasCoordinates;
        public readonly bool Success = true;
    }

    public readonly ref struct PlayerConnectEvent
    {
        public readonly int PlayerSlot;
        public readonly ReadOnlySpan<char> Name;
        public readonly int AccountId;
        public readonly bool Success;

        public PlayerConnectEvent(int playerSlot, ReadOnlySpan<char> name, int accountId)
        {
            PlayerSlot = playerSlot;
            Name = name;
            AccountId = accountId;
            Success = true;
        }
    }

    public readonly ref struct PlayerTeamChangeEvent(int playerSlot, int team)
    {
        public readonly int PlayerSlot = playerSlot;
        public readonly int Team = team;
        public readonly bool Success = true;
    }

    public readonly ref struct PlayerSelectEvent
    {
        public readonly int PlayerSlot;
        public readonly ReadOnlySpan<char> HeroIdentifier;
        public readonly bool Success;

        public PlayerSelectEvent(int playerSlot, ReadOnlySpan<char> heroIdentifier)
        {
            PlayerSlot = playerSlot;
            HeroIdentifier = heroIdentifier;
            Success = true;
        }
    }

    public readonly ref struct VoiceClientAddEvent
    {
        public readonly int PlayerSlot;
        public readonly ReadOnlySpan<char> ExplicitName;
        public readonly int AccountId;
        public readonly bool Success;

        public VoiceClientAddEvent(int playerSlot, ReadOnlySpan<char> explicitName, int accountId)
        {
            PlayerSlot = playerSlot;
            ExplicitName = explicitName;
            AccountId = accountId;
            Success = true;
        }
    }

    public readonly ref struct TeamJoinedEvent(int playerSlot, int team)
    {
        public readonly int PlayerSlot = playerSlot;
        public readonly int Team = team;
        public readonly bool Success = true;
    }

    public readonly ref struct RepickEvent(int playerSlot)
    {
        public readonly int PlayerSlot = playerSlot;
        public readonly bool Success = true;
    }

    public readonly ref struct HeroPickEvent
    {
        public readonly int PlayerSlot;
        public readonly ReadOnlySpan<char> HeroIdentifier;
        public readonly bool Success;

        public HeroPickEvent(int playerSlot, ReadOnlySpan<char> heroIdentifier)
        {
            PlayerSlot = playerSlot;
            HeroIdentifier = heroIdentifier;
            Success = true;
        }
    }

    public readonly ref struct ChatMessageEvent
    {
        public readonly ReadOnlySpan<char> Channel;
        public readonly ReadOnlySpan<char> AccountName;
        public readonly bool Success;

        public ChatMessageEvent(ReadOnlySpan<char> channel, ReadOnlySpan<char> accountName)
        {
            Channel = channel;
            AccountName = accountName;
            Success = true;
        }
    }
}
