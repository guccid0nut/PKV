// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;

namespace NEXUS.ControlPanel.GUI.Services;

public sealed class ParsedReplayPlayer
{
    public int AccountId { get; set; }
    public int PlayerIndex { get; set; }
    public string Name { get; set; } = string.Empty;
    public string HeroName { get; set; } = string.Empty;
    public string HeroDisplayName { get; set; } = string.Empty;
    public string HeroIconUrl { get; set; } = string.Empty;
    public int Team { get; set; } = 1; // 1 = Legion, 2 = Hellbourne
    public int Level { get; set; } = 1;
    public int Kills { get; set; }
    public int Deaths { get; set; }
    public int Assists { get; set; }
    public int Gold { get; set; }
    public int HeroDamage { get; set; }
    public int CreepKills { get; set; }
    public int NeutralKills { get; set; }
    public int Denies { get; set; }
    public int Mmr { get; set; } = 1500;
}

public sealed class ParsedReplayMetadata
{
    public int MatchId { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public string MapName { get; set; } = "Forests of Caldavar";
    public string RawMap { get; set; } = "caldavar";
    public int DurationSeconds { get; set; }
    public string DurationDisplay { get; set; } = "00:00";
    public string GameDate { get; set; } = string.Empty;
    public string GameMode { get; set; } = "All Pick";
    public string Version { get; set; } = "4.10.1";
    public string ServerName { get; set; } = "Project KONGOR Server";
    public int WinnerTeam { get; set; } = 1; // 1 = Legion, 2 = Hellbourne
    public string WinnerDisplay => WinnerTeam == 1 ? "Legion Victory" : "Hellbourne Victory";
    public List<ParsedReplayPlayer> LegionPlayers { get; set; } = [];
    public List<ParsedReplayPlayer> HellbournePlayers { get; set; } = [];
    public List<ParsedReplayPlayer> AllPlayers { get; set; } = [];
}

public interface ILocalReplayParserService
{
    Task<ParsedReplayMetadata?> ParseReplayFileAsync(string filePath, CancellationToken cancellationToken = default);
}

public sealed class LocalReplayParserService : ILocalReplayParserService
{
    public async Task<ParsedReplayMetadata?> ParseReplayFileAsync(string filePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return null;

        try
        {
            FileInfo fileInfo = new(filePath);
            await using FileStream stream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using MemoryStream memoryStream = new();
            await stream.CopyToAsync(memoryStream, cancellationToken);
            memoryStream.Position = 0;

            using ZipArchive archive = new(memoryStream, ZipArchiveMode.Read);
            ZipArchiveEntry? infoEntry = archive.GetEntry("replayinfo") ??
                                         archive.Entries.FirstOrDefault(e =>
                                             e.Name.Equals("replayinfo", StringComparison.OrdinalIgnoreCase));

            if (infoEntry == null) return null;

            await using Stream infoStream = infoEntry.Open();
            using StreamReader reader = new(infoStream);
            string xmlContent = await reader.ReadToEndAsync(cancellationToken);

            XDocument doc = XDocument.Parse(xmlContent);
            XElement? root = doc.Root;
            if (root == null) return null;

            ParsedReplayMetadata metadata = new()
            {
                FilePath = filePath,
                FileName = fileInfo.Name,
                FileSizeBytes = fileInfo.Length
            };

            // Parse match level attributes (supports real K2 ReplayInfo attributes and snake_case fallbacks)
            string? matchIdRaw = GetAttr(root, "matchid", "match_id", "id");
            if (int.TryParse(matchIdRaw, out int matchId)) metadata.MatchId = matchId;

            string rawMap = GetAttr(root, "mapname", "map") ?? "caldavar";
            metadata.RawMap = rawMap;
            string? fancyMap = GetAttr(root, "mapfancyname", "fancyname");
            metadata.MapName = !string.IsNullOrWhiteSpace(fancyMap) ? fancyMap : NormalizeMapName(rawMap);

            string? matchLengthMsRaw = GetAttr(root, "matchlength", "length", "duration");
            if (long.TryParse(matchLengthMsRaw, out long matchLengthMs) && matchLengthMs > 0)
            {
                int durationSec = (int)(matchLengthMs / 1000L);
                metadata.DurationSeconds = durationSec;
                TimeSpan duration = TimeSpan.FromSeconds(durationSec);
                metadata.DurationDisplay =
                    duration.Hours > 0 ? duration.ToString(@"h\:mm\:ss") : duration.ToString(@"mm\:ss");
            }
            else if (int.TryParse(GetAttr(root, "time"), out int timeSec) && timeSec > 0)
            {
                metadata.DurationSeconds = timeSec;
                TimeSpan duration = TimeSpan.FromSeconds(timeSec);
                metadata.DurationDisplay =
                    duration.Hours > 0 ? duration.ToString(@"h\:mm\:ss") : duration.ToString(@"mm\:ss");
            }

            string? dateRaw = GetAttr(root, "date");
            string? timeRaw = GetAttr(root, "time");
            metadata.GameDate = !string.IsNullOrWhiteSpace(dateRaw)
                ? (!string.IsNullOrWhiteSpace(timeRaw) && !int.TryParse(timeRaw, out _) ? $"{dateRaw} {timeRaw}" : dateRaw)
                : TimeProvider.System.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd HH:mm");
            metadata.Version = GetAttr(root, "version") ?? "4.10.1";
            metadata.ServerName = GetAttr(root, "gamename", "servername", "name", "server") ?? "Project KONGOR Match Server";
            metadata.GameMode =
                NormalizeGameMode(GetAttr(root, "gamemode", "match_type", "mode") ?? "All Pick");

            string? winnerRaw = GetAttr(root, "winner", "winning_team");
            if (!string.IsNullOrWhiteSpace(winnerRaw))
            {
                if (int.TryParse(winnerRaw, out int winner))
                    metadata.WinnerTeam = winner;
                else if (winnerRaw.Contains("hellbourne", StringComparison.OrdinalIgnoreCase))
                    metadata.WinnerTeam = 2;
                else if (winnerRaw.Contains("legion", StringComparison.OrdinalIgnoreCase))
                    metadata.WinnerTeam = 1;
            }

            // Parse players
            List<ParsedReplayPlayer> players = [];
            int index = 0;

            foreach (XElement playerElem in root.Elements())
            {
                if (!string.Equals(playerElem.Name.LocalName, "player", StringComparison.OrdinalIgnoreCase)) continue;

                int fallbackIdx = index++;
                ParsedReplayPlayer player = new()
                {
                    PlayerIndex = fallbackIdx,
                    Name = GetAttr(playerElem, "name", "playername") ?? $"Player {fallbackIdx + 1}",
                    HeroName = GetAttr(playerElem, "heroname", "hero") ?? "Hero_Empath"
                };

                if (int.TryParse(GetAttr(playerElem, "accountid", "account_id"), out int accId)) player.AccountId = accId;

                if (int.TryParse(GetAttr(playerElem, "team"), out int team)) player.Team = team;

                if (int.TryParse(GetAttr(playerElem, "clientnum", "client_num", "slot"), out int clientNum))
                    player.PlayerIndex = clientNum;
                else if (int.TryParse(GetAttr(playerElem, "teamindex", "team_index"), out int teamIndex))
                    player.PlayerIndex = player.Team == 2 ? 5 + teamIndex : teamIndex;

                if (int.TryParse(GetAttr(playerElem, "level", "hero_level"), out int lvl)) player.Level = lvl;

                if (int.TryParse(GetAttr(playerElem, "kills", "herokills", "hero_kills"), out int k)) player.Kills = k;

                if (int.TryParse(GetAttr(playerElem, "deaths"), out int d)) player.Deaths = d;

                if (int.TryParse(GetAttr(playerElem, "assists", "hero_assists"), out int a)) player.Assists = a;

                if (int.TryParse(GetAttr(playerElem, "gold"), out int g)) player.Gold = g;

                if (int.TryParse(GetAttr(playerElem, "herodamage", "hero_damage", "herodmg"), out int hd)) player.HeroDamage = hd;

                if (int.TryParse(GetAttr(playerElem, "creeps", "creepkills", "creep_kills", "team_creep_kills"), out int ck))
                    player.CreepKills = ck;

                if (int.TryParse(GetAttr(playerElem, "neutral_kills", "neutrals"), out int nk)) player.NeutralKills = nk;

                if (int.TryParse(GetAttr(playerElem, "denies"), out int den)) player.Denies = den;

                if (int.TryParse(GetAttr(playerElem, "mmr", "psr"), out int mmr))
                    player.Mmr = mmr;

                player.HeroDisplayName = CleanHeroDisplayName(player.HeroName);
                player.HeroIconUrl = GetHeroIconPath(player.HeroName);

                players.Add(player);
            }

            metadata.AllPlayers = players;
            metadata.LegionPlayers = [.. players.Where(p => p.Team == 1)];
            metadata.HellbournePlayers = [.. players.Where(p => p.Team == 2)];

            return metadata;
        }
        catch
        {
            return null;
        }
    }

