// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using KONGOR.MasterServer.Logging;

namespace KONGOR.MasterServer.Handlers.ClientRequester;

/// <summary>
///     Rejects legacy plain HTTP authentication requests (f=auth), enforcing SRP protocol compliance.
/// </summary>
public sealed class AuthHandler(ILogger<AuthHandler> logger) : IClientRequestHandler
{
    public Task<IActionResult> HandleRequestAsync(HttpContext context, CancellationToken cancellationToken = default)
    {
        HttpRequest request = context.Request;
        string? accountName = request.Form["login"];

        if (accountName is not null)
        {
            logger.LogAttemptingHttpClientAuth(accountName);
        }

        return Task.FromResult<IActionResult>(new PhpResult(
            new SRPAuthenticationFailureResponse(SRPAuthenticationFailureReason.SRPAuthenticationDisabled),
            StatusCodes.Status400BadRequest));
    }
}
