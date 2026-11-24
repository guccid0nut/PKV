// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Globalization;
using System.Text.Json;
using ASPIRE.Common.Caching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NEXUS.Contracts.CQRS.Telemetry;
using StackExchange.Redis;

namespace DAWNBRINGER.WebPortal.UI.Services;

/// <summary>
/// Resolves strictly public, non-PII aggregate population and TMM queue telemetry for the
/// DAWNBRINGER public landing page and anonymous <c>GET /api/telemetry/population</c> endpoint.
/// </summary>
public interface IPublicPopulationTelemetryService
{
    Task<PublicPopulationTelemetryDto> GetPublicPopulationAsync(CancellationToken ct = default);
}

/// <summary>
/// Single-flight 5-second cached reader that reads <c>TELEMETRY:METRICS</c> (<c>CCU</c> and
/// <c>PopulationBreakdownJson</c>) directly from Valkey/Redis without fanning anonymous web traffic
/// into ZORGATH's internal admin metrics API. Falls back to <see cref="IAdminMetricsService"/> only
/// when no Redis multiplexer is registered (e.g., isolated unit test hosts).
/// </summary>
public sealed class PublicPopulationTelemetryService : IPublicPopulationTelemetryService
{
    public const string CacheKey = "DAWNBRINGER:PUBLIC_POPULATION";
    public const string RateLimitPolicyName = "PublicTelemetry";

    public static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan LiveFreshnessThreshold = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan MaxStaleThreshold = TimeSpan.FromMinutes(5);

