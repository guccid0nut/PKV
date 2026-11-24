// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Protocol handshake constants: Denis Koroskin (korDen/honserver) & Theli (theli-ua/pyHoNBot)
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Kongor.Core.Security;
using KONGOR.MasterServer.Services.Store;
using MERRICK.DatabaseContext.Enumerations;
using MERRICK.DatabaseContext.Extensions;

namespace KONGOR.MasterServer.Handlers.SRP;

[SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms",
    Justification = "Legacy HoN client protocol requirement")]
[SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms",
    Justification = "Legacy HoN client protocol requirement")]
public static class SRPAuthenticationHandlers
{
    # region Chat Server Authentication Secret

    // Thank you, Denis Koroskin (aka korDen), for making this value public: https://github.com/korDen/honserver/blob/b35c23015fe915633dc5acd52db44403066cb0eb/utils.php#L6.
    // It is not clear how this magic string was obtained.
    // Project KONGOR would have not been possible without having this value in the public domain.

    private const string ChatServerAuthenticationSalt =
        "8roespiemlasToUmiuglEhOaMiaSWlesplUcOAniupr2esPOeBRiudOEphiutOuJ";

    # endregion

    public static SRPAuthenticationResponseStageTwo GenerateStageTwoResponse(StageTwoResponseParameters parameters,
        out string cookie)
    {
        cookie = Guid.CreateVersion7().ToString().Replace("-", string.Empty);

        int totalGames = parameters.Account.Statistics?.Sum(s => s.MatchesPlayed) ?? 0;
        string leaverThreshold = totalGames < 50 ? ".20" : totalGames < 100 ? ".10" : ".05";

        SRPAuthenticationResponseStageTwo response = new()
        {
            ServerProof = parameters.ServerProof,
            MainAccountID =
                parameters.Account.User.Accounts.FirstOrDefault(account => account.IsMain)?.ID.ToString() ??
                parameters.Account.ID.ToString(),
            ID = parameters.Account.ID.ToString(),
            GarenaID = parameters.Account.ID.ToString(),
            Name = parameters.Account.Name,
            Email = parameters.Account.User.EmailAddress,
            AccountType = Convert.ToInt32(parameters.Account.Type).ToString(),
            SuspensionID = "0", // 0 indicates active, unsuspended account in K2 client protocol
            UseCloud = parameters.Account.UseCloud ? "1" : "0",
            Cookie = cookie,
            IPAddress = parameters.ClientIPAddress,
            LeaverThreshold = leaverThreshold,
            HasSubAccounts = parameters.Account.User.Accounts.Any(account => account.IsMain.Equals(false)),
            IsSubAccount = parameters.Account.IsMain.Equals(false),
            ICBURL =
                !string.IsNullOrWhiteSpace(parameters.ICBURL)
                    ? parameters.ICBURL
                    : Environment.GetEnvironmentVariable("MASTER_SERVER_ADDRESS") != null
                        ? Environment.GetEnvironmentVariable("MASTER_SERVER_ADDRESS")!
                        : Environment.GetEnvironmentVariable("INFRASTRUCTURE_GATEWAY") != null
                            ? $"http://{Environment.GetEnvironmentVariable("INFRASTRUCTURE_GATEWAY")}"
                            : Environment.GetEnvironmentVariable("APPLICATION_URL") ?? "http://localhost:55555",
            AuthenticationHash = ComputeChatServerCookieHash(parameters.Account.ID, parameters.ClientIPAddress, cookie),
            ChatServerIPAddress = parameters.ChatServer.Address,
            ChatServerPort = parameters.ChatServer.Port.ToString(),
            ChatChannels = SetChatChannels(parameters.Account),
            Accounts =
            [
                .. parameters.Account.User.Accounts.OrderBy(account => account.TimestampCreated)
                    .Select(account => new List<string> { account.Name, account.ID.ToString() })
            ],
            GoldCoins = parameters.Account.User.GoldCoins.ToString(),
            SilverCoins = parameters.Account.User.SilverCoins,
            CustomIconSlotID = SetCustomIconSlotID(parameters.Account),
            CurrentSeason = parameters.CurrentSeason,
            MuteExpiration =
                parameters.Account.IsMuted ? (int)parameters.Account.MuteExpiration!.Value.ToUnixTimeSeconds() : 0,
            FriendAccountList = SetFriendAccountList(parameters.Account),
            IgnoredAccountsList = SetIgnoredAccountsList(parameters.Account),
            BannedAccountsList = SetBannedAccountsList(parameters.Account),
            ClanRoster = SetClanRoster(parameters.ClanRoster),
            ClanMembershipData = SetClanMembershipData(parameters.Account),
            OwnedStoreItems = UpgradesResolutionService.ExpandOwnedItems(parameters.Account.User.InventoryItems?.Select(i => i.ItemCode)).ToList(),
            SelectedStoreItems = parameters.Account.SelectedStoreItemCodes,
            OwnedStoreItemsData = SetOwnedStoreItemsData(parameters.Account, UpgradesResolutionService.ExpandOwnedItems(parameters.Account.User.InventoryItems?.Select(i => i.ItemCode))),
            AwardsTooltips = SetAwardsTooltips(),
            DataPoints = SetDataPoints(parameters.Account),
            CloudStorageInformation = SetCloudStorageInformation(parameters.Account),
            Notifications = SetNotifications()
        };

        return response;
    }

