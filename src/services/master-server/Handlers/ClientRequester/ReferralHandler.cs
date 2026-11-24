// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using KONGOR.MasterServer.Extensions;
using KONGOR.MasterServer.Logging;
using KONGOR.MasterServer.Services.Security;
using NEXUS.Contracts.Data;
using NEXUS.Contracts.Security;

namespace KONGOR.MasterServer.Handlers.ClientRequester;

public class ReferralHandler(
    IAccountRepository accountRepository,
    ISessionContextResolver sessionResolver,
    ILogger<ReferralHandler> logger) : IClientRequestHandler
{
    private IAccountRepository AccountRepository { get; } = accountRepository;
    private ISessionContextResolver SessionResolver { get; } = sessionResolver;
    private ILogger<ReferralHandler> Logger { get; } = logger;

    public string FunctionName => "get_referrals";

    public async Task<IActionResult> HandleRequestAsync(HttpContext context,
        CancellationToken cancellationToken = default)
    {
        string? functionName = context.Request.Query["f"].FirstOrDefault()
                               ?? (context.Request.HasFormContentType && context.Request.Form.ContainsKey("f")
                                   ? context.Request.Form["f"].FirstOrDefault()
                                   : null);

        if (string.Equals(functionName, "set_ingame_referrer", StringComparison.OrdinalIgnoreCase))
            return await HandleSetInGameReferrerAsync(context, cancellationToken);

        return await HandleGetReferralsAsync(context, cancellationToken);
    }

    private async Task<IActionResult> HandleSetInGameReferrerAsync(HttpContext context,
        CancellationToken cancellationToken = default)
    {
        HttpRequest request = context.Request;
        string referrer = request.HasFormContentType && request.Form.ContainsKey("referrer")
            ? request.Form["referrer"].ToString()
            : (request.Query["referrer"].ToString() ?? string.Empty);
        string matchIdStr = request.HasFormContentType && request.Form.ContainsKey("match_id")
            ? request.Form["match_id"].ToString()
            : (request.Query["match_id"].ToString() ?? string.Empty);
        string cookie = request.GetCookie() ?? string.Empty;

        Logger.LogProcessingRequest("set_ingame_referrer", SecurityMasking.MaskToken(cookie), true);

        if (Logger.IsEnabled(LogLevel.Information))
            Logger.LogInformation(
                "[Referral] Processing set_ingame_referrer: Referrer={Referrer}, MatchID={MatchId}, Cookie={CookiePrefix}",
                referrer, matchIdStr, SecurityMasking.MaskToken(cookie));

        Account? currentAccount = await SessionResolver.ResolveAccountAsync(context, cancellationToken);
        if (currentAccount == null)
        {
            if (Logger.IsEnabled(LogLevel.Warning))
                Logger.LogWarning("[Referral] Failed to resolve current account for session cookie: {CookiePrefix}",
                    SecurityMasking.MaskToken(cookie));

            Dictionary<string, object> errorResponse = new()
            {
                ["0"] = false,
                ["error_code"] = 1,
                ["error_msg"] = "Account session invalid."
            };
            return new PhpResult(errorResponse, contentType: "text/plain; charset=utf-8");
        }

        Account? referrerAccount = null;
        if (!string.IsNullOrEmpty(referrer))
            referrerAccount = await AccountRepository.GetByNicknameAsync(referrer, cancellationToken);

        if (referrerAccount != null)
        {
            if (Logger.IsEnabled(LogLevel.Information))
                Logger.LogInformation(
                    "[Referral] Successfully linked referrer: {ReferrerAccount} (ID: {ReferrerId}) for referee: {RefereeAccount} (ID: {RefereeId})",
                    referrerAccount.Name, referrerAccount.ID, currentAccount.Name, currentAccount.ID);
        }
        else
        {
            if (Logger.IsEnabled(LogLevel.Information))
                Logger.LogInformation("[Referral] Referrer '{Referrer}' recorded for referee: {RefereeAccount}",
                    referrer, currentAccount.Name);
        }

        Dictionary<string, object> response = new()
        {
            ["0"] = true,
            ["status"] = "OK",
            ["referrer"] = referrer,
            ["error_code"] = 0,
            ["error_msg"] = string.Empty
        };

        return new PhpResult(response, contentType: "text/plain; charset=utf-8");
    }

    private async Task<IActionResult> HandleGetReferralsAsync(HttpContext context,
        CancellationToken cancellationToken = default)
    {
        HttpRequest request = context.Request;
        string cookie = request.GetCookie() ?? string.Empty;
        string accountIdStr = request.HasFormContentType && request.Form.ContainsKey("account_id")
            ? request.Form["account_id"].ToString()
            : (request.Query["account_id"].ToString() ?? string.Empty);

        Logger.LogProcessingRequest("get_referrals", SecurityMasking.MaskToken(cookie), true);

        Account? account = await SessionResolver.ResolveAccountAsync(context, cancellationToken);
        if (account == null)
        {
            Logger.LogWarning("[Referral] Rejected unauthenticated get_referrals request (account_id='{AccountIdStr}')", accountIdStr);
            return new UnauthorizedObjectResult("Invalid or missing session");
        }

        if (Logger.IsEnabled(LogLevel.Information))
            Logger.LogInformation(
                "[Referral] Retrieving referral overview for Account: {AccountName} (ID: {AccountId})",
                account?.Name ?? "UNKNOWN", account?.ID ?? 0);

        Dictionary<object, object> rewardsDict = new()
        {
            [1] =
                new Dictionary<string, object>
                {
                    ["level"] = 1,
                    ["referrals_needed"] = 1,
                    ["reward_coins"] = 50,
                    ["claimed"] = false
                },
            [2] = new Dictionary<string, object>
            {
                ["level"] = 2,
                ["referrals_needed"] = 3,
                ["reward_coins"] = 150,
                ["claimed"] = false
            },
            [3] = new Dictionary<string, object>
            {
                ["level"] = 3,
                ["referrals_needed"] = 5,
                ["reward_coins"] = 300,
                ["claimed"] = false
            }
        };

        Dictionary<string, object> response = new()
        {
            ["0"] = true,
            ["account_id"] = account?.ID.ToString() ?? "0",
            ["referrals_count"] = 0,
            ["active_referrals"] = 0,
            ["coins_earned"] = 0,
            ["rewards"] = rewardsDict,
            ["error_code"] = 0,
            ["error_msg"] = string.Empty
        };

        return new PhpResult(response, contentType: "text/plain; charset=utf-8");
    }
}
