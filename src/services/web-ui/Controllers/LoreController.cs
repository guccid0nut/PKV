// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Security.Claims;
using DAWNBRINGER.WebPortal.UI.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

#pragma warning disable CS9113

namespace DAWNBRINGER.WebPortal.UI.Controllers;

[Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class LoreController(
    ILoreApiClient loreApiClient,
    IUserApiClient userApiClient,
    ILogger<LoreController>? logger = null) : Controller
{
    // 1. Lore Universe & Comics Catalog
    [HttpGet("/lore")]
    public async Task<IActionResult> Index(string? arc = null, CancellationToken ct = default)
    {
        if (!IsDiscordAuthenticated()) return RedirectToAction("LoginDiscord", "Account");

        AccountDto? currentAccount = await GetLoggedInAccountAsync(ct);
        bool isStaff = await IsStaffUserAsync(ct);

        if (!isStaff)
        {
            logger?.LogWarning("[Lore] Denied access to WIP Newerth Lore for non-staff user '{User}'", User.Identity?.Name ?? "Anonymous");
            return Forbid();
        }

        IReadOnlyList<LoreArcDto> arcs = await loreApiClient.GetArcsAsync(isStaff, ct);
        IReadOnlyList<LoreComicSummaryDto> comics = await loreApiClient.GetComicsAsync(null, isStaff, ct);

        ViewBag.CurrentAccount = currentAccount;
        ViewBag.IsStaff = isStaff;
        ViewBag.SelectedArcSlug = arc ?? string.Empty;
        ViewBag.Arcs = arcs;

        if (!string.IsNullOrWhiteSpace(arc) && arc != "all")
        {
            LoreArcDto? matchedArc = arcs.FirstOrDefault(a => a.Slug.Equals(arc, StringComparison.OrdinalIgnoreCase));
            if (matchedArc != null) comics = [.. comics.Where(c => c.ArcID == matchedArc.ID)];
        }

        return View(comics);
    }

    // 2. Interactive Comic Reader
    [HttpGet("/lore/{slug}")]
    [HttpGet("/lore/comic/{slug}")]
    public async Task<IActionResult> Reader(string slug, CancellationToken ct = default)
    {
        if (!IsDiscordAuthenticated()) return RedirectToAction("LoginDiscord", "Account");

        AccountDto? currentAccount = await GetLoggedInAccountAsync(ct);
        bool isStaff = await IsStaffUserAsync(ct);

        if (!isStaff)
        {
            logger?.LogWarning("[Lore] Denied access to WIP Comic Reader for non-staff user '{User}'", User.Identity?.Name ?? "Anonymous");
            return Forbid();
        }

        LoreComicDetailDto? comic = await loreApiClient.GetComicBySlugAsync(slug, currentAccount?.ID, isStaff, ct);
        if (comic == null) return NotFound("Comic not found");

        IReadOnlyList<LoreComicSummaryDto>
            allComicsInArc = await loreApiClient.GetComicsAsync(comic.ArcID, isStaff, ct);
        LoreComicSummaryDto? prevIssue = allComicsInArc
            .Where(c => c.IssueNumber < comic.IssueNumber)
            .OrderByDescending(c => c.IssueNumber)
            .FirstOrDefault();

        LoreComicSummaryDto? nextIssue = allComicsInArc
            .Where(c => c.IssueNumber > comic.IssueNumber)
            .OrderBy(c => c.IssueNumber)
            .FirstOrDefault();

        ViewBag.CurrentAccount = currentAccount;
        ViewBag.IsStaff = isStaff;
        ViewBag.PrevIssue = prevIssue;
        ViewBag.NextIssue = nextIssue;

        return View(comic);
    }

    // 3. Quick Star Rating Submission
    [HttpPost("/lore/comics/{id:int}/rate")]
    public async Task<IActionResult> Rate(int id, [FromBody] RateInputModel model, CancellationToken ct = default)
    {
        AccountDto? currentAccount = await GetLoggedInAccountAsync(ct);
        if (currentAccount == null) return Unauthorized(new { message = "You must be logged in to rate comics." });

        bool isStaff = await IsStaffUserAsync(ct);
        if (!isStaff)
        {
            logger?.LogWarning("[Lore] Denied comic rating submission for non-staff user '{User}'", User.Identity?.Name ?? "Anonymous");
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Staff authorization required." });
        }

        if (model.Score is < 1 or > 5) return BadRequest(new { message = "Score must be between 1 and 5." });

        bool success = await loreApiClient.RateComicAsync(id, currentAccount.ID, model.Score, ct);
        if (!success) return BadRequest(new { message = "Could not record rating." });

        return Json(new { success = true, score = model.Score });
    }

    private async Task<AccountDto?> GetLoggedInAccountAsync(CancellationToken ct)
    {
        if (User.Identity?.IsAuthenticated != true) return null;

        string? accountName = User.FindFirst(ClaimTypes.Name)?.Value;
        if (!string.IsNullOrEmpty(accountName))
        {
            AccountDto? acc = await userApiClient.GetAccountByNameAsync(accountName, ct);
            if (acc != null) return acc;
        }

        string? email = User.FindFirst(ClaimTypes.Email)?.Value;
        if (!string.IsNullOrEmpty(email))
        {
            UserDto? user = await userApiClient.GetUserByEmailAsync(email, ct);
            return user?.Accounts.FirstOrDefault(a => a.IsMain) ?? user?.Accounts.FirstOrDefault();
        }

        return null;
    }

    private async Task<bool> IsStaffUserAsync(CancellationToken ct)
    {
        if (User.Identity?.IsAuthenticated != true) return false;

        if (StaffAuthorizationHelper.IsStaff(User))
            return true;

        AccountDto? account = await GetLoggedInAccountAsync(ct);
        return account != null && (account.IsStaff || account.Type == AccountType.Staff ||
                                   account.Type == AccountType.GameMaster ||
                                   account.Type == AccountType.MatchModerator);
    }

    private bool IsDiscordAuthenticated()
    {
        if (User.Identity?.IsAuthenticated != true) return false;
        return User.HasClaim(c => c.Type is "discord_id" or "urn:discord:id" or "urn:discord:avatar") ||
               !string.IsNullOrEmpty(User.FindFirst("discord_id")?.Value) ||
               !string.IsNullOrEmpty(User.FindFirst("urn:discord:id")?.Value);
    }

    public record RateInputModel(int Score);
}