    /// <summary>
    ///     Generates a 64-character long SHA256 hash of the account's password.
    ///     The uppercase hashes (C# default) in this method need to be lowercased, to match the lowercase hashes (C++ default)
    ///     that the game client generates.
    ///     <br />
    ///     The expectation is that the password is not hashed for SRP registration, but is hashed for SRP authentication.
    /// </summary>
    public static string ComputeSRPPasswordHash(string password, string salt, bool passwordIsHashed = true)
    {
        return passwordIsHashed
            ? S2Crypto.ComputeSrpFromPrehashedMd5(password, salt)
            : S2Crypto.ComputeSrpFromPlaintext(password, salt);
    }

    public static string ComputeChatServerCookieHash(int accountID, string remoteIPAddress, string cookie)
    {
        return S2Crypto.ComputeChatServerCookieHash(accountID, remoteIPAddress, cookie);
    }

    public static string ComputeMatchServerChatAuthenticationHash(string key, string cookie)
    {
        return S2Crypto.ComputeMatchServerChatAuthenticationHash(key, cookie);
    }

    private static List<string> SetChatChannels(Account account)
    {
        List<string> channels = account.AutoConnectChatChannels;

        if (account.Type is not AccountType.ServerHost) channels = [.. channels.Prepend(ChatChannels.GeneralChannel)];

        if (account.Clan is not null) channels.Add(account.Clan.GetChatChannelName());

        if (account.Type is AccountType.GameMaster or AccountType.Staff) channels.Add(ChatChannels.GameMastersChannel);

        if (account.Type is AccountType.Guest or AccountType.Staff) channels.Add(ChatChannels.GuestsChannel);

        if (account.Type is AccountType.ServerHost or AccountType.Staff) channels.Add(ChatChannels.ServerHostsChannel);

        if (account.Type is AccountType.Streamer or AccountType.Staff) channels.Add(ChatChannels.StreamersChannel);

        if (account.Type is AccountType.VIP or AccountType.Staff) channels.Add(ChatChannels.VIPChannel);

        if (account.Type is AccountType.Staff) channels.Add(ChatChannels.StaffChannel);

        channels = [.. channels.Distinct().Order()];

        return channels;
    }

