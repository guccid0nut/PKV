// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Text.Json;
using static ASPIRE.Tests.Infrastructure.TestDtoMapper;
// ReSharper disable All
using ASPIRE.Common.DTOs;
using ASPIRE.Common.Security;
using DAWNBRINGER.WebPortal.UI.Services;
using MERRICK.DatabaseContext.Entities.Game;
using MERRICK.DatabaseContext.Entities.Statistics;
using MERRICK.DatabaseContext.Entities.Store;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NEXUS.Contracts.Clients;
using StaffStoreItemDto = NEXUS.Contracts.Clients.StaffStoreItemDto;
using StaffStoreViewModelDto = NEXUS.Contracts.Clients.StaffStoreViewModelDto;
using UIStaffStoreItemDto = DAWNBRINGER.WebPortal.UI.Models.StaffStoreItemDto;

namespace ASPIRE.Tests.Infrastructure;

public static class TestApiClientFactory
{
    public static IUserApiClient CreateUserApiClient(MerrickContext db) => new TestUserApiClient(db);
    public static IAdminApiClient CreateAdminApiClient(MerrickContext db) => new TestAdminApiClient(db);

    public static IPlayerStatsApiClient CreatePlayerStatsApiClient(MerrickContext db) =>
        new TestPlayerStatsApiClient(db);

    public static IStoreApiClient CreateStoreApiClient(MerrickContext db) => new TestStoreApiClient(db);
    public static IGuideApiClient CreateGuideApiClient(MerrickContext db) => new TestGuideApiClient(db);
}

internal static class TestDtoMapper
{
    public static UserDto? MapUser(User? u)
    {
        if (u == null) return null;
        return new UserDto
        {
            ID = u.ID,
            EmailAddress = u.EmailAddress,
            DiscordId = u.DiscordId,
            DiscordUsername = u.DiscordUsername,
            DiscordAvatar = u.DiscordAvatar,
            TwitchUsername = u.TwitchUsername,
            TwitchId = u.TwitchId,
            Role = u.Role != null ? new UserRoleDto { Id = u.Role.ID, Name = u.Role.Name } : null,
            Accounts = u.Accounts?.Select(MapAccountNonNull).ToList() ?? [],
            TimestampCreated = u.TimestampCreated,
            TimestampLastActive = u.TimestampLastActive,
            GoldCoins = u.GoldCoins,
            SilverCoins = u.SilverCoins,
            PlinkoTickets = u.PlinkoTickets,
            LastPlinkoDailyClaimUtc = u.LastPlinkoDailyClaimUtc,
            TotalLevel = u.TotalLevel,
            TotalExperience = u.TotalExperience,
            OwnedStoreItems = u.InventoryItems?.Select(i => i.ItemCode).ToList() ?? [],
            HasInGamePasswordSet = !string.IsNullOrEmpty(u.SRPPasswordHash)
        };
    }

    public static AccountDto MapAccountNonNull(Account a) => MapAccount(a)!;

    public static AccountDto? MapAccount(Account? a)
    {
        if (a == null) return null;
        return new AccountDto
        {
            ID = a.ID,
            UserID = a.User?.ID ?? 0,
            Name = a.Name,
            IsMain = a.IsMain,
            BanExpiration = a.BanExpiration,
            BanReason = a.BanReason,
            SuspensionExpiration = a.SuspensionExpiration,
            SuspensionReason = a.SuspensionReason,
            MuteExpiration = a.MuteExpiration,
            MuteReason = a.MuteReason,
            OwnerAccountID = a.OwnerAccountID,
            Type = a.Type,
            ClanId = a.ClanID,
            ClanName = a.Clan?.Name,
            ClanTag = a.Clan?.Tag,
            ClanTier = a.ClanTier,
            TimestampCreated = a.TimestampCreated,
            SelectedStoreItems = a.SelectedStoreItems != null ? [.. a.SelectedStoreItems] : [],
            FavoriteGuideIDs = a.FavoriteGuideIDs != null ? [.. a.FavoriteGuideIDs] : [],
            DefaultGuideIDs = a.DefaultGuideIDs != null ? [.. a.DefaultGuideIDs] : [],
            UseCloud = a.UseCloud,
            AutomaticCloudUpload = a.AutomaticCloudUpload,
            BackupLastUpdatedTime = a.BackupLastUpdatedTime,
            User = a.User != null
                ? new UserDto
                {
                    ID = a.User.ID,
                    EmailAddress = a.User.EmailAddress,
                    DiscordId = a.User.DiscordId,
                    DiscordUsername = a.User.DiscordUsername,
                    DiscordAvatar = a.User.DiscordAvatar,
                    TwitchUsername = a.User.TwitchUsername,
                    TwitchId = a.User.TwitchId,
                    Role = a.User.Role != null
                        ? new UserRoleDto { Id = a.User.Role.ID, Name = a.User.Role.Name }
                        : null,
                    Accounts = a.User.Accounts != null
                        ?
                        [
                            .. a.User.Accounts.Select(acc => new AccountDto
                            {
                                ID = acc.ID,
                                UserID = a.User.ID,
                                Name = acc.Name,
                                IsMain = acc.IsMain,
                                BanExpiration = acc.BanExpiration,
                                BanReason = acc.BanReason,
                                SuspensionExpiration = acc.SuspensionExpiration,
                                SuspensionReason = acc.SuspensionReason,
                                MuteExpiration = acc.MuteExpiration,
                                MuteReason = acc.MuteReason,
                                OwnerAccountID = acc.OwnerAccountID,
                                Type = acc.Type
                            })
                        ]
                        : []
                }
                : null
        };
    }

    public static StreamerApplicationDto MapStreamerAppNonNull(StreamerApplication s) => MapStreamerApp(s)!;

    public static StreamerApplicationDto? MapStreamerApp(StreamerApplication? s)
    {
        if (s == null) return null;
        return new StreamerApplicationDto
        {
            ID = s.ID,
            UserID = s.UserID,
            MainAccountID = s.MainAccountID,
            TwitchUsername = s.TwitchUsername,
            TwitchId = s.TwitchId,
            TwitchChannelUrl = s.TwitchChannelUrl ?? "",
            Status = s.Status,
            ReviewedByStaffName = s.ReviewedByStaffName,
            StaffNotes = s.StaffNotes,
            ApplicationNotes = s.ApplicationNotes,
            TimestampApplied = s.TimestampApplied,
            TimestampReviewed = s.TimestampReviewed,
            MainAccount = MapAccount(s.MainAccount)
        };
    }

    public static HostApplicationDto MapHostAppNonNull(HostApplication h) => MapHostApp(h)!;

    public static HostApplicationDto? MapHostApp(HostApplication? h)
    {
        if (h == null) return null;
        return new HostApplicationDto
        {
            ID = h.ID,
            UserID = h.UserID,
            HostUsername = h.HostUsername,
            EncryptedHostPassword = h.EncryptedHostPassword,
            EncryptedAuthToken = h.EncryptedAuthToken,
            Status = h.Status,
            Location = h.Location,
            Instances = h.Instances,
            ServerNamePrefix = h.ServerNamePrefix,
            ApplicationNotes = h.ApplicationNotes,
            StaffNotes = h.StaffNotes,
            ReviewedByStaffName = h.ReviewedByStaffName,
            TimestampApplied = h.TimestampApplied,
            TimestampReviewed = h.TimestampReviewed
        };
    }

    public static UserWebRewardDto MapUserWebRewardNonNull(UserWebReward r) => MapUserWebReward(r)!;

    public static UserWebRewardDto? MapUserWebReward(UserWebReward? r)
    {
        if (r == null) return null;
        return new UserWebRewardDto
        {
            ID = r.Id,
            UserID = r.UserID,
            RewardType = r.RewardType,
            RewardCode = r.RewardCode,
            RewardName = r.Name,
            IsEquipped = r.IsEquipped,
            TimestampClaimed = r.TimestampAcquired
        };
    }

    public static TokenDto MapTokenNonNull(Token t) => MapToken(t)!;

    public static TokenDto? MapToken(Token? t)
    {
        if (t == null) return null;
        int.TryParse(t.Data, out int userId);
        return new TokenDto
        {
            ID = t.ID,
            UserID = userId,
            EmailAddress = t.EmailAddress,
            Value = t.Value,
            Purpose = t.Purpose,
            Data = t.Data,
            TimestampIssued = t.TimestampCreated,
            TimestampExpires = t.TimestampCreated.AddDays(7),
            TimestampConsumed = t.TimestampConsumed
        };
    }

    public static PlayerReportDto MapPlayerReportNonNull(PlayerReport r) => MapPlayerReport(r)!;

    public static PlayerReportDto? MapPlayerReport(PlayerReport? r)
    {
        if (r == null) return null;
        return new PlayerReportDto
        {
            ID = r.ID,
            ReporterAccountID = r.ReporterAccountID ?? 0,
            ReporterAccountName = r.ReporterAccountName,
            ReportedAccountID = r.ReportedAccountID,
            ReportedAccountName = r.ReportedAccountName,
            MatchID = r.MatchID,
            Reason = r.Reason,
            Description = r.Description ?? string.Empty,
            Status = r.Status,
            TimestampCreated = r.TimestampCreated,
            StaffNotes = r.StaffNotes,
            ReviewedByStaffName = r.StaffNotes ?? "AdminStaff",
            PunishmentType = r.PunishmentType,
            PunishmentDurationHours = r.PunishmentDurationHours,
            TimestampReviewed = r.TimestampReviewed
        };
    }

    public static AccountStatisticsDto MapAccountStatsNonNull(AccountStatistics s) => MapAccountStats(s)!;

    public static AccountStatisticsDto? MapAccountStats(AccountStatistics? s)
    {
        if (s == null) return null;
        return new AccountStatisticsDto
        {
            ID = s.ID,
            AccountID = s.AccountID,
            Type = s.Type,
            MatchesPlayed = s.MatchesPlayed,
            MatchesWon = s.MatchesWon,
            MatchesLost = s.MatchesLost,
            MatchesConceded = s.MatchesConceded,
            Disconnects = s.MatchesDisconnected,
            SkillRating = s.SkillRating,
            HeroKills = s.AwardMostKills ?? 0,
            HeroAssists = s.AwardMostAssists ?? 0,
            CreepKills = s.AwardMostCreepKills ?? 0,
            BuildingDamage = s.AwardMostBuildingDamage ?? 0
        };
    }

    public static MatchStatisticsDto MapMatchStatsNonNull(MatchStatistics m) => MapMatchStats(m)!;

    public static MatchStatisticsDto? MapMatchStats(MatchStatistics? m)
    {
        if (m == null) return null;
        return new MatchStatisticsDto
        {
            ID = m.ID,
            MatchID = m.MatchID,
            Map = m.Map,
            Mode = m.GameMode,
            WinningTeam = m.ScoreTeam1 > m.ScoreTeam2 ? 1 : 2,
            TimePlayedSeconds = m.TimePlayed,
            HostAccountName = m.HostAccountName,
            TimestampRecorded = m.TimestampRecorded,
            ReplayUrl = m.ReplayUrl
        };
    }

    public static PlayerStatisticsDto MapPlayerStatsNonNull(PlayerStatistics p) => MapPlayerStats(p)!;

    public static PlayerStatisticsDto? MapPlayerStats(PlayerStatistics? p)
    {
        if (p == null) return null;
        return new PlayerStatisticsDto
        {
            ID = p.ID,
            MatchID = p.MatchID,
            AccountID = p.AccountID,
            AccountName = p.AccountName,
            Team = p.Team,
            Position = p.LobbyPosition,
            HeroIdentifier = p.HeroIdentifier,
            HeroKills = p.HeroKills,
            Deaths = p.HeroDeaths,
            HeroAssists = p.HeroAssists,
            CreepKills = p.TeamCreepKills,
            NeutralKills = p.NeutralCreepKills,
            GoldEarned = p.Gold,
            GoldSpent = p.GoldSpent,
            ExperienceEarned = p.Experience,
            HeroLevel = p.HeroLevel,
            Won = p.Win == 1,
            Inventory = p.Inventory != null ? [.. p.Inventory] : [],
            HeroDamage = p.HeroDamage,
            BuildingDamage = p.BuildingDamage,
            WardsPlaced = p.WardsPlaced
        };
    }

    public static MatchEvidenceDto MapMatchEvidenceNonNull(MatchEvidence e) => MapMatchEvidence(e)!;

    public static MatchEvidenceDto? MapMatchEvidence(MatchEvidence? e)
    {
        if (e == null) return null;
        return new MatchEvidenceDto
        {
            ID = e.ID,
            MatchID = e.MatchID,
            HasReplay = e.HasReplay,
            HasVoice = e.HasVoice,
            ChatLogJson = e.ChatLogJson,
            VoiceTrackLegionUrl = e.VoiceTrackLegionUrl,
            VoiceTrackHellbourneUrl = e.VoiceTrackHellbourneUrl,
            TimestampExtracted = e.TimestampExtracted,
            EvidenceJson = "{}",
            TimestampCreated = e.TimestampExtracted
        };
    }

    public static HeroDetailDto MapHeroDetailNonNull(HeroDetail h) => MapHeroDetail(h)!;

    public static HeroDetailDto? MapHeroDetail(HeroDetail? h)
    {
        if (h == null) return null;
        return new HeroDetailDto
        {
            ID = h.ID,
            HeroIdentifier = h.HeroCode,
            DisplayName = h.DisplayName,
            Description = h.Lore,
            PrimaryAttribute = h.PrimaryAttribute,
            Role = h.AttackType,
            AttackType = h.AttackType,
            BaseStrength = h.BaseStrength,
            StrengthGrowth = h.StrengthGrowth,
            BaseAgility = h.BaseAgility,
            AgilityGrowth = h.AgilityGrowth,
            BaseIntelligence = h.BaseIntelligence,
            IntelligenceGrowth = h.IntelligenceGrowth,
            Armor = h.Armor,
            ArmorMitigation = h.ArmorMitigation,
            MinDamage = h.MinDamage,
            MaxDamage = h.MaxDamage,
            MovementSpeed = h.MovementSpeed,
            IconPath = h.IconPath,
            SplashImagePath = h.SplashImagePath,
            Lore = h.Lore,
            AbilitiesJson = h.AbilitiesJson
        };
    }

    public static HeroItemDto MapHeroItemNonNull(HeroItem i) => MapHeroItem(i)!;

    public static HeroItemDto? MapHeroItem(HeroItem? i)
    {
        if (i == null) return null;
        return new HeroItemDto
        {
            ID = i.ID,
            ItemIdentifier = i.Code,
            DisplayName = i.Name,
            Description = i.Description,
            Cost = i.Cost,
            Category = i.Category,
            RecipeCost = i.RecipeCost,
            IconPath = i.IconPath,
            Flavor = i.Flavor,
            Upgrade = i.Upgrade,
            IsActive = i.IsActive,
            ComponentCodesJson = i.ComponentCodesJson
        };
    }

    public static HeroGuideDto MapHeroGuideNonNull(HeroGuide g) => MapHeroGuide(g)!;

