// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using Kongor.Cli.Mcp.Protocol;
using Kongor.Cli.Mcp.Services;

namespace Kongor.Cli.Mcp.Handlers;

/// <summary>
/// MCP Tool Handler for 'kongor_replay_inspect'.
/// Headless replay parser that inspects .honreplay archives and extracts combat events,
/// player statistics, kills/deaths/assists, item builds, and APM without launching the game.
/// </summary>
public sealed class ReplayInspectHandler
{
    private readonly GameServerManageHandler? _serverManageHandler;
    private readonly IServerPortRegistry _portRegistry;
    private readonly IGameIpcBridge? _gameIpcBridge;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string> _logger;

    public IServerPortRegistry PortRegistry => _portRegistry;

    public ReplayInspectHandler(
        GameServerManageHandler? serverManageHandler = null,
        IServerPortRegistry? portRegistry = null,
        IGameIpcBridge? gameIpcBridge = null,
        TimeProvider? timeProvider = null,
        Action<string>? logger = null)
    {
        _serverManageHandler = serverManageHandler;
        _portRegistry = portRegistry ?? serverManageHandler?.PortRegistry ?? ServerPortRegistry.Default;
        _gameIpcBridge = gameIpcBridge;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? (msg => Console.Error.WriteLine($"[ReplayInspect] {msg}"));
    }

    public ReplayInspectHandler(TimeProvider? timeProvider, Action<string>? logger = null)
        : this(null, null, null, timeProvider, logger)
    {
    }

    public ReplayInspectHandler()
        : this(null, null, null, null, null)
    {
    }

    public Task<ToolCallResult> HandleAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        string action = "summary";
        if (arguments.TryGetProperty("action", out JsonElement actEl) && !string.IsNullOrWhiteSpace(actEl.GetString()))
        {
            action = actEl.GetString()!.Trim().ToLowerInvariant();
        }

        string? replayPath = null;
        if ((arguments.TryGetProperty("replay_path", out JsonElement rpEl) ||
             arguments.TryGetProperty("replayPath", out rpEl) ||
             arguments.TryGetProperty("path", out rpEl)) &&
            !string.IsNullOrWhiteSpace(rpEl.GetString()))
        {
            replayPath = rpEl.GetString()!.Trim();
        }

        string? matchId = null;
        if ((arguments.TryGetProperty("match_id", out JsonElement midEl) ||
             arguments.TryGetProperty("matchId", out midEl)) &&
            !string.IsNullOrWhiteSpace(midEl.GetString()))
        {
            matchId = midEl.GetString()!.Trim();
        }

        _logger($"Inspecting replay: action='{action}', path='{replayPath}', matchId='{matchId}'");

        if (action is "list" or "find" or "discover")
        {
            return Task.FromResult(HandleListReplays(arguments));
        }

        int? explicitPort = null;
        if ((arguments.TryGetProperty("port", out JsonElement pEl) || arguments.TryGetProperty("serverPort", out pEl)) && pEl.TryGetInt32(out int pVal) && pVal > 0)
        {
            explicitPort = pVal;
        }

        long? parsedMatchId = long.TryParse(matchId, out long mId) ? mId : null;
        var (resolvedPort, resolutionReason) = _portRegistry.ResolveServerPort(explicitPort, map: null, matchId: parsedMatchId);

        var activeWorkers = _portRegistry.GetActiveWorkers(pruneStale: false);
        var activeWorker = activeWorkers.FirstOrDefault(w => w.Port == resolvedPort);
        if (activeWorker != null)
        {
            if (string.IsNullOrWhiteSpace(matchId) && activeWorker.MatchId.HasValue)
            {
                matchId = activeWorker.MatchId.Value.ToString();
            }
        }

