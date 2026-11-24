// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using ASPIRE.Common.DTOs;
using NEXUS.Contracts.Admin;
using NEXUS.Contracts.Fleet;

namespace NEXUS.Contracts.Clients;

public interface IUserApiClient
{
    Task<UserDto?> GetUserByIdAsync(int id, CancellationToken ct = default);
    Task<UserDto?> GetUserByEmailAsync(string email, CancellationToken ct = default);
    Task<UserDto?> GetUserByDiscordIdAsync(string discordId, CancellationToken ct = default);
    Task<UserDto?> GetUserByAccountNameAsync(string accountName, CancellationToken ct = default);
    Task<AccountDto?> GetAccountByNameAsync(string name, CancellationToken ct = default);
    Task<AccountDto?> GetAccountByIdAsync(int id, CancellationToken ct = default);
    Task<bool> ExistsAccountNameAsync(string name, CancellationToken ct = default);
    Task<bool> ExistsEmailAsync(string email, CancellationToken ct = default);
    Task<UserDto?> RegisterUserAsync(RegisterUserRequestDto payload, CancellationToken ct = default);
    Task<AuthenticationTokenResponseDto?> LogInUserAsync(LogInUserRequestDto payload, CancellationToken ct = default);
    Task<UserDto?> RegisterOrLoginDiscordAsync(DiscordAuthRequestDto payload, CancellationToken ct = default);

    Task<bool> SetInGamePasswordAsync(int userId, string salt, string srpHash, string? pbkdf2Hash = null,
        CancellationToken ct = default);

    Task<bool> LinkTwitchAsync(int userId, string twitchId, string twitchUsername, CancellationToken ct = default);
    Task<bool> UnlinkTwitchAsync(int userId, CancellationToken ct = default);
    Task<bool> ApplyHostAsync(ApplyHostRequestDto payload, CancellationToken ct = default);
    Task<bool> ApplyStreamerAsync(ApplyStreamerClientDto payload, CancellationToken ct = default);
    Task<StreamerApplicationDto?> GetStreamerApplicationAsync(int userId, CancellationToken ct = default);
    Task<HostApplicationDto?> GetHostApplicationAsync(int userId, CancellationToken ct = default);
    Task<IReadOnlyList<UserWebRewardDto>> GetUserWebRewardsAsync(int userId, CancellationToken ct = default);
    Task<bool> EquipWebRewardAsync(int userId, string rewardType, string rewardCode, CancellationToken ct = default);
    Task<HostTelemetryResultDto?> GetHostTelemetryAsync(string accountName, CancellationToken ct = default);
    Task<TokenDto?> ValidateTokenAsync(string tokenString, TokenPurpose purpose, CancellationToken ct = default);
    Task<TokenDto> IssueTokenAsync(int userId, TokenPurpose purpose, TimeSpan ttl, CancellationToken ct = default);
    Task<bool> InvalidateTokenAsync(string tokenString, CancellationToken ct = default);

    Task<GenerateInviteResultDto?> GenerateInviteCodeAsync(int userId, CancellationToken ct = default);
    Task<GenerateInviteResultDto?> GetUserInviteQuotaAsync(int userId, CancellationToken ct = default);
    Task<IReadOnlyList<UserInviteDto>> GetMyInvitesAsync(int userId, CancellationToken ct = default);
    Task<ValidateInviteResultDto> ValidateInviteCodeAsync(string code, CancellationToken ct = default);
    Task<bool> IsInviteOnlyModeEnabledAsync(CancellationToken ct = default);
}

public interface IAdminApiClient
{
    Task<UserSummaryStatsDto> GetSummaryStatsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<AdminUserDetailDto>> SearchUsersAsync(string? query, string? roleFilter, int limit = 100,
        CancellationToken ct = default);

