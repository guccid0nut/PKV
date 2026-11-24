// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json;
using ASPIRE.Common.DTOs;

namespace DAWNBRINGER.WebPortal.UI.Services.ApiClient;

public class AdminApiClient(HttpClient client, ILogger<AdminApiClient> logger) : IAdminApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<UserSummaryStatsDto> GetSummaryStatsAsync(CancellationToken ct = default)
    {
        try
        {
            UserSummaryStatsDto? stats =
                await client.GetFromJsonAsync<UserSummaryStatsDto>("api/admin/users/stats", JsonOptions, ct);
            return stats ?? new UserSummaryStatsDto
            {
                TotalUsers = 0, TotalAccounts = 0, TotalStaff = 0, TotalHosts = 0, IsDatabaseConnected = false,
                StatusMessage = "Error"
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[AdminApiClient] Error fetching admin summary stats");
            return new UserSummaryStatsDto
            {
                TotalUsers = 0, TotalAccounts = 0, TotalStaff = 0, TotalHosts = 0, IsDatabaseConnected = false,
                StatusMessage = ex.Message
            };
        }
    }

    public async Task<IReadOnlyList<AdminUserDetailDto>> SearchUsersAsync(string? query, string? roleFilter,
        int limit = 100, CancellationToken ct = default)
    {
        try
        {
            string url =
                $"api/admin/users?query={Uri.EscapeDataString(query ?? "")}&roleFilter={Uri.EscapeDataString(roleFilter ?? "")}";
            List<AdminUserDetailDto>? users =
                await client.GetFromJsonAsync<List<AdminUserDetailDto>>(url, JsonOptions, ct);
            return users ?? [];
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[AdminApiClient] Error searching users");
            return [];
        }
    }

    public async Task<AdminUserDetailDto?> GetUserByIdAsync(int id, CancellationToken ct = default)
    {
        try
        {
            return await client.GetFromJsonAsync<AdminUserDetailDto>($"api/admin/users/{id}", JsonOptions, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[AdminApiClient] Error getting user #{UserId}", id);
            return null;
        }
    }

    public async Task<AdminMetricsDTO> GetStaffMetricsAsync(CancellationToken ct = default)
    {
        DateTimeOffset now = TimeProvider.System.GetUtcNow();
        try
        {
            AdminMetricsDTO? metrics =
                await client.GetFromJsonAsync<AdminMetricsDTO>("api/admin/metrics", JsonOptions, ct);
            return metrics ??
                   new AdminMetricsDTO(
                       0, 0, 0, 0, [], 0, 0, 0, 0, [], now,
                       PopulationBreakdownDefaults.CreateUnavailable(now));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[AdminApiClient] Error fetching staff metrics");
            return new AdminMetricsDTO(
                0, 0, 0, 0, [], 0, 0, 0, 0, [], now,
                PopulationBreakdownDefaults.CreateUnavailable(now));
        }
    }

    public async Task<IReadOnlyList<ApprovedHostDto>> GetApprovedHostsAsync(CancellationToken ct = default)
    {
        try
        {
            List<ApprovedHostDto>? hosts =
                await client.GetFromJsonAsync<List<ApprovedHostDto>>("api/admin/fleet/hosts", JsonOptions, ct);
            return hosts ?? [];
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[AdminApiClient] Error fetching approved hosts");
            return [];
        }
    }

    public async Task<bool> SetUserRoleAsync(int userId, string roleName, CancellationToken ct = default)
    {
        try
        {
            HttpResponseMessage resp = await client.PostAsJsonAsync($"api/admin/users/{userId}/role",
                new SetUserRoleRequest { RoleName = roleName }, JsonOptions, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[AdminApiClient] Error setting role for user #{UserId}", userId);
            return false;
        }
    }

    public async Task<bool> SetAccountRoleAsync(int accountId, int accountType, CancellationToken ct = default)
    {
        try
        {
            HttpResponseMessage resp = await client.PostAsJsonAsync($"/api/admin/accounts/{accountId}/role",
                new SetAccountRoleRequest { Type = accountType }, JsonOptions, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[AdminApiClient] Error setting account role #{AccountId}", accountId);
            return false;
        }
    }

    public async Task<ResetCredentialResponse> ResetHostPasswordAsync(int accountId, CancellationToken ct = default)
    {
        try
        {
            HttpResponseMessage resp =
                await client.PostAsync($"/api/admin/accounts/{accountId}/reset-password", null, ct);
            resp.EnsureSuccessStatusCode();
            return (await resp.Content.ReadFromJsonAsync<ResetCredentialResponse>(JsonOptions, ct))!;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[AdminApiClient] Error resetting host password #{AccountId}", accountId);
            throw;
        }
    }

    public async Task<ResetCredentialResponse> ResetHostAuthTokenAsync(int accountId, CancellationToken ct = default)
    {
        try
        {
            HttpResponseMessage resp = await client.PostAsync($"/api/admin/accounts/{accountId}/reset-token", null, ct);
            resp.EnsureSuccessStatusCode();
            return (await resp.Content.ReadFromJsonAsync<ResetCredentialResponse>(JsonOptions, ct))!;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[AdminApiClient] Error resetting host auth token #{AccountId}", accountId);
            throw;
        }
    }

    public async Task<bool> UpdateUserEconomyAsync(int userId, UpdateUserEconomyRequest request,
        CancellationToken ct = default)
    {
        try
        {
            HttpResponseMessage resp =
                await client.PostAsJsonAsync($"api/admin/users/{userId}/economy", request, JsonOptions, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[AdminApiClient] Error updating user economy #{UserId}", userId);
            return false;
        }
    }

    public async Task<bool> AdjustUserBalanceAsync(int userId, AdjustUserBalanceRequest request,
        CancellationToken ct = default)
    {
        try
        {
            HttpResponseMessage resp =
                await client.PostAsJsonAsync($"api/admin/users/{userId}/adjust-balance", request, JsonOptions, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[AdminApiClient] Error adjusting user balance #{UserId}", userId);
            return false;
        }
    }

    public async Task<bool> PromoteUserToStaffAsync(int userId, CancellationToken ct = default)
    {
        try
        {
            HttpResponseMessage resp = await client.PostAsync($"api/admin/users/{userId}/promote-staff", null, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[AdminApiClient] Error promoting user to staff #{UserId}", userId);
            return false;
        }
    }

    public async Task<bool> PromoteUserToHostAsync(int userId, CancellationToken ct = default)
    {
        try
        {
            HttpResponseMessage resp = await client.PostAsync($"api/admin/users/{userId}/promote-host", null, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[AdminApiClient] Error promoting user to host #{UserId}", userId);
            return false;
        }
    }

    public async Task<bool> RevertUserToStandardAsync(int userId, CancellationToken ct = default)
    {
        try
        {
            HttpResponseMessage resp = await client.PostAsync($"api/admin/users/{userId}/revert-standard", null, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[AdminApiClient] Error reverting user to standard #{UserId}", userId);
            return false;
        }
    }

    public async Task<IReadOnlyList<PlayerReportDto>> GetPlayerReportsAsync(string? status, string? reason,
        string? search, int? matchId, int? reportId, int page = 1, int pageSize = 25, CancellationToken ct = default)
    {
        try
        {
            string url =
                $"api/admin/reports?status={Uri.EscapeDataString(status ?? "")}&reason={Uri.EscapeDataString(reason ?? "")}&search={Uri.EscapeDataString(search ?? "")}&matchId={matchId}&reportId={reportId}&page={page}&pageSize={pageSize}";
            List<PlayerReportDto>? reports = await client.GetFromJsonAsync<List<PlayerReportDto>>(url, JsonOptions, ct);
            return reports ?? [];
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[AdminApiClient] Error fetching player reports");
            return [];
        }
    }

    public async Task<int> GetPlayerReportsCountAsync(string? status, string? reason, string? search, int? matchId,
        int? reportId, CancellationToken ct = default)
    {
        try
        {
            string url =
                $"api/admin/reports/count?status={Uri.EscapeDataString(status ?? "")}&reason={Uri.EscapeDataString(reason ?? "")}&search={Uri.EscapeDataString(search ?? "")}&matchId={matchId}&reportId={reportId}";
            return await client.GetFromJsonAsync<int>(url, JsonOptions, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[AdminApiClient] Error fetching player reports count");
            return 0;
        }
    }

    public async Task<PlayerReportDto?> GetPlayerReportByIdAsync(int id, CancellationToken ct = default)
    {
        try
        {
            return await client.GetFromJsonAsync<PlayerReportDto>($"api/admin/reports/{id}", JsonOptions, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[AdminApiClient] Error fetching report #{ReportId}", id);
            return null;
        }
    }

    public async Task<bool> DismissReportAsync(int reportId, int staffAccountId, string? comment,
        CancellationToken ct = default)
    {
        try
        {
            HttpResponseMessage resp = await client.PostAsJsonAsync($"api/admin/reports/{reportId}/dismiss",
                new { StaffAccountId = staffAccountId, Comment = comment }, JsonOptions, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[AdminApiClient] Error dismissing report #{ReportId}", reportId);
            return false;
        }
    }

    public async Task<bool> ActionReportAsync(ActionReportRequestDto request, CancellationToken ct = default)
    {
        try
        {
            HttpResponseMessage resp =
                await client.PostAsJsonAsync("api/admin/reports/action", request, JsonOptions, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[AdminApiClient] Error actioning report #{ReportId}", request.ReportId);
            return false;
        }
    }

    public async Task<bool> DirectPunishAccountAsync(DirectPunishRequestDto request, CancellationToken ct = default)
    {
        try
        {
            HttpResponseMessage resp =
                await client.PostAsJsonAsync("api/admin/accounts/direct-punish", request, JsonOptions, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[AdminApiClient] Error punishing account '{Account}'", request.TargetAccountName);
            return false;
        }
    }

    public async Task<bool> BanAccountAsync(int accountId, DateTimeOffset? expiresAt, string reason,
        int? bannedBy = null, CancellationToken ct = default)
    {
        try
        {
            HttpResponseMessage resp = await client.PostAsJsonAsync($"api/admin/accounts/{accountId}/ban",
                new { ExpiresAt = expiresAt, Reason = reason, BannedBy = bannedBy }, JsonOptions, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[AdminApiClient] Error banning account #{AccountId}", accountId);
            return false;
        }
    }

    public async Task<bool> UnbanAccountAsync(int accountId, CancellationToken ct = default)
    {
        try
        {
            HttpResponseMessage resp = await client.PostAsync($"api/admin/accounts/{accountId}/unban", null, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[AdminApiClient] Error unbanning account #{AccountId}", accountId);
            return false;
        }
    }

    public async Task<IReadOnlyList<HostApplicationDto>> GetHostApplicationsAsync(CancellationToken ct = default)
    {
        try
        {
            List<HostApplicationDto>? apps =
                await client.GetFromJsonAsync<List<HostApplicationDto>>("api/admin/host-applications", JsonOptions, ct);
            return apps ?? [];
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[AdminApiClient] Error fetching host applications");
            return [];
        }
    }

    public async Task<bool> ApproveHostApplicationAsync(int applicationId, string? staffNotes = null,
        string? staffName = null, CancellationToken ct = default)
    {
        try
        {
            HttpResponseMessage resp = await client.PostAsJsonAsync(
                $"api/admin/host-applications/{applicationId}/approve",
                new { StaffNotes = staffNotes, StaffName = staffName }, JsonOptions, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[AdminApiClient] Error approving host app #{AppId}", applicationId);
            return false;
        }
    }

    public async Task<bool> RejectHostApplicationAsync(int applicationId, string? reason,
        CancellationToken ct = default)
    {
        try
        {
            HttpResponseMessage resp = await client.PostAsJsonAsync(
                $"api/admin/host-applications/{applicationId}/reject", new { Reason = reason }, JsonOptions, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[AdminApiClient] Error rejecting host app #{AppId}", applicationId);
            return false;
        }
    }

    public async Task<bool> RevokeHostApplicationAsync(int applicationId, string? reason = null,
        CancellationToken ct = default)
    {
        try
        {
            HttpResponseMessage resp = await client.PostAsJsonAsync(
                $"api/admin/host-applications/{applicationId}/revoke", new { Reason = reason }, JsonOptions, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[AdminApiClient] Error revoking host app #{AppId}", applicationId);
            return false;
        }
    }

    public async Task<IReadOnlyList<StreamerApplicationDto>> GetStreamerApplicationsAsync(
        CancellationToken ct = default)
    {
        try
        {
            List<StreamerApplicationDto>? apps =
                await client.GetFromJsonAsync<List<StreamerApplicationDto>>("api/admin/streamer-applications",
                    JsonOptions, ct);
            return apps ?? [];
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[AdminApiClient] Error fetching streamer applications");
            return [];
        }
    }

    public async Task<bool> ApproveStreamerApplicationAsync(int applicationId, string? staffName = null,
        string? staffNotes = null, CancellationToken ct = default)
    {
        try
        {
            HttpResponseMessage resp = await client.PostAsJsonAsync(
                $"api/admin/streamer-applications/{applicationId}/approve",
                new { StaffName = staffName, StaffNotes = staffNotes }, JsonOptions, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[AdminApiClient] Error approving streamer app #{AppId}", applicationId);
            return false;
        }
    }

    public async Task<bool> RejectStreamerApplicationAsync(int applicationId, string? reason,
        CancellationToken ct = default)
    {
        try
        {
            HttpResponseMessage resp = await client.PostAsJsonAsync(
                $"api/admin/streamer-applications/{applicationId}/reject", new { Reason = reason }, JsonOptions, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[AdminApiClient] Error rejecting streamer app #{AppId}", applicationId);
            return false;
        }
    }

    public async Task<bool> RevokeStreamerApplicationAsync(int applicationId, CancellationToken ct = default)
    {
        try
        {
            HttpResponseMessage resp =
                await client.PostAsync($"api/admin/streamer-applications/{applicationId}/revoke", null, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[AdminApiClient] Error revoking streamer app #{AppId}", applicationId);
            return false;
        }
    }

    public async Task<IReadOnlyList<AccountDto>> GetRecentAccountsAsync(int limit = 20, CancellationToken ct = default)
    {
        try
        {
            List<AccountDto>? accounts =
                await client.GetFromJsonAsync<List<AccountDto>>($"api/admin/accounts/recent?limit={limit}", JsonOptions,
                    ct);
            return accounts ?? [];
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[AdminApiClient] Error fetching recent accounts");
            return [];
        }
    }

    public async Task<AccountDto?> GetAccountDetailAsync(string accountName, CancellationToken ct = default)
    {
        try
        {
            return await client.GetFromJsonAsync<AccountDto>(
                $"api/admin/accounts/detail/{Uri.EscapeDataString(accountName)}", JsonOptions, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[AdminApiClient] Error fetching account detail for '{Account}'", accountName);
            return null;
        }
    }

    public async Task<bool> AddInventoryItemAsync(string accountName, string productCode,
        CancellationToken ct = default)
    {
        try
        {
            HttpResponseMessage resp = await client.PostAsJsonAsync("api/admin/inventory/add",
                new { AccountName = accountName, ProductCode = productCode }, JsonOptions, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[AdminApiClient] Error adding inventory item '{Item}' for '{Account}'", productCode,
                accountName);
            return false;
        }
    }

    public async Task<bool> AddInventoryItemsAsync(string accountName, IEnumerable<string> productCodes,
        CancellationToken ct = default)
    {
        foreach (string code in productCodes) await AddInventoryItemAsync(accountName, code, ct);
        return true;
    }

    public async Task<bool> RemoveInventoryItemAsync(string accountName, string productCode,
        CancellationToken ct = default)
    {
        try
        {
            HttpResponseMessage resp = await client.PostAsJsonAsync("api/admin/inventory/remove",
                new { AccountName = accountName, ProductCode = productCode }, JsonOptions, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[AdminApiClient] Error removing inventory item '{Item}' for '{Account}'", productCode,
                accountName);
            return false;
        }
    }

    public async Task<bool> ResetInventoryAsync(string accountName, CancellationToken ct = default)
    {
        try
        {
            HttpResponseMessage resp = await client.PostAsJsonAsync("api/admin/inventory/reset",
                new { AccountName = accountName }, JsonOptions, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[AdminApiClient] Error resetting inventory for '{Account}'", accountName);
            return false;
        }
    }

    public async Task<InviteSystemStatusDto?> GetInviteSystemStatusAsync(CancellationToken ct = default)
    {
        try
        {
            return await client.GetFromJsonAsync<InviteSystemStatusDto>("api/admin/invites/status", JsonOptions, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[AdminApiClient] Error fetching invite system status");
            return null;
        }
    }

    public async Task<bool> ToggleInviteOnlyModeAsync(bool enable, string? notes, int staffAccountId, string staffAccountName, CancellationToken ct = default)
    {
        try
        {
            HttpResponseMessage resp = await client.PostAsJsonAsync(
                $"api/admin/invites/toggle?staffAccountId={staffAccountId}&staffAccountName={Uri.EscapeDataString(staffAccountName)}",
                new InviteModeToggleRequestDto(enable, notes), JsonOptions, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[AdminApiClient] Error toggling invite-only mode");
            return false;
        }
    }

    public async Task<InviteTreeNodeDto?> GetInviteLineageTreeAsync(int rootUserId, CancellationToken ct = default)
    {
        try
        {
            return await client.GetFromJsonAsync<InviteTreeNodeDto>($"api/admin/invites/tree/{rootUserId}", JsonOptions, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[AdminApiClient] Error fetching invite lineage tree for user #{UserId}", rootUserId);
            return null;
        }
    }

    public async Task<bool> PruneInviteSubtreeAsync(int rootUserId, string reason, bool banAccounts = true, CancellationToken ct = default)
    {
        try
        {
            HttpResponseMessage resp = await client.PostAsJsonAsync("api/admin/invites/prune-branch",
                new PruneSubtreeRequestDto(rootUserId, reason, banAccounts), JsonOptions, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[AdminApiClient] Error pruning invite subtree for user #{UserId}", rootUserId);
            return false;
        }
    }
}
