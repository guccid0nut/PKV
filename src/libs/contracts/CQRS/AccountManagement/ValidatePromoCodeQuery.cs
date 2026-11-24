// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;

namespace NEXUS.Contracts.CQRS.AccountManagement;

/// <summary>
///     Query to validate a promotional code and check discount applicability.
/// </summary>
public record ValidatePromoCodeQuery(string PromoCode) : IRequest<ValidatePromoCodeResultDto>;

/// <summary>
///     Result of promo code validation, containing golden PHP response structure.
/// </summary>
public record ValidatePromoCodeResultDto(
    bool Valid,
    string PromoCode,
    string Discount,
    int ErrorCode,
    string ErrorMessage,
    Dictionary<string, object> RawResponse);
