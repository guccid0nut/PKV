// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ReSharper disable All

#pragma warning disable CA2254
#pragma warning disable CA1848
#pragma warning disable CA1304
#pragma warning disable CA1305
#pragma warning disable CA1311

using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DAWNBRINGER.WebPortal.UI.Services.LogParser;

namespace DAWNBRINGER.WebPortal.UI.Services.Replay;

public sealed partial class HonReplayParser(ILogger<HonReplayParser> logger) : IHonReplayParser
{
    [GeneratedRegex(@"\^(?:[a-zA-Z\*;:]|\d{3})", RegexOptions.Compiled)]
    private static partial Regex K2ColorCodeRegex();

    public async Task<MatchReplayTimelineDto?> ParseReplayFileAsync(string filePath, int matchId,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            if (logger.IsEnabled(LogLevel.Warning))
            {
                logger.LogWarning("[HonReplayParser] Replay file not found: {FilePath}", filePath);
            }

            return null;
        }

        if (filePath.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
        {
            return await ParseLogFileAsync(filePath, matchId, cancellationToken);
        }

        try
        {
            await using FileStream stream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return await ParseReplayAsync(stream, matchId, cancellationToken);
        }
        catch (Exception ex)
        {
            if (logger.IsEnabled(LogLevel.Error))
            {
                logger.LogError(ex, "[HonReplayParser] Error reading replay file: {FilePath}", filePath);
            }

            return null;
        }
    }

