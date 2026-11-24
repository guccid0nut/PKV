// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// TAO (Together As One) MCP & Kongor CLI -- Created by guccidonut (https://github.com/guccidonut/PKV)

using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using DuckDB.NET.Data;
using Kongor.Core.Analytics;

namespace Kongor.Cli.Mcp.Services;

/// <summary>
/// Result of an arbitrary analytical SQL query executed by the DuckDB Lakehouse engine.
/// </summary>
public sealed class MatchAnalyticsQueryResult
{
    public bool Success { get; init; }
    public string? Query { get; init; }
    public IReadOnlyList<string> Columns { get; init; } = [];
    public IReadOnlyList<IReadOnlyList<object?>> Rows { get; init; } = [];
    public int RowCount { get; init; }
    public double ExecutionDurationMs { get; init; }
    public string? ParquetSource { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Aggregated combat metrics for an account or player slot across lakehouse match archives.
/// </summary>
public sealed class PlayerCombatSummaryResult
{
    public int AccountId { get; init; }
    public byte PlayerIndex { get; init; }
    public short HeroId { get; init; }
    public int Kills { get; init; }
    public int Assists { get; init; }
    public int Deaths { get; init; }
    public float TotalDamage { get; init; }
    public int WardsPlaced { get; init; }
    public int TotalMatches { get; init; }
    public double KillDeathRatio => Deaths == 0 ? Kills : Math.Round((double)Kills / Deaths, 2);
}

/// <summary>
/// Combat breakdown for a single player within a match.
/// </summary>
public sealed class MatchCombatPlayerBreakdownResult
{
    public byte PlayerIndex { get; init; }
    public short HeroId { get; init; }
    public int Kills { get; init; }
    public int Assists { get; init; }
    public int Deaths { get; init; }
    public float TotalDamage { get; init; }
    public int WardsPlaced { get; init; }
    public int TowersKilled { get; init; }
    public double KillDeathRatio => Deaths == 0 ? Kills : Math.Round((double)Kills / Deaths, 2);
}

/// <summary>
/// Spatial 2D point representing combat activity coordinates and event frequency for minimap canvas rendering.
/// </summary>
public sealed class SpatialHeatmapPoint
{
    public byte EventType { get; init; }
    public string EventTypeName { get; init; } = string.Empty;
    public float PositionX { get; init; }
    public float PositionY { get; init; }
    public int Frequency { get; init; }
}

/// <summary>
/// Head-to-head combat matchup statistics between two heroes.
/// </summary>
public sealed class HeroMatchupResult
{
    public short HeroId { get; init; }
    public short OpponentHeroId { get; init; }
    public int KillsAgainst { get; init; }
    public int DeathsTo { get; init; }
    public float DamageDealt { get; init; }
    public int TotalMatches { get; init; }
    public double KillDeathRatio => DeathsTo == 0 ? KillsAgainst : Math.Round((double)KillsAgainst / DeathsTo, 2);
    public double WinRatePercent { get; init; }
}

/// <summary>
/// Metadata for an on-disk Apache Parquet match telemetry file.
/// </summary>
public sealed class ParquetFileInfo
{
    public string FileName { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public long FileSizeBytes { get; init; }
    public string FormattedSize { get; init; } = string.Empty;
    public DateTimeOffset LastModified { get; init; }
    public long? MatchId { get; init; }
}

/// <summary>
/// High-performance in-process DuckDB telemetry lakehouse query service.
/// Queries compressed Apache Parquet match logs in microseconds directly from TAO MCP.
/// </summary>
public sealed class DuckDbMatchAnalyticsService : IDisposable
{
    private static readonly Regex AllowedLeadingStatementPattern = new(
        @"^\s*(SELECT|WITH|EXPLAIN)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ForbiddenSqlPattern = new(
        @"\b(DROP|DELETE|UPDATE|INSERT|ALTER|TRUNCATE|ATTACH|DETACH|COPY|EXPORT|IMPORT|INSTALL|LOAD|PRAGMA|SET|RESET|CALL|CREATE|REPLACE|EXEC|EXECUTE)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ForbiddenExternalFunctionPattern = new(
        @"\b(read_parquet|read_csv|read_csv_auto|read_json|read_json_auto|read_json_objects|read_text|read_blob|parquet_scan|parquet_metadata|parquet_schema|parquet_kv_metadata|csv_scan|sqlite_scan|glob|getenv)\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SqlCommentStripPattern = new(
        @"(--[^\r\n]*)|(/\*[\s\S]*?\*/)",
        RegexOptions.Compiled);

    private readonly Action<string>? _logger;
    private DuckDBConnection? _connection;
    private string? _loadedParquetPath;
    private DateTime _loadedParquetWriteUtc;
    private readonly object _syncLock = new();
    private bool _disposed;

    public DuckDbMatchAnalyticsService(Action<string>? logger = null)
    {
        _logger = logger ?? (msg => Console.Error.WriteLine($"[DuckDbAnalytics] {msg}"));
    }

    private DuckDBConnection GetConnectionLocked()
    {
        if (_connection is null)
        {
            _connection = new DuckDBConnection("Data Source=:memory:");
            _connection.Open();
            LockConnectionExternalAccessForever(_connection);
            _logger?.Invoke("In-process DuckDB engine initialized (:memory:, enable_external_access permanently locked).");
        }

        return _connection;
    }

    /// <summary>
    /// Permanently locks external filesystem/network access on <paramref name="connection" />.
    /// Note: In DuckDB, <c>SET enable_external_access = false</c> is an irreversible one-way latch per DB instance.
    /// </summary>
    private void LockConnectionExternalAccessForever(DuckDBConnection connection)
    {
        string[] statements =
        [
            "SET autoinstall_known_extensions = false;",
            "SET autoload_known_extensions = false;",
            "SET enable_external_access = false;"
        ];

        foreach (string stmt in statements)
        {
            try
            {
                using DuckDBCommand cmd = connection.CreateCommand();
                cmd.CommandText = stmt;
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                if (stmt.Contains("enable_external_access", StringComparison.Ordinal))
                {
                    throw;
                }

                _logger?.Invoke($"Optional DuckDB sandbox flag skipped ({stmt}): {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Executes an arbitrary analytical SQL query over the telemetry lakehouse or a specific Parquet file.
    /// </summary>
    public async Task<MatchAnalyticsQueryResult> ExecuteQueryAsync(
        string sql,
        string? parquetPath = null,
        int maxRows = 1000,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return new MatchAnalyticsQueryResult
            {
                Success = false,
                Error = "Query string cannot be empty."
            };
        }

        string strippedSql = SqlCommentStripPattern.Replace(sql, " ").Trim();
        // Reject multi-statement batches (semicolons anywhere other than optional trailing semicolon)
        string withoutTrailingSemicolon = strippedSql.TrimEnd().TrimEnd(';').TrimEnd();

        // 1. Security Guardrail: Reject non-analytical/mutating SQL, multi-statement chains, or direct file-reader functions
        if (!AllowedLeadingStatementPattern.IsMatch(strippedSql) ||
            withoutTrailingSemicolon.Contains(';') ||
            ForbiddenSqlPattern.IsMatch(strippedSql) ||
            ForbiddenExternalFunctionPattern.IsMatch(strippedSql))
        {
            _logger?.Invoke($"[SECURITY-ALERT] Rejected forbidden DuckDB SQL query: {sql}");
            return new MatchAnalyticsQueryResult
            {
                Success = false,
                Query = sql,
                Error = "Security Violation: Only single-statement read-only analytical queries (SELECT, WITH, EXPLAIN) over registered match_combat_events tables are permitted. External file readers, PRAGMA/SET/INSTALL/LOAD, and DDL/DML statements are blocked."
            };
        }

        Stopwatch sw = Stopwatch.StartNew();

        try
        {
            string resolvedPath = string.Empty;
            if (!string.IsNullOrWhiteSpace(parquetPath))
            {
                resolvedPath = ResolveParquetPath(parquetPath);
                await EnsureViewRegisteredAsync(resolvedPath, cancellationToken).ConfigureAwait(false);
            }

            lock (_syncLock)
            {
                using DuckDBCommand cmd = GetConnectionLocked().CreateCommand();
                cmd.CommandText = sql;

                using DuckDBDataReader reader = (DuckDBDataReader)cmd.ExecuteReader();

                List<string> columns = [];
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    columns.Add(reader.GetName(i));
                }

                List<IReadOnlyList<object?>> rows = [];
                int count = 0;

                while (reader.Read() && count < maxRows)
                {
                    object?[] row = new object?[reader.FieldCount];
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        object val = reader.GetValue(i);
                        row[i] = val is DBNull ? null : NormalizeValue(val);
                    }
                    rows.Add(row);
                    count++;
                }

                sw.Stop();
                return new MatchAnalyticsQueryResult
                {
                    Success = true,
                    Query = sql,
                    Columns = columns,
                    Rows = rows,
                    RowCount = rows.Count,
                    ExecutionDurationMs = sw.Elapsed.TotalMilliseconds,
                    ParquetSource = string.IsNullOrEmpty(resolvedPath) ? null : resolvedPath
                };
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger?.Invoke($"Query execution failed: {ex.Message}");
            return new MatchAnalyticsQueryResult
            {
                Success = false,
                Query = sql,
                ExecutionDurationMs = sw.Elapsed.TotalMilliseconds,
                Error = ex.Message
            };
        }
    }

    /// <summary>
    /// Retrieves aggregated combat summary for a player/account across matches.
    /// </summary>
    public async Task<PlayerCombatSummaryResult?> GetPlayerSummaryAsync(
        int accountId,
        long? matchId = null,
        byte? playerIndex = null,
        short? heroId = null,
        string? parquetPath = null,
        CancellationToken cancellationToken = default)
    {
        string path = ResolveTargetParquet(parquetPath, matchId);
        await EnsureViewRegisteredAsync(path, cancellationToken).ConfigureAwait(false);

        List<string> predicates = [];
        if (matchId.HasValue)
        {
            predicates.Add($"match_id = {matchId.Value}");
        }
        if (playerIndex.HasValue)
        {
            predicates.Add($"source_player_index = {playerIndex.Value}");
        }
        if (heroId.HasValue)
        {
            predicates.Add($"source_hero_id = {heroId.Value}");
        }

        string whereClause = predicates.Count > 0
            ? "WHERE " + string.Join(" AND ", predicates)
            : string.Empty;

        // In HoN combat events:
        // EventType 0 = HeroKill, 1 = HeroAssist, 2 = HeroDeath, 6 = WardPlaced
        string sql = $"""
            SELECT
                COALESCE(source_player_index, 0) AS player_index,
                COALESCE(source_hero_id, 0) AS hero_id,
                COUNT(CASE WHEN event_type = 0 THEN 1 END) AS kills,
                COUNT(CASE WHEN event_type = 1 THEN 1 END) AS assists,
                COUNT(CASE WHEN event_type = 2 THEN 1 END) AS deaths,
                COALESCE(SUM(CASE WHEN event_type IN (0, 3) THEN event_value ELSE 0 END), 0.0) AS total_damage,
                COUNT(CASE WHEN event_type = 6 THEN 1 END) AS wards_placed,
                COUNT(DISTINCT match_id) AS total_matches
            FROM match_combat_events
            {whereClause}
            GROUP BY player_index, hero_id
            ORDER BY kills DESC, total_damage DESC
            LIMIT 1;
            """;

        MatchAnalyticsQueryResult result = await ExecuteQueryAsync(sql, path, maxRows: 1, cancellationToken).ConfigureAwait(false);
        if (!result.Success || result.Rows.Count == 0)
        {
            return null;
        }

        IReadOnlyList<object?> row = result.Rows[0];
        return new PlayerCombatSummaryResult
        {
            AccountId = accountId,
            PlayerIndex = Convert.ToByte(row[0], CultureInfo.InvariantCulture),
            HeroId = Convert.ToInt16(row[1], CultureInfo.InvariantCulture),
            Kills = Convert.ToInt32(row[2], CultureInfo.InvariantCulture),
            Assists = Convert.ToInt32(row[3], CultureInfo.InvariantCulture),
            Deaths = Convert.ToInt32(row[4], CultureInfo.InvariantCulture),
            TotalDamage = Convert.ToSingle(row[5], CultureInfo.InvariantCulture),
            WardsPlaced = Convert.ToInt32(row[6], CultureInfo.InvariantCulture),
            TotalMatches = Math.Max(1, Convert.ToInt32(row[7], CultureInfo.InvariantCulture))
        };
    }

    /// <summary>
    /// Retrieves the 10-player combat breakdown for a match.
    /// </summary>
    public async Task<IReadOnlyList<MatchCombatPlayerBreakdownResult>> GetMatchBreakdownAsync(
        long matchId,
        string? parquetPath = null,
        CancellationToken cancellationToken = default)
    {
        string path = ResolveTargetParquet(parquetPath, matchId);
        await EnsureViewRegisteredAsync(path, cancellationToken).ConfigureAwait(false);

        string sql = $"""
            SELECT
                source_player_index AS player_index,
                source_hero_id AS hero_id,
                COUNT(CASE WHEN event_type = 0 THEN 1 END) AS kills,
                COUNT(CASE WHEN event_type = 1 THEN 1 END) AS assists,
                COUNT(CASE WHEN event_type = 2 THEN 1 END) AS deaths,
                COALESCE(SUM(CASE WHEN event_type IN (0, 3) THEN event_value ELSE 0 END), 0.0) AS total_damage,
                COUNT(CASE WHEN event_type = 6 THEN 1 END) AS wards_placed,
                COUNT(CASE WHEN event_type = 7 THEN 1 END) AS towers_killed
            FROM match_combat_events
            WHERE match_id = {matchId}
            GROUP BY source_player_index, source_hero_id
            ORDER BY source_player_index ASC;
            """;

        MatchAnalyticsQueryResult result = await ExecuteQueryAsync(sql, path, maxRows: 12, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return [];
        }

        List<MatchCombatPlayerBreakdownResult> players = new(result.Rows.Count);
        foreach (IReadOnlyList<object?> row in result.Rows)
        {
            players.Add(new MatchCombatPlayerBreakdownResult
            {
                PlayerIndex = Convert.ToByte(row[0], CultureInfo.InvariantCulture),
                HeroId = Convert.ToInt16(row[1], CultureInfo.InvariantCulture),
                Kills = Convert.ToInt32(row[2], CultureInfo.InvariantCulture),
                Assists = Convert.ToInt32(row[3], CultureInfo.InvariantCulture),
                Deaths = Convert.ToInt32(row[4], CultureInfo.InvariantCulture),
                TotalDamage = Convert.ToSingle(row[5], CultureInfo.InvariantCulture),
                WardsPlaced = Convert.ToInt32(row[6], CultureInfo.InvariantCulture),
                TowersKilled = Convert.ToInt32(row[7], CultureInfo.InvariantCulture)
            });
        }

        return players;
    }

    /// <summary>
    /// Retrieves spatial coordinate clusters for kills, deaths, or wards for 2D minimap canvas visualization.
    /// </summary>
    public async Task<IReadOnlyList<SpatialHeatmapPoint>> GetSpatialHeatmapAsync(
        long matchId,
        CombatEventType? eventType = null,
        string? parquetPath = null,
        int bucketSize = 100,
        CancellationToken cancellationToken = default)
    {
        string path = ResolveTargetParquet(parquetPath, matchId);
        await EnsureViewRegisteredAsync(path, cancellationToken).ConfigureAwait(false);

        string eventFilter = eventType.HasValue
            ? $"AND event_type = {(byte)eventType.Value}"
            : "AND event_type IN (0, 2, 6)"; // Kills, deaths, wards by default

        // Bucket continuous coordinates into minimap grid cells
        string sql = $"""
            SELECT
                event_type,
                ROUND(position_x / {bucketSize}) * {bucketSize} AS bucket_x,
                ROUND(position_y / {bucketSize}) * {bucketSize} AS bucket_y,
                COUNT(*) AS frequency
            FROM match_combat_events
            WHERE match_id = {matchId} {eventFilter}
            GROUP BY event_type, bucket_x, bucket_y
            ORDER BY frequency DESC
            LIMIT 500;
            """;

        MatchAnalyticsQueryResult result = await ExecuteQueryAsync(sql, path, maxRows: 500, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return [];
        }

        List<SpatialHeatmapPoint> points = new(result.Rows.Count);
        foreach (IReadOnlyList<object?> row in result.Rows)
        {
            byte typeVal = Convert.ToByte(row[0], CultureInfo.InvariantCulture);
            points.Add(new SpatialHeatmapPoint
            {
                EventType = typeVal,
                EventTypeName = ((CombatEventType)typeVal).ToString(),
                PositionX = Convert.ToSingle(row[1], CultureInfo.InvariantCulture),
                PositionY = Convert.ToSingle(row[2], CultureInfo.InvariantCulture),
                Frequency = Convert.ToInt32(row[3], CultureInfo.InvariantCulture)
            });
        }

        return points;
    }

    /// <summary>
    /// Retrieves head-to-head combat and matchup statistics between a hero and opponent heroes.
    /// </summary>
    public async Task<IReadOnlyList<HeroMatchupResult>> GetHeroMatchupMatrixAsync(
        short heroId,
        short? opponentHeroId = null,
        string? parquetPath = null,
        CancellationToken cancellationToken = default)
    {
        string path = ResolveTargetParquet(parquetPath, null);
        await EnsureViewRegisteredAsync(path, cancellationToken).ConfigureAwait(false);

        string opponentFilter = opponentHeroId.HasValue
            ? $"AND target_hero_id = {opponentHeroId.Value}"
            : "AND target_hero_id != source_hero_id AND target_hero_id > 0";

        string sql = $"""
            SELECT
                source_hero_id AS hero_id,
                target_hero_id AS opponent_hero_id,
                COUNT(CASE WHEN event_type = 0 THEN 1 END) AS kills_against,
                COUNT(CASE WHEN event_type = 2 THEN 1 END) AS deaths_to,
                COALESCE(SUM(CASE WHEN event_type IN (0, 3) THEN event_value ELSE 0 END), 0.0) AS damage_dealt,
                COUNT(DISTINCT match_id) AS total_matches
            FROM match_combat_events
            WHERE source_hero_id = {heroId} {opponentFilter}
            GROUP BY source_hero_id, target_hero_id
            ORDER BY total_matches DESC, kills_against DESC
            LIMIT 50;
            """;

        MatchAnalyticsQueryResult result = await ExecuteQueryAsync(sql, path, maxRows: 50, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return [];
        }

        List<HeroMatchupResult> matchups = new(result.Rows.Count);
        foreach (IReadOnlyList<object?> row in result.Rows)
        {
            int kills = Convert.ToInt32(row[2], CultureInfo.InvariantCulture);
            int deaths = Convert.ToInt32(row[3], CultureInfo.InvariantCulture);
            int total = kills + deaths;
            double winRate = total > 0 ? Math.Round((double)kills / total * 100.0, 1) : 50.0;

            matchups.Add(new HeroMatchupResult
            {
                HeroId = Convert.ToInt16(row[0], CultureInfo.InvariantCulture),
                OpponentHeroId = Convert.ToInt16(row[1], CultureInfo.InvariantCulture),
                KillsAgainst = kills,
                DeathsTo = deaths,
                DamageDealt = Convert.ToSingle(row[4], CultureInfo.InvariantCulture),
                TotalMatches = Convert.ToInt32(row[5], CultureInfo.InvariantCulture),
                WinRatePercent = winRate
            });
        }

        return matchups;
    }

    /// <summary>
    /// Scans and returns metadata for all available Apache Parquet match telemetry files.
    /// </summary>
    public IReadOnlyList<ParquetFileInfo> ListParquetFiles(string? directoryPath = null)
    {
        List<ParquetFileInfo> files = [];
        List<string> candidateDirs = [];

        if (!string.IsNullOrWhiteSpace(directoryPath) && Directory.Exists(directoryPath))
        {
            candidateDirs.Add(directoryPath);
        }

        // Standard lakehouse and repository fixture directories
        string cwd = Directory.GetCurrentDirectory();
        candidateDirs.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "userdataKONGOR", "telemetry", "lakehouse"));
        candidateDirs.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", "matches"));
        candidateDirs.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "artifacts", "matches"));
        candidateDirs.Add(Path.Combine(cwd, "userdataKONGOR", "telemetry", "lakehouse"));
        candidateDirs.Add(Path.Combine(cwd, "logs", "matches"));
        candidateDirs.Add(Path.Combine(cwd, "artifacts", "matches"));
        candidateDirs.Add(Path.Combine(cwd, "artifacts"));
        candidateDirs.Add(Path.Combine(cwd, "tests", "fixtures"));
        candidateDirs.Add(Path.Combine(cwd, "games", "client", "userdata"));

        HashSet<string> seenPaths = new(StringComparer.OrdinalIgnoreCase);

        foreach (string dir in candidateDirs)
        {
            if (!Directory.Exists(dir)) continue;

            try
            {
                foreach (string filePath in Directory.EnumerateFiles(dir, "*.parquet", SearchOption.AllDirectories))
                {
                    if (!seenPaths.Add(filePath)) continue;

                    FileInfo fi = new(filePath);
                    long? matchId = null;

                    Match m = Regex.Match(fi.Name, @"match_(\d+)\.parquet", RegexOptions.IgnoreCase);
                    if (m.Success && long.TryParse(m.Groups[1].Value, out long mId))
                    {
                        matchId = mId;
                    }

                    files.Add(new ParquetFileInfo
                    {
                        FileName = fi.Name,
                        FullPath = fi.FullName,
                        FileSizeBytes = fi.Length,
                        FormattedSize = FormatBytes(fi.Length),
                        LastModified = fi.LastWriteTimeUtc,
                        MatchId = matchId
                    });
                }
            }
            catch { }
        }

        return files;
    }

    private async Task EnsureViewRegisteredAsync(string parquetPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(parquetPath) || parquetPath.Contains('\0'))
        {
            throw new ArgumentException("Target Parquet file path cannot be empty or contain NUL characters.", nameof(parquetPath));
        }

        string canonicalPath = Path.GetFullPath(parquetPath);
        if (!canonicalPath.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase))
        {
            _logger?.Invoke($"[SECURITY-ALERT] Blocked non-Parquet analytics source path: '{canonicalPath}'.");
            throw new InvalidOperationException(
                $"[SECURITY-ALERT] DuckDB analytics only permits '.parquet' match files (rejected '{Path.GetFileName(canonicalPath)}').");
        }