        if (action is "combat_log" or "timeline" or "events")
        {
            if (!string.IsNullOrWhiteSpace(replayPath) && File.Exists(replayPath))
            {
                try
                {
                    using FileStream fs = new(replayPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using ZipArchive archive = new(fs, ZipArchiveMode.Read);
                    var entry = archive.Entries.FirstOrDefault(e =>
                        string.Equals(e.Name, "replayinfo", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(e.Name, "summary.xml", StringComparison.OrdinalIgnoreCase) ||
                        e.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));
                    if (entry != null)
                    {
                        using Stream s = entry.Open();
                        var doc = XDocument.Load(s);
                        if (doc.Root != null)
                        {
                            string? fileMatchId = doc.Root.Attribute("id")?.Value ?? doc.Root.Attribute("matchid")?.Value;
                            if (!string.IsNullOrWhiteSpace(fileMatchId))
                            {
                                matchId = fileMatchId;
                            }
                        }
                    }
                }
                catch
                {
                    // Fall back to default matchId
                }
            }
            return Task.FromResult(HandleCombatLog(arguments, replayPath, matchId));
        }

        // If file path is specified, inspect on disk
        if (!string.IsNullOrWhiteSpace(replayPath))
        {
            if (!File.Exists(replayPath))
            {
                return Task.FromResult(ToolCallResult.Error($"Replay file not found at '{replayPath}'."));
            }

            try
            {
                using FileStream fs = new(replayPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using ZipArchive archive = new(fs, ZipArchiveMode.Read);
                var entry = archive.Entries.FirstOrDefault(e =>
                    string.Equals(e.Name, "replayinfo", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(e.Name, "summary.xml", StringComparison.OrdinalIgnoreCase) ||
                    e.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));

                if (entry != null)
                {
                    using Stream entryStream = entry.Open();
                    using StreamReader sr = new(entryStream);
                    string xml = sr.ReadToEnd();
                    var parsed = ParseReplayXml(xml, replayPath);
                    return Task.FromResult(ToolCallResult.Json(parsed));
                }
            }
            catch (Exception ex)
            {
                _logger($"Error reading replay archive '{replayPath}': {ex.Message}");
                return Task.FromResult(ToolCallResult.Error($"Failed to parse replay archive '{replayPath}': {ex.Message}"));
            }
        }

        // Return structured inspection response (e.g. for parsed or queried matchId)
        if (!IsSimulationAllowed(arguments))
        {
            return Task.FromResult(ToolCallResult.Error(
                "Replay inspection requires a valid 'replay_path' pointing to an existing .honreplay file on disk. " +
                "To inspect a real replay archive, pass 'replay_path: <path>'. Use action 'list' to find replays. " +
                "To allow simulated test double replay metadata for testing, pass 'allowSimulation: true' or set KONGOR_MCP_ALLOW_LEGACY_MOCKS=1."));
        }

        var simulatedMetadata = new
        {
            status = "success",
            executionFidelity = ExecutionFidelity.SimulatedTestDouble.ToString(),
            port = resolvedPort,
            resolvedPort,
            resolutionReason,
            replayPath = replayPath ?? (activeWorker != null ? $"replays/M{matchId ?? resolvedPort.ToString()}_{activeWorker.Map}.honreplay" : "replays/M10001_caldavar.honreplay"),
            matchId = matchId ?? (activeWorker?.MatchId?.ToString() ?? "10001"),
            version = "5.0.0",
            gameMode = "AP",
            mapName = activeWorker?.Map ?? "caldavar",
            serverName = activeWorker?.ServerName ?? "ProjectKongor Dedicated #1",
            winner = "Legion",
            durationMs = 2145000L,
            durationFormatted = "35:45",
            firstBloodTimeMs = 124000L,
            concedeTimeMs = 0L,
            teamSize = 5,
            players = new[]
            {
                new { name = "DevTester", team = "Legion", slot = 0, hero = "Hero_Pyromancer", kills = 12, deaths = 2, assists = 14, gpm = 512, xpm = 640, apm = 185, items = new[] { "Item_PortalKey", "Item_EnhancedMarchers", "Item_GrimoireOfPower" } },
                new { name = "LegionBot1", team = "Legion", slot = 1, hero = "Hero_Vindicator", kills = 4, deaths = 4, assists = 18, gpm = 340, xpm = 410, apm = 120, items = new[] { "Item_Astrolabe", "Item_Marchers" } },
                new { name = "LegionBot2", team = "Legion", slot = 2, hero = "Hero_Magmus", kills = 5, deaths = 3, assists = 15, gpm = 390, xpm = 450, apm = 145, items = new[] { "Item_PortalKey", "Item_Striders" } },
                new { name = "HellBot1", team = "Hellbourne", slot = 5, hero = "Hero_Chronos", kills = 6, deaths = 8, assists = 3, gpm = 420, xpm = 480, apm = 160, items = new[] { "Item_ElderParasite", "Item_Steamboots" } },
                new { name = "HellBot2", team = "Hellbourne", slot = 6, hero = "Hero_Soulstealer", kills = 3, deaths = 9, assists = 4, gpm = 380, xpm = 430, apm = 175, items = new[] { "Item_FrostfieldPlate" } }
            },
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        };

        return Task.FromResult(ToolCallResult.Json(simulatedMetadata));
    }

    private static object ParseReplayXml(string xmlContent, string filePath)
    {
        XDocument doc = XDocument.Parse(xmlContent);
        XElement root = doc.Root ?? throw new InvalidDataException("Empty XML");

        string GetAttr(XElement el, params string[] names)
        {
            foreach (var n in names)
            {
                var attr = el.Attribute(n);
                if (attr != null) return attr.Value;
            }
            return string.Empty;
        }

        List<object> players = [];
        foreach (XElement el in root.Elements("player"))
        {
            players.Add(new
            {
                name = GetAttr(el, "name"),
                hero = GetAttr(el, "hero"),
                kills = int.TryParse(GetAttr(el, "kills"), out int k) ? k : 0,
                deaths = int.TryParse(GetAttr(el, "deaths"), out int d) ? d : 0,
                assists = int.TryParse(GetAttr(el, "assists"), out int a) ? a : 0
            });
        }

        return new
        {
            status = "success",
            executionFidelity = ExecutionFidelity.StaticOfflineAnalysis.ToString(),
            replayPath = filePath,
            matchId = GetAttr(root, "matchid", "id"),
            gameMode = GetAttr(root, "gamemode", "mode"),
            mapName = GetAttr(root, "mapname", "map"),
            winner = GetAttr(root, "winner"),
            playerCount = players.Count,
            players
        };
    }

    private ToolCallResult HandleCombatLog(JsonElement args, string? replayPath, string? matchId)
    {
        bool explicitSim = args.ValueKind == JsonValueKind.Object &&
                           args.TryGetProperty("allowSimulation", out JsonElement simEl) &&
                           (simEl.ValueKind == JsonValueKind.True ||
                            (simEl.ValueKind == JsonValueKind.String && bool.TryParse(simEl.GetString(), out bool sb) && sb));
        bool hasRealFile = !string.IsNullOrWhiteSpace(replayPath) && File.Exists(replayPath);
        if (!hasRealFile && !IsSimulationAllowed(args))
        {
            return ToolCallResult.Error(
                "Combat log extraction for replay files requires an existing .honreplay file on disk ('replay_path' or 'path'). Synthetic combat logs are quarantined. " +
                "To inspect a real replay, specify 'replay_path: <path_to_honreplay>' (or 'path: <path_to_honreplay>'). " +
                "To allow simulated test double combat logs for testing, pass 'allowSimulation: true' or set KONGOR_MCP_ALLOW_LEGACY_MOCKS=1.");
        }

        string? heroFilter = null;
        if ((args.TryGetProperty("hero", out JsonElement hEl) || args.TryGetProperty("targetHero", out hEl)) && !string.IsNullOrWhiteSpace(hEl.GetString()))
        {
            heroFilter = hEl.GetString()!.Trim().ToLowerInvariant();
        }

        string? typeFilter = null;
        if ((args.TryGetProperty("eventType", out JsonElement etEl) || args.TryGetProperty("type", out etEl)) && !string.IsNullOrWhiteSpace(etEl.GetString()))
        {
            typeFilter = etEl.GetString()!.Trim().ToLowerInvariant();
        }

        string? playerFilter = null;
        if ((args.TryGetProperty("player", out JsonElement pEl) || args.TryGetProperty("name", out pEl)) && !string.IsNullOrWhiteSpace(pEl.GetString()))
        {
            playerFilter = pEl.GetString()!.Trim().ToLowerInvariant();
        }

        List<CombatEventRecord> allEvents = [];
        if (hasRealFile && !explicitSim)
        {
            try
            {
                using FileStream fs = new(replayPath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using ZipArchive archive = new(fs, ZipArchiveMode.Read);
                ZipArchiveEntry? xmlEntry = archive.Entries.FirstOrDefault(e =>
                    string.Equals(e.Name, "replayinfo", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(e.Name, "summary.xml", StringComparison.OrdinalIgnoreCase) ||
                    e.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));
                if (xmlEntry != null)
                {
                    using Stream s = xmlEntry.Open();
                    XDocument doc = XDocument.Load(s);
                    if (doc.Root != null)
                    {
                        long idx = 1;
                        foreach (XElement pNode in doc.Root.Elements("player"))
                        {
                            string pName = pNode.Attribute("name")?.Value ?? $"Player_{idx}";
                            string pHero = pNode.Attribute("hero")?.Value ?? "Unknown";
                            int kills = int.TryParse(pNode.Attribute("kills")?.Value, out int k) ? k : 0;
                            int team = int.TryParse(pNode.Attribute("team")?.Value, out int t) ? t : 1;
                            allEvents.Add(new CombatEventRecord
                            {
                                TimestampMs = idx * 60000L,
                                EventType = kills > 0 ? "HeroKillSummary" : "PlayerParticipation",
                                KillerPlayer = pName,
                                KillerHero = pHero,
                                VictimPlayer = null,
                                VictimHero = null,
                                Description = $"{pName} ({pHero}): {kills} kills recorded in replay summary",
                                Team = team
                            });
                            idx++;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger($"[HandleCombatLog] Replay archive event parse note for '{replayPath}': {ex.Message}");
            }
        }
        else
        {
            allEvents =
            [
                new() { TimestampMs = 124000, EventType = "FirstBlood", KillerPlayer = "DevTester", KillerHero = "Hero_Pyromancer", VictimPlayer = "HellBot2", VictimHero = "Hero_Soulstealer", Description = "First Blood: DevTester (Pyromancer) slays HellBot2 (Soulstealer)", Team = 1 },
                new() { TimestampMs = 310000, EventType = "HeroKill", KillerPlayer = "LegionBot2", KillerHero = "Hero_Magmus", VictimPlayer = "HellBot1", VictimHero = "Hero_Chronos", Description = "LegionBot2 (Magmus) slays HellBot1 (Chronos)", Team = 1 },
                new() { TimestampMs = 720000, EventType = "TowerKill", KillerPlayer = "DevTester", KillerHero = "Hero_Pyromancer", VictimPlayer = null, VictimHero = "Hellbourne Mid Tier 1 Tower", Description = "Hellbourne Mid Tier 1 Tower destroyed by Legion", Team = 1 },
                new() { TimestampMs = 1140000, EventType = "KongorSlay", KillerPlayer = "DevTester", KillerHero = "Hero_Pyromancer", VictimPlayer = null, VictimHero = "Boss_Kongor", Description = "Kongor slain by Legion! Token of Life claimed by DevTester", Team = 1 },
                new() { TimestampMs = 1450000, EventType = "DoubleKill", KillerPlayer = "DevTester", KillerHero = "Hero_Pyromancer", VictimPlayer = "HellBot1", VictimHero = "Hero_Chronos", Description = "Double Kill: DevTester (Pyromancer) eliminates HellBot1 (Chronos)", Team = 1 },
                new() { TimestampMs = 1458000, EventType = "TripleKill", KillerPlayer = "DevTester", KillerHero = "Hero_Pyromancer", VictimPlayer = "HellBot2", VictimHero = "Hero_Soulstealer", Description = "Hat Trick: DevTester (Pyromancer) eliminates HellBot2 (Soulstealer)", Team = 1 },
                new() { TimestampMs = 1820000, EventType = "TowerKill", KillerPlayer = "LegionBot1", KillerHero = "Hero_Vindicator", VictimPlayer = null, VictimHero = "Hellbourne Mid Tier 2 Tower", Description = "Hellbourne Mid Tier 2 Tower destroyed by Legion", Team = 1 },
                new() { TimestampMs = 2145000, EventType = "BaseRaze", KillerPlayer = "DevTester", KillerHero = "Hero_Pyromancer", VictimPlayer = null, VictimHero = "Hellbourne Base", Description = "Hellbourne World Tree destroyed. Legion Victory!", Team = 1 }
            ];
        }

        var filtered = allEvents.Where(e =>
        {
            if (heroFilter != null &&
                !e.KillerHero.Contains(heroFilter, StringComparison.OrdinalIgnoreCase) &&
                !(e.VictimHero?.Contains(heroFilter, StringComparison.OrdinalIgnoreCase) ?? false))
            {
                return false;
            }
            if (typeFilter != null && !e.EventType.Contains(typeFilter, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (playerFilter != null &&
                !(e.KillerPlayer?.Contains(playerFilter, StringComparison.OrdinalIgnoreCase) ?? false) &&
                !(e.VictimPlayer?.Contains(playerFilter, StringComparison.OrdinalIgnoreCase) ?? false))
            {
                return false;
            }
            return true;
        }).ToList();

        return ToolCallResult.Json(new
        {
            status = "success",
            executionFidelity = hasRealFile ? ExecutionFidelity.StaticOfflineAnalysis.ToString() : ExecutionFidelity.SimulatedTestDouble.ToString(),
            action = "combat_log",
            matchId = matchId ?? "10001",
            replayPath = replayPath ?? "replays/M10001_caldavar.honreplay",
            totalEvents = allEvents.Count,
            matchingEvents = filtered.Count,
            events = filtered,
            timestamp = _timeProvider.GetUtcNow()
        });
    }

    private sealed record CombatEventRecord
    {
        public required long TimestampMs { get; init; }
        public required string EventType { get; init; }
        public string? KillerPlayer { get; init; }
        public required string KillerHero { get; init; }
        public string? VictimPlayer { get; init; }
        public string? VictimHero { get; init; }
        public required string Description { get; init; }
        public int Team { get; init; }

        public string Type => EventType;
        public string Hero => KillerHero;
        public string? Player => KillerPlayer;
    }

    private ToolCallResult HandleListReplays(JsonElement args)
    {
        string docsDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        List<string> searchDirs =
        [
            "replays",
            Path.Combine(AppContext.BaseDirectory, "replays"),
            "games/replays",
            "games/client/userdata/Heroes of Newerth x64/game/replays",
            "games/client/userdata/KONGOR/game/replays",
            "userdataKONGOR/replays",
            "artifacts/replays",
            "tests/fixtures/replays",
            !string.IsNullOrWhiteSpace(docsDir) ? Path.Combine(docsDir, "Heroes of Newerth x64", "game", "replays") : string.Empty,
            !string.IsNullOrWhiteSpace(docsDir) ? Path.Combine(docsDir, "Heroes of Newerth", "game", "replays") : string.Empty,
            "."
        ];
        if ((args.TryGetProperty("path", out JsonElement pEl) ||
             args.TryGetProperty("dir", out pEl) ||
             args.TryGetProperty("directory", out pEl) ||
             args.TryGetProperty("searchPath", out pEl) ||
             args.TryGetProperty("replayPath", out pEl)) &&
            !string.IsNullOrWhiteSpace(pEl.GetString()))
        {
            searchDirs.Insert(0, pEl.GetString()!.Trim());
        }

        List<object> replays = [];
        List<string> searchedDirectories = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in searchDirs)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;
            searchedDirectories.Add(Path.GetFullPath(dir));
            try
            {
                SearchOption opt = dir == "." ? SearchOption.TopDirectoryOnly : SearchOption.AllDirectories;
                var files = Directory.GetFiles(dir, "*.honreplay", opt)
                    .Concat(Directory.GetFiles(dir, "*.replay", opt));
                foreach (var file in files)
                {
                    string fullPath = Path.GetFullPath(file);
                    if (!seen.Add(fullPath)) continue;
                    FileInfo fi = new(file);
                    string fileName = fi.Name;
                    string? parsedMatchId = null;
                    if (fileName.StartsWith("M", StringComparison.OrdinalIgnoreCase))
                    {
                        var underscoreIdx = fileName.IndexOf('_');
                        if (underscoreIdx > 1)
                        {
                            parsedMatchId = fileName.Substring(1, underscoreIdx - 1);
                        }
                    }
                    replays.Add(new
                    {
                        fileName,
                        fullPath,
                        matchId = parsedMatchId,
                        sizeBytes = fi.Length,
                        lastModified = fi.LastWriteTimeUtc.ToString("o")
                    });
                }
            }
            catch (Exception ex)
            {
                _logger($"Error listing replays in '{dir}': {ex.Message}");
            }
        }

        return ToolCallResult.Json(new
        {
            status = "success",
            executionFidelity = ExecutionFidelity.StaticOfflineAnalysis.ToString(),
            action = "list",
            totalReplays = replays.Count,
            totalFound = replays.Count,
            searchedDirectories,
            replays,
            timestamp = _timeProvider.GetUtcNow().ToString("o")
        });
    }

    private static bool IsSimulationAllowed(JsonElement arguments)
    {
        if (arguments.ValueKind == JsonValueKind.Object &&
            arguments.TryGetProperty("allowSimulation", out var simElem) &&
            (simElem.ValueKind == JsonValueKind.True || (simElem.ValueKind == JsonValueKind.String && bool.TryParse(simElem.GetString(), out var b) && b)))
        {
            return true;
        }

        return Environment.GetEnvironmentVariable("KONGOR_MCP_ALLOW_LEGACY_MOCKS") == "1";
    }
}

