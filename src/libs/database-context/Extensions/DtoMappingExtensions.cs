// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using NEXUS.Contracts.Clients;

namespace MERRICK.DatabaseContext.Extensions;

public static class DtoMappingExtensions
{
    public static AccountDto ToDto(this Account account)
    {
        return new AccountDto
        {
            ID = account.ID,
            Name = account.Name,
            UserID = account.User?.ID,
            OwnerAccountID = account.OwnerAccountID,
            Type = account.Type,
            IsMain = account.IsMain,
            ClanId = account.ClanID,
            ClanName = account.Clan?.Name,
            ClanTag = account.Clan?.Tag,
            ClanTier = account.ClanTier,
            AscensionLevel = account.AscensionLevel,
            GoldCoins = account.GoldCoins,
            SilverCoins = account.SilverCoins,
            TimestampCreated = account.TimestampCreated,
            TimestampLastActive = account.TimestampLastActive,
            SelectedStoreItems = [.. account.SelectedStoreItems.Select(item => item.ItemCode)],
            DefaultGuideIDs = [.. account.DefaultGuideIDs],
            FavoriteGuideIDs = [.. account.FavoriteGuideIDs],
            UseCloud = account.UseCloud,
            AutomaticCloudUpload = account.AutomaticCloudUpload,
            BackupLastUpdatedTime = account.BackupLastUpdatedTime,
            MuteExpiration = account.MuteExpiration,
            MuteReason = account.MuteReason,
            SuspensionExpiration = account.SuspensionExpiration,
            SuspensionReason = account.SuspensionReason,
            BanExpiration = account.BanExpiration,
            BanReason = account.BanReason,
            User = account.User != null ? new UserDto
            {
                ID = account.User.ID,
                EmailAddress = account.User.EmailAddress,
                DiscordId = account.User.DiscordId,
                DiscordUsername = account.User.DiscordUsername,
                DiscordAvatar = account.User.DiscordAvatar,
                TwitchUsername = account.User.TwitchUsername,
                TwitchId = account.User.TwitchId,
                Role = account.User.Role != null ? new UserRoleDto { Id = account.User.Role.ID, Name = account.User.Role.Name } : null
            } : null
        };
    }

    public static UserDto ToDto(this User user)
    {
        return new UserDto
        {
            ID = user.ID,
            EmailAddress = user.EmailAddress,
            DiscordId = user.DiscordId,
            DiscordUsername = user.DiscordUsername,
            DiscordAvatar = user.DiscordAvatar,
            TwitchUsername = user.TwitchUsername,
            TwitchId = user.TwitchId,
            Role = user.Role != null ? new UserRoleDto { Id = user.Role.ID, Name = user.Role.Name } : null,
            Accounts = [.. user.Accounts.Select(a => a.ToDto())],
            TimestampCreated = user.TimestampCreated,
            TimestampLastActive = user.TimestampLastActive,
            GoldCoins = user.GoldCoins,
            SilverCoins = user.SilverCoins,
            PlinkoTickets = user.PlinkoTickets,
            LastPlinkoDailyClaimUtc = user.LastPlinkoDailyClaimUtc,
            TotalLevel = user.TotalLevel,
            TotalExperience = user.TotalExperience,
            OwnedStoreItems = user.InventoryItems?.Select(i => i.ItemCode).Distinct().ToList() ?? [],
            HasInGamePasswordSet = !string.IsNullOrEmpty(user.SRPPasswordHash)
        };
    }

    public static StreamerApplicationDto ToDto(this StreamerApplication app)
    {
        return new StreamerApplicationDto
        {
            ID = app.ID,
            UserID = app.UserID,
            MainAccountID = app.MainAccountID,
            TwitchChannelUrl = app.TwitchChannelUrl ?? string.Empty,
            TwitchUsername = app.TwitchUsername,
            TwitchId = app.TwitchId,
            ApplicationNotes = app.ApplicationNotes,
            Status = app.Status,
            TimestampApplied = app.TimestampApplied,
            TimestampReviewed = app.TimestampReviewed,
            RejectionReason = app.StaffNotes
        };
    }

    public static HostApplicationDto ToDto(this HostApplication app)
    {
        return new HostApplicationDto
        {
            ID = app.ID,
            UserID = app.UserID,
            MainAccountID = app.MainAccountID,
            HostAccountID = app.HostAccountID,
            HostUsername = app.HostUsername,
            EncryptedHostPassword = app.EncryptedHostPassword,
            EncryptedAuthToken = app.EncryptedAuthToken,
            ServerNamePrefix = app.ServerNamePrefix,
            Location = app.Location,
            Instances = app.Instances,
            Status = app.Status,
            ServerLocation = app.Location,
            MaxPlayerCapacity = app.Instances * 10,
            DedicatedBandwidthMbps = 1000,
            HardwareSpecs = "Dedicated Node",
            ApplicationNotes = app.ApplicationNotes,
            TimestampApplied = app.TimestampApplied,
            TimestampReviewed = app.TimestampReviewed,
            RejectionReason = app.StaffNotes
        };
    }

