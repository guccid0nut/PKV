// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MERRICK.DatabaseContext.Entities.Core;
using MERRICK.DatabaseContext.Enumerations;
using NEXUS.Contracts.Clients;

namespace NEXUS.Contracts.Data;

public interface IAccountRepository
{
    Task<Account?> GetByIdAsync(int accountId, CancellationToken ct = default);
    Task<Account?> GetByNicknameAsync(string nickname, CancellationToken ct = default);
    Task<int?> GetIdByNicknameAsync(string nickname, CancellationToken ct = default);
    Task<Account?> GetByUserIdAsync(int userId, CancellationToken ct = default);
    Task<bool> ExistsByNicknameAsync(string nickname, CancellationToken ct = default);
    Task<bool> ExistsByEmailAsync(string email, CancellationToken ct = default);
    Task<Account> CreateAsync(Account account, CancellationToken ct = default);
    Task UpdateAsync(Account account, CancellationToken ct = default);

    Task BanAccountAsync(int accountId, DateTimeOffset? expiresAt, string reason, int? bannedBy = null,
        CancellationToken ct = default);

    Task UnbanAccountAsync(int accountId, CancellationToken ct = default);

    Task<User?> GetUserByIdAsync(int userId, CancellationToken ct = default);
    Task<User?> GetUserByEmailAsync(string email, CancellationToken ct = default);
    Task<User?> GetUserByDiscordIdAsync(string discordId, CancellationToken ct = default);
    Task<User?> GetUserByAccountNameAsync(string accountName, CancellationToken ct = default);
    Task<User> RegisterOrLoginDiscordUserAsync(DiscordAuthRequestDto payload, CancellationToken ct = default);

    Task<bool> SetInGamePasswordAsync(int userId, string salt, string srpHash, string? pbkdf2Hash = null,
        string? argon2idHash = null, CancellationToken ct = default);

    Task UpgradeUserArgon2idHashAsync(int userId, string argon2idHash, CancellationToken ct = default);

    Task<bool> LinkTwitchAsync(int userId, string twitchId, string twitchUsername, CancellationToken ct = default);
    Task<bool> UnlinkTwitchAsync(int userId, CancellationToken ct = default);

    Task<User> CreateUserAsync(User user, CancellationToken ct = default);
    Task UpdateUserAsync(User user, CancellationToken ct = default);
    Task<Role?> GetRoleByNameAsync(string roleName, CancellationToken ct = default);

    Task<IReadOnlyList<Account>> SearchAccountsAsync(string? query, int page = 1, int pageSize = 50,
        CancellationToken ct = default);

    Task<IReadOnlyList<Account>> GetAccountsByIdsAsync(IEnumerable<int> accountIds, CancellationToken ct = default);
    Task<IReadOnlyList<Account>> GetAccountsByNamesAsync(IEnumerable<string> names, CancellationToken ct = default);
    Task<IReadOnlyList<Account>> GetAccountsForUserAsync(int userId, CancellationToken ct = default);
    Task<int> GetTotalAccountsCountAsync(CancellationToken ct = default);
    Task<int> GetTotalUsersCountAsync(CancellationToken ct = default);
    Task<int> GetTotalStaffCountAsync(CancellationToken ct = default);
    Task<int> GetTotalHostsCountAsync(CancellationToken ct = default);

    Task<IReadOnlyList<User>> SearchUsersAsync(string? query, string? roleFilter, int limit = 100,
        CancellationToken ct = default);

    Task<HostApplication?> CreateHostApplicationAsync(string serverNamePrefix, string location, int instances,
        string? applicationNotes, CancellationToken ct = default);

    Task<StreamerApplication?> CreateOrUpdateStreamerApplicationAsync(int userId, string twitchUsername,
        string? twitchId, string? twitchChannelUrl, string? applicationNotes, CancellationToken ct = default);

