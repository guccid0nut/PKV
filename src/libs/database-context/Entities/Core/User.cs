// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2022-2026 [K]ONGOR and Contributors
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV) -- Upstream lineage: https://github.com/Project-KONGOR-Open-Source

using UserRoles = MERRICK.DatabaseContext.Constants.UserRoles;

namespace MERRICK.DatabaseContext.Entities.Core;

[Table("Users", Schema = "core")]
[Index(nameof(EmailAddress), IsUnique = true)]
[Index(nameof(DiscordId))]
[Index(nameof(RoleID))]
public class User
{
    [Key] public int ID { get; set; }

    [MaxLength(254)] public required string EmailAddress { get; set; }

    [MaxLength(32)] public string? DiscordId { get; set; }

    [MaxLength(64)] public string? DiscordUsername { get; set; }

    [MaxLength(128)] public string? DiscordAvatar { get; set; }

    [MaxLength(64)] public string? TwitchUsername { get; set; }

    [MaxLength(32)] public string? TwitchId { get; set; }

    public int RoleID { get; set; }

    [ForeignKey(nameof(RoleID))] public Role Role { get; set; } = null!;

    [StringLength(64)] public required string SRPPasswordSalt { get; set; }

    [StringLength(64)] public required string SRPPasswordHash { get; set; }

    [StringLength(84)] public string PBKDF2PasswordHash { get; set; } = null!;
 
    [MaxLength(256)] public string? Argon2idPasswordHash { get; set; }

    [InverseProperty(nameof(Account.User))]
    public List<Account> Accounts { get; set; } = [];

    public int? InvitedByUserID { get; set; }

    [ForeignKey(nameof(InvitedByUserID))]
    public User? InvitedByUser { get; set; }

    [InverseProperty(nameof(UserInvite.CreatedByUser))]
    public List<UserInvite> SentInvites { get; set; } = [];

    public DateTimeOffset TimestampCreated { get; set; } = TimeProvider.System.GetUtcNow();

    public DateTimeOffset TimestampLastActive { get; set; } = TimeProvider.System.GetUtcNow();

    public int GoldCoins { get; set; }

    public int SilverCoins { get; set; }

    public int PlinkoTickets { get; set; }

    public DateTimeOffset? LastPlinkoDailyClaimUtc { get; set; }

    public List<UserWebReward> WebRewards { get; set; } = [];

    public int TotalLevel { get; set; }

    public int TotalExperience { get; set; }

    [NotMapped]
    [Obsolete("Legacy array storage; migrated to core.UserInventory. Retained for dual-read legacy fallback.")]
    public List<string> OwnedStoreItems { get; set; } = [];

    [InverseProperty(nameof(UserInventory.User))]
    public List<UserInventory> InventoryItems { get; set; } = [];

    [InverseProperty(nameof(EconomyTransaction.User))]
    public List<EconomyTransaction> EconomyTransactions { get; set; } = [];

    [InverseProperty(nameof(AccountSession.User))]
    public List<AccountSession> Sessions { get; set; } = [];

    [NotMapped] public bool IsAdministrator => Role != null && Role.Name.Equals(UserRoles.Administrator);

    [NotMapped]
    public bool IsStaff => (Role != null && Role.Name.Equals(UserRoles.Administrator)) ||
                           Accounts.Any(a => a.IsStaff);
}
