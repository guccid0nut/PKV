// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NEXUS.Contracts.Clients;
using NEXUS.ControlPanel.GUI.Services;

namespace NEXUS.ControlPanel.GUI.ViewModels;

/// <summary>
/// ViewModel backing the NEXUS Control Panel Moderation Queue tab:
/// displays player reports, supports status/search filtering, and dispatches Mute, Ban, and Dismiss actions.
/// </summary>
public sealed partial class ModerationQueueViewModel : ViewModelBase
{
    private readonly IModerationQueueService _moderationService;

    [ObservableProperty] private ObservableCollection<PlayerReportDto> _reports = [];
    [ObservableProperty] private PlayerReportDto? _selectedReport;
    [ObservableProperty] private string _statusFilter = "PENDING";
    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private int _pendingCount;
    [ObservableProperty] private int _sanctionDurationHours = 24;
    [ObservableProperty] private string _staffNotes = string.Empty;
    [ObservableProperty] private string _statusBannerText = "Ready — select a report to review or sanction.";
    [ObservableProperty] private bool _isStatusBannerError;
    [ObservableProperty] private bool _isLoading;

    public static string[] AvailableStatusFilters => ["PENDING", "RESOLVED", "DISMISSED", "ALL"];

    public bool HasSelectedReport => SelectedReport is not null;

    public ModerationQueueViewModel(IModerationQueueService moderationService, bool autoLoad = true)
    {
        _moderationService = moderationService ?? throw new ArgumentNullException(nameof(moderationService));
        if (autoLoad)
        {
            _ = RefreshReportsAsync();
        }
    }

    partial void OnSelectedReportChanged(PlayerReportDto? value)
    {
        OnPropertyChanged(nameof(HasSelectedReport));
        if (value is not null && string.IsNullOrWhiteSpace(StaffNotes))
        {
            StaffNotes = $"Reviewed Report #{value.ID} ({value.Reason})";
        }
    }

    partial void OnStatusFilterChanged(string value)
    {
        _ = value;
        _ = RefreshReportsAsync();
    }

    [RelayCommand]
    public async Task RefreshReportsAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        try
        {
            List<PlayerReportDto> loaded = await _moderationService.GetReportsAsync(
                status: StatusFilter,
                search: string.IsNullOrWhiteSpace(SearchQuery) ? null : SearchQuery.Trim());

            Reports = new ObservableCollection<PlayerReportDto>(loaded);
            PendingCount = loaded.Count(r => string.Equals(r.Status, "PENDING", StringComparison.OrdinalIgnoreCase));

            if (SelectedReport is not null)
            {
                SelectedReport = loaded.FirstOrDefault(r => r.ID == SelectedReport.ID);
            }

            SelectedReport ??= loaded.FirstOrDefault();
            IsStatusBannerError = false;
            StatusBannerText = loaded.Count == 0
                ? $"No moderation reports matching filter '{StatusFilter}'."
                : $"Loaded {loaded.Count} report(s) ({PendingCount} pending triage).";
        }
        catch (Exception ex)
        {
            IsStatusBannerError = true;
            StatusBannerText = $"Failed to refresh moderation queue: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    public async Task MuteSelectedAsync()
    {
        await ApplySanctionToSelectedAsync("MUTE", Math.Max(1, SanctionDurationHours));
    }

    [RelayCommand]
    public async Task BanSelectedAsync()
    {
        int? duration = SanctionDurationHours > 0 ? SanctionDurationHours : null;
        await ApplySanctionToSelectedAsync("BAN", duration);
    }

    [RelayCommand]
    public async Task DismissSelectedAsync()
    {
        if (SelectedReport is null) return;
        int reportId = SelectedReport.ID;
        bool ok = await _moderationService.DismissReportAsync(
            reportId,
            staffAccountId: 1,
            comment: string.IsNullOrWhiteSpace(StaffNotes) ? "Dismissed from NEXUS Control Panel" : StaffNotes.Trim());

        if (ok)
        {
            IsStatusBannerError = false;
            StatusBannerText = $"Report #{reportId} dismissed cleanly.";
            await RefreshReportsAsync();
        }
        else
        {
            IsStatusBannerError = true;
            StatusBannerText = $"Failed to dismiss Report #{reportId}. Check WebPortal API / DB connectivity.";
        }
    }

    private async Task ApplySanctionToSelectedAsync(string actionType, int? durationHours)
    {
        if (SelectedReport is null) return;
        PlayerReportDto target = SelectedReport;
        bool ok = await _moderationService.ActionReportAsync(
            reportId: target.ID,
            actionType: actionType,
            durationHours: durationHours,
            reason: target.Reason,
            staffNotes: string.IsNullOrWhiteSpace(StaffNotes) ? $"{actionType} issued via Control Panel" : StaffNotes.Trim(),
            staffAccountId: 1);

        if (ok)
        {
            IsStatusBannerError = false;
            string durLabel = durationHours is > 0 ? $"{durationHours}h" : "Permanent";
            StatusBannerText = $"Applied {actionType} ({durLabel}) to '{target.ReportedAccountName}' (Report #{target.ID}).";
            await RefreshReportsAsync();
        }
        else
        {
            IsStatusBannerError = true;
            StatusBannerText = $"Failed to apply {actionType} on Report #{target.ID}.";
        }
    }
}
