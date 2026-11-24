// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using MERRICK.DatabaseContext.Enumerations;

namespace MERRICK.DatabaseContext.Entities.Core;

[Table("Accounts", Schema = "core")]
[Index(nameof(Name), IsUnique = true)]
[Index(nameof(UserID))]
[Index(nameof(OwnerAccountID))]
public class Account
{
    [Key] public int ID { get; set; }

    [MaxLength(15)] public required string Name { get; set; }

    public int UserID { get; set; }

    [ForeignKey(nameof(UserID))] public required User User { get; set; }

    public int? OwnerAccountID { get; set; }

    public Account? OwnerAccount { get; set; }

    [InverseProperty(nameof(OwnerAccount))]
    public List<Account> HostedNodes { get; set; } = [];

    [NotMapped]
    [Obsolete(
        "Session cookies are stored exclusively in Redis distributed cache and are not persisted to SQL database.")]
    public string? Cookie { get; set; }

    public AccountType Type { get; set; } = AccountType.Legacy;

    public bool IsMain { get; set; } = true;

    [NotMapped]
    public bool IsStaff => Type is AccountType.Staff or AccountType.GameMaster or AccountType.MatchModerator;

    public int? ClanID { get; set; }

    [ForeignKey(nameof(ClanID))]
    [InverseProperty(nameof(Clan.Members))]
    public Clan? Clan { get; set; }

    public ClanTier ClanTier { get; set; } = ClanTier.None;

    public Mastery? Mastery { get; set; }

    public DateTimeOffset? TimestampJoinedClan { get; set; }

    public int AscensionLevel { get; set; }

    [NotMapped]
    public int GoldCoins
    {
        get => User?.GoldCoins ?? 0;
        set => User?.GoldCoins = value;
    }

    [NotMapped]
    public int SilverCoins
    {
        get => User?.SilverCoins ?? 0;
        set => User?.SilverCoins = value;
    }

    public DateTimeOffset TimestampCreated { get; set; } = TimeProvider.System.GetUtcNow();

    public DateTimeOffset TimestampLastActive { get; set; } = TimeProvider.System.GetUtcNow();

    public List<string> AutoConnectChatChannels { get; set; } = [];

    [InverseProperty(nameof(AccountPeer.Account))]
    public List<AccountPeer> Peers { get; set; } = [];

    [NotMapped]
    public List<BannedPeer> BannedPeers
    {
        get
        {
            DateTimeOffset now = TimeProvider.System.GetUtcNow();
            return Peers?
                .Where(p => p.Type == PeerType.Banned && (p.TimestampExpires == null || p.TimestampExpires > now))
                .Select(p => new BannedPeer
                {
                    ID = p.PeerAccountID,
                    Name = p.PeerAccount?.Name ?? string.Empty,
                    BanReason = p.Reason ?? string.Empty
                }).ToList() ?? [];
        }
        set
        {
            Peers ??= [];
            Peers.RemoveAll(p => p.Type == PeerType.Banned);
            if (value is null) return;
            DateTimeOffset now = TimeProvider.System.GetUtcNow();
            foreach (BannedPeer bp in value)
            {
                Peers.Add(new AccountPeer
                {
                    AccountID = ID,
                    PeerAccountID = bp.ID,
                    Type = PeerType.Banned,
                    Reason = bp.BanReason,
                    TimestampCreated = now,
                    TimestampExpires = now.AddMonths(6)
                });
            }
        }
    }

    [NotMapped]
    public List<IgnoredPeer> IgnoredPeers
    {
        get
        {
            DateTimeOffset now = TimeProvider.System.GetUtcNow();
            return Peers?
                .Where(p => p.Type == PeerType.Ignored && (p.TimestampExpires == null || p.TimestampExpires > now))
                .Select(p => new IgnoredPeer
                {
                    ID = p.PeerAccountID,
                    Name = p.PeerAccount?.Name ?? string.Empty
                }).ToList() ?? [];
        }
        set
        {
            Peers ??= [];
            Peers.RemoveAll(p => p.Type == PeerType.Ignored);
            if (value is null) return;
            DateTimeOffset now = TimeProvider.System.GetUtcNow();
            foreach (IgnoredPeer ip in value)
            {
                Peers.Add(new AccountPeer
                {
                    AccountID = ID,
                    PeerAccountID = ip.ID,
                    Type = PeerType.Ignored,
                    TimestampCreated = now,
                    TimestampExpires = now.AddMonths(6)
                });
            }
        }
    }

