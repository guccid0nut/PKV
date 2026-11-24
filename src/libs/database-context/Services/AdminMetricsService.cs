// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Globalization;
using MERRICK.DatabaseContext.Queries;
using NEXUS.Contracts.CQRS.Telemetry;
using NEXUS.Contracts.Services;
using StackExchange.Redis;
using RedisDatabase = StackExchange.Redis.IDatabase;

namespace MERRICK.DatabaseContext.Services;

public sealed class AdminMetricsService : IAdminMetricsService, IDisposable
{
    private const string TelemetryHashKey = PopulationBreakdownDefaults.TelemetryRedisHashKey;
    public static readonly TimeSpan PopulationLiveThreshold = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan PopulationMaxStaleThreshold = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions PopulationJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly MerrickContext? _databaseContext;
    private readonly RedisDatabase? _distributedCache;
    private readonly ILogger<AdminMetricsService> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private readonly TimeProvider _timeProvider;
    private long _lastRefreshTicks;

    private AdminMetricsDTO _snapshot;

    public AdminMetricsService(
        ILogger<AdminMetricsService> logger,
        MerrickContext? databaseContext = null,
        RedisDatabase? distributedCache = null,
        IConnectionMultiplexer? multiplexer = null,
        TimeProvider? timeProvider = null)
    {
        _logger = logger;
        _databaseContext = databaseContext;
        _timeProvider = timeProvider ?? TimeProvider.System;
        DateTimeOffset now = _timeProvider.GetUtcNow();
        _snapshot = new AdminMetricsDTO(
            0,
            0,
            0.0,
            0,
            [],
            0,
            0,
            0,
            0.0,
            [],
            now,
            PopulationBreakdownDefaults.CreateUnavailable(now)
        );
        if (distributedCache != null)
            _distributedCache = distributedCache;
        else if (multiplexer != null)
            try
            {
                _distributedCache = multiplexer.GetDatabase();
            }
            catch
            {
                _distributedCache = null;
            }
    }

