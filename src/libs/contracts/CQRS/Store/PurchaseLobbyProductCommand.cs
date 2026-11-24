// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;
using NEXUS.Contracts.CQRS;

namespace NEXUS.Contracts.CQRS.Store;

/// <summary>
///     Command to purchase or equip an avatar directly from the match lobby.
///     Implements <see cref="ILockableRequest"/> to automatically serialize checkout operations per account.
/// </summary>
public sealed record PurchaseLobbyProductCommand(
    int AccountId,
    string HeroName,
    string AvatarCode,
    string Currency) : IRequest<PurchaseLobbyProductResultDto>, ILockableRequest
{
    public string LockKey => $"lock:account:{AccountId}:store_checkout";
    public TimeSpan LockTimeout => TimeSpan.FromSeconds(10);
}

/// <summary>
///     Result of attempting a match lobby store purchase.
/// </summary>
public sealed record PurchaseLobbyProductResultDto(
    bool Success,
    int ErrorCode,
    int PopupCode,
    IReadOnlyDictionary<string, object> RawResponse);