    public const int MaxActiveBannedPeers = 25;

    public void AddBannedPeer(int peerAccountId, string reason = "", DateTimeOffset? expires = null)
    {
        Peers ??= [];
        DateTimeOffset now = TimeProvider.System.GetUtcNow();
        DateTimeOffset effectiveExpiration = expires ?? now.AddMonths(6);

        AccountPeer? existing = Peers.FirstOrDefault(p => p.PeerAccountID == peerAccountId && p.Type == PeerType.Banned);
        if (existing != null)
        {
            existing.Reason = reason;
            existing.TimestampExpires = effectiveExpiration;
            return;
        }

        int activeBannedCount = Peers.Count(p => p.Type == PeerType.Banned && (p.TimestampExpires == null || p.TimestampExpires > now));
        if (activeBannedCount >= MaxActiveBannedPeers)
        {
            throw new InvalidOperationException($"You have reached the maximum limit of {MaxActiveBannedPeers} banned accounts. Remove an existing entry to add more.");
        }

        Peers.Add(new AccountPeer
        {
            AccountID = ID,
            PeerAccountID = peerAccountId,
            Type = PeerType.Banned,
            Reason = reason,
            TimestampCreated = now,
            TimestampExpires = effectiveExpiration
        });
    }

    public void AddIgnoredPeer(int peerAccountId, string reason = "", DateTimeOffset? expires = null)
    {
        Peers ??= [];
        DateTimeOffset now = TimeProvider.System.GetUtcNow();
        DateTimeOffset effectiveExpiration = expires ?? now.AddMonths(6);

        AccountPeer? existing = Peers.FirstOrDefault(p => p.PeerAccountID == peerAccountId && p.Type == PeerType.Ignored);
        if (existing != null)
        {
            existing.Reason = reason;
            existing.TimestampExpires = effectiveExpiration;
            return;
        }

        Peers.Add(new AccountPeer
        {
            AccountID = ID,
            PeerAccountID = peerAccountId,
            Type = PeerType.Ignored,
            Reason = reason,
            TimestampCreated = now,
            TimestampExpires = effectiveExpiration
        });
    }

    public bool RemovePeer(int peerAccountId, PeerType type)
    {
        if (Peers is null) return false;
        AccountPeer? match = Peers.FirstOrDefault(p => p.PeerAccountID == peerAccountId && p.Type == type);
        return match != null && Peers.Remove(match);
    }

    public bool HasBannedPeer(int peerAccountId)
    {
        DateTimeOffset now = TimeProvider.System.GetUtcNow();
        return Peers is not null && Peers.Any(p => p.PeerAccountID == peerAccountId && p.Type == PeerType.Banned && (p.TimestampExpires == null || p.TimestampExpires > now));
    }

    public bool HasIgnoredPeer(int peerAccountId)
    {
        DateTimeOffset now = TimeProvider.System.GetUtcNow();
        return Peers is not null && Peers.Any(p => p.PeerAccountID == peerAccountId && p.Type == PeerType.Ignored && (p.TimestampExpires == null || p.TimestampExpires > now));
    }

    [NotMapped]
    public byte[]? RowVersion { get; set; }

    [InverseProperty(nameof(AccountSelectedStoreItem.Account))]
    public List<AccountSelectedStoreItem> SelectedStoreItems { get; set; } = [];

    private List<int> _defaultGuideIds = [];
    private bool _defaultGuideIdsLoaded;
    private List<int> _favoriteGuideIds = [];
    private bool _favoriteGuideIdsLoaded;

    [NotMapped]
    public List<int> DefaultGuideIDs
    {
        get
        {
            if (!_defaultGuideIdsLoaded)
            {
                if (GuidePreferences is { Count: > 0 })
                {
                    _defaultGuideIds = GuidePreferences
                        .Where(p => p.IsDefault)
                        .Select(p => p.GuideID)
                        .Distinct()
                        .ToList();
                }
                _defaultGuideIdsLoaded = true;
            }
            return _defaultGuideIds;
        }
        set
        {
            _defaultGuideIds = value ?? [];
            _defaultGuideIdsLoaded = true;
            SyncDefaultGuidesFromList(_defaultGuideIds);
        }
    }

