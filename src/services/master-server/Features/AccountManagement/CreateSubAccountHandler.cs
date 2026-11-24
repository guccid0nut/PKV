// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;
using Microsoft.Extensions.Logging;
using NEXUS.Contracts.CQRS.AccountManagement;
using NEXUS.Contracts.Data;
using KONGOR.MasterServer.Services.Security;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Enumerations;

namespace KONGOR.MasterServer.Features.AccountManagement;

/// <summary>
///     MediatR command handler for subaccount creation (<see cref="CreateSubAccountCommand"/>).
///     Enforces account sanctions, password authentication, subaccount nickname uniqueness, and repository creation.
/// </summary>
public sealed class CreateSubAccountHandler(
    IAccountRepository accountRepository,
    IAccountPasswordVerifier passwordVerifier,
    ILogger<CreateSubAccountHandler> logger,
    TimeProvider? timeProvider = null)
    : IRequestHandler<CreateSubAccountCommand, CreateSubAccountResultDto>
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<CreateSubAccountResultDto> Handle(
        CreateSubAccountCommand request,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "[{Timestamp}] [CreateSubAccountHandler] Subaccount creation request: MainAccountId={MainAccountId}, SubNickname={SubNickname}",
            _timeProvider.GetUtcNow(),
            request.MainAccountId,
            request.SubNickname);

        MERRICK.DatabaseContext.Entities.Core.Account? mainAccount = await accountRepository.GetByIdAsync(request.MainAccountId, cancellationToken);
        if (mainAccount?.User is null)
        {
            logger.LogWarning(
                "[{Timestamp}] [CreateSubAccountHandler] Main account or User not found for AccountId={MainAccountId}",
                _timeProvider.GetUtcNow(),
                request.MainAccountId);

            Dictionary<string, object> unauthResponse = new()
            {
                ["0"] = false,
                ["success"] = 0,
                ["errors"] = "Session cookie invalid.",
                ["error_code"] = 1,
                ["error_msg"] = "Session cookie invalid."
            };

            return new CreateSubAccountResultDto(false, 1, "Session cookie invalid.", null, null, unauthResponse);
        }

        if (mainAccount.IsBanned || mainAccount.Type == AccountType.Disabled || mainAccount.IsSuspended)
        {
            logger.LogWarning(
                "[{Timestamp}] [CreateSubAccountHandler] Subaccount creation rejected for '{MainAccount}': Account under sanction",
                _timeProvider.GetUtcNow(),
                mainAccount.Name);

            Dictionary<string, object> sanctionResponse = new()
            {
                ["0"] = false,
                ["success"] = 0,
                ["errors"] = "Cannot create subaccounts while account is under an active disciplinary sanction.",
                ["error_code"] = 3,
                ["error_msg"] = "Cannot create subaccounts while account is under an active disciplinary sanction."
            };

            return new CreateSubAccountResultDto(false, 3, "Cannot create subaccounts while account is under an active disciplinary sanction.", null, null, sanctionResponse);
        }

        if (string.IsNullOrEmpty(request.Password) || !await passwordVerifier.VerifyPasswordAsync(mainAccount.User, request.Password, cancellationToken))
        {
            logger.LogWarning(
                "[{Timestamp}] [CreateSubAccountHandler] Subaccount creation rejected for '{MainAccount}': Incorrect password provided",
                _timeProvider.GetUtcNow(),
                mainAccount.Name);

            Dictionary<string, object> passResponse = new()
            {
                ["0"] = false,
                ["success"] = 0,
                ["error"] = 1,
                ["errors"] = "24",
                ["error_code"] = 24,
                ["error_msg"] = "Incorrect password."
            };

            return new CreateSubAccountResultDto(false, 24, "Incorrect password.", null, null, passResponse);
        }

        if (string.IsNullOrWhiteSpace(request.SubNickname) || request.SubNickname.Length < 3 || request.SubNickname.Length > 15)
        {
            logger.LogWarning(
                "[{Timestamp}] [CreateSubAccountHandler] Subaccount creation rejected for '{MainAccount}': Invalid nickname length '{SubNick}'",
                _timeProvider.GetUtcNow(),
                mainAccount.Name,
                request.SubNickname);

            Dictionary<string, object> lenResponse = new()
            {
                ["0"] = false,
                ["success"] = 0,
                ["errors"] = "Nickname must be between 3 and 15 characters.",
                ["error_code"] = 5,
                ["error_msg"] = "Nickname must be between 3 and 15 characters."
            };

            return new CreateSubAccountResultDto(false, 5, "Nickname must be between 3 and 15 characters.", null, null, lenResponse);
        }

        MERRICK.DatabaseContext.Entities.Core.Account? subAccount = await accountRepository.CreateSubAccountAsync(mainAccount.ID, request.SubNickname, cancellationToken);
        if (subAccount is null)
        {
            logger.LogWarning(
                "[{Timestamp}] [CreateSubAccountHandler] Subaccount nickname '{SubNick}' is already taken",
                _timeProvider.GetUtcNow(),
                request.SubNickname);

            Dictionary<string, object> takenResponse = new()
            {
                ["0"] = false,
                ["success"] = 0,
                ["errors"] = "Subaccount nickname is already taken.",
                ["error_code"] = 2,
                ["error_msg"] = "Subaccount nickname is already taken."
            };

            return new CreateSubAccountResultDto(false, 2, "Subaccount nickname is already taken.", null, null, takenResponse);
        }

        logger.LogInformation(
            "[{Timestamp}] [CreateSubAccountHandler] Created subaccount '{SubNick}' (ID: {SubId}) for User {UserId}",
            _timeProvider.GetUtcNow(),
            subAccount.Name,
            subAccount.ID,
            mainAccount.User.ID);

        Dictionary<string, object> response = new()
        {
            ["0"] = true,
            ["success"] = 1,
            ["status"] = "OK",
            ["account_id"] = subAccount.ID.ToString(),
            ["nickname"] = subAccount.Name,
            ["errors"] = string.Empty,
            ["error_code"] = 0,
            ["error_msg"] = string.Empty
        };

        return new CreateSubAccountResultDto(true, 0, string.Empty, subAccount.ID, subAccount.Name, response);
    }
}