    public static HeroGuideDto? MapHeroGuide(HeroGuide? g)
    {
        if (g == null) return null;
        return new HeroGuideDto
        {
            ID = g.ID,
            Name = g.Name,
            HeroIdentifier = g.HeroIdentifier,
            HeroName = g.HeroName,
            TargetMap = g.TargetMap,
            Intro = g.Intro,
            Content = g.Content,
            StartingItems = g.StartingItems,
            EarlyGameItems = g.EarlyGameItems,
            CoreItems = g.CoreItems,
            LuxuryItems = g.LuxuryItems,
            AbilityQueue = g.AbilityQueue,
            Public = g.Public,
            Official = g.Featured,
            Rating = g.Rating,
            Upvotes = g.UpVotes,
            Downvotes = g.DownVotes,
            Author = g.Author != null ? MapAccount(g.Author) : null,
            TimestampCreated = g.TimestampCreated,
            TimestampUpdated = g.TimestampLastUpdated ?? g.TimestampCreated
        };
    }

    public static HeroDifficultyVoteDto MapHeroVoteNonNull(HeroDifficultyVote v) => MapHeroVote(v)!;

    public static HeroDifficultyVoteDto? MapHeroVote(HeroDifficultyVote? v)
    {
        if (v == null) return null;
        return new HeroDifficultyVoteDto
        {
            ID = v.ID,
            AccountID = v.AccountID,
            HeroIdentifier = v.HeroIdentifier,
            InGameNickname = string.Empty,
            Difficulty = v.Rating,
            TimestampCreated = v.Timestamp
        };
    }

    public static StoreItemDto MapStoreItemNonNull(StoreItem s) => MapStoreItem(s)!;

    public static StoreItemDto? MapStoreItem(StoreItem? s)
    {
        if (s == null) return null;
        return new StoreItemDto
        {
            ID = s.Id,
            Code = s.Code,
            Name = s.Name,
            TypeName = s.UpgradeType.ToString(),
            Type = s.UpgradeType,
            GoldCost = s.PriceInMMP,
            SilverCost = s.Price,
            IsEnabled = s.Enabled,
            Purchasable = s.Purchasable,
            Resource = s.LocalContent ?? string.Empty
        };
    }
}

