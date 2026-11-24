// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using NEXUS.Contracts.Constants;

namespace NEXUS.Contracts.Clients;

public record RegisterUserRequestDto(
    string EmailAddress,
    string PlainTextPassword,
    string InGameNickname,
    string? DiscordId = null,
    string? DiscordUsername = null,
    string? AvatarHash = null
);

public record LogInUserRequestDto(
    string Login,
    string Password
);

public record AuthenticationTokenResponseDto(
    string Token,
    DateTimeOffset ExpiresAt
);

public record ApplyStreamerClientDto(
    int UserId,
    string TwitchChannelUrl,
    string? StreamSchedule = null,
    int AverageViewers = 0,
    string? TwitchUsername = null,
    string? TwitchId = null,
    string? ApplicationNotes = null
)
{
    public ApplyStreamerClientDto() : this(0, string.Empty)
    {
    }
}

public class StaffStoreItemDto
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string TypeName { get; set; } = string.Empty;
    public int Type { get; set; }
    public int GoldCost { get; set; }
    public int SilverCost { get; set; }
    public bool IsEnabled { get; set; }
    public bool Purchasable { get; set; }
    public string? Resource { get; set; }
    public string? HeroName { get; set; }
    public string? AltAvatarSuffix { get; set; }
    public bool IsOwnedByUser { get; set; }
    public bool IsEquippedByUser { get; set; }
}

public class SaveHeroGuideRequestDto
{
    public int? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string HeroIdentifier { get; set; } = string.Empty;
    public string? HeroName { get; set; }
    public string? TargetMap { get; set; } = "caldavar";
    public string? Intro { get; set; }
    public string? Content { get; set; }
    public string? StartingItems { get; set; }
    public string? EarlyGameItems { get; set; }
    public string? CoreItems { get; set; }
    public string? LuxuryItems { get; set; }
    public string? AbilityQueue { get; set; }
    public bool IsPublic { get; set; } = true;
}

public record PlayerStatsAggregateDto(
    int MatchesPlayed,
    int HeroKills,
    int Deaths,
    int HeroAssists,
    int CreepKills,
    int NeutralKills,
    int Wards,
    int Experience,
    int Gold,
    int HeroDamage,
    int BuildingDamage,
    int ConcedingVotes,
    int AccountId = 0
);

public record PlayerHeroCountDto(
    string HeroIdentifier,
    int MatchesPlayed,
    int HeroKills,
    int Deaths,
    int HeroAssists,
    int Wins
);

public record DiscordAuthRequestDto(
    string DiscordId,
    string? DiscordUsername,
    string? Email,
    string? DiscordAvatar,
    string? InGameName = null
);

public class UserRoleDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class AccountDto
{
    public int ID { get; set; }
    public string Name { get; set; } = string.Empty;
    public int? UserID { get; set; }
    public int? OwnerAccountID { get; set; }
    public AccountType Type { get; set; } = AccountType.Normal;
    public bool IsMain { get; set; }
    public bool IsStaff => Type is AccountType.Staff or AccountType.GameMaster or AccountType.MatchModerator;
    public int? ClanId { get; set; }
    public string? ClanName { get; set; }
    public string? ClanTag { get; set; }
    public ClanTier ClanTier { get; set; } = ClanTier.None;
    public int AscensionLevel { get; set; }
    public int GoldCoins { get; set; }
    public int SilverCoins { get; set; }
    public DateTimeOffset TimestampCreated { get; set; }
    public DateTimeOffset TimestampLastActive { get; set; }
    public List<string> SelectedStoreItems { get; set; } = [];
    public List<int> DefaultGuideIDs { get; set; } = [];
    public List<int> FavoriteGuideIDs { get; set; } = [];
    public bool UseCloud { get; set; }
    public bool AutomaticCloudUpload { get; set; }
    public DateTimeOffset? BackupLastUpdatedTime { get; set; }
    public DateTimeOffset? MuteExpiration { get; set; }
    public string? MuteReason { get; set; }
    public DateTimeOffset? SuspensionExpiration { get; set; }
    public string? SuspensionReason { get; set; }
    public DateTimeOffset? BanExpiration { get; set; }
    public string? BanReason { get; set; }
    public bool IsMuted => MuteExpiration.HasValue && MuteExpiration.Value > TimeProvider.System.GetUtcNow();

