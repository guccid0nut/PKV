// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NEXUS.Contracts.Domain;
using NEXUS.ControlPanel.GUI.Services;

namespace NEXUS.ControlPanel.GUI.ViewModels;

public sealed partial class UsersViewModel : ViewModelBase
{
    private readonly IUserManagementService _userManagementService;

    [ObservableProperty] private string _actionFeedbackText = string.Empty;

    [ObservableProperty] private int _editableGoldCoins;

    [ObservableProperty] private int _editablePlinkoTickets;

    [ObservableProperty] private int _editableSilverCoins;

    [ObservableProperty] private bool _isActionFeedbackSuccess = true;

    [ObservableProperty] private bool _isActionFeedbackVisible;

    [ObservableProperty] private bool _isDatabaseConnected;

    [ObservableProperty] private string _connectionStatusText = "CONNECTING...";

    [ObservableProperty] private bool _isLoading;

    [ObservableProperty] private string _roleFilter = "ALL";

    [ObservableProperty] private string _searchQuery = string.Empty;

    [ObservableProperty] private UserDetailDto? _selectedUser;

    [ObservableProperty] private string _statusMessage = string.Empty;

    [ObservableProperty] private int _totalAccounts;

    [ObservableProperty] private int _totalHosts;

    [ObservableProperty] private int _totalStaff;

    [ObservableProperty] private int _totalUsers;

    [ObservableProperty] private ObservableCollection<UserDetailDto> _users = [];

    public UsersViewModel(IUserManagementService userManagementService)
    {
        _userManagementService = userManagementService;
        _ = LoadInitialDataAsync();
    }

    public bool HasSelectedUser => SelectedUser is not null;

    public bool HasCurrencyChanges => SelectedUser is not null &&
                                      (EditableGoldCoins != SelectedUser.GoldCoins ||
                                       EditableSilverCoins != SelectedUser.SilverCoins ||
                                       EditablePlinkoTickets != SelectedUser.PlinkoTickets);

    public static AccountType[] AvailableAccountTypes =>
    [
        AccountType.Normal,
        AccountType.Legacy,
        AccountType.ServerHost,
        AccountType.Staff,
        AccountType.GameMaster,
        AccountType.MatchModerator,
        AccountType.MatchCaster,
        AccountType.VIP,
        AccountType.Streamer,
        AccountType.Disabled
    ];

    partial void OnSelectedUserChanged(UserDetailDto? value)
    {
        if (value is not null)
        {
            EditableGoldCoins = value.GoldCoins;
            EditableSilverCoins = value.SilverCoins;
            EditablePlinkoTickets = value.PlinkoTickets;
        }

        OnPropertyChanged(nameof(HasSelectedUser));
        OnPropertyChanged(nameof(HasCurrencyChanges));
    }

    partial void OnEditableGoldCoinsChanged(int value)
    {
        _ = value;
        OnPropertyChanged(nameof(HasCurrencyChanges));
    }

    partial void OnEditableSilverCoinsChanged(int value)
    {
        _ = value;
        OnPropertyChanged(nameof(HasCurrencyChanges));
    }

    partial void OnEditablePlinkoTicketsChanged(int value)
    {
        _ = value;
        OnPropertyChanged(nameof(HasCurrencyChanges));
    }

    partial void OnSearchQueryChanged(string value)
    {
        _ = value;
        _ = SearchUsersAsync();
    }

    public async Task LoadInitialDataAsync()
    {
        await RefreshStatsAsync();
        await SearchUsersAsync();
    }

