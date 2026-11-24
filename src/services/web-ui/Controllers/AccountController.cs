// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ReSharper disable All

#pragma warning disable CA1305
#pragma warning disable CA1304
#pragma warning disable CA1307
#pragma warning disable CA1862
#pragma warning disable CA1822
#pragma warning disable CA1869
#pragma warning disable CA1863
#pragma warning disable CS8602
#pragma warning disable CS8604
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using ASPIRE.Common.DTOs;
using ASPIRE.Common.Security;
using DAWNBRINGER.WebPortal.UI.Logging;
using DAWNBRINGER.WebPortal.UI.Models;
using DAWNBRINGER.WebPortal.UI.Utilities;
using MERRICK.DatabaseContext.Constants;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using NEXUS.Contracts.Clients;

namespace DAWNBRINGER.WebPortal.UI.Controllers;

// ReSharper disable All
public class AccountController(
    IUserApiClient userApiClient,
    IAdminApiClient adminApiClient,
    IHttpClientFactory httpClientFactory,
    ILogger<AccountController> logger,
    IOutputCacheStore outputCacheStore,
    IConfiguration configuration,
    IHostEnvironment? environment = null) : Controller
{
    private readonly IAdminApiClient _adminApiClient = adminApiClient;
    private readonly IConfiguration _configuration = configuration;
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly ILogger<AccountController> _logger = logger;
    private readonly IOutputCacheStore _outputCacheStore = outputCacheStore;
    private readonly IUserApiClient _userApiClient = userApiClient;
    private readonly IHostEnvironment? _environment = environment;

    private bool IsPasswordAuthEnabled =>
        string.Equals(_configuration?["KONGOR_STANDALONE"], "true", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(_configuration?["KONGOR_STANDALONE"], "1", StringComparison.OrdinalIgnoreCase);

    private async Task PopulateLoginViewDataAsync(string? returnUrl, string? invite = null, CancellationToken cancellationToken = default)
    {
        string? safeReturnUrl = !string.IsNullOrEmpty(returnUrl) && Url != null && Url.IsLocalUrl(returnUrl) ? returnUrl : null;

        string? configuredClientId = _configuration["discord-client-id"] ??
                                     _configuration["Authentication:Discord:ClientId"] ??
                                     _configuration["DISCORD_CLIENT_ID"] ??
                                     _configuration["Parameters:discord-client-id"];
        string? configuredClientSecret = _configuration["discord-client-secret"] ??
                                         _configuration["Authentication:Discord:ClientSecret"] ??
                                         _configuration["DISCORD_CLIENT_SECRET"] ??
                                         _configuration["Parameters:discord-client-secret"];

        bool isRealDiscordConfigured = !string.IsNullOrWhiteSpace(configuredClientId) &&
                                       !string.IsNullOrWhiteSpace(configuredClientSecret) &&
                                       ulong.TryParse(configuredClientId, out _) &&
                                       configuredClientId.Length >= 17;

        bool isInviteOnlyEnabled = await _userApiClient.IsInviteOnlyModeEnabledAsync(cancellationToken);

        ViewData["IsDiscordConfigured"] = isRealDiscordConfigured;
        ViewData["IsInviteOnlyEnabled"] = isInviteOnlyEnabled;
        ViewData["IsPasswordAuthEnabled"] = IsPasswordAuthEnabled;
        ViewData["ReturnUrl"] = safeReturnUrl;
        ViewData["AppliedInviteCode"] = invite?.Trim();
    }

    private async Task SignInWithUserAsync(UserDto user, AccountDto mainAccount)
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        List<Claim> claims =
        [
            new Claim(ClaimTypes.Name, mainAccount.Name),
            new Claim(ClaimTypes.Email, user.EmailAddress),
            new Claim(ClaimTypes.NameIdentifier, user.EmailAddress),
            new Claim("AccountId", mainAccount.ID.ToString()),
            new Claim("AccountType", mainAccount.Type.ToString())
        ];

        if (!string.IsNullOrEmpty(user.DiscordId))
        {
            claims.Add(new Claim("discord_id", user.DiscordId));
        }

        if (!string.IsNullOrEmpty(user.DiscordUsername))
        {
            claims.Add(new Claim("discord_username", user.DiscordUsername));
        }

        if (!string.IsNullOrEmpty(user.DiscordAvatar))
        {
            claims.Add(new Claim("urn:discord:avatar", user.DiscordAvatar));
        }

        bool hasStaffPrivilege = mainAccount.IsStaff ||
                                 mainAccount.Type is AccountType.Staff or AccountType.GameMaster or AccountType.MatchModerator ||
                                 user.Accounts.Any(a => a.IsStaff || a.Type is AccountType.Staff or AccountType.GameMaster or AccountType.MatchModerator) ||
                                 (user.Role != null && (user.Role.Name == UserRoles.Administrator ||
                                                        user.Role.Name.Equals("Staff", StringComparison.OrdinalIgnoreCase)));
        if (hasStaffPrivilege)
        {
            claims.Add(new Claim("is_staff", "true"));
            claims.Add(new Claim(ClaimTypes.Role, "Staff"));
        }

        ClaimsIdentity claimsIdentity = new(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        AuthenticationProperties properties = new()
        {
            IsPersistent = true,
            ExpiresUtc = TimeProvider.System.GetUtcNow().AddDays(14)
        };

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(claimsIdentity),
            properties);
    }

    [AllowAnonymous]
    [HttpGet]
    public async Task<IActionResult> Login(string? returnUrl = null, string? invite = null, CancellationToken cancellationToken = default)
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            if (!IsPasswordAuthEnabled && User.HasClaim("pending_registration", "true"))
            {
                return RedirectToAction("Register", new { invite });
            }

            if (!string.IsNullOrEmpty(returnUrl) && Url != null && Url.IsLocalUrl(returnUrl))
            {
                return LocalRedirect(returnUrl);
            }

            return RedirectToAction("Index", "Home");
        }

        if (!IsPasswordAuthEnabled && HttpContext?.Request?.Path.HasValue == true)
        {
            TempData?.Keep();
            List<string> queryParts = [];
            if (!string.IsNullOrEmpty(returnUrl) && (Url == null || Url.IsLocalUrl(returnUrl)))
            {
                queryParts.Add($"returnUrl={Uri.EscapeDataString(returnUrl)}");
            }
            if (!string.IsNullOrWhiteSpace(invite))
            {
                queryParts.Add($"invite={Uri.EscapeDataString(invite.Trim())}");
            }

            string target = queryParts.Count > 0 ? $"/?{string.Join("&", queryParts)}" : "/";
            _logger.LogInformation("[AccountController.Login] Discord-only portal mode active; redirecting GET {Path} -> '{Target}'.",
                HttpContext.Request.Path, target);
            return Redirect(target);
        }

        await PopulateLoginViewDataAsync(returnUrl, invite, cancellationToken);
        return View();
    }

    [AllowAnonymous]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> LoginPassword(string login, string password, string? returnUrl = null)
    {
        if (!IsPasswordAuthEnabled)
        {
            return NotFound();
        }

        string safeLogin = login?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(safeLogin) || string.IsNullOrWhiteSpace(password))
        {
            ModelState.AddModelError(string.Empty, "Invalid credentials.");
            await PopulateLoginViewDataAsync(returnUrl);
            ViewData["IsPasswordAuthEnabled"] = true;
            return View("Login");
        }

        AuthenticationTokenResponseDto? loginResult = null;
        try
        {
            loginResult = await _userApiClient.LogInUserAsync(new LogInUserRequestDto(safeLogin, password));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PasswordAuth] login fail exception for {Login}", safeLogin);
        }

        if (loginResult == null || string.IsNullOrEmpty(loginResult.Token))
        {
            _logger.LogWarning("[PasswordAuth] login fail for {Login}", safeLogin);
            ModelState.AddModelError(string.Empty, "Invalid credentials.");
            await PopulateLoginViewDataAsync(returnUrl);
            ViewData["IsPasswordAuthEnabled"] = true;
            return View("Login");
        }

        UserDto? user = await _userApiClient.GetUserByAccountNameAsync(safeLogin) ??
                        await _userApiClient.GetUserByEmailAsync(safeLogin);

        AccountDto? mainAccount = user?.Accounts.FirstOrDefault(a => a.IsMain) ??
                                  user?.Accounts.FirstOrDefault();

        if (user == null || mainAccount == null || mainAccount.Type == AccountType.Disabled || mainAccount.IsBanned)
        {
            _logger.LogWarning("[PasswordAuth] login fail for {Login} (disabled/banned or missing account)", safeLogin);
            ModelState.AddModelError(string.Empty, "Invalid credentials.");
            await PopulateLoginViewDataAsync(returnUrl);
            ViewData["IsPasswordAuthEnabled"] = true;
            return View("Login");
        }

        await SignInWithUserAsync(user, mainAccount);
        _logger.LogInformation("[PasswordAuth] login ok for {Login} (account={AccountName})", safeLogin, mainAccount.Name);

        if (!string.IsNullOrEmpty(returnUrl) && Url != null && Url.IsLocalUrl(returnUrl))
        {
            return LocalRedirect(returnUrl);
        }

        return RedirectToAction("Index", "Home");
    }

    [AllowAnonymous]
    [HttpGet]
    public IActionResult Signup(string? returnUrl = null, string? invite = null)
    {
        return RedirectToAction("Login", new { returnUrl, invite });
    }

    [AllowAnonymous]
    [HttpGet]
    public IActionResult DiscordSetupRequired(string? returnUrl = null)
    {
        string? host = Request.Host.Value;
        string scheme = Request.Scheme;
        string redirectUri = $"{scheme}://{host}/signin-discord";

        ViewData["RedirectUri"] = redirectUri;
        ViewData["ReturnUrl"] = returnUrl;
        return View();
    }

    [AllowAnonymous]
    [HttpGet]
    [HttpPost]
    public IActionResult LoginDiscord(string? returnUrl = null, string? invite = null)
    {
        string? configuredClientId = _configuration["discord-client-id"] ??
                                     _configuration["DISCORD_CLIENT_ID"] ??
                                     _configuration["Parameters:discord-client-id"] ??
                                     _configuration["Authentication:Discord:ClientId"];
        string? configuredClientSecret = _configuration["discord-client-secret"] ??
                                         _configuration["DISCORD_CLIENT_SECRET"] ??
                                         _configuration["Parameters:discord-client-secret"] ??
                                         _configuration["Authentication:Discord:ClientSecret"];

        bool isRealDiscordConfigured = !string.IsNullOrWhiteSpace(configuredClientId) &&
                                       !string.IsNullOrWhiteSpace(configuredClientSecret) &&
                                       ulong.TryParse(configuredClientId, out _) &&
                                       configuredClientId.Length >= 17;

        if (!isRealDiscordConfigured)
        {
            return RedirectToAction(nameof(DiscordSetupRequired), new { returnUrl });
        }

        string redirectUrl = !string.IsNullOrEmpty(returnUrl) && Url != null && Url.IsLocalUrl(returnUrl) ? returnUrl : "/";
        if (!string.IsNullOrEmpty(invite))
        {
            string sep = redirectUrl.Contains('?') ? "&" : "?";
            redirectUrl = $"{redirectUrl}{sep}invite={Uri.EscapeDataString(invite.Trim())}";
        }

        AuthenticationProperties properties = new() { RedirectUri = redirectUrl };
        return Challenge(properties, "Discord");
    }

    [AllowAnonymous]
    [HttpGet("discord-handshake")]
    public Task<IActionResult> DiscordHandshake() => DirectDiscordCallback(null, null, null, null);

    [AllowAnonymous]
    [HttpPost]
    [HttpGet]
    public async Task<IActionResult> DirectDiscordCallback(
        string? discordId,
        string? discordUsername,
        string? email,
        string? returnUrl = null,
        string? discordAvatar = null,
        string? invite = null,
        CancellationToken cancellationToken = default)
    {
        // If already authenticated via OAuth OnTicketReceived, fast-path straight to destination without re-authenticating
        if (string.IsNullOrEmpty(discordId) && User.Identity?.IsAuthenticated == true)
        {
            if (User.HasClaim("pending_registration", "true"))
            {
                return RedirectToAction("Register", new { invite });
            }

            if (!string.IsNullOrEmpty(returnUrl) && Url != null && Url.IsLocalUrl(returnUrl))
            {
                return LocalRedirect(returnUrl);
            }

            return RedirectToAction("Index", "Home");
        }

        IHostEnvironment? resolvedEnv = _environment ?? HttpContext?.RequestServices?.GetService<IHostEnvironment>();
        bool isMockDiscordAuthAllowed = resolvedEnv?.IsDevelopment() == true &&
                                        _configuration.GetValue<bool>("Authentication:AllowMockDiscordAuth");
        if (!isMockDiscordAuthAllowed)
        {
            if (!string.IsNullOrEmpty(discordId) ||
                !string.IsNullOrEmpty(email) ||
                !string.IsNullOrEmpty(discordUsername))
            {
                _logger.LogWarning(
                    "[SECURITY-ALERT] Blocked unauthenticated DirectDiscordCallback takeover attempt (DiscordId='{DiscordId}', Username='{Username}', Email='{Email}', Environment='{Environment}') from {RemoteIp}",
                    discordId,
                    discordUsername,
                    email,
                    resolvedEnv?.EnvironmentName ?? "Unknown",
                    HttpContext?.Connection.RemoteIpAddress?.ToString() ?? "unknown");
                return StatusCode(StatusCodes.Status403Forbidden);
            }

            return RedirectToAction("Login");
        }

        if (string.IsNullOrEmpty(discordId))
        {
            return RedirectToAction("Login");
        }

        UserDto? existingUser = await _userApiClient.GetUserByDiscordIdAsync(discordId, cancellationToken)
                                ?? await _userApiClient.GetUserByEmailAsync($"d_{discordId}", cancellationToken);

        if (existingUser != null && existingUser.Accounts.Count > 0)
        {
            UserDto user = await _userApiClient.RegisterOrLoginDiscordAsync(new DiscordAuthRequestDto(
                discordId,
                discordUsername,
                email,
                discordAvatar
            ), cancellationToken) ?? existingUser;

            AccountDto? mainAccount = user.Accounts.FirstOrDefault(a => a.IsMain)
                                      ?? user.Accounts.FirstOrDefault()
                                      ?? existingUser.Accounts.FirstOrDefault(a => a.IsMain)
                                      ?? existingUser.Accounts.FirstOrDefault();
            if (mainAccount != null)
            {
                if (mainAccount.Type == AccountType.Disabled || mainAccount.IsBanned)
                {
                    _logger.LogBlockedBannedLogin(mainAccount.Name);
                    await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                    if (TempData != null)
                    {
                        TempData["ErrorMessage"] =
                            $"Account '{mainAccount.Name}' is currently suspended or disabled. (Reason: {mainAccount.BanReason ?? "Administrative Action"})";
                    }

                    return Redirect("/Account/Login");
                }

                await SignInWithUserAsync(user, mainAccount);

                if (!string.IsNullOrEmpty(returnUrl) && Url != null && Url.IsLocalUrl(returnUrl))
                {
                    return LocalRedirect(returnUrl);
                }

                return RedirectToAction("Index", "Home");
            }
        }

        _logger.LogInformation(
            "[DirectDiscordCallback] First-time Discord user (DiscordId='{DiscordId}', Username='{Username}'); issuing pending_registration cookie and redirecting to /Account/Register.",
            discordId,
            discordUsername);

        List<Claim> pendingClaims =
        [
            new Claim(ClaimTypes.Name, discordUsername ?? discordId),
            new Claim(ClaimTypes.Email, existingUser?.EmailAddress ?? email ?? $"d_{discordId}"),
            new Claim(ClaimTypes.NameIdentifier, discordId),
            new Claim("discord_id", discordId),
            new Claim("pending_registration", "true")
        ];
        if (!string.IsNullOrEmpty(discordUsername))
        {
            pendingClaims.Add(new Claim("discord_username", discordUsername));
        }
        if (!string.IsNullOrEmpty(discordAvatar))
        {
            pendingClaims.Add(new Claim("urn:discord:avatar", discordAvatar));
        }
        ClaimsIdentity pendingIdentity = new(pendingClaims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(pendingIdentity));

        return RedirectToAction("Register", new { invite });
    }

    [AllowAnonymous]
    [HttpGet]
    public async Task<IActionResult> Register(string? invite = null, CancellationToken cancellationToken = default)
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            if (IsPasswordAuthEnabled)
            {
                bool isInviteOnlyMode = await _userApiClient.IsInviteOnlyModeEnabledAsync(cancellationToken);
                ViewData["IsInviteOnlyEnabled"] = isInviteOnlyMode;
                ViewData["InviteCode"] = invite?.Trim();
                ViewData["IsStandaloneMode"] = true;

                RegisterViewModel standaloneModel = new()
                {
                    IGN = string.Empty,
                    Password = string.Empty,
                    ConfirmPassword = string.Empty,
                    InviteCode = invite?.Trim() ?? string.Empty
                };

                return View(standaloneModel);
            }

            return RedirectToAction("Index", "Home");
        }

        string? discordId = User.FindFirst("discord_id")?.Value
                            ?? User.FindFirst("urn:discord:id")?.Value
                            ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                            ?? User.FindFirst("id")?.Value
                            ?? User.Claims.FirstOrDefault(c =>
                                c.Type.EndsWith("nameidentifier", StringComparison.OrdinalIgnoreCase))?.Value;

        if (!IsPasswordAuthEnabled && string.IsNullOrWhiteSpace(discordId))
        {
            _logger.LogWarning("[AccountController.Register] Rejected /Account/Register access without verified discord_id claim.");
            return RedirectToAction("Index", "Home");
        }

        string? email = User.FindFirst(ClaimTypes.Email)?.Value;
        string? name = User.Identity?.Name;

        UserDto? user = null;
        if (!string.IsNullOrEmpty(discordId))
        {
            user = await _userApiClient.GetUserByDiscordIdAsync(discordId, cancellationToken);
        }

        if (user == null && !string.IsNullOrEmpty(email))
        {
            user = await _userApiClient.GetUserByEmailAsync(email, cancellationToken);
        }

        if (user == null && !string.IsNullOrEmpty(name))
        {
            user = await _userApiClient.GetUserByAccountNameAsync(name, cancellationToken);
        }

        if (user != null && user.Accounts.Count > 0)
        {
            AccountDto? mainAccount = user.Accounts.FirstOrDefault(a => a.IsMain) ?? user.Accounts.FirstOrDefault();
            if (mainAccount != null)
            {
                await SignInWithUserAsync(user, mainAccount);
                return RedirectToAction("Index", "Home");
            }
        }

        bool isInviteOnly = await _userApiClient.IsInviteOnlyModeEnabledAsync(cancellationToken);
        ViewData["IsInviteOnlyEnabled"] = isInviteOnly;
        ViewData["InviteCode"] = invite?.Trim();

        RegisterViewModel model = new()
        {
            IGN = string.Empty,
            Password = string.Empty,
            ConfirmPassword = string.Empty,
            InviteCode = invite?.Trim()
        };

        return View(model);
    }

    [AllowAnonymous]
    [HttpGet]
    public IActionResult RegisterDirect()
    {
        if (!IsPasswordAuthEnabled) return NotFound();
        return View();
    }

    [AllowAnonymous]
    [HttpPost]
    public async Task<IActionResult> RegisterDirect(RegisterEmailViewModel model,
        CancellationToken cancellationToken = default)
    {
        if (!IsPasswordAuthEnabled) return NotFound();
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        return RedirectToAction("RegisterComplete", new { token = Guid.NewGuid().ToString() });
    }

    [AllowAnonymous]
    [HttpPost]
    public async Task<IActionResult> SignupMock([FromForm] string email, CancellationToken cancellationToken = default)
    {
        if (!IsPasswordAuthEnabled) return NotFound();
        if (string.IsNullOrWhiteSpace(email))
        {
            return RedirectToAction("Signup");
        }

        return RedirectToAction("RegisterComplete", new { token = Guid.NewGuid().ToString() });
    }

    [AllowAnonymous]
    [HttpGet]
    public IActionResult RegisterComplete(string? token)
    {
        if (!IsPasswordAuthEnabled) return NotFound();
        if (string.IsNullOrEmpty(token))
        {
            return RedirectToAction("Signup");
        }

        RegisterCompleteViewModel model = new()
        {
            Token = token,
            IGN = string.Empty,
            Password = string.Empty,
            ConfirmPassword = string.Empty
        };

        return View(model);
    }

    [AllowAnonymous]
    [HttpPost]
    public async Task<IActionResult> RegisterComplete(RegisterCompleteViewModel model,
        CancellationToken cancellationToken = default)
    {
        if (!IsPasswordAuthEnabled) return NotFound();
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        return await CompleteRegistration(new RegisterViewModel
        {
            IGN = model.IGN,
            Password = model.Password,
            ConfirmPassword = model.ConfirmPassword
        }, cancellationToken);
    }

    [AllowAnonymous]
    [HttpPost]
    public async Task<IActionResult> CompleteRegistration(RegisterViewModel model,
        CancellationToken cancellationToken = default)
    {
        bool isInviteOnly = await _userApiClient.IsInviteOnlyModeEnabledAsync(cancellationToken);
        ViewData["IsInviteOnlyEnabled"] = isInviteOnly;
        if (IsPasswordAuthEnabled)
        {
            ViewData["IsStandaloneMode"] = true;
        }

        if (isInviteOnly && string.IsNullOrWhiteSpace(model.InviteCode))
        {
            ModelState.AddModelError("InviteCode", "An invite code is required to register while private invite-only mode is active.");
            return View("Register", model);
        }

        if (!ModelState.IsValid)
        {
            return View("Register", model);
        }

        bool isStandaloneUnauthenticated = User.Identity?.IsAuthenticated != true && IsPasswordAuthEnabled;

        if (User.Identity?.IsAuthenticated != true && !IsPasswordAuthEnabled)
        {
            return RedirectToAction("Index", "Home");
        }

        string? discordId = null;
        string? email = null;
        string? discordUsername = null;
        string? avatarHash = null;
        string? identifier = null;

        if (User.Identity?.IsAuthenticated == true)
        {
            discordId = User.FindFirst("discord_id")?.Value
                        ?? User.FindFirst("urn:discord:id")?.Value
                        ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            email = User.FindFirst(ClaimTypes.Email)?.Value;
            discordUsername = User.FindFirst("discord_username")?.Value ?? User.FindFirst(ClaimTypes.Name)?.Value;
            avatarHash = User.FindFirst("urn:discord:avatar")?.Value;

            if (!IsPasswordAuthEnabled && string.IsNullOrWhiteSpace(discordId))
            {
                _logger.LogWarning("[AccountController.CompleteRegistration] Blocked CompleteRegistration without verified discord_id claim.");
                return RedirectToAction("Index", "Home");
            }

            if (!string.IsNullOrEmpty(discordId) && !discordId.Contains('@'))
            {
                identifier = $"d_{discordId}";
            }
            else if (!string.IsNullOrEmpty(email))
            {
                identifier = email;
            }
        }
        else if (isStandaloneUnauthenticated)
        {
            identifier = $"{model.IGN.Trim().ToLowerInvariant()}@players.kongor.local";
        }

        if (string.IsNullOrEmpty(identifier))
        {
            return RedirectToAction("Index", "Home");
        }

        bool ignTaken = await _userApiClient.ExistsAccountNameAsync(model.IGN, cancellationToken);
        if (ignTaken)
        {
            ModelState.AddModelError("IGN", "This IGN is already taken.");
            return View("Register", model);
        }

        try
        {
            RegisterDirectUserDTO dto = new(identifier, model.IGN, model.Password, discordId, discordUsername,
                avatarHash, model.InviteCode?.Trim());
            HttpClient client = _httpClientFactory.CreateClient("ZORGATH");
            HttpResponseMessage response =
                await client.PostAsJsonAsync("/Registration/Register", dto, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                string error = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogApiRegistrationFailed((int)response.StatusCode, error);

                string cleanError = error.Trim();
                if (cleanError.Length >= 2 && cleanError.StartsWith('"') && cleanError.EndsWith('"'))
                {
                    try
                    {
                        cleanError = System.Text.Json.JsonSerializer.Deserialize<string>(cleanError) ?? cleanError.Trim('"');
                    }
                    catch (System.Text.Json.JsonException)
                    {
                        cleanError = cleanError.Trim('"');
                    }
                }

                if (cleanError.Contains("invite code", StringComparison.OrdinalIgnoreCase) ||
                    cleanError.Contains("invite-only", StringComparison.OrdinalIgnoreCase))
                {
                    ModelState.AddModelError("InviteCode", cleanError);
                }
                else
                {
                    ModelState.AddModelError(string.Empty,
                        string.IsNullOrWhiteSpace(cleanError)
                            ? $"Registration failed ({response.StatusCode})."
                            : cleanError);
                }

                return View("Register", model);
            }

            UserDto? createdUser = await _userApiClient.GetUserByEmailAsync(identifier, cancellationToken)
                                   ?? await _userApiClient.GetUserByAccountNameAsync(model.IGN.Trim(), cancellationToken);

            if (createdUser != null)
            {
                AccountDto? mainAccount = createdUser.Accounts.FirstOrDefault(a => a.IsMain) ??
                                          createdUser.Accounts.FirstOrDefault();
                if (mainAccount != null)
                {
                    await SignInWithUserAsync(createdUser, mainAccount);

                    if (isStandaloneUnauthenticated)
                    {
                        _logger.LogInformation("[PasswordAuth] registered {Ign} invite={InviteState}",
                            model.IGN.Trim(),
                            string.IsNullOrWhiteSpace(model.InviteCode) ? "absent" : "present");
                    }

                    return RedirectToAction("Index", "Home");
                }
            }

            return RedirectToAction("Index", "Home");
        }
        catch (Exception ex)
        {
            _logger.LogRegistrationException(ex);
            ModelState.AddModelError(string.Empty, $"An error occurred during registration: {ex.Message}");
            return View("Register", model);
        }
    }


    [HttpPost]
    [HttpGet]
    public async Task<IActionResult> SyncDiscordAvatar(CancellationToken cancellationToken = default)
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            return RedirectToAction("Login");
        }

        string? ign = User.Identity.Name;
        if (string.IsNullOrWhiteSpace(ign))
        {
            return Unauthorized();
        }
        _logger.LogInformation("[AccountController] SyncDiscordAvatar requested for user '{AccountName}'", ign);

        string? discordId = User.FindFirst("discord_id")?.Value
                            ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                            ?? User.FindFirst("urn:discord:id")?.Value
                            ?? User.FindFirst("id")?.Value
                            ?? User.FindFirst("sub")?.Value;

        string? discordAvatar = User.FindFirst("urn:discord:avatar")?.Value
                                ?? User.FindFirst("avatar")?.Value;

        string? discordUsername = User.FindFirst(ClaimTypes.Name)?.Value
                                  ?? User.FindFirst("username")?.Value
                                  ?? User.FindFirst("discord_username")?.Value;

        string? email = User.FindFirst(ClaimTypes.Email)?.Value
                        ?? User.FindFirst("email")?.Value;

        if (string.IsNullOrEmpty(discordId))
        {
            UserDto? existingUser = await _userApiClient.GetUserByAccountNameAsync(ign, cancellationToken);
            if (existingUser != null && !string.IsNullOrEmpty(existingUser.DiscordId))
            {
                discordId = existingUser.DiscordId;
                if (string.IsNullOrEmpty(discordAvatar)) discordAvatar = existingUser.DiscordAvatar;
                if (string.IsNullOrEmpty(discordUsername)) discordUsername = existingUser.DiscordUsername;
                if (string.IsNullOrEmpty(email)) email = existingUser.EmailAddress;
            }
        }

        if (!string.IsNullOrEmpty(discordId))
        {
            _logger.LogInformation(
                "[AccountController] Syncing Discord details for '{AccountName}' (Discord: {DiscordId}, Avatar: {AvatarHash})",
                ign, discordId, discordAvatar ?? "NULL");

            UserDto? updated = await _userApiClient.RegisterOrLoginDiscordAsync(new DiscordAuthRequestDto(
                discordId,
                discordUsername,
                email,
                discordAvatar,
                ign
            ), cancellationToken);

            if (updated != null)
            {
                _logger.LogInformation(
                    "[AccountController] Successfully synced Discord avatar and in-game custom account icon for '{AccountName}'",
                    ign);
                if (TempData != null)
                {
                    TempData["SuccessMessage"] =
                        "Your Discord profile picture has been synced as your in-game custom account icon!";
                }
            }
            else if (TempData != null)
            {
                TempData["ErrorMessage"] = "Could not sync Discord avatar. Please try reconnecting Discord.";
            }
        }
        else if (TempData != null)
        {
            TempData["ErrorMessage"] = "No linked Discord account found to sync.";
        }

        return RedirectToAction("Settings");
    }

    [HttpGet("avatars/me")]
    public async Task<IActionResult> GetMyAvatar(CancellationToken cancellationToken = default)
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            return Redirect("/images/default-avatar.png");
        }

        string? ign = User.Identity.Name;
        if (string.IsNullOrWhiteSpace(ign))
        {
            return Redirect("/images/default-avatar.png");
        }

        UserDto? user = await _userApiClient.GetUserByAccountNameAsync(ign, cancellationToken);

        if (user == null)
        {
            string? discordId = User.FindFirst("discord_id")?.Value
                                ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                                ?? User.FindFirst("urn:discord:id")?.Value;
            if (!string.IsNullOrEmpty(discordId))
            {
                user = await _userApiClient.GetUserByDiscordIdAsync(discordId, cancellationToken);
            }
        }

        if (user != null && !string.IsNullOrEmpty(user.DiscordId) && !string.IsNullOrEmpty(user.DiscordAvatar))
        {
            string avatarHash = user.DiscordAvatar;
            string discordId = user.DiscordId;
            string discordUrl = avatarHash.StartsWith("a_", StringComparison.Ordinal)
                ? $"https://cdn.discordapp.com/avatars/{discordId}/{avatarHash}.gif"
                : $"https://cdn.discordapp.com/avatars/{discordId}/{avatarHash}.png?size=256";

            return Redirect(discordUrl);
        }
        else if (user != null && !string.IsNullOrEmpty(user.DiscordId))
        {
            ulong dId = ulong.TryParse(user.DiscordId, out ulong parsedId) ? parsedId : 0;
            return Redirect($"https://cdn.discordapp.com/embed/avatars/{(dId >> 22) % 6}.png");
        }

        return Redirect("/images/default-avatar.png");
    }

    [HttpGet("avatars/by-name/{accountName}")]
    public async Task<IActionResult> GetAvatarByName(string accountName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountName))
        {
            return Redirect("/images/default-avatar.png");
        }

        UserDto? user = await _userApiClient.GetUserByAccountNameAsync(accountName, cancellationToken);

        if (user != null && !string.IsNullOrEmpty(user.DiscordId) &&
            !string.IsNullOrEmpty(user.DiscordAvatar))
        {
            string avatarHash = user.DiscordAvatar;
            string discordId = user.DiscordId;
            string discordUrl = avatarHash.StartsWith("a_", StringComparison.Ordinal)
                ? $"https://cdn.discordapp.com/avatars/{discordId}/{avatarHash}.gif"
                : $"https://cdn.discordapp.com/avatars/{discordId}/{avatarHash}.png?size=256";

            return Redirect(discordUrl);
        }
        else if (user != null && !string.IsNullOrEmpty(user.DiscordId))
        {
            ulong dId = ulong.TryParse(user.DiscordId, out ulong parsedId) ? parsedId : 0;
            return Redirect($"https://cdn.discordapp.com/embed/avatars/{(dId >> 22) % 6}.png");
        }

        return Redirect("/images/default-avatar.png");
    }

    [HttpPost("apply-host")]
    [HttpPost("account/apply-host")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApplyHost([FromForm] ApplyHostRequestDto request,
        CancellationToken cancellationToken = default)
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            return RedirectToAction("Login");
        }

        string? ign = User.Identity.Name;
        if (string.IsNullOrWhiteSpace(ign))
        {
            _logger.LogWarning(
                "[SECURITY-ALERT] Rejected ApplyHost with empty authenticated account claim from {RemoteIp}",
                HttpContext?.Connection.RemoteIpAddress?.ToString() ?? "unknown");
            return Unauthorized();
        }

        AccountDto? mainAccount = await _userApiClient.GetAccountByNameAsync(ign, cancellationToken);

        if (mainAccount == null)
        {
            return NotFound("Account not found.");
        }

        bool success = await _userApiClient.ApplyHostAsync(request, cancellationToken);

        if (Request?.Headers.Accept.ToString().Contains("application/json") == true ||
            string.Equals(Request?.Headers.XRequestedWith.ToString(), "XMLHttpRequest",
                StringComparison.OrdinalIgnoreCase))
        {
            return Json(new
            {
                success,
                message = success
                    ? "Host application submitted successfully! Staff will review your application."
                    : "Failed to submit host application."
            });
        }

        if (TempData != null)
        {
            TempData["SuccessMessage"] =
                "Volunteer Host application submitted successfully! Staff will review your server node.";
        }

        return RedirectToAction("Settings");
    }

    [HttpGet("host-credentials-info")]
    public async Task<IActionResult> GetHostApplicationDetails(CancellationToken cancellationToken = default)
    {
        if (User?.Identity?.IsAuthenticated != true)
        {
            return Unauthorized(new { success = false, message = "Authentication required." });
        }

        string? ign = User.Identity.Name;
        if (string.IsNullOrWhiteSpace(ign))
        {
            return Unauthorized(new { success = false, message = "Authentication required." });
        }

        AccountDto? callerAccount = await _userApiClient.GetAccountByNameAsync(ign, cancellationToken);

        if (callerAccount == null)
        {
            return NotFound(new { success = false, message = "Account not found." });
        }

        HostTelemetryResultDto? telemetry =
            await _userApiClient.GetHostTelemetryAsync(callerAccount.Name, cancellationToken);
        HostApplicationDto? application = telemetry?.PrimaryHostApplication;

        if (application == null)
        {
            return NotFound(new { success = false, message = "No active approved host credentials found." });
        }

        return Json(new
        {
            success = true,
            hostUsername = application.HostUsername,
            serverName = application.ServerNamePrefix,
            location = application.Location,
            instances = application.Instances,
            status = application.Status.ToString()
        });
    }

    [HttpGet("account/settings")]
    [HttpGet("settings")]
    public async Task<IActionResult> Settings(CancellationToken cancellationToken = default)
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            return RedirectToAction("Login", new { returnUrl = "/account/settings" });
        }

        string? ign = User.Identity.Name;
        if (string.IsNullOrWhiteSpace(ign))
        {
            return RedirectToAction("Login", new { returnUrl = "/account/settings" });
        }

        UserDto? user = await _userApiClient.GetUserByAccountNameAsync(ign, cancellationToken);

        if (user == null)
        {
            string? discordId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!string.IsNullOrEmpty(discordId))
            {
                user = await _userApiClient.GetUserByDiscordIdAsync(discordId, cancellationToken);
            }
        }

        if (user == null)
        {
            return RedirectToAction("Login");
        }

        AccountDto? mainAcc = user.Accounts.FirstOrDefault(a => a.IsMain) ?? user.Accounts.FirstOrDefault();

        StreamerApplicationDto? streamerApp =
            await _userApiClient.GetStreamerApplicationAsync(user.ID, cancellationToken);
        HostApplicationDto? hostApp = await _userApiClient.GetHostApplicationAsync(user.ID, cancellationToken);

        var quotaTask = _userApiClient.GetUserInviteQuotaAsync(user.ID, cancellationToken);
        var myInvitesTask = _userApiClient.GetMyInvitesAsync(user.ID, cancellationToken);
        var isInviteOnlyTask = _userApiClient.IsInviteOnlyModeEnabledAsync(cancellationToken);

        await Task.WhenAll(quotaTask, myInvitesTask, isInviteOnlyTask);

        var quota = await quotaTask;
        var myInvites = await myInvitesTask;
        bool isInviteOnly = await isInviteOnlyTask;

        string? effectiveTwitchUsername = user.TwitchUsername ?? streamerApp?.TwitchUsername;

        bool isSpecialStreamerRole = mainAcc != null && (
            mainAcc.Type == AccountType.Staff ||
            mainAcc.Type == AccountType.GameMaster ||
            mainAcc.Type == AccountType.MatchModerator ||
            mainAcc.Type == AccountType.MatchCaster ||
            mainAcc.Type == AccountType.VIP
        );

        bool isStreamer = (mainAcc != null && mainAcc.Type == AccountType.Streamer) ||
                          (streamerApp != null && streamerApp.Status == StreamerApplicationStatus.Approved) ||
                          (isSpecialStreamerRole && !string.IsNullOrWhiteSpace(effectiveTwitchUsername));

        AccountSettingsViewModel model = new()
        {
            UserId = user.ID,
            MainAccountName = mainAcc?.Name ?? ign,
            EmailAddress = user.EmailAddress,
            DiscordUsername = user.DiscordUsername,
            DiscordId = user.DiscordId,
            DiscordAvatar = user.DiscordAvatar,
            TwitchUsername = user.TwitchUsername ?? streamerApp?.TwitchUsername,
            TwitchId = user.TwitchId ?? streamerApp?.TwitchId,
            IsStreamer = isStreamer,
            RoleName = user.Role?.Name ?? "User",
            AccountTypeName = mainAcc?.Type.ToString() ?? "Normal",
            GoldCoins = user.GoldCoins,
            SilverCoins = user.SilverCoins,
            PlinkoTickets = user.PlinkoTickets,
            TimestampCreated =
                mainAcc?.TimestampCreated ??
                user.Accounts.FirstOrDefault()?.TimestampCreated ?? TimeProvider.System.GetUtcNow(),
            HasInGamePasswordSet = user.HasInGamePasswordSet,
            StreamerApp = streamerApp,
            HostApp = hostApp,
            ApprovedHostCount = hostApp?.Status == HostApplicationStatus.Approved ? 1 : 0,
            TotalCompletedGames = quota?.TotalCompletedGames ?? 0,
            MaxAllowedActiveCodes = quota?.MaxAllowedActive ?? 0,
            ActiveUnusedCodes = quota?.ActiveUnused ?? 0,
            RemainingInviteQuota = quota?.RemainingQuota ?? 0,
            CanGenerateInvite = quota?.Success ?? false,
            MyInvites = [.. myInvites ?? []],
            IsInviteOnlyModeActive = isInviteOnly,
            ApplyHostModel =
                new ApplyHostRequestDto
                {
                    Location = hostApp?.Location ?? "USE",
                    Instances = hostApp?.Instances ?? 2,
                    ServerNamePrefix = hostApp?.ServerNamePrefix ?? $"{mainAcc?.Name ?? ign} Arena",
                    ApplicationNotes = hostApp?.ApplicationNotes
                },
            ApplyStreamerModel = new ApplyStreamerRequestDto
            {
                TwitchUsername = user.TwitchUsername ?? streamerApp?.TwitchUsername ?? string.Empty,
                TwitchId = user.TwitchId ?? streamerApp?.TwitchId,
                TwitchChannelUrl = streamerApp?.TwitchChannelUrl,
                ApplicationNotes = streamerApp?.ApplicationNotes
            },
            LinkedAccounts =
            [
                .. user.Accounts.OrderBy(a => !a.IsMain).ThenBy(a => a.ID).Select(a =>
                    new AccountItemDto
                    {
                        Id = a.ID,
                        Name = a.Name,
                        IsMain = a.IsMain,
                        Type = a.Type.ToString(),
                        TimestampCreated = a.TimestampCreated
                    })
            ]
        };

        return View(model);
    }

    [HttpPost("account/generate-invite")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> GenerateInvite(CancellationToken cancellationToken = default)
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            return Unauthorized(new { success = false, message = "Authentication required." });
        }

        string? ign = User.Identity.Name;
        if (string.IsNullOrWhiteSpace(ign))
        {
            return Unauthorized(new { success = false, message = "Authentication required." });
        }

        UserDto? user = await _userApiClient.GetUserByAccountNameAsync(ign, cancellationToken);
        if (user == null)
        {
            string? discordId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!string.IsNullOrEmpty(discordId))
            {
                user = await _userApiClient.GetUserByDiscordIdAsync(discordId, cancellationToken);
            }
        }

        if (user == null)
        {
            return NotFound(new { success = false, message = "User not found." });
        }

        var result = await _userApiClient.GenerateInviteCodeAsync(user.ID, cancellationToken);
        if (result == null || !result.Success)
        {
            return Json(new
            {
                success = false,
                message = result?.Message ?? "Could not generate invite code. Please check match requirements or active quota."
            });
        }

        return Json(new
        {
            success = true,
            code = result.Code,
            message = result.Message,
            remainingQuota = result.RemainingQuota,
            maxAllowedActive = result.MaxAllowedActive,
            activeUnused = result.ActiveUnused,
            totalCompletedGames = result.TotalCompletedGames
        });
    }

    [HttpPost("connect-twitch")]
    [HttpGet("connect-twitch")]
    public IActionResult ConnectTwitch(string? returnUrl = null)
    {
        return Redirect("/Account/Settings?openStreamerModal=true");
    }

    [HttpGet("twitch-handshake")]
    [HttpGet("account/twitch-handshake")]
    public async Task<IActionResult> TwitchHandshake(string? returnUrl = null,
        CancellationToken cancellationToken = default)
    {
        return Redirect("/Account/Settings?openStreamerModal=true");
    }

    [HttpPost("disconnect-twitch")]
    [HttpPost("account/disconnect-twitch")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DisconnectTwitch(CancellationToken cancellationToken = default)
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            return RedirectToAction("Login");
        }

        string? ign = User.Identity.Name;
        if (string.IsNullOrWhiteSpace(ign))
        {
            return RedirectToAction("Login");
        }

        UserDto? user = await _userApiClient.GetUserByAccountNameAsync(ign, cancellationToken);

        if (user != null)
        {
            await _userApiClient.UnlinkTwitchAsync(user.ID, cancellationToken);
            if (TempData != null)
            {
                TempData["SuccessMessage"] = "Twitch account disconnected successfully.";
            }
        }

        return RedirectToAction("Settings");
    }

    [HttpPost("link-twitch")]
    [HttpPost("account/link-twitch")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> LinkTwitch([FromForm] LinkTwitchRequestDto request,
        CancellationToken cancellationToken = default)
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            return RedirectToAction("Login");
        }

        string? ign = User.Identity.Name;
        if (string.IsNullOrWhiteSpace(ign))
        {
            return RedirectToAction("Login");
        }

        UserDto? user = await _userApiClient.GetUserByAccountNameAsync(ign, cancellationToken);

        if (user == null)
        {
            if (TempData != null)
            {
                TempData["ErrorMessage"] = "User account could not be found.";
            }

            return RedirectToAction("Settings");
        }

        string rawTwitch = request.TwitchUsername?.Trim() ?? string.Empty;
        if (rawTwitch.Contains("twitch.tv/", StringComparison.OrdinalIgnoreCase))
        {
            rawTwitch = rawTwitch
                .Substring(rawTwitch.LastIndexOf("twitch.tv/", StringComparison.OrdinalIgnoreCase) + 10).Trim('/');
        }

        rawTwitch = rawTwitch.TrimStart('@');
        await _userApiClient.LinkTwitchAsync(user.ID, request.TwitchId ?? string.Empty, rawTwitch, cancellationToken);

        string message = string.IsNullOrEmpty(rawTwitch)
            ? "Twitch account unlinked successfully."
            : $"Twitch account @{rawTwitch} linked successfully!";

        if (TempData != null)
        {
            TempData["SuccessMessage"] = message;
        }

        if (Request?.Headers.Accept.ToString().Contains("application/json") == true || string.Equals(
                Request?.Headers.XRequestedWith.ToString(), "XMLHttpRequest", StringComparison.OrdinalIgnoreCase))
        {
            return Json(new { success = true, twitchUsername = rawTwitch, message = message });
        }

        return RedirectToAction("Settings");
    }

    [HttpPost("apply-streamer")]
    [HttpPost("account/apply-streamer")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApplyStreamer([FromForm] ApplyStreamerRequestDto request,
        CancellationToken cancellationToken = default)
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            return RedirectToAction("Login");
        }

        string? ign = User.Identity.Name;
        if (string.IsNullOrWhiteSpace(ign))
        {
            return RedirectToAction("Login");
        }

        AccountDto? mainAccount = await _userApiClient.GetAccountByNameAsync(ign, cancellationToken);

        if (mainAccount == null)
        {
            return NotFound("Account not found.");
        }

        UserDto? user = await _userApiClient.GetUserByAccountNameAsync(ign, cancellationToken);
        if (user == null)
        {
            return NotFound("User not found.");
        }

        string rawTwitch = !string.IsNullOrWhiteSpace(request.TwitchUsername)
            ? request.TwitchUsername
            : (user.TwitchUsername ?? string.Empty);
        string cleanTwitch = rawTwitch.Trim().TrimStart('@');
        string channelUrl = request.TwitchChannelUrl ??
                            (!string.IsNullOrEmpty(cleanTwitch) ? $"https://twitch.tv/{cleanTwitch}" : string.Empty);
        string? twitchId = !string.IsNullOrWhiteSpace(request.TwitchId) ? request.TwitchId : user.TwitchId;

        ApplyStreamerClientDto clientDto = new(
            user.ID,
            channelUrl,
            null,
            0,
            cleanTwitch,
            twitchId,
            request.ApplicationNotes
        );

        bool success = await _userApiClient.ApplyStreamerAsync(clientDto, cancellationToken);

        string streamerSuccessMessage =
            "Thanks for applying to be a streamer! If approved, when you go live we will try to stream your content directly on to the homepage to help gain more exposure across the community.";
        if (TempData != null)
        {
            TempData["SuccessMessage"] = streamerSuccessMessage;
        }

        if (Request?.Headers.Accept.ToString().Contains("application/json") == true || string.Equals(
                Request?.Headers.XRequestedWith.ToString(), "XMLHttpRequest", StringComparison.OrdinalIgnoreCase))
        {
            return Json(new { success = true, message = streamerSuccessMessage });
        }

        return RedirectToAction("Settings");
    }

    [HttpPost("account/change-password")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangePassword([FromForm] ChangePasswordViewModel model,
        CancellationToken cancellationToken = default)
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            return RedirectToAction("Login");
        }

        if (model.NewPassword != model.ConfirmPassword)
        {
            TrySetTempData("ErrorMessage", "New password and confirmation password do not match.");
            return RedirectToAction("Settings");
        }

        if (string.IsNullOrWhiteSpace(model.NewPassword) || model.NewPassword.Length < 4)
        {
            TrySetTempData("ErrorMessage", "New password must be at least 4 characters long.");
            return RedirectToAction("Settings");
        }

        string? ign = User.Identity.Name;
        if (string.IsNullOrWhiteSpace(ign))
        {
            return RedirectToAction("Login");
        }

        UserDto? user = await _userApiClient.GetUserByAccountNameAsync(ign, cancellationToken);

        if (user == null)
        {
            TrySetTempData("ErrorMessage", "User account could not be found.");
            return RedirectToAction("Settings");
        }

        if (user.HasInGamePasswordSet)
        {
            string? currentPassword = !string.IsNullOrWhiteSpace(model.CurrentPassword)
                ? model.CurrentPassword
                : Request?.HasFormContentType == true
                    ? Request.Form["CurrentPassword"].FirstOrDefault()
                    : null;

            if (string.IsNullOrWhiteSpace(currentPassword))
            {
                _logger.LogWarning(
                    "[SECURITY-ALERT] ChangePassword rejected for '{Ign}' (#{UserId}) from {RemoteIp}: missing CurrentPassword when HasInGamePasswordSet=true",
                    ign,
                    user.ID,
                    HttpContext?.Connection.RemoteIpAddress?.ToString() ?? "unknown");
                TrySetTempData("ErrorMessage", "Current password is required to change an existing in-game password.");
                return RedirectToAction("Settings");
            }

            AuthenticationTokenResponseDto? verifiedLogin = await _userApiClient.LogInUserAsync(
                new LogInUserRequestDto(ign, currentPassword),
                cancellationToken);
            if (verifiedLogin == null || string.IsNullOrEmpty(verifiedLogin.Token))
            {
                _logger.LogWarning(
                    "[SECURITY-ALERT] ChangePassword rejected for '{Ign}' (#{UserId}) from {RemoteIp}: CurrentPassword verification failed",
                    ign,
                    user.ID,
                    HttpContext?.Connection.RemoteIpAddress?.ToString() ?? "unknown");
                TrySetTempData("ErrorMessage", "Current password is incorrect.");
                return RedirectToAction("Settings");
            }

            await _userApiClient.InvalidateTokenAsync(verifiedLogin.Token, cancellationToken);
        }

        string srpSalt = SRP.GenerateSRPPasswordSalt();
        string srpHash = SRP.ComputeSRPPasswordHash(model.NewPassword, srpSalt, passwordIsHashed: false);
        string pbkdf2Hash = new PasswordHasher<UserDto>().HashPassword(user, model.NewPassword);

        await _userApiClient.SetInGamePasswordAsync(user.ID, srpSalt, srpHash, pbkdf2Hash, cancellationToken);

        TrySetTempData("SuccessMessage",
            "Password successfully updated! You can now log into the HoN Game Client with this password.");

        return RedirectToAction("Settings");
    }

    private void TrySetTempData(string key, object? value)
    {
        try
        {
            if (TempData != null)
            {
                TempData[key] = value;
            }
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogDebug(ex, "[AccountController] TempData unavailable while setting key {Key}", key);
        }
    }

    [HttpPost("account/regenerate-host-password")]
    [HttpPost("regenerate-host-password")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RegenerateHostPassword(CancellationToken cancellationToken = default)
    {
        if (User.Identity?.IsAuthenticated != true) return Unauthorized();
        string? ign = User.Identity.Name;
        if (string.IsNullOrWhiteSpace(ign)) return Unauthorized();
        AccountDto? acc = await _userApiClient.GetAccountByNameAsync(ign, cancellationToken);
        if (acc == null) return NotFound(new { success = false, message = "Account not found" });
        if (acc.UserID.HasValue)
        {
            HostApplicationDto? hostApp = await _userApiClient.GetHostApplicationAsync(acc.UserID.Value, cancellationToken);
            if (hostApp == null || hostApp.Status != HostApplicationStatus.Approved)
            {
                _logger.LogWarning(
                    "[SECURITY-ALERT] Rejected RegenerateHostPassword for '{Ign}' (#{AccountId}) without Approved host application from {RemoteIp}",
                    ign,
                    acc.ID,
                    HttpContext?.Connection.RemoteIpAddress?.ToString() ?? "unknown");
                return Forbid();
            }
        }

        ResetCredentialResponse resp = await _adminApiClient.ResetHostPasswordAsync(acc.ID, cancellationToken);
        return Json(new { success = !string.IsNullOrEmpty(resp.Value), hostPassword = resp.Value });
    }

    [HttpPost("account/regenerate-host-token")]
    [HttpPost("regenerate-host-auth-token")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RegenerateHostAuthToken(CancellationToken cancellationToken = default)
    {
        if (User.Identity?.IsAuthenticated != true) return Unauthorized();
        string? ign = User.Identity.Name;
        if (string.IsNullOrWhiteSpace(ign)) return Unauthorized();
        AccountDto? acc = await _userApiClient.GetAccountByNameAsync(ign, cancellationToken);
        if (acc == null) return NotFound(new { success = false, message = "Account not found" });
        if (acc.UserID.HasValue)
        {
            HostApplicationDto? hostApp = await _userApiClient.GetHostApplicationAsync(acc.UserID.Value, cancellationToken);
            if (hostApp == null || hostApp.Status != HostApplicationStatus.Approved)
            {
                _logger.LogWarning(
                    "[SECURITY-ALERT] Rejected RegenerateHostAuthToken for '{Ign}' (#{AccountId}) without Approved host application from {RemoteIp}",
                    ign,
                    acc.ID,
                    HttpContext?.Connection.RemoteIpAddress?.ToString() ?? "unknown");
                return Forbid();
            }
        }

        ResetCredentialResponse resp = await _adminApiClient.ResetHostAuthTokenAsync(acc.ID, cancellationToken);
        return Json(new
            { success = !string.IsNullOrEmpty(resp.Value), authToken = resp.Value, hostAuthToken = resp.Value });
    }

    [HttpGet("host-credentials")]
    [HttpGet("account/get-host-credentials")]
    [HttpGet("account/reveal-host-password")]
    public async Task<IActionResult> GetHostCredentials(CancellationToken cancellationToken = default)
    {
        if (User.Identity?.IsAuthenticated != true) return Unauthorized();
        string? ign = User.Identity.Name;
        if (string.IsNullOrWhiteSpace(ign)) return Unauthorized();
        AccountDto? acc = await _userApiClient.GetAccountByNameAsync(ign, cancellationToken);
        if (acc == null) return NotFound(new { success = false, message = "Account not found" });

        HostTelemetryResultDto? telemetry = await _userApiClient.GetHostTelemetryAsync(acc.Name, cancellationToken);
        HostApplicationDto? hostApp = acc.UserID.HasValue
            ? await _userApiClient.GetHostApplicationAsync(acc.UserID.Value, cancellationToken)
            : null;

        if (hostApp == null || hostApp.Status != HostApplicationStatus.Approved)
        {
            return NotFound(new { success = false, message = "Approved host application not found" });
        }

        string? hostPassword = !string.IsNullOrEmpty(hostApp.EncryptedHostPassword)
            ? HostSecurityService.DecryptPassword(hostApp.EncryptedHostPassword)
            : null;

        string? hostAuthToken = !string.IsNullOrEmpty(hostApp.EncryptedAuthToken)
            ? HostSecurityService.DecryptPassword(hostApp.EncryptedAuthToken)
            : null;

        string hostUsername = hostApp.HostUsername ?? telemetry?.HostUsername ?? $"{acc.Name}_host";

        return Json(new
        {
            success = true,
            hostUsername = hostUsername,
            hostPassword = hostPassword,
            authToken = hostAuthToken,
            hostAuthToken = hostAuthToken
        });
    }

    [HttpGet("account/reveal-host-password-alias")]
    public Task<IActionResult> RevealHostPassword(CancellationToken cancellationToken = default) =>
        GetHostCredentials(cancellationToken);

    [HttpGet("account/download-compel-config")]
    public async Task<IActionResult> DownloadCompelConfig(CancellationToken cancellationToken = default)
    {
        if (User.Identity?.IsAuthenticated != true) return Unauthorized();
        string? ign = User.Identity.Name;
        if (string.IsNullOrWhiteSpace(ign)) return Unauthorized();
        UserDto? user = await _userApiClient.GetUserByAccountNameAsync(ign, cancellationToken);
        if (user == null)
        {
            string? discordId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!string.IsNullOrEmpty(discordId))
            {
                user = await _userApiClient.GetUserByDiscordIdAsync(discordId, cancellationToken);
            }
        }

        if (user == null) return NotFound();

        HostApplicationDto? hostApp = await _userApiClient.GetHostApplicationAsync(user.ID, cancellationToken);
        if (hostApp == null || hostApp.Status != HostApplicationStatus.Approved)
        {
            return BadRequest("No approved host application found.");
        }

        string rawPassword = !string.IsNullOrEmpty(hostApp.EncryptedHostPassword)
            ? HostSecurityService.DecryptPassword(hostApp.EncryptedHostPassword)
            : string.Empty;
        string hostAccountName = hostApp.HostUsername;

        var configObj = new
        {
            Description = "COMPEL Host Configuration",
            Value = "COMPEL_CONFIG",
            HostUsername = hostAccountName,
            HostPassword = rawPassword,
            Location = hostApp.Location,
            ServerNamePrefix = hostApp.ServerNamePrefix,
            Instances = hostApp.Instances
        };

        byte[] bytes =
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(configObj,
                new JsonSerializerOptions { WriteIndented = true }));
        return File(bytes, "application/json", "COMPEL.JSON");
    }

    [Authorize]
    [HttpGet("account/export-data")]
    public async Task<IActionResult> ExportMyData(CancellationToken cancellationToken = default)
    {
        if (User.Identity?.IsAuthenticated != true) return Unauthorized();
        UserDto? user = await ResolveCurrentPortalUserAsync(cancellationToken);
        if (user is null) return NotFound("Authenticated user record not found.");

        HttpClient client = _httpClientFactory.CreateClient("ZORGATH");
        using HttpRequestMessage req = new(HttpMethod.Get, "/api/account/me/export");
        req.Headers.TryAddWithoutValidation("X-Kongor-User-Id", user.ID.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using HttpResponseMessage resp = await client.SendAsync(req, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("[PRIVACY-UI] ExportMyData failed for UserId={UserId} with HTTP {StatusCode}.", user.ID, (int)resp.StatusCode);
            TempData["ErrorMessage"] = "Unable to export account data right now. Please retry shortly.";
            return RedirectToAction("Settings");
        }

        byte[] payload = await resp.Content.ReadAsByteArrayAsync(cancellationToken);
        return File(payload, "application/json", $"kongor_account_{user.ID}_export.json");
    }

    [Authorize]
    [ValidateAntiForgeryToken]
    [HttpPost("account/delete-account")]
    public async Task<IActionResult> DeleteMyAccount([FromForm] string confirmationPhrase, CancellationToken cancellationToken = default)
    {
        if (User.Identity?.IsAuthenticated != true) return Unauthorized();
        UserDto? user = await ResolveCurrentPortalUserAsync(cancellationToken);
        if (user is null) return NotFound("Authenticated user record not found.");

        HttpClient client = _httpClientFactory.CreateClient("ZORGATH");
        using HttpRequestMessage req = new(HttpMethod.Delete, "/api/account/me")
        {
            Content = JsonContent.Create(new DeleteAccountRequestDto(confirmationPhrase ?? string.Empty))
        };
        req.Headers.TryAddWithoutValidation("X-Kongor-User-Id", user.ID.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using HttpResponseMessage resp = await client.SendAsync(req, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            string errBody = await resp.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning("[PRIVACY-UI] DeleteMyAccount rejected for UserId={UserId} (HTTP {StatusCode}): {Error}", user.ID, (int)resp.StatusCode, errBody);
            TempData["ErrorMessage"] = string.IsNullOrWhiteSpace(errBody)
                ? "Confirmation phrase must exactly match: DELETE MY ACCOUNT"
                : errBody.Trim('"');
            return RedirectToAction("Settings");
        }

        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        TempData["SuccessMessage"] = "Your account has been anonymized and deleted.";
        return RedirectToAction("Index", "Home");
    }

    private async Task<UserDto?> ResolveCurrentPortalUserAsync(CancellationToken cancellationToken)
    {
        string? ign = User.Identity?.Name;
        if (!string.IsNullOrWhiteSpace(ign))
        {
            UserDto? byIgn = await _userApiClient.GetUserByAccountNameAsync(ign, cancellationToken);
            if (byIgn is not null) return byIgn;
        }

        string? discordId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!string.IsNullOrWhiteSpace(discordId))
        {
            return await _userApiClient.GetUserByDiscordIdAsync(discordId, cancellationToken);
        }

        return null;
    }

    [AllowAnonymous]
    [HttpPost]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction("Index", "Home");
    }
}