    Task<AdminUserDetailDto?> GetUserByIdAsync(int id, CancellationToken ct = default);
    Task<AdminMetricsDTO> GetStaffMetricsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ApprovedHostDto>> GetApprovedHostsAsync(CancellationToken ct = default);
    Task<bool> SetUserRoleAsync(int userId, string roleName, CancellationToken ct = default);
    Task<bool> SetAccountRoleAsync(int accountId, int accountType, CancellationToken ct = default);
    Task<ResetCredentialResponse> ResetHostPasswordAsync(int accountId, CancellationToken ct = default);
    Task<ResetCredentialResponse> ResetHostAuthTokenAsync(int accountId, CancellationToken ct = default);
    Task<bool> UpdateUserEconomyAsync(int userId, UpdateUserEconomyRequest request, CancellationToken ct = default);
    Task<bool> AdjustUserBalanceAsync(int userId, AdjustUserBalanceRequest request, CancellationToken ct = default);
    Task<bool> PromoteUserToStaffAsync(int userId, CancellationToken ct = default);
    Task<bool> PromoteUserToHostAsync(int userId, CancellationToken ct = default);
    Task<bool> RevertUserToStandardAsync(int userId, CancellationToken ct = default);

    Task<IReadOnlyList<PlayerReportDto>> GetPlayerReportsAsync(string? status, string? reason, string? search,
        int? matchId, int? reportId, int page = 1, int pageSize = 25, CancellationToken ct = default);

    Task<int> GetPlayerReportsCountAsync(string? status, string? reason, string? search, int? matchId, int? reportId,
        CancellationToken ct = default);

    Task<PlayerReportDto?> GetPlayerReportByIdAsync(int id, CancellationToken ct = default);
    Task<bool> DismissReportAsync(int reportId, int staffAccountId, string? comment, CancellationToken ct = default);
    Task<bool> ActionReportAsync(ActionReportRequestDto request, CancellationToken ct = default);
    Task<bool> DirectPunishAccountAsync(DirectPunishRequestDto request, CancellationToken ct = default);

    Task<bool> BanAccountAsync(int accountId, DateTimeOffset? expiresAt, string reason, int? bannedBy = null,
        CancellationToken ct = default);

    Task<bool> UnbanAccountAsync(int accountId, CancellationToken ct = default);
    Task<IReadOnlyList<HostApplicationDto>> GetHostApplicationsAsync(CancellationToken ct = default);

    Task<bool> ApproveHostApplicationAsync(int applicationId, string? staffNotes = null, string? staffName = null,
        CancellationToken ct = default);

    Task<bool> RejectHostApplicationAsync(int applicationId, string? reason, CancellationToken ct = default);
    Task<bool> RevokeHostApplicationAsync(int applicationId, string? reason = null, CancellationToken ct = default);
    Task<IReadOnlyList<StreamerApplicationDto>> GetStreamerApplicationsAsync(CancellationToken ct = default);

    Task<bool> ApproveStreamerApplicationAsync(int applicationId, string? staffName = null, string? staffNotes = null,
        CancellationToken ct = default);

    Task<bool> RejectStreamerApplicationAsync(int applicationId, string? reason, CancellationToken ct = default);
    Task<bool> RevokeStreamerApplicationAsync(int applicationId, CancellationToken ct = default);

    Task<InviteSystemStatusDto?> GetInviteSystemStatusAsync(CancellationToken ct = default);
    Task<bool> ToggleInviteOnlyModeAsync(bool enable, string? notes, int staffAccountId, string staffAccountName, CancellationToken ct = default);
    Task<InviteTreeNodeDto?> GetInviteLineageTreeAsync(int rootUserId, CancellationToken ct = default);
    Task<bool> PruneInviteSubtreeAsync(int rootUserId, string reason, bool banAccounts = true, CancellationToken ct = default);

    Task<IReadOnlyList<AccountDto>> GetRecentAccountsAsync(int limit = 20, CancellationToken ct = default);
    Task<AccountDto?> GetAccountDetailAsync(string accountName, CancellationToken ct = default);
    Task<bool> AddInventoryItemAsync(string accountName, string productCode, CancellationToken ct = default);

    Task<bool> AddInventoryItemsAsync(string accountName, IEnumerable<string> productCodes,
        CancellationToken ct = default);

    Task<bool> RemoveInventoryItemAsync(string accountName, string productCode, CancellationToken ct = default);
    Task<bool> ResetInventoryAsync(string accountName, CancellationToken ct = default);
}

