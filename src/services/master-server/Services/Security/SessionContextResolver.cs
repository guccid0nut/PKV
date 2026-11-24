// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using KONGOR.MasterServer.Extensions.Cache;
using NEXUS.Contracts.Security;

namespace KONGOR.MasterServer.Services.Security;

public class SessionContextResolver(
    IDatabase distributedCache,
    MerrickContext dbContext,
    ILogger<SessionContextResolver> logger) : ISessionContextResolver
{
    public async Task<Account?> ResolveAccountAsync(HttpContext context, CancellationToken cancellationToken = default)
    {
        string? accountName = await ResolveAccountNameAsync(context, cancellationToken);
        if (string.IsNullOrEmpty(accountName)) return null;

        return await dbContext.Accounts
            .Include(a => a.User)
            .ThenInclude(u => u.Role)
            .Include(a => a.Clan)
            .Include(a => a.SelectedStoreItems)
            .FirstOrDefaultAsync(a => a.Name == accountName, cancellationToken);
    }

    private async Task<string?> ResolveAccountNameAsync(HttpContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Items["SessionAccountName"] is string cachedName && !string.IsNullOrEmpty(cachedName))
            return cachedName;

        string? cookie = ClientRequestHelper.GetCookie(context.Request);
        if (string.IsNullOrEmpty(cookie)) return null;

        // Use centralized ValidateAccountSessionCookie with database fallback & negative caching protection
        (bool isValid, string? accountName) =
            await distributedCache.ValidateAccountSessionCookie(cookie, dbContext, logger,
                nameof(SessionContextResolver), "DatabaseContext");

        if (isValid && !string.IsNullOrEmpty(accountName))
        {
            context.Items["SessionAccountName"] = accountName;
            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation("[SessionResolver] Resolved session for account '{AccountName}'", accountName);

            return accountName;
        }

        return null;
    }
}
