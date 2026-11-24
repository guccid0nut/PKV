// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;
using Microsoft.EntityFrameworkCore;
using MERRICK.DatabaseContext.Entities.Game;
using MERRICK.DatabaseContext.Entities.Statistics;
using MERRICK.DatabaseContext.Extensions;
using MERRICK.DatabaseContext.Persistence;
using NEXUS.Contracts.Clients;
using NEXUS.Contracts.CQRS.PlayerStats;
using NEXUS.Contracts.Data;
using ZORGATH.WebPortal.API.Services.Stats;

namespace ZORGATH.WebPortal.API.Features.PlayerStats;

/// <summary>
///     Handler for retrieving gameplay statistics for a specific account.
/// </summary>
public sealed class GetAccountStatisticsHandler(IMatchHistoryRepository matchRepo)
    : IRequestHandler<GetAccountStatisticsQuery, AccountStatisticsDto?>
{
    /// <inheritdoc />
    public async Task<AccountStatisticsDto?> Handle(GetAccountStatisticsQuery request, CancellationToken cancellationToken)
    {
        AccountStatistics? stats = await matchRepo.GetAccountStatisticsAsync(request.AccountId, cancellationToken);
        return stats?.ToDto();
    }
}

/// <summary>
///     Handler for retrieving top ranked accounts based on ladder MMR.
/// </summary>
public sealed class GetTopRankedAccountsHandler(IMatchHistoryRepository matchRepo)
    : IRequestHandler<GetTopRankedAccountsQuery, IReadOnlyList<AccountStatisticsDto>>
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<AccountStatisticsDto>> Handle(GetTopRankedAccountsQuery request, CancellationToken cancellationToken)
    {
        IReadOnlyList<AccountStatistics> top = await matchRepo.GetTopRankedAccountsAsync(request.Limit, cancellationToken);
        return top.Select(s => s.ToDto()).ToList();
    }
}

/// <summary>
///     Handler for retrieving recently completed matches across all servers.
/// </summary>
public sealed class GetRecentMatchesHandler(IMatchHistoryRepository matchRepo)
    : IRequestHandler<GetRecentMatchesQuery, IReadOnlyList<MatchStatisticsDto>>
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<MatchStatisticsDto>> Handle(GetRecentMatchesQuery request, CancellationToken cancellationToken)
    {
        IReadOnlyList<MatchStatistics> matches = await matchRepo.GetRecentMatchesAsync(request.Limit, cancellationToken);
        return matches.Select(m => m.ToDto()).ToList();
    }
}

/// <summary>
///     Handler for retrieving recent match history for a specific account.
/// </summary>
public sealed class GetAccountMatchesHandler(IMatchHistoryRepository matchRepo)
    : IRequestHandler<GetAccountMatchesQuery, IReadOnlyList<MatchStatisticsDto>>
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<MatchStatisticsDto>> Handle(GetAccountMatchesQuery request, CancellationToken cancellationToken)
    {
        IReadOnlyList<MatchStatistics> matches =
            await matchRepo.GetRecentMatchesByAccountIdAsync(request.AccountId, request.Limit, cancellationToken);
        return matches.Select(m => m.ToDto()).ToList();
    }
}

/// <summary>
///     Handler for retrieving comprehensive match details by match ID.
/// </summary>
public sealed class GetMatchDetailsHandler(IMatchHistoryRepository matchRepo)
    : IRequestHandler<GetMatchDetailsQuery, MatchStatisticsDto?>
{
    /// <inheritdoc />
    public async Task<MatchStatisticsDto?> Handle(GetMatchDetailsQuery request, CancellationToken cancellationToken)
    {
        MatchStatistics? match = await matchRepo.GetMatchByIdAsync(request.MatchId, cancellationToken);
        return match?.ToDto();
    }
}