    private static string? GetAttr(XElement element, params string[] candidateNames)
    {
        for (int i = 0; i < candidateNames.Length; i++)
        {
            string name = candidateNames[i];
            foreach (XAttribute attr in element.Attributes())
                if (string.Equals(attr.Name.LocalName, name, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(attr.Value))
                    return attr.Value;
        }

        return null;
    }

    private static string NormalizeMapName(string raw)
    {
        string clean = raw.ToLowerInvariant().Replace("maps/", "").Trim();
        return clean switch
        {
            "caldavar" or "forests_of_caldavar" or "caldavar_1v1" => "Forests of Caldavar",
            "caldavar_reborn" => "Forests of Caldavar Reborn",
            "midwars_reborn" or "midwarsreborn" => "Mid Wars Reborn",
            "midwars" => "Mid Wars",
            "grimmscross" or "grimmscrossing" => "Grimms Crossing",
            "watchtower" => "Watchtower",
            "riftwars" => "Rift Wars",
            "devowars" => "Devo Wars",
            _ => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(clean.Replace("_", " "))
        };
    }

    private static string NormalizeGameMode(string raw)
    {
        string clean = raw.ToLowerInvariant().Trim();
        return clean switch
        {
            "ap" or "allpick" or "all pick" => "All Pick",
            "sd" or "singledraft" or "single draft" => "Single Draft",
            "ar" or "allrandom" or "all random" => "All Random",
            "bd" or "banningdraft" or "banning draft" => "Banning Draft",
            "bp" or "banningpick" or "banning pick" => "Banning Pick",
            "midwars" or "mw" => "Mid Wars",
            _ => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(clean.Replace("_", " "))
        };
    }

    private static string CleanHeroDisplayName(string heroIdentifier)
    {
        if (string.IsNullOrWhiteSpace(heroIdentifier)) return "Unknown Hero";

        string clean = heroIdentifier.StartsWith("Hero_", StringComparison.OrdinalIgnoreCase)
            ? heroIdentifier["Hero_".Length..]
            : heroIdentifier;

        // Insert spaces before capital letters (e.g. WitchSlayer -> Witch Slayer)
        string result = string.Concat(clean.Select((c, i) => i > 0 && char.IsUpper(c) ? " " + c : c.ToString()));
        return result;
    }

    private static string GetHeroIconPath(string heroIdentifier)
    {
        string clean = heroIdentifier.StartsWith("Hero_", StringComparison.OrdinalIgnoreCase)
            ? heroIdentifier["Hero_".Length..].ToLowerInvariant()
            : heroIdentifier.ToLowerInvariant();

        return $"/Assets/heroes/{clean}/icon.png";
    }
}
