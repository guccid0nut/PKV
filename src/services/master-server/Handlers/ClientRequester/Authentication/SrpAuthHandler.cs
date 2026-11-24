// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using KONGOR.MasterServer.Logging;
using KONGOR.MasterServer.Services.Network;
using KONGOR.MasterServer.Services.Quests;
using MERRICK.DatabaseContext.Extensions;
using NEXUS.Contracts.Events;
using NEXUS.Contracts.Services;

namespace KONGOR.MasterServer.Handlers.ClientRequester;

/// <summary>
///     Handles SRP stage two authentication (f=srpauth), verifying client cryptographic proofs,
///     issuing session tokens, resolving network topology, and dispatching login domain events.
/// </summary>
public sealed class SRPAuthHandler(
    IAccountRepository accountRepository,
    IDatabase distributedCache,
    INetworkTopologyResolver networkTopologyResolver,
    IEventBus eventBus,
    IOptions<OperationalConfiguration> operationalConfiguration,
    ILogger<SRPAuthHandler> logger,
    IWarEffortService? warEffortService = null) : IClientRequestHandler
{
    /// <summary>
    ///     Gets the resolved public IP from the network topology background service.
    /// </summary>
    public static string? GetPublicIP() =>
        NetworkTopologyResolverService.CurrentPublicIP ?? NetworkTopologyResolverService.GetLocalIPAddress();

    public async Task<IActionResult> HandleRequestAsync(HttpContext context, CancellationToken cancellationToken = default)
    {
        HttpRequest request = context.Request;

        string? accountName = request.Form["login"];
        if (accountName is null)
        {
            return new PhpResult(
                new SRPAuthenticationFailureResponse(SRPAuthenticationFailureReason.MissingLoginIdentifier),
                StatusCodes.Status404NotFound);
        }

        string? clientProof = request.Form["proof"];
        if (clientProof is null)
        {
            return new PhpResult(
                new SRPAuthenticationFailureResponse(SRPAuthenticationFailureReason.MissingSRPClientProof),
                StatusCodes.Status400BadRequest);
        }

        string? operatingSystemType = request.Form["OSType"];
        if (operatingSystemType is null)
        {
            return new PhpResult(
                new SRPAuthenticationFailureResponse(SRPAuthenticationFailureReason.MissingOperatingSystemType),
                StatusCodes.Status400BadRequest);
        }

        string? majorVersion = request.Form["MajorVersion"];
        if (majorVersion is null)
        {
            return new PhpResult(
                new SRPAuthenticationFailureResponse(SRPAuthenticationFailureReason.MissingMajorVersion),
                StatusCodes.Status400BadRequest);
        }

        string? minorVersion = request.Form["MinorVersion"];
        if (minorVersion is null)
        {
            return new PhpResult(
                new SRPAuthenticationFailureResponse(SRPAuthenticationFailureReason.MissingMinorVersion),
                StatusCodes.Status400BadRequest);
        }

        string? microVersion = request.Form["MicroVersion"];
        if (microVersion is null)
        {
            return new PhpResult(
                new SRPAuthenticationFailureResponse(SRPAuthenticationFailureReason.MissingMicroVersion),
                StatusCodes.Status400BadRequest);
        }

        string? systemInformationHashes = request.Form["SysInfo"];
        if (systemInformationHashes is null)
        {
            return new PhpResult(
                new SRPAuthenticationFailureResponse(SRPAuthenticationFailureReason.MissingSystemInformation),
                StatusCodes.Status400BadRequest);
        }

        SRPAuthenticationSessionDataStageOne? stageOneData =
            await distributedCache.GetSRPAuthenticationSessionData(accountName);

        if (stageOneData is null)
        {
            logger.LogMissingSRPDataBug(accountName);
            return new PhpResult(
                new SRPAuthenticationFailureResponse(SRPAuthenticationFailureReason.MissingCachedSRPData),
                StatusCodes.Status422UnprocessableEntity);
        }

        await distributedCache.RemoveSRPAuthenticationSessionData(accountName);

        string? systemInformation = await distributedCache.GetSRPAuthenticationSystemInformation(accountName);
        if (systemInformation is null)
        {
            logger.LogMissingSysInfoBug(accountName);
            return new PhpResult(
                new SRPAuthenticationFailureResponse(SRPAuthenticationFailureReason.MissingSystemInformation),
                StatusCodes.Status422UnprocessableEntity);
        }

        await distributedCache.RemoveSRPAuthenticationSystemInformation(accountName);

        SRPAuthenticationSessionDataStageTwo stageTwoData = new(stageOneData, clientProof);
        string? serverProof = stageTwoData.ServerProof;

        if (serverProof is null)
        {
            return new PhpResult(
                new SRPAuthenticationFailureResponse(SRPAuthenticationFailureReason.IncorrectPassword),
                StatusCodes.Status401Unauthorized);
        }

        Account? account = await accountRepository.GetForAuthenticationAsync(accountName, cancellationToken);
        if (account is null)
        {
            return new NotFoundObjectResult(PhpSerialization.Serialize(
                new SRPAuthenticationFailureResponse(SRPAuthenticationFailureReason.AccountNotFound)));
        }

        if (account.Type is AccountType.Disabled || account.IsBanned)
        {
            logger.LogSRPAuthBlockedBannedAccount(
                account.Name,
                account.BanExpiration?.ToString("u") ?? "PERMANENT",
                account.BanReason ?? "NONE");

            return new UnauthorizedObjectResult(PhpSerialization.Serialize(
                new SRPAuthenticationFailureResponse(SRPAuthenticationFailureReason.AccountIsDisabled,
                    account.GetNameWithClanTag())));
        }

        if (account.Type is AccountType.ServerHost)
        {
            logger.LogSRPAuthBlockedServerHost(account.Name);
            return new UnauthorizedObjectResult(PhpSerialization.Serialize(
                new SRPAuthenticationFailureResponse(SRPAuthenticationFailureReason.IsServerHostingAccount)));
        }

        if (context.Connection.RemoteIpAddress is null)
        {
            logger.LogRemoteIPNullBug(accountName);
            return new UnprocessableEntityObjectResult(PhpSerialization.Serialize(
                new SRPAuthenticationFailureResponse(SRPAuthenticationFailureReason.MissingIPAddress)));
        }

        IPAddress rawRemoteIP = global::KONGOR.MasterServer.Services.Security.TrustedProxyIpResolver.ResolveClientIp(context);

        string remoteIPAddress;
        if (IPAddress.IsLoopback(rawRemoteIP))
        {
            remoteIPAddress = "127.0.0.1";
        }
        else if (rawRemoteIP.IsIPv4MappedToIPv6)
        {
            remoteIPAddress = rawRemoteIP.MapToIPv4().ToString();
        }
        else
        {
            remoteIPAddress = rawRemoteIP.ToString();
        }

        string? macAddress = null;
        string? osType = null;
        string? processor = null;
        string? videoCard = null;
        string? ram = null;
        string? systemInformationHash = null;
        string? sysInfoToSave = null;

        if (account.Type is not AccountType.Staff)
        {
            string agent = request.Headers.UserAgent.FirstOrDefault() ?? string.Empty;
            if (Log.UserAgentRegex().IsMatch(agent).Equals(false))
            {
                logger.LogUnexpectedUserAgent(account.GetNameWithClanTag(), agent);
                return new BadRequestObjectResult(PhpSerialization.Serialize(
                    new SRPAuthenticationFailureResponse(SRPAuthenticationFailureReason.UnexpectedUserAgent)));
            }

            if (account.IPAddressCollection.Contains(remoteIPAddress).Equals(false))
            {
                account.IPAddressCollection.Add(remoteIPAddress);
            }

            bool isNonWindowsClient = systemInformation.Equals("not running on windows", StringComparison.Ordinal);
            if (isNonWindowsClient)
            {
                osType = "not running on windows";
                sysInfoToSave = systemInformation;
                if (account.SystemInformationCollection.Contains(systemInformation).Equals(false))
                {
                    account.SystemInformationCollection.Add(systemInformation);
                }
            }
            else
            {
                string[] systemInformationDataPoints =
                    [.. systemInformation.Split('|', StringSplitOptions.RemoveEmptyEntries)];

                if (systemInformationDataPoints.Length is not 5)
                {
                    logger.LogUnexpectedSysInfo(account.GetNameWithClanTag(), systemInformation);
                    return new BadRequestObjectResult(PhpSerialization.Serialize(
                        new SRPAuthenticationFailureResponse(
                            SRPAuthenticationFailureReason.IncorrectSystemInformationFormat)));
                }

                macAddress = systemInformationDataPoints[0];
                osType = systemInformationDataPoints[1];
                processor = systemInformationDataPoints[2];
                videoCard = systemInformationDataPoints[3];
                ram = systemInformationDataPoints[4];
                sysInfoToSave = string.Join('|', systemInformationDataPoints.Skip(1));

                if (account.MACAddressCollection.Contains(macAddress).Equals(false))
                {
                    account.MACAddressCollection.Add(macAddress);
                }

                if (account.SystemInformationCollection.Contains(sysInfoToSave).Equals(false))
                {
                    account.SystemInformationCollection.Add(sysInfoToSave);
                }
            }

            systemInformationHash = systemInformationHashes.Split('|', StringSplitOptions.RemoveEmptyEntries)
                .Distinct().SingleOrDefault();

            if (systemInformationHash is null)
            {
                logger.LogUnexpectedSysInfoHashes(account.GetNameWithClanTag(), systemInformationHashes);
                return new BadRequestObjectResult(PhpSerialization.Serialize(
                    new SRPAuthenticationFailureResponse(
                        SRPAuthenticationFailureReason.IncorrectSystemInformationFormat)));
            }

            if (account.SystemInformationHashCollection.Contains(systemInformationHash).Equals(false))
            {
                account.SystemInformationHashCollection.Add(systemInformationHash);
            }
        }

        string hostHeader = context.Request.Headers["X-Forwarded-Host"].FirstOrDefault()
                            ?? context.Request.Host.Value
                            ?? string.Empty;

        (string chatServerHost, int chatServerClientConnectionsPort) =
            networkTopologyResolver.ResolveChatServer(rawRemoteIP, hostHeader, account.Name, account.Type.ToString());

        string requestBaseUrl = $"{context.Request.Scheme}://{context.Request.Host}";

        SRPAuthenticationHandlers.StageTwoResponseParameters parameters = new()
        {
            Account = account,
            ClanRoster = account.Clan?.Members ?? [],
            ServerProof = serverProof,
            ClientIPAddress = remoteIPAddress,
            ChatServer = (chatServerHost, chatServerClientConnectionsPort),
            CurrentSeason = operationalConfiguration.Value.CurrentSeason,
            ICBURL = requestBaseUrl
        };

        SRPAuthenticationResponseStageTwo response =
            SRPAuthenticationHandlers.GenerateStageTwoResponse(parameters, out string cookie);

        if (warEffortService is not null && warEffortService.IsEnabled)
        {
            try
            {
                response.QuestSystem = await warEffortService
                    .BuildClientPayloadAsync(account.ID, includeOnlyNativeTriggers: false, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception qe)
            {
                logger.LogWarning(qe, "[WAR_EFFORT] Failed to populate login quest_system for AccountId={AccountId}; keeping fallback.", account.ID);
            }
        }

        account.TimestampLastActive = TimeProvider.System.GetUtcNow();
        string rawUserAgent = request.Headers.UserAgent.FirstOrDefault() ?? "KONGOR-Client";
        Match userAgentMatch = Log.UserAgentRegex().Match(rawUserAgent);
        string clientVersion = userAgentMatch.Success && userAgentMatch.Groups["version"].Success
            ? userAgentMatch.Groups["version"].Value
            : (!string.IsNullOrWhiteSpace(majorVersion) && !string.IsNullOrWhiteSpace(minorVersion) && !string.IsNullOrWhiteSpace(microVersion)
                ? $"{majorVersion}.{minorVersion}.{microVersion}"
                : (rawUserAgent.Length > 128 ? rawUserAgent[..128] : rawUserAgent));

        AccountSession session = new()
        {
            AccountID = account.ID,
            UserID = account.User?.ID,
            IPAddress = remoteIPAddress,
            MACAddress = macAddress,
            HWID = systemInformationHash,
            SystemInformationRaw = systemInformation,
            SystemInformationHash = systemInformationHash,
            OperatingSystem = osType,
            Processor = processor,
            VideoCard = videoCard,
            RAM = ram,
            ClientVersion = clientVersion,
            UserAgent = rawUserAgent.Length > 256 ? rawUserAgent[..256] : rawUserAgent,
            SessionCookie = cookie,
            TimestampLogin = TimeProvider.System.GetUtcNow(),
            TimestampLastSeen = TimeProvider.System.GetUtcNow(),
            IsActive = true
        };

        await accountRepository.RecordAccountTelemetryAndSessionAsync(
            account.ID,
            session,
            remoteIPAddress,
            macAddress,
            sysInfoToSave,
            systemInformationHash,
            cancellationToken);

        await distributedCache.SetAccountNameForSessionCookie(cookie, account.Name);
        await distributedCache.StringSetAsync($"ACCOUNT-ID-SESSION-COOKIE:{account.ID}", cookie, TimeSpan.FromHours(24));

        // Emit asynchronous domain event for Discord presence, session telemetry, and auditing
        await eventBus.PublishAsync(
            new UserLoggedInEvent(account.ID, account.Name, clientVersion, remoteIPAddress, cookie),
            cancellationToken);

        logger.LogSRPAuthSuccess(account.Name, account.ID);

        return new PhpResult(response);
    }
}