    [NotMapped]
    public List<int> FavoriteGuideIDs
    {
        get
        {
            if (!_favoriteGuideIdsLoaded)
            {
                if (GuidePreferences is { Count: > 0 })
                {
                    _favoriteGuideIds = GuidePreferences
                        .Where(p => p.IsFavorite)
                        .Select(p => p.GuideID)
                        .Distinct()
                        .ToList();
                }
                _favoriteGuideIdsLoaded = true;
            }
            return _favoriteGuideIds;
        }
        set
        {
            _favoriteGuideIds = value ?? [];
            _favoriteGuideIdsLoaded = true;
            SyncFavoriteGuidesFromList(_favoriteGuideIds);
        }
    }

    public void SyncDefaultGuidesFromList(List<int>? ids)
    {
        _defaultGuideIds = ids?.Distinct().ToList() ?? [];
        _defaultGuideIdsLoaded = true;

        GuidePreferences ??= [];
        foreach (AccountGuidePreference p in GuidePreferences)
        {
            p.IsDefault = false;
        }

        if (ids == null || ids.Count == 0)
        {
            GuidePreferences.RemoveAll(p => !p.IsDefault && !p.IsFavorite);
            return;
        }

        DateTimeOffset now = TimeProvider.System.GetUtcNow();
        foreach (int guideId in ids.Distinct())
        {
            AccountGuidePreference? existing = GuidePreferences.FirstOrDefault(p => p.GuideID == guideId);
            if (existing != null)
            {
                existing.IsDefault = true;
                existing.TimestampUpdated = now;
            }
            else
            {
                GuidePreferences.Add(new AccountGuidePreference
                {
                    Account = this,
                    AccountID = ID,
                    GuideID = guideId,
                    IsDefault = true,
                    IsFavorite = false,
                    TimestampUpdated = now
                });
            }
        }
        GuidePreferences.RemoveAll(p => !p.IsDefault && !p.IsFavorite);
    }

    public void SyncFavoriteGuidesFromList(List<int>? ids)
    {
        _favoriteGuideIds = ids?.Distinct().ToList() ?? [];
        _favoriteGuideIdsLoaded = true;

        GuidePreferences ??= [];
        foreach (AccountGuidePreference p in GuidePreferences)
        {
            p.IsFavorite = false;
        }

        if (ids == null || ids.Count == 0)
        {
            GuidePreferences.RemoveAll(p => !p.IsDefault && !p.IsFavorite);
            return;
        }

        DateTimeOffset now = TimeProvider.System.GetUtcNow();
        foreach (int guideId in ids.Distinct())
        {
            AccountGuidePreference? existing = GuidePreferences.FirstOrDefault(p => p.GuideID == guideId);
            if (existing != null)
            {
                existing.IsFavorite = true;
                existing.TimestampUpdated = now;
            }
            else
            {
                GuidePreferences.Add(new AccountGuidePreference
                {
                    Account = this,
                    AccountID = ID,
                    GuideID = guideId,
                    IsDefault = false,
                    IsFavorite = true,
                    TimestampUpdated = now
                });
            }
        }
        GuidePreferences.RemoveAll(p => !p.IsDefault && !p.IsFavorite);
    }

    [InverseProperty(nameof(AccountGuidePreference.Account))]
    public List<AccountGuidePreference> GuidePreferences { get; set; } = [];

    [InverseProperty(nameof(AccountIpAddress.Account))]
    public List<AccountIpAddress> IPAddressCollection { get; set; } = [];

    [InverseProperty(nameof(AccountMacAddress.Account))]
    public List<AccountMacAddress> MACAddressCollection { get; set; } = [];

    public List<string> SystemInformationCollection { get; set; } = [];

    public List<string> SystemInformationHashCollection { get; set; } = [];

    [InverseProperty(nameof(AccountBlockedPhrase.Account))]
    public List<AccountBlockedPhrase> BlockedPhrases { get; set; } = [];

    public bool UseCloud { get; set; }

    public bool AutomaticCloudUpload { get; set; }

    public DateTimeOffset? BackupLastUpdatedTime { get; set; }

    [InverseProperty(nameof(AccountStatistics.Account))]
    public List<AccountStatistics> Statistics { get; set; } = [];

    [InverseProperty(nameof(AccountSession.Account))]
    public List<AccountSession> Sessions { get; set; } = [];

    [InverseProperty(nameof(Friendship.Account))]
    public List<Friendship> Friendships { get; set; } = [];

    [InverseProperty(nameof(Friendship.FriendAccount))]
    public List<Friendship> ReverseFriendships { get; set; } = [];

    [InverseProperty(nameof(EconomyTransaction.Account))]
    public List<EconomyTransaction> EconomyTransactions { get; set; } = [];

