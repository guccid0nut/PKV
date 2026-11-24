// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;
using MERRICK.DatabaseContext.Persistence;
using MERRICK.DatabaseContext.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.Clients;
using NEXUS.Contracts.CQRS.PlayerStats;
using NEXUS.Contracts.Data;
using NEXUS.Contracts.DTOs;
using NEXUS.Contracts.Telemetry;
using Kongor.Core.Services.Session;
using ZORGATH.WebPortal.API.Services;
using ZORGATH.WebPortal.API.Services.Stats;

namespace ZORGATH.WebPortal.API.Features.PlayerStats;

/// <summary>
/// In-process MediatR dispatcher for PlayerStats queries and commands used in integration tests and fallback execution.
/// </summary>
public sealed class LocalPlayerStatsMediator(
    MerrickContext db,
    IMatchHistoryRepository matchRepo,
    IAccountRepository accountRepo,
    IRedisMatchSessionCacheService? sessionCache = null,
    TimeProvider? timeProvider = null) : IMediator
{
    private readonly GetPlayerProfileHandler _getPlayerProfile = new(db, NullLogger<GetPlayerProfileHandler>.Instance);
    private readonly GetLeaderboardHandler _getLeaderboard = new(db, NullLogger<GetLeaderboardHandler>.Instance);
    private readonly GetHostLeaderboardHandler _getHostLeaderboard = new(db, NullLogger<GetHostLeaderboardHandler>.Instance);
    private readonly GetAccountStatisticsHandler _getAccountStatistics = new(matchRepo);
    private readonly GetTopRankedAccountsHandler _getTopRanked = new(matchRepo);
    private readonly GetRecentMatchesHandler _getRecentMatches = new(matchRepo);
    private readonly GetAccountMatchesHandler _getAccountMatches = new(matchRepo);
    private readonly GetMatchDetailsHandler _getMatchDetails = new(matchRepo);
    private readonly GetMatchPlayerStatsHandler _getMatchPlayerStats = new(matchRepo);
    private readonly GetRecentPlayerStatsHandler _getRecentPlayerStats = new(matchRepo);
    private readonly GetSubmittedMatchIdsHandler _getSubmittedMatchIds = new(db);
    private readonly GetTotalMatchesCountHandler _getTotalMatchesCount = new(db);
    private readonly SearchAccountsHandler _searchAccounts = new(accountRepo);
    private readonly GetAccountByIgnHandler _getAccountByIgn = new(accountRepo);
    private readonly GetMatchEvidenceHandler _getMatchEvidence = new(db);
    private readonly SaveMatchEvidenceHandler _saveMatchEvidence = new(db, timeProvider);
    private readonly GetServerHostsHandler _getServerHosts = new(db);
    private readonly GetAllHeroDetailsHandler _getAllHeroDetails = new(db);
    private readonly GetHeroDetailHandler _getHeroDetail = new(db);
    private readonly GetAllHeroItemsHandler _getAllHeroItems = new(db);
    private readonly SubmitPlayerReportHandler _submitReport = new(db, timeProvider);
    private readonly GetApprovedStreamersHandler _getApprovedStreamers = new(db);
    private readonly GetLiveClusterTelemetryHandler _getLiveClusterTelemetry = new(db, timeProvider);
    private readonly GetLiveMatchTelemetryHandler _getLiveMatchTelemetry = new(sessionCache);
    private readonly GetMatchCustomStatsHandler _getMatchCustomStats = new(new CustomMapStatsRepository(db));
    private readonly GetPlayerMapStatsHandler _getPlayerMapStats = new(new CustomMapStatsRepository(db));
    private readonly GetPlayerMapsOverviewHandler _getPlayerMapsOverview = new(new CustomMapStatsRepository(db));
    private readonly GetPlayerMatchHistoryProjectionHandler _getPlayerMatchHistoryProjection = new(
        new DapperMatchStatsReadProjectionService(db, NullLogger<DapperMatchStatsReadProjectionService>.Instance));
    private readonly GetClanDirectoryHandler _getClanDirectory = new(db, NullLogger<GetClanDirectoryHandler>.Instance, timeProvider);
    private readonly GetClanDetailHandler _getClanDetail = new(db, null, NullLogger<GetClanDetailHandler>.Instance, timeProvider);

    public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        if (request is GetPlayerProfileQuery q1 && typeof(TResponse) == typeof(PlayerProfileSummaryDto))
            return (Task<TResponse>)(object)_getPlayerProfile.Handle(q1, cancellationToken)!;
        if (request is GetLeaderboardQuery q2 && typeof(TResponse) == typeof(LeaderboardResultDto))
            return (Task<TResponse>)(object)_getLeaderboard.Handle(q2, cancellationToken);
        if (request is GetHostLeaderboardQuery q3 && typeof(TResponse) == typeof(IReadOnlyList<HostLeaderboardEntryDto>))
            return (Task<TResponse>)(object)_getHostLeaderboard.Handle(q3, cancellationToken);
        if (request is GetAccountStatisticsQuery q4 && typeof(TResponse) == typeof(AccountStatisticsDto))
            return (Task<TResponse>)(object)_getAccountStatistics.Handle(q4, cancellationToken)!;
        if (request is GetTopRankedAccountsQuery q5 && typeof(TResponse) == typeof(IReadOnlyList<AccountStatisticsDto>))
            return (Task<TResponse>)(object)_getTopRanked.Handle(q5, cancellationToken);
        if (request is GetRecentMatchesQuery q6 && typeof(TResponse) == typeof(IReadOnlyList<MatchStatisticsDto>))
            return (Task<TResponse>)(object)_getRecentMatches.Handle(q6, cancellationToken);
        if (request is GetAccountMatchesQuery q7 && typeof(TResponse) == typeof(IReadOnlyList<MatchStatisticsDto>))
            return (Task<TResponse>)(object)_getAccountMatches.Handle(q7, cancellationToken);
        if (request is GetMatchDetailsQuery q8 && typeof(TResponse) == typeof(MatchStatisticsDto))
            return (Task<TResponse>)(object)_getMatchDetails.Handle(q8, cancellationToken)!;
        if (request is GetMatchPlayerStatsQuery q9 && typeof(TResponse) == typeof(IReadOnlyList<PlayerStatisticsDto>))
            return (Task<TResponse>)(object)_getMatchPlayerStats.Handle(q9, cancellationToken);
        if (request is GetRecentPlayerStatsQuery q10 && typeof(TResponse) == typeof(IReadOnlyList<PlayerStatisticsDto>))
            return (Task<TResponse>)(object)_getRecentPlayerStats.Handle(q10, cancellationToken);
        if (request is GetSubmittedMatchIdsQuery q11 && typeof(TResponse) == typeof(List<int>))
            return (Task<TResponse>)(object)_getSubmittedMatchIds.Handle(q11, cancellationToken);
        if (request is GetTotalMatchesCountQuery q12 && typeof(TResponse) == typeof(int))
            return (Task<TResponse>)(object)_getTotalMatchesCount.Handle(q12, cancellationToken);
        if (request is SearchAccountsQuery q13 && typeof(TResponse) == typeof(IReadOnlyList<AccountDto>))
            return (Task<TResponse>)(object)_searchAccounts.Handle(q13, cancellationToken);
        if (request is GetAccountByIgnQuery q14 && typeof(TResponse) == typeof(AccountDto))
            return (Task<TResponse>)(object)_getAccountByIgn.Handle(q14, cancellationToken)!;
        if (request is GetMatchEvidenceQuery q15 && typeof(TResponse) == typeof(MatchEvidenceDto))
            return (Task<TResponse>)(object)_getMatchEvidence.Handle(q15, cancellationToken)!;
        if (request is SaveMatchEvidenceCommand c1 && typeof(TResponse) == typeof(bool))
            return (Task<TResponse>)(object)_saveMatchEvidence.Handle(c1, cancellationToken);
        if (request is GetServerHostsQuery q16 && typeof(TResponse) == typeof(List<ServerHostDto>))
            return (Task<TResponse>)(object)_getServerHosts.Handle(q16, cancellationToken);
        if (request is GetAllHeroDetailsQuery q17 && typeof(TResponse) == typeof(List<HeroDetailDto>))
            return (Task<TResponse>)(object)_getAllHeroDetails.Handle(q17, cancellationToken);
        if (request is GetHeroDetailQuery q18 && typeof(TResponse) == typeof(HeroDetailDto))
            return (Task<TResponse>)(object)_getHeroDetail.Handle(q18, cancellationToken)!;
        if (request is GetAllHeroItemsQuery q19 && typeof(TResponse) == typeof(List<HeroItemDto>))
            return (Task<TResponse>)(object)_getAllHeroItems.Handle(q19, cancellationToken);
        if (request is SubmitPlayerReportCommand c2 && typeof(TResponse) == typeof(bool))
            return (Task<TResponse>)(object)_submitReport.Handle(c2, cancellationToken);
        if (request is GetApprovedStreamersQuery q20 && typeof(TResponse) == typeof(List<string>))
            return (Task<TResponse>)(object)_getApprovedStreamers.Handle(q20, cancellationToken);
        if (request is GetLiveClusterTelemetryQuery q21 && typeof(TResponse) == typeof(LiveTelemetryDataDto))
            return (Task<TResponse>)(object)_getLiveClusterTelemetry.Handle(q21, cancellationToken);
        if (request is GetLiveMatchTelemetryQuery q22 && typeof(TResponse) == typeof(LiveServerTickPayloadDto))
            return (Task<TResponse>)(object)_getLiveMatchTelemetry.Handle(q22, cancellationToken)!;
        if (request is GetMatchCustomStatsQuery q23 && typeof(TResponse) == typeof(CustomMatchStatsResponseDto))
            return (Task<TResponse>)(object)_getMatchCustomStats.Handle(q23, cancellationToken)!;
        if (request is GetPlayerMapStatsQuery q24 && typeof(TResponse) == typeof(PlayerMapStatsResponseDto))
            return (Task<TResponse>)(object)_getPlayerMapStats.Handle(q24, cancellationToken)!;
        if (request is GetPlayerMapsOverviewQuery q25 && typeof(TResponse) == typeof(IReadOnlyList<PlayerMapOverviewItemDto>))
            return (Task<TResponse>)(object)_getPlayerMapsOverview.Handle(q25, cancellationToken);
        if (request is GetPlayerMatchHistoryProjectionQuery q26 && typeof(TResponse) == typeof(IReadOnlyList<PlayerMatchHistoryProjectionDto>))
            return (Task<TResponse>)(object)_getPlayerMatchHistoryProjection.Handle(q26, cancellationToken);
        if (request is GetClanDirectoryQuery q27 && typeof(TResponse) == typeof(ClanDirectoryResultDto))
            return (Task<TResponse>)(object)_getClanDirectory.Handle(q27, cancellationToken);
        if (request is GetClanDetailQuery q28 && typeof(TResponse) == typeof(ClanDetailDto))
            return (Task<TResponse>)(object)_getClanDetail.Handle(q28, cancellationToken)!;

        throw new NotSupportedException($"LocalPlayerStatsMediator does not support request type {request.GetType().Name}");
    }

    public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task Publish(object notification, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification =>
        throw new NotSupportedException();

    public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
        throw new NotSupportedException();
}
