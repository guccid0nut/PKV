// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using Dapper;
using MERRICK.DatabaseContext.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NEXUS.Contracts.CQRS.PlayerStats;

namespace ZORGATH.WebPortal.API.Services.Stats;

/// <summary>
///     Wave 3B: High-throughput read projection service backed by parameterized Dapper SQL
///     on relational databases (PostgreSQL / SQLite / SQL Server) with automatic EF Core fallback
///     for non-relational in-memory test contexts.
/// </summary>
public interface IMatchStatsReadProjectionService
{
    Task<IReadOnlyList<PlayerMatchHistoryProjectionDto>> GetPlayerMatchHistoryAsync(
        int accountId,
        int limit,
        CancellationToken ct = default);
}

public sealed class DapperMatchStatsReadProjectionService(
    MerrickContext db,
    ILogger<DapperMatchStatsReadProjectionService> logger)
    : IMatchStatsReadProjectionService
{
    private sealed class DapperMatchHistoryRow
    {
        public int MatchID { get; init; }
        public int AccountID { get; init; }
        public string AccountName { get; init; } = string.Empty;
        public string HeroIdentifier { get; init; } = string.Empty;
        public string Map { get; init; } = string.Empty;
        public string GameMode { get; init; } = string.Empty;
        public int TimePlayed { get; init; }
        public int Team { get; init; }
        public int Win { get; init; }
        public int HeroKills { get; init; }
        public int HeroDeaths { get; init; }
        public int HeroAssists { get; init; }
        public int HeroDamage { get; init; }
        public int Actions { get; init; }
        public int MVP { get; init; }
        public object? TimestampRecorded { get; init; }
    }

    public async Task<IReadOnlyList<PlayerMatchHistoryProjectionDto>> GetPlayerMatchHistoryAsync(
        int accountId,
        int limit,
        CancellationToken ct = default)
    {
        int safeLimit = Math.Clamp(limit, 1, 100);
        Stopwatch sw = Stopwatch.StartNew();

        if (db.Database.IsRelational())
        {
            string providerName = db.Database.ProviderName ?? string.Empty;
            string sql = BuildDialectSql(providerName);
            DbConnection conn = db.Database.GetDbConnection();
            DbTransaction? currentTransaction = db.Database.CurrentTransaction?.GetDbTransaction();
            int? commandTimeout = db.Database.GetCommandTimeout();
            bool shouldClose = currentTransaction is null && conn.State != ConnectionState.Open;
            if (shouldClose)
            {
                await conn.OpenAsync(ct).ConfigureAwait(false);
            }

            try
            {
                CommandDefinition cmd = new(
                    sql,
                    new { AccountId = accountId, Limit = safeLimit },
                    transaction: currentTransaction,
                    commandTimeout: commandTimeout,
                    cancellationToken: ct);

                IEnumerable<DapperMatchHistoryRow> rows = await conn
                    .QueryAsync<DapperMatchHistoryRow>(cmd)
                    .ConfigureAwait(false);

                List<PlayerMatchHistoryProjectionDto> projected = [];
                foreach (DapperMatchHistoryRow r in rows)
                {
                    projected.Add(new PlayerMatchHistoryProjectionDto(
                        MatchId: r.MatchID,
                        AccountId: r.AccountID,
                        AccountName: r.AccountName ?? string.Empty,
                        HeroIdentifier: r.HeroIdentifier ?? string.Empty,
                        Map: r.Map ?? string.Empty,
                        GameMode: r.GameMode ?? string.Empty,
                        TimePlayedSeconds: r.TimePlayed,
                        Team: r.Team,
                        Won: r.Win > 0,
                        Kills: r.HeroKills,
                        Deaths: r.HeroDeaths,
                        Assists: r.HeroAssists,
                        HeroDamage: r.HeroDamage,
                        Actions: r.Actions,
                        IsMvp: r.MVP > 0,
                        TimestampRecordedUtc: ParseTimestamp(r.TimestampRecorded, logger)));
                }

                sw.Stop();
                logger.LogInformation(
                    "[DapperStatsProjection] Provider={Provider} AccountId={AccountId} Limit={Limit} Rows={Rows} HasTransaction={HasTransaction} CommandTimeoutSec={CommandTimeoutSec} ElapsedMs={ElapsedMs}",
                    providerName, accountId, safeLimit, projected.Count, currentTransaction is not null, commandTimeout, sw.ElapsedMilliseconds);

                return projected;
            }
            finally
            {
                if (shouldClose && conn.State == ConnectionState.Open)
                {
                    await conn.CloseAsync().ConfigureAwait(false);
                }
            }
        }

        // Non-relational fallback (EF Core InMemory test provider)
        List<PlayerMatchHistoryProjectionDto> fallbackRows = await (
            from p in db.PlayerStatistics.AsNoTracking()
            join m in db.MatchStatistics.AsNoTracking() on p.MatchID equals m.MatchID
            where p.AccountID == accountId
            orderby m.TimestampRecorded descending, m.MatchID descending
            select new PlayerMatchHistoryProjectionDto(
                m.MatchID,
                p.AccountID,
                p.AccountName,
                p.HeroIdentifier,
                m.Map,
                m.GameMode,
                m.TimePlayed,
                p.Team,
                p.Win > 0,
                p.HeroKills,
                p.HeroDeaths,
                p.HeroAssists,
                p.HeroDamage,
                p.Actions,
                p.MVP > 0,
                m.TimestampRecorded))
            .Take(safeLimit)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        sw.Stop();
        logger.LogDebug(
            "[DapperStatsProjection] InMemoryFallback AccountId={AccountId} Limit={Limit} Rows={Rows} ElapsedMs={ElapsedMs}",
            accountId, safeLimit, fallbackRows.Count, sw.ElapsedMilliseconds);

        return fallbackRows;
    }

    public static string BuildDialectSql(string providerName)
    {
        if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            return """
                SELECT
                    m."MatchID",
                    p."AccountID",
                    p."AccountName",
                    p."HeroIdentifier",
                    m."Map",
                    m."GameMode",
                    m."TimePlayed",
                    p."Team",
                    p."Win",
                    p."HeroKills",
                    p."HeroDeaths",
                    p."HeroAssists",
                    p."HeroDamage",
                    p."Actions",
                    p."MVP",
                    m."TimestampRecorded"
                FROM "PlayerStatistics" AS p
                INNER JOIN "MatchStatistics" AS m ON p."MatchID" = m."MatchID"
                WHERE p."AccountID" = @AccountId
                ORDER BY m."TimestampRecorded" DESC, m."MatchID" DESC
                LIMIT @Limit;
                """;
        }

        if (providerName.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
        {
            return """
                SELECT TOP (@Limit)
                    m.[MatchID],
                    p.[AccountID],
                    p.[AccountName],
                    p.[HeroIdentifier],
                    m.[Map],
                    m.[GameMode],
                    m.[TimePlayed],
                    p.[Team],
                    p.[Win],
                    p.[HeroKills],
                    p.[HeroDeaths],
                    p.[HeroAssists],
                    p.[HeroDamage],
                    p.[Actions],
                    p.[MVP],
                    m.[TimestampRecorded]
                FROM [stat].[PlayerStatistics] AS p
                INNER JOIN [stat].[MatchStatistics] AS m ON p.[MatchID] = m.[MatchID]
                WHERE p.[AccountID] = @AccountId
                ORDER BY m.[TimestampRecorded] DESC, m.[MatchID] DESC;
                """;
        }

        // Default: PostgreSQL (Npgsql)
        return """
            SELECT
                m."MatchID",
                p."AccountID",
                p."AccountName",
                p."HeroIdentifier",
                m."Map",
                m."GameMode",
                m."TimePlayed",
                p."Team",
                p."Win",
                p."HeroKills",
                p."HeroDeaths",
                p."HeroAssists",
                p."HeroDamage",
                p."Actions",
                p."MVP",
                m."TimestampRecorded"
            FROM stat."PlayerStatistics" AS p
            INNER JOIN stat."MatchStatistics" AS m ON p."MatchID" = m."MatchID"
            WHERE p."AccountID" = @AccountId
            ORDER BY m."TimestampRecorded" DESC, m."MatchID" DESC
            LIMIT @Limit;
            """;
    }

    public static DateTimeOffset ParseTimestamp(object? raw, ILogger? logger = null)
    {
        switch (raw)
        {
            case DateTimeOffset dto:
                return dto.ToUniversalTime();
            case DateTime dt when dt.Kind == DateTimeKind.Local:
                return new DateTimeOffset(dt.ToUniversalTime(), TimeSpan.Zero);
            case DateTime dt when dt.Kind == DateTimeKind.Utc:
                return new DateTimeOffset(dt, TimeSpan.Zero);
            case DateTime dt:
                return new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc), TimeSpan.Zero);
            case string s when DateTimeOffset.TryParse(
                s,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset parsed):
                return parsed.ToUniversalTime();
            default:
                logger?.LogWarning(
                    "[DapperStatsProjection] Unparseable TimestampRecorded value of type '{ValueType}' (raw='{RawValue}'); falling back to DateTimeOffset.UnixEpoch.",
                    raw?.GetType().FullName ?? "null",
                    raw?.ToString() ?? "null");
                return DateTimeOffset.UnixEpoch;
        }
    }
}
