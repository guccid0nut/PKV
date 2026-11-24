// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ReSharper disable All
// ReSharper disable SpecifyACultureInStringConversionExplicitly
// ReSharper disable StringIndexOfIsCultureSpecific.1
// ReSharper disable StringCompareToIsCultureSpecific.1

#pragma warning disable CA1311
#pragma warning disable CA1304
#pragma warning disable CA1305
#pragma warning disable CA1307
#pragma warning disable CA1311
#pragma warning disable CA1862
#pragma warning disable CA1822
#pragma warning disable CA1869
#pragma warning disable CA1863
#pragma warning disable CS8602
#pragma warning disable CS8604
using System.Security.Claims;
using System.Text.RegularExpressions;
using ASPIRE.Common.Caching;
using ASPIRE.Common.Utilities;
using DAWNBRINGER.WebPortal.UI.Infrastructure;
using DAWNBRINGER.WebPortal.UI.Models;
using StaffStoreItemDto = DAWNBRINGER.WebPortal.UI.Models.StaffStoreItemDto;
using MERRICK.DatabaseContext.Entities.Game;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace DAWNBRINGER.WebPortal.UI.Controllers;

public partial class HomeController(
    IPlayerStatsApiClient playerStatsClient,
    IUserApiClient userApiClient,
    IGuideApiClient guideApiClient,
    IAdminApiClient adminApiClient,
    IConfiguration configuration,
    IMatchReplayService? replayService = null,
    IMatchTimelineService? timelineService = null,
    IStaffStoreService? storeService = null,
    ILogger<HomeController>? logger = null,
    ITwitchStreamService? twitchStreamService = null,
    INexusHybridCache? hybridCache = null,
    IMemoryCache? memoryCache = null) : Controller
{
    private static readonly char[] SearchSeparators = [' ', '_', '-'];

    private readonly IMemoryCache _memoryCache = memoryCache ?? new MemoryCache(new MemoryCacheOptions { SizeLimit = 1000 });
    private readonly IAdminApiClient _adminApiClient = adminApiClient;
    private readonly IConfiguration _configuration = configuration;
    private readonly IGuideApiClient _guideApiClient = guideApiClient;
    private readonly INexusHybridCache? _hybridCache = hybridCache;
    private readonly ILogger<HomeController> _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<HomeController>.Instance;
    private readonly IPlayerStatsApiClient _playerStatsClient = playerStatsClient;
    private readonly IMatchReplayService? _replayService = replayService;
    private readonly IStaffStoreService? _storeService = storeService;
    private readonly IMatchTimelineService? _timelineService = timelineService;
    private readonly ITwitchStreamService? _twitchStreamService = twitchStreamService;
    private readonly IUserApiClient _userApiClient = userApiClient;

    [GeneratedRegex("[^a-z0-9]", RegexOptions.IgnoreCase)]
    private static partial Regex NonAlphanumericRegex();

    [GeneratedRegex(@"Upgradable up to Level (\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex UpgradeLevelRegex();

    [GeneratedRegex(@"\+([0-9,]+)\s*Gold", RegexOptions.IgnoreCase)]
    private static partial Regex ScrollGoldRegex();

    [AllowAnonymous]
    public async Task<IActionResult> Index(CancellationToken cancellationToken = default)
    {
        if (User.HasClaim("pending_registration", "true"))
        {
            _logger.LogInformation("[HomeController.Index] Pending Discord user without HoN Account visited '/'; redirecting to /Account/Register.");
            return RedirectToAction("Register", "Account");
        }

        if (User.Identity?.IsAuthenticated == true)
        {
            string ign = User.Identity.Name!;
            PlayerProfileSummaryDto? summary = await _playerStatsClient.GetPlayerProfileAsync(ign, cancellationToken);

            if (summary?.Account == null)
            {
                string? discordId = User.FindFirst("discord_id")?.Value
                                    ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                                    ?? User.FindFirst("urn:discord:id")?.Value;
                if (!string.IsNullOrEmpty(discordId))
                {
                    UserDto? u = await _userApiClient.GetUserByDiscordIdAsync(discordId, cancellationToken);
                    AccountDto? mainAcc = u?.Accounts.FirstOrDefault(a => a.IsMain) ?? u?.Accounts.FirstOrDefault();
                    if (mainAcc != null)
                    {
                        summary = await _playerStatsClient.GetPlayerProfileAsync(mainAcc.Name, cancellationToken);
                    }
                }
            }

            if (summary?.Account != null)
            {
                string? mostPlayedRaw = summary.TopHeroes.FirstOrDefault()?.HeroIdentifier;

                string resolvedHeroDisplayName = !string.IsNullOrEmpty(mostPlayedRaw)
                    ? Heroes.ResolveHeroDisplayName(mostPlayedRaw)
                    : "Bubbles";

                if (!string.IsNullOrEmpty(mostPlayedRaw))
                {
                    ViewData["MostPlayedHero"] = $"{resolvedHeroDisplayName} Main";
                }
                else
                {
                    ViewData["MostPlayedHero"] = "Newb";
                }

                string heroCode = !string.IsNullOrEmpty(mostPlayedRaw) ? mostPlayedRaw : "Hero_Bubbles";
                string cleanHero = heroCode.Replace("Hero_", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant();

                List<HeroAvatarModelDto> availableAvatars =
                [
                    new HeroAvatarModelDto
                    {
                        Name = resolvedHeroDisplayName, GlbUrl = $"/api/v1/assets/3d/hero/{cleanHero}"
                    }
                ];

                if (_storeService != null)
                {
                    List<StaffStoreItemDto> heroStoreAvatars =
                        [.. _storeService.GetItemsByHero(resolvedHeroDisplayName).Where(i => i.Type == 3)];

                    foreach (StaffStoreItemDto alt in heroStoreAvatars)
                    {
                        string altClean = alt.AltAvatarSuffix?.ToLowerInvariant() ?? "alt";
                        string glbPath = $"/api/v1/assets/3d/hero/{cleanHero}?alt={altClean}";

                        availableAvatars.Add(new HeroAvatarModelDto
                        {
                            Name = alt.Name,
                            GlbUrl = glbPath
                        });
                    }
                }

                ViewData["AvailableAvatars"] = availableAvatars;
                ViewData["MainHeroAvatars"] = availableAvatars;
                ViewData["MainHeroGlbUrl"] = $"/api/v1/assets/3d/hero/{cleanHero}";
                ViewData["MainHeroAvatarName"] = resolvedHeroDisplayName;
                ViewData["CurrentAccount"] = summary.Account;
                ViewData["CurrentUser"] = summary.User;

                UserDto user = summary.User;
                List<AccountDto> allUserAccounts = (user.Accounts?.ToList()) ?? [summary.Account];
                List<string> linkedAccNames =
                    [.. allUserAccounts.Where(a => a.Type != AccountType.ServerHost).Select(a => a.Name)];
                if (linkedAccNames.Count == 0) linkedAccNames = [summary.Account.Name];
                bool hasMultipleAccounts = linkedAccNames.Count > 1;

                ViewData["HasMultipleAccounts"] = hasMultipleAccounts;
                ViewData["LinkedAccounts"] = hasMultipleAccounts ? linkedAccNames : null;
                ViewData["MainAccountName"] =
                    allUserAccounts.FirstOrDefault(a => a.IsMain)?.Name ?? summary.Account.Name;
            }

            IReadOnlyList<string> approvedStreamers =
                await _playerStatsClient.GetApprovedStreamerLoginsAsync(cancellationToken);
            ViewData["ApprovedStreamers"] = approvedStreamers.ToList();

            if (_twitchStreamService != null)
            {
                List<TwitchStreamInfo> liveStreams =
                    await _twitchStreamService.GetLiveHonStreamsAsync(approvedStreamers, cancellationToken);
                ViewData["LiveHonStreams"] = liveStreams.Where(s => s.IsLive && s.IsPlayingHon).ToList();
            }
        }

        return View();
    }

    [AllowAnonymous]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    [HttpGet("/Home/Error")]
    public IActionResult Error()
    {
        Microsoft.AspNetCore.Diagnostics.IExceptionHandlerPathFeature? feature =
            HttpContext.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerPathFeature>();
        if (feature?.Error != null)
        {
            _logger.LogError(feature.Error, "[HomeController.Error] Unhandled portal exception at path '{Path}'.", feature.Path);
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpGet("api/players/search")]
    [HttpGet("api/v1/players/search")]
    [HttpGet("SearchPlayers")]
    [HttpGet("Home/SearchPlayers")]
    public async Task<IActionResult> SearchPlayers(
        [FromQuery] string? q = null,
        [FromQuery] string? query = null,
        CancellationToken cancellationToken = default)
    {
        string? searchTerm = !string.IsNullOrWhiteSpace(q) ? q : query;
        if (string.IsNullOrWhiteSpace(searchTerm) || searchTerm.Trim().Length < 2)
        {
            return Json(Array.Empty<object>());
        }

        string cleanQuery = searchTerm.Trim().ToLowerInvariant().TrimStart('@');
        bool enableCache = _configuration.GetValue("Search:EnableCache", false);
        string cacheKey = $"search:player:{cleanQuery}";
        if (enableCache && _memoryCache.TryGetValue(cacheKey, out List<object>? cached) && cached != null)
        {
            return Json(cached);
        }

        IReadOnlyList<AccountDto> accounts =
            await _playerStatsClient.SearchAccountsAsync(cleanQuery, 1, 15, cancellationToken);

        List<object> results =
        [
            .. accounts.Select(a =>
            {
                string avatarUrl =
                    (!string.IsNullOrEmpty(a.User?.DiscordId) && !string.IsNullOrEmpty(a.User?.DiscordAvatar))
                        ? (a.User.DiscordAvatar.StartsWith("a_", StringComparison.OrdinalIgnoreCase)
                            ? $"https://cdn.discordapp.com/avatars/{a.User.DiscordId}/{a.User.DiscordAvatar}.gif"
                            : $"https://cdn.discordapp.com/avatars/{a.User.DiscordId}/{a.User.DiscordAvatar}.png?size=256")
                        : "/images/default-avatar.png";

                string status = (a.IsBanned || a.Type == AccountType.Disabled)
                    ? "Banned"
                    : (a.IsSuspended ? "Suspended" : (a.IsMuted ? "Muted" : "Active"));

                return new
                {
                    id = a.ID,
                    name = a.Name,
                    avatarUrl,
                    discordUsername = a.User?.DiscordUsername,
                    status
                } as object;
            })
        ];

        if (enableCache)
        {
            _memoryCache.Set(cacheKey, results, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(45),
                Size = 1
            });
        }
        return Json(results);
    }

    [Authorize]
    [HttpGet("setup")]
    public IActionResult Setup()
    {
        return View();
    }

    [Authorize(Policy = StaffAuthorizationHelper.PolicyName)]
    [HttpGet("docs")]
    [HttpGet("developer-docs")]
    public IActionResult DeveloperDocs()
    {
        if (!StaffAuthorizationHelper.IsStaff(User))
        {
            _logger.LogWarning(
                "[HomeController] Forbidden access attempt to Developer Docs ('/docs') by non-staff user '{User}'.",
                User?.Identity?.Name ?? "Anonymous");
            return Forbid();
        }

        string docsUrl = _configuration["DeveloperDocs:Url"] ?? "http://localhost:8080";
        _logger.LogInformation(
            "[HomeController] Staff user '{User}' opening Developer Docs redirect -> '{DocsUrl}'.",
            User?.Identity?.Name ?? "Staff",
            docsUrl);
        return Redirect(docsUrl);
    }

    [HttpGet("terms")]
    public IActionResult Terms()
    {
        return View();
    }

    [HttpGet("privacy")]
    public IActionResult Privacy()
    {
        return View();
    }

    [HttpGet("lobby-hub")]
    public IActionResult LobbyHub([FromQuery] string? lang = null)
    {
        ViewData["Language"] = lang ?? "en";
        return View();
    }

    public class AccountSearchProjection
    {
        public int ID { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? DiscordUsername { get; set; }
        public string? DiscordId { get; set; }
        public string? DiscordAvatar { get; set; }
        public bool IsBanned { get; set; }
        public bool IsSuspended { get; set; }
        public bool IsMuted { get; set; }
        public AccountType Type { get; set; }
    }
}
