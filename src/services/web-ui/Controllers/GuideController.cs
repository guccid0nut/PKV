// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

#pragma warning disable CS9113

using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using GuideExportPackage = ASPIRE.Common.Models.Guides.GuideExportPackage;
using HeroOptionDto = ASPIRE.Common.Models.Guides.HeroOptionDto;

namespace DAWNBRINGER.WebPortal.UI.Controllers;

[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class GuideController(
    IGuideApiClient guideApiClient,
    IUserApiClient userApiClient,
    IPlayerStatsApiClient? playerStatsClient = null,
    IWebHostEnvironment? webHostEnvironment = null,
    ILogger<GuideController>? logger = null)
    : Controller
{
    [ActivatorUtilitiesConstructor]
    public GuideController(IGuideApiClient guideApiClient, IUserApiClient userApiClient, ILogger<GuideController>? logger)
        : this(guideApiClient, userApiClient, null, null, logger)
    {
    }

    // 1. Browse Community & Official Guides
    [HttpGet("/guides")]
    public async Task<IActionResult> Index(
        string? hero = null,
        string? map = null,
        string? q = null,
        string? sort = "featured",
        int page = 1,
        CancellationToken cancellationToken = default)
    {
        AccountDto? currentAccount = await GetLoggedInAccountAsync(cancellationToken);
        bool isStaff = await IsStaffUserAsync(cancellationToken);
        ViewBag.CurrentAccount = currentAccount;
        ViewBag.IsStaff = isStaff;
        ViewBag.UserDefaultGuideIds = currentAccount?.DefaultGuideIDs ?? [];
        ViewBag.UserFavoriteGuideIds = currentAccount?.FavoriteGuideIDs ?? [];
        ViewBag.SelectedHero = hero ?? string.Empty;
        ViewBag.SelectedMap = map ?? "all";
        ViewBag.SearchQuery = q ?? string.Empty;
        ViewBag.CurrentSort = sort ?? "featured";
        ViewBag.CurrentPage = Math.Max(1, page);

        GuideSearchResultDto searchResult = await guideApiClient.GetGuidesAsync(new GuideSearchQueryDto(
            hero,
            map,
            q,
            sort,
            page,
            18,
            currentAccount?.Name,
            isStaff
        ), cancellationToken);

        ViewBag.TotalCount = searchResult.TotalCount;
        ViewBag.TotalPages = searchResult.TotalPages;

        return View(searchResult.Guides);
    }

    // 2. Guide Detail View
    [HttpGet("/guides/{id:int}")]
    public async Task<IActionResult> Details(int id, CancellationToken cancellationToken = default)
    {
        HeroGuideDto? guide = await guideApiClient.GetGuideByIdAsync(id, cancellationToken);
        if (guide == null) return NotFound();

        AccountDto? currentAccount = await GetLoggedInAccountAsync(cancellationToken);
        bool isStaff = await IsStaffUserAsync(cancellationToken);

        if (!guide.Public && !guide.Featured)
        {
            bool isAuthor = currentAccount != null && (guide.AuthorAccountID == currentAccount.ID ||
                                                       (guide.Author != null && guide.Author.ID == currentAccount.ID) ||
                                                       string.Equals(guide.AuthorAccountName, currentAccount.Name,
                                                           StringComparison.OrdinalIgnoreCase));
            if (!isAuthor && !isStaff) return NotFound("Private guide");
        }

        ViewBag.CurrentAccount = currentAccount;
        ViewBag.IsStaff = isStaff;
        ViewBag.IsDefault = currentAccount?.DefaultGuideIDs.Contains(guide.ID) ?? false;
        ViewBag.IsFavorite = currentAccount?.FavoriteGuideIDs.Contains(guide.ID) ?? false;

        IReadOnlyList<HeroDifficultyVoteDto> difficultyVotes =
            await guideApiClient.GetHeroDifficultyVotesAsync(guide.HeroIdentifier, cancellationToken);
        ViewBag.AverageHeroDifficulty = difficultyVotes.Count > 0
            ? Math.Round(difficultyVotes.Average(v => (double)v.Rating), 1)
            : 3.0;
        ViewBag.TotalHeroDifficultyVotes = difficultyVotes.Count;
        ViewBag.UserHeroDifficultyVote = currentAccount != null
            ? difficultyVotes.FirstOrDefault(v => v.AccountID == currentAccount.ID)?.Rating
            : null;

        IReadOnlyList<HeroGuideDto> otherGuides =
            await guideApiClient.GetGuidesForHeroAsync(guide.HeroIdentifier, cancellationToken);
        ViewBag.OtherGuidesForHero = otherGuides.Where(g => g.ID != guide.ID).Take(4).ToList();

        return View("Detail", guide);
    }

    [HttpGet("/guides/detail/{id:int}")]
    public Task<IActionResult> Detail(int id, CancellationToken cancellationToken = default)
    {
        return Details(id, cancellationToken);
    }

    [HttpGet("/guides/{id:int}/export")]
    [HttpGet("/guides/export/{id:int}")]
    public async Task<IActionResult> ExportGuide(int id, CancellationToken cancellationToken = default)
    {
        HeroGuideDto? guide = await guideApiClient.GetGuideByIdAsync(id, cancellationToken);
        if (guide == null) return NotFound();

        GuideExportPackage package = new()
        {
            GuideName = guide.Name,
            HeroIdentifier = guide.HeroIdentifier,
            HeroName = guide.HeroIdentifier,
            TargetMap = guide.TargetMap ?? "all",
            Intro = guide.Intro ?? string.Empty,
            DetailedDescription = guide.Content ?? string.Empty,
            StartingItems = guide.StartingItems ?? string.Empty,
            EarlyGameItems = guide.EarlyGameItems ?? string.Empty,
            CoreItems = guide.CoreItems ?? string.Empty,
            LuxuryItems = guide.LuxuryItems ?? string.Empty,
            AbilityQueue = guide.AbilityQueue ?? string.Empty,
            Author = guide.Author?.Name ?? string.Empty
        };

        byte[] bytes =
            Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(package, new JsonSerializerOptions { WriteIndented = true }));
        return File(bytes, "application/json", $"{guide.Name}.json");
    }

    public Task<IActionResult> DeleteGuide(int id, CancellationToken cancellationToken = default)
    {
        return Delete(id, cancellationToken);
    }

    public Task<IActionResult> VoteGuide(int id, [FromQuery] bool isUpvote,
        CancellationToken cancellationToken = default)
    {
        return Vote(id, isUpvote, cancellationToken);
    }

    // 3. Guide Builder / Editor
    [HttpGet("/guides/builder")]
    [HttpGet("/guides/{id:int}/edit")]
    public async Task<IActionResult> Builder(int? id, string? hero = null, int? templateId = null,
        CancellationToken cancellationToken = default)
    {
        AccountDto? currentAccount = await GetLoggedInAccountAsync(cancellationToken);
        if (currentAccount == null)
            return RedirectToAction("Login", "Account", new { returnUrl = Request.Path + Request.QueryString });

        HeroGuideDto? guide = null;
        bool isEditing = false;
        if (id.HasValue && id.Value > 0)
        {
            guide = await guideApiClient.GetGuideByIdAsync(id.Value, cancellationToken);
            if (guide == null) return NotFound();

            bool isStaff = await IsStaffUserAsync(cancellationToken);
            bool isAuthor = guide.Author != null && currentAccount != null && (guide.Author.ID == currentAccount.ID ||
                                                     string.Equals(guide.Author.Name, currentAccount.Name,
                                                         StringComparison.OrdinalIgnoreCase));
            if (!isAuthor && !isStaff) return Forbid();
            isEditing = true;
        }
        else if (templateId.HasValue && templateId.Value > 0)
        {
            HeroGuideDto? templateGuide = await guideApiClient.GetGuideByIdAsync(templateId.Value, cancellationToken);
            if (templateGuide != null)
            {
                guide = new HeroGuideDto
                {
                    ID = 0,
                    Name = $"{templateGuide.Name} (Copy)",
                    HeroIdentifier = templateGuide.HeroIdentifier,
                    HeroName = templateGuide.HeroName,
                    TargetMap = templateGuide.TargetMap,
                    Intro = templateGuide.Intro,
                    Content = templateGuide.Content,
                    StartingItems = templateGuide.StartingItems,
                    EarlyGameItems = templateGuide.EarlyGameItems,
                    CoreItems = templateGuide.CoreItems,
                    LuxuryItems = templateGuide.LuxuryItems,
                    AbilityQueue = templateGuide.AbilityQueue,
                    Public = true,
                    Official = false
                };
            }
        }

        ViewBag.CurrentAccount = currentAccount;
        ViewBag.IsEditing = isEditing;
        ViewBag.SelectedHero = hero ?? guide?.HeroIdentifier ?? "Hero_LegionCommander";

        if (playerStatsClient != null)
        {
            try
            {
                IReadOnlyList<HeroDetailDto> heroes = await playerStatsClient.GetAllHeroDetailsAsync(cancellationToken);
                ViewBag.HeroList = heroes
                    .Select(h => new HeroOptionDto
                    {
                        Id = h.HeroIdentifier,
                        DisplayName = h.DisplayName,
                        CleanId = h.HeroIdentifier.Replace("Hero_", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant()
                    })
                    .OrderBy(h => h.DisplayName)
                    .ToList();
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "[GuideController] Failed to fetch hero roster for guide builder");
            }
        }

        return View("GuideBuilder", guide);
    }

    [HttpGet("/guides/create")]
    public Task<IActionResult> Create(string? hero = null, CancellationToken cancellationToken = default) =>
        Builder(null, hero, templateId: null, cancellationToken);

    [HttpGet("/guides/edit/{id:int}")]
    public Task<IActionResult> Edit(int id, string? hero = null, CancellationToken cancellationToken = default) =>
        Builder(id, hero, templateId: null, cancellationToken);

    // 4. Save Guide (AJAX)
    [HttpPost("/guides/save")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveGuide([FromBody] SaveHeroGuideRequestDto request,
        CancellationToken cancellationToken = default)
    {
        AccountDto? currentAccount = await GetLoggedInAccountAsync(cancellationToken);
        if (currentAccount == null)
            return Unauthorized(new { success = false, message = "You must be logged in to create or edit a guide." });

        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { success = false, message = "Guide name is required." });

        if (string.IsNullOrWhiteSpace(request.HeroIdentifier))
            return BadRequest(new { success = false, message = "Hero is required." });

        bool isStaff = await IsStaffUserAsync(cancellationToken);
        SaveGuideResultDto result =
            await guideApiClient.SaveGuideAsync(request, currentAccount.Name, isStaff, cancellationToken);

        if (!result.Success)
            return BadRequest(new { success = false, message = result.Message ?? "Failed to save guide." });

        return Ok(new
        {
            success = true,
            guideId = result.GuideId,
            message = result.Message ?? "Guide saved successfully!"
        });
    }

    // 5. Delete Guide
    [HttpPost("/guides/{id:int}/delete")]
    [HttpPost("/guides/delete/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken = default)
    {
        AccountDto? currentAccount = await GetLoggedInAccountAsync(cancellationToken);
        if (currentAccount == null) return Unauthorized();

        HeroGuideDto? guide = await guideApiClient.GetGuideByIdAsync(id, cancellationToken);
        if (guide != null && guide.Featured)
            return BadRequest(new { success = false, message = "Official guides cannot be deleted" });

        bool success = await guideApiClient.DeleteGuideAsync(id, currentAccount.Name, cancellationToken);
        if (!success) return BadRequest(new { success = false, message = "Failed to delete guide or unauthorized." });

        return Ok(new { success = true, message = "Guide deleted successfully." });
    }

    // 6. Upvote / Downvote Guide
    [HttpPost("/guides/{id:int}/vote")]
    [HttpPost("/guides/vote/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Vote(int id, [FromQuery] bool isUpvote,
        CancellationToken cancellationToken = default)
    {
        AccountDto? currentAccount = await GetLoggedInAccountAsync(cancellationToken);
        if (currentAccount == null)
            return Unauthorized(new { success = false, message = "You must be logged in to vote." });

        VoteGuideResultDto result = await guideApiClient.VoteGuideAsync(id, isUpvote, cancellationToken);
        if (!result.Success) return BadRequest(new { success = false, message = result.Message ?? "Voting failed." });

        return Ok(new
        {
            success = true,
            upVotes = result.UpVotes,
            downVotes = result.DownVotes,
            rating = result.Rating,
            message = result.Message
        });
    }

    // 7. Toggle Public / Private
    [HttpPost("/guides/{id:int}/toggle-privacy")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> TogglePrivacy(int id, CancellationToken cancellationToken = default)
    {
        AccountDto? currentAccount = await GetLoggedInAccountAsync(cancellationToken);
        if (currentAccount == null) return Unauthorized();

        bool isStaff = await IsStaffUserAsync(cancellationToken);
        TogglePrivacyResultDto result =
            await guideApiClient.TogglePrivacyAsync(id, currentAccount.Name, isStaff, cancellationToken);
        if (!result.Success)
            return BadRequest(new { success = false, message = result.Message ?? "Failed to toggle privacy." });

        return Ok(new { success = true, isPublic = result.IsPublic, message = result.Message });
    }

    // 8. Toggle Official / Featured (Staff Only)
    [HttpPost("/guides/{id:int}/toggle-official")]
    [HttpPost("/guides/feature/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleOfficial(int id, CancellationToken cancellationToken = default)
    {
        bool isStaff = await IsStaffUserAsync(cancellationToken);
        if (!isStaff) return Forbid();

        ToggleOfficialResultDto result = await guideApiClient.ToggleOfficialAsync(id, cancellationToken);
        if (!result.Success)
            return BadRequest(new { success = false, message = result.Message ?? "Failed to toggle official status." });

        return Ok(new { success = true, isFeatured = result.IsFeatured, message = result.Message });
    }

    // 9. Set / Unset Default Guide for Hero
    [HttpPost("/guides/{id:int}/toggle-default")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleDefault(int id, CancellationToken cancellationToken = default)
    {
        AccountDto? currentAccount = await GetLoggedInAccountAsync(cancellationToken);
        if (currentAccount == null) return Unauthorized();

        ToggleDefaultGuideResultDto result =
            await guideApiClient.ToggleDefaultGuideAsync(id, currentAccount.Name, cancellationToken);
        if (!result.Success)
            return BadRequest(new { success = false, message = result.Message ?? "Failed to toggle default guide." });

        return Ok(new
        {
            success = true,
            isDefault = result.IsDefault,
            guideId = result.GuideId,
            heroName = result.HeroName,
            message = result.Message
        });
    }

    // 10. Toggle Favorite Guide
    [HttpPost("/guides/{id:int}/toggle-favorite")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleFavorite(int id, CancellationToken cancellationToken = default)
    {
        AccountDto? currentAccount = await GetLoggedInAccountAsync(cancellationToken);
        if (currentAccount == null) return Unauthorized();

        ToggleFavoriteGuideResultDto result =
            await guideApiClient.ToggleFavoriteGuideAsync(id, currentAccount.Name, cancellationToken);
        if (!result.Success)
            return BadRequest(new { success = false, message = result.Message ?? "Failed to toggle favorite guide." });

        return Ok(new
        {
            success = true,
            isFavorite = result.IsFavorite,
            guideId = result.GuideId,
            message = result.Message
        });
    }

    // 11. Hero Community Difficulty Vote
    [HttpPost("/guides/hero/{heroIdentifier}/vote-difficulty")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> VoteHeroDifficulty(string heroIdentifier, [FromQuery] int difficulty,
        CancellationToken cancellationToken = default)
    {
        AccountDto? currentAccount = await GetLoggedInAccountAsync(cancellationToken);
        if (currentAccount == null)
            return Unauthorized(new { success = false, message = "You must be logged in to vote." });

        if (difficulty is < 1 or > 5)
            return BadRequest(new { success = false, message = "Difficulty must be between 1 and 5." });

        HeroDifficultyVoteResultDto result =
            await guideApiClient.VoteHeroDifficultyAsync(heroIdentifier, currentAccount.Name, difficulty,
                cancellationToken);
        if (!result.Success)
            return BadRequest(new { success = false, message = result.Message ?? "Failed to submit difficulty vote." });

        return Ok(new
        {
            success = true,
            averageDifficulty = result.AverageDifficulty,
            totalVotes = result.TotalVotes,
            message = result.Message
        });
    }

    private async Task<bool> IsStaffUserAsync(CancellationToken cancellationToken = default)
    {
        if (User.Identity?.IsAuthenticated != true) return false;

        if (User.Claims.Any(c =>
                c.Type == ClaimTypes.Role && (c.Value == "Admin" || c.Value == "Staff" || c.Value == "GameMaster" ||
                                              c.Value == "SuperAdmin"))) return true;

        foreach (string claimType in new[] { "role", "roles", "Role", "Roles" })
        {
            string? roleClaim = User.FindFirst(claimType)?.Value;
            if (roleClaim is not null and ("Admin" or "Staff" or "GameMaster" or "SuperAdmin")) return true;
        }

        AccountDto? acc = await GetLoggedInAccountAsync(cancellationToken);
        return acc != null && (acc.IsStaff ||
                               acc.Type == AccountType.Staff ||
                               acc.Type == AccountType.GameMaster);
    }

    private async Task<AccountDto?> GetLoggedInAccountAsync(CancellationToken cancellationToken = default)
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            string? discordId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            string? email = User.FindFirst(ClaimTypes.Email)?.Value;
            string? name = User.FindFirst(ClaimTypes.Name)?.Value;

            if (!string.IsNullOrEmpty(discordId))
            {
                UserDto? user = await userApiClient.GetUserByDiscordIdAsync(discordId, cancellationToken);
                AccountDto? mainAcc = user?.Accounts.FirstOrDefault(a => a.IsMain) ?? user?.Accounts.FirstOrDefault();
                if (mainAcc != null) return mainAcc;
            }

            if (!string.IsNullOrEmpty(email))
            {
                UserDto? user = await userApiClient.GetUserByEmailAsync(email, cancellationToken);
                AccountDto? mainAcc = user?.Accounts.FirstOrDefault(a => a.IsMain) ?? user?.Accounts.FirstOrDefault();
                if (mainAcc != null) return mainAcc;
            }

            if (!string.IsNullOrEmpty(name))
            {
                AccountDto? acc = await userApiClient.GetAccountByNameAsync(name, cancellationToken);
                if (acc != null) return acc;
            }
        }

        if (webHostEnvironment?.IsDevelopment() == true)
        {
            return new AccountDto
            {
                ID = 1,
                Name = "GUEST-01",
                Type = AccountType.Staff
            };
        }

        return null;
    }
}
