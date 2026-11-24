// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;
using Microsoft.Extensions.Logging;
using NEXUS.Contracts.CQRS.AccountManagement;
using NEXUS.Contracts.Data;

namespace KONGOR.MasterServer.Features.AccountManagement;

/// <summary>
///     MediatR query handler for checking player nickname availability.
/// </summary>
public sealed class CheckNicknameAvailabilityHandler(
    IAccountRepository accountRepository,
    ILogger<CheckNicknameAvailabilityHandler> logger,
    TimeProvider? timeProvider = null)
    : IRequestHandler<CheckNicknameAvailabilityQuery, NicknameAvailabilityResultDto>
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<NicknameAvailabilityResultDto> Handle(
        CheckNicknameAvailabilityQuery request,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "[{Timestamp}] [CheckNicknameAvailabilityHandler] Checking nickname availability: {Nickname}",
            _timeProvider.GetUtcNow(),
            request.Nickname);

        if (string.IsNullOrWhiteSpace(request.Nickname) || request.Nickname.Length < 3 || request.Nickname.Length > 15)
        {
            logger.LogWarning(
                "[{Timestamp}] [CheckNicknameAvailabilityHandler] Nickname length invalid: '{Nickname}'",
                _timeProvider.GetUtcNow(),
                request.Nickname);

            Dictionary<string, object> invalidResponse = new()
            {
                ["0"] = false,
                ["success"] = 0,
                ["available"] = false,
                ["errors"] = "Nickname must be between 3 and 15 characters.",
                ["error_code"] = 1,
                ["error_msg"] = "Nickname must be between 3 and 15 characters."
            };

            return new NicknameAvailabilityResultDto(false, 1, "Nickname must be between 3 and 15 characters.", invalidResponse);
        }

        bool isTaken = await accountRepository.ExistsByNicknameAsync(request.Nickname, cancellationToken);

        logger.LogInformation(
            "[{Timestamp}] [CheckNicknameAvailabilityHandler] Nickname '{Nickname}' availability: Taken={IsTaken}",
            _timeProvider.GetUtcNow(),
            request.Nickname,
            isTaken);

        Dictionary<string, object> response = new()
        {
            ["0"] = !isTaken,
            ["success"] = !isTaken ? 1 : 0,
            ["available"] = !isTaken,
            ["nickname"] = request.Nickname,
            ["errors"] = isTaken ? "Nickname is already taken." : string.Empty,
            ["error_code"] = isTaken ? 2 : 0,
            ["error_msg"] = isTaken ? "Nickname is already taken." : string.Empty
        };

        return new NicknameAvailabilityResultDto(!isTaken, isTaken ? 2 : 0, isTaken ? "Nickname is already taken." : string.Empty, response);
    }
}