    [InverseProperty(nameof(HeroGuide.Author))]
    public List<HeroGuide> AuthoredGuides { get; set; } = [];

    [InverseProperty(nameof(PlayerReport.ReporterAccount))]
    public List<PlayerReport> ReportsFiled { get; set; } = [];

    [InverseProperty(nameof(PlayerReport.ReportedAccount))]
    public List<PlayerReport> ReportsReceived { get; set; } = [];

    [InverseProperty(nameof(Notification.Account))]
    public List<Notification> Notifications { get; set; } = [];

    [InverseProperty(nameof(PlayerStatistics.Account))]
    public List<PlayerStatistics> MatchParticipations { get; set; } = [];

    public DateTimeOffset? MuteExpiration { get; set; }

    [MaxLength(255)] public string? MuteReason { get; set; }

    public DateTimeOffset? SuspensionExpiration { get; set; }

    [MaxLength(255)] public string? SuspensionReason { get; set; }

    public DateTimeOffset? BanExpiration { get; set; }

    [MaxLength(255)] public string? BanReason { get; set; }

    [NotMapped]
    public bool IsMuted => MuteExpiration.HasValue && MuteExpiration.Value > TimeProvider.System.GetUtcNow();

    [NotMapped]
    public bool IsSuspended =>
        SuspensionExpiration.HasValue && SuspensionExpiration.Value > TimeProvider.System.GetUtcNow();

    [NotMapped]
    public bool IsBanned => BanExpiration.HasValue
        ? BanExpiration.Value > TimeProvider.System.GetUtcNow()
        : Type == AccountType.Disabled;

    #region Selected Store Item Helpers

    [NotMapped]
    public List<string> SelectedStoreItemCodes =>
        SelectedStoreItems?.Select(item => item.ItemCode).ToList() ?? [];

    public void SetSelectedStoreItems(IEnumerable<string>? items)
    {
        SelectedStoreItems ??= [];
        SelectedStoreItems.Clear();
        if (items is null) return;

        foreach (string item in items
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            SelectedStoreItems.Add(new AccountSelectedStoreItem
            {
                AccountID = ID,
                ItemCode = item
            });
        }
    }

    public void AddSelectedStoreItem(string itemCode)
    {
        if (string.IsNullOrWhiteSpace(itemCode)) return;
        string trimmed = itemCode.Trim();
        SelectedStoreItems ??= [];
        if (!SelectedStoreItems.Any(i => string.Equals(i.ItemCode, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            SelectedStoreItems.Add(new AccountSelectedStoreItem
            {
                AccountID = ID,
                ItemCode = trimmed
            });
        }
    }

    public bool RemoveSelectedStoreItem(string itemCode)
    {
        if (string.IsNullOrWhiteSpace(itemCode) || SelectedStoreItems is null) return false;
        string trimmed = itemCode.Trim();
        AccountSelectedStoreItem? match = SelectedStoreItems.FirstOrDefault(i =>
            string.Equals(i.ItemCode, trimmed, StringComparison.OrdinalIgnoreCase));
        return match != null && SelectedStoreItems.Remove(match);
    }

    public bool HasSelectedStoreItem(string itemCode) =>
        !string.IsNullOrWhiteSpace(itemCode) &&
        SelectedStoreItems is not null &&
        SelectedStoreItems.Any(i => string.Equals(i.ItemCode, itemCode.Trim(), StringComparison.OrdinalIgnoreCase));

    #endregion

    #region Network Identifier Helpers

    [NotMapped]
    public List<string> IPAddressStrings =>
        IPAddressCollection?.Select(ip => ip.IPAddress).ToList() ?? [];

    [NotMapped]
    public ICollection<AccountIpAddress> IpAddresses => IPAddressCollection;

    public bool AddIPAddress(string ipAddress)
    {
        if (string.IsNullOrWhiteSpace(ipAddress)) return false;
        string trimmed = ipAddress.Trim();
        IPAddressCollection ??= [];
        if (IPAddressCollection.Any(ip => string.Equals(ip.IPAddress, trimmed, StringComparison.OrdinalIgnoreCase)))
            return false;

        IPAddressCollection.Add(new AccountIpAddress
        {
            AccountID = ID,
            IPAddress = trimmed
        });
        return true;
    }

    public bool HasIPAddress(string ipAddress) =>
        !string.IsNullOrWhiteSpace(ipAddress) &&
        IPAddressCollection is not null &&
        IPAddressCollection.Any(ip => string.Equals(ip.IPAddress, ipAddress.Trim(), StringComparison.OrdinalIgnoreCase));

    [NotMapped]
    public List<string> MACAddressStrings =>
        MACAddressCollection?.Select(mac => mac.MACAddress).ToList() ?? [];

    [NotMapped]
    public ICollection<AccountMacAddress> MacAddresses => MACAddressCollection;

    public bool AddMACAddress(string macAddress)
    {
        if (string.IsNullOrWhiteSpace(macAddress)) return false;
        string trimmed = macAddress.Trim();
        MACAddressCollection ??= [];
        if (MACAddressCollection.Any(mac => string.Equals(mac.MACAddress, trimmed, StringComparison.OrdinalIgnoreCase)))
            return false;

        MACAddressCollection.Add(new AccountMacAddress
        {
            AccountID = ID,
            MACAddress = trimmed
        });
        return true;
    }

    public bool HasMACAddress(string macAddress) =>
        !string.IsNullOrWhiteSpace(macAddress) &&
        MACAddressCollection is not null &&
        MACAddressCollection.Any(mac => string.Equals(mac.MACAddress, macAddress.Trim(), StringComparison.OrdinalIgnoreCase));

    #endregion

    #region Blocked Phrase Helpers

    [NotMapped]
    public List<string> BlockedPhraseStrings =>
        BlockedPhrases?.Select(bp => bp.Phrase).ToList() ?? [];

    public bool AddBlockedPhrase(string phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase)) return false;
        string trimmed = phrase.Trim();
        if (trimmed.Length > 200) trimmed = trimmed[..200];
        BlockedPhrases ??= [];
        if (BlockedPhrases.Any(bp => string.Equals(bp.Phrase, trimmed, StringComparison.OrdinalIgnoreCase)))
            return false;

        BlockedPhrases.Add(new AccountBlockedPhrase
        {
            AccountID = ID,
            Phrase = trimmed
        });
        return true;
    }

