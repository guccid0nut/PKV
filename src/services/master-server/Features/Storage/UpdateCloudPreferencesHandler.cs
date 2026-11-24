// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;
using Microsoft.Extensions.Logging;
using NEXUS.Contracts.CQRS.Storage;
using NEXUS.Contracts.Data;

namespace KONGOR.MasterServer.Features.Storage;

/// <summary>
///     MediatR command handler for updating cloud storage preferences (<see cref="UpdateCloudPreferencesCommand"/>).
/// </summary>
public sealed class UpdateCloudPreferencesHandler(
    IAccountRepository accountRepository,
    ILogger<UpdateCloudPreferencesHandler>? logger = null,
    TimeProvider? timeProvider = null)
    : IRequestHandler<UpdateCloudPreferencesCommand, UpdateCloudPreferencesResultDto>
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<UpdateCloudPreferencesResultDto> Handle(
        UpdateCloudPreferencesCommand request,
        CancellationToken cancellationToken)
    {
        logger?.LogInformation(
            "[{Timestamp}] [UpdateCloudPreferencesHandler] Updating preferences for Account ID: {AccountId}, UseCloud: {UseCloud}, AutoUpload: {AutoUpload}",
            _timeProvider.GetUtcNow(),
            request.AccountId,
            request.UseCloud,
            request.AutoUpload);

        await accountRepository.UpdateCloudStoragePreferencesAsync(
            request.AccountId,
            request.UseCloud,
            request.AutoUpload,
            null,
            cancellationToken);

        return new UpdateCloudPreferencesResultDto(true);
    }
}
