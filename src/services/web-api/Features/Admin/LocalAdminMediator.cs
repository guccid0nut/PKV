// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using NEXUS.Contracts.Events;
using MediatR;
using MERRICK.DatabaseContext.Persistence;
using MERRICK.DatabaseContext.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.Clients;
using NEXUS.Contracts.CQRS.Admin;
using NEXUS.Contracts.CQRS.Admin.Accounts;
using NEXUS.Contracts.CQRS.Admin.Applications;
using NEXUS.Contracts.CQRS.Admin.Inventory;
using NEXUS.Contracts.CQRS.Admin.Moderation;
using NEXUS.Contracts.CQRS.Admin.Reports;
using NEXUS.Contracts.Data;
using ZORGATH.WebPortal.API.Features.Admin.Accounts;
using ZORGATH.WebPortal.API.Features.Admin.Applications;
using ZORGATH.WebPortal.API.Features.Admin.Inventory;
using ZORGATH.WebPortal.API.Features.Admin.Moderation;
using ZORGATH.WebPortal.API.Features.Admin.Reports;

namespace ZORGATH.WebPortal.API.Features.Admin;

/// <summary>
/// In-process MediatR dispatcher for Admin handlers used in integration testing and fallback execution.
/// </summary>
public sealed class LocalAdminMediator(
    MerrickContext db,
    IAccountRepository accountRepo,
    IEventBus? eventBus = null,
    TimeProvider? timeProvider = null) : IMediator
{
    private readonly GetPlayerReportsHandler _getPlayerReports = new(db, NullLogger<GetPlayerReportsHandler>.Instance);
    private readonly GetPlayerReportsCountHandler _getPlayerReportsCount = new(db, NullLogger<GetPlayerReportsCountHandler>.Instance);
    private readonly GetPlayerReportByIdHandler _getPlayerReportById = new(db);
    private readonly DismissReportHandler _dismissReport = new(db, NullLogger<DismissReportHandler>.Instance, eventBus, timeProvider);
    private readonly ActionReportHandler _actionReport = new(db, NullLogger<ActionReportHandler>.Instance, eventBus, timeProvider);
    private readonly DirectPunishAccountHandler _directPunish = new(db, eventBus, timeProvider);
    private readonly BanAccountHandler _banAccount = new(db, accountRepo, NullLogger<BanAccountHandler>.Instance, eventBus, timeProvider);
    private readonly UnbanAccountHandler _unbanAccount = new(accountRepo, NullLogger<UnbanAccountHandler>.Instance);
    private readonly MuteAccountHandler _muteAccount = new(db, NullLogger<MuteAccountHandler>.Instance, eventBus, timeProvider);
    private readonly UnmuteAccountHandler _unmuteAccount = new(db, NullLogger<UnmuteAccountHandler>.Instance, eventBus);
    private readonly GetHostApplicationsHandler _getHostApplications = new(db);
    private readonly ApproveHostApplicationHandler _approveHostApp = new(db, timeProvider);
    private readonly RejectHostApplicationHandler _rejectHostApp = new(db, timeProvider);
    private readonly RevokeHostApplicationHandler _revokeHostApp = new(db, timeProvider);
    private readonly GetStreamerApplicationsHandler _getStreamerApplications = new(db);
    private readonly ApproveStreamerApplicationHandler _approveStreamerApp = new(db, timeProvider);
    private readonly RejectStreamerApplicationHandler _rejectStreamerApp = new(db, timeProvider);
    private readonly RevokeStreamerApplicationHandler _revokeStreamerApp = new(db, timeProvider);
    private readonly GetRecentAccountsHandler _getRecentAccounts = new(db);
    private readonly GetAccountDetailHandler _getAccountDetail = new(db);
    private readonly AddInventoryItemHandler _addInventory = new(db, timeProvider);
    private readonly RemoveInventoryItemHandler _removeInventory = new(db);
    private readonly ResetInventoryHandler _resetInventory = new(db);

    public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        if (request is GetPlayerReportsQuery q1 && typeof(TResponse) == typeof(IReadOnlyList<PlayerReportDto>))
            return (Task<TResponse>)(object)_getPlayerReports.Handle(q1, cancellationToken);
        if (request is GetPlayerReportsCountQuery q2 && typeof(TResponse) == typeof(int))
            return (Task<TResponse>)(object)_getPlayerReportsCount.Handle(q2, cancellationToken);
        if (request is GetPlayerReportByIdQuery q3 && typeof(TResponse) == typeof(PlayerReportDto))
            return (Task<TResponse>)(object)_getPlayerReportById.Handle(q3, cancellationToken)!;
        if (request is DismissReportCommand c1 && typeof(TResponse) == typeof(AdminActionResultDto))
            return (Task<TResponse>)(object)_dismissReport.Handle(c1, cancellationToken);
        if (request is ActionReportCommand c2 && typeof(TResponse) == typeof(AdminActionResultDto))
            return (Task<TResponse>)(object)_actionReport.Handle(c2, cancellationToken);
        if (request is DirectPunishAccountCommand c3 && typeof(TResponse) == typeof(AdminActionResultDto))
            return (Task<TResponse>)(object)_directPunish.Handle(c3, cancellationToken);
        if (request is BanAccountCommand c4 && typeof(TResponse) == typeof(AdminActionResultDto))
            return (Task<TResponse>)(object)_banAccount.Handle(c4, cancellationToken);
        if (request is UnbanAccountCommand c5 && typeof(TResponse) == typeof(AdminActionResultDto))
            return (Task<TResponse>)(object)_unbanAccount.Handle(c5, cancellationToken);
        if (request is MuteAccountCommand c6 && typeof(TResponse) == typeof(AdminActionResultDto))
            return (Task<TResponse>)(object)_muteAccount.Handle(c6, cancellationToken);
        if (request is UnmuteAccountCommand c7 && typeof(TResponse) == typeof(AdminActionResultDto))
            return (Task<TResponse>)(object)_unmuteAccount.Handle(c7, cancellationToken);
        if (request is GetHostApplicationsQuery q4 && typeof(TResponse) == typeof(List<HostApplicationDto>))
            return (Task<TResponse>)(object)_getHostApplications.Handle(q4, cancellationToken);
        if (request is ApproveHostApplicationCommand c8 && typeof(TResponse) == typeof(AdminActionResultDto))
            return (Task<TResponse>)(object)_approveHostApp.Handle(c8, cancellationToken);
        if (request is RejectHostApplicationCommand c9 && typeof(TResponse) == typeof(AdminActionResultDto))
            return (Task<TResponse>)(object)_rejectHostApp.Handle(c9, cancellationToken);
        if (request is RevokeHostApplicationCommand c10 && typeof(TResponse) == typeof(AdminActionResultDto))
            return (Task<TResponse>)(object)_revokeHostApp.Handle(c10, cancellationToken);
        if (request is GetStreamerApplicationsQuery q5 && typeof(TResponse) == typeof(List<StreamerApplicationDto>))
            return (Task<TResponse>)(object)_getStreamerApplications.Handle(q5, cancellationToken);
        if (request is ApproveStreamerApplicationCommand c11 && typeof(TResponse) == typeof(AdminActionResultDto))
            return (Task<TResponse>)(object)_approveStreamerApp.Handle(c11, cancellationToken);
        if (request is RejectStreamerApplicationCommand c12 && typeof(TResponse) == typeof(AdminActionResultDto))
            return (Task<TResponse>)(object)_rejectStreamerApp.Handle(c12, cancellationToken);
        if (request is RevokeStreamerApplicationCommand c13 && typeof(TResponse) == typeof(AdminActionResultDto))
            return (Task<TResponse>)(object)_revokeStreamerApp.Handle(c13, cancellationToken);
        if (request is GetRecentAccountsQuery q6 && typeof(TResponse) == typeof(List<AccountDto>))
            return (Task<TResponse>)(object)_getRecentAccounts.Handle(q6, cancellationToken);
        if (request is GetAccountDetailQuery q7 && typeof(TResponse) == typeof(AccountDto))
            return (Task<TResponse>)(object)_getAccountDetail.Handle(q7, cancellationToken)!;
        if (request is AddInventoryItemCommand c14 && typeof(TResponse) == typeof(AdminActionResultDto))
            return (Task<TResponse>)(object)_addInventory.Handle(c14, cancellationToken);
        if (request is RemoveInventoryItemCommand c15 && typeof(TResponse) == typeof(AdminActionResultDto))
            return (Task<TResponse>)(object)_removeInventory.Handle(c15, cancellationToken);
        if (request is ResetInventoryCommand c16 && typeof(TResponse) == typeof(AdminActionResultDto))
            return (Task<TResponse>)(object)_resetInventory.Handle(c16, cancellationToken);

        throw new NotSupportedException($"LocalAdminMediator does not support request type {request.GetType().Name}");
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