    public static UserWebRewardDto ToDto(this UserWebReward reward)
    {
        return new UserWebRewardDto
        {
            ID = reward.Id,
            UserID = reward.UserID,
            RewardType = reward.RewardType,
            RewardCode = reward.RewardCode,
            RewardName = reward.Name,
            IsEquipped = reward.IsEquipped,
            TimestampClaimed = reward.TimestampAcquired
        };
    }

    public static TokenDto ToDto(this Token token)
    {
        return new TokenDto
        {
            ID = token.ID,
            UserID = int.TryParse(token.Data, out int uid) ? uid : 0,
            EmailAddress = token.EmailAddress,
            Value = token.Value,
            Purpose = (TokenPurpose)(int)token.Purpose,
            Data = token.Data,
            TimestampIssued = token.TimestampCreated,
            TimestampExpires = token.TimestampCreated.AddHours(24),
            TimestampConsumed = token.TimestampConsumed
        };
    }

    public static AccountStatisticsDto ToDto(this AccountStatistics stats)
    {
        return new AccountStatisticsDto
        {
            ID = stats.ID,
            AccountID = stats.AccountID,
            Type = stats.Type,
            MatchesPlayed = stats.MatchesPlayed,
            MatchesWon = stats.MatchesWon,
            MatchesLost = stats.MatchesLost,
            MatchesConceded = stats.MatchesConceded,
            Disconnects = stats.MatchesDisconnected,
            SkillRating = stats.SkillRating,
            Experience = 0,
            Gold = 0,
            HeroKills = stats.AwardMostKills ?? 0,
            Deaths = stats.AwardLeastDeaths ?? 0,
            HeroAssists = stats.AwardMostAssists ?? 0,
            CreepKills = stats.AwardMostCreepKills ?? 0,
            NeutralKills = 0,
            Wards = stats.AwardMostWardsKilled ?? 0,
            HeroDamage = stats.AwardMostHeroDamageDealt ?? 0,
            BuildingDamage = stats.AwardMostBuildingDamage ?? 0,
            TimePlayedSeconds = 0
        };
    }

    public static MatchStatisticsDto ToDto(this MatchStatistics match)
    {
        return new MatchStatisticsDto
        {
            ID = match.ID,
            MatchID = match.MatchID,
            Name = match.Map,
            Map = match.Map,
            Mode = match.GameMode,
            ScoreTeam1 = match.ScoreTeam1,
            ScoreTeam2 = match.ScoreTeam2,
            WinningTeam = match.ScoreTeam1 > match.ScoreTeam2 ? 1 : (match.ScoreTeam2 > match.ScoreTeam1 ? 2 : 1),
            TimePlayedSeconds = match.TimePlayed,
            HostAccountName = match.HostAccountName,
            ServerLocation = string.Empty,
            TimestampRecorded = match.TimestampRecorded,
            ReplayUrl = match.ReplayUrl
        };
    }

    public static PlayerReportDto ToDto(this PlayerReport report)
    {
        return new PlayerReportDto
        {
            ID = report.ID,
            MatchID = report.MatchID,
            ReporterAccountID = report.ReporterAccountID,
            ReporterAccountName = report.ReporterAccountName,
            ReportedAccountID = report.ReportedAccountID,
            ReportedAccountName = report.ReportedAccountName,
            Reason = report.Reason,
            Description = report.Description ?? string.Empty,
            Status = report.Status,
            TimestampCreated = report.TimestampCreated,
            TimestampReviewed = report.TimestampReviewed,
            ReviewedByStaffName = report.ReviewedByStaffName,
            PunishmentType = report.PunishmentType,
            PunishmentDurationHours = report.PunishmentDurationHours,
            PunishmentExpiration = report.PunishmentExpiration,
            StaffNotes = report.StaffNotes
        };
    }

    public static HeroGuideDto ToDto(this HeroGuide guide)
    {
        return new HeroGuideDto
        {
            ID = guide.ID,
            Name = guide.Name,
            HeroIdentifier = guide.HeroIdentifier,
            HeroName = guide.HeroName,
            TargetMap = guide.TargetMap,
            Intro = guide.Intro,
            Content = guide.Content,
            StartingItems = guide.StartingItems,
            EarlyGameItems = guide.EarlyGameItems,
            CoreItems = guide.CoreItems,
            LuxuryItems = guide.LuxuryItems,
            AbilityQueue = guide.AbilityQueue,
            Public = guide.Public,
            Official = guide.Featured,
            Upvotes = guide.UpVotes,
            Downvotes = guide.DownVotes,
            AuthorAccountName = guide.Author?.Name ?? string.Empty,
            TimestampCreated = guide.TimestampCreated,
            TimestampUpdated = guide.TimestampLastUpdated ?? guide.TimestampCreated
        };
    }