    public async Task<AdminMetricsDTO> GetAdminMetricsAsync(CancellationToken ct = default)
    {
        long nowTicks = Environment.TickCount64;

        // Sub-millisecond instant return if refreshed within 2 seconds
        if (_lastRefreshTicks > 0 && nowTicks - _lastRefreshTicks < 2000) return _snapshot;

        // Non-blocking lock check: if another request is currently refreshing, return snapshot instantly
        if (!await _refreshLock.WaitAsync(0, ct)) return _snapshot;

        try
        {
            int ccu = 0;
            int activeServers = 0;
            int activeQueues = 0;
            double avgWaitSeconds = 0.0;
            List<string> regions = [];
            bool redisSuccess = false;

            long slowQueries = 0;
            long masterRequests = 0;
            long chatCommands = 0;
            double p95Latency = 0.0;

            long memKongor = 0;
            long memChat = 0;
            long memAuth = 0;
            long memRedis = 0;

            DateTimeOffset now = _timeProvider.GetUtcNow();
            PopulationBreakdownResponseDto? populationBreakdown = null;

            if (_distributedCache != null)
                try
                {
                    HashEntry[] hashEntries = await _distributedCache.HashGetAllAsync(TelemetryHashKey);
                    if (hashEntries.Length > 0)
                    {
                        Dictionary<string, string> dict = hashEntries.ToDictionary(
                            e => e.Name.ToString(),
                            e => e.Value.ToString(),
                            StringComparer.OrdinalIgnoreCase);

                        if (dict.TryGetValue("CCU", out string? ccuStr) && int.TryParse(ccuStr, out int parsedCcu))
                            ccu = Math.Max(0, parsedCcu);

                        if (dict.TryGetValue("ActiveMatchServers", out string? serversStr) &&
                            int.TryParse(serversStr, out int parsedServers))
                            activeServers = Math.Max(0, parsedServers);

                        if (dict.TryGetValue("ActiveMatchmakingQueues", out string? queuesStr) &&
                            int.TryParse(queuesStr, out int parsedQueues))
                            activeQueues = Math.Max(0, parsedQueues);

                        if (dict.TryGetValue("AverageWaitTimeSeconds", out string? waitStr) &&
                            double.TryParse(waitStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedWait))
                            avgWaitSeconds = Math.Max(0.0, parsedWait);

                        if (dict.TryGetValue("P95LatencyMs", out string? p95Str) &&
                            double.TryParse(p95Str, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedP95))
                            p95Latency = Math.Max(0.0, parsedP95);

                        if (dict.TryGetValue("SlowQueriesTotal", out string? slowStr) &&
                            long.TryParse(slowStr, out long parsedSlow))
                            slowQueries = Math.Max(0, parsedSlow);

                        if (dict.TryGetValue("MasterServerRequestsTotal", out string? masterStr) &&
                            long.TryParse(masterStr, out long parsedMaster))
                            masterRequests = Math.Max(0, parsedMaster);

                        if (dict.TryGetValue("ChatCommandsTotal", out string? cmdStr) &&
                            long.TryParse(cmdStr, out long parsedCmd))
                            chatCommands = Math.Max(0, parsedCmd);

                        if (dict.TryGetValue("MEM_KONGOR", out string? kMemStr) &&
                            long.TryParse(kMemStr, out long parsedKMem))
                            memKongor = parsedKMem;

                        if (dict.TryGetValue("MEM_TRANSMUTANSTEIN", out string? cMemStr) &&
                            long.TryParse(cMemStr, out long parsedCMem))
                            memChat = parsedCMem;

                        if (dict.TryGetValue("MEM_ZORGATH", out string? aMemStr) &&
                            long.TryParse(aMemStr, out long parsedAMem))
                            memAuth = parsedAMem;

                        if (dict.TryGetValue("MEM_REDIS", out string? rMemStr) &&
                            long.TryParse(rMemStr, out long parsedRMem))
                            memRedis = parsedRMem;

                        if (dict.TryGetValue("ActiveRegionsJson", out string? regionsJson) &&
                            !string.IsNullOrWhiteSpace(regionsJson))
                            try
                            {
                                List<string>? parsedRegions = JsonSerializer.Deserialize<List<string>>(regionsJson);
                                if (parsedRegions is { Count: > 0 }) regions = parsedRegions;
                            }
                            catch (JsonException jex)
                            {
                                _logger.LogWarning(jex,
                                    "[AdminMetricsService] Failed deserializing ActiveRegionsJson from TELEMETRY:METRICS.");
                            }

                        if (dict.TryGetValue(PopulationBreakdownDefaults.PopulationBreakdownRedisField, out string? popJson) &&
                            !string.IsNullOrWhiteSpace(popJson))
                        {
                            populationBreakdown = ParsePopulationBreakdownSnapshot(popJson, ccu, now);
                        }
                        else
                        {
                            _logger.LogDebug(
                                "[AdminMetricsService] PopulationBreakdownJson missing from TELEMETRY:METRICS; marking population breakdown unavailable.");
                        }

                        redisSuccess = true;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "[AdminMetricsService] Failed reading TELEMETRY:METRICS hash from Redis. Falling back to SQL DB.");
                }

            // 2. Secondary Fallback Read: SQL Server SystemTelemetryMetrics Table
            if (!redisSuccess && _databaseContext != null)
                try
                {
                    SystemTelemetryMetrics? dbMetrics =
                        await CompiledQueries.GetTelemetryMetricsById(_databaseContext, 1);

                    if (dbMetrics != null)
                    {
                        ccu = Math.Max(0, dbMetrics.ConcurrentConnectedUsers);
                        activeServers = Math.Max(0, dbMetrics.ActiveMatchServers);
                        activeQueues = Math.Max(0, dbMetrics.ActiveMatchmakingQueues);
                        avgWaitSeconds = Math.Max(0.0, dbMetrics.AverageWaitTimeSeconds);

                        if (!string.IsNullOrWhiteSpace(dbMetrics.ActiveRegionsJson))
                            try
                            {
                                List<string>? parsedRegions =
                                    JsonSerializer.Deserialize<List<string>>(dbMetrics.ActiveRegionsJson);
                                if (parsedRegions is { Count: > 0 }) regions = parsedRegions;
                            }
                            catch
                            {
                                // Keep default regions
                            }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[AdminMetricsService] Failed reading SystemTelemetryMetrics from SQL DB.");
                }

            long currentMem = Process.GetCurrentProcess().WorkingSet64;
            Dictionary<string, long> serviceMemory = new(StringComparer.OrdinalIgnoreCase)
            {
                ["KONGOR (Master)"] = memKongor > 0 ? memKongor : 0,
                ["TRANSMUTANSTEIN (Chat)"] = memChat > 0 ? memChat : 0,
                ["ZORGATH (Auth API)"] = memAuth > 0 ? memAuth : 0,
                ["MERRICK (SQL DB)"] = currentMem,
                ["DAWNBRINGER (Web Host)"] = currentMem,
                ["Redis (Pub/Sub)"] = memRedis > 0 ? memRedis : 0
            };

            populationBreakdown ??= PopulationBreakdownDefaults.CreateUnavailable(now);

            _snapshot = new AdminMetricsDTO(
                ccu,
                activeQueues,
                Math.Round(avgWaitSeconds, 1),
                activeServers,
                regions,
                slowQueries,
                masterRequests,
                chatCommands,
                p95Latency,
                serviceMemory,
                now,
                populationBreakdown
            );

            _lastRefreshTicks = Environment.TickCount64;
        }
        finally
        {
            _refreshLock.Release();
        }

        return _snapshot;
    }

    private PopulationBreakdownResponseDto ParsePopulationBreakdownSnapshot(
        string json,
        int liveCcu,
        DateTimeOffset now)
    {
        try
        {
            PopulationBreakdownResponseDto? parsed =
                JsonSerializer.Deserialize<PopulationBreakdownResponseDto>(json, PopulationJsonOptions);
            if (parsed is null || parsed.UpdatedAtUtc == default)
            {
                _logger.LogWarning(
                    "[AdminMetricsService] PopulationBreakdownJson was null or missing UpdatedAtUtc; marking unavailable.");
                return PopulationBreakdownDefaults.CreateUnavailable(now);
            }

            TimeSpan age = now - parsed.UpdatedAtUtc;
            if (age <= PopulationLiveThreshold)
            {
                PopulationBreakdownDefaults.NormalizeForOutput(parsed, liveCcu, PopulationTelemetryStatus.Live);
                return parsed;
            }

            if (age <= PopulationMaxStaleThreshold)
            {
                _logger.LogInformation(
                    "[AdminMetricsService] PopulationBreakdownJson is stale (AgeSeconds={AgeSeconds:F1}); serving stale snapshot.",
                    age.TotalSeconds);
                PopulationBreakdownDefaults.NormalizeForOutput(parsed, liveCcu, PopulationTelemetryStatus.Stale);
                return parsed;
            }

            _logger.LogWarning(
                "[AdminMetricsService] PopulationBreakdownJson expired (AgeSeconds={AgeSeconds:F1} > {MaxSeconds}s); marking unavailable.",
                age.TotalSeconds,
                PopulationMaxStaleThreshold.TotalSeconds);
            return PopulationBreakdownDefaults.CreateUnavailable(now);
        }
        catch (JsonException jex)
        {
            _logger.LogWarning(jex,
                "[AdminMetricsService] Failed deserializing PopulationBreakdownJson; marking unavailable.");
            return PopulationBreakdownDefaults.CreateUnavailable(now);
        }
    }

    public void Dispose()
    {
        _refreshLock.Dispose();
    }
}
