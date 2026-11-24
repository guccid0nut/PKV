// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;
using MERRICK.DatabaseContext.Persistence;
using NEXUS.Contracts.Clients;
using NEXUS.Contracts.CQRS.Guides;

namespace ZORGATH.WebPortal.API.Features.Guides;

/// <summary>
/// In-process MediatR dispatcher for Guide queries and commands used in integration tests.
/// </summary>
public sealed class LocalGuideMediator(
    MerrickContext db,
    TimeProvider? timeProvider = null) : IMediator
{
    private readonly SearchGuidesHandler _searchHandler = new(db);
    private readonly GetGuideByIdHandler _getByIdHandler = new(db);
    private readonly GetGuidesForHeroHandler _getForHeroHandler = new(db);
    private readonly SaveGuideHandler _saveHandler = new(db, timeProvider);
    private readonly DeleteGuideHandler _deleteHandler = new(db);
    private readonly VoteGuideHandler _voteHandler = new(db);
    private readonly ToggleGuidePrivacyHandler _togglePrivacyHandler = new(db, timeProvider);
    private readonly ToggleGuideOfficialHandler _toggleOfficialHandler = new(db, timeProvider);
    private readonly ToggleDefaultGuideHandler _toggleDefaultHandler = new(db);
    private readonly ToggleFavoriteGuideHandler _toggleFavoriteHandler = new(db);
    private readonly GetHeroDifficultyVotesHandler _getDifficultyVotesHandler = new(db);
    private readonly VoteHeroDifficultyHandler _voteDifficultyHandler = new(db, timeProvider);

    public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        if (request is SearchGuidesQuery q1 && typeof(TResponse) == typeof(GuideSearchResultDto))
            return (Task<TResponse>)(object)_searchHandler.Handle(q1, cancellationToken);
        if (request is GetGuideByIdQuery q2 && typeof(TResponse) == typeof(HeroGuideDto))
            return (Task<TResponse>)(object)_getByIdHandler.Handle(q2, cancellationToken)!;
        if (request is GetGuidesForHeroQuery q3 && typeof(TResponse) == typeof(List<HeroGuideDto>))
            return (Task<TResponse>)(object)_getForHeroHandler.Handle(q3, cancellationToken);
        if (request is SaveGuideCommand c1 && typeof(TResponse) == typeof(SaveGuideCommandResponse))
            return (Task<TResponse>)(object)_saveHandler.Handle(c1, cancellationToken);
        if (request is DeleteGuideCommand c2 && typeof(TResponse) == typeof(DeleteGuideCommandResponse))
            return (Task<TResponse>)(object)_deleteHandler.Handle(c2, cancellationToken);
        if (request is VoteGuideCommand c3 && typeof(TResponse) == typeof(VoteGuideResultDto))
            return (Task<TResponse>)(object)_voteHandler.Handle(c3, cancellationToken)!;
        if (request is ToggleGuidePrivacyCommand c4 && typeof(TResponse) == typeof(TogglePrivacyCommandResponse))
            return (Task<TResponse>)(object)_togglePrivacyHandler.Handle(c4, cancellationToken);
        if (request is ToggleGuideOfficialCommand c5 && typeof(TResponse) == typeof(ToggleOfficialResultDto))
            return (Task<TResponse>)(object)_toggleOfficialHandler.Handle(c5, cancellationToken)!;
        if (request is ToggleDefaultGuideCommand c6 && typeof(TResponse) == typeof(ToggleDefaultGuideCommandResponse))
            return (Task<TResponse>)(object)_toggleDefaultHandler.Handle(c6, cancellationToken);
        if (request is ToggleFavoriteGuideCommand c7 && typeof(TResponse) == typeof(ToggleFavoriteGuideCommandResponse))
            return (Task<TResponse>)(object)_toggleFavoriteHandler.Handle(c7, cancellationToken);
        if (request is GetHeroDifficultyVotesQuery q4 && typeof(TResponse) == typeof(List<HeroDifficultyVoteDto>))
            return (Task<TResponse>)(object)_getDifficultyVotesHandler.Handle(q4, cancellationToken);
        if (request is VoteHeroDifficultyCommand c8 && typeof(TResponse) == typeof(HeroDifficultyVoteResultDto))
            return (Task<TResponse>)(object)_voteDifficultyHandler.Handle(c8, cancellationToken);

        throw new NotSupportedException($"LocalGuideMediator does not support {request.GetType().Name}");
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
