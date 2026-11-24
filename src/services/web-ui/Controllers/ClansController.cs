// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using DAWNBRINGER.WebPortal.UI.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.DTOs;

namespace DAWNBRINGER.WebPortal.UI.Controllers;

public sealed class ClansController(
    IPlayerStatsApiClient playerStatsClient,
    ILogger<ClansController>? logger = null) : Controller
{
    public const string DirectoryRoute = "/clans";
    public const string DetailRoutePrefix = "/clans/";
    private const int DefaultPageSize = 24;
    private static readonly HashSet<string> AllowedSorts = new(StringComparer.OrdinalIgnoreCase)
    {
        "mmr", "members", "winrate", "matches", "newest"
    };

    private readonly ILogger<ClansController> _logger = logger ?? NullLogger<ClansController>.Instance;
    private readonly IPlayerStatsApiClient _playerStatsClient = playerStatsClient ?? throw new ArgumentNullException(nameof(playerStatsClient));

    [HttpGet("/clans")]
    public async Task<IActionResult> Index(
        [FromQuery] int page = 1,
        [FromQuery] string? search = null,
        [FromQuery] string sort = "mmr",
        CancellationToken ct = default)
    {
        int safePage = Math.Max(1, page);
        string safeSort = AllowedSorts.Contains(sort ?? string.Empty)
            ? sort!.Trim().ToLowerInvariant()
            : "mmr";
        string cleanSearch = (search ?? string.Empty).Trim();

        _logger.LogInformation(
            "[ClansController.Index] Loading clans directory: page={Page}, sort='{Sort}', search='{Search}', user='{User}'",
            safePage, safeSort, cleanSearch, User?.Identity?.Name ?? "anonymous");

        Task<ClanDirectoryResultDto> directoryTask =
            _playerStatsClient.GetClanDirectoryAsync(safePage, DefaultPageSize, cleanSearch, safeSort, ct);

        Task<AccountDto?> myAccountTask = ResolveCurrentUserAccountAsync(ct);

        await Task.WhenAll(directoryTask, myAccountTask);

        ClanDirectoryResultDto directory = await directoryTask;
        AccountDto? myAccount = await myAccountTask;

        int? myClanId = myAccount?.ClanId;
        string? myClanTag = myAccount?.ClanTag;
        string? myClanName = myAccount?.ClanName;

        _logger.LogInformation(
            "[ClansController.Index] Rendered {Count} clans (total={Total}, page={Page}/{TotalPages}, myClanId={MyClanId})",
            directory.Items.Count, directory.TotalCount, directory.Page, directory.TotalPages, myClanId);

        ClanDirectoryViewModel model = new(
            Directory: directory,
            SearchQuery: cleanSearch,
            CurrentSort: safeSort,
            MyClanId: myClanId,
            MyClanTag: myClanTag,
            MyClanName: myClanName
        );

        return View(model);
    }

    [HttpGet("/clans/{id:int}")]
    public async Task<IActionResult> Detail(int id, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "[ClansController.Detail] Loading clan #{ClanId} for user='{User}'",
            id, User?.Identity?.Name ?? "anonymous");

        if (id <= 0)
        {
            _logger.LogWarning("[ClansController.Detail] Invalid ClanId={ClanId}; returning 404 view", id);
            Response.StatusCode = StatusCodes.Status404NotFound;
            return View(new ClanDetailViewModel(null, id, false));
        }

        Task<ClanDetailDto?> detailTask = _playerStatsClient.GetClanDetailAsync(id, ct);
        Task<AccountDto?> myAccountTask = ResolveCurrentUserAccountAsync(ct);

        await Task.WhenAll(detailTask, myAccountTask);

        ClanDetailDto? clan = await detailTask;
        AccountDto? myAccount = await myAccountTask;

        if (clan is null)
        {
            _logger.LogWarning("[ClansController.Detail] Clan #{ClanId} not found or inactive; returning 404 view", id);
            Response.StatusCode = StatusCodes.Status404NotFound;
            return View(new ClanDetailViewModel(null, id, false));
        }

        bool isMyClan = myAccount?.ClanId == clan.Summary.Id;

        _logger.LogInformation(
            "[ClansController.Detail] Rendered clan #{ClanId} '[{Tag}] {Name}' with {Members} members (isMyClan={IsMyClan})",
            clan.Summary.Id, clan.Summary.Tag, clan.Summary.Name, clan.Members.Count, isMyClan);

        return View(new ClanDetailViewModel(clan, id, isMyClan));
    }

    private async Task<AccountDto?> ResolveCurrentUserAccountAsync(CancellationToken ct)
    {
        string? ign = User?.Identity?.IsAuthenticated == true ? User.Identity.Name : null;
        if (string.IsNullOrWhiteSpace(ign))
        {
            return null;
        }

        try
        {
            return await _playerStatsClient.GetAccountByIgnAsync(ign, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ClansController] Failed resolving lightweight account info for '{IGN}'", ign);
            return null;
        }
    }
}
