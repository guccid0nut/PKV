// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: ReplayMetadata.cs
// System: NEXUS Replay Video Renderer Service
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.Text;
using System.Text.RegularExpressions;

namespace ReplayRenderer.Service.Replay;

/// <summary>
///     Match confidence level for player resolution.
/// </summary>
public enum PlayerMatchConfidence
{
    Exact,
    Fuzzy,
    Fallback
}

/// <summary>
///     Detailed result of resolving a player camera target query.
/// </summary>
public sealed class PlayerResolutionResult
{
    public int ClientIndex { get; set; }
    public ReplayPlayerMetadata? Player { get; set; }
    public PlayerMatchConfidence Confidence { get; set; }
    public string MatchReason { get; set; } = string.Empty;
    public string Query { get; set; } = string.Empty;

    public override string ToString()
    {
        string playerName = Player != null ? $"{Player.Name} ({Player.CleanHeroName})" : "Unassigned";
        return $"Resolved[Slot={ClientIndex}, Player={playerName}, Confidence={Confidence}, Reason='{MatchReason}']";
    }
}

/// <summary>
///     Represents individual player metadata and match statistics extracted from the .honreplay replayinfo XML.
/// </summary>
public sealed class ReplayPlayerMetadata
{
    public string Name { get; set; } = string.Empty;
    public long AccountId { get; set; }
    public int Team { get; set; } // 1 = Legion, 2 = Hellbourne
    public int TeamIndex { get; set; } // 0..4 within team
    public int ClientNum { get; set; } // 0..9 in match
    public string HeroName { get; set; } = string.Empty;
    public string HeroIcon { get; set; } = string.Empty;
    public string Avatar { get; set; } = string.Empty;

    // Detailed Match Statistics
    public int Kills { get; set; }
    public int Deaths { get; set; }
    public int Assists { get; set; }
    public long HeroDamage { get; set; }
    public long TowerDamage { get; set; }
    public long CreepDamage { get; set; }
    public int Gold { get; set; }
    public int GoldPerMinute { get; set; }
    public int ExpPerMinute { get; set; }
    public int Level { get; set; }
    public int Denies { get; set; }
    public int Creeps { get; set; }
    public int Actions { get; set; }
    public double APM { get; set; }
    public int Wards { get; set; }
    public int Consumables { get; set; }
    public int Smackdowns { get; set; }
    public int Humiliations { get; set; }
    public int Disconnects { get; set; }

    /// <summary>
    ///     Friendly team name ("Legion" for Team 1, "Hellbourne" for Team 2).
    /// </summary>
    public string TeamName => Team switch
    {
        1 => "Legion",
        2 => "Hellbourne",
        _ => "Spectator"
    };

    public bool IsLegion => Team == 1;
    public bool IsHellbourne => Team == 2;

    /// <summary>
    ///     Clean hero name stripped of "Hero_" prefix (e.g. "Hero_BabaYaga" -> "BabaYaga").
    /// </summary>
    public string CleanHeroName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(HeroName)) return string.Empty;

            if (HeroName.StartsWith("Hero_", StringComparison.OrdinalIgnoreCase)) return HeroName.Substring(5);

            return HeroName;
        }
    }

    /// <summary>
    ///     Formatted Kill/Death/Assist string (e.g. "12/2/15").
    /// </summary>
    public string FormattedKda => $"{Kills}/{Deaths}/{Assists}";

    public override string ToString()
    {
        return
            $"Player[ClientNum={ClientNum}, Team={TeamName}({TeamIndex}), Name='{Name}', Hero='{CleanHeroName}', KDA={FormattedKda}, GPM={GoldPerMinute}]";
    }
}

