// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using KONGOR.MasterServer.Extensions;
using KONGOR.MasterServer.Logging;
using KONGOR.MasterServer.Services.Security;
using NEXUS.Contracts.Data;
using NEXUS.Contracts.Security;

namespace KONGOR.MasterServer.Handlers.ClientRequester;

/// <summary>
///     Handles native K2 CChatManager notification endpoints over client_requester.php:
///     - remove_all_notifications (k2_x64.dll 0x1807064a0 -> 0x1806d4240, case 0x12)
///     - delete_notification (k2_x64.dll 0x1806ce750 -> 0x1806d45d0, case 0x11)
///     - test_create_notification (k2_x64.dll 0x1806cebc0 -> 0x1806d4ba0, case 0x10)
/// </summary>
public class NotificationsHandler(
    IAccountRepository accountRepository,
    ISessionContextResolver sessionResolver,
    ILogger<NotificationsHandler> logger) : IClientRequestHandler
{
    public string FunctionName => "remove_all_notifications";

    public async Task<IActionResult> HandleRequestAsync(HttpContext context,
        CancellationToken cancellationToken = default)
    {
        string functionName = (context.Request.Query["f"].FirstOrDefault()
                               ?? (context.Request.HasFormContentType && context.Request.Form.ContainsKey("f")
                                   ? context.Request.Form["f"].FirstOrDefault()
                                   : null)
                               ?? FunctionName).ToLowerInvariant();

        HttpRequest request = context.Request;
        string cookie = request.GetCookie() ?? string.Empty;
        logger.LogProcessingRequest(functionName, SecurityMasking.MaskToken(cookie), true);

        Account? currentAccount = await sessionResolver.ResolveAccountAsync(context, cancellationToken);
        if (currentAccount == null)
        {
            if (logger.IsEnabled(LogLevel.Warning))
                logger.LogWarning(
                    "[Notifications] Rejected unauthenticated request f={FunctionName} (cookie={CookieMask})",
                    functionName, SecurityMasking.MaskToken(cookie));

            return new UnauthorizedObjectResult(new Dictionary<string, object>
            {
                ["status"] = "FAIL",
                ["error_code"] = 1,
                ["error_msg"] = "Session cookie invalid."
            });
        }

        return functionName switch
        {
            "delete_notification" => await HandleDeleteNotificationAsync(context, currentAccount, cancellationToken),
            "test_create_notification" => await HandleTestCreateNotificationAsync(context, currentAccount, cancellationToken),
            _ => await HandleRemoveAllNotificationsAsync(context, currentAccount, cancellationToken)
        };
    }

    private async Task<IActionResult> HandleRemoveAllNotificationsAsync(
        HttpContext context,
        Account currentAccount,
        CancellationToken cancellationToken)
    {
        if (!TryValidateAccountOwnership(context, currentAccount, "remove_all_notifications", out IActionResult? errorResult))
            return errorResult!;

        int deletedCount = await accountRepository.RemoveAllNotificationsAsync(currentAccount.ID, cancellationToken);

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "[Notifications] remove_all_notifications succeeded for Account #{AccountId} ('{AccountName}', deletedRows={DeletedCount})",
                currentAccount.ID, currentAccount.Name, deletedCount);

        // satisfies: k2_x64.dll 0x1806d4240 compares CPHPData["status"] case-insensitively against L"OK" (0x180f1f4e4)
        return new PhpResult(new Dictionary<string, object>
        {
            ["status"] = "OK"
        }, contentType: "text/plain; charset=utf-8");
    }

    private async Task<IActionResult> HandleDeleteNotificationAsync(
        HttpContext context,
        Account currentAccount,
        CancellationToken cancellationToken)
    {
        if (!TryValidateAccountOwnership(context, currentAccount, "delete_notification", out IActionResult? errorResult))
            return errorResult!;

        int internalId = ReadIntParameter(context.Request, "internal_id");
        int notifyId = ReadIntParameter(context.Request, "notify_id");

        bool deleted = false;
        if (notifyId > 0)
            deleted = await accountRepository.DeleteNotificationAsync(currentAccount.ID, notifyId, cancellationToken);

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "[Notifications] delete_notification completed for Account #{AccountId} ('{AccountName}', internalId={InternalId}, notifyId={NotifyId}, dbDeleted={Deleted})",
                currentAccount.ID, currentAccount.Name, internalId, notifyId, deleted);

        // satisfies: k2_x64.dll 0x1806d45d0 requires status="OK", internal_id, and notify_id to invoke 0x1806d0040
        return new PhpResult(new Dictionary<string, object>
        {
            ["status"] = "OK",
            ["internal_id"] = internalId,
            ["notify_id"] = notifyId
        }, contentType: "text/plain; charset=utf-8");
    }

    private async Task<IActionResult> HandleTestCreateNotificationAsync(
        HttpContext context,
        Account currentAccount,
        CancellationToken cancellationToken)
    {
        HttpRequest request = context.Request;
        bool isStaff = currentAccount.IsStaff || (currentAccount.User?.IsStaff ?? false);

        List<int> targetAccountIds = [];
        if (request.HasFormContentType)
        {
            foreach (string? rawId in request.Form["account_id[]"])
                if (int.TryParse(rawId, out int parsed) && parsed > 0)
                    targetAccountIds.Add(parsed);

            if (targetAccountIds.Count == 0 && request.Form.TryGetValue("account_id", out var singleValues))
                foreach (string? rawId in singleValues)
                    if (int.TryParse(rawId, out int parsed) && parsed > 0)
                        targetAccountIds.Add(parsed);
        }

        if (targetAccountIds.Count == 0)
        {
            int queryAccountId = ReadIntParameter(request, "account_id");
            targetAccountIds.Add(queryAccountId > 0 ? queryAccountId : currentAccount.ID);
        }

        foreach (int targetId in targetAccountIds)
        {
            if (targetId != currentAccount.ID && !isStaff)
            {
                logger.LogWarning(
                    "[SECURITY-ALERT] [Notifications] Account #{CallerId} ('{CallerName}') attempted cross-account test_create_notification targeting Account #{TargetId}",
                    currentAccount.ID, currentAccount.Name, targetId);

                return new UnauthorizedObjectResult(new Dictionary<string, object>
                {
                    ["status"] = "FAIL",
                    ["error_code"] = 403,
                    ["error_msg"] = "Forbidden cross-account notification target."
                });
            }
        }

        string notificationContent = ReadStringParameter(request, "params[notification]")
                                     ?? ReadStringParameter(request, "notification")
                                     ?? string.Empty;
        if (notificationContent.Length > 2048)
            notificationContent = notificationContent[..2048];

        int lastCreatedId = 0;
        foreach (int targetId in targetAccountIds.Distinct())
            lastCreatedId = await accountRepository.CreateNotificationAsync(targetId, notificationContent, cancellationToken);

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "[Notifications] test_create_notification created notifyId={NotifyId} for {TargetCount} account(s) by Account #{CallerId}",
                lastCreatedId, targetAccountIds.Count, currentAccount.ID);

        // satisfies: k2_x64.dll 0x1806d4ba0 expects status="OK", notification, and notify_id
        return new PhpResult(new Dictionary<string, object>
        {
            ["status"] = "OK",
            ["notification"] = notificationContent,
            ["notify_id"] = lastCreatedId
        }, contentType: "text/plain; charset=utf-8");
    }

    private bool TryValidateAccountOwnership(
        HttpContext context,
        Account currentAccount,
        string functionName,
        out IActionResult? errorResult)
    {
        errorResult = null;
        string? rawAccountId = ReadStringParameter(context.Request, "account_id");
        if (string.IsNullOrWhiteSpace(rawAccountId))
            return true;

        if (!int.TryParse(rawAccountId.Trim(), out int requestedAccountId) || requestedAccountId != currentAccount.ID)
        {
            logger.LogWarning(
                "[SECURITY-ALERT] [Notifications] Cross-account mismatch on f={FunctionName}: session Account #{SessionAccountId} ('{AccountName}') supplied account_id='{SuppliedAccountId}'",
                functionName, currentAccount.ID, currentAccount.Name, rawAccountId);

            errorResult = new UnauthorizedObjectResult(new Dictionary<string, object>
            {
                ["status"] = "FAIL",
                ["error_code"] = 403,
                ["error_msg"] = "Account ownership mismatch."
            });
            return false;
        }

        return true;
    }

    private static int ReadIntParameter(HttpRequest request, string key)
    {
        string? raw = ReadStringParameter(request, key);
        return int.TryParse(raw, out int value) ? value : 0;
    }

    private static string? ReadStringParameter(HttpRequest request, string key)
    {
        if (request.HasFormContentType && request.Form.TryGetValue(key, out var formVal) && formVal.Count > 0)
            return formVal[0];

        if (request.Query.TryGetValue(key, out var queryVal) && queryVal.Count > 0)
            return queryVal[0];

        return null;
    }
}