public interface IPlayerStatsApiClient
{
    Task<PlayerProfileSummaryDto?> GetPlayerProfileAsync(string ign, CancellationToken ct = default);
    Task<AccountStatisticsDto?> GetAccountStatisticsAsync(int accountId, CancellationToken ct = default);

    Task<IReadOnlyList<AccountStatisticsDto>>
        GetTopRankedAccountsAsync(int count = 100, CancellationToken ct = default);

    Task<LeaderboardResultDto> GetLeaderboardAsync(int page = 1, int pageSize = 50, string? search = null,
        string sort = "winrate", string? mode = null, string? map = null, CancellationToken ct = default);

    Task<IReadOnlyList<HostLeaderboardEntryDto>> GetHostLeaderboardAsync(CancellationToken ct = default);
    Task<IReadOnlyList<MatchStatisticsDto>> GetRecentMatchesAsync(int limit = 50, CancellationToken ct = default);

    Task<IReadOnlyList<MatchStatisticsDto>> GetMatchesForAccountAsync(int accountId, int limit = 20,
        CancellationToken ct = default);

    Task<MatchStatisticsDto?> GetMatchByIdAsync(int matchId, CancellationToken ct = default);

    Task<IReadOnlyList<PlayerStatisticsDto>> GetPlayerStatisticsForMatchAsync(int matchId,
        CancellationToken ct = default);

    Task<IReadOnlyList<PlayerStatisticsDto>> GetRecentPlayerStatisticsAsync(int accountId, int limit = 20,
        CancellationToken ct = default);

    Task<IReadOnlyList<int>> GetSubmittedMatchIdsAsync(IEnumerable<int> matchIds, CancellationToken ct = default);
    Task<MatchEvidenceDto?> GetMatchEvidenceAsync(int matchId, CancellationToken ct = default);
    Task<bool> SaveMatchEvidenceAsync(MatchEvidenceDto evidence, CancellationToken ct = default);
    Task<IReadOnlyList<ServerHostDto>> GetServerHostsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<HeroDetailDto>> GetAllHeroDetailsAsync(CancellationToken ct = default);
    Task<HeroDetailDto?> GetHeroDetailAsync(string heroId, CancellationToken ct = default);
    Task<IReadOnlyList<HeroItemDto>> GetAllHeroItemsAsync(CancellationToken ct = default);
    Task<bool> SubmitPlayerReportAsync(PlayerReportDto report, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetApprovedStreamerLoginsAsync(CancellationToken ct = default);
    Task<LiveTelemetryDataDto> GetLiveTelemetryAsync(CancellationToken ct = default);

    Task<IReadOnlyList<AccountDto>> SearchAccountsAsync(string? query, int page = 1, int pageSize = 50,
        CancellationToken ct = default);

    Task<AccountDto?> GetAccountByIgnAsync(string ign, CancellationToken ct = default);
    Task<int> GetTotalMatchesCountAsync(CancellationToken ct = default);

    Task<NEXUS.Contracts.DTOs.ClanDirectoryResultDto> GetClanDirectoryAsync(
        int page = 1,
        int pageSize = 24,
        string? search = null,
        string sort = "mmr",
        CancellationToken ct = default);

    Task<NEXUS.Contracts.DTOs.ClanDetailDto?> GetClanDetailAsync(int clanId, CancellationToken ct = default);
}

public interface IGuideApiClient
{
    Task<GuideSearchResultDto> GetGuidesAsync(GuideSearchQueryDto query, CancellationToken ct = default);
    Task<HeroGuideDto?> GetGuideByIdAsync(int id, CancellationToken ct = default);

    Task<SaveGuideResultDto> SaveGuideAsync(SaveHeroGuideRequestDto request, string authorAccountName, bool isStaff,
        CancellationToken ct = default);

    Task<bool> DeleteGuideAsync(int id, string accountName, CancellationToken ct = default);
    Task<VoteGuideResultDto> VoteGuideAsync(int id, bool isUpvote, CancellationToken ct = default);

    Task<TogglePrivacyResultDto> TogglePrivacyAsync(int id, string accountName, bool isStaff,
        CancellationToken ct = default);

