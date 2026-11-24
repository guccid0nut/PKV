// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

// ReSharper disable All

#pragma warning disable CA2254
#pragma warning disable CA1848
#pragma warning disable CA1304
#pragma warning disable CA1305
#pragma warning disable CA1311

using System.Security.Claims;
using ASPIRE.Common.DTOs;
using DAWNBRINGER.WebPortal.UI.Infrastructure;
using DAWNBRINGER.WebPortal.UI.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StaffStoreItemDto = DAWNBRINGER.WebPortal.UI.Models.StaffStoreItemDto;


namespace DAWNBRINGER.WebPortal.UI.Controllers;

[Authorize(Policy = StaffAuthorizationHelper.PolicyName)]
[Route("staff")]
[AutoValidateAntiforgeryToken]
public class StaffController(
    IAdminApiClient adminApiClient,
    IUserApiClient userApiClient,
    IPlayerStatsApiClient playerStatsClient,
    IStaffStoreService storeService,
    ILoreApiClient? loreApiClient = null,
    IReplayEvidenceService? evidenceService = null,
    IAnnouncementService? announcementService = null,
    ILogger<StaffController>? logger = null) : Controller
{
    private bool IsAuthorizedStaff() => StaffAuthorizationHelper.IsStaff(User);

    private async Task<AccountDto?> ResolveCurrentStaffAccountAsync(CancellationToken cancellationToken = default)
    {
        string? name = User.FindFirst(ClaimTypes.Name)?.Value;
        if (!string.IsNullOrEmpty(name))
        {
            AccountDto? acc = await userApiClient.GetAccountByNameAsync(name, cancellationToken);
            if (acc != null) return acc;
        }

        string? email = User.FindFirst(ClaimTypes.Email)?.Value;
        if (!string.IsNullOrEmpty(email))
        {
            UserDto? user = await userApiClient.GetUserByEmailAsync(email, cancellationToken);
            return user?.Accounts.FirstOrDefault(a => a.IsMain) ?? user?.Accounts.FirstOrDefault();
        }

        string? discordId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!string.IsNullOrEmpty(discordId))
        {
            UserDto? user = await userApiClient.GetUserByDiscordIdAsync(discordId, cancellationToken);
            return user?.Accounts.FirstOrDefault(a => a.IsMain) ?? user?.Accounts.FirstOrDefault();
        }

        return null;
    }

    [HttpGet("announcements")]
    public IActionResult Announcements([FromQuery] string? success = null, [FromQuery] string? error = null)
    {
        if (!IsAuthorizedStaff())
        {
            return Unauthorized();
        }

        IReadOnlyList<AnnouncementItemDto> items = announcementService?.GetAllAnnouncements() ?? [];
        StaffAnnouncementsViewModel model = new()
        {
            Announcements = items,
            SuccessMessage = success,
            ErrorMessage = error
        };

        return View(model);
    }

    [HttpPost("announcements/create")]
    [ValidateAntiForgeryToken]
    public IActionResult CreateAnnouncement([FromForm] AnnouncementItemDto dto)
    {
        if (!IsAuthorizedStaff())
        {
            return Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(dto.Title))
        {
            return RedirectToAction(nameof(Announcements), new { error = "Announcement title is required." });
        }

        bool result = announcementService?.CreateAnnouncement(dto) ?? false;
        return RedirectToAction(nameof(Announcements),
            new { success = result ? "Announcement created successfully." : "Failed to create announcement." });
    }

    [HttpPost("announcements/update")]
    [ValidateAntiForgeryToken]
    public IActionResult UpdateAnnouncement([FromForm] AnnouncementItemDto dto)
    {
        if (!IsAuthorizedStaff())
        {
            return Unauthorized();
        }

        bool result = announcementService?.UpdateAnnouncement(dto) ?? false;
        return RedirectToAction(nameof(Announcements),
            new { success = result ? "Announcement updated successfully." : "Failed to update announcement." });
    }

    [HttpPost("announcements/delete")]
    [ValidateAntiForgeryToken]
    public IActionResult DeleteAnnouncement([FromForm] string id)
    {
        if (!IsAuthorizedStaff())
        {
            return Unauthorized();
        }

        bool result = announcementService?.DeleteAnnouncement(id) ?? false;
        return RedirectToAction(nameof(Announcements),
            new { success = result ? "Announcement deleted successfully." : "Failed to delete announcement." });
    }

    [HttpPost("announcements/toggle")]
    [ValidateAntiForgeryToken]
    public IActionResult ToggleAnnouncement([FromForm] string id)
    {
        if (!IsAuthorizedStaff())
        {
            return Unauthorized();
        }

        bool result = announcementService?.ToggleActive(id) ?? false;
        return RedirectToAction(nameof(Announcements),
            new { success = result ? "Announcement status toggled." : "Failed to toggle announcement." });
    }

    [HttpGet("")]
    [HttpGet("reports")]
    public async Task<IActionResult> Index(
        string? status = null,
        string? reason = null,
        string? query = null,
        int? matchId = null,
        int? reportId = null,
        int page = 1,
        string? message = null,
        string? error = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsAuthorizedStaff())
        {
            AccountDto? currentAcc = await ResolveCurrentStaffAccountAsync(cancellationToken);
            if (currentAcc?.IsStaff != true)
            {
                if (logger != null && logger.IsEnabled(LogLevel.Warning))
                {
                    logger.LogWarning("[Staff] Unauthorized access attempt to Staff Reports by user '{User}'",
                        User.Identity?.Name ?? "Anonymous");
                }

                return RedirectToAction("Index", "Home");
            }

            logger?.LogInformation("[Staff] Verified staff access via database account lookup for user '{User}' (Type: {Type})",
                currentAcc.Name, currentAcc.Type);
        }

        if (page < 1) page = 1;
        const int pageSize = 25;
        return await LoadReportsView(status, reason, query, matchId, reportId, page, pageSize, message, error,
            cancellationToken);
    }

    [HttpGet("reports-alias")]
    public Task<IActionResult> Reports(
        string? status = null,
        string? reason = null,
        string? query = null,
        int? matchId = null,
        int? reportId = null,
        int page = 1,
        string? message = null,
        string? error = null,
        CancellationToken cancellationToken = default) =>
        Index(status, reason, query, matchId, reportId, page, message, error, cancellationToken);

    private async Task<IActionResult> LoadReportsView(
        string? status,
        string? reason,
        string? query,
        int? matchId,
        int? reportId,
        int page,
        int pageSize,
        string? message,
        string? error,
        CancellationToken cancellationToken = default)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 25;

        logger?.LogInformation("[Staff.LoadReportsView] Loading reports: status='{Status}', reason='{Reason}', search='{Query}', page={Page}",
            status, reason, query, page);

        var reportsTask = adminApiClient.GetPlayerReportsAsync(status, reason, query,
            matchId, reportId, page, pageSize, cancellationToken);
        var totalCountTask = adminApiClient.GetPlayerReportsCountAsync(status, reason, query, matchId, reportId,
            cancellationToken);
        var allRecentTask = adminApiClient.GetPlayerReportsAsync(null, null, null, null, null, 1, 100, cancellationToken);

        await Task.WhenAll(reportsTask, totalCountTask, allRecentTask);

        IReadOnlyList<PlayerReportDto> reports = await reportsTask;
        int totalCount = await totalCountTask;
        IReadOnlyList<PlayerReportDto> allRecentForStats = await allRecentTask;

        int totalReports = totalCount > 0 ? totalCount : allRecentForStats.Count;
        int pendingReports =
            allRecentForStats.Count(r => string.Equals(r.Status, "PENDING", StringComparison.OrdinalIgnoreCase));
        int inReviewReports =
            allRecentForStats.Count(r => string.Equals(r.Status, "IN_REVIEW", StringComparison.OrdinalIgnoreCase));
        int resolvedReports =
            allRecentForStats.Count(r => string.Equals(r.Status, "RESOLVED", StringComparison.OrdinalIgnoreCase));
        int dismissedReports =
            allRecentForStats.Count(r => string.Equals(r.Status, "DISMISSED", StringComparison.OrdinalIgnoreCase));
        int appealedReports =
            allRecentForStats.Count(r => string.Equals(r.Status, "APPEALED", StringComparison.OrdinalIgnoreCase));

        List<string> availableReasons = [.. allRecentForStats.Select(r => r.Reason).Distinct()];

        StaffReportsViewModel viewModel = new()
        {
            Reports = [.. reports],
            TotalReports = totalReports,
            PendingReports = pendingReports,
            InReviewReports = inReviewReports,
            ResolvedReports = resolvedReports,
            DismissedReports = dismissedReports,
            AppealedReports = appealedReports,
            StatusFilter = status,
            ReasonFilter = reason,
            SearchQuery = query,
            MatchIdFilter = matchId,
            ReportIdFilter = reportId,
            PageNumber = page,
            PageSize = pageSize,
            TotalCount = totalCount,
            AvailableReasons = availableReasons
        };

        return View("Reports", viewModel);
    }

    [HttpPost("reports/{id:int}/dismiss")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DismissReport(int id, [FromForm] string? comment,
        CancellationToken cancellationToken = default)
    {
        if (!IsAuthorizedStaff()) return Forbid();

        AccountDto? staffAccount = await ResolveCurrentStaffAccountAsync(cancellationToken);
        int staffId = staffAccount?.ID ?? 1;

        bool success = await adminApiClient.DismissReportAsync(id, staffId, comment, cancellationToken);
        if (!success)
        {
            return RedirectToAction(nameof(Index), new { error = $"Could not dismiss Report #{id}." });
        }

        return RedirectToAction(nameof(Index), new { message = $"Report #{id} has been dismissed." });
    }

    [HttpPost("reports/{id:int}/action")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ActionReport(
        int id,
        [FromForm] string actionType,
        [FromForm] string? reason,
        [FromForm] int? durationHours,
        [FromForm] string? staffNotes,
        CancellationToken cancellationToken = default)
    {
        if (!IsAuthorizedStaff()) return Forbid();

        AccountDto? staffAccount = await ResolveCurrentStaffAccountAsync(cancellationToken);
        int staffId = staffAccount?.ID ?? 1;

        bool success = await adminApiClient.ActionReportAsync(new ActionReportRequestDto(
            id,
            staffId,
            actionType,
            reason,
            durationHours,
            staffNotes
        ), cancellationToken);

        if (!success)
        {
            return RedirectToAction(nameof(Index), new { error = $"Could not action Report #{id}." });
        }

        return RedirectToAction(nameof(Index),
            new { message = $"Disciplinary action '{actionType}' applied to Report #{id}." });
    }

    [HttpPost("punish/direct")]
    [HttpPost("reports/create-direct")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DirectPunish(
        [FromForm] string targetAccountName,
        [FromForm] string? punishmentType = null,
        [FromForm] string? judgmentAction = null,
        [FromForm] string? reason = null,
        [FromForm] int? durationHours = null,
        [FromForm] string? notes = null,
        [FromForm] string? description = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsAuthorizedStaff()) return Forbid();

        string resolvedPunishment = !string.IsNullOrWhiteSpace(punishmentType)
            ? punishmentType
            : (!string.IsNullOrWhiteSpace(judgmentAction) ? judgmentAction : "MUTE");

        string resolvedReason = !string.IsNullOrWhiteSpace(reason)
            ? reason
            : "Direct Staff Action";

        string? resolvedNotes = !string.IsNullOrWhiteSpace(notes)
            ? notes
            : description;

        AccountDto? staffAccount = await ResolveCurrentStaffAccountAsync(cancellationToken);
        int staffId = staffAccount?.ID ?? 1;

        bool success = await adminApiClient.DirectPunishAccountAsync(new DirectPunishRequestDto(
            targetAccountName,
            staffId,
            resolvedPunishment,
            resolvedReason,
            durationHours,
            resolvedNotes
        ), cancellationToken);

        if (Request.Headers.XRequestedWith == "XMLHttpRequest" ||
            Request.Headers.Accept.ToString().Contains("application/json"))
        {
            if (!success)
            {
                return BadRequest(new { success = false, message = $"Could not apply punishment to '{targetAccountName}'." });
            }

            return Ok(new { success = true, message = $"Direct {resolvedPunishment} applied to '{targetAccountName}'." });
        }

        if (!success)
        {
            return RedirectToAction(nameof(Index),
                new { error = $"Could not apply punishment to '{targetAccountName}'." });
        }

        return RedirectToAction(nameof(Index),
            new { message = $"Direct {resolvedPunishment} applied to '{targetAccountName}'." });
    }

    [HttpGet("reports/create-direct")]
    public IActionResult CreateDirectReportGet()
    {
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("evidence/{matchId:int}")]
    public async Task<IActionResult> ReplayEvidence(int matchId, string? reportedAccountName = null,
        int? reportId = null, CancellationToken cancellationToken = default)
    {
        if (!IsAuthorizedStaff()) return RedirectToAction("Index", "Home");

        MatchEvidenceResultDto evidence = evidenceService != null
            ? await evidenceService.GetMatchEvidenceAsync(matchId, reportedAccountName,
                cancellationToken: cancellationToken)
            : new MatchEvidenceResultDto
            {
                MatchID = matchId,
                HasReplay = false,
                HasVoice = false,
                DurationSeconds = 1200,
                MapName = "caldavar",
                ReportedAccountName = reportedAccountName,
                ChatLog = []
            };

        ViewBag.ReportId = reportId;
        return View("Evidence", evidence);
    }

    [HttpGet("evidence/{matchId:int}/data")]
    public async Task<IActionResult> GetMatchEvidence(int matchId, string? reportedAccountName = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsAuthorizedStaff()) return Forbid();
        if (evidenceService == null) return NotFound();
        MatchEvidenceResultDto evidence =
            await evidenceService.GetMatchEvidenceAsync(matchId, reportedAccountName,
                cancellationToken: cancellationToken);
        return Json(evidence);
    }

    [HttpGet("evidence/{matchId:int}/voice/{team:int}")]
    public Task<IActionResult> GetTeamVoiceStream(int matchId, int team)
    {
        if (!IsAuthorizedStaff()) return Task.FromResult<IActionResult>(Forbid());
        byte[] dummyWav = new byte[100];
        MemoryStream stream = new(dummyWav);
        return Task.FromResult<IActionResult>(File(stream, "audio/wav", enableRangeProcessing: true));
    }

    [HttpPost("evidence/{matchId:int}/extract")]
    [HttpPost("matches/{matchId:int}/evidence/extract")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ExtractMatchEvidence(int matchId, object? dummy = null,
        string? reportedAccountName = null, CancellationToken cancellationToken = default)
    {
        if (!IsAuthorizedStaff()) return Forbid();
        if (evidenceService == null) return NotFound();
        MatchEvidenceResultDto evidence = await evidenceService.GetMatchEvidenceAsync(matchId, reportedAccountName,
            forceRefresh: true, cancellationToken: cancellationToken);
        return Json(evidence);
    }

    [HttpGet("inventory")]
    public async Task<IActionResult> Inventory(string? accountName, string? message = null, string? error = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsAuthorizedStaff())
        {
            return RedirectToAction("Index", "Home");
        }

        StaffInventoryViewModel viewModel = new()
        {
            Message = message ?? string.Empty,
            ErrorMessage = error ?? string.Empty,
            IsUnlockAllItemsEnabled = storeService.IsUnlockAllItemsEnabled,
            TotalCatalogItems = storeService.GetAllItems().Count
        };

        if (string.IsNullOrWhiteSpace(accountName))
        {
            IReadOnlyList<AccountDto> recent = await adminApiClient.GetRecentAccountsAsync(15, cancellationToken);
            viewModel.RecentAccounts = [.. recent.Select(a => a.Name)];
            return View(viewModel);
        }

        AccountDto? account = await userApiClient.GetAccountByNameAsync(accountName, cancellationToken);
        if (account == null)
        {
            viewModel.ErrorMessage = $"Account '{accountName}' not found.";
            return View(viewModel);
        }

        viewModel.AccountName = account.Name;
        viewModel.AccountId = account.ID;
        UserDto? user = account.User != null
            ? await userApiClient.GetUserByIdAsync(account.User.ID, cancellationToken)
            : null;
        viewModel.OwnedItems = user?.OwnedStoreItems.ToList() ?? [];

        foreach (string code in viewModel.OwnedItems)
        {
            StaffStoreItemDto? c = storeService.ResolveItem(code);
            if (c != null)
            {
                viewModel.OwnedItemDetails.Add(new StaffInventoryItemDto
                {
                    Code = code,
                    Name = c.Name,
                    TypeName = c.TypeName,
                    Resource = c.Resource ?? string.Empty,
                    IconUrl = !string.IsNullOrEmpty(c.Resource)
                        ? $"/api/v1/assets/icon?path={Uri.EscapeDataString(c.Resource)}"
                        : "/images/icons/hero_default.png",
                    HeroName = c.HeroName ?? string.Empty,
                    AltAvatarSuffix = c.AltAvatarSuffix ?? string.Empty
                });
            }
            else
            {
                viewModel.OwnedItemDetails.Add(new StaffInventoryItemDto
                {
                    Code = code,
                    Name = code,
                    TypeName = "Legacy Product",
                    Resource = string.Empty,
                    IconUrl = "/images/icons/hero_default.png",
                    HeroName = string.Empty,
                    AltAvatarSuffix = string.Empty
                });
            }
        }

        return View(viewModel);
    }

    [HttpPost("inventory/add")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddInventoryItem([FromForm] string accountName, [FromForm] string productCode,
        CancellationToken cancellationToken = default)
    {
        if (!IsAuthorizedStaff()) return Forbid();

        bool success = await adminApiClient.AddInventoryItemAsync(accountName, productCode, cancellationToken);
        return RedirectToAction(nameof(Inventory), new
        {
            accountName,
            message = success ? $"Granted product '{productCode}' to {accountName}." : null,
            error = !success ? $"Failed to grant product '{productCode}'." : null
        });
    }

    [HttpPost("inventory/remove")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveInventoryItem([FromForm] string accountName, [FromForm] string productCode,
        CancellationToken cancellationToken = default)
    {
        if (!IsAuthorizedStaff()) return Forbid();

        bool success = await adminApiClient.RemoveInventoryItemAsync(accountName, productCode, cancellationToken);
        return RedirectToAction(nameof(Inventory), new
        {
            accountName,
            message = success ? $"Removed product '{productCode}' from {accountName}." : null,
            error = !success ? $"Failed to remove product '{productCode}'." : null
        });
    }

    [HttpPost("inventory/reset")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetInventory([FromForm] string accountName,
        CancellationToken cancellationToken = default)
    {
        if (!IsAuthorizedStaff()) return Forbid();

        bool success = await adminApiClient.ResetInventoryAsync(accountName, cancellationToken);
        return RedirectToAction(nameof(Inventory), new
        {
            accountName,
            message = success ? $"Reset vault inventory for {accountName}." : null,
            error = !success ? $"Failed to reset inventory." : null
        });
    }

    [HttpPost("inventory/reset-player")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult>
        ResetPlayerInventory(string accountName, CancellationToken cancellationToken = default) =>
        ResetInventory(accountName, cancellationToken);

    [HttpPost("inventory/grant-all")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> GrantAllStoreItems([FromForm] string accountName,
        [FromForm] string? category = null, CancellationToken cancellationToken = default)
    {
        if (!IsAuthorizedStaff()) return Forbid();
        IReadOnlyList<StaffStoreItemDto> items = storeService.GetAllItems();
        string cat = (category ?? "all").ToLowerInvariant();
        List<string> matchedCodes = [];
        foreach (StaffStoreItemDto item in items)
        {
            bool match = cat switch
            {
                "all" => true,
                "avatars" or "alt_avatars" => item.Type == 3 || item.Type == 12 ||
                                              (item.Code.StartsWith("Hero_", StringComparison.OrdinalIgnoreCase) &&
                                               item.Code.Contains('.')),
                _ => string.Equals(item.TypeName, category, StringComparison.OrdinalIgnoreCase)
            };
            if (match)
            {
                matchedCodes.Add(item.Code);
            }
        }

        await adminApiClient.AddInventoryItemsAsync(accountName, matchedCodes.Distinct(), cancellationToken);
        return RedirectToAction(nameof(Inventory), new { accountName, message = $"Granted items to {accountName}." });
    }

    [HttpGet("hosts")]
    public Task<IActionResult> Hosts(string? status = null, string? search = null,
        CancellationToken cancellationToken = default) => HostApplications(status, search, cancellationToken);

    [HttpGet("host-applications")]
    public async Task<IActionResult> HostApplications(string? status = null, string? search = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsAuthorizedStaff())
        {
            return RedirectToAction("Index", "Home");
        }

        IReadOnlyList<HostApplicationDto> applications =
            await adminApiClient.GetHostApplicationsAsync(cancellationToken);

        List<StaffHostApplicationDto> dtoList =
        [
            .. applications.Select(a => new StaffHostApplicationDto
            {
                Id = a.ID,
                UserId = a.UserID,
                MainAccountName = a.MainAccount?.Name ?? "Unknown",
                HostUsername = a.HostUsername,
                Status = a.Status.ToString().ToUpperInvariant(),
                Location = a.Location,
                Instances = a.Instances,
                ServerNamePrefix = a.ServerNamePrefix,
                ApplicationNotes = a.ApplicationNotes,
                StaffNotes = a.StaffNotes,
                ReviewedByStaffName = a.ReviewedByStaffName,
                TimestampApplied = a.TimestampApplied,
                TimestampReviewed = a.TimestampReviewed,
                TotalMatchesPlayed = 0,
                DisciplinaryReportCount = 0,
                HasHostAccount = a.HostAccountID.HasValue
            })
        ];

        if (!string.IsNullOrWhiteSpace(search))
        {
            string cleanSearch = search.Trim();
            dtoList =
            [
                .. dtoList.Where(d =>
                    d.MainAccountName.Contains(cleanSearch, StringComparison.OrdinalIgnoreCase) ||
                    d.HostUsername.Contains(cleanSearch, StringComparison.OrdinalIgnoreCase) ||
                    (d.ApplicationNotes != null &&
                     d.ApplicationNotes.Contains(cleanSearch, StringComparison.OrdinalIgnoreCase)) ||
                    (d.StaffNotes != null && d.StaffNotes.Contains(cleanSearch, StringComparison.OrdinalIgnoreCase)))
            ];
        }

        int totalHostedMatches = await playerStatsClient.GetTotalMatchesCountAsync(cancellationToken);

        StaffHostsViewModel viewModel = new()
        {
            PendingApplications = [.. dtoList.Where(d => d.Status == "PENDING")],
            ApprovedHosts = [.. dtoList.Where(d => d.Status == "APPROVED")],
            RejectedApplications = [.. dtoList.Where(d => d.Status == "REJECTED")],
            RevokedHosts = [.. dtoList.Where(d => d.Status == "REVOKED")],
            TotalHostedMatches = totalHostedMatches,
            StatusFilter = status,
            SearchQuery = search
        };

        return View("Hosts", viewModel);
    }

    [HttpPost("hosts/{id:int}/approve")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApproveHostApplication(int id, [FromForm] string? staffNotes,
        CancellationToken cancellationToken = default)
    {
        if (!IsAuthorizedStaff()) return Forbid();

        string staffName = User.Identity?.Name ?? "Staff";
        bool success = await adminApiClient.ApproveHostApplicationAsync(id, staffNotes, staffName, cancellationToken);
        if (!success)
        {
            return NotFound(new { success = false, message = $"Failed to approve host application #{id}." });
        }

        return Json(new { success = true, message = $"Host application #{id} approved successfully." });
    }

    [HttpPost("hosts/{id:int}/reject")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RejectHostApplication(int id, [FromForm] string? reason,
        CancellationToken cancellationToken = default)
    {
        if (!IsAuthorizedStaff()) return Forbid();

        bool success = await adminApiClient.RejectHostApplicationAsync(id, reason, cancellationToken);
        if (!success)
        {
            return NotFound(new { success = false, message = $"Host application #{id} not found." });
        }

        return Json(new { success = true, message = $"Host application #{id} rejected." });
    }

    [HttpPost("hosts/{id:int}/revert")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RevertHostApplication(int id, CancellationToken cancellationToken = default)
    {
        if (!IsAuthorizedStaff()) return Forbid();

        bool success = await adminApiClient.RejectHostApplicationAsync(id, "Reverted", cancellationToken);
        if (!success)
        {
            return NotFound(new { success = false, message = $"Host application #{id} not found." });
        }

        return Json(new { success = true, message = $"Host application #{id} reverted." });
    }

    [HttpPost("hosts/{id:int}/revoke")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RevokeHostApplication(int id, string? reason = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsAuthorizedStaff()) return Forbid();

        bool success = await adminApiClient.RevokeHostApplicationAsync(id, reason, cancellationToken);
        if (!success)
        {
            return NotFound(new { success = false, message = $"Host application #{id} not found." });
        }

        return Json(new { success = true, message = $"Host application #{id} revoked." });
    }

    [HttpPost("hosts/{id:int}/provision-manual")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ProvisionManualHost(int id, CancellationToken cancellationToken = default)
    {
        if (!IsAuthorizedStaff()) return Forbid();

        bool success = await adminApiClient.ApproveHostApplicationAsync(id, ct: cancellationToken);
        if (!success)
        {
            return NotFound(new { success = false, message = $"Host application #{id} not found." });
        }

        return Json(new { success = true, message = $"Manual host credentials provisioned for application #{id}." });
    }

    [HttpPost("hosts/{id:int}/regenerate-password")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RegenerateHostPassword(int id, CancellationToken cancellationToken = default)
    {
        if (!IsAuthorizedStaff()) return Forbid();
        ResetCredentialResponse resp = await adminApiClient.ResetHostPasswordAsync(id, cancellationToken);
        if (string.IsNullOrEmpty(resp.Value))
            return NotFound(new { success = false, message = "Failed to regenerate host password" });
        return Json(new { success = true, hostPassword = resp.Value });
    }

    [HttpGet("streamers")]
    public Task<IActionResult> Streamers(string? status = null, string? search = null,
        CancellationToken cancellationToken = default) => StreamerApplications(status, search, cancellationToken);

    [HttpGet("streamer-applications")]
    public async Task<IActionResult> StreamerApplications(string? status = null, string? search = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsAuthorizedStaff())
        {
            return RedirectToAction("Index", "Home");
        }

        IReadOnlyList<StreamerApplicationDto> applications =
            await adminApiClient.GetStreamerApplicationsAsync(cancellationToken);

        List<StaffStreamerApplicationDto> dtoList =
        [
            .. applications.Select(a => new StaffStreamerApplicationDto
            {
                Id = a.ID,
                UserId = a.UserID,
                MainAccountName = a.MainAccount?.Name ?? a.User?.EmailAddress ?? "Unknown",
                TwitchChannelUrl = a.TwitchChannelUrl,
                TwitchUsername = !string.IsNullOrEmpty(a.TwitchUsername)
                    ? a.TwitchUsername
                    : (!string.IsNullOrEmpty(a.TwitchChannelUrl)
                        ? a.TwitchChannelUrl.TrimEnd('/').Split('/').LastOrDefault() ?? a.TwitchChannelUrl
                        : string.Empty),
                TwitchId = a.TwitchId ?? a.User?.TwitchId,
                ApplicationNotes = a.ApplicationNotes,
                Status = a.Status.ToString().ToUpperInvariant(),
                StaffNotes = a.StaffNotes,
                ReviewedByStaffName = a.ReviewedByStaffName,
                TimestampApplied = a.TimestampApplied,
                TimestampReviewed = a.TimestampReviewed,
                TotalMatchesPlayed = 0,
                DisciplinaryReportCount = 0
            })
        ];

        if (!string.IsNullOrWhiteSpace(search))
        {
            string cleanSearch = search.Trim();
            dtoList =
            [
                .. dtoList.Where(d =>
                    d.MainAccountName.Contains(cleanSearch, StringComparison.OrdinalIgnoreCase) ||
                    d.TwitchUsername.Contains(cleanSearch, StringComparison.OrdinalIgnoreCase) ||
                    (d.StaffNotes != null && d.StaffNotes.Contains(cleanSearch, StringComparison.OrdinalIgnoreCase)))
            ];
        }

        StaffStreamersViewModel viewModel = new()
        {
            PendingApplications = [.. dtoList.Where(d => d.Status == "PENDING")],
            ApprovedStreamers = [.. dtoList.Where(d => d.Status == "APPROVED")],
            RejectedApplications = [.. dtoList.Where(d => d.Status == "REJECTED")],
            RevokedStreamers = [.. dtoList.Where(d => d.Status == "REVOKED")]
        };

        return View("Streamers", viewModel);
    }

    [HttpPost("streamers/{id:int}/approve")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApproveStreamerApplication(int id, [FromForm] string? staffNotes,
        CancellationToken cancellationToken = default)
    {
        if (!IsAuthorizedStaff()) return Forbid();

        string staffName = User.Identity?.Name ?? "Staff";
        bool success =
            await adminApiClient.ApproveStreamerApplicationAsync(id, staffName, staffNotes, cancellationToken);
        if (!success)
        {
            return NotFound(new { success = false, message = $"Streamer application #{id} not found." });
        }

        return Json(new { success = true, message = $"Streamer application #{id} approved." });
    }

    [HttpPost("streamers/{id:int}/reject")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RejectStreamerApplication(int id, [FromForm] string? reason,
        CancellationToken cancellationToken = default)
    {
        if (!IsAuthorizedStaff()) return Forbid();

        bool success = await adminApiClient.RejectStreamerApplicationAsync(id, reason, cancellationToken);
        if (!success)
        {
            return NotFound(new { success = false, message = $"Streamer application #{id} not found." });
        }

        return Json(new { success = true, message = $"Streamer application #{id} rejected." });
    }

    [HttpPost("streamers/{id:int}/revoke")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RevokeStreamerApplication(int id, [FromForm] string? reason,
        CancellationToken cancellationToken = default)
    {
        if (!IsAuthorizedStaff()) return Forbid();

        bool success = await adminApiClient.RevokeStreamerApplicationAsync(id, cancellationToken);
        if (!success)
        {
            return NotFound(new { success = false, message = $"Streamer application #{id} not found." });
        }

        return Json(new { success = true, message = $"Streamer privileges revoked for application #{id}." });
    }

    [HttpGet("store")]
    public IActionResult Store(string? category, string? search, int page = 1)
    {
        if (!IsAuthorizedStaff()) return RedirectToAction("Index", "Home");

        StaffStoreViewModel model = storeService.GetCatalog(category, search, page, pageSize: 16);
        return View("Store", model);
    }

    [HttpPost("store/update-item")]
    [ValidateAntiForgeryToken]
    public IActionResult UpdateStoreItem([FromBody] UpdateItemRequest request)
    {
        if (!IsAuthorizedStaff()) return Forbid();

        bool success = storeService.UpdateItem(request.Id, request.GoldCost, request.SilverCost, request.IsEnabled,
            request.Purchasable);
        if (!success) return NotFound(new { error = $"Store item #{request.Id} not found." });

        return Json(new { success = true, message = "Store item updated successfully." });
    }

    [HttpPost("store/bulk-discount")]
    [ValidateAntiForgeryToken]
    public IActionResult BulkDiscount([FromBody] BulkDiscountRequest request)
    {
        if (!IsAuthorizedStaff()) return Forbid();

        bool success = storeService.ApplyBulkDiscount(request.Category, request.Currency, request.DiscountPercent);
        if (!success)
            return BadRequest(new
                { error = "Failed to apply bulk discount. Validate category and discount percentage." });

        return Json(new
            { success = true, message = $"Applied {request.DiscountPercent}% discount across '{request.Category}'." });
    }

    [HttpPost("store/restore-baseline")]
    [ValidateAntiForgeryToken]
    public IActionResult RestoreBaseline()
    {
        if (!IsAuthorizedStaff()) return Forbid();

        bool success = storeService.RestoreBaselinePrices();
        if (!success) return BadRequest(new { error = "No custom price baseline modifications found." });

        return Json(new { success = true, message = "Restored all items to baseline configured prices." });
    }

    // --- LORE MANAGEMENT ENDPOINTS ---
    [HttpGet("lore")]
    public async Task<IActionResult> LoreManagement(CancellationToken ct = default)
    {
        if (!IsAuthorizedStaff()) return Forbid();
        if (loreApiClient == null) return NotFound("Lore service unavailable");

        IReadOnlyList<LoreArcDto> arcs = await loreApiClient.GetArcsAsync(includeUnpublished: true, ct);
        IReadOnlyList<LoreComicSummaryDto> comics =
            await loreApiClient.GetComicsAsync(arcId: null, includeUnpublished: true, ct);

        // Fetch staff rating metrics for each comic
        Dictionary<int, LoreRatingMetricDto> ratingMetrics = [];
        foreach (LoreComicSummaryDto c in comics)
        {
            LoreRatingMetricDto? metric = await loreApiClient.GetStaffRatingMetricsAsync(c.ID, ct);
            if (metric != null)
            {
                ratingMetrics[c.ID] = metric;
            }
        }

        ViewBag.Arcs = arcs;
        ViewBag.Comics = comics;
        ViewBag.RatingMetrics = ratingMetrics;
        ViewBag.CurrentStaffAccount = await ResolveCurrentStaffAccountAsync(ct);

        return View("LoreManagement", comics);
    }

    [HttpGet("lore/comic/new")]
    public async Task<IActionResult> NewComic(CancellationToken ct = default)
    {
        if (!IsAuthorizedStaff()) return Forbid();
        if (loreApiClient == null) return NotFound("Lore service unavailable");

        IReadOnlyList<LoreArcDto> arcs = await loreApiClient.GetArcsAsync(includeUnpublished: true, ct);
        ViewBag.Arcs = arcs;
        ViewBag.IsNew = true;
        ViewBag.CurrentStaffAccount = await ResolveCurrentStaffAccountAsync(ct);

        return View("ComicEditor", new LoreComicDetailDto());
    }

    [HttpGet("lore/comic/edit/{id:int}")]
    public async Task<IActionResult> EditComic(int id, CancellationToken ct = default)
    {
        if (!IsAuthorizedStaff()) return Forbid();
        if (loreApiClient == null) return NotFound("Lore service unavailable");

        AccountDto? staffAccount = await ResolveCurrentStaffAccountAsync(ct);
        LoreComicDetailDto? comic = await loreApiClient.GetComicByIdAsync(id, staffAccount?.ID, isStaff: true, ct);
        if (comic == null) return NotFound("Comic not found");

        IReadOnlyList<LoreArcDto> arcs = await loreApiClient.GetArcsAsync(includeUnpublished: true, ct);
        LoreRatingMetricDto? staffMetrics = await loreApiClient.GetStaffRatingMetricsAsync(id, ct);

        ViewBag.Arcs = arcs;
        ViewBag.IsNew = false;
        ViewBag.StaffMetrics = staffMetrics;
        ViewBag.CurrentStaffAccount = staffAccount;

        return View("ComicEditor", comic);
    }

    [HttpPost("lore/comic/save")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveComic([FromBody] SaveComicRequestDto request, CancellationToken ct = default)
    {
        if (!IsAuthorizedStaff()) return Forbid();
        if (loreApiClient == null) return BadRequest("Lore service unavailable");

        LoreComicSummaryDto? saved = await loreApiClient.SaveComicAsync(request, isStaff: true, ct);
        if (saved == null) return BadRequest("Failed to save comic");

        return Json(new { success = true, comic = saved });
    }

    [HttpPost("lore/comic/delete/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteComic(int id, CancellationToken ct = default)
    {
        if (!IsAuthorizedStaff()) return Forbid();
        if (loreApiClient == null) return BadRequest("Lore service unavailable");

        bool success = await loreApiClient.DeleteComicAsync(id, isStaff: true, ct);
        return Json(new { success });
    }

    [HttpPost("lore/arc/save")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveArc([FromBody] SaveArcRequestDto request, CancellationToken ct = default)
    {
        if (!IsAuthorizedStaff()) return Forbid();
        if (loreApiClient == null) return BadRequest("Lore service unavailable");

        LoreArcDto? saved = await loreApiClient.SaveArcAsync(request, isStaff: true, ct);
        if (saved == null) return BadRequest("Failed to save arc");

        return Json(new { success = true, arc = saved });
    }

    [HttpPost("lore/arc/delete/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteArc(int id, CancellationToken ct = default)
    {
        if (!IsAuthorizedStaff()) return Forbid();
        if (loreApiClient == null) return BadRequest("Lore service unavailable");

        bool success = await loreApiClient.DeleteArcAsync(id, isStaff: true, ct);
        return Json(new { success });
    }

    [HttpGet("invites")]
    public async Task<IActionResult> Invites([FromQuery] string? search = null, [FromQuery] string? success = null, [FromQuery] string? error = null, CancellationToken ct = default)
    {
        if (!IsAuthorizedStaff()) return Unauthorized();

        InviteSystemStatusDto? status = await adminApiClient.GetInviteSystemStatusAsync(ct);
        InviteTreeNodeDto? tree = null;

        if (!string.IsNullOrWhiteSpace(search))
        {
            int? targetUserId = null;
            if (int.TryParse(search.Trim().TrimStart('#'), out int parsedId))
            {
                UserDto? user = await userApiClient.GetUserByIdAsync(parsedId, ct);
                if (user != null)
                {
                    targetUserId = user.ID;
                }
                else
                {
                    AccountDto? acc = await userApiClient.GetAccountByIdAsync(parsedId, ct);
                    if (acc != null)
                    {
                        UserDto? u = await userApiClient.GetUserByAccountNameAsync(acc.Name, ct);
                        targetUserId = u?.ID;
                    }
                }
            }
            else
            {
                AccountDto? account = await userApiClient.GetAccountByNameAsync(search.Trim(), ct);
                if (account != null)
                {
                    UserDto? user = await userApiClient.GetUserByAccountNameAsync(account.Name, ct);
                    targetUserId = user?.ID;
                }
            }

            if (targetUserId.HasValue)
            {
                tree = await adminApiClient.GetInviteLineageTreeAsync(targetUserId.Value, ct);
                if (tree == null)
                {
                    error = $"No lineage graph found for user #{targetUserId.Value}.";
                }
            }
            else
            {
                error = $"No player account or user found matching '{search}'.";
            }
        }

        StaffInvitesViewModel model = new()
        {
            Status = status ?? new InviteSystemStatusDto(false, 0, 0, []),
            LineageTree = tree,
            SearchQuery = search,
            SuccessMessage = success,
            ErrorMessage = error
        };

        return View(model);
    }

    [HttpPost("invites/toggle")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleInviteMode([FromForm] bool enabled, [FromForm] string? notes, CancellationToken ct = default)
    {
        if (!IsAuthorizedStaff()) return Unauthorized();

        AccountDto? staffAcc = await ResolveCurrentStaffAccountAsync(ct);
        int staffAccountId = staffAcc?.ID ?? 1;
        string staffAccountName = staffAcc?.Name ?? User.Identity?.Name ?? "Staff";

        bool result = await adminApiClient.ToggleInviteOnlyModeAsync(enabled, notes, staffAccountId, staffAccountName, ct);

        string message = result
            ? $"Invite-only mode successfully {(enabled ? "ENABLED" : "DISABLED")}."
            : "Failed to update invite-only mode.";

        return RedirectToAction(nameof(Invites), new { success = result ? message : null, error = result ? null : message });
    }

    [HttpPost("invites/prune")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PruneInviteSubtree([FromForm] int rootUserId, [FromForm] string reason, [FromForm] string? searchQuery, CancellationToken ct = default)
    {
        if (!IsAuthorizedStaff()) return Unauthorized();

        if (string.IsNullOrWhiteSpace(reason))
        {
            reason = "Staff disciplinary subtree prune";
        }

        bool result = await adminApiClient.PruneInviteSubtreeAsync(rootUserId, reason, banAccounts: true, ct);

        string message = result
            ? $"Successfully pruned invite subtree starting at User #{rootUserId}. Accounts suspended and pending invites revoked."
            : $"Failed to prune invite subtree for User #{rootUserId}.";

        return RedirectToAction(nameof(Invites), new { search = searchQuery, success = result ? message : null, error = result ? null : message });
    }

    [HttpPost("unban")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UnbanAccount([FromForm] int accountId, [FromForm] string? returnUrl, CancellationToken ct = default)
    {
        if (!IsAuthorizedStaff()) return Unauthorized();

        bool result = await adminApiClient.UnbanAccountAsync(accountId, ct);

        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return Redirect(returnUrl);
        }

        return RedirectToAction(nameof(Invites), new { success = result ? $"Account #{accountId} successfully unbanned." : $"Failed to unban account #{accountId}." });
    }

    [HttpGet("invites/tree/{userId:int}")]
    public async Task<IActionResult> GetInviteTree(int userId, CancellationToken ct = default)
    {
        if (!IsAuthorizedStaff()) return Unauthorized();

        InviteTreeNodeDto? tree = await adminApiClient.GetInviteLineageTreeAsync(userId, ct);
        if (tree == null) return NotFound(new { error = "Lineage tree not found" });

        return Json(tree);
    }

    public record UpdateItemRequest(int Id, int GoldCost, int SilverCost, bool IsEnabled, bool Purchasable);

    public record BulkDiscountRequest(string Category, string Currency, int DiscountPercent);
}