    public bool IsSuspended =>
        SuspensionExpiration.HasValue && SuspensionExpiration.Value > TimeProvider.System.GetUtcNow();

    public bool IsBanned => (BanExpiration.HasValue && BanExpiration.Value > TimeProvider.System.GetUtcNow()) ||
                            Type == AccountType.Disabled;

    public UserDto? User { get; set; }
}

public class UserDto
{
    public int ID { get; set; }
    public string EmailAddress { get; set; } = string.Empty;
    public string? DiscordId { get; set; }
    public string? DiscordUsername { get; set; }
    public string? DiscordAvatar { get; set; }
    public string? TwitchUsername { get; set; }
    public string? TwitchId { get; set; }
    public UserRoleDto? Role { get; set; }
    public string RoleName => Role?.Name ?? "User";
    public List<AccountDto> Accounts { get; set; } = [];
    public DateTimeOffset TimestampCreated { get; set; }
    public DateTimeOffset TimestampLastActive { get; set; }
    public int GoldCoins { get; set; }
    public int SilverCoins { get; set; }
    public int PlinkoTickets { get; set; }
    public DateTimeOffset? LastPlinkoDailyClaimUtc { get; set; }
    public int TotalLevel { get; set; }
    public int TotalExperience { get; set; }
    public List<string> OwnedStoreItems { get; set; } = [];
    public bool HasInGamePasswordSet { get; set; }

    public bool IsAdministrator =>
        Role?.Name.Equals(UserRoles.Administrator, StringComparison.OrdinalIgnoreCase) == true;

    public bool IsStaff => IsAdministrator || Accounts.Any(a => a.IsStaff);
}

public class StreamerApplicationDto
{
    public int ID { get; set; }
    public int UserID { get; set; }
    public int MainAccountID { get; set; }
    public string? MainAccountName => MainAccount?.Name;
    public AccountDto? MainAccount { get; set; }
    public UserDto? User { get; set; }
    public string TwitchChannelUrl { get; set; } = string.Empty;
    public string? StreamSchedule { get; set; }
    public int AverageViewers { get; set; }
    public string? TwitchUsername { get; set; }
    public string? TwitchId { get; set; }
    public string? ApplicationNotes { get; set; }
    public string? StaffNotes { get; set; }
    public string? ReviewedByStaffName { get; set; }
    public StreamerApplicationStatus Status { get; set; }
    public DateTimeOffset TimestampApplied { get; set; }
    public DateTimeOffset? TimestampReviewed { get; set; }
    public string? RejectionReason { get; set; }
}

