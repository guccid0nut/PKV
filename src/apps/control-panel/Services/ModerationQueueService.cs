// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Net.Http.Json;
using System.Text.Json;
using MERRICK.DatabaseContext.Entities.Game;
using MERRICK.DatabaseContext.Persistence;
using Microsoft.EntityFrameworkCore;
using NEXUS.Contracts.Clients;
using NEXUS.Contracts.Configuration;
using NEXUS.Contracts.Security;

namespace NEXUS.ControlPanel.GUI.Services;

/// <summary>
/// Implements moderation queue retrieval and sanction dispatch against ZORGATH WebPortal Admin API
/// with optional direct <see cref="MerrickContext"/> fallback for offline/headless testing.
/// </summary>
public sealed class ModerationQueueService(
    ISecretsManagerService? secretsManager = null,
    HttpClient? httpClient = null,
    Action<string, string, bool>? logSink = null,
    Func<MerrickContext>? dbContextFactory = null,
    TimeProvider? timeProvider = null) : IModerationQueueService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient = httpClient ?? ASPIRE.Common.Http.SharedSocketsHttpClientFactory.Create(TimeSpan.FromSeconds(4));
    private readonly ISecretsManagerService? _secretsManager = secretsManager;
    private readonly Action<string, string, bool>? _logSink = logSink;
    private readonly Func<MerrickContext>? _dbContextFactory = dbContextFactory;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<List<PlayerReportDto>> GetReportsAsync(
        string? status = "PENDING",
        string? search = null,
        int page = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        string? effectiveStatus = string.IsNullOrWhiteSpace(status) ||
                                  string.Equals(status, "ALL", StringComparison.OrdinalIgnoreCase)
            ? null
            : status.Trim().ToUpperInvariant();

        if (_dbContextFactory is not null)
        {
            await using MerrickContext db = _dbContextFactory();
            IQueryable<PlayerReport> query = db.PlayerReports.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(effectiveStatus))
            {
                query = query.Where(r => r.Status == effectiveStatus);
            }

            if (!string.IsNullOrWhiteSpace(search))
            {
                string term = search.Trim();
                query = query.Where(r =>
                    r.ReportedAccountName.Contains(term) ||
                    r.ReporterAccountName.Contains(term) ||
                    r.Reason.Contains(term) ||
                    (r.Description != null && r.Description.Contains(term)));
            }

            List<PlayerReport> rows = await query
                .OrderByDescending(r => r.TimestampCreated)
                .Skip(Math.Max(0, page - 1) * Math.Clamp(pageSize, 1, 200))
                .Take(Math.Clamp(pageSize, 1, 200))
                .ToListAsync(cancellationToken);

            Log($"[MODERATION-QUEUE] Loaded {rows.Count} report(s) from Direct DB (status={effectiveStatus ?? "ALL"}).");
            return rows.Select(MapToDto).ToList();
        }

        try
        {
            List<string> queryParts = [$"page={Math.Max(1, page)}", $"pageSize={Math.Clamp(pageSize, 1, 200)}"];
            if (!string.IsNullOrWhiteSpace(effectiveStatus))
            {
                queryParts.Add($"status={Uri.EscapeDataString(effectiveStatus)}");
            }

            if (!string.IsNullOrWhiteSpace(search))
            {
                queryParts.Add($"search={Uri.EscapeDataString(search.Trim())}");
            }

            string path = $"api/admin/reports?{string.Join("&", queryParts)}";
            using HttpRequestMessage req = await CreateAuthenticatedRequestAsync(HttpMethod.Get, path, cancellationToken);
            using HttpResponseMessage resp = await _httpClient.SendAsync(req, cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                Log($"[MODERATION-QUEUE] GET {path} failed with HTTP {(int)resp.StatusCode}.", isError: true);
                return [];
            }

            List<PlayerReportDto>? reports = await resp.Content.ReadFromJsonAsync<List<PlayerReportDto>>(JsonOptions, cancellationToken);
            Log($"[MODERATION-QUEUE] Loaded {reports?.Count ?? 0} report(s) via WebPortal API (status={effectiveStatus ?? "ALL"}).");
            return reports ?? [];
        }
        catch (Exception ex)
        {
            Log($"[MODERATION-QUEUE] Failed to fetch moderation reports: {ex.Message}", isError: true);
            return [];
        }
    }

    public async Task<bool> ActionReportAsync(
        int reportId,
        string actionType,
        int? durationHours,
        string reason,
        string? staffNotes = null,
        int staffAccountId = 1,
        CancellationToken cancellationToken = default)
    {
        string normalizedAction = string.IsNullOrWhiteSpace(actionType) ? "MUTE" : actionType.Trim().ToUpperInvariant();
        if (_dbContextFactory is not null)
        {
            await using MerrickContext db = _dbContextFactory();
            PlayerReport? report = await db.PlayerReports.FirstOrDefaultAsync(r => r.ID == reportId, cancellationToken);
            if (report is null)
            {
                Log($"[MODERATION-QUEUE] ActionReport failed: Report #{reportId} not found in Direct DB.", isError: true);
                return false;
            }

            DateTimeOffset now = _timeProvider.GetUtcNow();
            DateTimeOffset? expiresAt = durationHours is > 0 ? now.AddHours(durationHours.Value) : null;

            report.Status = "RESOLVED";
            report.TimestampReviewed = now;
            report.ReviewedByStaffName = $"Staff#{staffAccountId}";
            report.PunishmentType = normalizedAction;
            report.PunishmentDurationHours = durationHours;
            report.PunishmentExpiration = expiresAt;
            report.StaffNotes = staffNotes;

            if (report.ReportedAccountID is int reportedId)
            {
                MERRICK.DatabaseContext.Entities.Core.Account? account =
                    await db.Accounts.FirstOrDefaultAsync(a => a.ID == reportedId, cancellationToken);
                if (account is not null)
                {
                    if (normalizedAction == "MUTE")
                    {
                        account.MuteExpiration = expiresAt ?? now.AddHours(24);
                        account.MuteReason = reason;
                    }
                    else if (normalizedAction == "SUSPEND")
                    {
                        account.SuspensionExpiration = expiresAt ?? now.AddHours(24);
                        account.SuspensionReason = reason;
                    }
                    else if (normalizedAction == "BAN")
                    {
                        account.BanExpiration = expiresAt;
                        account.BanReason = reason;
                        if (expiresAt is null)
                        {
                            account.Type = NEXUS.Contracts.Domain.AccountType.Disabled;
                        }
                    }
                }
            }

            await db.SaveChangesAsync(cancellationToken);
            Log($"[MODERATION-QUEUE] Direct DB resolved Report #{reportId} with {normalizedAction} (DurationHours={durationHours?.ToString() ?? "PERM"}).");
            return true;
        }

        try
        {
            ActionReportRequestDto payload = new(
                ReportId: reportId,
                StaffAccountId: staffAccountId,
                ActionType: normalizedAction,
                Reason: reason,
                DurationHours: durationHours,
                StaffNotes: staffNotes);

            using HttpRequestMessage req = await CreateAuthenticatedRequestAsync(HttpMethod.Post, "api/admin/reports/action", cancellationToken);
            req.Content = JsonContent.Create(payload);
            using HttpResponseMessage resp = await _httpClient.SendAsync(req, cancellationToken);
            if (resp.IsSuccessStatusCode)
            {
                Log($"[MODERATION-QUEUE] Applied {normalizedAction} to Report #{reportId} via WebPortal API.");
                return true;
            }

            Log($"[MODERATION-QUEUE] ActionReport #{reportId} failed with HTTP {(int)resp.StatusCode}.", isError: true);
            return false;
        }
        catch (Exception ex)
        {
            Log($"[MODERATION-QUEUE] ActionReport #{reportId} error: {ex.Message}", isError: true);
            return false;
        }
    }

    public async Task<bool> DismissReportAsync(
        int reportId,
        int staffAccountId = 1,
        string? comment = null,
        CancellationToken cancellationToken = default)
    {
        if (_dbContextFactory is not null)
        {
            await using MerrickContext db = _dbContextFactory();
            PlayerReport? report = await db.PlayerReports.FirstOrDefaultAsync(r => r.ID == reportId, cancellationToken);
            if (report is null)
            {
                return false;
            }

            report.Status = "DISMISSED";
            report.TimestampReviewed = _timeProvider.GetUtcNow();
            report.ReviewedByStaffName = $"Staff#{staffAccountId}";
            report.PunishmentType = "NONE";
            report.StaffNotes = comment;
            await db.SaveChangesAsync(cancellationToken);
            Log($"[MODERATION-QUEUE] Direct DB dismissed Report #{reportId}.");
            return true;
        }

        try
        {
            using HttpRequestMessage req = await CreateAuthenticatedRequestAsync(
                HttpMethod.Post,
                $"api/admin/reports/{reportId}/dismiss",
                cancellationToken);
            req.Content = JsonContent.Create(new { StaffAccountId = staffAccountId, Comment = comment });
            using HttpResponseMessage resp = await _httpClient.SendAsync(req, cancellationToken);
            if (resp.IsSuccessStatusCode)
            {
                Log($"[MODERATION-QUEUE] Dismissed Report #{reportId} via WebPortal API.");
                return true;
            }

            Log($"[MODERATION-QUEUE] DismissReport #{reportId} failed with HTTP {(int)resp.StatusCode}.", isError: true);
            return false;
        }
        catch (Exception ex)
        {
            Log($"[MODERATION-QUEUE] DismissReport #{reportId} error: {ex.Message}", isError: true);
            return false;
        }
    }

    private static PlayerReportDto MapToDto(PlayerReport r) => new()
    {
        ID = r.ID,
        MatchID = r.MatchID,
        ReporterAccountID = r.ReporterAccountID,
        ReporterAccountName = r.ReporterAccountName,
        ReportedAccountID = r.ReportedAccountID,
        ReportedAccountName = r.ReportedAccountName,
        Reason = r.Reason,
        Description = r.Description ?? string.Empty,
        Status = r.Status,
        TimestampCreated = r.TimestampCreated,
        TimestampReviewed = r.TimestampReviewed,
        ReviewedByStaffName = r.ReviewedByStaffName,
        PunishmentType = r.PunishmentType,
        PunishmentDurationHours = r.PunishmentDurationHours
    };

    private async Task<HttpRequestMessage> CreateAuthenticatedRequestAsync(
        HttpMethod method,
        string path,
        CancellationToken cancellationToken)
    {
        string baseUrl = await ResolveBaseApiUrlAsync(cancellationToken);
        HttpRequestMessage req = new(method, $"{baseUrl}/{path.TrimStart('/')}");

        string serviceKey = SecurityConstants.DefaultInternalServiceKey;
        if (_secretsManager is not null)
        {
            try
            {
                NexusSecrets secrets = await _secretsManager.LoadSecretsAsync(cancellationToken);
                if (!string.IsNullOrWhiteSpace(secrets.InternalServiceKey))
                {
                    serviceKey = secrets.InternalServiceKey;
                }
            }
            catch
            {
                // Keep fallback
            }
        }

        req.Headers.TryAddWithoutValidation(SecurityConstants.InternalServiceKeyHeader, serviceKey);
        return req;
    }

    private async Task<string> ResolveBaseApiUrlAsync(CancellationToken cancellationToken)
    {
        string? envUrl = Environment.GetEnvironmentVariable("WEB_PORTAL_API_URL")
                         ?? Environment.GetEnvironmentVariable("ZORGATH_API_URL");
        if (!string.IsNullOrWhiteSpace(envUrl))
        {
            return envUrl.TrimEnd('/');
        }

        int port = 5200;
        if (_secretsManager is not null)
        {
            try
            {
                NexusSecrets secrets = await _secretsManager.LoadSecretsAsync(cancellationToken);
                if (secrets.WebPortalApiHttpPort > 0 && secrets.WebPortalApiHttpPort != 5100)
                {
                    port = secrets.WebPortalApiHttpPort;
                }
            }
            catch
            {
                // Fallback default port
            }
        }

        return $"http://127.0.0.1:{port}";
    }

    private void Log(string message, bool isError = false)
        => _logSink?.Invoke("MODERATION", message, isError);
}
