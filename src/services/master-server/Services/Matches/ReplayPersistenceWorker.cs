// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Globalization;
using System.Text.Json;
using ASPIRE.Common.Constants;
using ASPIRE.Common.Telemetry;
using KONGOR.MasterServer.Models.RequestResponse.ServerManagement;
using KONGOR.MasterServer.Serialization;
using KONGOR.MasterServer.Services.Replay;
using KONGOR.MasterServer.Services.Stats;
using MERRICK.DatabaseContext.Entities.Statistics;
using MERRICK.DatabaseContext.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NEXUS.Contracts.Domain;
using NEXUS.Contracts.Events;
using NEXUS.Contracts.Services;
using StackExchange.Redis;

namespace KONGOR.MasterServer.Services.Matches;

public interface IReplayPersistenceWorker
{
    Task PersistAsync(
        IngestedMatchContext context,
        CancellationToken cancellationToken = default);

    Task FinalizeSideEffectsAsync(
        IngestedMatchContext context,
        MatchServer? matchServer,
        CancellationToken cancellationToken = default);

    Task PersistAndFinalizeAsync(
        IngestedMatchContext context,
        MatchServer? matchServer,
        CancellationToken cancellationToken = default);
}

public sealed class ReplayPersistenceWorker(
    MerrickContext merrickContext,
    IPlayerStatisticsService statsService,
    IDatabase distributedCache,
    ILogger<ReplayPersistenceWorker> logger,
    IReplayAutoUploadService? replayAutoUploadService = null,
    IEventBus? eventBus = null) : IReplayPersistenceWorker
{
    public async Task PersistAsync(
        IngestedMatchContext context,
        CancellationToken cancellationToken = default)
    {
        // Mark authoritative MatchRecord as Completed
        MatchRecord? record = await merrickContext.MatchRecords
            .FirstOrDefaultAsync(m => m.MatchID == context.MatchID, cancellationToken);
        if (record != null)
        {
            record.State = MatchLifecycleState.Completed;
            record.EndedAt = TimeProvider.System.GetUtcNow();
            record.PlayerCount = context.ProcessedPlayerEntries.Count;
        }

        bool isNpgsql = merrickContext.Database.IsNpgsql();
        if (isNpgsql)
        {
            await merrickContext.SaveChangesAsync(cancellationToken);
            await UpsertAccountMapStatisticsNpgsqlAsync(context, cancellationToken);
        }
        else
        {
            await UpsertAccountMapStatisticsInMemoryAsync(context, cancellationToken);
            await merrickContext.SaveChangesAsync(cancellationToken);
        }
    }

    private static (Dictionary<string, double> Totals, Dictionary<string, double> Bests, Dictionary<string, int> HeroCounts) ParseExistingRollup(
        AccountMapStatistics? existing)
    {
        Dictionary<string, double> totals = existing is not null && !string.IsNullOrWhiteSpace(existing.CustomTotals)
            ? JsonSerializer.Deserialize<Dictionary<string, double>>(existing.CustomTotals) ?? new(StringComparer.Ordinal)
            : new(StringComparer.Ordinal);
        Dictionary<string, double> bests = existing is not null && !string.IsNullOrWhiteSpace(existing.BestRecords)
            ? JsonSerializer.Deserialize<Dictionary<string, double>>(existing.BestRecords) ?? new(StringComparer.Ordinal)
            : new(StringComparer.Ordinal);
        Dictionary<string, int> heroCounts = existing is not null && !string.IsNullOrWhiteSpace(existing.HeroPlayCountsJson)
            ? JsonSerializer.Deserialize<Dictionary<string, int>>(existing.HeroPlayCountsJson) ?? new(StringComparer.OrdinalIgnoreCase)
            : new(StringComparer.OrdinalIgnoreCase);

        return (totals, bests, heroCounts);
    }

    internal static (string TotalsJson, string BestsJson, string HeroCountsJson, string? FavoriteHero, bool SchemaUpgraded, int PreviousSchemaVersion) MergeRollupJson(
        AccountMapStatistics? existing,
        CustomMapStatSchema schema,
        PlayerStatistics playerStats,
        string rawHeroKey,
        bool updateBestRecords)
    {
        (Dictionary<string, double> totals, Dictionary<string, double> bests, Dictionary<string, int> heroCounts) =
            ParseExistingRollup(existing);

        int prevSchemaVersion = totals.TryGetValue("_schema_version", out double sv) ? (int)Math.Round(sv) : 1;
        bool schemaUpgraded = existing is not null && prevSchemaVersion < schema.SchemaVersion;
        if (schemaUpgraded)
        {
            HashSet<string> validKeys = schema.Fields.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
            foreach (string staleKey in totals.Keys.Where(k => !string.Equals(k, "_schema_version", StringComparison.Ordinal) && !validKeys.Contains(k)).ToList())
            {
                totals.Remove(staleKey);
            }
            foreach (string staleBestKey in bests.Keys.Where(k => !validKeys.Contains(k) && !string.Equals(k, "kda", StringComparison.Ordinal) && !string.Equals(k, "kills", StringComparison.Ordinal)).ToList())
            {
                bests.Remove(staleBestKey);
            }
            // rounds_played changed aggregation from Last -> Sum in schema v2 (arena / prophets / tdm).
            if (prevSchemaVersion < 2 && totals.ContainsKey("rounds_played"))
            {
                totals["rounds_played"] = 0.0;
            }
        }

        Dictionary<string, double> matchCustom = !string.IsNullOrWhiteSpace(playerStats.CustomStats)
            ? JsonSerializer.Deserialize<Dictionary<string, double>>(playerStats.CustomStats) ?? new(StringComparer.Ordinal)
            : CustomMapStatSchemaCatalog.BuildPlayerCustomStats(
                schema,
                playerStats.GameplayStat0, playerStats.GameplayStat1, playerStats.GameplayStat2,
                playerStats.GameplayStat3, playerStats.GameplayStat4, playerStats.GameplayStat5,
                playerStats.GameplayStat6, playerStats.GameplayStat7, playerStats.GameplayStat8,
                playerStats.GameplayStat9,
                playerStats.HeroKills, playerStats.HeroDeaths,
                playerStats.TeamCreepKills, playerStats.NeutralCreepKills,
                playerStats.Denies, playerStats.HeroDamage, playerStats.BuildingDamage,
                playerStats.Gold, playerStats.Experience, playerStats.WardsPlaced,
                playerStats.SecondsPlayed);

        foreach (CustomStatFieldSpec field in schema.Fields)
        {
            if (field.Aggregation == CustomStatAggregation.RatioPercent) continue;
            if (!matchCustom.TryGetValue(field.Key, out double val) || !double.IsFinite(val)) continue;

            if (field.Aggregation == CustomStatAggregation.Sum)
            {
                totals[field.Key] = Math.Round(totals.GetValueOrDefault(field.Key) + val, 2);
            }
            else
            {
                totals[field.Key] = Math.Round(Math.Max(totals.GetValueOrDefault(field.Key), val), 2);
            }

            if (updateBestRecords)
            {
                if (!bests.TryGetValue(field.Key, out double curBest) ||
                    (field.HigherIsBetter ? val > curBest : val < curBest))
                {
                    bests[field.Key] = Math.Round(val, 2);
                }
            }
        }

        // Recompute lifetime ratio metrics from accumulated numerator/denominator totals.
        foreach (CustomStatFieldSpec field in schema.Fields)
        {
            if (field.Aggregation == CustomStatAggregation.RatioPercent &&
                !string.IsNullOrEmpty(field.NumeratorKey) &&
                !string.IsNullOrEmpty(field.DenominatorKey))
            {
                double num = totals.GetValueOrDefault(field.NumeratorKey);
                double den = totals.GetValueOrDefault(field.DenominatorKey);
                totals[field.Key] = den > 0.0 ? Math.Round(num * 100.0 / den, 1) : 0.0;

                if (updateBestRecords && matchCustom.TryGetValue(field.Key, out double matchRatio))
                {
                    double matchDen = matchCustom.GetValueOrDefault(field.DenominatorKey);
                    if (matchDen >= 5.0 && (!bests.TryGetValue(field.Key, out double curBestRatio) || matchRatio > curBestRatio))
                    {
                        bests[field.Key] = Math.Round(matchRatio, 1);
                    }
                }
            }
        }

        totals["_schema_version"] = schema.SchemaVersion;

        string canonicalHero = !string.IsNullOrWhiteSpace(rawHeroKey) ? rawHeroKey : playerStats.HeroIdentifier;
        if (!string.IsNullOrWhiteSpace(canonicalHero))
        {
            heroCounts[canonicalHero] = heroCounts.GetValueOrDefault(canonicalHero) + 1;
        }

        string? favoriteHero = heroCounts.Count > 0
            ? heroCounts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).First().Key
            : null;

        return (
            JsonSerializer.Serialize(totals),
            JsonSerializer.Serialize(bests),
            JsonSerializer.Serialize(heroCounts),
            favoriteHero,
            schemaUpgraded,
            prevSchemaVersion);
    }

    private async Task UpsertAccountMapStatisticsInMemoryAsync(IngestedMatchContext context, CancellationToken cancellationToken)
    {
        if (context.ProcessedPlayerEntries.Count == 0 || context.MatchStatistics is null) return;

        string mapSlug = MapCatalog.NormalizeSlug(context.MatchStatistics.Map);
        CustomMapStatSchema schema = CustomMapStatSchemaCatalog.ResolveForMap(mapSlug, context.ProcessedPlayerEntries.Count);
        bool updateBest = !context.IsUnrankedListenServer;
        DateTimeOffset now = TimeProvider.System.GetUtcNow();

        List<int> accountIds = [.. context.ProcessedPlayerEntries.Select(e => e.Account.ID).Distinct()];
        Dictionary<int, AccountMapStatistics> existingRows = await merrickContext.AccountMapStatistics
            .Where(s => accountIds.Contains(s.AccountID) && s.MapSlug == mapSlug)
            .ToDictionaryAsync(s => s.AccountID, cancellationToken);

        foreach (PlayerIngestionEntry entry in context.ProcessedPlayerEntries)
        {
            if (entry.Account.Type == AccountType.Guest || entry.Account.ID <= 0) continue;

            existingRows.TryGetValue(entry.Account.ID, out AccountMapStatistics? row);
            (string totalsJson, string bestsJson, string heroCountsJson, string? favoriteHero, bool schemaUpgraded, int prevSchemaVersion) =
                MergeRollupJson(row, schema, entry.PlayerStats, entry.HeroKey, updateBest);
            if (schemaUpgraded)
            {
                logger.LogInformation(
                    "[ReplayPersistenceWorker] Upgraded AccountMapStatistics rollup (InMemory) for AccountId={AccountId} Map='{MapSlug}' Schema='{SchemaKey}' v{PrevVersion} -> v{NewVersion}",
                    entry.Account.ID, mapSlug, schema.SchemaKey, prevSchemaVersion, schema.SchemaVersion);
            }

            if (row is null)
            {
                row = new AccountMapStatistics
                {
                    AccountID = entry.Account.ID,
                    MapSlug = mapSlug,
                    SchemaKey = schema.SchemaKey,
                    MatchesPlayed = 1,
                    MatchesWon = entry.IsWin ? 1 : 0,
                    MatchesLost = entry.IsWin ? 0 : 1,
                    MatchesConceded = entry.PlayerStats.Conceded > 0 ? 1 : 0,
                    Disconnects = entry.PlayerStats.Disconnected > 0 ? 1 : 0,
                    TimePlayedSeconds = Math.Max(0, entry.PlayerStats.SecondsPlayed),
                    HeroKills = Math.Max(0, entry.PlayerStats.HeroKills),
                    Deaths = Math.Max(0, entry.PlayerStats.HeroDeaths),
                    HeroAssists = Math.Max(0, entry.PlayerStats.HeroAssists),
                    FavoriteHeroIdentifier = favoriteHero,
                    HeroPlayCountsJson = heroCountsJson,
                    CustomTotals = totalsJson,
                    BestRecords = bestsJson,
                    LastMatchID = context.MatchID,
                    LastPlayedAtUtc = now
                };
                await merrickContext.AccountMapStatistics.AddAsync(row, cancellationToken);
                existingRows[entry.Account.ID] = row;
            }
            else
            {
                row.SchemaKey = schema.SchemaKey;
                row.MatchesPlayed++;
                if (entry.IsWin) row.MatchesWon++;
                else row.MatchesLost++;
                if (entry.PlayerStats.Conceded > 0) row.MatchesConceded++;
                if (entry.PlayerStats.Disconnected > 0) row.Disconnects++;
                row.TimePlayedSeconds += Math.Max(0, entry.PlayerStats.SecondsPlayed);
                row.HeroKills += Math.Max(0, entry.PlayerStats.HeroKills);
                row.Deaths += Math.Max(0, entry.PlayerStats.HeroDeaths);
                row.HeroAssists += Math.Max(0, entry.PlayerStats.HeroAssists);
                row.FavoriteHeroIdentifier = favoriteHero;
                row.HeroPlayCountsJson = heroCountsJson;
                row.CustomTotals = totalsJson;
                row.BestRecords = bestsJson;
                row.LastMatchID = context.MatchID;
                row.LastPlayedAtUtc = now;
            }
        }
    }

    private async Task UpsertAccountMapStatisticsNpgsqlAsync(IngestedMatchContext context, CancellationToken cancellationToken)
    {
        if (context.ProcessedPlayerEntries.Count == 0 || context.MatchStatistics is null) return;

        string mapSlug = MapCatalog.NormalizeSlug(context.MatchStatistics.Map);
        CustomMapStatSchema schema = CustomMapStatSchemaCatalog.ResolveForMap(mapSlug, context.ProcessedPlayerEntries.Count);
        bool updateBest = !context.IsUnrankedListenServer;
        DateTimeOffset now = TimeProvider.System.GetUtcNow();

        // Read current rows inside active ReadCommitted transaction for JSON merge initialization,
        // while using ON CONFLICT DO UPDATE for atomic conflict-free counters and idempotent inserts.
        List<int> accountIds = [.. context.ProcessedPlayerEntries.Select(e => e.Account.ID).Distinct()];
        Dictionary<int, AccountMapStatistics> existingRows = await merrickContext.AccountMapStatistics
            .AsNoTracking()
            .Where(s => accountIds.Contains(s.AccountID) && s.MapSlug == mapSlug)
            .ToDictionaryAsync(s => s.AccountID, cancellationToken);

        foreach (PlayerIngestionEntry entry in context.ProcessedPlayerEntries)
        {
            if (entry.Account.Type == AccountType.Guest || entry.Account.ID <= 0) continue;

            existingRows.TryGetValue(entry.Account.ID, out AccountMapStatistics? existing);
            (string totalsJson, string bestsJson, string heroCountsJson, string? favoriteHero, bool schemaUpgraded, int prevSchemaVersion) =
                MergeRollupJson(existing, schema, entry.PlayerStats, entry.HeroKey, updateBest);
            if (schemaUpgraded)
            {
                logger.LogInformation(
                    "[ReplayPersistenceWorker] Upgraded AccountMapStatistics rollup (Npgsql) for AccountId={AccountId} Map='{MapSlug}' Schema='{SchemaKey}' v{PrevVersion} -> v{NewVersion}",
                    entry.Account.ID, mapSlug, schema.SchemaKey, prevSchemaVersion, schema.SchemaVersion);
            }

            int winDelta = entry.IsWin ? 1 : 0;
            int lossDelta = entry.IsWin ? 0 : 1;
            int concedeDelta = entry.PlayerStats.Conceded > 0 ? 1 : 0;
            int discoDelta = entry.PlayerStats.Disconnected > 0 ? 1 : 0;
            long secDelta = Math.Max(0, entry.PlayerStats.SecondsPlayed);
            int killDelta = Math.Max(0, entry.PlayerStats.HeroKills);
            int deathDelta = Math.Max(0, entry.PlayerStats.HeroDeaths);
            int assistDelta = Math.Max(0, entry.PlayerStats.HeroAssists);

            await merrickContext.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO data.""AccountMapStatistics""
                    (""AccountID"", ""MapSlug"", ""SchemaKey"", ""MatchesPlayed"", ""MatchesWon"", ""MatchesLost"",
                     ""MatchesConceded"", ""Disconnects"", ""TimePlayedSeconds"", ""HeroKills"", ""Deaths"", ""HeroAssists"",
                     ""FavoriteHeroIdentifier"", ""HeroPlayCountsJson"", ""CustomTotals"", ""BestRecords"", ""LastMatchID"", ""LastPlayedAtUtc"")
                VALUES
                    ({entry.Account.ID}, {mapSlug}, {schema.SchemaKey}, 1, {winDelta}, {lossDelta},
                     {concedeDelta}, {discoDelta}, {secDelta}, {killDelta}, {deathDelta}, {assistDelta},
                     {favoriteHero}, CAST({heroCountsJson} AS jsonb), CAST({totalsJson} AS jsonb), CAST({bestsJson} AS jsonb), {context.MatchID}, {now})
                ON CONFLICT (""AccountID"", ""MapSlug"") DO UPDATE SET
                    ""SchemaKey"" = EXCLUDED.""SchemaKey"",
                    ""MatchesPlayed"" = data.""AccountMapStatistics"".""MatchesPlayed"" + 1,
                    ""MatchesWon"" = data.""AccountMapStatistics"".""MatchesWon"" + {winDelta},
                    ""MatchesLost"" = data.""AccountMapStatistics"".""MatchesLost"" + {lossDelta},
                    ""MatchesConceded"" = data.""AccountMapStatistics"".""MatchesConceded"" + {concedeDelta},
                    ""Disconnects"" = data.""AccountMapStatistics"".""Disconnects"" + {discoDelta},
                    ""TimePlayedSeconds"" = data.""AccountMapStatistics"".""TimePlayedSeconds"" + {secDelta},
                    ""HeroKills"" = data.""AccountMapStatistics"".""HeroKills"" + {killDelta},
                    ""Deaths"" = data.""AccountMapStatistics"".""Deaths"" + {deathDelta},
                    ""HeroAssists"" = data.""AccountMapStatistics"".""HeroAssists"" + {assistDelta},
                    ""FavoriteHeroIdentifier"" = EXCLUDED.""FavoriteHeroIdentifier"",
                    ""HeroPlayCountsJson"" = EXCLUDED.""HeroPlayCountsJson"",
                    ""CustomTotals"" = EXCLUDED.""CustomTotals"",
                    ""BestRecords"" = EXCLUDED.""BestRecords"",
                    ""LastMatchID"" = {context.MatchID},
                    ""LastPlayedAtUtc"" = {now}
                WHERE COALESCE(data.""AccountMapStatistics"".""LastMatchID"", 0) <> {context.MatchID};",
                cancellationToken);
        }
    }

    public async Task FinalizeSideEffectsAsync(
        IngestedMatchContext context,
        MatchServer? matchServer,
        CancellationToken cancellationToken = default)
    {
        int matchID = context.MatchID;

        // Invalidate cached statistics for all involved players
        foreach (var acc in context.AccountMap.Values)
            await statsService.InvalidateAggregatedStatisticsAsync(acc.ID, acc.Name, cancellationToken);

        // Cleanup Match Start Data from Redis
        await distributedCache.RemoveMatchStartData(matchID);

        // Publish Match Concluded Redis event for real-time live map & UI sync
        MatchConcludedPayload concludedPayload = new(matchID, TimeProvider.System.GetUtcNow(), context.AccountMap.Count);
        string matchConcludedPayload = JsonSerializer.Serialize(concludedPayload, MasterServerJsonContext.Default.MatchConcludedPayload);

        await distributedCache.PublishAsync(
            RedisChannel.Literal("match:concluded"),
            matchConcludedPayload,
            CommandFlags.FireAndForget);

        // Publish MatchCompletedEvent domain event
        if (eventBus != null)
        {
            var matchStats = context.MatchStatistics;
            int? winner = matchStats != null
                ? matchStats.ScoreTeam1 > matchStats.ScoreTeam2 ? 1 :
                matchStats.ScoreTeam2 > matchStats.ScoreTeam1 ? 2 : null
                : null;

            MatchCompletedEvent domainEvent = new(
                Guid.NewGuid(),
                TimeProvider.System.GetUtcNow(),
                matchID,
                [.. context.AccountMap.Values.Select(a => a.ID).Distinct()],
                winner,
                TimeSpan.FromSeconds(context.DurationSeconds),
                matchStats?.Map ?? "caldavar",
                matchServer?.ID,
                matchServer?.IPAddress
            );

            await eventBus.PublishAsync(domainEvent, cancellationToken);
        }

        // Automatically queue replay search, compression, and cloud upload (skip 0-byte aborted / unrecorded matches)
        if (replayAutoUploadService != null)
        {
            int reportedFileSize = context.MatchStatistics?.FileSize ?? 0;
            string expectedReplayName = $"M{matchID}.honreplay";
            if (reportedFileSize <= 0 && !MatchSummary.TryFindReplayFile(matchID, expectedReplayName, out _, out _))
            {
                if (logger.IsEnabled(LogLevel.Information))
                    logger.LogInformation(
                        "[ReplayPersistenceWorker] Skipping cloud replay upload for Match #{MatchId} (reported file_size={FileSize}, no {FileName} generated on disk — aborted or unrecorded match).",
                        matchID,
                        reportedFileSize,
                        expectedReplayName);
            }
            else
            {
                await replayAutoUploadService.QueueReplayUploadAsync(matchID, cancellationToken: cancellationToken);
            }
        }

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "[ReplayPersistenceWorker] Successfully finalized side-effects for Match ID: {MatchId}",
                matchID);
    }

    public async Task PersistAndFinalizeAsync(
        IngestedMatchContext context,
        MatchServer? matchServer,
        CancellationToken cancellationToken = default)
    {
        await PersistAsync(context, cancellationToken);
        await FinalizeSideEffectsAsync(context, matchServer, cancellationToken);
    }
}
