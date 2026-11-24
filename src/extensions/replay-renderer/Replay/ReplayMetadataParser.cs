// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ============================================================================
// File: ReplayMetadataParser.cs
// System: NEXUS Replay Video Renderer Service
// Policy: Strict Explicit Typing (NO 'var')
// ============================================================================

using System.IO.Compression;
using System.Xml.Linq;

namespace ReplayRenderer.Service.Replay;

/// <summary>
///     Parser for extracting match and player metadata and match statistics from .honreplay archives and replayinfo XML.
/// </summary>
public static class ReplayMetadataParser
{
    /// <summary>
    ///     Parses replay metadata directly from a .honreplay file path on disk.
    /// </summary>
    public static ReplayMetadata ParseFromFile(string replayFilePath)
    {
        if (string.IsNullOrWhiteSpace(replayFilePath))
            throw new ArgumentException("Replay file path must not be null or empty.", nameof(replayFilePath));

        if (!File.Exists(replayFilePath))
            throw new FileNotFoundException($"Replay file not found: {replayFilePath}", replayFilePath);

        using FileStream fs = new(replayFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ParseFromZipStream(fs);
    }

    /// <summary>
    ///     Parses replay metadata from an open zip archive stream containing a 'replayinfo' entry.
    /// </summary>
    public static ReplayMetadata ParseFromZipStream(Stream zipStream)
    {
        ArgumentNullException.ThrowIfNull(zipStream);

        using ZipArchive archive = new(zipStream, ZipArchiveMode.Read, true);
        ZipArchiveEntry? replayInfoEntry = null;

        foreach (ZipArchiveEntry entry in archive.Entries)
            if (string.Equals(entry.Name, "replayinfo", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(entry.FullName, "replayinfo", StringComparison.OrdinalIgnoreCase))
            {
                replayInfoEntry = entry;
                break;
            }

        if (replayInfoEntry == null)
            throw new InvalidDataException("The .honreplay archive does not contain a 'replayinfo' entry.");

        using Stream entryStream = replayInfoEntry.Open();
        using StreamReader reader = new(entryStream, System.Text.Encoding.UTF8);
        string xmlContent = reader.ReadToEnd();

        return ParseFromXml(xmlContent);
    }

    /// <summary>
    ///     Parses replay metadata from raw XML string content.
    /// </summary>
    public static ReplayMetadata ParseFromXml(string xmlContent)
    {
        if (string.IsNullOrWhiteSpace(xmlContent))
            throw new ArgumentException("XML content must not be null or empty.", nameof(xmlContent));

        XDocument doc = XDocument.Parse(xmlContent);
        XElement? root = doc.Root;
        if (root == null || !string.Equals(root.Name.LocalName, "replayinfo", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("XML root element is not <replayinfo>.");

        ReplayMetadata metadata = new()
        {
            Version = GetAttr(root, "version"),
            GameMode = GetAttr(root, "gamemode", "mode"),
            GameOptions = GetAttr(root, "gameoptions", "options"),
            ServerName = GetAttr(root, "servername", "server"),
            MapName = GetAttr(root, "mapname", "map"),
            MapFancyName = GetAttr(root, "mapfancyname", "fancyname"),
            GameName = GetAttr(root, "gamename", "name"),
            MatchId = GetAttr(root, "matchid", "id"),
            Host = GetAttr(root, "host"),
            Winner = GetAttr(root, "winner"),
            Date = GetAttr(root, "date"),
            Time = GetAttr(root, "time")
        };

        if (int.TryParse(GetAttr(root, "teamsize", "team_size"), out int teamSize)) metadata.TeamSize = teamSize;

        if (long.TryParse(GetAttr(root, "matchlength", "length", "duration"), out long matchLengthMs))
            metadata.MatchLengthMs = matchLengthMs;

        if (long.TryParse(GetAttr(root, "concedetime", "concede_time"), out long concedeMs))
            metadata.ConcedeTimeMs = concedeMs;

        if (long.TryParse(GetAttr(root, "firstbloodtime", "first_blood_time"), out long fbMs))
            metadata.FirstBloodTimeMs = fbMs;

        foreach (XElement playerElem in root.Elements())
        {
            if (!string.Equals(playerElem.Name.LocalName, "player", StringComparison.OrdinalIgnoreCase)) continue;

            ReplayPlayerMetadata player = new()
            {
                Name = GetAttr(playerElem, "name", "playername"),
                HeroName = GetAttr(playerElem, "heroname", "hero"),
                HeroIcon = GetAttr(playerElem, "heroicon", "icon"),
                Avatar = GetAttr(playerElem, "avatar")
            };

            if (long.TryParse(GetAttr(playerElem, "accountid", "account_id"), out long accountId))
                player.AccountId = accountId;

            if (int.TryParse(GetAttr(playerElem, "team"), out int team)) player.Team = team;

            if (int.TryParse(GetAttr(playerElem, "teamindex", "team_index"), out int teamIndex))
                player.TeamIndex = teamIndex;

            if (int.TryParse(GetAttr(playerElem, "clientnum", "client_num", "slot"), out int clientNum))
                player.ClientNum = clientNum;
            else
                // Infer slot from team and teamindex if clientnum is absent
                player.ClientNum = player.Team == 2 ? 5 + player.TeamIndex : player.TeamIndex;

            // Statistics extraction
            if (int.TryParse(GetAttr(playerElem, "kills", "herokills"), out int kills)) player.Kills = kills;
            if (int.TryParse(GetAttr(playerElem, "deaths"), out int deaths)) player.Deaths = deaths;
            if (int.TryParse(GetAttr(playerElem, "assists"), out int assists)) player.Assists = assists;
            if (long.TryParse(GetAttr(playerElem, "herodamage", "hero_damage"), out long heroDmg))
                player.HeroDamage = heroDmg;
            if (long.TryParse(GetAttr(playerElem, "towerdamage", "tower_damage", "bdamage"), out long towerDmg))
                player.TowerDamage = towerDmg;
            if (long.TryParse(GetAttr(playerElem, "creepdamage", "creep_damage"), out long creepDmg))
                player.CreepDamage = creepDmg;
            if (int.TryParse(GetAttr(playerElem, "gold"), out int gold)) player.Gold = gold;
            if (int.TryParse(GetAttr(playerElem, "gpm", "gold_per_minute"), out int gpm)) player.GoldPerMinute = gpm;
            if (int.TryParse(GetAttr(playerElem, "xpm", "exp_per_minute"), out int xpm)) player.ExpPerMinute = xpm;
            if (int.TryParse(GetAttr(playerElem, "level"), out int level)) player.Level = level;
            if (int.TryParse(GetAttr(playerElem, "denies"), out int denies)) player.Denies = denies;
            if (int.TryParse(GetAttr(playerElem, "creeps", "creepkills", "neutral_kills", "neutrals"), out int creeps))
                player.Creeps = creeps;
            if (int.TryParse(GetAttr(playerElem, "actions"), out int actions)) player.Actions = actions;
            if (double.TryParse(GetAttr(playerElem, "apm"), out double apm)) player.APM = apm;
            if (int.TryParse(GetAttr(playerElem, "wards"), out int wards)) player.Wards = wards;
            if (int.TryParse(GetAttr(playerElem, "consumables"), out int consumables)) player.Consumables = consumables;
            if (int.TryParse(GetAttr(playerElem, "smackdowns"), out int smackdowns)) player.Smackdowns = smackdowns;
            if (int.TryParse(GetAttr(playerElem, "humiliations"), out int humiliations))
                player.Humiliations = humiliations;
            if (int.TryParse(GetAttr(playerElem, "disconnects"), out int disconnects)) player.Disconnects = disconnects;

            metadata.Players.Add(player);
        }

        return metadata;
    }

    private static string GetAttr(XElement element, params string[] candidateNames)
    {
        for (int i = 0; i < candidateNames.Length; i++)
        {
            string name = candidateNames[i];
            foreach (XAttribute attr in element.Attributes())
                if (string.Equals(attr.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))
                    return attr.Value;
        }

        return string.Empty;
    }
}