    private static Dictionary<string, Dictionary<string, FriendAccount>> SetFriendAccountList(Account account)
    {
        return new Dictionary<string, Dictionary<string, FriendAccount>>
        {
            {
                account.ID.ToString(), (account.Friendships ?? [])
                    .Where(friendship => friendship.Status == FriendshipStatus.Accepted)
                    .DistinctBy(friendship => friendship.FriendAccountID)
                    .ToDictionary(
                        friendship => friendship.FriendAccountID.ToString(),
                        friendship => new FriendAccount
                        {
                            ID = friendship.FriendAccountID.ToString(),
                            Name = friendship.FriendAccount?.Name ?? string.Empty,
                            Group = friendship.FriendGroup,
                            ClanTag = friendship.FriendAccount?.Clan?.Tag ?? string.Empty
                        })
            }
        };
    }

    private static Dictionary<string, List<IgnoredAccount>> SetIgnoredAccountsList(Account account)
    {
        return new Dictionary<string, List<IgnoredAccount>>
        {
            {
                account.ID.ToString(), [
                    .. account.IgnoredPeers
                        .Select(ignored => new IgnoredAccount { ID = ignored.ID.ToString(), Name = ignored.Name })
                ]
            }
        };
    }

    private static Dictionary<string, List<BannedAccount>> SetBannedAccountsList(Account account)
    {
        return new Dictionary<string, List<BannedAccount>>
        {
            {
                account.ID.ToString(), [
                    .. account.BannedPeers
                        .Select(banned =>
                            new BannedAccount
                            {
                                ID = banned.ID.ToString(), Name = banned.Name, Reason = banned.BanReason
                            })
                ]
            }
        };
    }

    private static Dictionary<string, ClanMemberAccount> SetClanRoster(List<Account> members)
    {
        return members.Select(member => new KeyValuePair<string, ClanMemberAccount>(member.ID.ToString(),
                new ClanMemberAccount
                {
                    ClanID = member.Clan?.ID.ToString() ?? string.Empty,
                    ID = member.ID.ToString(),
                    JoinDate =
                        member.TimestampJoinedClan is not null
                            ? member.TimestampJoinedClan.GetValueOrDefault().ToString("yyyy-MM-dd HH:mm:ss")
                            : string.Empty,
                    Name = member.Name,
                    Rank = member.GetClanTierName(),
                    Message = string.Empty,
                    Standing = Convert.ToInt32(member.Type).ToString()
                }))
            .ToDictionary();
    }

    private static OneOf<ClanMemberData, ClanMemberDataError> SetClanMembershipData(Account account)
    {
        return account.Clan is null
            ? new ClanMemberDataError()
            : new ClanMemberData
            {
                ClanID = account.Clan.ID.ToString(),
                ID = account.ID.ToString(),
                ClanName = account.Clan.Name,
                ClanTag = account.Clan.Tag,
                ClanOwnerAccountID =
                    account.Clan.Members.FirstOrDefault(member => member.ClanTier is ClanTier.Leader)?.ID
                        .ToString() ?? string.Empty,
                JoinDate =
                    account.TimestampJoinedClan is not null
                        ? account.TimestampJoinedClan.GetValueOrDefault().ToString("yyyy-MM-dd HH:mm:ss")
                        : string.Empty,
                Rank = account.GetClanTierName(),
                Message = string.Empty,
                Title = !string.IsNullOrEmpty(account.Clan?.Name) ? $"[CLAN] {account.Clan.Name}" : string.Empty
            };
    }

