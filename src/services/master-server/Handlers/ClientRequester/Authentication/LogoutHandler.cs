// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using NEXUS.Contracts.Events;

namespace KONGOR.MasterServer.Handlers.ClientRequester;

/// <summary>
///     Handles player logout requests (f=logout), invalidating the active session cookie in Redis and database.
/// </summary>
public sealed class LogoutHandler(
    ISessionRepository sessionRepository,
    IDatabase distributedCache,
    IEventBus eventBus) : IClientRequestHandler
{
    public async Task<IActionResult> HandleRequestAsync(HttpContext context, CancellationToken cancellationToken = default)
    {
        string? cookie = ClientRequestHelper.GetCookie(context.Request);
        if (cookie is not null)
        {
            string? accountName = await distributedCache.GetAccountNameForSessionCookie(cookie);
            await distributedCache.RemoveAccountNameForSessionCookie(cookie);
            await sessionRepository.RemoveSessionCookieAsync(cookie, cancellationToken);

            await eventBus.PublishAsync(new UserLoggedOutEvent(cookie, accountName), cancellationToken);
        }

        return new PhpResult(true);
    }
}