        if (!File.Exists(canonicalPath))
        {
            throw new FileNotFoundException($"Target Parquet file not found: {canonicalPath}", canonicalPath);
        }

        DateTime fileWriteUtc = File.GetLastWriteTimeUtc(canonicalPath);
        string normalizedPath = canonicalPath.Replace('\\', '/').Replace("'", "''");
        string ddl = $"CREATE OR REPLACE TEMP TABLE match_combat_events AS SELECT * FROM read_parquet('{normalizedPath}');";

        lock (_syncLock)
        {
            if (_connection is not null &&
                string.Equals(_loadedParquetPath, canonicalPath, StringComparison.OrdinalIgnoreCase) &&
                _loadedParquetWriteUtc == fileWriteUtc)
            {
                return;
            }

            // Because DuckDB's 'SET enable_external_access = false' is an irreversible one-way latch per DB instance,
            // materialize the validated .parquet file into a fresh :memory: instance first and then permanently lock it.
            DuckDBConnection freshConn = new("Data Source=:memory:");
            freshConn.Open();
            try
            {
                using (DuckDBCommand cmd = freshConn.CreateCommand())
                {
                    cmd.CommandText = ddl;
                    cmd.ExecuteNonQuery();
                }

                LockConnectionExternalAccessForever(freshConn);
            }
            catch
            {
                freshConn.Dispose();
                throw;
            }

            DuckDBConnection? previous = _connection;
            _connection = freshConn;
            _loadedParquetPath = canonicalPath;
            _loadedParquetWriteUtc = fileWriteUtc;
            previous?.Dispose();
        }