    private static readonly string[] CacheTags = ["telemetry"];
    private static readonly RedisValue[] TelemetryFields =
    [
        "CCU",
        PopulationBreakdownDefaults.PopulationBreakdownRedisField
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ILogger<PublicPopulationTelemetryService> _logger;
    private readonly INexusHybridCache? _hybridCache;
    private readonly IConnectionMultiplexer? _multiplexer;
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly TimeProvider _timeProvider;

    private readonly Lock _localCacheLock = new();
    private PublicPopulationTelemetryDto? _localCached;
    private DateTimeOffset _localCacheExpiry = DateTimeOffset.MinValue;

    public PublicPopulationTelemetryService(
        ILogger<PublicPopulationTelemetryService> logger,
        INexusHybridCache? hybridCache = null,
        IConnectionMultiplexer? multiplexer = null,
        IServiceScopeFactory? scopeFactory = null,
        TimeProvider? timeProvider = null)
    {
        _logger = logger;
        _hybridCache = hybridCache;
        _multiplexer = multiplexer;
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<PublicPopulationTelemetryDto> GetPublicPopulationAsync(CancellationToken ct = default)
    {
        if (_hybridCache != null)
        {
            PublicPopulationTelemetryDto? cached = await _hybridCache.GetOrCreateAsync(
                CacheKey,
                async token => await ComputeSnapshotAsync(token).ConfigureAwait(false),
                tags: CacheTags,
                l1Expiration: CacheDuration,
                l2Expiration: CacheDuration,
                allowStaleOnError: true,
                cancellationToken: ct).ConfigureAwait(false);

            return cached ?? PublicPopulationTelemetryDto.CreateUnavailable(_timeProvider.GetUtcNow());
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        lock (_localCacheLock)
        {
            if (_localCached is not null && now < _localCacheExpiry)
            {
                return _localCached;
            }
        }

        PublicPopulationTelemetryDto fresh = await ComputeSnapshotAsync(ct).ConfigureAwait(false);
        lock (_localCacheLock)
        {
            _localCached = fresh;
            _localCacheExpiry = now.Add(CacheDuration);
        }

        return fresh;
    }

    private async Task<PublicPopulationTelemetryDto> ComputeSnapshotAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        DateTimeOffset now = _timeProvider.GetUtcNow();

        // 1. Primary path: Direct Valkey/Redis read (2 hash fields in 1 round-trip, zero ZORGATH admin API hop)
        if (_multiplexer != null)
        {
            try
            {
                IDatabase db = _multiplexer.GetDatabase();
                RedisValue[] values = await db.HashGetAsync(
                    PopulationBreakdownDefaults.TelemetryRedisHashKey,
                    TelemetryFields).ConfigureAwait(false);

                int? liveCcu = null;
                if (values.Length > 0 && values[0].HasValue &&
                    int.TryParse(values[0].ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedCcu))
                {
                    liveCcu = Math.Max(0, parsedCcu);
                }

                string? popJson = values.Length > 1 && values[1].HasValue && !values[1].IsNullOrEmpty
                    ? values[1].ToString()
                    : null;

                if (!string.IsNullOrWhiteSpace(popJson))
                {
                    PublicPopulationTelemetryDto parsed = ParseSnapshot(popJson, liveCcu, now, source: "valkey_direct");
                    return parsed;
                }

                _logger.LogWarning(
                    "[PublicPopulationTelemetry] PopulationBreakdownJson missing in Valkey TELEMETRY:METRICS (LiveCcu={LiveCcu}); returning unavailable state.",
                    liveCcu);
                return PublicPopulationTelemetryDto.CreateUnavailable(now);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[PublicPopulationTelemetry] Failed reading TELEMETRY:METRICS directly from Valkey; returning unavailable state.");
                return PublicPopulationTelemetryDto.CreateUnavailable(now);
            }
        }

        // 2. Fallback path (only when Valkey multiplexer is not configured, e.g., test hosts):
        // project strictly through PublicPopulationTelemetryDto so no admin fields can leak.
        try
        {
            AdminMetricsDTO? adminMetrics = null;
            if (_scopeFactory != null)
            {
                using IServiceScope scope = _scopeFactory.CreateScope();
                IAdminMetricsService? scopedService = scope.ServiceProvider.GetService<IAdminMetricsService>();
                if (scopedService != null)
                {
                    adminMetrics = await scopedService.GetAdminMetricsAsync(ct).ConfigureAwait(false);
                }
            }

            if (adminMetrics?.PopulationBreakdown is not null)
            {
                PublicPopulationTelemetryDto projected = PublicPopulationTelemetryDto.FromBreakdown(
                    adminMetrics.PopulationBreakdown,
                    now);
                _logger.LogDebug(
                    "[PublicPopulationTelemetry] Resolved via fallback service (Status={Status}, OnlinePlayers={OnlinePlayers}, ActiveGames={ActiveGames}).",
                    projected.Status,
                    projected.OnlinePlayers,
                    projected.GamesInProgressTotal);
                return projected;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[PublicPopulationTelemetry] Fallback population resolution failed; returning unavailable state.");
        }

        return PublicPopulationTelemetryDto.CreateUnavailable(now);
    }

    private PublicPopulationTelemetryDto ParseSnapshot(
        string json,
        int? liveCcu,
        DateTimeOffset now,
        string source)
    {
        try
        {
            PopulationBreakdownResponseDto? snapshot =
                JsonSerializer.Deserialize<PopulationBreakdownResponseDto>(json, JsonOptions);
            if (snapshot is null || snapshot.UpdatedAtUtc == default)
            {
                _logger.LogWarning(
                    "[PublicPopulationTelemetry] Snapshot from {Source} was null or missing UpdatedAtUtc; returning unavailable.",
                    source);
                return PublicPopulationTelemetryDto.CreateUnavailable(now);
            }

            TimeSpan age = now - snapshot.UpdatedAtUtc;
            if (age <= LiveFreshnessThreshold)
            {
                PopulationBreakdownDefaults.NormalizeForOutput(snapshot, liveCcu, PopulationTelemetryStatus.Live);
                PublicPopulationTelemetryDto dto = PublicPopulationTelemetryDto.FromBreakdown(snapshot, now);
                _logger.LogDebug(
                    "[PublicPopulationTelemetry] Resolved live snapshot (Source={Source}, OnlinePlayers={OnlinePlayers}, ActiveGames={ActiveGames}, AvailableServers={AvailableServers}, TotalQueued={TotalQueued}).",
                    source,
                    dto.OnlinePlayers,
                    dto.GamesInProgressTotal,
                    dto.ServersAvailableByRegion.Sum(static r => r.Count),
                    dto.TmmQueues.Sum(static q => q.TotalQueued));
                return dto;
            }

            if (age <= MaxStaleThreshold)
            {
                PopulationBreakdownDefaults.NormalizeForOutput(snapshot, liveCcu, PopulationTelemetryStatus.Stale);
                PublicPopulationTelemetryDto dto = PublicPopulationTelemetryDto.FromBreakdown(snapshot, now);
                _logger.LogInformation(
                    "[PublicPopulationTelemetry] Serving stale snapshot (Source={Source}, AgeSeconds={AgeSeconds:F1}, OnlinePlayers={OnlinePlayers}).",
                    source,
                    age.TotalSeconds,
                    dto.OnlinePlayers);
                return dto;
            }

            _logger.LogWarning(
                "[PublicPopulationTelemetry] Snapshot from {Source} expired (AgeSeconds={AgeSeconds:F1} > {MaxSeconds}s); returning unavailable.",
                source,
                age.TotalSeconds,
                MaxStaleThreshold.TotalSeconds);
            return PublicPopulationTelemetryDto.CreateUnavailable(now);
        }
        catch (JsonException jex)
        {
            _logger.LogWarning(jex,
                "[PublicPopulationTelemetry] Failed deserializing PopulationBreakdownJson from {Source}; returning unavailable.",
                source);
            return PublicPopulationTelemetryDto.CreateUnavailable(now);
        }
    }
}