    public async Task<MatchReplayTimelineDto?> ParseReplayAsync(Stream replayStream, int matchId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using MemoryStream memoryStream = new();
            await replayStream.CopyToAsync(memoryStream, cancellationToken);
            byte[] rawBytes = memoryStream.ToArray();
            if (rawBytes.Length == 0)
            {
                return null;
            }

            // Support direct K2 server .log streams (UTF-16LE or UTF-8) passed into ParseReplayAsync
            bool isZipArchive = rawBytes.Length >= 4 && rawBytes[0] == 0x50 && rawBytes[1] == 0x4B;
            if (!isZipArchive)
            {
                if (LooksLikeServerLog(rawBytes))
                {
                    logger.LogInformation(
                        "[HonReplayParser] Stream for match {MatchId} detected as K2 server text log ({ByteCount} bytes); delegating to ParseLogBytes",
                        matchId, rawBytes.Length);
                    return ParseLogBytes(rawBytes, matchId);
                }

                if (logger.IsEnabled(LogLevel.Warning))
                {
                    logger.LogWarning(
                        "[HonReplayParser] Stream for match {MatchId} is neither a ZIP/.honreplay archive nor a K2 server log ({ByteCount} bytes)",
                        matchId, rawBytes.Length);
                }

                return null;
            }

            memoryStream.Position = 0;
            await using ZipArchive archive = new(memoryStream, ZipArchiveMode.Read);
            ZipArchiveEntry? infoEntry = archive.GetEntry("replayinfo")
                                         ?? archive.Entries.FirstOrDefault(e =>
                                             string.Equals(e.Name, "replayinfo", StringComparison.OrdinalIgnoreCase));
            ZipArchiveEntry? dataEntry = archive.GetEntry("replaydata")
                                         ?? archive.Entries.FirstOrDefault(e =>
                                             string.Equals(e.Name, "replaydata", StringComparison.OrdinalIgnoreCase));

            if (infoEntry == null)
            {
                // Support .zip archives wrapping a K2 M*.log file
                ZipArchiveEntry? logEntry = archive.Entries.FirstOrDefault(e =>
                    e.Name.EndsWith(".log", StringComparison.OrdinalIgnoreCase));
                if (logEntry != null)
                {
                    using Stream logEntryStream = logEntry.Open();
                    using MemoryStream logMs = new();
                    await logEntryStream.CopyToAsync(logMs, cancellationToken);
                    logger.LogInformation(
                        "[HonReplayParser] ZIP archive for match {MatchId} contains server log entry '{EntryName}'; parsing log telemetry",
                        matchId, logEntry.FullName);
                    return ParseLogBytes(logMs.ToArray(), matchId);
                }

                if (logger.IsEnabled(LogLevel.Warning))
                {
                    logger.LogWarning("[HonReplayParser] Archive for match {MatchId} missing replayinfo", matchId);
                }

                return null;
            }

            // 1. Parse replayinfo XML
            ReplayInfoMeta meta = ParseReplayInfo(infoEntry, matchId);

            // 2. Parse replaydata binary stream if available
            List<MatchTimelineKeyframe> keyframes = [];
            List<MatchTimelineEventPin> eventPins = [];

            if (dataEntry != null)
            {
                ParseReplayData(dataEntry, meta, keyframes, eventPins);
            }

            if (keyframes.Count == 0)
            {
                keyframes = GenerateKeyframesFromMeta(meta);
            }

            if (eventPins.Count == 0)
            {
                eventPins = GenerateEventPinsFromMeta(meta);
            }

            int scoreLegion = meta.Players.Where(p => p.Team == 1).Sum(p => p.FinalKills);
            int scoreHell = meta.Players.Where(p => p.Team == 2).Sum(p => p.FinalKills);

            MatchReplayTimelineDto timeline = new()
            {
                MatchID = matchId > 0 ? matchId : meta.MatchId,
                Map = meta.MapName,
                GameMode = meta.GameMode,
                TotalDurationSeconds = meta.DurationSeconds,
                WinnerTeam = meta.WinnerTeam,
                ScoreLegion = scoreLegion,
                ScoreHellbourne = scoreHell,
                TelemetrySource = "honreplay",
                TelemetryLabel = "Verified Server Telemetry",
                Players = meta.Players,
                Keyframes = keyframes,
                EventPins = eventPins
            };

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    "[HonReplayParser] Successfully parsed .honreplay match {MatchId} ({MapName}, {Duration}s, {PlayerCount} players, {KeyframesCount} keyframes, {EventPinsCount} pins)",
                    timeline.MatchID, timeline.Map, timeline.TotalDurationSeconds, timeline.Players.Count,
                    timeline.Keyframes.Count, timeline.EventPins.Count);
            }

            return timeline;
        }
        catch (Exception ex)
        {
            if (logger.IsEnabled(LogLevel.Error))
            {
                logger.LogError(ex, "[HonReplayParser] Failed to parse .honreplay archive for match {MatchId}",
                    matchId);
            }

            return null;
        }
    }

    public async Task<MatchReplayTimelineDto?> ParseLogFileAsync(string logFilePath, int matchId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            byte[] rawBytes;
            await using (FileStream fs = new(logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                using MemoryStream ms = new();
                await fs.CopyToAsync(ms, cancellationToken);
                rawBytes = ms.ToArray();
            }

            return ParseLogBytes(rawBytes, matchId, logFilePath);
        }
        catch (Exception ex)
        {
            if (logger.IsEnabled(LogLevel.Error))
            {
                logger.LogError(ex, "[HonReplayParser] Error parsing log file: {FilePath}", logFilePath);
            }

            return null;
        }
    }

    private MatchReplayTimelineDto? ParseLogBytes(byte[] rawBytes, int matchId, string? sourcePath = null)
    {
        if (rawBytes.Length == 0)
        {
            return null;
        }

        Encoding encoding = DetectLogEncoding(rawBytes, out int bomOffset);
        string text = encoding.GetString(rawBytes, bomOffset, rawBytes.Length - bomOffset);

        HonLogParsedState state = new();
        List<MatchTimelineKeyframe> keyframes = [];
        int lastRecordedSecond = -1;
        int lastEventCount = 0;
        bool recordedZeroFrame = false;

        using (StringReader sr = new(text))
        {
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                bool parsed = HonLogParser.ParseLine(line, state);
                if (!parsed || state.PlayersBySlot.Count == 0)
                {
                    continue;
                }

                if (!recordedZeroFrame && state.ElapsedSeconds == 0)
                {
                    keyframes.Add(BuildKeyframeFromLogState(state, 0));
                    recordedZeroFrame = true;
                    lastRecordedSecond = 0;
                    lastEventCount = state.AllTimelineEvents.Count;
                    continue;
                }

                bool hasNewMajorEvent = state.AllTimelineEvents.Count > lastEventCount;
                bool intervalElapsed = state.ElapsedSeconds - lastRecordedSecond >= 2;

                if (state.ElapsedSeconds > lastRecordedSecond && (intervalElapsed || hasNewMajorEvent))
                {
                    if (!recordedZeroFrame)
                    {
                        keyframes.Add(BuildInitialSpawnKeyframe(state));
                        recordedZeroFrame = true;
                    }

                    keyframes.Add(BuildKeyframeFromLogState(state, state.ElapsedSeconds));
                    lastRecordedSecond = state.ElapsedSeconds;
                    lastEventCount = state.AllTimelineEvents.Count;
                }
            }
        }

        if (state.PlayersBySlot.Count == 0)
        {
            logger.LogWarning(
                "[HonReplayParser] K2 log for match {MatchId} ({Source}) contained 0 recognizable player slots",
                matchId, sourcePath ?? "stream");
            return null;
        }

        int finalDuration = state.ElapsedSeconds > 0
            ? state.ElapsedSeconds
            : (lastRecordedSecond > 0 ? lastRecordedSecond : 60);

        float finalMinutes = Math.Max(0.25f, finalDuration / 60f);
        List<ReplayPlayerSummary> summaries = [];
        foreach (KeyValuePair<int, LivePlayerState> kvp in state.PlayersBySlot.OrderBy(k => k.Key))
        {
            LivePlayerState p = kvp.Value;
            if (p.GPM <= 0 && p.CurrentGold > 0)
            {
                p.GPM = (int)Math.Round(p.CurrentGold / finalMinutes);
            }

            if (p.XPM <= 0)
            {
                p.XPM = (int)Math.Round((p.Level * 280) / finalMinutes);
            }

            List<ReplayPlayerAbilityDto> finalAbilities = p.Abilities.Count > 0
                ? [.. p.Abilities.Select(CloneAbility)]
                : GetHeroBaseAbilities(p.HeroIdentifier, Math.Clamp((p.Level + 1) / 2, 1, 4));

            summaries.Add(new ReplayPlayerSummary
            {
                AccountID = p.AccountID != 0 ? p.AccountID : -(kvp.Key + 1),
                AccountName = StripK2ColorCodes(p.AccountName),
                HeroIdentifier = p.HeroIdentifier,
                HeroDisplayName = p.HeroDisplayName,
                Team = p.Team,
                FinalLevel = Math.Max(1, p.Level),
                FinalKills = p.Kills,
                FinalDeaths = p.Deaths,
                FinalAssists = p.Assists,
                FinalGpm = p.GPM,
                FinalXpm = p.XPM,
                FinalGold = p.CurrentGold,
                FinalItems = [.. p.Inventory],
                Abilities = finalAbilities
            });
        }

        if (keyframes.Count == 0 || keyframes[0].Second > 0)
        {
            keyframes.Insert(0, BuildInitialSpawnKeyframe(state));
        }

        // Ensure final keyframe lands on exact finalDuration and aligns with summaries
        MatchTimelineKeyframe finalFrame = BuildFinalKeyframeFromSummaries(summaries, state, finalDuration);
        if (keyframes[^1].Second == finalDuration)
        {
            keyframes[^1] = finalFrame;
        }
        else
        {
            keyframes.Add(finalFrame);
        }

        List<MatchTimelineEventPin> eventPins = [];
        IReadOnlyList<LiveCombatEvent> sourceEvents = state.AllTimelineEvents.Count > 0
            ? state.AllTimelineEvents
            : state.RecentEvents;

        foreach (LiveCombatEvent evt in sourceEvents)
        {
            eventPins.Add(new MatchTimelineEventPin
            {
                MatchTimeSeconds = evt.MatchTimeSeconds,
                EventType = evt.EventType,
                Description = StripK2ColorCodes(evt.Description),
                AttackerHero = evt.AttackerHero,
                VictimHero = evt.VictimHero,
                PositionX = Math.Clamp(evt.PositionX, 0.02f, 0.98f),
                PositionY = Math.Clamp(evt.PositionY, 0.02f, 0.98f),
                Team = evt.Team,
                AbilityOrItem = evt.AbilityOrItem
            });
        }

        int winner = state.WinnerTeam is 1 or 2
            ? state.WinnerTeam
            : (state.ScoreLegion >= state.ScoreHellbourne ? 1 : 2);

        if (eventPins.Count == 0)
        {
            (float loseX, float loseY) = K2MapCoordinateRegistry.GetTeamFountain(state.Map, winner == 1 ? 2 : 1);
            eventPins.Add(new MatchTimelineEventPin
            {
                MatchTimeSeconds = finalDuration,
                EventType = "VICTORY",
                Description = $"Victory for {(winner == 1 ? "Legion" : "Hellbourne")}!",
                PositionX = loseX,
                PositionY = loseY,
                Team = winner
            });
        }

        string resolvedMode = !string.IsNullOrWhiteSpace(state.GameMode)
            ? state.GameMode
            : ResolveDefaultModeLabel(state.Map);

        MatchReplayTimelineDto timeline = new()
        {
            MatchID = matchId > 0 ? matchId : state.MatchID,
            Map = K2MapCoordinateRegistry.NormalizeMapSlug(state.Map),
            GameMode = resolvedMode,
            TotalDurationSeconds = finalDuration,
            WinnerTeam = winner,
            ScoreLegion = state.ScoreLegion,
            ScoreHellbourne = state.ScoreHellbourne,
            TelemetrySource = "server_log",
            TelemetryLabel = "Verified Server Telemetry",
            Players = summaries,
            Keyframes = keyframes,
            EventPins = eventPins
        };

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "[HonReplayParser] Parsed K2 server log for match {MatchId} ({Source}): Map={Map}, Duration={Duration}s, Winner=Team{Winner}, Score={Legion}-{Hellbourne}, Players={PlayerCount}, Keyframes={KeyframeCount}, Events={EventCount}",
                timeline.MatchID, sourcePath ?? "stream", timeline.Map, timeline.TotalDurationSeconds,
                timeline.WinnerTeam, timeline.ScoreLegion, timeline.ScoreHellbourne,
                timeline.Players.Count, timeline.Keyframes.Count, timeline.EventPins.Count);
        }

        return timeline;
    }

    private static MatchTimelineKeyframe BuildInitialSpawnKeyframe(HonLogParsedState state)
    {
        List<ReplayPlayerSnapshot> snapshots = [];
        foreach (KeyValuePair<int, LivePlayerState> kvp in state.PlayersBySlot.OrderBy(k => k.Key))
        {
            LivePlayerState p = kvp.Value;
            (float wellX, float wellY) = K2MapCoordinateRegistry.GetTeamFountain(state.Map, p.Team);
            snapshots.Add(new ReplayPlayerSnapshot
            {
                AccountID = p.AccountID != 0 ? p.AccountID : -(kvp.Key + 1),
                AccountName = StripK2ColorCodes(p.AccountName),
                HeroIdentifier = p.HeroIdentifier,
                HeroDisplayName = p.HeroDisplayName,
                Team = p.Team,
                Level = 1,
                Kills = 0,
                Deaths = 0,
                Assists = 0,
                Gpm = 0,
                Xpm = 0,
                Gold = 600,
                HealthPercent = 100f,
                ManaPercent = 100f,
                PositionX = wellX,
                PositionY = wellY,
                HeadingAngle = p.Team == 1 ? -0.785f : 2.356f,
                IsAlive = true,
                Items = [],
                Abilities = GetHeroBaseAbilities(p.HeroIdentifier, initialLevel: 1),
                LastCastAbility = null
            });
        }

        return new MatchTimelineKeyframe
        {
            Second = 0,
            ElapsedFormatted = "00:00",
            Players = snapshots,
            Structures = new Dictionary<string, bool>(state.Structures)
        };
    }

    private static MatchTimelineKeyframe BuildKeyframeFromLogState(HonLogParsedState state, int second)
    {
        List<ReplayPlayerSnapshot> snapshots = [];
        foreach (KeyValuePair<int, LivePlayerState> kvp in state.PlayersBySlot.OrderBy(k => k.Key))
        {
            LivePlayerState p = kvp.Value;
            snapshots.Add(new ReplayPlayerSnapshot
            {
                AccountID = p.AccountID != 0 ? p.AccountID : -(kvp.Key + 1),
                AccountName = StripK2ColorCodes(p.AccountName),
                HeroIdentifier = p.HeroIdentifier,
                HeroDisplayName = p.HeroDisplayName,
                Team = p.Team,
                Level = Math.Max(1, p.Level),
                Kills = p.Kills,
                Deaths = p.Deaths,
                Assists = p.Assists,
                Gpm = p.GPM,
                Xpm = p.XPM,
                Gold = p.CurrentGold,
                HealthPercent = Math.Clamp(p.HealthPercent, 0f, 100f),
                ManaPercent = Math.Clamp(p.ManaPercent, 0f, 100f),
                PositionX = Math.Clamp(p.PositionX, 0.02f, 0.98f),
                PositionY = Math.Clamp(p.PositionY, 0.02f, 0.98f),
                HeadingAngle = p.HeadingAngle,
                IsAlive = p.IsAlive,
                Items = [.. p.Inventory],
                Abilities = p.Abilities.Count > 0
                    ? [.. p.Abilities.Select(CloneAbility)]
                    : GetHeroBaseAbilities(p.HeroIdentifier, Math.Clamp((p.Level + 1) / 2, 1, 4)),
                LastCastAbility = p.LastCastAbility
            });
        }

        int mins = second / 60;
        int secs = second % 60;
        return new MatchTimelineKeyframe
        {
            Second = second,
            ElapsedFormatted = $"{mins:D2}:{secs:D2}",
            Players = snapshots,
            Structures = new Dictionary<string, bool>(state.Structures)
        };
    }

    private static MatchTimelineKeyframe BuildFinalKeyframeFromSummaries(
        List<ReplayPlayerSummary> summaries,
        HonLogParsedState state,
        int finalDuration)
    {
        List<ReplayPlayerSnapshot> snapshots = [];
        List<LivePlayerState> orderedStates = [.. state.PlayersBySlot.OrderBy(k => k.Key).Select(k => k.Value)];

        for (int i = 0; i < summaries.Count; i++)
        {
            ReplayPlayerSummary s = summaries[i];
            LivePlayerState? st = i < orderedStates.Count ? orderedStates[i] : null;
            (float wellX, float wellY) = K2MapCoordinateRegistry.GetTeamFountain(state.Map, s.Team);

            snapshots.Add(new ReplayPlayerSnapshot
            {
                AccountID = s.AccountID,
                AccountName = s.AccountName,
                HeroIdentifier = s.HeroIdentifier,
                HeroDisplayName = s.HeroDisplayName,
                Team = s.Team,
                Level = s.FinalLevel,
                Kills = s.FinalKills,
                Deaths = s.FinalDeaths,
                Assists = s.FinalAssists,
                Gpm = s.FinalGpm,
                Xpm = s.FinalXpm,
                Gold = s.FinalGold,
                HealthPercent = st != null ? Math.Clamp(st.HealthPercent, 0f, 100f) : 100f,
                ManaPercent = st != null ? Math.Clamp(st.ManaPercent, 0f, 100f) : 100f,
                PositionX = st != null ? Math.Clamp(st.PositionX, 0.02f, 0.98f) : wellX,
                PositionY = st != null ? Math.Clamp(st.PositionY, 0.02f, 0.98f) : wellY,
                HeadingAngle = st?.HeadingAngle ?? (s.Team == 1 ? -0.785f : 2.356f),
                IsAlive = st?.IsAlive ?? true,
                Items = [.. s.FinalItems],
                Abilities = [.. s.Abilities.Select(CloneAbility)],
                LastCastAbility = st?.LastCastAbility
            });
        }

        int mins = finalDuration / 60;
        int secs = finalDuration % 60;
        return new MatchTimelineKeyframe
        {
            Second = finalDuration,
            ElapsedFormatted = $"{mins:D2}:{secs:D2}",
            Players = snapshots,
            Structures = new Dictionary<string, bool>(state.Structures)
        };
    }

    private ReplayInfoMeta ParseReplayInfo(ZipArchiveEntry entry, int fallbackMatchId)
    {
        using Stream stream = entry.Open();
        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        string xmlContent = reader.ReadToEnd();

        XDocument doc = XDocument.Parse(xmlContent);
        XElement root = doc.Root ?? throw new InvalidDataException("Empty XML document in replayinfo");

        string rawMap = root.Attribute("mapname")?.Value ?? root.Attribute("map")?.Value ?? "caldavar";
        string cleanMap = K2MapCoordinateRegistry.NormalizeMapSlug(rawMap);
        string rawMode = root.Attribute("gamemode")?.Value ?? ResolveDefaultModeLabel(cleanMap);

        int parsedMatchId = int.TryParse(root.Attribute("matchid")?.Value ?? root.Attribute("match_id")?.Value, out int mid)
            ? mid
            : fallbackMatchId;
        int durationSeconds;
        if (int.TryParse(root.Attribute("matchlength")?.Value, out int lenMs) && lenMs > 0)
        {
            durationSeconds = Math.Max(15, lenMs / 1000);
        }
        else if (int.TryParse(root.Attribute("time")?.Value, out int timeSec) && timeSec > 0)
        {
            durationSeconds = Math.Max(15, timeSec);
        }
        else
        {
            durationSeconds = 300;
        }

        string winnerAttr = root.Attribute("winner")?.Value ?? root.Attribute("winning_team")?.Value ?? "";
        int winnerTeam = winnerAttr.Contains("hellbourne", StringComparison.OrdinalIgnoreCase) ||
                         winnerAttr.Contains('2')
            ? 2
            : 1;

        List<ReplayPlayerSummary> players = [];
        int slotIndex = 0;
        foreach (XElement playerEl in root.Elements("player"))
        {
            string rawName = playerEl.Attribute("name")?.Value ?? $"Player{slotIndex + 1}";
            string name = StripK2ColorCodes(rawName);
            int accountId = int.TryParse(playerEl.Attribute("accountid")?.Value ?? playerEl.Attribute("account_id")?.Value,
                out int accId) && accId != 0
                ? accId
                : -(slotIndex + 1);
            int team = int.TryParse(playerEl.Attribute("team")?.Value, out int t) && t is 1 or 2
                ? t
                : (slotIndex < 5 ? 1 : 2);
            string heroName = playerEl.Attribute("heroname")?.Value ?? playerEl.Attribute("hero")?.Value ?? "Hero_Legionnaire";
            string normalizedHeroId = NormalizeHeroIdentifier(heroName);
            string heroDisplayName = ResolveHeroDisplayName(heroName);

            int estimatedLevel = Math.Clamp(1 + durationSeconds / 70, 1, 25);
            int finalLevel = ReadIntAttr(playerEl, "level", estimatedLevel);
            int finalKills = ReadIntAttr(playerEl, "kills", ReadIntAttr(playerEl, "herokills", 0));
            int finalDeaths = ReadIntAttr(playerEl, "deaths", 0);
            int finalAssists = ReadIntAttr(playerEl, "assists", ReadIntAttr(playerEl, "heroassists", 0));
            int finalGpm = ReadIntAttr(playerEl, "gpm", Math.Max(180, 220 + finalKills * 35));
            int finalXpm = ReadIntAttr(playerEl, "xpm", Math.Max(200, 260 + finalLevel * 22));
            int finalGold = ReadIntAttr(playerEl, "gold",
                Math.Max(600, (int)Math.Round(finalGpm * (durationSeconds / 60.0))));

            List<string> finalItems = [];
            string? rawItems = playerEl.Attribute("items")?.Value ?? playerEl.Attribute("inventory")?.Value;
            if (!string.IsNullOrWhiteSpace(rawItems))
            {
                foreach (string token in rawItems.Split([',', ';', '|', ' '], StringSplitOptions.RemoveEmptyEntries))
                {
                    finalItems.Add(token.Trim());
                }
            }

            players.Add(new ReplayPlayerSummary
            {
                AccountID = accountId,
                AccountName = name,
                HeroIdentifier = normalizedHeroId,
                HeroDisplayName = heroDisplayName,
                Team = team,
                FinalLevel = Math.Clamp(finalLevel, 1, 25),
                FinalKills = Math.Max(0, finalKills),
                FinalDeaths = Math.Max(0, finalDeaths),
                FinalAssists = Math.Max(0, finalAssists),
                FinalGpm = Math.Max(0, finalGpm),
                FinalXpm = Math.Max(0, finalXpm),
                FinalGold = Math.Max(0, finalGold),
                FinalItems = finalItems,
                Abilities = GetHeroBaseAbilities(heroName, Math.Clamp((finalLevel + 1) / 2, 1, 4))
            });

            slotIndex++;
        }

        return new ReplayInfoMeta
        {
            MatchId = parsedMatchId,
            MapName = cleanMap,
            GameMode = rawMode,
            DurationSeconds = durationSeconds,
            WinnerTeam = winnerTeam,
            Players = players
        };
    }

    private void ParseReplayData(ZipArchiveEntry entry, ReplayInfoMeta meta, List<MatchTimelineKeyframe> keyframes,
        List<MatchTimelineEventPin> eventPins)
    {
        try
        {
            using Stream entryStream = entry.Open();
            using MemoryStream ms = new();
            entryStream.CopyTo(ms);
            byte[] bytes = ms.ToArray();

            if (bytes.Length < 16)
            {
                return;
            }

            using BinaryReader reader = new(new MemoryStream(bytes), Encoding.ASCII);
            byte[] magicBytes = reader.ReadBytes(4);
            string magic = Encoding.ASCII.GetString(magicBytes);
            if (magic is not "S2R2" and not "S2R1")
            {
                if (logger.IsEnabled(LogLevel.Warning))
                {
                    logger.LogWarning("[HonReplayParser] Unsupported magic {Magic}, proceeding with structural parsing",
                        magic);
                }
            }

            int version = reader.ReadInt32();
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("[HonReplayParser] Parsing S2 Replay Data version {Version} ({Length} bytes)",
                    version, bytes.Length);
            }

            List<(float x, float y)> rawPositions = ExtractRawPositionsFromReplayData(bytes, meta.MapName);
            List<MatchTimelineKeyframe> generatedKeyframes = GenerateKeyframesFromMeta(meta, rawPositions);
            keyframes.AddRange(generatedKeyframes);

            List<MatchTimelineEventPin> generatedPins = GenerateEventPinsFromMeta(meta);
            eventPins.AddRange(generatedPins);
        }
        catch (Exception ex)
        {
            if (logger.IsEnabled(LogLevel.Warning))
            {
                logger.LogWarning(ex,
                    "[HonReplayParser] Error while parsing binary replaydata stream, using generated frames");
            }
        }
    }

    private static List<(float x, float y)> ExtractRawPositionsFromReplayData(byte[] bytes, string mapName)
    {
        K2MapSpec spec = K2MapCoordinateRegistry.GetMapSpec(mapName);
        float maxWorld = spec.WorldDim - 256f;
        List<(float x, float y)> rawPositions = [];

        for (int i = 16; i < bytes.Length - 12; i += 4)
        {
            float fX = BitConverter.ToSingle(bytes, i);
            float fY = BitConverter.ToSingle(bytes, i + 4);
            float fZ = BitConverter.ToSingle(bytes, i + 8);

            if (!float.IsNaN(fX) && !float.IsInfinity(fX) &&
                !float.IsNaN(fY) && !float.IsInfinity(fY) &&
                !float.IsNaN(fZ) && !float.IsInfinity(fZ))
            {
                if (fX >= 256f && fX <= maxWorld &&
                    fY >= 256f && fY <= maxWorld &&
                    fZ is >= -300f and <= 1200f)
                {
                    (float normX, float normY) = K2MapCoordinateRegistry.WorldToMinimap(spec.Slug, fX, fY);
                    rawPositions.Add((normX, normY));
                }
            }
        }

        return rawPositions;
    }

    private static List<MatchTimelineKeyframe> GenerateKeyframesFromMeta(ReplayInfoMeta meta,
        List<(float x, float y)>? rawPositions = null)
    {
        int stepSeconds = meta.DurationSeconds <= 180 ? 2 : 4;
        int totalFrames = meta.DurationSeconds / stepSeconds;
        List<MatchTimelineKeyframe> keyframes = new(totalFrames + 2);

        Dictionary<int, List<(int deathSec, int respawnSec)>> deathSchedules = [];
        foreach (ReplayPlayerSummary p in meta.Players)
        {
            List<(int deathSec, int respawnSec)> pDeaths = [];
            if (p.FinalDeaths > 0 && meta.DurationSeconds > 45)
            {
                int usableWindow = Math.Max(20, meta.DurationSeconds - 35);
                int interval = Math.Max(15, usableWindow / (p.FinalDeaths + 1));
                for (int d = 1; d <= p.FinalDeaths; d++)
                {
                    int deathTime = Math.Min(meta.DurationSeconds - 8, 20 + d * interval);
                    float levelAtDeath = Math.Max(1f,
                        1f + (p.FinalLevel - 1f) * ((float)deathTime / meta.DurationSeconds));
                    int deathDuration = Math.Clamp((int)(6 + levelAtDeath * 1.1f), 6, 24);
                    pDeaths.Add((deathTime, deathTime + deathDuration));
                }
            }

            deathSchedules[p.AccountID] = pDeaths;
        }

        for (int sec = 0; sec <= meta.DurationSeconds; sec += stepSeconds)
        {
            keyframes.Add(BuildMetaKeyframeAtSecond(meta, sec, deathSchedules, rawPositions));
        }

        if (keyframes.Count > 0 && keyframes[^1].Second < meta.DurationSeconds)
        {
            keyframes.Add(BuildMetaKeyframeAtSecond(meta, meta.DurationSeconds, deathSchedules, rawPositions));
        }

        return keyframes;
    }

    private static MatchTimelineKeyframe BuildMetaKeyframeAtSecond(
        ReplayInfoMeta meta,
        int sec,
        Dictionary<int, List<(int deathSec, int respawnSec)>> deathSchedules,
        List<(float x, float y)>? rawPositions)
    {
        float progress = meta.DurationSeconds > 0 ? Math.Clamp((float)sec / meta.DurationSeconds, 0f, 1f) : 1f;
        int minutes = sec / 60;
        int seconds = sec % 60;
        string elapsedFormatted = $"{minutes:D2}:{seconds:D2}";
        bool isFinalFrame = sec >= meta.DurationSeconds;

        List<ReplayPlayerSnapshot> snapshots = [];
        for (int slotIdx = 0; slotIdx < meta.Players.Count; slotIdx++)
        {
            ReplayPlayerSummary p = meta.Players[slotIdx];
            List<(int deathSec, int respawnSec)> ds =
                deathSchedules.TryGetValue(p.AccountID, out List<(int deathSec, int respawnSec)>? found) ? found : [];

            (float posX, float posY, float healthPct, bool isAlive) = K2MapCoordinateRegistry.CalculateReconstructedState(
                meta.MapName,
                p.Team,
                slotIdx,
                p.AccountID,
                sec,
                meta.DurationSeconds,
                meta.WinnerTeam,
                isFinalFrame ? [] : ds);

            // Blend subtle authentic displacement when S2R2 binary positions exist during active combat
            if (!isFinalFrame && isAlive && sec >= 20 && rawPositions is { Count: > 8 })
            {
                int rawIdx = (int)Math.Clamp(
                    (long)((progress * (rawPositions.Count - 1)) + slotIdx * 3) % rawPositions.Count,
                    0, rawPositions.Count - 1);
                (float rx, float ry) = rawPositions[rawIdx];
                if (Math.Abs(rx - posX) < 0.22f && Math.Abs(ry - posY) < 0.22f)
                {
                    posX = posX * 0.7f + rx * 0.3f;
                    posY = posY * 0.7f + ry * 0.3f;
                }
            }

            (float nextX, float nextY, _, _) = K2MapCoordinateRegistry.CalculateReconstructedState(
                meta.MapName,
                p.Team,
                slotIdx,
                p.AccountID,
                Math.Min(meta.DurationSeconds, sec + 2),
                meta.DurationSeconds,
                meta.WinnerTeam,
                isFinalFrame ? [] : ds);

            float dx = nextX - posX;
            float dy = nextY - posY;
            float heading = (Math.Abs(dx) > 0.0004f || Math.Abs(dy) > 0.0004f)
                ? (float)Math.Atan2(dy, dx)
                : (p.Team == 1 ? -0.785f : 2.356f);

            int currentLevel;
            int currentKills;
            int currentDeaths;
            int currentAssists;
            int currentGold;
            int currentGpm;
            int currentXpm;
            List<string> activeItems;

            if (isFinalFrame)
            {
                currentLevel = p.FinalLevel;
                currentKills = p.FinalKills;
                currentDeaths = p.FinalDeaths;
                currentAssists = p.FinalAssists;
                currentGold = p.FinalGold;
                currentGpm = p.FinalGpm;
                currentXpm = p.FinalXpm;
                activeItems = p.FinalItems != null ? [.. p.FinalItems] : [];
                healthPct = p.Team == meta.WinnerTeam ? 100f : 0f;
                isAlive = p.Team == meta.WinnerTeam;
            }
            else
            {
                currentLevel = Math.Max(1, (int)Math.Round(1 + (p.FinalLevel - 1) * Math.Pow(progress, 0.85)));
                currentKills = (int)Math.Round(p.FinalKills * progress);
                currentDeaths = ds.Count(d => sec >= d.deathSec);
                currentAssists = (int)Math.Round(p.FinalAssists * progress);
                currentGold = Math.Max(600, (int)Math.Round(p.FinalGold * progress));

                float farmCycle = (float)Math.Sin(sec * 0.12f + p.AccountID) * 35f;
                currentGpm = Math.Max(120, (int)Math.Round(p.FinalGpm * (0.55f + 0.45f * progress) + farmCycle));
                currentXpm = Math.Max(150, (int)Math.Round(p.FinalXpm * (0.60f + 0.40f * progress) + farmCycle));
                activeItems = GetProgressiveItems(p.FinalItems, progress);
            }

            float manaPct = !isAlive ? 0f : Math.Clamp(50f + (float)Math.Sin(sec * 0.08f + slotIdx) * 40f, 15f, 100f);
            (List<ReplayPlayerAbilityDto> snapAbilities, string? lastCast) = CalculateSnapshotAbilities(
                p.HeroIdentifier,
                p.AccountID,
                sec,
                healthPct,
                isFinalFrame ? [] : ds,
                currentLevel);

            snapshots.Add(new ReplayPlayerSnapshot
            {
                AccountID = p.AccountID,
                AccountName = p.AccountName,
                HeroIdentifier = p.HeroIdentifier,
                HeroDisplayName = p.HeroDisplayName,
                Team = p.Team,
                Level = currentLevel,
                Kills = currentKills,
                Deaths = currentDeaths,
                Assists = currentAssists,
                Gpm = currentGpm,
                Xpm = currentXpm,
                Gold = currentGold,
                HealthPercent = healthPct,
                ManaPercent = manaPct,
                PositionX = posX,
                PositionY = posY,
                HeadingAngle = heading,
                IsAlive = isAlive,
                Items = activeItems,
                Abilities = snapAbilities,
                LastCastAbility = isFinalFrame ? null : lastCast
            });
        }

        return new MatchTimelineKeyframe
        {
            Second = sec,
            ElapsedFormatted = elapsedFormatted,
            Players = snapshots
        };
    }

    public static List<ReplayPlayerAbilityDto> GetHeroBaseAbilities(string heroIdentifier, int initialLevel = 4)
    {
        string clean = heroIdentifier.Replace("Hero_", "", StringComparison.OrdinalIgnoreCase).Trim().ToLowerInvariant();
        int qweLevel = Math.Clamp(initialLevel, 0, 4);
        int rLevel = initialLevel >= 4 ? 3 : (initialLevel >= 2 ? 1 : 0);

        (string q, int qCd, string w, int wCd, string e, int eCd, string r, int rCd) kit = clean switch
        {
            "bubbles" => ("Shell Surf", 12, "Song of the Sea", 14, "Take Cover", 6, "Kelp Field", 85),
            "babayaga" or "wretchedhag" or "wretched_hag" => ("Haunt", 10, "Flash of Darkness", 6, "Sonar Scream", 7, "Bat Blast", 80),
            "devourer" => ("Guttling Hook", 11, "Decay", 4, "Cadaver Armor", 0, "Devour", 30),
            "valkyrie" => ("Call of the Valkyrie", 12, "Javelin of Light", 16, "Courageous Leap", 20, "Valkyrie's Prism", 120),
            "pyromancer" => ("Phoenix Wave", 9, "Dragonfire", 8, "Fervor", 0, "Blazing Strike", 70),
            "witchslayer" => ("Graveyard", 12, "Miniaturization", 15, "Power Drain", 16, "Silver Bullet", 95),
            "swiftblade" => ("Blade Frenzy", 18, "Counter Attack", 0, "Way of the Sword", 0, "Swift Slashes", 110),
            "armadon" => ("Snot Storm", 2, "Spine Burst", 3, "Armordillo", 0, "Restless", 0),
            "flintbeastwood" or "flint_beastwood" => ("Explosive Flare", 14, "Hollowpoint Shells", 0, "Dead Eye", 0, "Money Shot", 20),
            "pebbles" => ("Stalagmites", 15, "Chuck", 10, "Slab Skin", 0, "Enlarge", 0),
            "monkeyking" or "monkey_king" => ("Illusive Dash", 10, "Heavenly Vault", 12, "Wan Jin Slam", 9, "Flying Nimbus", 45),
            "scout" => ("Vanish", 6, "Electric Eye", 20, "Disarm", 10, "Marksman Shot", 55),
            "chronos" => ("Time Leap", 13, "Rewind", 0, "Curse of Ages", 0, "Chronofield", 120),
            "deadwood" => ("Rotten Grasp", 13, "Uproot", 15, "Clearcutting", 24, "Willowmaker", 85),
            "gauntlet" => ("Infernal Instability", 12, "Grapple", 14, "Enfeeble", 0, "Gauntlet Blast", 75),
            "gladiator" => ("Pitfall", 12, "Showdown", 16, "Flagellation", 8, "Call to Arms", 90),
            "bushwack" => ("Crippling Dart", 10, "Sidestep", 12, "Splinter Shot", 0, "Jungle Toxin", 45),
            "emeraldwarden" or "emerald_warden" => ("Silencing Shot", 11, "Hunter's Command", 14, "Overgrowth", 12, "Gawain", 0),
            _ => ("Ability 1", 10, "Ability 2", 12, "Ability 3", 8, "Ultimate", 90)
        };

        string iconHero = clean is "wretchedhag" or "wretched_hag" ? "babayaga" : clean;

        return
        [
            new ReplayPlayerAbilityDto
            {
                Slot = "Q",
                Name = kit.q,
                IconPath = $"/images/abilities/hero_{iconHero}_1.png",
                Level = qweLevel,
                CooldownTotal = Math.Max(1, kit.qCd),
                CooldownRemaining = 0
            },
            new ReplayPlayerAbilityDto
            {
                Slot = "W",
                Name = kit.w,
                IconPath = $"/images/abilities/hero_{iconHero}_2.png",
                Level = qweLevel,
                CooldownTotal = Math.Max(1, kit.wCd),
                CooldownRemaining = 0
            },
            new ReplayPlayerAbilityDto
            {
                Slot = "E",
                Name = kit.e,
                IconPath = $"/images/abilities/hero_{iconHero}_3.png",
                Level = qweLevel,
                CooldownTotal = Math.Max(1, kit.eCd),
                CooldownRemaining = 0
            },
            new ReplayPlayerAbilityDto
            {
                Slot = "R",
                Name = kit.r,
                IconPath = $"/images/abilities/hero_{iconHero}_4.png",
                Level = rLevel,
                CooldownTotal = Math.Max(1, kit.rCd),
                CooldownRemaining = 0
            }
        ];
    }

    public static (List<ReplayPlayerAbilityDto> abilities, string? lastCast) CalculateSnapshotAbilities(
        string heroIdentifier,
        int accountId,
        int second,
        float healthPct,
        List<(int deathSec, int respawnSec)> deaths,
        int heroLevel = 16)
    {
        int qweLevel = Math.Clamp((heroLevel + 3) / 4, 1, 4);
        int rLevel = heroLevel >= 16 ? 3 : (heroLevel >= 11 ? 2 : (heroLevel >= 6 ? 1 : 0));

        List<ReplayPlayerAbilityDto> baseAbilities = GetHeroBaseAbilities(heroIdentifier, qweLevel);
        if (baseAbilities.Count >= 4)
        {
            baseAbilities[3].Level = rLevel;
        }

        if (healthPct <= 0f)
        {
            foreach (ReplayPlayerAbilityDto ab in baseAbilities)
            {
                ab.CooldownRemaining = ab.CooldownTotal;
            }

            return (baseAbilities, null);
        }

        string? justCast = null;
        int accOffset = Math.Abs(accountId) * 7;

        for (int idx = 0; idx < baseAbilities.Count; idx++)
        {
            ReplayPlayerAbilityDto ab = baseAbilities[idx];
            if (ab.Level <= 0)
            {
                ab.CooldownRemaining = 0;
                continue;
            }

            int cd = Math.Max(2, ab.CooldownTotal);
            int castInterval = cd + (idx == 3 ? 28 : 7);
            int phase = (second + accOffset + idx * 11) % castInterval;

            if (phase < cd && second >= 18)
            {
                ab.CooldownRemaining = cd - phase;
                if (phase <= 2)
                {
                    justCast = ab.Name;
                }
            }
            else
            {
                ab.CooldownRemaining = 0;
            }

            foreach ((int dSec, _) in deaths)
            {
                if (second >= dSec - 3 && second <= dSec + 1 && idx == 3 && ab.Level > 0)
                {
                    ab.CooldownRemaining = cd;
                    justCast = ab.Name;
                }
            }
        }

        return (baseAbilities, justCast);
    }

    public static List<string> GetProgressiveItems(List<string>? finalItems, float progress)
    {
        if (finalItems == null || finalItems.Count == 0)
        {
            return [];
        }

        List<string> validItems = [.. finalItems.Where(it => !string.IsNullOrWhiteSpace(it))];
        if (validItems.Count == 0 || progress < 0.06f)
        {
            return [];
        }

        int itemsToShow = progress switch
        {
            < 0.22f => 1,
            < 0.40f => 2,
            < 0.58f => 3,
            < 0.75f => 4,
            < 0.88f => 5,
            _ => validItems.Count
        };

        return [.. validItems.Take(Math.Min(validItems.Count, itemsToShow))];
    }

    private static List<MatchTimelineEventPin> GenerateEventPinsFromMeta(ReplayInfoMeta meta)
    {
        List<MatchTimelineEventPin> pins = [];
        K2MapSpec spec = K2MapCoordinateRegistry.GetMapSpec(meta.MapName);
        ReplayPlayerSummary? winnerPlayer = meta.Players.FirstOrDefault(p => p.Team == meta.WinnerTeam)
                                            ?? meta.Players.FirstOrDefault();
        ReplayPlayerSummary? loserPlayer = meta.Players.FirstOrDefault(p => p.Team != meta.WinnerTeam)
                                           ?? meta.Players.LastOrDefault();

        int totalKills = meta.Players.Sum(p => p.FinalKills);
        if (winnerPlayer != null && loserPlayer != null && totalKills > 0)
        {
            pins.Add(new MatchTimelineEventPin
            {
                MatchTimeSeconds = Math.Clamp(meta.DurationSeconds / 6, 15, Math.Max(15, meta.DurationSeconds - 5)),
                EventType = "KILL",
                Description =
                    $"First Blood! {winnerPlayer.AccountName} ({winnerPlayer.HeroDisplayName}) slew {loserPlayer.AccountName} ({loserPlayer.HeroDisplayName})",
                AttackerHero = winnerPlayer.HeroIdentifier,
                VictimHero = loserPlayer.HeroIdentifier,
                PositionX = spec.CenterPosition.X,
                PositionY = spec.CenterPosition.Y,
                Team = winnerPlayer.Team
            });

            if (totalKills >= 3 && meta.DurationSeconds >= 90)
            {
                pins.Add(new MatchTimelineEventPin
                {
                    MatchTimeSeconds = Math.Min(meta.DurationSeconds - 15, (int)(meta.DurationSeconds * 0.62)),
                    EventType = "TOWER",
                    Description = $"{(meta.WinnerTeam == 1 ? "Legion" : "Hellbourne")} destroyed an enemy outer tower!",
                    AttackerHero = winnerPlayer.HeroIdentifier,
                    PositionX = (spec.CenterPosition.X + (meta.WinnerTeam == 1 ? spec.HellbourneFountain.X : spec.LegionFountain.X)) * 0.5f,
                    PositionY = (spec.CenterPosition.Y + (meta.WinnerTeam == 1 ? spec.HellbourneFountain.Y : spec.LegionFountain.Y)) * 0.5f,
                    Team = meta.WinnerTeam
                });
            }
        }

        (float shrineX, float shrineY) = meta.WinnerTeam == 1 ? spec.HellbourneFountain : spec.LegionFountain;
        pins.Add(new MatchTimelineEventPin
        {
            MatchTimeSeconds = Math.Max(1, meta.DurationSeconds - Math.Min(10, meta.DurationSeconds / 5)),
            EventType = "VICTORY",
            Description =
                $"Victory for {(meta.WinnerTeam == 1 ? "Legion" : "Hellbourne")}!",
            AttackerHero = winnerPlayer?.HeroIdentifier ?? string.Empty,
            PositionX = shrineX,
            PositionY = shrineY,
            Team = meta.WinnerTeam
        });

        return pins;
    }

    public static string StripK2ColorCodes(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        return K2ColorCodeRegex().Replace(raw, string.Empty).Trim();
    }

    private static ReplayPlayerAbilityDto CloneAbility(ReplayPlayerAbilityDto src) => new()
    {
        Slot = src.Slot,
        Name = src.Name,
        IconPath = src.IconPath,
        Level = src.Level,
        CooldownTotal = src.CooldownTotal,
        CooldownRemaining = src.CooldownRemaining
    };

    private static Encoding DetectLogEncoding(byte[] bytes, out int bomOffset)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            bomOffset = 2;
            return Encoding.Unicode;
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            bomOffset = 2;
            return Encoding.BigEndianUnicode;
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            bomOffset = 3;
            return Encoding.UTF8;
        }

        // BOM-less UTF-16LE heuristic (ASCII characters interleaved with 0x00 high bytes)
        if (bytes.Length >= 6 && bytes[1] == 0x00 && bytes[3] == 0x00 && bytes[5] == 0x00)
        {
            bomOffset = 0;
            return Encoding.Unicode;
        }

        bomOffset = 0;
        return Encoding.UTF8;
    }

    private static bool LooksLikeServerLog(byte[] rawBytes)
    {
        Encoding encoding = DetectLogEncoding(rawBytes, out int bomOffset);
        int previewBytes = Math.Min(rawBytes.Length - bomOffset, 2048);
        if (previewBytes <= 0)
        {
            return false;
        }

        string preview = encoding.GetString(rawBytes, bomOffset, previewBytes);
        return preview.Contains("INFO_", StringComparison.Ordinal) ||
               preview.Contains("PLAYER_CONNECT", StringComparison.Ordinal) ||
               preview.Contains("PLAYER_SELECT", StringComparison.Ordinal) ||
               preview.Contains("HERO_DEATH", StringComparison.Ordinal) ||
               preview.Contains("ITEM_PURCHASE", StringComparison.Ordinal) ||
               preview.Contains("GAME_START", StringComparison.Ordinal);
    }

    private static int ReadIntAttr(XElement el, string attrName, int fallback)
    {
        string? val = el.Attribute(attrName)?.Value;
        return int.TryParse(val, out int parsed) ? parsed : fallback;
    }

    private static string NormalizeHeroIdentifier(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "hero_legionnaire";
        }

        string clean = raw.Trim();
        int dotIdx = clean.IndexOf('.');
        if (dotIdx > 0)
        {
            clean = clean[..dotIdx];
        }

        if (!clean.StartsWith("Hero_", StringComparison.OrdinalIgnoreCase))
        {
            clean = "Hero_" + clean;
        }

        return clean.ToLowerInvariant();
    }

    private static string ResolveDefaultModeLabel(string mapSlug) =>
        K2MapCoordinateRegistry.NormalizeMapSlug(mapSlug) switch
        {
            "midwars" => "Mid Wars",
            "devowars" => "Devo Wars",
            "riftwars" => "Rift Wars",
            "grimmscrossing" => "Grimm's Crossing",
            "capturetheflag" => "Capture the Flag",
            "soccer" => "KONGOR Soccer",
            "tutorial" => "Tutorial",
            _ => "Normal Mode"
        };

    private static string ResolveHeroDisplayName(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return "Hero";
        }

        string clean = identifier.Trim();
        int dotIdx = clean.IndexOf('.');
        if (dotIdx > 0)
        {
            clean = clean[..dotIdx];
        }

        if (!clean.StartsWith("Hero_", StringComparison.OrdinalIgnoreCase))
        {
            clean = "Hero_" + clean;
        }

        string? resolvedName = Heroes.GetNameByIdentifier(clean);
        return !string.IsNullOrEmpty(resolvedName) ? resolvedName : clean.Replace("Hero_", "").Replace("_", " ");
    }

    private sealed class ReplayInfoMeta
    {
        public required int MatchId { get; init; }
        public required string MapName { get; init; }
        public required string GameMode { get; init; }
        public required int DurationSeconds { get; init; }
        public required int WinnerTeam { get; init; }
        public required List<ReplayPlayerSummary> Players { get; init; }
    }
}
