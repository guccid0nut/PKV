// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Security;
using MediatR;
using Microsoft.Extensions.Logging;
using NEXUS.Contracts.CQRS.AccountManagement;
using NEXUS.Contracts.Data;
using KONGOR.MasterServer.Extensions.Cache;
using KONGOR.MasterServer.Services.Security;
using MERRICK.DatabaseContext.Entities.Core;
using StackExchange.Redis;

namespace KONGOR.MasterServer.Features.AccountManagement;

/// <summary>
///     MediatR command handler for player nickname changes (<see cref="ChangeNicknameCommand"/>).
///     Enforces password authentication, name sanitization, repository persistence, and cache invalidation.
/// </summary>
public sealed class ChangeNicknameHandler(
    IAccountRepository accountRepository,
    IDatabase distributedCache,
    IAccountPasswordVerifier passwordVerifier,
    ILogger<ChangeNicknameHandler> logger,
    TimeProvider? timeProvider = null)
    : IRequestHandler<ChangeNicknameCommand, ChangeNicknameResultDto>
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<ChangeNicknameResultDto> Handle(
        ChangeNicknameCommand request,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "[{Timestamp}] [ChangeNicknameHandler] Change nickname request: AccountId={AccountId}, NewNick={NewNick}",
            _timeProvider.GetUtcNow(),
            request.AccountId,
            request.NewNickname);

        MERRICK.DatabaseContext.Entities.Core.Account? account = await accountRepository.GetByIdAsync(request.AccountId, cancellationToken);
        if (account?.User is null)
        {
            logger.LogWarning(
                "[{Timestamp}] [ChangeNicknameHandler] Account or User not found for AccountId={AccountId}",
                _timeProvider.GetUtcNow(),
                request.AccountId);

            Dictionary<string, object> unauthResponse = new()
            {
                ["0"] = false,
                ["success"] = 0,
                ["errors"] = "Session cookie invalid.",
                ["error_code"] = 1,
                ["error_msg"] = "Session cookie invalid."
            };

            return new ChangeNicknameResultDto(false, 1, "Session cookie invalid.", null, null, unauthResponse);
        }

        if (string.IsNullOrEmpty(request.Password) || !await passwordVerifier.VerifyPasswordAsync(account.User, request.Password, cancellationToken))
        {
            logger.LogWarning(
                "[{Timestamp}] [ChangeNicknameHandler] Nickname change rejected for '{AccountName}': Incorrect password provided",
                _timeProvider.GetUtcNow(),
                account.Name);

            Dictionary<string, object> passResponse = new()
            {
                ["0"] = false,
                ["success"] = 0,
                ["error"] = 1,
                ["errors"] = "24",
                ["error_code"] = 24,
                ["error_msg"] = "Incorrect password."
            };

            return new ChangeNicknameResultDto(false, 24, "Incorrect password.", null, account.Name, passResponse);
        }

        if (string.IsNullOrWhiteSpace(request.NewNickname) ||
            request.NewNickname.Length < 3 ||
            request.NewNickname.Length > 15 ||
            !PerimeterSecuritySanitizer.IsValidNickname(request.NewNickname))
        {
            logger.LogWarning(
                "[{Timestamp}] [ChangeNicknameHandler] New nickname invalid length or characters: '{NewNick}'",
                _timeProvider.GetUtcNow(),
                request.NewNickname);

            Dictionary<string, object> lenResponse = new()
            {
                ["0"] = false,
                ["success"] = 0,
                ["errors"] = "New nickname must be between 3 and 15 alphanumeric characters or underscores.",
                ["error_code"] = 2,
                ["error_msg"] = "New nickname must be between 3 and 15 alphanumeric characters or underscores."
            };

            return new ChangeNicknameResultDto(false, 2, "New nickname must be between 3 and 15 alphanumeric characters or underscores.", null, account.Name, lenResponse);
        }

        string oldName = account.Name;
        bool success = await accountRepository.ChangeNicknameAsync(account.ID, request.NewNickname, cancellationToken);
        if (!success)
        {
            logger.LogWarning(
                "[{Timestamp}] [ChangeNicknameHandler] Nickname '{NewNick}' is already taken",
                _timeProvider.GetUtcNow(),
                request.NewNickname);

            Dictionary<string, object> takenResponse = new()
            {
                ["0"] = false,
                ["success"] = 0,
                ["errors"] = "Nickname is already in use by another player.",
                ["error_code"] = 3,
                ["error_msg"] = "Nickname is already in use by another player."
            };

            return new ChangeNicknameResultDto(false, 3, "Nickname is already in use by another player.", null, oldName, takenResponse);
        }

        if (!string.IsNullOrEmpty(request.Cookie))
        {
            await distributedCache.SetAccountNameForSessionCookie(request.Cookie, request.NewNickname);
        }

        logger.LogInformation(
            "[{Timestamp}] [ChangeNicknameHandler] Nickname successfully changed from '{OldName}' to '{NewNick}'",
            _timeProvider.GetUtcNow(),
            oldName,
            request.NewNickname);

        Dictionary<string, object> response = new()
        {
            ["0"] = true,
            ["success"] = 1,
            ["status"] = "OK",
            ["nickname"] = request.NewNickname,
            ["old_nickname"] = oldName,
            ["errors"] = string.Empty,
            ["error_code"] = 0,
            ["error_msg"] = string.Empty
        };

        return new ChangeNicknameResultDto(true, 0, string.Empty, request.NewNickname, oldName, response);
    }
}
