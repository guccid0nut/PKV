// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;
using Microsoft.Extensions.Logging;
using NEXUS.Contracts.CQRS.AccountManagement;
using NEXUS.Contracts.Data;

namespace KONGOR.MasterServer.Features.AccountManagement;

/// <summary>
///     MediatR query handler for validating promotional codes (<see cref="ValidatePromoCodeQuery"/>).
/// </summary>
public sealed class ValidatePromoCodeHandler(
    IInventoryRepository inventoryRepository,
    ILogger<ValidatePromoCodeHandler> logger,
    TimeProvider? timeProvider = null)
    : IRequestHandler<ValidatePromoCodeQuery, ValidatePromoCodeResultDto>
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<ValidatePromoCodeResultDto> Handle(
        ValidatePromoCodeQuery request,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "[{Timestamp}] [ValidatePromoCodeHandler] Validating promo code: {PromoCode}",
            _timeProvider.GetUtcNow(),
            request.PromoCode);

        bool isValid = await inventoryRepository.ValidatePromoCodeAsync(request.PromoCode, cancellationToken);

        logger.LogInformation(
            "[{Timestamp}] [ValidatePromoCodeHandler] Promo code '{PromoCode}' validity: {IsValid}",
            _timeProvider.GetUtcNow(),
            request.PromoCode,
            isValid);

        Dictionary<string, object> response = new()
        {
            ["0"] = isValid,
            ["success"] = isValid ? 1 : 0,
            ["valid"] = isValid,
            ["promocode"] = request.PromoCode,
            ["discount"] = isValid ? "20%" : "0%",
            ["errors"] = isValid ? string.Empty : "Invalid or already redeemed promotional code.",
            ["error_code"] = isValid ? 0 : 1,
            ["error_msg"] = isValid ? string.Empty : "Invalid or already redeemed promotional code."
        };

        return new ValidatePromoCodeResultDto(
            isValid,
            request.PromoCode,
            isValid ? "20%" : "0%",
            isValid ? 0 : 1,
            isValid ? string.Empty : "Invalid or already redeemed promotional code.",
            response);
    }
}