    public bool RemoveBlockedPhrase(string phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase) || BlockedPhrases is null) return false;
        string trimmed = phrase.Trim();
        if (trimmed.Length > 200) trimmed = trimmed[..200];
        AccountBlockedPhrase? match = BlockedPhrases.FirstOrDefault(bp =>
            string.Equals(bp.Phrase, trimmed, StringComparison.OrdinalIgnoreCase));
        return match != null && BlockedPhrases.Remove(match);
    }

    public bool HasBlockedPhrase(string phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase) || BlockedPhrases is null) return false;
        string trimmed = phrase.Trim();
        if (trimmed.Length > 200) trimmed = trimmed[..200];
        return BlockedPhrases.Any(bp => string.Equals(bp.Phrase, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    #endregion

    #region Friendship Helpers

    /// <summary>
    /// Checks if this account has an accepted friendship with the specified account ID.
    /// Requires <see cref="Friendships"/> navigation to be loaded.
    /// </summary>
    public bool HasFriend(int friendAccountId) =>
        Friendships is not null &&
        Friendships.Any(f => f.FriendAccountID == friendAccountId && f.Status == Enumerations.FriendshipStatus.Accepted);

    /// <summary>
    /// Gets all accepted friend account IDs for this account.
    /// Requires <see cref="Friendships"/> navigation to be loaded.
    /// </summary>
    [NotMapped]
    public List<int> FriendAccountIDs =>
        Friendships?
            .Where(f => f.Status == Enumerations.FriendshipStatus.Accepted)
            .Select(f => f.FriendAccountID)
            .ToList() ?? [];

    /// <summary>
    /// Transitional backward-compatibility projection of <see cref="Friendships"/> into <see cref="FriendedPeer"/> DTOs.
    /// Not persisted in database. Requires <see cref="Friendships"/> and <see cref="Friendship.FriendAccount"/> to be loaded.
    /// </summary>
    [NotMapped]
    [Obsolete("Transitional projection. Migrate callers to query core.Friendships via IFriendshipRepository.")]
    public IReadOnlyList<Entities.Relational.FriendedPeer> FriendedPeers =>
        Friendships?
            .Where(f => f.Status == Enumerations.FriendshipStatus.Accepted)
            .Select(f => new Entities.Relational.FriendedPeer
            {
                ID = f.FriendAccountID,
                Name = f.FriendAccount?.Name ?? string.Empty,
                ClanTag = f.FriendAccount?.Clan?.Tag,
                FriendGroup = f.FriendGroup
            })
            .ToList() ?? [];

    #endregion
}

