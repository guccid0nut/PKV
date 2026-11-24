// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;
using NEXUS.Contracts.Store;

namespace NEXUS.Contracts.CQRS.Store;

/// <summary>
///     Query to retrieve store items for a specific category and page.
/// </summary>
public sealed record GetStoreCategoryQuery(
    int AccountId,
    int CategoryId = (int)StoreCategory.AccountIcons,
    int Page = 1,
    string HostTime = "0") : IRequest<StoreCategoryResultDto>;

/// <summary>
///     Result of viewing a store category.
/// </summary>
public sealed record StoreCategoryResultDto(
    int CategoryId,
    int CurrentPage,
    string HostTime,
    int ResponseCode,
    IReadOnlyDictionary<string, object> RawResponse);
