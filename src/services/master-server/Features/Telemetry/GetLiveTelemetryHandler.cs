// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Caching;
using MediatR;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Entities.Statistics;
using MERRICK.DatabaseContext.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NEXUS.Contracts.CQRS.Telemetry;
using NEXUS.Contracts.Domain;
using StackExchange.Redis;

namespace KONGOR.MasterServer.Features.Telemetry;

/// <summary>
/// MediatR handler for computing live ecosystem telemetry metrics, active matches, and winrate leaderboards.
/// </summary>
public sealed partial class GetLiveTelemetryHandler(
    MerrickContext dbContext,
    IDatabase distributedCache,
    ILogger<GetLiveTelemetryHandler> logger,
    INexusHybridCache? hybridCache = null,
    IServiceScopeFactory? serviceScopeFactory = null)
    : IRequestHandler<GetLiveTelemetryQuery, LiveTelemetryResponseDto>
{
    private static LiveTelemetryResponseDto? _fallbackCache;
    private static DateTimeOffset _fallbackCacheExpiry = DateTimeOffset.MinValue;
    private static readonly Lock FallbackLock = new();

    internal static void ResetFallbackCacheForTests()
    {
        lock (FallbackLock)
        {
            _fallbackCache = null;
            _fallbackCacheExpiry = DateTimeOffset.MinValue;
        }
    }

    public async Task<LiveTelemetryResponseDto> Handle(GetLiveTelemetryQuery request, CancellationToken cancellationToken)
    {
        if (hybridCache != null)
        {
            LiveTelemetryResponseDto? result = await hybridCache.GetOrCreateAsync(
                "TELEMETRY:LIVE",
                async ct =>
                {
                    if (serviceScopeFactory != null)
                    {
                        await using AsyncServiceScope scope = serviceScopeFactory.CreateAsyncScope();
                        MerrickContext scopedContext = scope.ServiceProvider.GetRequiredService<MerrickContext>();
                        return await ComputeTelemetryAsync(scopedContext, ct);
                    }
                    return await ComputeTelemetryAsync(dbContext, ct);
                },
                tags: ["telemetry"],
                l1Expiration: TimeSpan.FromSeconds(30),
                l2Expiration: TimeSpan.FromSeconds(60),
                allowStaleOnError: true,
                cancellationToken: cancellationToken);

            return result ?? new LiveTelemetryResponseDto();
        }

        DateTimeOffset now = TimeProvider.System.GetUtcNow();
        lock (FallbackLock)
        {
            if (_fallbackCache != null && now < _fallbackCacheExpiry)
                return _fallbackCache;
        }

        LiveTelemetryResponseDto computed = await ComputeTelemetryAsync(dbContext, cancellationToken);
        lock (FallbackLock)
        {
            _fallbackCache = computed;
            _fallbackCacheExpiry = now.AddSeconds(30);
        }

        return computed;
    }

    private sealed record LeaderboardStatProjection(
        string AccountName,
        int MatchesPlayed,
        int MatchesWon);

    private async Task<LiveTelemetryResponseDto> ComputeTelemetryAsync(MerrickContext context, CancellationToken ct)
    {
        // 1. Resolve Online Players (CCU) from Redis or DB
        int ccu = 0;
        try
        {
            RedisValue redisCcu = await distributedCache.HashGetAsync("TELEMETRY:METRICS", "CCU");
            if (redisCcu.HasValue && int.TryParse(redisCcu.ToString(), out int parsedCcu))
                ccu = Math.Max(0, parsedCcu);
        }
        catch (Exception ex)
        {
            LogRedisError(ex.Message);
        }

        // 2. Resolve Active Matches In Progress from Redis
        int matchesCount = 0;
        try
        {
            RedisValue redisMatches = await distributedCache.HashGetAsync("TELEMETRY:METRICS", "MatchesInProgress");
            if (redisMatches.HasValue && int.TryParse(redisMatches.ToString(), out int parsedMatches))
                matchesCount = Math.Max(0, parsedMatches);
        }
        catch (Exception ex)
        {
            LogRedisError(ex.Message);
        }

        SystemTelemetryMetrics? telemetryMetrics = await context.SystemTelemetryMetrics
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.ID == 1, ct);

        if (ccu == 0 && telemetryMetrics != null) ccu = Math.Max(0, telemetryMetrics.ConcurrentConnectedUsers);

        // 3. Resolve Top 10 Winrate Players via a single lightweight projection query (preferring MatchesPlayed >= 5 first)
        List<LeaderboardStatProjection> projectedStats = await context.AccountStatistics
            .AsNoTracking()
            .Where(s => s.Type == AccountStatisticsType.Matchmaking &&
                        s.MatchesPlayed > 0 &&
                        s.MatchesWon >= 0 &&
                        s.MatchesWon <= s.MatchesPlayed &&
                        s.Account != null &&
                        s.Account.Type != AccountType.ServerHost &&
                        !s.Account.Name.StartsWith("guest") &&
                        !s.Account.Name.Contains("host"))
            .OrderByDescending(s => s.MatchesPlayed >= 5 ? 1 : 0)
            .ThenByDescending(s => (double)s.MatchesWon / s.MatchesPlayed)
            .ThenByDescending(s => s.MatchesPlayed)
            .Take(10)
            .Select(s => new LeaderboardStatProjection(
                s.Account!.Name,
                s.MatchesPlayed,
                s.MatchesWon))
            .ToListAsync(ct);

        bool hasQualifiedPlayers = projectedStats.Any(static s => s.MatchesPlayed >= 5);
        IEnumerable<LeaderboardStatProjection> effectiveStats = hasQualifiedPlayers
            ? projectedStats.Where(static s => s.MatchesPlayed >= 5)
            : projectedStats;

        List<LeaderboardPlayerDto> topPlayers = new(projectedStats.Count);
        int rank = 1;
        foreach (LeaderboardStatProjection stat in effectiveStats)
        {
            double calculated = (double)stat.MatchesWon / Math.Max(1, stat.MatchesPlayed) * 100.0;
            double winRate = Math.Round(Math.Clamp(calculated, 0.0, 100.0), 1);
            topPlayers.Add(new LeaderboardPlayerDto
            {
                Rank = rank++,
                AccountName = stat.AccountName,
                WinRate = winRate,
                MatchesPlayed = stat.MatchesPlayed,
                RankTier = winRate >= 70.0 ? "Immortal" : winRate >= 60.0 ? "Diamond" : "Gold"
            });
        }

        return new LiveTelemetryResponseDto
        {
            OnlinePlayers = ccu,
            MatchesInProgress = matchesCount,
            TopWinratePlayers = topPlayers
        };
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "[LiveTelemetry] Could not fetch metric from Redis: {ErrorMessage}")]
    private partial void LogRedisError(string errorMessage);
}