        _logger?.Invoke($"Materialized sandboxed temp table 'match_combat_events' from '{normalizedPath}' (external access permanently locked).");
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private string ResolveTargetParquet(string? parquetPath, long? matchId)
    {
        if (!string.IsNullOrWhiteSpace(parquetPath) && File.Exists(parquetPath))
        {
            return Path.GetFullPath(parquetPath);
        }

        var files = ListParquetFiles();
        if (matchId.HasValue)
        {
            var matchFile = files.FirstOrDefault(f => f.MatchId == matchId.Value);
            if (matchFile != null)
            {
                return matchFile.FullPath;
            }
        }

        if (files.Count > 0)
        {
            return files[0].FullPath;
        }

        throw new FileNotFoundException("No Parquet match files found in standard lakehouse directories.");
    }

    private static string ResolveParquetPath(string path)
    {
        if (File.Exists(path))
        {
            return Path.GetFullPath(path);
        }

        // Relative path check
        string abs = Path.GetFullPath(path);
        if (File.Exists(abs))
        {
            return abs;
        }

        return path;
    }

    private static object NormalizeValue(object val)
    {
        return val switch
        {
            DateTime dt => dt.ToString("o"),
            DateTimeOffset dto => dto.ToString("o"),
            _ => val
        };
    }

    private static string FormatBytes(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        int order = 0;
        double len = bytes;
        while (len >= 1024 && order < suffixes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.##} {suffixes[order]}";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _connection?.Dispose();
        _logger?.Invoke("DuckDbMatchAnalyticsService disposed.");
    }
}