    Task<StreamerApplication?> GetStreamerApplicationByUserIdAsync(int userId, CancellationToken ct = default);
    Task<HostApplication?> GetHostApplicationByUserIdAsync(int userId, CancellationToken ct = default);
    Task<IReadOnlyList<HostApplication>> GetApprovedHostApplicationsAsync(CancellationToken ct = default);
    Task<HostApplication?> CreateHostApplicationForAccountAsync(string applicantAccountName, string? serverNamePrefix,
        string location, int instances, string? applicationNotes, CancellationToken ct = default) =>
        CreateHostApplicationAsync(applicantAccountName, location, instances, applicationNotes, ct);
    Task<IReadOnlyList<HostApplication>> GetAllHostApplicationsAsync(CancellationToken ct = default);
    Task<HostApplication?> GetHostApplicationByIdOrAccountIdAsync(int id, CancellationToken ct = default);
    Task<HostTelemetryResultDto?> GetHostTelemetryAsync(string accountName, CancellationToken ct = default);

    Task<Account?> GetAuthIdentityByNicknameAsync(string nickname, CancellationToken ct = default) =>
        GetForPreAuthAsync(nickname, ct);
    Task<Account?> GetForPreAuthAsync(string accountName, CancellationToken ct = default);
    Task<Account?> GetForAuthenticationAsync(string accountName, CancellationToken ct = default);
    Task<Account?> GetForSwitchAuthAsync(int targetAccountId, string? targetAccountName, CancellationToken ct = default);
    Task<int?> GetAccountIdByNicknameAsync(string nickname, CancellationToken ct = default);
    Task RecordAccountTelemetryAndSessionAsync(int accountId, AccountSession session, string? ipAddress, string? macAddress, string? sysInfo, string? sysInfoHash, CancellationToken ct = default);
    Task UpdateLastActiveAsync(int accountId, CancellationToken ct = default);
    Task<Account?> CreateSubAccountAsync(int mainAccountId, string subAccountNickname, CancellationToken ct = default);
    Task<bool> ChangeNicknameAsync(int accountId, string newNickname, CancellationToken ct = default);
    Task<bool> UpdateCloudStoragePreferencesAsync(int accountId, bool? useCloud, bool? autoUpload, DateTimeOffset? backupTime = null, CancellationToken ct = default);
    Task<bool> UpdateCloudStoragePreferencesByNameAsync(string accountName, bool? useCloud, bool? autoUpload, DateTimeOffset? backupTime = null, CancellationToken ct = default);
    Task SavePlayerReportAsync(PlayerReport report, CancellationToken ct = default);
    Task SavePlayerReportsAsync(IEnumerable<PlayerReport> reports, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetApprovedStreamerTwitchUsernamesAsync(int limit = 4, CancellationToken ct = default);
    Task DissociateFriendshipsAsync(int accountId, CancellationToken ct = default);
    Task<AccountPeerResult> AddPeerAsync(int accountId, int peerAccountId, MERRICK.DatabaseContext.Enumerations.PeerType type, string reason = "", DateTimeOffset? expires = null, CancellationToken ct = default);
    Task<bool> RemovePeerAsync(int accountId, int peerAccountId, MERRICK.DatabaseContext.Enumerations.PeerType type, CancellationToken ct = default);
    Task<IReadOnlyList<AccountPeer>> GetPeersAsync(int accountId, MERRICK.DatabaseContext.Enumerations.PeerType? type = null, CancellationToken ct = default);

    Task<int> RemoveAllNotificationsAsync(int accountId, CancellationToken ct = default) => Task.FromResult(0);
    Task<bool> DeleteNotificationAsync(int accountId, int notificationId, CancellationToken ct = default) => Task.FromResult(false);
    Task<int> CreateNotificationAsync(int accountId, string content, CancellationToken ct = default) => Task.FromResult(0);

    Task<Account?> FindBaseServerHostByPrefixAsync(
        string baseNamePrefix,
        CancellationToken ct = default) => Task.FromResult<Account?>(null);

    Task<Account?> ProvisionDerivedArcadeHostAccountAsync(
        string arcadeAccountName,
        Account verifiedBaseHost,
        CancellationToken ct = default) => Task.FromResult<Account?>(null);
}

public record AccountPeerResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public AccountPeer? Peer { get; init; }

    public static AccountPeerResult Succeeded(AccountPeer peer) => new()
    {
        Success = true,
        Peer = peer
    };

    public static AccountPeerResult Failed(string errorMessage) => new()
    {
        Success = false,
        ErrorMessage = errorMessage
    };
}





