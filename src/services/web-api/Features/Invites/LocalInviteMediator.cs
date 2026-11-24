// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;
using MERRICK.DatabaseContext.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.CQRS.Invites;
using NEXUS.Contracts.Data;

namespace ZORGATH.WebPortal.API.Features.Invites;

/// <summary>
/// In-process MediatR dispatcher for Invite handlers used in integration testing.
/// </summary>
public sealed class LocalInviteMediator(
    IInviteRepository inviteRepo,
    IAccountRepository accountRepo) : IMediator
{
    private readonly GenerateInviteHandler _generateHandler = new(inviteRepo, NullLogger<GenerateInviteHandler>.Instance);
    private readonly GetUserQuotaHandler _quotaHandler = new(inviteRepo);
    private readonly GetMyInvitesHandler _myInvitesHandler = new(inviteRepo);
    private readonly ValidateInviteHandler _validateHandler = new(inviteRepo);
    private readonly GetInviteOnlyModeHandler _modeHandler = new(inviteRepo);
    private readonly GetInviteSystemStatusHandler _statusHandler = new(inviteRepo);
    private readonly ToggleInviteOnlyModeHandler _toggleHandler = new(inviteRepo);
    private readonly GetInviteLineageTreeHandler _treeHandler = new(inviteRepo);
    private readonly PruneInviteSubtreeHandler _pruneHandler = new(inviteRepo, accountRepo, NullLogger<PruneInviteSubtreeHandler>.Instance);
    private readonly BootstrapInvitesHandler _bootstrapHandler = new(inviteRepo, NullLogger<BootstrapInvitesHandler>.Instance);

    public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        if (request is GenerateInviteCommand genCmd && typeof(TResponse) == typeof(GenerateInviteResult))
            return (Task<TResponse>)(object)_generateHandler.Handle(genCmd, cancellationToken);
        if (request is GetUserQuotaQuery quotaQry && typeof(TResponse) == typeof(GenerateInviteResult))
            return (Task<TResponse>)(object)_quotaHandler.Handle(quotaQry, cancellationToken);
        if (request is GetMyInvitesQuery myCmd && typeof(TResponse) == typeof(List<InviteSummaryDto>))
            return (Task<TResponse>)(object)_myInvitesHandler.Handle(myCmd, cancellationToken);
        if (request is ValidateInviteQuery valQry && typeof(TResponse) == typeof(ValidateInviteResult))
            return (Task<TResponse>)(object)_validateHandler.Handle(valQry, cancellationToken);
        if (request is GetInviteOnlyModeQuery modeQry && typeof(TResponse) == typeof(bool))
            return (Task<TResponse>)(object)_modeHandler.Handle(modeQry, cancellationToken);
        if (request is GetInviteSystemStatusQuery statQry && typeof(TResponse) == typeof(InviteSystemStatusResult))
            return (Task<TResponse>)(object)_statusHandler.Handle(statQry, cancellationToken);
        if (request is ToggleInviteOnlyModeCommand togCmd && typeof(TResponse) == typeof(ToggleInviteOnlyModeResult))
            return (Task<TResponse>)(object)_toggleHandler.Handle(togCmd, cancellationToken);
        if (request is GetInviteLineageTreeQuery treeQry && typeof(TResponse) == typeof(InviteTreeNodeResult))
            return (Task<TResponse>)(object)_treeHandler.Handle(treeQry, cancellationToken)!;
        if (request is PruneInviteSubtreeCommand pruneCmd && typeof(TResponse) == typeof(PruneInviteSubtreeResult))
            return (Task<TResponse>)(object)_pruneHandler.Handle(pruneCmd, cancellationToken);
        if (request is BootstrapInvitesCommand bootCmd && typeof(TResponse) == typeof(BootstrapInvitesResult))
            return (Task<TResponse>)(object)_bootstrapHandler.Handle(bootCmd, cancellationToken);

        throw new NotSupportedException($"LocalInviteMediator does not support request type {request.GetType().Name}");
    }

    public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => throw new NotSupportedException();
    public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
}
