// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.Extensions.Cache;
using ASPIRE.Common.Security;
using System.Security.Cryptography;
using System.Text;
using Kongor.Core.Security;
using KONGOR.MasterServer.Extensions;
using KONGOR.MasterServer.Services.Security;
using MediatR;
using Microsoft.AspNetCore.Identity;
using NEXUS.Contracts.CQRS.AccountManagement;

namespace KONGOR.MasterServer.Controllers;

[ApiController]
[EnableRateLimiting(RateLimiterPolicies.Strict)]
public class AccountManagementController(
    IAccountRepository accountRepository,
    IInventoryRepository inventoryRepository,
    IDatabase distributedCache,
    ISessionContextResolver sessionResolver,
    ILogger<AccountManagementController> logger,
    IMediator? mediator = null) : ControllerBase
{
    private readonly IMediator? _mediator = mediator;
    private bool VerifyAccountPassword(User user, string password)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            if (logger.IsEnabled(LogLevel.Warning))
            {
                logger.LogWarning(
                    "[AccountManagement] Password verification failed: Password was null or empty for User ID {UserId}",
                    user.ID);
            }

            return false;
        }

        // 1. Check SRP hash assuming password was provided as MD5 (standard client name change form: _store_namechange_confirmpass)
        string srpHashFromMd5 = S2Crypto.ComputeSrpFromPrehashedMd5(password, user.SRPPasswordSalt);
        if (CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(srpHashFromMd5.ToLowerInvariant()),
            Encoding.UTF8.GetBytes(user.SRPPasswordHash.ToLowerInvariant())))
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    "[AccountManagement] Password verified successfully via SRP (MD5 mode) for User ID {UserId}",
                    user.ID);
            }

            return true;
        }

        // 2. Check SRP hash assuming password was provided as plaintext (standard client subaccount form: store_SubAccount_confirmpass)
        string srpHashFromPlain =
            S2Crypto.ComputeSrpFromPlaintext(password, user.SRPPasswordSalt);
        if (CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(srpHashFromPlain.ToLowerInvariant()),
            Encoding.UTF8.GetBytes(user.SRPPasswordHash.ToLowerInvariant())))
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    "[AccountManagement] Password verified successfully via SRP (Plaintext mode) for User ID {UserId}",
                    user.ID);
            }

            return true;
        }

        // 3. Fallback: Check PBKDF2 hash if set on the User
        if (!string.IsNullOrEmpty(user.PBKDF2PasswordHash))
        {
            PasswordHasher<User> hasher = new();
            PasswordVerificationResult result = hasher.VerifyHashedPassword(user, user.PBKDF2PasswordHash, password);
            if (result != PasswordVerificationResult.Failed)
            {
                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation(
                        "[AccountManagement] Password verified successfully via PBKDF2 for User ID {UserId}", user.ID);
                }

                return true;
            }
        }

        if (logger.IsEnabled(LogLevel.Warning))
        {
            logger.LogWarning(
                "[AccountManagement] Password verification failed for User ID {UserId}: credentials mismatch", user.ID);
        }

        return false;
    }

    [HttpPost("signup/nick_check.php")]
    [HttpPost("signup/ks_nick_check.php")]
    [HttpPost("signup/nick_exists_check.php")]
    public async Task<IActionResult> CheckNicknameAvailability(CancellationToken cancellationToken = default)
    {
        IFormCollection form = Request.Form;
        string nickname = form["nickname"].ToString();
        if (string.IsNullOrEmpty(nickname))
        {
            nickname = form["nick"].ToString();
        }

        if (_mediator is not null)
        {
            NicknameAvailabilityResultDto result = await _mediator.Send(
                new CheckNicknameAvailabilityQuery(nickname),
                cancellationToken);

            return new PhpResult(result.RawResponse, contentType: "text/plain; charset=utf-8");
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("[AccountManagement] Checking nickname availability: {Nickname}", nickname);
        }

        if (string.IsNullOrWhiteSpace(nickname) || nickname.Length < 3 || nickname.Length > 15)
        {
            Dictionary<string, object> invalidResponse = new()
            {
                ["0"] = false,
                ["success"] = 0,
                ["available"] = false,
                ["errors"] = "Nickname must be between 3 and 15 characters.",
                ["error_code"] = 1,
                ["error_msg"] = "Nickname must be between 3 and 15 characters."
            };
            return new PhpResult(invalidResponse, contentType: "text/plain; charset=utf-8");
        }

        bool isTaken = await accountRepository.ExistsByNicknameAsync(nickname, cancellationToken);

        Dictionary<string, object> response = new()
        {
            ["0"] = !isTaken,
            ["success"] = !isTaken ? 1 : 0,
            ["available"] = !isTaken,
            ["nickname"] = nickname,
            ["errors"] = isTaken ? "Nickname is already taken." : string.Empty,
            ["error_code"] = isTaken ? 2 : 0,
            ["error_msg"] = isTaken ? "Nickname is already taken." : string.Empty
        };

        return new PhpResult(response, contentType: "text/plain; charset=utf-8");
    }

    [HttpPost("client_require_nickchange.php")]
    public async Task<IActionResult> RequestNickChange(CancellationToken cancellationToken = default)
    {
        string newNick = string.Empty;
        string password = string.Empty;
        if (Request.HasFormContentType)
        {
            IFormCollection form = Request.Form;
            newNick = form["new_nickname"].ToString();
            if (string.IsNullOrEmpty(newNick))
            {
                newNick = form["nickname"].ToString();
            }

            password = form["password"].ToString();
            if (string.IsNullOrEmpty(password))
            {
                password = form["pass"].ToString();
            }
        }
        else
        {
            newNick = Request.Query["new_nickname"].ToString();
            if (string.IsNullOrEmpty(newNick))
            {
                newNick = Request.Query["nickname"].ToString();
            }

            password = Request.Query["password"].ToString();
            if (string.IsNullOrEmpty(password))
            {
                password = Request.Query["pass"].ToString();
            }
        }

        string cookie = Request.GetCookie() ?? string.Empty;

        Account? account = await sessionResolver.ResolveAccountAsync(HttpContext, cancellationToken);
        if (account == null)
        {
            Dictionary<string, object> unauthResponse = new()
            {
                ["0"] = false,
                ["success"] = 0,
                ["errors"] = "Session cookie invalid.",
                ["error_code"] = 1,
                ["error_msg"] = "Session cookie invalid."
            };
            return new PhpResult(unauthResponse, contentType: "text/plain; charset=utf-8");
        }

        if (_mediator is not null)
        {
            ChangeNicknameResultDto result = await _mediator.Send(
                new ChangeNicknameCommand(account.ID, newNick, password, cookie),
                cancellationToken);

            if (result.Success && !string.IsNullOrEmpty(result.NewNickname))
            {
                HttpContext.Items["SessionAccountName"] = result.NewNickname;
            }

            return new PhpResult(result.RawResponse, contentType: "text/plain; charset=utf-8");
        }

        if (string.IsNullOrEmpty(password) || !VerifyAccountPassword(account.User, password))
        {
            if (logger.IsEnabled(LogLevel.Warning))
            {
                logger.LogWarning(
                    "[AccountManagement] Nickname change rejected for '{AccountName}': Incorrect password provided",
                    account.Name);
            }

            Dictionary<string, object> passResponse = new()
            {
                ["0"] = false,
                ["success"] = 0,
                ["error"] = 1,
                ["errors"] = "24",
                ["error_code"] = 24,
                ["error_msg"] = "Incorrect password."
            };
            return new PhpResult(passResponse, contentType: "text/plain; charset=utf-8");
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("[AccountManagement] Nickname change requested: Old='{OldNick}', New='{NewNick}'",
                account.Name, newNick);
        }

        if (string.IsNullOrWhiteSpace(newNick) || newNick.Length < 3 || newNick.Length > 15 || !PerimeterSecuritySanitizer.IsValidNickname(newNick))
        {
            Dictionary<string, object> lenResponse = new()
            {
                ["0"] = false,
                ["success"] = 0,
                ["errors"] = "New nickname must be between 3 and 15 alphanumeric characters or underscores.",
                ["error_code"] = 2,
                ["error_msg"] = "New nickname must be between 3 and 15 alphanumeric characters or underscores."
            };
            return new PhpResult(lenResponse, contentType: "text/plain; charset=utf-8");
        }

        string oldName = account.Name;
        bool success = await accountRepository.ChangeNicknameAsync(account.ID, newNick, cancellationToken);
        if (!success)
        {
            Dictionary<string, object> takenResponse = new()
            {
                ["0"] = false,
                ["success"] = 0,
                ["errors"] = "Nickname is already in use by another player.",
                ["error_code"] = 3,
                ["error_msg"] = "Nickname is already in use by another player."
            };
            return new PhpResult(takenResponse, contentType: "text/plain; charset=utf-8");
        }

        if (!string.IsNullOrEmpty(cookie))
        {
            await distributedCache.SetAccountNameForSessionCookie(cookie, newNick);
            HttpContext.Items["SessionAccountName"] = newNick;
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("[AccountManagement] Nickname successfully changed from '{OldName}' to '{NewNick}'",
                oldName, newNick);
        }

        Dictionary<string, object> response = new()
        {
            ["0"] = true,
            ["success"] = 1,
            ["status"] = "OK",
            ["nickname"] = newNick,
            ["old_nickname"] = oldName,
            ["errors"] = string.Empty,
            ["error_code"] = 0,
            ["error_msg"] = string.Empty
        };

        return new PhpResult(response, contentType: "text/plain; charset=utf-8");
    }

    [HttpPost("client_require_identity.php")]
    public async Task<IActionResult> RequestSubAccount(CancellationToken cancellationToken = default)
    {
        IFormCollection form = Request.Form;
        string subAccountNick = form["sub_nickname"].ToString();
        if (string.IsNullOrEmpty(subAccountNick))
        {
            subAccountNick = form["nickname"].ToString();
        }

        Account? mainAccount = await sessionResolver.ResolveAccountAsync(HttpContext, cancellationToken);
        if (mainAccount == null)
        {
            Dictionary<string, object> unauthResponse = new()
            {
                ["0"] = false,
                ["success"] = 0,
                ["errors"] = "Session cookie invalid.",
                ["error_code"] = 1,
                ["error_msg"] = "Session cookie invalid."
            };
            return new PhpResult(unauthResponse, contentType: "text/plain; charset=utf-8");
        }

        if (mainAccount.IsBanned || mainAccount.Type == AccountType.Disabled || mainAccount.IsSuspended)
        {
            Dictionary<string, object> sanctionResponse = new()
            {
                ["0"] = false,
                ["success"] = 0,
                ["errors"] = "Cannot create subaccounts while account is under an active disciplinary sanction.",
                ["error_code"] = 3,
                ["error_msg"] = "Cannot create subaccounts while account is under an active disciplinary sanction."
            };
            return new PhpResult(sanctionResponse, contentType: "text/plain; charset=utf-8");
        }

        string password = form["password"].ToString();
        if (string.IsNullOrEmpty(password))
        {
            password = form["pass"].ToString();
        }

        if (_mediator is not null)
        {
            CreateSubAccountResultDto result = await _mediator.Send(
                new CreateSubAccountCommand(mainAccount.ID, subAccountNick, password),
                cancellationToken);

            return new PhpResult(result.RawResponse, contentType: "text/plain; charset=utf-8");
        }

        if (string.IsNullOrEmpty(password) || !VerifyAccountPassword(mainAccount.User, password))
        {
            if (logger.IsEnabled(LogLevel.Warning))
            {
                logger.LogWarning(
                    "[AccountManagement] Subaccount creation rejected for '{MainAccount}': Incorrect password provided",
                    mainAccount.Name);
            }

            Dictionary<string, object> passResponse = new()
            {
                ["0"] = false,
                ["success"] = 0,
                ["error"] = 1,
                ["errors"] = "24",
                ["error_code"] = 24,
                ["error_msg"] = "Incorrect password."
            };
            return new PhpResult(passResponse, contentType: "text/plain; charset=utf-8");
        }

        if (string.IsNullOrWhiteSpace(subAccountNick) || subAccountNick.Length < 3 || subAccountNick.Length > 15)
        {
            if (logger.IsEnabled(LogLevel.Warning))
            {
                logger.LogWarning(
                    "[AccountManagement] Subaccount creation rejected for '{MainAccount}': Invalid nickname length '{SubNick}' (Length: {Length})",
                    mainAccount.Name, subAccountNick, subAccountNick.Length);
            }

            Dictionary<string, object> lenResponse = new()
            {
                ["0"] = false,
                ["success"] = 0,
                ["errors"] = "Nickname must be between 3 and 15 characters.",
                ["error_code"] = 5,
                ["error_msg"] = "Nickname must be between 3 and 15 characters."
            };
            return new PhpResult(lenResponse, contentType: "text/plain; charset=utf-8");
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "[AccountManagement] Subaccount creation requested: Main='{MainAccount}', Sub='{SubNick}'",
                mainAccount.Name, subAccountNick);
        }

        Account? subAccount = await accountRepository.CreateSubAccountAsync(mainAccount.ID, subAccountNick, cancellationToken);
        if (subAccount is null)
        {
            Dictionary<string, object> takenResponse = new()
            {
                ["0"] = false,
                ["success"] = 0,
                ["errors"] = "Subaccount nickname is already taken.",
                ["error_code"] = 2,
                ["error_msg"] = "Subaccount nickname is already taken."
            };
            return new PhpResult(takenResponse, contentType: "text/plain; charset=utf-8");
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("[AccountManagement] Created subaccount '{SubNick}' (ID: {SubId}) for User {UserId}",
                subAccount.Name, subAccount.ID, mainAccount.User.ID);
        }

        Dictionary<string, object> response = new()
        {
            ["0"] = true,
            ["success"] = 1,
            ["status"] = "OK",
            ["account_id"] = subAccount.ID.ToString(),
            ["nickname"] = subAccount.Name,
            ["errors"] = string.Empty,
            ["error_code"] = 0,
            ["error_msg"] = string.Empty
        };

        return new PhpResult(response, contentType: "text/plain; charset=utf-8");
    }

    [HttpPost("client_require_resetstats.php")]
    public async Task<IActionResult> RequestStatReset(CancellationToken cancellationToken = default)
    {
        Account? account = await sessionResolver.ResolveAccountAsync(HttpContext, cancellationToken);
        if (account == null)
        {
            Dictionary<string, object> unauthResponse = new()
            {
                ["0"] = false,
                ["success"] = 0,
                ["errors"] = "Session cookie invalid.",
                ["error_code"] = 1,
                ["error_msg"] = "Session cookie invalid."
            };
            return new PhpResult(unauthResponse, contentType: "text/plain; charset=utf-8");
        }

        if (_mediator is not null)
        {
            ResetStatsResultDto result = await _mediator.Send(
                new ResetStatsCommand(account.ID),
                cancellationToken);

            return new PhpResult(result.RawResponse, contentType: "text/plain; charset=utf-8");
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("[AccountManagement] Stat reset requested for account: {AccountName}", account.Name);
        }

        // Consume stat reset item if present in inventory
        string[] statResetCodes = ["m.Stat Reset", "Stat Reset", "ac.resetstats", "other.163", "163"];
        foreach (string code in statResetCodes)
        {
            await inventoryRepository.RevokeItemAsync(account.ID, code, cancellationToken);
        }
        if (account.User.InventoryItems is not null)
        {
            account.User.InventoryItems.RemoveAll(inv =>
                statResetCodes.Any(c => c.Equals(inv.ItemCode, StringComparison.OrdinalIgnoreCase)));
        }

        Dictionary<string, object> response = new()
        {
            ["0"] = true,
            ["success"] = 1,
            ["status"] = "OK",
            ["account_id"] = account.ID.ToString(),
            ["nickname"] = account.Name,
            ["errors"] = string.Empty,
            ["error_code"] = 0,
            ["error_msg"] = string.Empty
        };

        return new PhpResult(response, contentType: "text/plain; charset=utf-8");
    }

    [HttpPost("signup/test_promocode_check.php")]
    public async Task<IActionResult> CheckPromoCode(CancellationToken cancellationToken = default)
    {
        IFormCollection form = Request.Form;
        string promoCode = form["promocode"].ToString();
        if (string.IsNullOrEmpty(promoCode))
        {
            promoCode = form["code"].ToString();
        }

        if (_mediator is not null)
        {
            ValidatePromoCodeResultDto result = await _mediator.Send(
                new ValidatePromoCodeQuery(promoCode),
                cancellationToken);

            return new PhpResult(result.RawResponse, contentType: "text/plain; charset=utf-8");
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("[AccountManagement] Validating promo code: {PromoCode}", promoCode);
        }

        bool isValid = await inventoryRepository.ValidatePromoCodeAsync(promoCode, cancellationToken);

        Dictionary<string, object> response = new()
        {
            ["0"] = isValid,
            ["success"] = isValid ? 1 : 0,
            ["valid"] = isValid,
            ["promocode"] = promoCode,
            ["discount"] = isValid ? "20%" : "0%",
            ["errors"] = isValid ? string.Empty : "Invalid or already redeemed promotional code.",
            ["error_code"] = isValid ? 0 : 1,
            ["error_msg"] = isValid ? string.Empty : "Invalid or already redeemed promotional code."
        };

        return new PhpResult(response, contentType: "text/plain; charset=utf-8");
    }

    [HttpPost("signup/client_create_account.php")]
    [HttpPost("signup/client_upgrade.php")]
    public IActionResult DirectToDiscordWebPortalSignup()
    {
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "[AccountManagement] In-game account creation attempted - redirecting to Discord Web Portal");
        }

        Dictionary<string, object> response = new()
        {
            ["0"] = false,
            ["success"] = 0,
            ["auth_type"] = "DISCORD_OAUTH2",
            ["portal_url"] = "https://nexus.local/auth/discord",
            ["message"] = "Please create your account via Discord on the NEXUS Web Portal.",
            ["error_code"] = 403,
            ["error_msg"] = "Account creation is managed via Discord OAuth on our Web Portal."
        };

        return new PhpResult(response, contentType: "text/plain; charset=utf-8");
    }

    [HttpPost("clientPassRetrieve.php")]
    public IActionResult DirectToDiscordWebPortalPassword()
    {
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "[AccountManagement] In-game password reset attempted - directing to Discord Web Portal");
        }

        Dictionary<string, object> response = new()
        {
            ["0"] = false,
            ["success"] = 0,
            ["auth_type"] = "DISCORD_OAUTH2",
            ["portal_url"] = "https://nexus.local/auth/discord",
            ["message"] = "Credentials and passwords are authenticated via Discord on the NEXUS Web Portal.",
            ["error_code"] = 403,
            ["error_msg"] = "Please manage your account credentials via Discord on our Web Portal."
        };

        return new PhpResult(response, contentType: "text/plain; charset=utf-8");
    }
}
