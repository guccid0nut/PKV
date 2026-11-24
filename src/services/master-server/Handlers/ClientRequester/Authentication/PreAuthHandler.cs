// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using KONGOR.MasterServer.Logging;
using MERRICK.DatabaseContext.Extensions;

namespace KONGOR.MasterServer.Handlers.ClientRequester;

/// <summary>
///     Handles SRP pre-authentication stage (f=pre_auth), generating ephemeral keys and session salts.
/// </summary>
public sealed class PreAuthHandler(
    IAccountRepository accountRepository,
    IDatabase distributedCache,
    ILogger<PreAuthHandler> logger,
    IConfiguration? configuration = null) : IClientRequestHandler
{
    public async Task<IActionResult> HandleRequestAsync(HttpContext context, CancellationToken cancellationToken = default)
    {
        HttpRequest request = context.Request;

        string? accountName = request.Form["login"];
        logger.LogPreAuthReceived(accountName ?? "NULL");

        if (accountName is null)
        {
            return new PhpResult(
                new SRPAuthenticationFailureResponse(SRPAuthenticationFailureReason.MissingLoginIdentifier),
                StatusCodes.Status404NotFound);
        }

        string? clientPublicEphemeral = request.Form["A"];
        if (clientPublicEphemeral is null)
        {
            return new PhpResult(
                new SRPAuthenticationFailureResponse(SRPAuthenticationFailureReason.MissingClientPublicEphemeral),
                StatusCodes.Status400BadRequest);
        }

        string? systemInformation = request.Form["SysInfo"];
        if (systemInformation is null)
        {
            return new PhpResult(
                new SRPAuthenticationFailureResponse(SRPAuthenticationFailureReason.MissingSystemInformation),
                StatusCodes.Status400BadRequest);
        }

        bool enableAntiEnumeration = configuration?.GetValue<bool>("Security:EnableAntiUsernameEnumeration") ??
            string.Equals(Environment.GetEnvironmentVariable("ENABLE_ANTI_USERNAME_ENUMERATION"), "true", StringComparison.OrdinalIgnoreCase);

        Account? account = await accountRepository.GetForPreAuthAsync(accountName, cancellationToken);
        if (account is null)
        {
            logger.LogPreAuthAccountNotFound(accountName);

            if (enableAntiEnumeration)
            {
                // Anti-enumeration defense: return a deterministic synthetic SRP challenge
                // so an external scanner cannot determine whether an account exists.
                string dummySalt = SRPAuthenticationSessionDataStageOne.GenerateSRPSessionSalt();
                string dummyVerifier = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes($"v:{accountName}"))).ToLowerInvariant();
                string dummyPasswordSalt = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes($"s:{accountName}"))).ToLowerInvariant();
                (string dummyPriv, string dummyPub) = SRPAuthenticationSessionDataStageOne.ComputeServerEphemeral(dummyVerifier);

                SRPAuthenticationSessionDataStageOne dummyData = new()
                {
                    LoginIdentifier = accountName,
                    SessionSalt = dummySalt,
                    PasswordSalt = dummyPasswordSalt,
                    PasswordHash = string.Empty,
                    ClientPublicEphemeral = clientPublicEphemeral,
                    Verifier = dummyVerifier,
                    ServerPrivateEphemeral = dummyPriv,
                    ServerPublicEphemeral = dummyPub
                };

                await distributedCache.SetSRPAuthenticationSessionData(accountName, dummyData);
                await distributedCache.SetSRPAuthenticationSystemInformation(accountName, systemInformation);

                return new PhpResult(new SRPAuthenticationResponseStageOne(dummyData));
            }

            return new PhpResult(
                new SRPAuthenticationFailureResponse(SRPAuthenticationFailureReason.AccountNotFound),
                StatusCodes.Status404NotFound);
        }

        if (account.Type is AccountType.Disabled || account.IsBanned)
        {
            logger.LogPreAuthBannedAccount(
                account.Name,
                account.BanExpiration?.ToString("u") ?? "PERMANENT",
                account.BanReason ?? "NONE");

            return new PhpResult(
                new SRPAuthenticationFailureResponse(
                    SRPAuthenticationFailureReason.AccountIsDisabled,
                    account.GetNameWithClanTag()),
                StatusCodes.Status401Unauthorized);
        }

        User user = account.User;
        string authenticationSessionSalt = SRPAuthenticationSessionDataStageOne.GenerateSRPSessionSalt();

        string verifier = SRPAuthenticationSessionDataStageOne.ComputeVerifier(
            authenticationSessionSalt,
            accountName,
            user.SRPPasswordHash);

        (string serverPrivateEphemeral, string serverPublicEphemeral) =
            SRPAuthenticationSessionDataStageOne.ComputeServerEphemeral(verifier);

        SRPAuthenticationSessionDataStageOne data = new()
        {
            LoginIdentifier = accountName,
            SessionSalt = authenticationSessionSalt,
            PasswordSalt = user.SRPPasswordSalt,
            PasswordHash = user.SRPPasswordHash,
            ClientPublicEphemeral = clientPublicEphemeral,
            Verifier = verifier,
            ServerPrivateEphemeral = serverPrivateEphemeral,
            ServerPublicEphemeral = serverPublicEphemeral
        };

        await distributedCache.SetSRPAuthenticationSessionData(accountName, data);
        await distributedCache.SetSRPAuthenticationSystemInformation(accountName, systemInformation);

        logger.LogPreAuthSuccess(accountName);

        return new PhpResult(new SRPAuthenticationResponseStageOne(data));
    }
}