/// <summary>
///     Represents parsed match and player metadata from a .honreplay replayinfo entry.
/// </summary>
public sealed class ReplayMetadata
{
    public string Version { get; set; } = string.Empty;
    public string GameMode { get; set; } = string.Empty;
    public string GameOptions { get; set; } = string.Empty;
    public string ServerName { get; set; } = string.Empty;
    public string MapName { get; set; } = string.Empty;
    public string MapFancyName { get; set; } = string.Empty;
    public int TeamSize { get; set; }
    public string GameName { get; set; } = string.Empty;
    public string MatchId { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public string Winner { get; set; } = string.Empty;
    public string Date { get; set; } = string.Empty;
    public string Time { get; set; } = string.Empty;
    public long MatchLengthMs { get; set; }
    public long ConcedeTimeMs { get; set; }
    public long FirstBloodTimeMs { get; set; }

    /// <summary>
    ///     Match duration calculated from MatchLengthMs.
    /// </summary>
    public TimeSpan MatchLength => TimeSpan.FromMilliseconds(MatchLengthMs);

    /// <summary>
    ///     Friendly name of the winning team ("Legion" or "Hellbourne").
    /// </summary>
    public string WinnerTeamName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Winner)) return "Unknown";

            if (Winner.Contains("legion", StringComparison.OrdinalIgnoreCase) ||
                Winner.Equals("1", StringComparison.Ordinal)) return "Legion";

            if (Winner.Contains("hellbourne", StringComparison.OrdinalIgnoreCase) ||
                Winner.Equals("2", StringComparison.Ordinal)) return "Hellbourne";

            return Winner;
        }
    }

    /// <summary>
    ///     List of all players participating in the match.
    /// </summary>
    public List<ReplayPlayerMetadata> Players { get; set; } = [];

    /// <summary>
    ///     Legion team players (Team 1, slots 0..4).
    /// </summary>
    public List<ReplayPlayerMetadata> LegionPlayers => Players.FindAll(p => p.Team == 1);

    /// <summary>
    ///     Hellbourne team players (Team 2, slots 5..9).
    /// </summary>
    public List<ReplayPlayerMetadata> HellbournePlayers => Players.FindAll(p => p.Team == 2);

    /// <summary>
    ///     Finds a player by player name (case-insensitive exact match).
    /// </summary>
    public ReplayPlayerMetadata? FindPlayerByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        for (int i = 0; i < Players.Count; i++)
            if (string.Equals(Players[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return Players[i];

        return null;
    }

    /// <summary>
    ///     Finds a player by hero name (case-insensitive match, supporting "Hero_Name" or "Name").
    /// </summary>
    public ReplayPlayerMetadata? FindPlayerByHero(string heroQuery)
    {
        if (string.IsNullOrWhiteSpace(heroQuery)) return null;

        string cleanQuery = heroQuery.StartsWith("Hero_", StringComparison.OrdinalIgnoreCase)
            ? heroQuery.Substring(5)
            : heroQuery;

        for (int i = 0; i < Players.Count; i++)
            if (string.Equals(Players[i].CleanHeroName, cleanQuery, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Players[i].HeroName, heroQuery, StringComparison.OrdinalIgnoreCase))
                return Players[i];

        return null;
    }

    /// <summary>
    ///     Finds a player by their slot/client index (0..9).
    /// </summary>
    public ReplayPlayerMetadata? FindPlayerBySlot(int clientNum)
    {
        for (int i = 0; i < Players.Count; i++)
            if (Players[i].ClientNum == clientNum)
                return Players[i];

        return null;
    }

    /// <summary>
    ///     Finds a player by team and team index (Team 1/2, TeamIndex 0..4).
    /// </summary>
    public ReplayPlayerMetadata? FindPlayerByTeamSlot(int team, int teamIndex)
    {
        for (int i = 0; i < Players.Count; i++)
            if (Players[i].Team == team && Players[i].TeamIndex == teamIndex)
                return Players[i];

        return null;
    }

    /// <summary>
    ///     Resolves a user-supplied player query string into a structured PlayerResolutionResult.
    ///     Supports:
    ///     1. Direct slot index integer (0..9)
    ///     2. Team slot aliases: "L1".."L5", "H1".."H5", "legion:0".."legion:4", "hellbourne:0".."hellbourne:4"
    ///     3. Exact player name (case-insensitive)
    ///     4. Exact hero name (e.g. "Devourer", "Hero_Devourer")
    ///     5. Partial/prefix player name (e.g. "click" -> "ClickBotAlpha")
    ///     6. Partial/prefix hero name (e.g. "baba" -> "Hero_BabaYaga")
    ///     7. Graceful fallback to slot 0 if not found.
    /// </summary>
    public PlayerResolutionResult ResolvePlayer(string? playerQuery)
    {
        if (string.IsNullOrWhiteSpace(playerQuery))
        {
            ReplayPlayerMetadata? defaultPlayer = FindPlayerBySlot(0) ?? (Players.Count > 0 ? Players[0] : null);
            return new PlayerResolutionResult
            {
                ClientIndex = defaultPlayer?.ClientNum ?? 0,
                Player = defaultPlayer,
                Confidence = PlayerMatchConfidence.Fallback,
                MatchReason = "Empty player query; defaulted to slot 0.",
                Query = string.Empty
            };
        }

        string trimmed = playerQuery.Trim();

        // 1. Direct integer slot or client number (0..15)
        if (int.TryParse(trimmed, out int slot))
        {
            ReplayPlayerMetadata? slotPlayer = FindPlayerBySlot(slot);
            if (slotPlayer != null)
                return new PlayerResolutionResult
                {
                    ClientIndex = slotPlayer.ClientNum,
                    Player = slotPlayer,
                    Confidence = PlayerMatchConfidence.Exact,
                    MatchReason = $"Direct slot index {slot} specified.",
                    Query = trimmed
                };
        }

        // 2. Team slot aliases (e.g. "L1".."L5", "H1".."H5", "Legion 1", "Hellbourne 3", "legion:0", "hellbourne:4")
        Match teamMatch = Regex.Match(trimmed,
            @"^(?:(?<team>[lhLH]|legion|hellbourne|team1|team2)[\s:_]?(?<index>\d+))$", RegexOptions.IgnoreCase);
        if (teamMatch.Success)
        {
            string teamStr = teamMatch.Groups["team"].Value.ToLowerInvariant();
            int index = int.Parse(teamMatch.Groups["index"].Value);
            int targetTeam = teamStr.StartsWith('l') || teamStr.Equals("team1", StringComparison.Ordinal) ? 1 : 2;

            // Normalize 1-based (L1..L5, H1..H5) to 0-based team index, preserving 0-based aliases (legion:0, hellbourne:4)
            int teamIndex;
            if (teamStr.Length == 1)
                teamIndex = index >= 1 ? index - 1 : index;
            else if (trimmed.Contains(':') || trimmed.Contains('_'))
                teamIndex = index;
            else
                teamIndex = index is >= 1 and <= 5 ? index - 1 : index;

            ReplayPlayerMetadata? teamPlayer = FindPlayerByTeamSlot(targetTeam, teamIndex);
            if (teamPlayer != null)
                return new PlayerResolutionResult
                {
                    ClientIndex = teamPlayer.ClientNum,
                    Player = teamPlayer,
                    Confidence = PlayerMatchConfidence.Exact,
                    MatchReason = $"Team slot alias matched {teamPlayer.TeamName} slot {teamIndex + 1}.",
                    Query = trimmed
                };
        }

        // 3. Exact player name match (case-insensitive)
        ReplayPlayerMetadata? exactPlayer = FindPlayerByName(trimmed);
        if (exactPlayer != null)
            return new PlayerResolutionResult
            {
                ClientIndex = exactPlayer.ClientNum,
                Player = exactPlayer,
                Confidence = PlayerMatchConfidence.Exact,
                MatchReason = $"Exact player name match for '{exactPlayer.Name}'.",
                Query = trimmed
            };

        // 4. Exact hero name match (case-insensitive)
        ReplayPlayerMetadata? exactHero = FindPlayerByHero(trimmed);
        if (exactHero != null)
            return new PlayerResolutionResult
            {
                ClientIndex = exactHero.ClientNum,
                Player = exactHero,
                Confidence = PlayerMatchConfidence.Exact,
                MatchReason = $"Exact hero name match for '{exactHero.CleanHeroName}'.",
                Query = trimmed
            };

        // 5. Prefix or substring player name match
        for (int i = 0; i < Players.Count; i++)
            if (Players[i].Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
                return new PlayerResolutionResult
                {
                    ClientIndex = Players[i].ClientNum,
                    Player = Players[i],
                    Confidence = PlayerMatchConfidence.Fuzzy,
                    MatchReason = $"Fuzzy player name match ('{trimmed}' -> '{Players[i].Name}').",
                    Query = trimmed
                };

        // 6. Substring hero name match
        for (int i = 0; i < Players.Count; i++)
            if (Players[i].CleanHeroName.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
                Players[i].HeroName.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
                return new PlayerResolutionResult
                {
                    ClientIndex = Players[i].ClientNum,
                    Player = Players[i],
                    Confidence = PlayerMatchConfidence.Fuzzy,
                    MatchReason = $"Fuzzy hero name match ('{trimmed}' -> '{Players[i].CleanHeroName}').",
                    Query = trimmed
                };

        // 7. Fallback: default to slot 0 or first player
        ReplayPlayerMetadata? fallbackPlayer = FindPlayerBySlot(0) ?? (Players.Count > 0 ? Players[0] : null);
        return new PlayerResolutionResult
        {
            ClientIndex = fallbackPlayer?.ClientNum ?? 0,
            Player = fallbackPlayer,
            Confidence = PlayerMatchConfidence.Fallback,
            MatchReason =
                $"No matching player found for '{trimmed}'; defaulted to Player [1] (Slot {fallbackPlayer?.ClientNum ?? 0}).",
            Query = trimmed
        };
    }

    /// <summary>
    ///     Backward-compatible resolution method returning integer client index.
    /// </summary>
    public int? ResolveClientNum(string playerOrSlot)
    {
        if (string.IsNullOrWhiteSpace(playerOrSlot)) return null;

        PlayerResolutionResult result = ResolvePlayer(playerOrSlot);
        if (result.Confidence != PlayerMatchConfidence.Fallback) return result.ClientIndex;

        if (int.TryParse(playerOrSlot.Trim(), out int slot) && slot is >= 0 and <= 9) return slot;

        return null;
    }

    /// <summary>
    ///     Generates a formatted ASCII roster summary showing Legion (1-5) and Hellbourne (6-10) players.
    /// </summary>
    public string GetRosterSummary()
    {
        StringBuilder sb = new();
        sb.AppendLine("================================================================================");
        sb.AppendLine($" MATCH ROSTER: {GameName} (Match ID: {MatchId}) | Map: {MapFancyName} ({GameMode})");
        sb.AppendLine(
            $" Winner: {WinnerTeamName} | Duration: {MatchLength.Minutes}:{MatchLength.Seconds:D2} | Date: {Date} {Time}");
        sb.AppendLine("================================================================================");
        sb.AppendLine(" LEGION (Team 1):");
        List<ReplayPlayerMetadata> legion = LegionPlayers;
        if (legion.Count == 0)
            sb.AppendLine("   (No players recorded)");
        else
            for (int i = 0; i < legion.Count; i++)
            {
                ReplayPlayerMetadata p = legion[i];
                int playerNum = p.ClientNum + 1;
                sb.AppendLine(
                    $"   [{playerNum,2}] (L{p.TeamIndex + 1}): {p.Name,-16} | Hero: {p.CleanHeroName,-18} | KDA: {p.FormattedKda,-8} | GPM: {p.GoldPerMinute}");
            }

        sb.AppendLine("--------------------------------------------------------------------------------");
        sb.AppendLine(" HELLBOURNE (Team 2):");
        List<ReplayPlayerMetadata> hellbourne = HellbournePlayers;
        if (hellbourne.Count == 0)
            sb.AppendLine("   (No players recorded)");
        else
            for (int i = 0; i < hellbourne.Count; i++)
            {
                ReplayPlayerMetadata p = hellbourne[i];
                int playerNum = p.ClientNum + 1;
                sb.AppendLine(
                    $"   [{playerNum,2}] (H{p.TeamIndex + 1}): {p.Name,-16} | Hero: {p.CleanHeroName,-18} | KDA: {p.FormattedKda,-8} | GPM: {p.GoldPerMinute}");
            }

        sb.AppendLine("================================================================================");

        return sb.ToString();
    }
}
