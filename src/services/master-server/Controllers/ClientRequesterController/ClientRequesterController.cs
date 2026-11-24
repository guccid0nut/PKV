// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ReSharper disable LogMessageIsConditional

using KONGOR.MasterServer.Logging;
using KONGOR.MasterServer.Services.Security;
using Microsoft.Extensions.Primitives;
using NEXUS.Contracts.Security;

namespace KONGOR.MasterServer.Controllers.ClientRequesterController;

[ApiController]
[Route("client_requester.php")]
public class ClientRequesterController(
    IDatabase distributedCache,
    ILogger<ClientRequesterController> logger,
    ClientRequestDispatcher clientRequestDispatcher,
    IPerIpAuthRateLimiter? authRateLimiter = null) : ControllerBase
{
    private static (string? functionName, string cookieRaw) ExtractQueryParameters(HttpRequest request)
    {
        string? functionName = null;
        string cookieRaw = "NULL";

        if (request.QueryString.HasValue)
        {
            ReadOnlySpan<char> querySpan = request.QueryString.Value.AsSpan();
            if (SpanFormUrlEncodedParser.TryExtractParameter(querySpan, "f", out ReadOnlySpan<char> fSpan))
                functionName = fSpan.ToString();

            if (SpanFormUrlEncodedParser.TryExtractParameter(querySpan, "cookie", out ReadOnlySpan<char> cookieSpan))
                cookieRaw = cookieSpan.ToString();
        }

        return (functionName, cookieRaw);
    }

    private static (string? functionName, string cookieRaw) ExtractFormParameters(IFormCollection form,
        HttpRequest request)
    {
        string? functionName = null;
        string cookieRaw = "NULL";

        if (form.TryGetValue("f", out StringValues fValues) && !StringValues.IsNullOrEmpty(fValues))
            functionName = fValues.ToString();
        else if (request.Query.TryGetValue("f", out StringValues qfValues) && !StringValues.IsNullOrEmpty(qfValues))
            functionName = qfValues.ToString();

        if (form.TryGetValue("cookie", out StringValues cookieValues) && !StringValues.IsNullOrEmpty(cookieValues))
            cookieRaw = cookieValues.ToString();
        else if (request.Query.TryGetValue("cookie", out StringValues qCookieValues) &&
                 !StringValues.IsNullOrEmpty(qCookieValues)) cookieRaw = qCookieValues.ToString();

        return (functionName, cookieRaw);
    }

    [HttpGet]
    [HttpPost]
    public async Task<IActionResult> ProcessRequest(CancellationToken cancellationToken = default)
    {
        string? functionName;
        string cookieRaw;

        if (Request.Method == "GET")
        {
            (functionName, cookieRaw) = ExtractQueryParameters(Request);
        }
        else if (Request.Method == "POST")
        {
            if (Request.HasFormContentType)
            {
                IFormCollection form = await Request.ReadFormAsync(cancellationToken);
                (functionName, cookieRaw) = ExtractFormParameters(form, Request);
            }
            else
            {
                (functionName, cookieRaw) = ExtractQueryParameters(Request);
            }
        }
        else
        {
            return StatusCode(StatusCodes.Status405MethodNotAllowed);
        }

        cookieRaw = cookieRaw.Trim('"', '\'');
        functionName = functionName?.ToLowerInvariant();

        if (string.IsNullOrEmpty(functionName)) return BadRequest("Missing function parameter 'f'");
        if (functionName is "ping") return Ok("pong");

        if (authRateLimiter is not null && authRateLimiter.IsAuthFunction(functionName))
        {
            string clientIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            AuthRateLimitDecision rateDecision = authRateLimiter.TryAcquire(clientIp, functionName);
            if (!rateDecision.IsAllowed)
            {
                Response.Headers.RetryAfter = rateDecision.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);

                return StatusCode(
                    StatusCodes.Status429TooManyRequests,
                    $"Too many authentication attempts from {rateDecision.NormalizedClientIp}. Retry after {rateDecision.RetryAfterSeconds}s.");
            }
        }

        bool endpointRequiresCookieValidation = functionName is "selected_upgrades"
            or "claim_season_rewards" or "claim_mastery_reward" or "take_mastery_reward"
            or "use_mastery_boost" or "apply_mastery_boost" or "mastery_boost" or "use_boost"
            or "create_game" or "new_game_available"
            or "guide_default_set" or "guide_default_remove" or "guide_favorite_set" or "guide_favorite_remove"
            or "guide_vote" or "switch_auth"
            or "remove_all_notifications" or "delete_notification" or "test_create_notification";

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogProcessingRequest(functionName, SecurityMasking.MaskToken(cookieRaw),
                endpointRequiresCookieValidation);

        (bool accountSessionCookieIsValid, string? sessionAccountName) =
            await distributedCache.ValidateAccountSessionCookie(cookieRaw);

        if (endpointRequiresCookieValidation && !accountSessionCookieIsValid)
            if (logger.IsEnabled(LogLevel.Warning))
                logger.LogRedisMiss(functionName, SecurityMasking.MaskToken(cookieRaw));

        if (endpointRequiresCookieValidation && !accountSessionCookieIsValid)
        {
            if (logger.IsEnabled(LogLevel.Warning))
            {
                string remoteIp = Request.HttpContext.Connection.RemoteIpAddress?.MapToIPv4().ToString() ?? "UNKNOWN";
                logger.LogWarning("[SECURITY-ALERT] Rejected forged or unrecognized session cookie '{CookieMask}' for f={Function} from {RemoteIp}.", SecurityMasking.MaskToken(cookieRaw), functionName, remoteIp);
                logger.LogForgedCookie(remoteIp, SecurityMasking.MaskToken(cookieRaw));
            }

            return Unauthorized($@"Unrecognized Cookie ""{SecurityMasking.MaskToken(cookieRaw)}""");
        }

        if (accountSessionCookieIsValid) HttpContext.Items["SessionAccountName"] = sessionAccountName;

        return await clientRequestDispatcher.DispatchAsync(functionName, HttpContext, cancellationToken);
    }
}
