// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;
using MERRICK.DatabaseContext.Persistence;
using NEXUS.Contracts.Clients;
using NEXUS.Contracts.CQRS.Lore;

namespace ZORGATH.WebPortal.API.Features.Lore;

/// <summary>
/// In-process MediatR dispatcher for Lore handlers used in integration testing.
/// </summary>
public sealed class LocalLoreMediator(
    MerrickContext db,
    TimeProvider? timeProvider = null) : IMediator
{
    private readonly GetArcsHandler _arcsHandler = new(db);
    private readonly GetArcBySlugHandler _arcBySlugHandler = new(db);
    private readonly GetComicsHandler _comicsHandler = new(db);
    private readonly GetComicDetailHandler _comicDetailHandler = new(db);
    private readonly RateComicHandler _rateHandler = new(db, timeProvider);
    private readonly GetUserRatingHandler _userRatingHandler = new(db);
    private readonly GetStaffMetricsHandler _metricsHandler = new(db);
    private readonly SaveComicHandler _saveComicHandler = new(db, timeProvider);
    private readonly DeleteComicHandler _deleteComicHandler = new(db);
    private readonly SaveArcHandler _saveArcHandler = new(db, timeProvider);
    private readonly DeleteArcHandler _deleteArcHandler = new(db);

    public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        if (request is GetArcsQuery q1 && typeof(TResponse) == typeof(List<LoreArcDto>))
            return (Task<TResponse>)(object)_arcsHandler.Handle(q1, cancellationToken);
        if (request is GetArcBySlugQuery q2 && typeof(TResponse) == typeof(LoreArcDto))
            return (Task<TResponse>)(object)_arcBySlugHandler.Handle(q2, cancellationToken)!;
        if (request is GetComicsQuery q3 && typeof(TResponse) == typeof(List<LoreComicSummaryDto>))
            return (Task<TResponse>)(object)_comicsHandler.Handle(q3, cancellationToken);
        if (request is GetComicDetailQuery q4 && typeof(TResponse) == typeof(LoreComicDetailDto))
            return (Task<TResponse>)(object)_comicDetailHandler.Handle(q4, cancellationToken)!;
        if (request is RateComicCommand c1 && typeof(TResponse) == typeof(RateComicResult))
            return (Task<TResponse>)(object)_rateHandler.Handle(c1, cancellationToken);
        if (request is GetUserRatingQuery q5 && typeof(TResponse) == typeof(int?))
            return (Task<TResponse>)(object)_userRatingHandler.Handle(q5, cancellationToken);
        if (request is GetStaffMetricsQuery q6 && typeof(TResponse) == typeof(LoreRatingMetricDto))
            return (Task<TResponse>)(object)_metricsHandler.Handle(q6, cancellationToken);
        if (request is SaveComicCommand c2 && typeof(TResponse) == typeof(LoreComicSummaryDto))
            return (Task<TResponse>)(object)_saveComicHandler.Handle(c2, cancellationToken)!;
        if (request is DeleteComicCommand c3 && typeof(TResponse) == typeof(LoreActionResult))
            return (Task<TResponse>)(object)_deleteComicHandler.Handle(c3, cancellationToken);
        if (request is SaveArcCommand c4 && typeof(TResponse) == typeof(LoreArcDto))
            return (Task<TResponse>)(object)_saveArcHandler.Handle(c4, cancellationToken)!;
        if (request is DeleteArcCommand c5 && typeof(TResponse) == typeof(LoreActionResult))
            return (Task<TResponse>)(object)_deleteArcHandler.Handle(c5, cancellationToken);

        throw new NotSupportedException($"LocalLoreMediator does not support {request.GetType().Name}");
    }

    public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => throw new NotSupportedException();
    public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
}
