// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Globalization;
using System.Text.Json;
using ASPIRE.Common.Caching;
using ASPIRE.Common.Models.ServerManagement;
using KONGOR.MasterServer.Extensions;
using KONGOR.MasterServer.Serialization;
using MediatR;
using Microsoft.Extensions.Logging;
using NEXUS.Contracts.CQRS.Telemetry;
using StackExchange.Redis;

namespace KONGOR.MasterServer.Features.Telemetry;

/// <summary>
/// MediatR handler for resolving the detailed live population breakdown (<c>GET /api/telemetry/population</c>).
/// Reads the ChatServer-published <c>TELEMETRY:METRICS:PopulationBreakdownJson</c> snapshot from Redis/Garnet
/// (cached for 5 seconds in <see cref="INexusHybridCache"/> under <c>TELEMETRY:POPULATION</c>), and gracefully
/// falls back to computing live server counts from <c>DistributedCache.GetMatchServers()</c> whenever the
/// snapshot is missing, invalid, or older than 60 seconds.
/// </summary>
public sealed partial class GetPopulationBreakdownHandler(
    IDatabase distributedCache,
    ILogger<GetPopulationBreakdownHandler> logger,
    INexusHybridCache? hybridCache = null,
    TimeProvider? timeProvider = null)
    : IRequestHandler<GetPopulationBreakdownQuery, PopulationBreakdownResponseDto>
{
    public const string PopulationCacheKey = "TELEMETRY:POPULATION";
    public static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan SnapshotStalenessThreshold = TimeSpan.FromSeconds(60);

    private static readonly string[] CacheTags = ["telemetry"];

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly Lock _instanceCacheLock = new();
    private PopulationBreakdownResponseDto? _instanceCachedValue;
    private DateTimeOffset _instanceCacheExpiry = DateTimeOffset.MinValue;

    public async Task<PopulationBreakdownResponseDto> Handle(
        GetPopulationBreakdownQuery request,
        CancellationToken cancellationToken)
    {
        if (hybridCache != null)
        {
            PopulationBreakdownResponseDto? cached = await hybridCache.GetOrCreateAsync(
                PopulationCacheKey,
                async ct => await ComputePopulationBreakdownAsync(ct).ConfigureAwait(false),
                tags: CacheTags,
                l1Expiration: CacheDuration,
                l2Expiration: CacheDuration,
                allowStaleOnError: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return cached ?? PopulationBreakdownDefaults.CreateEmpty(_timeProvider.GetUtcNow());
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        lock (_instanceCacheLock)
        {
            if (_instanceCachedValue is not null && now < _instanceCacheExpiry)
            {
                return _instanceCachedValue;
            }
        }

        PopulationBreakdownResponseDto computed = await ComputePopulationBreakdownAsync(cancellationToken)
            .ConfigureAwait(false);

        lock (_instanceCacheLock)
        {
            _instanceCachedValue = computed;
            _instanceCacheExpiry = now.Add(CacheDuration);
        }

        return computed;
    }

    private async Task<PopulationBreakdownResponseDto> ComputePopulationBreakdownAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        DateTimeOffset now = _timeProvider.GetUtcNow();

        // 1. Read live CCU from Redis hash TELEMETRY:METRICS:CCU
        int? redisCcu = null;
        try
        {
            RedisValue rawCcu = await distributedCache.HashGetAsync(
                PopulationBreakdownDefaults.TelemetryRedisHashKey,
                "CCU").ConfigureAwait(false);

            if (rawCcu.HasValue &&
                int.TryParse(rawCcu.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedCcu))
            {
                redisCcu = Math.Max(0, parsedCcu);
            }
        }
        catch (Exception ex)
        {
            LogRedisReadError("CCU", ex.Message);
        }

        // 2. Read ChatServer-published PopulationBreakdownJson snapshot from Redis
        string fallbackReason = "missing";
        try
        {
            RedisValue rawSnapshot = await distributedCache.HashGetAsync(
                PopulationBreakdownDefaults.TelemetryRedisHashKey,
                PopulationBreakdownDefaults.PopulationBreakdownRedisField).ConfigureAwait(false);

            if (rawSnapshot.HasValue && !rawSnapshot.IsNullOrEmpty)
            {
                string json = rawSnapshot.ToString();
                PopulationBreakdownResponseDto? snapshot = null;
                try
                {
                    snapshot = JsonSerializer.Deserialize(
                        json,
                        MasterServerJsonContext.Default.PopulationBreakdownResponseDto);
                }
                catch (JsonException jex)
                {
                    fallbackReason = $"invalid_json:{jex.Message}";
                    LogInvalidSnapshotJson(jex.Message);
                }

                if (snapshot is not null)
                {
                    if (snapshot.UpdatedAtUtc == default)
                    {
                        fallbackReason = "missing_timestamp";
                    }
                    else
                    {
                        TimeSpan age = now - snapshot.UpdatedAtUtc;
                        if (age > SnapshotStalenessThreshold)
                        {
                            fallbackReason = $"stale_age_{age.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)}s";
                        }
                        else
                        {
                            PopulationBreakdownDefaults.NormalizeForOutput(
                                snapshot,
                                redisCcu,
                                PopulationTelemetryStatus.Live);

                            int availableServersTotal = snapshot.ServersAvailableByRegion.Sum(static r => r.Count);
                            int totalQueuedPlayers = snapshot.TmmQueues.Sum(static q => q.TotalQueued);
                            LogPopulationResolved(
                                "redis_snapshot",
                                "fresh",
                                snapshot.OnlinePlayers,
                                snapshot.GamesInProgressTotal,
                                availableServersTotal,
                                totalQueuedPlayers);

                            return snapshot;
                        }
                    }
                }
                else if (fallbackReason == "missing")
                {
                    fallbackReason = "null_deserialized_snapshot";
                }
            }
        }
        catch (Exception ex)
        {
            fallbackReason = $"redis_error:{ex.GetType().Name}";
            LogRedisReadError(PopulationBreakdownDefaults.PopulationBreakdownRedisField, ex.Message);
        }

        // 3. Graceful fallback: compute live GamesInProgressByRegion & ServersAvailableByRegion from MATCH-SERVERS
        return await BuildLiveServerFallbackAsync(now, redisCcu ?? 0, fallbackReason).ConfigureAwait(false);
    }

    private async Task<PopulationBreakdownResponseDto> BuildLiveServerFallbackAsync(
        DateTimeOffset now,
        int onlinePlayers,
        string fallbackReason)
    {
        List<MatchServer> servers = [];
        try
        {
            servers = await distributedCache.GetMatchServers().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogRedisReadError("MATCH-SERVERS", ex.Message);
        }

        int gamesInProgressTotal = 0;
        int excludedArcadeCount = 0;
        Dictionary<string, int> gamesInProgressCounts = new(StringComparer.Ordinal);
        Dictionary<string, int> serversAvailableCounts = new(StringComparer.Ordinal);

        foreach (MatchServer server in servers)
        {
            if (server is null)
            {
                continue;
            }

            if (server.IsQueueArcadeServer())
            {
                excludedArcadeCount++;
                continue;
            }

            string region = PopulationBreakdownDefaults.NormalizeServerRegion(server.Location);
            switch (server.Status)
            {
                case ServerStatus.SERVER_STATUS_ACTIVE:
                case ServerStatus.SERVER_STATUS_LOADING:
                    gamesInProgressTotal++;
                    gamesInProgressCounts[region] = gamesInProgressCounts.TryGetValue(region, out int inProg)
                        ? inProg + 1
                        : 1;
                    break;

                case ServerStatus.SERVER_STATUS_IDLE:
                case ServerStatus.SERVER_STATUS_SLEEPING:
                    serversAvailableCounts[region] = serversAvailableCounts.TryGetValue(region, out int avail)
                        ? avail + 1
                        : 1;
                    break;
            }
        }

        List<RegionCountDto> gamesInProgressByRegion = new(gamesInProgressCounts.Count + 1)
        {
            new RegionCountDto
            {
                Region = PopulationBreakdownDefaults.AllLocationsLabel,
                Count = gamesInProgressTotal
            }
        };

        foreach (KeyValuePair<string, int> kvp in gamesInProgressCounts
                     .Where(static kv => kv.Value > 0)
                     .OrderBy(static kv => kv.Key, StringComparer.Ordinal))
        {
            gamesInProgressByRegion.Add(new RegionCountDto { Region = kvp.Key, Count = kvp.Value });
        }

        List<RegionCountDto> serversAvailableByRegion =
        [
            .. serversAvailableCounts
                .Where(static kv => kv.Value > 0)
                .OrderBy(static kv => kv.Key, StringComparer.Ordinal)
                .Select(static kv => new RegionCountDto { Region = kv.Key, Count = kv.Value })
        ];

        PopulationBreakdownResponseDto fallback = new()
        {
            Status = PopulationTelemetryStatus.Stale,
            OnlinePlayers = Math.Max(0, onlinePlayers),
            GamesInProgressTotal = gamesInProgressTotal,
            GamesInProgressByRegion = gamesInProgressByRegion,
            ServersAvailableByRegion = serversAvailableByRegion,
            TmmQueues = PopulationBreakdownDefaults.CreateDefaultTmmQueues(),
            UpdatedAtUtc = now
        };

        int availableServersTotal = serversAvailableByRegion.Sum(static r => r.Count);
        LogPopulationFallbackDetails(fallbackReason, servers.Count, excludedArcadeCount);
        LogPopulationResolved(
            "live_server_fallback",
            fallbackReason,
            fallback.OnlinePlayers,
            fallback.GamesInProgressTotal,
            availableServersTotal,
            totalQueuedPlayers: 0);

        return fallback;
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "[LiveTelemetry][Population] Resolved population breakdown (Source={Source}, Reason={Reason}, OnlinePlayers={OnlinePlayers}, GamesInProgressTotal={GamesInProgressTotal}, AvailableServersTotal={AvailableServersTotal}, TotalQueuedPlayers={TotalQueuedPlayers}).")]
    private partial void LogPopulationResolved(
        string source,
        string reason,
        int onlinePlayers,
        int gamesInProgressTotal,
        int availableServersTotal,
        int totalQueuedPlayers);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "[LiveTelemetry][Population] Fallback active (Reason={FallbackReason}): scanned {TotalServers} cached MatchServer(s), excluded {ExcludedArcadeCount} QueueArcade server(s).")]
    private partial void LogPopulationFallbackDetails(
        string fallbackReason,
        int totalServers,
        int excludedArcadeCount);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "[LiveTelemetry][Population] Could not read Redis field '{FieldName}': {ErrorMessage}")]
    private partial void LogRedisReadError(string fieldName, string errorMessage);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "[LiveTelemetry][Population] Failed to deserialize PopulationBreakdownJson from Redis: {ErrorMessage}")]
    private partial void LogInvalidSnapshotJson(string errorMessage);
}