internal class TestUserApiClient(MerrickContext db) : IUserApiClient
{
    public async Task<UserDto?> GetUserByIdAsync(int id, CancellationToken ct = default)
    {
        return MapUser(await db.Users
            .Include(u => u.Accounts)
            .Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.ID == id, ct));
    }

    public async Task<UserDto?> GetUserByEmailAsync(string email, CancellationToken ct = default)
    {
        return MapUser(await db.Users
            .Include(u => u.Accounts)
            .Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.EmailAddress == email, ct));
    }

    public async Task<UserDto?> GetUserByDiscordIdAsync(string discordId, CancellationToken ct = default)
    {
        return MapUser(await db.Users
            .Include(u => u.Accounts)
            .Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.DiscordId == discordId, ct));
    }

    public async Task<UserDto?> GetUserByAccountNameAsync(string accountName, CancellationToken ct = default)
    {
        return MapUser(await db.Users
            .Include(u => u.Accounts)
            .Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.Accounts.Any(a => a.Name.ToLower() == accountName.ToLower()), ct));
    }

    public async Task<AccountDto?> GetAccountByNameAsync(string name, CancellationToken ct = default)
    {
        return MapAccount(await db.Accounts
            .Include(a => a.User).ThenInclude(u => u.Role)
            .Include(a => a.User).ThenInclude(u => u.Accounts)
            .FirstOrDefaultAsync(a => a.Name.ToLower() == name.ToLower(), ct));
    }

    public async Task<AccountDto?> GetAccountByIdAsync(int id, CancellationToken ct = default)
    {
        return MapAccount(await db.Accounts
            .Include(a => a.User).ThenInclude(u => u.Role)
            .Include(a => a.User).ThenInclude(u => u.Accounts)
            .FirstOrDefaultAsync(a => a.ID == id, ct));
    }

    public async Task<bool> ExistsAccountNameAsync(string name, CancellationToken ct = default)
    {
        return await db.Accounts.AnyAsync(a => a.Name.ToLower() == name.ToLower(), ct);
    }

    public async Task<bool> ExistsEmailAsync(string email, CancellationToken ct = default)
    {
        return await db.Users.AnyAsync(u => u.EmailAddress.ToLower() == email.ToLower(), ct);
    }

    public async Task<UserDto?> RegisterUserAsync(RegisterUserRequestDto payload, CancellationToken ct = default)
    {
        Role? userRole = await db.Roles.FirstOrDefaultAsync(r => r.Name == UserRoles.User, ct);
        if (userRole == null)
        {
            userRole = new Role { Name = UserRoles.User };
            db.Roles.Add(userRole);
            await db.SaveChangesAsync(ct);
        }

        User user = new()
        {
            EmailAddress = payload.EmailAddress,
            Role = userRole,
            SRPPasswordSalt = "salt",
            SRPPasswordHash = "hash",
            PBKDF2PasswordHash = "hash"
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);

        Account account = new()
        {
            Name = payload.InGameNickname,
            User = user,
            Type = AccountType.Normal,
            IsMain = true
        };
        db.Accounts.Add(account);
        await db.SaveChangesAsync(ct);

        return await GetUserByIdAsync(user.ID, ct);
    }

    public async Task<AuthenticationTokenResponseDto?> LogInUserAsync(LogInUserRequestDto payload,
        CancellationToken ct = default)
    {
        User? dbUser = await db.Users
            .Include(u => u.Accounts)
            .Include(u => u.Role)
            .FirstOrDefaultAsync(
                u => u.EmailAddress == payload.Login || u.Accounts.Any(a => a.Name == payload.Login),
                ct);
        if (dbUser == null) return null;
        if (!string.IsNullOrEmpty(dbUser.SRPPasswordSalt) && !string.IsNullOrEmpty(dbUser.SRPPasswordHash))
        {
            string expectedHash = global::DAWNBRINGER.WebPortal.UI.Utilities.SRP.ComputeSRPPasswordHash(payload.Password, dbUser.SRPPasswordSalt, passwordIsHashed: false);
            if (!string.Equals(expectedHash, dbUser.SRPPasswordHash, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }
        UserDto? user = MapUser(dbUser);
        if (user == null) return null;
        return new AuthenticationTokenResponseDto("test_token", TimeProvider.System.GetUtcNow().AddDays(7));
    }

    public async Task<UserDto?> RegisterOrLoginDiscordAsync(DiscordAuthRequestDto payload,
        CancellationToken ct = default)
    {
        User? user = await db.Users.Include(u => u.Accounts).Include(u => u.Role).FirstOrDefaultAsync(
            u => u.DiscordId == payload.DiscordId || u.EmailAddress == $"d_{payload.DiscordId}" ||
                 (payload.Email != null && u.EmailAddress == payload.Email), ct);
        if (user != null)
        {
            if (string.IsNullOrEmpty(user.DiscordId)) user.DiscordId = payload.DiscordId;
            if (!string.IsNullOrEmpty(payload.DiscordUsername)) user.DiscordUsername = payload.DiscordUsername;
            if (!string.IsNullOrEmpty(payload.DiscordAvatar)) user.DiscordAvatar = payload.DiscordAvatar;
            await db.SaveChangesAsync(ct);
            return MapUser(user);
        }

        Role? userRole = await db.Roles.FirstOrDefaultAsync(r => r.Name == UserRoles.User, ct);
        if (userRole == null)
        {
            userRole = new Role { Name = UserRoles.User };
            db.Roles.Add(userRole);
            await db.SaveChangesAsync(ct);
        }

        user = new User
        {
            EmailAddress = payload.Email ?? $"d_{payload.DiscordId}",
            DiscordId = payload.DiscordId,
            DiscordUsername = payload.DiscordUsername,
            DiscordAvatar = payload.DiscordAvatar,
            Role = userRole,
            SRPPasswordSalt = string.Empty,
            SRPPasswordHash = string.Empty
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        return MapUser(user);
    }

    public async Task<bool> SetInGamePasswordAsync(int userId, string salt, string srpHash, string? pbkdf2Hash = null,
        CancellationToken ct = default)
    {
        User? user = await db.Users.FirstOrDefaultAsync(u => u.ID == userId, ct);
        if (user == null) return false;
        user.SRPPasswordSalt = salt;
        user.SRPPasswordHash = srpHash;
        if (!string.IsNullOrEmpty(pbkdf2Hash))
        {
            user.PBKDF2PasswordHash = pbkdf2Hash;
        }

        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> LinkTwitchAsync(int userId, string twitchId, string twitchUsername,
        CancellationToken ct = default)
    {
        User? user = await db.Users.FirstOrDefaultAsync(u => u.ID == userId, ct);
        if (user == null) return false;
        user.TwitchId = twitchId;
        user.TwitchUsername = twitchUsername;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> UnlinkTwitchAsync(int userId, CancellationToken ct = default)
    {
        User? user = await db.Users.FirstOrDefaultAsync(u => u.ID == userId, ct);
        if (user == null) return false;
        user.TwitchId = null;
        user.TwitchUsername = null;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> ApplyHostAsync(ApplyHostRequestDto payload, CancellationToken ct = default)
    {
        Account? mainAccount = await db.Accounts.Include(a => a.User)
                                   .FirstOrDefaultAsync(a => a.Name == payload.ServerNamePrefix, ct)
                               ?? await db.Accounts.Include(a => a.User).OrderByDescending(a => a.IsMain)
                                   .ThenBy(a => a.ID).FirstOrDefaultAsync(ct);
        if (mainAccount == null) return false;
        User user = mainAccount.User;

        int existingHostCount = await db.Accounts.CountAsync(
            a => (a.OwnerAccountID == mainAccount.ID || (a.User != null && a.User.ID == user.ID)) &&
                 a.Type == AccountType.ServerHost, ct);
        int approvedAppCount =
            await db.HostApplications.CountAsync(
                h => h.MainAccountID == mainAccount.ID && h.Status == HostApplicationStatus.Approved, ct);
        int totalApproved = Math.Max(existingHostCount, approvedAppCount);
        string suffix = totalApproved > 0 ? (totalApproved + 1).ToString() : "";
        string hostUsername = $"{mainAccount.Name}_host{suffix}";

        HostApplication? pendingApp =
            await db.HostApplications.FirstOrDefaultAsync(
                h => h.MainAccountID == mainAccount.ID && h.Status == HostApplicationStatus.Pending, ct);
        if (pendingApp != null)
        {
            pendingApp.ServerNamePrefix = payload.ServerNamePrefix;
            pendingApp.Location = payload.Location;
            pendingApp.Instances = payload.Instances;
            pendingApp.ApplicationNotes = payload.ApplicationNotes;
            pendingApp.TimestampApplied = TimeProvider.System.GetUtcNow();
            await db.SaveChangesAsync(ct);
            return true;
        }

        HostApplication app = new()
        {
            UserID = user.ID,
            User = user,
            MainAccountID = mainAccount.ID,
            MainAccount = mainAccount,
            HostUsername = hostUsername,
            EncryptedHostPassword = Guid.NewGuid().ToString("N"),
            EncryptedAuthToken = Guid.NewGuid().ToString("N"),
            Status = HostApplicationStatus.Pending,
            Location = payload.Location ?? "USE",
            Instances = payload.Instances,
            ServerNamePrefix = payload.ServerNamePrefix ?? mainAccount.Name,
            ApplicationNotes = payload.ApplicationNotes,
            TimestampApplied = TimeProvider.System.GetUtcNow()
        };
        db.HostApplications.Add(app);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> ApplyStreamerAsync(ApplyStreamerClientDto payload, CancellationToken ct = default)
    {
        User? user = await db.Users.Include(u => u.Accounts).FirstOrDefaultAsync(u => u.ID == payload.UserId, ct)
                     ?? await db.Users.Include(u => u.Accounts)
                         .FirstOrDefaultAsync(u => u.Accounts.Any(a => a.Name == payload.TwitchUsername), ct)
                     ?? await db.Users.Include(u => u.Accounts).OrderBy(u => u.ID).FirstOrDefaultAsync(ct);
        if (user == null) return false;

        Account? mainAccount = user.Accounts.FirstOrDefault(a => a.IsMain) ?? user.Accounts.FirstOrDefault();
        int mainAccountId = mainAccount?.ID ?? user.ID;

        user.TwitchUsername = payload.TwitchUsername;
        user.TwitchId = payload.TwitchId;

        StreamerApplication app = new()
        {
            UserID = user.ID,
            User = user,
            MainAccountID = mainAccountId,
            MainAccount = mainAccount!,
            TwitchUsername = payload.TwitchUsername ?? mainAccount?.Name ?? "",
            TwitchId = payload.TwitchId,
            TwitchChannelUrl = payload.TwitchChannelUrl ?? "",
            Status = StreamerApplicationStatus.Pending,
            ApplicationNotes = payload.ApplicationNotes,
            TimestampApplied = TimeProvider.System.GetUtcNow()
        };
        db.StreamerApplications.Add(app);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<StreamerApplicationDto?> GetStreamerApplicationAsync(int userId, CancellationToken ct = default)
    {
        return MapStreamerApp(await db.StreamerApplications.Include(s => s.User).Include(s => s.MainAccount)
            .FirstOrDefaultAsync(s => s.UserID == userId, ct));
    }

    public async Task<HostApplicationDto?> GetHostApplicationAsync(int userId, CancellationToken ct = default)
    {
        return MapHostApp(await db.HostApplications.FirstOrDefaultAsync(h => h.UserID == userId, ct));
    }

    public async Task<IReadOnlyList<UserWebRewardDto>> GetUserWebRewardsAsync(int userId,
        CancellationToken ct = default)
    {
        List<UserWebReward> list = await db.UserWebRewards.Where(r => r.UserID == userId).ToListAsync(ct);
        return [.. list.Select(MapUserWebRewardNonNull)];
    }

    public async Task<bool> EquipWebRewardAsync(int userId, string rewardType, string rewardCode,
        CancellationToken ct = default)
    {
        List<UserWebReward> rewards = await db.UserWebRewards
            .Where(r => r.UserID == userId && r.RewardType == rewardType).ToListAsync(ct);
        foreach (UserWebReward r in rewards)
        {
            r.IsEquipped = (r.RewardCode == rewardCode);
        }

        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<HostTelemetryResultDto?> GetHostTelemetryAsync(string accountName, CancellationToken ct = default)
    {
        Account? account = await db.Accounts
            .Include(a => a.User).ThenInclude(u => u.Accounts).ThenInclude(acc => acc.Statistics)
            .Include(a => a.Statistics)
            .FirstOrDefaultAsync(a => a.Name.ToLower() == accountName.ToLower(), ct);
        if (account == null) return null;

        User? user = account.User;
        List<Account> allLinkedAccounts = user?.Accounts?.ToList() ?? [account];
        List<HostApplication> hostApps = await db.HostApplications
            .Where(h => (user != null && h.UserID == user.ID) || h.MainAccountID == account.ID ||
                        h.HostAccountID == account.ID || h.HostUsername == account.Name)
            .ToListAsync(ct);

        List<string> linkedHostNames =
        [
            .. allLinkedAccounts
                .Where(a => a.Type == AccountType.ServerHost)
                .Select(a => a.Name)
                .Union(hostApps.Where(h => h.Status == HostApplicationStatus.Approved).Select(h => h.HostUsername))
                .Distinct(StringComparer.OrdinalIgnoreCase)
        ];

        List<Account> playerAccounts = [.. allLinkedAccounts.Where(a => a.Type != AccountType.ServerHost)];
        List<int> linkedPlayerAccountIds = [.. playerAccounts.Select(a => a.ID).Distinct()];
        List<string> linkedPlayerAccountNames = [.. playerAccounts.Select(a => a.Name).Distinct()];

        if (account.OwnerAccountID.HasValue && !linkedPlayerAccountIds.Contains(account.OwnerAccountID.Value))
            linkedPlayerAccountIds.Add(account.OwnerAccountID.Value);

        if (linkedPlayerAccountIds.Count == 0 && account.Type != AccountType.ServerHost)
        {
            linkedPlayerAccountIds.Add(account.ID);
            linkedPlayerAccountNames.Add(account.Name);
        }

        bool hasLinkedHostAccounts =
            linkedHostNames.Count > 0 || hostApps.Any(h => h.Status == HostApplicationStatus.Approved);
        bool hasPendingHostApp = hostApps.Any(h => h.Status == HostApplicationStatus.Pending);
        HostApplication? primaryHostApp = hostApps.FirstOrDefault(h => h.Status == HostApplicationStatus.Approved) ??
                                          hostApps.FirstOrDefault();
        string primaryHostUsername = primaryHostApp?.HostUsername ?? (account.Type == AccountType.ServerHost
            ? account.Name
            : linkedHostNames.FirstOrDefault() ?? $"{account.Name}_host");

        int gamesHosted = 0;
        if (linkedHostNames.Count > 0)
        {
            gamesHosted = await db.MatchStatistics.Where(m => linkedHostNames.Contains(m.HostAccountName))
                .CountAsync(ct);
        }

        int totalPlayed = 0;
        if (linkedPlayerAccountIds.Count > 0)
        {
            totalPlayed = await db.PlayerStatistics.Where(ps => linkedPlayerAccountIds.Contains(ps.AccountID))
                .CountAsync(ct);
            if (totalPlayed == 0)
            {
                totalPlayed = playerAccounts.Sum(a =>
                    a.Statistics
                        .FirstOrDefault(s =>
                            s.Type is AccountStatisticsType.Matchmaking or AccountStatisticsType.Player)
                        ?.MatchesPlayed ?? 0);
            }
        }

        double hostingRatio = gamesHosted > 0 ? Math.Round((double)gamesHosted / Math.Max(1, totalPlayed), 2) : 0.0;
        double communityContribution = 0.0;
        if (gamesHosted > 0)
        {
            int totalCommunityMatches = await db.MatchStatistics.CountAsync(ct);
            if (totalCommunityMatches > 0)
            {
                communityContribution = Math.Round((double)gamesHosted / totalCommunityMatches * 100.0, 1);
            }
        }

        string? hostBadgeTier = null;
        if (hasLinkedHostAccounts || account.Type == AccountType.ServerHost || gamesHosted > 0)
        {
            hostBadgeTier = gamesHosted switch
            {
                >= 250 => "Patron of Newerth",
                _ when hostingRatio >= 2.0 && gamesHosted >= 50 => "Patron of Newerth",
                >= 100 => "Master Host",
                _ when hostingRatio >= 1.0 && gamesHosted >= 25 => "Master Host",
                >= 25 => "Dedicated Host",
                _ when hostingRatio >= 0.5 && gamesHosted >= 10 => "Dedicated Host",
                _ => "Community Host"
            };
        }

        return new HostTelemetryResultDto(
            hasLinkedHostAccounts,
            hasPendingHostApp,
            linkedHostNames,
            linkedPlayerAccountIds,
            linkedPlayerAccountNames,
            gamesHosted,
            totalPlayed,
            hostingRatio,
            communityContribution,
            hostBadgeTier,
            MapHostApp(primaryHostApp),
            primaryHostUsername
        );
    }

    public async Task<TokenDto?> ValidateTokenAsync(string tokenString, TokenPurpose purpose,
        CancellationToken ct = default)
    {
        if (Guid.TryParse(tokenString, out Guid guid))
        {
            return MapToken(
                await db.Tokens.FirstOrDefaultAsync(
                    t => t.Value == guid && t.Purpose == purpose && t.TimestampConsumed == null, ct));
        }

        return null;
    }

    public async Task<TokenDto> IssueTokenAsync(int userId, TokenPurpose purpose, TimeSpan ttl,
        CancellationToken ct = default)
    {
        User? user = await db.Users.FirstOrDefaultAsync(u => u.ID == userId, ct);
        Token token = new()
        {
            Value = Guid.NewGuid(),
            Purpose = purpose,
            EmailAddress = user?.EmailAddress ?? "",
            Data = userId.ToString(),
            TimestampCreated = TimeProvider.System.GetUtcNow()
        };
        db.Tokens.Add(token);
        await db.SaveChangesAsync(ct);
        return MapToken(token)!;
    }

    public async Task<bool> InvalidateTokenAsync(string tokenString, CancellationToken ct = default)
    {
        if (Guid.TryParse(tokenString, out Guid guid))
        {
            Token? token = await db.Tokens.FirstOrDefaultAsync(t => t.Value == guid, ct);
            if (token != null)
            {
                token.TimestampConsumed = TimeProvider.System.GetUtcNow();
                await db.SaveChangesAsync(ct);
                return true;
            }
        }

        return false;
    }

    public Task<GenerateInviteResultDto?> GenerateInviteCodeAsync(int userId, CancellationToken ct = default)
    {
        return Task.FromResult<GenerateInviteResultDto?>(new GenerateInviteResultDto(true, "KONGOR-TEST-1234", "Invite code generated successfully.", 4, 5, 1, 500));
    }

    public Task<GenerateInviteResultDto?> GetUserInviteQuotaAsync(int userId, CancellationToken ct = default)
    {
        return Task.FromResult<GenerateInviteResultDto?>(new GenerateInviteResultDto(true, null, "OK", 5, 5, 0, 500));
    }

    public Task<IReadOnlyList<UserInviteDto>> GetMyInvitesAsync(int userId, CancellationToken ct = default)
    {
        IReadOnlyList<UserInviteDto> list = Array.Empty<UserInviteDto>();
        return Task.FromResult(list);
    }

    public Task<ValidateInviteResultDto> ValidateInviteCodeAsync(string code, CancellationToken ct = default)
    {
        return Task.FromResult(new ValidateInviteResultDto(true, "Valid invite code", "Staff"));
    }

    public Task<bool> IsInviteOnlyModeEnabledAsync(CancellationToken ct = default)
    {
        return Task.FromResult(false);
    }
}

internal class TestAdminApiClient(MerrickContext db) : IAdminApiClient
{
    private Account? _cachedAccount;

    public async Task<UserSummaryStatsDto> GetSummaryStatsAsync(CancellationToken ct = default)
    {
        int totalUsers = await db.Users.CountAsync(ct);
        int totalAccounts = await db.Accounts.CountAsync(ct);
        int totalHosts = await db.Accounts.CountAsync(a => a.Type == AccountType.ServerHost, ct);
        int totalStaff = await db.Users.CountAsync(u => u.Role != null && u.Role.Name == UserRoles.Administrator, ct);
        return new UserSummaryStatsDto
        {
            TotalUsers = totalUsers,
            TotalAccounts = totalAccounts,
            TotalHosts = totalHosts,
            TotalStaff = totalStaff,
            IsDatabaseConnected = true,
            StatusMessage = "Operational"
        };
    }

    public async Task<IReadOnlyList<AdminUserDetailDto>> SearchUsersAsync(string? query, string? roleFilter,
        int limit = 100, CancellationToken ct = default)
    {
        IQueryable<User> q = db.Users.Include(u => u.Accounts).Include(u => u.Role).AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query))
        {
            q = q.Where(u => u.EmailAddress.Contains(query) || u.Accounts.Any(a => a.Name.Contains(query)));
        }

        if (!string.IsNullOrWhiteSpace(roleFilter))
        {
            q = q.Where(u => u.Role != null && u.Role.Name == roleFilter);
        }

        List<User> list = await q.Take(limit).ToListAsync(ct);
        return
        [
            .. list.Select(u => new AdminUserDetailDto
            {
                Id = u.ID,
                EmailAddress = u.EmailAddress,
                DiscordId = u.DiscordId,
                DiscordUsername = u.DiscordUsername,
                DiscordAvatar = u.DiscordAvatar,
                RoleName = u.Role?.Name ?? "User",
                IsStaff = u.Role?.Name == UserRoles.Administrator,
                GoldCoins = u.GoldCoins,
                SilverCoins = u.SilverCoins,
                PlinkoTickets = u.PlinkoTickets,
                TotalLevel = u.TotalLevel,
                TotalExperience = u.TotalExperience,
                TimestampCreated = u.TimestampCreated,
                TimestampLastActive = u.TimestampLastActive,
                Accounts = u.Accounts?.Select(a => new AdminAccountItemDto
                {
                    Id = a.ID,
                    Name = a.Name,
                    IsMain = a.IsMain,
                    IsStaff = u.Role?.Name == UserRoles.Administrator,
                    Type = a.Type,
                    TimestampCreated = a.TimestampCreated
                }).ToList() ?? []
            })
        ];
    }

    public async Task<AdminUserDetailDto?> GetUserByIdAsync(int id, CancellationToken ct = default)
    {
        User? u = await db.Users.Include(u => u.Accounts).Include(u => u.Role).FirstOrDefaultAsync(u => u.ID == id, ct);
        if (u == null) return null;
        return new AdminUserDetailDto
        {
            Id = u.ID,
            EmailAddress = u.EmailAddress,
            DiscordId = u.DiscordId,
            DiscordUsername = u.DiscordUsername,
            DiscordAvatar = u.DiscordAvatar,
            RoleName = u.Role?.Name ?? "User",
            IsStaff = u.Role?.Name == UserRoles.Administrator,
            GoldCoins = u.GoldCoins,
            SilverCoins = u.SilverCoins,
            PlinkoTickets = u.PlinkoTickets,
            TotalLevel = u.TotalLevel,
            TotalExperience = u.TotalExperience,
            TimestampCreated = u.TimestampCreated,
            TimestampLastActive = u.TimestampLastActive,
            Accounts = u.Accounts?.Select(a => new AdminAccountItemDto
            {
                Id = a.ID,
                Name = a.Name,
                IsMain = a.IsMain,
                IsStaff = u.Role?.Name == UserRoles.Administrator,
                Type = a.Type,
                TimestampCreated = a.TimestampCreated
            }).ToList() ?? []
        };
    }

    public Task<AdminMetricsDTO> GetStaffMetricsAsync(CancellationToken ct = default)
    {
        return Task.FromResult(new AdminMetricsDTO(
            10, 2, 1.5, 5, ["USE", "USW", "EU"], 0, 100, 50, 25.0, [], TimeProvider.System.GetUtcNow()
        ));
    }

    public async Task<IReadOnlyList<ApprovedHostDto>> GetApprovedHostsAsync(CancellationToken ct = default)
    {
        List<Account> hosts = await db.Accounts.Where(a => a.Type == AccountType.ServerHost).ToListAsync(ct);
        return
        [
            .. hosts.Select(h => new ApprovedHostDto
            {
                ApplicationId = h.ID,
                UserId = h.User?.ID ?? 0,
                HostAccountId = h.ID,
                HostAccountName = h.Name,
                ServerName = h.Name,
                ServerLocation = "USE",
                HostIpAddress = "127.0.0.1",
                Port = 11235
            })
        ];
    }

    public async Task<bool> SetUserRoleAsync(int userId, string roleName, CancellationToken ct = default)
    {
        User? user = await db.Users.FirstOrDefaultAsync(u => u.ID == userId, ct);
        if (user == null) return false;
        Role? role = await db.Roles.FirstOrDefaultAsync(r => r.Name == roleName, ct);
        if (role == null)
        {
            role = new Role { Name = roleName };
            db.Roles.Add(role);
            await db.SaveChangesAsync(ct);
        }

        user.Role = role;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> SetAccountRoleAsync(int accountId, int accountType, CancellationToken ct = default)
    {
        Account? a = await db.Accounts.FirstOrDefaultAsync(a => a.ID == accountId, ct);
        if (a == null) return false;
        a.Type = (AccountType)accountType;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<ResetCredentialResponse> ResetHostPasswordAsync(int targetId, CancellationToken ct = default)
    {
        HostApplication? app = await db.HostApplications.Include(h => h.User).Include(h => h.MainAccount)
            .FirstOrDefaultAsync(h => h.ID == targetId, ct);
        Account? acc = null;
        if (app == null)
        {
            acc = await db.Accounts.Include(a => a.User).FirstOrDefaultAsync(a => a.ID == targetId, ct);
            if (acc != null)
            {
                app = await db.HostApplications.FirstOrDefaultAsync(
                    h => h.UserID == (acc.User != null ? acc.User.ID : 0) || h.MainAccountID == acc.ID ||
                         h.HostAccountID == acc.ID, ct);
            }
        }
        else
        {
            acc = app.MainAccount ?? (app.MainAccountID > 0
                ? await db.Accounts.FirstOrDefaultAsync(a => a.ID == app.MainAccountID, ct)
                : null);
        }

        string newPass = "NEW_SECURE_HOST_PWD_99";
        string encPass = HostSecurityService.EncryptPassword(newPass);

        if (app != null)
        {
            app.EncryptedHostPassword = encPass;

            string hostEmail = $"host_{app.HostUsername.ToLower()}@nexus.local";
            User? hostUser = await db.Users.FirstOrDefaultAsync(u => u.EmailAddress == hostEmail, ct);
            if (hostUser == null)
            {
                Role? hostRole = await db.Roles.FirstOrDefaultAsync(r => r.Name == "ServerHost", ct)
                                 ?? await db.Roles.FirstOrDefaultAsync(r => r.Name == UserRoles.User, ct)
                                 ?? new Role { Name = "ServerHost" };
                hostUser = new User
                {
                    EmailAddress = hostEmail,
                    Role = hostRole,
                    SRPPasswordSalt = "NEW_HOST_SALT_99",
                    SRPPasswordHash = "NEW_HOST_HASH_99",
                    PBKDF2PasswordHash = "NEW_HOST_PBKDF2_99"
                };
                db.Users.Add(hostUser);
            }
            else
            {
                hostUser.SRPPasswordSalt = "NEW_HOST_SALT_99";
                hostUser.SRPPasswordHash = "NEW_HOST_HASH_99";
                hostUser.PBKDF2PasswordHash = "NEW_HOST_PBKDF2_99";
            }
        }

        await db.SaveChangesAsync(ct);
        return new ResetCredentialResponse { Value = newPass };
    }

    public async Task<ResetCredentialResponse> ResetHostAuthTokenAsync(int targetId, CancellationToken ct = default)
    {
        HostApplication? app = await db.HostApplications.Include(h => h.User).Include(h => h.MainAccount)
            .FirstOrDefaultAsync(h => h.ID == targetId, ct);
        Account? acc = null;
        if (app == null)
        {
            acc = await db.Accounts.Include(a => a.User).FirstOrDefaultAsync(a => a.ID == targetId, ct);
            if (acc != null)
            {
                app = await db.HostApplications.FirstOrDefaultAsync(
                    h => h.UserID == (acc.User != null ? acc.User.ID : 0) || h.MainAccountID == acc.ID ||
                         h.HostAccountID == acc.ID, ct);
            }
        }
        else
        {
            acc = app.MainAccount ?? (app.MainAccountID > 0
                ? await db.Accounts.FirstOrDefaultAsync(a => a.ID == app.MainAccountID, ct)
                : null);
        }

        string newToken = $"compel_{Guid.NewGuid():N}";
        string encToken = HostSecurityService.EncryptPassword(newToken);

        app?.EncryptedAuthToken = encToken;

        await db.SaveChangesAsync(ct);
        return new ResetCredentialResponse { Value = newToken };
    }

    public async Task<bool> UpdateUserEconomyAsync(int userId, UpdateUserEconomyRequest request,
        CancellationToken ct = default)
    {
        User? user = await db.Users.FirstOrDefaultAsync(u => u.ID == userId, ct);
        if (user == null) return false;
        user.GoldCoins = request.GoldCoins;
        user.SilverCoins = request.SilverCoins;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> AdjustUserBalanceAsync(int userId, AdjustUserBalanceRequest request,
        CancellationToken ct = default)
    {
        User? user = await db.Users.FirstOrDefaultAsync(u => u.ID == userId, ct);
        if (user == null) return false;
        user.GoldCoins += request.GoldDelta;
        user.SilverCoins += request.SilverDelta;
        user.PlinkoTickets += request.TicketsDelta;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public Task<bool> PromoteUserToStaffAsync(int userId, CancellationToken ct = default) =>
        SetUserRoleAsync(userId, UserRoles.Administrator, ct);

    public Task<bool> PromoteUserToHostAsync(int userId, CancellationToken ct = default) =>
        SetUserRoleAsync(userId, "ServerHost", ct);

    public Task<bool> RevertUserToStandardAsync(int userId, CancellationToken ct = default) =>
        SetUserRoleAsync(userId, UserRoles.User, ct);

    public async Task<IReadOnlyList<PlayerReportDto>> GetPlayerReportsAsync(string? status, string? reason,
        string? search, int? matchId, int? reportId, int page = 1, int pageSize = 25, CancellationToken ct = default)
    {
        IQueryable<PlayerReport> q = db.PlayerReports.AsNoTracking();
        if (reportId.HasValue && reportId.Value > 0) q = q.Where(r => r.ID == reportId.Value);
        else
        {
            if (matchId.HasValue && matchId.Value > 0) q = q.Where(r => r.MatchID == matchId.Value);
            if (!string.IsNullOrWhiteSpace(status) && status != "all") q = q.Where(r => r.Status == status);
            if (!string.IsNullOrWhiteSpace(reason) && reason != "all") q = q.Where(r => r.Reason == reason);
            if (!string.IsNullOrWhiteSpace(search))
            {
                string s = search.Trim();
                int? parsedId = int.TryParse(s.TrimStart('#'), out int pid) ? pid : null;
                q = q.Where(r =>
                    (parsedId.HasValue && r.ID == parsedId.Value) || r.ReportedAccountName.Contains(s) ||
                    r.ReporterAccountName.Contains(s) || (r.Description != null && r.Description.Contains(s)));
            }
        }

        List<PlayerReport> reports = await q.OrderByDescending(r => r.TimestampCreated).Skip((page - 1) * pageSize)
            .Take(pageSize).ToListAsync(ct);
        return [.. reports.Select(MapPlayerReportNonNull)];
    }

    public async Task<int> GetPlayerReportsCountAsync(string? status, string? reason, string? search, int? matchId,
        int? reportId, CancellationToken ct = default)
    {
        IQueryable<PlayerReport> q = db.PlayerReports.AsNoTracking();
        if (reportId.HasValue && reportId.Value > 0) q = q.Where(r => r.ID == reportId.Value);
        else
        {
            if (matchId.HasValue && matchId.Value > 0) q = q.Where(r => r.MatchID == matchId.Value);
            if (!string.IsNullOrWhiteSpace(status) && status != "all") q = q.Where(r => r.Status == status);
            if (!string.IsNullOrWhiteSpace(reason) && reason != "all") q = q.Where(r => r.Reason == reason);
            if (!string.IsNullOrWhiteSpace(search))
            {
                string s = search.Trim();
                int? parsedId = int.TryParse(s.TrimStart('#'), out int pid) ? pid : null;
                q = q.Where(r =>
                    (parsedId.HasValue && r.ID == parsedId.Value) || r.ReportedAccountName.Contains(s) ||
                    r.ReporterAccountName.Contains(s) || (r.Description != null && r.Description.Contains(s)));
            }
        }

        return await q.CountAsync(ct);
    }

    public async Task<PlayerReportDto?> GetPlayerReportByIdAsync(int id, CancellationToken ct = default)
    {
        PlayerReport? r = await db.PlayerReports.FirstOrDefaultAsync(r => r.ID == id, ct);
        return MapPlayerReport(r);
    }

    public async Task<bool> DismissReportAsync(int reportId, int staffAccountId, string? comment,
        CancellationToken ct = default)
    {
        PlayerReport? r = await db.PlayerReports.FirstOrDefaultAsync(x => x.ID == reportId, ct);
        if (r == null) return false;
        r.Status = "DISMISSED";
        r.StaffNotes = comment;
        r.TimestampReviewed = TimeProvider.System.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> ActionReportAsync(ActionReportRequestDto request, CancellationToken ct = default)
    {
        PlayerReport? r = await db.PlayerReports.FirstOrDefaultAsync(x => x.ID == request.ReportId, ct);
        if (r == null) return false;
        r.Status = "RESOLVED";
        r.PunishmentType = request.ActionType;
        r.StaffNotes = request.Reason ?? request.StaffNotes;
        r.PunishmentDurationHours = request.DurationHours;
        r.TimestampReviewed = TimeProvider.System.GetUtcNow();
        if (request.DurationHours.HasValue && request.DurationHours.Value > 0)
        {
            r.PunishmentExpiration = TimeProvider.System.GetUtcNow().AddHours(request.DurationHours.Value);
        }

        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> DirectPunishAccountAsync(DirectPunishRequestDto request, CancellationToken ct = default)
    {
        Account? a =
            await db.Accounts.FirstOrDefaultAsync(acc => acc.Name.ToLower() == request.TargetAccountName.ToLower(), ct);
        if (a == null) return false;
        if (request.PunishmentType.Equals("BAN", StringComparison.OrdinalIgnoreCase))
        {
            a.BanReason = request.Reason;
            a.BanExpiration = request.DurationHours.HasValue
                ? TimeProvider.System.GetUtcNow().AddHours(request.DurationHours.Value)
                : TimeProvider.System.GetUtcNow().AddYears(10);
        }

        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> BanAccountAsync(int accountId, DateTimeOffset? expiresAt, string reason,
        int? bannedBy = null, CancellationToken ct = default)
    {
        Account? a = await db.Accounts.FirstOrDefaultAsync(acc => acc.ID == accountId, ct);
        if (a == null) return false;
        a.BanReason = reason;
        a.BanExpiration = expiresAt ?? TimeProvider.System.GetUtcNow().AddYears(10);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> UnbanAccountAsync(int accountId, CancellationToken ct = default)
    {
        Account? a = await db.Accounts.FirstOrDefaultAsync(acc => acc.ID == accountId, ct);
        if (a == null) return false;
        a.BanExpiration = null;
        a.BanReason = null;
        if (a.Type == AccountType.Disabled) a.Type = AccountType.Legacy;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<HostApplicationDto>> GetHostApplicationsAsync(CancellationToken ct = default)
    {
        List<HostApplication> apps = await db.HostApplications.Include(h => h.User).Include(h => h.MainAccount)
            .Include(h => h.HostAccount).ToListAsync(ct);
        return [.. apps.Select(MapHostAppNonNull)];
    }

    public async Task<bool> ApproveHostApplicationAsync(int applicationId, string? staffNotes = null,
        string? staffName = null, CancellationToken ct = default)
    {
        HostApplication? app = await db.HostApplications.Include(h => h.User).Include(h => h.MainAccount)
            .FirstOrDefaultAsync(h => h.ID == applicationId, ct);
        if (app == null) return false;
        app.Status = HostApplicationStatus.Approved;
        app.TimestampReviewed = TimeProvider.System.GetUtcNow();
        if (!string.IsNullOrEmpty(staffNotes)) app.StaffNotes = staffNotes;
        app.ReviewedByStaffName = staffName ?? "AdminStaff";

        string rawPass = "TEMP_SECURE_HOST_PWD_99";
        app.EncryptedHostPassword = HostSecurityService.EncryptPassword(rawPass);

        string hostEmail = $"host_{app.HostUsername.ToLower()}@nexus.local";
        User? hostUser = await db.Users.FirstOrDefaultAsync(u => u.EmailAddress == hostEmail, ct);
        if (hostUser == null)
        {
            Role? hostRole = await db.Roles.FirstOrDefaultAsync(r => r.Name == "ServerHost", ct)
                             ?? await db.Roles.FirstOrDefaultAsync(r => r.Name == UserRoles.User, ct)
                             ?? new Role { Name = "ServerHost" };
            hostUser = new User
            {
                EmailAddress = hostEmail,
                Role = hostRole,
                SRPPasswordSalt = "HOST_SALT_123",
                SRPPasswordHash = "HOST_HASH_123",
                PBKDF2PasswordHash = "HOST_PBKDF2_123"
            };
            db.Users.Add(hostUser);
            await db.SaveChangesAsync(ct);
        }

        Account? hostAccount =
            await db.Accounts.FirstOrDefaultAsync(a => a.Name.ToLower() == app.HostUsername.ToLower(), ct);
        if (hostAccount == null)
        {
            hostAccount = new Account
            {
                Name = app.HostUsername,
                User = hostUser,
                Type = AccountType.ServerHost,
                IsMain = false,
                OwnerAccountID = app.MainAccountID
            };
            db.Accounts.Add(hostAccount);
            await db.SaveChangesAsync(ct);
        }
        else
        {
            hostAccount.Type = AccountType.ServerHost;
            hostAccount.OwnerAccountID = app.MainAccountID;
            hostAccount.User = hostUser;
        }

        app.HostAccountID = hostAccount.ID;
        app.HostAccount = hostAccount;

        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> RejectHostApplicationAsync(int applicationId, string? reason,
        CancellationToken ct = default)
    {
        HostApplication? app = await db.HostApplications.FirstOrDefaultAsync(h => h.ID == applicationId, ct);
        if (app == null) return false;
        app.Status = HostApplicationStatus.Rejected;
        app.StaffNotes = reason;
        app.TimestampReviewed = TimeProvider.System.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> RevokeHostApplicationAsync(int applicationId, string? reason = null,
        CancellationToken ct = default)
    {
        HostApplication? app = await db.HostApplications.Include(h => h.HostAccount)
            .FirstOrDefaultAsync(h => h.ID == applicationId, ct);
        if (app == null) return false;
        app.Status = HostApplicationStatus.Revoked;
        app.StaffNotes = reason;
        app.TimestampReviewed = TimeProvider.System.GetUtcNow();
        app.HostAccount?.Type = AccountType.Disabled;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<StreamerApplicationDto>> GetStreamerApplicationsAsync(
        CancellationToken ct = default)
    {
        List<StreamerApplication> apps =
            await db.StreamerApplications.Include(s => s.User).Include(s => s.MainAccount).ToListAsync(ct);
        return [.. apps.Select(MapStreamerAppNonNull)];
    }

    public async Task<bool> ApproveStreamerApplicationAsync(int applicationId, string? staffName = null,
        string? staffNotes = null, CancellationToken ct = default)
    {
        StreamerApplication? app = await db.StreamerApplications.Include(s => s.User).Include(s => s.MainAccount)
            .FirstOrDefaultAsync(s => s.ID == applicationId, ct);
        if (app == null) return false;
        app.Status = StreamerApplicationStatus.Approved;
        app.ReviewedByStaffName = staffName ?? "AdminStaff";
        app.StaffNotes = staffNotes;
        app.TimestampReviewed = TimeProvider.System.GetUtcNow();
        if (app.User != null)
        {
            app.User.TwitchUsername = app.TwitchUsername;
            app.User.TwitchId = app.TwitchId;
        }

        app.MainAccount?.Type = AccountType.Streamer;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> RejectStreamerApplicationAsync(int applicationId, string? reason,
        CancellationToken ct = default)
    {
        StreamerApplication? app = await db.StreamerApplications.FirstOrDefaultAsync(s => s.ID == applicationId, ct);
        if (app == null) return false;
        app.Status = StreamerApplicationStatus.Rejected;
        app.StaffNotes = reason;
        app.TimestampReviewed = TimeProvider.System.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> RevokeStreamerApplicationAsync(int applicationId, CancellationToken ct = default)
    {
        StreamerApplication? app = await db.StreamerApplications.Include(s => s.MainAccount)
            .FirstOrDefaultAsync(s => s.ID == applicationId, ct);
        if (app == null) return false;
        app.Status = StreamerApplicationStatus.Revoked;
        app.TimestampReviewed = TimeProvider.System.GetUtcNow();
        app.MainAccount?.Type = AccountType.Normal;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<AccountDto>> GetRecentAccountsAsync(int limit = 20, CancellationToken ct = default)
    {
        List<Account> accounts = await db.Accounts.Include(a => a.User).OrderByDescending(a => a.TimestampCreated)
            .Take(limit).ToListAsync(ct);
        return [.. accounts.Select(MapAccountNonNull)];
    }

    public async Task<AccountDto?> GetAccountDetailAsync(string accountName, CancellationToken ct = default)
    {
        Account? a = await db.Accounts.Include(a => a.User).ThenInclude(u => u.Role).Include(a => a.User)
            .ThenInclude(u => u.Accounts).FirstOrDefaultAsync(a => a.Name.ToLower() == accountName.ToLower(), ct);
        return MapAccount(a);
    }

    public async Task<bool> AddInventoryItemAsync(string accountName, string productCode,
        CancellationToken ct = default)
    {
        Account? a = _cachedAccount != null &&
                     string.Equals(_cachedAccount.Name, accountName, StringComparison.OrdinalIgnoreCase)
            ? _cachedAccount
            : await db.Accounts.Include(acc => acc.User).ThenInclude(u => u!.InventoryItems).Include(acc => acc.SelectedStoreItems)
                .FirstOrDefaultAsync(acc => acc.Name.ToLower() == accountName.ToLower(), ct);
        if (a == null) return false;
        _cachedAccount = a;
        if (a.User != null)
        {
            a.User.InventoryItems ??= [];
            if (!a.User.InventoryItems.Any(i => string.Equals(i.ItemCode, productCode, StringComparison.OrdinalIgnoreCase)))
            {
                a.User.InventoryItems.Add(new UserInventory
                {
                    UserId = a.User.ID,
                    ItemCode = productCode,
                    GrantSource = "Admin",
                    GrantedAtUtc = TimeProvider.System.GetUtcNow()
                });
            }
        }

        a.AddSelectedStoreItem(productCode);

        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> AddInventoryItemsAsync(string accountName, IEnumerable<string> productCodes,
        CancellationToken ct = default)
    {
        Account? a = _cachedAccount != null &&
                     string.Equals(_cachedAccount.Name, accountName, StringComparison.OrdinalIgnoreCase)
            ? _cachedAccount
            : await db.Accounts.Include(acc => acc.User).ThenInclude(u => u!.InventoryItems).Include(acc => acc.SelectedStoreItems)
                .FirstOrDefaultAsync(acc => acc.Name.ToLower() == accountName.ToLower(), ct);
        if (a == null) return false;
        _cachedAccount = a;
        if (a.User != null)
        {
            a.User.InventoryItems ??= [];
            HashSet<string> owned = new(a.User.InventoryItems.Select(i => i.ItemCode), StringComparer.OrdinalIgnoreCase);
            foreach (string code in productCodes)
            {
                if (owned.Add(code))
                {
                    a.User.InventoryItems.Add(new UserInventory
                    {
                        UserId = a.User.ID,
                        ItemCode = code,
                        GrantSource = "Admin",
                        GrantedAtUtc = TimeProvider.System.GetUtcNow()
                    });
                }
            }
        }

        foreach (string code in productCodes)
        {
            a.AddSelectedStoreItem(code);
        }

        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> RemoveInventoryItemAsync(string accountName, string productCode,
        CancellationToken ct = default)
    {
        Account? a = await db.Accounts.Include(acc => acc.User).ThenInclude(u => u!.InventoryItems).Include(acc => acc.SelectedStoreItems)
            .FirstOrDefaultAsync(acc => acc.Name.ToLower() == accountName.ToLower(), ct);
        if (a == null) return false;
        if (a.User != null)
        {
            UserInventory? existing = a.User.InventoryItems?.FirstOrDefault(i => string.Equals(i.ItemCode, productCode, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                a.User.InventoryItems?.Remove(existing);
                db.UserInventories.Remove(existing);
            }
        }

        a.RemoveSelectedStoreItem(productCode);

        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> ResetInventoryAsync(string accountName, CancellationToken ct = default)
    {
        Account? a = await db.Accounts.Include(acc => acc.User).ThenInclude(u => u!.InventoryItems).Include(acc => acc.SelectedStoreItems)
            .FirstOrDefaultAsync(acc => acc.Name.ToLower() == accountName.ToLower(), ct);
        if (a == null) return false;
        if (a.User?.InventoryItems != null)
        {
            db.UserInventories.RemoveRange(a.User.InventoryItems);
            a.User.InventoryItems.Clear();
        }
        a.SelectedStoreItems?.Clear();
        await db.SaveChangesAsync(ct);
        return true;
    }

    public Task<InviteSystemStatusDto?> GetInviteSystemStatusAsync(CancellationToken ct = default)
    {
        return Task.FromResult<InviteSystemStatusDto?>(new InviteSystemStatusDto(false, 0, 0, []));
    }

    public Task<bool> ToggleInviteOnlyModeAsync(bool enable, string? notes, int staffAccountId, string staffAccountName, CancellationToken ct = default)
    {
        return Task.FromResult(true);
    }

    public Task<InviteTreeNodeDto?> GetInviteLineageTreeAsync(int userId, CancellationToken ct = default)
    {
        return Task.FromResult<InviteTreeNodeDto?>(new InviteTreeNodeDto(userId, "TestUser", null, "TestUser", 1, TimeProvider.System.GetUtcNow(), false, null, 0, null, []));
    }

    public Task<bool> PruneInviteSubtreeAsync(int rootUserId, string reason, bool banAccounts = true, CancellationToken ct = default)
    {
        return Task.FromResult(true);
    }
}

internal class TestPlayerStatsApiClient(MerrickContext db) : IPlayerStatsApiClient
{
    private static readonly Lazy<List<HeroItem>> FallbackHeroItems = new(() =>
    {
        string[] searchPaths =
        [
            Path.Combine(AppContext.BaseDirectory, "Data", "HeroItems.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "Data", "HeroItems.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "src", "libs", "database-context", "Data", "HeroItems.json")
        ];

        foreach (string p in searchPaths)
        {
            if (File.Exists(p))
            {
                try
                {
                    string json = File.ReadAllText(p);
                    List<HeroItem>? parsed = JsonSerializer.Deserialize<List<HeroItem>>(json,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (parsed != null && parsed.Count > 0)
                    {
                        return parsed;
                    }
                }
                catch
                {
                }
            }
        }

        return [];
    });

    public async Task<PlayerProfileSummaryDto?> GetPlayerProfileAsync(string ign, CancellationToken ct = default)
    {
        Account? acc = await db.Accounts.Include(a => a.User).Include(a => a.Statistics)
            .FirstOrDefaultAsync(a => a.Name.ToLower() == ign.ToLower(), ct);
        if (acc == null) return null;
        User user = acc.User;

        AccountStatistics? ranked = acc.Statistics.FirstOrDefault(s => s.Type == AccountStatisticsType.Matchmaking);
        AccountStatistics? pub = acc.Statistics.FirstOrDefault(s => s.Type == AccountStatisticsType.Public || s.Type == AccountStatisticsType.Player);

        List<PlayerStatistics> rawPlayerStats = await db.PlayerStatistics
            .Where(ps => ps.AccountID == acc.ID)
            .ToListAsync(ct);

        PlayerStatsAggregateDto? agg;
        if (rawPlayerStats.Count > 0)
        {
            agg = new PlayerStatsAggregateDto(
                rawPlayerStats.Count,
                rawPlayerStats.Sum(p => p.HeroKills),
                rawPlayerStats.Sum(p => p.HeroDeaths),
                rawPlayerStats.Sum(p => p.HeroAssists),
                rawPlayerStats.Sum(p => p.TeamCreepKills + p.NeutralCreepKills),
                0, 0, 0, 0,
                rawPlayerStats.Sum(p => p.HeroDamage),
                rawPlayerStats.Sum(p => p.BuildingDamage),
                rawPlayerStats.Count(p => p.Conceded > 0),
                acc.ID
            );
        }
        else if (ranked != null)
        {
            agg = new PlayerStatsAggregateDto(
                ranked.MatchesPlayed, ranked.AwardMostKills ?? 0, ranked.MatchesLost, ranked.AwardMostAssists ?? 0,
                ranked.AwardMostCreepKills ?? 0, 0, 0, 0, 0,
                ranked.AwardMostHeroDamageDealt ?? 0, ranked.AwardMostBuildingDamage ?? 0,
                ranked.MatchesConceded, acc.ID
            );
        }
        else
        {
            agg = null;
        }

        List<PlayerHeroCountDto> topHeroes =
        [
            .. rawPlayerStats
                .GroupBy(ps => ps.HeroIdentifier)
                .Select(g => new PlayerHeroCountDto(
                    g.Key.Replace("Hero_", "", StringComparison.OrdinalIgnoreCase),
                    g.Count(),
                    g.Sum(p => p.HeroKills),
                    g.Sum(p => p.HeroDeaths),
                    g.Sum(p => p.HeroAssists),
                    g.Count(p => p.Win > 0)
                ))
                .OrderByDescending(h => h.MatchesPlayed)
                .Take(5)
        ];

        List<int> recentMatchIds = await db.PlayerStatistics
            .Where(ps => ps.AccountID == acc.ID)
            .OrderByDescending(ps => ps.MatchID)
            .Select(ps => ps.MatchID)
            .Take(10)
            .ToListAsync(ct);

        List<MatchStatistics> recentMatches = await db.MatchStatistics
            .Where(m => recentMatchIds.Contains(m.MatchID))
            .OrderByDescending(m => m.MatchID)
            .ToListAsync(ct);

        List<int> allUserAccountIds = (user.Accounts != null && user.Accounts.Count > 0)
            ? [.. user.Accounts.Select(a => a.ID)]
            : [acc.ID];
        List<string> allUserAccountNames = (user.Accounts != null && user.Accounts.Count > 0)
            ? [.. user.Accounts.Select(a => a.Name.ToLower())]
            : [ign.ToLower()];

        List<PlayerReport> recentReports = await db.PlayerReports
            .Where(r => (r.ReportedAccountID.HasValue && allUserAccountIds.Contains(r.ReportedAccountID.Value)) ||
                        allUserAccountNames.Contains(r.ReportedAccountName.ToLower()))
            .OrderByDescending(r => r.TimestampCreated)
            .Take(10)
            .ToListAsync(ct);

        StreamerApplication? streamerApp =
            await db.StreamerApplications.FirstOrDefaultAsync(
                s => s.UserID == user.ID && s.Status == StreamerApplicationStatus.Approved, ct);
        HostApplication? primaryHostApp =
            await db.HostApplications.FirstOrDefaultAsync(
                h => h.UserID == user.ID && h.Status == HostApplicationStatus.Approved, ct);

        List<UserWebReward> equippedRewards =
            await db.UserWebRewards.Where(r => r.UserID == user.ID && r.IsEquipped).ToListAsync(ct);

        List<Account> allLinkedAccounts = user.Accounts?.ToList() ?? [acc];
        List<HostApplication> hostApps = await db.HostApplications
            .Where(h => h.UserID == user.ID || h.MainAccountID == acc.ID).ToListAsync(ct);

        List<string> linkedHostNames =
        [
            .. allLinkedAccounts
                .Where(a => a.Type == AccountType.ServerHost)
                .Select(a => a.Name)
                .Union(hostApps.Where(h => h.Status == HostApplicationStatus.Approved).Select(h => h.HostUsername))
                .Distinct(StringComparer.OrdinalIgnoreCase)
        ];

        List<Account> playerAccounts = [.. allLinkedAccounts.Where(a => a.Type != AccountType.ServerHost)];
        List<int> linkedPlayerAccountIds = [.. playerAccounts.Select(a => a.ID).Distinct()];
        List<string> linkedPlayerAccountNames = [.. playerAccounts.Select(a => a.Name).Distinct()];

        if (acc.OwnerAccountID.HasValue && !linkedPlayerAccountIds.Contains(acc.OwnerAccountID.Value))
            linkedPlayerAccountIds.Add(acc.OwnerAccountID.Value);

        if (linkedPlayerAccountIds.Count == 0 && acc.Type != AccountType.ServerHost)
        {
            linkedPlayerAccountIds.Add(acc.ID);
            linkedPlayerAccountNames.Add(acc.Name);
        }

        bool hasLinkedHostAccounts =
            linkedHostNames.Count > 0 || hostApps.Any(h => h.Status == HostApplicationStatus.Approved);
        bool hasPendingHostApp = hostApps.Any(h => h.Status == HostApplicationStatus.Pending);
        string primaryHostUsername = primaryHostApp?.HostUsername ?? (acc.Type == AccountType.ServerHost
            ? acc.Name
            : linkedHostNames.FirstOrDefault() ?? $"{acc.Name}_host");

        int gamesHosted = 0;
        if (linkedHostNames.Count > 0)
        {
            gamesHosted = await db.MatchStatistics.Where(m => linkedHostNames.Contains(m.HostAccountName))
                .CountAsync(ct);
        }

        int totalPlayed = 0;
        if (linkedPlayerAccountIds.Count > 0)
        {
            totalPlayed = await db.PlayerStatistics.Where(ps => linkedPlayerAccountIds.Contains(ps.AccountID))
                .CountAsync(ct);
            if (totalPlayed == 0)
            {
                totalPlayed = playerAccounts.Sum(a =>
                    a.Statistics
                        .FirstOrDefault(s =>
                            s.Type is AccountStatisticsType.Matchmaking or AccountStatisticsType.Player or AccountStatisticsType.Public)
                        ?.MatchesPlayed ?? 0);
            }
        }

        double hostingRatio = gamesHosted > 0 ? Math.Round((double)gamesHosted / Math.Max(1, totalPlayed), 2) : 0.0;
        double communityContribution = 0.0;
        if (gamesHosted > 0)
        {
            int totalCommunityMatches = await db.MatchStatistics.CountAsync(ct);
            if (totalCommunityMatches > 0)
            {
                communityContribution = Math.Round((double)gamesHosted / totalCommunityMatches * 100.0, 1);
            }
        }

        string? hostBadgeTier = null;
        if (hasLinkedHostAccounts || acc.Type == AccountType.ServerHost || gamesHosted > 0)
        {
            hostBadgeTier = gamesHosted switch
            {
                >= 250 => "Patron of Newerth",
                _ when hostingRatio >= 2.0 && gamesHosted >= 50 => "Patron of Newerth",
                >= 100 => "Master Host",
                _ when hostingRatio >= 1.0 && gamesHosted >= 25 => "Master Host",
                >= 25 => "Dedicated Host",
                _ when hostingRatio >= 0.5 && gamesHosted >= 10 => "Dedicated Host",
                _ => "Community Host"
            };
        }

        HostTelemetryResultDto hostTel = new(
            hasLinkedHostAccounts,
            hasPendingHostApp,
            linkedHostNames,
            linkedPlayerAccountIds,
            linkedPlayerAccountNames,
            gamesHosted,
            totalPlayed,
            hostingRatio,
            communityContribution,
            hostBadgeTier,
            MapHostApp(primaryHostApp),
            primaryHostUsername
        );

        return new PlayerProfileSummaryDto(
            MapAccount(acc)!,
            MapUser(user)!,
            MapAccountStats(ranked),
            MapAccountStats(pub),
            agg,
            topHeroes,
            [.. recentMatches.Select(MapMatchStatsNonNull)],
            [.. recentReports.Select(MapPlayerReportNonNull)],
            MapStreamerApp(streamerApp),
            MapHostApp(primaryHostApp),
            hostTel,
            [.. equippedRewards.Select(MapUserWebRewardNonNull)]
        );
    }

    public async Task<AccountStatisticsDto?> GetAccountStatisticsAsync(int accountId, CancellationToken ct = default)
    {
        AccountStatistics? stat =
            await db.AccountStatistics.FirstOrDefaultAsync(
                s => s.AccountID == accountId && s.Type == AccountStatisticsType.Matchmaking, ct);
        return MapAccountStats(stat);
    }

    public async Task<IReadOnlyList<AccountStatisticsDto>> GetTopRankedAccountsAsync(int count = 100,
        CancellationToken ct = default)
    {
        List<AccountStatistics> list = await db.AccountStatistics.Include(s => s.Account)
            .Where(s => s.Type == AccountStatisticsType.Matchmaking).OrderByDescending(s => s.SkillRating).Take(count)
            .ToListAsync(ct);
        return [.. list.Select(MapAccountStatsNonNull)];
    }

    public async Task<LeaderboardResultDto> GetLeaderboardAsync(int page = 1, int pageSize = 50, string? search = null,
        string sort = "winrate", string? mode = null, string? map = null, CancellationToken ct = default)
    {
        List<Account> allAccounts = await db.Accounts.Include(a => a.Statistics)
            .Where(a => a.Type != AccountType.ServerHost && a.Type != AccountType.Disabled).ToListAsync(ct);
        if (!string.IsNullOrWhiteSpace(search))
        {
            string s = search.Trim();
            allAccounts = [.. allAccounts.Where(a => a.Name.Contains(s, StringComparison.OrdinalIgnoreCase))];
        }

        AccountStatisticsType targetStatType = (mode ?? "").ToLowerInvariant() switch
        {
            "public" => AccountStatisticsType.Public,
            _ => AccountStatisticsType.Matchmaking
        };

        List<LeaderboardEntryDto> allEntries = [];
        List<int> accountIds = [.. allAccounts.Select(a => a.ID)];
        IQueryable<PlayerStatistics> psQuery = db.PlayerStatistics.Where(p => accountIds.Contains(p.AccountID));
        if (!string.IsNullOrWhiteSpace(map) && !map.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            string cleanMap = map.Trim().ToLowerInvariant();
            if (cleanMap.EndsWith("_ranked")) cleanMap = cleanMap[..^7];
            else if (cleanMap.EndsWith("_public")) cleanMap = cleanMap[..^7];
            psQuery = psQuery.Where(ps => ps.Match != null && ps.Match.Map.ToLower().Contains(cleanMap));
        }

        List<PlayerStatistics> allPlayerStats = await psQuery.ToListAsync(ct);
        Dictionary<int, List<PlayerStatistics>> statsByAccount =
            allPlayerStats.GroupBy(p => p.AccountID).ToDictionary(g => g.Key, g => g.ToList());

        foreach (Account a in allAccounts)
        {
            AccountStatistics? s = a.Statistics.FirstOrDefault(s => s.Type == targetStatType);
            int wins = s?.MatchesWon ?? 0;
            int losses = s?.MatchesLost ?? 0;
            int kills = s?.AwardMostKills ?? 0;
            int deaths = s?.MatchesLost ?? 0;
            int assists = s?.AwardMostAssists ?? 0;
            int totalGames = wins + losses;

            List<PlayerStatistics> pStats = statsByAccount.TryGetValue(a.ID, out List<PlayerStatistics>? pList)
                ? pList
                : [];

            if (totalGames == 0)
            {
                if (pStats.Count > 0)
                {
                    wins = pStats.Count(p => p.Win > 0);
                    losses = pStats.Count(p => p.Win == 0);
                    totalGames = pStats.Count;
                    kills = pStats.Sum(p => p.HeroKills);
                    deaths = pStats.Sum(p => p.HeroDeaths);
                    assists = pStats.Sum(p => p.HeroAssists);
                }
            }

            double winrate = totalGames > 0 ? Math.Round((double)wins / totalGames * 100, 1) : 0.0;
            double kdr = deaths > 0 ? Math.Round((double)kills / deaths, 2) : kills;

            string? favoriteHero = pStats.Count > 0
                ? pStats.GroupBy(p => p.HeroIdentifier).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key
                : null;
            if (!string.IsNullOrEmpty(favoriteHero))
            {
                favoriteHero = favoriteHero.Replace("Hero_", "", StringComparison.OrdinalIgnoreCase);
            }

            allEntries.Add(new LeaderboardEntryDto(0, a.ID, a.Name, (int)(s?.SkillRating ?? 1500), wins, losses,
                winrate, kills, deaths, assists, kdr, favoriteHero));
        }

        if (sort.Equals("matches", StringComparison.OrdinalIgnoreCase))
        {
            allEntries =
            [
                .. allEntries.OrderByDescending(e => (e.Wins + e.Losses)).ThenByDescending(e => e.WinRate)
                    .ThenBy(e => e.AccountName)
            ];
        }
        else if (sort.Equals("rating", StringComparison.OrdinalIgnoreCase) ||
                 sort.Equals("mmr", StringComparison.OrdinalIgnoreCase))
        {
            allEntries =
            [
                .. allEntries.OrderByDescending(e => e.Rating).ThenByDescending(e => e.WinRate)
                    .ThenBy(e => e.AccountName)
            ];
        }
        else
        {
            allEntries =
            [
                .. allEntries.OrderByDescending(e => e.WinRate).ThenByDescending(e => (e.Wins + e.Losses))
                    .ThenBy(e => e.AccountName)
            ];
        }

        for (int i = 0; i < allEntries.Count; i++)
        {
            allEntries[i] = allEntries[i] with { Rank = i + 1 };
        }

        int total = allEntries.Count;
        int totalPages = (int)Math.Ceiling(total / (double)pageSize);
        List<LeaderboardEntryDto> pagedEntries = [.. allEntries.Skip((page - 1) * pageSize).Take(pageSize)];
        return new LeaderboardResultDto(pagedEntries, total, page, totalPages);
    }

    public async Task<IReadOnlyList<HostLeaderboardEntryDto>> GetHostLeaderboardAsync(CancellationToken ct = default)
    {
        List<HostApplication> approvedApps = await db.HostApplications
            .Include(a => a.User)
            .ThenInclude(u => u.Accounts)
            .Include(a => a.MainAccount)
            .Include(a => a.HostAccount)
            .Where(a => a.Status == HostApplicationStatus.Approved)
            .ToListAsync(ct);

        List<Account> serverHostAccounts = await db.Accounts
            .Include(a => a.User)
            .ThenInclude(u => u.Accounts)
            .Where(a => a.Type == AccountType.ServerHost)
            .ToListAsync(ct);

        int totalMatchesHosted = await db.MatchStatistics.CountAsync(ct);

        List<User> allUsers =
        [
            .. approvedApps.Select(a => a.User)
                .Concat(serverHostAccounts.Select(s => s.User))
                .Where(u => u != null)
                .GroupBy(u => u.ID)
                .Select(g => g.First())
        ];

        List<HostLeaderboardEntryDto> list = [];

        foreach (User user in allUsers)
        {
            List<HostApplication> userApps = [.. approvedApps.Where(a => a.UserID == user.ID)];
            List<Account> userHostAccounts = user.Accounts != null
                ? [.. user.Accounts.Where(a => a.Type == AccountType.ServerHost)]
                : [.. serverHostAccounts.Where(a => a.User != null && a.User.ID == user.ID)];

            List<string> hostNames =
            [
                .. userApps.Select(a => a.HostUsername)
                    .Concat(userHostAccounts.Select(a => a.Name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
            ];

            if (hostNames.Count == 0) continue;

            HostApplication? primaryApp = userApps.FirstOrDefault();
            Account? mainPlayerAcc = user.Accounts?.FirstOrDefault(a => a.IsMain && a.Type != AccountType.ServerHost)
                                     ?? user.Accounts?.FirstOrDefault(a => a.Type != AccountType.ServerHost)
                                     ?? await db.Accounts.FirstOrDefaultAsync(
                                         a => a.User != null && a.User.ID == user.ID &&
                                              a.Type != AccountType.ServerHost, ct);

            string displayName = primaryApp?.HostUsername
                                 ?? mainPlayerAcc?.Name
                                 ?? hostNames.FirstOrDefault()
                                 ?? "Host";

            List<int> playerAccIds = user.Accounts != null
                ? [.. user.Accounts.Where(a => a.Type != AccountType.ServerHost).Select(a => a.ID)]
                : (mainPlayerAcc != null ? [mainPlayerAcc.ID] : []);

            int hosted = await db.MatchStatistics.Where(m => hostNames.Contains(m.HostAccountName)).CountAsync(ct);
            int played = playerAccIds.Count > 0
                ? await db.PlayerStatistics.Where(ps => playerAccIds.Contains(ps.AccountID)).CountAsync(ct)
                : 0;

            if (hosted == 0 && primaryApp == null) continue;

            double ratio = played > 0 ? Math.Round((double)hosted / played, 2) : (hosted > 0 ? hosted : 0.0);
            double contrib = totalMatchesHosted > 0 ? Math.Round((double)hosted / totalMatchesHosted * 100.0, 1) : 0.0;

            string tier = hosted switch
            {
                >= 250 => "Patron of Newerth",
                _ when ratio >= 2.0 && hosted >= 50 => "Patron of Newerth",
                >= 100 => "Master Host",
                _ when ratio >= 1.0 && hosted >= 25 => "Master Host",
                >= 25 => "Dedicated Host",
                _ when ratio >= 0.5 && hosted >= 10 => "Dedicated Host",
                _ => "Community Host"
            };

            list.Add(new HostLeaderboardEntryDto
            {
                Rank = 0,
                AccountId = mainPlayerAcc?.ID ?? user.ID,
                AccountName = displayName,
                HostAccountName = displayName,
                AvatarUrl = "/images/default-avatar.png",
                GamesHosted = hosted,
                GamesPlayed = played,
                HostingRatio = ratio,
                CommunityContributionPercentage = contrib,
                HostBadgeTier = tier,
                Location = primaryApp?.Location ?? "USE",
                Instances = primaryApp?.Instances ?? 2
            });
        }

        list =
        [
            .. list
                .OrderByDescending(h => h.HostingRatio)
                .ThenByDescending(h => h.GamesHosted)
        ];

        for (int i = 0; i < list.Count; i++)
        {
            list[i].Rank = i + 1;
        }

        return list;
    }

    public async Task<IReadOnlyList<MatchStatisticsDto>> GetRecentMatchesAsync(int limit = 50,
        CancellationToken ct = default)
    {
        List<MatchStatistics> matches =
            await db.MatchStatistics.OrderByDescending(m => m.MatchID).Take(limit).ToListAsync(ct);
        return [.. matches.Select(MapMatchStatsNonNull)];
    }

    public async Task<IReadOnlyList<MatchStatisticsDto>> GetMatchesForAccountAsync(int accountId, int limit = 20,
        CancellationToken ct = default)
    {
        List<int> matchIds = await db.PlayerStatistics.Where(ps => ps.AccountID == accountId)
            .OrderByDescending(ps => ps.MatchID).Select(ps => ps.MatchID).Take(limit).ToListAsync(ct);
        List<MatchStatistics> matches = await db.MatchStatistics.Where(m => matchIds.Contains(m.MatchID))
            .OrderByDescending(m => m.MatchID).ToListAsync(ct);
        return [.. matches.Select(MapMatchStatsNonNull)];
    }

    public async Task<MatchStatisticsDto?> GetMatchByIdAsync(int matchId, CancellationToken ct = default)
    {
        MatchStatistics? m = await db.MatchStatistics.FirstOrDefaultAsync(m => m.MatchID == matchId, ct);
        return MapMatchStats(m);
    }

    public async Task<IReadOnlyList<PlayerStatisticsDto>> GetPlayerStatisticsForMatchAsync(int matchId,
        CancellationToken ct = default)
    {
        List<PlayerStatistics> stats = await db.PlayerStatistics.Where(p => p.MatchID == matchId).ToListAsync(ct);
        return [.. stats.Select(MapPlayerStatsNonNull)];
    }

    public async Task<IReadOnlyList<PlayerStatisticsDto>> GetRecentPlayerStatisticsAsync(int accountId, int limit = 20,
        CancellationToken ct = default)
    {
        List<PlayerStatistics> stats = await db.PlayerStatistics.Where(p => p.AccountID == accountId)
            .OrderByDescending(p => p.MatchID).Take(limit).ToListAsync(ct);
        return [.. stats.Select(MapPlayerStatsNonNull)];
    }

    public async Task<IReadOnlyList<int>> GetSubmittedMatchIdsAsync(IEnumerable<int> matchIds,
        CancellationToken ct = default)
    {
        List<int> ids = [.. matchIds];
        return await db.MatchStatistics.Where(m => ids.Contains(m.MatchID)).Select(m => m.MatchID).ToListAsync(ct);
    }

    public async Task<MatchEvidenceDto?> GetMatchEvidenceAsync(int matchId, CancellationToken ct = default) =>
        MapMatchEvidence(await db.MatchEvidence.FirstOrDefaultAsync(e => e.MatchID == matchId, ct));

    public async Task<bool> SaveMatchEvidenceAsync(MatchEvidenceDto evidence, CancellationToken ct = default)
    {
        MatchEvidence? existing = await db.MatchEvidence.FirstOrDefaultAsync(e => e.MatchID == evidence.MatchID, ct);
        if (existing == null)
        {
            existing = new MatchEvidence { MatchID = evidence.MatchID };
            db.MatchEvidence.Add(existing);
        }

        existing.HasReplay = evidence.HasReplay;
        existing.HasVoice = evidence.HasVoice;
        existing.ChatLogJson = evidence.ChatLogJson ?? "[]";
        existing.VoiceTrackLegionUrl = evidence.VoiceTrackLegionUrl;
        existing.VoiceTrackHellbourneUrl = evidence.VoiceTrackHellbourneUrl;
        existing.TimestampExtracted = evidence.TimestampExtracted ?? TimeProvider.System.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<ServerHostDto>> GetServerHostsAsync(CancellationToken ct = default)
    {
        List<Account> hosts = await db.Accounts.Where(a => a.Type == AccountType.ServerHost).ToListAsync(ct);
        return
        [
            .. hosts.Select(h =>
                new ServerHostDto(h.ID, h.Name, "USE", 10, TimeProvider.System.GetUtcNow(), "Gold", true))
        ];
    }

    public async Task<IReadOnlyList<HeroDetailDto>> GetAllHeroDetailsAsync(CancellationToken ct = default) =>
        [.. (await db.HeroDetails.ToListAsync(ct)).Select(MapHeroDetailNonNull)];

    public async Task<HeroDetailDto?> GetHeroDetailAsync(string heroId, CancellationToken ct = default) =>
        MapHeroDetail(
            await db.HeroDetails.FirstOrDefaultAsync(
                h => h.HeroCode == heroId || h.DisplayName.ToLower() == heroId.ToLower(), ct));

    public async Task<IReadOnlyList<HeroItemDto>> GetAllHeroItemsAsync(CancellationToken ct = default)
    {
        List<HeroItem> items = await db.HeroItems.ToListAsync(ct);
        if (items.Count == 0)
        {
            items = FallbackHeroItems.Value;
        }

        return [.. items.Select(MapHeroItemNonNull)];
    }

    public async Task<bool> SubmitPlayerReportAsync(PlayerReportDto report, CancellationToken ct = default)
    {
        PlayerReport entity = new()
        {
            ReporterAccountID = report.ReporterAccountID,
            ReporterAccountName = report.ReporterAccountName ?? "Unknown",
            ReportedAccountID = report.ReportedAccountID,
            ReportedAccountName = report.ReportedAccountName ?? "Unknown",
            MatchID = report.MatchID,
            Reason = report.Reason,
            Description = report.Description,
            Status = report.Status,
            TimestampCreated = report.TimestampCreated != default
                ? report.TimestampCreated
                : TimeProvider.System.GetUtcNow()
        };
        db.PlayerReports.Add(entity);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<string>> GetApprovedStreamerLoginsAsync(CancellationToken ct = default) =>
        await db.StreamerApplications.Where(s => s.Status == StreamerApplicationStatus.Approved)
            .Select(s => s.TwitchUsername).ToListAsync(ct);

    public Task<LiveTelemetryDataDto> GetLiveTelemetryAsync(CancellationToken ct = default) =>
        Task.FromResult(new LiveTelemetryDataDto(0, 0, 0, [], TimeProvider.System.GetUtcNow()));

    public async Task<IReadOnlyList<AccountDto>> SearchAccountsAsync(string? query, int page = 1, int pageSize = 50,
        CancellationToken ct = default)
    {
        List<Account> allAccounts = await db.Accounts.Include(a => a.User).ToListAsync(ct);
        if (!string.IsNullOrWhiteSpace(query))
        {
            string s = query.Trim();
            allAccounts =
            [
                .. allAccounts.Where(a => a.Name.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                                          (a.User != null &&
                                           (a.User.EmailAddress.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                                            (a.User.DiscordUsername != null &&
                                             a.User.DiscordUsername.Contains(s, StringComparison.OrdinalIgnoreCase)) ||
                                            (a.User.DiscordId != null &&
                                             a.User.DiscordId.Contains(s, StringComparison.OrdinalIgnoreCase)))))
            ];
        }

        List<Account> accounts = [.. allAccounts.Skip((page - 1) * pageSize).Take(pageSize)];
        return [.. accounts.Select(MapAccountNonNull)];
    }

    public async Task<AccountDto?> GetAccountByIgnAsync(string ign, CancellationToken ct = default)
    {
        Account? a = await db.Accounts
            .Include(a => a.Clan)
            .Include(a => a.User).ThenInclude(u => u.Role)
            .Include(a => a.User).ThenInclude(u => u.Accounts)
            .FirstOrDefaultAsync(a => a.Name.ToLower() == ign.ToLower(), ct);
        return MapAccount(a);
    }

    public Task<int> GetTotalMatchesCountAsync(CancellationToken ct = default) => db.MatchStatistics.CountAsync(ct);

    public Task<NEXUS.Contracts.DTOs.ClanDirectoryResultDto> GetClanDirectoryAsync(
        int page = 1,
        int pageSize = 24,
        string? search = null,
        string sort = "mmr",
        CancellationToken ct = default)
    {
        global::ZORGATH.WebPortal.API.Features.PlayerStats.GetClanDirectoryHandler handler = new(db);
        return handler.Handle(new NEXUS.Contracts.CQRS.PlayerStats.GetClanDirectoryQuery(page, pageSize, search, sort), ct);
    }

    public Task<NEXUS.Contracts.DTOs.ClanDetailDto?> GetClanDetailAsync(int clanId, CancellationToken ct = default)
    {
        global::ZORGATH.WebPortal.API.Features.PlayerStats.GetClanDetailHandler handler = new(db);
        return handler.Handle(new NEXUS.Contracts.CQRS.PlayerStats.GetClanDetailQuery(clanId), ct);
    }
}

internal class TestStoreApiClient(MerrickContext db) : IStoreApiClient
{
    private static readonly StaffStoreService SharedStaffStoreService =
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Store:UnlockAllItems"] = "false" }).Build(), NullLogger<StaffStoreService>.Instance);

    public async Task<StaffStoreViewModelDto> GetCatalogAsync(string? category, string? search, int page = 1,
        int pageSize = 12, IReadOnlyCollection<string>? userOwnedCodes = null, string? ownershipFilter = "all",
        IReadOnlyCollection<string>? userEquippedCodes = null, CancellationToken ct = default)
    {
        List<StoreItem> items = await db.StoreItems.ToListAsync(ct);
        List<StaffStoreItemDto> dtos =
        [
            .. items.Select(i => new StaffStoreItemDto
            {
                Id = i.Id,
                Name = i.Name,
                Code = i.Code,
                GoldCost = i.PriceInMMP,
                SilverCost = i.Price,
                IsEnabled = i.Enabled,
                Purchasable = i.Purchasable,
                Resource = i.LocalContent,
                Type = i.UpgradeType,
                TypeName = i.UpgradeType.ToString(),
                IsOwnedByUser = userOwnedCodes?.Contains(i.Code) ?? false,
                IsEquippedByUser = userEquippedCodes?.Contains(i.Code) ?? false
            })
        ];
        return new StaffStoreViewModelDto(dtos, dtos.Count, page, (int)Math.Ceiling(dtos.Count / (double)pageSize),
            pageSize, category, search, ownershipFilter);
    }

    public async Task<StaffStoreItemDto?> GetItemByIdAsync(int id, CancellationToken ct = default)
    {
        StoreItem? item = await db.StoreItems.FirstOrDefaultAsync(i => i.Id == id, ct);
        if (item == null) return null;
        return new StaffStoreItemDto
        {
            Id = item.Id,
            Name = item.Name,
            Code = item.Code,
            GoldCost = item.PriceInMMP,
            SilverCost = item.Price,
            IsEnabled = item.Enabled,
            Purchasable = item.Purchasable,
            Resource = item.LocalContent,
            Type = item.UpgradeType,
            TypeName = item.UpgradeType.ToString()
        };
    }

    public async Task<IReadOnlyList<StaffStoreItemDto>> GetItemsByHeroAsync(string heroName,
        CancellationToken ct = default)
    {
        List<StoreItem> items = await db.StoreItems.Where(i => i.Name.Contains(heroName)).ToListAsync(ct);
        return
        [
            .. items.Select(i => new StaffStoreItemDto
            {
                Id = i.Id,
                Name = i.Name,
                Code = i.Code,
                GoldCost = i.PriceInMMP,
                SilverCost = i.Price,
                IsEnabled = i.Enabled,
                Purchasable = i.Purchasable,
                Resource = i.LocalContent,
                Type = i.UpgradeType,
                TypeName = i.UpgradeType.ToString()
            })
        ];
    }

    public async Task<PurchaseItemResultDto> PurchaseItemAsync(int userId, int itemId, string currency,
        CancellationToken ct = default)
    {
        User? user = await db.Users.Include(u => u.InventoryItems).FirstOrDefaultAsync(u => u.ID == userId, ct);
        if (user == null) return new PurchaseItemResultDto(false, "User not found", 0, 0, itemId, "", "");

        StoreItem? dbItem = await db.StoreItems.FirstOrDefaultAsync(i => i.Id == itemId, ct);
        StaffStoreItemDto? serviceItem = null;
        try
        {
            UIStaffStoreItemDto? m = SharedStaffStoreService.ResolveItem(itemId.ToString());
            if (m != null)
                serviceItem = new StaffStoreItemDto
                {
                    Id = m.Id, Code = m.Code, Name = m.Name, TypeName = m.TypeName, Type = m.Type,
                    GoldCost = m.GoldCost, SilverCost = m.SilverCost, IsEnabled = m.IsEnabled,
                    Purchasable = m.Purchasable, Resource = m.Resource, HeroName = m.HeroName,
                    AltAvatarSuffix = m.AltAvatarSuffix, IsOwnedByUser = m.IsOwnedByUser,
                    IsEquippedByUser = m.IsEquippedByUser
                };
        }
        catch
        {
        }

        string code = dbItem?.Code ?? serviceItem?.Code ?? $"item_{itemId}";
        string name = dbItem?.Name ?? serviceItem?.Name ?? code;
        int goldCost = dbItem?.PriceInMMP ?? serviceItem?.GoldCost ?? 0;
        int silverCost = dbItem?.Price ?? serviceItem?.SilverCost ?? 0;

        if (currency.Equals("silver", StringComparison.OrdinalIgnoreCase))
        {
            if (silverCost <= 0)
                return new PurchaseItemResultDto(false, "Item cannot be purchased with silver.", user.GoldCoins,
                    user.SilverCoins, itemId, code, name);
            if (user.SilverCoins < silverCost)
                return new PurchaseItemResultDto(false, "Insufficient silver coins.", user.GoldCoins, user.SilverCoins,
                    itemId, code, name);
            user.SilverCoins -= silverCost;
        }
        else
        {
            if (goldCost <= 0)
                return new PurchaseItemResultDto(false, "Item cannot be purchased with gold.", user.GoldCoins,
                    user.SilverCoins, itemId, code, name);
            if (user.GoldCoins < goldCost)
                return new PurchaseItemResultDto(false, "Insufficient gold coins.", user.GoldCoins, user.SilverCoins,
                    itemId, code, name);
            user.GoldCoins -= goldCost;
        }

        user.InventoryItems ??= [];
        if (user.InventoryItems.Any(i => string.Equals(i.ItemCode, code, StringComparison.OrdinalIgnoreCase)))
        {
            return new PurchaseItemResultDto(false, "You already own this item.", user.GoldCoins, user.SilverCoins,
                itemId, code, name);
        }

        user.InventoryItems.Add(new UserInventory
        {
            UserId = user.ID,
            ItemCode = code,
            GrantSource = "StorePurchase",
            GrantedAtUtc = TimeProvider.System.GetUtcNow()
        });
        await db.SaveChangesAsync(ct);
        return new PurchaseItemResultDto(true, "Purchase successful", user.GoldCoins, user.SilverCoins, itemId, code,
            name);
    }

    public async Task<EquipItemResultDto> EquipItemAsync(string accountName, int itemId, string? code,
        CancellationToken ct = default)
    {
        Account? account = await db.Accounts.Include(a => a.User).ThenInclude(u => u!.InventoryItems).Include(a => a.SelectedStoreItems)
            .FirstOrDefaultAsync(a => a.Name.ToLower() == accountName.ToLower(), ct);
        if (account == null) return new EquipItemResultDto(false, "Account not found", code ?? "", []);

        StoreItem? dbItem = itemId > 0 ? await db.StoreItems.FirstOrDefaultAsync(i => i.Id == itemId, ct) : null;
        StaffStoreItemDto? serviceItem = null;
        if (itemId > 0)
        {
            try
            {
                UIStaffStoreItemDto? m = SharedStaffStoreService.ResolveItem(itemId.ToString());
                if (m != null)
                    serviceItem = new StaffStoreItemDto
                    {
                        Id = m.Id, Code = m.Code, Name = m.Name, TypeName = m.TypeName, Type = m.Type,
                        GoldCost = m.GoldCost, SilverCost = m.SilverCost, IsEnabled = m.IsEnabled,
                        Purchasable = m.Purchasable, Resource = m.Resource, HeroName = m.HeroName,
                        AltAvatarSuffix = m.AltAvatarSuffix, IsOwnedByUser = m.IsOwnedByUser,
                        IsEquippedByUser = m.IsEquippedByUser
                    };
            }
            catch
            {
            }
        }
        else if (!string.IsNullOrEmpty(code))
        {
            try
            {
                UIStaffStoreItemDto? m = SharedStaffStoreService.ResolveItem(code);
                if (m != null)
                    serviceItem = new StaffStoreItemDto
                    {
                        Id = m.Id, Code = m.Code, Name = m.Name, TypeName = m.TypeName, Type = m.Type,
                        GoldCost = m.GoldCost, SilverCost = m.SilverCost, IsEnabled = m.IsEnabled,
                        Purchasable = m.Purchasable, Resource = m.Resource, HeroName = m.HeroName,
                        AltAvatarSuffix = m.AltAvatarSuffix, IsOwnedByUser = m.IsOwnedByUser,
                        IsEquippedByUser = m.IsEquippedByUser
                    };
            }
            catch
            {
            }
        }

        string itemCode = code ?? dbItem?.Code ?? serviceItem?.Code ?? $"item_{itemId}";

        User? user = account.User;
        List<string> owned = user?.InventoryItems != null ? user.InventoryItems.Select(i => i.ItemCode).ToList() : [];
        bool isDefault = itemCode.EndsWith(".Default", StringComparison.OrdinalIgnoreCase) ||
                         itemCode.Equals("ai.default", StringComparison.OrdinalIgnoreCase);
        if (!isDefault && !owned.Any(o =>
                o.Equals(itemCode, StringComparison.OrdinalIgnoreCase) ||
                o.Contains(itemCode, StringComparison.OrdinalIgnoreCase)))
        {
            return new EquipItemResultDto(false, "Item not owned", itemCode,
                account.SelectedStoreItemCodes);
        }

        List<string> selected = account.SelectedStoreItemCodes;
        if (!selected.Remove(itemCode))
        {
            string prefix = itemCode.Contains('.') ? itemCode.Substring(0, itemCode.IndexOf('.')) : itemCode;
            selected.RemoveAll(s =>
                s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                s.Contains(prefix, StringComparison.OrdinalIgnoreCase));
            selected.Add(itemCode);
        }

        account.SetSelectedStoreItems(selected);
        await db.SaveChangesAsync(ct);
        return new EquipItemResultDto(true, "Success", itemCode, selected);
    }

    public async Task<bool> UpdateStoreItemAsync(UpdateStoreItemRequestDto request, CancellationToken ct = default)
    {
        StoreItem? item = await db.StoreItems.FirstOrDefaultAsync(i => i.Id == request.Id, ct);
        if (item == null) return false;
        item.PriceInMMP = request.GoldCost;
        item.Price = request.SilverCost;
        item.Enabled = request.IsEnabled;
        item.Purchasable = request.Purchasable;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<StaffStoreItemDto>> SearchStoreAsync(string query, CancellationToken ct = default)
    {
        List<StoreItem> items = await db.StoreItems.Where(i => i.Name.Contains(query) || i.Code.Contains(query))
            .ToListAsync(ct);
        return
        [
            .. items.Select(i => new StaffStoreItemDto
            {
                Id = i.Id,
                Name = i.Name,
                Code = i.Code,
                GoldCost = i.PriceInMMP,
                SilverCost = i.Price,
                IsEnabled = i.Enabled,
                Purchasable = i.Purchasable,
                Resource = i.LocalContent,
                Type = i.UpgradeType,
                TypeName = i.UpgradeType.ToString()
            })
        ];
    }

    public async Task<PlinkoStatusResponseDto> GetPlinkoStatusAsync(int userId, CancellationToken ct = default)
    {
        User? user = await db.Users.FirstOrDefaultAsync(u => u.ID == userId, ct);
        if (user == null) return new PlinkoStatusResponseDto(false, false, 0, 0, 0, 0, 10, 10, [], [], null, null);
        bool canClaimDaily = !user.LastPlinkoDailyClaimUtc.HasValue ||
                             user.LastPlinkoDailyClaimUtc.Value.Date < TimeProvider.System.GetUtcNow().Date;
        List<UserWebReward> rewards = await db.UserWebRewards.Where(r => r.UserID == userId).ToListAsync(ct);
        List<string> borders =
        [
            .. rewards.Where(r => r.RewardType.Equals("border", StringComparison.OrdinalIgnoreCase))
                .Select(r => r.RewardCode)
        ];
        List<string> titles =
        [
            .. rewards.Where(r => r.RewardType.Equals("title", StringComparison.OrdinalIgnoreCase))
                .Select(r => r.RewardCode)
        ];
        string? eqBorder = rewards
            .FirstOrDefault(r => r.RewardType.Equals("border", StringComparison.OrdinalIgnoreCase) && r.IsEquipped)
            ?.RewardCode;
        string? eqTitle = rewards
            .FirstOrDefault(r => r.RewardType.Equals("title", StringComparison.OrdinalIgnoreCase) && r.IsEquipped)
            ?.RewardCode;
        return new PlinkoStatusResponseDto(true, canClaimDaily, 0, user.PlinkoTickets, user.GoldCoins, user.SilverCoins,
            10, 10, borders, titles, eqBorder, eqTitle);
    }

    public async Task<PlinkoDropResultDto> PlinkoDropAsync(int userId, string currency, bool useDaily,
        CancellationToken ct = default)
    {
        User? user = await db.Users.FirstOrDefaultAsync(u => u.ID == userId, ct);
        if (user == null) return new PlinkoDropResultDto(false, "User not found", 0, "None", "", "", 0, 0, 0, 0, false);
        if (useDaily)
        {
            if (user.LastPlinkoDailyClaimUtc.HasValue && user.LastPlinkoDailyClaimUtc.Value.Date ==
                TimeProvider.System.GetUtcNow().UtcDateTime.Date)
            {
                return new PlinkoDropResultDto(false, "Daily drop already claimed today.", 0, "None", "", "", 0,
                    user.PlinkoTickets, user.GoldCoins, user.SilverCoins, false);
            }

            user.LastPlinkoDailyClaimUtc = TimeProvider.System.GetUtcNow();
        }
        else if (currency.Equals("tickets", StringComparison.OrdinalIgnoreCase))
        {
            if (user.PlinkoTickets < 1)
            {
                return new PlinkoDropResultDto(false, "Not enough tickets.", 0, "None", "", "", 0, user.PlinkoTickets,
                    user.GoldCoins, user.SilverCoins, false);
            }

            user.PlinkoTickets--;
        }

        user.SilverCoins += 50;
        await db.SaveChangesAsync(ct);
        return new PlinkoDropResultDto(true, "Success", 4, "Silver", "silver_50", "50 Silver", 50, user.PlinkoTickets,
            user.GoldCoins, user.SilverCoins, false);
    }

    public async Task<RedeemCodeResultDto> RedeemCodeAsync(int userId, string code, CancellationToken ct = default)
    {
        User? user = await db.Users.FirstOrDefaultAsync(u => u.ID == userId, ct);
        if (user == null) return new RedeemCodeResultDto(false, "User not found", 0, 0, []);
        return new RedeemCodeResultDto(true, "Code redeemed successfully", 100, 500, []);
    }

    public async Task<UserBalancesDto?> GetUserBalancesAsync(int userId, CancellationToken ct = default)
    {
        User? user = await db.Users.Include(u => u.InventoryItems).FirstOrDefaultAsync(u => u.ID == userId, ct);
        if (user == null) return null;
        return new UserBalancesDto(user.ID, user.GoldCoins, user.SilverCoins, user.PlinkoTickets,
            user.InventoryItems?.Select(i => i.ItemCode).ToList() ?? [], []);
    }

    public async Task<IReadOnlyList<StoreItemDto>> GetStoreCatalogEntitiesAsync(CancellationToken ct = default)
    {
        List<StoreItem> items = await db.StoreItems.ToListAsync(ct);
        return [.. items.Select(MapStoreItemNonNull)];
    }
}

internal class TestGuideApiClient(MerrickContext db) : IGuideApiClient
{
    public async Task<GuideSearchResultDto> GetGuidesAsync(GuideSearchQueryDto query, CancellationToken ct = default)
    {
        IQueryable<HeroGuide> q = db.HeroGuides
            .Include(g => g.Author)
            .Include(g => g.Items)
            .Include(g => g.Abilities)
            .AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Hero))
            q = q.Where(g => g.HeroIdentifier == query.Hero);
        if (!string.IsNullOrWhiteSpace(query.Map) && !query.Map.Equals("all", StringComparison.OrdinalIgnoreCase))
            q = q.Where(g => g.TargetMap.ToLower() == query.Map.ToLower());
        if (!string.IsNullOrWhiteSpace(query.Query))
            q = q.Where(g => g.Name.Contains(query.Query) || g.HeroName.Contains(query.Query));
        if (query.Sort != "admin")
            q = q.Where(g => g.Public);

        int total = await q.CountAsync(ct);
        int totalPages = (int)Math.Ceiling(total / (double)query.PageSize);
        List<HeroGuide> guides = await q.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync(ct);
        return new GuideSearchResultDto([.. guides.Select(MapHeroGuideNonNull)], total, totalPages, query.Page);
    }

    public async Task<HeroGuideDto?> GetGuideByIdAsync(int id, CancellationToken ct = default) =>
        MapHeroGuide(await db.HeroGuides
            .Include(g => g.Author)
            .Include(g => g.Items)
            .Include(g => g.Abilities)
            .FirstOrDefaultAsync(g => g.ID == id, ct));

    public async Task<SaveGuideResultDto> SaveGuideAsync(SaveHeroGuideRequestDto request, string authorAccountName,
        bool isStaff, CancellationToken ct = default)
    {
        Account? author =
            await db.Accounts.FirstOrDefaultAsync(a => a.Name.ToLower() == authorAccountName.ToLower(), ct)
            ?? await db.Accounts.OrderBy(a => a.ID).FirstOrDefaultAsync(ct);
        if (author == null) return new SaveGuideResultDto(false, 0, "Author account not found");

        string normalizedAbilityQueue = NormalizeAbilityQueue(request.AbilityQueue, request.HeroIdentifier);

        string targetMap =
            string.IsNullOrWhiteSpace(request.TargetMap) ||
            request.TargetMap.Equals("all", StringComparison.OrdinalIgnoreCase)
                ? "caldavar"
                : request.TargetMap.ToLowerInvariant();

        HeroGuide guide;
        if (request.Id.HasValue && request.Id.Value > 0)
        {
            HeroGuide? existing = await db.HeroGuides
                .Include(g => g.Author)
                .Include(g => g.Items)
                .Include(g => g.Abilities)
                .FirstOrDefaultAsync(g => g.ID == request.Id.Value, ct);
            if (existing == null) return new SaveGuideResultDto(false, 0, "Guide not found");
            guide = existing;
        }
        else
        {
            guide = new HeroGuide
            {
                Name = !string.IsNullOrEmpty(request.Name) ? request.Name : "New Guide",
                HeroName = request.HeroName ?? request.HeroIdentifier,
                HeroIdentifier = request.HeroIdentifier,
                Intro = request.Intro ?? "",
                Content = request.Content ?? "",
                StartingItems = request.StartingItems ?? "[]",
                EarlyGameItems = request.EarlyGameItems ?? "[]",
                CoreItems = request.CoreItems ?? "[]",
                LuxuryItems = request.LuxuryItems ?? "[]",
                AbilityQueue = normalizedAbilityQueue,
                Author = author,
                Rating = 0.0f,
                UpVotes = 0,
                DownVotes = 0,
                Public = request.IsPublic,
                Featured = false,
                TargetMap = targetMap,
                TimestampCreated = TimeProvider.System.GetUtcNow()
            };
            db.HeroGuides.Add(guide);
        }

        if (!string.IsNullOrEmpty(request.Name)) guide.Name = request.Name;
        if (!string.IsNullOrEmpty(request.HeroIdentifier)) guide.HeroIdentifier = request.HeroIdentifier;
        if (request.HeroName != null) guide.HeroName = request.HeroName;
        if (request.Intro != null) guide.Intro = request.Intro;
        if (request.Content != null) guide.Content = request.Content;
        if (request.StartingItems != null) guide.StartingItems = request.StartingItems;
        if (request.EarlyGameItems != null) guide.EarlyGameItems = request.EarlyGameItems;
        if (request.CoreItems != null) guide.CoreItems = request.CoreItems;
        if (request.LuxuryItems != null) guide.LuxuryItems = request.LuxuryItems;
        guide.AbilityQueue = normalizedAbilityQueue;
        guide.TargetMap = targetMap;
        guide.Public = request.IsPublic;
        guide.TimestampLastUpdated = TimeProvider.System.GetUtcNow();

        await db.SaveChangesAsync(ct);
        return new SaveGuideResultDto(true, guide.ID, "Guide saved successfully");
    }

    public async Task<bool> DeleteGuideAsync(int id, string accountName, CancellationToken ct = default)
    {
        HeroGuide? guide = await db.HeroGuides.Include(g => g.Author).FirstOrDefaultAsync(g => g.ID == id, ct);
        if (guide == null) return false;
        db.HeroGuides.Remove(guide);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<VoteGuideResultDto> VoteGuideAsync(int id, bool isUpvote, CancellationToken ct = default)
    {
        HeroGuide? guide = await db.HeroGuides.FirstOrDefaultAsync(g => g.ID == id, ct);
        if (guide == null) return new VoteGuideResultDto(false, 0, 0, 0, "Guide not found");
        if (isUpvote) guide.UpVotes++;
        else guide.DownVotes++;
        int total = guide.UpVotes + guide.DownVotes;
        guide.Rating = total > 0 ? (float)Math.Round((double)guide.UpVotes / total * 5.0, 1) : 0f;
        await db.SaveChangesAsync(ct);
        return new VoteGuideResultDto(true, guide.UpVotes, guide.DownVotes, guide.Rating, "Vote recorded");
    }

    public async Task<TogglePrivacyResultDto> TogglePrivacyAsync(int id, string accountName, bool isStaff,
        CancellationToken ct = default)
    {
        HeroGuide? guide = await db.HeroGuides.Include(g => g.Author).FirstOrDefaultAsync(g => g.ID == id, ct);
        if (guide == null) return new TogglePrivacyResultDto(false, false, "Guide not found");
        guide.Public = !guide.Public;
        await db.SaveChangesAsync(ct);
        return new TogglePrivacyResultDto(true, guide.Public, "Privacy updated");
    }

    public async Task<ToggleOfficialResultDto> ToggleOfficialAsync(int id, CancellationToken ct = default)
    {
        HeroGuide? guide = await db.HeroGuides.FirstOrDefaultAsync(g => g.ID == id, ct);
        if (guide == null) return new ToggleOfficialResultDto(false, false, "Guide not found");
        if (!guide.Public) return new ToggleOfficialResultDto(false, false, "Cannot make private guide official");
        guide.Featured = !guide.Featured;
        await db.SaveChangesAsync(ct);
        return new ToggleOfficialResultDto(true, guide.Featured, "Official status updated");
    }


    public async Task<ToggleFavoriteGuideResultDto> ToggleFavoriteGuideAsync(int id, string accountName,
        CancellationToken ct = default)
    {
        Account? account = await db.Accounts.FirstOrDefaultAsync(a => a.Name.ToLower() == accountName.ToLower(), ct);
        if (account == null) return new ToggleFavoriteGuideResultDto(false, false, id, "Account not found");
        List<int> favorites = account.FavoriteGuideIDs != null ? [.. account.FavoriteGuideIDs] : [];
        bool isFavorited;
        if (favorites.Remove(id))
        {
            isFavorited = false;
        }
        else
        {
            favorites.Add(id);
            isFavorited = true;
        }

        account.FavoriteGuideIDs = favorites;
        await db.SaveChangesAsync(ct);
        return new ToggleFavoriteGuideResultDto(true, isFavorited, id, "Favorite guide updated");
    }

    public async Task<ToggleDefaultGuideResultDto> ToggleDefaultGuideAsync(int id, string accountName,
        CancellationToken ct = default)
    {
        Account? account = await db.Accounts.FirstOrDefaultAsync(a => a.Name.ToLower() == accountName.ToLower(), ct);
        if (account == null) return new ToggleDefaultGuideResultDto(false, false, id, "", "Account not found");
        HeroGuide? guide = await db.HeroGuides.FirstOrDefaultAsync(g => g.ID == id, ct);
        if (guide == null) return new ToggleDefaultGuideResultDto(false, false, id, "", "Guide not found");

        List<int> defaults = account.DefaultGuideIDs != null ? [.. account.DefaultGuideIDs] : [];
        bool isDefault;
        if (defaults.Remove(id))
        {
            isDefault = false;
        }
        else
        {
            List<int> otherGuideIds = await db.HeroGuides
                .Where(g => defaults.Contains(g.ID) && g.HeroIdentifier == guide.HeroIdentifier && g.ID != id)
                .Select(g => g.ID)
                .ToListAsync(ct);
            defaults.RemoveAll(otherGuideIds.Contains);

            defaults.Add(id);
            isDefault = true;
        }

        account.DefaultGuideIDs = defaults;
        await db.SaveChangesAsync(ct);
        return new ToggleDefaultGuideResultDto(true, isDefault, id, guide.HeroIdentifier, "Default guide updated");
    }

    public async Task<IReadOnlyList<HeroDifficultyVoteDto>> GetHeroDifficultyVotesAsync(string heroIdentifier,
        CancellationToken ct = default)
    {
        List<HeroDifficultyVote> votes =
            await db.HeroDifficultyVotes.Where(v => v.HeroIdentifier == heroIdentifier).ToListAsync(ct);
        return [.. votes.Select(MapHeroVoteNonNull)];
    }

    public async Task<HeroDifficultyVoteResultDto> VoteHeroDifficultyAsync(string heroIdentifier, string ign,
        int difficulty, CancellationToken ct = default)
    {
        Account? account = await db.Accounts.FirstOrDefaultAsync(a => a.Name.ToLower() == ign.ToLower(), ct)
                           ?? await db.Accounts.OrderBy(a => a.ID).FirstOrDefaultAsync(ct);
        if (account == null) return new HeroDifficultyVoteResultDto(false, 0.0, 0, "Account not found");

        HeroDifficultyVote? existing =
            await db.HeroDifficultyVotes.FirstOrDefaultAsync(
                v => v.HeroIdentifier == heroIdentifier && v.AccountID == account.ID, ct);
        if (existing == null)
        {
            existing = new HeroDifficultyVote
            {
                HeroIdentifier = heroIdentifier,
                AccountID = account.ID,
                Rating = difficulty,
                Timestamp = TimeProvider.System.GetUtcNow()
            };
            db.HeroDifficultyVotes.Add(existing);
        }
        else
        {
            existing.Rating = difficulty;
            existing.Timestamp = TimeProvider.System.GetUtcNow();
        }

        await db.SaveChangesAsync(ct);

        List<int> allRatings = await db.HeroDifficultyVotes.Where(v => v.HeroIdentifier == heroIdentifier)
            .Select(v => v.Rating).ToListAsync(ct);
        double avg = allRatings.Count > 0 ? Math.Round(allRatings.Average(), 1) : difficulty;
        return new HeroDifficultyVoteResultDto(true, avg, allRatings.Count, "Vote recorded");
    }

    public async Task<IReadOnlyList<HeroGuideDto>> GetGuidesForHeroAsync(string heroIdentifier,
        CancellationToken ct = default)
    {
        List<HeroGuide> guides = await db.HeroGuides
            .Include(g => g.Author)
            .Include(g => g.Items)
            .Include(g => g.Abilities)
            .Where(g => g.HeroIdentifier == heroIdentifier).ToListAsync(ct);
        return [.. guides.Select(MapHeroGuideNonNull)];
    }

    private static string NormalizeAbilityQueue(string? rawQueue, string? heroIdentifier)
    {
        if (string.IsNullOrWhiteSpace(rawQueue)) return string.Empty;
        string heroCode = (heroIdentifier ?? string.Empty).Replace("Hero_", "").Trim();
        string[] tokens = rawQueue
            .Replace('[', ' ')
            .Replace(']', ' ')
            .Replace('"', ' ')
            .Split([',', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        List<string> normalized = [];
        foreach (string token in tokens)
        {
            string t = token.Trim();
            if (string.IsNullOrEmpty(t)) continue;
            if (t.StartsWith("Ability_", StringComparison.OrdinalIgnoreCase))
            {
                normalized.Add(t);
                continue;
            }

            if (t.Equals("Ability1", StringComparison.OrdinalIgnoreCase) || t.Equals("1"))
            {
                normalized.Add($"Ability_{heroCode}1");
                continue;
            }

            if (t.Equals("Ability2", StringComparison.OrdinalIgnoreCase) || t.Equals("2"))
            {
                normalized.Add($"Ability_{heroCode}2");
                continue;
            }

            if (t.Equals("Ability3", StringComparison.OrdinalIgnoreCase) || t.Equals("3"))
            {
                normalized.Add($"Ability_{heroCode}3");
                continue;
            }

            if (t.Equals("Ability4", StringComparison.OrdinalIgnoreCase) || t.Equals("4"))
            {
                normalized.Add($"Ability_{heroCode}4");
                continue;
            }

            if (t.Equals("AttributeBoost", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("Stats", StringComparison.OrdinalIgnoreCase) || t.Equals("5"))
            {
                normalized.Add("Ability_AttributeBoost");
                continue;
            }

            normalized.Add(t);
        }

        return string.Join("|", normalized);
    }
}
