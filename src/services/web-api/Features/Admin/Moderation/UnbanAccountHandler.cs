// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;
using NEXUS.Contracts.Data;
using Microsoft.Extensions.Logging;
using NEXUS.Contracts.CQRS.Admin;
using NEXUS.Contracts.CQRS.Admin.Moderation;

namespace ZORGATH.WebPortal.API.Features.Admin.Moderation;

public sealed class UnbanAccountHandler(
    IAccountRepository accountRepo,
    ILogger<UnbanAccountHandler> logger) : IRequestHandler<UnbanAccountCommand, AdminActionResultDto>
{
    public async Task<AdminActionResultDto> Handle(UnbanAccountCommand request, CancellationToken cancellationToken)
    {
        logger.LogInformation("[UnbanAccountHandler] Unbanning Account {AccountId}", request.AccountId);

        await accountRepo.UnbanAccountAsync(request.AccountId, cancellationToken);

        return new AdminActionResultDto(
            Success: true,
            Message: $"Account {request.AccountId} successfully unbanned.",
            AffectedId: request.AccountId);
    }
}
