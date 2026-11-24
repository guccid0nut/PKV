// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;
using NEXUS.Contracts.Clients;

namespace NEXUS.Contracts.CQRS.Admin.Accounts;

/// <summary>
///     Query to retrieve recently created accounts.
/// </summary>
/// <param name="Limit">The maximum number of recent accounts to return.</param>
public sealed record GetRecentAccountsQuery(int Limit = 20) : IRequest<List<AccountDto>>;

/// <summary>
///     Query to retrieve account details by account name.
/// </summary>
/// <param name="AccountName">The name of the account to look up.</param>
public sealed record GetAccountDetailQuery(string AccountName) : IRequest<AccountDto?>;
