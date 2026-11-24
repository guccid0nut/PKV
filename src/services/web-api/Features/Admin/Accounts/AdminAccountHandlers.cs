// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MediatR;
using Microsoft.EntityFrameworkCore;
using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Extensions;
using MERRICK.DatabaseContext.Persistence;
using NEXUS.Contracts.Clients;
using NEXUS.Contracts.CQRS.Admin.Accounts;

namespace ZORGATH.WebPortal.API.Features.Admin.Accounts;

/// <summary>
///     Handler for retrieving recently created player accounts.
/// </summary>
public sealed class GetRecentAccountsHandler(MerrickContext db)
    : IRequestHandler<GetRecentAccountsQuery, List<AccountDto>>
{
    /// <inheritdoc />
    public async Task<List<AccountDto>> Handle(GetRecentAccountsQuery request, CancellationToken cancellationToken)
    {
        List<Account> accounts = await db.Accounts
            .Include(a => a.User)
            .OrderByDescending(a => a.TimestampCreated)
            .Take(request.Limit)
            .ToListAsync(cancellationToken);

        return accounts.Select(a => a.ToDto()).ToList();
    }
}

/// <summary>
///     Handler for retrieving account details by account name.
/// </summary>
public sealed class GetAccountDetailHandler(MerrickContext db)
    : IRequestHandler<GetAccountDetailQuery, AccountDto?>
{
    /// <inheritdoc />
    public async Task<AccountDto?> Handle(GetAccountDetailQuery request, CancellationToken cancellationToken)
    {
        Account? account = await db.Accounts
            .Include(a => a.User)
            .Include(a => a.Statistics)
            .FirstOrDefaultAsync(a => a.Name == request.AccountName, cancellationToken);

        return account?.ToDto();
    }
}