public class HostApplicationDto
{
    public int ID { get; set; }
    public int UserID { get; set; }
    public int MainAccountID { get; set; }
    public string? MainAccountName => MainAccount?.Name;
    public AccountDto? MainAccount { get; set; }
    public UserDto? User { get; set; }
    public int? HostAccountID { get; set; }
    public string HostUsername { get; set; } = string.Empty;
    public string? EncryptedHostPassword { get; set; }
    public string? EncryptedAuthToken { get; set; }
    public string ServerNamePrefix { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public int Instances { get; set; }
    public HostApplicationStatus Status { get; set; }
    public string ServerLocation { get; set; } = string.Empty;
    public int MaxPlayerCapacity { get; set; }
    public int DedicatedBandwidthMbps { get; set; }
    public string HardwareSpecs { get; set; } = string.Empty;
    public string? ApplicationNotes { get; set; }
    public string? StaffNotes { get; set; }
    public string? ReviewedByStaffName { get; set; }
    public DateTimeOffset TimestampApplied { get; set; }
    public DateTimeOffset? TimestampReviewed { get; set; }
    public string? RejectionReason { get; set; }
}

public class UserWebRewardDto
{
    public int ID { get; set; }
    public int UserID { get; set; }
    public string RewardType { get; set; } = string.Empty;
    public string RewardCode { get; set; } = string.Empty;
    public string RewardName { get; set; } = string.Empty;
    public bool IsEquipped { get; set; }
    public DateTimeOffset TimestampClaimed { get; set; }
}

public class TokenDto
{
    public int ID { get; set; }
    public int UserID { get; set; }
    public string EmailAddress { get; set; } = string.Empty;
    public Guid Value { get; set; }
    public TokenPurpose Purpose { get; set; }
    public string? Data { get; set; }
    public DateTimeOffset TimestampIssued { get; set; }
    public DateTimeOffset TimestampExpires { get; set; }
    public DateTimeOffset? TimestampConsumed { get; set; }
    public bool IsConsumed => TimestampConsumed.HasValue;
    public bool IsExpired => TimestampExpires <= TimeProvider.System.GetUtcNow();
    public bool IsValid => !IsConsumed && !IsExpired;
}

public record HostTelemetryResultDto(
    bool HasLinkedHostAccounts,
    bool HasPendingHostApplication,
    List<string> LinkedHostAccountNames,
    List<int> LinkedPlayerAccountIds,
    List<string> LinkedPlayerAccountNames,
    int GamesHosted,
    int TotalPlayedAcrossLinkedAccounts,
    double HostingRatio,
    double CommunityContributionPercentage,
    string? HostBadgeTier,
    HostApplicationDto? PrimaryHostApplication,
    string HostUsername
);

public record ActionReportRequestDto(
    int ReportId,
    int StaffAccountId,
    string ActionType,
    string? Reason,
    int? DurationHours = null,
    string? StaffNotes = null
);

public record DirectPunishRequestDto(
    string TargetAccountName,
    int StaffAccountId,
    string PunishmentType,
    string Reason,
    int? DurationHours = null,
    string? Notes = null
);

public record StaffStoreViewModelDto(
    IReadOnlyList<StaffStoreItemDto> Items,
    int TotalCount,
    int CurrentPage,
    int TotalPages,
    int PageSize,
    string? CurrentCategory,
    string? SearchQuery,
    string? OwnershipFilter
);

public record PurchaseItemResultDto(
    bool Success,
    string? Message,
    int NewGold,
    int NewSilver,
    int ItemId,
    string? Code,
    string? Name
);

public record EquipItemResultDto(
    bool Success,
    string? Message,
    string? EquippedCode,
    List<string> SelectedItems
);

public record UpdateStoreItemRequestDto(
    int Id,
    int GoldCost,
    int SilverCost,
    bool IsEnabled,
    bool Purchasable
);

public record PlinkoStatusResponseDto(
    bool IsAuthenticated,
    bool CanClaimDaily,
    double DailyCooldownRemainingSeconds,
    int PlinkoTickets,
    int GoldCoins,
    int SilverCoins,
    int TicketCost,
    int GoldCost,
    List<string> UnlockedBorders,
    List<string> UnlockedTitles,
    string? EquippedBorder,
    string? EquippedTitle
);

public record PlinkoDropResultDto(
    bool Success,
    string? Message,
    int DropSlot,
    string? RewardType,
    string? RewardCode,
    string? RewardName,
    int RewardAmount,
    int NewTickets,
    int NewGold,
    int NewSilver,
    bool IsJackpot
);

public record RedeemCodeResultDto(
    bool Success,
    string? Message,
    int GoldGranted,
    int SilverGranted,
    List<string> ProductsGranted
);

public record UserBalancesDto(
    int UserId,
    int GoldCoins,
    int SilverCoins,
    int PlinkoTickets,
    List<string> OwnedStoreItems,
    List<string> SelectedStoreItems
);

public record GuideSearchQueryDto(
    string? Hero = null,
    string? Map = null,
    string? Query = null,
    string? Sort = "featured",
    int Page = 1,
    int PageSize = 18,
    string? CurrentAccountName = null,
    bool IsStaff = false
);

public class HeroGuideDto
{
    public int ID { get; set; }
    public string Name { get; set; } = string.Empty;
    public string HeroIdentifier { get; set; } = string.Empty;
    public string? HeroName { get; set; }
    public string? TargetMap { get; set; }
    public string? Intro { get; set; }
    public string? Content { get; set; }
    public string? StartingItems { get; set; }
    public string? EarlyGameItems { get; set; }
    public string? CoreItems { get; set; }
    public string? LuxuryItems { get; set; }
    public string? AbilityQueue { get; set; }
    public bool Public { get; set; }
    public bool Official { get; set; }

    public bool Featured
    {
        get => Official;
        set => Official = value;
    }

