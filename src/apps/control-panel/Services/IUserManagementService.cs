// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using NEXUS.Contracts.Domain;

namespace NEXUS.ControlPanel.GUI.Services;

public sealed class UserSummaryStats
{
    public int TotalUsers { get; set; }
    public int TotalAccounts { get; set; }
    public int TotalStaff { get; set; }
    public int TotalHosts { get; set; }
    public bool IsDatabaseConnected { get; set; }
    public string? StatusMessage { get; set; }
}

public sealed partial class AccountItemDto : ObservableObject
{
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(AuthTokenDisplay))]
    private string? _authToken;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HostPasswordDisplay))]
    private string? _hostPassword;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AuthTokenDisplay))]
    [NotifyPropertyChangedFor(nameof(AuthTokenRevealIcon))]
    private bool _isAuthTokenRevealed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HostPasswordDisplay))]
    [NotifyPropertyChangedFor(nameof(PasswordRevealIcon))]
    private bool _isPasswordRevealed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCustomIcon))]
    [NotifyPropertyChangedFor(nameof(IconDisplay))]
    private string? _icon;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAvatar))]
    private Bitmap? _avatarBitmap;

    public bool HasAvatar => AvatarBitmap is not null;
    public bool HasCustomIcon => !string.IsNullOrWhiteSpace(Icon);
    public string IconDisplay => !string.IsNullOrWhiteSpace(Icon) ? $"🏷️ {Icon}" : string.Empty;

    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsMain { get; set; }
    public bool IsStaff { get; set; }
    public AccountType Type { get; set; } = AccountType.Normal;
    public int AscensionLevel { get; set; }
    public DateTimeOffset TimestampCreated { get; set; }
    public DateTimeOffset TimestampLastActive { get; set; }

    public int? HostApplicationId { get; set; }

    public string HostPasswordDisplay => IsPasswordRevealed && !string.IsNullOrEmpty(HostPassword)
        ? HostPassword
        : string.IsNullOrEmpty(HostPassword)
            ? "None Generated"
            : "••••••••••••••••";

    public string PasswordRevealIcon => IsPasswordRevealed ? "🙈 Hide" : "👁️ Reveal";

    public string AuthTokenDisplay => IsAuthTokenRevealed && !string.IsNullOrEmpty(AuthToken)
        ? AuthToken
        : string.IsNullOrEmpty(AuthToken)
            ? "None Generated"
            : "••••••••••••••••";

    public string AuthTokenRevealIcon => IsAuthTokenRevealed ? "🙈 Hide" : "👁️ Reveal";

    public bool IsHost => Type == AccountType.ServerHost;
    public string CategoryTag => IsMain ? "MAIN" : IsHost ? "HOST" : "SUB";
    public string CategoryIcon => IsMain ? "👑" : IsHost ? "🖥️" : "🎮";
}

public sealed partial class UserDetailDto : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAvatar))]
    private Bitmap? _avatarBitmap;

    public bool HasAvatar => AvatarBitmap is not null;

    public int Id { get; set; }
    public string EmailAddress { get; set; } = string.Empty;
    public string? DiscordId { get; set; }
    public string? DiscordUsername { get; set; }
    public string? DiscordAvatar { get; set; }
    public string RoleName { get; set; } = string.Empty;
    public bool IsStaff { get; set; }
    public int GoldCoins { get; set; }
    public int SilverCoins { get; set; }
    public int PlinkoTickets { get; set; }
    public int TotalLevel { get; set; }
    public int TotalExperience { get; set; }
    public DateTimeOffset TimestampCreated { get; set; }
    public DateTimeOffset TimestampLastActive { get; set; }
    public List<AccountItemDto> Accounts { get; set; } = [];

    public string DisplayName => !string.IsNullOrWhiteSpace(DiscordUsername)
        ? $"@{DiscordUsername}"
        : MainAccount != null
            ? MainAccount.Name
            : $"User #{Id}";

    public AccountItemDto? MainAccount => Accounts.Find(a => a.IsMain) ?? (Accounts.Count > 0 ? Accounts[0] : null);
    public List<AccountItemDto> HostAccounts => Accounts.FindAll(a => a.Type == AccountType.ServerHost);
    public List<AccountItemDto> SubAccounts => Accounts.FindAll(a => !a.IsMain && a.Type != AccountType.ServerHost);
    public bool HasHostAccounts => HostAccounts.Count > 0;
    public bool HasSubAccounts => SubAccounts.Count > 0;
}

public interface IUserManagementService
{
    Task<UserSummaryStats> GetSummaryStatsAsync(CancellationToken cancellationToken = default);

    Task<List<UserDetailDto>> SearchUsersAsync(string query, string roleFilter,
        CancellationToken cancellationToken = default);

    Task<UserDetailDto?> GetUserByIdAsync(int userId, CancellationToken cancellationToken = default);
    Task<bool> SetUserRoleAsync(int userId, string roleName, CancellationToken cancellationToken = default);
    Task<bool> SetAccountRoleAsync(int accountId, AccountType type, CancellationToken cancellationToken = default);

    Task<string?> ResetHostPasswordAsync(int accountId, CancellationToken cancellationToken = default);
    Task<string?> ResetHostAuthTokenAsync(int accountId, CancellationToken cancellationToken = default);

    Task<bool> UpdateUserEconomyAsync(int userId, int goldCoins, int silverCoins, int plinkoTickets,
        CancellationToken cancellationToken = default);

    Task<bool> AdjustUserBalanceAsync(int userId, int goldCoins, int silverCoins, int plinkoTickets,
        CancellationToken cancellationToken = default);

    Task<bool> PromoteUserToStaffAsync(int userId, CancellationToken cancellationToken = default);
    Task<bool> PromoteUserToHostAsync(int userId, CancellationToken cancellationToken = default);
    Task<bool> RevertUserToStandardAsync(int userId, CancellationToken cancellationToken = default);
}
