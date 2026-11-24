// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;

namespace NEXUS.Contracts.CQRS.Store;

/// <summary>
///     Query to retrieve all user-owned items in the vault and selected upgrades.
/// </summary>
public sealed record GetVaultItemsQuery(
    int AccountId,
    string HostTime = "0") : IRequest<VaultItemsResultDto>;

/// <summary>
///     Result of viewing the vault items.
/// </summary>
public sealed record VaultItemsResultDto(
    int AccountId,
    string HostTime,
    int ResponseCode,
    IReadOnlyDictionary<string, object> RawResponse);
