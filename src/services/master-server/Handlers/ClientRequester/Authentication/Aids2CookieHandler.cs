// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using KONGOR.MasterServer.Logging;
using NEXUS.Contracts.Security;

namespace KONGOR.MasterServer.Handlers.ClientRequester;

/// <summary>
///     Handles aids2cookie requests (f=aids2cookie), translating an authenticated session cookie into an Account ID.
/// </summary>
public sealed class Aids2CookieHandler(
    IAccountRepository accountRepository,
    IDatabase distributedCache,
    ILogger<Aids2CookieHandler> logger) : IClientRequestHandler
{
    public async Task<IActionResult> HandleRequestAsync(HttpContext context, CancellationToken cancellationToken = default)
    {
        HttpRequest request = context.Request;
        string? cookie = ClientRequestHelper.GetCookie(request);
        cookie = cookie?.Replace("-", string.Empty);

        if (cookie is null)
        {
            logger.LogMissingCookie();
            return new PhpResult(new { error = "Missing Cookie" }, StatusCodes.Status400BadRequest);
        }

        string? accountName = context.Items["SessionAccountName"] as string
                              ?? await distributedCache.GetAccountNameForSessionCookie(cookie);

        if (accountName is null)
        {
            logger.LogCookieValidatedButNotFound(SecurityMasking.MaskToken(cookie));
            return new PhpResult(new { error = "Invalid Session" }, StatusCodes.Status401Unauthorized);
        }

        int? accountId = await accountRepository.GetAccountIdByNicknameAsync(accountName, cancellationToken);
        if (accountId is null)
        {
            logger.LogAccountNameNotFoundInDB(accountName);
            return new PhpResult(new { error = "Account Not Found" }, StatusCodes.Status404NotFound);
        }

        return new PhpResult(accountId.Value);
    }
}