    private static string SetCustomIconSlotID(Account account)
    {
        string? explicitCustomIcon = account.SelectedStoreItemCodes
            .FirstOrDefault(item => item.StartsWith("ai.custom_icon", StringComparison.OrdinalIgnoreCase));

        if (explicitCustomIcon is not null)
        {
            string slot =
                explicitCustomIcon.Replace("ai.custom_icon:", string.Empty, StringComparison.OrdinalIgnoreCase);
            return string.IsNullOrWhiteSpace(slot) ? "1" : slot;
        }

        // If the user selected an explicit non-custom store icon (e.g. ai.beer, ai.hellbourne), return "0"
        bool hasOtherStoreIcon = account.SelectedStoreItemCodes
            .Any(item => item.StartsWith("ai.", StringComparison.OrdinalIgnoreCase) &&
                         !item.Equals("ai.Default Icon", StringComparison.OrdinalIgnoreCase));

        if (hasOtherStoreIcon) return "0";

        // If user has a Discord account or Discord avatar, or owns custom_icon:1, enable custom icon slot 1
        if (!string.IsNullOrWhiteSpace(account.User.DiscordAvatar) ||
            !string.IsNullOrWhiteSpace(account.User.DiscordId) ||
            (account.User.InventoryItems?.Any(item =>
                item.ItemCode.StartsWith("ai.custom_icon", StringComparison.OrdinalIgnoreCase)) == true))
            return "1";

        return "0";
    }

    private static CloudStorageInformation SetCloudStorageInformation(Account account)
    {
        return new CloudStorageInformation
        {
            AccountID = account.ID.ToString(),
            UseCloud = account.UseCloud ? "1" : "0",
            AutomaticCloudUpload = account.AutomaticCloudUpload ? "1" : "0",
            BackupLastUpdatedTime = account.BackupLastUpdatedTime?.ToString("yyyy-MM-dd HH:mm:ss") ??
                                    DateTimeOffset.MinValue.ToString("yyyy-MM-dd HH:mm:ss")
        };
    }

    private static List<DataPoint> SetDataPoints(Account account)
    {
        AccountStatistics? publicStats = account.Statistics.FirstOrDefault(s => s.Type == AccountStatisticsType.Public);
        AccountStatistics? matchmakingStats =
            account.Statistics.FirstOrDefault(s => s.Type == AccountStatisticsType.Matchmaking);
        AccountStatistics? casualStats =
            account.Statistics.FirstOrDefault(s => s.Type == AccountStatisticsType.MatchmakingCasual);
        AccountStatistics? midWarsStats =
            account.Statistics.FirstOrDefault(s => s.Type == AccountStatisticsType.MidWars);
        AccountStatistics? riftWarsStats =
            account.Statistics.FirstOrDefault(s => s.Type == AccountStatisticsType.RiftWars);
        AccountStatistics? cooperativeStats =
            account.Statistics.FirstOrDefault(s => s.Type == AccountStatisticsType.Cooperative);

        int totalDisconnects = account.Statistics.Sum(s => s.MatchesDisconnected);
        int totalMatchesPlayed = account.Statistics.Sum(s => s.MatchesPlayed);

        List<DataPoint> dataPoints =
        [
            new()
            {
                ID = account.ID.ToString(),
                Level = account.AscensionLevel.ToString(),
                Disconnects = totalDisconnects.ToString(),
                MatchesPlayed = totalMatchesPlayed.ToString(),
                BotMatchesWon = cooperativeStats?.MatchesWon.ToString() ?? "0",
                PSR = publicStats?.SkillRating.ToString("F3") ?? "1500.000",
                PublicMatchesPlayed = publicStats?.MatchesPlayed.ToString() ?? "0",
                PublicMatchesWon = publicStats?.MatchesWon.ToString() ?? "0",
                PublicMatchesLost = publicStats?.MatchesLost.ToString() ?? "0",
                PublicMatchDisconnects = publicStats?.MatchesDisconnected.ToString() ?? "0",
                MMR = matchmakingStats?.SkillRating.ToString("F3") ?? "1500.000",
                RankedMatchesPlayed = matchmakingStats?.MatchesPlayed.ToString() ?? "0",
                RankedMatchesWon = matchmakingStats?.MatchesWon.ToString() ?? "0",
                RankedMatchesLost = matchmakingStats?.MatchesLost.ToString() ?? "0",
                RankedMatchDisconnects = matchmakingStats?.MatchesDisconnected.ToString() ?? "0",
                MidWarsMMR = midWarsStats?.SkillRating.ToString("F3") ?? "1500.000",
                RankedMidWarsMatchesPlayed = midWarsStats?.MatchesPlayed.ToString() ?? "0",
                RankedMidWarsMatchDisconnects = midWarsStats?.MatchesDisconnected.ToString() ?? "0",
                RiftWarsMMR = riftWarsStats?.SkillRating.ToString("F3") ?? "1500.000",
                RankedRiftWarsMatchesPlayed = riftWarsStats?.MatchesPlayed.ToString() ?? "0",
                RankedRiftWarsMatchDisconnects = riftWarsStats?.MatchesDisconnected.ToString() ?? "0",
                CasualMMR = casualStats?.SkillRating.ToString("F3") ?? "1500.000",
                CasualRankedMatchDisconnects = casualStats?.MatchesDisconnected.ToString() ?? "0",
                CasualRankedMatchesLost = casualStats?.MatchesLost.ToString() ?? "0",
                CasualRankedMatchesPlayed = casualStats?.MatchesPlayed.ToString() ?? "0",
                CasualRankedMatchesWon = casualStats?.MatchesWon.ToString() ?? "0",
                SeasonalRankedMatchesPlayed = 0,
                SeasonalRankedMatchDisconnects = 0,
                CasualSeasonalRankedMatchesPlayed = 0,
                CasualSeasonalRankedMatchDisconnects = 0,
                Experience = account.User.TotalExperience.ToString()
            }
        ];

        return dataPoints;
    }

