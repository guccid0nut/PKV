// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Globalization;
using System.Security.Claims;
using Microsoft.Extensions.Primitives;
using NEXUS.Contracts.Clients;
using ZORGATH.WebPortal.API.Services;

namespace ZORGATH.WebPortal.API.Controllers;

/// <summary>
/// Self-service GDPR/privacy endpoints enabling players (including Discord-only accounts with no email)
/// to export their personal data archive or permanently anonymize/delete their account.
/// </summary>
[ApiController]
[Route("api/account")]
[Produces("application/json")]
[EnableRateLimiting(RateLimiterPolicies.Strict)]
public sealed class AccountPrivacyController(IAccountPrivacyService privacyService) : ControllerBase
{
    public const string RequiredDeletionConfirmationPhrase = "DELETE MY ACCOUNT";
    public const string ProxyUserIdHeader = "X-Kongor-User-Id";

    /// <summary>
    /// Exports a sanitized JSON archive of the authenticated player's profile, linked Discord metadata,
    /// in-game accounts, cosmetic inventory, and submitted reports (zero password hashes, tokens, or staff notes).
    /// </summary>
    [HttpGet("me/export")]
    [ProducesResponseType(typeof(AccountDataExportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ExportMyData(CancellationToken ct = default)
    {
        int? userId = ResolveAuthenticatedUserId();
        if (userId is null)
        {
            return Unauthorized("Authentication required to export account data.");
        }

        AccountDataExportDto? export = await privacyService.ExportUserDataAsync(userId.Value, ct);
        return export is not null ? Ok(export) : NotFound($"User #{userId.Value} not found.");
    }

    /// <summary>
    /// Permanently anonymizes PII, unlinks Discord OAuth identity, revokes all active sessions,
    /// and disables all game accounts for the authenticated player while preserving referential match history integrity.
    /// Does not require an email address (fully supports Discord-only authentication).
    /// </summary>
    [HttpDelete("me")]
    [ProducesResponseType(typeof(AccountDeletionResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteMyAccount(
        [FromBody] DeleteAccountRequestDto? request,
        CancellationToken ct = default)
    {
        int? userId = ResolveAuthenticatedUserId();
        if (userId is null)
        {
            return Unauthorized("Authentication required to delete an account.");
        }

        string phrase = request?.ConfirmationPhrase?.Trim() ?? string.Empty;
        if (!string.Equals(phrase, RequiredDeletionConfirmationPhrase, StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(
                $"Explicit confirmation required. Set confirmationPhrase to \"{RequiredDeletionConfirmationPhrase}\".");
        }

        AccountDeletionResultDto? result = await privacyService.DeleteAndAnonymizeUserAsync(userId.Value, ct);
        return result is not null ? Ok(result) : NotFound($"User #{userId.Value} not found.");
    }

    private int? ResolveAuthenticatedUserId()
    {
        // 1. Standard JWT / Cookie ClaimsPrincipal
        if (User?.Identity?.IsAuthenticated == true)
        {
            string? claimVal = User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? User.FindFirstValue("UserID")
                ?? User.FindFirstValue("sub");
            if (int.TryParse(claimVal, NumberStyles.Integer, CultureInfo.InvariantCulture, out int claimUserId) && claimUserId > 0)
            {
                return claimUserId;
            }
        }

        // 2. Trusted DAWNBRINGER WebPortal internal proxy with validated X-Internal-Service-Key
        if (HttpContext is not null &&
            RateLimiterPolicies.IsValidInternalServiceKey(HttpContext) &&
            Request.Headers.TryGetValue(ProxyUserIdHeader, out StringValues headerVals) &&
            int.TryParse(headerVals.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int proxyUserId) &&
            proxyUserId > 0)
        {
            return proxyUserId;
        }

        return null;
    }
}
