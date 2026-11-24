// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ASPIRE.Common.Constants;
using MERRICK.DatabaseContext.Entities;
using MERRICK.DatabaseContext.Entities.Statistics;
using MERRICK.DatabaseContext.Persistence;
using Microsoft.EntityFrameworkCore;
using NEXUS.Contracts.DTOs;

namespace MERRICK.DatabaseContext.Repositories;

/// <summary>
///     Shared query &amp; projection reader for custom map scoreboards and per-map career profiles,
///     used by both MasterServer (K2 client UI) and WebAPI (web portal).
/// </summary>
public static class CustomMapStatsReader
{
    public static async Task<CustomMatchStatsResponseDto?> GetMatchCustomStatsAsync(
        MerrickContext db,
        int matchId,
        Func<string, (string? DisplayName, string? IconVfs)>? heroResolver = null,
        CancellationToken ct = default)
    {
        MatchStatistics? match = await db.MatchStatistics
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.MatchID == matchId, ct);

        if (match is null)
        {
            return null;
        }

        List<PlayerStatistics> playerRows = await db.PlayerStatistics
            .AsNoTracking()
            .Include(p => p.Inventories)
            .Where(p => p.MatchID == matchId)
            .ToListAsync(ct);

        string canonicalMap = MapCatalog.NormalizeSlug(match.Map);
        CustomMapStatSchema schema = CustomMapStatSchemaCatalog.ResolveForMap(canonicalMap, playerRows.Count);
        MapDefinition? mapDef = MapCatalog.GetDefinition(canonicalMap);
        string mapDisplayName = mapDef?.DisplayName ?? MapCatalog.FormatMapName(canonicalMap);

        int? waveReached = null;
        int? maxWaves = null;
        string? difficulty = null;
        int? legionLivesLeft = null;
        int? hellbourneLivesLeft = null;
        int? topRoundWins = null;
        string? topRoundWinner = null;
        int? roundsPlayed = null;

