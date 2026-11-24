// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using NEXUS.Contracts.Events;
using MediatR;
using MERRICK.DatabaseContext.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.CQRS.Admin;
using NEXUS.Contracts.CQRS.Admin.Users;

namespace ZORGATH.WebPortal.API.Features.Admin.Users;

/// <summary>
/// In-process MediatR dispatcher for AdminUser handlers used in integration testing and fallback execution.
/// </summary>
public sealed class LocalAdminUserMediator(
    MerrickContext db,
    IEventBus? eventBus = null,
    TimeProvider? timeProvider = null) : IMediator
{
    private readonly GetAdminUserSummaryHandler _summaryHandler = new(db, NullLogger<GetAdminUserSummaryHandler>.Instance);
    private readonly SearchAdminUsersHandler _searchHandler = new(db, NullLogger<SearchAdminUsersHandler>.Instance);
    private readonly GetAdminUserByIdHandler _getByIdHandler = new(db, NullLogger<GetAdminUserByIdHandler>.Instance);
    private readonly SetUserRoleHandler _setUserRoleHandler = new(db, NullLogger<SetUserRoleHandler>.Instance);
    private readonly SetAccountRoleHandler _setAccountRoleHandler = new(db, NullLogger<SetAccountRoleHandler>.Instance, eventBus, timeProvider);
    private readonly ResetHostPasswordHandler _resetPassHandler = new(db, NullLogger<ResetHostPasswordHandler>.Instance);
    private readonly ResetHostAuthTokenHandler _resetTokenHandler = new(db, NullLogger<ResetHostAuthTokenHandler>.Instance);
    private readonly UpdateUserEconomyHandler _updateEcoHandler = new(db, NullLogger<UpdateUserEconomyHandler>.Instance, timeProvider);
    private readonly AdjustUserBalanceHandler _adjustBalHandler = new(db, NullLogger<AdjustUserBalanceHandler>.Instance, timeProvider);
    private readonly PromoteUserToStaffHandler _promoteStaffHandler = new(db, NullLogger<PromoteUserToStaffHandler>.Instance);
    private readonly PromoteUserToHostHandler _promoteHostHandler = new(db, NullLogger<PromoteUserToHostHandler>.Instance);
    private readonly RevertUserToStandardHandler _revertHandler = new(db, NullLogger<RevertUserToStandardHandler>.Instance);

    public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        if (request is GetAdminUserSummaryQuery q1 && typeof(TResponse) == typeof(UserSummaryStatsDto))
            return (Task<TResponse>)(object)_summaryHandler.Handle(q1, cancellationToken);
        if (request is SearchAdminUsersQuery q2 && typeof(TResponse) == typeof(List<AdminUserDetailDto>))
            return (Task<TResponse>)(object)_searchHandler.Handle(q2, cancellationToken);
        if (request is GetAdminUserByIdQuery q3 && typeof(TResponse) == typeof(AdminUserDetailDto))
            return (Task<TResponse>)(object)_getByIdHandler.Handle(q3, cancellationToken)!;
        if (request is SetUserRoleCommand c1 && typeof(TResponse) == typeof(AdminActionResultDto))
            return (Task<TResponse>)(object)_setUserRoleHandler.Handle(c1, cancellationToken);
        if (request is SetAccountRoleCommand c2 && typeof(TResponse) == typeof(AdminActionResultDto))
            return (Task<TResponse>)(object)_setAccountRoleHandler.Handle(c2, cancellationToken);
        if (request is ResetHostPasswordCommand c3 && typeof(TResponse) == typeof(ResetCredentialResponse))
            return (Task<TResponse>)(object)_resetPassHandler.Handle(c3, cancellationToken)!;
        if (request is ResetHostAuthTokenCommand c4 && typeof(TResponse) == typeof(ResetCredentialResponse))
            return (Task<TResponse>)(object)_resetTokenHandler.Handle(c4, cancellationToken)!;
        if (request is UpdateUserEconomyCommand c5 && typeof(TResponse) == typeof(AdminActionResultDto))
            return (Task<TResponse>)(object)_updateEcoHandler.Handle(c5, cancellationToken);
        if (request is AdjustUserBalanceCommand c6 && typeof(TResponse) == typeof(AdminActionResultDto))
            return (Task<TResponse>)(object)_adjustBalHandler.Handle(c6, cancellationToken);
        if (request is PromoteUserToStaffCommand c7 && typeof(TResponse) == typeof(AdminActionResultDto))
            return (Task<TResponse>)(object)_promoteStaffHandler.Handle(c7, cancellationToken);
        if (request is PromoteUserToHostCommand c8 && typeof(TResponse) == typeof(AdminActionResultDto))
            return (Task<TResponse>)(object)_promoteHostHandler.Handle(c8, cancellationToken);
        if (request is RevertUserToStandardCommand c9 && typeof(TResponse) == typeof(AdminActionResultDto))
            return (Task<TResponse>)(object)_revertHandler.Handle(c9, cancellationToken);

        throw new NotSupportedException($"LocalAdminUserMediator does not support request type {request.GetType().Name}");
    }

    public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => throw new NotSupportedException();
    public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
}