    public static HeroDifficultyVoteDto ToDto(this HeroDifficultyVote vote)
    {
        return new HeroDifficultyVoteDto
        {
            ID = vote.ID,
            HeroIdentifier = vote.HeroIdentifier,
            InGameNickname = vote.AccountID.ToString(),
            Difficulty = vote.Rating,
            TimestampCreated = vote.Timestamp
        };
    }

    private static (string Hero, string Alt) ParseHeroAndAlt(int type, string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return (string.Empty, string.Empty);
        }

        string clean = code.Trim();
        if (clean.StartsWith("aa.", StringComparison.OrdinalIgnoreCase)) clean = clean[3..];

        if (clean.StartsWith("Hero_", StringComparison.OrdinalIgnoreCase))
        {
            string remainder = clean[5..];
            if (remainder.Contains('.'))
            {
                string[] parts = remainder.Split('.', 2);
                return (parts[0], parts.Length > 1 ? parts[1] : string.Empty);
            }
            return (remainder, string.Empty);
        }

        return (string.Empty, string.Empty);
    }

    public static StoreItemDto ToDto(this StoreItem item)
    {
        (string hero, string alt) = ParseHeroAndAlt(item.UpgradeType, item.Code);
        return new StoreItemDto
        {
            ID = item.Id,
            Code = item.Code,
            Name = item.Name,
            TypeName = item.UpgradeType.ToString(),
            Type = item.UpgradeType,
            GoldCost = item.Price,
            SilverCost = item.PriceInMMP,
            IsEnabled = item.Enabled,
            Purchasable = item.Purchasable,
            Resource = item.LocalContent,
            HeroName = hero,
            AltAvatarSuffix = alt
        };
    }

    public static HeroDetailDto ToDto(this HeroDetail detail)
    {
        return new HeroDetailDto
        {
            ID = detail.ID,
            HeroIdentifier = detail.HeroCode,
            DisplayName = detail.DisplayName,
            Description = detail.Lore,
            PrimaryAttribute = detail.PrimaryAttribute,
            Role = detail.AttackType,
            AttackType = detail.AttackType,
            BaseStrength = detail.BaseStrength,
            StrengthGrowth = detail.StrengthGrowth,
            BaseAgility = detail.BaseAgility,
            AgilityGrowth = detail.AgilityGrowth,
            BaseIntelligence = detail.BaseIntelligence,
            IntelligenceGrowth = detail.IntelligenceGrowth,
            Armor = detail.Armor,
            ArmorMitigation = detail.ArmorMitigation,
            MinDamage = detail.MinDamage,
            MaxDamage = detail.MaxDamage,
            MovementSpeed = detail.MovementSpeed,
            IconPath = detail.IconPath,
            SplashImagePath = detail.SplashImagePath,
            Lore = detail.Lore,
            AbilitiesJson = detail.AbilitiesJson
        };
    }

    public static HeroItemDto ToDto(this HeroItem item)
    {
        return new HeroItemDto
        {
            ID = item.ID,
            ItemIdentifier = item.Code,
            DisplayName = item.Name,
            Description = item.Description,
            Cost = item.Cost,
            Category = item.Category,
            RecipeCost = item.RecipeCost,
            IconPath = item.IconPath,
            Flavor = item.Flavor,
            Upgrade = item.Upgrade,
            IsActive = item.IsActive,
            ComponentCodesJson = item.ComponentCodesJson
        };
    }

    public static MatchEvidenceDto ToDto(this MatchEvidence evidence)
    {
        return new MatchEvidenceDto
        {
            ID = evidence.ID,
            MatchID = evidence.MatchID,
            HasReplay = evidence.HasReplay,
            HasVoice = evidence.HasVoice,
            ChatLogJson = evidence.ChatLogJson,
            VoiceTrackLegionUrl = evidence.VoiceTrackLegionUrl,
            VoiceTrackHellbourneUrl = evidence.VoiceTrackHellbourneUrl,
            TimestampExtracted = evidence.TimestampExtracted,
            EvidenceJson = evidence.ChatLogJson,
            TimestampCreated = evidence.TimestampExtracted
        };
    }

    public static PlayerStatisticsDto ToDto(this PlayerStatistics stats)
    {
        return new PlayerStatisticsDto
        {
            ID = stats.ID,
            AccountID = stats.AccountID,
            AccountName = stats.AccountName,
            MatchID = stats.MatchID,
            HeroIdentifier = stats.HeroIdentifier,
            Team = stats.Team,
            Position = stats.LobbyPosition,
            HeroLevel = stats.HeroLevel > 0 ? stats.HeroLevel : 1,
            HeroKills = stats.HeroKills,
            Deaths = stats.HeroDeaths,
            HeroAssists = stats.HeroAssists,
            CreepKills = stats.TeamCreepKills,
            NeutralKills = stats.NeutralCreepKills,
            GoldEarned = stats.Gold,
            ExperienceEarned = stats.Experience,
            HeroDamage = stats.HeroDamage,
            BuildingDamage = stats.BuildingDamage,
            WardsPlaced = stats.WardsPlaced,
            Won = stats.Win == 1
        };
    }
}