    private static Dictionary<string, OwnedStoreItemDTO> SetOwnedStoreItemsData(
        Account account,
        IEnumerable<string>? ownedCodes = null)
    {
        long now = TimeProvider.System.GetUtcNow().ToUnixTimeSeconds();
        long forever = now + 999999999;
        string nowStr = now.ToString(CultureInfo.InvariantCulture);
        string foreverStr = forever.ToString(CultureInfo.InvariantCulture);

        IEnumerable<string> codes = ownedCodes ?? account.User.InventoryItems?
            .Select(i => i.ItemCode)
            .Distinct() ?? [];

        Dictionary<string, OwnedStoreItemDTO> items = [];
        foreach (string upgrade in codes)
        {
            if (string.IsNullOrWhiteSpace(upgrade)) continue;
            if (upgrade.StartsWith("ma.", StringComparison.OrdinalIgnoreCase) ||
                upgrade.StartsWith("cp.", StringComparison.OrdinalIgnoreCase))
                continue;

            items[upgrade] = new OwnedStoreItemDTO
            {
                Data = string.Empty,
                StartTime = nowStr,
                EndTime = foreverStr,
                Used = 0,
                Score = "0",
                ExpirationDate = "0",
                Permanent = "1"
            };
        }

        return items;
    }

    private static AwardsTooltips SetAwardsTooltips()
    {
        return new AwardsTooltips();
    }

    private static List<Notification> SetNotifications()
    {
        // System broadcast and client notification queue (empty on initial SRP handshake).
        return [];
    }

    public class StageTwoResponseParameters
    {
        public required Account Account { get; set; }
        public required List<Account> ClanRoster { get; set; }
        public required string ServerProof { get; set; }
        public required string ClientIPAddress { get; set; }
        public required (string Address, int Port) ChatServer { get; set; }
        public required string CurrentSeason { get; set; }
        public string? ICBURL { get; set; }
    }

    # region Secure Remote Password Magic Strings

    // Thank you, Anton Romanov (aka Theli), for making these values public: https://github.com/theli-ua/pyHoNBot/blob/cabde31b8601c1ca55dc10fcf663ec663ec0eb71/hon/masterserver.py#L37.
    // The first magic string is also present in the k2_x64 DLL of the Windows client, between offsets 0xF2F4D0 and 0xF2F4D0.
    // It is not clear how the second magic string was obtained.
    // Project KONGOR would have not been possible without having these values in the public domain.

    private const string MagicStringOne = "[!~esTo0}";
    private const string MagicStringTwo = "taquzaph_?98phab&junaj=z=kuChusu";

    # endregion
}
