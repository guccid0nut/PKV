// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;

namespace NEXUS.Contracts.CQRS.Store;

/// <summary>
///     Query to retrieve currently selected upgrades for an account.
/// </summary>
public sealed record GetSelectedUpgradesQuery(
    int AccountId,
    string HostTime = "0") : IRequest<SelectedUpgradesResultDto>;

/// <summary>
///     Result of listing selected upgrades.
/// </summary>
public sealed record SelectedUpgradesResultDto(
    int AccountId,
    string HostTime,
    int ResponseCode,
    IReadOnlyDictionary<string, object> RawResponse);
