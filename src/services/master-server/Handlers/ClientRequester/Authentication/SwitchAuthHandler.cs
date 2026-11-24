// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using KONGOR.MasterServer.Logging;
using KONGOR.MasterServer.Services.Quests;
using KONGOR.MasterServer.Services.Security;
using MERRICK.DatabaseContext.Extensions;
using NEXUS.Contracts.Events;
using NEXUS.Contracts.Security;
using NEXUS.Contracts.Services;

namespace KONGOR.MasterServer.Handlers.ClientRequester;

/// <summary>
///     Handles sub-account switching (f=switch_auth), verifying account ownership,
///     issuing a new session cookie, updating timestamps, and dispatching switch domain events.
/// </summary>
public sealed class SwitchAuthHandler(
    IAccountRepository accountRepository,
    IDatabase distributedCache,
    INetworkTopologyResolver networkTopologyResolver,
    IEventBus eventBus,
    IOptions<OperationalConfiguration> operationalConfiguration,
    ILogger<SwitchAuthHandler> logger,
    IWarEffortService? warEffortService = null) : IClientRequestHandler
{
    public async Task<IActionResult> HandleRequestAsync(HttpContext context, CancellationToken cancellationToken = default)
    {
        HttpRequest request = context.Request;
        string? cookie = ClientRequestHelper.GetCookie(request);
        cookie = cookie?.Replace("-", string.Empty);

        logger.LogSwitchAuthReceived(SecurityMasking.MaskToken(cookie));

        if (string.IsNullOrEmpty(cookie))
        {
            logger.LogSwitchAuthMissingCookie();
            return new PhpResult(
                new SRPAuthenticationFailureResponse(SRPAuthenticationFailureReason.MissingCachedSRPData),
                StatusCodes.Status401Unauthorized);
        }

        string? targetLogin = request.Form["login"].FirstOrDefault() ?? request.Query["login"].FirstOrDefault();
        string? accountIdStr = request.Form["account_id"].FirstOrDefault() ?? request.Query["account_id"].FirstOrDefault();
        int targetAccountId = int.TryParse(accountIdStr, out int parsedId) ? parsedId : 0;

        Account? targetAccount = null;
        if (!string.IsNullOrEmpty(targetLogin) || targetAccountId > 0)
        {
            targetAccount = await accountRepository.GetForSwitchAuthAsync(targetAccountId, targetLogin, cancellationToken);
        }

        string? currentAccountName = context.Items["SessionAccountName"] as string
                                     ?? await distributedCache.GetAccountNameForSessionCookie(cookie);

        if (string.IsNullOrEmpty(currentAccountName))
        {
            string peerIp = context.Connection.RemoteIpAddress?.MapToIPv4().ToString() ?? "UNKNOWN";
            logger.LogWarning(
                "[SECURITY-ALERT] Blocked f=switch_auth with unrecognized cookie '{CookieMask}' targeting '{TargetLogin}' (#{TargetId}) from {RemoteIp}.",
                SecurityMasking.MaskToken(cookie), targetLogin ?? "NULL", targetAccountId, peerIp);
            logger.LogSwitchAuthInvalidCookie(SecurityMasking.MaskToken(cookie));
            return new PhpResult(
                new SRPAuthenticationFailureResponse(SRPAuthenticationFailureReason.MissingCachedSRPData),
                StatusCodes.Status401Unauthorized);
        }

        Account? currentAccount = await accountRepository.GetForPreAuthAsync(currentAccountName, cancellationToken);
        if (currentAccount is null)
        {
            logger.LogSwitchAuthAccountNotFound(currentAccountName);
            return new PhpResult(
                new SRPAuthenticationFailureResponse(SRPAuthenticationFailureReason.AccountNotFound),
                StatusCodes.Status404NotFound);
        }

        if (string.IsNullOrEmpty(targetLogin) && targetAccountId <= 0)
        {
            logger.LogSwitchAuthMissingTarget(currentAccount.User.ID);
            return new PhpResult(
                new SRPAuthenticationFailureResponse(SRPAuthenticationFailureReason.MissingLoginIdentifier),
                StatusCodes.Status400BadRequest);
        }

        if (targetAccount is null)
        {
            logger.LogSwitchAuthTargetNotFound(targetLogin ?? "NULL", targetAccountId);
            return new PhpResult(
                new SRPAuthenticationFailureResponse(SRPAuthenticationFailureReason.AccountNotFound),
                StatusCodes.Status404NotFound);
        }

        if (targetAccount.User.ID != currentAccount.User.ID)
        {
            logger.LogWarning(
                "[SECURITY-ALERT] Blocked cross-user f=switch_auth attempt by Account '{CurrentAccount}' (User #{CurrentUserId}) targeting '{TargetAccount}' (User #{TargetUserId}).",
                currentAccount.Name, currentAccount.User.ID, targetAccount.Name, targetAccount.User.ID);
            logger.LogSwitchAuthUnauthorizedUser(targetAccount.Name, targetAccount.User.ID, currentAccount.User.ID);
            return new PhpResult(
                new SRPAuthenticationFailureResponse(SRPAuthenticationFailureReason.AccountNotFound),
                StatusCodes.Status403Forbidden);
        }

        if (targetAccount.Type == AccountType.ServerHost || currentAccount.Type == AccountType.ServerHost)
        {
            logger.LogWarning(
                "[SECURITY-ALERT] Blocked f=switch_auth involving ServerHost account (Current='{CurrentAccount}' [{CurrentType}], Target='{TargetAccount}' [{TargetType}]).",
                currentAccount.Name, currentAccount.Type, targetAccount.Name, targetAccount.Type);
            return new PhpResult(
                new SRPAuthenticationFailureResponse(SRPAuthenticationFailureReason.AccountIsDisabled,
                    targetAccount.GetNameWithClanTag()),
                StatusCodes.Status401Unauthorized);
        }

        if (targetAccount.Type is AccountType.Disabled || targetAccount.IsBanned)
        {
            logger.LogSwitchAuthBlockedBanned(targetAccount.Name);
            return new PhpResult(
                new SRPAuthenticationFailureResponse(SRPAuthenticationFailureReason.AccountIsDisabled,
                    targetAccount.GetNameWithClanTag()), StatusCodes.Status401Unauthorized);
        }

        IPAddress rawRemoteIP = TrustedProxyIpResolver.ResolveClientIp(context);

        string remoteIPAddress = rawRemoteIP.MapToIPv4().ToString();
        string hostHeader = context.Request.Headers["X-Forwarded-Host"].FirstOrDefault()
                            ?? context.Request.Host.Value
                            ?? string.Empty;

        (string chatServerHost, int chatServerPort) =
            networkTopologyResolver.ResolveChatServer(rawRemoteIP, hostHeader, targetAccount.Name, targetAccount.Type.ToString());

        string requestBaseUrl = $"{context.Request.Scheme}://{context.Request.Host}";

        SRPAuthenticationHandlers.StageTwoResponseParameters parameters = new()
        {
            Account = targetAccount,
            ClanRoster = targetAccount.Clan?.Members ?? [],
            ServerProof = "0",
            ClientIPAddress = remoteIPAddress,
            ChatServer = (chatServerHost, chatServerPort),
            CurrentSeason = operationalConfiguration.Value.CurrentSeason,
            ICBURL = requestBaseUrl
        };

        SRPAuthenticationResponseStageTwo response =
            SRPAuthenticationHandlers.GenerateStageTwoResponse(parameters, out string newCookie);

        if (warEffortService is not null && warEffortService.IsEnabled)
        {
            try
            {
                response.QuestSystem = await warEffortService
                    .BuildClientPayloadAsync(targetAccount.ID, includeOnlyNativeTriggers: false, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception qe)
            {
                logger.LogWarning(qe, "[WAR_EFFORT] Failed to populate switch_auth quest_system for AccountId={AccountId}.", targetAccount.ID);
            }
        }

        await accountRepository.UpdateLastActiveAsync(targetAccount.ID, cancellationToken);
        await distributedCache.SetAccountNameForSessionCookie(newCookie, targetAccount.Name);
        await distributedCache.StringSetAsync($"ACCOUNT-ID-SESSION-COOKIE:{targetAccount.ID}", newCookie, TimeSpan.FromHours(24));

        // Emit asynchronous domain event for Discord presence and telemetry
        await eventBus.PublishAsync(
            new UserSwitchedAccountEvent(
                currentAccount.User.ID,
                currentAccount.ID,
                currentAccount.Name,
                targetAccount.ID,
                targetAccount.Name,
                remoteIPAddress,
                newCookie),
            cancellationToken);

        logger.LogSwitchAuthSuccess(
            currentAccount.User.ID,
            currentAccount.Name,
            targetAccount.Name,
            SecurityMasking.MaskToken(newCookie));

        return new PhpResult(response);
    }
}