    public float Rating { get; set; }
    public int Upvotes { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public int UpVotes
    {
        get => Upvotes;
        set => Upvotes = value;
    }

    public int Downvotes { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public int DownVotes
    {
        get => Downvotes;
        set => Downvotes = value;
    }

    public int AuthorAccountID { get; set; }
    public string AuthorAccountName { get; set; } = string.Empty;
    public AccountDto? Author { get; set; }
    public DateTimeOffset TimestampCreated { get; set; }
    public DateTimeOffset TimestampUpdated { get; set; }
}

public class HeroDifficultyVoteDto
{
    public int ID { get; set; }
    public int AccountID { get; set; }
    public string HeroIdentifier { get; set; } = string.Empty;
    public string InGameNickname { get; set; } = string.Empty;
    public int Difficulty { get; set; }
    public int Rating => Difficulty;
    public DateTimeOffset TimestampCreated { get; set; }
}

public record GuideSearchResultDto(
    IReadOnlyList<HeroGuideDto> Guides,
    int TotalCount,
    int TotalPages,
    int CurrentPage
);

public record SaveGuideResultDto(
    bool Success,
    int GuideId,
    string? Message
);

public record VoteGuideResultDto(
    bool Success,
    int UpVotes,
    int DownVotes,
    float Rating,
    string? Message
);

public record TogglePrivacyResultDto(
    bool Success,
    bool IsPublic,
    string? Message
);

public record ToggleOfficialResultDto(
    bool Success,
    bool IsFeatured,
    string? Message
);

public record ToggleDefaultGuideResultDto(
    bool Success,
    bool IsDefault,
    int GuideId,
    string? HeroName,
    string? Message
);

public record ToggleFavoriteGuideResultDto(
    bool Success,
    bool IsFavorite,
    int GuideId,
    string? Message
);

public record HeroDifficultyVoteResultDto(
    bool Success,
    double AverageDifficulty,
    int TotalVotes,
    string? Message
);

public class AccountStatisticsDto
{
    public int ID { get; set; }
    public int AccountID { get; set; }
    public AccountStatisticsType Type { get; set; }
    public int MatchesPlayed { get; set; }
    public int MatchesWon { get; set; }
    public int MatchesLost { get; set; }
    public int MatchesConceded { get; set; }
    public int Disconnects { get; set; }
    public double SkillRating { get; set; }
    public int Experience { get; set; }
    public int Gold { get; set; }
    public int HeroKills { get; set; }
    public int Deaths { get; set; }
    public int HeroAssists { get; set; }
    public int CreepKills { get; set; }
    public int NeutralKills { get; set; }
    public int Wards { get; set; }
    public int HeroDamage { get; set; }
    public int BuildingDamage { get; set; }
    public int TimePlayedSeconds { get; set; }
}

public class MatchStatisticsDto
{
    public int ID { get; set; }
    public int MatchID { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Map { get; set; } = string.Empty;
    public string Mode { get; set; } = string.Empty;
    public string GameMode => Mode;
    public int WinningTeam { get; set; }
    public int ScoreTeam1 { get; set; }
    public int ScoreTeam2 { get; set; }
    public int TimePlayedSeconds { get; set; }
    public int TimePlayed => TimePlayedSeconds;
    public string HostAccountName { get; set; } = string.Empty;
    public string ServerLocation { get; set; } = string.Empty;
    public DateTimeOffset TimestampRecorded { get; set; }
    public string? ReplayUrl { get; set; }
}

public class PlayerReportDto
{
    public int ID { get; set; }
    public int? MatchID { get; set; }
    public int? ReporterAccountID { get; set; }
    public string ReporterAccountName { get; set; } = string.Empty;
    public int? ReportedAccountID { get; set; }
    public string ReportedAccountName { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Status { get; set; } = "PENDING";
    public DateTimeOffset TimestampCreated { get; set; }
    public DateTimeOffset? TimestampReviewed { get; set; }
    public string? ReviewedByStaffName { get; set; }
    public string? PunishmentType { get; set; }
    public int? PunishmentDurationHours { get; set; }
    public DateTimeOffset? PunishmentExpiration { get; set; }
    public string? StaffNotes { get; set; }
}

public class StoreItemDto
{
    public int ID { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string TypeName { get; set; } = string.Empty;
    public int Type { get; set; }
    public int GoldCost { get; set; }
    public int SilverCost { get; set; }
    public bool IsEnabled { get; set; }
    public bool Purchasable { get; set; }
    public string? Resource { get; set; }
    public string? HeroName { get; set; }
    public string? AltAvatarSuffix { get; set; }
}

public class HeroDetailDto
{
    public int ID { get; set; }
    public string HeroIdentifier { get; set; } = string.Empty;
    public string HeroCode => HeroIdentifier;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string PrimaryAttribute { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string AttackType { get; set; } = "Melee";
    public double BaseStrength { get; set; }
    public double StrengthGrowth { get; set; } = 2.0;
    public double BaseAgility { get; set; }
    public double AgilityGrowth { get; set; } = 2.0;
    public double BaseIntelligence { get; set; }
    public double IntelligenceGrowth { get; set; } = 2.0;
    public double Armor { get; set; } = 3.0;
    public string ArmorMitigation { get; set; } = "15.0%";
    public int MinDamage { get; set; } = 50;
    public int MaxDamage { get; set; } = 55;
    public string AttackDamage => $"{MinDamage} - {MaxDamage}";
    public int MovementSpeed { get; set; } = 300;
    public int AttackRange { get; set; } = 128;
    public List<string> Roles { get; set; } = [];
    public string Faction { get; set; } = "Legion";
    public string? IconPath { get; set; }
    public string? SplashImagePath { get; set; }
    public string? Lore { get; set; }
    public string? AbilitiesJson { get; set; }
}

public class HeroItemDto
{
    public int ID { get; set; }
    public string ItemIdentifier { get; set; } = string.Empty;
    public string Code => ItemIdentifier;
    public string DisplayName { get; set; } = string.Empty;
    public string Name => DisplayName;
    public string Description { get; set; } = string.Empty;
    public int Cost { get; set; }
    public string Category { get; set; } = string.Empty;
    public int RecipeCost { get; set; }
    public string IconPath { get; set; } = string.Empty;
    public string Flavor { get; set; } = string.Empty;
    public string Upgrade { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public string ComponentCodesJson { get; set; } = "[]";
    public List<object> Components { get; set; } = [];
    public List<object> BuildsInto { get; set; } = [];
    public string SearchTerms { get; set; } = string.Empty;
}

public class MatchEvidenceDto
{
    public int ID { get; set; }
    public int MatchID { get; set; }
    public bool HasReplay { get; set; }
    public bool HasVoice { get; set; }
    public string? ChatLogJson { get; set; }
    public string? VoiceTrackLegionUrl { get; set; }
    public string? VoiceTrackHellbourneUrl { get; set; }
    public DateTimeOffset? TimestampExtracted { get; set; }
    public string EvidenceJson { get; set; } = "{}";
    public DateTimeOffset TimestampCreated { get; set; }
}

public class PlayerStatisticsDto
{
    public int ID { get; set; }
    public int AccountID { get; set; }
    public string AccountName { get; set; } = string.Empty;
    public int MatchID { get; set; }
    public string HeroIdentifier { get; set; } = string.Empty;
    public int Team { get; set; }
    public int Position { get; set; }
    public int LobbyPosition => Position;
    public int HeroLevel { get; set; } = 1;
    public int HeroKills { get; set; }
    public int Deaths { get; set; }
    public int HeroDeaths => Deaths;
    public int HeroAssists { get; set; }
    public int CreepKills { get; set; }
    public int TeamCreepKills => CreepKills;
    public int NeutralKills { get; set; }
    public int NeutralCreepKills => NeutralKills;
    public int GoldEarned { get; set; }
    public int GoldSpent { get; set; }
    public int Gold => GoldEarned;
    public int ExperienceEarned { get; set; }
    public int Experience => ExperienceEarned;
    public int HeroDamage { get; set; }
    public int BuildingDamage { get; set; }
    public int WardsPlaced { get; set; }
    public bool Won { get; set; }
    public int Win => Won ? 1 : 0;
    public List<string> Inventory { get; set; } = [];
}

public class MasteryDto
{
    public int ID { get; set; }
    public int AccountID { get; set; }
    public int TotalMasteryPoints { get; set; }
    public int Level { get; set; }
}

public record PlayerProfileSummaryDto(
    AccountDto Account,
    UserDto User,
    AccountStatisticsDto? RankedStats,
    AccountStatisticsDto? PublicStats,
    PlayerStatsAggregateDto? AggregatedStats,
    List<PlayerHeroCountDto> TopHeroes,
    List<MatchStatisticsDto> RecentMatches,
    List<PlayerReportDto> RecentReports,
    StreamerApplicationDto? ApprovedStreamerApp,
    HostApplicationDto? ApprovedHostApp,
    HostTelemetryResultDto HostTelemetry,
    List<UserWebRewardDto> EquippedRewards
);

public record LeaderboardResultDto(
    IReadOnlyList<LeaderboardEntryDto> Entries,
    int TotalCount,
    int CurrentPage,
    int TotalPages
);

public record LeaderboardEntryDto(
    int Rank,
    int AccountId,
    string AccountName,
    int Rating,
    int Wins,
    int Losses,
    double WinRate,
    int Kills,
    int Deaths,
    int Assists,
    double Kdr,
    string? FavoriteHero
);

public record ServerHostDto(
    int UserId,
    string HostAccountName,
    string? Location,
    int TotalMatchesHosted,
    DateTimeOffset TimestampCreated,
    string? BadgeTier,
    bool IsOnline
);

public record LiveTelemetryDataDto(
    int OnlinePlayers,
    int MatchesInProgress,
    int ServerStatus,
    List<string> ActiveRegions,
    DateTimeOffset Timestamp
);

public class LoreArcDto
{
    public int ID { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? BannerImageUrl { get; set; }
    public int OrderIndex { get; set; }
    public bool IsPublished { get; set; }
    public int ComicCount { get; set; }
    public List<LoreComicSummaryDto> Comics { get; set; } = [];
}

public class LoreComicSummaryDto
{
    public int ID { get; set; }
    public int ArcID { get; set; }
    public string ArcTitle { get; set; } = string.Empty;
    public string ArcSlug { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public int IssueNumber { get; set; }
    public string Synopsis { get; set; } = string.Empty;
    public string CoverImageUrl { get; set; } = string.Empty;
    public string? CinematicVideoUrl { get; set; }
    public bool IsPublished { get; set; }
    public int ViewCount { get; set; }
    public int PageCount { get; set; }
    public DateTimeOffset TimestampCreated { get; set; }
    public DateTimeOffset? TimestampPublished { get; set; }
    public List<LoreCreditDto> Credits { get; set; } = [];
}

public class LoreComicDetailDto : LoreComicSummaryDto
{
    public List<LorePageDto> Pages { get; set; } = [];
    public int? UserScore { get; set; }
    public LoreRatingMetricDto? StaffRatingMetrics { get; set; }
}

public class LorePageDto
{
    public int ID { get; set; }
    public int ComicID { get; set; }
    public int PageNumber { get; set; }
    public string ImageUrl { get; set; } = string.Empty;
    public string? PanelHotspotsJson { get; set; }
    public string? DialogueTranscript { get; set; }
}

public class LoreCreditDto
{
    public int ID { get; set; }
    public int ComicID { get; set; }
    public int? AccountID { get; set; }
    public string? AccountName { get; set; }
    public string Role { get; set; } = string.Empty;
    public string? CustomNameOverride { get; set; }

    public string DisplayName => !string.IsNullOrWhiteSpace(CustomNameOverride)
        ? CustomNameOverride
        : AccountName ?? "Unknown Creator";

    public int OrderIndex { get; set; }
}

public class LoreRatingMetricDto
{
    public int ComicID { get; set; }
    public double AverageScore { get; set; }
    public int TotalRatings { get; set; }
    public Dictionary<int, int> ScoreDistribution { get; set; } = [];
}

public record SaveComicRequestDto(
    int? ID,
    int ArcID,
    string Title,
    string Slug,
    int IssueNumber,
    string Synopsis,
    string CoverImageUrl,
    string? CinematicVideoUrl,
    bool IsPublished,
    List<SavePageRequestDto> Pages,
    List<SaveCreditRequestDto> Credits
);

public record SavePageRequestDto(
    int? ID,
    int PageNumber,
    string ImageUrl,
    string? PanelHotspotsJson,
    string? DialogueTranscript
);

public record SaveCreditRequestDto(
    int? ID,
    int? AccountID,
    string? AccountName,
    string Role,
    string? CustomNameOverride,
    int OrderIndex
);

public record SaveArcRequestDto(
    int? ID,
    string Title,
    string Slug,
    string Description,
    string? BannerImageUrl,
    int OrderIndex,
    bool IsPublished
);

public record RateComicRequestDto(
    int ComicID,
    int AccountID,
    int Score
);
