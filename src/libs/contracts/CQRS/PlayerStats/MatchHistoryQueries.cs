// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;
using NEXUS.Contracts.Clients;

namespace NEXUS.Contracts.CQRS.PlayerStats;

/// <summary>
///     Query to retrieve gameplay statistics for a specific account.
/// </summary>
/// <param name="AccountId">The unique account identifier.</param>
public sealed record GetAccountStatisticsQuery(int AccountId)
    : IRequest<AccountStatisticsDto?>, ICacheableQuery<AccountStatisticsDto?>
{
    /// <inheritdoc />
    public string CacheKey => $"cache:stats:account:{AccountId}";

    /// <inheritdoc />
    public TimeSpan CacheDuration => TimeSpan.FromMinutes(2);
}

/// <summary>
///     Query to retrieve top ranked accounts based on ladder MMR.
/// </summary>
/// <param name="Limit">The maximum number of top ranked accounts to return.</param>
public sealed record GetTopRankedAccountsQuery(int Limit = 100)
    : IRequest<IReadOnlyList<AccountStatisticsDto>>, ICacheableQuery<IReadOnlyList<AccountStatisticsDto>>
{
    /// <inheritdoc />
    public string CacheKey => $"cache:stats:topranked:{Limit}";

    /// <inheritdoc />
    public TimeSpan CacheDuration => TimeSpan.FromMinutes(2);
}

/// <summary>
///     Query to retrieve recently completed matches across all servers.
/// </summary>
/// <param name="Limit">The maximum number of matches to retrieve.</param>
public sealed record GetRecentMatchesQuery(int Limit = 50)
    : IRequest<IReadOnlyList<MatchStatisticsDto>>, ICacheableQuery<IReadOnlyList<MatchStatisticsDto>>
{
    /// <inheritdoc />
    public string CacheKey => $"cache:matches:recent:{Limit}";

    /// <inheritdoc />
    public TimeSpan CacheDuration => TimeSpan.FromSeconds(30);
}

/// <summary>
///     Query to retrieve recent match history for a specific account.
/// </summary>
/// <param name="AccountId">The unique account identifier.</param>
/// <param name="Limit">The maximum number of matches to retrieve.</param>
public sealed record GetAccountMatchesQuery(int AccountId, int Limit = 20)
    : IRequest<IReadOnlyList<MatchStatisticsDto>>, ICacheableQuery<IReadOnlyList<MatchStatisticsDto>>
{
    /// <inheritdoc />
    public string CacheKey => $"cache:matches:account:{AccountId}:l{Limit}";

    /// <inheritdoc />
    public TimeSpan CacheDuration => TimeSpan.FromMinutes(1);
}

/// <summary>
///     Query to retrieve match details by unique match ID.
/// </summary>
/// <param name="MatchId">The unique match identifier.</param>
public sealed record GetMatchDetailsQuery(int MatchId)
    : IRequest<MatchStatisticsDto?>, ICacheableQuery<MatchStatisticsDto?>
{
    /// <inheritdoc />
    public string CacheKey => $"cache:match:detail:{MatchId}";

    /// <inheritdoc />
    public TimeSpan CacheDuration => TimeSpan.FromMinutes(10);
}

/// <summary>
///     Query to retrieve player statistics for all participants in a match.
/// </summary>
/// <param name="MatchId">The unique match identifier.</param>
public sealed record GetMatchPlayerStatsQuery(int MatchId)
    : IRequest<IReadOnlyList<PlayerStatisticsDto>>, ICacheableQuery<IReadOnlyList<PlayerStatisticsDto>>
{
    /// <inheritdoc />
    public string CacheKey => $"cache:match:players:{MatchId}";

    /// <inheritdoc />
    public TimeSpan CacheDuration => TimeSpan.FromMinutes(10);
}

/// <summary>
///     Query to retrieve recent player performance stats across matches for an account.
/// </summary>
/// <param name="AccountId">The unique account identifier.</param>
/// <param name="Limit">The maximum number of recent records to retrieve.</param>
public sealed record GetRecentPlayerStatsQuery(int AccountId, int Limit = 20)
    : IRequest<IReadOnlyList<PlayerStatisticsDto>>, ICacheableQuery<IReadOnlyList<PlayerStatisticsDto>>
{
    /// <inheritdoc />
    public string CacheKey => $"cache:playerstats:account:{AccountId}:l{Limit}";

    /// <inheritdoc />
    public TimeSpan CacheDuration => TimeSpan.FromMinutes(1);
}

/// <summary>
///     Query to filter match IDs and return only those that already exist in the database.
/// </summary>
/// <param name="MatchIds">The list of candidate match IDs to check.</param>
public sealed record GetSubmittedMatchIdsQuery(List<int> MatchIds) : IRequest<List<int>>;

/// <summary>
///     Query to retrieve the total count of matches recorded.
/// </summary>
public sealed record GetTotalMatchesCountQuery : IRequest<int>, ICacheableQuery<int>
{
    /// <inheritdoc />
    public string CacheKey => "cache:matches:total-count";

    /// <inheritdoc />
    public TimeSpan CacheDuration => TimeSpan.FromMinutes(5);
}

/// <summary>
///     Query to search accounts by nickname pattern with pagination.
/// </summary>
/// <param name="Query">The search query term.</param>
/// <param name="Page">The 1-based page index.</param>
/// <param name="PageSize">The page size.</param>
public sealed record SearchAccountsQuery(string? Query, int Page = 1, int PageSize = 50)
    : IRequest<IReadOnlyList<AccountDto>>;

/// <summary>
///     Query to retrieve an account by exact in-game nickname.
/// </summary>
/// <param name="Ign">The in-game nickname to look up.</param>
public sealed record GetAccountByIgnQuery(string Ign)
    : IRequest<AccountDto?>, ICacheableQuery<AccountDto?>
{
    /// <inheritdoc />
    public string CacheKey => $"cache:account:ign:{Ign.Trim().ToLowerInvariant()}";

    /// <inheritdoc />
    public TimeSpan CacheDuration => TimeSpan.FromMinutes(2);
}

/// <summary>
///     Wave 3B: Lightweight read-optimized player match history row projected directly via Dapper SQL.
/// </summary>
public sealed record PlayerMatchHistoryProjectionDto(
    int MatchId,
    int AccountId,
    string AccountName,
    string HeroIdentifier,
    string Map,
    string GameMode,
    int TimePlayedSeconds,
    int Team,
    bool Won,
    int Kills,
    int Deaths,
    int Assists,
    int HeroDamage,
    int Actions,
    bool IsMvp,
    DateTimeOffset TimestampRecordedUtc);

/// <summary>
///     Wave 3B: High-throughput Dapper read projection query for <c>GET /api/stats/player/{accountId}/history</c>.
/// </summary>
public sealed record GetPlayerMatchHistoryProjectionQuery(int AccountId, int Limit = 20)
    : IRequest<IReadOnlyList<PlayerMatchHistoryProjectionDto>>, ICacheableQuery<IReadOnlyList<PlayerMatchHistoryProjectionDto>>
{
    /// <inheritdoc />
    public string CacheKey => $"cache:stats:player:history:proj:{AccountId}:l{Limit}";

    /// <inheritdoc />
    public TimeSpan CacheDuration => TimeSpan.FromSeconds(45);
}