/// <summary>
///     Handler for retrieving individual player performance statistics for all participants in a match.
/// </summary>
public sealed class GetMatchPlayerStatsHandler(IMatchHistoryRepository matchRepo)
    : IRequestHandler<GetMatchPlayerStatsQuery, IReadOnlyList<PlayerStatisticsDto>>
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<PlayerStatisticsDto>> Handle(GetMatchPlayerStatsQuery request, CancellationToken cancellationToken)
    {
        IReadOnlyList<PlayerStatistics> players =
            await matchRepo.GetPlayerStatisticsForMatchAsync(request.MatchId, cancellationToken);
        return players.Select(p => p.ToDto()).ToList();
    }
}

/// <summary>
///     Handler for retrieving recent player performance records across recent matches for an account.
/// </summary>
public sealed class GetRecentPlayerStatsHandler(IMatchHistoryRepository matchRepo)
    : IRequestHandler<GetRecentPlayerStatsQuery, IReadOnlyList<PlayerStatisticsDto>>
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<PlayerStatisticsDto>> Handle(GetRecentPlayerStatsQuery request, CancellationToken cancellationToken)
    {
        IReadOnlyList<PlayerStatistics> stats =
            await matchRepo.GetRecentPlayerStatisticsByAccountIdAsync(request.AccountId, request.Limit, cancellationToken);
        return stats.Select(s => s.ToDto()).ToList();
    }
}

/// <summary>
///     Handler for checking and filtering match IDs that are already recorded in the database.
/// </summary>
public sealed class GetSubmittedMatchIdsHandler(MerrickContext db)
    : IRequestHandler<GetSubmittedMatchIdsQuery, List<int>>
{
    /// <inheritdoc />
    public async Task<List<int>> Handle(GetSubmittedMatchIdsQuery request, CancellationToken cancellationToken)
    {
        return await db.MatchStatistics
            .AsNoTracking()
            .Where(m => request.MatchIds.Contains(m.MatchID))
            .Select(m => m.MatchID)
            .ToListAsync(cancellationToken);
    }
}

/// <summary>
///     Handler for calculating the total count of matches recorded in the system.
/// </summary>
public sealed class GetTotalMatchesCountHandler(MerrickContext db)
    : IRequestHandler<GetTotalMatchesCountQuery, int>
{
    /// <inheritdoc />
    public async Task<int> Handle(GetTotalMatchesCountQuery request, CancellationToken cancellationToken)
    {
        return await db.MatchStatistics.CountAsync(cancellationToken);
    }
}

/// <summary>
///     Handler for searching player accounts by nickname query with pagination.
/// </summary>
public sealed class SearchAccountsHandler(IAccountRepository accountRepo)
    : IRequestHandler<SearchAccountsQuery, IReadOnlyList<AccountDto>>
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<AccountDto>> Handle(SearchAccountsQuery request, CancellationToken cancellationToken)
    {
        IReadOnlyList<Account> accounts =
            await accountRepo.SearchAccountsAsync(request.Query, request.Page, request.PageSize, cancellationToken);
        return accounts.Select(a => a.ToDto()).ToList();
    }
}

/// <summary>
///     Handler for retrieving an account by exact in-game nickname.
/// </summary>
public sealed class GetAccountByIgnHandler(IAccountRepository accountRepo)
    : IRequestHandler<GetAccountByIgnQuery, AccountDto?>
{
    /// <inheritdoc />
    public async Task<AccountDto?> Handle(GetAccountByIgnQuery request, CancellationToken cancellationToken)
    {
        Account? account = await accountRepo.GetByNicknameAsync(request.Ign, cancellationToken);
        return account?.ToDto();
    }
}

/// <summary>
///     Wave 3B: Handler executing Dapper parameterized read projections for player match history.
/// </summary>
public sealed class GetPlayerMatchHistoryProjectionHandler(
    IMatchStatsReadProjectionService projectionService)
    : IRequestHandler<GetPlayerMatchHistoryProjectionQuery, IReadOnlyList<PlayerMatchHistoryProjectionDto>>
{
    /// <inheritdoc />
    public Task<IReadOnlyList<PlayerMatchHistoryProjectionDto>> Handle(
        GetPlayerMatchHistoryProjectionQuery request,
        CancellationToken cancellationToken)
    {
        return projectionService.GetPlayerMatchHistoryAsync(request.AccountId, request.Limit, cancellationToken);
    }
}
