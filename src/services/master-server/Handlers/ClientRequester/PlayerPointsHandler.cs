// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using KONGOR.MasterServer.Extensions;
using KONGOR.MasterServer.Logging;
using KONGOR.MasterServer.Services.Security;
using NEXUS.Contracts.Security;

namespace KONGOR.MasterServer.Handlers.ClientRequester;

public class PlayerPointsHandler(
    ISessionContextResolver sessionResolver,
    ILogger<PlayerPointsHandler> logger) : IClientRequestHandler
{
    public string FunctionName => "get_points";

    public async Task<IActionResult> HandleRequestAsync(HttpContext context,
        CancellationToken cancellationToken = default)
    {
        string? functionName = context.Request.Query["f"].FirstOrDefault()
                               ?? (context.Request.HasFormContentType && context.Request.Form.ContainsKey("f")
                                   ? context.Request.Form["f"].FirstOrDefault()
                                   : null);

        if (string.Equals(functionName, "receive_revival_gift", StringComparison.OrdinalIgnoreCase))
            return await HandleReceiveRevivalGiftAsync(context, cancellationToken);

        return await HandleGetPointsAsync(context, cancellationToken);
    }

    private async Task<IActionResult> HandleGetPointsAsync(HttpContext context,
        CancellationToken cancellationToken = default)
    {
        HttpRequest request = context.Request;
        string cookie = request.GetCookie() ?? string.Empty;

        logger.LogProcessingRequest("get_points", SecurityMasking.MaskToken(cookie), true);

        Account? account = await sessionResolver.ResolveAccountAsync(context, cancellationToken);
        if (account == null)
        {
            if (logger.IsEnabled(LogLevel.Warning))
                logger.LogWarning("[Points] Failed to resolve account for points balance query: {CookiePrefix}",
                    SecurityMasking.MaskToken(cookie));

            Dictionary<string, object> errorResponse = new()
            {
                ["0"] = false,
                ["error_code"] = 1,
                ["error_msg"] = "Session cookie invalid."
            };
            return new PhpResult(errorResponse, contentType: "text/plain; charset=utf-8");
        }

        int silver = account.User.SilverCoins;
        int gold = account.User.GoldCoins;
        int tickets = account.User.PlinkoTickets;

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "[Points] Returning currency balance for Account '{AccountName}' (Silver: {Silver}, Gold: {Gold}, Tickets: {Tickets})",
                account.Name, silver, gold, tickets);

        Dictionary<string, object> response = new()
        {
            ["0"] = true,
            ["silver"] = silver.ToString(),
            ["gold"] = gold.ToString(),
            ["points"] = silver.ToString(),
            ["plinko_tickets"] = tickets.ToString(),
            ["error_code"] = 0,
            ["error_msg"] = string.Empty
        };

        return new PhpResult(response, contentType: "text/plain; charset=utf-8");
    }

    private async Task<IActionResult> HandleReceiveRevivalGiftAsync(HttpContext context,
        CancellationToken cancellationToken = default)
    {
        HttpRequest request = context.Request;
        string cookie = request.GetCookie() ?? string.Empty;

        logger.LogProcessingRequest("receive_revival_gift", SecurityMasking.MaskToken(cookie), true);

        Account? account = await sessionResolver.ResolveAccountAsync(context, cancellationToken);
        if (account == null)
        {
            if (logger.IsEnabled(LogLevel.Warning))
                logger.LogWarning("[RevivalGift] Rejected unauthenticated revival gift claim: {CookiePrefix}",
                    SecurityMasking.MaskToken(cookie));

            Dictionary<string, object> errorResponse = new()
            {
                ["0"] = false,
                ["error_code"] = 1,
                ["error_msg"] = "Session cookie invalid."
            };
            return new PhpResult(errorResponse, contentType: "text/plain; charset=utf-8");
        }

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "[RevivalGift] Processing revival gift for Account: {AccountName}",
                account.Name);

        Dictionary<string, object> response = new()
        {
            ["0"] = true,
            ["status"] = "OK",
            ["reward"] = true,
            ["gift_claimed"] = true,
            ["error_code"] = 0,
            ["error_msg"] = string.Empty
        };

        return new PhpResult(response, contentType: "text/plain; charset=utf-8");
    }
}