        if (!string.IsNullOrWhiteSpace(match.CustomMatchStats))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(match.CustomMatchStats);
                JsonElement root = doc.RootElement;
                if (root.TryGetProperty("wave_reached", out JsonElement wr) && wr.TryGetInt32(out int wrVal))
                    waveReached = wrVal;
                if (root.TryGetProperty("max_waves", out JsonElement mw) && mw.TryGetInt32(out int mwVal))
                    maxWaves = mwVal;
                if (root.TryGetProperty("difficulty", out JsonElement df) && df.ValueKind == JsonValueKind.String)
                    difficulty = df.GetString();
                if (root.TryGetProperty("legion_lives_left", out JsonElement ll) && ll.TryGetInt32(out int llVal))
                    legionLivesLeft = llVal;
                if (root.TryGetProperty("hellbourne_lives_left", out JsonElement hl) && hl.TryGetInt32(out int hlVal))
                    hellbourneLivesLeft = hlVal;
                if (root.TryGetProperty("top_round_wins", out JsonElement trw) && trw.TryGetInt32(out int trwVal))
                    topRoundWins = trwVal;
                if (root.TryGetProperty("top_round_winner", out JsonElement trn) && trn.ValueKind == JsonValueKind.String)
                    topRoundWinner = trn.GetString();
                if (root.TryGetProperty("rounds_played", out JsonElement rp) && rp.TryGetInt32(out int rpVal))
                    roundsPlayed = rpVal;
            }
            catch (JsonException ex)
            {
                // Derived fallback below handles display on legacy/corrupt payloads.
                Trace.TraceWarning("[CustomMapStatsReader] Malformed MatchStatistics.CustomMatchStats JSON for MatchId={0}: {1}", match.MatchID, ex.Message);
            }
        }

        if (schema.SchemaKey == "enfos" && playerRows.Count > 0)
        {
            waveReached ??= (int)Math.Round(playerRows.Max(p => p.GameplayStat0));
            maxWaves ??= 39;
            difficulty ??= CustomMapStatSchemaCatalog.FormatDifficultyCode(playerRows.Max(p => p.GameplayStat1));
            legionLivesLeft ??= (int)Math.Round(playerRows
                .Where(p => p.Team == 1)
                .Select(p => p.GameplayStat3)
                .DefaultIfEmpty(match.ScoreTeam1)
                .Max());
            hellbourneLivesLeft ??= (int)Math.Round(playerRows
                .Where(p => p.Team == 2)
                .Select(p => p.GameplayStat3)
                .DefaultIfEmpty(match.ScoreTeam2)
                .Max());
        }
        else if (schema.SchemaKey == "duel_lives" && playerRows.Count > 0)
        {
            legionLivesLeft ??= (int)Math.Round(playerRows
                .Where(p => p.Team == 1)
                .Select(p => p.GameplayStat0)
                .DefaultIfEmpty(match.ScoreTeam1)
                .Max());
            hellbourneLivesLeft ??= (int)Math.Round(playerRows
                .Where(p => p.Team == 2)
                .Select(p => p.GameplayStat0)
                .DefaultIfEmpty(match.ScoreTeam2)
                .Max());
        }
        else if (schema.SchemaKey == "arena" && playerRows.Count > 0)
        {
            PlayerStatistics topPlayer = playerRows
                .OrderByDescending(p => p.GameplayStat0)
                .ThenByDescending(p => p.HeroKills)
                .ThenBy(p => p.AccountID)
                .First();
            topRoundWins ??= (int)Math.Round(topPlayer.GameplayStat0);
            topRoundWinner ??= topPlayer.AccountName;
            roundsPlayed ??= (int)Math.Round(playerRows.Max(p => p.GameplayStat1));
        }

        (string team1Label, string team2Label) = ResolveTeamLabels(schema);
        string scoreSummaryText = BuildBannerSummaryText(
            schema, team1Label, team2Label, match.ScoreTeam1, match.ScoreTeam2,
            waveReached, maxWaves, difficulty, legionLivesLeft, hellbourneLivesLeft,
            topRoundWins, topRoundWinner, roundsPlayed);

        CustomMatchBannerDto banner = new(
            match.MatchID,
            canonicalMap,
            mapDisplayName,
            schema.SchemaKey,
            schema.SchemaVersion,
            schema.Is10v10 || playerRows.Count > 10,
            schema.IsCoOpPvE,
            playerRows.Count,
            match.ScoreTeam1,
            match.ScoreTeam2,
            team1Label,
            team2Label,
            scoreSummaryText,
            match.TimePlayed,
            match.GameMode ?? "",
            waveReached,
            maxWaves,
            difficulty,
            legionLivesLeft,
            hellbourneLivesLeft);

        List<CustomStatFieldSpec> visibleFields = schema.Fields.Where(f => f.ShowOnScoreboard).ToList();
        List<CustomStatColumnDto> columns = visibleFields
            .Select(f => new CustomStatColumnDto(f.Key, f.ShortHeader, f.DisplayLabel, f.Format.ToString(), f.HigherIsBetter))
            .ToList();

        bool schemaUpgradedForMatch = match.CustomSchemaVersion < schema.SchemaVersion;
        List<CustomMatchPlayerDto> players = new(playerRows.Count);
        foreach (PlayerStatistics p in playerRows
                     .OrderBy(r => r.Team)
                     .ThenBy(r => r.LobbyPosition)
                     .ThenByDescending(r => r.HeroKills)
                     .ThenBy(r => r.AccountID))
        {
            Dictionary<string, double>? customValues = null;
            if (!schemaUpgradedForMatch && !string.IsNullOrWhiteSpace(p.CustomStats))
            {
                try
                {
                    customValues = CustomMapStatSchemaCatalog.DeserializePlayerCustomStatsJson(p.CustomStats);
                }
                catch (JsonException ex)
                {
                    Trace.TraceWarning("[CustomMapStatsReader] Malformed PlayerStatistics.CustomStats JSON for MatchId={0}, AccountId={1}: {2}", match.MatchID, p.AccountID, ex.Message);
                    customValues = null;
                }
            }

            customValues ??= CustomMapStatSchemaCatalog.BuildPlayerCustomStats(
                schema,
                p.GameplayStat0, p.GameplayStat1, p.GameplayStat2, p.GameplayStat3, p.GameplayStat4,
                p.GameplayStat5, p.GameplayStat6, p.GameplayStat7, p.GameplayStat8, p.GameplayStat9,
                p.HeroKills, p.HeroDeaths, p.TeamCreepKills, p.NeutralCreepKills, p.Denies,
                p.HeroDamage, p.BuildingDamage, p.Gold, p.Experience, p.WardsPlaced, p.SecondsPlayed);

            Dictionary<string, string> formattedColumns = new(StringComparer.Ordinal);
            foreach (CustomStatFieldSpec field in visibleFields)
            {
                double val = customValues.GetValueOrDefault(field.Key);
                formattedColumns[field.Key] = CustomMapStatSchemaCatalog.FormatStatValue(val, field.Format);
            }

            (string? knownName, string? knownIcon) = heroResolver?.Invoke(p.HeroIdentifier) ?? (null, null);
            (string _, string heroDisplayName, string heroIconVfs, bool isCustomUnit) =
                CustomHeroCatalog.ResolveHeroDisplay(p.HeroIdentifier, knownName, knownIcon);

            int secs = Math.Max(1, p.SecondsPlayed);
            double mins = secs / 60.0;
            double gpm = Math.Round(p.Gold / mins, 1);
            double xpm = Math.Round(p.Experience / mins, 1);
            double apm = Math.Round(p.Actions / mins, 1);

            List<string> items = p.Inventory.Where(i => !string.IsNullOrWhiteSpace(i)).Take(6).ToList();

            players.Add(new CustomMatchPlayerDto(
                p.AccountID,
                p.AccountName ?? "",
                p.ClanTag ?? "",
                p.Team,
                p.LobbyPosition,
                p.Win > 0,
                p.Disconnected > 0,
                p.HeroIdentifier,
                heroDisplayName,
                heroIconVfs,
                p.AlternativeAvatarName,
                isCustomUnit,
                p.HeroLevel,
                p.HeroKills,
                p.HeroDeaths,
                p.HeroAssists,
                p.TeamCreepKills,
                p.Denies,
                p.NeutralCreepKills,
                p.Gold,
                gpm,
                xpm,
                p.WardsPlaced,
                p.HeroDamage,
                p.BuildingDamage,
                p.Actions,
                apm,
                p.SecondsPlayed,
                p.MVP > 0,
                items,
                customValues,
                formattedColumns));
        }

        return new CustomMatchStatsResponseDto(match.MatchID, banner, columns, players);
    }

    public static async Task<PlayerMapStatsResponseDto?> GetPlayerMapStatsAsync(
        MerrickContext db,
        string nickname,
        string? rawMapSlug,
        Func<string, (string? DisplayName, string? IconVfs)>? heroResolver = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(nickname))
        {
            return null;
        }

        string cleanNick = nickname.Trim().ToLowerInvariant();
        Account? account = await db.Accounts
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Name.ToLower() == cleanNick, ct);

        if (account is null)
        {
            return null;
        }

        string mapSlug = MapCatalog.NormalizeSlug(string.IsNullOrWhiteSpace(rawMapSlug) ? "caldavar" : rawMapSlug);
        CustomMapStatSchema schema = CustomMapStatSchemaCatalog.ResolveForMap(mapSlug);
        MapDefinition? mapDef = MapCatalog.GetDefinition(mapSlug);
        string mapDisplayName = mapDef?.DisplayName ?? MapCatalog.FormatMapName(mapSlug);

        AccountMapStatistics? mapSummary = await db.AccountMapStatistics
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.AccountID == account.ID && x.MapSlug == mapSlug, ct);

        // Fetch player matches joined with MatchStatistics to power RecentMatches and fallback aggregation
        var rawHistory = await db.PlayerStatistics
            .AsNoTracking()
            .Where(ps => ps.AccountID == account.ID && ps.Match != null)
            .OrderByDescending(ps => ps.MatchID)
            .Take(400)
            .Select(ps => new
            {
                Stats = ps,
                MatchMap = ps.Match!.Map,
                MatchDate = ps.Match.TimestampRecorded,
                Duration = ps.Match.TimePlayed,
                Score1 = ps.Match.ScoreTeam1,
                Score2 = ps.Match.ScoreTeam2,
                CustomMatchStats = ps.Match.CustomMatchStats,
                MatchSchemaVersion = ps.Match.CustomSchemaVersion
            })
            .ToListAsync(ct);

        var matchingHistory = rawHistory
            .Where(r => MapCatalog.NormalizeSlug(r.MatchMap) == mapSlug)
            .ToList();

        int matchesPlayed = mapSummary?.MatchesPlayed ?? 0;
        int matchesWon = mapSummary?.MatchesWon ?? 0;
        int matchesLost = mapSummary?.MatchesLost ?? 0;
        long timePlayedSeconds = mapSummary?.TimePlayedSeconds ?? 0;
        int heroKills = mapSummary?.HeroKills ?? 0;
        int deaths = mapSummary?.Deaths ?? 0;
        int heroAssists = mapSummary?.HeroAssists ?? 0;
        string? favoriteHero = mapSummary?.FavoriteHeroIdentifier;

        Dictionary<string, int> heroPlayCounts = ParseIntDict(mapSummary?.HeroPlayCountsJson);
        Dictionary<string, double> customTotals = ParseDoubleDict(mapSummary?.CustomTotals);
        Dictionary<string, double> bestRecords = ParseDoubleDict(mapSummary?.BestRecords);

        int storedSchemaVersion = customTotals.TryGetValue("_schema_version", out double sv) ? (int)Math.Round(sv) : 1;
        bool needsSchemaUpgradeRecompute = mapSummary is not null && storedSchemaVersion < schema.SchemaVersion;

        // Fallback aggregation from historical PlayerStatistics rows if AccountMapStatistics is empty yet
        // OR if the persisted rollup predates the active custom-map schema version (e.g. v1 -> v2).
        if ((matchesPlayed == 0 || needsSchemaUpgradeRecompute) && matchingHistory.Count > 0)
        {
            bool rebuildCounters = matchesPlayed == 0;
            if (rebuildCounters)
            {
                matchesPlayed = matchingHistory.Count;
                matchesWon = matchingHistory.Count(r => r.Stats.Win > 0);
                matchesLost = matchingHistory.Count(r => r.Stats.Win == 0);
                timePlayedSeconds = matchingHistory.Sum(r => (long)r.Stats.SecondsPlayed);
                heroKills = matchingHistory.Sum(r => r.Stats.HeroKills);
                deaths = matchingHistory.Sum(r => r.Stats.HeroDeaths);
                heroAssists = matchingHistory.Sum(r => r.Stats.HeroAssists);
            }
            else
            {
                customTotals.Clear();
                bestRecords.Clear();
            }

            foreach (var r in matchingHistory)
            {
                if (rebuildCounters)
                {
                    string heroId = string.IsNullOrWhiteSpace(r.Stats.HeroIdentifier) ? "Hero_Unknown" : r.Stats.HeroIdentifier;
                    heroPlayCounts[heroId] = heroPlayCounts.GetValueOrDefault(heroId) + 1;
                }

                Dictionary<string, double>? rowCustom = null;
                if (r.MatchSchemaVersion >= schema.SchemaVersion && !string.IsNullOrWhiteSpace(r.Stats.CustomStats))
                {
                    try
                    {
                        rowCustom = CustomMapStatSchemaCatalog.DeserializePlayerCustomStatsJson(r.Stats.CustomStats);
                    }
                    catch (JsonException ex)
                    {
                        Trace.TraceWarning("[CustomMapStatsReader] Malformed historical PlayerStatistics.CustomStats JSON for MatchId={0}, AccountId={1}: {2}", r.Stats.MatchID, account.ID, ex.Message);
                        rowCustom = null;
                    }
                }

                rowCustom ??= CustomMapStatSchemaCatalog.BuildPlayerCustomStats(
                    schema,
                    r.Stats.GameplayStat0, r.Stats.GameplayStat1, r.Stats.GameplayStat2, r.Stats.GameplayStat3, r.Stats.GameplayStat4,
                    r.Stats.GameplayStat5, r.Stats.GameplayStat6, r.Stats.GameplayStat7, r.Stats.GameplayStat8, r.Stats.GameplayStat9,
                    r.Stats.HeroKills, r.Stats.HeroDeaths, r.Stats.TeamCreepKills, r.Stats.NeutralCreepKills, r.Stats.Denies,
                    r.Stats.HeroDamage, r.Stats.BuildingDamage, r.Stats.Gold, r.Stats.Experience, r.Stats.WardsPlaced, r.Stats.SecondsPlayed);

                foreach (CustomStatFieldSpec field in schema.Fields)
                {
                    if (field.Aggregation == CustomStatAggregation.RatioPercent) continue;
                    double v = rowCustom.GetValueOrDefault(field.Key);
                    if (field.Aggregation == CustomStatAggregation.Sum)
                    {
                        customTotals[field.Key] = Math.Round(customTotals.GetValueOrDefault(field.Key) + v, 2);
                    }
                    else
                    {
                        customTotals[field.Key] = Math.Round(Math.Max(customTotals.GetValueOrDefault(field.Key), v), 2);
                    }

                    if (field.HigherIsBetter)
                    {
                        bestRecords[field.Key] = Math.Round(Math.Max(bestRecords.GetValueOrDefault(field.Key), v), 2);
                    }
                }

                double kda = (r.Stats.HeroKills + r.Stats.HeroAssists) / (double)Math.Max(1, r.Stats.HeroDeaths);
                bestRecords["kda"] = Math.Max(bestRecords.GetValueOrDefault("kda"), Math.Round(kda, 2));
                bestRecords["kills"] = Math.Max(bestRecords.GetValueOrDefault("kills"), r.Stats.HeroKills);
            }

            foreach (CustomStatFieldSpec field in schema.Fields)
            {
                if (field.Aggregation == CustomStatAggregation.RatioPercent &&
                    !string.IsNullOrEmpty(field.NumeratorKey) &&
                    !string.IsNullOrEmpty(field.DenominatorKey))
                {
                    double num = customTotals.GetValueOrDefault(field.NumeratorKey);
                    double den = customTotals.GetValueOrDefault(field.DenominatorKey);
                    customTotals[field.Key] = den > 0 ? Math.Round(num * 100.0 / den, 1) : 0.0;
                }
            }

            if (rebuildCounters)
            {
                favoriteHero = heroPlayCounts.OrderByDescending(kv => kv.Value).FirstOrDefault().Key;
            }
        }
        else if (needsSchemaUpgradeRecompute)
        {
            // No matching history rows available; strip obsolete v1 keys not in active schema.
            HashSet<string> validKeys = schema.Fields.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
            foreach (string staleKey in customTotals.Keys.Where(k => !validKeys.Contains(k)).ToList())
            {
                customTotals.Remove(staleKey);
            }
            foreach (string staleBestKey in bestRecords.Keys.Where(k => !validKeys.Contains(k) && k != "kda" && k != "kills").ToList())
            {
                bestRecords.Remove(staleBestKey);
            }
        }

        double winRate = matchesPlayed > 0 ? Math.Round(matchesWon * 100.0 / matchesPlayed, 1) : 0.0;

        List<PlayerMapHeroSummaryDto> topHeroes = heroPlayCounts
            .OrderByDescending(kv => kv.Value)
            .Take(5)
            .Select(kv =>
            {
                (string? knownName, string? knownIcon) = heroResolver?.Invoke(kv.Key) ?? (null, null);
                (string canonicalId, string displayName, string iconVfs, bool isCustom) =
                    CustomHeroCatalog.ResolveHeroDisplay(kv.Key, knownName, knownIcon);
                return new PlayerMapHeroSummaryDto(canonicalId, displayName, iconVfs, kv.Value, isCustom);
            })
            .ToList();

        List<PlayerMapSummaryCardDto> summaryCards = [];
        foreach (CustomStatFieldSpec field in schema.Fields.Where(f => f.ShowOnProfileCard).Take(6))
        {
            double rawTotal = customTotals.GetValueOrDefault(field.Key);
            double rawAvg = matchesPlayed > 0 && field.Aggregation == CustomStatAggregation.Sum
                ? Math.Round(rawTotal / matchesPlayed, 1)
                : rawTotal;

            summaryCards.Add(new PlayerMapSummaryCardDto(
                field.Key,
                field.DisplayLabel,
                field.ShortHeader,
                CustomMapStatSchemaCatalog.FormatStatValue(rawTotal, field.Format),
                field.Format == CustomStatFormat.Percent1 || field.Aggregation != CustomStatAggregation.Sum
                    ? CustomMapStatSchemaCatalog.FormatStatValue(rawAvg, field.Format)
                    : rawAvg.ToString("0.0", CultureInfo.InvariantCulture),
                rawTotal,
                rawAvg));
        }

        List<PlayerMapRecordItemDto> recordItems = [];
        foreach ((string key, double val) in bestRecords.OrderBy(kv => kv.Key))
        {
            if (key.StartsWith('_'))
            {
                continue;
            }
            string rawFieldKey = key.StartsWith("best_", StringComparison.Ordinal) ? key["best_".Length..] : key;
            CustomStatFieldSpec? fieldSpec = schema.Fields.FirstOrDefault(f => f.Key == rawFieldKey);
            if (fieldSpec is null && rawFieldKey != "kda" && rawFieldKey != "kills")
            {
                continue;
            }
            string label = fieldSpec is not null
                ? $"Best {fieldSpec.DisplayLabel}"
                : $"Best {rawFieldKey.Replace("_", " ", StringComparison.Ordinal).ToUpperInvariant()}";
            CustomStatFormat fmt = fieldSpec?.Format ?? (rawFieldKey == "kda" ? CustomStatFormat.Float1 : CustomStatFormat.Int);
            recordItems.Add(new PlayerMapRecordItemDto(
                key,
                label,
                CustomMapStatSchemaCatalog.FormatStatValue(val, fmt),
                val));
        }

        (string team1Label, string team2Label) = ResolveTeamLabels(schema);
        List<PlayerMapRecentMatchSummaryDto> recentMatches = matchingHistory
            .Take(15)
            .Select(r =>
            {
                (string? knownName, string? knownIcon) = heroResolver?.Invoke(r.Stats.HeroIdentifier) ?? (null, null);
                (string canonicalId, string displayName, string iconVfs, bool _) =
                    CustomHeroCatalog.ResolveHeroDisplay(r.Stats.HeroIdentifier, knownName, knownIcon);
                string summary = $"{team1Label} {r.Score1} - {r.Score2} {team2Label}";
                return new PlayerMapRecentMatchSummaryDto(
                    r.Stats.MatchID,
                    r.MatchDate,
                    r.Duration,
                    r.Stats.Win > 0,
                    canonicalId,
                    displayName,
                    iconVfs,
                    r.Stats.HeroKills,
                    r.Stats.HeroDeaths,
                    r.Stats.HeroAssists,
                    summary);
            })
            .ToList();

        return new PlayerMapStatsResponseDto(
            account.ID,
            account.Name,
            mapSlug,
            mapDisplayName,
            schema.SchemaKey,
            schema.SchemaVersion,
            matchesPlayed,
            matchesWon,
            matchesLost,
            winRate,
            timePlayedSeconds,
            heroKills,
            deaths,
            heroAssists,
            favoriteHero,
            topHeroes,
            summaryCards,
            recordItems,
            recentMatches);
    }

    public static async Task<IReadOnlyList<PlayerMapOverviewItemDto>> GetPlayerMapOverviewAsync(
        MerrickContext db,
        string nickname,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(nickname))
        {
            return [];
        }

        string cleanNick = nickname.Trim().ToLowerInvariant();
        int? accountId = await db.Accounts
            .AsNoTracking()
            .Where(a => a.Name.ToLower() == cleanNick)
            .Select(a => (int?)a.ID)
            .FirstOrDefaultAsync(ct);

        if (!accountId.HasValue)
        {
            return [];
        }

        List<AccountMapStatistics> rows = await db.AccountMapStatistics
            .AsNoTracking()
            .Where(x => x.AccountID == accountId.Value && x.MatchesPlayed > 0)
            .OrderByDescending(x => x.MatchesPlayed)
            .ToListAsync(ct);

        return rows.Select(r =>
        {
            MapDefinition? def = MapCatalog.GetDefinition(r.MapSlug);
            double winRate = r.MatchesPlayed > 0 ? Math.Round(r.MatchesWon * 100.0 / r.MatchesPlayed, 1) : 0.0;
            return new PlayerMapOverviewItemDto(
                r.MapSlug,
                def?.DisplayName ?? MapCatalog.FormatMapName(r.MapSlug),
                r.SchemaKey,
                r.MatchesPlayed,
                r.MatchesWon,
                r.MatchesLost,
                winRate,
                r.LastPlayedAtUtc);
        }).ToList();
    }

    private static (string Team1Label, string Team2Label) ResolveTeamLabels(CustomMapStatSchema schema) =>
        schema.SchemaKey switch
        {
            "prophets" => ("Alchemists", "Warlocks"),
            "coop_survival" => ("Survivors", "Scourge"),
            _ => ("Legion", "Hellbourne")
        };

    private static string BuildBannerSummaryText(
        CustomMapStatSchema schema,
        string team1Label,
        string team2Label,
        int score1,
        int score2,
        int? waveReached,
        int? maxWaves,
        string? difficulty,
        int? legionLivesLeft,
        int? hellbourneLivesLeft,
        int? topRoundWins = null,
        string? topRoundWinner = null,
        int? roundsPlayed = null)
    {
        if (schema.SchemaKey == "enfos" && waveReached.HasValue)
        {
            string diff = difficulty ?? "Normal";
            return $"Wave {waveReached}/{maxWaves ?? 39} ({diff})  |  Team Lives Left: {legionLivesLeft ?? score1}";
        }

        if (schema.SchemaKey == "duel_lives" && (legionLivesLeft.HasValue || hellbourneLivesLeft.HasValue))
        {
            return $"{team1Label} Lives Left: {legionLivesLeft ?? score1}  |  {team2Label} Lives Left: {hellbourneLivesLeft ?? score2}";
        }

        if (schema.SchemaKey == "arena" && !string.IsNullOrWhiteSpace(topRoundWinner) && (roundsPlayed ?? 0) > 0)
        {
            return $"Top Gladiator: {topRoundWinner} ({topRoundWins ?? 0}/{roundsPlayed} Rounds)";
        }

        if (!string.IsNullOrWhiteSpace(schema.TeamScoreLabel))
        {
            return $"{team1Label} {score1} - {score2} {team2Label} ({schema.TeamScoreLabel})";
        }

        return $"{team1Label} {score1} - {score2} {team2Label}";
    }

    private static Dictionary<string, int> ParseIntDict(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}")
        {
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            Dictionary<string, int>? parsed = CustomMapStatSchemaCatalog.DeserializeHeroPlayCountsJson(json);
            return parsed is not null
                ? new Dictionary<string, int>(parsed, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException ex)
        {
            Trace.TraceWarning("[CustomMapStatsReader] Malformed HeroPlayCountsJson: {0}", ex.Message);
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static Dictionary<string, double> ParseDoubleDict(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}")
        {
            return new Dictionary<string, double>(StringComparer.Ordinal);
        }

        try
        {
            Dictionary<string, double>? parsed = CustomMapStatSchemaCatalog.DeserializePlayerCustomStatsJson(json);
            return parsed is not null
                ? new Dictionary<string, double>(parsed, StringComparer.Ordinal)
                : new Dictionary<string, double>(StringComparer.Ordinal);
        }
        catch (JsonException ex)
        {
            Trace.TraceWarning("[CustomMapStatsReader] Malformed PlayerCustomStatsJson: {0}", ex.Message);
            return new Dictionary<string, double>(StringComparer.Ordinal);
        }
    }
}