    Task<ToggleOfficialResultDto> ToggleOfficialAsync(int id, CancellationToken ct = default);

    Task<ToggleDefaultGuideResultDto> ToggleDefaultGuideAsync(int id, string accountName,
        CancellationToken ct = default);

    Task<ToggleFavoriteGuideResultDto> ToggleFavoriteGuideAsync(int id, string accountName,
        CancellationToken ct = default);

    Task<IReadOnlyList<HeroDifficultyVoteDto>> GetHeroDifficultyVotesAsync(string heroIdentifier,
        CancellationToken ct = default);

    Task<HeroDifficultyVoteResultDto> VoteHeroDifficultyAsync(string heroIdentifier, string ign, int difficulty,
        CancellationToken ct = default);

    Task<IReadOnlyList<HeroGuideDto>> GetGuidesForHeroAsync(string heroIdentifier, CancellationToken ct = default);
}

public interface IStoreApiClient
{
    Task<StaffStoreViewModelDto> GetCatalogAsync(string? category, string? search, int page = 1, int pageSize = 12,
        IReadOnlyCollection<string>? userOwnedCodes = null, string? ownershipFilter = "all",
        IReadOnlyCollection<string>? userEquippedCodes = null, CancellationToken ct = default);

    Task<StaffStoreItemDto?> GetItemByIdAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<StaffStoreItemDto>> GetItemsByHeroAsync(string heroName, CancellationToken ct = default);

    Task<PurchaseItemResultDto> PurchaseItemAsync(int userId, int itemId, string currency,
        CancellationToken ct = default);

    Task<EquipItemResultDto> EquipItemAsync(string accountName, int itemId, string? code,
        CancellationToken ct = default);

    Task<bool> UpdateStoreItemAsync(UpdateStoreItemRequestDto request, CancellationToken ct = default);
    Task<IReadOnlyList<StaffStoreItemDto>> SearchStoreAsync(string query, CancellationToken ct = default);
    Task<PlinkoStatusResponseDto> GetPlinkoStatusAsync(int userId, CancellationToken ct = default);

    Task<PlinkoDropResultDto> PlinkoDropAsync(int userId, string currency, bool useDaily,
        CancellationToken ct = default);

    Task<RedeemCodeResultDto> RedeemCodeAsync(int userId, string code, CancellationToken ct = default);
    Task<UserBalancesDto?> GetUserBalancesAsync(int userId, CancellationToken ct = default);
    Task<IReadOnlyList<StoreItemDto>> GetStoreCatalogEntitiesAsync(CancellationToken ct = default);
}

public interface ILoreApiClient
{
    Task<IReadOnlyList<LoreArcDto>> GetArcsAsync(bool includeUnpublished = false, CancellationToken ct = default);
    Task<LoreArcDto?> GetArcBySlugAsync(string slug, bool includeUnpublished = false, CancellationToken ct = default);

    Task<IReadOnlyList<LoreComicSummaryDto>> GetComicsAsync(int? arcId = null, bool includeUnpublished = false,
        CancellationToken ct = default);

    Task<LoreComicDetailDto?> GetComicBySlugAsync(string slug, int? currentAccountId = null, bool isStaff = false,
        CancellationToken ct = default);

    Task<LoreComicDetailDto?> GetComicByIdAsync(int id, int? currentAccountId = null, bool isStaff = false,
        CancellationToken ct = default);

    Task<bool> RateComicAsync(int comicId, int accountId, int score, CancellationToken ct = default);
    Task<int?> GetUserRatingAsync(int comicId, int accountId, CancellationToken ct = default);
    Task<LoreRatingMetricDto?> GetStaffRatingMetricsAsync(int comicId, CancellationToken ct = default);

    Task<LoreComicSummaryDto?> SaveComicAsync(SaveComicRequestDto request, bool isStaff,
        CancellationToken ct = default);

    Task<bool> DeleteComicAsync(int id, bool isStaff, CancellationToken ct = default);
    Task<LoreArcDto?> SaveArcAsync(SaveArcRequestDto request, bool isStaff, CancellationToken ct = default);
    Task<bool> DeleteArcAsync(int id, bool isStaff, CancellationToken ct = default);
}
