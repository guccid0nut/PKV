// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;
using MERRICK.DatabaseContext.Entities.Core;
using Microsoft.Extensions.Logging;
using NEXUS.Contracts.CQRS.Storage;
using NEXUS.Contracts.Data;

namespace KONGOR.MasterServer.Features.Storage;

/// <summary>
///     MediatR query handler for retrieving cloud storage enrollment information (<see cref="GetUserEnrollmentQuery"/>).
/// </summary>
public sealed class GetUserEnrollmentHandler(
    IAccountRepository accountRepository,
    ILogger<GetUserEnrollmentHandler>? logger = null,
    TimeProvider? timeProvider = null)
    : IRequestHandler<GetUserEnrollmentQuery, UserEnrollmentResultDto?>
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<UserEnrollmentResultDto?> Handle(
        GetUserEnrollmentQuery request,
        CancellationToken cancellationToken)
    {
        logger?.LogInformation(
            "[{Timestamp}] [GetUserEnrollmentHandler] Fetching enrollment for Account ID: {AccountId}",
            _timeProvider.GetUtcNow(),
            request.AccountId);

        Account? account = await accountRepository.GetByIdAsync(request.AccountId, cancellationToken);
        if (account is null)
        {
            logger?.LogWarning(
                "[{Timestamp}] [GetUserEnrollmentHandler] Account not found for Account ID: {AccountId}",
                _timeProvider.GetUtcNow(),
                request.AccountId);
            return null;
        }

        return new UserEnrollmentResultDto(
            account.ID.ToString(),
            account.UseCloud ? "1" : "0",
            account.AutomaticCloudUpload ? "1" : "0",
            account.BackupLastUpdatedTime?.ToString("yyyy-MM-dd HH:mm:ss") ??
            DateTimeOffset.MinValue.ToString("yyyy-MM-dd HH:mm:ss"));
    }
}