    [RelayCommand]
    public async Task RefreshStatsAsync()
    {
        IsLoading = true;
        try
        {
            UserSummaryStats stats = await _userManagementService.GetSummaryStatsAsync();
            string statusText = stats.IsDatabaseConnected
                ? ((stats.StatusMessage != null && stats.StatusMessage.Contains("Supabase", StringComparison.OrdinalIgnoreCase))
                    ? "SUPABASE DIRECT"
                    : "API CONNECTED")
                : "DB / API OFFLINE";

            if (Dispatcher.UIThread.CheckAccess())
            {
                TotalUsers = stats.TotalUsers;
                TotalAccounts = stats.TotalAccounts;
                TotalStaff = stats.TotalStaff;
                TotalHosts = stats.TotalHosts;
                IsDatabaseConnected = stats.IsDatabaseConnected;
                StatusMessage = stats.StatusMessage ?? string.Empty;
                ConnectionStatusText = statusText;
            }
            else
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    TotalUsers = stats.TotalUsers;
                    TotalAccounts = stats.TotalAccounts;
                    TotalStaff = stats.TotalStaff;
                    TotalHosts = stats.TotalHosts;
                    IsDatabaseConnected = stats.IsDatabaseConnected;
                    StatusMessage = stats.StatusMessage ?? string.Empty;
                    ConnectionStatusText = statusText;
                });
            }

            await SearchUsersAsync();
        }
        catch (Exception ex)
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                IsDatabaseConnected = false;
                StatusMessage = $"Failed to fetch stats: {ex.Message}";
            }
            else
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    IsDatabaseConnected = false;
                    StatusMessage = $"Failed to fetch stats: {ex.Message}";
                });
            }
        }
        finally
        {
            if (Dispatcher.UIThread.CheckAccess())
                IsLoading = false;
            else
                await Dispatcher.UIThread.InvokeAsync(() => { IsLoading = false; });
        }
    }

    [RelayCommand]
    public async Task SearchUsersAsync()
    {
        IsLoading = true;
        try
        {
            string filter = RoleFilter == "ALL" ? string.Empty : RoleFilter;
            List<UserDetailDto> results = await _userManagementService.SearchUsersAsync(SearchQuery, filter);

            void ApplyResults()
            {
                Users.Clear();
                foreach (UserDetailDto dto in results)
                {
                    Users.Add(dto);
                    _ = HydrateUserAvatarsAsync(dto);
                }

                if (SelectedUser is not null)
                {
                    int currentSelectedId = SelectedUser.Id;
                    SelectedUser = Users.Count > 0
                        ? Users.FirstOrDefault(u => u.Id == currentSelectedId) ?? Users[0]
                        : null;
                }
                else if (Users.Count > 0)
                {
                    SelectedUser = Users[0];
                }
            }

            if (Dispatcher.UIThread.CheckAccess())
                ApplyResults();
            else
                await Dispatcher.UIThread.InvokeAsync(ApplyResults);
        }
        catch (Exception ex)
        {
            if (Dispatcher.UIThread.CheckAccess())
                ShowActionFeedback($"Search error: {ex.Message}", false);
            else
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    ShowActionFeedback($"Search error: {ex.Message}", false);
                });
        }
        finally
        {
            if (Dispatcher.UIThread.CheckAccess())
                IsLoading = false;
            else
                await Dispatcher.UIThread.InvokeAsync(() => { IsLoading = false; });
        }
    }

    [RelayCommand]
    public void SetRoleFilter(string filter)
    {
        RoleFilter = filter.ToUpperInvariant();
        _ = SearchUsersAsync();
    }

    [RelayCommand]
    public void ClearSearch()
    {
        SearchQuery = string.Empty;
        RoleFilter = "ALL";
        _ = SearchUsersAsync();
    }

    [RelayCommand]
    public async Task SaveCurrenciesAsync()
    {
        if (SelectedUser == null) return;

        IsLoading = true;
        try
        {
            bool success = await _userManagementService.UpdateUserEconomyAsync(
                SelectedUser.Id,
                EditableGoldCoins,
                EditableSilverCoins,
                EditablePlinkoTickets);

            if (success)
            {
                ShowActionFeedback(
                    $"✓ Currencies updated for '{SelectedUser.DisplayName}': 🪙 {EditableGoldCoins:N0} Gold, 🥈 {EditableSilverCoins:N0} Silver, 🎟️ {EditablePlinkoTickets:N0} Tickets",
                    true);
                await ReloadSelectedUserAsync();
            }
            else
            {
                ShowActionFeedback("Failed to update user currencies.", false);
            }
        }
        catch (Exception ex)
        {
            ShowActionFeedback($"Error: {ex.Message}", false);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    public async Task PromoteToStaffAsync()
    {
        if (SelectedUser == null) return;

        IsLoading = true;
        try
        {
            bool success = await _userManagementService.PromoteUserToStaffAsync(SelectedUser.Id);
            if (success)
            {
                ShowActionFeedback($"✓ User '{SelectedUser.DisplayName}' promoted to STAFF across all linked accounts!",
                    true);
                await ReloadSelectedUserAsync();
                await RefreshStatsAsync();
            }
            else
            {
                ShowActionFeedback("Failed to promote user to Staff.", false);
            }
        }
        catch (Exception ex)
        {
            ShowActionFeedback($"Error: {ex.Message}", false);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    public async Task PromoteToHostAsync()
    {
        if (SelectedUser == null) return;

        IsLoading = true;
        try
        {
            bool success = await _userManagementService.PromoteUserToHostAsync(SelectedUser.Id);
            if (success)
            {
                ShowActionFeedback(
                    $"✓ User '{SelectedUser.DisplayName}' granted HOST privileges (ServerHost)! Pending host apps approved.",
                    true);
                await ReloadSelectedUserAsync();
                await RefreshStatsAsync();
            }
            else
            {
                ShowActionFeedback("Failed to promote user to Host.", false);
            }
        }
        catch (Exception ex)
        {
            ShowActionFeedback($"Error: {ex.Message}", false);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    public async Task RevertToUserAsync()
    {
        if (SelectedUser == null) return;

        IsLoading = true;
        try
        {
            bool success = await _userManagementService.RevertUserToStandardAsync(SelectedUser.Id);
            if (success)
            {
                ShowActionFeedback($"✓ User '{SelectedUser.DisplayName}' reverted to standard USER.", true);
                await ReloadSelectedUserAsync();
                await RefreshStatsAsync();
            }
            else
            {
                ShowActionFeedback("Failed to revert user.", false);
            }
        }
        catch (Exception ex)
        {
            ShowActionFeedback($"Error: {ex.Message}", false);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    public async Task UpdateAccountRoleAsync(AccountItemDto? account)
    {
        if (account == null) return;

        IsLoading = true;
        try
        {
            bool success = await _userManagementService.SetAccountRoleAsync(account.Id, account.Type);
            if (success)
            {
                ShowActionFeedback($"✓ Account '{account.Name}' type updated to {account.Type}", true);
                await ReloadSelectedUserAsync();
                await RefreshStatsAsync();
            }
            else
            {
                ShowActionFeedback($"Failed to update account '{account.Name}'.", false);
            }
        }
        catch (Exception ex)
        {
            ShowActionFeedback($"Error: {ex.Message}", false);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    public void ToggleRevealHostPassword(AccountItemDto? account)
    {
        if (account == null) return;

        account.IsPasswordRevealed = !account.IsPasswordRevealed;
    }

    [RelayCommand]
    public async Task CopyHostPasswordAsync(AccountItemDto? account)
    {
        if (string.IsNullOrEmpty(account?.HostPassword))
        {
            ShowActionFeedback("No host password available to copy.", false);
            return;
        }

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime
            {
                MainWindow.Clipboard: not null
            } desktop)
        {
            await desktop.MainWindow.Clipboard.SetTextAsync(account.HostPassword);
            ShowActionFeedback($"✓ Copied host password for '{account.Name}' to clipboard!", true);
        }
    }

    [RelayCommand]
    public async Task ResetHostPasswordAsync(AccountItemDto? account)
    {
        if (account is null) return;

        IsLoading = true;
        try
        {
            string? newPass = await _userManagementService.ResetHostPasswordAsync(account.Id);
            if (!string.IsNullOrEmpty(newPass))
            {
                account.HostPassword = newPass;
                account.IsPasswordRevealed = true;
                ShowActionFeedback($"✓ Host password for '{account.Name}' reset successfully!", true);
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime
                    {
                        MainWindow.Clipboard: not null
                    } desktop) await desktop.MainWindow.Clipboard.SetTextAsync(newPass);
            }
            else
            {
                ShowActionFeedback($"Failed to reset host password for '{account.Name}'.", false);
            }
        }
        catch (Exception ex)
        {
            ShowActionFeedback($"Error: {ex.Message}", false);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    public void ToggleRevealAuthToken(AccountItemDto? account)
    {
        if (account is null) return;

        account.IsAuthTokenRevealed = !account.IsAuthTokenRevealed;
    }

    [RelayCommand]
    public async Task CopyAuthTokenAsync(AccountItemDto? account)
    {
        if (string.IsNullOrEmpty(account?.AuthToken))
        {
            ShowActionFeedback("No COMPEL auth token available to copy.", false);
            return;
        }

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime
            {
                MainWindow.Clipboard: not null
            } desktop)
        {
            await desktop.MainWindow.Clipboard.SetTextAsync(account.AuthToken);
            ShowActionFeedback($"✓ Copied COMPEL auth token for '{account.Name}' to clipboard!", true);
        }
    }

    [RelayCommand]
    public async Task ResetAuthTokenAsync(AccountItemDto? account)
    {
        if (account is null) return;

        IsLoading = true;
        try
        {
            string? newToken = await _userManagementService.ResetHostAuthTokenAsync(account.Id);
            if (!string.IsNullOrEmpty(newToken))
            {
                account.AuthToken = newToken;
                account.IsAuthTokenRevealed = true;
                ShowActionFeedback($"✓ COMPEL auth token for '{account.Name}' reset successfully!", true);
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime
                    {
                        MainWindow.Clipboard: not null
                    } desktop) await desktop.MainWindow.Clipboard.SetTextAsync(newToken);
            }
            else
            {
                ShowActionFeedback($"Failed to reset COMPEL auth token for '{account.Name}'.", false);
            }
        }
        catch (Exception ex)
        {
            ShowActionFeedback($"Error: {ex.Message}", false);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task ReloadSelectedUserAsync()
    {
        if (SelectedUser is null) return;

        UserDetailDto? updated = await _userManagementService.GetUserByIdAsync(SelectedUser.Id);
        if (updated is not null)
        {
            int index = Users.IndexOf(SelectedUser);
            if (index >= 0) Users[index] = updated;

            SelectedUser = updated;
            EditableGoldCoins = updated.GoldCoins;
            EditableSilverCoins = updated.SilverCoins;
            EditablePlinkoTickets = updated.PlinkoTickets;
            OnPropertyChanged(nameof(HasCurrencyChanges));
            _ = HydrateUserAvatarsAsync(updated);
        }
    }

    private async Task HydrateUserAvatarsAsync(UserDetailDto user)
    {
        try
        {
            Bitmap? avatar = await DiscordAvatarService.LoadAvatarFromCdnAsync(user.DiscordId, user.DiscordAvatar);
            avatar ??= DiscordAvatarService.GetDefaultAvatar();

            if (avatar != null)
            {
                if (Dispatcher.UIThread.CheckAccess())
                {
                    user.AvatarBitmap = avatar;
                    foreach (AccountItemDto acc in user.Accounts)
                    {
                        acc.AvatarBitmap = avatar;
                    }
                }
                else
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        user.AvatarBitmap = avatar;
                        foreach (AccountItemDto acc in user.Accounts)
                        {
                            acc.AvatarBitmap = avatar;
                        }
                    });
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[UsersViewModel] Failed to load avatar for user #{user.Id}: {ex.Message}");
        }
    }

    private void ShowActionFeedback(string message, bool success)
    {
        ActionFeedbackText = message;
        IsActionFeedbackSuccess = success;
        IsActionFeedbackVisible = true;
    }

    [RelayCommand]
    public void DismissFeedback()
    {
        IsActionFeedbackVisible = false;
    }
}
