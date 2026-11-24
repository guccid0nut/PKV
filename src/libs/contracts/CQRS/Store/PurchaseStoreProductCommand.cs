// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;
using NEXUS.Contracts.CQRS;
using NEXUS.Contracts.Store;

namespace NEXUS.Contracts.CQRS.Store;

/// <summary>
///     Command to purchase a store product (avatar, taunt, announcer, boost, etc.).
///     Implements <see cref="ILockableRequest"/> to automatically serialize checkout operations per account.
/// </summary>
public sealed record PurchaseStoreProductCommand(
    int AccountId,
    int ProductId,
    string Currency,
    int CategoryId,
    int CurrentPage,
    string HostTime,
    int DiscountCode = 0,
    string? IdempotencyKey = null,
    int UserId = 0) : IRequest<PurchaseStoreProductResultDto>, ILockableRequest
{
    public string LockKey => UserId > 0
        ? $"lock:user:{UserId}:store_checkout"
        : $"lock:account:{AccountId}:store_checkout";
    public TimeSpan LockTimeout => TimeSpan.FromSeconds(10);
}

/// <summary>
///     Result of attempting a store purchase.
/// </summary>
public sealed record PurchaseStoreProductResultDto(
    bool Success,
    int ErrorCode,
    int PopupCode,
    IReadOnlyDictionary<string, object> RawResponse);
